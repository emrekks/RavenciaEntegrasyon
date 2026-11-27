using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Jobs;

// Worker lanes step 4 (23 Nov 2026, owner's go): the lane model, read the same way by the worker (LaneSlotPool) and the api
// (borrowed slots in the lane metrics). Flexible is the default since this release; Worker:LaneModel=legacy (the
// Worker__LaneModel environment variable, given to the worker and the api alike) brings back the earlier model, each lane
// on its own slots only. Any other value counts as flexible.
// Queue phase 2 (27 Nov 2026): Worker:LaneModel=dispatcher runs one dispatcher over the work-type lanes (DispatcherLanes,
// LaneDispatcher) instead of the lanes' own and borrowing loops. The shipped appsettings of the worker and the api name the
// model explicitly; switching is a release that changes both (docs/RUNBOOK-GERI-ALMA.md). Any value other than legacy and
// dispatcher counts as flexible, as before.
public static class WorkerLaneModel
{
    public const string Legacy = "legacy";
    public const string Flexible = "flexible";
    public const string Dispatcher = "dispatcher";

    // Flexible or dispatcher: every lane model but legacy pools the slots (the bulkhead classes take their isolation caps).
    public static bool IsFlexible(IConfiguration? configuration) =>
        !string.Equals(configuration?["Worker:LaneModel"]?.Trim(), Legacy, StringComparison.OrdinalIgnoreCase);

    public static bool IsDispatcher(IConfiguration? configuration) =>
        string.Equals(configuration?["Worker:LaneModel"]?.Trim(), Dispatcher, StringComparison.OrdinalIgnoreCase);

    // The lanes the queue metrics and the lane alert group jobs by: the dispatcher's work-type lanes, or the earlier lanes.
    public static IReadOnlyList<WorkerLane> Lanes(IConfiguration? configuration, int hotPriorityCeiling) =>
        IsDispatcher(configuration) ? DispatcherLanes.All(hotPriorityCeiling, DispatcherSettings.From(configuration)) : WorkerLanes.All(hotPriorityCeiling);

    // The filters a CASE over the lanes tests, in order (a dispatcher lane with two lease filters, toplu, listed twice under its
    // name): every job lands in the lane LaneFor names.
    public static IReadOnlyList<WorkerLane> ClassifyingLanes(IReadOnlyList<WorkerLane> lanes) => [.. lanes.SelectMany(DispatcherLanes.LeaseFilters)];

    public static string LaneFor(IConfiguration? configuration, string jobType, int priority, int hotPriorityCeiling) =>
        IsDispatcher(configuration) ? DispatcherLanes.LaneFor(jobType, priority, hotPriorityCeiling) : WorkerLanes.LaneFor(jobType, priority, hotPriorityCeiling);
}
