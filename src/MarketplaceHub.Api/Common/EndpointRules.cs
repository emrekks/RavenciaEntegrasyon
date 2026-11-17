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

/// <summary>How a write that takes no required Idempotency-Key behaves when the same request comes twice.</summary>
public enum WriteRepeat
{
    /// <summary>Repeating it leaves the same state (a replacing PUT, a DELETE, a version-guarded change, a read-only POST).</summary>
    Repeatable,
    /// <summary>Each call has its own effect (a new secret, a sent message, a created record, a paid call).</summary>
    NotIdempotent
}

/// <summary>
/// Endpoint metadata (API-GUVENLIK-8 c): what a second identical call of a write does, with the reason. Every /api/v1
/// write declares either this or <see cref="IdempotencyKeyRequired"/> (ApiEndpointRulesTests). Declaring only: nothing
/// here makes a write require a key.
/// </summary>
public sealed class WriteIdempotency(WriteRepeat repeat, string reason)
{
    public WriteRepeat Repeat { get; } = repeat;
    public string Reason { get; } = reason;
}

/// <summary>The reasons most writes share (<see cref="EndpointRules.Idempotent{TBuilder}"/>).</summary>
public static class WriteReasons
{
    public const string VersionGuarded = "If-Match names the version it changes; a repeat is refused as stale (412/409)";
    public const string Replaces = "sets the state the body names; a repeat writes the same state";
    public const string Removes = "removes the record; a repeat finds it removed";
    public const string ReadOnly = "a read with a body: it changes nothing";
}

/// <summary>
/// Endpoint metadata (documentation only): the write reads If-Match "v{version}". Required: 428 PRECONDITION_REQUIRED
/// without it (EndpointHelpers.TryIfMatch); optional: a blind write when absent (OptionalIfMatch). Either way a stale
/// version is refused (412 or 409, per service) and a malformed one is 400 INVALID_ETAG.
/// </summary>
public sealed class IfMatchMetadata
{
    public static readonly IfMatchMetadata RequiredHeader = new(true);
    public static readonly IfMatchMetadata OptionalHeader = new(false);
    private IfMatchMetadata(bool required) => Required = required;

    public bool Required { get; }
}

/// <summary>Endpoint metadata: the read calls a marketplace live and spends its quota (API-GUVENLIK-7).</summary>
public sealed class LiveMarketplaceReadMetadata
{
    public static readonly LiveMarketplaceReadMetadata Instance = new();
    private LiveMarketplaceReadMetadata() { }
}

/// <summary>
/// Endpoint metadata (documentation only): the success answer of <see cref="Status"/> is one of several shapes, each a
/// named type or, for a null entry, the JSON null. The document writes it as a oneOf of the shapes.
/// </summary>
public sealed class ResponseAlternatives(int status, IReadOnlyList<Type?> types)
{
    public int Status { get; } = status;
    public IReadOnlyList<Type?> Types { get; } = types;
}

public static class EndpointRules
{
    /// <summary>
    /// Declares a success answer that is one of several shapes (a null entry: the JSON null). The first non-null type is
    /// also declared the usual way, so the document generates its schema; the rest are added by OpenApiOperationContract.
    /// </summary>
    public static RouteHandlerBuilder ProducesOneOf(this RouteHandlerBuilder builder, int status, params Type?[] types) =>
        builder.Produces(status, types.First(type => type is not null)).WithMetadata(new ResponseAlternatives(status, types));

    /// <summary>
    /// Declares a file answer: its body is the bytes of one of <paramref name="mediaTypes"/> (a string with format
    /// binary in the document), not JSON.
    /// </summary>
    public static RouteHandlerBuilder ProducesFile(this RouteHandlerBuilder builder, string mediaType, params string[] mediaTypes) =>
        builder.Produces<Stream>(StatusCodes.Status200OK, mediaType, mediaTypes);

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

    /// <summary>Declares a write without a required key whose repeat leaves the same state, and why.</summary>
    public static TBuilder Idempotent<TBuilder>(this TBuilder builder, string reason) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new WriteIdempotency(WriteRepeat.Repeatable, reason));

    /// <summary>Declares a write without a required key whose every call has its own effect, and why.</summary>
    public static TBuilder NotIdempotent<TBuilder>(this TBuilder builder, string reason) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new WriteIdempotency(WriteRepeat.NotIdempotent, reason));

    /// <summary>
    /// The declaration of every write of the group that makes none itself and requires no key: for groups whose writes
    /// all behave alike (settings that replace a stored document, mapping writes guarded by their version).
    /// </summary>
    public static TBuilder WithWriteIdempotency<TBuilder>(this TBuilder builder, WriteRepeat repeat, string reason) where TBuilder : IEndpointConventionBuilder
    {
        builder.Finally(endpoint =>
        {
            if (endpoint.Metadata.OfType<WriteIdempotency>().Any() || endpoint.Metadata.OfType<IdempotencyKeyRequired>().Any()) return;
            var methods = endpoint.Metadata.OfType<HttpMethodMetadata>().LastOrDefault()?.HttpMethods ?? [];
            if (methods.Count == 0 || methods.All(method => HttpMethods.IsGet(method) || HttpMethods.IsHead(method))) return;
            endpoint.Metadata.Add(new WriteIdempotency(repeat, reason));
        });
        return builder;
    }

    /// <summary>Documents the If-Match header the handler reads (the check itself stays in the handler).</summary>
    public static TBuilder IfMatch<TBuilder>(this TBuilder builder, bool required = true) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(required ? IfMatchMetadata.RequiredHeader : IfMatchMetadata.OptionalHeader);

    /// <summary>A read answered by a live marketplace call: every role that works with orders, and only from the panel itself.</summary>
    public static TBuilder LiveMarketplaceRead<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(LiveMarketplaceReadMetadata.Instance).RequireGate(ApiGate.Working).RequireSameOriginFetch();
}
