namespace MarketplaceHub.Domain;

// One Claude API call made for a panel feature (Ayarlar › Yapay Zekâ shows the latest ones): who asked, for what,
// what it used and what it cost. Feature names the consumer ("SIZE_CHART", "CONNECTION_TEST", "SKILL_UPLOAD").
public sealed class AiUsageLog
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? UserId { get; set; }
    public required string Feature { get; set; }
    public string? SubjectType { get; set; }
    public Guid? SubjectId { get; set; }
    public string? SubjectLabel { get; set; }
    public required string Model { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? CacheReadInputTokens { get; set; }
    public long? CacheCreationInputTokens { get; set; }
    // Token cost by the model's list price; null when the API reported no usage. Code execution time is not in it.
    public decimal? CostUsd { get; set; }
    public required string Status { get; set; }
    public string? Error { get; set; }
    public int DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// A product card's size chart: the measurements last entered for it (the dialog opens with them) and which of its
// pictures is the chart (always the last picture of every variant). LastGeneratedAssetId is the newest generated
// image, which the editor may attach with "Kullan" before it has any product_media row.
public sealed class ProductSizeChart
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? FileAssetId { get; set; }
    public Guid? LastGeneratedAssetId { get; set; }
    public string? MeasurementsJson { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}
