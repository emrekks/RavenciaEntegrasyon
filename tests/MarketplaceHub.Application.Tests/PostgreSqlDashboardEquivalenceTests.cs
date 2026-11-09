using System.Globalization;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The overview's per-order figures were rewritten on 10 Nov 2026 so that each is read once (grouped subqueries joined to
// the order) instead of inlined by EF wherever it was used, and a one-day period is now read by the hour only. These
// tests run the old queries (LegacyDashboardPerformanceQueries) and the new ones on the same data and require the same
// rows to the last digit, scale included: partial and whole returns, cancelled lines and packages, split and resent
// packages, Trendyol's payment placeholder, several currencies and stores, micro-export returns with and without a
// statement refund, exchanges, the marketplace's own discounts and Istanbul hour and day boundaries.
[Collection("postgres-integration")]
public sealed class PostgreSqlDashboardEquivalenceTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    // 20 Oct 2026, 10:30 in Istanbul.
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 7, 30, 0, TimeSpan.Zero);
    private const string Timezone = "Europe/Istanbul";

    public static TheoryData<string, string> Periods => new()
    {
        { "2026-10-20", "2026-10-20" }, // today so far, against yesterday until the same time
        { "2026-10-19", "2026-10-19" },
        { "2026-10-15", "2026-10-15" },
        { "2026-10-14", "2026-10-20" },
        { "2026-09-01", "2026-10-20" }
    };

    [PostgreSqlTheory]
    [MemberData(nameof(Periods))]
    public async Task The_rewritten_queries_give_the_old_figures(string from, string to)
    {
        var tenant = NewTenant("dash-eq");
        try
        {
            await SeedAsync(tenant);
            var period = DashboardPerformancePeriod.Resolve(DateOnly.Parse(from, CultureInfo.InvariantCulture), DateOnly.Parse(to, CultureInfo.InvariantCulture), Now, TimeZoneInfo.FindSystemTimeZoneById(Timezone)).Value!;

            await using var db = fixture.CreateContext();
            var service = new DashboardReadService(db, new FixedClock(Now));
            foreach (var hourly in new[] { false, true })
            {
                var old = await LegacyDashboardPerformanceQueries.GroupsAsync(db, tenant.Id, period, Timezone, hourly);
                var rewritten = await service.PerformanceGroupsAsync(tenant.Id, period, Timezone, hourly, CancellationToken.None);
                // The data reaches every rule: returned money, several stores and currencies.
                Assert.Contains(old.Revenue, x => x.ReturnedAmount > 0 && x.Amount > 0);
                Assert.Contains(old.Revenue, x => x.Currency == "EUR");
                Assert.True(old.Revenue.Select(x => x.ConnectionId).Distinct().Count() >= 3);
                Assert.Equal(Text(old.Revenue), Text(rewritten.Revenue));
                Assert.Equal(Text(old.Units), Text(rewritten.Units));
                Assert.Equal(Text(old.Packages), Text(rewritten.Packages));
            }

            // The endpoint's answer: the day rows as the old daily queries gave them, and for one day the old hours.
            var oldDayGroups = await LegacyDashboardPerformanceQueries.GroupsAsync(db, tenant.Id, period, Timezone, hourly: false);
            var oldDays = DashboardReadService.MergePerformanceRows(oldDayGroups.Revenue, oldDayGroups.Units, oldDayGroups.Packages);
            var view = (await service.PerformanceAsync(tenant.Id, period.From, period.To, CancellationToken.None)).Value!;
            Assert.Equal(Text(oldDays.Where(x => x.Day >= period.From && x.Day <= period.To)), Text(view.Current));
            Assert.Equal(Text(oldDays.Where(x => x.Day >= period.PreviousFrom && x.Day <= period.PreviousTo)), Text(view.Previous));
            if (period.From == period.To)
            {
                var oldHourGroups = await LegacyDashboardPerformanceQueries.GroupsAsync(db, tenant.Id, period, Timezone, hourly: true);
                var oldHours = DashboardReadService.MergePerformanceHours(oldHourGroups.Revenue, oldHourGroups.Units, oldHourGroups.Packages);
                var day = period.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                Assert.Equal(Text(oldHours.Where(x => x.Hour.StartsWith(day, StringComparison.Ordinal))), Text(view.CurrentHours!));
                Assert.Equal(Text(oldHours.Where(x => !x.Hour.StartsWith(day, StringComparison.Ordinal))), Text(view.PreviousHours!));
                Assert.NotEmpty(view.Current);
            }
            else
            {
                Assert.Null(view.CurrentHours);
                Assert.Null(view.PreviousHours);
            }
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    // The statement stays one screen of SQL: every source is read once, not once per use.
    [Fact]
    public void The_revenue_statement_reads_each_source_once()
    {
        using var db = DashboardOrderVisibilityTests.MetadataOnlyContext();
        var period = DashboardPerformancePeriod.Resolve(new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 20), Now, TimeZoneInfo.FindSystemTimeZoneById(Timezone)).Value!;
        var orders = DashboardReadService.PerformanceOrders(db, Guid.NewGuid(), period, Timezone);
        var revenueSql = DashboardReadService.RevenueQuery(orders).ToQueryString();
        var legacySql = DashboardReadService.RevenueQuery(LegacyDashboardPerformanceQueries.PerformanceOrders(db, Guid.NewGuid(), period, Timezone)).ToQueryString();

        // 815 KB with 1,434 package reads before; 74 KB with 6 after (the CASE arithmetic of the amount, repeated by EF,
        // is what is left of the size: 2.5 ms of planning against 234 ms on the same database).
        Assert.True(revenueSql.Length < 100_000, $"{revenueSql.Length} characters");
        Assert.True(revenueSql.Length * 10 < legacySql.Length, $"{revenueSql.Length} against {legacySql.Length}");
        Assert.True(Count(revenueSql, "shipment_packages") <= 6, $"{Count(revenueSql, "shipment_packages")} package reads");
        Assert.Equal(1, Count(revenueSql, "return_lines"));
        Assert.Equal(1, Count(revenueSql, "marketplace_finance_transactions"));
    }

    private static int Count(string text, string word)
    {
        var count = 0;
        for (var index = text.IndexOf(word, StringComparison.Ordinal); index >= 0; index = text.IndexOf(word, index + word.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // Decimals compared as text, so a different scale (12.50 against 12.5) fails as well.
    private static List<string> Text<T>(IEnumerable<T> rows) => rows.Select(row => row!.ToString()!).OrderBy(x => x, StringComparer.Ordinal).ToList();

    private async Task SeedAsync(Tenant tenant)
    {
        var trendyol = NewConnection(tenant.Id, "TRENDYOL", "ACTIVE");
        var hepsiburada = NewConnection(tenant.Id, "HEPSIBURADA", "ACTIVE");
        var paused = NewConnection(tenant.Id, "N11", "DRAFT");
        var hidden = NewConnection(tenant.Id, "PAZARAMA", "HIDDEN");
        var stage = NewConnection(tenant.Id, "HEPSIBURADA", "ACTIVE");
        stage.Environment = "STAGE";
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.AddRange(trendyol, hepsiburada, paused, hidden, stage);

        var ordinal = 0;
        // Times across both periods of every case, with Istanbul's day (21:00Z) and hour edges.
        DateTimeOffset[] times =
        [
            new(2026, 10, 20, 7, 29, 59, TimeSpan.Zero), new(2026, 10, 20, 7, 30, 0, TimeSpan.Zero), new(2026, 10, 19, 21, 0, 0, TimeSpan.Zero),
            new(2026, 10, 19, 20, 59, 59, TimeSpan.Zero), new(2026, 10, 19, 7, 0, 0, TimeSpan.Zero), new(2026, 10, 19, 6, 59, 59, TimeSpan.Zero),
            new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero), new(2026, 10, 15, 20, 30, 0, TimeSpan.Zero), new(2026, 10, 14, 22, 30, 0, TimeSpan.Zero),
            new(2026, 10, 14, 10, 15, 0, TimeSpan.Zero), new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero), new(2026, 10, 3, 4, 0, 0, TimeSpan.Zero),
            new(2026, 9, 20, 15, 45, 0, TimeSpan.Zero), new(2026, 8, 5, 8, 0, 0, TimeSpan.Zero)
        ];
        foreach (var at in times)
        {
            Order Add(PlatformConnection store, decimal amount, string status = "DELIVERED", string currency = "TRY")
            {
                var order = NewOrder(tenant.Id, store.Id, $"o{++ordinal}", at, amount, status);
                order.Currency = currency;
                db.Orders.Add(order);
                return order;
            }
            OrderLine Line(Order order, decimal price, decimal quantity, decimal? discount = null, decimal cancelled = 0m, decimal returned = 0m)
            {
                var line = NewLine(order, $"l{++ordinal}", price, quantity);
                line.PlatformDiscount = discount;
                line.CancelledQuantity = cancelled;
                line.ShippedQuantity = returned;
                line.DeliveredQuantity = returned;
                line.ReturnedQuantity = returned;
                db.OrderLines.Add(line);
                return line;
            }
            ShipmentPackage Package(Order order, string id, decimal net, decimal gross, ShipmentPackageStatus status, string? origin = null)
            {
                var package = NewPackage(order, id, net, status);
                package.GrossAmount = gross;
                package.OriginExternalPackageId = origin;
                db.ShipmentPackages.Add(package);
                return package;
            }
            void Claim(Order order, OrderLine line, decimal quantity, ReturnClaimStatus status, string? lineStatus = null, string? request = null, string raw = "Completed")
            {
                var claim = new ReturnClaim
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenant.Id,
                    ConnectionId = order.ConnectionId,
                    OrderId = order.Id,
                    ExternalClaimId = $"c{++ordinal}",
                    Status = status,
                    RawStatus = raw,
                    CustomerRequest = request,
                    LastRemoteModifiedAt = Now,
                    CreatedAt = Now,
                    UpdatedAt = Now
                };
                db.ReturnClaims.Add(claim);
                db.ReturnLines.Add(new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, OrderLineId = line.Id, ExternalLineId = $"cl{++ordinal}", Quantity = quantity, RawStatus = lineStatus });
            }

            // A plain sale with two lines, one partly cancelled.
            var plain = Add(trendyol, 449.90m);
            Line(plain, 199.95m, 2m);
            Line(plain, 50m, 3m, cancelled: 1m);
            Package(plain, $"p{++ordinal}", 449.90m, 520m, ShipmentPackageStatus.Delivered);

            // A split order: one package cancelled; a resent package replaces its original.
            var split = Add(trendyol, 900m);
            Line(split, 300m, 3m, discount: 12.35m);
            Package(split, $"p{++ordinal}", 300m, 330m, ShipmentPackageStatus.Delivered);
            Package(split, $"p{++ordinal}", 300m, 330m, ShipmentPackageStatus.Cancelled);
            var original = Package(split, $"p{++ordinal}", 300m, 330m, ShipmentPackageStatus.Returned);
            Package(split, $"p{++ordinal}", 300m, 330m, ShipmentPackageStatus.Shipped, origin: original.ExternalPackageId);

            // Trendyol's payment placeholder beside its real package.
            var placeholder = Add(trendyol, 250m);
            Line(placeholder, 250m, 1m);
            Package(placeholder, "0", 250m, 250m, ShipmentPackageStatus.New);
            Package(placeholder, $"p{++ordinal}", 250m, 275m, ShipmentPackageStatus.Delivered);

            // A partial return through an accepted claim, a rejected line beside it, and a returned package whose line
            // carries the marketplace's discount.
            var partial = Add(hepsiburada, 1094.15m);
            var kept = Line(partial, 694.15m, 1m, discount: 194.85m);
            var back = Line(partial, 400m, 1m, discount: 20m);
            var returnedPackage = Package(partial, $"p{++ordinal}", 400m, 400m, ShipmentPackageStatus.Returned);
            Package(partial, $"p{++ordinal}", 694.15m, 694.15m, ShipmentPackageStatus.Delivered);
            db.PackageLineAllocations.Add(new PackageLineAllocation { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PackageId = returnedPackage.Id, OrderLineId = back.Id, AllocatedQuantity = 1m, SourceEventId = $"s{++ordinal}" });
            Claim(partial, back, 1m, ReturnClaimStatus.Completed);
            Claim(partial, kept, 1m, ReturnClaimStatus.Completed, lineStatus: "Rejected");

            // Returned quantities on the line only, an exchange not refunded and one refunded.
            var exchanged = Add(trendyol, 777.77m);
            var exchangeLine = Line(exchanged, 259.259m, 3m, returned: 1m);
            Package(exchanged, $"p{++ordinal}", 777.77m, 800m, ShipmentPackageStatus.Delivered);
            Claim(exchanged, exchangeLine, 1m, ReturnClaimStatus.Approved, request: ReturnCustomerRequestPolicy.Change, raw: "Approved");
            Claim(exchanged, exchangeLine, 2m, ReturnClaimStatus.Approved, lineStatus: "accepted", request: ReturnCustomerRequestPolicy.Change, raw: "Refunded");

            // A whole return, and a cancelled order (not counted).
            var whole = Add(trendyol, 333.33m, "RETURNED");
            Line(whole, 333.33m, 1m, returned: 1m);
            Package(whole, $"p{++ordinal}", 333.33m, 350m, ShipmentPackageStatus.Returned);
            var cancelledOrder = Add(trendyol, 120m, "CANCELLED");
            Line(cancelledOrder, 120m, 1m, cancelled: 1m);

            // Micro-export returns: Romania with no statement refund keeps its sale, Azerbaijan's statement refunded it.
            var romania = Add(trendyol, 584.75m, "RETURNED");
            romania.StoreFrontCode = "RO";
            var romaniaLine = Line(romania, 584.75m, 1m, returned: 1m);
            Package(romania, $"p{++ordinal}", 584.75m, 600m, ShipmentPackageStatus.Returned);
            Claim(romania, romaniaLine, 1m, ReturnClaimStatus.Completed);
            var azerbaijan = Add(trendyol, 410m);
            azerbaijan.StoreFrontCode = "AZ";
            var azerbaijanLine = Line(azerbaijan, 205m, 2m, returned: 1m);
            Package(azerbaijan, $"p{++ordinal}", 410m, 410m, ShipmentPackageStatus.Delivered);
            Claim(azerbaijan, azerbaijanLine, 1m, ReturnClaimStatus.Completed);
            db.MarketplaceFinanceTransactions.Add(new MarketplaceFinanceTransaction
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ConnectionId = trendyol.Id,
                PlatformCode = "TRENDYOL",
                Source = "SETTLEMENTS",
                ExternalId = $"f{++ordinal}",
                RequestedType = "Return",
                TransactionType = "İade",
                Category = MarketplaceFinanceCategories.Return,
                TransactionDate = Now,
                OrderNumber = azerbaijan.OrderNumber,
                Debt = 205m,
                RawJson = "{}",
                FirstSeenAt = Now,
                UpdatedAt = Now
            });

            // Other currencies and stores; an order with no packages, one whose packages carry no amount, one without lines.
            var euro = Add(trendyol, 49.99m, currency: "EUR");
            Line(euro, 49.99m, 1m);
            Package(euro, $"p{++ordinal}", 49.99m, 59.99m, ShipmentPackageStatus.Delivered);
            var unpackaged = Add(paused, 625m);
            Line(unpackaged, 312.5m, 2m, discount: 25m);
            var unpriced = Add(hepsiburada, 199m);
            Line(unpriced, 99.5m, 2m);
            Package(unpriced, $"p{++ordinal}", 0m, 0m, ShipmentPackageStatus.Delivered);
            var noLines = Add(trendyol, 88.88m);
            noLines.NetAmount = 0m;
            Package(noLines, $"p{++ordinal}", 0m, 88.88m, ShipmentPackageStatus.Delivered);
            Line(Add(hidden, 500m), 500m, 1m);
            Line(Add(stage, 1m), 1m, 1m);
        }
        await db.SaveChangesAsync();
    }

    private static Tenant NewTenant(string code) => new()
    {
        Id = Guid.CreateVersion7(),
        Code = $"{code}-{Guid.NewGuid():N}"[..20],
        DisplayName = code,
        Status = RecordStatus.Active,
        Timezone = Timezone,
        CreatedAt = Now,
        UpdatedAt = Now,
        Version = 1
    };

    private static PlatformConnection NewConnection(Guid tenantId, string platformCode, string status) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = $"{platformCode} {status}",
        ExternalStoreId = $"store-{Guid.NewGuid():N}",
        Status = status,
        ApiVersion = "V1"
    };

    private static Order NewOrder(Guid tenantId, Guid connectionId, string code, DateTimeOffset orderedAt, decimal amount, string status) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        ExternalOrderId = code,
        OrderNumber = code,
        Currency = "TRY",
        GrossAmount = amount,
        NetAmount = amount,
        OrderedAt = orderedAt,
        LastRemoteModifiedAt = orderedAt,
        CustomerSnapshotJson = "{}",
        ShipmentAddressSnapshotJson = "{}",
        InvoiceAddressSnapshotJson = "{}",
        DerivedStatus = status,
        CreatedAt = Now,
        UpdatedAt = Now,
        Version = 1
    };

    private static ShipmentPackage NewPackage(Order order, string externalId, decimal amount, ShipmentPackageStatus status) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = order.TenantId,
        ConnectionId = order.ConnectionId,
        OrderId = order.Id,
        ExternalPackageId = externalId,
        GrossAmount = amount,
        NetAmount = amount,
        Status = status,
        RawStatus = status.ToString(),
        StatusOccurredAt = Now,
        MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.NotInvoiced,
        CreatedAt = Now,
        UpdatedAt = Now,
        Version = 1
    };

    private static OrderLine NewLine(Order order, string externalId, decimal unitPrice, decimal quantity) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = order.TenantId,
        OrderId = order.Id,
        ExternalLineId = externalId,
        Sku = externalId,
        TitleSnapshot = externalId,
        OrderedQuantity = quantity,
        UnitPrice = unitPrice,
        VatRate = 20m,
        RawStatus = "Delivered"
    };

    private async Task DeleteTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceFinanceTransactions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PackageLineAllocations.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnClaims.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.DashboardSnapshots.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
