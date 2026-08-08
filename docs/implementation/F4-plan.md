# F4 — Trendyol E-Faturam Tamamlama Planı

## Hedef

Trendyol siparişi/paketi için provider hesabındaki mali kapsamı kullanarak doğru E-Fatura veya internet satışı E-Arşiv belgesini oluşturmak, provider durumunu sayısal kodlarla uzlaştırmak, PDF'yi private saklamak, E-Arşiv iptalini takip etmek ve Trendyol paketine güvenli fatura linki iletmek.

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
11. Duplicate submit/delivery koruması, ETag, idempotency ve parola + açık onay.
12. Panelde yalnız credential, otomatik belge türü açıklaması, manuel package policy, filtre, submit/reconcile/deliver/cancel, belge erişimi ve capability evidence yüzeyleri.
13. Operatörün PDF/JPEG/PNG fatura belgesini dosya imzası doğrulaması, SHA-256 tekrar koruması ve audit kaydıyla private storage'a eklemesi; bu akış provider submit veya marketplace link teslimi oluşturmaz.

## Dış kabul kapısı

- Exact .NET/Node/npm toolchain ile build, unit, integration, Vitest ve Playwright.
- Docker/PostgreSQL Testcontainers ve Compose smoke.
- Trendyol E-Faturam Stage credential ve token company/user scope kanıtı.
- Kontrollü kurumsal E-Fatura uygun, kurumsal E-Fatura uygun olmayan ve bireysel sipariş/package fixtureları.
- E-Arşiv create/status/PDF/cancel ve Trendyol link teslim E2E.
- Giden E-Fatura status endpoint'i için exact relative path, tarihli source ve fixture checksum kanıtı; mali write evidence için yalnız resmî E-Faturam doküman hostu kabul edilir.

## Çıkış durumu

`CODE_COMPLETE_STATIC_VERIFIED / DYNAMIC_AND_STAGE_REVALIDATION_REQUIRED / PRODUCTION_BLOCKED`
