using MarketplaceHub.Application;

namespace MarketplaceHub.Api.Security;

// One error shape for every endpoint and middleware: application/problem+json with type, title, status,
// code, correlationId, retryable and field errors, plus optional extra members (for example the tenant
// choices of TENANT_SELECTION_REQUIRED). The correlation id is read from the request when the result
// runs, so helpers that do not hold the HttpContext still answer with it.
public static class ApiProblems
{
    public const string ContentType = "application/problem+json";

    public static IResult Of(HttpContext? http, ServiceError error, IReadOnlyDictionary<string, object?>? extensions = null) => new ProblemResult(error, extensions);

    public static IResult Of(HttpContext? http, string code, string message, int status, IReadOnlyDictionary<string, object?>? extensions = null) =>
        Of(http, new ServiceError(code, message, status), extensions);

    public static IResult Unauthenticated(HttpContext? http) => Of(http, "AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401);

    public static IResult NotFound(HttpContext? http, string message = "Kayıt bulunamadı.") => Of(http, "RESOURCE_NOT_FOUND", message, 404);

    // For middleware that answers before an endpoint runs.
    public static Task WriteAsync(HttpContext http, ServiceError error, IReadOnlyDictionary<string, object?>? extensions = null)
    {
        http.Response.StatusCode = error.Status;
        return http.Response.WriteAsJsonAsync(Body(error, http.TraceIdentifier, extensions), (System.Text.Json.JsonSerializerOptions?)null, ContentType, http.RequestAborted);
    }

    public static Task WriteAsync(HttpContext http, string code, string message, int status, IReadOnlyDictionary<string, object?>? extensions = null) =>
        WriteAsync(http, new ServiceError(code, message, status), extensions);

    public static Dictionary<string, object?> Body(ServiceError error, string? correlationId, IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["type"] = $"https://marketplacehub.invalid/problems/{error.Code.ToLowerInvariant().Replace('_', '-')}",
            ["title"] = error.Message,
            ["status"] = error.Status,
            ["code"] = error.Code,
            ["correlationId"] = correlationId,
            ["retryable"] = error.Status is 429 or >= 500,
            ["fieldErrors"] = error.FieldErrors
        };
        if (extensions is not null)
        {
            foreach (var (key, value) in extensions) body.TryAdd(key, value);
        }
        return body;
    }

    private sealed class ProblemResult(ServiceError error, IReadOnlyDictionary<string, object?>? extensions) : IResult, IStatusCodeHttpResult, IContentTypeHttpResult
    {
        public int? StatusCode => error.Status;
        public string? ContentType => ApiProblems.ContentType;
        public Task ExecuteAsync(HttpContext httpContext) => WriteAsync(httpContext, error, extensions);
    }
}
