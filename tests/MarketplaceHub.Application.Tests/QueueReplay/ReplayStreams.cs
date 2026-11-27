using System.Globalization;

namespace MarketplaceHub.Application.Tests.QueueReplay;

// Queue phase 2: the replay's job streams. The synthetic ones are generated here from a seed (the same stream for the same
// seed); the live ones are read from an anonymised export of integration.job_attempts (hashed ids, no payloads) kept outside
// git in the worktree's .local-docs (MARKETPLACEHUB_QUEUE_REPLAY_FILE, MARKETPLACEHUB_QUEUE_REPLAY_ELIGIBLE_FILE).
internal static class ReplayStreams
{
    public static readonly int[] Seeds = [11, 23, 37, 41, 53];

    internal sealed class Builder(int seed)
    {
        private readonly List<ReplayJob> jobs = [];
        public Random Random { get; } = new(seed);
        public int NextId => jobs.Count + 1;

        public ReplayJob Make(TimeSpan at, string jobType, int priority, string tenant, string? connection, string platform, TimeSpan duration, ReplaySignal signal = ReplaySignal.Notify, string? lockGroup = null) =>
            new(0, at, jobType, priority, tenant, connection, platform, lockGroup, duration, signal);

        public ReplayJob Add(ReplayJob job)
        {
            var numbered = job with { Id = NextId };
            jobs.Add(numbered);
            return numbered;
        }

        public ReplayJob Add(TimeSpan at, string jobType, int priority, string tenant, string? connection, string platform, TimeSpan duration, ReplaySignal signal = ReplaySignal.Notify, string? lockGroup = null) =>
            Add(Make(at, jobType, priority, tenant, connection, platform, duration, signal, lockGroup));

        public TimeSpan Seconds(double from, double to) => TimeSpan.FromSeconds(from + (Random.NextDouble() * (to - from)));

        public IReadOnlyList<ReplayJob> Jobs => [.. jobs.OrderBy(job => job.Arrival).ThenBy(job => job.Id)];
    }

    private static readonly (string Platform, string OrderSync, string StatusSync)[] Platforms =
    [
        ("TRENDYOL", MarketplaceJobTypes.OrderSync, MarketplaceJobTypes.OrderStatusSync),
        ("HEPSIBURADA", MarketplaceJobTypes.HepsiburadaOrderSync, MarketplaceJobTypes.HepsiburadaOrderStatusSync),
        ("N11", MarketplaceJobTypes.N11OrderSync, MarketplaceJobTypes.OrderStatusSync),
        ("PAZARAMA", MarketplaceJobTypes.PazaramaOrderSync, MarketplaceJobTypes.PazaramaOrderStatusSync),
        ("PTTAVM", MarketplaceJobTypes.PttAvmOrderSync, MarketplaceJobTypes.PttAvmOrderStatusSync),
        ("SHOPIFY", MarketplaceJobTypes.ShopifyOrderSync, MarketplaceJobTypes.ShopifyOrderStatusSync)
    ];

    // Each store's steady work: a new-order read every 3 minutes, a status scan every 3 minutes, a return read and a question
    // read every 10; Trendyol stores get a webhook about every 90 s; a user action every 10 minutes per tenant; a price/stock
    // write every few minutes per store when writes is set.
    internal static void Steady(Builder builder, int stores, TimeSpan length, string tenantPrefix = "t", int tenants = 1, double orderSeconds = 2, bool writes = false, TimeSpan? from = null)
    {
        var start = from ?? TimeSpan.Zero;
        for (var store = 0; store < stores; store++)
        {
            var (platform, orderSync, statusSync) = Platforms[store % Platforms.Length];
            var connection = $"c{store}";
            var tenant = $"{tenantPrefix}{store % tenants}";
            var offset = start + builder.Seconds(0, 180);
            for (var at = offset; at < start + length; at += TimeSpan.FromSeconds(180))
            {
                builder.Add(at, orderSync, 0, tenant, connection, platform, platform == "TRENDYOL" ? builder.Seconds(8, 30) : builder.Seconds(0.2, orderSeconds));
                builder.Add(at + builder.Seconds(20, 60), statusSync, 0, tenant, connection, platform, builder.Seconds(0.5, 4));
            }
            for (var at = offset + TimeSpan.FromSeconds(90); at < start + length; at += TimeSpan.FromSeconds(600))
            {
                builder.Add(at, MarketplaceJobTypes.ReturnSync, 2, tenant, connection, platform, builder.Seconds(0.2, 4));
                builder.Add(at + TimeSpan.FromSeconds(20), MarketplaceJobTypes.QuestionSync, 3, tenant, connection, platform, builder.Seconds(0.5, 2));
            }
            if (platform == "TRENDYOL")
                for (var at = start + builder.Seconds(0, 90); at < start + length; at += builder.Seconds(30, 150))
                    builder.Add(at, MarketplaceJobTypes.WebhookIngest, 0, tenant, connection, platform, builder.Seconds(0.05, 0.8));
            if (writes)
                for (var at = start + builder.Seconds(0, 240); at < start + length; at += builder.Seconds(120, 360))
                    builder.Add(at, MarketplaceJobTypes.PriceInventorySync, 1, tenant, connection, platform, builder.Seconds(0.5, 6));
        }
        for (var tenant = 0; tenant < tenants; tenant++)
            for (var at = start + builder.Seconds(30, 600); at < start + length; at += builder.Seconds(300, 900))
                builder.Add(at, MarketplaceJobTypes.ShipmentAction, -3, $"{tenantPrefix}{tenant}", "c0", Platforms[0].Platform, builder.Seconds(0.5, 3));
    }

    // "Tüm Ürünleri Çek": seven stores read their whole catalogue at once (20-50 minutes each), their link checks and fifty
    // background reads each queue behind them, while the steady order reads, webhooks and user actions go on.
    public static ReplayStream AllProducts(int seed)
    {
        var builder = new Builder(seed);
        var length = TimeSpan.FromMinutes(60);
        Steady(builder, 7, length);
        for (var store = 0; store < 7; store++)
        {
            var (platform, _, _) = Platforms[store % Platforms.Length];
            var catalog = platform switch { "HEPSIBURADA" => MarketplaceJobTypes.HepsiburadaProductSync, "SHOPIFY" => MarketplaceJobTypes.ShopifyProductSync, _ => MarketplaceJobTypes.ProductSync };
            builder.Add(TimeSpan.FromSeconds(60 + store), catalog, 5, "t0", $"c{store}", platform, builder.Seconds(20 * 60, 50 * 60));
            builder.Add(TimeSpan.FromSeconds(65 + store), MarketplaceJobTypes.ListingVerify, 6, "t0", $"c{store}", platform, builder.Seconds(60, 300));
            for (var index = 0; index < 50; index++)
            {
                var type = (index % 5) switch
                {
                    0 => MarketplaceJobTypes.OrderReconciliation,
                    1 => MarketplaceJobTypes.StockReconciliation,
                    2 => MarketplaceJobTypes.TrendyolFinanceSync,
                    3 => MarketplaceJobTypes.ReferenceSync,
                    _ => MarketplaceJobTypes.ReturnReconciliation
                };
                builder.Add(TimeSpan.FromSeconds(70 + store) + TimeSpan.FromMilliseconds(index), type, 4, "t0", $"c{store}", platform, builder.Seconds(2, 40));
            }
        }
        return new ReplayStream("Tüm Ürünleri Çek", builder.Jobs);
    }

    // Sixty stores (ten tenants), each with its order reads, status scans, returns, questions and writes.
    public static ReplayStream SixtyStores(int seed)
    {
        var builder = new Builder(seed);
        Steady(builder, 60, TimeSpan.FromMinutes(30), tenants: 10, orderSeconds: 6, writes: true);
        return new ReplayStream("60 Mağaza", builder.Jobs);
    }

    // One store queues fifty jobs of five lock groups at once while the other stores go on.
    public static ReplayStream StoreFlood(int seed)
    {
        var builder = new Builder(seed);
        Steady(builder, 6, TimeSpan.FromMinutes(30));
        string[] types = [MarketplaceJobTypes.OrderReconciliation, MarketplaceJobTypes.StockReconciliation, MarketplaceJobTypes.TrendyolFinanceSync, MarketplaceJobTypes.ReferenceSync, MarketplaceJobTypes.ReturnReconciliation];
        for (var index = 0; index < 50; index++)
            builder.Add(TimeSpan.FromSeconds(120) + TimeSpan.FromMilliseconds(index), types[index % 5], 4, "t0", "c0", "TRENDYOL", builder.Seconds(5, 60));
        return new ReplayStream("Mağaza Seli (50 İş)", builder.Jobs);
    }

    // Three Trendyol stores' background reads are held for their full quota for ten minutes.
    public static ReplayStream QuotaHeld(int seed)
    {
        var builder = new Builder(seed);
        Steady(builder, 13, TimeSpan.FromMinutes(40));
        List<ReplayQuotaHold> holds = [];
        foreach (var store in new[] { "c0", "c6", "c12" })
        {
            for (var index = 0; index < 20; index++)
                builder.Add(TimeSpan.FromSeconds(60 + index), index % 2 == 0 ? MarketplaceJobTypes.TrendyolFinanceSync : MarketplaceJobTypes.OrderReconciliation, 4, "t0", store, "TRENDYOL", builder.Seconds(10, 60));
            holds.Add(new ReplayQuotaHold(store, MarketplaceJobTypes.TrendyolFinanceSync, TimeSpan.FromSeconds(50), TimeSpan.FromMinutes(11)));
            holds.Add(new ReplayQuotaHold(store, MarketplaceJobTypes.OrderReconciliation, TimeSpan.FromSeconds(50), TimeSpan.FromMinutes(11)));
        }
        return new ReplayStream("Kota Bekleyen Trendyol", builder.Jobs, holds);
    }

    // The worker starts with 300 jobs due (twenty stores), no notification for any of them, and the steady work after.
    public static ReplayStream StartBacklog(int seed)
    {
        var builder = new Builder(seed);
        for (var index = 0; index < 300; index++)
        {
            var store = index % 20;
            var (platform, orderSync, statusSync) = Platforms[store % Platforms.Length];
            (string type, int priority) = (index % 6) switch
            {
                0 => (orderSync, 0),
                1 => (statusSync, 0),
                2 => (MarketplaceJobTypes.ReturnSync, 2),
                3 => (MarketplaceJobTypes.QuestionSync, 3),
                4 => (MarketplaceJobTypes.OrderReconciliation, 4),
                _ => (MarketplaceJobTypes.ReferenceSync, 5)
            };
            builder.Add(TimeSpan.Zero, type, priority, $"t{store % 4}", $"c{store}", platform, builder.Seconds(0.2, 20), ReplaySignal.Due);
        }
        Steady(builder, 20, TimeSpan.FromMinutes(20), tenants: 4);
        return new ReplayStream("Açılışta 300 Vadeli İş", builder.Jobs, StartBacklog: true);
    }

    // Six and a half hours of twenty stores with a worker restart every three hours (45 restarts a week on live), each with work
    // running and queued.
    public static ReplayStream Restarts(int seed)
    {
        var builder = new Builder(seed);
        Steady(builder, 20, TimeSpan.FromHours(6.5), tenants: 4, writes: true);
        for (var hour = 0.5; hour < 6.5; hour += 1)
            for (var store = 0; store < 20; store += 4)
                builder.Add(TimeSpan.FromHours(hour) + TimeSpan.FromSeconds(store), MarketplaceJobTypes.OrderReconciliation, 4, $"t{store % 4}", $"c{store}", Platforms[store % Platforms.Length].Platform, builder.Seconds(5, 40));
        return new ReplayStream("Her 3 Saatte Yeniden Başlatma", builder.Jobs) { Restarts = [TimeSpan.FromHours(3), TimeSpan.FromHours(6)] };
    }

    // A marketplace outage (PttAVM, 27 Nov): for twenty minutes every request of two PttAVM stores fails at once; the reads
    // retry by the table while the next polls keep coming (and a failed read puts the store's lock group in a 30 s backoff).
    public static ReplayStream Outage(int seed)
    {
        var builder = new Builder(seed);
        var length = TimeSpan.FromMinutes(45);
        Steady(builder, 10, length);
        var outageFrom = TimeSpan.FromMinutes(10);
        var outageTo = TimeSpan.FromMinutes(30);
        foreach (var store in new[] { "p0", "p1" })
            for (var at = builder.Seconds(0, 130); at < length; at += TimeSpan.FromSeconds(130))
            {
                foreach (var (type, priority) in new[] { (MarketplaceJobTypes.PttAvmOrderSync, 0), (MarketplaceJobTypes.ReturnSync, 2) })
                {
                    if (type == MarketplaceJobTypes.ReturnSync && at.TotalSeconds % 600 > 130) continue;
                    // Each attempt fails while the outage lasts (by when that attempt would run, roughly), then succeeds.
                    var attempts = new List<(TimeSpan, bool)>();
                    var t = at;
                    TimeSpan[] delays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];
                    for (var attempt = 0; attempt < 6; attempt++)
                    {
                        var failing = t >= outageFrom && t < outageTo;
                        attempts.Add((failing ? builder.Seconds(0.2, 1.5) : builder.Seconds(0.2, 2), !failing));
                        if (!failing) break;
                        t += delays[Math.Min(attempt, delays.Length - 1)];
                    }
                    builder.Add(builder.Make(at, type, priority, "t1", store, "PTTAVM", attempts[0].Item1) with { Attempts = attempts, BackoffOnFailure = TimeSpan.FromSeconds(30) });
                }
            }
        return new ReplayStream("PttAVM Kesintisi (Birikmeli)", builder.Jobs);
    }

    // A bulk invoice issue: 300 invoices at once (bulk user work, -2, on the invoice provider's connection; each a submit with a
    // follow-up document fetch), and single user actions (-3) arriving meanwhile, with the steady order work.
    public static ReplayStream BulkInvoices(int seed)
    {
        var builder = new Builder(seed);
        Steady(builder, 6, TimeSpan.FromMinutes(30));
        for (var index = 0; index < 300; index++)
        {
            var fetch = builder.Make(TimeSpan.Zero, InvoicingJobTypes.InvoiceDocumentFetch, -2, "t0", "efaturam", "TRENDYOL_EFATURAM", builder.Seconds(0.5, 3));
            var submit = builder.Make(TimeSpan.FromSeconds(60) + TimeSpan.FromMilliseconds(index * 10), InvoicingJobTypes.InvoiceSubmit, -2, "t0", "efaturam", "TRENDYOL_EFATURAM", builder.Seconds(1, 4));
            builder.Add(submit with { FollowUps = [(TimeSpan.FromSeconds(5), fetch with { Id = 0 })] });
        }
        for (var at = TimeSpan.FromSeconds(70); at < TimeSpan.FromMinutes(25); at += builder.Seconds(20, 60))
        {
            var invoice = builder.Random.Next(2) == 0;
            builder.Add(at, invoice ? InvoicingJobTypes.InvoiceSubmit : MarketplaceJobTypes.ShipmentAction, -3, "t0", invoice ? "efaturam" : "c0", "TRENDYOL", builder.Seconds(0.5, 3));
        }
        return new ReplayStream("Toplu Fatura (300) ve Tekil İşlemler", builder.Jobs);
    }

    // A user's bulk update of 300 products on one store (PRODUCT_UPDATE, priority 2: one at a time per store, its products lock
    // group) while orders and webhooks arrive and other stores write prices.
    public static ReplayStream BulkPublish(int seed)
    {
        var builder = new Builder(seed);
        Steady(builder, 6, TimeSpan.FromMinutes(40), writes: true);
        for (var index = 0; index < 300; index++)
            builder.Add(TimeSpan.FromSeconds(60) + TimeSpan.FromMilliseconds(index * 5), MarketplaceJobTypes.ProductUpdate, 2, "t0", "c0", "TRENDYOL", builder.Seconds(2, 6));
        builder.Add(TimeSpan.FromSeconds(65), "IMPORT_APPLY", 0, "t0", null, "", TimeSpan.FromMinutes(6));
        return new ReplayStream("300 Ürün Toplu Güncelleme", builder.Jobs);
    }

    // One store's two long reference reads (8.5 minutes each, one references lock group) under the store cap, with the rest.
    public static ReplayStream LongReferenceReads(int seed)
    {
        var builder = new Builder(seed);
        Steady(builder, 6, TimeSpan.FromMinutes(40));
        builder.Add(TimeSpan.FromSeconds(30), MarketplaceJobTypes.PazaramaReferenceSync, 5, "t0", "c3", "PAZARAMA", TimeSpan.FromSeconds(513));
        builder.Add(TimeSpan.FromSeconds(31), MarketplaceJobTypes.PazaramaReferenceSync, 5, "t0", "c3", "PAZARAMA", TimeSpan.FromSeconds(510), lockGroup: "references-2");
        builder.Add(TimeSpan.FromSeconds(40), MarketplaceJobTypes.StockReconciliation, 4, "t0", "c3", "PAZARAMA", builder.Seconds(5, 30));
        builder.Add(TimeSpan.FromSeconds(45), MarketplaceJobTypes.OrderReconciliation, 4, "t0", "c3", "PAZARAMA", builder.Seconds(5, 30));
        return new ReplayStream("Bir Mağazanın İki Uzun Referans Okuması", builder.Jobs);
    }

    // Two stores activated at once: each one's first import (order history, products, references, returns, finance, questions:
    // priority -1, several minutes each) with the steady work of the other stores.
    public static ReplayStream TwoInitialImports(int seed)
    {
        var builder = new Builder(seed);
        Steady(builder, 6, TimeSpan.FromMinutes(45));
        foreach (var (store, platform) in new[] { ("n0", "TRENDYOL"), ("n1", "HEPSIBURADA") })
        {
            builder.Add(TimeSpan.FromSeconds(30), platform == "TRENDYOL" ? MarketplaceJobTypes.OrderSync : MarketplaceJobTypes.HepsiburadaOrderSync, -1, "t2", store, platform, TimeSpan.FromMinutes(10));
            builder.Add(TimeSpan.FromSeconds(30), platform == "TRENDYOL" ? MarketplaceJobTypes.ProductSync : MarketplaceJobTypes.HepsiburadaProductSync, -1, "t2", store, platform, TimeSpan.FromMinutes(18));
            builder.Add(TimeSpan.FromSeconds(30), MarketplaceJobTypes.ReferenceSync, -1, "t2", store, platform, TimeSpan.FromMinutes(8));
            builder.Add(TimeSpan.FromSeconds(30), MarketplaceJobTypes.ReturnSync, -1, "t2", store, platform, TimeSpan.FromMinutes(3));
            builder.Add(TimeSpan.FromSeconds(30), MarketplaceJobTypes.TrendyolFinanceSync, -1, "t2", store, platform, TimeSpan.FromMinutes(5));
            builder.Add(TimeSpan.FromSeconds(30), MarketplaceJobTypes.QuestionSync, -1, "t2", store, platform, TimeSpan.FromMinutes(1));
        }
        return new ReplayStream("İki Mağazanın İlk Çekimi", builder.Jobs);
    }

    // Writes: price/stock bursts on every store and product updates, with webhooks and orders.
    public static ReplayStream Writes(int seed)
    {
        var builder = new Builder(seed);
        Steady(builder, 12, TimeSpan.FromMinutes(30), tenants: 2, writes: true);
        for (var burst = 0; burst < 3; burst++)
            for (var store = 0; store < 12; store++)
            {
                builder.Add(TimeSpan.FromMinutes(5 + (burst * 8)) + TimeSpan.FromMilliseconds(store), MarketplaceJobTypes.StockProjectionDispatch, 1, $"t{store % 2}", $"c{store}", Platforms[store % 6].Platform, builder.Seconds(1, 8));
                builder.Add(TimeSpan.FromMinutes(5 + (burst * 8)) + TimeSpan.FromMilliseconds(store + 20), MarketplaceJobTypes.ProductUpdate, 2, $"t{store % 2}", $"c{store}", Platforms[store % 6].Platform, builder.Seconds(2, 6));
            }
        return new ReplayStream("Pazaryerine Yazma Dalgası", builder.Jobs);
    }

    // Nothing to do: how often each model reads the queue while idle.
    public static ReplayStream Idle() => new("Boşta 10 dk", [], IdleTail: TimeSpan.FromMinutes(10));

    // Light work with every signal dropped: each job must still start within the safety tick.
    public static ReplayStream Light(int seed)
    {
        var builder = new Builder(seed);
        Steady(builder, 4, TimeSpan.FromMinutes(20));
        return new ReplayStream("Hafif (4 Mağaza)", builder.Jobs);
    }

    // ---- Live, from the anonymised exports ----
    // Old export (attempts since 13 Nov, EligibleAt reconstructed before 27 Nov 13:33Z), 15 columns: job, attempt, tenant,
    // connection, platform, type, priority, lock group, trigger, eligible ms, started ms, completed ms, succeeded, eligible
    // recorded, queued for later. New export (EligibleAt recorded), 17 columns: ... completed ms, succeeded, quota wait ms, lock
    // wait ms, slot wait ms, lane. A job's attempts become one ReplayJob: it arrives at its first attempt's eligible moment and
    // its later attempts follow causally (the retry table), with their recorded run times and outcomes.
    internal sealed record LiveAttempt(string Job, int Attempt, string Tenant, string Connection, string Platform, string JobType, int Priority, string LockGroup,
        DateTimeOffset Eligible, DateTimeOffset Started, DateTimeOffset Completed, bool Succeeded, bool QueuedForLater, int QuotaWaitMs);

    public static IReadOnlyList<LiveAttempt> ReadLiveAttempts(string path)
    {
        var rows = new List<LiveAttempt>();
        foreach (var raw in File.ReadLines(path))
        {
            var fields = raw.Trim().Split(',');
            if (fields.Length is not (15 or 17) || !long.TryParse(fields[9], CultureInfo.InvariantCulture, out var eligibleMs)) continue;
            var started = long.Parse(fields[10], CultureInfo.InvariantCulture);
            var completed = long.Parse(fields[11], CultureInfo.InvariantCulture);
            rows.Add(new LiveAttempt(fields[0], int.Parse(fields[1], CultureInfo.InvariantCulture), fields[2], fields[3], fields[4], fields[5], int.Parse(fields[6], CultureInfo.InvariantCulture),
                fields[7], DateTimeOffset.FromUnixTimeMilliseconds(eligibleMs), DateTimeOffset.FromUnixTimeMilliseconds(started), DateTimeOffset.FromUnixTimeMilliseconds(completed),
                fields[12] == "1", fields.Length == 15 && fields[14] == "1", fields.Length == 17 ? int.Parse(fields[13], CultureInfo.InvariantCulture) : -1));
        }
        return rows;
    }

    public static IReadOnlyList<ReplayJob> ReadLive(string path, DateTimeOffset from, DateTimeOffset to) => ToJobs(ReadLiveAttempts(path), from, to);

    public static IReadOnlyList<ReplayJob> ToJobs(IReadOnlyList<LiveAttempt> attempts, DateTimeOffset from, DateTimeOffset to)
    {
        var jobs = new List<ReplayJob>();
        foreach (var group in attempts.GroupBy(row => row.Job).Select(group => group.OrderBy(row => row.Attempt).ToList()))
        {
            var first = group[0];
            if (first.Eligible < from || first.Eligible >= to) continue;
            var runs = group.Select(row => (TimeSpan.FromMilliseconds(Math.Max(10, (row.Completed - row.Started).TotalMilliseconds)), row.Succeeded)).ToList();
            var signal = first.Attempt == 1 && !first.QueuedForLater ? ReplaySignal.Notify : ReplaySignal.Due;
            jobs.Add(new ReplayJob(jobs.Count + 1, first.Eligible - from, first.JobType, first.Priority, first.Tenant, first.Connection.Length == 0 ? null : first.Connection,
                first.Platform, first.LockGroup.Length == 0 ? null : first.LockGroup, runs[0].Item1, signal)
            { Attempts = runs, MaxAttempts = runs.All(run => !run.Succeeded) ? runs.Count : 6 });
        }
        return jobs;
    }
}
