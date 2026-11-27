namespace MarketplaceHub.Worker;

// Queue phase 2 (27 Nov 2026), the dispatcher's state and its one decision (Worker:LaneModel=dispatcher). One place decides
// which lane a free slot goes to; LaneDispatcher runs it and leases one job at a time through JobLeaseService, which stays the
// source of truth for which job (priority, fairness, lock groups, backoffs, holds, bulkheads, the per-store cap).
//
// Slots. Each lane has Reserved slots no other lane ever uses (kullanici, acil, siparis-okuma, durum-taramasi) and a Share of
// the shared slots (the total less every reservation). A lane may start a job on a reserved slot of its own, else on a free
// shared slot. Running jobs never exceed the total: sum(running) = sum(min(running, reserved)) + shared used <= reserved +
// shared. A running job is never taken back.
//
// Which lane gets a slot (Decide, pure): among the lanes that may lease now (a hint says they have work and they are not backing
// off after a failure) and have room, the first by tier: tier 0 a lane below its reservation, tier 1 a lane below its share of
// the shared slots, tier 2 any other, and within tier 2 the lane running the fewest jobs beyond its own slots; ties go by lane
// order, the display order (Kullanıcı, Acil, Sipariş Okuma, Durum Taraması, İade, Katalog, Arka Plan, İlk Çekim). So a lane
// with work always gets its share of the shared slots before a lane beyond its share, and the slots beyond spread evenly.
//
// Hints (what the dispatcher knows of each lane's queue without reading it):
//   Ready         a notification named a job of the lane, a job of it became due, a lock group or a quota hold may have
//                 opened, or the lane's last lease found a job: lease when there is room.
//   Unclassified  the last lease found nothing; the next census says whether the lane's jobs are blocked or gone.
//   Blocked       the census saw runnable jobs the lease did not take (a lock group, the per-store cap, a quota hold, a
//                 bulkhead): leased again when a job ends (here or elsewhere), a quota hold ends, or at the safety census.
//   Idle          the census saw no runnable job: only a notification, a due job or the safety census makes it Ready.
// Each lane has an epoch that every signal moves on: a lease or census that started before a signal cannot undo it with its
// older answer (the "quiet lane" lesson of 24 Nov). Every event makes each lane Ready at most once, and each Ready lane costs
// at most one lease that finds nothing, so the leases per event are bounded by the number of lanes and an idle worker reads
// the queue once per safety tick.
public sealed class DispatcherCore
{
    public enum LaneHint
    {
        Idle,
        Ready,
        Unclassified,
        Blocked
    }

    public sealed record LaneConfig(string Name, int Reserved, int Share);

    public static readonly TimeSpan FirstFaultDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumFaultDelay = TimeSpan.FromSeconds(15);

    private readonly LaneConfig[] lanes;
    private readonly int[] running;
    private readonly LaneHint[] hints;
    private readonly long[] epochs;
    private readonly DateTimeOffset[] notBefore;
    private readonly int[] faults;
    private int leasing = -1;

    public DispatcherCore(IReadOnlyList<LaneConfig> lanes, DateTimeOffset? createdAt = null)
    {
        this.lanes = [.. lanes.Select(lane => lane with { Reserved = Math.Max(0, lane.Reserved), Share = Math.Max(0, lane.Share) })];
        running = new int[this.lanes.Length];
        // At start nothing is known: every lane is read once (a job queued while the worker was down raised no notification).
        hints = [.. this.lanes.Select(_ => LaneHint.Ready)];
        epochs = new long[this.lanes.Length];
        notBefore = new DateTimeOffset[this.lanes.Length];
        faults = new int[this.lanes.Length];
        Total = this.lanes.Sum(lane => lane.Reserved + lane.Share);
        SharedSlots = this.lanes.Sum(lane => lane.Share);
        spells = new NoRoomSpells(this.lanes.Length, createdAt ?? DateTimeOffset.UtcNow);
    }

    public int Total { get; }
    public int SharedSlots { get; }
    public IReadOnlyList<LaneConfig> Lanes => lanes;
    public int LeasingLane => leasing;
    public int RunningTotal => running.Sum();
    public int Running(int lane) => running[lane];
    public LaneHint Hint(int lane) => hints[lane];
    public long Epoch(int lane) => epochs[lane];

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
            else tier = running[index] - lanes[index].Reserved < lanes[index].Share ? 1 : 2;
            // Beyond their own slots (tier 2), the lane running the fewest jobs beyond them goes first, so the shared slots
            // spread over the lanes with work instead of filling up with the first one's.
            var excess = tier == 2 ? running[index] - lanes[index].Reserved - lanes[index].Share : 0;
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
        Decide(lanes, running, [.. Enumerable.Range(0, lanes.Length).Select(index => hints[index] == LaneHint.Ready && notBefore[index] <= now)], leasing >= 0);

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

    // A copy of the decision state (the exhaustive state test explores from it); the no-room spells start empty.
    internal DispatcherCore Clone()
    {
        var copy = new DispatcherCore(lanes, spells.CreatedAt);
        Array.Copy(running, copy.running, running.Length);
        Array.Copy(hints, copy.hints, hints.Length);
        Array.Copy(epochs, copy.epochs, epochs.Length);
        Array.Copy(notBefore, copy.notBefore, notBefore.Length);
        Array.Copy(faults, copy.faults, faults.Length);
        copy.leasing = leasing;
        return copy;
    }

    internal bool BackingOff(int lane, DateTimeOffset now) => notBefore[lane] > now;

    // A lease for the lane starts. Returns the lane's epoch (handed back with the answer) and whether a job leased now runs
    // outside the lane's own share (reserved plus share: what job_attempts.Borrowed records under the dispatcher).
    public (long Epoch, bool OutsideShare) BeginLease(int lane)
    {
        if (leasing >= 0) throw new InvalidOperationException("A lease is already in flight.");
        leasing = lane;
        return (epochs[lane], running[lane] >= lanes[lane].Reserved + lanes[lane].Share);
    }

    // The lease took a job: it runs (the lane stays Ready, it may have more).
    public void Leased(int lane, DateTimeOffset now)
    {
        EndLease(lane);
        running[lane]++;
        faults[lane] = 0;
        notBefore[lane] = default;
        spells.Track(this, now);
    }

    // The lease found nothing. Unless a signal came since it started (the epoch moved), the next census classifies the lane.
    public void LeaseFoundNothing(int lane, long epoch)
    {
        EndLease(lane);
        faults[lane] = 0;
        notBefore[lane] = default;
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

    // A job of the lane ended here. Its end may have opened a lock group, the per-store cap or a bulkhead for any lane.
    public void Ended(int lane, DateTimeOffset now)
    {
        if (running[lane] <= 0) throw new InvalidOperationException($"Lane {lane} has no running job.");
        running[lane]--;
        spells.Track(this, now);
        Unblock();
    }

    // A notification named a job of the lane, or a job of it became due.
    public void Notify(int lane)
    {
        epochs[lane]++;
        hints[lane] = LaneHint.Ready;
    }

    // Something may have made a held job leasable that no notification names (a job ended elsewhere, a quota hold ended):
    // every lane whose jobs were blocked, or whose last lease found nothing, tries once more. Every epoch moves on, so a lease
    // or census in flight cannot mark a lane blocked or idle after it.
    public void Unblock()
    {
        for (var index = 0; index < lanes.Length; index++)
        {
            epochs[index]++;
            if (hints[index] is LaneHint.Blocked or LaneHint.Unclassified) hints[index] = LaneHint.Ready;
        }
    }

    public bool NeedsCensus => hints.Any(hint => hint == LaneHint.Unclassified);

    public long[] Epochs() => [.. epochs];

    // The census read which lanes have runnable jobs (withRows[lane]). A lane whose epoch moved since the census started keeps
    // its hint (a signal is newer than the census). Otherwise:
    //   with rows:    Unclassified -> Blocked (its last lease found none of them), Idle -> Ready (jobs no notification
    //                 announced); at the safety census Blocked -> Ready as well (a lost "freed" waits one tick at most);
    //   without rows: -> Idle (a Ready lane too: its lease would find nothing).
    public void ApplyCensus(IReadOnlyList<bool> withRows, IReadOnlyList<long> epochsAtStart, bool safetyTick)
    {
        for (var index = 0; index < lanes.Length; index++)
        {
            if (epochs[index] != epochsAtStart[index]) continue;
            if (!withRows[index])
            {
                // A lane whose lease is in flight keeps its hint: LeaseFoundNothing or Leased settles it.
                if (leasing != index) hints[index] = LaneHint.Idle;
                continue;
            }
            hints[index] = hints[index] switch
            {
                LaneHint.Unclassified => safetyTick ? LaneHint.Ready : LaneHint.Blocked,
                LaneHint.Idle => LaneHint.Ready,
                LaneHint.Blocked => safetyTick ? LaneHint.Ready : LaneHint.Blocked,
                _ => hints[index]
            };
        }
    }

    // The earliest moment a Ready lane backing off after a failure may lease again (null: none waits).
    public DateTimeOffset? NextRetry(DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        for (var index = 0; index < lanes.Length; index++)
            if (hints[index] == LaneHint.Ready && notBefore[index] > now && (next is null || notBefore[index] < next))
                next = notBefore[index];
        return next;
    }

    // The invariants (tests): null when they hold.
    public string? Violation()
    {
        if (RunningTotal > Total) return $"running {RunningTotal} of {Total}";
        if (SharedUsed > SharedSlots) return $"shared slots used {SharedUsed} of {SharedSlots}";
        for (var index = 0; index < lanes.Length; index++)
            if (running[index] < 0) return $"lane {lanes[index].Name} runs {running[index]}";
        return null;
    }

    // Queue phase 0 recording: when each lane had no free slot (neither its reservation nor a shared slot), so a leased job's
    // wait can say how long no slot was free for it (job_attempts.WaitSlotMs). Measurement only: nothing decides by it.
    private readonly NoRoomSpells spells;

    public IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> NoRoomSpellsOf(int lane, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now) =>
        spells.Of(lane, from, to, now);

    private sealed class NoRoomSpells(int laneCount, DateTimeOffset createdAt)
    {
        private static readonly TimeSpan Kept = TimeSpan.FromHours(6);
        private const int MaximumPerLane = 1_024;
        private readonly DateTimeOffset?[] since = new DateTimeOffset?[laneCount];
        private readonly List<(DateTimeOffset From, DateTimeOffset To)>[] ended = [.. Enumerable.Range(0, laneCount).Select(_ => new List<(DateTimeOffset, DateTimeOffset)>())];
        public DateTimeOffset CreatedAt { get; } = createdAt;

        public void Track(DispatcherCore core, DateTimeOffset now)
        {
            for (var lane = 0; lane < since.Length; lane++)
            {
                if (!core.HasRoom(lane))
                {
                    since[lane] ??= now;
                    continue;
                }
                if (since[lane] is not { } start) continue;
                since[lane] = null;
                if (now <= start) continue;
                var list = ended[lane];
                list.Add((start, now));
                var drop = 0;
                while (drop < list.Count && list[drop].To < now - Kept) drop++;
                if (list.Count - drop > MaximumPerLane) drop = list.Count - (MaximumPerLane * 4 / 5);
                if (drop > 0) list.RemoveRange(0, drop);
            }
        }

        public IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> Of(int lane, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
        {
            var result = new List<(DateTimeOffset From, DateTimeOffset To)>();
            if (from < CreatedAt) from = CreatedAt;
            IEnumerable<(DateTimeOffset From, DateTimeOffset To)> all = ended[lane];
            if (since[lane] is { } open) all = all.Append((open, now));
            foreach (var (start, end) in all)
            {
                if (end <= from || start >= to) continue;
                var clipped = (From: start < from ? from : start, To: end > to ? to : end);
                if (result.Count > 0 && clipped.From <= result[^1].To) { if (clipped.To > result[^1].To) result[^1] = (result[^1].From, clipped.To); }
                else result.Add(clipped);
            }
            return result;
        }
    }
}
