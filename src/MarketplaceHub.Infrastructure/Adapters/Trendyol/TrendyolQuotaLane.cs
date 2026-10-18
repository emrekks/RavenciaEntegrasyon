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

    // An action the user waits for on screen: it waits at most two seconds for a quota token and
    // eight seconds for Trendyol's answer. A token not granted in time means nothing was sent.
    public sealed record InstantLimits(TimeSpan MaximumQuotaWait, TimeSpan RequestTimeout);
    private static readonly AsyncLocal<InstantLimits?> instant = new();

    public static InstantLimits? Limits => instant.Value;

    public static IDisposable Instant()
    {
        var previous = instant.Value;
        instant.Value = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8));
        return new RestoreInstant(previous);
    }

    private sealed class RestoreInstant(InstantLimits? previous) : IDisposable
    {
        public void Dispose() => instant.Value = previous;
    }
}
