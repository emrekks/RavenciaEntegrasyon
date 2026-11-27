namespace MarketplaceHub.Worker;

// Queue phase 2 (27 Nov 2026), the dispatcher's state and its one decision (Worker:LaneModel=dispatcher). One place decides
// which lane a free slot goes to; LaneDispatcher runs it and leases one job at a time through JobLeaseService, which stays the
// source of truth for which job (priority, fairness, lock groups, backoffs, holds, bulkheads, the per-store cap, deferral).
//
// Slots. Each lane has Reserved slots no other lane ever uses and a Share of the shared slots (the total less every
// reservation). A lane may start a job on a reserved slot of its own, else on a free shared slot. Running jobs never exceed the
// total: sum(running) = sum(min(running, reserved)) + shared used <= reserved + shared. A running job is never taken back.
//
// Which lane gets a slot (Decide, pure): among the lanes that may lease now (a hint says they have work and they are not backing
// off) and have room, the first by tier: tier 0 a lane below its reservation, tier 1 a lane below its share of the shared slots,
// tier 2 a lane of short work beyond its share, tier 3 a lane of bulk work (LaneConfig.Bulk) beyond its share; within tiers 2
// and 3 the lane running the fewest jobs beyond its own slots; ties go by lane order (the display order). So a lane with work
// always gets its share of the shared slots before a lane beyond its share, short work gets the slots beyond the shares before
// bulk work, and they spread evenly.
//
// Hints (what the dispatcher knows of each lane's queue without reading it):
//   Ready         a notification named a job of the lane, a job of it became due, its blocker ended, or its last lease found
//                 a job: lease when there is room.
//   Unclassified  the last lease found nothing; the next census says why.
//   BlockedOn     the census saw the lane's runnable jobs held only by their stores (a busy lock group, the store cap, an order
//                 read of the store queued ahead): Ready again when a job of one of those stores ends here, or when the census
//                 sees one of its jobs free (the safety census; the short tick while a kullanici, webhook or yazma job waits).
//                 Other stores' job ends do not touch it (review of 27 Nov: a blocked lane retried on every job end anywhere,
//                 one empty lease and one census each, on the single lease line).
//   Blocked       the census saw a free job the lease did not take (a quota hold, a bulkhead, a race): tried again when a job
//                 ends or a hold ends, at most once per RetrySpacing, and at the safety census.
//   Idle          the census saw no runnable job: only a notification, a due job or the safety census makes it Ready.
// Each lane has an epoch that every signal moves on: a lease or census that started before a signal cannot undo it with its
// older answer (the "quiet lane" lesson of 24 Nov). A store whose job ended while a census ran is taken out of what that census
// says blocks a lane. Every event makes a lane Ready at most once and each Ready lane costs at most one lease that finds nothing.
public sealed class DispatcherCore
{
    public enum LaneHint
    {
        Idle,
        Ready,
        Unclassified,
        BlockedOn,
        Blocked
    }

    // Bulk: a lane of bulk work (toplu, katalog, arka-plan, ilk-cekim), served beyond its share only after every other lane.
    public sealed record LaneConfig(string Name, int Reserved, int Share, bool Bulk = false);

    // What the census saw for one lane: a job its lease could take now (HasFree), and the stores whose jobs only wait for
    // something of their own store (BlockedOn).
    public readonly record struct LaneCensus(bool HasFree, IReadOnlyCollection<Guid> BlockedOn)
    {
        public static readonly LaneCensus Empty = new(false, []);
        public bool HasRows => HasFree || BlockedOn.Count > 0;
    }

    public static readonly TimeSpan FirstFaultDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumFaultDelay = TimeSpan.FromSeconds(15);
    // A Blocked lane (held for a reason the census cannot name) is tried again at most this often.
    public static readonly TimeSpan RetrySpacing = TimeSpan.FromSeconds(1);

    private readonly LaneConfig[] lanes;
    private readonly int[] running;
    private readonly LaneHint[] hints;
    private readonly long[] epochs;
    private readonly DateTimeOffset[] notBefore;
    // When a lane held for an unnamed reason may be tried again (RetrySpacing); a notification clears it, a fault backoff does not.
    private readonly DateTimeOffset[] retryAt;
    private readonly DateTimeOffset[] lastEmpty;
    private readonly int[] faults;
    private readonly HashSet<Guid>[] blockedOn;
    // Stores whose job ended, in order (sequence, store): a census removes the stores that ended after it started.
    private readonly List<(long Sequence, Guid Connection)> endedStores = [];
    private long endedSequence;
    private int leasing = -1;

    public DispatcherCore(IReadOnlyList<LaneConfig> lanes)
    {
        this.lanes = [.. lanes.Select(lane => lane with { Reserved = Math.Max(0, lane.Reserved), Share = Math.Max(0, lane.Share) })];
        running = new int[this.lanes.Length];
        // At start nothing is known: every lane is read once (a job queued while the worker was down raised no notification).
        hints = [.. this.lanes.Select(_ => LaneHint.Ready)];
        epochs = new long[this.lanes.Length];
        notBefore = new DateTimeOffset[this.lanes.Length];
        retryAt = new DateTimeOffset[this.lanes.Length];
        lastEmpty = new DateTimeOffset[this.lanes.Length];
        faults = new int[this.lanes.Length];
        blockedOn = [.. this.lanes.Select(_ => new HashSet<Guid>())];
        Total = this.lanes.Sum(lane => lane.Reserved + lane.Share);
        SharedSlots = this.lanes.Sum(lane => lane.Share);
    }

    public int Total { get; }
    public int SharedSlots { get; }
    public IReadOnlyList<LaneConfig> Lanes => lanes;
    public int LeasingLane => leasing;
    public int RunningTotal => running.Sum();
    public int Running(int lane) => running[lane];
    public LaneHint Hint(int lane) => hints[lane];

    // The lane waits out its retry spacing (its last lease found nothing less than RetrySpacing ago).
    public bool Spacing(int lane, DateTimeOffset now) => retryAt[lane] > now;
    public long Epoch(int lane) => epochs[lane];
    public IReadOnlyCollection<Guid> BlockedOnStores(int lane) => blockedOn[lane];

    public int SharedUsed => SharedUsedOf(lanes, running);

    private static int SharedUsedOf(IReadOnlyList<LaneConfig> lanes, IReadOnlyList<int> running)
    {
        var used = 0;
        for (var index = 0; index < lanes.Count; index++) used += Math.Max(0, running[index] - lanes[index].Reserved);
        return used;
    }

    // The one decision, pure and deterministic: the lane the next free slot goes to, or -1 (none may lease, no room, or a lease
    // is already in flight: the dispatcher leases one job at a time, so the per-store cap the lease query counts is never raced
    // by a second lease of this worker).
    public static int Decide(IReadOnlyList<LaneConfig> lanes, IReadOnlyList<int> running, IReadOnlyList<bool> mayLease, bool leaseInFlight)
    {
        if (leaseInFlight) return -1;
        var shared = lanes.Sum(lane => lane.Share);
        var sharedFree = SharedUsedOf(lanes, running) < shared;
        var best = -1;
        var bestTier = int.MaxValue;
        var bestExcess = int.MaxValue;
        for (var index = 0; index < lanes.Count; index++)
        {
            if (!mayLease[index]) continue;
            int tier;
            if (running[index] < lanes[index].Reserved) tier = 0;
            else if (!sharedFree) continue;
            else tier = running[index] - lanes[index].Reserved < lanes[index].Share ? 1 : lanes[index].Bulk ? 3 : 2;
            // Beyond their own slots (tier 2), the lane running the fewest jobs beyond them goes first, so the shared slots
            // spread over the lanes with work instead of filling up with the first one's.
            var excess = tier >= 2 ? running[index] - lanes[index].Reserved - lanes[index].Share : 0;
            if (tier < bestTier || (tier == bestTier && excess < bestExcess))
            {
                best = index;
                bestTier = tier;
                bestExcess = excess;
            }
        }
        return best;
    }

    // Whether the lane could start a job now (its reservation or a free shared slot), whatever its hint.
    public bool HasRoom(int lane) => running[lane] < lanes[lane].Reserved || SharedUsed < SharedSlots;

    public int Next(DateTimeOffset now) =>
        Decide(lanes, running, [.. Enumerable.Range(0, lanes.Length).Select(index => hints[index] == LaneHint.Ready && notBefore[index] <= now && retryAt[index] <= now)], leasing >= 0);

    public enum StepKind
    {
        Lease,
        Census,
        Wait
    }

    // The dispatcher's next step: lease for the lane Decide picks; else read the census when the safety tick is due or a lease
    // found nothing (and a failed census's retry time has come: censusAllowed); else wait for a signal or a timer.
    public (StepKind Kind, int Lane) Plan(DateTimeOffset now, bool safetyDue, bool censusAllowed)
    {
        var lane = Next(now);
        if (lane >= 0) return (StepKind.Lease, lane);
        return (safetyDue || NeedsCensus) && censusAllowed && leasing < 0 ? (StepKind.Census, -1) : (StepKind.Wait, -1);
    }

    // Whether one of these lanes waits for a blocker (the dispatcher then reads the census on the short tick).
    public bool AnyWaiting(IReadOnlyCollection<int> laneIndexes) =>
        laneIndexes.Any(index => hints[index] is LaneHint.BlockedOn or LaneHint.Blocked or LaneHint.Unclassified);

    // A copy of the decision state (the exhaustive state test explores from it).
    internal DispatcherCore Clone()
    {
        var copy = new DispatcherCore(lanes);
        Array.Copy(running, copy.running, running.Length);
        Array.Copy(hints, copy.hints, hints.Length);
        Array.Copy(epochs, copy.epochs, epochs.Length);
        Array.Copy(notBefore, copy.notBefore, notBefore.Length);
        Array.Copy(retryAt, copy.retryAt, retryAt.Length);
        Array.Copy(lastEmpty, copy.lastEmpty, lastEmpty.Length);
        Array.Copy(faults, copy.faults, faults.Length);
        for (var index = 0; index < lanes.Length; index++) copy.blockedOn[index].UnionWith(blockedOn[index]);
        copy.endedStores.AddRange(endedStores);
        copy.endedSequence = endedSequence;
        copy.leasing = leasing;
        return copy;
    }

    internal bool BackingOff(int lane, DateTimeOffset now) => notBefore[lane] > now || retryAt[lane] > now;

    // A lease for the lane starts. Returns the lane's epoch (handed back with the answer) and whether a job leased now runs
    // outside the lane's own share (reserved plus share: what job_attempts.Borrowed records under the dispatcher).
    public (long Epoch, bool OutsideShare) BeginLease(int lane)
    {
        if (leasing >= 0) throw new InvalidOperationException("A lease is already in flight.");
        leasing = lane;
        return (epochs[lane], running[lane] >= lanes[lane].Reserved + lanes[lane].Share);
    }

    // The lease took a job: it runs (the lane stays Ready, it may have more).
    public void Leased(int lane)
    {
        EndLease(lane);
        running[lane]++;
        faults[lane] = 0;
        notBefore[lane] = default;
    }

    // The lease found nothing. Unless a signal came since it started (the epoch moved), the next census classifies the lane.
    public void LeaseFoundNothing(int lane, long epoch, DateTimeOffset now)
    {
        EndLease(lane);
        faults[lane] = 0;
        notBefore[lane] = default;
        lastEmpty[lane] = now;
        if (epochs[lane] == epoch) hints[lane] = LaneHint.Unclassified;
    }

    // The lease failed (the database, a timeout): the lane stays Ready and waits 1, 2, 4 ... 15 s before its next try.
    public void LeaseFailed(int lane, DateTimeOffset now)
    {
        EndLease(lane);
        faults[lane] = Math.Min(faults[lane] + 1, 16);
        var delay = TimeSpan.FromTicks(Math.Min(MaximumFaultDelay.Ticks, FirstFaultDelay.Ticks << (faults[lane] - 1)));
        notBefore[lane] = now + delay;
    }

    // The lease was cancelled by the shutdown: nothing changes.
    public void LeaseAbandoned(int lane) => EndLease(lane);

    private void EndLease(int lane)
    {
        if (leasing != lane) throw new InvalidOperationException($"No lease in flight for lane {lane}.");
        leasing = -1;
    }

    // A job of the lane ended here. The lanes waiting for its store try again; the lanes held for an unnamed reason too (at most
    // once per RetrySpacing).
    public void Ended(int lane, Guid? connection, DateTimeOffset now)
    {
        if (running[lane] <= 0) throw new InvalidOperationException($"Lane {lane} has no running job.");
        running[lane]--;
        if (connection is { } store)
        {
            endedStores.Add((++endedSequence, store));
            if (endedStores.Count > 4_096) endedStores.RemoveRange(0, 2_048);
            for (var index = 0; index < lanes.Length; index++)
                if (hints[index] == LaneHint.BlockedOn && blockedOn[index].Contains(store)) MakeReady(index);
        }
        RetryBlocked(now);
    }

    // A notification named a job of the lane, or a job of it became due.
    public void Notify(int lane) => MakeReady(lane);

    private void MakeReady(int lane)
    {
        epochs[lane]++;
        hints[lane] = LaneHint.Ready;
        blockedOn[lane].Clear();
        retryAt[lane] = default;
    }

    // A job ended in another process ("freed") or a quota hold ended: the lanes held for a reason the census cannot name try
    // again, at most once per RetrySpacing each. The lanes waiting for a store are left to that store's job end here or to the
    // census (a "freed" names no store; on one worker every "freed" is a job end it already saw).
    public void RetryBlocked(DateTimeOffset now)
    {
        for (var index = 0; index < lanes.Length; index++)
        {
            if (hints[index] is not (LaneHint.Blocked or LaneHint.Unclassified)) continue;
            var allowed = lastEmpty[index] + RetrySpacing;
            MakeReady(index);
            if (allowed > retryAt[index]) retryAt[index] = allowed;
        }
    }

    public bool NeedsCensus => hints.Any(hint => hint == LaneHint.Unclassified);

    // What a census hands back to ApplyCensus: each lane's epoch and the store-end sequence when it started.
    public (long[] Epochs, long EndedSequence) CensusStart() => ([.. epochs], endedSequence);

    // The census read each lane's runnable jobs (censuses[lane]). A lane whose epoch moved since the census started keeps its
    // hint (a signal is newer than the census). Otherwise:
    //   a free job:        Unclassified -> Blocked (its last lease found none: held for an unnamed reason), Idle -> Ready,
    //                      BlockedOn -> Ready, Blocked -> Ready at the safety census;
    //   only store-held:   -> BlockedOn those stores, less the stores whose job ended since the census started (none left:
    //                      Ready); a lane whose lease is in flight keeps its hint;
    //   no rows:           -> Idle (a Ready lane too: its lease would find nothing), unless its lease is in flight.
    public void ApplyCensus(IReadOnlyList<LaneCensus> censuses, (long[] Epochs, long EndedSequence) start, bool safetyTick, DateTimeOffset now)
    {
        var endedSince = endedStores.Where(item => item.Sequence > start.EndedSequence).Select(item => item.Connection).ToHashSet();
        for (var index = 0; index < lanes.Length; index++)
        {
            if (epochs[index] != start.Epochs[index] || leasing == index) continue;
            var census = censuses[index];
            if (!census.HasRows)
            {
                hints[index] = LaneHint.Idle;
                blockedOn[index].Clear();
                continue;
            }
            if (census.HasFree)
            {
                switch (hints[index])
                {
                    case LaneHint.Unclassified:
                        hints[index] = safetyTick ? LaneHint.Ready : LaneHint.Blocked;
                        blockedOn[index].Clear();
                        if (safetyTick) retryAt[index] = lastEmpty[index] + RetrySpacing;
                        break;
                    case LaneHint.Blocked:
                        if (safetyTick) MakeReady(index);
                        break;
                    case LaneHint.Idle or LaneHint.BlockedOn:
                        MakeReady(index);
                        break;
                }
                continue;
            }
            var stores = census.BlockedOn.Where(store => !endedSince.Contains(store)).ToHashSet();
            if (stores.Count == 0)
            {
                MakeReady(index);
                continue;
            }
            hints[index] = LaneHint.BlockedOn;
            blockedOn[index].Clear();
            blockedOn[index].UnionWith(stores);
        }
    }

    // The earliest moment a Ready lane backing off may lease again (null: none waits).
    public DateTimeOffset? NextRetry(DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        for (var index = 0; index < lanes.Length; index++)
            if (hints[index] == LaneHint.Ready && (notBefore[index] > retryAt[index] ? notBefore[index] : retryAt[index]) is var at && at > now && (next is null || at < next))
                next = at;
        return next;
    }

    // The invariants (tests): null when they hold.
    public string? Violation()
    {
        if (RunningTotal > Total) return $"running {RunningTotal} of {Total}";
        if (SharedUsed > SharedSlots) return $"shared slots used {SharedUsed} of {SharedSlots}";
        for (var index = 0; index < lanes.Length; index++)
        {
            if (running[index] < 0) return $"lane {lanes[index].Name} runs {running[index]}";
            if ((hints[index] == LaneHint.BlockedOn) != (blockedOn[index].Count > 0)) return $"lane {lanes[index].Name} is {hints[index]} with {blockedOn[index].Count} blocking store(s)";
        }
        return null;
    }
}
