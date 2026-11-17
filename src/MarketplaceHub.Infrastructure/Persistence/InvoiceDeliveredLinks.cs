using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

/// <summary>
/// The invoice links the panel delivered or may meet on a marketplace (MarketplaceInvoiceLinkPolicy, 17 Nov 2026).
/// A delivery since then records its link on its state row; an older one is read the way it was chosen then: the latest
/// invoice document with a link (the panel's MARKETPLACE_DELIVERY row when one was made, else E-Faturam's PDF row).
/// </summary>
public static class InvoiceDeliveredLinks
{
    public const string MarketplaceDeliveryDocumentType = "MARKETPLACE_DELIVERY";

    // The link the latest invoice document holds: how a delivery recorded before InvoiceLink found its link.
    public static Task<string?> LegacyAsync(AppDbContext db, Guid tenantId, Guid invoiceId, CancellationToken cancellationToken) =>
        db.InvoiceDocuments.AsNoTracking()
            .Where(document => document.TenantId == tenantId && document.InvoiceId == invoiceId && document.PermanentUrl != null)
            .OrderByDescending(document => document.CreatedAt)
            .Select(document => document.PermanentUrl)
            .FirstOrDefaultAsync(cancellationToken);

    // Every link of the invoice: the document links (E-Faturam's URL, the panel link and its .pdf alias) and the links its
    // deliveries recorded. Any of them on a marketplace package is this invoice; none of them is any other invoice's.
    public static async Task<IReadOnlyList<string>> OwnAsync(AppDbContext db, Guid tenantId, Guid invoiceId, CancellationToken cancellationToken)
    {
        var documentLinks = await db.InvoiceDocuments.AsNoTracking()
            .Where(document => document.TenantId == tenantId && document.InvoiceId == invoiceId && document.PermanentUrl != null)
            .OrderByDescending(document => document.CreatedAt)
            .Select(document => document.PermanentUrl!)
            .ToListAsync(cancellationToken);
        var deliveryLinks = await db.MarketplaceDeliveryStates.AsNoTracking()
            .Where(state => state.TenantId == tenantId && state.InvoiceId == invoiceId && state.InvoiceLink != null)
            .Select(state => state.InvoiceLink!)
            .ToListAsync(cancellationToken);
        return Expand(deliveryLinks.Concat(documentLinks));
    }

    public static IReadOnlyList<string> Expand(IEnumerable<string> links)
    {
        var stored = links.Where(link => !string.IsNullOrWhiteSpace(link)).ToList();
        return stored.Concat(stored.Select(InvoiceDocumentLink.PdfAlias)).Distinct(StringComparer.Ordinal).ToList();
    }
}
