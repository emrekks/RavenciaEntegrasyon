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
    private int running;

    public LaneSlotPool(IReadOnlyDictionary<string, int> slots, IEnumerable<string> protectedLanes, IReadOnlyDictionary<string, int>? borrowLimits = null, TimeProvider? clock = null)
    {
        reserved = slots.ToDictionary(item => item.Key, item => Math.Max(0, item.Value), StringComparer.Ordinal);
        this.protectedLanes = new HashSet<string>(protectedLanes.Where(reserved.ContainsKey), StringComparer.Ordinal);
        this.borrowLimits = (borrowLimits ?? new Dictionary<string, int>()).Where(item => reserved.ContainsKey(item.Key))
            .ToDictionary(item => item.Key, item => Math.Max(0, item.Value), StringComparer.Ordinal);
        this.clock = clock ?? TimeProvider.System;
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
            if (reserved.ContainsKey(lane)) workNoticed[lane] = clock.GetUtcNow();
    }

    // One of the lane's slots polled and found nothing: it has no work it could take now.
    public void NoteNoWork(string lane)
    {
        lock (gate)
        {
            workNoticed.Remove(lane);
            wanting.Remove(lane);
        }
    }

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
                    // polls idly does not hold the next free slot up.
                    if (workNoticed.TryGetValue(lane, out var noticed) && clock.GetUtcNow() - noticed <= WorkNoticeTtl) wanting.Add(lane);
                    return false;
                }
                wanting.Remove(lane);
                own[lane] = own.GetValueOrDefault(lane) + 1;
                running++;
                return true;
            }
            // Borrow only when the lane's own slots are all busy, within the lane's borrow limit, and never into a slot another
            // lane may need now: the idle own slots of protected lanes and of lanes that want theirs stay free.
            if (own.GetValueOrDefault(lane) < floor) return false;
            if (borrowLimits.TryGetValue(lane, out var limit) && borrowed.GetValueOrDefault(lane) >= limit) return false;
            if (running + Kept(lane, includeWanting: true) >= Total) return false;
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

    public void Release(string lane, bool ownSlot)
    {
        List<string> lanes;
        lock (gate)
        {
            var counts = ownSlot ? own : borrowed;
            if (counts.GetValueOrDefault(lane) <= 0) return;
            counts[lane]--;
            running--;
            lanes = wanting.Count > 0
                ? [.. wanting]
                : [.. reserved.Where(item => item.Value > 0 && own.GetValueOrDefault(item.Key) >= item.Value).Select(item => item.Key)];
        }
        if (lanes.Count > 0) SlotFreed?.Invoke(lanes);
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
}
