using System.Globalization;

namespace MarketplaceHub.Application.Tests.QueueReplay;

// Queue phase 2: the replay's job streams. The synthetic ones are generated here with a fixed seed (the same stream on every
// run); the live ones are read from an anonymised export of integration.job_attempts (hashed ids, no payloads), kept outside
// git in the worktree's .local-docs (MARKETPLACEHUB_QUEUE_REPLAY_FILE).
internal static class ReplayStreams
{
    private sealed class Builder(int seed)
    {
        private readonly List<ReplayJob> jobs = [];
        public Random Random { get; } = new(seed);

        public void Add(TimeSpan at, string jobType, int priority, string tenant, string? connection, string platform, TimeSpan duration, ReplaySignal signal = ReplaySignal.Notify, string? lockGroup = null) =>
            jobs.Add(new ReplayJob(jobs.Count + 1, at, jobType, priority, tenant, connection, platform, lockGroup, duration, signal));

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

    // Each store's steady work: a new-order read every 3 minutes, a status scan every 3 minutes, a return read every 10, a
    // question read every 10; Trendyol stores get a webhook about every 90 s; a user action every 10 minutes per tenant.
    private static void Steady(Builder builder, int stores, TimeSpan length, string tenantPrefix = "t", int tenants = 1, double orderSeconds = 2)
    {
        for (var store = 0; store < stores; store++)
        {
            var (platform, orderSync, statusSync) = Platforms[store % Platforms.Length];
            var connection = $"c{store}";
            var tenant = $"{tenantPrefix}{store % tenants}";
            var offset = builder.Seconds(0, 180);
            for (var at = offset; at < length; at += TimeSpan.FromSeconds(180))
            {
                builder.Add(at, orderSync, 0, tenant, connection, platform, platform == "TRENDYOL" ? builder.Seconds(8, 30) : builder.Seconds(0.2, orderSeconds));
                builder.Add(at + TimeSpan.FromSeconds(40), statusSync, 0, tenant, connection, platform, builder.Seconds(0.5, 4));
            }
            for (var at = offset + TimeSpan.FromSeconds(90); at < length; at += TimeSpan.FromSeconds(600))
            {
                builder.Add(at, MarketplaceJobTypes.ReturnSync, 2, tenant, connection, platform, builder.Seconds(0.2, 4));
                builder.Add(at + TimeSpan.FromSeconds(20), MarketplaceJobTypes.QuestionSync, 3, tenant, connection, platform, builder.Seconds(0.5, 2));
            }
            if (platform == "TRENDYOL")
                for (var at = builder.Seconds(0, 90); at < length; at += builder.Seconds(30, 150))
                    builder.Add(at, MarketplaceJobTypes.WebhookIngest, 0, tenant, connection, platform, builder.Seconds(0.05, 0.8));
        }
        for (var tenant = 0; tenant < tenants; tenant++)
            for (var at = builder.Seconds(30, 600); at < length; at += builder.Seconds(300, 900))
                builder.Add(at, MarketplaceJobTypes.ShipmentAction, -3, $"{tenantPrefix}{tenant}", "c0", Platforms[0].Platform, builder.Seconds(0.5, 3));
    }

    // "Tüm Ürünleri Çek": seven stores read their whole catalogue at once (20-50 minutes each) and their link checks and a few
    // hundred background reads queue behind them, while the steady order reads, webhooks and user actions go on.
    public static ReplayStream AllProducts()
    {
        var builder = new Builder(11);
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

    // Sixty stores (ten tenants), each with its order reads, status scans, returns and questions.
    public static ReplayStream SixtyStores()
    {
        var builder = new Builder(23);
        Steady(builder, 60, TimeSpan.FromMinutes(30), tenants: 10, orderSeconds: 6);
        return new ReplayStream("60 Mağaza", builder.Jobs);
    }

    // One store queues fifty jobs of five lock groups at once while the other stores go on.
    public static ReplayStream StoreFlood()
    {
        var builder = new Builder(37);
        Steady(builder, 6, TimeSpan.FromMinutes(30));
        string[] types = [MarketplaceJobTypes.OrderReconciliation, MarketplaceJobTypes.StockReconciliation, MarketplaceJobTypes.TrendyolFinanceSync, MarketplaceJobTypes.ReferenceSync, MarketplaceJobTypes.ReturnReconciliation];
        for (var index = 0; index < 50; index++)
            builder.Add(TimeSpan.FromSeconds(120) + TimeSpan.FromMilliseconds(index), types[index % 5], 4, "t0", "c0", "TRENDYOL", builder.Seconds(5, 60));
        return new ReplayStream("Mağaza Seli (50 İş)", builder.Jobs);
    }

    // Three Trendyol stores' background reads are held for their full quota for ten minutes.
    public static ReplayStream QuotaHeld()
    {
        var builder = new Builder(41);
        Steady(builder, 13, TimeSpan.FromMinutes(40));
        List<ReplayQuotaHold> holds = [];
        foreach (var store in new[] { "c0", "c6", "c12" })
        {
            builder.Add(TimeSpan.FromSeconds(30), MarketplaceJobTypes.ReturnSync, 2, "t0", store, "TRENDYOL", TimeSpan.FromSeconds(1));
            for (var index = 0; index < 20; index++)
                builder.Add(TimeSpan.FromSeconds(60 + index), index % 2 == 0 ? MarketplaceJobTypes.TrendyolFinanceSync : MarketplaceJobTypes.OrderReconciliation, 4, "t0", store, "TRENDYOL", builder.Seconds(10, 60));
            holds.Add(new ReplayQuotaHold(store, MarketplaceJobTypes.TrendyolFinanceSync, TimeSpan.FromSeconds(50), TimeSpan.FromMinutes(11)));
            holds.Add(new ReplayQuotaHold(store, MarketplaceJobTypes.OrderReconciliation, TimeSpan.FromSeconds(50), TimeSpan.FromMinutes(11)));
        }
        return new ReplayStream("Kota Bekleyen Trendyol", builder.Jobs, holds);
    }

    // The worker starts with 300 jobs due (twenty stores), no notification for any of them, and the steady work after.
    public static ReplayStream StartBacklog()
    {
        var builder = new Builder(53);
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

    // Nothing to do: how often each model reads the queue while idle.
    public static ReplayStream Idle() => new("Boşta 10 dk", [], IdleTail: TimeSpan.FromMinutes(10));

    // Light work with every signal dropped: each job must still start within the safety tick.
    public static ReplayStream Light()
    {
        var builder = new Builder(67);
        Steady(builder, 4, TimeSpan.FromMinutes(20));
        return new ReplayStream("Hafif (4 Mağaza)", builder.Jobs);
    }

    // ---- Live, from the anonymised export ----
    // Columns: job hash, attempt, tenant hash, connection hash, platform, job type, priority, lock group, trigger, eligible ms,
    // started ms, completed ms, succeeded, eligible recorded, queued for later. A first attempt queued runnable arrives with
    // its notification; a retry or a job queued for later through the due-time wake.
    public static IReadOnlyList<ReplayJob> ReadLive(string path, DateTimeOffset from, DateTimeOffset to)
    {
        var jobs = new List<ReplayJob>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            var fields = line.Split(',');
            if (fields.Length != 15 || !long.TryParse(fields[9], CultureInfo.InvariantCulture, out var eligibleMs)) continue;
            var eligible = DateTimeOffset.FromUnixTimeMilliseconds(eligibleMs);
            if (eligible < from || eligible >= to) continue;
            var started = long.Parse(fields[10], CultureInfo.InvariantCulture);
            var completed = long.Parse(fields[11], CultureInfo.InvariantCulture);
            var attempt = int.Parse(fields[1], CultureInfo.InvariantCulture);
            var signal = attempt == 1 && fields[14] == "0" ? ReplaySignal.Notify : ReplaySignal.Due;
            jobs.Add(new ReplayJob(jobs.Count + 1, eligible - from, fields[5], int.Parse(fields[6], CultureInfo.InvariantCulture), fields[2],
                fields[3].Length == 0 ? null : fields[3], fields[4], fields[7].Length == 0 ? null : fields[7],
                TimeSpan.FromMilliseconds(Math.Max(10, completed - started)), signal));
        }
        return jobs;
    }
}
