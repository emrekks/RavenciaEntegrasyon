-- PTT AVM ürün eşlemesinin durumu. Salt-okunur; hiçbir satır değiştirmez.
-- Çalıştırma:
--   sudo docker exec -i marketplacehub-postgres-1 psql -U marketplacehub -d marketplacehub < ~/RavenciaEntegrasyon/deploy/sql/pttavm-esleme.sql
\set ON_ERROR_STOP on
\pset pager off
begin transaction read only;

\echo '== 1. Son PTT AVM ürün işleri (durum, süre, hata) =='
select j."CreatedAt" at time zone 'Europe/Istanbul' as olusturuldu, j."Status" as durum, j."AttemptCount" as deneme,
       j."LastErrorCode" as hata_kodu, left(j."LastErrorSummary", 120) as hata
from integration.jobs j
join integration.platform_connections c on c."Id" = j."ConnectionId"
where c."PlatformCode" = 'PTTAVM' and j."JobType" = 'PRODUCT_SYNC'
order by j."CreatedAt" desc
limit 5;

\echo '== 2. Son işin ilerlemesi (aktarım havuzu: STAGED = bekliyor, COMPLETED = işlendi) =='
select s."State" as durum, count(*) as model
from catalog.product_import_staging_records s
where s."JobId" = (select j."Id" from integration.jobs j join integration.platform_connections c on c."Id" = j."ConnectionId"
                   where c."PlatformCode" = 'PTTAVM' and j."JobType" = 'PRODUCT_SYNC' order by j."CreatedAt" desc limit 1)
group by 1;

\echo '== 3. PTT AVM varyant bağları: doğrulanmış kod ile eski kimlik (MODEL/123) =='
select case when l."ExternalId" ~ '/[0-9]+$' then 'eski kimlik (doğrulanmadı)' else 'kod ile bağlı' end as bag, count(*) as varyant
from catalog.marketplace_variant_links l
join integration.platform_connections c on c."Id" = l."ConnectionId"
where c."PlatformCode" = 'PTTAVM'
group by 1;

\echo '== 4. Son 6 saatin PTT AVM eşleme uyarıları =='
select i."Code" as kod,
       case when i."Summary" like 'PTT AVM panel varyantını tanımadı%' then 'PTT tanımadı'
            when i."Summary" like 'PTT AVM kodu%' then 'kod başka varyantta'
            else 'diğer' end as tur,
       count(*) as adet, max(i."LastSeenAt") at time zone 'Europe/Istanbul' as son
from ops.operational_issues i
join integration.platform_connections c on i."DedupeKey" like '%' || c."Id"::text || '%'
where c."PlatformCode" = 'PTTAVM' and i."LastSeenAt" > now() - interval '6 hours'
  and i."Code" in ('PRODUCT_MAPPING_UNMATCHED', 'PTTAVM_CODE_CHECK_FAILED')
group by 1, 2
order by 3 desc;

\echo '== 5. Kod sorgusu hatası (varsa metni) =='
select i."Summary" as ozet, i."LastSeenAt" at time zone 'Europe/Istanbul' as son
from ops.operational_issues i
where i."Code" = 'PTTAVM_CODE_CHECK_FAILED' and i."LastSeenAt" > now() - interval '6 hours';

rollback;
