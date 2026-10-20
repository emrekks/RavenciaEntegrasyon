using MarketplaceHub.Domain;

namespace MarketplaceHub.Api.Security;

// Irreversible, tenant-wide work needs a fresh proof of who is at the keyboard, not only a session cookie:
// emptying data (settings or connection data reset), deleting a connection with its data, and replacing a
// connection's credentials. The session must have signed in or re-authenticated (POST /auth/reauthenticate:
// password, and the authenticator code when one is on) within the last five minutes. Otherwise the answer
// is 403 REAUTHENTICATION_REQUIRED; the panel asks for the password (and code) and sends the same request
// again. The check runs before the idempotency middleware, so the refusal is not stored as the key's result.
public sealed class StepUpMiddleware(RequestDelegate next)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    public const string Code = "REAUTHENTICATION_REQUIRED";

    public async Task InvokeAsync(HttpContext context, TimeProvider time)
    {
        if (context.Items["UserSession"] is UserSession session && RequiresStepUp(context.Request.Method, context.Request.Path.Value)
            && !IsRecent(session, time.GetUtcNow()))
        {
            await ApiProblems.WriteAsync(context, Code, "Bu işlem için parolanızla (ve açıksa doğrulayıcı kodunuzla) yeniden doğrulama gerekir.", StatusCodes.Status403Forbidden,
                new Dictionary<string, object?> { ["reauthenticateWithinSeconds"] = (int)Window.TotalSeconds });
            return;
        }
        await next(context);
    }

    internal static bool IsRecent(UserSession session, DateTimeOffset now) => session.ReauthenticatedAt is { } at && at >= now - Window;

    // Segments compare whole, empty ones dropped, like RoleAuthorizationMiddleware.
    internal static bool RequiresStepUp(string method, string? path)
    {
        var segments = (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 || !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) || !segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase)) return false;
        var area = segments[2].ToLowerInvariant();
        var last = segments[^1].ToLowerInvariant();
        if (HttpMethods.IsPost(method))
            return area == "settings" && segments.Length == 4 && last == "data-reset"
                || area == "connections" && segments.Length == 5 && last is "data-reset" or "deep-delete";
        if (HttpMethods.IsPut(method))
            return area == "connections" && segments.Length == 5 && last == "credential";
        return false;
    }
}
