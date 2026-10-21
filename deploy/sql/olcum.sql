-- Salt-okunur canlı ölçüm (son 24 saat). Hiçbir satır değiştirmez.
-- Çalıştırma:
--   sudo docker exec -i marketplacehub-postgres-1 psql -U marketplacehub -d marketplacehub < ~/RavenciaEntegrasyon/deploy/sql/olcum.sql
\set ON_ERROR_STOP on
\pset pager off
begin transaction read only;

\echo '== 1. Sipariş okuma politikaları (beklenen: 180 sn; elle kaydedilenler değişmez) =='
select c."PlatformCode" as platform, c."DisplayName" as baglanti, p."IntervalSeconds" as aralik_sn,
       p."UserDefined" as elle, p."Enabled" as acik, p."LastScheduledAt" at time zone 'Europe/Istanbul' as son_kuyruk
from integration.connection_sync_policies p
join integration.platform_connections c on c."Id" = p."ConnectionId"
where p."ResourceType" = 'ORDERS' and c."Status" in ('ACTIVE', 'VERIFIED')
order by 1, 2;

\echo '== 2. Sipariş okuma işleri: gerçek aralık (iki iş arası), bekleme ve çalışma süresi =='
with order_jobs as (
  select j."Id", j."ConnectionId", j."JobType", j."CreatedAt", j."Status",
         j."CreatedAt" - lag(j."CreatedAt") over (partition by j."ConnectionId", j."JobType" order by j."CreatedAt") as gap
  from integration.jobs j
  where j."CreatedAt" > now() - interval '24 hours'
    and j."JobType" in ('TRENDYOL_ORDER_SYNC', 'HEPSIBURADA_ORDER_SYNC', 'N11_ORDER_SYNC', 'PAZARAMA_ORDER_SYNC', 'SHOPIFY_ORDER_SYNC')
), first_attempts as (
  select a."JobId", min(a."StartedAt") as started, max(a."CompletedAt" - a."StartedAt") as run
  from integration.job_attempts a join order_jobs o on o."Id" = a."JobId"
  group by a."JobId"
)
select c."PlatformCode" as platform, c."DisplayName" as baglanti, count(*) as is_sayisi,
       round(extract(epoch from percentile_cont(0.5) within group (order by o.gap))::numeric) as aralik_p50_sn,
       round(extract(epoch from percentile_cont(0.95) within group (order by o.gap))::numeric) as aralik_p95_sn,
       round(extract(epoch from percentile_cont(0.95) within group (order by f.started - o."CreatedAt"))::numeric) as bekleme_p95_sn,
       round(extract(epoch from percentile_cont(0.5) within group (order by f.run))::numeric, 1) as calisma_p50_sn,
       round(extract(epoch from percentile_cont(0.95) within group (order by f.run))::numeric, 1) as calisma_p95_sn,
       count(*) filter (where o."Status" in ('DEAD', 'MANUAL_REVIEW')) as basarisiz
from order_jobs o
join integration.platform_connections c on c."Id" = o."ConnectionId"
left join first_attempts f on f."JobId" = o."Id"
group by 1, 2
order by 1, 2;

\echo '== 3. Webhook durumu (sağlıklıyken sipariş okuması 8 dk, değilse 3 dk) =='
select c."PlatformCode" as platform, c."DisplayName" as baglanti, w."Status" as durum,
       w."VerifiedAt" at time zone 'Europe/Istanbul' as dogrulandi,
       w."LastReceivedAt" at time zone 'Europe/Istanbul' as son_bildirim,
       round(extract(epoch from now() - w."LastReceivedAt") / 60) as son_bildirimden_dk,
       h."OpaqueCursor" as kacirilan_kayitlari
from integration.webhook_subscriptions w
join integration.platform_connections c on c."Id" = w."ConnectionId"
left join integration.sync_cursors h on h."ConnectionId" = w."ConnectionId" and h."ResourceType" = 'WEBHOOK_HEALTH'
where w."Status" <> 'DELETED'
order by 1, 2;

\echo '== 4. Yeni siparişin panele düşme gecikmesi (sipariş anı -> panele yazılma) =='
select c."PlatformCode" as platform, count(*) as siparis,
       round(extract(epoch from percentile_cont(0.5) within group (order by d.lag)) / 60, 1) as gecikme_p50_dk,
       round(extract(epoch from percentile_cont(0.95) within group (order by d.lag)) / 60, 1) as gecikme_p95_dk,
       round(extract(epoch from max(d.lag)) / 60, 1) as gecikme_max_dk
from (
  select o."ConnectionId",
         -- Trendyol sipariş zamanları Türkiye saatiyle (UTC+3) saklanır.
         o."CreatedAt" - (o."OrderedAt" - case when pc."PlatformCode" = 'TRENDYOL' then interval '3 hours' else interval '0' end) as lag
  from sales.orders o
  join integration.platform_connections pc on pc."Id" = o."ConnectionId"
  where o."CreatedAt" > now() - interval '24 hours'
) d
join integration.platform_connections c on c."Id" = d."ConnectionId"
where d.lag between interval '0' and interval '1 day'
group by 1
order by 1;

\echo '== 5. Okuma kayıtları: son başarı yaşı, süre, istek sayısı, art arda hata =='
select c."PlatformCode" as platform, c."DisplayName" as baglanti, c."Status" as baglanti_durumu, s."ResourceType" as kaynak,
       round(extract(epoch from now() - s."LastSuccessAt") / 60, 1) as son_basari_dk,
       s."LastDurationMs" as son_sure_ms, s."LastRequestCount" as istek, s."LastReceivedCount" as alinan,
       s."ConsecutiveFailureCount" as ardisik_hata, left(s."LastError", 80) as son_hata
from integration.sync_cursors s
join integration.platform_connections c on c."Id" = s."ConnectionId"
where c."Status" <> 'DELETED'
  and s."ResourceType" in ('ORDERS_HOT', 'ORDERS_RECOVERY', 'ORDER_LIFECYCLE', 'RETURNS', 'QUESTIONS')
order by 1, 2, 4;

\echo '== 6. Son 24 saatte başarısız / incelemeye düşen işler (tür ve hata koduna göre) =='
select j."JobType" as is_turu, j."Status" as durum, j."LastErrorCode" as hata_kodu, count(*) as adet
from integration.jobs j
where j."CreatedAt" > now() - interval '24 hours' and j."Status" in ('DEAD', 'MANUAL_REVIEW', 'BLOCKED')
group by 1, 2, 3
order by 4 desc
limit 30;

\echo '== 7. Bugünkü değişikliklerle gelen yeni kodlar (açık sorunlar ve iş hataları) =='
select 'issue' as kaynak, i."Code" as kod, count(*) as adet, max(i."LastSeenAt") at time zone 'Europe/Istanbul' as son
from ops.operational_issues i
where lower(i."Status") <> 'resolved' and i."LastSeenAt" > now() - interval '24 hours'
group by 2
union all
select 'job', j."LastErrorCode", count(*), max(j."CreatedAt") at time zone 'Europe/Istanbul'
from integration.jobs j
where j."CreatedAt" > now() - interval '24 hours'
  and j."LastErrorCode" in ('RESTORE_QUARANTINE', 'INVOICE_SUBMISSION_STATE_CHANGED', 'EINVOICE_RECIPIENT_REVIEW_REQUIRED',
                            'INVOICE_SALE_STATUS_NOT_ELIGIBLE', 'TRENDYOL_ORDER_SCAN_INCOMPLETE', 'SHOPIFY_TIMEOUT')
group by 2
order by 3 desc;

\echo '== 8. Veritabanı bağlantıları (sınır max_connections) =='
select coalesce(nullif(application_name, ''), '(adsız)') as uygulama, state as durum, count(*) as baglanti,
       current_setting('max_connections') as sinir
from pg_stat_activity
where datname = current_database()
group by 1, 2
order by 3 desc;

\echo '== 9. Panel stoğu ve pazaryerinde son görülen stok (aktif teklifler) =='
-- ObservedRemoteQuantity is the stock the last product import of any marketplace saw for the variant.
select c."PlatformCode" as platform, c."DisplayName" as baglanti, count(*) as teklif,
       count(*) filter (where i."ObservedRemoteQuantity" is null) as gozlem_yok,
       count(*) filter (where i."ObservedRemoteQuantity" is not null and i."OnHand" <> i."ObservedRemoteQuantity") as stok_farkli,
       coalesce(sum(abs(i."OnHand" - i."ObservedRemoteQuantity")), 0) as toplam_fark_adet,
       count(*) filter (where i."Available" <= 0) as panelde_satilabilir_yok,
       count(*) filter (where i."Reserved" > 0) as ayrilmis_var,
       round((percentile_cont(0.5) within group (order by extract(epoch from now() - i."ObservedRemoteAt")) / 86400)::numeric, 1) as gozlem_yasi_gun_p50
from inventory.channel_offers o
join inventory.inventory_items i on i."TenantId" = o."TenantId" and i."VariantId" = o."VariantId" and i."LocationCode" = 'MAIN'
join integration.platform_connections c on c."Id" = o."ConnectionId"
where o."Status" = 'ACTIVE' and c."Status" in ('ACTIVE', 'VERIFIED')
group by 1, 2
order by 1, 2;

\echo '== 10. Stok ve fiyat gönderimi (ikisi de açık olmadan stok gitmez) =='
select c."PlatformCode" as platform, c."DisplayName" as baglanti,
       c."SettingsJson"::jsonb ->> 'ExternalWritesEnabled' as dis_yazma,
       bool_or(p."Enabled") filter (where p."ResourceType" = 'STOCK_WRITE') as stok_yazma,
       bool_or(p."Enabled") filter (where p."ResourceType" = 'PRICE_WRITE') as fiyat_yazma
from integration.platform_connections c
left join integration.connection_sync_policies p on p."ConnectionId" = c."Id" and p."ResourceType" in ('STOCK_WRITE', 'PRICE_WRITE')
where c."Status" in ('ACTIVE', 'VERIFIED')
group by 1, 2, 3
order by 1, 2;

\echo '== 11. Stoğu karşılanamayan sipariş satırları (gün ve platforma göre, son 30 gün) =='
select date(i."FirstSeenAt" at time zone 'Europe/Istanbul') as gun, c."PlatformCode" as platform, count(*) as satir
from ops.operational_issues i
join sales.order_lines l on 'stock-order-consumption:' || replace(l."Id"::text, '-', '') = i."DedupeKey"
join sales.orders o on o."Id" = l."OrderId"
join integration.platform_connections c on c."Id" = o."ConnectionId"
where i."Code" = 'STOCK_ORDER_CONSUMPTION_BLOCKED' and i."FirstSeenAt" > now() - interval '30 days'
group by 1, 2
order by 1, 2;

rollback;
