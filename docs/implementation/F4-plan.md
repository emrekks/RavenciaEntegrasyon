# F4 — Trendyol E-Faturam Tamamlama Planı

## Hedef

Trendyol siparişi/paketi için doğru E-Fatura veya internet satışı E-Arşiv belgesini oluşturmak, provider durumunu sayısal kodlarla uzlaştırmak, PDF'yi private saklamak, E-Arşiv iptalini takip etmek ve Trendyol paketine güvenli fatura linki iletmek.

## Kodlanan kapsam

1. `API_USER`: `signIn` ve `x-access-token`.
2. `MARKETPLACE`: partner `signIn` → müşteri `customerSignIn`; company/user/customer scope doğrulaması.
3. Partner/VKN-TCKN bazlı mükellefiyet sorgusu ve başvuru ayrıntıları.
4. Bağlantı bazlı Temel/Ticari E-Fatura senaryosu.
5. E-Arşiv internet satışı için zorunlu payment/delivery ve kargo tüzel kimlik eşlemesi.
6. Kuruş dönüşümü, satır/toplam denklemi, yalnız tutarı içeren Türkçe not ve deterministic request hash.
7. Durable submit → status reconcile → document fetch → Trendyol link delivery akışı.
8. Resmî sayısal status kataloğu; unknown sonuçta manuel inceleme.
9. Kalıcı PDF URL, güvenli indirme ve private immutable storage.
10. E-Arşiv iptal submit → status reconcile; E-Fatura için otomatik iptal yok.
11. Duplicate submit/delivery koruması, ETag, idempotency ve parola + açık onay.
12. Panelde güvenli mali ayar read-back, çoklu kargo eşlemesi, filtre, submit/reconcile/deliver/cancel, belge erişimi, mükellef sorgusu ve capability evidence yüzeyleri.

## Dış kabul kapısı

- Exact .NET/Node/npm toolchain ile build, unit, integration, Vitest ve Playwright.
- Docker/PostgreSQL Testcontainers ve Compose smoke.
- Trendyol E-Faturam Stage credential, company/user scope, kontrollü VKN/TCKN, paket ve fatura fixture'ları.
- E-Arşiv create/status/PDF/cancel ve Trendyol link teslim E2E.
- Giden E-Fatura status endpoint'i için exact relative path, tarihli source ve fixture checksum kanıtı; mali write evidence için yalnız resmî E-Faturam doküman hostu kabul edilir.

## Çıkış durumu

`CODE_COMPLETE_STATIC_VERIFIED / DYNAMIC_AND_STAGE_REVALIDATION_REQUIRED / PRODUCTION_BLOCKED`
