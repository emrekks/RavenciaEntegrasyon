using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Infrastructure.Persistence.DashboardReadService;

namespace MarketplaceHub.Application.Tests;

// The overview's per-order queries as they were before 10 Nov 2026, each per-order value inlined by EF wherever it was
// used (an 815 KB revenue statement on live). Kept only so PostgreSqlDashboardEquivalenceTests can prove that the
// rewrite (DashboardReadService.PerformanceOrders) gives the same figures on the same data.
internal static class LegacyDashboardPerformanceQueries
{
    private static readonly ReturnClaimStatus[] AcceptedClaims = DashboardRevenuePolicy.AcceptedReturnStatuses;
    private static readonly string[] AcceptedLines = DashboardRevenuePolicy.AcceptedReturnLineRawStatuses;

    internal static async Task<(List<RevenueGroup> Revenue, List<UnitGroup> Units, List<PackageGroup> Packages)> GroupsAsync(AppDbContext db, Guid tenantId, DashboardPerformancePeriod period, string timezone, bool hourly)
    {
        var orders = PerformanceOrders(db, tenantId, period, timezone, hourly);
        return (await RevenueQuery(orders).ToListAsync(), await UnitQuery(db, orders).ToListAsync(), await PackageQuery(db, orders).ToListAsync());
    }

    internal static IQueryable<UnitGroup> UnitQuery(AppDbContext db, IQueryable<PerformanceOrderRow> orders) =>
        from row in orders
        join line in db.OrderLines.AsNoTracking() on new { row.TenantId, OrderId = row.Id } equals new { line.TenantId, line.OrderId }
        where !row.Returned
        let active = line.OrderedQuantity - line.CancelledQuantity
        let accepted = !row.ReturnBooked ? 0m : db.ReturnLines.Where(returned => returned.TenantId == line.TenantId && returned.OrderLineId == line.Id
                && db.ReturnClaims.Any(claim => claim.TenantId == returned.TenantId && claim.Id == returned.ClaimId && (claim.CustomerRequest != ReturnCustomerRequestPolicy.Change || claim.RawStatus.ToUpper() == ReturnCustomerRequestPolicy.RefundedRawStatus)
                    && (returned.RawStatus == null || returned.RawStatus == ""
                        ? AcceptedClaims.Contains(claim.Status)
                        : AcceptedLines.Contains(returned.RawStatus.ToUpper()))))
            .Sum(returned => (decimal?)returned.Quantity) ?? 0m
        let returned = row.ReturnBooked ? line.ReturnedQuantity : 0m
        let back = returned > accepted ? returned : accepted
        group new { active, back } by new { row.Day, row.ConnectionId, row.Currency } into grouped
        select new UnitGroup(grouped.Key.Day, grouped.Key.ConnectionId, grouped.Key.Currency,
            grouped.Sum(x => x.active > x.back ? x.active - x.back : 0m));

    internal static IQueryable<PackageGroup> PackageQuery(AppDbContext db, IQueryable<PerformanceOrderRow> orders) =>
        from row in orders
        join package in MarketplaceSalesService.CurrentShipmentPackages(db) on new { row.TenantId, OrderId = row.Id } equals new { package.TenantId, package.OrderId }
        where !row.Returned && package.Status != ShipmentPackageStatus.Cancelled && (package.Status != ShipmentPackageStatus.Returned || !row.ReturnBooked)
        group package by new { row.Day, row.ConnectionId, row.Currency } into grouped
        select new PackageGroup(grouped.Key.Day, grouped.Key.ConnectionId, grouped.Key.Currency, grouped.Count());

    internal static IQueryable<PerformanceOrderRow> PerformanceOrders(AppDbContext db, Guid tenantId, DashboardPerformancePeriod period, string timezone, bool hourly = false)
    {
        var cancelled = DashboardRevenuePolicy.CancelledStatuses;
        var returnedStatuses = DashboardRevenuePolicy.ReturnedStatuses;
        var excludedConnections = DashboardRevenuePolicy.ExcludedConnectionStatuses;
        var acceptedClaims = DashboardRevenuePolicy.AcceptedReturnStatuses;
        var acceptedLines = DashboardRevenuePolicy.AcceptedReturnLineRawStatuses;
        var previousStart = period.PreviousStart;
        var previousEnd = period.PreviousEnd;
        var currentStart = period.CurrentStart;
        var currentEnd = period.CurrentEnd;
        var currentPackages = MarketplaceSalesService.CurrentShipmentPackages(db).Where(package => package.TenantId == tenantId);
        var lines = db.OrderLines.AsNoTracking().Where(line => line.TenantId == tenantId);
        var allocations = db.PackageLineAllocations.AsNoTracking().Where(allocation => allocation.TenantId == tenantId);
        var unbookedReturns = DashboardRevenuePolicy.OrdersWithUnbookedReturn(db, tenantId);
        return from order in db.Orders.AsNoTracking()
               join connection in db.PlatformConnections.AsNoTracking() on new { order.TenantId, Id = order.ConnectionId } equals new { connection.TenantId, connection.Id }
               where order.TenantId == tenantId
                   && !excludedConnections.Contains(connection.Status)
                   && connection.Environment.ToUpper() == "PRODUCTION"
                   && !cancelled.Contains(order.DerivedStatus)
                   && (order.NetAmount > 0 || order.GrossAmount > 0)
               let realAt = order.OrderedAt
               where (realAt >= previousStart && realAt < previousEnd) || (realAt >= currentStart && realAt < currentEnd)
               let priced = currentPackages.Any(package => package.OrderId == order.Id && (package.NetAmount > 0 || package.GrossAmount > 0))
               let paid = priced
                   ? currentPackages.Where(package => package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled)
                       .Sum(package => package.NetAmount > 0 ? package.NetAmount : package.GrossAmount)
                   : (order.NetAmount > 0 ? order.NetAmount : order.GrossAmount)
               let listAmount = priced
                   ? currentPackages.Where(package => package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled)
                       .Sum(package => package.GrossAmount > 0 ? package.GrossAmount : package.NetAmount)
                   : (order.GrossAmount > 0 ? order.GrossAmount : order.NetAmount)
               let platformDiscount = lines.Where(line => line.OrderId == order.Id && line.PlatformDiscount != null)
                   .Sum(line => line.PlatformDiscount!.Value * (line.OrderedQuantity - line.CancelledQuantity))
               let lineValue = lines.Where(line => line.OrderId == order.Id)
                   .Sum(line => (line.UnitPrice + (line.PlatformDiscount ?? 0m)) * (line.OrderedQuantity - line.CancelledQuantity))
               let ceiling = listAmount > paid ? (listAmount > lineValue ? listAmount : lineValue) : (paid > lineValue ? paid : lineValue)
               let amount = paid + platformDiscount > ceiling ? ceiling : paid + platformDiscount
               let returnBooked = !unbookedReturns.Contains(order.Id)
               let packagesBack = priced && returnBooked
                   ? currentPackages.Where(package => package.OrderId == order.Id && package.Status == ShipmentPackageStatus.Returned)
                       .Sum(package => package.NetAmount > 0 ? package.NetAmount : package.GrossAmount)
                     + (from package in currentPackages
                        where package.OrderId == order.Id && package.Status == ShipmentPackageStatus.Returned
                        join allocation in allocations on package.Id equals allocation.PackageId
                        join line in lines on allocation.OrderLineId equals line.Id
                        where line.PlatformDiscount != null
                        select line.PlatformDiscount!.Value * allocation.AllocatedQuantity).Sum()
                   : 0m
               let lineBack = lines.Where(line => line.OrderId == order.Id)
                   .Select(line => new
                   {
                       UnitPrice = line.UnitPrice + (line.PlatformDiscount ?? 0m),
                       Active = line.OrderedQuantity - line.CancelledQuantity,
                       ReturnedQuantity = returnBooked ? line.ReturnedQuantity : 0m,
                       Accepted = !returnBooked ? 0m : db.ReturnLines.Where(returned => returned.TenantId == tenantId && returned.OrderLineId == line.Id
                               && db.ReturnClaims.Any(claim => claim.TenantId == tenantId && claim.Id == returned.ClaimId && (claim.CustomerRequest != ReturnCustomerRequestPolicy.Change || claim.RawStatus.ToUpper() == ReturnCustomerRequestPolicy.RefundedRawStatus)
                                   && (returned.RawStatus == null || returned.RawStatus == ""
                                       ? acceptedClaims.Contains(claim.Status)
                                       : acceptedLines.Contains(returned.RawStatus.ToUpper()))))
                           .Sum(returned => (decimal?)returned.Quantity) ?? 0m
                   })
                   .Sum(line => line.UnitPrice * Math.Min(line.Active, Math.Max(line.ReturnedQuantity, line.Accepted)))
               let linesBack = lineValue > 0 ? amount * lineBack / lineValue : 0m
               select new PerformanceOrderRow
               {
                   Id = order.Id,
                   TenantId = order.TenantId,
                   ConnectionId = order.ConnectionId,
                   Currency = order.Currency,
                   Amount = amount,
                   ReturnedAmount = packagesBack > linesBack ? packagesBack : linesBack,
                   Returned = returnBooked && returnedStatuses.Contains(order.DerivedStatus),
                   ReturnBooked = returnBooked,
                   Day = hourly
                       ? TimeZoneInfo.ConvertTimeBySystemTimeZoneId(realAt.UtcDateTime, timezone).Date.AddHours(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(realAt.UtcDateTime, timezone).Hour)
                       : TimeZoneInfo.ConvertTimeBySystemTimeZoneId(realAt.UtcDateTime, timezone).Date
               };
    }
}
