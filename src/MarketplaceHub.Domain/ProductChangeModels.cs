namespace MarketplaceHub.Domain;

// One change of a product card that no other table keeps: its sale status (Satışa Açık / Kapalı / Taslak) or the
// panel's own price of a card or variant. Written in the same save as the change it describes, append-only, shown in
// İşlem Takibi › Loglar › Ürün Hareketleri. Stock moves stay in the stock ledger and marketplace offer prices in the
// channel price history; this table never repeats them. Rows exist only from 4 Nov 2026 on: nothing was backfilled.
public sealed class ProductChangeEntry
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    // No foreign key: the history outlives nothing it names, and a deleted product's rows are removed with it.
    public Guid ProductId { get; set; }
    // The variant the change is for; null for a change of the whole card.
    public Guid? VariantId { get; set; }
    // ProductChangeKinds: STATUS or DEFAULT_PRICE.
    public required string Kind { get; set; }
    // STATUS: the ProductStatus names before and after (ACTIVE, ARCHIVED, DRAFT); null before means none known.
    public string? PreviousStatus { get; set; }
    public string? NewStatus { get; set; }
    // DEFAULT_PRICE: the panel prices before and after; a null was not set.
    public decimal? PreviousSalePrice { get; set; }
    public decimal? NewSalePrice { get; set; }
    public decimal? PreviousListPrice { get; set; }
    public decimal? NewListPrice { get; set; }
    // ProductChangeSources: where the change came from (editor, bulk edit, file import, marketplace import...).
    public required string Source { get; set; }
    // The marketplace connection of a change read from a marketplace; null for the panel's own changes.
    public Guid? ConnectionId { get; set; }
    // The user who made it; null for a change the system made (a marketplace import).
    public Guid? ActorUserId { get; set; }
    public string? Note { get; set; }
    public required string CorrelationId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
