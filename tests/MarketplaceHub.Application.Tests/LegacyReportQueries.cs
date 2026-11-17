using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Application.Tests;

// The reports' per-order and per-line reads as they were before 15 Nov 2026 (audit FATURA-FINANS-4), each per-order value
// inlined by EF wherever it was used. Kept only so PostgreSqlDashboardEquivalenceTests can prove that the rewrite, on
// the overview's grouped reads (OrderRevenueQuery), gives the same rows on the same data.
internal static class LegacyReportQueries
{
    internal static IQueryable<ReportOrderRow> OrderRows(AppDbContext db, Guid tenantId, IQueryable<ReportsService.PeriodOrder> periodOrders)
    {
        var acceptedClaims = LegacyAcceptedReturnRule.ClaimStatuses;
        var acceptedLines = LegacyAcceptedReturnRule.LineRawStatuses;
        var currentPackages = MarketplaceSalesService.CurrentShipmentPackages(db).Where(package => package.TenantId == tenantId);
        var orderLines = db.OrderLines.AsNoTracking().Where(line => line.TenantId == tenantId);
        var allocations = db.PackageLineAllocations.AsNoTracking().Where(allocation => allocation.TenantId == tenantId);
        var unbookedReturns = DashboardRevenuePolicy.OrdersWithUnbookedReturn(db, tenantId);
        return from row in periodOrders
               let order = row.Order
               let priced = currentPackages.Any(package => package.OrderId == order.Id && (package.NetAmount > 0 || package.GrossAmount > 0))
               let paid = priced
                   ? currentPackages.Where(package => package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled)
                       .Sum(package => package.NetAmount > 0 ? package.NetAmount : package.GrossAmount)
                   : (order.NetAmount > 0 ? order.NetAmount : order.GrossAmount)
               let listAmount = priced
                   ? currentPackages.Where(package => package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled)
                       .Sum(package => package.GrossAmount > 0 ? package.GrossAmount : package.NetAmount)
                   : (order.GrossAmount > 0 ? order.GrossAmount : order.NetAmount)
               let platformDiscount = orderLines.Where(line => line.OrderId == order.Id && line.PlatformDiscount != null)
                   .Sum(line => line.PlatformDiscount!.Value * (line.OrderedQuantity - line.CancelledQuantity))
               let lineValue = orderLines.Where(line => line.OrderId == order.Id)
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
                        join line in orderLines on allocation.OrderLineId equals line.Id
                        where line.PlatformDiscount != null
                        select line.PlatformDiscount!.Value * allocation.AllocatedQuantity).Sum()
                   : 0m
               let lineBack = orderLines.Where(line => line.OrderId == order.Id)
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
               select new ReportOrderRow
               {
                   Id = order.Id,
                   ConnectionId = order.ConnectionId,
                   OrderNumber = order.OrderNumber,
                   Currency = order.Currency,
                   DerivedStatus = order.DerivedStatus,
                   Amount = amount,
                   ListAmount = listAmount,
                   OrderListAmount = order.GrossAmount,
                   PackagesBack = packagesBack,
                   LineValue = lineValue,
                   LineBack = lineBack,
                   RealAt = row.RealAt,
                   OrderedAt = order.OrderedAt,
                   ShipmentDueAt = order.ShipmentDueAt,
                   StoreFrontCode = order.StoreFrontCode,
                   ReturnBooked = returnBooked,
                   UnreadableLines = currentPackages.Where(package => package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled)
                       .Sum(package => package.UnreadableLineCount)
               };
    }

    internal static IQueryable<ReportLineRow> LineRows(AppDbContext db, Guid tenantId, IQueryable<ReportsService.PeriodOrder> periodOrders)
    {
        var acceptedClaims = LegacyAcceptedReturnRule.ClaimStatuses;
        var acceptedLines = LegacyAcceptedReturnRule.LineRawStatuses;
        var unbookedReturns = DashboardRevenuePolicy.OrdersWithUnbookedReturn(db, tenantId);
        return from row in periodOrders
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
                   PlatformDiscount = line.PlatformDiscount ?? 0m,
                   OrderedQuantity = line.OrderedQuantity,
                   CancelledQuantity = line.CancelledQuantity,
                   ReturnedQuantity = unbookedReturns.Contains(row.Order.Id) ? 0m : line.ReturnedQuantity,
                   AcceptedReturnQuantity = unbookedReturns.Contains(row.Order.Id)
                       ? 0m
                       : db.ReturnLines.Where(returned => returned.TenantId == tenantId && returned.OrderLineId == line.Id
                           && db.ReturnClaims.Any(claim => claim.TenantId == tenantId && claim.Id == returned.ClaimId && (claim.CustomerRequest != ReturnCustomerRequestPolicy.Change || claim.RawStatus.ToUpper() == ReturnCustomerRequestPolicy.RefundedRawStatus)
                               && (returned.RawStatus == null || returned.RawStatus == ""
                                   ? acceptedClaims.Contains(claim.Status)
                                   : acceptedLines.Contains(returned.RawStatus.ToUpper()))))
                       .Sum(returned => (decimal?)returned.Quantity) ?? 0m,
                   CostPrice = line.UnitCostSnapshot ?? (variant == null ? null : variant.CostPrice),
                   VatRate = line.VatRate,
                   Desi = line.DesiSnapshot ?? (variant == null ? null : variant.Desi)
               };
    }
}
