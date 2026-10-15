namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Pazarama refuses requests with plain Turkish sentences. The sentence is kept as Pazarama wrote it;
// a short hint about what to change in the panel follows it.
public static class PazaramaErrorAdvice
{
    private static readonly (string Contains, string Hint)[] Rules =
    [
        ("önceki değerleriyle aynı olamaz", "Pazarama bu fiyat veya stok değerini zaten tutuyor; panel yalnız değişen değeri gönderir. Önce panelde fiyatı ya da stoğu değiştirin."),
        ("satış fiyatı boş", "Satış fiyatı boş gitti; teklifin satış fiyatını girin."),
        ("attribute bulunamadı", "Ürüne kategori özellikleri gitmemiş; Eşleştirmeler'de kategorinin zorunlu özelliklerini eşleyin."),
        ("Ürün bulunamadı", "Bu barkodla Pazarama'da ürün yok; barkodu ve eşleştirmeyi kontrol edin."),
        ("brand", "Marka kimliği kabul edilmedi; Eşleştirmeler'de markayı Pazarama listesinden yeniden eşleyin."),
        ("kategori", "Kategori kabul edilmedi; ürünün Pazarama kategorisinin en alt (yaprak) kategori olduğunu kontrol edin."),
        ("barkod", "Barkod sorunu: her varyantın barkodu benzersiz olmalı ve Pazarama'da başka üründe kullanılmamalı."),
        ("görsel", "Görsel sorunu: görsel adresleri herkese açık HTTPS olmalı."),
        ("açıklama", "Ürün açıklaması boş ya da geçersiz."),
        ("desi", "Desi değeri en az 1 olan bir tam sayı olmalı.")
    ];

    public static string WithAdvice(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return message;
        // Pazarama pads some sentences with double spaces; the hint rules read them as single spaces.
        var squeezed = System.Text.RegularExpressions.Regex.Replace(message, @"\s+", " ");
        foreach (var (contains, hint) in Rules)
            if (squeezed.Contains(contains, StringComparison.OrdinalIgnoreCase)) return $"{message} · {hint}";
        return message;
    }
}
