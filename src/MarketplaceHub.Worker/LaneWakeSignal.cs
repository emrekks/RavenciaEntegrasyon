using MarketplaceHub.Infrastructure.Jobs;

namespace MarketplaceHub.Worker;

// KUYRUK-6 (16 Nov 2026): the idle slots of one lane. A slot registers before it polls the queue (so a wake that comes
// between its poll and its wait is not lost) and leaves after. WakeOne wakes the longest-registered slot only; a slot that
// was woken but found a job (or failed) passes the wake on to the next one, so one notification ends one wait. WakeAll
// wakes every registered slot. A slot nobody wakes still polls after its idle delay: a missed wake only delays a job.
public sealed class LaneWakeSignal
{
    private readonly object gate = new();
    private readonly LinkedList<TaskCompletionSource> waiters = new();

    public Ticket Register()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) return new Ticket(source, waiters.AddLast(source));
    }

    // forward: the slot did not use a wake it may have got (it found a job or failed), so the next slot gets it.
    public void Leave(Ticket ticket, bool forward)
    {
        lock (gate)
        {
            if (ticket.Node.List is not null) waiters.Remove(ticket.Node);
        }
        if (forward && ticket.Source.Task.IsCompleted) WakeOne();
    }

    public void WakeOne()
    {
        lock (gate)
        {
            while (waiters.First is { } first)
            {
                waiters.RemoveFirst();
                if (first.Value.TrySetResult()) return;
            }
        }
    }

    public void WakeAll()
    {
        lock (gate)
        {
            foreach (var waiter in waiters) waiter.TrySetResult();
            waiters.Clear();
        }
    }

    // How many slots wait now (tests).
    internal int Waiting
    {
        get { lock (gate) return waiters.Count; }
    }

    public sealed class Ticket
    {
        internal Ticket(TaskCompletionSource source, LinkedListNode<TaskCompletionSource> node)
        {
            Source = source;
            Node = node;
        }

        internal TaskCompletionSource Source { get; }
        internal LinkedListNode<TaskCompletionSource> Node { get; }
        public Task Woken => Source.Task;
    }
}

// Routes a job notification to the lanes that can lease the job. The trigger names the job ("job:<type>:<priority>") when
// a job becomes runnable, and sends "freed" when a leased job ends (a lock group may have opened for a job of any lane).
// Any other payload (the trigger before migration 20261116000405 sent an empty one) wakes every slot, as before.
public sealed class JobWakeRouter
{
    public const string JobPrefix = "job:";
    public const string Freed = "freed";

    private readonly IReadOnlyList<(WorkerLane Lane, LaneWakeSignal Signal, LaneWakeSignal Borrow)> lanes;

    public JobWakeRouter(IEnumerable<WorkerLane> lanes) =>
        this.lanes = [.. lanes.Select(lane => (lane, new LaneWakeSignal(), new LaneWakeSignal()))];

    public LaneWakeSignal SignalFor(string laneName) => lanes.First(entry => entry.Lane.Name == laneName).Signal;

    // The lane's borrowing slots (flexible lane model) wait on a signal of their own, so a borrowing slot that may not borrow
    // never uses up the wake its lane's own slots need. A notification wakes one of them too: with the lane's own slots all
    // busy (hot 3/3), a new webhook is leased on a free pool slot at once instead of after the borrowing slot's idle wait.
    public LaneWakeSignal BorrowSignalFor(string laneName) => lanes.First(entry => entry.Lane.Name == laneName).Borrow;

    // Raised for each lane a job notification names (not for "freed" or a wake of every slot).
    public event Action<string>? LaneNotified;

    // Raised for a "freed" notification (a leased job ended), before the lanes are woken.
    public event Action? JobEnded;

    public void Route(string? payload)
    {
        if (string.Equals(payload, Freed, StringComparison.Ordinal))
        {
            JobEnded?.Invoke();
            foreach (var (_, signal, _) in lanes) signal.WakeOne();
            return;
        }
        if (Parse(payload) is not { } job)
        {
            WakeAll();
            return;
        }
        // Every lane that could lease it (a job may fit more than one): one slot each. A job no lane takes wakes no one.
        // LaneNotified tells the flexible pool which lanes now have work (LaneSlotPool.NoteWork).
        foreach (var (lane, signal, borrow) in lanes)
            if (lane.Takes(job.JobType, job.Priority))
            {
                LaneNotified?.Invoke(lane.Name);
                signal.WakeOne();
                borrow.WakeOne();
            }
    }

    // Jobs that became leasable only because time passed (their AvailableAt, the end of a quota backoff) raise no
    // notification: the lanes that can lease one of them are told they have work as a notification would (LaneNotified),
    // so a lane whose slot was lent out wants it back for them too. Each lane once; the wake itself is WakeAll.
    public void NoteDue(IReadOnlyCollection<(string JobType, int Priority)> jobs)
    {
        foreach (var (lane, _, _) in lanes)
            if (jobs.Any(job => lane.Takes(job.JobType, job.Priority)))
                LaneNotified?.Invoke(lane.Name);
    }

    public void WakeAll()
    {
        foreach (var (_, signal, borrow) in lanes)
        {
            signal.WakeAll();
            borrow.WakeOne();
        }
    }

    public static string Payload(string jobType, int priority) => $"{JobPrefix}{jobType}:{priority}";

    internal static (string JobType, int Priority)? Parse(string? payload)
    {
        if (payload is null || !payload.StartsWith(JobPrefix, StringComparison.Ordinal)) return null;
        var separator = payload.LastIndexOf(':');
        if (separator <= JobPrefix.Length) return null;
        var jobType = payload[JobPrefix.Length..separator];
        return int.TryParse(payload.AsSpan(separator + 1), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var priority) && jobType.Length > 0
            ? (jobType, priority)
            : null;
    }
}
