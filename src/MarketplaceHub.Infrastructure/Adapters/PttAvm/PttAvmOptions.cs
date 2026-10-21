namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

public sealed class PttAvmOptions
{
    public const string SectionName = "PttAvm";
    // PTT AVM has no documented sandbox: every connection talks to the live store.
    public Uri BaseAddress { get; init; } = new("https://integration-api.pttavm.com/api/v1/");
    // The cargo (barcode/label) service lives on its own host (docs/PTTAVM-ENTEGRASYON-PLANI.md §3.1).
    public Uri ShipmentBaseAddress { get; init; } = new("https://shipment.pttavm.com/api/v1/");
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    // PTT AVM documents no quotas; cautious starting values (plan §5.4). Product and price/stock
    // writes are asynchronous and the same request may not be repeated within 5 minutes.
    public int OrderRequestsPerMinute { get; init; } = 30;
    public int ReadRequestsPerMinute { get; init; } = 30;
    public int ShipmentRequestsPerMinute { get; init; } = 30;
    public TimeSpan ProductWriteSpacing { get; init; } = TimeSpan.FromSeconds(15);
}
