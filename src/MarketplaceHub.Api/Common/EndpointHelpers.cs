using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;

namespace MarketplaceHub.Api.Common;

// The helpers every endpoint file used to carry its own copy of (API-GUVENLIK-8): the caller's tenant, the shared
// refusals, paging arguments, the Idempotency-Key and If-Match checks, and ServiceResult → IResult. Endpoint files
// import them with `using static`; a file that answers some errors differently (Marketplace maps a marketplace's
// status for the panel) passes its mapping. Every body, status and header is the one the copies produced.
public static class EndpointHelpers
{
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 200;
    public const int MaximumPageNumber = 100_000;

    /// <summary>The signed-in user's tenant, or null without one (the endpoint answers <see cref="Unauthorized"/>).</summary>
    public static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;

    /// <summary>401 AUTHENTICATION_REQUIRED, the one answer for a caller without a tenant.</summary>
    public static IResult Unauthorized(HttpContext http) => ApiProblems.Unauthenticated(http);

    public static IResult Problem(HttpContext http, ServiceError error) => ApiProblems.Of(http, error);

    /// <summary>The cursor lists' limit: 50 when omitted, else 1-200 (an ArgumentException answered 400 INVALID_REQUEST).</summary>
    public static int PageSize(int? limit) => limit is null ? DefaultPageSize : limit is >= 1 and <= MaximumPageSize ? limit.Value : throw new ArgumentException("limit 1-200 arasında olmalıdır.");

    /// <summary>A 1-based page number: 1 when omitted, else 1-100000.</summary>
    public static int PageNumber(int? page) => page is null ? 1 : page is >= 1 and <= MaximumPageNumber ? page.Value : throw new ArgumentException("page 1-100000 arasında olmalıdır.");

    /// <summary>
    /// The refusal for a write that must carry an Idempotency-Key, or null when it has a usable one. A key over 256
    /// characters is refused earlier by IdempotencyMiddleware with the same answer.
    /// </summary>
    public static IResult? RequireIdempotency(HttpContext http) => string.IsNullOrWhiteSpace(http.Request.Headers["Idempotency-Key"])
        ? Problem(http, new("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key başlığı zorunludur.", 400))
        : http.Request.Headers["Idempotency-Key"].ToString().Length > 256
            ? Problem(http, new("IDEMPOTENCY_KEY_INVALID", "Idempotency-Key en fazla 256 karakterdir.", 400))
            : null;

    /// <summary>The If-Match version a write must name: false with 428 PRECONDITION_REQUIRED when missing, 400 INVALID_ETAG when malformed.</summary>
    public static bool TryIfMatch(HttpContext http, out long version, out IResult? failure)
    {
        var parsed = OptionalIfMatch(http, out failure);
        version = parsed ?? 0;
        if (failure is null && parsed is null) failure = Problem(http, new("PRECONDITION_REQUIRED", "If-Match gereklidir.", 428));
        return failure is null;
    }

    /// <summary>An optional If-Match "v{version}": null when absent; a malformed one sets <paramref name="failure"/> (400 INVALID_ETAG).</summary>
    public static long? OptionalIfMatch(HttpContext http, out IResult? failure)
    {
        failure = null;
        var value = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length >= 4 && value.StartsWith("\"v", StringComparison.Ordinal) && value.EndsWith('"') && long.TryParse(value[2..^1], out var version) && version > 0) return version;
        failure = Problem(http, new("INVALID_ETAG", "If-Match güçlü ETag biçiminde olmalıdır: \"v{version}\".", 400));
        return null;
    }

    /// <summary>The same check returning the refusal: null and <paramref name="expected"/> set (or null when absent), or 400 INVALID_ETAG.</summary>
    public static IResult? IfMatchVersion(HttpContext http, out long? expected)
    {
        expected = OptionalIfMatch(http, out var failure);
        return failure;
    }

    /// <summary>200 with the value and its strong ETag "v{version}", or the error.</summary>
    public static IResult WithEtag<T>(HttpContext http, ServiceResult<T> result, Func<T, long> version, Func<ServiceError, ServiceError>? mapError = null)
    {
        if (!result.Succeeded) return Problem(http, Map(result.Error!, mapError));
        http.Response.Headers.ETag = $"\"v{version(result.Value!)}\"";
        return Results.Ok(result.Value);
    }

    /// <summary>201 with Location "{collection}/{id}" (the value's Id when it is a Guid, else the collection), or the error.</summary>
    public static IResult Created<T>(ServiceResult<T> result, string collection, Func<ServiceError, ServiceError>? mapError = null)
    {
        if (!result.Succeeded) return ApiProblems.Of(null, Map(result.Error!, mapError));
        var id = typeof(T).GetProperty("Id")?.GetValue(result.Value);
        return Results.Created(id is Guid guid ? $"{collection}/{guid:D}" : collection, result.Value);
    }

    /// <summary>202 for a queued job: Location /api/v1/jobs/{id} and { jobId }, or the error.</summary>
    public static IResult Accepted(ServiceResult<Guid> result, Func<ServiceError, ServiceError>? mapError = null) =>
        result.Succeeded ? Results.Accepted($"/api/v1/jobs/{result.Value:D}", new { jobId = result.Value }) : ApiProblems.Of(null, Map(result.Error!, mapError));

    /// <summary>The success answer <paramref name="success"/> builds, or the error.</summary>
    public static IResult Result<T>(ServiceResult<T> result, Func<T, IResult> success, Func<ServiceError, ServiceError>? mapError = null) =>
        result.Succeeded ? success(result.Value!) : ApiProblems.Of(null, Map(result.Error!, mapError));

    /// <summary>200 with the value, or the error.</summary>
    public static IResult Result<T>(ServiceResult<T> result) => result.Succeeded ? Results.Ok(result.Value) : ApiProblems.Of(null, result.Error!);

    /// <summary>200 with the value, or the error.</summary>
    public static IResult Result<T>(HttpContext http, ServiceResult<T> result) => result.Succeeded ? Results.Ok(result.Value) : Problem(http, result.Error!);

    /// <summary>
    /// The answer of a write whose guard expression failed: 401 without a tenant, else the Idempotency-Key refusal,
    /// else 400 REQUEST_INVALID.
    /// </summary>
    public static IResult MissingContext(HttpContext http) =>
        Tenant(http) is null ? Unauthorized(http) : RequireIdempotency(http) ?? Problem(http, new("REQUEST_INVALID", "İstek tamamlanamadı.", 400));

    /// <summary>
    /// Answers an ArgumentException a handler of the group throws as 400 (RequestArgumentError) inside the endpoint.
    /// ApiExceptionHandler gives the same body for every other group, but only after the exception has passed
    /// IdempotencyMiddleware, which then holds a keyed write's outcome as UNKNOWN for 7 days; answered here, the 400
    /// is recorded as a COMPLETED refusal and replayed. The Catalog, Invoicing and Marketplace groups keep that, so
    /// the filter stays on exactly those three (API-GUVENLIK-4 asked to remove it; that would change the replay).
    /// </summary>
    public static RouteGroupBuilder WithArgumentErrorsAnswered(this RouteGroupBuilder group) => group.AddEndpointFilter(async (context, next) =>
    {
        try { return await next(context); }
        catch (ArgumentException exception)
        {
            var (code, message) = RequestArgumentError.From(exception);
            return Problem(context.HttpContext, new(code, message, 400));
        }
    });

    private static ServiceError Map(ServiceError error, Func<ServiceError, ServiceError>? mapError) => mapError is null ? error : mapError(error);
}
