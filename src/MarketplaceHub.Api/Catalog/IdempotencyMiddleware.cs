using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketplaceHub.Api.Catalog;

public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    private const int MaximumRequestBytes = 10 * 1024 * 1024;
    private const int MaximumStoredResponseBytes = 1024 * 1024;

    public async Task InvokeAsync(HttpContext context, AppDbContext db, ITenantContextAccessor tenants, TimeProvider timeProvider)
    {
        var isMutation = HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method);
        if (!isMutation || !context.Request.Path.StartsWithSegments("/api/v1") || context.Request.Path.StartsWithSegments("/api/v1/auth") || tenants.Current is not { } tenant || !context.Request.Headers.TryGetValue("Idempotency-Key", out var header) || string.IsNullOrWhiteSpace(header))
        {
            await next(context); return;
        }

        var key = header.ToString();
        if (key.Length > 256) { await next(context); return; }
        context.Request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: MaximumRequestBytes);
        await using var requestBytes = new MemoryStream();
        try { await context.Request.Body.CopyToAsync(requestBytes, context.RequestAborted); }
        catch (IOException)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { title = "İstek gövdesi 10 MiB üst sınırını aşıyor.", status = 413, code = "UPLOAD_TOO_LARGE", correlationId = context.TraceIdentifier, retryable = false }, context.RequestAborted);
            return;
        }
        context.Request.Body.Position = 0;
        var route = context.Request.Path.Value ?? string.Empty;
        var hashInput = Encoding.UTF8.GetBytes($"{context.Request.Method}\n{route}\n{context.Request.QueryString}\n");
        var combined = new byte[hashInput.Length + requestBytes.Length];
        hashInput.CopyTo(combined, 0); requestBytes.ToArray().CopyTo(combined, hashInput.Length);
        var hash = Convert.ToHexString(SHA256.HashData(combined));

        var now = timeProvider.GetUtcNow();
        // Completed records are disposable after their replay window. An
        // unfinished record is evidence that the endpoint may have committed
        // before the process or client failed, so it must become an explicit
        // recovery state instead of being deleted and retried blindly.
        await db.ApiIdempotencyRecords
            .Where(x => x.TenantId == tenant.TenantId && x.ExpiresAt <= now && x.State == "COMPLETED")
            .ExecuteDeleteAsync(context.RequestAborted);
        await db.ApiIdempotencyRecords
            .Where(x => x.TenantId == tenant.TenantId && x.ExpiresAt <= now && x.State != "COMPLETED")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, "UNKNOWN")
                .SetProperty(x => x.ExpiresAt, now.AddDays(7)), context.RequestAborted);
        var existing = await db.ApiIdempotencyRecords.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.RouteTemplate == route && x.IdempotencyKey == key, context.RequestAborted);
        if (existing is not null)
        {
            var decision = ApiIdempotencyPolicy.Resolve(existing.State, existing.RequestHash, hash);
            if (decision is ApiIdempotencyDecisionKind.InProgress or ApiIdempotencyDecisionKind.Unknown)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                context.Response.ContentType = "application/problem+json";
                var inProgress = decision == ApiIdempotencyDecisionKind.InProgress;
                await context.Response.WriteAsJsonAsync(new
                {
                    title = inProgress ? "Aynı idempotent istek eşzamanlı olarak işleniyor." : "İşlemin sonucu kesinleşmedi; aynı anahtarla körlemesine tekrar çalıştırılamaz.",
                    status = 409,
                    code = inProgress ? "IDEMPOTENCY_IN_PROGRESS" : "IDEMPOTENCY_RESULT_UNKNOWN",
                    correlationId = context.TraceIdentifier,
                    retryable = inProgress,
                    existing.ResponseStatus,
                    existing.ResourceId,
                    existing.JobId
                }, context.RequestAborted);
                return;
            }
            if (decision == ApiIdempotencyDecisionKind.Replay)
            {
                context.Response.StatusCode = existing.ResponseStatus ?? StatusCodes.Status200OK;
                context.Response.Headers["Idempotency-Replayed"] = "true";
                if (!string.IsNullOrEmpty(existing.ResponseBody))
                {
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsync(existing.ResponseBody, context.RequestAborted);
                }
                return;
            }
            const string code = "IDEMPOTENCY_KEY_REUSED";
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new { type = $"https://marketplacehub.invalid/problems/{code.ToLowerInvariant().Replace('_', '-')}", title = "Aynı Idempotency-Key farklı bir istek için kullanılamaz.", status = 409, code, correlationId = context.TraceIdentifier, retryable = false, existing.ResponseStatus, existing.ResourceId, existing.JobId }, context.RequestAborted);
            return;
        }

        var record = new ApiIdempotencyRecord { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, RouteTemplate = route, IdempotencyKey = key, RequestHash = hash, State = "IN_PROGRESS", CreatedAt = now, ExpiresAt = now.AddHours(24) };
        db.ApiIdempotencyRecords.Add(record);
        try { await db.SaveChangesAsync(context.RequestAborted); }
        catch (DbUpdateException exception) when (IsExpectedIdempotencyRace(exception))
        {
            db.Entry(record).State = EntityState.Detached;
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { title = "Aynı idempotent istek eşzamanlı olarak işleniyor.", status = 409, code = "IDEMPOTENCY_IN_PROGRESS", correlationId = context.TraceIdentifier, retryable = true }, context.RequestAborted);
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
                record.ResponseStatus = context.Response.StatusCode;
                if (responseBuffer.Length <= MaximumStoredResponseBytes && context.Response.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    responseBuffer.Position = 0;
                    using var reader = new StreamReader(responseBuffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                    record.ResponseBody = await reader.ReadToEndAsync(context.RequestAborted);
                }
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
        catch
        {
            context.Response.Body = originalResponseBody;
            await MarkUnknownAsync(db, record, timeProvider.GetUtcNow());
            throw;
        }
    }

    private static async Task MarkUnknownAsync(AppDbContext db, ApiIdempotencyRecord record, DateTimeOffset now)
    {
        // The endpoint may have left unsaved tracked changes behind while
        // throwing. Persist only the idempotency state so this recovery marker
        // cannot accidentally commit unrelated work from the failed request.
        foreach (var entry in db.ChangeTracker.Entries().Where(entry => !ReferenceEquals(entry.Entity, record)).ToList())
            entry.State = EntityState.Detached;

        record.State = "UNKNOWN";
        record.ResponseStatus = null;
        record.ResponseBody = null;
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
