namespace MarketplaceHub.Application;

// A record of Trendyol's current account statement (settlements or other financials), as read. Amounts are as sent;
// RawJson keeps the whole record so a later reading can use fields the mapping does not know yet.
public sealed record RemoteStatementRecord(
    string ExternalId,
    string Source,
    string RequestedType,
    string? TransactionSubType,
    string? TransactionType,
    DateTimeOffset TransactionDate,
    string? Description,
    string? OrderNumber,
    string? ShipmentPackageId,
    string? Barcode,
    decimal Debt,
    decimal Credit,
    decimal? CommissionRate,
    decimal? CommissionAmount,
    decimal? SellerRevenue,
    string? PaymentOrderId,
    DateTimeOffset? PaymentDate,
    string RawJson,
    string? Country = null,
    string? Affiliate = null,
    string? Currency = null,
    string? InvoiceNumber = null);

public sealed record RemoteStatementPage(IReadOnlyList<RemoteStatementRecord> Items, int Page, int TotalPages, long TotalElements, IReadOnlyList<RemoteSkippedFinanceRecord>? Skipped = null)
{
    public IReadOnlyList<RemoteSkippedFinanceRecord> Skipped { get; init; } = Skipped ?? [];
}

// A finance record a page sent without an id, a readable main amount or a date. It is not stored (nothing is made up
// for it) and is reported by name; the page's other records are kept. Key: the record's id, or a hash of the record
// when it has none. Description: Turkish, names the record and what it lacks.
public sealed record RemoteSkippedFinanceRecord(string Key, string Description);

public sealed record RemoteCargoInvoiceItem(string? ShipmentPackageType, string? ParcelUniqueId, string? OrderNumber, decimal Amount, int? Desi, string RawJson);

public sealed record RemoteCargoInvoicePage(IReadOnlyList<RemoteCargoInvoiceItem> Items, int Page, int TotalPages);

// A payout Trendyol made (payment-order: only paid ones are listed), as sent.
public sealed record RemotePayoutOrder(string ExternalId, string? Status, string? PaymentType, decimal Amount, DateTimeOffset? PayoutDate, string? RegionCode, string? RegionName, string RawJson);

public sealed record RemotePayoutOrderPage(IReadOnlyList<RemotePayoutOrder> Items, int Page, int TotalPages);

// Trendyol's accounting services (Cari Hesap Ekstresi), read only. A window may span at most 15 days.
public interface ITrendyolFinancePort
{
    Task<AdapterResult<RemoteStatementPage>> GetSettlementsAsync(AdapterContext context, string transactionType, DateTimeOffset start, DateTimeOffset end, int page, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteStatementPage>> GetOtherFinancialsAsync(AdapterContext context, string transactionType, string? transactionSubType, DateTimeOffset start, DateTimeOffset end, int page, CancellationToken cancellationToken);
    // The records of one payout, by its id instead of a date window (source: settlements or other financials).
    Task<AdapterResult<RemoteStatementPage>> GetPayoutRecordsAsync(AdapterContext context, string source, string transactionType, string? transactionSubType, string paymentOrderId, int page, CancellationToken cancellationToken);
    // The paid payouts, ten a page.
    Task<AdapterResult<RemotePayoutOrderPage>> GetPayoutOrdersAsync(AdapterContext context, int page, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteCargoInvoicePage>> GetCargoInvoiceItemsAsync(AdapterContext context, string invoiceSerialNumber, int page, CancellationToken cancellationToken);
}

// n11's settlement days (GetSettlementList) and a day's order item lines (GetSettlementDetail), as sent. Days are
// Turkey's calendar days. Status: n11's own word for the day (its values are not documented), also on each day's lines.
public sealed record RemoteSettlementDay(DateOnly? SettlementDate, DateOnly? RemittanceDate, string? Status, decimal PaymentAmount, decimal DeductionAmount, decimal SettlementAmount);

public sealed record RemoteSettlementDayPage(IReadOnlyList<RemoteSettlementDay> Items, int Page, int PageCount);

// One order item's settlement on a day. Fees hold their VAT; Discount: the seller's discount and coupon. SettlementAmount:
// what the seller is paid for the line (n11's own figure).
public sealed record RemoteSettlementItem(string OrderItemId, string? OrderNumber, decimal Commission, decimal ServiceFee, decimal AdFee, decimal CargoPrice, decimal Discount,
    decimal DeductionAmount, decimal PaymentAmount, decimal SettlementAmount, DateTimeOffset? CompletedAt, DateOnly? SettlementDate, DateOnly? RemittanceDate, DateOnly? PaymentDate, string RawJson);

public sealed record RemoteSettlementItemPage(IReadOnlyList<RemoteSettlementItem> Items, int Page, int PageCount, string? Status, IReadOnlyList<RemoteSkippedFinanceRecord>? Skipped = null)
{
    public IReadOnlyList<RemoteSkippedFinanceRecord> Skipped { get; init; } = Skipped ?? [];
}

// n11's SOAP SettlementService, read only.
public interface IN11SettlementPort
{
    Task<AdapterResult<RemoteSettlementDayPage>> GetSettlementDaysAsync(AdapterContext context, DateOnly start, DateOnly end, int page, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteSettlementItemPage>> GetSettlementItemsAsync(AdapterContext context, DateOnly day, int page, CancellationToken cancellationToken);
}

// One Hepsiburada accounting record (Kayıt Bazlı Muhasebe), as sent: Status Paid or WillBePaid; Amount and NetAmount
// as sent (their sign is not documented: IsIncome says whether the record pays the seller or charges them).
public sealed record RemoteFinanceRecord(string Id, string? TransactionType, string? Category, string? Status, string? OrderNumber, string? PackageNumber, string? InvoiceNumber,
    string? Sku, decimal? Amount, decimal? NetAmount, string? Currency, bool? IsIncome, DateTimeOffset? OrderDate, DateTimeOffset? InvoiceDate, DateTimeOffset? DueDate,
    DateTimeOffset? PaymentDate, string RawJson);

public sealed record RemoteFinanceRecordPage(IReadOnlyList<RemoteFinanceRecord> Items, int Count, IReadOnlyList<RemoteSkippedFinanceRecord>? Skipped = null)
{
    public IReadOnlyList<RemoteSkippedFinanceRecord> Skipped { get; init; } = Skipped ?? [];
}

// Hepsiburada's accounting service, read only: the records of a status by their due day or their payment day.
public interface IHepsiburadaFinancePort
{
    Task<AdapterResult<RemoteFinanceRecordPage>> GetFinanceRecordsAsync(AdapterContext context, string status, bool byDueDate, DateTimeOffset start, DateTimeOffset end, int offset, CancellationToken cancellationToken);
}
