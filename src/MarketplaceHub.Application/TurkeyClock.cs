namespace MarketplaceHub.Application;

// Turkey's clock: UTC+3 all year (no daylight saving since September 2016). The one definition of the offset every
// policy, import parser and marketplace adapter (Infrastructure's TurkeyTime) uses (ADAPTOR-DIGER-8).
public static class TurkeyClock
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(3);
}
