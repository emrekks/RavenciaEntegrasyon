# F4 — Trendyol E-Faturam Tamamlama Planı

## Hedef

Trendyol siparişi/paketi için provider hesabındaki mali kapsamı kullanarak doğru E-Fatura veya internet satışı E-Arşiv belgesini oluşturmak, provider durumunu sayısal kodlarla uzlaştırmak, PDF'yi private saklamak, E-Arşiv iptalini takip etmek ve Trendyol paketine güvenli fatura linki iletmek.

## Sağlayıcı kabul ön koşulu

Aktif kapsam tek işletmenin kendi E-Faturam hesabını yöneten `API_USER` modelidir. Panel yalnız hesap e-posta/parolasını şifreli saklar; `companyId` ve `userId` sağlayıcının `signIn` access tokenından okunur. Tek firma/kullanıcı kapsamı güvenli biçimde çıkarılamazsa işlem fail-closed kalır. Partner `customerSignIn` ve çoklu müşteri modeli aktif kapsam dışıdır.

v10.59 Stage kabulünde bu hesabın `signIn` ve token kapsamı doğrulandı; korumalı E-Arşiv create endpointi aynı tokenı `401` ile reddetti. Bu nedenle doğrudan hesap modeli için ek dış ön koşul, sağlayıcının hesaba fatura API kapsamı tanımlamasıdır. Uygulama bunu capability/evidence veya Stage switch ile bypass etmez. Sağlayıcı çoklu müşteri pazaryeri modeli dayatırsa bu aktif kapsam değişikliği ayrıca kararlaştırılmalıdır.

## Kodlanan kapsam

1. Doğrudan hesap `signIn`, access token (`x-access-token`) ve şifreli hesap e-posta/parolası.
2. `companyId/userId` değerlerinin access token claimlerinden okunması; mali hesap/seri ayarlarının panel ve persistence dışında tutulması.
3. `commercial && eInvoiceAvailable` ile otomatik `TEMELFATURA`; diğer siparişlerde `EARSIVFATURA`.
4. Ayrı taxpayer sorgusu ve Temel/Ticari seçiminin kaldırılması; tekil token kapsamının belirsiz veya çoklu firma olması halinde fail-closed davranışın korunması.
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
