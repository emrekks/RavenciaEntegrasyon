using System.Text.Json;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.ErrorMapping;

internal static class TrendyolEFaturamProblemDetails
{
    private const int MaximumBodyBytes = 16 * 1024;
    private const int MaximumReferenceLength = 240;
    internal const string ApplicationMismatchReference = "problem:/etransformation/gateway/application-mismatch";

    public static async Task<string?> TryReadReferenceAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumBodyBytes) return null;

        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            using var limited = new MemoryStream();
            var buffer = new byte[2048];
            while (limited.Length <= MaximumBodyBytes)
            {
                var remaining = MaximumBodyBytes + 1 - (int)limited.Length;
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0) break;
                await limited.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (limited.Length > MaximumBodyBytes) return null;

            limited.Position = 0;
            using var document = await JsonDocument.ParseAsync(limited, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("instance", out var instance) && instance.ValueKind == JsonValueKind.String)
            {
                var normalized = Normalize(instance.GetString());
                if (normalized is not null) return normalized;
            }

            // Documented provider problems may expose the stable identifier in
            // `type` while omitting `instance`. Keep only that allowlisted path;
            // never persist the free-form response body.
            if (document.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                var normalized = Normalize(type.GetString());
                if (normalized is not null) return normalized;
            }

            // Validation responses commonly omit `instance` and expose only a
            // field/code pair. Preserve those non-secret identifiers so a Stage
            // rejection is diagnosable without logging the response body.
            if (TryGetProperty(document.RootElement, "errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var error in errors.EnumerateArray())
                {
                    var field = SafeSegment(error, "field");
                    var code = SafeSegment(error, "code");
                    if (field is not null || code is not null)
                        return $"validation:{field ?? "unknown"}:{code ?? "rejected"}";
                }
            }

            // ASP.NET-compatible validation problems use an object whose keys
            // are field paths and whose values are message arrays. Retain only
            // the safe field identifier; never store the messages.
            if (errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var error in errors.EnumerateObject())
                {
                    var field = SafeSegment(error.Name);
                    if (field is not null) return $"validation:{field}:rejected";
                }
            }

            var problemCode = SafeSegment(document.RootElement, "code");
            if (problemCode is not null) return $"provider:{problemCode}";

            // ProblemDetails `detail` and provider-specific `message` carry the
            // actionable validation reason for some 400 responses. Persist only
            // a short, character-allowlisted diagnostic; values containing e-mail
            // addresses, URLs or other unsafe punctuation are discarded.
            var detail = SafeDiagnostic(document.RootElement, "detail")
                ?? SafeDiagnostic(document.RootElement, "message");
            if (detail is not null) return $"provider-detail:{detail}";

            var title = SafeWords(document.RootElement, "title");
            return title is null ? null : $"provider-title:{title}";
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = value.Trim();
        if (!path.StartsWith('/') && Uri.TryCreate(path, UriKind.Absolute, out var absolute))
        {
            // The provider documents this stable ProblemDetails type outside the
            // historical /problem/* namespace. Accept only the official host and
            // exact path so no arbitrary response URL is persisted.
            if (string.Equals(absolute.Host, "api.trendyol.com", StringComparison.OrdinalIgnoreCase)
                && string.Equals(absolute.AbsolutePath, "/etransformation/gateway/application-mismatch", StringComparison.OrdinalIgnoreCase))
                return ApplicationMismatchReference;
            path = absolute.AbsolutePath + (string.IsNullOrEmpty(absolute.Query) ? absolute.Fragment : string.Empty);
        }
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0) path = path[..queryIndex];
        if (path.Length > MaximumReferenceLength) path = path[..MaximumReferenceLength];
        if (path.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_' or '.' or '#'))) return null;
        if (string.Equals(path, "/etransformation/gateway/application-mismatch", StringComparison.OrdinalIgnoreCase))
            return ApplicationMismatchReference;
        return path.StartsWith("/problem/", StringComparison.OrdinalIgnoreCase) ? $"problem:{path}" : null;
    }

    private static string? SafeSegment(JsonElement parent, string name)
    {
        if (!TryGetProperty(parent, name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var segment = value.GetString()?.Trim();
        return SafeSegment(segment);
    }

    private static string? SafeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var segment = value.Trim().Replace("[", ".", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal);
        if (segment.Length > 80) segment = segment[..80];
        return segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') ? segment : null;
    }

    private static bool TryGetProperty(JsonElement parent, string name, out JsonElement value)
    {
        foreach (var property in parent.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }

    private static string? SafeWords(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var words = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(words)) return null;
        if (words.Length > 80) words = words[..80];
        if (!words.All(character => char.IsAsciiLetterOrDigit(character) || character is ' ' or '-' or '_' or '.')) return null;
        return string.Join('-', words.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    private static string? SafeDiagnostic(JsonElement parent, string name)
    {
        if (!TryGetProperty(parent, name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var diagnostic = string.Join(' ', (value.GetString() ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(diagnostic)) return null;
        if (diagnostic.Length > MaximumReferenceLength) diagnostic = diagnostic[..MaximumReferenceLength];
        return diagnostic.All(character => char.IsLetterOrDigit(character) || character is ' ' or '-' or '_' or '.' or ':' or ',' or '[' or ']' or '(' or ')' or '/' or '%')
            ? diagnostic
            : null;
    }
}
