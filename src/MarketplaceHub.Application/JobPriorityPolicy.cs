namespace MarketplaceHub.Application;

/// <summary>
/// Single source of truth for integration job priorities (lower runs first).
/// The worker's hot lane takes priority 0..Worker:HotPriorityCeiling (default 2),
/// the background lane takes everything above it, the initial-data-sync lane takes -1
/// and the user lane takes BulkUserAction (-2) and below.
/// </summary>
public static class JobPriorityPolicy
{
    // Actions a user waits for (shipment and return actions, labels, a requested order refresh,
    // invoice submission and delivery) run in their own worker lane, ahead of everything else.
    public const int UserAction = -3;
    // Work in the same lane that nobody is waiting on one by one: a bulk invoice confirmation (150 invoices
    // become some 300 jobs) and the automatic steps that follow it. The lane leases lower numbers first, so
    // a single click waits at most for one running bulk job instead of behind the whole batch.
    public const int BulkUserAction = -2;
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
    public static bool IsUserAction(string jobType) => jobType is
        MarketplaceJobTypes.ShipmentAction or MarketplaceJobTypes.ReturnAction or MarketplaceJobTypes.CommonLabel or MarketplaceJobTypes.PriceInventorySync
        or MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.PazaramaOrderSync or MarketplaceJobTypes.PttAvmOrderSync
        or InvoicingJobTypes.InvoiceSubmit or InvoicingJobTypes.MarketplaceDelivery or InvoicingJobTypes.ManualOrderInvoiceDelivery;

    // Priority of a job a user requested from the panel.
    public static int ForUser(string jobType) => IsUserAction(jobType) ? UserAction : For(jobType, userInitiated: true);

    // A user-lane job requested in bulk (or produced automatically) runs after the single requests.
    public static int ForUserLane(bool bulk) => bulk ? BulkUserAction : UserAction;

    // An automatic follow-up (status check, document, delivery) keeps the pace of the job that started the
    // chain: a single user's invoice stays a single request, a bulk one or an unknown start stays bulk.
    public static int ForFollowUp(int? triggeringPriority) =>
        triggeringPriority is { } priority && priority <= UserAction ? UserAction : BulkUserAction;

    public static int For(string jobType, bool userInitiated) => jobType switch
    {
        // A background sweep (deadlines, adjustments, delivery recovery): it must not share the first-import slot
        // that user-waited invoice work also depends on.
        InvoicingJobTypes.InvoiceDueScan => Reconciliation,

        MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.PazaramaOrderSync or MarketplaceJobTypes.PttAvmOrderSync
            or MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.HepsiburadaOrderStatusSync or MarketplaceJobTypes.PazaramaOrderStatusSync or MarketplaceJobTypes.PttAvmOrderStatusSync
            or MarketplaceJobTypes.ShipmentAction or MarketplaceJobTypes.CommonLabel
            or MarketplaceJobTypes.WebhookIngest or MarketplaceJobTypes.ShopifyWebhookIngest or MarketplaceJobTypes.HepsiburadaWebhookIngest => Realtime,

        MarketplaceJobTypes.StockProjectionDispatch or MarketplaceJobTypes.PriceInventorySync => Writes,

        MarketplaceJobTypes.ProductCreate or MarketplaceJobTypes.ProductUpdate
            or MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.ReturnStatusSync or MarketplaceJobTypes.ReturnAction => Interactive,

        MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.N11OrderRecoverySync or MarketplaceJobTypes.PazaramaOrderRecoverySync or MarketplaceJobTypes.PttAvmOrderRecoverySync or MarketplaceJobTypes.HepsiburadaReturnSync => userInitiated ? Interactive : Default,

        MarketplaceJobTypes.OrderReconciliation or MarketplaceJobTypes.ShopifyOrderReconciliation
            or MarketplaceJobTypes.ReturnReconciliation or MarketplaceJobTypes.StockReconciliation
            or MarketplaceJobTypes.ProductArchive or MarketplaceJobTypes.ProductApprovalReconcile => Reconciliation,

        MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ShopifyProductSync or MarketplaceJobTypes.HepsiburadaProductSync
            or MarketplaceJobTypes.ReferenceSync or MarketplaceJobTypes.HepsiburadaReferenceSync or MarketplaceJobTypes.N11ReferenceSync or MarketplaceJobTypes.PazaramaReferenceSync or MarketplaceJobTypes.PttAvmReferenceSync
            or MarketplaceJobTypes.CatalogStructureImport => Catalog,

        MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync => Recovery,

        _ => Default
    };
}
