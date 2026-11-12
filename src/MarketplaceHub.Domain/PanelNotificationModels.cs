namespace MarketplaceHub.Domain;

// One notification the panel showed in its bell (owner, 13 Nov 2026: "buradan silinse de orada yazılar kalsın 1 hafta
// boyunca"). The bell is the browser's short list; this row is the notification center's copy (İşlem Takibi ›
// Bildirimler). Removing it from the bell only sets DismissedAt; the row stays until the hourly retention removes it
// seven days after CreatedAt. UserId is the person whose action raised it (null: a notice for the whole tenant, such
// as a read failing run after run). ClientKey is the browser's id of the notification, so a repeat send or a burst
// that grows (Count) updates the same row.
public sealed class PanelNotification
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? UserId { get; set; }
    public required string ClientKey { get; set; }
    // SUCCESS, ERROR or INFO (PanelNotificationSeverities).
    public required string Severity { get; set; }
    // What it is about (PanelNotificationCategories): ORDER, PRODUCT, INVOICE, CONNECTION, JOB, ATTENTION...
    public required string Category { get; set; }
    public required string Title { get; set; }
    public required string Message { get; set; }
    public string? Href { get; set; }
    public string? ActionLabel { get; set; }
    public string? PlatformCode { get; set; }
    public string? StoreName { get; set; }
    public int Count { get; set; } = 1;
    // The browser's full notification (details, records it touched), kept so the center shows the same card.
    public string? Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? DismissedAt { get; set; }
}
