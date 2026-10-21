namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

public sealed class PttAvmOptions
{
    public const string SectionName = "PttAvm";
    // PTT AVM has no documented sandbox: every connection talks to the live store's SOAP web service
    // (WCF, binding WsSecured_IService, port 93).
    public Uri SoapBaseAddress { get; init; } = new("https://ws.pttavm.com:93/service.svc");
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    // PTT AVM documents no quotas; cautious starting values (plan §5.4). Product and price/stock
    // writes are asynchronous and the same request may not be repeated within 5 minutes.
    public int OrderRequestsPerMinute { get; init; } = 30;
    public int ReadRequestsPerMinute { get; init; } = 30;
    public TimeSpan ProductWriteSpacing { get; init; } = TimeSpan.FromSeconds(15);
}
