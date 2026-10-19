namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

// Trendyol's order quota (stream and Order V2) depends on the seller's product listing limit tier:
// 30, 40, 50 or 100 requests a minute. The API does not report the tier, so it is learned: reads start
// at the configured floor; when the quota keeps filling up (reads had to wait for it many times within
// ten minutes) and no 429 came for a day, the next tier is tried. A 429 steps one tier back and holds
// it for a day. One seller's tier, kept per process; guarded by the resilience state's lock.
internal sealed class TrendyolOrderTier
{
    internal static readonly int[] Tiers = [30, 40, 50, 100];
    internal const int PressureToStepUp = 20;
    internal static readonly TimeSpan PressureWindow = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan HoldAfterRateLimit = TimeSpan.FromDays(1);
    internal static readonly TimeSpan HoldAfterStepUp = TimeSpan.FromMinutes(30);

    private int index;
    private int pressure;
    private DateTimeOffset pressureWindowStart;
    private DateTimeOffset holdUntil;

    // Requests a minute for this seller; never below the configured floor.
    public int Limit(int floor) => Math.Max(floor, Tiers[index]);

    // The read waited for a full quota. True when this moved the seller to the next tier.
    public bool RecordPressure(DateTimeOffset now)
    {
        if (now - pressureWindowStart > PressureWindow)
        {
            pressureWindowStart = now;
            pressure = 0;
        }
        pressure++;
        if (pressure < PressureToStepUp || now < holdUntil || index == Tiers.Length - 1) return false;
        index++;
        pressure = 0;
        pressureWindowStart = now;
        holdUntil = now + HoldAfterStepUp;
        return true;
    }

    // Trendyol answered 429 on an order read: the tier tried is above the seller's. True when the tier dropped.
    public bool RecordRateLimited(DateTimeOffset now)
    {
        var stepped = index > 0;
        if (stepped) index--;
        pressure = 0;
        pressureWindowStart = now;
        holdUntil = now + HoldAfterRateLimit;
        return stepped;
    }
}
