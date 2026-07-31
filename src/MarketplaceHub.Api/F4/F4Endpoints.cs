using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace MarketplaceHub.Api.F4;

public static class F4Endpoints
{
    public static IEndpointRouteBuilder MapF4Endpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").AddEndpointFilter(async (context, next) => { try { return await next(context); } catch (ArgumentException exception) { return Problem(context.HttpContext, new("INVALID_CURSOR", exception.Message, 400)); } });

        api.MapGet("/billing/legal-entity-profile", async (HttpContext http, IF4BillingService service) => Tenant(http) is { } tenant ? WithEtag(http, await service.GetLegalEntityAsync(tenant.TenantId, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapPut("/billing/legal-entity-profile", UpsertLegalEntity);
        api.MapGet("/billing/invoice-policies/{connectionId:guid}", async (Guid connectionId, HttpContext http, IF4BillingService service) => Tenant(http) is { } tenant ? WithEtag(http, await service.GetPolicyAsync(tenant.TenantId, connectionId, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapPut("/billing/invoice-policies/{connectionId:guid}", UpsertPolicy);

        api.MapGet("/invoices", async (HttpContext http, IF4BillingService service, int? limit, string? after, string? status) => Tenant(http) is { } tenant ? Results.Ok(await service.ListAsync(tenant.TenantId, PageSize(limit), after, status, http.RequestAborted)) : Unauthorized(http));
        api.MapPost("/invoices", async (CreateInvoiceCommand command, HttpContext http, IF4BillingService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Created(await service.CreateDraftAsync(tenant.TenantId, command, http.Request.Headers["Idempotency-Key"].ToString(), http.RequestAborted), "/api/v1/invoices") : MissingContext(http));
        api.MapGet("/invoices/{id:guid}", async (Guid id, HttpContext http, IF4BillingService service) => Tenant(http) is { } tenant ? WithEtag(http, await service.GetAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapPost("/invoices/{id:guid}/validate", async (Guid id, HttpContext http, IF4BillingService service) => Tenant(http) is { } tenant ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.ValidateAsync(tenant.TenantId, id, version, http.RequestAborted), x => x.Version) : failure! : Unauthorized(http));
        api.MapPost("/invoices/{id:guid}/submit-jobs", async (Guid id, ConfirmedAction command, HttpContext http, IF4BillingService service, UserManager<ApplicationUser> users) => await EnqueueProtected(id, command, http, users, (tenant, version, key) => service.EnqueueSubmitAsync(tenant, id, version, key, http.TraceIdentifier, http.RequestAborted)));
        api.MapPost("/invoices/{id:guid}/reconcile-jobs", async (Guid id, HttpContext http, IF4BillingService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueReconcileAsync(tenant.TenantId, id, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));
        api.MapPost("/invoices/{id:guid}/marketplace-delivery-jobs", async (Guid id, ConfirmedAction command, HttpContext http, IF4BillingService service, UserManager<ApplicationUser> users) => await EnqueueProtected(id, command, http, users, (tenant, _, key) => service.EnqueueDeliveryAsync(tenant, id, key, http.TraceIdentifier, http.RequestAborted), false));
        api.MapPost("/invoices/{id:guid}/cancellation-jobs", async (Guid id, ConfirmedAction command, HttpContext http, IF4BillingService service, UserManager<ApplicationUser> users) => await EnqueueProtected(id, command, http, users, (tenant, version, key) => service.EnqueueCancellationAsync(tenant, id, version, key, http.TraceIdentifier, http.RequestAborted)));
        api.MapGet("/invoices/{invoiceId:guid}/documents/{documentId:guid}/content", async (Guid invoiceId, Guid documentId, HttpContext http, IF4BillingService service) => Tenant(http) is { } tenant ? Stream(await service.OpenDocumentAsync(tenant.TenantId, invoiceId, documentId, http.RequestAborted), http) : Unauthorized(http));
        return endpoints;
    }

    private static async Task<IResult> UpsertLegalEntity(UpsertLegalEntityProfileCommand command, HttpContext http, IF4BillingService service)
    {
        var tenant = Tenant(http); if (tenant is null) return Unauthorized(http); var version = OptionalIfMatch(http, out var failure); if (failure is not null) return failure;
        return WithEtag(http, await service.UpsertLegalEntityAsync(tenant.TenantId, version, command, http.RequestAborted), x => x.Version);
    }

    private static async Task<IResult> UpsertPolicy(Guid connectionId, UpsertInvoicePolicyCommand command, HttpContext http, IF4BillingService service)
    {
        var tenant = Tenant(http); if (tenant is null) return Unauthorized(http); var version = OptionalIfMatch(http, out var failure); if (failure is not null) return failure;
        return WithEtag(http, await service.UpsertPolicyAsync(tenant.TenantId, connectionId, version, command, http.RequestAborted), x => x.Version);
    }

    private static async Task<IResult> EnqueueProtected(Guid _, ConfirmedAction command, HttpContext http, UserManager<ApplicationUser> users, Func<Guid, long, string, Task<ServiceResult<Guid>>> enqueue, bool requireVersion = true)
    {
        var tenant = Tenant(http); if (tenant is null) return Unauthorized(http);
        var missingKey = RequireIdempotency(http); if (missingKey is not null) return missingKey;
        if (!command.Confirmed) return Problem(http, new("EXPLICIT_CONFIRMATION_REQUIRED", "Dış mali işlem için açık onay zorunludur.", 422));
        var user = await users.FindByIdAsync(tenant.UserId.ToString());
        if (user is null || string.IsNullOrWhiteSpace(command.Password) || !await users.CheckPasswordAsync(user, command.Password)) return Problem(http, new("REAUTHENTICATION_FAILED", "İşlem için parola ile yeniden doğrulama başarısız.", 401));
        long version = 0;
        if (requireVersion && !TryIfMatch(http, out version, out var failure)) return failure!;
        return Accepted(await enqueue(tenant.TenantId, version, http.Request.Headers["Idempotency-Key"].ToString()));
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
    private static IResult Stream(ServiceResult<(Stream Content, string MimeType, string FileName)> result, HttpContext http) { if (!result.Succeeded) return Problem(http, result.Error!); http.Response.Headers.CacheControl = "private, no-store"; return Results.File(result.Value.Content, result.Value.MimeType, result.Value.FileName, enableRangeProcessing: false); }
    private static IResult Unauthorized(HttpContext http) => Problem(http, new("AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401));
    private static IResult MissingContext(HttpContext http) => Tenant(http) is null ? Unauthorized(http) : RequireIdempotency(http) ?? Problem(http, new("REQUEST_INVALID", "İstek tamamlanamadı.", 400));
    private static IResult Problem(HttpContext http, ServiceError error) => Results.Json(ToProblem(error, http.TraceIdentifier), statusCode: error.Status, contentType: "application/problem+json");
    private static object ToProblem(ServiceError error, string? correlationId) => new { type = $"https://marketplacehub.invalid/problems/{error.Code.ToLowerInvariant().Replace('_', '-')}", title = error.Message, status = error.Status, code = error.Code, correlationId, retryable = error.Status is 429 or >= 500, fieldErrors = error.FieldErrors };
    public sealed record ConfirmedAction(string Password, bool Confirmed);
}
