using MarketplaceHub.Application;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Jobs;

// Queue phase 1A switches (27 Nov 2026), read by the scheduler on every pass. Each is on by default and written out in the
// worker's appsettings.json; off gives the earlier behaviour exactly. Production passes none of them through compose, so
// turning one off is a release: set the value to false in src/MarketplaceHub.Worker/appsettings.json and ship
// (docs/RUNBOOK-GERI-ALMA.md, "Kuyruk Faz 1A anahtarları").
//   Worker:ReferenceReads:OneAtATime       a connection's daily reference lists are queued one after the other (the category
//                                          tree first, the brand list once it ended) and a tenant runs one scheduled reference
//                                          read at a time; off = every stale list of every connection queued in the same pass
//   Worker:ReferenceReads:Spread           each connection's daily reference read gets its own slot in the window below
//                                          (ReferenceReadSchedulePolicy); off = the afternoon daily window (14:00-17:00 Istanbul)
//   Worker:ReferenceReads:WindowStartHour  Istanbul hour the window opens, default 6 (0..23)
//   Worker:ReferenceReads:WindowHours      window length in hours, default 4 (1..12)
//   Worker:StartupSpread:Enabled           the background reads due at a worker start are spread (StartupSpreadPolicy)
//   Worker:StartupSpread:Seconds           over how many seconds, default 180 (10..900)
//   Worker:HoldPollsWhileFailing:Enabled   no new background poll for a connection whose marketplace is failing
//                                          (FailingConnectionPollPolicy)
public sealed record SchedulerTimingSettings(
    bool ReferenceOneAtATime,
    bool ReferenceSpread,
    TimeSpan ReferenceWindowStart,
    TimeSpan ReferenceWindowLength,
    bool StartupSpread,
    int StartupSpreadSeconds,
    bool HoldPollsWhileFailing,
    int HotPriorityCeiling)
{
    public static SchedulerTimingSettings From(IConfiguration configuration) => new(
        configuration.GetValue("Worker:ReferenceReads:OneAtATime", true),
        configuration.GetValue("Worker:ReferenceReads:Spread", true),
        TimeSpan.FromHours(Math.Clamp(configuration.GetValue("Worker:ReferenceReads:WindowStartHour", (int)ReferenceReadSchedulePolicy.DefaultWindowStart.TotalHours), 0, 23)),
        TimeSpan.FromHours(Math.Clamp(configuration.GetValue("Worker:ReferenceReads:WindowHours", (int)ReferenceReadSchedulePolicy.DefaultWindowLength.TotalHours), 1, 12)),
        configuration.GetValue("Worker:StartupSpread:Enabled", true),
        Math.Clamp(configuration.GetValue("Worker:StartupSpread:Seconds", StartupSpreadPolicy.DefaultSeconds), 10, 900),
        configuration.GetValue("Worker:HoldPollsWhileFailing:Enabled", true),
        Math.Clamp(configuration.GetValue("Worker:HotPriorityCeiling", WorkerLanes.DefaultHotPriorityCeiling), 0, 5));
}
