namespace MarketplaceHub.Domain;

// The cost and revenue categories of a marketplace's accounting records. The raw type always stays on the record;
// a record no rule recognises is OTHER, never dropped.
public static class MarketplaceFinanceCategories
{
    public const string Sale = "SALE";
    public const string Return = "RETURN";
    public const string Discount = "DISCOUNT";
    public const string Coupon = "COUPON";
    public const string Commission = "COMMISSION";
    public const string Cargo = "CARGO";
    public const string ServiceFee = "SERVICE_FEE";
    // The card payment fees of the own store (the payment provider's and Shopify's): estimated, never a marketplace record.
    public const string PaymentFee = "PAYMENT_FEE";
    public const string Stoppage = "STOPPAGE";
    public const string Penalty = "PENALTY";
    public const string EarlyPayment = "EARLY_PAYMENT";
    public const string Advertising = "ADVERTISING";
    // A charge to the seller whose kind no rule names (Trendyol "Yurtdışı Operasyon Iade Bedeli"): a deduction invoice
    // is always a charge, so an unrecognised one is a cost, not OTHER.
    public const string OtherDeduction = "OTHER_DEDUCTION";
    public const string Other = "OTHER";

    public static readonly IReadOnlyList<string> All = [Sale, Return, Discount, Coupon, Commission, Cargo, ServiceFee, PaymentFee, Stoppage, Penalty, EarlyPayment, Advertising, OtherDeduction, Other];

    // The categories that are a cost to the seller; they add up to the report's real cost total. OTHER is shown apart:
    // an unrecognised record may be a correction in either direction.
    public static readonly IReadOnlyList<string> Costs = [Commission, Cargo, ServiceFee, PaymentFee, Stoppage, Penalty, EarlyPayment, Advertising, OtherDeduction];
}

// One record of a marketplace's current account statement (Trendyol "Cari Hesap Ekstresi"), as the marketplace sent
// it. Source tells the service it came from (SETTLEMENTS or OTHER_FINANCIALS); the external id is unique per source
// within a connection. RequestedType is the type the record was asked for (Sale, DeductionInvoices, ...);
// TransactionType is the marketplace's own name for it ("Satış", "Kargo Faturası"). Debt lowers the seller's balance,
// credit raises it.
public sealed class MarketplaceFinanceTransaction
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string PlatformCode { get; set; }
    public required string Source { get; set; }
    public required string ExternalId { get; set; }
    public required string RequestedType { get; set; }
    public string? TransactionSubType { get; set; }
    public string? TransactionType { get; set; }
    public required string Category { get; set; }
    public DateTimeOffset TransactionDate { get; set; }
    public string? Description { get; set; }
    public string? OrderNumber { get; set; }
    public string? ShipmentPackageId { get; set; }
    public string? Barcode { get; set; }
    public decimal Debt { get; set; }
    public decimal Credit { get; set; }
    public decimal? CommissionRate { get; set; }
    public decimal? CommissionAmount { get; set; }
    public decimal? SellerRevenue { get; set; }
    public string? PaymentOrderId { get; set; }
    public DateTimeOffset? PaymentDate { get; set; }
    public required string RawJson { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// One line of a marketplace cargo invoice (Trendyol "Kargo Faturası" items): the parcel, its order and what was charged.
// The lines of an invoice are replaced together, so LineNo is their order in the marketplace's answer.
public sealed class MarketplaceCargoInvoiceItem
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string InvoiceSerialNumber { get; set; }
    public int LineNo { get; set; }
    public string? ShipmentPackageType { get; set; }
    public string? ParcelUniqueId { get; set; }
    public string? OrderNumber { get; set; }
    public decimal Amount { get; set; }
    public int? Desi { get; set; }
    public required string RawJson { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
}

// How far a connection's accounting records have been read: every record dated in [CoveredFrom, CoveredThrough) was
// read and stored completely. LastSyncedAt: when a run last brought the coverage up to its run time.
public sealed class MarketplaceFinanceSyncState
{
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public DateTimeOffset? CoveredFrom { get; set; }
    public DateTimeOffset? CoveredThrough { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    // Transaction types the marketplace refused (400) in the last run; their records are missing.
    public string? RejectedTypes { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public long Version { get; set; } = 1;
}
