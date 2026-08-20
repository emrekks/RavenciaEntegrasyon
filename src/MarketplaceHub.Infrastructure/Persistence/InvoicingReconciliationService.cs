using System.Globalization;
using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class InvoicingReconciliationService(AppDbContext db) : IInvoicingReconciliationService
{
    public async Task<ServiceResult<InvoiceReconciliationView>> RunLocalDryAsync(Guid tenantId, Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invoiceId, cancellationToken);
        if (invoice is null) return ServiceResult<InvoiceReconciliationView>.Fail("RESOURCE_NOT_FOUND", "Fatura bulunamadı.", 404);

        var lines = await db.InvoiceLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.InvoiceId == invoiceId).ToListAsync(cancellationToken);
        var differences = new List<ReconciliationDifferenceView>();
        var calculated = lines.Sum(x => x.LineTotal);
        if (calculated != invoice.PayableTotal)
            differences.Add(new("INVOICE", invoice.Id.ToString("D"), "PAYABLE_TOTAL", Money(invoice.PayableTotal), Money(calculated), "LOCAL_REVIEW_REQUIRED"));
        if (invoice.TaxExclusiveTotal + invoice.TaxTotal - invoice.DiscountTotal != invoice.PayableTotal)
            differences.Add(new("INVOICE", invoice.Id.ToString("D"), "TOTAL_EQUATION", Money(invoice.PayableTotal), Money(invoice.TaxExclusiveTotal + invoice.TaxTotal - invoice.DiscountTotal), "LOCAL_REVIEW_REQUIRED"));

        return ServiceResult<InvoiceReconciliationView>.Ok(new(invoice.Id, differences.Count == 0 ? "COMPLETED_LOCAL_DRY" : "DIFFERENCES_FOUND", differences));
    }

    private static string Money(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
