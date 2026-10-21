namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// PTT AVM refuses requests with SOAP faults whose text is meant for the seller. The sentence is kept as PTT AVM
// wrote it; a short hint about what to change in the panel follows it.
public static class PttAvmErrorAdvice
{
    // WCF gives one answer for a wrong user name, a wrong password and an expired request timestamp.
    public const string CredentialsRejected =
        "PTT AVM API kullanıcı adı ve şifresini kabul etmedi. PTT AVM satıcı panelinde Hesap Yönetimi › Entegrasyon Bilgileri ekranındaki API Kullanıcı Adı ve API Şifresi'ni olduğu gibi kopyalayıp yeniden kaydedin; şifre değiştirildiyse eskisi geçersiz olur.";

    public const string LegacyCredentials =
        "Bu bağlantıda eski biçimde (API Key ve Access Token) kaydedilmiş PTT AVM bilgileri var. PTT AVM artık Mağaza ID, API Kullanıcı Adı ve API Şifresi ile bağlanıyor: bu bilgileri PTT AVM satıcı panelindeki Hesap Yönetimi › Entegrasyon Bilgileri ekranından alıp bağlantı ayrıntısında yeniden girin.";

    public const string StoreIdInvalid =
        "PTT AVM Mağaza ID yalnız rakamlardan oluşmalıdır. Değeri PTT AVM satıcı panelindeki Hesap Yönetimi › Entegrasyon Bilgileri ekranından kopyalayın.";

    private static readonly (string Contains, string Hint)[] Rules =
    [
        ("şifre", "Kimlik bilgisi sorunu: Hesap Yönetimi › Entegrasyon Bilgileri'ndeki güncel API Şifresi'ni girin."),
        ("kullanıcı", "Kimlik bilgisi sorunu: Hesap Yönetimi › Entegrasyon Bilgileri'ndeki API Kullanıcı Adı'nı girin."),
        ("tarih", "Tarih aralığı en fazla 40 gün olabilir ve bitiş tarihi başlangıçtan önce olamaz."),
        ("barkod", "Barkod sorunu: barkod en fazla 250 karakter olmalı ve PTT AVM'de başka üründe kullanılmamalı."),
        ("kdv", "KDV oranı yalnız 0, 1, 10 veya 20 olabilir.")
    ];

    public static string WithAdvice(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return message;
        var squeezed = System.Text.RegularExpressions.Regex.Replace(message, @"\s+", " ");
        foreach (var (contains, hint) in Rules)
            if (squeezed.Contains(contains, StringComparison.OrdinalIgnoreCase)) return $"{message} · {hint}";
        return message;
    }
}
