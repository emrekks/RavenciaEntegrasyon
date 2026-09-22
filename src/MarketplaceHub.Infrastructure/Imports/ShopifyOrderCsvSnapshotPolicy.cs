using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Imports;

internal static class ShopifyOrderCsvSnapshotPolicy
{
    private const string ImportKey = "shopifyCsvImport";

    public static string ImportCustomer(
        string currentJson,
        string? customerName,
        string? email,
        string? phone,
        string? financialStatus,
        string? fulfillmentStatus,
        string? paymentMethod,
        decimal? grossTotal,
        decimal? netTotal,
        decimal? discountAmount,
        string? currency,
        DateTimeOffset importedAt)
    {
        var root = ParseObject(currentJson);
        Set(root, "customerName", customerName);
        Set(root, "email", email);
        Set(root, "phone", phone);
        var marker = root[ImportKey] as JsonObject ?? new JsonObject();
        marker["source"] = "SHOPIFY_CSV";
        marker["importedAt"] = importedAt.ToString("O", CultureInfo.InvariantCulture);
        Set(marker, "financialStatus", financialStatus);
        Set(marker, "fulfillmentStatus", fulfillmentStatus);
        Set(marker, "paymentMethod", paymentMethod);
        Set(marker, "grossTotal", grossTotal);
        Set(marker, "netTotal", netTotal);
        Set(marker, "discountAmount", discountAmount);
        Set(marker, "currency", currency);
        root[ImportKey] = marker;
        return root.ToJsonString();
    }

    public static string ImportAddress(string currentJson, ShopifyOrderCsvAddress address)
    {
        var root = ParseObject(currentJson);
        Set(root, "name", address.Name);
        Set(root, "street", address.Street);
        Set(root, "address1", address.Address1);
        Set(root, "address2", address.Address2);
        Set(root, "company", address.Company);
        Set(root, "city", address.City);
        Set(root, "zip", address.Zip);
        Set(root, "province", address.Province);
        Set(root, "country", address.Country);
        Set(root, "phone", address.Phone);
        return root.ToJsonString();
    }

    public static string ImportLine(string? currentJson, ShopifyOrderCsvLine imported, DateTimeOffset importedAt)
    {
        var root = ParseObject(currentJson);
        var marker = new JsonObject
        {
            ["source"] = "SHOPIFY_CSV",
            ["importedAt"] = importedAt.ToString("O", CultureInfo.InvariantCulture),
            ["title"] = imported.Title
        };
        Set(marker, "sku", imported.Sku);
        Set(marker, "unitPrice", imported.UnitPrice);
        root[ImportKey] = marker;
        return root.ToJsonString();
    }

    public static string MergeRemoteSnapshot(string remoteJson, string? existingJson)
    {
        var remote = ParseNode(remoteJson);
        var existing = ParseNode(existingJson);
        return MergeMissing(remote, existing)?.ToJsonString() ?? "{}";
    }

    public static bool HasImport(string? snapshotJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(snapshotJson) ? "{}" : snapshotJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(ImportKey, out var marker)
                && marker.ValueKind == JsonValueKind.Object
                && marker.TryGetProperty("source", out var source)
                && source.GetString() == "SHOPIFY_CSV";
        }
        catch (JsonException) { return false; }
    }

    public static string? ImportedText(string? snapshotJson, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(snapshotJson) ? "{}" : snapshotJson);
            if (document.RootElement.TryGetProperty(ImportKey, out var marker)
                && marker.ValueKind == JsonValueKind.Object
                && marker.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    public static decimal? ImportedAmount(string? snapshotJson, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(snapshotJson) ? "{}" : snapshotJson);
            if (document.RootElement.TryGetProperty(ImportKey, out var marker)
                && marker.ValueKind == JsonValueKind.Object
                && marker.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetDecimal(out var amount))
                return amount;
        }
        catch (JsonException) { }
        return null;
    }

    public static (decimal GrossAmount, decimal DiscountAmount, decimal NetAmount) MergeRemoteAmounts(
        string? importedSnapshot,
        decimal remoteGrossAmount,
        decimal remoteDiscountAmount,
        decimal remoteNetAmount)
    {
        if (!HasImport(importedSnapshot)) return (remoteGrossAmount, remoteDiscountAmount, remoteNetAmount);
        var importedGross = ImportedAmount(importedSnapshot, "grossTotal");
        var importedDiscount = ImportedAmount(importedSnapshot, "discountAmount");
        var importedNet = ImportedAmount(importedSnapshot, "netTotal");
        var remoteMissing = remoteGrossAmount == 0 && remoteDiscountAmount == 0 && remoteNetAmount == 0;
        var remoteMatchesCsv = importedNet is { } expectedNet
            && (remoteNetAmount == expectedNet || remoteGrossAmount == expectedNet)
            && (importedDiscount is null || remoteDiscountAmount == importedDiscount.Value);
        if (!remoteMissing && !remoteMatchesCsv)
            return (remoteGrossAmount, remoteDiscountAmount, remoteNetAmount);
        return (
            importedGross ?? remoteGrossAmount,
            importedDiscount ?? remoteDiscountAmount,
            importedNet ?? remoteNetAmount);
    }

    public static string? PreserveRemoteSku(string remoteValue, OrderLine existing) =>
        (string.IsNullOrWhiteSpace(remoteValue) || string.Equals(remoteValue, "SHOPIFY-LINE", StringComparison.OrdinalIgnoreCase))
            && HasImport(existing.SourceSnapshotJson)
            ? ImportedText(existing.SourceSnapshotJson, "sku") ?? existing.Sku
            : remoteValue;

    public static string PreserveRemoteTitle(string remoteValue, OrderLine existing) =>
        (string.IsNullOrWhiteSpace(remoteValue) || string.Equals(remoteValue.Trim(), "Shopify ürünü", StringComparison.OrdinalIgnoreCase))
            && HasImport(existing.SourceSnapshotJson)
            ? ImportedText(existing.SourceSnapshotJson, "title") ?? existing.TitleSnapshot
            : remoteValue;

    private static JsonObject ParseObject(string? json) => ParseNode(json) as JsonObject ?? new JsonObject();

    private static JsonNode? ParseNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static JsonNode? MergeMissing(JsonNode? remote, JsonNode? existing)
    {
        if (existing is null) return remote?.DeepClone();
        if (IsEmpty(remote)) return existing.DeepClone();
        if (remote is JsonObject remoteObject && existing is JsonObject existingObject)
        {
            var merged = (JsonObject)remoteObject.DeepClone();
            foreach (var (key, existingValue) in existingObject)
            {
                if (!merged.TryGetPropertyValue(key, out var remoteValue)) merged[key] = existingValue?.DeepClone();
                else if (remoteValue is JsonObject && existingValue is JsonObject)
                    merged[key] = MergeMissing(remoteValue, existingValue);
                else if (IsEmpty(remoteValue) && !IsEmpty(existingValue)) merged[key] = existingValue?.DeepClone();
            }
            return merged;
        }
        return remote?.DeepClone();
    }

    private static bool IsEmpty(JsonNode? node) => node is null
        || node is JsonValue value && value.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text);

    private static void Set(JsonObject target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) target[key] = value.Trim();
    }

    private static void Set(JsonObject target, string key, decimal? value)
    {
        if (value is not null) target[key] = value.Value;
    }
}
