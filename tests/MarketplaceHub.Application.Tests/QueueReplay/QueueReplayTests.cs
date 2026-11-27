using System.Globalization;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace MarketplaceHub.Application.Tests.QueueReplay;

// Queue phase 2 (27 Nov 2026), the acceptance gate of the dispatcher: every stream replayed on today's flexible lanes and on
// the dispatcher (ReplayHarness), compared per work type. Each model runs the stream three times and each figure is the
// median of the three: today's lane loops decide by thread timing within a moment (as on live), so one run alone varies.
// The live windows run only with MARKETPLACEHUB_QUEUE_REPLAY_FILE set (an anonymised export kept outside git); the synthetic
// streams always run.
public sealed class QueueReplayTests(ITestOutputHelper output)
{
    private const int Runs = 3;

    // A work type may start later under the dispatcher by at most this much at p50 and p95 (1 s, or 10 % of today's figure).
    private static TimeSpan Epsilon(TimeSpan current) => TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, current.Ticks / 10));

    internal sealed record Stats(int Count, TimeSpan P50, TimeSpan P95, TimeSpan Max);

    // Per work type over the runs (medians), and the worst run's counters.
    internal sealed record Replayed(string Stream, IReadOnlyList<ReplayResult> Results, Func<int, bool>? Compared = null)
    {
        public int NotStarted => Results.Max(result => result.NotStarted);
        public int MaxRunning => Results.Max(result => result.MaxRunning);
        public int MaxStoreCounted => Results.Max(result => result.MaxStoreCounted);
        public int OrderMinimumViolations => Results.Max(result => result.OrderMinimumViolations);
        public long Queries => (long)Results.Average(result => result.Leases + result.Censuses);

        public Dictionary<string, Stats> ByWorkType(bool slot, Func<int, bool>? include = null)
        {
            var perRun = Results.Select(result => QueueReplayTests.ByWorkType(result, include, slot)).ToList();
            return perRun.SelectMany(run => run.Keys).Distinct(StringComparer.Ordinal).ToDictionary(type => type, type =>
            {
                var runs = perRun.Where(run => run.ContainsKey(type)).Select(run => run[type]).ToList();
                return new Stats(runs.Max(stats => stats.Count), Median(runs.Select(stats => stats.P50)), Median(runs.Select(stats => stats.P95)), Median(runs.Select(stats => stats.Max)));
            }, StringComparer.Ordinal);
        }

        private static TimeSpan Median(IEnumerable<TimeSpan> values)
        {
            var sorted = values.Order().ToList();
            return sorted[sorted.Count / 2];
        }
    }

    internal static Stats StatsOf(IEnumerable<TimeSpan> delays)
    {
        var sorted = delays.OrderBy(delay => delay).ToList();
        if (sorted.Count == 0) return new Stats(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        TimeSpan At(double quantile) => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(quantile * sorted.Count) - 1)];
        return new Stats(sorted.Count, At(0.5), At(0.95), sorted[^1]);
    }

    // Start delay (arrival to start) or slot wait (from the later of arrival, the last opening of the job's lock group and
    // the end of its quota hold, to start: the part of the wait a lane model decides; a lock wait depends on when the store's
    // other jobs happen to run, under the same rules in every model).
    internal static Dictionary<string, Stats> ByWorkType(ReplayResult result, Func<int, bool>? include = null, bool slot = false) =>
        (slot ? result.SlotWaits : result.StartDelays).Where(item => include?.Invoke(item.Key) ?? true)
            .GroupBy(item => result.WorkTypes[item.Key], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => StatsOf(group.Select(item => item.Value)), StringComparer.Ordinal);

    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString(value.TotalSeconds < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture);

    private static string Triple(Stats stats) => $"{Seconds(stats.P50)} / {Seconds(stats.P95)} / {Seconds(stats.Max)}";

    internal static string Table(Replayed current, Replayed dispatcher)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"### {current.Stream}");
        builder.AppendLine("| İş türü | n | Başlama: bugün p50/p95/maks | Başlama: dağıtıcı | Slot beklemesi: bugün | Slot beklemesi: dağıtıcı |");
        builder.AppendLine("|---|---|---|---|---|---|");
        var today = current.ByWorkType(slot: false);
        var next = dispatcher.ByWorkType(slot: false);
        var todaySlot = current.ByWorkType(slot: true);
        var nextSlot = dispatcher.ByWorkType(slot: true);
        var empty = new Stats(0, default, default, default);
        foreach (var type in today.Keys.Union(next.Keys).Order(StringComparer.Ordinal))
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {type} | {Math.Max(today.GetValueOrDefault(type)?.Count ?? 0, next.GetValueOrDefault(type)?.Count ?? 0)} | {Triple(today.GetValueOrDefault(type) ?? empty)} | {Triple(next.GetValueOrDefault(type) ?? empty)} | {Triple(todaySlot.GetValueOrDefault(type) ?? empty)} | {Triple(nextSlot.GetValueOrDefault(type) ?? empty)} |");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Sorgu (kira+sayım, ort.): bugün {current.Queries}, dağıtıcı {dispatcher.Queries}; en çok eşzamanlı {current.MaxRunning} / {dispatcher.MaxRunning}; bir mağazanın sınırlı şeritlerdeki eşzamanlı işi en çok {current.MaxStoreCounted} / {dispatcher.MaxStoreCounted}; sipariş asgarisi ihlali (dağıtıcı) {dispatcher.OrderMinimumViolations}; başlamayan {current.NotStarted} / {dispatcher.NotStarted}");
        return builder.ToString();
    }

    private async Task<(Replayed Current, Replayed Dispatcher)> CompareAsync(ReplayStream stream, ReplayOptions? options = null, int runs = Runs)
    {
        var started = global::System.Diagnostics.Stopwatch.StartNew();
        var current = new List<ReplayResult>();
        var dispatcher = new List<ReplayResult>();
        for (var run = 0; run < runs; run++)
        {
            current.Add(await ReplayHarness.RunAsync(ReplayModel.Flexible, stream, options));
            dispatcher.Add(await ReplayHarness.RunAsync(ReplayModel.Dispatcher, stream, options));
        }
        var result = (new Replayed(stream.Name, current), new Replayed(stream.Name, dispatcher));
        output.WriteLine(Table(result.Item1, result.Item2));
        output.WriteLine($"({stream.Jobs.Count} jobs x {runs} runs per model, real {started.Elapsed})");
        foreach (var note in dispatcher.SelectMany(item => item.Notes ?? []).Take(20)) output.WriteLine($"order minimum: {note}");
        return result;
    }

    // The acceptance rules of the plan for one stream (compared: the jobs whose waits are compared; every job counts for the rest).
    // A work type is worse only when its start delay and its slot wait are both worse beyond epsilon: a lock wait shifts with
    // the store's other jobs (the dispatcher starts order reads sooner, so a webhook meets them more often), and the per-store
    // cap's wait replaces part of a lock wait.
    internal static void Accept(Replayed current, Replayed dispatcher, Func<int, bool>? compared = null)
    {
        Assert.Equal(0, current.NotStarted);
        Assert.Equal(0, dispatcher.NotStarted);
        Assert.True(dispatcher.MaxRunning <= 20, $"{dispatcher.MaxRunning} jobs ran at once");
        Assert.True(dispatcher.MaxStoreCounted <= Infrastructure.Jobs.DispatcherSettings.DefaultConnectionCap, $"a store ran {dispatcher.MaxStoreCounted} capped jobs at once");
        Assert.Equal(0, dispatcher.OrderMinimumViolations);
        var todayStart = current.ByWorkType(false, compared);
        var nextStart = dispatcher.ByWorkType(false, compared);
        var todaySlot = current.ByWorkType(true, compared);
        var nextSlot = dispatcher.ByWorkType(true, compared);
        foreach (var (type, now) in todaySlot)
        {
            if (!nextSlot.TryGetValue(type, out var then) || now.Count < 3) continue;
            var start = todayStart[type];
            var nextStartOfType = nextStart[type];
            bool Worse(Func<Stats, TimeSpan> figure) =>
                figure(then) > figure(now) + Epsilon(figure(now)) && figure(nextStartOfType) > figure(start) + Epsilon(figure(start));
            Assert.False(Worse(stats => stats.P50), $"{current.Stream} {type}: p50 slot wait {Seconds(then.P50)} s against {Seconds(now.P50)} s, start {Seconds(nextStartOfType.P50)} s against {Seconds(start.P50)} s today");
            Assert.False(Worse(stats => stats.P95), $"{current.Stream} {type}: p95 slot wait {Seconds(then.P95)} s against {Seconds(now.P95)} s, start {Seconds(nextStartOfType.P95)} s against {Seconds(start.P95)} s today");
        }
        var all = dispatcher.ByWorkType(false);
        if (all.TryGetValue(Infrastructure.Jobs.DispatcherLanes.User, out var user)) Assert.True(user.P95 <= TimeSpan.FromSeconds(5), $"kullanici p95 {Seconds(user.P95)} s");
        // A webhook waits for its store's order lock (an order read or status scan of the store running) in every model alike:
        // its wait for a slot, from the moment its lock group last opened, is what the dispatcher must keep within 1 s.
        if (dispatcher.ByWorkType(true).TryGetValue("webhook", out var webhook)) Assert.True(webhook.P95 <= TimeSpan.FromSeconds(1), $"webhook slot wait p95 {Seconds(webhook.P95)} s");
    }

    [Fact]
    public async Task AllProductsAtOnce()
    {
        var (current, dispatcher) = await CompareAsync(ReplayStreams.AllProducts());
        Accept(current, dispatcher);
    }

    [Fact]
    public async Task SixtyStores()
    {
        var (current, dispatcher) = await CompareAsync(ReplayStreams.SixtyStores());
        Accept(current, dispatcher);
    }

    [Fact]
    public async Task OneStoreFloodsFiftyJobs()
    {
        var stream = ReplayStreams.StoreFlood();
        var (current, dispatcher) = await CompareAsync(stream);
        // The flooding store's own capped work (its fifty jobs and its other background reads) waits for the per-store cap by
        // design (at most two at once); every other job is compared.
        var settings = Infrastructure.Jobs.DispatcherSettings.Default;
        var flooder = stream.Jobs.Where(job => job.Connection == "c0" && settings.IsCapped(Infrastructure.Jobs.DispatcherLanes.LaneFor(job.JobType, job.Priority))).Select(job => job.Id).ToHashSet();
        Accept(current, dispatcher, id => !flooder.Contains(id));
    }

    [Fact]
    public async Task QuotaHeldTrendyolReads()
    {
        var (current, dispatcher) = await CompareAsync(ReplayStreams.QuotaHeld());
        Accept(current, dispatcher);
    }

    [Fact]
    public async Task WorkerStartsWithThreeHundredDueJobs()
    {
        var (current, dispatcher) = await CompareAsync(ReplayStreams.StartBacklog());
        Accept(current, dispatcher);
    }

    [Fact]
    public async Task AnIdleWorkerReadsTheQueueLessOften()
    {
        var stream = ReplayStreams.Idle();
        var current = await ReplayHarness.RunAsync(ReplayModel.Flexible, stream);
        var dispatcher = await ReplayHarness.RunAsync(ReplayModel.Dispatcher, stream);
        output.WriteLine($"idle queries per second: flexible {current.IdleQueriesPerSecond:0.000}, dispatcher {dispatcher.IdleQueriesPerSecond:0.000}");
        Assert.True(dispatcher.IdleQueriesPerSecond <= current.IdleQueriesPerSecond, $"dispatcher {dispatcher.IdleQueriesPerSecond} > {current.IdleQueriesPerSecond}");
        Assert.True(dispatcher.IdleQueriesPerSecond <= 0.1, $"dispatcher reads {dispatcher.IdleQueriesPerSecond} times a second while idle");
    }

    // Every notification, "freed" and due wake dropped: each job still starts within the safety tick (15 s) plus a lease of the
    // moment it could run (its store's lock group free: a lock wait is not a lost wake-up).
    [Fact]
    public async Task WithEverySignalLostEachJobStartsWithinTheSafetyTick()
    {
        var (current, dispatcher) = await CompareAsync(ReplayStreams.Light(), new ReplayOptions(Signals: false));
        Assert.Equal(0, dispatcher.NotStarted);
        Assert.Equal(0, current.NotStarted);
        var worst = dispatcher.Results.Max(result => result.SlotWaits.Values.Max());
        Assert.True(worst <= Infrastructure.Jobs.DispatcherSettings.DefaultSafetyTick + TimeSpan.FromSeconds(1), $"a job waited {worst} with no signal");
    }

    // Live windows (opt-in): MARKETPLACEHUB_QUEUE_REPLAY_FILE names the anonymised export.
    [Fact]
    public async Task LiveWindows()
    {
        var path = Environment.GetEnvironmentVariable("MARKETPLACEHUB_QUEUE_REPLAY_FILE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            output.WriteLine("MARKETPLACEHUB_QUEUE_REPLAY_FILE is not set: the live windows are not replayed.");
            return;
        }
        (string Name, DateTimeOffset From, TimeSpan Length)[] windows =
        [
            ("26 Kas 08:25-08:50 tepe (toplu fatura)", new DateTimeOffset(2026, 11, 26, 8, 25, 0, TimeSpan.Zero), TimeSpan.FromMinutes(25)),
            ("25 Kas 10:55-11:40 referans dalgası", new DateTimeOffset(2026, 11, 25, 10, 55, 0, TimeSpan.Zero), TimeSpan.FromMinutes(45)),
            ("27 Kas 08:15-09:20 PttAVM kesintisi", new DateTimeOffset(2026, 11, 27, 8, 15, 0, TimeSpan.Zero), TimeSpan.FromMinutes(65)),
            ("26 Kas 00:00-06:00 sıradan gece", new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(6)),
            ("24 Kas 06:00-12:00 sıradan sabah", new DateTimeOffset(2026, 11, 24, 6, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(6))
        ];
        var failures = new List<string>();
        foreach (var (name, from, length) in windows)
        {
            var jobs = ReplayStreams.ReadLive(path, from, from + length);
            if (jobs.Count == 0) continue;
            var (current, dispatcher) = await CompareAsync(new ReplayStream(name, jobs));
            try { Accept(current, dispatcher); }
            catch (Exception exception) { failures.Add($"{name}: {exception.Message}"); }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
