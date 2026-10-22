using System.Text.Json;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

/// <summary>
/// The narrow allowances needed to complete Hepsiburada's SIT certification on a STAGE
/// connection: a test order from an imported hbSku, a manual price/stock upload of the
/// imported offers, and a product import before PRODUCT_WRITE evidence exists. Every rule
/// is false for any other platform or environment, so a PRODUCTION Hepsiburada connection
/// keeps all of its existing gates.
/// </summary>
public static class HepsiburadaStageCertificationPolicy
{
    private const string InventoryUploadPrefix = "LISTING_INVENTORY:";
    private const string ProductUpdatePrefix = "PRODUCT_UPDATE:";

    public static bool IsStageHepsiburada(PlatformConnection connection) =>
        string.Equals(connection.PlatformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
        && IntegrationRuntimePolicy.IsStage(connection);

    // PRODUCT_WRITE evidence can only come from a product import made on the SIT account itself
    // (the connection test never writes), so a STAGE connection cannot demand it before that
    // import: SIT is where the evidence is produced. PRICE_WRITE and INVENTORY_WRITE are recorded
    // by the connection test and stay required; PRODUCTION keeps all three.
    public static string[] PublicationEvidenceCapabilities(PlatformConnection connection) =>
        IsStageHepsiburada(connection)
            ? new[] { MarketplaceCapabilities.PriceWrite, MarketplaceCapabilities.InventoryWrite }
            : new[] { MarketplaceCapabilities.ProductWrite, MarketplaceCapabilities.PriceWrite, MarketplaceCapabilities.InventoryWrite };

    // A Hepsiburada catalogue SKU (hbSku) starts with "HB", for example HBV0000KSJL98. Imported
    // links that carry the merchant SKU instead do not, and cannot be ordered or uploaded by hbSku.
    public static bool LooksLikeHepsiburadaSku(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        return trimmed.Length is > 2 and <= 100
            && trimmed.StartsWith("HB", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Any(char.IsWhiteSpace);
    }

    public static string? FirstHepsiburadaSku(IEnumerable<string?> externalIds) =>
        externalIds.Where(LooksLikeHepsiburadaSku).Select(value => value!.Trim()).FirstOrDefault();

    // A price/stock send started from the panel (manual payload) on a STAGE Hepsiburada connection is
    // the SIT inventory upload: it runs as a manual Stage operation instead of an automatic write, so
    // the global/connection write switches that guard automatic sends do not apply to it.
    public static bool IsManualStagePriceInventory(PlatformConnection connection, bool manualPayload) =>
        manualPayload && IsStageHepsiburada(connection);

    // The batch or tracking id a job's payload carries (Trendyol batchRequestId, Hepsiburada trackingId or
    // inventoryUploadId, PTT AVM trackingId, Pazarama batch id),
    // without the internal kind prefix, so it can be read back from the job detail. Null when absent.
    public static string? OperationIdForDisplay(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "ExternalOperationId", StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.String) continue;
                var value = property.Value.GetString()?.Trim();
                if (string.IsNullOrEmpty(value)) return null;
                if (value.StartsWith(InventoryUploadPrefix, StringComparison.Ordinal)) value = value[InventoryUploadPrefix.Length..];
                else if (value.StartsWith(ProductUpdatePrefix, StringComparison.Ordinal)) value = value[ProductUpdatePrefix.Length..];
                // PTT AVM tracking ids ("t:{trackingId}"); Pazarama batches ("b:{batchId}:{count}:{lines}") show their batch
                // id, and its "c:{lines}" marker (no remote batch) shows nothing. Trendyol's batchRequestId is plain.
                else if (value.StartsWith("t:", StringComparison.Ordinal)) value = value[2..];
                else if (value.StartsWith("b:", StringComparison.Ordinal)) value = value[2..].Split(':')[0];
                else if (value.StartsWith("c:", StringComparison.Ordinal)) return null;
                return value.Length == 0 ? null : value;
            }
            return null;
        }
        catch (JsonException) { return null; }
    }
}
