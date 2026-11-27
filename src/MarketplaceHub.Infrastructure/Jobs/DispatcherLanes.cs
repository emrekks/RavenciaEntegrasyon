using MarketplaceHub.Application;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Jobs;

// Queue phase 2 (27 Nov 2026, lane table revised after review the same day), the dispatcher's lanes
// (Worker:LaneModel=dispatcher): one lane per kind of work instead of one per platform, and every job in exactly one lane (the
// lanes are disjoint, so LaneFor never depends on their order). In the order LaneFor tests them:
//   kullanici       a single action a user waits for: priority UserAction (-3) or lower, any type but the price writes, the
//                   automatic read-backs (to yazma) and the resets (to toplu): UserPriorityWriteJobTypes, UserPriorityBulkJobTypes
//   toplu           work started in bulk: priority BulkUserAction (-2: a bulk invoice run and its follow-ups) and the file
//                   imports (IMPORT_PREVIEW, IMPORT_APPLY, which run for minutes), so a single action never queues behind them
//   ilk-cekim       a new store's first import: priority InitialDataSync (-1)
//   webhook         webhook deliveries (priority 0 and above): short jobs only, nothing long can take their slot
//   yazma           writes to a marketplace or the invoice provider (JobTypeRegistry.WritingJobTypes, priority 0 and above:
//                   price and stock, product create/update/archive, shipment, label, invoice); one store's writes of a kind
//                   share a lock group, so a 300-product publish runs one job at a time per store
//   siparis-okuma   every platform's new-order and recovery reads (priority 0 and above)
//   durum-taramasi  every platform's open-order status scans (priority 0 and above)
//   iade            return reads (priority 0 and above)
//   katalog         product catalogue reads and link checks (priority 0 and above)
//   acil            any other job of priority 0..hot ceiling (reads a user started, the read-back of uncertain writes)
//   arka-plan       everything else above the hot ceiling (reconciliations, reference lists, finance, questions)
// The ids are Turkish and differ from the earlier lanes' ids on purpose: job_attempts.Lane holds both kinds around a switch.
// Each lane's slots (DispatcherSettings): Reserved slots only that lane ever uses; a Share of the shared slots the lane is
// served first on when it has work (lent while it is idle). WorkerLane.DefaultSlots is the two together.
public static class DispatcherLanes
{
    public const string User = "kullanici";
    public const string Bulk = "toplu";
    public const string Webhooks = "webhook";
    public const string Writes = "yazma";
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

    // Open-order status scans, every platform. They share the "orders" lock group with the order reads.
    public static readonly IReadOnlyList<string> StatusScanJobTypes =
    [
        MarketplaceJobTypes.OrderStatusSync, MarketplaceJobTypes.ShopifyOrderStatusSync, MarketplaceJobTypes.HepsiburadaOrderStatusSync,
        MarketplaceJobTypes.PazaramaOrderStatusSync, MarketplaceJobTypes.PttAvmOrderStatusSync
    ];

    public static readonly IReadOnlyList<string> ReturnReadJobTypes = WorkerLanes.ReturnReadJobTypes;

    public static readonly IReadOnlyList<string> CatalogJobTypes = [.. JobTypeRegistry.ProductCatalogSyncJobTypes, .. JobTypeRegistry.ListingVerificationJobTypes];

    public static readonly IReadOnlyList<string> WebhookJobTypes = [MarketplaceJobTypes.WebhookIngest, MarketplaceJobTypes.ShopifyWebhookIngest, MarketplaceJobTypes.HepsiburadaWebhookIngest];

    public static readonly IReadOnlyList<string> WriteJobTypes = JobTypeRegistry.WritingJobTypes;

    public static readonly IReadOnlyList<string> ImportJobTypes = ["IMPORT_PREVIEW", "IMPORT_APPLY"];

    // Only the new-order reads (no recovery read): the ones a status scan or a reconciliation of the store defers to.
    public static readonly IReadOnlyList<string> NewOrderReadJobTypes =
    [
        MarketplaceJobTypes.OrderSync, MarketplaceJobTypes.ShopifyOrderSync, MarketplaceJobTypes.HepsiburadaOrderSync,
        MarketplaceJobTypes.N11OrderSync, MarketplaceJobTypes.PazaramaOrderSync, MarketplaceJobTypes.PttAvmOrderSync
    ];

    // Second correctness review (28 Nov): priority UserAction (-3) is not only a single click. These types run at -3 without a
    // user waiting on that one job, so the dispatcher leases them outside kullanici (their priority stays as it is; the
    // earlier lane models are not touched):
    //   to yazma: the price and stock write (a bulk price publish queues one per product, hundreds at once) and the
    //             automatic read-backs (invoice status, the package invoice check, the read-back of an uncertain marketplace
    //             write): short calls to a marketplace or the invoice provider
    //   to toplu: the factory reset and a connection's full reset (long runs)
    public static readonly IReadOnlyList<string> UserPriorityWriteJobTypes =
    [
        MarketplaceJobTypes.PriceInventorySync, InvoicingJobTypes.InvoiceReconcile, MarketplaceJobTypes.OrderInvoiceReconciliation,
        MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation, MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation,
        MarketplaceJobTypes.ExternalEffectReconcile
    ];

    public static readonly IReadOnlyList<string> UserPriorityBulkJobTypes = [PanelJobTypes.FactoryReset, MarketplaceJobTypes.ConnectionFullReset];

    // The types the typed lanes take at priority 0 and above: never acil or arka-plan, whatever their priority.
    public static readonly IReadOnlyList<string> TypedLaneJobTypes =
        [.. ImportJobTypes, .. WebhookJobTypes, .. WriteJobTypes, .. OrderReadJobTypes, .. StatusScanJobTypes, .. ReturnReadJobTypes, .. CatalogJobTypes];

    // In display order, the order a free shared slot beyond every share is offered in when two lanes are equally far beyond
    // their own slots (DispatcherCore).
    public static IReadOnlyList<WorkerLane> All(int hotPriorityCeiling = WorkerLanes.DefaultHotPriorityCeiling, DispatcherSettings? settings = null)
    {
        settings ??= DispatcherSettings.Default;
        return
        [
            new(User, settings.SlotsOf(User), JobPriorityPolicy.UserAction, null, [], [.. UserPriorityWriteJobTypes, .. UserPriorityBulkJobTypes]),
            new(Webhooks, settings.SlotsOf(Webhooks), null, 0, WebhookJobTypes, []),
            new(Writes, settings.SlotsOf(Writes), null, 0, WriteJobTypes, ImportJobTypes),
            new(Urgent, settings.SlotsOf(Urgent), hotPriorityCeiling, 0, [], TypedLaneJobTypes),
            new(OrderReads, settings.SlotsOf(OrderReads), null, 0, OrderReadJobTypes, []),
            new(StatusScans, settings.SlotsOf(StatusScans), null, 0, StatusScanJobTypes, []),
            new(Returns, settings.SlotsOf(Returns), null, 0, ReturnReadJobTypes, []),
            new(Bulk, settings.SlotsOf(Bulk), JobPriorityPolicy.BulkUserAction, JobPriorityPolicy.BulkUserAction, [], []),
            new(Catalog, settings.SlotsOf(Catalog), null, 0, CatalogJobTypes, []),
            new(Background, settings.SlotsOf(Background), null, hotPriorityCeiling + 1, [], TypedLaneJobTypes),
            new(InitialSync, settings.SlotsOf(InitialSync), JobPriorityPolicy.InitialDataSync, JobPriorityPolicy.InitialDataSync, [], [])
        ];
    }

    public static IReadOnlyList<string> Names { get; } = [User, Webhooks, Writes, Urgent, OrderReads, StatusScans, Returns, Bulk, Catalog, Background, InitialSync];

    public static bool IsDispatcherLane(string? lane) => lane is not null && Names.Contains(lane, StringComparer.Ordinal);

    // The lanes of bulk work: beyond their share they get a shared slot only after every other lane with work.
    public static bool IsBulk(string lane) => lane is Bulk or Catalog or Background or InitialSync;

    // The one lane that leases a job of this type and priority (the lanes are disjoint). The file imports of priority 0 and
    // above go to toplu; the lane definition above lists that as two filters (All: toplu takes -2; ImportLane below).
    public static string LaneFor(string jobType, int priority, int hotPriorityCeiling = WorkerLanes.DefaultHotPriorityCeiling)
    {
        if (priority <= JobPriorityPolicy.UserAction)
            return UserPriorityWriteJobTypes.Contains(jobType) ? Writes : UserPriorityBulkJobTypes.Contains(jobType) ? Bulk : User;
        if (priority == JobPriorityPolicy.BulkUserAction) return Bulk;
        if (priority < 0) return InitialSync;
        if (ImportJobTypes.Contains(jobType)) return Bulk;
        if (WebhookJobTypes.Contains(jobType)) return Webhooks;
        if (WriteJobTypes.Contains(jobType)) return Writes;
        if (OrderReadJobTypes.Contains(jobType)) return OrderReads;
        if (StatusScanJobTypes.Contains(jobType)) return StatusScans;
        if (ReturnReadJobTypes.Contains(jobType)) return Returns;
        if (CatalogJobTypes.Contains(jobType)) return Catalog;
        return priority <= hotPriorityCeiling ? Urgent : Background;
    }

    // The filters a lane's lease passes. The lease query takes one priority range, so a lane taking jobs of two ranges leases
    // them with more filters: toplu (bulk user work at -2, file imports at 0 and above, the resets at -3), yazma (writes at 0
    // and above, the -3 price writes and read-backs).
    public static IReadOnlyList<WorkerLane> LeaseFilters(WorkerLane lane) => lane.Name switch
    {
        Bulk => [lane, new WorkerLane(Bulk, lane.DefaultSlots, null, 0, ImportJobTypes, []), new WorkerLane(Bulk, lane.DefaultSlots, JobPriorityPolicy.UserAction, null, UserPriorityBulkJobTypes, [])],
        Writes => [lane, new WorkerLane(Writes, lane.DefaultSlots, JobPriorityPolicy.UserAction, null, UserPriorityWriteJobTypes, [])],
        _ => [lane]
    };

    // Whether the lane takes the job (every filter of the lane).
    public static bool Takes(WorkerLane lane, string jobType, int priority) => LeaseFilters(lane).Any(filter => filter.Takes(jobType, priority));

    // The per-store cap as the lease query applies it: the jobs of the capped lanes count and wait (DispatcherSettings.CappedLanes).
    public static JobConnectionCap ConnectionCap(DispatcherSettings settings, int hotPriorityCeiling = WorkerLanes.DefaultHotPriorityCeiling) =>
        new(settings.ConnectionCap, [.. All(hotPriorityCeiling, settings).Where(lane => settings.IsCapped(lane.Name)).SelectMany(LeaseFilters)
            .Select(lane => new JobConnectionCap.Rule(lane.MinimumPriority, lane.MaximumPriority, lane.IncludedJobTypes, lane.ExcludedJobTypes))]);

    // Status scans and background reads wait while their store has a runnable order read in their lock group ("orders": the
    // status scan, the order reconciliations, the invoice and cargo checks): a store's new orders are read first (review of
    // 27 Nov: a status scan or a reconciliation took the lock just before the store's order read, which then waited all of it).
    public static readonly IReadOnlyList<string> DeferringLanes = [StatusScans, Background];

    // Second correctness review (28 Nov): only a new-order read the lease could take right now defers them: of priority
    // 0..hot ceiling (the quota hold never holds that range, a first-import read at -1 never counts), runnable, not backed off,
    // not held by the store cap (JobDeferral; the lease and the census apply the same predicate). A recovery read (priority 6,
    // quota-held for up to 15 min) or a store's first import no longer holds its status scans.
    public static JobDeferral? DeferralFor(string lane, DispatcherSettings? settings = null, int hotPriorityCeiling = WorkerLanes.DefaultHotPriorityCeiling) =>
        DeferringLanes.Contains(lane) && (settings ?? DispatcherSettings.Default).DeferStatusScans ? new JobDeferral(NewOrderReadJobTypes, 0, hotPriorityCeiling) : null;

    // The filters of the deferring lanes, for the census.
    public static IReadOnlyList<JobConnectionCap.Rule> DeferredRules(DispatcherSettings settings, int hotPriorityCeiling = WorkerLanes.DefaultHotPriorityCeiling) =>
        settings.DeferStatusScans
            ? [.. All(hotPriorityCeiling, settings).Where(lane => DeferringLanes.Contains(lane.Name)).Select(lane => new JobConnectionCap.Rule(lane.MinimumPriority, lane.MaximumPriority, lane.IncludedJobTypes, lane.ExcludedJobTypes))]
            : [];

    public static string DisplayName(string lane) => lane switch
    {
        User => "Kullanıcı",
        Bulk => "Toplu İşler",
        Webhooks => "Webhook",
        Writes => "Pazaryerine Yazma",
        Urgent => "Elle Başlatılan Okuma",
        OrderReads => "Sipariş Okuma",
        StatusScans => "Durum Taraması",
        Returns => "İade",
        Catalog => "Katalog",
        Background => "Arka Plan",
        InitialSync => "İlk Çekim",
        _ => lane
    };
}

// Queue phase 2, the dispatcher's slots, read the same way by the worker (DispatcherCore) and the api (the lane table). The
// defaults (20 slots, the database pool needs 2 x total + 10, WorkerLanes.RequiredPoolSize):
//   reserved  kullanici 2, webhook 1, yazma 1, siparis-okuma 3, durum-taramasi 2      (9, never lent)
//   share     yazma 1, iade 2, toplu 2, katalog 2, arka-plan 2, ilk-cekim 2            (11 shared slots)
// Second correctness review (28 Nov): kullanici's second reserved slot came from the shared ones. Taking it from yazma's
// reservation instead (yazma 0 + 2) kept the startup burst as before, but then writes waited behind long background reads
// (QueueReplayTests.ASinglePackageAction...: yazma p50 ~900 s against ~500 s today); the idle second kullanici slot costs
// the startup burst a little instead (status scans p95 44 -> 63 s, target 300 s; LaneTableVariants).
//   acil      no slot of its own: a read a user started takes a free shared slot
// Beyond its share a lane of bulk work (toplu, katalog, arka-plan, ilk-cekim) gets a free shared slot only after every lane
// of short work (DispatcherCore.Decide). Why these figures: docs/ISLEYIS-KILAVUZU.md (Şerit modeli ve dağıtıcı).
// Settings:
//   Worker:Dispatcher:Reserved:<lane>   slots only the lane uses, never lent (0..8)
//   Worker:Dispatcher:Share:<lane>      the lane's share of the shared slots (0..8): served first on that many shared slots while
//                                       it has work, lent while idle
//   Worker:Dispatcher:ConnectionCap     at most this many running jobs of one store in the capped lanes (default 3, 0 = off)
//   Worker:Dispatcher:CappedLanes       the lanes the cap counts and holds (comma list; default katalog,arka-plan,ilk-cekim: the
//                                       bulk reads one store can queue by the dozen). Never kullanici, toplu, webhook or yazma.
//                                       siparis-okuma, durum-taramasi and iade are not capped by default: a store runs at most
//                                       one of each at a time anyway (lock groups "orders" and "returns"), and capping them
//                                       made a store's new-order reads wait behind its own catalogue and background reads
//                                       (QueueReplayTests.CapVariants)
//   Worker:Dispatcher:SafetyTickSeconds how often the dispatcher reads which lanes have runnable jobs even if no notification
//                                       came (default 15, 5..60); 5 s while a kullanici, webhook or yazma job waits for a blocker
//   Worker:Dispatcher:LeaseTimeoutSeconds a lease or census that takes longer is cancelled and its lane backs off (default 10)
public sealed record DispatcherSettings(IReadOnlyDictionary<string, int> Reserved, IReadOnlyDictionary<string, int> Share, int ConnectionCap, TimeSpan SafetyTick, IReadOnlyList<string> CappedLanes)
{
    public const string DefaultCappedLanes = DispatcherLanes.Catalog + "," + DispatcherLanes.Background + "," + DispatcherLanes.InitialSync;

    public TimeSpan LeaseTimeout { get; init; } = DefaultLeaseTimeout;
    // Worker:Dispatcher:DeferStatusScans (default true): a status scan or background job waits while its store has an order read
    // queued in its lock group (DeferralFor).
    public bool DeferStatusScans { get; init; } = true;
    public static readonly TimeSpan DefaultLeaseTimeout = TimeSpan.FromSeconds(10);

    // The lanes whose waits are held to 5 s targets: while one of them waits for a blocker the census is read every 5 s.
    public static readonly IReadOnlyList<string> ShortTickLanes = [DispatcherLanes.User, DispatcherLanes.Webhooks, DispatcherLanes.Writes];
    public static readonly TimeSpan ShortSafetyTick = TimeSpan.FromSeconds(5);

    private static IReadOnlyList<string> ParseCappedLanes(string? value) =>
        [.. (value ?? DefaultCappedLanes).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(lane => DispatcherLanes.Names.Contains(lane, StringComparer.Ordinal) && lane is not (DispatcherLanes.User or DispatcherLanes.Bulk or DispatcherLanes.Webhooks or DispatcherLanes.Writes))
            .Distinct(StringComparer.Ordinal)];

    // Whether the cap holds the lane's jobs (its lease passes JobConnectionCap).
    public bool IsCapped(string lane) => ConnectionCap > 0 && CappedLanes.Contains(lane, StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, int> DefaultReserved = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [DispatcherLanes.User] = 2,
        [DispatcherLanes.Webhooks] = 1,
        [DispatcherLanes.Writes] = 1,
        [DispatcherLanes.OrderReads] = 3,
        [DispatcherLanes.StatusScans] = 2
    };

    public static readonly IReadOnlyDictionary<string, int> DefaultShare = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [DispatcherLanes.Bulk] = 2,
        [DispatcherLanes.Writes] = 1,
        [DispatcherLanes.Returns] = 2,
        [DispatcherLanes.Catalog] = 2,
        [DispatcherLanes.Background] = 2,
        [DispatcherLanes.InitialSync] = 2
    };

    public const int DefaultConnectionCap = 3;
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
            ParseCappedLanes(configuration["Worker:Dispatcher:CappedLanes"]))
        {
            LeaseTimeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Worker:Dispatcher:LeaseTimeoutSeconds", (int)DefaultLeaseTimeout.TotalSeconds), 2, 60)),
            DeferStatusScans = configuration.GetValue("Worker:Dispatcher:DeferStatusScans", true)
        };
    }

    // A copy with other cap settings (the replay compares them).
    public DispatcherSettings WithCap(int cap, IReadOnlyList<string> cappedLanes) => this with { ConnectionCap = cap, CappedLanes = cappedLanes };

    public int ReservedOf(string lane) => Reserved.GetValueOrDefault(lane);
    public int ShareOf(string lane) => Share.GetValueOrDefault(lane);
    public int SlotsOf(string lane) => ReservedOf(lane) + ShareOf(lane);
    public int Total => DispatcherLanes.Names.Sum(SlotsOf);
}
