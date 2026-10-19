using MarketplaceHub.Application;

namespace MarketplaceHub.Api.Security;

// One error shape for every endpoint: application/problem+json with type, title, status, code,
// correlationId, retryable and field errors. Endpoint files that still keep their own copy produce the
// same body; new code uses this.
public static class ApiProblems
{
    public static IResult Of(HttpContext? http, ServiceError error) =>
        Results.Json(Body(error, http?.TraceIdentifier), statusCode: error.Status, contentType: "application/problem+json");

    public static IResult Of(HttpContext? http, string code, string message, int status) => Of(http, new ServiceError(code, message, status));

    public static IResult Unauthenticated(HttpContext? http) => Of(http, "AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401);

    public static object Body(ServiceError error, string? correlationId) => new
    {
        type = $"https://marketplacehub.invalid/problems/{error.Code.ToLowerInvariant().Replace('_', '-')}",
        title = error.Message,
        status = error.Status,
        code = error.Code,
        correlationId,
        retryable = error.Status is 429 or >= 500,
        fieldErrors = error.FieldErrors
    };
}
