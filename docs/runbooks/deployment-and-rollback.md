# F1 Deployment ve Rollback Runbook'u

## Production kapıları

- Hedef Ubuntu Server 26.04 LTS üzerinde doğrudan Linux/amd64 Docker Engine, exact Compose v2.40.2, systemd reboot, kalıcı volume ve restore kanıtı tamamlanmış olmalı.
- Production domaininin DNS A/AAAA kaydı hedef Ubuntu sunucuyu göstermeli; dışarıdan yalnız `80/443` erişimi ve kalıcı Caddy data volume'u doğrulanmalı. Production edge public ACME sertifikası ve HTTP→HTTPS yönlendirmesi kullanır; `tls internal` yalnız PILOT_LOCAL içindir.
- `MARKETPLACEHUB_APP_IMAGE` ve `MARKETPLACEHUB_EDGE_IMAGE` registry tag değil `name@sha256:...` olmalı.
- Production site address HTTPS olmalı; yalnız Caddy host `80/443` açmalı. API/Worker'ın host portu olmadan outbound `egress` ağı bulunmalı; PostgreSQL/backup bu ağa bağlanmamalı.
- Data Protection PFX ve parolası read-only secret olarak mount edilmeli; yoksa uygulama fail-closed olmalı.
- `external-writes=false`; gerçek platform capability'leri `UNKNOWN` kalmalı.
- Deploy öncesi doğrulanmış backup ve restore kanıtı bulunmalı.

## Deploy sırası

1. [Immutable image release runbook'u](image-release.md) ile app/edge imajlarını üret; image digest, Git commit, migration listesi ve backup manifestini release kaydına yaz.
2. `compose.production.yaml` ile `config` çıktısını incele; floating/latest ve doğrudan API/DB portu olmadığını doğrula.
3. Backup al ve off-host aktarımını doğrula.
4. `migrate` one-shot servisini çalıştır; exit `0` olmadan API/Worker başlatma.
5. API/Worker ve son olarak Caddy başlat; live/ready ve güvenli header/cookie smoke yap.
6. Bootstrap yalnız ilk kurulumda ayrı onayla çalışır.

## Rollback

Uygulama sorunu migration gerektirmiyorsa önceki onaylı app/edge digest'lerine dön ve readiness doğrula. Şema uyumsuzsa sistemi write kapalı tut, onaylı backup setinden yeni boş volume/DB'ye restore et ve önceki digest'lerle doğrula. In-place tahmini downgrade çalıştırma. Volume silen `down -v`, otomatik migration rollback ve secret loglama yasaktır.
