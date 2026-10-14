using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace MarketplaceHub.Infrastructure.Persistence;

public static class ManualOrderInvoiceDocumentLink
{
    private const string Purpose = "MarketplaceHub.ManualOrderInvoiceDocument.v1";

    public static string Create(IDataProtectionProvider provider, string origin, Guid tenantId, Guid documentId)
    {
        if (!ProductPublicationComposer.IsPublicHttpsUrl(origin)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var baseUri)
            || baseUri.AbsolutePath != "/" || baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0)
            throw new ArgumentException("Geçerli herkese açık HTTPS belge sunucusu ayarı gereklidir.", nameof(origin));
        var token = provider.CreateProtector(Purpose).Protect($"{tenantId:N}:{documentId:N}");
        return new Uri(baseUri, $"/api/v1/public/manual-invoice-documents/{token}/content").AbsoluteUri;
    }

    public static bool TryRead(IDataProtectionProvider provider, string token, out Guid tenantId, out Guid documentId)
    {
        tenantId = documentId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024) return false;
        try
        {
            var values = provider.CreateProtector(Purpose).Unprotect(token).Split(':');
            return values.Length == 2
                && Guid.TryParseExact(values[0], "N", out tenantId)
                && Guid.TryParseExact(values[1], "N", out documentId);
        }
        catch (CryptographicException) { return false; }
    }
}
