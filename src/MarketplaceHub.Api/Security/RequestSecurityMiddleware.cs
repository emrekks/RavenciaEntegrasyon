using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Infrastructure.Security;

namespace MarketplaceHub.Api.Security;

public sealed class RequestSecurityMiddleware(RequestDelegate next)
{
    public const string ProductionCsrfCookie = "__Host-MarketplaceHub-CSRF";
    public const string PilotLocalCsrfCookie = "MarketplaceHub-CSRF-PILOT-LOCAL";
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; base-uri 'self'";
        if (!SafeMethods.Contains(context.Request.Method) && context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/api/v1/hooks"))
        {
            if (!SameOrigin(context) || !ValidCsrf(context))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { type = "https://marketplacehub.invalid/problems/request-verification", title = "Request verification failed", status = 400, code = "REQUEST_VERIFICATION_FAILED", retryable = true }, context.RequestAborted);
                return;
            }
        }
        await next(context);
    }

    public static string IssueCsrf(HttpResponse response)
    {
        var token = TokenHasher.NewToken();
        var secure = !string.Equals(response.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["MARKETPLACEHUB_ENVIRONMENT"], "PILOT_LOCAL", StringComparison.OrdinalIgnoreCase) || response.HttpContext.Request.IsHttps;
        response.Cookies.Append(CsrfCookieName(response.HttpContext), token, new CookieOptions { HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.FromMinutes(30) });
        return token;
    }

    private static bool ValidCsrf(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CsrfCookieName(context), out var cookie) || !context.Request.Headers.TryGetValue("X-CSRF-TOKEN", out var header)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(header.ToString()));
    }

    private static string CsrfCookieName(HttpContext context) =>
        string.Equals(context.RequestServices.GetRequiredService<IConfiguration>()["MARKETPLACEHUB_ENVIRONMENT"], "PILOT_LOCAL", StringComparison.OrdinalIgnoreCase) && !context.Request.IsHttps
            ? PilotLocalCsrfCookie
            : ProductionCsrfCookie;

    private static bool SameOrigin(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("Origin", out var origin) || string.IsNullOrWhiteSpace(origin)) return true;
        if (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri)) return false;
        if (IsPilotLocalHttp(context))
        {
            return string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                && uri.Port == 5173
                && (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal));
        }
        return string.Equals(uri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPilotLocalHttp(HttpContext context) =>
        string.Equals(context.RequestServices.GetRequiredService<IConfiguration>()["MARKETPLACEHUB_ENVIRONMENT"], "PILOT_LOCAL", StringComparison.OrdinalIgnoreCase) && !context.Request.IsHttps;
}
