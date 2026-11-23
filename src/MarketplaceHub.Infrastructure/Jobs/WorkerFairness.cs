using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Jobs;

// Worker lanes step 2 (19 Nov 2026), connection fairness: when the worker last leased a job of each connection. The lease
// orders a priority's candidates by it (JobFairness), so the connection served longest ago goes first. Process-wide (one
// worker process) and kept in memory only, on purpose: after a restart every connection counts as never served, so the
// first passes order by running load and age (the earlier order), which is fair too; nothing is lost that matters later.
public sealed class ConnectionLeaseClock
{
    public static ConnectionLeaseClock Shared { get; } = new();

    internal const int MaximumConnections = 10_000;
    private readonly object gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> leasedAt = [];

    public void Record(Guid connectionId, DateTimeOffset now)
    {
        lock (gate)
        {
            leasedAt[connectionId] = now;
            if (leasedAt.Count <= MaximumConnections) return;
            foreach (var oldest in leasedAt.OrderBy(entry => entry.Value).Take(MaximumConnections / 10).Select(entry => entry.Key).ToList())
                leasedAt.Remove(oldest);
        }
    }

    public JobFairness Fairness(TimeSpan aging)
    {
        lock (gate)
            return new JobFairness(aging, [.. leasedAt.Keys], [.. leasedAt.Values]);
    }
}

// Worker lanes step 2, per-marketplace bulkheads: "at most N jobs of a class for one tenant's marketplace at once"
// (JobBulkhead: per tenant and marketplace, only jobs within the class's priority range 0..hot ceiling count and are limited).
// While today's dedicated lanes exist every class cap defaults to 0 (off): the lanes already give that isolation, and a cap
// would only add a way to wait. IsolationCap is the cap a class takes when the lanes are merged (step 3), the isolation one
// dedicated slot gives today. A lane cap (Worker:Bulkhead:LaneCaps:<lane>) limits the jobs of one tenant's marketplace in a
// shared lane; it is off by default because the replay of the live week shows it only lengthens waits (five tenants with
// per-marketplace caps hot 2, background 1, catalog 2: hot p95 18.6 -> 30.1 s, catalog longest 3148 -> 6297 s; two
// tenants: background p95 0.3 -> 4.8 s), while the quota-aware lease (step 1) already keeps a throttled marketplace out of
// the shared slots.
public static class WorkerBulkheads
{
    public sealed record BulkheadClass(string Name, IReadOnlyList<string> JobTypes, int DefaultCap, int IsolationCap);

    public static IReadOnlyList<BulkheadClass> DefaultClasses { get; } =
    [
        new("pazarama-orders", WorkerLanes.PazaramaOrderJobTypes, 0, 1),
        new("pttavm-orders", WorkerLanes.PttAvmOrderJobTypes, 0, 1),
        new("hepsiburada-orders", WorkerLanes.HepsiburadaOrderJobTypes, 0, 1),
        new("hepsiburada-status", WorkerLanes.HepsiburadaStatusJobTypes, 0, 1),
        new("order-status", WorkerLanes.OrderStatusJobTypes, 0, 1)
    ];

    // The bulkhead a lane's lease passes: every class with a cap above zero (counted in the hot priority range), and the
    // lane's own cap (null: none). Nothing set: an empty bulkhead, which the lease ignores.
    public static JobBulkhead For(IReadOnlyDictionary<string, int> classCaps, int? laneCap, int hotPriorityCeiling = WorkerLanes.DefaultHotPriorityCeiling)
    {
        var classes = DefaultClasses.Where(item => classCaps.GetValueOrDefault(item.Name, item.DefaultCap) > 0).ToList();
        return new JobBulkhead(
            [.. classes.SelectMany(item => item.JobTypes)],
            [.. classes.SelectMany(item => item.JobTypes.Select(_ => item.Name))],
            [.. classes.Select(item => item.Name)],
            [.. classes.Select(item => classCaps.GetValueOrDefault(item.Name, item.DefaultCap))],
            laneCap is > 0 ? laneCap : null,
            0,
            hotPriorityCeiling);
    }
}
