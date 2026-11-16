# Geçmiş Tablolarının Saklama Süreleri (VERITABANI-DAGITIM-13)

Bu beş tablo bugün hiç silinmiyor. Silme kodu hazır (`JobRetentionService.Purge*Async`, worker'ın saatlik saklama
turu), ama **her tablo için kapalı**: süre verilmedikçe satırlar sonsuza kadar kalır. Sahip bir tabloyu açmak için
`deploy/secrets/production.env`'e o tablonun satırını yazar ve yeniden deploy eder (`gh workflow run deploy.yml --ref main`).
Satırı silmek ya da boş bırakmak tabloyu yeniden kapatır. Her tablonun bir **alt sınırı** var: daha kısa bir süre
yazılırsa alt sınır uygulanır (fazla saklamak her zaman güvenlidir); üst sınır 10 yıl.

## Canlı ölçüm (16 Kasım 2026, salt-okunur SQL)

Veritabanının tamamı 1353 MB.

| Tablo | Satır | Boyut | En eski kayıt | Silme sütunu ve indexi |
|---|---:|---:|---|---|
| `inventory.channel_price_history` | 6.092 | 4,8 MB | 14 Kas 2026 | `EffectiveAt`; `(TenantId, EffectiveAt DESC, Id DESC)` var |
| `sales.order_status_history` | 2.020 | 1,5 MB | 14 Kas 2026 | `RecordedAt`; index yok (sipariş başına index var) |
| `ops.audit_logs` | 35 | 0,7 MB | 13 Kas 2026 | `CreatedAt`; index yok, `Id` artan sayı |
| `catalog.product_change_entries` | 85 | 0,3 MB | 14 Kas 2026 | `OccurredAt`; `(TenantId, OccurredAt DESC, Id DESC)` var |
| `ops.ai_usage_logs` | 0 | 48 kB | — | `CreatedAt`; `(TenantId, CreatedAt)` var |

Hepsi küçük ve yeni (en eski kayıtlar 13–14 Kasım 2026 tarihli). Bugün yer
kazancı yok; karar bir yıl sonra büyüme görülünce de verilebilir. Index'i olmayan iki tablo için silme sorgusu tabloyu
tarar; bu boyutlarda saniyenin altında. Bir tablo milyonlarca satıra çıkarsa `CONCURRENTLY` index migration'ı eklenir.

## Öneri

Kural: muhasebecinin, vergi incelemesinin ya da bir pazaryeri/müşteri anlaşmazlığının isteyebileceği hiçbir kayıt
silinmez. Faturalar, sipariş satırları, hakediş ve finans hareketleri bu listede değil ve hiç silinmez.

| Tablo | Ne tutuyor | Öneri | Ayar (`production.env`) | Alt sınır |
|---|---|---|---|---|
| `sales.order_status_history` | Siparişin her durum değişimi (oluştu, kargolandı, teslim, iade) ve zamanı | **Hiç silmeyin.** Teslim/iade zamanı anlaşmazlıkta ve iade süresi hesabında kanıt; sipariş aktarımı da geçmişi okuyor (toplama zamanı, elle durum). Açılsa bile okunan satırlar kalır (aşağıda "Nasıl çalışır"); bugün bu, tablonun tamamı demek. | `MARKETPLACEHUB_RETENTION_ORDER_STATUS_HISTORY_DAYS` | 1095 gün |
| `ops.audit_logs` | Kim ne zaman ne yaptı (giriş, iki adımlı doğrulama, rol, sıfırlama, soru bildirimi) | **Hiç silmeyin.** Güvenlik ve sorumluluk kaydı; satır sayısı çok düşük. Silinecekse en az 2 yıl. | `MARKETPLACEHUB_RETENTION_AUDIT_LOG_DAYS` | 730 gün |
| `catalog.product_change_entries` | Ürün Hareketleri: durum, panel fiyatı ve bilgi alanı değişimleri | **730 gün (2 yıl).** Panelde Ürün Hareketleri ekranı gösterir; iki yıldan eski bir alan değişikliği anlaşmazlıkta nadiren gerekir. | `MARKETPLACEHUB_RETENTION_PRODUCT_CHANGE_ENTRY_DAYS` | 365 gün |
| `inventory.channel_price_history` | Pazaryeri teklifinin her fiyat sürümü | **730 gün (2 yıl), her teklifin son 50 sürümü her zaman kalır.** Fiyat anlaşmazlığında (ör. yanlış fiyattan satış) satışın tarihindeki fiyat sipariş satırında zaten yazılı; bu tablo ek kanıt. | `MARKETPLACEHUB_RETENTION_CHANNEL_PRICE_HISTORY_DAYS` | 365 gün |
| `ops.ai_usage_logs` | Yapay zekâ çağrıları: özellik, model, token, maliyet | **365 gün.** Günlük harcama sınırı yalnız son 24 saati okur; masraf faturası Anthropic'ten ayrıca gelir. Maliyeti gider olarak belgelemek gerekirse 730 gün seçin. | `MARKETPLACEHUB_RETENTION_AI_USAGE_LOG_DAYS` | 90 gün |

Önerilen `production.env` satırları (yalnız açmak istenenler):

```
MARKETPLACEHUB_RETENTION_PRODUCT_CHANGE_ENTRY_DAYS=730
MARKETPLACEHUB_RETENTION_CHANNEL_PRICE_HISTORY_DAYS=730
MARKETPLACEHUB_RETENTION_AI_USAGE_LOG_DAYS=365
```

## Nasıl çalışır

- Worker'ın saatlik saklama turu (`Worker.RunJobRetentionAsync`) süre verilmiş her tabloda 2.000 satırlık partilerle,
  turda en fazla 25 parti siler. Açık bir tablo worker açılışında `History retention: <tablo> keeps <gün> days` logu
  yazar; silinen satırlar `History retention removed ...` logunda görünür (`/prod-logs logs worker --grep "History retention"`).
- Ayar adı: `Worker:HistoryRetention:<Tablo>Days` (`HistoryRetentionPolicy`); Compose bunu `compose.production.yaml`'da
  worker'a `production.env`'deki değişkenden verir.
- Değer tam sayı gün olmalı (`730`). Sayı olmayan bir değer (`730gün`, `2y`) tabloyu kapalı bırakır ve worker logunda
  `History retention: ... is not a whole number of days` hatası yazar; worker durmaz.
- Fiyat geçmişinde süreden eski bir sürüm, teklifin ondan yeni en az 50 sürümü varsa silinir.
- Sipariş geçmişinde kodun okuduğu satırlar yaşı ne olursa olsun kalır: her siparişin en son satırı; `SHIPPED` ve
  `DELIVERED` satırları (raporlardaki ve sipariş listesindeki kargo/teslim zamanı); toplama isteği (`PICKING_REQUESTED`,
  sipariş aktarımı toplama zamanını buradan okur); elle Shopify durumları (`MANUAL_SHOPIFY_STATUS:`); ve olay kimliği
  (`SourceEventId`) dolu her satır (sipariş aktarımı aynı olayı ikinci kez yazmamak için bunlara bakar; silinen olay
  yeniden yazılırdı). Canlıda (16 Kasım 2026) **her satırın olay kimliği dolu**: tablo açılsa da pratikte hiçbir satır
  silinmez. Bu yüzden öneri "hiç silmeyin"dir.
- Silinen satır yedeklerde kalır (yedek saklama planı: `docs/RUNBOOK-GERI-ALMA.md` bölüm 3).
- Testler: `tests/MarketplaceHub.Application.Tests/HistoryRetentionPostgreSqlTests.cs` (sınırın iki yanı, son 50 fiyat,
  siparişin son satırı, varsayılan kapalı).
