namespace MarketplaceHub.Worker;

// Worker lanes step 3 (19 Nov 2026), flexible lanes (Worker:LaneModel=flexible): the lanes stay as they are, and their slots
// become one pool of the same size. Each lane keeps its own slots (its guaranteed floor); a lane whose own slots are all busy
// may borrow an idle slot of another lane, so a backlog in one lane (five tenants' catalog reads, six stores' first imports)
// uses capacity the quiet lanes do not need.
//   - A borrowed slot is never taken back from a running job; when the lending lane has work (a notification named a job it
//     can lease, or one of its slots just found one: NoteWork), its own slot waits for the next job anywhere to end and
//     borrowers take nothing new meanwhile (the lane "wants" its slot). A lane that only polls idly wants nothing.
//   - Protected lanes (Worker:FlexibleLanes:ProtectedLanes; default the user lane and every lane that reads new orders or
//     order states) never lend: their idle slots stay free for a user action or a new order at once. They may borrow.
//   - A lane may borrow up to its limit (Worker:FlexibleLanes:BorrowLimits:<lane>; default none).
//   - The pool never runs more jobs than the lanes' slots add up to, so no marketplace sees more requests than before; the
//     quota limiter, lock groups, holds, fairness and bulkheads apply to every lease as before.
// Replay of the live week (sim4, with steps 1 and 2, order lanes protected): five tenants user p95 35 -> 3.9 s, first imports
// 346 -> 0 s, catalog 431 -> 75 s, jobs waiting over 30 min 41 -> 0; six stores activated together: first imports p95
// 6574 -> 1779 s; long reads filling the pool (nine 60-minute catalog reads, twelve 30-minute first imports): order reads
// wait at most 1 s either way, catalog p95 7194 -> 3595 s; one tenant, a full quota and a marketplace timing out for 20 min
// unchanged or better. Lanes per job class instead of these lanes gave nothing more (fixed: the same as today; pooled: first
// imports of six stores 1475 s), letting user and hot lend their slots delayed new orders by up to 260 s, and borrow limits
// on the long lanes lost most of the gain (4 slots: catalog p95 119 s, six stores' first imports 2675 s), so the lanes keep
// their names, order lanes never lend, and no borrow limit is set by default.
public sealed class LaneSlotPool
{
    // How long a lane counts as having work after a notification named a job it can lease or one of its slots found a job.
    public static readonly TimeSpan WorkNoticeTtl = TimeSpan.FromMinutes(1);

    private readonly object gate = new();
    private readonly Dictionary<string, int> reserved;
    private readonly HashSet<string> protectedLanes;
    private readonly Dictionary<string, int> borrowLimits;
    private readonly TimeProvider clock;
    private readonly Dictionary<string, int> own = [];
    private readonly Dictionary<string, int> borrowed = [];
    private readonly HashSet<string> wanting = [];
    private readonly Dictionary<string, DateTimeOffset> workNoticed = [];
    // Lanes whose last poll found nothing, quiet (borrowing nothing) until this moment, and when each lane last found nothing.
    private readonly Dictionary<string, DateTimeOffset> quietUntil = [];
    private readonly Dictionary<string, DateTimeOffset> lastQuiet = [];
    // Each lane's epoch: NoteWork and Unquiet move it on, so a poll that started before a notification, a job's end or the end
    // of a quota hold cannot mark the lane quiet after it (its snapshot may have missed the job those made leasable).
    private readonly Dictionary<string, long> epochs = [];
    // Lanes with known work whose borrowing slot found no room: the next release wakes them, an empty poll's too.
    private readonly HashSet<string> starved = [];
    private readonly DateTimeOffset createdAt;
    private int running;

    // How long a lane stays quiet after an empty poll at most: a job may become leasable without anything telling the lane (a
    // lock group or a bulkhead freed by another worker, a missed notification), and it is never forgotten for longer than a
    // slot's longest idle wait. A job's end, a "freed" notification and the end of a quota hold end the quiet at once (Unquiet).
    public static readonly TimeSpan QuietFor = JobIdleWait.Maximum;

    // An unprotected lane whose own slot finds no room and that found nothing for this long wants its slot back anyway, so a
    // job no notification announced (a missed one, at startup or a listener reconnect) waits at most this long plus the end
    // of the next unprotected or borrowed job, never for a whole backlog of borrowed reads.
    public static readonly TimeSpan IdleProbeAfter = TimeSpan.FromMinutes(2);

    public LaneSlotPool(IReadOnlyDictionary<string, int> slots, IEnumerable<string> protectedLanes, IReadOnlyDictionary<string, int>? borrowLimits = null, TimeProvider? clock = null)
    {
        reserved = slots.ToDictionary(item => item.Key, item => Math.Max(0, item.Value), StringComparer.Ordinal);
        this.protectedLanes = new HashSet<string>(protectedLanes.Where(reserved.ContainsKey), StringComparer.Ordinal);
        this.borrowLimits = (borrowLimits ?? new Dictionary<string, int>()).Where(item => reserved.ContainsKey(item.Key))
            .ToDictionary(item => item.Key, item => Math.Max(0, item.Value), StringComparer.Ordinal);
        this.clock = clock ?? TimeProvider.System;
        createdAt = this.clock.GetUtcNow();
        Total = reserved.Values.Sum();
    }

    public int Total { get; }

    public IReadOnlyCollection<string> ProtectedLanes => protectedLanes;

    public IReadOnlyDictionary<string, int> BorrowLimits => borrowLimits;

    // Raised (outside the lock) after a slot was freed, with the lanes that may lease now: the lanes waiting for one of their
    // own slots first, else the lanes whose own slots are all busy (they may borrow).
    public event Action<IReadOnlyList<string>>? SlotFreed;

    // The lane has work: a notification named a job it can lease, or one of its slots just found one.
    public void NoteWork(string lane)
    {
        lock (gate)
            if (reserved.ContainsKey(lane))
            {
                workNoticed[lane] = clock.GetUtcNow();
                epochs[lane] = epochs.GetValueOrDefault(lane) + 1;
                quietUntil.Remove(lane);
            }
    }

    // The lane's work epoch now: a slot reads it before it polls and hands it to NoteNoWork.
    public long Epoch(string lane)
    {
        lock (gate) return epochs.GetValueOrDefault(lane);
    }

    // One of the lane's slots polled and found nothing: it has no work it could take now. seenEpoch: the epoch the slot read
    // before its poll; when work was noted or a job ended since, the poll's answer is stale and changes nothing (false). The
    // lane is quiet for QuietFor.
    public bool NoteNoWork(string lane, long? seenEpoch = null)
    {
        lock (gate)
        {
            if (seenEpoch is { } seen && seen != epochs.GetValueOrDefault(lane)) return false;
            workNoticed.Remove(lane);
            wanting.Remove(lane);
            starved.Remove(lane);
            if (!reserved.ContainsKey(lane)) return true;
            var now = clock.GetUtcNow();
            quietUntil[lane] = now + QuietFor;
            lastQuiet[lane] = now;
            return true;
        }
    }

    // Something may have made a job leasable that no notification names: a job ended (its connection's lock group, its
    // marketplace's bulkhead may have opened; JobWakeRouter's "freed", or a release of this worker's), or quota-aware leasing
    // ended a hold. No lane is quiet any more, and a poll running now cannot make one quiet (its snapshot may be older).
    public void Unquiet()
    {
        lock (gate) UnquietLocked();
    }

    public void QuotaReleased() => Unquiet();

    private void UnquietLocked()
    {
        quietUntil.Clear();
        foreach (var lane in reserved.Keys) epochs[lane] = epochs.GetValueOrDefault(lane) + 1;
    }

    // Called under the lock.
    private bool IsQuietNow(string lane) => quietUntil.TryGetValue(lane, out var until) && clock.GetUtcNow() < until;

    // A slot of the lane asks for room before it polls the queue. own: one of the lane's own slots; otherwise a borrowing slot.
    public bool TryClaim(string lane, bool ownSlot)
    {
        lock (gate)
        {
            if (!reserved.TryGetValue(lane, out var floor)) return false;
            if (ownSlot)
            {
                // Every claim, an unprotected lane's own slot included, leaves the idle own slots of the other protected
                // lanes free (lanes that want theirs back do not block each other: only borrowers leave those free too). So
                // the unprotected lanes never run more than the pool less the protected lanes' slots, and a protected lane
                // always has its own slots at once: a lane whose slot was lent out takes it back from the unprotected part only.
                if (running + Kept(lane, includeWanting: false) >= Total)
                {
                    // No room. A lane known to have work wants its slot back, so borrowers take nothing new; a lane that only
                    // polls idly does not hold the next free slot up, unless it found nothing for IdleProbeAfter (it may
                    // have a job no notification announced: it takes the next free slot for one poll).
                    var now = clock.GetUtcNow();
                    if ((workNoticed.TryGetValue(lane, out var noticed) && now - noticed <= WorkNoticeTtl)
                        || now - lastQuiet.GetValueOrDefault(lane, createdAt) >= IdleProbeAfter)
                        wanting.Add(lane);
                    return false;
                }
                wanting.Remove(lane);
                own[lane] = own.GetValueOrDefault(lane) + 1;
                running++;
                return true;
            }
            // Borrow only when the lane's own slots are all busy, within the lane's borrow limit, and never into a slot another
            // lane may need now: the idle own slots of protected lanes and of lanes that want theirs stay free.
            // Never while the lane's last poll found nothing (23 Nov, after the release): an own slot that is only polling
            // counts as busy, so every freed slot woke the lane's borrowing slots to poll the same empty queue, and their
            // releases woke the next ones; the storm filled the unprotected part, and an unprotected lane's own slot found no
            // room for minutes (a Trendyol return read waited 113 s after the restart on an idle worker).
            // Quiet for QuietFor at most, and not after a job's end or a hold's end (24 Nov review: a lane quiet until a
            // notification named its job kept a webhook waiting whose lock group another lane's job had held).
            if (IsQuietNow(lane)) return false;
            if (own.GetValueOrDefault(lane) < floor) return false;
            if (borrowLimits.TryGetValue(lane, out var limit) && borrowed.GetValueOrDefault(lane) >= limit) return false;
            if (running + Kept(lane, includeWanting: true) >= Total)
            {
                // No room for a lane with known work: the next release wakes it, an empty poll's included.
                if (workNoticed.TryGetValue(lane, out var noticed) && clock.GetUtcNow() - noticed <= WorkNoticeTtl) starved.Add(lane);
                return false;
            }
            starved.Remove(lane);
            borrowed[lane] = borrowed.GetValueOrDefault(lane) + 1;
            running++;
            return true;
        }
    }

    // The idle own slots a claim of the lane must leave free: the other protected lanes', and with includeWanting those of the
    // lanes that want theirs back. Called under the lock.
    private int Kept(string lane, bool includeWanting) =>
        reserved.Where(item => item.Key != lane && (protectedLanes.Contains(item.Key) || (includeWanting && wanting.Contains(item.Key))))
            .Sum(item => Math.Max(0, item.Value - own.GetValueOrDefault(item.Key)));

    // Jobs running in unprotected lanes (own and borrowed) and the most they may ever be: the pool less the protected slots.
    internal int UnprotectedRunning
    {
        get
        {
            lock (gate)
                return own.Where(item => !protectedLanes.Contains(item.Key)).Sum(item => item.Value)
                    + borrowed.Where(item => !protectedLanes.Contains(item.Key)).Sum(item => item.Value);
        }
    }

    internal int UnprotectedCapacity => Total - reserved.Where(item => protectedLanes.Contains(item.Key)).Sum(item => item.Value);

    // ranJob: the slot ran a job (false: its poll found nothing or failed). Only a slot that ran a job wakes the lanes whose own
    // slots are all busy: an empty poll's slot was busy for one query only, and waking every such lane for it (their own slots
    // are mostly busy because they are polling too) made each empty poll wake the next ones (23 Nov: a storm of polls, every
    // idle wait cut back to one second). An empty poll wakes only the lanes known to have work that found no room: those
    // that want their slot back, and those whose borrowing slot was refused for room (each until it is woken once). A job that
    // ran also ends every lane's quiet (Unquiet): its end may have opened a lock group or a bulkhead for any lane.
    public void Release(string lane, bool ownSlot, bool ranJob = true)
    {
        List<string> lanes;
        lock (gate)
        {
            var counts = ownSlot ? own : borrowed;
            if (counts.GetValueOrDefault(lane) <= 0) return;
            counts[lane]--;
            running--;
            if (!ranJob)
                lanes = [.. wanting.Union(starved, StringComparer.Ordinal)];
            else
            {
                UnquietLocked();
                lanes = wanting.Count > 0
                    ? [.. wanting]
                    : [.. reserved.Where(item => item.Value > 0 && own.GetValueOrDefault(item.Key) >= item.Value).Select(item => item.Key)];
            }
            // Only the lanes woken now leave the starved set.
            starved.ExceptWith(lanes);
        }
        if (lanes.Count > 0) SlotFreed?.Invoke(lanes);
    }

    // The pool's invariants, checked under the lock (tests): null when they hold, else what broke.
    internal string? Violation()
    {
        lock (gate)
        {
            if (running < 0 || running > Total) return $"running {running} of {Total}";
            if (running != own.Values.Sum() + borrowed.Values.Sum()) return $"running {running} differs from the slots counted";
            var unprotected = own.Where(item => !protectedLanes.Contains(item.Key)).Sum(item => item.Value)
                + borrowed.Where(item => !protectedLanes.Contains(item.Key)).Sum(item => item.Value);
            if (unprotected > UnprotectedCapacity) return $"unprotected lanes run {unprotected} of {UnprotectedCapacity}";
            // Every protected lane can take each idle own slot at once: the free slots cover the protected lanes' idle own slots.
            var protectedIdle = reserved.Where(item => protectedLanes.Contains(item.Key)).Sum(item => Math.Max(0, item.Value - own.GetValueOrDefault(item.Key)));
            if (Total - running < protectedIdle) return $"{Total - running} free slot(s) for {protectedIdle} idle protected slot(s)";
            return null;
        }
    }

    // Running jobs of a lane beyond its own slots (the slots it borrowed), and in all (tests, logs).
    public int Borrowed(string lane)
    {
        lock (gate) return borrowed.GetValueOrDefault(lane);
    }

    public int Running
    {
        get { lock (gate) return running; }
    }

    internal bool Wants(string lane)
    {
        lock (gate) return wanting.Contains(lane);
    }

    internal bool IsQuiet(string lane)
    {
        lock (gate) return IsQuietNow(lane);
    }
}
