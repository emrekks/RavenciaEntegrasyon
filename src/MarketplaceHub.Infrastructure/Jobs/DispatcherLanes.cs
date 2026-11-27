using MarketplaceHub.Application;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Jobs;

// Queue phase 2 (27 Nov 2026), the dispatcher's lanes (Worker:LaneModel=dispatcher): one lane per kind of work instead of one
// per platform, and every job in exactly one lane (the lanes are disjoint, so LaneFor never depends on their order):
//   kullanici       user work: priority BulkUserAction (-2) or lower, any type
//   ilk-cekim       a new store's first import: priority InitialDataSync (-1)
//   acil            webhooks and marketplace writes: priority 0..hot ceiling, every type the typed lanes below do not take
//   siparis-okuma   every platform's new-order and recovery reads (priority 0 and above)
//   durum-taramasi  every platform's open-order status scans (priority 0 and above)
//   iade            return reads (priority 0 and above)
//   katalog         product catalogue reads and link checks (priority 0 and above)
//   arka-plan       everything else above the hot ceiling (reconciliations, reference lists, finance, questions)
// The ids are Turkish and differ from the earlier lanes' ids on purpose: job_attempts.Lane holds both kinds around a switch.
// Each lane's slots (DispatcherSettings): Reserved slots only that lane ever uses (kullanici and acil "reserved", siparis-okuma
// and durum-taramasi a "minimum", the same rule); a Share of the shared slots the lane is served first on when it has work
// (iade, katalog, arka-plan, ilk-cekim; lent while the lane is idle). WorkerLane.DefaultSlots is the two together, the lane's
// own share of the 20 slots.
public static class DispatcherLanes
{
    public const string User = "kullanici";
    public const string Urgent = "acil";
    public const string OrderReads = "siparis-okuma";
    public const string StatusScans = "durum-taramasi";
    public const string Returns = "iade";
    public const string Catalog = "katalog";
    public const string Background = "arka-plan";
    public const string InitialSync = "ilk-cekim";

    // New-order reads and their recovery reads, every platform.
    public static readonly IReadOnlyList<string> OrderReadJobTypes =
    [
        MarketplaceJobTypes.OrderSync, MarketplaceJobTypes.OrderRecoverySync,
        MarketplaceJobTypes.ShopifyOrderSync, MarketplaceJobTypes.ShopifyOrderRecoverySync,
        MarketplaceJobTypes.HepsiburadaOrderSync, MarketplaceJobTypes.HepsiburadaOrderRecoverySync,
        MarketplaceJobTypes.N11OrderSync, MarketplaceJobTypes.N11OrderRecoverySync,
        MarketplaceJobTypes.PazaramaOrderSync, MarketplaceJobTypes.PazaramaOrderRecoverySync,
        MarketplaceJobTypes.PttAvmOrderSync, MarketplaceJobTypes.PttAvmOrderRecoverySync
    ];

    // Open-order status scans, every platform.
    public static readonly IReadOnlyList<string> StatusScanJobTypes =
    [
        MarketplaceJobTypes.OrderStatusSync, MarketplaceJobTypes.ShopifyOrderStatusSync, MarketplaceJobTypes.HepsiburadaOrderStatusSync,
        MarketplaceJobTypes.PazaramaOrderStatusSync, MarketplaceJobTypes.PttAvmOrderStatusSync
    ];

    public static readonly IReadOnlyList<string> ReturnReadJobTypes = WorkerLanes.ReturnReadJobTypes;

    public static readonly IReadOnlyList<string> CatalogJobTypes = [.. JobTypeRegistry.ProductCatalogSyncJobTypes, .. JobTypeRegistry.ListingVerificationJobTypes];

    // The types the typed lanes take: never urgent or background, whatever their priority.
    public static readonly IReadOnlyList<string> TypedLaneJobTypes = [.. OrderReadJobTypes, .. StatusScanJobTypes, .. ReturnReadJobTypes, .. CatalogJobTypes];

    // In display order (the plan's): Kullanıcı, Acil, Sipariş Okuma, Durum Taraması, İade, Katalog, Arka Plan, İlk Çekim. The
    // dispatcher serves free shared slots in this order too (after each lane's reserved slots and share; DispatcherCore).
    public static IReadOnlyList<WorkerLane> All(int hotPriorityCeiling = WorkerLanes.DefaultHotPriorityCeiling, DispatcherSettings? settings = null)
    {
        settings ??= DispatcherSettings.Default;
        return
        [
            new(User, settings.SlotsOf(User), JobPriorityPolicy.BulkUserAction, null, [], []),
            new(Urgent, settings.SlotsOf(Urgent), hotPriorityCeiling, 0, [], TypedLaneJobTypes),
            new(OrderReads, settings.SlotsOf(OrderReads), null, 0, OrderReadJobTypes, []),
            new(StatusScans, settings.SlotsOf(StatusScans), null, 0, StatusScanJobTypes, []),
            new(Returns, settings.SlotsOf(Returns), null, 0, ReturnReadJobTypes, []),
            new(Catalog, settings.SlotsOf(Catalog), null, 0, CatalogJobTypes, []),
            new(Background, settings.SlotsOf(Background), null, hotPriorityCeiling + 1, [], TypedLaneJobTypes),
            new(InitialSync, settings.SlotsOf(InitialSync), JobPriorityPolicy.InitialDataSync, JobPriorityPolicy.InitialDataSync, [], [])
        ];
    }

    public static IReadOnlyList<string> Names { get; } = [User, Urgent, OrderReads, StatusScans, Returns, Catalog, Background, InitialSync];

    public static bool IsDispatcherLane(string? lane) => lane is not null && Names.Contains(lane, StringComparer.Ordinal);

    // The one lane that leases a job of this type and priority (the lanes are disjoint).
    public static string LaneFor(string jobType, int priority, int hotPriorityCeiling = WorkerLanes.DefaultHotPriorityCeiling)
    {
        if (priority <= JobPriorityPolicy.BulkUserAction) return User;
        if (priority < 0) return InitialSync;
        if (OrderReadJobTypes.Contains(jobType)) return OrderReads;
        if (StatusScanJobTypes.Contains(jobType)) return StatusScans;
        if (ReturnReadJobTypes.Contains(jobType)) return Returns;
        if (CatalogJobTypes.Contains(jobType)) return Catalog;
        return priority <= hotPriorityCeiling ? Urgent : Background;
    }

    // The per-store cap as the lease query applies it: the jobs of the capped lanes count and wait (DispatcherSettings.CappedLanes).
    public static JobConnectionCap ConnectionCap(int cap, IReadOnlyCollection<string> cappedLanes, int hotPriorityCeiling = WorkerLanes.DefaultHotPriorityCeiling) =>
        new(cap, cappedLanes.Contains(User), cappedLanes.Contains(InitialSync), cappedLanes.Contains(Urgent), cappedLanes.Contains(Background),
            [.. (cappedLanes.Contains(OrderReads) ? OrderReadJobTypes : []), .. (cappedLanes.Contains(StatusScans) ? StatusScanJobTypes : []),
             .. (cappedLanes.Contains(Returns) ? ReturnReadJobTypes : []), .. (cappedLanes.Contains(Catalog) ? CatalogJobTypes : [])],
            TypedLaneJobTypes, hotPriorityCeiling);

    public static JobConnectionCap ConnectionCap(DispatcherSettings settings, int hotPriorityCeiling = WorkerLanes.DefaultHotPriorityCeiling) =>
        ConnectionCap(settings.ConnectionCap, settings.CappedLanes, hotPriorityCeiling);

    public static string DisplayName(string lane) => lane switch
    {
        User => "Kullanıcı",
        Urgent => "Acil",
        OrderReads => "Sipariş Okuma",
        StatusScans => "Durum Taraması",
        Returns => "İade",
        Catalog => "Katalog",
        Background => "Arka Plan",
        InitialSync => "İlk Çekim",
        _ => lane
    };
}

// Queue phase 2, the dispatcher's slots, read the same way by the worker (DispatcherCore) and the api (the lane table):
//   Worker:Dispatcher:Reserved:<lane>   slots only the lane uses, never lent (0..8); default kullanici 2, acil 1,
//                                       siparis-okuma 3, durum-taramasi 3. The earlier protected lanes held ten, but the hot
//                                       lane's three carried the Trendyol, N11 and Shopify order reads too; acil keeps one of its
//                                       own (webhooks run in tens of milliseconds and are served first on a free shared slot) and
//                                       the status scans get the other (replay "Açılışta 300 Vadeli İş": with acil 3 at most 16
//                                       jobs ran, slots reserved for idle lanes, against 18 today; with durum-taramasi 2 its p95
//                                       was 80 s against 69 s today)
//   Worker:Dispatcher:Share:<lane>      the lane's share of the shared slots (0..8): it is served first on that many shared slots
//                                       when it has work, and lends them while idle; default iade 3, katalog 3, arka-plan 2,
//                                       ilk-cekim 3 (the earlier unprotected lanes' own slots, iade one more: the same replay
//                                       gave return reads p95 130 s against 102 s today with 2)
//   Worker:Dispatcher:ConnectionCap     at most this many running jobs of one store in the capped lanes (default 2, 0 = off)
//   Worker:Dispatcher:CappedLanes       the lanes the cap counts and holds (comma list; default katalog,arka-plan,ilk-cekim:
//                                       the bulk work one store can queue by the dozen); kullanici and acil are never capped.
//                                       siparis-okuma, durum-taramasi and iade are not capped by default: a store runs at most
//                                       one of each at a time anyway (lock groups "orders" and "returns"), and capping them made
//                                       a store's new-order reads wait behind its own catalogue and background reads (replay
//                                       "Tüm Ürünleri Çek": order reads p95 2.8 s today, 25.6 s with every lane capped)
//   Worker:Dispatcher:SafetyTickSeconds how often the dispatcher reads which lanes have runnable jobs even if no notification
//                                       came (default 15, 5..60): the longest a job waits for a lost wake-up
// The total stays the sum (20 by default; the worker's database pool needs 2 x total + 10, WorkerLanes.RequiredPoolSize).
public sealed record DispatcherSettings(IReadOnlyDictionary<string, int> Reserved, IReadOnlyDictionary<string, int> Share, int ConnectionCap, TimeSpan SafetyTick, IReadOnlyList<string> CappedLanes)
{
    public const string DefaultCappedLanes = DispatcherLanes.Catalog + "," + DispatcherLanes.Background + "," + DispatcherLanes.InitialSync;

    private static IReadOnlyList<string> ParseCappedLanes(string? value) =>
        [.. (value ?? DefaultCappedLanes).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(lane => DispatcherLanes.Names.Contains(lane, StringComparer.Ordinal) && lane is not (DispatcherLanes.User or DispatcherLanes.Urgent)).Distinct(StringComparer.Ordinal)];

    // Whether the cap holds the lane's jobs (its lease passes JobConnectionCap).
    public bool IsCapped(string lane) => ConnectionCap > 0 && CappedLanes.Contains(lane, StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, int> DefaultReserved = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [DispatcherLanes.User] = 2,
        [DispatcherLanes.Urgent] = 1,
        [DispatcherLanes.OrderReads] = 3,
        [DispatcherLanes.StatusScans] = 3
    };

    public static readonly IReadOnlyDictionary<string, int> DefaultShare = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [DispatcherLanes.Returns] = 3,
        [DispatcherLanes.Catalog] = 3,
        [DispatcherLanes.Background] = 2,
        [DispatcherLanes.InitialSync] = 3
    };

    public const int DefaultConnectionCap = 2;
    public static readonly TimeSpan DefaultSafetyTick = TimeSpan.FromSeconds(15);

    public static DispatcherSettings Default { get; } = new(DefaultReserved, DefaultShare, DefaultConnectionCap, DefaultSafetyTick, ParseCappedLanes(null));

    public static DispatcherSettings From(IConfiguration? configuration)
    {
        if (configuration is null) return Default;
        return new(
            DispatcherLanes.Names.ToDictionary(lane => lane, lane => Math.Clamp(configuration.GetValue($"Worker:Dispatcher:Reserved:{lane}", DefaultReserved.GetValueOrDefault(lane)), 0, 8), StringComparer.Ordinal),
            DispatcherLanes.Names.ToDictionary(lane => lane, lane => Math.Clamp(configuration.GetValue($"Worker:Dispatcher:Share:{lane}", DefaultShare.GetValueOrDefault(lane)), 0, 8), StringComparer.Ordinal),
            Math.Clamp(configuration.GetValue("Worker:Dispatcher:ConnectionCap", DefaultConnectionCap), 0, 20),
            TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Worker:Dispatcher:SafetyTickSeconds", (int)DefaultSafetyTick.TotalSeconds), 5, 60)),
            ParseCappedLanes(configuration["Worker:Dispatcher:CappedLanes"]));
    }

    public int ReservedOf(string lane) => Reserved.GetValueOrDefault(lane);
    public int ShareOf(string lane) => Share.GetValueOrDefault(lane);
    public int SlotsOf(string lane) => ReservedOf(lane) + ShareOf(lane);
    public int Total => DispatcherLanes.Names.Sum(SlotsOf);
}
