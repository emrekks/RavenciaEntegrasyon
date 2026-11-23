using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Jobs;

// Worker lanes step 4 (23 Nov 2026, owner's go): the lane model, read the same way by the worker (LaneSlotPool) and the api
// (borrowed slots in the lane metrics). Flexible is the default since this release; Worker:LaneModel=legacy (on the server:
// MARKETPLACEHUB_WORKER_LANE_MODEL=legacy in production.env, which deploy/compose/compose.production.yaml hands to both
// services) brings back the earlier model, each lane on its own slots only. Any other value counts as flexible.
public static class WorkerLaneModel
{
    public const string Legacy = "legacy";
    public const string Flexible = "flexible";

    public static bool IsFlexible(IConfiguration? configuration) =>
        !string.Equals(configuration?["Worker:LaneModel"]?.Trim(), Legacy, StringComparison.OrdinalIgnoreCase);
}
