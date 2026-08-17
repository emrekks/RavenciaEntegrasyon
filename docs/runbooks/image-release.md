# Immutable Image Release Runbook'u

Bu runbook, onaylı bir Git commit'inden production adayına ait Linux/amd64 uygulama ve edge imajlarını üretir. İmaj yayınlamak deploy değildir; hedef Ubuntu Server 26.04 LTS, production secret, DNS, backup/restore ve Go/No-Go kapıları ayrıca tamamlanır.

## Ön koşullar

- Yayınlanacak commit `main` dalında olmalı ve `Verify source changes` GitHub kontrolünü başarıyla tamamlamış olmalıdır. Bu kontrol locked restore, build, test, format, web build ve repository guard setini exact runtime ile çalıştırır.
- GitHub Actions repository için etkin olmalı; workflow'un GitHub Container Registry'ye yazabilmesi için `packages: write` izni korunmalıdır.
- Gerçek secret, `.env`, PFX, platform credential veya fixture workflow'a eklenmez. Yayın akışı yalnız GitHub'ın kısa ömürlü `GITHUB_TOKEN` değerini registry oturumu için kullanır.

## Yayın

1. GitHub'da **Actions → Publish immutable release images → Run workflow** yoluyla `main` dalındaki onaylı commit'i seç.
2. Akış, commit'in `main` üzerinde olduğunu ve aynı commit için başarılı `Verify source changes` kontrolü bulunduğunu GitHub Checks API ile doğrular. Doğrulama yoksa registry oturumu açılmaz ve imaj yayınlanmaz; kaynak testleri tag'de ikinci kez çalıştırılmaz.
3. Akışın iki imajı da başarıyla oluşturmasını bekle. Uygulama `Dockerfile`, production edge ise yalnız `deploy/caddy/Dockerfile.production` üzerinden oluşturulur.
4. Job özetindeki Git commit'i ve iki `name@sha256:...` değerini release kaydına kopyala:
   - `MARKETPLACEHUB_APP_IMAGE`
   - `MARKETPLACEHUB_EDGE_IMAGE`
5. `sha-<commit>` etiketi yalnız bulunabilirlik içindir. Compose veya deploy kaydında etiket kullanma; yalnız digest'li tam adı kullan.

Akış, iki digest'i de `sha256:` ve 64 küçük hexadecimal karakter olarak doğrulamadan başarılı sayılmaz. Base image'lar Dockerfile içinde sabit digest ile bağlıdır; checkout action'ı tam commit SHA ile sabittir. Buildx `v0.34.1` resmi release binary'si bounded retry ile indirilir ve sabit SHA-256 doğrulamasından geçmeden builder oluşturulmaz. Buildx ayrıca provenance ve SBOM üretir.

Resmî doğrulama kaynakları: [GitHub Container Registry image yayınlama](https://docs.github.com/en/actions/tutorials/publish-packages/publish-docker-images), [Caddy Automatic HTTPS](https://caddyserver.com/docs/automatic-https) ve [Docker Buildx releases](https://github.com/docker/buildx/releases).

## Deploy öncesi bağımsız doğrulama

AWS Ubuntu sunucusunda digest'leri registry'den çek ve `sudo docker image inspect` ile platformun `linux/amd64` olduğunu doğrula. Ardından production Compose çözümlemesini yalnız secret içeriklerini ekrana basmadan incele. `compose.production.yaml`, digest değişkenlerinden biri yoksa fail-closed olur.

Production edge imajındaki Caddy yapılandırması public DNS adı için otomatik HTTPS ve HTTP→HTTPS yönlendirmesi kullanır. DNS A/AAAA kayıtları hedef sunucuya yönelmeden, dışarıdan 80/443 erişimi olmadan ve kalıcı `caddy_data` volume'u hazır olmadan production Caddy başlatılmaz.

## Geri alma kaydı

Her release kaydında mevcut ve bir önceki onaylı app/edge digest çifti birlikte tutulur. Rollback yalnız önceki digest çiftine dönerek yapılır; `latest`, floating tag veya tahmini image yeniden üretimi kullanılmaz. Migration uyumsuzluğunda [deployment-and-rollback.md](deployment-and-rollback.md) ve [backup-and-restore.md](backup-and-restore.md) birlikte uygulanır.
