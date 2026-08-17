using System.Text.Json;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.ErrorMapping;

internal static class TrendyolEFaturamProblemDetails
{
    private const int MaximumBodyBytes = 16 * 1024;
    private const int MaximumReferenceLength = 240;

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
            if (!document.RootElement.TryGetProperty("instance", out var instance) || instance.ValueKind != JsonValueKind.String) return null;
            return Normalize(instance.GetString());
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = value.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute))
            path = absolute.AbsolutePath + (string.IsNullOrEmpty(absolute.Query) ? absolute.Fragment : string.Empty);
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0) path = path[..queryIndex];
        if (path.Length > MaximumReferenceLength) path = path[..MaximumReferenceLength];
        if (path.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_' or '.' or '#'))) return null;
        return path.StartsWith("/problem/", StringComparison.OrdinalIgnoreCase) ? $"problem:{path}" : null;
    }
}
