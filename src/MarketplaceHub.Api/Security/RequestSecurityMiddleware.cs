using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Infrastructure.Security;

namespace MarketplaceHub.Api.Security;

public sealed class RequestSecurityMiddleware(RequestDelegate next)
{
    public const string CsrfCookie = "__Host-MarketplaceHub-CSRF";
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; base-uri 'self'";
        if (!SafeMethods.Contains(context.Request.Method) && context.Request.Path.StartsWithSegments("/api"))
        {
            if (!SameOrigin(context) || !ValidCsrf(context))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { type = "https://marketplacehub.invalid/problems/request-verification", title = "Request verification failed", status = 400 }, context.RequestAborted);
                return;
            }
        }
        await next(context);
    }

    public static string IssueCsrf(HttpResponse response)
    {
        var token = TokenHasher.NewToken();
        response.Cookies.Append(CsrfCookie, token, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.FromMinutes(30) });
        return token;
    }

    private static bool ValidCsrf(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CsrfCookie, out var cookie) || !context.Request.Headers.TryGetValue("X-CSRF-TOKEN", out var header)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(header.ToString()));
    }

    private static bool SameOrigin(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("Origin", out var origin) || string.IsNullOrWhiteSpace(origin)) return true;
        return Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
