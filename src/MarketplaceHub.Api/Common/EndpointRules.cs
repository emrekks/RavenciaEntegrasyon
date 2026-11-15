using MarketplaceHub.Api.Security;

namespace MarketplaceHub.Api.Common;

/// <summary>Who may call an endpoint (API-GUVENLIK-8): the role gate as endpoint metadata instead of path text.</summary>
public enum ApiGate
{
    /// <summary>No session needed: sign-in, webhooks (route token), public document links.</summary>
    Open,
    /// <summary>Any signed-in role, READONLY included; the endpoint refuses a caller without a tenant (401).</summary>
    SignedIn,
    /// <summary>OWNER, ADMINISTRATOR, OPERATIONS, ACCOUNTING.</summary>
    Working,
    /// <summary>OWNER, ADMINISTRATOR, OPERATIONS.</summary>
    Operations,
    /// <summary>OWNER, ADMINISTRATOR, ACCOUNTING.</summary>
    Accounting,
    /// <summary>OWNER, ADMINISTRATOR.</summary>
    Elevated,
    /// <summary>OWNER.</summary>
    Owner
}

/// <summary>Endpoint metadata: the gate <see cref="RoleAuthorizationMiddleware"/> holds the endpoint's callers to.</summary>
public sealed class RequiredGate
{
    private static readonly Dictionary<ApiGate, RequiredGate> All = Enum.GetValues<ApiGate>().ToDictionary(gate => gate, gate => new RequiredGate(gate));

    private RequiredGate(ApiGate gate)
    {
        Gate = gate;
        Roles = gate switch
        {
            ApiGate.Open or ApiGate.SignedIn => null,
            ApiGate.Working => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OWNER", "ADMINISTRATOR", "OPERATIONS", "ACCOUNTING" },
            ApiGate.Operations => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OWNER", "ADMINISTRATOR", "OPERATIONS" },
            ApiGate.Accounting => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OWNER", "ADMINISTRATOR", "ACCOUNTING" },
            ApiGate.Elevated => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OWNER", "ADMINISTRATOR" },
            ApiGate.Owner => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OWNER" },
            _ => throw new ArgumentOutOfRangeException(nameof(gate))
        };
    }

    public ApiGate Gate { get; }

    /// <summary>The roles let through, or null when the gate checks no role (Open, SignedIn).</summary>
    public IReadOnlySet<string>? Roles { get; }

    public static RequiredGate For(ApiGate gate) => All[gate];

    public override string ToString() => Gate.ToString();
}

/// <summary>Endpoint metadata: the write refuses a request without an Idempotency-Key (EndpointHelpers.RequireIdempotency).</summary>
public sealed class IdempotencyKeyRequired
{
    public static readonly IdempotencyKeyRequired Instance = new();
    private IdempotencyKeyRequired() { }
}

/// <summary>Endpoint metadata: the read calls a marketplace live and spends its quota (API-GUVENLIK-7).</summary>
public sealed class LiveMarketplaceReadMetadata
{
    public static readonly LiveMarketplaceReadMetadata Instance = new();
    private LiveMarketplaceReadMetadata() { }
}

public static class EndpointRules
{
    /// <summary>The gate this endpoint's callers must pass. It wins over a group's <see cref="WithGates{TBuilder}"/> default.</summary>
    public static TBuilder RequireGate<TBuilder>(this TBuilder builder, ApiGate gate) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(RequiredGate.For(gate));

    /// <summary>
    /// The gate of every endpoint of the group that names none itself: <paramref name="read"/> for GET and HEAD,
    /// <paramref name="write"/> for the rest.
    /// </summary>
    public static TBuilder WithGates<TBuilder>(this TBuilder builder, ApiGate read, ApiGate write) where TBuilder : IEndpointConventionBuilder
    {
        builder.Finally(endpoint =>
        {
            if (endpoint.Metadata.OfType<RequiredGate>().Any()) return;
            var methods = endpoint.Metadata.OfType<HttpMethodMetadata>().LastOrDefault()?.HttpMethods ?? [];
            var reads = methods.Count > 0 && methods.All(method => HttpMethods.IsGet(method) || HttpMethods.IsHead(method));
            endpoint.Metadata.Add(RequiredGate.For(reads ? read : write));
        });
        return builder;
    }

    /// <summary>
    /// Documents that the handler refuses a request without an Idempotency-Key (the check itself stays in the handler,
    /// where it runs after the tenant check, as it always has).
    /// </summary>
    public static TBuilder RequireIdempotencyKey<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(IdempotencyKeyRequired.Instance);

    /// <summary>A read answered by a live marketplace call: every role that works with orders, and only from the panel itself.</summary>
    public static TBuilder LiveMarketplaceRead<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(LiveMarketplaceReadMetadata.Instance).RequireGate(ApiGate.Working).RequireSameOriginFetch();
}
