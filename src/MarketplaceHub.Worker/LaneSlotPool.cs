namespace MarketplaceHub.Worker;

// Worker lanes step 3 (19 Nov 2026), flexible lanes (Worker:LaneModel=flexible): the lanes stay as they are, and their slots
// become one pool of the same size. Each lane keeps its own slots (its guaranteed floor); a lane whose own slots are all busy
// may borrow an idle slot of another lane, so a backlog in one lane (five tenants' catalog reads, six stores' first imports)
// uses capacity the quiet lanes do not need.
//   - A borrowed slot is never taken back from a running job; when the lending lane gets work, its own slot waits for the
//     next job anywhere to end and borrowers take nothing new meanwhile (the lane "wants" its slot).
//   - Protected lanes (Worker:FlexibleLanes:ProtectedLanes, default user and hot) never lend: their idle slots stay free for
//     a user action or a new order at once. They may borrow.
//   - The pool never runs more jobs than the lanes' slots add up to, so no marketplace sees more requests than before; the
//     quota limiter, lock groups, holds, fairness and bulkheads apply to every lease as before.
// Replay of the live week (sim4): five tenants user p95 35 -> 3.9 s, first imports 549 -> 2 s, catalog 431 -> 16 s; six
// stores activated together: first imports p95 6574 -> 1184 s; one tenant unchanged; no lane over its target that was
// within it before. Capping a lane's borrowing at its own slot count lost most of that (five tenants catalog p95 170 s), so a
// lane may borrow up to the whole pool.
public sealed class LaneSlotPool
{
    private readonly object gate = new();
    private readonly Dictionary<string, int> reserved;
    private readonly HashSet<string> protectedLanes;
    private readonly Dictionary<string, int> own = [];
    private readonly Dictionary<string, int> borrowed = [];
    private readonly HashSet<string> wanting = [];
    private int running;

    public LaneSlotPool(IReadOnlyDictionary<string, int> slots, IEnumerable<string> protectedLanes)
    {
        reserved = slots.ToDictionary(item => item.Key, item => Math.Max(0, item.Value), StringComparer.Ordinal);
        this.protectedLanes = new HashSet<string>(protectedLanes.Where(reserved.ContainsKey), StringComparer.Ordinal);
        Total = reserved.Values.Sum();
    }

    public int Total { get; }

    public IReadOnlyCollection<string> ProtectedLanes => protectedLanes;

    // Raised (outside the lock) after a slot was freed, with the lanes that may lease now: the lanes waiting for one of their
    // own slots first, else the lanes whose own slots are all busy (they may borrow).
    public event Action<IReadOnlyList<string>>? SlotFreed;

    // A slot of the lane asks for room before it polls the queue. own: one of the lane's own slots; otherwise a borrowing slot.
    public bool TryClaim(string lane, bool ownSlot)
    {
        lock (gate)
        {
            if (!reserved.TryGetValue(lane, out var floor)) return false;
            if (ownSlot)
            {
                if (running >= Total)
                {
                    // Every slot is busy (some lent out): the lane wants its slot back, so borrowers take nothing new.
                    wanting.Add(lane);
                    return false;
                }
                wanting.Remove(lane);
                own[lane] = own.GetValueOrDefault(lane) + 1;
                running++;
                return true;
            }
            // Borrow only when the lane's own slots are all busy, and never into a slot another lane may need now: the idle
            // own slots of protected lanes and of lanes that want theirs stay free.
            if (own.GetValueOrDefault(lane) < floor) return false;
            var keep = reserved.Where(item => item.Key != lane && (protectedLanes.Contains(item.Key) || wanting.Contains(item.Key)))
                .Sum(item => Math.Max(0, item.Value - own.GetValueOrDefault(item.Key)));
            if (running + keep >= Total) return false;
            borrowed[lane] = borrowed.GetValueOrDefault(lane) + 1;
            running++;
            return true;
        }
    }

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
