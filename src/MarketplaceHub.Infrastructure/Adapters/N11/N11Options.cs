namespace MarketplaceHub.Infrastructure.Adapters.N11;

public sealed class N11Options
{
    public const string SectionName = "N11";
    // n11 serves product (/ms), category (/cdn) and order (/rest) REST services
    // and the remaining SOAP services (/ws) from the same host. There is no
    // sandbox, so every connection talks to the live store.
    public Uri BaseAddress { get; init; } = new("https://api.n11.com/");
    // n11 asks integrators to send the same integrator name with every write.
    public string IntegratorName { get; init; } = "Ravencia";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}
