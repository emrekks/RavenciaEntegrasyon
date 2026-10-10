using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Identity;

namespace MarketplaceHub.Api.Operations;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1/jobs");
        api.MapGet("", async (HttpContext http, IJobOperationsService service, string? status) =>
            Tenant(http) is { } tenant
                ? Results.Ok(await service.ListAsync(tenant.TenantId, status, http.RequestAborted))
                : Unauthorized(http));
        api.MapGet("/{id:guid}", async (Guid id, HttpContext http, IJobOperationsService service) =>
            Tenant(http) is { } tenant
                ? Result(http, await service.GetAsync(tenant.TenantId, id, http.RequestAborted))
                : Unauthorized(http));
        api.MapPost("/{id:guid}/retry", async (Guid id, HttpContext http, IJobOperationsService service) =>
            Tenant(http) is { } tenant
                ? RequireIdempotency(http) ?? Result(http, await service.RetryAsync(tenant.TenantId, id, http.RequestAborted))
                : Unauthorized(http));
        api.MapPost("/{id:guid}/invoice-delivery-once", async (Guid id, OneTimeInvoiceDeliveryAction command, HttpContext http, IJobOperationsService service) =>
        {
            var tenant = Tenant(http);
            if (tenant is null) return Unauthorized(http);
            if (RequireIdempotency(http) is { } idempotencyFailure) return idempotencyFailure;
            if (!command.Confirmed) return Problem(http, new("EXPLICIT_CONFIRMATION_REQUIRED", "Bu tek seferlik dış fatura iletimi için açık onay zorunludur.", 422));
            if (!OneTimeInvoiceDeliveryPolicy.IsAuthorizedTarget(command.OrderNumber)) return Problem(http, new("ONE_TIME_INVOICE_ORDER_NOT_AUTHORIZED", "Tek seferlik fatura iletimi yalnızca yetkilendirilmiş test siparişleri için yetkilendirildi.", 403));
            var result = await service.EnqueueOneTimeInvoiceDeliveryAsync(
                tenant.TenantId,
                id,
                command.OrderNumber,
                http.Request.Headers["Idempotency-Key"].ToString(),
                http.TraceIdentifier,
                http.RequestAborted);
            return Result(http, result);
        });
        api.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext http, IJobOperationsService service) =>
            Tenant(http) is { } tenant
                ? RequireIdempotency(http) ?? Result(http, await service.CancelAsync(tenant.TenantId, id, http.RequestAborted))
                : Unauthorized(http));
        return endpoints;
    }

    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
    private static IResult? RequireIdempotency(HttpContext http) => string.IsNullOrWhiteSpace(http.Request.Headers["Idempotency-Key"])
        ? Problem(http, new("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key başlığı zorunludur.", 400))
        : http.Request.Headers["Idempotency-Key"].ToString().Length > 256
            ? Problem(http, new("IDEMPOTENCY_KEY_INVALID", "Idempotency-Key en fazla 256 karakterdir.", 400))
            : null;
    private static IResult Result<T>(HttpContext http, ServiceResult<T> result) => result.Succeeded ? Results.Ok(result.Value) : Problem(http, result.Error!);
    private static IResult Unauthorized(HttpContext http) => Problem(http, new("AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401));
    private static IResult Problem(HttpContext http, ServiceError error) => Results.Json(new
    {
        type = $"https://marketplacehub.invalid/problems/{error.Code.ToLowerInvariant().Replace('_', '-')}",
        title = error.Message,
        status = error.Status,
        code = error.Code,
        correlationId = http.TraceIdentifier,
        retryable = error.Status is 429 or >= 500,
        fieldErrors = error.FieldErrors
    }, statusCode: error.Status, contentType: "application/problem+json");

    public sealed record OneTimeInvoiceDeliveryAction(bool Confirmed, string OrderNumber);
}
