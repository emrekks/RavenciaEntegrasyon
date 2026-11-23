using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Infrastructure.Persistence.InvoiceLabelPolicy;
using static MarketplaceHub.Infrastructure.Persistence.OrderSnapshotReader;
using static MarketplaceHub.Infrastructure.Persistence.SalesResults;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class OrderQueryService
{
    // The tabs the order summary counts; "ALL" is the list without a status tab.
    internal static readonly string[] OrderSummaryTabs = ["ALL", "NEW", "PROCESSING", "SHIPPED", "DELIVERED", "RESENT", "ON_HOLD", "CANCELLED", "RETURNED", "RETURN_IN_TRANSIT", "PARTIALLY_CANCELLED", "MANUAL_REVIEW", "PENDING", "UNVERIFIED", "PAYMENT_PENDING", "UNDELIVERED", "LATE"];

    public Task<OrderSummaryView> OrderSummaryAsync(Guid tenantId, string? platform, CancellationToken cancellationToken) =>
        OrderSummaryAsync(tenantId, new OrderListQuery(Platform: platform), cancellationToken);

    // A cached answer per tenant, filters and tab set. Every open panel page polls the menu counter; without
    // this, identical counts ran side by side and their memory took the database over its limit (live
    // 20 Oct 2026: PostgreSQL killed by the out-of-memory killer while several 15-tab counts ran).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, string Stamp, OrderSummaryView View)> OrderSummaryCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> OrderSummaryGates = new();
    // An answer is reused while no order or package of the tenant changed (a cheap stamp read), at most this long.
    // The full count takes seconds on live data (8 s on 20 Oct 2026), so it runs again only after a change.
    internal static readonly TimeSpan OrderSummaryCacheLifetime = TimeSpan.FromMinutes(5);

    // Each counter is the number of distinct orders its tab lists for the same filters (date,
    // search, platform, cargo, invoice, label): the tab query itself, counted once per tab in a
    // single round trip. Only the requested tabs are counted.
    public async Task<OrderSummaryView> OrderSummaryAsync(Guid tenantId, OrderListQuery options, CancellationToken cancellationToken, IReadOnlyCollection<string>? tabs = null, bool allowCached = false)
    {
        var requested = tabs is null ? OrderSummaryTabs : OrderSummaryTabs.Where(tab => tabs.Contains(tab, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (requested.Length == 0) requested = OrderSummaryTabs;
        // The micro-export count and its countries are read only by the card, which asks for one tab by name over the
        // micro-export orders (OrderMicroExportRules): the pending ones (PENDING). The tab counters skip them.
        var includeMicro = options.MicroExport == true && tabs is not null && requested.Length == 1;
        if (!allowCached) return await CountOrderSummaryAsync(tenantId, options, requested, includeMicro, cancellationToken);
        // SIPARIS-STOK-4: the Geciken tab moves with the clock (an order passes its ship-by time with no change to stamp),
        // so it is counted fresh on every call; the other tabs keep their cached answer.
        if (requested.Contains("LATE"))
        {
            if (requested.Length == 1) return await CountOrderSummaryAsync(tenantId, options, requested, includeMicro, cancellationToken);
            var others = await CachedOrderSummaryAsync(tenantId, options, requested.Where(tab => tab != "LATE").ToArray(), includeMicro: false, cancellationToken);
            var late = await CountOrderSummaryAsync(tenantId, options, ["LATE"], includeMicro: false, cancellationToken);
            return others with { Late = late.Late, LateByConnection = late.LateByConnection };
        }
        return await CachedOrderSummaryAsync(tenantId, options, requested, includeMicro, cancellationToken);
    }

    private async Task<OrderSummaryView> CachedOrderSummaryAsync(Guid tenantId, OrderListQuery options, string[] requested, bool includeMicro, CancellationToken cancellationToken)
    {
        var key = $"{tenantId:N}|{string.Join(',', requested)}|{JsonSerializer.Serialize(options)}";
        var stamp = await OrderSummaryStampAsync(tenantId, cancellationToken);
        if (OrderSummaryCache.TryGetValue(key, out var cached) && cached.Stamp == stamp && DateTimeOffset.UtcNow - cached.At < OrderSummaryCacheLifetime) return cached.View;
        var gate = OrderSummaryGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // A request that waited behind an identical one takes its answer.
            if (OrderSummaryCache.TryGetValue(key, out cached) && cached.Stamp == stamp && DateTimeOffset.UtcNow - cached.At < OrderSummaryCacheLifetime) return cached.View;
            var view = await CountOrderSummaryAsync(tenantId, options, requested, includeMicro, cancellationToken);
            if (OrderSummaryCache.Count > 1000) OrderSummaryCache.Clear();
            OrderSummaryCache[key] = (DateTimeOffset.UtcNow, stamp, view);
            return view;
        }
        finally { gate.Release(); }
    }

    // Changes of the tenant's orders (a label print too: it sets LabelPrintedAt without touching UpdatedAt), packages,
    // return claims, invoices and their deliveries, or connections: any of them can move a counter (SIPARIS-STOK-4).
    private async Task<string> OrderSummaryStampAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var orders = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId).GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Changed = group.Max(x => (DateTimeOffset?)x.UpdatedAt), LabelPrinted = group.Max(x => x.LabelPrintedAt) }).SingleOrDefaultAsync(cancellationToken);
        var packages = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId).GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Changed = group.Max(x => (DateTimeOffset?)x.UpdatedAt) }).SingleOrDefaultAsync(cancellationToken);
        var claims = await db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId).MaxAsync(x => (DateTimeOffset?)x.UpdatedAt, cancellationToken);
        var invoices = await db.Invoices.AsNoTracking().Where(x => x.TenantId == tenantId).GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Changed = group.Max(x => (DateTimeOffset?)x.UpdatedAt), Versions = group.Sum(x => x.Version) }).SingleOrDefaultAsync(cancellationToken);
        var deliveryStates = await db.MarketplaceDeliveryStates.AsNoTracking().Where(x => x.TenantId == tenantId).GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Changed = group.Max(x => (DateTimeOffset?)x.UpdatedAt) }).SingleOrDefaultAsync(cancellationToken);
        var deliveries = await db.MarketplaceDeliveries.AsNoTracking().Where(x => x.TenantId == tenantId).GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Completed = group.Max(x => x.CompletedAt) }).SingleOrDefaultAsync(cancellationToken);
        var connections = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId).SumAsync(x => (long)x.Version, cancellationToken);
        return $"{orders?.Count}|{orders?.Changed:O}|{orders?.LabelPrinted:O}|{packages?.Count}|{packages?.Changed:O}|{claims:O}"
            + $"|{invoices?.Count}|{invoices?.Changed:O}|{invoices?.Versions}|{deliveryStates?.Count}|{deliveryStates?.Changed:O}|{deliveries?.Count}|{deliveries?.Completed:O}"
            + $"|{connections}|{timeProvider.GetUtcNow():yyyyMMddHH}"
            + await ManualResendStampAsync(tenantId, cancellationToken);
    }

    // The hand-added re-shipments move the Yeniden Gönderimler counter (added, removed, marked shipped).
    private async Task<string> ManualResendStampAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var manual = await db.ManualResends.AsNoTracking().Where(x => x.TenantId == tenantId).GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Changed = group.Max(x => (DateTimeOffset?)x.UpdatedAt), Versions = group.Sum(x => x.Version) }).SingleOrDefaultAsync(cancellationToken);
        return $"|{manual?.Count}|{manual?.Changed:O}|{manual?.Versions}";
    }

    private async Task<OrderSummaryView> CountOrderSummaryAsync(Guid tenantId, OrderListQuery options, string[] requested, bool includeMicro, CancellationToken cancellationToken)
    {
        IQueryable<string>? tagged = null;
        foreach (var tab in requested)
        {
            var tabName = tab;
            var rows = FilteredOrders(tenantId, options with { Status = tab, Sort = null }).Select(_ => tabName);
            tagged = tagged is null ? rows : tagged.Concat(rows);
        }
        var counts = await tagged!.GroupBy(tab => tab).Select(group => new { Tab = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Tab, row => row.Count, cancellationToken);
        int Count(string tab) => counts.GetValueOrDefault(tab);
        // The micro-export card counts them in the one tab it asked for, under the same filters.
        var (micro, microCountries) = includeMicro ? await MicroExportCountsAsync(tenantId, options with { Status = requested[0] }, cancellationToken) : (0, null);
        var newUnits = !includeMicro && requested.Contains("NEW") ? await TabUnitsByConnectionAsync(tenantId, options, "NEW", cancellationToken) : null;
        var processingUnits = !includeMicro && requested.Contains("PROCESSING") ? await TabUnitsByConnectionAsync(tenantId, options, "PROCESSING", cancellationToken) : null;
        // The Geciken card's stores (the late orders per store), and the Yeniden Gönderim card's, which asks for the RESENT
        // tab over the resends not handed to the carrier (resentNotShipped=true); the tab counters skip them otherwise.
        var lateStores = !includeMicro && requested.Contains("LATE") && Count("LATE") > 0 ? await TabOrdersByConnectionAsync(tenantId, options, "LATE", cancellationToken) : null;
        var resentStores = !includeMicro && options.ResentNotShipped == true && requested.Contains("RESENT") ? await TabOrdersByConnectionAsync(tenantId, options, "RESENT", cancellationToken) : null;
        // Of those, the exchanges whose package is not known to be handed to the carrier (the card's hover says what it counts).
        var resentExchangeStores = resentStores is null ? null : await FilteredOrders(tenantId, options with { Status = "RESENT", Sort = null })
            .Where(order => db.ReturnClaims.Any(claim => claim.TenantId == order.TenantId && claim.OrderId == order.Id && claim.Status != ReturnClaimStatus.Cancelled
                && claim.CustomerRequest == ReturnCustomerRequestPolicy.Change
                && (claim.ReplacementPackageId != null || claim.ResolvedWith == ReturnCustomerRequestPolicy.Change)
                && claim.ReplacementCargoTrackingLink == null && claim.ReplacementShippedAt == null))
            .GroupBy(order => order.ConnectionId).Select(group => new { ConnectionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.ConnectionId.ToString("D"), row => row.Count, cancellationToken);
        return new OrderSummaryView(Count("ALL"), Count("NEW"), Count("PROCESSING"), Count("SHIPPED"), Count("DELIVERED"), Count("RESENT"), Count("ON_HOLD"),
            Count("CANCELLED"), Count("RETURNED"), Count("RETURN_IN_TRANSIT"), Count("PARTIALLY_CANCELLED"), Count("MANUAL_REVIEW"), Count("PENDING"), Count("UNVERIFIED"), Count("PAYMENT_PENDING"), Count("UNDELIVERED"), Count("LATE"), micro, microCountries, newUnits, processingUnits,
            lateStores ?? (requested.Contains("LATE") && !includeMicro ? new Dictionary<string, int>() : null), resentStores,
            !includeMicro && requested.Contains("NEW") ? await TabByCargoAsync(tenantId, options, "NEW", cancellationToken) : null,
            !includeMicro && requested.Contains("PROCESSING") ? await TabByCargoAsync(tenantId, options, "PROCESSING", cancellationToken) : null,
            !includeMicro && requested.Contains("NEW") ? await TabOrdersByConnectionAsync(tenantId, options, "NEW", cancellationToken) : null,
            !includeMicro && requested.Contains("PROCESSING") ? await TabOrdersByConnectionAsync(tenantId, options, "PROCESSING", cancellationToken) : null,
            !includeMicro && requested.Contains("PENDING") ? await TabOrdersByConnectionAsync(tenantId, options, "PENDING", cancellationToken) : null,
            !includeMicro && requested.Contains("PENDING") ? await TabUnitsByConnectionAsync(tenantId, options, "PENDING", cancellationToken) : null,
            resentExchangeStores);
    }


    // The orders a tab lists under the same filters by the cargo company of their current package, with their products.
    private async Task<IReadOnlyList<OrderCargoCountView>> TabByCargoAsync(Guid tenantId, OrderListQuery options, string tab, CancellationToken cancellationToken)
    {
        var orders = FilteredOrders(tenantId, options with { Status = tab, Sort = null });
        var pairs = await CurrentShipmentPackages()
            .Where(package => package.TenantId == tenantId && orders.Any(order => order.Id == package.OrderId))
            .Select(package => new { package.OrderId, Cargo = package.CargoProviderExternalId ?? "" })
            .Distinct().ToListAsync(cancellationToken);
        var units = await (from order in orders
                           join line in db.OrderLines.AsNoTracking() on new { order.TenantId, OrderId = order.Id } equals new { line.TenantId, line.OrderId }
                           group line by order.Id into grouped
                           select new { OrderId = grouped.Key, Units = grouped.Sum(line => line.OrderedQuantity > line.CancelledQuantity ? line.OrderedQuantity - line.CancelledQuantity : 0m) })
            .ToDictionaryAsync(x => x.OrderId, x => x.Units, cancellationToken);
        var known = pairs.Select(x => x.OrderId).ToHashSet();
        var rows = pairs.Select(x => (x.OrderId, Cargo: x.Cargo.Trim()))
            .Concat(units.Keys.Where(id => !known.Contains(id)).Select(id => (OrderId: id, Cargo: "")));
        return rows.GroupBy(x => x.Cargo)
            .Select(group => { var ids = group.Select(x => x.OrderId).Distinct().ToList(); return new OrderCargoCountView(group.Key, ids.Count, ids.Sum(id => units.GetValueOrDefault(id))); })
            .OrderByDescending(x => x.Orders).ThenBy(x => x.Cargo, StringComparer.Ordinal).ToList();
    }
    // The orders a tab lists under the same filters, per store.
    private async Task<IReadOnlyDictionary<string, int>> TabOrdersByConnectionAsync(Guid tenantId, OrderListQuery options, string tab, CancellationToken cancellationToken) =>
        await FilteredOrders(tenantId, options with { Status = tab, Sort = null })
            .GroupBy(order => order.ConnectionId).Select(group => new { ConnectionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.ConnectionId.ToString("D"), row => row.Count, cancellationToken);

    // The products of the orders a tab (Yeni, İşleme Alınanlar, Bekleyen) lists under the same filters, per store: each line's
    // ordered quantity less what was cancelled of it (the rule of the overview's Bekleyen Ürün Adedi).
    private async Task<IReadOnlyDictionary<string, decimal>> TabUnitsByConnectionAsync(Guid tenantId, OrderListQuery options, string tab, CancellationToken cancellationToken)
    {
        var orders = FilteredOrders(tenantId, options with { Status = tab, Sort = null });
        var rows = await (
            from order in orders
            join line in db.OrderLines.AsNoTracking() on new { order.TenantId, OrderId = order.Id } equals new { line.TenantId, line.OrderId }
            group line by order.ConnectionId into grouped
            select new { ConnectionId = grouped.Key, Units = grouped.Sum(line => line.OrderedQuantity > line.CancelledQuantity ? line.OrderedQuantity - line.CancelledQuantity : 0m) })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.ConnectionId.ToString("D"), row => row.Units);
    }

    private async Task<(int Count, IReadOnlyDictionary<string, int> Countries)> MicroExportCountsAsync(Guid tenantId, OrderListQuery options, CancellationToken cancellationToken)
    {
        var rows = await FilteredOrders(tenantId, options with { Sort = null, MicroExport = true })
            .Select(order => new { order.InvoiceAddressSnapshotJson, order.StoreFrontCode }).ToListAsync(cancellationToken);
        return (rows.Count, MicroExportCountryCounts(rows.Select(row => (row.InvoiceAddressSnapshotJson, row.StoreFrontCode))));
    }

    // Micro-export orders per country, the country the row's flag shows (the invoice address's, else the storefront's).
    // An order whose country is not known (a domestic address and storefront) is listed as "".
    internal static IReadOnlyDictionary<string, int> MicroExportCountryCounts(IEnumerable<(string InvoiceAddressJson, string? StoreFrontCode)> orders) => orders
        .GroupBy(order => MicroExportCountry(order.InvoiceAddressJson, order.StoreFrontCode) ?? "")
        .ToDictionary(group => group.Key, group => group.Count());

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, string Stamp, OrderFacetsView View)> OrderFacetsCache = new();

    // The orders each platform and cargo choice would list: the list's filters, status tab included, without the
    // column's own choice. A cargo counts the orders with a current package of that provider, as its filter does.
    public async Task<OrderFacetsView> OrderFacetsAsync(Guid tenantId, OrderListQuery options, CancellationToken cancellationToken)
    {
        var key = $"{tenantId:N}|{JsonSerializer.Serialize(options with { Sort = null })}";
        var stamp = await OrderSummaryStampAsync(tenantId, cancellationToken);
        if (OrderFacetsCache.TryGetValue(key, out var cached) && cached.Stamp == stamp && DateTimeOffset.UtcNow - cached.At < OrderSummaryCacheLifetime) return cached.View;

        var byPlatform = FilteredOrders(tenantId, options with { Platform = null, Platforms = null, Sort = null });
        var platforms = await byPlatform
            .Join(db.PlatformConnections.AsNoTracking().Where(connection => connection.TenantId == tenantId), order => order.ConnectionId, connection => connection.Id, (_, connection) => connection.PlatformCode)
            .GroupBy(code => code).Select(group => new { Code = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Code, row => row.Count, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var byCargo = FilteredOrders(tenantId, options with { Cargo = null, Sort = null });
        var cargos = await CurrentShipmentPackages()
            .Where(package => package.TenantId == tenantId && package.CargoProviderExternalId != null && package.CargoProviderExternalId != ""
                && byCargo.Any(order => order.Id == package.OrderId))
            .Select(package => new { package.OrderId, Cargo = package.CargoProviderExternalId! }).Distinct()
            .GroupBy(row => row.Cargo).Select(group => new { Cargo = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Cargo, row => row.Count, StringComparer.Ordinal, cancellationToken);
        var view = new OrderFacetsView(platforms, cargos);
        if (OrderFacetsCache.Count > 1000) OrderFacetsCache.Clear();
        OrderFacetsCache[key] = (DateTimeOffset.UtcNow, stamp, view);
        return view;
    }
}
