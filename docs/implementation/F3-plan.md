# F3 — Trendyol Tamamlama Planı

## Hedef

Trendyol ürün, referans, sipariş, paket, iade ve fatura-link akışlarını idempotent ve uzlaştırılabilir biçimde tamamlamak.

## Uygulama sırası

1. Connection/capability probe sözleşmesini güncel resmî endpointlerle doğrula.
2. Category, brand, category attributes ve values pagination/leaf testlerini tamamla.
3. Approved product import mapping ve local identity eşlemesini tamamla.
4. `ProductCreate` komutunu ayrı sözleşme, çift dış-yazma kapısı ve doğrulanmış payload composer ile uygula. `ProductUpdate` ve uzak archive ayrı kalır.
5. Publication job, `SUBMIT -> POLL` batch-result durum makinesi, partial failure ve satır sonuç kaydını ekle.
6. En az bir satırı kabul edilen batch sonrasında approved -> unapproved fallback read-back ile onay uzlaştırması, ret nedeni ve uzak ürün/varyant kimliği kaydını ekle.
7. Ayrı stock/price portunu birleşik `PriceInventoryBatch` komutuna dönüştür.
8. Order stream/polling overlap, duplicate ve out-of-order fixture setini genişlet.
9. Package action ve return action capability’lerini ayrı ayrı Stage’de kanıtla.
10. Webhook signed delivery ile reconciliation fallback’i birlikte test et.
11. Invoice-link tesliminde `SUBMITTED` ara durumu ve uzlaştırma ekle.

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
- Tam batch kabulü `APPROVAL_PENDING` durumudur; ürünün Trendyol'da canlı olduğu anlamına gelmez. En az bir satırı kabul edilen tam veya kısmi batch için otomatik `TRENDYOL_PRODUCT_APPROVAL_RECONCILE` işi oluşur; batch reddi alan satırlar read-back dışında korunur. Approval job güncel listing-state payload hash’iyle eşleşmezse daha yeni yayın denemesini ezmeden `PRODUCT_APPROVAL_SUPERSEDED` olarak durur.
- Onay uzlaştırması barkodu önce approved, sonra unapproved serviste okur; `APPROVED`, `PENDING_APPROVAL`, `REJECTED`, `ARCHIVED`, `LOCKED`, `BLACKLISTED` ve `NOT_FOUND` durumlarını yerel profile/satır kayıtlarına taşır. Onaylı `contentId/variantId` kimlikleri idempotent kaydedilir; mevcut link uyuşmazlığı otomatik değiştirilmez ve `MANUAL_REVIEW` olur.
- Başarı/replay, partial-batch, tam onay, kısmi ret, görünürlük gecikmesi ve kimlik çatışması PostgreSQL testleri kodlandı; exact .NET/PostgreSQL ortamında çalıştırılmadığı için faz kapanmış değildir.
