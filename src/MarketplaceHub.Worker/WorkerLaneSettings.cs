namespace MarketplaceHub.Worker;

// Worker lanes step 1 (19 Nov 2026), quota-aware leasing:
//   Worker:QuotaAwareLeasing:Enabled            default true; false = jobs wait for their quota inside their slot (as before)
//   Worker:QuotaAwareLeasing:SaturationSeconds  default 10 (3..120): a request waiting this long for a token marks its bucket full
//   Worker:QuotaAwareLeasing:MaximumHoldMinutes default 15 (1..60): a job is held at most this long after it became runnable
// A replay of the live week with a marketplace quota full for 30 minutes: other marketplaces' p95 wait 781 s today, 0.4 s held
// for 15 minutes (551 s with 5, 179 s with 10); the throttled marketplace's own jobs finish at the same time either way.
internal sealed record QuotaAwareLeasingSettings(bool Enabled, TimeSpan Saturation, TimeSpan MaximumHold)
{
    public static QuotaAwareLeasingSettings From(IConfiguration configuration) => new(
        configuration.GetValue("Worker:QuotaAwareLeasing:Enabled", true),
        TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Worker:QuotaAwareLeasing:SaturationSeconds", 10), 3, 120)),
        TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("Worker:QuotaAwareLeasing:MaximumHoldMinutes", 15), 1, 60)));
}

// Worker lanes step 1, lane wait alerts (LaneWaitMonitor, LaneWaitAlarm; targets in LaneWaitTargets):
//   Worker:LaneAlerts:Enabled         default true; false = no QUEUE_WAIT_TARGET_EXCEEDED issue is opened or closed
//   Worker:LaneAlerts:EvaluateSeconds default 60 (15..600)
//   LaneAlerts:WindowMinutes          default 5 (1..60): the recent jobs whose p95 wait is compared with the target; shared
//                                     with the API (LaneWaitTargets.Window), like the targets LaneTargets:<lane>
//   Worker:LaneAlerts:SustainMinutes  default 10 (1..120): missed at every look this long before the issue opens
//   Worker:LaneAlerts:ClearMinutes    default 10 (1..120): within target this long before it closes
internal sealed record LaneAlertSettings(bool Enabled, TimeSpan Evaluate, TimeSpan Window, TimeSpan Sustain, TimeSpan Clear)
{
    public static LaneAlertSettings From(IConfiguration configuration) => new(
        configuration.GetValue("Worker:LaneAlerts:Enabled", true),
        TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Worker:LaneAlerts:EvaluateSeconds", 60), 15, 600)),
        MarketplaceHub.Infrastructure.Jobs.LaneWaitTargets.Window(configuration),
        TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("Worker:LaneAlerts:SustainMinutes", 10), 1, 120)),
        TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("Worker:LaneAlerts:ClearMinutes", 10), 1, 120)));
}
