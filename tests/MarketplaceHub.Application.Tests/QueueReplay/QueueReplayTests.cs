using System.Globalization;
using System.Text;
using MarketplaceHub.Infrastructure.Jobs;
using Xunit;
using Xunit.Abstractions;

namespace MarketplaceHub.Application.Tests.QueueReplay;

// Queue phase 2 (27 Nov 2026, redone after review), the acceptance gate of the dispatcher: every synthetic stream replayed with
// five seeds on today's flexible lanes and on the dispatcher (ReplayHarness, deterministic: one seed gives one result), compared
// per work type on the start delay (eligible to started, every lock wait counted except waiting for a job of the same type on the
// same store's lock group). A work type with 30 or more attempts is compared at p50 and p95, a smaller one at its maximum; each
// figure is the median over the seeds, and the band (min-max over the seeds) is reported beside it. The live windows run only
// with the exports set (anonymised, outside git).
public sealed class QueueReplayTests(ITestOutputHelper output)
{
    // Today's figure plus this much at most (1 s, or 10 % of today's figure).
    internal static TimeSpan Epsilon(TimeSpan current) => TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, current.Ticks / 10));

    internal sealed record Stats(int Count, TimeSpan P50, TimeSpan P95, TimeSpan Max);

    internal static Stats StatsOf(IEnumerable<TimeSpan> delays)
    {
        var sorted = delays.OrderBy(delay => delay).ToList();
        if (sorted.Count == 0) return new Stats(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        TimeSpan At(double quantile) => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(quantile * sorted.Count) - 1)];
        return new Stats(sorted.Count, At(0.5), At(0.95), sorted[^1]);
    }

    internal static Dictionary<string, Stats> ByWorkType(ReplayResult result, Func<ReplayAttempt, bool>? include = null, bool lockFree = false) =>
        result.Attempts.Where(attempt => include?.Invoke(attempt) ?? true)
            .GroupBy(attempt => attempt.WorkType, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => StatsOf(group.Select(attempt => lockFree ? attempt.LockFreeWait : attempt.StartDelay)), StringComparer.Ordinal);

    // One stream, every seed, both models.
    internal sealed record Comparison(string Stream, IReadOnlyList<ReplayResult> Current, IReadOnlyList<ReplayResult> Dispatcher);

    // A figure over the seeds: the median and the band.
    internal sealed record Band(TimeSpan Median, TimeSpan Min, TimeSpan Max);

    internal static Band BandOf(IEnumerable<TimeSpan> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? new Band(default, default, default) : new Band(sorted[sorted.Count / 2], sorted[0], sorted[^1]);
    }

    internal static Dictionary<string, (int Count, Band P50, Band P95, Band Max)> Bands(IReadOnlyList<ReplayResult> results, Func<ReplayAttempt, bool>? include = null, bool lockFree = false)
    {
        var perSeed = results.Select(result => ByWorkType(result, include, lockFree)).ToList();
        return perSeed.SelectMany(run => run.Keys).Distinct(StringComparer.Ordinal).ToDictionary(type => type, type =>
        {
            var runs = perSeed.Where(run => run.ContainsKey(type)).Select(run => run[type]).ToList();
            return (runs.Min(stats => stats.Count), BandOf(runs.Select(stats => stats.P50)), BandOf(runs.Select(stats => stats.P95)), BandOf(runs.Select(stats => stats.Max)));
        }, StringComparer.Ordinal);
    }

    internal static string Seconds(TimeSpan value) => value.TotalSeconds.ToString(value.TotalSeconds < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture);
    private static string Show(Band band) => band.Min == band.Max ? Seconds(band.Median) : $"{Seconds(band.Median)} [{Seconds(band.Min)}–{Seconds(band.Max)}]";

    internal static string Table(Comparison comparison, Func<ReplayAttempt, bool>? include = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"### {comparison.Stream} ({comparison.Current.Count} tohum)");
        builder.AppendLine("| İş türü | n | Bugün p50 | Bugün p95 / maks | Dağıtıcı p50 | Dağıtıcı p95 / maks |");
        builder.AppendLine("|---|---|---|---|---|---|");
        var today = Bands(comparison.Current, include);
        var next = Bands(comparison.Dispatcher, include);
        foreach (var type in today.Keys.Union(next.Keys).Order(StringComparer.Ordinal))
        {
            var a = today.GetValueOrDefault(type);
            var b = next.GetValueOrDefault(type);
            var count = Math.Max(a.Count, b.Count);
            string Tail((int Count, Band P50, Band P95, Band Max) cell) => count >= 30 ? $"{Show(cell.P95)} / {Seconds(cell.Max.Median)}" : $"maks {Show(cell.Max)}";
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {type} | {count} | {Show(a.P50)} | {Tail(a)} | {Show(b.P50)} | {Tail(b)} |");
        }
        static string Worst(IEnumerable<ReplayResult> results, Func<ReplayResult, double> pick) => results.Max(pick).ToString("0.###", CultureInfo.InvariantCulture);
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Sorgu (kira+sayım, ort.): bugün {comparison.Current.Average(result => result.Leases + result.Censuses):0}, dağıtıcı {comparison.Dispatcher.Average(result => result.Leases + result.Censuses):0}; en çok eşzamanlı {Worst(comparison.Current, r => r.MaxRunning)} / {Worst(comparison.Dispatcher, r => r.MaxRunning)}; bir mağazanın sınırlı şeritlerdeki işi en çok {Worst(comparison.Current, r => r.MaxStoreCounted)} / {Worst(comparison.Dispatcher, r => r.MaxStoreCounted)}; sipariş asgarisi ihlali {Worst(comparison.Dispatcher, r => r.OrderMinimumViolations)}; bitmeyen {Worst(comparison.Current, r => r.NotFinished)} / {Worst(comparison.Dispatcher, r => r.NotFinished)}");
        return builder.ToString();
    }

    private void Report(string text)
    {
        output.WriteLine(text);
        if (Environment.GetEnvironmentVariable("MARKETPLACEHUB_QUEUE_REPLAY_REPORT") is { Length: > 0 } path)
            lock (ReportGate) File.AppendAllText(path, text + Environment.NewLine);
    }

    private static readonly object ReportGate = new();

    internal static async Task<Comparison> CompareAsync(Func<int, ReplayStream> stream, IReadOnlyList<int> seeds, ReplayOptions? options = null)
    {
        var current = new List<ReplayResult>();
        var dispatcher = new List<ReplayResult>();
        foreach (var seed in seeds)
        {
            var built = stream(seed);
            current.Add(await ReplayHarness.RunAsync(ReplayModel.Flexible, built, options));
            dispatcher.Add(await ReplayHarness.RunAsync(ReplayModel.Dispatcher, built, options));
        }
        return new Comparison(stream(seeds[0]).Name, current, dispatcher);
    }

    // The plan's acceptance rules for one stream (include: the attempts whose waits are compared; every job counts for the rest).
    // A known cell (Known): a work type of a stream that the dispatcher leaves worse than today for a reason stated where it is
    // passed; it may not grow beyond Bound (p95 at n >= 30, else max) and its max may not exceed today's.
    internal sealed record Known(string WorkType, TimeSpan Bound, string Reason, bool CheckMax = true);

    internal static List<string> Violations(Comparison comparison, Func<ReplayAttempt, bool>? include = null, int cap = DispatcherSettings.DefaultConnectionCap, bool signalsLost = false, params Known[] known)
    {
        var problems = new List<string>();
        foreach (var result in comparison.Current.Concat(comparison.Dispatcher))
            if (result.NotFinished > 0) problems.Add($"{result.Model}: {result.NotFinished} job(s) never finished");
        foreach (var result in comparison.Dispatcher)
        {
            if (result.MaxRunning > 20) problems.Add($"{result.MaxRunning} jobs ran at once");
            if (result.MaxStoreCounted > cap) problems.Add($"a store ran {result.MaxStoreCounted} capped jobs at once");
            // With notifications lost, an order read whose notification was lost is found by the next safety census (at most
            // 15 s, 5 s for kullanici, webhook and yazma); the wait comparison below still holds for every work type.
            if (result.OrderMinimumViolations > 0 && !signalsLost) problems.Add($"{result.OrderMinimumViolations} order read(s) waited behind other work with a slot of their minimum free: {string.Join(" | ", result.Notes)}");
        }
        var today = Bands(comparison.Current, include);
        var next = Bands(comparison.Dispatcher, include);
        foreach (var (type, now) in today)
        {
            if (!next.TryGetValue(type, out var then)) continue;
            if (known.FirstOrDefault(cell => cell.WorkType == type) is { } cell)
            {
                var value = Math.Max(now.Count, then.Count) >= 30 ? then.P95.Median : then.Max.Median;
                if (value > cell.Bound) problems.Add($"{type}: {Seconds(value)} s beyond its known bound {Seconds(cell.Bound)} s ({cell.Reason})");
                if (cell.CheckMax && then.Max.Median > now.Max.Median + Epsilon(now.Max.Median)) problems.Add($"{type}: max {Seconds(then.Max.Median)} s against {Seconds(now.Max.Median)} s today ({cell.Reason})");
                continue;
            }
            if (Math.Max(now.Count, then.Count) >= 30)
            {
                if (then.P50.Median > now.P50.Median + Epsilon(now.P50.Median)) problems.Add($"{type}: p50 {Seconds(then.P50.Median)} s against {Seconds(now.P50.Median)} s today");
                if (then.P95.Median > now.P95.Median + Epsilon(now.P95.Median)) problems.Add($"{type}: p95 {Seconds(then.P95.Median)} s against {Seconds(now.P95.Median)} s today");
            }
            else if (then.Max.Median > now.Max.Median + Epsilon(now.Max.Median))
                problems.Add($"{type}: max {Seconds(then.Max.Median)} s against {Seconds(now.Max.Median)} s today");
        }
        var all = Bands(comparison.Dispatcher);
        if (all.TryGetValue(DispatcherLanes.User, out var user) && (user.Count >= 10 ? user.P95.Median : user.Max.Median) > TimeSpan.FromSeconds(5))
            problems.Add($"kullanici p95 {Seconds(user.P95.Median)} s");
        // A webhook's wait for a slot once its store's lock is free must stay within 1 s.
        var lockFree = Bands(comparison.Dispatcher, lockFree: true);
        if (lockFree.TryGetValue(DispatcherLanes.Webhooks, out var webhook) && (webhook.Count >= 30 ? webhook.P95.Median : webhook.Max.Median) > TimeSpan.FromSeconds(1))
            problems.Add($"webhook slot wait {Seconds(webhook.Count >= 30 ? webhook.P95.Median : webhook.Max.Median)} s");
        return problems;
    }

    private async Task<Comparison> AcceptAsync(Func<int, ReplayStream> stream, Func<ReplayAttempt, bool>? include = null, ReplayOptions? options = null, params Known[] known)
    {
        var comparison = await CompareAsync(stream, ReplayStreams.Seeds, options);
        Report(Table(comparison, include));
        var problems = Violations(comparison, include, DispatcherSettings.DefaultConnectionCap, options is { NotifyLoss: > 0 }, known);
        Assert.True(problems.Count == 0, $"{comparison.Stream}: {string.Join("; ", problems)}");
        return comparison;
    }

    // The store's own capped work (katalog, arka-plan, ilk-cekim of that store): it waits for the store's two slots by design.
    private static Func<ReplayAttempt, bool> NotCappedOwnWorkOf(params string[] stores) =>
        attempt => !(attempt.Connection is { } store && stores.Contains(store) && DispatcherSettings.Default.IsCapped(attempt.WorkType));

    // Known cell: an order read that arrives while a reconciliation of its store already runs in the "orders" lock group waits
    // for it (deferral holds only work that would start after the order read is queued). The dispatcher runs background work
    // sooner (arka-plan p50 and p95 far lower than today), so more reconciliations are in flight when order reads arrive: the
    // p95 rises from ~2 s to ~10 s while the max falls. Bound: 15 s.
    private static readonly Known OrderReadsBehindRunningReconciliations =
        new(DispatcherLanes.OrderReads, TimeSpan.FromSeconds(15), "behind a reconciliation of the same store already running in its lock group");

    [Fact]
    public Task AllProductsAtOnce() => AcceptAsync(ReplayStreams.AllProducts, known: OrderReadsBehindRunningReconciliations);
    [Fact] public Task SixtyStores() => AcceptAsync(ReplayStreams.SixtyStores);
    [Fact] public Task QuotaHeldTrendyolReads() => AcceptAsync(ReplayStreams.QuotaHeld);
    [Fact] public Task WorkerStartsWithThreeHundredDueJobs() => AcceptAsync(ReplayStreams.StartBacklog);
    [Fact] public Task RestartsEveryThreeHours() => AcceptAsync(ReplayStreams.Restarts);
    [Fact] public Task MarketplaceOutageWithPilingRetries() => AcceptAsync(ReplayStreams.Outage);
    [Fact] public Task BulkInvoicesWithSingleActions() => AcceptAsync(ReplayStreams.BulkInvoices);
    [Fact] public Task BulkProductUpdate() => AcceptAsync(ReplayStreams.BulkPublish);
    // The store's two 8.5-minute reference reads take both of its capped slots: its reconciliations wait for the first one to
    // end (the cap's cost, ~500 s, reported in the table); no other store's work may be worse.
    [Fact]
    public async Task OneStoresTwoLongReferenceReads()
    {
        var comparison = await AcceptAsync(ReplayStreams.LongReferenceReads, NotCappedOwnWorkOf("c3"));
        var own = Bands(comparison.Dispatcher, attempt => attempt.Connection == "c3" && DispatcherSettings.Default.IsCapped(attempt.WorkType));
        Assert.True(own.Values.All(band => band.Max.Median <= TimeSpan.FromSeconds(513 + 30)), "the store's own work waited longer than its own reference read");
    }

    // Two new stores' first imports (six jobs each, 1-18 minutes): two at a time per store under the cap, so the last one starts
    // after ~21 minutes instead of ~3 (the cap's cost, reported in the table); no other store's work may be worse.
    [Fact]
    public async Task TwoStoresInitialImports()
    {
        var comparison = await AcceptAsync(ReplayStreams.TwoInitialImports, NotCappedOwnWorkOf("n0", "n1"));
        var own = Bands(comparison.Dispatcher, attempt => attempt.Connection is "n0" or "n1" && DispatcherSettings.Default.IsCapped(attempt.WorkType));
        Assert.True(own.Values.All(band => band.Max.Median <= TimeSpan.FromMinutes(18 + 5 + 1)), "a first import waited longer than its store's own longer imports");
    }
    [Fact] public Task MarketplaceWriteWave() => AcceptAsync(ReplayStreams.Writes);

    [Fact]
    public Task OneStoreFloodsFiftyJobs() =>
        // The flooding store's own capped work waits for the per-store cap by design (at most two at once); its cost is reported
        // by CapVariants. Every other attempt is compared.
        AcceptAsync(ReplayStreams.StoreFlood, attempt => !(attempt.Connection == "c0" && DispatcherSettings.Default.IsCapped(attempt.WorkType)));

    // Ten per cent of the notifications (job and "freed") lost, every one delayed 50 ms. A user action whose notification was
    // lost is found by the 5-second short safety tick (today's model polls and does not lose it): bound 5 s.
    [Fact]
    public Task SixtyStoresWithLostNotifications() =>
        AcceptAsync(ReplayStreams.SixtyStores, options: new ReplayOptions { NotifyLoss = 0.1, NotifyDelay = TimeSpan.FromMilliseconds(50) },
            known: new Known(DispatcherLanes.User, TimeSpan.FromSeconds(5), "a lost notification waits for the short safety tick", CheckMax: false));

    // A slower database: every lease and census 40 ms (the first review's results flipped between 3 and 40 ms).
    [Fact]
    public async Task SlowLeasesAt40Milliseconds()
    {
        var problems = new List<string>();
        foreach (var stream in new Func<int, ReplayStream>[] { ReplayStreams.AllProducts, ReplayStreams.StartBacklog, ReplayStreams.StoreFlood })
        {
            var comparison = await CompareAsync(stream, ReplayStreams.Seeds, new ReplayOptions(LeaseLatency: TimeSpan.FromMilliseconds(40)));
            Report(Table(comparison).Replace("### ", "### 40 ms: ", StringComparison.Ordinal));
            Func<ReplayAttempt, bool>? include = comparison.Stream.StartsWith("Mağaza Seli", StringComparison.Ordinal)
                ? attempt => !(attempt.Connection == "c0" && DispatcherSettings.Default.IsCapped(attempt.WorkType)) : null;
            // At 40 ms per lease the dispatcher's one lease at a time takes the startup burst of 300 due jobs (and the empty leases
            // between them) serially, while today's 220 loops lease in parallel: status scans start a few seconds later (bound 60 s,
            // the flip's order-read trigger), their max does not grow.
            Known[] known = stream == ReplayStreams.AllProducts ? [OrderReadsBehindRunningReconciliations]
                : stream == ReplayStreams.StartBacklog ? [new Known(DispatcherLanes.StatusScans, TimeSpan.FromSeconds(60), "one lease at a time at 40 ms per lease")]
                : [];
            problems.AddRange(Violations(comparison, include, DispatcherSettings.DefaultConnectionCap, false, known).Select(problem => $"{comparison.Stream}: {problem}"));
        }
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public async Task AnIdleWorkerReadsTheQueueLessOften()
    {
        var stream = ReplayStreams.Idle();
        var current = await ReplayHarness.RunAsync(ReplayModel.Flexible, stream);
        var dispatcher = await ReplayHarness.RunAsync(ReplayModel.Dispatcher, stream);
        Report($"Boşta saniyede sorgu: bugün {current.IdleQueriesPerSecond:0.000}, dağıtıcı {dispatcher.IdleQueriesPerSecond:0.000}");
        Assert.True(dispatcher.IdleQueriesPerSecond <= current.IdleQueriesPerSecond, $"dispatcher {dispatcher.IdleQueriesPerSecond} > {current.IdleQueriesPerSecond}");
        Assert.True(dispatcher.IdleQueriesPerSecond <= 0.1, $"dispatcher reads {dispatcher.IdleQueriesPerSecond} times a second while idle");
    }

    // Every notification, "freed" and due wake dropped: each job still starts within the safety tick plus a lease of the moment
    // it could run (its store's lock group free: a lock wait is not a lost wake-up).
    [Fact]
    public async Task WithEverySignalLostEachJobStartsWithinTheSafetyTick()
    {
        foreach (var seed in ReplayStreams.Seeds)
        {
            var result = await ReplayHarness.RunAsync(ReplayModel.Dispatcher, ReplayStreams.Light(seed), new ReplayOptions(Signals: false));
            Assert.Equal(0, result.NotFinished);
            var worst = result.Attempts.Max(attempt => attempt.LockFreeWait);
            Assert.True(worst <= DispatcherSettings.DefaultSafetyTick + TimeSpan.FromSeconds(1), $"seed {seed}: a job waited {worst} with no signal");
        }
    }

    // The same stream gives the same result: the harness is deterministic (the first one varied 2-4x between runs).
    [Fact]
    public async Task TheReplayIsDeterministic()
    {
        foreach (var model in new[] { ReplayModel.Flexible, ReplayModel.Dispatcher })
        {
            var first = await ReplayHarness.RunAsync(model, ReplayStreams.AllProducts(11));
            var second = await ReplayHarness.RunAsync(model, ReplayStreams.AllProducts(11));
            Assert.Equal(first.Attempts.Select(attempt => (attempt.JobId, attempt.Attempt, attempt.StartDelay)), second.Attempts.Select(attempt => (attempt.JobId, attempt.Attempt, attempt.StartDelay)));
            Assert.Equal(first.Leases, second.Leases);
        }
    }

    // The two cap choices of the review (report only): cap 2 on katalog, arka-plan and ilk-cekim (the default), cap 3, and cap 2
    // on every lane but kullanici, toplu, webhook and yazma.
    [Fact]
    public async Task CapVariants()
    {
        string[] allLanes = [DispatcherLanes.OrderReads, DispatcherLanes.StatusScans, DispatcherLanes.Returns, DispatcherLanes.Catalog, DispatcherLanes.Background, DispatcherLanes.InitialSync, DispatcherLanes.Urgent];
        foreach (var stream in new Func<int, ReplayStream>[] { ReplayStreams.AllProducts, ReplayStreams.StoreFlood })
            foreach (var (name, settings) in new[]
            {
                ("cap 2, katalog/arka-plan/ilk-cekim", DispatcherSettings.Default),
                ("cap 3, katalog/arka-plan/ilk-cekim", DispatcherSettings.Default.WithCap(3, DispatcherSettings.Default.CappedLanes)),
                ("cap 2, every lane but kullanici/toplu/webhook/yazma", DispatcherSettings.Default.WithCap(2, allLanes))
            })
            {
                var comparison = await CompareAsync(stream, ReplayStreams.Seeds, new ReplayOptions { Dispatcher = settings });
                Report(Table(comparison).Replace("### ", $"### {name}: ", StringComparison.Ordinal));
            }
    }

    // The lane table variants tried before the defaults were chosen (report only; MARKETPLACEHUB_QUEUE_REPLAY_VARIANTS=1).
    [Fact]
    public async Task LaneTableVariants()
    {
        if (Environment.GetEnvironmentVariable("MARKETPLACEHUB_QUEUE_REPLAY_VARIANTS") != "1") return;
        DispatcherSettings With(DispatcherSettings settings, params (string Lane, int Reserved, int Share)[] lanes)
        {
            var reserved = new Dictionary<string, int>(settings.Reserved, StringComparer.Ordinal);
            var share = new Dictionary<string, int>(settings.Share, StringComparer.Ordinal);
            foreach (var (lane, r, s) in lanes) { reserved[lane] = r; share[lane] = s; }
            return settings with { Reserved = reserved, Share = share };
        }
        var defaults = DispatcherSettings.Default;
        (string Name, DispatcherSettings Settings)[] variants =
        [
            ("default", defaults),
            ("no status deferral", defaults with { DeferStatusScans = false }),
            ("kullanici 1+1", With(defaults, (DispatcherLanes.User, 1, 1), (DispatcherLanes.InitialSync, 0, 1))),
            ("durum 3, ilk-cekim 1", With(defaults, (DispatcherLanes.StatusScans, 3, 0), (DispatcherLanes.InitialSync, 0, 1))),
            ("siparis 4, durum 3, iade 3, katalog 1, ilk-cekim 0", With(defaults, (DispatcherLanes.OrderReads, 4, 0), (DispatcherLanes.StatusScans, 3, 0), (DispatcherLanes.Returns, 0, 3), (DispatcherLanes.Catalog, 0, 1), (DispatcherLanes.InitialSync, 0, 0)))
        ];
        foreach (var stream in new Func<int, ReplayStream>[] { ReplayStreams.StartBacklog, ReplayStreams.AllProducts })
            foreach (var (name, settings) in variants)
            {
                var comparison = await CompareAsync(stream, ReplayStreams.Seeds, new ReplayOptions { Dispatcher = settings });
                var problems = Violations(comparison);
                Report(Table(comparison).Replace("### ", $"### {name}: ", StringComparison.Ordinal) + $"Sorunlar: {(problems.Count == 0 ? "yok" : string.Join("; ", problems))}\n");
            }
    }

    // Live windows of the first export (EligibleAt reconstructed before 27 Nov 13:33Z; opt-in, report only: those arrivals carry
    // live's own decisions, so they are not used to accept).
    [Fact]
    public async Task LiveWindowsOfTheFirstExport()
    {
        var path = Environment.GetEnvironmentVariable("MARKETPLACEHUB_QUEUE_REPLAY_FILE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            output.WriteLine("MARKETPLACEHUB_QUEUE_REPLAY_FILE is not set: the live windows are not replayed.");
            return;
        }
        var attempts = ReplayStreams.ReadLiveAttempts(path);
        (string Name, DateTimeOffset From, TimeSpan Length)[] windows =
        [
            ("26 Kas 08:25-08:50 tepe (toplu fatura)", new DateTimeOffset(2026, 11, 26, 8, 25, 0, TimeSpan.Zero), TimeSpan.FromMinutes(25)),
            ("25 Kas 10:55-11:40 referans dalgası", new DateTimeOffset(2026, 11, 25, 10, 55, 0, TimeSpan.Zero), TimeSpan.FromMinutes(45)),
            ("27 Kas 08:15-09:20 PttAVM kesintisi", new DateTimeOffset(2026, 11, 27, 8, 15, 0, TimeSpan.Zero), TimeSpan.FromMinutes(65))
        ];
        foreach (var (name, from, length) in windows)
        {
            var jobs = ReplayStreams.ToJobs(attempts, from, from + length);
            if (jobs.Count == 0) continue;
            var comparison = await CompareAsync(_ => new ReplayStream(name, jobs), [0]);
            Report(Table(comparison));
        }
    }

    // Calibration (opt-in): the window with EligibleAt recorded (27 Nov 13:33Z on), replayed on today's flexible lanes, must
    // reproduce live's measured waits per work type within a stated tolerance before the models are compared on it.
    [Fact]
    public async Task CalibrationWindowWithRecordedEligibleAt()
    {
        var path = Environment.GetEnvironmentVariable("MARKETPLACEHUB_QUEUE_REPLAY_ELIGIBLE_FILE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            output.WriteLine("MARKETPLACEHUB_QUEUE_REPLAY_ELIGIBLE_FILE is not set: the calibration window is not replayed.");
            return;
        }
        var attempts = ReplayStreams.ReadLiveAttempts(path);
        var from = attempts.Min(attempt => attempt.Eligible);
        var to = attempts.Max(attempt => attempt.Eligible) + TimeSpan.FromSeconds(1);
        var jobs = ReplayStreams.ToJobs(attempts, from, to);
        var name = $"Canlı {from:dd MMM HH:mm}–{to:HH:mm} UTC (EligibleAt kayıtlı)";
        var comparison = await CompareAsync(_ => new ReplayStream(name, jobs), [0]);
        Report(Table(comparison));
        // Live's own waits, by the same work types.
        var live = attempts.Where(attempt => attempt.Attempt == 1).GroupBy(attempt => ReplayHarness.WorkType(attempt.JobType, attempt.Priority), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => StatsOf(group.Select(attempt => attempt.Started - attempt.Eligible)), StringComparer.Ordinal);
        var replayed = ByWorkType(comparison.Current[0], attempt => attempt.Attempt == 1);
        var builder = new StringBuilder("| İş türü | n | Canlı p50 / p95 / maks | Esnek replay p50 / p95 / maks | Fark (p95, maks) |\n|---|---|---|---|---|\n");
        var misses = new List<string>();
        foreach (var (type, measured) in live.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var model = replayed.GetValueOrDefault(type) ?? new Stats(0, default, default, default);
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {type} | {measured.Count} | {Seconds(measured.P50)} / {Seconds(measured.P95)} / {Seconds(measured.Max)} | {Seconds(model.P50)} / {Seconds(model.P95)} / {Seconds(model.Max)} | {Seconds(model.P95 - measured.P95)}, {Seconds(model.Max - measured.Max)} |");
            // The tolerance: p95 within 1 s or 25 %, the maximum within 2 s or 50 % of live's.
            if ((model.P95 - measured.P95).Duration() > TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, measured.P95.Ticks / 4))) misses.Add($"{type} p95");
            if ((model.Max - measured.Max).Duration() > TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(2).Ticks, measured.Max.Ticks / 2))) misses.Add($"{type} max");
        }
        var quota = attempts.Where(attempt => attempt.QuotaWaitMs >= 0).ToList();
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Kalibrasyon: {(misses.Count == 0 ? "her iş türü tolerans içinde" : "tolerans dışı: " + string.Join(", ", misses))}. Çalışma sürelerinde kota beklemesi: {quota.Count} denemenin toplam süresi {quota.Sum(attempt => (attempt.Completed - attempt.Started).TotalSeconds):0} sn, içinde kota beklemesi {quota.Sum(attempt => attempt.QuotaWaitMs) / 1000.0:0.0} sn.");
        Report(builder.ToString());
        Assert.True(misses.Count == 0, string.Join(", ", misses));
        var problems = Violations(comparison);
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
