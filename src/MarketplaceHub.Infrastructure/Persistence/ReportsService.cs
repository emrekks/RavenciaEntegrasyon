using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
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
        // The previous period's addresses, commission and statement too, so its provinces, countries and profit are worked
        // out the way the period's are (the map's and the strips' change against the previous period).
        var previousOrderIds = previousOrders.Select(x => x.Id).ToArray();
        var previousOrderExtras = await OrderExtrasAsync(tenantId, previousOrderIds, cancellationToken);
        var previousLineExtras = await LineExtrasAsync(tenantId, previousOrderIds, cancellationToken);

        // Uploaded commission tariffs whose weeks touch the period or the previous one: the fallback rate for a line that
        // carries none.
        var tariffRows = await (from tariff in db.CommissionTariffs.AsNoTracking()
                                join row in db.CommissionTariffRows.AsNoTracking() on new { tariff.TenantId, TariffId = tariff.Id } equals new { row.TenantId, row.TariffId }
                                where tariff.TenantId == tenantId && allowedIds.Contains(tariff.ConnectionId)
                                    && tariff.PeriodEnd > previous.Start && tariff.PeriodStart < current.End
                                select new { tariff, row }).ToListAsync(cancellationToken);
        var tariffs = tariffRows.Count == 0 ? null : new ReportCommissionTariffs(tariffRows.Select(x => (x.tariff, x.row)));
        var finance = await FinanceAsync(tenantId, allowed, orders, current, cancellationToken);
        var learnedDesi = await LearnedDesiAsync(tenantId, cancellationToken);
        var input = new ReportInput(current, granularity, timezone, allowed, orders, orderExtras, lines, lineExtras, claims, packages, names, query.IncludeTest, tariffs, finance,
            await PazaramaCommissionsAsync(tenantId, allowed, orders, cancellationToken),
            new ReportDesiInput(learnedDesi, await DeclaredDesiAsync(tenantId, allowed, orders, cancellationToken)));
        var previousFinance = await FinanceAsync(tenantId, allowed, previousOrders, previous, cancellationToken);
        var previousInput = new ReportInput(previous, granularity, timezone, allowed, previousOrders, previousOrderExtras, previousLines,
            previousLineExtras, previousClaims, [], names, query.IncludeTest, tariffs, previousFinance, await PazaramaCommissionsAsync(tenantId, allowed, previousOrders, cancellationToken),
            new ReportDesiInput(learnedDesi, await DeclaredDesiAsync(tenantId, allowed, previousOrders, cancellationToken)));
        var stockVariants = ReportBuilder.StockCandidates(input);
        var stock = stockVariants.Length == 0
            ? new Dictionary<Guid, decimal>()
            : await db.InventoryItems.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && stockVariants.Contains(x.VariantId))
                .GroupBy(x => x.VariantId)
                .Select(x => new { x.Key, Available = x.Sum(item => item.Available) })
                .ToDictionaryAsync(x => x.Key, x => x.Available, cancellationToken);

        var report = ReportBuilder.Build(input, previousInput, stock) with { GeneratedAt = timeProvider.GetUtcNow(), Channels = channels };
        return ServiceResult<ReportSalesView>.Ok(await WithProductDetailsAsync(tenantId, report, cancellationToken));
    }

    // The listed products' catalogue details, as the product list shows them: the variant's model code and options
    // (size, colour…) and a picture, the variant's own first, else the product's. Only the top lists are looked up.
    private async Task<ReportSalesView> WithProductDetailsAsync(Guid tenantId, ReportSalesView report, CancellationToken cancellationToken)
    {
        var listed = report.TopProducts.Concat(report.TopReturned).Concat(report.Rising ?? []).Concat(report.Falling ?? []).ToList();
        var variantIds = listed.Where(x => x.VariantId != null).Select(x => x.VariantId!.Value).Distinct().ToArray();
        var productIds = listed.Where(x => x.ProductId != null).Select(x => x.ProductId!.Value).Distinct().ToArray();
        if (variantIds.Length == 0 && productIds.Length == 0) return report;
        var modelCodes = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && variantIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.ModelCode, cancellationToken);
        var optionRows = await (from assignment in db.VariantOptionValues.AsNoTracking()
                                join option in db.ProductOptions.AsNoTracking() on new { assignment.TenantId, assignment.OptionId } equals new { option.TenantId, OptionId = option.Id }
                                join value in db.ProductOptionValues.AsNoTracking() on new { assignment.TenantId, assignment.OptionValueId } equals new { value.TenantId, OptionValueId = value.Id }
                                where assignment.TenantId == tenantId && variantIds.Contains(assignment.VariantId)
                                orderby option.SortOrder
                                select new { assignment.VariantId, Name = option.Label, Value = value.Label }).ToListAsync(cancellationToken);
        var options = optionRows.GroupBy(x => x.VariantId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ReportProductOptionView>)group.Select(x => new ReportProductOptionView(x.Name, x.Value)).ToList());
        var media = await (from item in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on new { item.TenantId, item.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                           where item.TenantId == tenantId && productIds.Contains(item.ProductId) && item.Status == "ACTIVE" && asset.Status == "ACTIVE"
                               && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                           orderby item.SortOrder, item.Id
                           select new { item.ProductId, item.VariantId, asset.Id, asset.Classification, Url = asset.RelativePath }).ToListAsync(cancellationToken);
        string? Image(ReportProductView product)
        {
            var picked = (product.VariantId is { } variantId ? media.FirstOrDefault(x => x.VariantId == variantId) : null)
                ?? (product.ProductId is { } productId ? media.FirstOrDefault(x => x.ProductId == productId && x.VariantId == null) ?? media.FirstOrDefault(x => x.ProductId == productId) : null);
            return picked is null ? null : CatalogMediaDisplay.Url(picked.Id, picked.Classification, picked.Url);
        }
        ReportProductView Detailed(ReportProductView product) => product with
        {
            ModelCode = product.VariantId is { } variantId && modelCodes.TryGetValue(variantId, out var code) && !string.IsNullOrWhiteSpace(code) ? code : null,
            ImageUrl = Image(product),
            Options = product.VariantId is { } id && options.TryGetValue(id, out var list) ? list : []
        };
        return report with
        {
            TopProducts = report.TopProducts.Select(Detailed).ToList(),
            TopReturned = report.TopReturned.Select(Detailed).ToList(),
            Rising = report.Rising?.Select(Detailed).ToList(),
            Falling = report.Falling?.Select(Detailed).ToList()
        };
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
                      let listAmount = priced
                          ? currentPackages.Where(package => package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled)
                              .Sum(package => package.GrossAmount > 0 ? package.GrossAmount : package.NetAmount)
                          : (order.GrossAmount > 0 ? order.GrossAmount : order.NetAmount)
                      // A Trendyol micro-export order's return (claim, returned line or package, returned status) counts once Trendyol's
                      // statement records the refund: Trendyol keeps the sale and charges a weekly overseas return fee instead.
                      let returnBooked = order.StoreFrontCode == null || order.StoreFrontCode == "" || order.StoreFrontCode == "TR"
                          || db.MarketplaceFinanceTransactions.Any(entry => entry.TenantId == tenantId && entry.ConnectionId == order.ConnectionId
                              && entry.OrderNumber == order.OrderNumber && entry.Category == MarketplaceFinanceCategories.Return)
                      let packagesBack = priced && returnBooked
                          ? currentPackages.Where(package => package.OrderId == order.Id && package.Status == ShipmentPackageStatus.Returned)
                              .Sum(package => package.NetAmount > 0 ? package.NetAmount : package.GrossAmount)
                          : 0m
                      let lineValue = orderLines.Where(line => line.OrderId == order.Id).Sum(line => line.UnitPrice * (line.OrderedQuantity - line.CancelledQuantity))
                      let lineBack = orderLines.Where(line => line.OrderId == order.Id)
                          .Select(line => new
                          {
                              line.UnitPrice,
                              Active = line.OrderedQuantity - line.CancelledQuantity,
                              ReturnedQuantity = returnBooked ? line.ReturnedQuantity : 0m,
                              Accepted = !returnBooked ? 0m : db.ReturnLines.Where(returned => returned.TenantId == tenantId && returned.OrderLineId == line.Id
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
                          OrderNumber = order.OrderNumber,
                          Currency = order.Currency,
                          DerivedStatus = order.DerivedStatus,
                          Amount = amount,
                          ListAmount = listAmount,
                          PackagesBack = packagesBack,
                          LineValue = lineValue,
                          LineBack = lineBack,
                          RealAt = row.RealAt,
                          OrderedAt = order.OrderedAt,
                          ShipmentDueAt = order.ShipmentDueAt,
                          StoreFrontCode = order.StoreFrontCode,
                          ReturnBooked = returnBooked
                      }).ToListAsync(cancellationToken);
    }

    // One row per order line, with the units accepted back by return claims, the variant's current cost price and
    // its product's brand and category. A Trendyol micro-export order's accepted claim counts only once Trendyol's
    // statement records the return: 23-30 Sep 2026, a Saudi order's three accepted units had no
    // refund and Trendyol's sales report did not count them (17 units back, the panel 20).
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
                          ReturnedQuantity = row.Order.StoreFrontCode == null || row.Order.StoreFrontCode == "" || row.Order.StoreFrontCode == "TR"
                              || db.MarketplaceFinanceTransactions.Any(entry => entry.TenantId == tenantId && entry.ConnectionId == row.Order.ConnectionId
                                  && entry.OrderNumber == row.Order.OrderNumber && entry.Category == MarketplaceFinanceCategories.Return)
                              ? line.ReturnedQuantity
                              : 0m,
                          AcceptedReturnQuantity = !(row.Order.StoreFrontCode == null || row.Order.StoreFrontCode == "" || row.Order.StoreFrontCode == "TR"
                                  || db.MarketplaceFinanceTransactions.Any(entry => entry.TenantId == tenantId && entry.ConnectionId == row.Order.ConnectionId
                                      && entry.OrderNumber == row.Order.OrderNumber && entry.Category == MarketplaceFinanceCategories.Return))
                              ? 0m
                              : db.ReturnLines.Where(returned => returned.TenantId == tenantId && returned.OrderLineId == line.Id
                                  && db.ReturnClaims.Any(claim => claim.TenantId == tenantId && claim.Id == returned.ClaimId
                                      && (returned.RawStatus == null || returned.RawStatus == ""
                                          ? acceptedClaims.Contains(claim.Status)
                                          : acceptedLines.Contains(returned.RawStatus.ToUpper()))))
                              .Sum(returned => (decimal?)returned.Quantity) ?? 0m,
                          CostPrice = variant == null ? null : variant.CostPrice,
                          VatRate = line.VatRate,
                          Desi = variant == null ? null : variant.Desi
                      }).ToListAsync(cancellationToken);
    }

    private async Task<List<ReportClaimRow>> ClaimRowsAsync(Guid tenantId, Guid[] allowedIds, ReportPeriod period, CancellationToken cancellationToken) =>
        await (from row in PeriodOrders(tenantId, allowedIds, period)
               join claim in db.ReturnClaims.AsNoTracking() on new { row.Order.TenantId, OrderId = row.Order.Id } equals new { claim.TenantId, claim.OrderId }
               // A micro-export order's claim counts once Trendyol's statement records the return, as its units do (LineRowsAsync).
               where row.Order.StoreFrontCode == null || row.Order.StoreFrontCode == "" || row.Order.StoreFrontCode == "TR"
                   || db.MarketplaceFinanceTransactions.Any(entry => entry.TenantId == tenantId && entry.ConnectionId == row.Order.ConnectionId
                       && entry.OrderNumber == row.Order.OrderNumber && entry.Category == MarketplaceFinanceCategories.Return)
               select new ReportClaimRow { OrderId = claim.OrderId, ConnectionId = claim.ConnectionId, Status = claim.Status, ReasonCode = claim.ReasonCode, ReasonText = claim.ReasonText })
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
                   coalesce(nullif(a->>'countryCode', ''), nullif(a->>'shippingCountryCode', '')) AS "CountryCode",
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

    // The accounting records of the report's production Trendyol stores (ReportCosts): those tied to an order of the
    // period, and those dated in the period that belong to no order the panel knows (fees, withholding tax, a record of
    // an order never imported). A record of an order of another period is left to that period. Cargo invoice lines
    // follow the same rule, by their order or by their invoice's date. Null when the report has no such store.
    // The commission of the period's Pazarama orders whose money movements were read: the movements' commission netted
    // (a refund gives it back), as the payout takes it off. An order with no movement read yet is not here.
    private async Task<Dictionary<Guid, decimal>?> PazaramaCommissionsAsync(Guid tenantId, IReadOnlyDictionary<Guid, ReportChannelView> allowed, IReadOnlyList<ReportOrderRow> orders,
        CancellationToken cancellationToken)
    {
        var stores = allowed.Values.Where(channel => channel.PlatformCode == "PAZARAMA").Select(channel => channel.Id).ToHashSet();
        var pazarama = orders.Where(order => stores.Contains(order.ConnectionId)).ToList();
        if (pazarama.Count == 0) return null;
        var storeIds = stores.ToArray();
        var numbers = pazarama.Select(order => order.OrderNumber).Distinct(StringComparer.Ordinal).ToArray();
        var sums = await db.MarketplaceFinanceTransactions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && storeIds.Contains(x.ConnectionId) && x.Source == Jobs.Handlers.PazaramaFinanceSyncJobHandler.Source
                && x.OrderNumber != null && numbers.Contains(x.OrderNumber))
            .GroupBy(x => new { x.ConnectionId, x.OrderNumber })
            .Select(group => new { group.Key.ConnectionId, group.Key.OrderNumber, Commission = group.Sum(x => x.CommissionAmount ?? 0m) })
            .ToListAsync(cancellationToken);
        var byOrder = sums.ToDictionary(x => (x.ConnectionId, x.OrderNumber!), x => x.Commission);
        return pazarama.Where(order => byOrder.ContainsKey((order.ConnectionId, order.OrderNumber)))
            .ToDictionary(order => order.Id, order => byOrder[(order.ConnectionId, order.OrderNumber)]);
    }

    // The unit desi learned from every billed Trendyol shipping parcel of the tenant's last year, whatever stores the
    // report shows: the same products go to every marketplace in the same parcels. A parcel is one order with one
    // shipping line; an order billed in several parcels says nothing of one parcel's contents and is left out.
    private async Task<ReportParcelDesi.Learned> LearnedDesiAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var since = timeProvider.GetUtcNow().ToUniversalTime().AddDays(-365);
        var items = await db.MarketplaceCargoInvoiceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OrderNumber != null && x.Desi != null && x.FetchedAt >= since)
            .Select(x => new { x.ConnectionId, OrderNumber = x.OrderNumber!, x.ShipmentPackageType, x.ParcelUniqueId, Desi = x.Desi!.Value })
            .ToListAsync(cancellationToken);
        var parcels = items
            .Where(x => !TrendyolFinanceCategorizer.Fold(x.ShipmentPackageType).Contains("iade", StringComparison.Ordinal))
            .GroupBy(x => (x.ConnectionId, x.OrderNumber))
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => (decimal)group.First().Desi);
        if (parcels.Count == 0) return ReportParcelDesi.Learned.None;

        var lines = new List<(Guid ConnectionId, string OrderNumber, ReportParcelDesi.ParcelLine Line)>();
        foreach (var chunk in parcels.Keys.Select(key => key.OrderNumber).Distinct(StringComparer.Ordinal).Chunk(2_000))
            lines.AddRange((await (from order in db.Orders.AsNoTracking()
                                   where order.TenantId == tenantId && chunk.Contains(order.OrderNumber)
                                   join line in db.OrderLines.AsNoTracking() on new { order.TenantId, OrderId = order.Id } equals new { line.TenantId, line.OrderId }
                                   join variant in db.ProductVariants.AsNoTracking() on new { line.TenantId, Id = line.VariantId ?? Guid.Empty } equals new { variant.TenantId, variant.Id } into variants
                                   from variant in variants.DefaultIfEmpty()
                                   join product in db.Products.AsNoTracking() on new { TenantId = (Guid?)variant.TenantId, Id = (Guid?)variant.ProductId } equals new { TenantId = (Guid?)product.TenantId, Id = (Guid?)product.Id } into products
                                   from product in products.DefaultIfEmpty()
                                   select new
                                   {
                                       order.ConnectionId,
                                       order.OrderNumber,
                                       ProductId = variant == null ? null : (Guid?)variant.ProductId,
                                       CategoryId = product == null ? null : product.CategoryId,
                                       Units = line.OrderedQuantity - line.CancelledQuantity
                                   }).ToListAsync(cancellationToken))
                .Select(row => (row.ConnectionId, row.OrderNumber, new ReportParcelDesi.ParcelLine(row.ProductId, row.CategoryId, row.Units))));
        return ReportParcelDesi.Learn(lines
            .Where(row => parcels.ContainsKey((row.ConnectionId, row.OrderNumber)))
            .GroupBy(row => (row.ConnectionId, row.OrderNumber))
            .Select(group => new ReportParcelDesi.Parcel(parcels[group.Key], group.Select(row => row.Line).ToList())));
    }

    // By Hepsiburada order: the desi Hepsiburada put on its lines, added up; an order with a line without one has none.
    private async Task<IReadOnlyDictionary<Guid, decimal>> DeclaredDesiAsync(Guid tenantId, IReadOnlyDictionary<Guid, ReportChannelView> allowed, IReadOnlyList<ReportOrderRow> orders,
        CancellationToken cancellationToken)
    {
        var stores = allowed.Values.Where(channel => channel.PlatformCode == "HEPSIBURADA").Select(channel => channel.Id).ToHashSet();
        var orderIds = orders.Where(order => stores.Contains(order.ConnectionId)).Select(order => order.Id).ToArray();
        var result = new Dictionary<Guid, decimal>();
        foreach (var chunk in orderIds.Chunk(2_000))
        {
            var lines = await db.OrderLines.AsNoTracking()
                .Where(line => line.TenantId == tenantId && chunk.Contains(line.OrderId))
                .Select(line => new { line.OrderId, line.SourceSnapshotJson })
                .ToListAsync(cancellationToken);
            foreach (var order in lines.GroupBy(line => line.OrderId))
            {
                var desi = order.Select(line => ReportParcelDesi.HepsiburadaLineDesi(line.SourceSnapshotJson)).ToList();
                if (desi.All(value => value is not null)) result[order.Key] = desi.Sum(value => value!.Value);
            }
        }
        return result;
    }

    private async Task<ReportFinance?> FinanceAsync(Guid tenantId, IReadOnlyDictionary<Guid, ReportChannelView> allowed, IReadOnlyList<ReportOrderRow> orders,
        ReportPeriod period, CancellationToken cancellationToken)
    {
        var storeIds = allowed.Values.Where(x => x.PlatformCode == "TRENDYOL" && !x.IsTest).Select(x => x.Id).ToArray();
        if (storeIds.Length == 0) return null;
        var stored = await db.MarketplaceFinanceSyncStates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && storeIds.Contains(x.ConnectionId))
            .ToDictionaryAsync(x => x.ConnectionId, cancellationToken);
        var states = storeIds.ToDictionary(id => id, id => stored.GetValueOrDefault(id));
        if (stored.Count == 0) return new([], [], new HashSet<(Guid, string)>(), states);

        var start = period.Start;
        var end = period.End;
        var rows = new List<ReportFinanceRow>();
        var items = new List<ReportCargoItemRow>();
        var itemized = new HashSet<(Guid ConnectionId, string Serial)>();
        var samples = new Dictionary<Guid, ReportCargoSample>();
        // Store by store: an order number is only that store's order.
        foreach (var storeId in stored.Keys)
        {
            var numbers = orders.Where(x => x.ConnectionId == storeId && !string.IsNullOrEmpty(x.OrderNumber)).Select(x => x.OrderNumber).Distinct(StringComparer.Ordinal).ToArray();
            var storeRows = await db.MarketplaceFinanceTransactions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == storeId
                    && ((x.OrderNumber != null && numbers.Contains(x.OrderNumber))
                        || (x.TransactionDate >= start && x.TransactionDate < end
                            && (x.OrderNumber == null || !db.Orders.Any(order => order.TenantId == tenantId && order.ConnectionId == storeId && order.OrderNumber == x.OrderNumber)))))
                .Select(x => new ReportFinanceRow
                {
                    ConnectionId = x.ConnectionId,
                    Source = x.Source,
                    ExternalId = x.ExternalId,
                    RequestedType = x.RequestedType,
                    TransactionType = x.TransactionType,
                    Category = x.Category,
                    TransactionDate = x.TransactionDate,
                    OrderNumber = x.OrderNumber,
                    Barcode = x.Barcode,
                    Debt = x.Debt,
                    Credit = x.Credit,
                    CommissionAmount = x.CommissionAmount
                })
                .ToListAsync(cancellationToken);
            rows.AddRange(storeRows);
            // The cargo invoices counted by date: their lines of orders the panel does not know count with them, and an
            // invoice that has lines counts through them instead of as a whole.
            var datedInvoices = storeRows.Where(x => TrendyolFinanceCategorizer.IsCargoInvoice(x.Source, x.RequestedType, x.Category))
                .Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).ToArray();
            items.AddRange(await db.MarketplaceCargoInvoiceItems.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == storeId
                    && ((x.OrderNumber != null && numbers.Contains(x.OrderNumber))
                        || (datedInvoices.Contains(x.InvoiceSerialNumber)
                            && (x.OrderNumber == null || !db.Orders.Any(order => order.TenantId == tenantId && order.ConnectionId == storeId && order.OrderNumber == x.OrderNumber)))))
                .Select(x => new ReportCargoItemRow { ConnectionId = x.ConnectionId, InvoiceSerialNumber = x.InvoiceSerialNumber, OrderNumber = x.OrderNumber, ShipmentPackageType = x.ShipmentPackageType, Amount = x.Amount })
                .ToListAsync(cancellationToken));
            if (datedInvoices.Length > 0)
                foreach (var serial in await db.MarketplaceCargoInvoiceItems.AsNoTracking()
                             .Where(x => x.TenantId == tenantId && x.ConnectionId == storeId && datedInvoices.Contains(x.InvoiceSerialNumber))
                             .Select(x => x.InvoiceSerialNumber).Distinct().ToListAsync(cancellationToken))
                    itemized.Add((storeId, serial));

            // The store's cargo tariff, for the period's orders Trendyol has not billed cargo for yet: the shipping lines
            // of its cargo invoices dated in the 30 days before the period's end (60 when none was dated then).
            var sampleFrom = end.AddDays(-30);
            if (!await db.MarketplaceFinanceTransactions.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == storeId
                    && x.Source == TrendyolFinanceRequestContract.OtherFinancials && x.RequestedType == TrendyolFinanceCategorizer.DeductionInvoices
                    && x.Category == MarketplaceFinanceCategories.Cargo && x.TransactionDate >= sampleFrom && x.TransactionDate < end, cancellationToken))
                sampleFrom = end.AddDays(-60);
            var sampleSerials = db.MarketplaceFinanceTransactions
                .Where(x => x.TenantId == tenantId && x.ConnectionId == storeId && x.Source == TrendyolFinanceRequestContract.OtherFinancials
                    && x.RequestedType == TrendyolFinanceCategorizer.DeductionInvoices && x.Category == MarketplaceFinanceCategories.Cargo
                    && x.TransactionDate >= sampleFrom && x.TransactionDate < end)
                .Select(x => x.ExternalId);
            var shipping = (await db.MarketplaceCargoInvoiceItems.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ConnectionId == storeId && x.OrderNumber != null && sampleSerials.Contains(x.InvoiceSerialNumber))
                    .Select(x => new ReportCargoItemRow { ConnectionId = x.ConnectionId, InvoiceSerialNumber = x.InvoiceSerialNumber, OrderNumber = x.OrderNumber, ShipmentPackageType = x.ShipmentPackageType, Amount = x.Amount })
                    .ToListAsync(cancellationToken))
                .Where(x => !x.IsReturn)
                .ToList();
            if (ReportCargoSample.Tariff(shipping.Select(x => (x.Amount, x.OrderNumber)).ToList()) is { } sample) samples[storeId] = sample;
        }
        return new(rows, items, itemized, states, samples);
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
