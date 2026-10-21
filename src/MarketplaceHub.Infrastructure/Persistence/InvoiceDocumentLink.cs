using System.Security.Cryptography;
using MarketplaceHub.Application;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public static class InvoiceDocumentLink
{
    private const string Purpose = "MarketplaceHub.InvoiceDocumentDelivery.v1";
    private const string PathPrefix = "/api/v1/public/invoice-documents/";
    public const string PdfFileName = "invoice.pdf";

    // The stored link ends in /content; the API serves the same document under /invoice.pdf for
    // marketplaces that require a document extension. Other links are returned unchanged.
    public static string PdfAlias(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)
            || !uri.AbsolutePath.StartsWith(PathPrefix, StringComparison.Ordinal)
            || !uri.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)) return link;
        var builder = new UriBuilder(uri) { Path = uri.AbsolutePath[..^"content".Length] + PdfFileName };
        return builder.Uri.AbsoluteUri;
    }

    // A link has no expiry and its document row is immutable, so it is withdrawn by the revocation itself (G10): once
    // a marketplace link of the invoice was removed and none is left to remove, the public link stops answering.
    // While a package still waits, its marketplace may still be reading the link.
    public static async Task<bool> IsWithdrawnAsync(AppDbContext db, Guid tenantId, Guid invoiceId, CancellationToken cancellationToken)
    {
        var statuses = await db.MarketplaceDeliveryStates.AsNoTracking().Where(state => state.TenantId == tenantId && state.InvoiceId == invoiceId).Select(state => state.Status).ToListAsync(cancellationToken);
        return statuses.Contains(MarketplaceInvoiceLinkRevocationPolicy.Removed) && !statuses.Any(MarketplaceInvoiceLinkRevocationPolicy.IsOpen);
    }

    public static string Create(IDataProtectionProvider provider, string origin, Guid tenantId, Guid invoiceId, Guid documentId)
    {
        if (!ProductPublicationComposer.IsPublicHttpsUrl(origin)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var baseUri)
            || baseUri.AbsolutePath != "/" || baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0)
            throw new ArgumentException("Geçerli HTTPS belge sunucusu ayarı gereklidir.", nameof(origin));
        var token = provider.CreateProtector(Purpose).Protect($"{tenantId:N}:{invoiceId:N}:{documentId:N}");
        return new Uri(baseUri, $"/api/v1/public/invoice-documents/{token}/content").AbsoluteUri;
    }

    public static bool TryRead(IDataProtectionProvider provider, string token, out Guid tenantId, out Guid invoiceId, out Guid documentId)
    {
        tenantId = invoiceId = documentId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024) return false;
        try
        {
            var values = provider.CreateProtector(Purpose).Unprotect(token).Split(':');
            return values.Length == 3 && Guid.TryParseExact(values[0], "N", out tenantId)
                && Guid.TryParseExact(values[1], "N", out invoiceId) && Guid.TryParseExact(values[2], "N", out documentId);
        }
        catch (CryptographicException) { return false; }
    }
}
