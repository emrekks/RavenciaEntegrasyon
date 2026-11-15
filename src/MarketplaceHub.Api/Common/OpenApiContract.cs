using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace MarketplaceHub.Api.Common;

// Keeps the OpenAPI description stable and limited to the panel's API: a fixed title and version, no server list (it
// would name whichever host generated it), the host-only /internal routes left out, and paths and components in
// ordinal order so the contract file changes only when a route or a type does.
//
// It also makes the response schemas say what the serializer writes (API-GUVENLIK-8). The API's JSON options are the
// web defaults (camelCase, nulls written, enums - none on the wire - as numbers), which READ a number from a string
// too, so the generator describes every number as "integer or string". An answer always carries a JSON number: in a
// schema only answers use, the string alternative and its pattern are dropped. And an answer writes every member, so
// there every non-nullable member is required; a nullable one is not, because GET /products leaves its null members
// out (CatalogEndpoints.ProductListJson). Schemas requests use as well keep their request reading, except that a
// nullable member is never required.
public sealed class OpenApiContract : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo { Title = "Ravencia Marketplace Hub API", Version = "v1" };
        document.Servers = [];
        var paths = new OpenApiPaths();
        foreach (var (path, item) in document.Paths.Where(entry => entry.Key.StartsWith("/api/v1/", StringComparison.Ordinal)).OrderBy(entry => entry.Key, StringComparer.Ordinal))
            paths.Add(path, item);
        document.Paths = paths;
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        document.Components.Schemas[OpenApiOperationContract.ProblemSchema] = OpenApiOperationContract.Problem();
        document.Components.Responses = new SortedDictionary<string, IOpenApiResponse>(OpenApiOperationContract.ProblemResponses(document), StringComparer.Ordinal);
        DescribeAnswers(document);
        document.Components.Schemas = new SortedDictionary<string, IOpenApiSchema>(document.Components.Schemas, StringComparer.Ordinal);
        return Task.CompletedTask;
    }

    private static void DescribeAnswers(OpenApiDocument document)
    {
        var components = document.Components!.Schemas!;
        var answered = new HashSet<string>(StringComparer.Ordinal);
        var requested = new HashSet<string>(StringComparer.Ordinal);
        var inlineAnswers = new List<IOpenApiSchema>();
        foreach (var operation in document.Paths.Values.SelectMany(item => item.Operations?.Values.AsEnumerable() ?? []))
        {
            foreach (var (status, response) in operation.Responses ?? new OpenApiResponses())
            {
                if (!status.StartsWith('2')) continue;
                foreach (var media in response.Content?.Values ?? [])
                {
                    if (media.Schema is null) continue;
                    inlineAnswers.Add(media.Schema);
                    Reach(media.Schema, components, answered);
                }
            }
            foreach (var media in operation.RequestBody?.Content?.Values ?? [])
                if (media.Schema is not null) Reach(media.Schema, components, requested);
            foreach (var parameter in operation.Parameters ?? [])
                if (parameter.Schema is not null) Reach(parameter.Schema, components, requested);
        }

        foreach (var schema in inlineAnswers) Narrow(schema, followComponents: false);
        foreach (var name in answered)
        {
            if (components[name] is not OpenApiSchema schema) continue;
            var onlyAnswered = !requested.Contains(name);
            if (onlyAnswered) Narrow(schema, followComponents: false);
            if (schema.Properties is not { Count: > 0 } properties) continue;
            var required = properties.Where(property => !IsNullable(property.Value) && (onlyAnswered || schema.Required?.Contains(property.Key) == true)).Select(property => property.Key);
            schema.Required = OrderLike(required.ToHashSet(StringComparer.Ordinal), properties.Keys);
        }
        foreach (var name in requested.Where(name => !answered.Contains(name)))
        {
            if (components[name] is not OpenApiSchema { Properties: { Count: > 0 } properties, Required: { Count: > 0 } } schema) continue;
            schema.Required = OrderLike(schema.Required.Where(member => !properties.TryGetValue(member, out var property) || !IsNullable(property)).ToHashSet(StringComparer.Ordinal), properties.Keys);
        }
    }

    // The set in the order the properties are written, as the generator lists them.
    private static HashSet<string>? OrderLike(ISet<string> members, IEnumerable<string> order)
    {
        var ordered = new HashSet<string>(order.Where(members.Contains), StringComparer.Ordinal);
        return ordered.Count > 0 ? ordered : null;
    }

    private static bool IsNullable(IOpenApiSchema schema) =>
        schema.Type is { } type && type.HasFlag(JsonSchemaType.Null)
        || schema.OneOf?.Any(option => option.Type == JsonSchemaType.Null) == true;

    private static void Reach(IOpenApiSchema schema, IDictionary<string, IOpenApiSchema> components, HashSet<string> reached)
    {
        if (schema is OpenApiSchemaReference reference)
        {
            var id = reference.Reference.Id;
            if (id is null || !reached.Add(id) || !components.TryGetValue(id, out var target)) return;
            Reach(target, components, reached);
            return;
        }
        foreach (var child in Children(schema)) Reach(child, components, reached);
    }

    private static IEnumerable<IOpenApiSchema> Children(IOpenApiSchema schema)
    {
        if (schema is OpenApiSchemaReference) yield break;
        foreach (var property in schema.Properties?.Values ?? []) yield return property;
        if (schema.Items is not null) yield return schema.Items;
        if (schema.AdditionalProperties is not null) yield return schema.AdditionalProperties;
        foreach (var option in (schema.OneOf ?? []).Concat(schema.AnyOf ?? []).Concat(schema.AllOf ?? [])) yield return option;
    }

    // A number an answer writes is a JSON number, never the string form the reader also accepts.
    private static void Narrow(IOpenApiSchema schema, bool followComponents)
    {
        if (schema is OpenApiSchemaReference) return;
        if (schema is OpenApiSchema { Type: { } type } concrete && type.HasFlag(JsonSchemaType.String) && (type.HasFlag(JsonSchemaType.Integer) || type.HasFlag(JsonSchemaType.Number)))
        {
            concrete.Type = type & ~JsonSchemaType.String;
            concrete.Pattern = null;
        }
        foreach (var child in Children(schema)) Narrow(child, followComponents);
    }
}
