using System.Text;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketplaceHub.Api.Catalog;

public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    private const int MaximumRequestBytes = 10 * 1024 * 1024;

    public async Task InvokeAsync(HttpContext context, AppDbContext db, ITenantContextAccessor tenants, TimeProvider timeProvider, TokenHasher hasher)
    {
        var isMutation = HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method);
        var isApiMutation = isMutation && context.Request.Path.StartsWithSegments("/api/v1") && !context.Request.Path.StartsWithSegments("/api/v1/auth");
        if (!isApiMutation || tenants.Current is not { } tenant)
        {
            await next(context); return;
        }

        var hasIdempotencyKey = ApiIdempotencyHeaderPolicy.TryGetKey(context.Request.Headers, out var key, out var conflictingKeys);
        if (conflictingKeys)
        {
            await ApiProblems.WriteAsync(context, "IDEMPOTENCY_KEY_CONFLICT", "Idempotency-Key ile X-Idempotency-Key aynı değeri taşımalıdır.", StatusCodes.Status400BadRequest);
            return;
        }
        if (!hasIdempotencyKey)
        {
            await next(context); return;
        }

        // A key too long to store was let through unprotected; it is refused instead.
        if (key.Length > 256)
        {
            await ApiProblems.WriteAsync(context, "IDEMPOTENCY_KEY_INVALID", "Idempotency-Key en fazla 256 karakterdir.", StatusCodes.Status400BadRequest);
            return;
        }
        context.Request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: MaximumRequestBytes);
        await using var requestBytes = new MemoryStream();
        try { await context.Request.Body.CopyToAsync(requestBytes, context.RequestAborted); }
        catch (IOException)
        {
            await ApiProblems.WriteAsync(context, "UPLOAD_TOO_LARGE", "İstek gövdesi 10 MiB üst sınırını aşıyor.", StatusCodes.Status413PayloadTooLarge);
            return;
        }
        context.Request.Body.Position = 0;
        var route = context.Request.Path.Value ?? string.Empty;
        var secretResponse = SecretResponseRoutes.Matches(context.Request.Method, route);
        // The version the request was made against is part of it: the same key with another If-Match is a
        // different request, not a replay.
        var ifMatch = context.Request.Headers.IfMatch.ToString();
        var versionLine = string.IsNullOrEmpty(ifMatch) ? string.Empty : $"if-match:{ifMatch}\n";
        var hashInput = Encoding.UTF8.GetBytes($"{context.Request.Method}\n{route}\n{context.Request.QueryString}\n{versionLine}");
        var combined = new byte[hashInput.Length + requestBytes.Length];
        hashInput.CopyTo(combined, 0); requestBytes.ToArray().CopyTo(combined, hashInput.Length);
        // Bodies can carry credentials (connection keys, webhook passwords). A plain SHA-256 of them, kept
        // for a day, would let anyone who reads the table test guesses offline; the digest is keyed with the
        // server's key instead.
        var hash = hasher.HashBytes(IdempotencyRequestDigest.Prefix(combined));
        // Records belong to the user who made the request: another user of the tenant who happens to send
        // the same key neither replays nor blocks it.
        var storedKey = IdempotencyRequestDigest.StoredKey(tenant.UserId, key);

        var now = timeProvider.GetUtcNow();
        var existing = await db.ApiIdempotencyRecords.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.RouteTemplate == route && x.IdempotencyKey == storedKey, context.RequestAborted);
        // Expired rows are swept hourly (ApiIdempotencyRetentionService), not on every request; a row
        // the sweep has not reached yet is settled here, for this key only. A completed record is
        // disposable after its replay window. An unfinished one is evidence that the endpoint may
        // have committed before the process or client failed, so it becomes an explicit recovery
        // state instead of being deleted and retried blindly.
        if (existing is not null && existing.ExpiresAt <= now)
        {
            if (existing.State == "COMPLETED")
            {
                await db.ApiIdempotencyRecords.Where(x => x.Id == existing.Id && x.State == "COMPLETED").ExecuteDeleteAsync(context.RequestAborted);
                db.Entry(existing).State = EntityState.Detached;
                existing = null;
            }
            else
            {
                await db.ApiIdempotencyRecords.Where(x => x.Id == existing.Id && x.State != "COMPLETED")
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, "UNKNOWN").SetProperty(x => x.ExpiresAt, now.AddDays(7)), context.RequestAborted);
                existing.State = "UNKNOWN";
                existing.ExpiresAt = now.AddDays(7);
            }
        }
        if (existing is null)
        {
            // Records written before keys were scoped to the user hold the raw key. An unfinished one of those
            // (the request was running, or failed, across the deploy) must still stop a blind replay; its
            // request digest may be of an older form, so its state alone decides.
            var legacy = await db.ApiIdempotencyRecords.AsNoTracking()
                .Where(x => x.TenantId == tenant.TenantId && x.RouteTemplate == route && x.IdempotencyKey == key && (x.State == "IN_PROGRESS" || x.State == "UNKNOWN"))
                .Select(x => new { x.State, x.ResponseStatus, x.ResourceId, x.JobId, x.ExpiresAt })
                .FirstOrDefaultAsync(context.RequestAborted);
            if (legacy is not null)
            {
                var running = legacy.State == "IN_PROGRESS" && legacy.ExpiresAt > now;
                var earlierLegacy = new Dictionary<string, object?> { ["responseStatus"] = legacy.ResponseStatus, ["resourceId"] = legacy.ResourceId, ["jobId"] = legacy.JobId };
                await ApiProblems.WriteAsync(context, running
                    ? new ServiceError("IDEMPOTENCY_IN_PROGRESS", "Aynı idempotent istek eşzamanlı olarak işleniyor.", StatusCodes.Status409Conflict)
                    : new ServiceError("IDEMPOTENCY_RESULT_UNKNOWN", "İşlemin sonucu kesinleşmedi; aynı anahtarla körlemesine tekrar çalıştırılamaz.", StatusCodes.Status409Conflict), earlierLegacy);
                return;
            }
        }
        if (existing is not null)
        {
            var decision = ApiIdempotencyPolicy.Resolve(existing.State, existing.RequestHash, hash);
            var earlier = new Dictionary<string, object?> { ["responseStatus"] = existing.ResponseStatus, ["resourceId"] = existing.ResourceId, ["jobId"] = existing.JobId };
            if (decision is ApiIdempotencyDecisionKind.InProgress or ApiIdempotencyDecisionKind.Unknown)
            {
                var inProgress = decision == ApiIdempotencyDecisionKind.InProgress;
                await ApiProblems.WriteAsync(context, inProgress
                    ? new ServiceError("IDEMPOTENCY_IN_PROGRESS", "Aynı idempotent istek eşzamanlı olarak işleniyor.", StatusCodes.Status409Conflict)
                    : new ServiceError("IDEMPOTENCY_RESULT_UNKNOWN", "İşlemin sonucu kesinleşmedi; aynı anahtarla körlemesine tekrar çalıştırılamaz.", StatusCodes.Status409Conflict), earlier);
                return;
            }
            if (decision == ApiIdempotencyDecisionKind.Replay)
            {
                // The first answer held a secret that is shown once and was not kept (SecretResponseRoutes).
                if (secretResponse && existing.ResponseStatus is >= 200 and < 300)
                {
                    await ApiProblems.WriteAsync(context, new ServiceError("IDEMPOTENCY_RESPONSE_WITHHELD", "İşlem daha önce tamamlandı; içerdiği gizli bilgi yalnız ilk yanıtta gösterilir ve yeniden gönderilmez.", StatusCodes.Status409Conflict), earlier);
                    return;
                }
                await IdempotentResponse.ReplayAsync(existing, context.Response, context.RequestAborted);
                return;
            }
            await ApiProblems.WriteAsync(context, new ServiceError("IDEMPOTENCY_KEY_REUSED", "Aynı Idempotency-Key farklı bir istek için kullanılamaz.", StatusCodes.Status409Conflict), earlier);
            return;
        }

        var record = new ApiIdempotencyRecord { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, RouteTemplate = route, IdempotencyKey = storedKey, RequestHash = hash, State = "IN_PROGRESS", CreatedAt = now, ExpiresAt = now.AddHours(24) };
        db.ApiIdempotencyRecords.Add(record);
        try { await db.SaveChangesAsync(context.RequestAborted); }
        catch (DbUpdateException exception) when (IsExpectedIdempotencyRace(exception))
        {
            db.Entry(record).State = EntityState.Detached;
            await ApiProblems.WriteAsync(context, "IDEMPOTENCY_IN_PROGRESS", "Aynı idempotent istek eşzamanlı olarak işleniyor.", StatusCodes.Status409Conflict);
            return;
        }

        static bool IsExpectedIdempotencyRace(DbUpdateException exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current is PostgresException
                    {
                        SqlState: PostgresErrorCodes.UniqueViolation,
                        ConstraintName: var constraintName
                    }
                    && constraintName?.StartsWith("IX_api_idempotency_records_TenantId_RouteTemplate_IdempotencyK", StringComparison.Ordinal) == true)
                    return true;
            }

            return false;
        }

        var originalResponseBody = context.Response.Body;
        await using var responseBuffer = new MemoryStream();
        context.Response.Body = responseBuffer;
        try
        {
            await next(context);
            responseBuffer.Position = 0;
            if (context.Response.StatusCode < 500)
            {
                record.State = "COMPLETED";
                string? body = null;
                var withhold = secretResponse && context.Response.StatusCode is >= 200 and < 300;
                if (!withhold && IdempotentResponse.IsStorableBody(context.Response.ContentType, responseBuffer.Length))
                {
                    responseBuffer.Position = 0;
                    using var reader = new StreamReader(responseBuffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                    body = await reader.ReadToEndAsync(context.RequestAborted);
                }
                IdempotentResponse.Capture(record, context.Response, body);
                // A refused request (4xx) may have changed tracked entities before it refused; only the idempotency
                // record is saved, so a "nothing was saved" answer stays true.
                if (context.Response.StatusCode >= 400) DetachAllBut(db, record);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            else
            {
                await MarkUnknownAsync(db, record, timeProvider.GetUtcNow());
            }
            context.Response.Body = originalResponseBody;
            responseBuffer.Position = 0;
            await responseBuffer.CopyToAsync(originalResponseBody, context.RequestAborted);
        }
        catch (BadHttpRequestException)
        {
            // The body or parameters could not be read, so the endpoint never ran: the key is released
            // for the corrected request instead of being held as an unknown outcome.
            context.Response.Body = originalResponseBody;
            await ForgetAsync(db, record);
            throw;
        }
        catch
        {
            context.Response.Body = originalResponseBody;
            await MarkUnknownAsync(db, record, timeProvider.GetUtcNow());
            throw;
        }
    }

    private static void DetachAllBut(AppDbContext db, ApiIdempotencyRecord record)
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(entry => !ReferenceEquals(entry.Entity, record)).ToList())
            entry.State = EntityState.Detached;
    }

    private static async Task ForgetAsync(AppDbContext db, ApiIdempotencyRecord record)
    {
        DetachAllBut(db, record);
        db.Entry(record).State = EntityState.Deleted;
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException)
        {
            // The expiry sweep turns a row that could not be removed into an unknown outcome later.
        }
    }

    private static async Task MarkUnknownAsync(AppDbContext db, ApiIdempotencyRecord record, DateTimeOffset now)
    {
        // The endpoint may have left unsaved tracked changes behind while
        // throwing. Persist only the idempotency state so this recovery marker
        // cannot accidentally commit unrelated work from the failed request.
        DetachAllBut(db, record);

        record.State = "UNKNOWN";
        record.ResponseStatus = null;
        record.ResponseBody = null;
        record.ResponseContentType = null;
        record.ResponseETag = null;
        record.ResponseLocation = null;
        record.ExpiresAt = now.AddDays(7);
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException)
        {
            // Preserve the endpoint's original response/exception. The row
            // remains IN_PROGRESS when this durable recovery marker itself
            // cannot be written and will be retried by the expiry sweep.
        }
    }
}
