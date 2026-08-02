# F1 Backup ve Restore Runbook'u

## Backup

```bash
docker compose --env-file deploy/secrets/production.env \
  -f deploy/compose/compose.yaml \
  -f deploy/compose/compose.production.yaml \
  --profile operations run --rm backup
```

Her set `database.dump`, `private-volumes.tar.gz`, `SHA256SUMS` ve `manifest.json` üretir. Private archive app files ile Data Protection key ring'i birlikte içerir. Staging volume backup değildir; set aynı çalışma periyodunda onaylı, şifreli off-host hedefe taşınmalıdır.

## İzole restore doğrulaması

1. PostgreSQL 18.4 ile boş ve üretimden izole hedef oluştur.
2. Dump owner rolü `marketplacehub` hedefte yoksa önce rolü oluştur.
3. `sha256sum -c SHA256SUMS` ve `pg_restore --list database.dump` çalıştır.
4. `pg_restore --exit-on-error` ile boş veritabanına restore et.
5. `iam`, `integration`, `ops` şemalarını, migration history'yi ve Owner/bootstrap sayımlarını doğrula.
6. Private archive'ı boş bir volume'a aç; safe path'leri, dosya checksum'larını ve Data Protection key'lerini doğrula.
7. API/Worker'ı restore edilmiş DB/volume'larla yalnız internal ağda başlat; readiness ve kontrollü login testi yap.
8. Kanıtı `backup manifest id`, image digest, başlangıç/bitiş, sonuç ve operatör ile kaydet; geçici restore hedefini kaldır.

2026-07-31 yerel kanıtında dump izole PostgreSQL 18.4 hedefine restore edilmiş ve üç F1 şemasında 21 tablo görülmüştür. Bu sonuç hedef Ubuntu Server RTO ölçümü değildir.
