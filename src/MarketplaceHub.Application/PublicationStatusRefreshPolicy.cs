using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

/// <summary>
/// Which connections can have a product's publication state read back, and the refusal the seller sees otherwise. The
/// refusal names the store and the reason (not supported, not active, last test failed) instead of listing every
/// marketplace: "Yayın durumunu sorgulamak için etkin Trendyol, Hepsiburada… bağlantısı gerekir" never said which
/// store was refused or why.
/// </summary>
public static class PublicationStatusRefreshPolicy
{
    private static readonly IReadOnlyDictionary<string, string> PlatformNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["TRENDYOL"] = "Trendyol",
        ["HEPSIBURADA"] = "Hepsiburada",
        ["SHOPIFY"] = "Shopify",
        ["N11"] = "N11",
        ["PAZARAMA"] = "Pazarama",
        ["PTTAVM"] = "PTT AVM"
    };

    private static readonly IReadOnlyDictionary<string, string> StatusLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ACTIVE"] = "etkin",
        ["VERIFIED"] = "doğrulandı",
        ["DRAFT"] = "taslak",
        ["DISABLED"] = "pasif",
        ["DELETED"] = "silindi"
    };

    public static bool Supports(string platformCode) => PlatformNames.ContainsKey(platformCode);

    public static string PlatformName(string platformCode) => PlatformNames.TryGetValue(platformCode, out var name) ? name : platformCode;

    /// <summary>Null when the refresh may run; otherwise the sentence the seller sees.</summary>
    public static string? Refusal(PlatformConnection? connection)
    {
        if (connection is null) return "Bağlantı bulunamadı; yayın durumu sorgulanamadı.";
        var store = StoreLabel(connection);
        if (!Supports(connection.PlatformCode))
            return $"{store} için yayın durumu sorgulanamaz; bu pazaryerinde yayın durumu okunmuyor.";
        if (IntegrationRuntimePolicy.AllowsManualRead(connection)) return null;
        if (IntegrationRuntimePolicy.IsStage(connection) && !string.IsNullOrWhiteSpace(connection.LastErrorCode))
            return $"{store} bağlantısının son testi başarısız ({connection.LastErrorCode}); bağlantıyı yeniden test edip tekrar deneyin.";
        var status = StatusLabels.TryGetValue(connection.Status, out var label) ? label : connection.Status;
        return $"{store} bağlantısı etkin değil (durum: {status}); yayın durumu yalnız etkin veya doğrulanmış bağlantıda sorgulanabilir.";
    }

    // "Pazarama · Ravencia", or only the platform when the store carries the platform's own name.
    private static string StoreLabel(PlatformConnection connection)
    {
        var platform = PlatformName(connection.PlatformCode);
        var store = connection.DisplayName?.Trim();
        return string.IsNullOrEmpty(store) || string.Equals(store, platform, StringComparison.OrdinalIgnoreCase) ? platform : $"{platform} · {store}";
    }
}
