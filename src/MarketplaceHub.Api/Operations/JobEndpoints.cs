using MarketplaceHub.Application;

namespace MarketplaceHub.Api.Operations;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1/jobs");
        api.MapGet("", async (HttpContext http, IJobOperationsService service, int? limit, string? status) =>
            Tenant(http) is { } tenant
                ? Results.Ok(await service.ListAsync(tenant.TenantId, limit is >= 1 and <= 200 ? limit.Value : 50, status, http.RequestAborted))
                : Unauthorized(http));
        api.MapGet("/{id:guid}", async (Guid id, HttpContext http, IJobOperationsService service) =>
            Tenant(http) is { } tenant
                ? Result(http, await service.GetAsync(tenant.TenantId, id, http.RequestAborted))
                : Unauthorized(http));
        api.MapPost("/{id:guid}/retry", async (Guid id, HttpContext http, IJobOperationsService service) =>
            Tenant(http) is { } tenant
                ? RequireIdempotency(http) ?? Result(http, await service.RetryAsync(tenant.TenantId, id, http.RequestAborted))
                : Unauthorized(http));
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
}
