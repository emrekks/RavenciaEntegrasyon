using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using MarketplaceHub.Api.Marketplace;
using Microsoft.AspNetCore.RateLimiting;

namespace MarketplaceHub.Api.Security;

// Every limit counts per sending network (an IPv4 address, or an IPv6 /64; see ClientNetwork):
//   - "auth": sign-in, 10 per minute;
//   - "auth-step": second factor, re-authentication, password change and authenticator changes, 10 per
//     minute per session (per network without one), so a sign-in burst does not use up the second step;
//   - global: every API and SignalR request, RateLimits:PerNetworkPerMinute (default 1800) per minute,
//     webhooks excepted; they have their own chain (WebhookRateLimits) and a concurrency cap;
//   - media: product image content (the public links marketplaces fetch while a bulk publish runs, and the
//     panel's thumbnails) does not use up the global allowance: it has its own, generous one,
//     RateLimits:MediaPerNetworkPerMinute (default 6000), so an office behind one NAT browsing products
//     or a marketplace fetching thousands of images is not answered with 429 on everything else.
// The limiter runs before the session lookup and the idempotency middleware, so a refused request costs
// no database round trip and no buffered body.
public static class RateLimitPolicies
{
    public const int SignInPerMinute = 10;
    public const int StepsPerMinute = 10;
    public const int DefaultPerNetworkPerMinute = 1800;
    public const string PerNetworkSetting = "RateLimits:PerNetworkPerMinute";
    public const int DefaultMediaPerNetworkPerMinute = 6000;
    public const string MediaPerNetworkSetting = "RateLimits:MediaPerNetworkPerMinute";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static void Configure(RateLimiterOptions options, IConfiguration configuration)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = (context, _) => new ValueTask(ApiProblems.WriteAsync(context.HttpContext, "RATE_LIMITED", "Çok fazla istek gönderildi. Kısa süre sonra yeniden deneyin.", StatusCodes.Status429TooManyRequests));
        options.AddPolicy(AuthEndpoints.SignInRateLimit, context => FixedWindow($"sign-in:{ClientNetwork.PartitionKey(context)}", SignInPerMinute));
        options.AddPolicy(AuthEndpoints.StepRateLimit, context => FixedWindow(StepPartition(context), StepsPerMinute));
        var perNetwork = configuration.GetValue<int?>(PerNetworkSetting) is int configured && configured > 0 ? configured : DefaultPerNetworkPerMinute;
        var media = configuration.GetValue<int?>(MediaPerNetworkSetting) is int configuredMedia && configuredMedia > 0 ? configuredMedia : DefaultMediaPerNetworkPerMinute;
        options.GlobalLimiter = PartitionedRateLimiter.CreateChained(WebhookRateLimits.Create(), WebhookRateLimits.Concurrency(), PerNetwork(perNetwork, media));
    }

    public static PartitionedRateLimiter<HttpContext> PerNetwork(int perMinute, int mediaPerMinute = DefaultMediaPerNetworkPerMinute) => PartitionedRateLimiter.Create<HttpContext, string>(context =>
        IsMediaContent(context.Request)
            ? RateLimitPartition.GetFixedWindowLimiter($"media:{ClientNetwork.PartitionKey(context)}", _ => new FixedWindowRateLimiterOptions { PermitLimit = mediaPerMinute, Window = Window, QueueLimit = 0 })
            : IsLimitedPath(context.Request.Path)
                ? RateLimitPartition.GetFixedWindowLimiter($"network:{ClientNetwork.PartitionKey(context)}", _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = Window, QueueLimit = 0 })
                : RateLimitPartition.GetNoLimiter(string.Empty));

    // GET /api/v1/public/product-media/{id}/content and /api/v1/files/product-media/{id}/content.
    internal static bool IsMediaContent(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && (request.Path.StartsWithSegments("/api/v1/public/product-media", StringComparison.OrdinalIgnoreCase, out var rest)
            || request.Path.StartsWithSegments("/api/v1/files/product-media", StringComparison.OrdinalIgnoreCase, out rest))
        && rest.Value is { } tail && tail.Split('/', StringSplitOptions.RemoveEmptyEntries) is [_, var last] && string.Equals(last, "content", StringComparison.OrdinalIgnoreCase);

    // The session cookie, hashed, names the bucket: the session is not looked up yet when the limiter runs.
    internal static string StepPartition(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(SessionAuthMiddleware.CookieName(context), out var token) && !string.IsNullOrEmpty(token))
            return "step:session:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..32];
        return "step:network:" + ClientNetwork.PartitionKey(context);
    }

    private static bool IsLimitedPath(PathString path) =>
        (path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs")) && !path.StartsWithSegments("/api/v1/hooks");

    private static RateLimitPartition<string> FixedWindow(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = Window, QueueLimit = 0 });
}
