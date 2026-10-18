namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

// Which share of the seller's order quota the current flow draws from. A worker job marks its
// background reads; everything else (API requests, new-order reads, user actions) is foreground.
public static class TrendyolQuotaLane
{
    private static readonly AsyncLocal<bool> background = new();

    public static bool IsBackground => background.Value;

    public static IDisposable Enter(bool isBackground)
    {
        var previous = background.Value;
        background.Value = isBackground;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        public void Dispose() => background.Value = previous;
    }
}
