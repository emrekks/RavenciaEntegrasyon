# F3 — Trendyol Türkiye CORE Tamamlama Planı

- [x] 19. Kategori ve marka eşleştirmelerini iki tutarlı sekmede birleştir; aramayı seçim kutularına taşı; panel kategorisini eşleştirme ekranından güvenli yerel işlemle oluştur.

- [x] 18. Sol menüyü kalıcı ikon görünümüne daraltılabilir yap; yinelenen sipariş detay özetini kaldır; eksik Stage terminini açık veri eksikliği olarak göster.

- [x] 17. Mikro ihracat rozetini yalnız fatura sütununda kısa adla göster; mavi satır göstergesini koru.

## Hedef

Trendyol Türkiye CORE ürün, referans, sipariş, paket, iade, etiket, webhook ve fatura-link akışlarını idempotent, fail-closed ve uzlaştırılabilir biçimde tamamlamak.

## Kodlanan kapsam

1. Connection/credential/capability probe ve tarihli Stage/SIT evidence kaydı.
2. Category, brand, category attributes ve values snapshot/pagination/leaf mapping.
3. Approved product read için `page -> nextPageToken` cursor ve 100 kayıt sınırı.
4. Product Create durable `SUBMIT -> POLL -> APPROVAL_RECONCILE`.
5. Product Update: unapproved bulk veya approved content/variant/delivery fazları.
6. Product archive/unarchive batch ve publication read-back.
7. Birleşik `price-and-inventory` batch; fiyat/stok sürüm kanıtı ve stale sonucu uygulamama.
8. Order stream + `/v2/orders` exact read ve 2026 alan aliasları.
9. Capability kontrollü shipment action state machine ve exact order read-back.
10. Common label create/poll/private document storage.
11. Return poll/exact claim read, approve/reject, private evidence ve terminal read-back.
12. Webhook bounded ingress, verification, inbox ve reconciliation fallback.
13. Invoice-link `SUBMITTED` sınırı; doğrulanmamış terminal query yerine manuel inceleme.
14. Ürün, fiyat-stok, shipment, return ve capability evidence panel yüzeyleri.
15. Sipariş operasyon ekranında kompakt filtre çalışma alanı, açık/gelişmiş filtre ayrımı ve taşmasız ayrı sipariş satırı yüzeyleri.
16. Sipariş satırı menülerinde viewport-aware yukarı/aşağı yerleşim, ürün bilgi hizası ve tarihsel PM3–Arvato ihracat partneri snapshot geri uyumluluğu.

## Güvenlik ve tutarlılık kapıları

- Her write capability için `SUPPORTED` ve tarihli environment/store scope evidence gerekir.
- Write capability evidence'ında SHA-256 Stage/SIT fixture checksum zorunludur.
- Global ve connection external-write anahtarları birlikte açık olmalıdır.
- Her dış yazma deterministic idempotency key ve `ExternalEffectRecord` fence kullanır.
- Belirsiz ağ/5xx sonucu otomatik duplicate write üretmez; `MANUAL_REVIEW` olur.
- Batch satır sonuçları barkod/contentId/stockCode ile korunur.
- Read-back veya sürüm eşleşmesi olmadan yerel durum kesin başarıya yükseltilmez.
- `storeFrontCode=TR`, product create `channels=["CORE"]`; LUXE/uluslararası kapsam dışıdır.

## Çıkış kapısı

Kod kapsamı tamamlanmıştır; faz production kapanışı için aşağıdakilerin tamamı gerekir:

- Exact .NET/PostgreSQL ve frontend suite PASS.
- Docker Compose/API/Worker/Caddy smoke PASS.
- Stage read ve açık onaylı write fixture PASS.
- Duplicate, timeout, rate-limit, partial batch, stale payload ve rollback/read-back PASS.
- Capability evidence, operatör prosedürü ve rollback kaydı tamam.

**Güncel durum:** `CODE_COMPLETE_STATIC_VERIFIED / DYNAMIC_AND_STAGE_REVALIDATION_REQUIRED / PRODUCTION_BLOCKED`.
