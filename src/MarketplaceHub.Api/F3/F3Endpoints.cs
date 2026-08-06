using System.Security.Cryptography;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.F3;

public static class F3Endpoints
{
    public static IEndpointRouteBuilder MapF3Endpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").AddEndpointFilter(async (context, next) => { try { return await next(context); } catch (ArgumentException exception) { return Problem(context.HttpContext, new("INVALID_CURSOR", exception.Message, 400)); } });
        api.MapGet("/connections", async (HttpContext http, IF3ConnectionService service, int? limit, string? after) => Tenant(http) is { } tenant ? Results.Ok(await service.ListAsync(tenant.TenantId, PageSize(limit), after, http.RequestAborted)) : Unauthorized(http));
        api.MapPost("/connections", async (CreateConnectionCommand command, HttpContext http, IF3ConnectionService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Created(await service.CreateAsync(tenant.TenantId, command, http.RequestAborted), "/api/v1/connections") : MissingContext(http));
        api.MapGet("/connections/{id:guid}", async (Guid id, HttpContext http, IF3ConnectionService service) => Tenant(http) is { } tenant ? WithEtag(http, await service.GetAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapPatch("/connections/{id:guid}", async (Guid id, UpdateConnectionCommand command, HttpContext http, IF3ConnectionService service) => Tenant(http) is { } tenant ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.UpdateAsync(tenant.TenantId, id, version, command, http.RequestAborted), x => x.Version) : failure! : Unauthorized(http));
        api.MapPut("/connections/{id:guid}/credential", async (Guid id, CredentialCommand command, HttpContext http, IF3ConnectionService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.RotateCredentialAsync(tenant.TenantId, id, version, command, http.RequestAborted), x => x.Version) : failure! : MissingContext(http));
        api.MapPost("/connections/{id:guid}/test-jobs", async (Guid id, HttpContext http, IF3ConnectionService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueTestAsync(tenant.TenantId, id, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));
        api.MapPut("/connections/{id:guid}/active", async (Guid id, ActiveCommand command, HttpContext http, IF3ConnectionService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.SetActiveAsync(tenant.TenantId, id, version, command.Active, http.RequestAborted), x => x.Version) : failure! : MissingContext(http));
        api.MapGet("/connections/{id:guid}/capabilities", async (Guid id, HttpContext http, IF3ConnectionService service) => Tenant(http) is { } tenant ? Result(await service.CapabilitiesAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapPut("/connections/{id:guid}/capabilities/{code}/evidence", async (Guid id, string code, RecordCapabilityEvidenceCommand command, HttpContext http, IF3ConnectionService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null
                ? TryIfMatch(http, out var version, out var failure)
                    ? WithEtag(http, await service.RecordCapabilityEvidenceAsync(tenant.TenantId, tenant.UserId, id, code, version, command, http.TraceIdentifier, http.RequestAborted), x => x.Version)
                    : failure!
                : MissingContext(http));
        api.MapGet("/connections/{id:guid}/sync-policies", async (Guid id, HttpContext http, IF3ConnectionService service) => Tenant(http) is { } tenant ? Result(await service.SyncPoliciesAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapPut("/connections/{id:guid}/sync-policies/{resourceType}", async (Guid id, string resourceType, UpdateSyncPolicyCommand command, HttpContext http, IF3ConnectionService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http); var expected = OptionalIfMatch(http, out var malformed); if (malformed is not null) return malformed;
            return Result(await service.UpsertSyncPolicyAsync(tenant.TenantId, id, resourceType, expected, command, http.RequestAborted), value => { http.Response.Headers.ETag = $"\"v{value.Version}\""; return Results.Ok(value); });
        });
        api.MapGet("/connections/{id:guid}/webhooks", async (Guid id, HttpContext http, IF3ConnectionService service) => Tenant(http) is { } tenant ? Result(await service.WebhooksAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapPost("/connections/{id:guid}/webhooks", async (Guid id, CreateWebhookSubscriptionCommand command, HttpContext http, IF3ConnectionService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Created(await service.CreateWebhookAsync(tenant.TenantId, id, command, http.RequestAborted), $"/api/v1/connections/{id:D}/webhooks") : MissingContext(http));

        api.MapGet("/orders", async (HttpContext http, IF3SalesService service, int? limit, string? after, string? status) => Tenant(http) is { } tenant ? Results.Ok(await service.OrdersAsync(tenant.TenantId, PageSize(limit), after, status, http.RequestAborted)) : Unauthorized(http));
        api.MapGet("/orders/{id:guid}", async (Guid id, HttpContext http, IF3SalesService service) => Tenant(http) is { } tenant ? WithEtag(http, await service.OrderAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapPost("/connections/{connectionId:guid}/order-sync-jobs", async (Guid connectionId, OrderSyncCommand command, HttpContext http, IF3SalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueOrderSyncAsync(tenant.TenantId, connectionId, command.ExternalOrderId, http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));
        api.MapPost("/connections/{connectionId:guid}/reference-sync-jobs", async (Guid connectionId, string? resourceType, string? parentExternalId, HttpContext http, IF3SalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueReferenceSyncAsync(tenant.TenantId, connectionId, resourceType ?? "CATEGORIES", parentExternalId, http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));
        api.MapGet("/shipments", async (HttpContext http, IF3SalesService service, int? limit, string? after, string? status) => Tenant(http) is { } tenant ? Results.Ok(await service.ShipmentsAsync(tenant.TenantId, PageSize(limit), after, status, http.RequestAborted)) : Unauthorized(http));
        api.MapGet("/shipments/{id:guid}", async (Guid id, HttpContext http, IF3SalesService service) => Tenant(http) is { } tenant ? Result(await service.ShipmentAsync(tenant.TenantId, id, http.RequestAborted), value => { http.Response.Headers.ETag = $"\"v{value.Package.Version}\""; return Results.Ok(value); }) : Unauthorized(http));
        api.MapPost("/shipments/{id:guid}/actions", async (Guid id, ShipmentActionCommand command, HttpContext http, IF3SalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Accepted(await service.EnqueueShipmentActionAsync(tenant.TenantId, id, version, command, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted)) : failure! : MissingContext(http));
        api.MapPost("/shipments/{id:guid}/common-label-jobs", async (Guid id, CommonLabelCommand command, HttpContext http, IF3SalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Accepted(await service.EnqueueCommonLabelAsync(tenant.TenantId, id, version, command.BoxQuantity, command.VolumetricHeight, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted)) : failure! : MissingContext(http));
        api.MapGet("/returns", async (HttpContext http, IF3SalesService service, int? limit, string? after, string? status) => Tenant(http) is { } tenant ? Results.Ok(await service.ReturnsAsync(tenant.TenantId, PageSize(limit), after, status, http.RequestAborted)) : Unauthorized(http));
        api.MapGet("/returns/{id:guid}", async (Guid id, HttpContext http, IF3SalesService service) => Tenant(http) is { } tenant ? WithEtag(http, await service.ReturnAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapGet("/returns/{id:guid}/rejection-reasons", async (Guid id, HttpContext http, IF3SalesService service) => Tenant(http) is { } tenant ? Result(await service.ReturnIssueReasonsAsync(tenant.TenantId, id, http.TraceIdentifier, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapPost("/connections/{connectionId:guid}/return-sync-jobs", async (Guid connectionId, HttpContext http, IF3SalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueReturnSyncAsync(tenant.TenantId, connectionId, http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));
        api.MapPost("/returns/{id:guid}/actions", async (Guid id, ReturnDecisionCommand command, HttpContext http, IF3SalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Accepted(await service.EnqueueReturnActionAsync(tenant.TenantId, tenant.UserId, id, version, command, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted)) : failure! : MissingContext(http));
        api.MapPost("/returns/{id:guid}/stock-dispositions", async (Guid id, ReturnDispositionCommand command, HttpContext http, IF3SalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? WithEtag(http, await service.ApplyDispositionAsync(tenant.TenantId, tenant.UserId, id, command, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted), x => x.Version) : MissingContext(http));
        api.MapPost("/files/return-evidence", UploadReturnEvidenceAsync).DisableAntiforgery();

        api.MapPost("/hooks/{connectionPublicId:guid}/{routeToken}", ReceiveWebhook).DisableAntiforgery().RequireRateLimiting("webhook");
        return endpoints;
    }

    private static async Task<IResult> UploadReturnEvidenceAsync(HttpContext http, AppDbContext db, IPrivateFileStorage storage, TimeProvider timeProvider)
    {
        const long maximumBytes = 10 * 1024 * 1024;
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (!http.Request.HasFormContentType) return Problem(http, new("EVIDENCE_FORM_REQUIRED", "multipart/form-data body gereklidir.", 400));
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length <= 0) return Problem(http, new("EVIDENCE_FILE_REQUIRED", "file alanında kanıt dosyası gereklidir.", 400));
        if (file.Length > maximumBytes) return Problem(http, new("EVIDENCE_TOO_LARGE", "İade kanıtı en fazla 10 MiB olabilir.", 413));
        if (file.ContentType is not ("application/pdf" or "image/jpeg" or "image/png")) return Problem(http, new("EVIDENCE_TYPE_UNSUPPORTED", "Yalnız PDF, JPEG veya PNG iade kanıtı kabul edilir.", 415));

        await using var input = file.OpenReadStream(maximumBytes);
        await using var buffer = new MemoryStream((int)file.Length);
        await input.CopyToAsync(buffer, http.RequestAborted);
        if (buffer.Length != file.Length || buffer.Length > maximumBytes) return Problem(http, new("EVIDENCE_SIZE_MISMATCH", "Dosya boyutu doğrulanamadı.", 422));
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var asset = await db.FileAssets.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Classification == "RETURN_EVIDENCE" && x.Sha256 == hash && x.ArchivedAt == null && x.Status == "ACTIVE", http.RequestAborted);
        if (asset is null)
        {
            var id = Guid.CreateVersion7();
            var extension = file.ContentType switch { "application/pdf" => ".pdf", "image/jpeg" => ".jpg", _ => ".png" };
            buffer.Position = 0;
            var stored = await storage.SaveAsync(tenant.TenantId, $"{id:N}{extension}", file.ContentType, buffer, maximumBytes, http.RequestAborted);
            asset = new FileAsset { Id = id, TenantId = tenant.TenantId, Classification = "RETURN_EVIDENCE", RelativePath = stored, OriginalNameSafe = Path.GetFileName(file.FileName), MimeType = file.ContentType, SizeBytes = bytes.LongLength, Sha256 = hash, Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() };
            db.FileAssets.Add(asset);
            await db.SaveChangesAsync(http.RequestAborted);
        }
        return Results.Created($"/api/v1/files/return-evidence/{asset.Id:D}", new { asset.Id, asset.MimeType, asset.SizeBytes, asset.Sha256 });
    }

    private static async Task<IResult> ReceiveWebhook(Guid connectionPublicId, string routeToken, HttpContext http, IF3WebhookService service)
    {
        const int maximumBytes = 10 * 1024 * 1024;
        var sizeFeature = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = maximumBytes;
        if (http.Request.ContentLength is > maximumBytes) return Problem(http, new("WEBHOOK_TOO_LARGE", "Webhook gövdesi kabul edilen üst sınırı aşıyor.", 413));

        await using var body = new MemoryStream(http.Request.ContentLength is > 0 and <= maximumBytes ? (int)http.Request.ContentLength.Value : 0);
        var buffer = new byte[81_920];
        var total = 0;
        while (true)
        {
            var read = await http.Request.Body.ReadAsync(buffer.AsMemory(), http.RequestAborted);
            if (read == 0) break;
            total += read;
            if (total > maximumBytes) return Problem(http, new("WEBHOOK_TOO_LARGE", "Webhook gövdesi kabul edilen üst sınırı aşıyor.", 413));
            await body.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted);
        }

        var headers = http.Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var result = await service.ReceiveAsync(connectionPublicId, routeToken, body.ToArray(), headers, http.TraceIdentifier, http.RequestAborted);
        return result.Succeeded ? Results.Ok(new { accepted = true }) : Problem(http, result.Error!);
    }
    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
    private static int PageSize(int? limit) => limit is null ? 50 : limit is >= 1 and <= 200 ? limit.Value : throw new ArgumentException("limit 1-200 arasında olmalıdır.");
    private static IResult? RequireIdempotency(HttpContext http) => string.IsNullOrWhiteSpace(http.Request.Headers["Idempotency-Key"]) ? Problem(http, new("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key başlığı zorunludur.", 400)) : http.Request.Headers["Idempotency-Key"].ToString().Length > 256 ? Problem(http, new("IDEMPOTENCY_KEY_INVALID", "Idempotency-Key en fazla 256 karakterdir.", 400)) : null;
    private static bool TryIfMatch(HttpContext http, out long version, out IResult? failure) { var parsed = OptionalIfMatch(http, out failure); version = parsed ?? 0; if (failure is null && parsed is null) failure = Problem(http, new("PRECONDITION_REQUIRED", "If-Match gereklidir.", 428)); return failure is null; }
    private static long? OptionalIfMatch(HttpContext http, out IResult? failure) { failure = null; var value = http.Request.Headers.IfMatch.ToString(); if (string.IsNullOrWhiteSpace(value)) return null; if (value.Length >= 4 && value.StartsWith("\"v", StringComparison.Ordinal) && value.EndsWith('"') && long.TryParse(value[2..^1], out var version) && version > 0) return version; failure = Problem(http, new("INVALID_ETAG", "If-Match güçlü ETag biçiminde olmalıdır: \"v{version}\".", 400)); return null; }
    private static IResult WithEtag<T>(HttpContext http, ServiceResult<T> result, Func<T, long> version) { if (!result.Succeeded) return Problem(http, result.Error!); http.Response.Headers.ETag = $"\"v{version(result.Value!)}\""; return Results.Ok(result.Value); }
    private static IResult Created<T>(ServiceResult<T> result, string collection) { if (!result.Succeeded) return Results.Json(ToProblem(result.Error!, null), statusCode: result.Error!.Status); var id = typeof(T).GetProperty("Id")?.GetValue(result.Value); return Results.Created(id is Guid guid ? $"{collection}/{guid:D}" : collection, result.Value); }
    private static IResult Accepted(ServiceResult<Guid> result) => result.Succeeded ? Results.Accepted($"/api/v1/jobs/{result.Value:D}", new { jobId = result.Value }) : Results.Json(ToProblem(result.Error!, null), statusCode: result.Error!.Status);
    private static IResult Result<T>(ServiceResult<T> result, Func<T, IResult> success) => result.Succeeded ? success(result.Value!) : Results.Json(ToProblem(result.Error!, null), statusCode: result.Error!.Status);
    private static IResult Unauthorized(HttpContext http) => Problem(http, new("AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401));
    private static IResult MissingContext(HttpContext http) => Tenant(http) is null ? Unauthorized(http) : RequireIdempotency(http) ?? Problem(http, new("REQUEST_INVALID", "İstek tamamlanamadı.", 400));
    private static IResult Problem(HttpContext http, ServiceError error) => Results.Json(ToProblem(error, http.TraceIdentifier), statusCode: error.Status, contentType: "application/problem+json");
    private static object ToProblem(ServiceError error, string? correlationId) => new { type = $"https://marketplacehub.invalid/problems/{error.Code.ToLowerInvariant().Replace('_', '-')}", title = error.Message, status = error.Status, code = error.Code, correlationId, retryable = error.Status is 429 or >= 500, fieldErrors = error.FieldErrors };
    public sealed record ActiveCommand(bool Active);
    public sealed record OrderSyncCommand(string? ExternalOrderId);
    public sealed record CommonLabelCommand(int BoxQuantity, decimal VolumetricHeight);
}
