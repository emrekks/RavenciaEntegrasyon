using System.Text;
using System.Text.Json.Nodes;
using MarketplaceHub.Api.Security;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace MarketplaceHub.Api.Common;

// API-GUVENLIK-8: what every operation of the document says beyond its route and body, read from the endpoint's
// metadata so it follows the code: a stable operationId, the headers the pipeline reads (X-CSRF-TOKEN,
// Idempotency-Key, If-Match), the error answers (all in the one ApiProblem shape ApiProblems writes) and the paging
// format of a list. Nothing here changes what the API does; it only describes it.
public sealed class OpenApiOperationContract : IOpenApiOperationTransformer
{
    public const string ProblemSchema = "ApiProblem";

    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var description = context.Description;
        var method = (description.HttpMethod ?? "GET").ToUpperInvariant();
        var path = "/" + (description.RelativePath ?? string.Empty).TrimStart('/');
        var metadata = description.ActionDescriptor.EndpointMetadata;
        var gate = metadata.OfType<RequiredGate>().LastOrDefault()?.Gate ?? ApiGate.Open;
        var read = method is "GET" or "HEAD";
        var hasRouteValue = path.Contains('{', StringComparison.Ordinal);
        var ifMatch = metadata.OfType<IfMatchMetadata>().LastOrDefault();
        var idempotencyRequired = metadata.OfType<IdempotencyKeyRequired>().Any();
        var underAuth = path.StartsWith("/api/v1/auth/", StringComparison.Ordinal);
        var underHooks = path.StartsWith("/api/v1/hooks/", StringComparison.Ordinal);
        var underPublic = path.StartsWith("/api/v1/public/", StringComparison.Ordinal);

        operation.OperationId = OperationId(method, path);
        // The generator tags an operation with its declaring class (or the assembly for a lambda); a client is grouped
        // by the API area instead: the first path segment after /api/v1.
        var area = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(2) ?? "api";
        operation.Tags = new HashSet<OpenApiTagReference> { new(area, context.Document) };

        operation.Parameters ??= [];
        if (!read && !underHooks)
            AddHeader(operation, "X-CSRF-TOKEN", true, "The token GET /api/v1/auth/csrf issued, with its cookie (every write but the webhooks).");
        if (!read && !underAuth && !underHooks)
            AddHeader(operation, "Idempotency-Key", idempotencyRequired, idempotencyRequired
                ? "Required: the write is refused without one (400 IDEMPOTENCY_KEY_REQUIRED); up to 256 characters. The same key replays the first answer."
                : "Optional: with one, a repeated request replays the first answer instead of running again; up to 256 characters.");
        if (ifMatch is not null)
            AddHeader(operation, "If-Match", ifMatch.Required, ifMatch.Required
                ? "The version read (\"v{version}\", the ETag of the read). Required: 428 PRECONDITION_REQUIRED without it."
                : "The version read (\"v{version}\"); without it the write does not check the version, unless the service requires one once a value is stored.");
        Paging.Describe(operation, path);
        // Unified filter names (FilterContract): the old spelling stays, deprecated, naming its replacement.
        foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>().Where(parameter => parameter.In == ParameterLocation.Query) ?? [])
        {
            if (FilterContract.ReplacementOf(DocumentPath(path), parameter.Name ?? string.Empty) is not { } replacement) continue;
            parameter.Deprecated = true;
            // The old single platform is read together with platforms on some lists and only without it on others: no rule.
            parameter.Description = parameter.Name == "platform"
                ? $"Use {replacement}; kept for older browser tabs."
                : $"Use {replacement}; kept for older browser tabs. When both are sent, {replacement} wins.";
        }
        foreach (var alternatives in metadata.OfType<ResponseAlternatives>())
            await DescribeAlternativesAsync(operation, context, alternatives, cancellationToken);

        // Every operation answers its errors in the ApiProblem shape; which statuses it can give follows its gate, its
        // route values and the headers it reads. A session-less write meets the role gate first (403, as the route table
        // pins); an endpoint that checks the tenant itself answers 401.
        var sessionStep = underAuth && !path.EndsWith("/csrf", StringComparison.Ordinal) && !path.EndsWith("/login", StringComparison.Ordinal);
        var statuses = new List<string> { "400" };
        if (gate != ApiGate.Open || sessionStep) statuses.Add("401");
        if (gate is not (ApiGate.Open or ApiGate.SignedIn) || metadata.OfType<SameOriginFetchRequired>().Any() || sessionStep) statuses.Add("403");
        if (hasRouteValue || underPublic) statuses.Add("404");
        if (idempotencyRequired || ifMatch is not null || !read && !underAuth && !underHooks) statuses.Add("409");
        if (ifMatch is not null) statuses.Add("412");
        if (!read) statuses.Add("422");
        if (ifMatch is not null) statuses.Add("428");
        statuses.Add("429");
        statuses.Add("500");
        operation.Responses ??= [];
        foreach (var status in statuses)
            if (!operation.Responses.ContainsKey(status)) operation.Responses[status] = new OpenApiResponseReference(ProblemResponseNames[status], context.Document);
    }

    // A success answer of several shapes (EndpointRules.ProducesOneOf): its JSON schema becomes a oneOf of the first
    // shape (already generated), the other named shapes (generated here and kept as components) and the JSON null.
    private static async Task DescribeAlternativesAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, ResponseAlternatives alternatives, CancellationToken cancellationToken)
    {
        var status = alternatives.Status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (operation.Responses?.TryGetValue(status, out var response) != true || response!.Content?.TryGetValue("application/json", out var media) != true || media!.Schema is null) return;
        var options = new List<IOpenApiSchema>();
        foreach (var type in alternatives.Types)
        {
            if (type is null) { options.Add(new OpenApiSchema { Type = JsonSchemaType.Null }); continue; }
            if (type == alternatives.Types.First(item => item is not null)) { options.Add(media.Schema); continue; }
            var document = context.Document!;
            document.Components ??= new OpenApiComponents();
            document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
            if (!document.Components.Schemas.ContainsKey(type.Name))
                document.Components.Schemas[type.Name] = await context.GetOrCreateSchemaAsync(type, null, cancellationToken);
            options.Add(new OpenApiSchemaReference(type.Name, document));
        }
        media.Schema = new OpenApiSchema { OneOf = options };
    }

    private static readonly Dictionary<string, (string Name, string Description)> ProblemResponseTable = new(StringComparer.Ordinal)
    {
        ["400"] = ("BadRequest", "The request is invalid: a body, parameter or header that cannot be read or is refused (INVALID_REQUEST, REQUEST_VERIFICATION_FAILED without the CSRF token, IDEMPOTENCY_KEY_REQUIRED, INVALID_ETAG, ...)."),
        ["401"] = ("Unauthenticated", "No signed-in session or no tenant (AUTHENTICATION_REQUIRED)."),
        ["403"] = ("Forbidden", "The caller's role or session may not do this (PERMISSION_DENIED), or another site started the request."),
        ["404"] = ("NotFound", "The record named in the path does not exist for this tenant (RESOURCE_NOT_FOUND and the domain's own codes)."),
        ["409"] = ("Conflict", "A conflict: the Idempotency-Key was used for another request or is still running, or the record's state or version has moved on."),
        ["412"] = ("PreconditionFailed", "If-Match names a version that is no longer the current one."),
        ["422"] = ("UnprocessableContent", "The request was read but its values are refused (VALIDATION_FAILED and the domain's own codes)."),
        ["428"] = ("PreconditionRequired", "If-Match is missing where the write needs it: always, or once a value is stored (PRECONDITION_REQUIRED)."),
        ["429"] = ("TooManyRequests", "Too many requests from this network or for this step (RATE_LIMITED)."),
        ["500"] = ("ServerError", "An unexpected server failure (INTERNAL_ERROR); retryable.")
    };

    private static readonly Dictionary<string, string> ProblemResponseNames = ProblemResponseTable.ToDictionary(entry => entry.Key, entry => entry.Value.Name, StringComparer.Ordinal);

    /// <summary>The shared error answers (components/responses), each an ApiProblem body.</summary>
    public static Dictionary<string, IOpenApiResponse> ProblemResponses(OpenApiDocument document) => ProblemResponseTable.Values.ToDictionary(
        entry => entry.Name,
        entry => (IOpenApiResponse)new OpenApiResponse
        {
            Description = entry.Description,
            Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
            {
                [ApiProblems.ContentType] = new() { Schema = new OpenApiSchemaReference(ProblemSchema, document) }
            }
        },
        StringComparer.Ordinal);

    /// <summary>
    /// The operation's id: the method in lower case and every path segment after /api/v1 in Pascal case, a route value
    /// as "By" + its name ("GET /api/v1/catalog/categories/{id:guid}" → getCatalogCategoriesById). A client generator
    /// names its functions by it, so it depends on nothing but the method and the route.
    /// </summary>
    public static string OperationId(string method, string path)
    {
        var builder = new StringBuilder(method.ToLowerInvariant());
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(2))
        {
            if (segment.StartsWith('{'))
            {
                var name = segment.Trim('{', '}', '*').Split(':')[0];
                builder.Append("By").Append(Pascal(name));
            }
            else
            {
                builder.Append(Pascal(segment));
            }
        }
        return builder.ToString();
    }

    private static string Pascal(string text)
    {
        var builder = new StringBuilder();
        foreach (var word in text.Split(['-', '.', '_'], StringSplitOptions.RemoveEmptyEntries))
            builder.Append(char.ToUpperInvariant(word[0])).Append(word.AsSpan(1));
        return builder.ToString();
    }

    private static void AddHeader(OpenApiOperation operation, string name, bool required, string description)
    {
        if (operation.Parameters!.Any(parameter => parameter.In == ParameterLocation.Header && string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))) return;
        operation.Parameters!.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = required,
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        });
    }

    /// <summary>The ApiProblem schema: the body ApiProblems.Body writes, plus the extension members some errors add.</summary>
    public static OpenApiSchema Problem() => new()
    {
        Type = JsonSchemaType.Object,
        Description = "Every error of the API (application/problem+json). Some errors add members (for example tenants on TENANT_SELECTION_REQUIRED).",
        Required = new HashSet<string>(StringComparer.Ordinal) { "type", "title", "status", "code", "correlationId", "retryable", "fieldErrors" },
        Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
        {
            ["type"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
            ["code"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "One of the codes in ApiErrorCatalog (or a service's own)." },
            ["correlationId"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
            ["retryable"] = new OpenApiSchema { Type = JsonSchemaType.Boolean, Description = "true for 429 and 5xx." },
            ["fieldErrors"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object | JsonSchemaType.Null,
                AdditionalProperties = new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchema { Type = JsonSchemaType.String } }
            }
        },
        // Any extension member (an empty schema: any value).
        AdditionalProperties = new OpenApiSchema()
    };

    /// <summary>A route as the document writes it: route constraints and catch-all marks dropped, no trailing slash.</summary>
    public static string DocumentPath(string route) =>
        System.Text.RegularExpressions.Regex.Replace(route, @"\{\*{0,2}([^}:]+)(:[^}]+)?\}", "{$1}") is { Length: > 1 } path ? path.TrimEnd('/') : route;

    // The paging formats on the wire (PagingContract): an x-paging extension naming the operation's format, and each
    // paging parameter's default and range as the endpoint reads it.
    private static class Paging
    {
        public static void Describe(OpenApiOperation operation, string path)
        {
            var query = operation.Parameters!.Where(parameter => parameter.In == ParameterLocation.Query).Select(parameter => parameter.Name ?? string.Empty).ToHashSet(StringComparer.Ordinal);
            var format = PagingContract.FormatOf(DocumentPath(path), query);
            if (format is null) return;
            operation.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
            operation.Extensions["x-paging"] = new JsonNodeExtension(JsonValue.Create(format.Value.Name));
            foreach (var parameter in operation.Parameters!.OfType<OpenApiParameter>().Where(parameter => parameter.In == ParameterLocation.Query))
            {
                // Unified paging: the old parameters stay for old browser tabs (deprecated in the document only); after is
                // the cursor the previous page answered.
                if (format.Value.Deprecated?.Contains(parameter.Name ?? string.Empty) == true) parameter.Deprecated = true;
                if (format.Value.TakesCursor && parameter.Name == "after")
                    parameter.Description = "The nextCursor of the previous page; wins over the page number. Refused (400) when it is not one, when it falls inside a page of the asked limit, or where the list's order gives no cursor (nextCursor null).";
                if (parameter.Schema is not OpenApiSchema schema) continue;
                if (format.Value.RangeOf(parameter.Name ?? string.Empty) is not { } range) continue;
                var invariant = System.Globalization.CultureInfo.InvariantCulture;
                var span = range.Maximum is { } top ? $"{range.Minimum}-{top}" : $"{range.Minimum} or more";
                if (range.Refused)
                {
                    schema.Minimum = range.Minimum.ToString(invariant);
                    if (range.Maximum is { } maximum) schema.Maximum = maximum.ToString(invariant);
                }
                if (range.Default is { } fallback) schema.Default = JsonValue.Create(fallback);
                parameter.Description = (range.Refused ? $"{span}; outside it the request is refused (400)." : $"{span}; the service brings any other value into it.")
                    + (range.Default is { } shown ? $" Default {shown}." : string.Empty) + (range.Note is { } note ? " " + note : string.Empty);
            }
        }
    }
}
