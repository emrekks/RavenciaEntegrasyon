using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

// What a price/stock request carries and how its answer is read, per the marketplaces' documents (checked 30 Oct 2026):
// - Trendyol refuses an identical request for 15 minutes ("15 dakika boyunca aynı isteği tekrarlı olarak atamazsınız!"),
//   takes a line with only the stock or only the prices, and allows 30 price requests a minute per barcode. A line
//   therefore carries only the values that differ from the ones Trendyol last confirmed.
// - PTT AVM refuses the same request within five minutes.
// - Pazarama may answer a batch up to four hours late, so its batch is waited for eight.
// The pipeline (outbox, dispatch gates, monitoring) may use these helpers; they read no state of their own.
// What a save of a channel offer changed. Price: a value the outbound price is made of (list and sale price, currency,
// VAT rate and inclusion, rounding); only that is a new price version and a price send. SafetyStock: the quantity sent
// (available less safety stock) changed, a stock send. Status: the offer was switched on or off.
public sealed record ChannelOfferChange(bool Price, bool SafetyStock, bool Status)
{
    public bool Any => Price || SafetyStock || Status;
}

public static class ChannelOfferChangePolicy
{
    public static ChannelOfferChange Classify(ChannelOffer offer, decimal listPrice, decimal salePrice, string currency, decimal vatRate, string vatInclusion, string roundingMode, decimal safetyStock, string status) => new(
        offer.ListPrice != listPrice || offer.SalePrice != salePrice
            || !string.Equals(offer.Currency?.Trim(), currency, StringComparison.OrdinalIgnoreCase)
            || offer.VatRate != vatRate
            || !string.Equals(offer.VatInclusion?.Trim(), vatInclusion, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(offer.RoundingMode?.Trim(), roundingMode, StringComparison.OrdinalIgnoreCase),
        offer.SafetyStock != safetyStock,
        !string.Equals(offer.Status, status, StringComparison.Ordinal));
}

public static class PriceInventorySendPolicy
{
    public static readonly TimeSpan DefaultPollDeadline = TimeSpan.FromHours(4);
    public static readonly TimeSpan PazaramaPollDeadline = TimeSpan.FromHours(8);

    // How long a submitted price/stock batch is polled before the job goes to manual review.
    public static TimeSpan PollDeadline(string? platformCode) =>
        string.Equals(platformCode, "PAZARAMA", StringComparison.OrdinalIgnoreCase) ? PazaramaPollDeadline : DefaultPollDeadline;

    public sealed record SentFields(bool Quantity, bool Price)
    {
        public bool Any => Quantity || Price;
    }

    // The fields a Trendyol line sends. A field goes when its mark says it changed (a new stock projection or price
    // version) and its value differs from the one Trendyol last confirmed for the same barcode. A mark cleared on
    // purpose (null: a reset, a new address) always sends, and so does a value never confirmed.
    public static SentFields TrendyolFields(ChannelOffer offer, string code, decimal quantity, decimal salePrice, decimal listPrice, string priceHash, long projectionVersion)
    {
        var priceMarked = offer.LastPriceHash != priceHash;
        var stockMarked = offer.LastStockProjectionVersion != projectionVersion;
        var sameCode = offer.LastSentCode is not null && string.Equals(offer.LastSentCode, code, StringComparison.Ordinal);
        var price = priceMarked && (offer.LastPriceHash is null || !sameCode || offer.LastSentSalePrice != salePrice || offer.LastSentListPrice != listPrice);
        var stock = stockMarked && (offer.LastStockProjectionVersion is null || !sameCode || offer.LastSentQuantity != quantity);
        return new(stock, price);
    }

    // Whether the values of a line are the ones the marketplace last confirmed for the same code: a refusal of the
    // request as a repeat (Trendyol's 15 minutes, PTT AVM's five) then means the marketplace already holds them.
    public static bool MatchesLastSent(ChannelOffer offer, PriceInventoryPushLine line) =>
        offer.LastSentCode is not null && string.Equals(offer.LastSentCode, line.Barcode, StringComparison.Ordinal)
        && (line.QuantityOmitted || offer.LastSentQuantity == line.Quantity)
        && (line.PriceOmitted || offer.LastSentSalePrice == line.SalePrice && offer.LastSentListPrice == line.ListPrice);

    // A line the marketplace accepted: the values it carried are now the confirmed ones. A field left out keeps the value
    // confirmed before it, unless the code changed (then nothing is known of the new code but what this line sent).
    public static void RecordSent(ChannelOffer offer, PriceInventoryPushLine line, DateTimeOffset sentAt)
    {
        var sameCode = offer.LastSentCode is not null && string.Equals(offer.LastSentCode, line.Barcode, StringComparison.Ordinal);
        if (!sameCode)
        {
            offer.LastSentQuantity = null;
            offer.LastSentSalePrice = null;
            offer.LastSentListPrice = null;
        }
        offer.LastSentCode = line.Barcode;
        if (!line.QuantityOmitted) offer.LastSentQuantity = line.Quantity;
        if (!line.PriceOmitted)
        {
            offer.LastSentSalePrice = line.SalePrice;
            offer.LastSentListPrice = line.ListPrice;
        }
        offer.LastSentAt = sentAt.ToUniversalTime();
    }

    // Whether a polled line's stock still is the offer's current one: the same stock projection and, when the line names
    // it, the same safety stock (a safety stock edit clears the stock mark; the answer of an older send must not set it
    // back). A line queued before lines carried the safety stock is judged by the projection alone.
    public static bool StockCurrent(PriceInventoryPushLine line, long projectionVersion, decimal safetyStock) =>
        line.ProjectionVersion == projectionVersion && (line.SafetyStock is null || line.SafetyStock == safetyStock);

    // Whether a polled line's price still is the offer's current one.
    public static bool PriceCurrent(PriceInventoryPushLine line, long priceVersion) => line.PriceVersion == priceVersion;

    public static void ClearSent(ChannelOffer offer)
    {
        offer.LastSentCode = null;
        offer.LastSentQuantity = null;
        offer.LastSentSalePrice = null;
        offer.LastSentListPrice = null;
        offer.LastSentAt = null;
    }
}
