namespace MarketplaceHub.Api.Security;

/// <summary>Endpoint metadata: the endpoint refuses a request another site started (FetchMetadata.RequireSameOriginFetch).</summary>
public sealed class SameOriginFetchRequired
{
    public static readonly SameOriginFetchRequired Instance = new();
    private SameOriginFetchRequired() { }
}

public static class FetchMetadata
{
    // Reads that call a marketplace live (and spend its quota) are GETs, and the session cookie is
    // SameSite=Lax, so a link or image on another site could make a signed-in browser spend the quota.
    // Browsers say who started a request in Sec-Fetch-Site: only the panel itself (same-origin) or the
    // user typing the address (none) may start these. Clients that send no such header are not browsers
    // and are let through.
    public static bool IsStartedByAnotherSite(HttpRequest request)
    {
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        return site.Length > 0
            && !site.Equals("same-origin", StringComparison.OrdinalIgnoreCase)
            && !site.Equals("none", StringComparison.OrdinalIgnoreCase);
    }

    // The endpoint also carries SameOriginFetchRequired, so a test can check that every live read has the filter.
    public static TBuilder RequireSameOriginFetch<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(SameOriginFetchRequired.Instance).AddEndpointFilter((context, next) => IsStartedByAnotherSite(context.HttpContext.Request)
            ? ValueTask.FromResult<object?>(ApiProblems.Of(context.HttpContext, "CROSS_SITE_REQUEST_REFUSED", "Bu istek yalnız panelin kendisinden yapılabilir.", StatusCodes.Status403Forbidden))
            : next(context));

    // The queue monitor (/health/queues, 503 when a queue is stuck) is for the server's own network;
    // a request that came through the public edge carries a public client address and is not served.
    public static bool IsFromServerNetwork(HttpContext context) =>
        context.Connection.RemoteIpAddress is not { } address || !PublicNetwork.IsPublic(address);
}
