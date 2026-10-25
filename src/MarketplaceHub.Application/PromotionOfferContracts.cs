namespace MarketplaceHub.Application;

// Trendyol's promotion offers the seller uploads (Plus commission tariff, advantage product label, flash products): what
// each offered price leaves after the commission and the product's cost, beside what the price leaves now. Nothing is
// sent to the marketplace. Kind is PLUS, ADVANTAGE or FLASH (PromotionOfferKinds).
public sealed record PromotionOfferSummary(string Kind, string FileName, DateTimeOffset UploadedAt, string? PeriodText, int RowCount, int MatchedRowCount);

public sealed record PromotionOfferUploadResult(PromotionOfferSummary Offer, int SkippedRows, IReadOnlyList<string> Warnings);

// A price, the commission it pays and what it leaves after the commission and the cost. Rate is null when the
// commission at that price is not known (no commission tariff covers it); Remaining is null then or without a cost.
// RemainingHigh: the most a model's variants leave when their costs differ (Remaining is then the least).
public sealed record PromotionPriceView(decimal Price, decimal? Rate, decimal? Commission, decimal? Remaining, decimal? RemainingHigh = null);

// Now: the commission-basis price at the current commission. Plus: the Plus price ceiling at the offered commission.
// Gain: what Plus leaves minus what the price leaves now.
public sealed record PlusOfferView(string Barcode, string? SellerSku, string? ModelCode, string? ProductName, string? Size, string? Color, string? ImageUrl, decimal? Stock,
    decimal? CostPrice, PromotionPriceView? Now, PromotionPriceView? Plus, decimal? Gain, string? Promotions);

// One star of the advantage label: the price range that earns it and its ceiling priced with the commission tariff.
public sealed record AdvantageStarView(int Star, decimal? Min, decimal Max, PromotionPriceView Price);

// CurrentStars: the stars the customer's price earns now (0 when it is above the 1-star ceiling).
public sealed record AdvantageOfferView(string Barcode, string? ModelCode, string? ProductName, string? Size, string? Color, string? ImageUrl, decimal? Stock,
    decimal? CostPrice, bool? InTariff, int CurrentStars, PromotionPriceView? Now, IReadOnlyList<AdvantageStarView> Stars, string? Promotions);

// One flash slot (24 hours or 3 hours) with its times as the file writes them.
public sealed record FlashSlotView(string Slot, string? Start, string? End, PromotionPriceView Price);

public sealed record FlashOfferView(string? ModelCode, IReadOnlyList<string> Barcodes, string? ProductName, string? Color, string? ImageUrl, decimal? Stock,
    decimal? CostLow, decimal? CostHigh, bool? InTariff, bool? Campaign, PromotionPriceView? Now, IReadOnlyList<FlashSlotView> Slots, string? Promotions);

public sealed record PromotionOfferDetail(PromotionOfferSummary Offer, IReadOnlyList<PlusOfferView>? Plus, IReadOnlyList<AdvantageOfferView>? Advantage, IReadOnlyList<FlashOfferView>? Flash);

public interface IPromotionOfferService
{
    Task<ServiceResult<PromotionOfferUploadResult>> UploadAsync(Guid tenantId, Guid? userId, Guid connectionId, string fileName, Stream content, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<PromotionOfferSummary>>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
    Task<ServiceResult<PromotionOfferDetail>> DetailAsync(Guid tenantId, Guid connectionId, string kind, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> DeleteAsync(Guid tenantId, Guid? userId, Guid connectionId, string kind, string correlationId, CancellationToken cancellationToken);
}
