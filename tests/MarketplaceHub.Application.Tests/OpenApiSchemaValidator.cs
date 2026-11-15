using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-8: checks a real JSON answer against the schema the contract file (ApiContract/openapi.v1.json)
// declares for it. Strict where a generated client relies on the document: every member the answer writes must be
// declared (a schema with properties and no additionalProperties allows no others), every required member must be
// there, types (null included), formats (uuid, date-time, date, int32, int64), enums and patterns must hold.
internal sealed class OpenApiSchemaValidator
{
    private readonly JsonElement document;

    public OpenApiSchemaValidator(JsonElement document) => this.document = document;

    public static OpenApiSchemaValidator FromContractFile()
    {
        var text = File.ReadAllText(Path.Combine(TestRepository.TestProject(), "ApiContract", "openapi.v1.json"));
        return new OpenApiSchemaValidator(JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>The response object the document declares for the operation and status, or null when it declares none.</summary>
    public JsonElement? Response(string method, string path, int status)
    {
        if (!document.GetProperty("paths").TryGetProperty(path, out var item)) throw new InvalidOperationException($"{path} is not in the document.");
        if (!item.TryGetProperty(method.ToLowerInvariant(), out var operation)) throw new InvalidOperationException($"{method} {path} is not in the document.");
        if (!operation.GetProperty("responses").TryGetProperty(status.ToString(CultureInfo.InvariantCulture), out var response)) return null;
        return Resolve(response);
    }

    public JsonElement Operation(string method, string path) => document.GetProperty("paths").GetProperty(path).GetProperty(method.ToLowerInvariant());

    public IReadOnlyList<string> Validate(JsonElement value, JsonElement schema, string at = "$")
    {
        var errors = new List<string>();
        Check(value, schema, at, errors);
        return errors;
    }

    private JsonElement Resolve(JsonElement node)
    {
        while (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("$ref", out var reference))
        {
            var target = document;
            foreach (var part in reference.GetString()!.TrimStart('#', '/').Split('/')) target = target.GetProperty(part);
            node = target;
        }
        return node;
    }

    private void Check(JsonElement value, JsonElement schema, string at, List<string> errors)
    {
        schema = Resolve(schema);
        if (schema.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            var matches = oneOf.EnumerateArray().Count(option => Validate(value, option, at).Count == 0);
            if (matches != 1) errors.Add($"{at}: {matches} of the oneOf options match {Show(value)}.");
            return;
        }
        if (schema.TryGetProperty("type", out var type))
        {
            var allowed = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(entry => entry.GetString()!).ToHashSet() : [type.GetString()!];
            if (!allowed.Contains(KindOf(value)) && !(KindOf(value) == "integer" && allowed.Contains("number")))
            {
                errors.Add($"{at}: {KindOf(value)} {Show(value)} where the schema says {string.Join("|", allowed)}.");
                return;
            }
        }
        if (value.ValueKind == JsonValueKind.Null) return;
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(choice, value)))
            errors.Add($"{at}: {Show(value)} is not one of the enum values.");
        if (schema.TryGetProperty("format", out var format)) CheckFormat(value, format.GetString()!, at, errors);
        if (value.ValueKind == JsonValueKind.String && schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(value.GetString()!, pattern.GetString()!))
            errors.Add($"{at}: {Show(value)} does not match {pattern.GetString()}.");
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var element in value.EnumerateArray()) Check(element, items, $"{at}[{index++}]", errors);
        }
        if (value.ValueKind == JsonValueKind.Object) CheckObject(value, schema, at, errors);
    }

    private void CheckObject(JsonElement value, JsonElement schema, string at, List<string> errors)
    {
        var hasProperties = schema.TryGetProperty("properties", out var properties);
        if (schema.TryGetProperty("required", out var required))
            foreach (var member in required.EnumerateArray().Select(entry => entry.GetString()!))
                if (!value.TryGetProperty(member, out _)) errors.Add($"{at}: required member {member} is missing.");
        var hasAdditional = schema.TryGetProperty("additionalProperties", out var additional);
        foreach (var member in value.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(member.Name, out var memberSchema)) Check(member.Value, memberSchema, $"{at}.{member.Name}", errors);
            else if (hasAdditional) { if (additional.ValueKind == JsonValueKind.Object) Check(member.Value, additional, $"{at}.{member.Name}", errors); }
            else if (hasProperties) errors.Add($"{at}: member {member.Name} is not declared.");
        }
    }

    private static void CheckFormat(JsonElement value, string format, string at, List<string> errors)
    {
        var ok = (format, value.ValueKind) switch
        {
            ("uuid", JsonValueKind.String) => Guid.TryParseExact(value.GetString(), "D", out _),
            ("date-time", JsonValueKind.String) => value.GetString()!.Contains('T') && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            ("date", JsonValueKind.String) => DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            ("int32", JsonValueKind.Number) => value.TryGetInt32(out _),
            ("int64", JsonValueKind.Number) => value.TryGetInt64(out _),
            ("double" or "float", JsonValueKind.Number) => value.TryGetDouble(out var number) && double.IsFinite(number),
            _ => true
        };
        if (!ok) errors.Add($"{at}: {Show(value)} is not a valid {format}.");
    }

    private static string KindOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Number => value.TryGetInt64(out _) && !value.GetRawText().Contains('.') && !value.GetRawText().Contains('e', StringComparison.OrdinalIgnoreCase) ? "integer" : "number",
        JsonValueKind.String => "string",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        _ => "undefined"
    };

    private static string Show(JsonElement value) => value.GetRawText() is { Length: > 80 } text ? text[..80] + "…" : value.GetRawText();
}
