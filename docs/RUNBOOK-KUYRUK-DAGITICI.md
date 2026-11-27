# Kuyruk Dağıtıcısına Geçiş ve Geri Dönüş (Kuyruk Faz 2)

`Worker:LaneModel=dispatcher` geçişinin kontrol listesi, salt okunur doğrulama sorguları ve sayıyla yazılmış geri dönüş
eşikleri. Modelin kendisi `docs/ISLEYIS-KILAVUZU.md` (İşlem Takibi › Şerit modeli ve dağıtıcı), geri dönüşün yayını
`docs/RUNBOOK-GERI-ALMA.md` 2b. Sorgular canlıda yalnız `/prod-sql` ile (salt okunur rol, `BEGIN READ ONLY … ROLLBACK`)
çalıştırılır; hiçbiri yazmaz.

## 1. Geçişten önce

1. Esnek modelin (bugünkü) referans ölçümü: geçişten bir gün önce aynı saatlerde 3. bölümdeki Q1-Q6'yı çalıştır, sonucu
   `.local-docs/` altına kaydet. Referans dalgasının (sabah 10:55-11:40 arası mağazaların referans okumaları) ve deploy
   anlarının sayıları ayrı tutulur: Q1-Q3 her pencereyi ayrı verir (bkz. `params`).
2. `bash .claude/skills/prod-logs/run.sh status`: worker ve api aynı sha'da, sağlıklı; `errors` son 1 saatte boş.
3. Kuyruk sakinken geç (gece değil, normal iş saatinde ama toplu fatura ya da Tüm Ürünleri Çek sürmüyorken): sorun çıkarsa
   gerçek yük altında görünsün, ama ilk dakikalar bir dalgaya denk gelmesin.
4. Geçiş: `src/MarketplaceHub.Worker/appsettings.json` ve `src/MarketplaceHub.Api/appsettings.json` içinde
   `"LaneModel": "dispatcher"` (ikisi aynı olmalı, bir test bunu ister), commit, `ship`. Veritabanı değişikliği yoktur.
5. Geçiş anını (`flip_at`, worker konteynerinin başlama zamanı, `prod-logs status`) not al.

## 2. Geçişten sonra: ne zaman neye bakılır

| Ne zaman | Ne |
|---|---|
| İlk 10 dk | `prod-logs logs worker --since 10m --grep -i dispatcher`: "dispatcher turn" hatası yok; Kuyruk ve Kota sayfası 11 şeridi gösteriyor; Q5 (bekleyen ama başlamayan iş) boş |
| İlk 1 saat | Q1, Q2, Q4, Q5, Q6; eşiklerden biri aşılırsa hemen geri dön (4. bölüm) |
| İlk referans dalgası (ertesi sabah 10:55-11:40) | Q1 ve Q3 bu pencere için ayrı; esnek modelin aynı penceresiyle karşılaştır |
| 24 saat | Q1-Q6 tam pencere; Q7 iki kez (60 sn arayla) |

## 3. Doğrulama sorguları (salt okunur)

Her sorgunun başındaki `params` satırında `flip_at` geçiş anıdır; `deploy_until` deploy anının sonu (geçişten 10 dk sonra:
yeniden açılışta biriken işler bu pencerede ayrı sayılır); `ref_from`/`ref_to` referans dalgasıdır. Referans ölçümü için
`flip_at` yerine bir gün önceki aynı saat yazılır. Bekleme = ilk denemenin `StartedAt - EligibleAt` süresi (kilit beklemeleri
dahil); `WaitSlotMs` dağıtıcıda "slot için bekleme" (bütün bekleme eksi kilit, geri çekilme ve kota bekletmesi),
`WaitLockMs` mağazanın aynı kilit grubundaki işini bekleme, `WaitQuotaHoldMs` kota bekletmesi.

İş türü sınıflaması iki modelde aynıdır (`DispatcherLanes.LaneFor` ile aynı kurallar; yeni bir iş tipi eklenince burası da
güncellenir):

```sql
-- WORK_TYPE: Q1-Q3'te aynen kullanılır.
CASE WHEN j."Priority" <= -3 THEN 'kullanici'
     WHEN j."Priority" = -2 THEN 'toplu'
     WHEN j."Priority" < 0 THEN 'ilk-cekim'
     WHEN j."JobType" IN ('IMPORT_PREVIEW','IMPORT_APPLY') THEN 'toplu'
     WHEN j."JobType" IN ('TRENDYOL_WEBHOOK_INGEST','SHOPIFY_WEBHOOK_INGEST','HEPSIBURADA_WEBHOOK_INGEST') THEN 'webhook'
     WHEN j."JobType" IN ('SHIPMENT_ACTION','RETURN_ACTION','TRENDYOL_COMMON_LABEL','PRODUCT_CREATE','PRODUCT_UPDATE','PRODUCT_ARCHIVE','PRICE_INVENTORY_SYNC','STOCK_PROJECTION_DISPATCH','TRENDYOL_STAGE_TEST_ORDER','TRENDYOL_CAPABILITY_PROBE','EFATURAM_STAGE_CAPABILITY_PROBE','INVOICE_SUBMIT','INVOICE_CANCELLATION','INVOICE_MARKETPLACE_DELIVERY','MANUAL_ORDER_INVOICE_DELIVERY') THEN 'yazma'
     WHEN j."JobType" IN ('TRENDYOL_ORDER_SYNC','TRENDYOL_ORDER_RECOVERY_SYNC','SHOPIFY_ORDER_SYNC','SHOPIFY_ORDER_RECOVERY_SYNC','HEPSIBURADA_ORDER_SYNC','HEPSIBURADA_ORDER_RECOVERY_SYNC','N11_ORDER_SYNC','N11_ORDER_RECOVERY_SYNC','PAZARAMA_ORDER_SYNC','PAZARAMA_ORDER_RECOVERY_SYNC','PTTAVM_ORDER_SYNC','PTTAVM_ORDER_RECOVERY_SYNC') THEN 'siparis-okuma'
     WHEN j."JobType" IN ('ORDER_STATUS_SYNC','SHOPIFY_ORDER_STATUS_SYNC','HEPSIBURADA_ORDER_STATUS_SYNC','PAZARAMA_ORDER_STATUS_SYNC','PTTAVM_ORDER_STATUS_SYNC') THEN 'durum-taramasi'
     WHEN j."JobType" IN ('RETURN_SYNC','HEPSIBURADA_RETURN_SYNC','RETURN_STATUS_SYNC') THEN 'iade'
     WHEN j."JobType" IN ('PRODUCT_SYNC','SHOPIFY_PRODUCT_SYNC','HEPSIBURADA_PRODUCT_SYNC','CATALOG_STRUCTURE_IMPORT','LISTING_VERIFY','LISTING_LOOKUP') THEN 'katalog'
     WHEN j."Priority" <= 2 THEN 'acil' ELSE 'arka-plan' END
```

### Q1 İş türüne göre bekleme ve bekleme nedenleri (pencere: deploy, referans dalgası, geri kalan)

```sql
WITH params AS (SELECT timestamptz '2026-11-28 09:00:00+00' AS flip_at, interval '24 hours' AS span,
                       timestamptz '2026-11-29 07:55:00+00' AS ref_from, timestamptz '2026-11-29 08:40:00+00' AS ref_to),
typed AS (
  SELECT a.*, <WORK_TYPE> AS work_type,
         CASE WHEN a."StartedAt" < p.flip_at + interval '10 minutes' THEN 'deploy'
              WHEN a."StartedAt" >= p.ref_from AND a."StartedAt" < p.ref_to THEN 'referans'
              ELSE 'normal' END AS slice,
         EXTRACT(EPOCH FROM a."StartedAt" - a."EligibleAt") AS wait_s
  FROM integration.job_attempts a JOIN integration.jobs j ON j."Id" = a."JobId" CROSS JOIN params p
  WHERE a."StartedAt" >= p.flip_at AND a."StartedAt" < p.flip_at + p.span
    AND a."EligibleAt" IS NOT NULL AND a."AttemptNumber" = 1)
SELECT slice, work_type, count(*) AS n,
       round(percentile_cont(0.5) WITHIN GROUP (ORDER BY wait_s)::numeric, 2) AS p50_s,
       round(percentile_cont(0.95) WITHIN GROUP (ORDER BY wait_s)::numeric, 2) AS p95_s,
       round(max(wait_s)::numeric, 1) AS max_s,
       round(percentile_cont(0.95) WITHIN GROUP (ORDER BY coalesce("WaitSlotMs", 0))::numeric / 1000, 2) AS slot_p95_s,
       round(max("WaitSlotMs")::numeric / 1000, 1) AS slot_max_s,
       round(sum("WaitSlotMs")::numeric / 1000, 1) AS slot_sum_s,
       round(sum("WaitLockMs")::numeric / 1000, 1) AS lock_sum_s,
       round(sum("WaitQuotaHoldMs")::numeric / 1000, 1) AS hold_sum_s,
       round(sum("WaitBackoffMs")::numeric / 1000, 1) AS backoff_sum_s,
       count(*) FILTER (WHERE "WaitSlotMs" IS NULL) AS cause_unknown
FROM typed GROUP BY 1, 2 ORDER BY 1, 2;
```

`<WORK_TYPE>` yerine yukarıdaki CASE yazılır. n < 30 olan satırda p95 yerine maks okunur.

### Q2 Webhook: slot beklemesi ve başlama

```sql
WITH params AS (SELECT timestamptz '2026-11-28 09:00:00+00' AS flip_at, interval '24 hours' AS span)
SELECT count(*) AS n,
       round(percentile_cont(0.95) WITHIN GROUP (ORDER BY coalesce(a."WaitSlotMs", 0))::numeric / 1000, 2) AS slot_p95_s,
       round(max(EXTRACT(EPOCH FROM a."StartedAt" - a."EligibleAt"))::numeric, 1) AS start_max_s
FROM integration.job_attempts a JOIN integration.jobs j ON j."Id" = a."JobId" CROSS JOIN params p
WHERE a."StartedAt" >= p.flip_at + interval '10 minutes' AND a."StartedAt" < p.flip_at + p.span AND a."AttemptNumber" = 1
  AND j."Priority" >= 0 AND j."JobType" IN ('TRENDYOL_WEBHOOK_INGEST','SHOPIFY_WEBHOOK_INGEST','HEPSIBURADA_WEBHOOK_INGEST');
```

### Q3 Mağaza başına aynı anda çalışan sınırlı iş (Katalog + Arka Plan + İlk Çekim birlikte)

Sınır üç şeridin işlerini **birlikte** sayar (bir mağazanın üçünde toplam en çok `ConnectionCap`, varsayılan 2); sipariş,
durum, iade, webhook, yazma, toplu ve kullanıcı işleri sayılmaz. Sınıflama Q1'in CASE'iyle yapılır (eski sürümdeki
`NOT LIKE '%ORDER%'` süzgeci uzlaştırma işlerini, örneğin `ORDER_RECONCILIATION`, yanlışlıkla dışarıda bırakıyordu).

```sql
WITH params AS (SELECT timestamptz '2026-11-28 09:00:00+00' AS flip_at, interval '24 hours' AS span),
capped AS (
  SELECT j."ConnectionId", a."StartedAt", coalesce(a."CompletedAt", now()) AS ended
  FROM integration.job_attempts a JOIN integration.jobs j ON j."Id" = a."JobId" CROSS JOIN params p
  WHERE a."StartedAt" >= p.flip_at AND a."StartedAt" < p.flip_at + p.span AND j."ConnectionId" IS NOT NULL
    AND (<WORK_TYPE>) IN ('katalog', 'arka-plan', 'ilk-cekim')),
edges AS (
  SELECT "ConnectionId", "StartedAt" AS t, 1 AS d FROM capped
  UNION ALL SELECT "ConnectionId", ended, -1 FROM capped)
SELECT left(md5("ConnectionId"::text), 8) AS connection, max(running) AS max_capped_at_once
FROM (SELECT "ConnectionId", sum(d) OVER (PARTITION BY "ConnectionId" ORDER BY t, d) AS running FROM edges) s
GROUP BY "ConnectionId" ORDER BY 2 DESC LIMIT 20;
```

### Q4 Kira süresi dolan işler ve hata oranı (geçişten sonraki saat, bir gün önceki aynı saate karşı)

```sql
WITH params AS (SELECT timestamptz '2026-11-28 09:00:00+00' AS flip_at)
SELECT window_name, count(*) AS attempts,
       count(*) FILTER (WHERE NOT a."Succeeded") AS failed,
       round(100.0 * count(*) FILTER (WHERE NOT a."Succeeded") / greatest(count(*), 1), 2) AS failed_pct,
       count(*) FILTER (WHERE a."ErrorCode" = 'LEASE_EXPIRED') AS lease_expired
FROM params p CROSS JOIN LATERAL (VALUES ('sonra', p.flip_at, p.flip_at + interval '1 hour'),
                                         ('dun ayni saat', p.flip_at - interval '1 day', p.flip_at - interval '23 hours')) w(window_name, w_from, w_to)
JOIN integration.job_attempts a ON a."StartedAt" >= w.w_from AND a."StartedAt" < w.w_to
GROUP BY 1 ORDER BY 1;
```

### Q5 Şu an bekleyen ama başlamayan iş (kilidi boş, geri çekilmede değil, 120 sn'den eski)

```sql
SELECT j."JobType", j."Priority", left(md5(coalesce(j."ConnectionId"::text, '')), 8) AS connection,
       round(EXTRACT(EPOCH FROM now() - j."AvailableAt")::numeric) AS waiting_s
FROM integration.jobs j
WHERE j."Status" IN ('PENDING', 'RETRY_SCHEDULED') AND j."AvailableAt" < now() - interval '120 seconds'
  AND j."AttemptCount" < j."MaxAttempts"
  AND NOT EXISTS (SELECT 1 FROM integration.jobs r WHERE r."Status" = 'LEASED' AND r."LeaseExpiresAt" > now()
                  AND r."ConnectionId" = j."ConnectionId" AND coalesce(r."LockGroup", '') = coalesce(j."LockGroup", ''))
  AND NOT EXISTS (SELECT 1 FROM integration.connection_backoffs b WHERE b."ConnectionId" = j."ConnectionId" AND b."Until" > now())
ORDER BY waiting_s DESC LIMIT 20;
```

Kilit grubu çoğu işte iş tipinden türetilir (`MarketplaceSyncExecutionLock.GroupFor`) ve `LockGroup` sütunu boştur; bu
sorgu yalnız sütunu karşılaştırır, bu yüzden bir satır "kilidi gerçekten boş" demek değildir: satırları `prod-logs` ile ve
aynı mağazanın çalışan işiyle tek tek kontrol et. Toplu ve uzun şeritlerin (Toplu İşler, Katalog, Arka Plan, İlk Çekim)
işleri slot doluyken burada görünebilir; eşik kısa iş şeritleri içindir.

### Q6 Şerit sayfasının kendi uyarıları

Kuyruk ve Kota sayfasında her şeridin Durum sütunu (Hedefte / Hedef Aşıldı) ve açık uyarının başlama zamanı; aynı bilgi
`/jobs/metrics/lanes` yanıtında. Hedefler: Kullanıcı ve Webhook 5 sn, Pazaryerine Yazma, Elle Başlatılan Okuma, Sipariş
Okuma ve Katalog 60 sn, İlk Çekim 120 sn, Durum Taraması ve İade 300 sn, Toplu İşler ve Arka Plan 600 sn.

### Q7 Boşta işlem hızı

```sql
SELECT clock_timestamp() AS sample_at, xact_commit, xact_rollback FROM pg_stat_database WHERE datname = current_database();
```

Tek çalıştırmada istatistik donmuş kalır: 60 sn arayla iki kez çalıştır, (ikinci `xact_commit` - birinci) / geçen saniye.
Kuyruk boşken ölç. Replay: boşta esnek model ~1,5 sorgu/sn, dağıtıcı ~0,09 sorgu/sn.

## 4. Geri dönüş eşikleri (biri aşılırsa geri dön)

Geri dönüş: iki `appsettings.json` dosyasında `"LaneModel": "flexible"`, commit, `ship` (`docs/RUNBOOK-GERI-ALMA.md` 2b).
Deploy penceresi (geçişten sonraki ilk 10 dk) eşiklere girmez; o pencere ayrı okunur ve esnek modelin bir yeniden
açılışıyla karşılaştırılır.

| Eşik | Kaynak |
|---|---|
| Kısa iş şeritlerinde (Kullanıcı, Webhook, Pazaryerine Yazma, Elle Başlatılan Okuma, Sipariş Okuma, Durum Taraması, İade) bir denemenin `WaitSlotMs` değeri > 16 sn (güvenlik sayımı 15 sn + 1 sn: kaçmış bir uyandırma) | Q1 `slot_max_s` |
| Kullanıcı p95 > 5 sn (n ≥ 10) ya da herhangi biri > 15 sn | Q1 |
| Webhook slot beklemesi p95 > 1 sn ya da başlama > 30 sn | Q2 |
| Sipariş Okuma başlama p95 > 60 sn ya da herhangi biri > 180 sn | Q1 |
| Kilidi boş, geri çekilmede olmayan koşulabilir bir iş > 120 sn başlamadı (kısa iş şeritleri) | Q5 |
| Bir mağaza aynı anda sınırdan (`ConnectionCap`, 2) fazla sınırlı iş çalıştırdı | Q3 |
| `LEASE_EXPIRED` > 0 | Q4 |
| Hata oranı bir gün önceki aynı saatin 2 katından fazla | Q4 |
| Worker günlüğünde "dispatcher turn" hatası dakikada birden çok, ya da sağlık dosyası bayat (watchdog yeniden başlattı) | `prod-logs logs worker --grep dispatcher`, `status` |

Bilinen ve eşik dışı sayılmayan farklar (replay, `QueueReplayTests`): bir mağazanın sınırlı işleri o mağazanın kendi uzun
işlerini bekler (iki uzun referans okuması varken uzlaştırmaları ~500 sn, iki yeni mağazanın ilk çekiminde son iş ~21 dk);
"Tüm Ürünleri Çek" sırasında bir sipariş okuması, mağazanın o an çalışan uzlaştırmasını bekleyebilir (p95 ~10 sn, bugün
~2 sn; en kötüsü bugünkünden kısa).
