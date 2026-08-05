# F3 — Trendyol Tamamlama Planı

## Hedef

Trendyol ürün, referans, sipariş, paket, iade ve fatura-link akışlarını idempotent ve uzlaştırılabilir biçimde tamamlamak.

## Uygulama sırası

1. Connection/capability probe sözleşmesini güncel resmî endpointlerle doğrula.
2. Category, brand, category attributes ve values pagination/leaf testlerini tamamla.
3. Approved product import mapping ve local identity eşlemesini tamamla.
4. `ProductCreate` komutunu ayrı sözleşme, çift dış-yazma kapısı ve doğrulanmış payload composer ile uygula. `ProductUpdate` ve uzak archive ayrı kalır.
5. Publication job, `SUBMIT -> POLL` batch-result durum makinesi, partial failure ve satır sonuç kaydını ekle.
6. Ayrı stock/price portunu birleşik `PriceInventoryBatch` komutuna dönüştür.
7. Order stream/polling overlap, duplicate ve out-of-order fixture setini genişlet.
8. Package action ve return action capability’lerini ayrı ayrı Stage’de kanıtla.
9. Webhook signed delivery ile reconciliation fallback’i birlikte test et.
10. Invoice-link tesliminde `SUBMITTED` ara durumu ve uzlaştırma ekle.

## Çıkış kapısı

- Read akışları en az iki ardışık sync’te duplicate üretmez.
- Write işlemleri aynı idempotency key ile güvenli tekrar edilir.
- Partial batch hataları satır bazında görünür.
- Rate-limit/timeout geçici hata, validation kalıcı hata olarak ayrılır.
- Reconciliation uzak/yerel farkı tespit eder.
- Gerçek Stage write kanıtı ve rollback adımı vardır.

## 2026-08-05 uygulama durumu

- `ProductCreate`, eski genel `UpsertAsync` adından ayrıldı.
- API yalnız `PRODUCT_WRITE=SUPPORTED`, global `FeatureFlags:ExternalWrites=true` ve bağlantı `ExternalWritesEnabled=true` birlikteyken durable job üretir.
- Composer güncel kategori/marka/özellik/değer eşlemelerini, listing profile, barkod/SKU/model kodunu, aktif TRY teklifini, MAIN stok kaydını ve kalıcı HTTPS görsel URL'lerini doğrular.
- Worker dış etki kaydıyla tekrar gönderimi fail-closed sınırlar; create batch kimliğini kaydeder, sonucu poll eder ve varyant bazında `CREATE_ACCEPTED`/`CREATE_REJECTED` yazar.
- Tam batch kabulü `APPROVAL_PENDING` durumudur; ürünün Trendyol'da canlı olduğu anlamına gelmez. Approved-products read-back reconciliation henüz planlıdır.
- Başarı/replay ve partial-batch PostgreSQL testleri kodlandı; exact .NET/PostgreSQL ortamında çalıştırılmadığı için faz kapanmış değildir.
