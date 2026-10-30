using System.Text.RegularExpressions;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

// What came of one line of a price/stock request (PriceStockSendLog.Result).
public static class PriceStockSendResults
{
    // The marketplace took the request; its answer is not read yet.
    public const string Submitted = "SUBMITTED";
    public const string Applied = "APPLIED";
    // Refused as a repeat of values the marketplace already holds (Trendyol 15 minutes, PTT AVM five).
    public const string Unchanged = "UNCHANGED";
    public const string Rejected = "REJECTED";
    // The line or the request failed without a refusal of the line (missing from the answer, upload failed, request refused).
    public const string Failed = "FAILED";
    // A newer price or stock arrived before the answer; the answer was not applied to the newer values.
    public const string Superseded = "SUPERSEDED";
    // The answer never came or could not be read: the marketplace may or may not hold the values.
    public const string Unknown = "UNKNOWN";

    public static readonly IReadOnlyList<string> All = [Submitted, Applied, Unchanged, Rejected, Failed, Superseded, Unknown];
}

// Why a price/stock request was sent (PriceStockSendLog.Trigger).
public static partial class PriceStockSendTriggers
{
    public const string Order = "ORDER";
    public const string ManualStock = "MANUAL_STOCK";
    public const string PriceEdit = "PRICE_EDIT";
    public const string StockImport = "STOCK_IMPORT";
    // Any other stock change (a return restock, a transfer of an unnamed source).
    public const string StockChange = "STOCK_CHANGE";
    public const string Reconcile = "RECONCILE";
    public const string PanelSend = "PANEL_SEND";
    public const string Automatic = "AUTOMATIC";

    public static readonly IReadOnlyList<string> All = [Order, ManualStock, PriceEdit, StockImport, StockChange, Reconcile, PanelSend, Automatic];

    // The trigger of an automatic dispatch, read from the source event id the outbox queued it with: "manual:" (a stock
    // edit or transfer), "price:" (a price edit), "catalog-stock:" (stock read from a marketplace catalog) and an order
    // line's "{lineId:N}:{unix ms}..." each name their cause; anything else is a stock change.
    public static string FromSourceEvent(string? sourceEventId)
    {
        if (string.IsNullOrWhiteSpace(sourceEventId)) return StockChange;
        if (sourceEventId.StartsWith("manual:", StringComparison.Ordinal)) return ManualStock;
        if (sourceEventId.StartsWith("price:", StringComparison.Ordinal)) return PriceEdit;
        if (sourceEventId.StartsWith("catalog-stock:", StringComparison.Ordinal)) return StockImport;
        return OrderLineEvent().IsMatch(sourceEventId) ? Order : StockChange;
    }

    // The trigger a job records: the one its payload names, else what the job itself shows (a job queued before the
    // payload carried one).
    public static string ForJob(string? payloadTrigger, bool manual, bool automatic, bool userTriggered, bool singleVariant)
    {
        if (payloadTrigger is not null && All.Contains(payloadTrigger, StringComparer.Ordinal)) return payloadTrigger;
        if (manual || userTriggered) return PanelSend;
        if (automatic) return singleVariant ? Automatic : Reconcile;
        return PanelSend;
    }

    [GeneratedRegex("^[0-9a-f]{32}:[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex OrderLineEvent();
}

// The values a marketplace last confirmed for an offer (ChannelOffer.LastSent*), read once per request.
public sealed record PriceStockConfirmedValues(string? Code, decimal? Quantity, decimal? SalePrice, decimal? ListPrice);

public static class PriceStockSendLogPolicy
{
    public const int MaxCodeLength = 100;
    public const int MaxMessageLength = 500;
    public const int MaxBarcodeLength = 256;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    // One row for a line of a request. The previous values are the ones confirmed for the same code; another code's
    // values say nothing of this one. A field the request left out has no new value.
    public static PriceStockSendLog Row(Guid tenantId, Guid connectionId, string platformCode, Guid jobId, PriceInventoryPushLine line, PriceStockConfirmedValues? confirmed,
        string trigger, string? externalRequestId, DateTimeOffset now, string result = PriceStockSendResults.Submitted, string? errorCode = null, string? errorMessage = null)
    {
        var sameCode = confirmed?.Code is not null && string.Equals(confirmed.Code, line.Barcode, StringComparison.Ordinal);
        var row = new PriceStockSendLog
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            PlatformCode = Cut(platformCode.ToUpperInvariant(), 32)!,
            JobId = jobId,
            VariantId = line.VariantId,
            OfferId = line.OfferId,
            Barcode = Cut(line.Barcode, MaxBarcodeLength) ?? "",
            StockCode = Cut(line.MerchantSku ?? line.HepsiburadaSku ?? line.ExternalVariantId, MaxBarcodeLength),
            PreviousQuantity = sameCode ? confirmed!.Quantity : null,
            PreviousSalePrice = sameCode ? confirmed!.SalePrice : null,
            PreviousListPrice = sameCode ? confirmed!.ListPrice : null,
            NewQuantity = line.QuantityOmitted ? null : line.Quantity,
            NewSalePrice = line.PriceOmitted ? null : line.SalePrice,
            NewListPrice = line.PriceOmitted ? null : line.ListPrice,
            Currency = Cut(string.IsNullOrWhiteSpace(line.Currency) ? "TRY" : line.Currency.Trim().ToUpperInvariant(), 3)!,
            Trigger = trigger,
            ExternalRequestId = Cut(externalRequestId, 256),
            Result = PriceStockSendResults.Submitted,
            CreatedAt = now.ToUniversalTime()
        };
        if (result != PriceStockSendResults.Submitted) Settle(row, result, errorCode, errorMessage, now);
        return row;
    }

    // The answer for a line. A row already settled keeps its outcome.
    public static void Settle(PriceStockSendLog row, string result, string? errorCode, string? errorMessage, DateTimeOffset now)
    {
        if (row.Result != PriceStockSendResults.Submitted) return;
        row.Result = result;
        row.ErrorCode = Cut(errorCode, MaxCodeLength);
        row.ErrorMessage = Cut(errorMessage, MaxMessageLength);
        row.CompletedAt = now.ToUniversalTime();
    }

    private static string? Cut(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= length ? trimmed : trimmed[..length];
    }
}

// The log list: newest first, at most Limit rows (default 50, max 100); After is the NextCursor of the previous page.
public sealed record PriceStockSendLogQuery(int? Limit = null, string? After = null, IReadOnlyList<string>? Platforms = null, Guid? ConnectionId = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, string? Search = null, IReadOnlyList<string>? Results = null, IReadOnlyList<string>? Triggers = null)
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 100;
    public const int MaximumSearchLength = 100;
    public int EffectiveLimit => Math.Clamp(Limit ?? DefaultLimit, 1, MaximumLimit);
}

public sealed record PriceStockSendLogView(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    Guid ConnectionId,
    string PlatformCode,
    string? StoreName,
    Guid JobId,
    Guid VariantId,
    Guid? ProductId,
    string? ProductTitle,
    string? ImageUrl,
    string Barcode,
    string? StockCode,
    string? ModelCode,
    decimal? PreviousQuantity,
    decimal? NewQuantity,
    decimal? PreviousSalePrice,
    decimal? NewSalePrice,
    decimal? PreviousListPrice,
    decimal? NewListPrice,
    string Currency,
    string Trigger,
    string Result,
    string? ErrorCode,
    string? ErrorMessage,
    string? ExternalRequestId);

public interface IPriceStockSendLogService
{
    Task<ServiceResult<PageResult<PriceStockSendLogView>>> PageAsync(Guid tenantId, PriceStockSendLogQuery query, CancellationToken cancellationToken);
}
