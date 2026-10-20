using Microsoft.AspNetCore.Diagnostics;

namespace MarketplaceHub.Api.Security;

// A request whose body or parameters cannot be read (malformed JSON, a value of the wrong type, a
// missing body) is the caller's mistake: it is answered with 400 in the panel's error shape instead
// of the bare 400 (or 500) the framework gives. Any other exception keeps the default handling.
public sealed class ApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badRequest || httpContext.Response.HasStarted) return false;
        if (badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge)
            await ApiProblems.WriteAsync(httpContext, "REQUEST_TOO_LARGE", "İstek gövdesi izin verilen boyutu aşıyor.", StatusCodes.Status413PayloadTooLarge);
        else
            await ApiProblems.WriteAsync(httpContext, "INVALID_REQUEST_BODY", "İstek gövdesi veya parametreleri okunamadı; değerleri kontrol edip yeniden deneyin.", StatusCodes.Status400BadRequest);
        return true;
    }
}
