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
    string RawJson);

public sealed record RemoteStatementPage(IReadOnlyList<RemoteStatementRecord> Items, int Page, int TotalPages, long TotalElements);

public sealed record RemoteCargoInvoiceItem(string? ShipmentPackageType, string? ParcelUniqueId, string? OrderNumber, decimal Amount, int? Desi, string RawJson);

public sealed record RemoteCargoInvoicePage(IReadOnlyList<RemoteCargoInvoiceItem> Items, int Page, int TotalPages);

// Trendyol's accounting services (Cari Hesap Ekstresi), read only. A window may span at most 15 days.
public interface ITrendyolFinancePort
{
    Task<AdapterResult<RemoteStatementPage>> GetSettlementsAsync(AdapterContext context, string transactionType, DateTimeOffset start, DateTimeOffset end, int page, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteStatementPage>> GetOtherFinancialsAsync(AdapterContext context, string transactionType, string? transactionSubType, DateTimeOffset start, DateTimeOffset end, int page, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteCargoInvoicePage>> GetCargoInvoiceItemsAsync(AdapterContext context, string invoiceSerialNumber, int page, CancellationToken cancellationToken);
}
