namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

public sealed class PazaramaOptions
{
    public const string SectionName = "Pazarama";
    // Pazarama has no documented sandbox: every connection talks to the live store.
    public Uri BaseAddress { get; init; } = new("https://isortagimapi.pazarama.com/");
    public Uri TokenAddress { get; init; } = new("https://isortagimgiris.pazarama.com/connect/token");
    public string Scope { get; init; } = "merchantgatewayapi.fullaccess";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    // Pazarama documents 10 s between price/stock requests and between product create
    // requests per seller; a quarter more is kept because the gap is measured on its side.
    public TimeSpan PriceStockSpacing { get; init; } = TimeSpan.FromSeconds(12.5);
    public TimeSpan ProductCreateSpacing { get; init; } = TimeSpan.FromSeconds(12.5);
    // Undocumented quotas (requests per minute): cautious starting values.
    public int OrderRequestsPerMinute { get; init; } = 60;
    public int ReadRequestsPerMinute { get; init; } = 30;
}
