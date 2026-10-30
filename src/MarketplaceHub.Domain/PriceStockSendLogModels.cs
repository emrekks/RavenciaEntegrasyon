namespace MarketplaceHub.Domain;

// One line of a price/stock request sent to a marketplace: what it carried against what the marketplace had last
// confirmed, why it was sent and what came of it. Written by the price/stock job where it already knows the outcome
// (the submit records the request, the poll its answer), kept 90 days. Append-only apart from that one outcome update.
public sealed class PriceStockSendLog
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string PlatformCode { get; set; }
    // The job that sent it; no foreign key, since finished jobs are removed sooner than this history.
    public Guid JobId { get; set; }
    public Guid VariantId { get; set; }
    public Guid OfferId { get; set; }
    public required string Barcode { get; set; }
    public string? StockCode { get; set; }
    // The values the marketplace had last confirmed for the same code (null: none known).
    public decimal? PreviousQuantity { get; set; }
    public decimal? PreviousSalePrice { get; set; }
    public decimal? PreviousListPrice { get; set; }
    // The values the request carried (null: the field was left out of the request).
    public decimal? NewQuantity { get; set; }
    public decimal? NewSalePrice { get; set; }
    public decimal? NewListPrice { get; set; }
    public required string Currency { get; set; }
    public required string Trigger { get; set; }
    public string? ExternalRequestId { get; set; }
    public required string Result { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
