# F4 — Trendyol E-Faturam Tamamlama Planı

## Hedef

Trendyol siparişi/paketi için provider hesabındaki mali kapsamı kullanarak doğru E-Fatura veya internet satışı E-Arşiv belgesini oluşturmak, provider durumunu sayısal kodlarla uzlaştırmak, PDF'yi private saklamak, E-Arşiv iptalini takip etmek ve Trendyol paketine güvenli fatura linki iletmek.

## Sağlayıcı kabul ön koşulu

Resmî Trendyol E-Faturam pazaryeri API'si, fatura çağrılarında partner `signIn` ardından her test müşterisi için `customerSignIn` ile alınan müşteri token'ını ister. Tekil kullanıcı e-posta/parolası başarılı oturum açsa bile bu token'ın yerine geçmez. Bu nedenle partner + Stage test müşteri API hesabı sağlanmadan gerçek submit/status/PDF/cancel kabulü `BLOCKED_PROVIDER_API_ACCOUNT` kalır; uygulama bu dış yetki koşulunu bypass etmez.

## Kodlanan kapsam

1. Doğrudan `API_USER` `signIn`, `x-access-token` ve şifreli e-posta/parola.
2. `companyId/userId` değerlerinin sign-in tokenından okunması; mali hesap/seri ayarlarının panel ve persistence dışına çıkarılması; eski connection settings verilerinin migration ile temizlenmesi.
3. `commercial && eInvoiceAvailable` ile otomatik `TEMELFATURA`; diğer siparişlerde `EARSIVFATURA`.
4. Ayrı taxpayer sorgusu, Temel/Ticari seçim ve partner/customerSignIn yüzeylerinin kaldırılması.
5. E-Arşiv internet satışı için gereken payment/delivery alanlarının Trendyol siparişi ve resmî carrier kataloğundan otomatik üretilmesi.
6. Kuruş dönüşümü, satır/toplam denklemi, pozitif faturalanabilir miktar filtresi, yalnız tutarı içeren Türkçe not ve deterministic request hash.
7. Durable submit → status reconcile → document fetch → Trendyol link delivery akışı.
8. Resmî sayısal status kataloğu; unknown sonuçta manuel inceleme.
9. Kalıcı PDF URL, güvenli indirme ve private immutable storage.
10. E-Arşiv iptal submit → status reconcile; E-Fatura için otomatik iptal yok.
11. Duplicate submit/delivery koruması ve ETag/idempotency. Parola + açık onay yalnız Production mali write için zorunludur; manuel Stage write ek onay istemez.
12. Panelde yalnız credential, otomatik belge türü açıklaması, manuel package policy, filtre, submit/reconcile/deliver/cancel, belge erişimi ve capability evidence yüzeyleri.
13. Operatörün PDF/JPEG/PNG fatura belgesini dosya imzası doğrulaması, SHA-256 tekrar koruması ve audit kaydıyla private storage'a eklemesi; bu akış provider submit veya marketplace link teslimi oluşturmaz.
14. Sipariş listesinden API kaynaklı müşteri/adres/ürün/KDV özeti gösteren fatura ön izleme; devam adımı idempotent yerel taslak oluşturur. Provider submit, Stage'de ek onay istemez; Production'da parola ve açık onay gerektirir.

## Dış kabul kapısı

- Exact .NET/Node/npm toolchain ile build, unit, integration, Vitest ve Playwright.
- Docker/PostgreSQL Testcontainers ve Compose smoke.
- Trendyol E-Faturam Stage credential ve token company/user scope kanıtı.
- Kontrollü kurumsal E-Fatura uygun, kurumsal E-Fatura uygun olmayan ve bireysel sipariş/package fixtureları.
- E-Arşiv create/status/PDF/cancel ve Trendyol link teslim E2E.
- Giden E-Fatura status endpoint'i için exact relative path, tarihli source ve fixture checksum kanıtı; mali write evidence için yalnız resmî E-Faturam doküman hostu kabul edilir.

## Çıkış durumu

`CODE_COMPLETE_STATIC_VERIFIED / DYNAMIC_AND_STAGE_REVALIDATION_REQUIRED / PRODUCTION_BLOCKED`
