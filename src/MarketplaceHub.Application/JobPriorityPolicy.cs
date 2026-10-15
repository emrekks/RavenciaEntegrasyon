namespace MarketplaceHub.Application;

/// <summary>
/// Single source of truth for integration job priorities (lower runs first).
/// The worker's hot lane takes priority 0..Worker:HotPriorityCeiling (default 2),
/// the background lane takes everything above it and the initial-data-sync lane takes -1.
/// </summary>
public static class JobPriorityPolicy
{
    public const int InitialDataSync = -1;
    public const int Realtime = 0;
    public const int Writes = 1;
    public const int Interactive = 2;
    public const int Default = 3;
    public const int Reconciliation = 4;
    public const int Catalog = 5;
    public const int Recovery = 6;

    /// <param name="userInitiated">
    /// True for jobs a user requested from the panel. A few recurring reads run in the hot lane
    /// when requested manually but stay in the background lane when the scheduler produces them.
    /// </param>
    public static int For(string jobType, bool userInitiated) => jobType switch
    {
        InvoicingJobTypes.InvoiceDueScan => InitialDataSync,

        MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.PazaramaOrderSync
            or MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.HepsiburadaOrderStatusSync or MarketplaceJobTypes.PazaramaOrderStatusSync
            or MarketplaceJobTypes.ShipmentAction or MarketplaceJobTypes.CommonLabel
            or MarketplaceJobTypes.WebhookIngest or MarketplaceJobTypes.ShopifyWebhookIngest or MarketplaceJobTypes.HepsiburadaWebhookIngest => Realtime,

        MarketplaceJobTypes.StockProjectionDispatch or MarketplaceJobTypes.PriceInventorySync => Writes,

        MarketplaceJobTypes.ProductCreate or MarketplaceJobTypes.ProductUpdate
            or MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.ReturnStatusSync or MarketplaceJobTypes.ReturnAction => Interactive,

        MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.N11OrderRecoverySync or MarketplaceJobTypes.PazaramaOrderRecoverySync or MarketplaceJobTypes.HepsiburadaReturnSync => userInitiated ? Interactive : Default,

        MarketplaceJobTypes.OrderReconciliation or MarketplaceJobTypes.ShopifyOrderReconciliation
            or MarketplaceJobTypes.ReturnReconciliation or MarketplaceJobTypes.StockReconciliation
            or MarketplaceJobTypes.ProductArchive or MarketplaceJobTypes.ProductApprovalReconcile => Reconciliation,

        MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ShopifyProductSync or MarketplaceJobTypes.HepsiburadaProductSync
            or MarketplaceJobTypes.ReferenceSync or MarketplaceJobTypes.HepsiburadaReferenceSync or MarketplaceJobTypes.N11ReferenceSync
            or MarketplaceJobTypes.CatalogStructureImport => Catalog,

        MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync => Recovery,

        _ => Default
    };
}
