# Geçmiş Tablolarının Saklama Süreleri (VERITABANI-DAGITIM-13)

Sahip 16 Kasım 2026'da önerilen süreleri kabul etti; bunlar artık **koddaki varsayılanlar**
(`HistoryRetentionPolicy.DefaultDays`), sunucuda ayar gerekmez. Worker'ın saatlik saklama turu
(`JobRetentionService.Purge*Async`) varsayılanı olan tabloları kendiliğinden temizler:

| Tablo | Varsayılan |
|---|---|
| `catalog.product_change_entries` | 730 gün |
| `inventory.channel_price_history` | 730 gün (her teklifin son 50 sürümü her zaman kalır) |
| `ops.ai_usage_logs` | 365 gün |
| `ops.audit_logs` | hiç silinmez |
| `sales.order_status_history` | hiç silinmez |

Bir ayar varsayılanı her zaman geçersiz kılar: sahip `deploy/secrets/production.env`'e o tablonun satırını yazar ve yeniden
deploy eder (`gh workflow run deploy.yml --ref main`).

- Satır yok ya da boş (`..._DAYS=`): tablonun **varsayılanı** geçerli. (Compose tanımsız değişkeni boş metin olarak
  verir; bu yüzden boş değer "hiç silme" anlamına gelemez.)
- `0`: tablo **hiç silinmez**. "Hiç silme" demenin tek yolu budur.
- Pozitif tam sayı (`1000`): o kadar gün. Her tablonun bir **alt sınırı** var: daha kısa bir süre yazılırsa alt sınır
  uygulanır (fazla saklamak her zaman güvenlidir); üst sınır 10 yıl.
- Başka her değer (`730gün`, `2y`, `-5`): tablo hiç silinmez (varsayılana da dönmez) ve worker logunda hata yazar.

Örnek, fiyat geçmişini hiç silmemek için:

```
MARKETPLACEHUB_RETENTION_CHANNEL_PRICE_HISTORY_DAYS=0
```

## Varsayılanlar devreye girerken (16 Kasım 2026, salt-okunur SQL)

Hiçbir tablo silinecek yaşta satır içermiyor: varsayılanların bugün sileceği satır sayısı üç tabloda da **0**. İlk silme
en erken Kasım 2027'de (`ops.ai_usage_logs`, bugün boş) ve Kasım 2028'de (diğer ikisi) olabilir. Canlıda bu beş ayarın
hiçbiri tanımlı değil (worker'ın 16 Kasım açılış logunda `History retention` satırı yok), yani canlı varsayılanlarla çalışır.

| Tablo | Satır | En eski kayıt | Varsayılanın bugün sileceği |
|---|---:|---|---:|
| `catalog.product_change_entries` | 85 | 14 Kas 2026 | 0 |
| `inventory.channel_price_history` | 3.725 | 14 Kas 2026 | 0 |
| `ops.ai_usage_logs` | 0 | — | 0 |
| `ops.audit_logs` | 39 | 13 Kas 2026 | (silinmez) |
| `sales.order_status_history` | 2.131 | 14 Kas 2026 | (silinmez) |

## İlk ölçüm (16 Kasım 2026, salt-okunur SQL)

Veritabanının tamamı 1353 MB.

| Tablo | Satır | Boyut | En eski kayıt | Silme sütunu ve indexi |
|---|---:|---:|---|---|
| `inventory.channel_price_history` | 6.092 | 4,8 MB | 14 Kas 2026 | `EffectiveAt`; `(TenantId, EffectiveAt DESC, Id DESC)` var |
| `sales.order_status_history` | 2.020 | 1,5 MB | 14 Kas 2026 | `RecordedAt`; index yok (sipariş başına index var) |
| `ops.audit_logs` | 35 | 0,7 MB | 13 Kas 2026 | `CreatedAt`; index yok, `Id` artan sayı |
| `catalog.product_change_entries` | 85 | 0,3 MB | 14 Kas 2026 | `OccurredAt`; `(TenantId, OccurredAt DESC, Id DESC)` var |
| `ops.ai_usage_logs` | 0 | 48 kB | — | `CreatedAt`; `(TenantId, CreatedAt)` var |

Hepsi küçük ve yeni (en eski kayıtlar 13–14 Kasım 2026 tarihli); saklama yerden çok büyümeyi sınırlamak için. Index'i
olmayan iki tablo için silme sorgusu tabloyu tarar; bu boyutlarda saniyenin altında. Bir tablo milyonlarca satıra çıkarsa
`CONCURRENTLY` index migration'ı eklenir.

## Öneri (kabul edildi, 16 Kasım 2026)

Kural: muhasebecinin, vergi incelemesinin ya da bir pazaryeri/müşteri anlaşmazlığının isteyebileceği hiçbir kayıt
silinmez. Faturalar, sipariş satırları, hakediş ve finans hareketleri bu listede değil ve hiç silinmez.

| Tablo | Ne tutuyor | Öneri = varsayılan | Ayar (`production.env`) | Alt sınır |
|---|---|---|---|---|
| `sales.order_status_history` | Siparişin her durum değişimi (oluştu, kargolandı, teslim, iade) ve zamanı | **Hiç silmeyin.** Teslim/iade zamanı anlaşmazlıkta ve iade süresi hesabında kanıt; sipariş aktarımı da geçmişi okuyor (toplama zamanı, elle durum). Açılsa bile okunan satırlar kalır (aşağıda "Nasıl çalışır"); bugün bu, tablonun tamamı demek. | `MARKETPLACEHUB_RETENTION_ORDER_STATUS_HISTORY_DAYS` | 1095 gün |
| `ops.audit_logs` | Kim ne zaman ne yaptı (giriş, iki adımlı doğrulama, rol, sıfırlama, soru bildirimi) | **Hiç silmeyin.** Güvenlik ve sorumluluk kaydı; satır sayısı çok düşük. Silinecekse en az 2 yıl. | `MARKETPLACEHUB_RETENTION_AUDIT_LOG_DAYS` | 730 gün |
| `catalog.product_change_entries` | Ürün Hareketleri: durum, panel fiyatı ve bilgi alanı değişimleri | **730 gün (2 yıl).** Panelde Ürün Hareketleri ekranı gösterir; iki yıldan eski bir alan değişikliği anlaşmazlıkta nadiren gerekir. | `MARKETPLACEHUB_RETENTION_PRODUCT_CHANGE_ENTRY_DAYS` | 365 gün |
| `inventory.channel_price_history` | Pazaryeri teklifinin her fiyat sürümü | **730 gün (2 yıl), her teklifin son 50 sürümü her zaman kalır.** Fiyat anlaşmazlığında (ör. yanlış fiyattan satış) satışın tarihindeki fiyat sipariş satırında zaten yazılı; bu tablo ek kanıt. | `MARKETPLACEHUB_RETENTION_CHANNEL_PRICE_HISTORY_DAYS` | 365 gün |
| `ops.ai_usage_logs` | Yapay zekâ çağrıları: özellik, model, token, maliyet | **365 gün.** Günlük harcama sınırı yalnız son 24 saati okur; masraf faturası Anthropic'ten ayrıca gelir. Maliyeti gider olarak belgelemek gerekirse ayarı 730 yapın. | `MARKETPLACEHUB_RETENTION_AI_USAGE_LOG_DAYS` | 90 gün |

## Nasıl çalışır

- Worker'ın saatlik saklama turu (`Worker.RunJobRetentionAsync`) süresi (varsayılan ya da ayar) olan her tabloda 2.000
  satırlık partilerle, turda en fazla 25 parti siler. Worker açılışında her tablo için
  `History retention: <tablo> keeps <gün> days` ya da `History retention: <tablo> is kept forever` logu yazar; silinen
  satırlar `History retention removed ...` logunda görünür (`/prod-logs logs worker --grep "History retention"`).
- Ayar adı: `Worker:HistoryRetention:<Tablo>Days` (`HistoryRetentionPolicy`); Compose bunu `compose.production.yaml`'da
  worker'a `production.env`'deki değişkenden verir.
- Değer tam sayı gün olmalı (`730`). Sayı olmayan ya da negatif bir değer (`730gün`, `2y`, `-5`) tabloyu hiç silmez ve
  worker logunda `History retention: ... is not a whole number of days` hatası yazar; worker durmaz.
- Fiyat geçmişinde süreden eski bir sürüm, teklifin ondan yeni en az 50 sürümü varsa silinir.
- Sipariş geçmişinde kodun okuduğu satırlar yaşı ne olursa olsun kalır: her siparişin en son satırı; `SHIPPED` ve
  `DELIVERED` satırları (raporlardaki ve sipariş listesindeki kargo/teslim zamanı); toplama isteği (`PICKING_REQUESTED`,
  sipariş aktarımı toplama zamanını buradan okur); elle Shopify durumları (`MANUAL_SHOPIFY_STATUS:`); ve olay kimliği
  (`SourceEventId`) dolu her satır (sipariş aktarımı aynı olayı ikinci kez yazmamak için bunlara bakar; silinen olay
  yeniden yazılırdı). Canlıda (16 Kasım 2026) **her satırın olay kimliği dolu**: tablo açılsa da pratikte hiçbir satır
  silinmez. Bu yüzden öneri "hiç silmeyin"dir.
- Silinen satır yedeklerde kalır (yedek saklama planı: `docs/RUNBOOK-GERI-ALMA.md` bölüm 3).
- Testler: `tests/MarketplaceHub.Application.Tests/HistoryRetentionPostgreSqlTests.cs` (sınırın iki yanı, son 50 fiyat,
  siparişin son satırı, varsayılanlar ve `0`).
