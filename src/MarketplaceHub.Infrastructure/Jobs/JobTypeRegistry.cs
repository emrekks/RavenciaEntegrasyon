using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Jobs;

// How a job type runs, in one place: the execution-lock group it shares with other jobs of a connection,
// its default queue priority, how long it may run (and which setting changes that), whether a timeout
// also stops its lease heartbeat, and how soon it is retried after a timeout. The worker, the lock and
// the producers read these instead of keeping their own lists.
public sealed record JobTimeout(string SettingKey, string? FallbackSettingKey, double DefaultMinutes, double MinimumMinutes, double MaximumMinutes);

public sealed record JobTypeProfile(string JobType, string LockGroup, int Priority, JobTimeout Timeout, bool StopHeartbeatOnTimeout, TimeSpan? RetryAfterTimeout);

public static class JobTypeRegistry
{
    public static readonly IReadOnlyList<string> OrderReadSyncJobTypes =
    [
        MarketplaceJobTypes.OrderSync, MarketplaceJobTypes.OrderRecoverySync, MarketplaceJobTypes.OrderStatusSync,
        MarketplaceJobTypes.OrderReconciliation, MarketplaceJobTypes.OrderInvoiceReconciliation, MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation,
        MarketplaceJobTypes.ShopifyOrderSync, MarketplaceJobTypes.ShopifyOrderRecoverySync, MarketplaceJobTypes.ShopifyOrderStatusSync,
        MarketplaceJobTypes.ShopifyOrderReconciliation, MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation,
        MarketplaceJobTypes.HepsiburadaOrderSync, MarketplaceJobTypes.HepsiburadaOrderRecoverySync,
        MarketplaceJobTypes.HepsiburadaOrderStatusSync, MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation,
        MarketplaceJobTypes.N11OrderSync, MarketplaceJobTypes.N11OrderRecoverySync,
        // Pazarama order reads used to fall back to the 60-minute default ceiling.
        MarketplaceJobTypes.PazaramaOrderSync, MarketplaceJobTypes.PazaramaOrderRecoverySync, MarketplaceJobTypes.PazaramaOrderStatusSync
    ];

    public static readonly IReadOnlyList<string> ProductCatalogSyncJobTypes =
        [MarketplaceJobTypes.ProductSync, MarketplaceJobTypes.ShopifyProductSync, MarketplaceJobTypes.HepsiburadaProductSync, MarketplaceJobTypes.CatalogStructureImport];

    public static readonly IReadOnlyList<string> OrderInvoiceReconciliationJobTypes =
        [MarketplaceJobTypes.OrderInvoiceReconciliation, MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation, MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation];

    public static bool IsOrderReadSync(string jobType) => OrderReadSyncJobTypes.Contains(jobType);
    public static bool IsProductCatalogSync(string jobType) => ProductCatalogSyncJobTypes.Contains(jobType);
    public static bool IsOrderInvoiceReconciliation(string jobType) => OrderInvoiceReconciliationJobTypes.Contains(jobType);

    public static JobTypeProfile For(string jobType) => new(
        jobType,
        MarketplaceSyncExecutionLock.GroupFor(jobType),
        JobPriorityPolicy.For(jobType, userInitiated: false),
        TimeoutOf(jobType),
        // An invoice check that ran out of time must not keep its lease alive while it unwinds.
        StopHeartbeatOnTimeout: IsOrderInvoiceReconciliation(jobType),
        // A timed-out order read waits a little before trying again instead of piling onto a busy quota.
        RetryAfterTimeout: IsOrderReadSync(jobType) ? TimeSpan.FromMinutes(3) : null);

    public static TimeSpan TimeoutFor(string jobType, IConfiguration configuration)
    {
        var timeout = TimeoutOf(jobType);
        var minutes = configuration.GetValue<double?>(timeout.SettingKey)
            ?? (timeout.FallbackSettingKey is { } fallback ? configuration.GetValue<double?>(fallback) : null)
            ?? timeout.DefaultMinutes;
        return TimeSpan.FromMinutes(Math.Clamp(minutes, timeout.MinimumMinutes, timeout.MaximumMinutes));
    }

    private static JobTimeout TimeoutOf(string jobType)
    {
        if (IsProductCatalogSync(jobType)) return new("Worker:ProductSyncTimeoutMinutes", null, 60, 1, 180);
        if (IsOrderInvoiceReconciliation(jobType)) return new("Worker:OrderInvoiceReconciliationTimeoutMinutes", null, 15, 1, 30);
        if (jobType == MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation) return new("Worker:TrendyolCargoInfoTimeoutMinutes", null, 30, 1, 60);
        if (jobType == MarketplaceJobTypes.OrderStatusSync) return new("Worker:TrendyolOrderStatusTimeoutMinutes", null, 20, 1, 60);
        if (IsOrderReadSync(jobType)) return new("Worker:OrderReadSyncTimeoutMinutes", "Worker:OrderStatusSyncTimeoutMinutes", 10, 1, 60);
        // Every other job type gets a generous ceiling so a hung call cannot hold a lane forever.
        return new("Worker:DefaultJobTimeoutMinutes", null, 60, 5, 180);
    }
}
