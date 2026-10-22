using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MarketplaceHub.Infrastructure.Persistence;

// The sales report. Read-only queries over the period's orders (orders with their amounts, their lines, return claims,
// packages) and the previous period of equal length, then the breakdowns in memory (ReportBuilder): one small row per
// order, line and package, so a year of a large store stays a few MB. The amounts follow the overview's revenue
// (DashboardReadService.PerformanceOrders) so both pages agree. Values inside the stored JSON snapshots (city,
// district, customer key, commission, cancel reason) are read in the database; personal data never leaves it.
public sealed class ReportsService(AppDbContext db, TimeProvider timeProvider) : IReportsService
{
    private static readonly InvoiceStatus[] IssuedInvoiceStatuses =
        [InvoiceStatus.Submitted, InvoiceStatus.Accepted, InvoiceStatus.MarketplacePending, InvoiceStatus.MarketplaceFailed, InvoiceStatus.Completed];

    public async Task<ServiceResult<ReportSalesView>> SalesAsync(Guid tenantId, ReportSalesQuery query, CancellationToken cancellationToken)
    {
        var timezoneId = await db.Tenants.AsNoTracking().Where(x => x.Id == tenantId).Select(x => x.Timezone).SingleOrDefaultAsync(cancellationToken);
        var timezone = DashboardReadService.ResolveTimezone(timezoneId);
        var resolved = DashboardPerformancePeriod.Resolve(query.From, query.To, timeProvider.GetUtcNow(), timezone);
        if (!resolved.Succeeded) return ServiceResult<ReportSalesView>.Fail(resolved.Error!.Code, resolved.Error.Message, resolved.Error.Status);
        var period = resolved.Value!;
        var granularity = ReportBuckets.Resolve(query.Granularity, period.From, period.To);
        if (granularity is null) return ServiceResult<ReportSalesView>.Fail("REPORT_GRANULARITY_INVALID", "Gruplama gün, hafta veya ay olmalıdır.", 400);

        var excluded = DashboardRevenuePolicy.ExcludedConnectionStatuses;
        var channels = (await db.PlatformConnections.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !excluded.Contains(x.Status))
                .OrderBy(x => x.PlatformCode).ThenBy(x => x.DisplayName)
                .Select(x => new { x.Id, x.PlatformCode, x.DisplayName, x.Environment })
                .ToListAsync(cancellationToken))
            .Select(x => new ReportChannelView(x.Id, x.PlatformCode.Trim().ToUpperInvariant(), x.DisplayName, ReportBuckets.IsTestEnvironment(x.Environment)))
            .ToList();
        var platformFilter = query.PlatformCodes?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var connectionFilter = query.ConnectionIds?.ToHashSet();
        var allowed = channels
            .Where(x => query.IncludeTest || !x.IsTest)
            .Where(x => platformFilter is not { Count: > 0 } || platformFilter.Contains(x.PlatformCode))
            .Where(x => connectionFilter is not { Count: > 0 } || connectionFilter.Contains(x.Id))
            .ToDictionary(x => x.Id);
        var allowedIds = allowed.Keys.ToArray();

        // The previous period: the same number of days right before (the overview's comparison period).
        var previous = new ReportPeriod(period.PreviousFrom, period.PreviousTo, period.PreviousStart, period.PreviousEnd);
        var current = new ReportPeriod(period.From, period.To, period.CurrentStart, period.CurrentEnd);

        var orders = await OrderRowsAsync(tenantId, allowedIds, current, cancellationToken);
        var orderIds = orders.Select(x => x.Id).ToArray();
        var orderExtras = await OrderExtrasAsync(tenantId, orderIds, cancellationToken);
        var lines = await LineRowsAsync(tenantId, allowedIds, current, cancellationToken);
        var lineExtras = await LineExtrasAsync(tenantId, orderIds, cancellationToken);
        var claims = await ClaimRowsAsync(tenantId, allowedIds, current, cancellationToken);
        var packages = await PackageRowsAsync(tenantId, allowedIds, current, cancellationToken);
        var names = await CatalogNamesAsync(tenantId, lines, cancellationToken);

        var previousOrders = await OrderRowsAsync(tenantId, allowedIds, previous, cancellationToken);
        var previousLines = await LineRowsAsync(tenantId, allowedIds, previous, cancellationToken);
        var previousClaims = await ClaimRowsAsync(tenantId, allowedIds, previous, cancellationToken);

        // Uploaded commission tariffs whose weeks touch the period: the fallback rate for a line that carries none.
        var tariffRows = await (from tariff in db.CommissionTariffs.AsNoTracking()
                                join row in db.CommissionTariffRows.AsNoTracking() on new { tariff.TenantId, TariffId = tariff.Id } equals new { row.TenantId, row.TariffId }
                                where tariff.TenantId == tenantId && allowedIds.Contains(tariff.ConnectionId)
                                    && tariff.PeriodEnd > current.Start && tariff.PeriodStart < current.End
                                select new { tariff, row }).ToListAsync(cancellationToken);
        var tariffs = tariffRows.Count == 0 ? null : new ReportCommissionTariffs(tariffRows.Select(x => (x.tariff, x.row)));
        var input = new ReportInput(current, granularity, timezone, allowed, orders, orderExtras, lines, lineExtras, claims, packages, names, query.IncludeTest, tariffs);
        var previousInput = new ReportInput(previous, granularity, timezone, allowed, previousOrders, new Dictionary<Guid, ReportOrderExtra>(), previousLines,
            new Dictionary<Guid, ReportLineExtra>(), previousClaims, [], names, query.IncludeTest);
        var stockVariants = ReportBuilder.StockCandidates(input);
        var stock = stockVariants.Length == 0
            ? new Dictionary<Guid, decimal>()
            : await db.InventoryItems.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && stockVariants.Contains(x.VariantId))
                .GroupBy(x => x.VariantId)
                .Select(x => new { x.Key, Available = x.Sum(item => item.Available) })
                .ToDictionaryAsync(x => x.Key, x => x.Available, cancellationToken);

        var report = ReportBuilder.Build(input, previousInput, stock) with { GeneratedAt = timeProvider.GetUtcNow(), Channels = channels };
        return ServiceResult<ReportSalesView>.Ok(report);
    }

    // The period's orders by their real order time (Trendyol's stored time minus its offset), any status.
    private IQueryable<PeriodOrder> PeriodOrders(Guid tenantId, Guid[] allowedIds, ReportPeriod period)
    {
        var wallClockPlatforms = MarketplaceStoredClock.WallClockPlatforms;
        var ahead = MarketplaceStoredClock.TurkeyWallClockAhead;
        var start = period.Start;
        var end = period.End;
        var scanEnd = period.End + ahead;
        return from order in db.Orders.AsNoTracking()
               join connection in db.PlatformConnections.AsNoTracking() on new { order.TenantId, Id = order.ConnectionId } equals new { connection.TenantId, connection.Id }
               where order.TenantId == tenantId && allowedIds.Contains(order.ConnectionId)
                   && order.OrderedAt >= start && order.OrderedAt < scanEnd
               let realAt = wallClockPlatforms.Contains(connection.PlatformCode) ? order.OrderedAt - ahead : order.OrderedAt
               where realAt >= start && realAt < end
               select new PeriodOrder { Order = order, RealAt = realAt };
    }

    // One row per order. Amount: the order's current packages that are not cancelled (an order whose packages carry
    // no amount keeps its order total); the returned part is worked out from these in ReportBuilder, as the overview does.
    private async Task<List<ReportOrderRow>> OrderRowsAsync(Guid tenantId, Guid[] allowedIds, ReportPeriod period, CancellationToken cancellationToken)
    {
        var acceptedClaims = DashboardRevenuePolicy.AcceptedReturnStatuses;
        var acceptedLines = DashboardRevenuePolicy.AcceptedReturnLineRawStatuses;
        var currentPackages = MarketplaceSalesService.CurrentShipmentPackages(db).Where(package => package.TenantId == tenantId);
        var orderLines = db.OrderLines.AsNoTracking().Where(line => line.TenantId == tenantId);
        return await (from row in PeriodOrders(tenantId, allowedIds, period)
                      let order = row.Order
                      let priced = currentPackages.Any(package => package.OrderId == order.Id && (package.NetAmount > 0 || package.GrossAmount > 0))
                      let amount = priced
                          ? currentPackages.Where(package => package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled)
                              .Sum(package => package.NetAmount > 0 ? package.NetAmount : package.GrossAmount)
                          : (order.NetAmount > 0 ? order.NetAmount : order.GrossAmount)
                      let packagesBack = priced
                          ? currentPackages.Where(package => package.OrderId == order.Id && package.Status == ShipmentPackageStatus.Returned)
                              .Sum(package => package.NetAmount > 0 ? package.NetAmount : package.GrossAmount)
                          : 0m
                      let lineValue = orderLines.Where(line => line.OrderId == order.Id).Sum(line => line.UnitPrice * (line.OrderedQuantity - line.CancelledQuantity))
                      let lineBack = orderLines.Where(line => line.OrderId == order.Id)
                          .Select(line => new
                          {
                              line.UnitPrice,
                              Active = line.OrderedQuantity - line.CancelledQuantity,
                              line.ReturnedQuantity,
                              Accepted = db.ReturnLines.Where(returned => returned.TenantId == tenantId && returned.OrderLineId == line.Id
                                      && db.ReturnClaims.Any(claim => claim.TenantId == tenantId && claim.Id == returned.ClaimId
                                          && (returned.RawStatus == null || returned.RawStatus == ""
                                              ? acceptedClaims.Contains(claim.Status)
                                              : acceptedLines.Contains(returned.RawStatus.ToUpper()))))
                                  .Sum(returned => (decimal?)returned.Quantity) ?? 0m
                          })
                          .Sum(line => line.UnitPrice * Math.Min(line.Active, Math.Max(line.ReturnedQuantity, line.Accepted)))
                      select new ReportOrderRow
                      {
                          Id = order.Id,
                          ConnectionId = order.ConnectionId,
                          Currency = order.Currency,
                          DerivedStatus = order.DerivedStatus,
                          Amount = amount,
                          PackagesBack = packagesBack,
                          LineValue = lineValue,
                          LineBack = lineBack,
                          RealAt = row.RealAt,
                          OrderedAt = order.OrderedAt,
                          ShipmentDueAt = order.ShipmentDueAt
                      }).ToListAsync(cancellationToken);
    }

    // One row per order line, with the units accepted back by return claims, the variant's current cost price and
    // its product's brand and category.
    private async Task<List<ReportLineRow>> LineRowsAsync(Guid tenantId, Guid[] allowedIds, ReportPeriod period, CancellationToken cancellationToken)
    {
        var acceptedClaims = DashboardRevenuePolicy.AcceptedReturnStatuses;
        var acceptedLines = DashboardRevenuePolicy.AcceptedReturnLineRawStatuses;
        return await (from row in PeriodOrders(tenantId, allowedIds, period)
                      join line in db.OrderLines.AsNoTracking() on new { row.Order.TenantId, OrderId = row.Order.Id } equals new { line.TenantId, line.OrderId }
                      join variant in db.ProductVariants.AsNoTracking() on new { line.TenantId, Id = line.VariantId } equals new { variant.TenantId, Id = (Guid?)variant.Id } into variants
                      from variant in variants.DefaultIfEmpty()
                      join product in db.Products.AsNoTracking() on new { TenantId = (Guid?)variant.TenantId, Id = (Guid?)variant.ProductId } equals new { TenantId = (Guid?)product.TenantId, Id = (Guid?)product.Id } into products
                      from product in products.DefaultIfEmpty()
                      select new ReportLineRow
                      {
                          Id = line.Id,
                          OrderId = line.OrderId,
                          VariantId = line.VariantId,
                          ProductId = variant == null ? null : (Guid?)variant.ProductId,
                          BrandId = product == null ? null : product.BrandId,
                          CategoryId = product == null ? null : product.CategoryId,
                          Title = line.TitleSnapshot,
                          Sku = line.Sku,
                          Barcode = line.Barcode,
                          UnitPrice = line.UnitPrice,
                          OrderedQuantity = line.OrderedQuantity,
                          CancelledQuantity = line.CancelledQuantity,
                          ReturnedQuantity = line.ReturnedQuantity,
                          AcceptedReturnQuantity = db.ReturnLines.Where(returned => returned.TenantId == tenantId && returned.OrderLineId == line.Id
                                  && db.ReturnClaims.Any(claim => claim.TenantId == tenantId && claim.Id == returned.ClaimId
                                      && (returned.RawStatus == null || returned.RawStatus == ""
                                          ? acceptedClaims.Contains(claim.Status)
                                          : acceptedLines.Contains(returned.RawStatus.ToUpper()))))
                              .Sum(returned => (decimal?)returned.Quantity) ?? 0m,
                          CostPrice = variant == null ? null : variant.CostPrice
                      }).ToListAsync(cancellationToken);
    }

    private async Task<List<ReportClaimRow>> ClaimRowsAsync(Guid tenantId, Guid[] allowedIds, ReportPeriod period, CancellationToken cancellationToken) =>
        await (from row in PeriodOrders(tenantId, allowedIds, period)
               join claim in db.ReturnClaims.AsNoTracking() on new { row.Order.TenantId, OrderId = row.Order.Id } equals new { claim.TenantId, claim.OrderId }
               select new ReportClaimRow { OrderId = claim.OrderId, Status = claim.Status, ReasonCode = claim.ReasonCode, ReasonText = claim.ReasonText })
            .ToListAsync(cancellationToken);

    // The current packages of the period's orders with their first "shipped" and "delivered" status times and what
    // tells whether they hold an invoice.
    private async Task<List<ReportPackageRow>> PackageRowsAsync(Guid tenantId, Guid[] allowedIds, ReportPeriod period, CancellationToken cancellationToken)
    {
        var issued = IssuedInvoiceStatuses;
        return await (from row in PeriodOrders(tenantId, allowedIds, period)
                      join package in MarketplaceSalesService.CurrentShipmentPackages(db) on new { row.Order.TenantId, OrderId = row.Order.Id } equals new { package.TenantId, package.OrderId }
                      select new ReportPackageRow
                      {
                          Id = package.Id,
                          OrderId = package.OrderId,
                          CargoProvider = package.CargoProviderExternalId,
                          Status = package.Status,
                          Amount = package.NetAmount > 0 ? package.NetAmount : package.GrossAmount,
                          MarketplaceInvoiceStatus = package.MarketplaceInvoiceStatus,
                          ManualInvoiceStatus = package.ManualInvoiceStatus,
                          PanelInvoiced = db.Invoices.Any(invoice => invoice.TenantId == tenantId && invoice.OrderId == package.OrderId
                              && (invoice.PackageId == null || invoice.PackageId == package.Id) && issued.Contains(invoice.Status)),
                          ShippedAt = db.OrderStatusHistory.Where(history => history.TenantId == tenantId && history.PackageId == package.Id && history.CanonicalStatus == "SHIPPED")
                              .Min(history => (DateTimeOffset?)history.OccurredAt),
                          DeliveredAt = db.OrderStatusHistory.Where(history => history.TenantId == tenantId && history.PackageId == package.Id && history.CanonicalStatus == "DELIVERED")
                              .Min(history => (DateTimeOffset?)history.OccurredAt)
                      }).ToListAsync(cancellationToken);
    }

    // City, district and a one-way customer key from the order's JSON snapshots, read in the database. A snapshot that
    // is not valid JSON reads as empty. The customer key is the store's own customer id, else the e-mail, else the
    // last ten digits of the phone, each as an MD5 digest; the text itself is never returned.
    private async Task<Dictionary<Guid, ReportOrderExtra>> OrderExtrasAsync(Guid tenantId, Guid[] orderIds, CancellationToken cancellationToken)
    {
        if (orderIds.Length == 0) return [];
        const string sql = """
            WITH src AS (
                SELECT o."Id", o."ConnectionId",
                       CASE WHEN pg_input_is_valid(o."ShipmentAddressSnapshotJson", 'jsonb') THEN o."ShipmentAddressSnapshotJson"::jsonb END AS a,
                       CASE WHEN pg_input_is_valid(o."CustomerSnapshotJson", 'jsonb') THEN o."CustomerSnapshotJson"::jsonb END AS c
                FROM sales.orders o
                WHERE o."TenantId" = @tenant AND o."Id" = ANY(@ids)),
            keys AS (
                SELECT "Id", a, c,
                       nullif(trim(coalesce(c->>'customerId', c->>'CustomerId', '')), '') AS customer_id,
                       lower(trim(coalesce(nullif(c->>'email', ''), nullif(c->>'customerEmail', ''), nullif(a->>'email', ''), nullif(a->>'customerEmail', ''), ''))) AS email,
                       regexp_replace(coalesce(nullif(c->>'phone', ''), nullif(c->>'phoneNumber', ''), nullif(c->>'gsm', ''), nullif(a->>'phone', ''), nullif(a->>'phoneNumber', ''), nullif(a->>'gsm', ''), ''), '\D', '', 'g') AS phone,
                       "ConnectionId"
                FROM src)
            SELECT "Id",
                   a->>'cityName' AS "CityName", a->>'city' AS "City", a->>'shippingCity' AS "ShippingCity", a->>'province' AS "Province",
                   a->>'districtName' AS "DistrictName", a->>'district' AS "District", a->>'shippingDistrict' AS "ShippingDistrict",
                   a->>'town' AS "Town", a->>'shippingTown' AS "ShippingTown",
                   CASE WHEN customer_id IS NOT NULL AND customer_id <> '0' THEN 'i:' || md5("ConnectionId"::text || ':' || customer_id)
                        WHEN email LIKE '%_@_%' THEN 'e:' || md5(email)
                        WHEN length(phone) >= 10 THEN 'p:' || md5(right(phone, 10))
                   END AS "CustomerKey"
            FROM keys
            """;
        var rows = await db.Database.SqlQueryRaw<ReportOrderExtra>(sql,
                new NpgsqlParameter("tenant", tenantId),
                new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = orderIds })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(x => x.Id);
    }

    // The commission each marketplace sent on the line (Trendyol "commission" rate, Hepsiburada "commission" amount
    // object, n11 "commissionRate", PTT AVM "Komisyon") and a cancel reason, read in the database.
    private async Task<Dictionary<Guid, ReportLineExtra>> LineExtrasAsync(Guid tenantId, Guid[] orderIds, CancellationToken cancellationToken)
    {
        if (orderIds.Length == 0) return [];
        const string sql = """
            WITH src AS (
                SELECT l."Id",
                       CASE WHEN l."SourceSnapshotJson" IS NOT NULL AND pg_input_is_valid(l."SourceSnapshotJson", 'jsonb') THEN l."SourceSnapshotJson"::jsonb END AS j
                FROM sales.order_lines l
                WHERE l."TenantId" = @tenant AND l."OrderId" = ANY(@ids))
            SELECT "Id",
                   jsonb_typeof(j->'commission') AS "CommissionType",
                   CASE WHEN jsonb_typeof(j->'commission') = 'object' THEN j->'commission'->>'amount' ELSE j->>'commission' END AS "Commission",
                   j->>'commissionRate' AS "CommissionRate",
                   j->>'Komisyon' AS "Komisyon",
                   coalesce(nullif(j->>'cancelReason', ''), nullif(j->>'cancelReasonCode', ''), nullif(j->>'cancellationReason', ''), nullif(j->>'CancelReason', ''), nullif(j->>'IptalNedeni', '')) AS "CancelReason"
            FROM src
            """;
        var rows = await db.Database.SqlQueryRaw<ReportLineExtra>(sql,
                new NpgsqlParameter("tenant", tenantId),
                new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = orderIds })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(x => x.Id);
    }

    private async Task<ReportCatalogNames> CatalogNamesAsync(Guid tenantId, IReadOnlyList<ReportLineRow> lines, CancellationToken cancellationToken)
    {
        var brandIds = lines.Where(x => x.BrandId != null).Select(x => x.BrandId!.Value).Distinct().ToArray();
        var categoryIds = lines.Where(x => x.CategoryId != null).Select(x => x.CategoryId!.Value).Distinct().ToArray();
        var brands = brandIds.Length == 0 ? [] : await db.Brands.AsNoTracking().Where(x => x.TenantId == tenantId && brandIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var categories = categoryIds.Length == 0 ? [] : await db.Categories.AsNoTracking().Where(x => x.TenantId == tenantId && categoryIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        return new(brands, categories);
    }

    // Set through an object initializer, not a constructor, so the queries composed on it can still be translated.
    internal sealed class PeriodOrder
    {
        public Order Order { get; init; } = null!;
        public DateTimeOffset RealAt { get; init; }
    }
}
