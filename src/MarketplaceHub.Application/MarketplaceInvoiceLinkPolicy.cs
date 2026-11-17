namespace MarketplaceHub.Application;

/// <summary>
/// Which link an issued invoice's marketplace delivery carries (owner decision, 17 Nov 2026): E-Faturam's own permanent
/// document URL, the one the panel downloaded the PDF from and checked (the PDF row's PermanentUrl), instead of the panel's
/// public link. One setting, <see cref="SettingKey"/>: <c>Provider</c> (default) or <c>Panel</c> (every marketplace gets the
/// panel's public link again). n11 and Pazarama always get the panel link: their link rule wants a path ending in a
/// document extension (.pdf/.png/.jpeg) and E-Faturam's URL has none (…/api/invoice/documents/download/&lt;number&gt;).
/// A delivery that already carried a link keeps it (MarketplaceDeliveryState.InvoiceLink); this policy only chooses the
/// link of a delivery that has none yet.
/// </summary>
public static class MarketplaceInvoiceLinkPolicy
{
    public const string SettingKey = "Invoicing:MarketplaceInvoiceLink";
    public const string ProviderSource = "Provider";
    public const string PanelSource = "Panel";
    public const string ProviderLinkMissingCode = "INVOICE_PROVIDER_LINK_MISSING";
    public const string ProviderLinkInvalidCode = "INVOICE_PROVIDER_LINK_INVALID";
    public const int MaximumLinkLength = 2048;

    private static readonly HashSet<string> PanelOnlyPlatforms = new(StringComparer.OrdinalIgnoreCase) { "N11", "PAZARAMA" };

    // Anything but an explicit "Panel" keeps the provider link (the owner's default), so a typo cannot silently bring the
    // panel link back.
    public static bool PanelConfigured(string? setting) =>
        string.Equals(setting?.Trim(), PanelSource, StringComparison.OrdinalIgnoreCase);

    public static bool UsesProviderLink(string? platformCode, string? setting) =>
        !PanelConfigured(setting) && !string.IsNullOrWhiteSpace(platformCode) && !PanelOnlyPlatforms.Contains(platformCode);

    /// <summary>
    /// Why the provider URL cannot be delivered (code and the owner's Turkish reason), or null when it can. The URL is
    /// never part of the reason, so a query secret it might carry does not reach a job message or the screen.
    /// </summary>
    public static (string Code, string Message)? ProviderLinkRejection(string? platformCode, string? url)
    {
        var marketplace = MarketplaceName(platformCode);
        if (string.IsNullOrWhiteSpace(url))
            return (ProviderLinkMissingCode, $"E-Faturam'ın doğrulanmış kalıcı belge linki kayıtlı değil; fatura linki {marketplace} pazaryerine gönderilmedi. Belge E-Faturam'dan yeniden alındıktan sonra Tekrar Gönder ile gönderebilirsiniz.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
            return (ProviderLinkInvalidCode, $"E-Faturam'ın kalıcı belge linki güvenli bir https adresi değil; fatura linki {marketplace} pazaryerine gönderilmedi ve panel linkine geçilmedi. E-Faturam'dan kontrol edip Tekrar Gönder ile gönderebilirsiniz.");
        if (uri.AbsoluteUri.Length > MaximumLinkLength)
            return (ProviderLinkInvalidCode, $"E-Faturam'ın kalıcı belge linki {MaximumLinkLength} karakterden uzun; fatura linki {marketplace} pazaryerine gönderilmedi ve panel linkine geçilmedi.");
        if (PanelOnlyPlatforms.Contains(platformCode ?? ""))
            return (ProviderLinkInvalidCode, $"{marketplace} yalnız pdf, png veya jpeg ile biten fatura linklerini kabul ediyor; E-Faturam linki gönderilmedi.");
        return null;
    }

    private static string MarketplaceName(string? platformCode) => platformCode?.ToUpperInvariant() switch
    {
        "TRENDYOL" => "Trendyol",
        "HEPSIBURADA" => "Hepsiburada",
        "N11" => "n11",
        "PAZARAMA" => "Pazarama",
        "PTTAVM" => "PttAVM",
        "SHOPIFY" => "Shopify",
        _ => platformCode ?? "pazaryeri"
    };
}
