namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// PTT AVM refuses requests with Turkish sentences in error.error_message. The sentence is kept as PTT AVM
// wrote it; a short hint about what to change in the panel follows it.
public static class PttAvmErrorAdvice
{
    // The keys are produced in the seller panel for one integrator and one store; a key made for another
    // integrator, or one regenerated since, is refused.
    public const string CredentialsRejected =
        "PTT AVM API Key ve Access Token çiftini kabul etmedi. PTT AVM satıcı panelinde Hesap Yönetimi › Entegrasyon Bilgileri'nde entegratör olarak Ravencia'nın seçili olduğunu kontrol edin ve orada Ravencia için gösterilen güncel API Key ile Access Token'ı girin; yeni anahtar üretildiyse eskisi geçersiz olur.";

    private static readonly (string Contains, string Hint)[] Rules =
    [
        ("token", "Kimlik bilgisi sorunu: Hesap Yönetimi › Entegrasyon Bilgileri'nde Ravencia'nın seçili olduğunu ve Access Token'ın güncel olduğunu kontrol edin."),
        ("api key", "Kimlik bilgisi sorunu: Hesap Yönetimi › Entegrasyon Bilgileri'ndeki güncel API Key'i girin."),
        ("entegratör", "Ravencia PTT AVM'nin entegratör listesinde seçili değil; Hesap Yönetimi › Entegrasyon Bilgileri'nde Ravencia'yı seçin."),
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
