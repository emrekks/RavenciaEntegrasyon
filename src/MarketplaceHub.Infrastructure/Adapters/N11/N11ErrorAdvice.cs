namespace MarketplaceHub.Infrastructure.Adapters.N11;

// n11 refuses product and task requests with plain Turkish sentences and no error codes
// (developer.n11.com "API Hata Mesajları ve Açıklamaları"). The sentence is kept as n11 wrote
// it; a short hint about what to change in the panel is added after it.
public static class N11ErrorAdvice
{
    private static readonly (string Contains, string Hint)[] Rules =
    [
        ("seller stock code tarafınızdan kullanılmaktadır", "Bu stok kodu (SKU) N11'de zaten kullanılıyor; ürün zaten açılmış olabilir, N11 ürün listesini kontrol edin."),
        ("Farklı ürünlerinizde aynı Stok Kodu", "Aynı istekte aynı stok kodu (SKU) iki ürüne verilmiş; SKU'ları benzersiz yapın."),
        ("aynı barcode kullanılmıştır", "Aynı istekteki ürünlerde aynı barkod var; her varyantın barkodu benzersiz olmalı."),
        ("aynı attributes kullanılmıştır", "Aynı başlık ve özelliklere sahip birden fazla varyant var; renk/beden gibi ayırt edici özellikleri eşleyin."),
        ("Marka ve Kategori id değerlerini", "Kategori eşlemesini ve markayı kontrol edin; markayı N11'de serbest metin olarak gönderiyoruz."),
        ("Önerdiğiniz kategori ürün ile uyumlu değildir", "N11 mesajdaki kategoriyi öneriyor; Eşleştirmeler'de ürünün N11 kategorisini o kategoriye çevirin."),
        ("seçtiğiniz kategoriye ürün yükleyemezsiniz", "Bu kategori N11'de kısıtlı; N11 mağaza destek talebi ve tedarikçi faturası gerekir."),
        ("girmiş olduğunuz marka ile ürün yükleyemezsiniz", "Bu marka bu kategoride kısıtlı; N11 mağaza destek talebi ve tedarikçi faturası gerekir."),
        ("özellik alanı boş olamaz", "N11'in zorunlu kategori özelliği eksik; Eşleştirmeler'de bu özelliği eşleyin veya bağlantı varsayılanı tanımlayın."),
        ("özellik değeri", "Özellik değeri bu kategoride yok; N11 kategori özelliklerini yenileyip değer eşlemesini güncelleyin."),
        ("sadece valueId ile ürün girişi yapılabilir", "Bu özellik serbest metin kabul etmiyor; panel değerini N11 değerine eşleyin."),
        ("All Images are invalid", "Görsel adresleri geçersiz ya da herkese açık değil; ürün görsellerinin açık HTTPS adresini kontrol edin."),
        ("İlk görsel boş olamaz", "Ürünün ilk görseli boş; en az bir görsel ekleyin."),
        ("vatRate", "KDV oranı yalnız 0, 1, 10 veya 20 olabilir."),
        ("15 karakter altında ürün adları", "Ürün başlığı en az 15 karakter olmalı."),
        ("title boyut 0 ile 255", "Ürün başlığı en fazla 255 karakter olmalı."),
        ("shipmentTemplate", "N11 bağlantısındaki kargo şablonu adı, N11 satıcı panelindeki şablonla birebir aynı olmalı."),
        ("catalogId", "Katalog numarası reddedildi; N11'e katalog numarası göndermiyoruz, barkodu kontrol edin."),
        ("Bu kelimeler yasaklıdır", "Açıklamada N11'in yasakladığı kelimeler var; açıklamadan çıkarın."),
        ("sözcükleri kullanılamaz", "Başlıkta N11'in yasakladığı sözcükler var; başlıktan çıkarın."),
        ("description alanı boş olamaz", "Ürün açıklaması boş olamaz."),
        ("description alanı geçersizdir", "Açıklamadaki HTML etiketleri kapatılmamış; HTML'i düzeltin."),
        ("category alanı geçersizdir", "N11 kategorisi kapatılmış; kategori eşlemesini yenileyin."),
        ("mainId girişi zorunludur", "Bu kategori ortak model kodu istiyor; varyantlara model kodu verin."),
        ("fahiş fiyat düşüklüğü", "Fiyat N11'in izin verdiği en düşük değerin altında; fiyatı yükseltin veya N11 destekle görüşün."),
        ("fahiş fiyat artışı", "Fiyat N11'in izin verdiği en yüksek değerin üstünde; fiyatı düşürün veya N11 destekle görüşün."),
        ("listPrice salePrice dan küçük", "Liste fiyatı satış fiyatından küçük olamaz."),
        ("2 haneden fazla", "Fiyat virgülden sonra en fazla 2 hane olmalı."),
        ("ürün bulunamamaktadır", "Bu stok koduyla N11'de ürün yok; önce ürünü açın."),
        ("yalnızca onaylı ve beklemede", "Ürün N11'de onay sürecinde; onaylanana ya da beklemeye geçene kadar güncelleme yapılamaz."),
        ("katalog tarafından reddedildi", "N11 satıcı panelinde ret nedenine bakın, ürünü silip düzeltilmiş halini yeniden açın.")
    ];

    // The message with the first matching hint added; messages without a rule come back unchanged.
    public static string WithAdvice(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return message;
        foreach (var (contains, hint) in Rules)
            if (message.Contains(contains, StringComparison.OrdinalIgnoreCase))
                return message.EndsWith('.') ? $"{message} Öneri: {hint}" : $"{message}. Öneri: {hint}";
        return message;
    }
}
