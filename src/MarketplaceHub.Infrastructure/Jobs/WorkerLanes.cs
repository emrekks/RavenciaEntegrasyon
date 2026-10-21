using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Jobs;

// One worker lane: the loops that lease jobs of a priority range and job-type set. The worker starts its
// loops from these definitions and the queue metrics group jobs by them, so both describe the same lanes.
public sealed record WorkerLane(string Name, int DefaultSlots, int? MaximumPriority, int? MinimumPriority, IReadOnlyList<string> IncludedJobTypes, IReadOnlyList<string> ExcludedJobTypes)
{
    public bool Takes(string jobType, int priority) =>
        (MaximumPriority is not { } maximum || priority <= maximum)
        && (MinimumPriority is not { } minimum || priority >= minimum)
        && (IncludedJobTypes.Count == 0 || IncludedJobTypes.Contains(jobType))
        && !ExcludedJobTypes.Contains(jobType);
}

public static class WorkerLanes
{
    public const int DefaultHotPriorityCeiling = 2;

    public static readonly IReadOnlyList<string> HepsiburadaOrderJobTypes =
        [MarketplaceJobTypes.HepsiburadaOrderSync, MarketplaceJobTypes.HepsiburadaOrderRecoverySync];
    // Pazarama has no change filter on orders, so its status scan re-reads open orders one by one;
    // the three order flows run in their own lane so they never occupy the hot lane's slots.
    public static readonly IReadOnlyList<string> PazaramaOrderJobTypes =
        [MarketplaceJobTypes.PazaramaOrderSync, MarketplaceJobTypes.PazaramaOrderRecoverySync, MarketplaceJobTypes.PazaramaOrderStatusSync];
    // PTT AVM reads whole order-date windows (no paging); its three order flows get a lane of their own too.
    public static readonly IReadOnlyList<string> PttAvmOrderJobTypes =
        [MarketplaceJobTypes.PttAvmOrderSync, MarketplaceJobTypes.PttAvmOrderRecoverySync, MarketplaceJobTypes.PttAvmOrderStatusSync];
    public static readonly IReadOnlyList<string> ReturnReadJobTypes =
        [MarketplaceJobTypes.ReturnSync, MarketplaceJobTypes.HepsiburadaReturnSync, MarketplaceJobTypes.ReturnStatusSync];
    // The open-order status scans can run for minutes (Trendyol's for up to twenty); they have their own
    // slot so webhooks, new-order reads and user writes in the hot lane never wait behind them. Hepsiburada's
    // status scan has a lane of its own as well.
    public static readonly IReadOnlyList<string> OrderStatusJobTypes = [MarketplaceJobTypes.OrderStatusSync, MarketplaceJobTypes.ShopifyOrderStatusSync];
    public static readonly IReadOnlyList<string> HepsiburadaStatusJobTypes = [MarketplaceJobTypes.HepsiburadaOrderStatusSync];
    public static readonly IReadOnlyList<string> HotLaneExcludedJobTypes =
        [.. HepsiburadaOrderJobTypes, .. PazaramaOrderJobTypes, .. PttAvmOrderJobTypes, .. ReturnReadJobTypes, .. OrderStatusJobTypes, .. HepsiburadaStatusJobTypes];

    // In precedence order: a job that more than one lane could lease is reported under the first.
    public static IReadOnlyList<WorkerLane> All(int hotPriorityCeiling = DefaultHotPriorityCeiling) =>
    [
        // Jobs a user waits for (JobPriorityPolicy.UserAction) never queue behind syncs; bulk user work
        // (JobPriorityPolicy.BulkUserAction) shares the lane but is leased after them.
        new("user", 2, JobPriorityPolicy.BulkUserAction, null, [], []),
        new("initial-data-sync", 1, JobPriorityPolicy.InitialDataSync, JobPriorityPolicy.InitialDataSync, [], []),
        new("order-status", 1, hotPriorityCeiling, 0, OrderStatusJobTypes, []),
        // Return reads and product-catalog syncs have dedicated lanes and are kept out of the shared
        // hot/background lanes so long reads cannot occupy their slots.
        new("returns-read", 2, null, null, ReturnReadJobTypes, []),
        new("product-catalog", 1, null, null, JobTypeRegistry.ProductCatalogSyncJobTypes, []),
        new("hepsiburada-status", 1, hotPriorityCeiling, 0, HepsiburadaStatusJobTypes, []),
        new("hepsiburada-orders", 1, hotPriorityCeiling, 0, HepsiburadaOrderJobTypes, []),
        new("pazarama-orders", 1, hotPriorityCeiling, 0, PazaramaOrderJobTypes, []),
        new("pttavm-orders", 1, hotPriorityCeiling, 0, PttAvmOrderJobTypes, []),
        new("hot", 3, hotPriorityCeiling, 0, [], HotLaneExcludedJobTypes),
        new("background", 2, null, hotPriorityCeiling + 1, [], [.. JobTypeRegistry.ProductCatalogSyncJobTypes, .. ReturnReadJobTypes])
    ];

    // The lane that leases a job of this type and priority; "unassigned" when no lane takes it.
    public static string LaneFor(string jobType, int priority, int hotPriorityCeiling = DefaultHotPriorityCeiling) =>
        All(hotPriorityCeiling).FirstOrDefault(lane => lane.Takes(jobType, priority))?.Name ?? "unassigned";

    // Every running job can hold two pooled connections at once (its own context with the advisory lock,
    // and a heartbeat or completion scope); the scheduler, reaper, retention and sweep loops need a few more.
    public static int RequiredPoolSize(int totalSlots) => (2 * Math.Max(0, totalSlots)) + 10;
}
