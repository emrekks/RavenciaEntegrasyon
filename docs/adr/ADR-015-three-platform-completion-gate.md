# ADR-015 - Üç platform tamamlanma kapısı

Durum: Accepted
Tarih: 2026-08-03
Supersedes: ADR-014 aktif teslim sırası ve Shopify'ın Hepsiburada ön koşulu
Bağlayıcı belge: `output/pdf/Ravencia_Entegrasyon_v3_5_Nihai_Uygulama_Surumu.pdf`
SHA-256: `DDA0DBE58555EB323A84A6E2C5449133FAF8584979BD8DB795DFEE587AED8B58`

## Karar

İşletme sahibi, yeni bir karar verilene kadar aktif geliştirme, test, Stage/SIT doğrulama ve üretim kabul kapsamını yalnız şu sırayla sınırlandırmıştır:

1. Trendyol
2. Hepsiburada
3. Trendyol E-Faturam

Bu üç platform işletme sahibinin onayladığı iş kapsamı için uçtan uca çalışır ve kanıtlı hale gelmeden Shopify, N11 veya Pazarama üzerinde yeni geliştirme, doğrulama, capability açılışı, route/menü, migration, safe-write ya da production smoke başlatılmaz.

## Tam çalışma ölçütü

Bir platform yalnız aşağıdaki kanıtlar birlikte bulunduğunda bu karar kapsamında tamamlanmış sayılır:

- Bağlantı, environment, hesap/store/merchant scope ve gerekli API sürümü tarihli kanıtla doğrulanmıştır.
- İşletme kapsamındaki read akışları gerçek Stage/SIT verisiyle generic modele doğru ve idempotent işlenmiştir.
- Gerekli write akışları yalnız açık iş otoritesi, işlem bazlı kullanıcı onayı, düşük adet safe-write ve geri dönüş kanıtıyla açılmıştır.
- Retry, duplicate/out-of-order, partial-result ve hata sınıfları açıklanabilir; sessiz başarı yoktur.
- Read-only reconciliation, rollback ve hedef üretim health/readiness kanıtı vardır; açıklanamayan kritik fark yoktur.
- Secret/PII sızıntısı yoktur ve kapsam dışı capability'ler `UNKNOWN`/kapalı kalır.

## Faz kapısı değişikliği

- Shopify'ın eksik development-store ve production reconciliation/rollback kanıtı tamamlanmış sayılmaz; `DEFERRED` olarak korunur.
- Shopify eksikliği artık Hepsiburada'nın kendi Stage/SIT, safe-write, reconciliation ve rollback çalışmalarını bloke etmez.
- Hepsiburada yalnız kendi kanıtlarıyla ilerler; bu değişiklik hiçbir Hepsiburada write capability'sini otomatik açmaz.
- N11 ve Pazarama için F6B/F6C başlatılmaz.
- Trendyol E-Faturam tamamlanana kadar F7/F7B/F8 veya başka platform genişlemesi başlatılmaz; zorunlu güvenlik, hata düzeltme ve operasyonel bakım bu dondurmanın dışındadır.

## Sonuçlar

- Mevcut Shopify/N11/Pazarama kod ve tarihsel kanıtları silinmez, fakat aktif teslim sayılmaz.
- Panel, bağlantı API'si, eşitleme, webhook ve Worker faaliyet kapıları yalnız aktif üç platformu kabul eder; kapsam dışı tarihsel kayıtlar okunabilir ve devre dışı bırakılabilir fakat yeniden etkinleştirilemez veya yeni iş üretemez.
- Yeni çalışma ve kabul raporları yalnız aktif üç platformu ayrı ayrı gösterir; bir platformun kanıtı diğerine taşınmaz.
- Bütün dış yazmalar mevcut global, connection, capability ve business-authority kapılarından geçmeye devam eder.
- Bu kapsamın veya sıranın yeniden değiştirilmesi yeni işletme sahibi kararı ve ADR güncellemesi gerektirir.
