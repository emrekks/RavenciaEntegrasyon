using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

/// <summary>
/// The last check right before an approved sale invoice is sent to the provider (owner's rule of 5 Nov 2026): the
/// draft gates are applied again to the order as it is now. The order or a line cancelled, units returned since the
/// approval (every unit, or some: the invoice then covers fewer units, owner's rule of 6 Nov 2026), a package
/// undelivered or waiting for payment, the line VAT unknown, a product line that could not be read, or the amounts the order gives now differing from the
/// approved draft (its lines, quantities, prices, VAT and totals) all stop the issue: the invoice goes back to wait
/// for the owner's approval with the reason, and is never issued with an amount recomputed on its own.
/// </summary>
internal static class InvoicePreIssueCheck
{
    // Every unit of the order still ordered came back (InvoiceSaleReturnPolicy): no sale invoice is approved or issued.
    public static async Task<bool> IsFullyReturnedAsync(AppDbContext db, Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        var returned = await InvoiceSaleReturns.ReturnedUnitsAsync(db, tenantId, orderId, cancellationToken);
        if (returned.Count == 0) return false;
        var lines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == orderId).ToListAsync(cancellationToken);
        return InvoiceSaleReturnPolicy.Classify(InvoiceSaleReturns.Units(lines, returned)) == InvoiceSaleReturnScope.Full;
    }

    public static async Task<InvoiceIssueRefusal?> CheckAsync(AppDbContext db, Invoice invoice, CancellationToken cancellationToken)
    {
        if (invoice.SequencePurpose != "SALE" || invoice.OriginalInvoiceId is not null) return null;
        var tenantId = invoice.TenantId;
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invoice.OrderId, cancellationToken);
        if (order is null) return Changed("Sipariş kaydı okunamadı.");
        if (DashboardMetricPolicy.InvoiceExcludedOrderStatuses.Contains(order.DerivedStatus) || DashboardMetricPolicy.IsSaleInvoiceBlockedOrder(order.DerivedStatus))
            return Changed($"Sipariş artık {OrderStatusText(order.DerivedStatus)}.");
        var packages = await MarketplaceSalesService.CurrentShipmentPackages(db)
            .Where(x => x.TenantId == tenantId && x.OrderId == order.Id && x.Status != ShipmentPackageStatus.Cancelled)
            .OrderBy(x => x.Id).ToListAsync(cancellationToken);
        if (packages.Count == 0) return Changed("Siparişte faturalanabilir etkin paket kalmadı (paketler iptal edildi).");
        if (packages.FirstOrDefault(x => DashboardMetricPolicy.IsSaleInvoiceBlockedPackage(x.Status)) is { } blocked)
            return Changed($"{blocked.ExternalPackageId} paketi artık {PackageStatusText(blocked.Status)}.");
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == order.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        // A package holding a product line that could not be read is incomplete: nothing is issued until a read fills it in.
        if (packages.Any(x => x.UnreadableLineCount > 0)) return InvoiceIssuePolicy.OrderLineUnreadableFor(platformCode);
        // A legacy invoice bound to one package was drafted from that package; only the status gates apply to it, a
        // returned package among them as before 6 Nov 2026.
        if (invoice.PackageId is not null)
            return packages.FirstOrDefault(x => x.Status == ShipmentPackageStatus.Returned) is { } returnedPackage
                ? Changed($"{returnedPackage.ExternalPackageId} paketi artık {PackageStatusText(returnedPackage.Status)}.")
                : null;

        var orderLines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var packageIds = packages.Select(x => x.Id).ToArray();
        var allocations = await db.PackageLineAllocations.AsNoTracking().Where(x => x.TenantId == tenantId && packageIds.Contains(x.PackageId)).ToListAsync(cancellationToken);
        if (!InvoicingBillingService.TryResolveWholeOrderInvoiceLines(orderLines, allocations, packageIds, out var activeLines, out var allocationError))
            return Changed(allocationError ?? "Sipariş satırları paketlerle eşleşmiyor.");
        // Returns that came after the approval change the lines and the amount: the issue stops and asks the owner
        // again (owner's rule of 5 Nov 2026), the remainder is never issued on its own.
        var returned = await InvoiceSaleReturns.ReturnedUnitsAsync(db, tenantId, order.Id, cancellationToken);
        var units = InvoiceSaleReturns.Units(activeLines, returned);
        if (InvoiceSaleReturnPolicy.Classify(units) == InvoiceSaleReturnScope.Full) return InvoiceIssuePolicy.OrderFullyReturned;
        var keptLines = activeLines.Zip(units).Where(pair => pair.Second.KeptQuantity > 0).Select(pair => pair.First).ToList();
        var rates = await InvoiceLineVatRates.ResolveAsync(db, tenantId, platformCode, order.ConnectionId, keptLines, cancellationToken);
        if (!InvoiceLineVatRates.TryApply(keptLines, rates, out var vatRates)) return InvoiceIssuePolicy.LineVatRateUnknownFor(platformCode);
        var totals = InvoiceAmounts.ResolveOrderTotals(order.NetAmount, order.GrossAmount, order.DiscountAmount,
            packages.Select(x => new InvoicePackageTotalSource(x.NetAmount, x.GrossAmount, x.DiscountAmount)).ToArray());
        var plan = InvoiceSaleReturns.Plan(activeLines, returned, line => vatRates[line.Id], totals);
        if (!InvoiceAmounts.TryCalculatePackage(plan.Lines, plan.Totals.GrossAmount, plan.Totals.DiscountAmount, plan.Totals.PayableAmount, out var calculated, out var calculationError))
            return Changed(calculationError ?? "Sipariş tutarı ürün satırlarıyla eşleşmiyor.");

        var invoiceLines = await db.InvoiceLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id).OrderBy(x => x.LineSequence).ToListAsync(cancellationToken);
        return Describe(invoice, invoiceLines, calculated!) is { } difference ? Changed(difference) : null;
    }

    // A current, not cancelled package of the order holds a product line the marketplace listed but the panel could not
    // read (ShipmentPackage.UnreadableLineCount): the draft gates refuse the order with InvoiceIssuePolicy.OrderLineUnreadable.
    public static Task<bool> HasUnreadableLinesAsync(AppDbContext db, Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        MarketplaceSalesService.CurrentShipmentPackages(db).AnyAsync(x => x.TenantId == tenantId && x.OrderId == orderId
            && x.Status != ShipmentPackageStatus.Cancelled && x.UnreadableLineCount > 0, cancellationToken);

    // What differs between the approved draft and the order as it is now, in words; null when they are the same.
    internal static string? Describe(Invoice invoice, IReadOnlyList<InvoiceLine> invoiceLines, InvoicePackageCalculation now)
    {
        if (invoice.PayableTotal != now.PayableTotal)
            return string.Create(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), $"Fatura tutarı onaylanan {invoice.PayableTotal:N2} yerine şimdi {now.PayableTotal:N2}.");
        if (invoice.TaxExclusiveTotal != now.TaxExclusiveTotal || invoice.TaxTotal != now.TaxTotal || invoice.DiscountTotal != now.DiscountTotal)
            return "Faturanın KDV, indirim veya KDV hariç tutarı onaylanandan farklı.";
        if (invoiceLines.Count != now.Lines.Count)
            return $"Faturalanacak ürün satırı sayısı onaylanan {invoiceLines.Count} yerine şimdi {now.Lines.Count} (iptal veya iade edilen ya da eklenen satır).";
        foreach (var (stored, current) in invoiceLines.Zip(now.Lines))
        {
            if (stored.OrderLineId is { } orderLineId && orderLineId != current.OrderLineId)
                return "Faturalanacak ürün satırları onaylananlardan farklı.";
            if (stored.Quantity != current.Quantity)
                return $"“{stored.DescriptionSnapshot}” adedi onaylanan {stored.Quantity:0.##} yerine şimdi {current.Quantity:0.##} (kısmi iptal veya iade).";
            if (stored.UnitPrice != current.UnitPrice || stored.DiscountAmount != current.DiscountAmount || stored.LineTotal != current.PayableAmount)
                return $"“{stored.DescriptionSnapshot}” fiyatı veya indirimi onaylanandan farklı.";
            if (stored.VatRate != current.VatRate || stored.VatAmount != current.VatAmount)
                return $"“{stored.DescriptionSnapshot}” KDV oranı veya tutarı onaylanandan farklı.";
        }
        return null;
    }

    private static InvoiceIssueRefusal Changed(string detail) => new(InvoiceIssuePolicy.PreIssueChangedCode,
        $"Fatura kesilmeden hemen önceki son kontrolde sipariş onaydan sonra değişmiş: {detail} Fatura kesilmedi ve yeniden onayınızı bekliyor; güncel önizlemeyi açıp kontrol edin.");

    private static string OrderStatusText(string? status) => status?.Trim().ToUpperInvariant() switch
    {
        "CANCELLED" or "CANCELED" => "iptal edildi",
        "RETURNED" => "iade edildi",
        "UNDELIVERED" => "teslim edilemedi",
        "PAYMENT_PENDING" => "ödeme bekliyor",
        _ => "faturalanamaz durumda"
    };

    private static string PackageStatusText(ShipmentPackageStatus status) => status switch
    {
        ShipmentPackageStatus.Returned => "iade edildi",
        ShipmentPackageStatus.Undelivered => "teslim edilemedi",
        ShipmentPackageStatus.PaymentPending => "ödeme bekliyor",
        ShipmentPackageStatus.Cancelled => "iptal edildi",
        _ => "faturalanamaz durumda"
    };
}
