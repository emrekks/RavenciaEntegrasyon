using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Security;

// Every exception that leaves an endpoint is answered in the panel's error shape (ApiProblems: Turkish title, code,
// correlationId, retryable), never with the framework's English ProblemDetails:
//   - a body or parameters that cannot be read (malformed JSON, a value of the wrong type, a missing body): 400 / 413;
//   - a row another session changed meanwhile (DbUpdateConcurrencyException): 409 CONCURRENCY_CONFLICT;
//   - an ArgumentException, in every endpoint group (RequestArgumentError): 400;
//   - anything else: logged here with its stack trace and answered 500 INTERNAL_ERROR, without the exception's text.
// A request the client abandoned and a response already started keep the framework's handling.
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler>? logger = null) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (httpContext.Response.HasStarted) return false;
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested) return false;
        switch (exception)
        {
            case BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge }:
                await ApiProblems.WriteAsync(httpContext, "REQUEST_TOO_LARGE", "İstek gövdesi izin verilen boyutu aşıyor.", StatusCodes.Status413PayloadTooLarge);
                return true;
            case BadHttpRequestException:
                await ApiProblems.WriteAsync(httpContext, "INVALID_REQUEST_BODY", "İstek gövdesi veya parametreleri okunamadı; değerleri kontrol edip yeniden deneyin.", StatusCodes.Status400BadRequest);
                return true;
            case DbUpdateConcurrencyException:
                await ApiProblems.WriteAsync(httpContext, "CONCURRENCY_CONFLICT", "Kayıt başka bir oturumda değişti; sayfayı yenileyip yeniden deneyin.", StatusCodes.Status409Conflict);
                return true;
            case ArgumentException argument:
                var (code, message) = RequestArgumentError.From(argument);
                await ApiProblems.WriteAsync(httpContext, code, message, StatusCodes.Status400BadRequest);
                return true;
            default:
                // The framework does not log an exception a handler took (.NET 10), so it is logged here.
                logger?.LogError(exception, "Unhandled exception for {Method} {Path}.", httpContext.Request.Method, httpContext.Request.Path.Value);
                await ApiProblems.WriteAsync(httpContext, "INTERNAL_ERROR", $"Beklenmeyen bir hata oluştu; tekrar deneyin. Sürerse bu hata numarasını iletin: {httpContext.TraceIdentifier}", StatusCodes.Status500InternalServerError);
                return true;
        }
    }
}
