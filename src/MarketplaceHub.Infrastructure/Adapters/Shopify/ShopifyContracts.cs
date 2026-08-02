using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public static class ShopifyContract
{
    public const string PlatformCode = "SHOPIFY";
    public const string ApiVersion = "2026-07";
    public const string ConnectionTestJob = "SHOPIFY_CONNECTION_TEST";
    public const string OrderSyncJob = "SHOPIFY_ORDER_SYNC";
    public const string WebhookIngestJob = "SHOPIFY_WEBHOOK_INGEST";
    public const string VersionSource = "https://shopify.dev/docs/api/usage/versioning";

    public static bool TryNormalizeShopDomain(string? value, out string domain)
    {
        domain = value?.Trim().ToLowerInvariant() ?? "";
        return domain.Length > ".myshopify.com".Length && domain.EndsWith(".myshopify.com", StringComparison.Ordinal)
            && Uri.CheckHostName(domain) == UriHostNameType.Dns && !domain.Contains('/') && !domain.Contains(':');
    }
}

public static class ShopifyGraphQlContract
{
    public static IReadOnlyList<string> Errors(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array) return [];
        return errors.EnumerateArray().Select(x => x.TryGetProperty("message", out var message) ? message.GetString() ?? "GraphQL error" : "GraphQL error").ToArray();
    }

    public static IReadOnlyList<string> UserErrors(string json, string mutationField)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty(mutationField, out var mutation)
            || !mutation.TryGetProperty("userErrors", out var errors) || errors.ValueKind != JsonValueKind.Array) return [];
        return errors.EnumerateArray().Select(x => x.TryGetProperty("message", out var message) ? message.GetString() ?? "Mutation error" : "Mutation error").ToArray();
    }
}

public sealed record ShopifyBulkLine(long LineNumber, string Json, string Checkpoint);

public static class ShopifyBulkJsonl
{
    public static async IAsyncEnumerable<ShopifyBulkLine> ReadAsync(Stream stream, long completedLines = 0, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 16_384, leaveOpen: true);
        long lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            if (lineNumber <= completedLines || string.IsNullOrWhiteSpace(line)) continue;
            using var _ = JsonDocument.Parse(line);
            yield return new(lineNumber, line, lineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}

internal static class ShopifyFailures
{
    public static AdapterError Configuration(string message = "Shopify bağlantı ayarları veya şifreli credential eksik.") => new(AdapterErrorClass.Validation, "SHOPIFY_CONFIGURATION_INVALID", message, 422, null, null);
    public static AdapterError Closed(string message) => new(AdapterErrorClass.NotSupported, "SHOPIFY_CAPABILITY_UNVERIFIED", message, 422, null, null);
    public static AdapterError WriteClosed() => new(AdapterErrorClass.NotSupported, "EXTERNAL_WRITE_DISABLED", "Shopify dış yazmaları capability kanıtı ve açık yazma kapıları olmadan çalışmaz.", 422, null, null);
}
