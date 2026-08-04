using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.F2;

public sealed class F2IdempotencyMiddleware(RequestDelegate next)
{
    private const int MaximumRequestBytes = 10 * 1024 * 1024;

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
        await db.ApiIdempotencyRecords.Where(x => x.TenantId == tenant.TenantId && x.ExpiresAt <= now).ExecuteDeleteAsync(context.RequestAborted);
        var existing = await db.ApiIdempotencyRecords.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.RouteTemplate == route && x.IdempotencyKey == key, context.RequestAborted);
        if (existing is not null)
        {
            var code = existing.RequestHash == hash ? "IDEMPOTENCY_REPLAY" : "IDEMPOTENCY_KEY_REUSED";
            var title = existing.RequestHash == hash ? "Bu idempotent istek daha önce işlendi; yinelenen yan etki oluşturulmadı." : "Aynı Idempotency-Key farklı bir istek için kullanılamaz.";
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new { type = $"https://marketplacehub.invalid/problems/{code.ToLowerInvariant().Replace('_', '-')}", title, status = 409, code, correlationId = context.TraceIdentifier, retryable = false, existing.ResponseStatus, existing.ResourceId, existing.JobId }, context.RequestAborted);
            return;
        }

        var record = new ApiIdempotencyRecord { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, RouteTemplate = route, IdempotencyKey = key, RequestHash = hash, State = "IN_PROGRESS", CreatedAt = now, ExpiresAt = now.AddHours(24) };
        db.ApiIdempotencyRecords.Add(record);
        try { await db.SaveChangesAsync(context.RequestAborted); }
        catch (DbUpdateException)
        {
            db.Entry(record).State = EntityState.Detached;
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { title = "Aynı idempotent istek eşzamanlı olarak işleniyor.", status = 409, code = "IDEMPOTENCY_IN_PROGRESS", correlationId = context.TraceIdentifier, retryable = true }, context.RequestAborted);
            return;
        }

        try
        {
            await next(context);
            if (context.Response.StatusCode < 500)
            {
                record.State = "COMPLETED";
                record.ResponseStatus = context.Response.StatusCode;
                await db.SaveChangesAsync(CancellationToken.None);
            }
            else
            {
                db.ApiIdempotencyRecords.Remove(record);
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch
        {
            try
            {
                db.ApiIdempotencyRecords.Remove(record);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (DbUpdateException)
            {
                // Preserve the original endpoint exception. Expired in-progress records are
                // removed opportunistically on a later idempotent request.
            }
            throw;
        }
    }
}
