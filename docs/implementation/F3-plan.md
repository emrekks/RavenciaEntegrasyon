# F3 — Trendyol Tamamlama Planı

## Hedef

Trendyol ürün, referans, sipariş, paket, iade ve fatura-link akışlarını idempotent ve uzlaştırılabilir biçimde tamamlamak.

## Uygulama sırası

1. Connection/capability probe sözleşmesini güncel resmî endpointlerle doğrula.
2. Category, brand, category attributes ve values pagination/leaf testlerini tamamla.
3. Approved product import mapping ve local identity eşlemesini tamamla.
4. `ProductCreate` ve `ProductUpdate` komutlarını ayır.
5. Publication job, batch-result polling, partial failure ve operatör hata ekranını ekle.
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
