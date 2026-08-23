namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed class TrendyolOptions
{
    public const string SectionName = "Trendyol";
    public Uri ProductionBaseAddress { get; init; } = new("https://apigw.trendyol.com/integration/");
    public Uri StageBaseAddress { get; init; } = new("https://stageapigw.trendyol.com/integration/");
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

internal static class TrendyolEndpoints
{
    public static string ProductCreate(string sellerId) => $"product/sellers/{Uri.EscapeDataString(sellerId)}/v2/products";
    public static string ProductUpdateUnapproved(string sellerId) => $"product/sellers/{Uri.EscapeDataString(sellerId)}/products/unapproved-bulk-update";
    public static string ProductUpdateContent(string sellerId) => $"product/sellers/{Uri.EscapeDataString(sellerId)}/products/content-bulk-update";
    public static string ProductUpdateVariant(string sellerId) => $"product/sellers/{Uri.EscapeDataString(sellerId)}/products/variant-bulk-update";
    public static string ProductUpdateDelivery(string sellerId) => $"product/sellers/{Uri.EscapeDataString(sellerId)}/products/delivery-info-bulk-update";
    public static string ProductArchiveState(string sellerId) => $"product/sellers/{Uri.EscapeDataString(sellerId)}/products/archive-state";
    public static string ApprovedProducts(string sellerId) => $"product/sellers/{Uri.EscapeDataString(sellerId)}/products/approved";
    public static string ProductByBarcode(string sellerId, string barcode) => $"product/sellers/{Uri.EscapeDataString(sellerId)}/product/{Uri.EscapeDataString(barcode)}";
    public static string UnapprovedProducts(string sellerId) => $"product/sellers/{Uri.EscapeDataString(sellerId)}/products/unapproved";
    public static string BatchResult(string sellerId, string batchId) => $"product/sellers/{Uri.EscapeDataString(sellerId)}/products/batch-requests/{Uri.EscapeDataString(batchId)}";
    public static string PriceAndInventory(string sellerId) => $"inventory/sellers/{Uri.EscapeDataString(sellerId)}/products/price-and-inventory";
    public static string OrderStream(string sellerId) => $"order/sellers/{Uri.EscapeDataString(sellerId)}/orders/stream";
    public static string Orders(string sellerId) => $"order/sellers/{Uri.EscapeDataString(sellerId)}/v2/orders";
    public static string ShipmentPackage(string sellerId, string packageId) => $"order/sellers/{Uri.EscapeDataString(sellerId)}/shipment-packages/{Uri.EscapeDataString(packageId)}";
    public static string ShipmentUnsupplied(string sellerId, string packageId) => ShipmentPackage(sellerId, packageId) + "/items/unsupplied";
    public static string ShipmentTrackingDetails(string sellerId, string packageId) => ShipmentPackage(sellerId, packageId) + "/tracking-details";
    public static string ShipmentSplit(string sellerId, string packageId) => ShipmentPackage(sellerId, packageId) + "/split";
    public static string ShipmentMultiSplit(string sellerId, string packageId) => ShipmentPackage(sellerId, packageId) + "/multi-split";
    public static string ShipmentCargoProvider(string sellerId, string packageId) => ShipmentPackage(sellerId, packageId) + "/cargo-providers";
    public static string ShipmentAlternativeDelivery(string sellerId, string packageId) => ShipmentPackage(sellerId, packageId) + "/alternative-delivery";
    public static string ShipmentManualDeliver(string sellerId, string packageId) => ShipmentPackage(sellerId, packageId) + "/manual-deliver";
    public static string ShipmentManualReturn(string sellerId, string packageId) => ShipmentPackage(sellerId, packageId) + "/manual-return";
    public static string CommonLabel(string sellerId, string cargoTrackingNumber) => $"sellers/{Uri.EscapeDataString(sellerId)}/common-label/{Uri.EscapeDataString(cargoTrackingNumber)}";
    public const string StageTestOrder = "test/order/orders/core";
    public static string Claims(string sellerId) => $"order/sellers/{Uri.EscapeDataString(sellerId)}/claims";
    public static string ApproveClaim(string sellerId, string claimId) => $"order/sellers/{Uri.EscapeDataString(sellerId)}/claims/{Uri.EscapeDataString(claimId)}/items/approve";
    public const string ClaimIssueReasons = "order/claim-issue-reasons";
    public static string RejectClaim(string sellerId, string claimId, string reasonId, IReadOnlyList<string> lineIds, string description) => $"order/sellers/{Uri.EscapeDataString(sellerId)}/claims/{Uri.EscapeDataString(claimId)}/issue?claimIssueReasonId={Uri.EscapeDataString(reasonId)}&claimItemIdList={Uri.EscapeDataString(string.Join(',', lineIds))}&description={Uri.EscapeDataString(description)}";
    public static string InvoiceLinks(string sellerId) => $"sellers/{Uri.EscapeDataString(sellerId)}/seller-invoice-links";
    public const string Categories = "product/product-categories";
    public const string Brands = "product/brands";
    public static string CategoryAttributes(string categoryId) => $"product/categories/{Uri.EscapeDataString(categoryId)}/attributes";
    public static string AttributeValues(string categoryId, string attributeId) => $"product/categories/{Uri.EscapeDataString(categoryId)}/attributes/{Uri.EscapeDataString(attributeId)}/values";
}

internal static class TrendyolReadStorefronts
{
    // The seller panel exposes Türkiye and international/micro-export packages
    // under separate storefront headers. Reads are intentionally broader than
    // writes; the latter remain guarded by IntegrationRuntimePolicy.
    public static readonly string[] Codes = ["TR", "AE", "SA", "GR", "DE", "BG", "QA", "KW", "OM", "BH", "AZ", "SK", "RO", "CZ"];
    // getClaims is a global claims endpoint in the documented V2 contract; the
    // storefront header is required for order stream reads, not claims reads.
    public static readonly string[] ReturnCodes = ["TR"];
    // Return hydration is a bounded fallback for the seller's Türkiye and UAE
    // order scopes. Other storefront orders arrive through the full order stream.
    public static readonly string[] ReturnOrderCodes = ["AE", "TR"];
}
