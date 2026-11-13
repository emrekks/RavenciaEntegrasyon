# Otomatik Deploy (GitHub Actions → Hostinger Ubuntu)

`main` dalına push → `Validate` workflow'u geçer → `Deploy` workflow'u sunucuda
`deploy/scripts/fast-deploy.sh` çalıştırır (imaj derleme, taze yedek, migration, health doğrulaması).
Geri alma: [RUNBOOK-GERI-ALMA.md](RUNBOOK-GERI-ALMA.md).

## Akış

1. `.github/workflows/ci.yml` (`Validate`) derleme, test, format, imaj derlemesi ve güvenlik denetimlerini çalıştırır.
2. Deploy iki yoldan tetiklenir (sabah 09:20 zamanlanmış çalıştırma 27 Ekim 2026'da kaldırıldı):
   - `main` üzerindeki başarılı bir push'un Validate'i bittiğinde (`workflow_run`), o commit;
   - elle: Actions → Deploy → Run workflow (yalnızca `main`; isteğe bağlı `redeploy` kutusu).
   Elle çalıştırma **son yeşil Validate SHA'sını** (`main`'e push, `success`) çözer
   (`permissions: actions: read`).
3. Deploy işi sunucuya SSH ile bağlanır ve 40 karakterlik SHA'yı (gerekirse `"<sha> redeploy"`) gönderir.
4. Sunucuda `/usr/local/bin/ravencia-ci-deploy` (`deploy/scripts/ci-entrypoint.sh`) çalışır:
   kilit alır, aşağıdaki karar tablosunu uygular, gerekirse repoyu o commit'e fast-forward eder,
   `fast-deploy.sh`'ı SSH bağlantısından bağımsız çalıştırır ve logu `~/deploy-logs/` altına yazar.
5. İş sonunda `https://panel.ravencia.com/health/live` kontrol edilir (ready/queues dışarıya kapalı,
   bkz. Kenar). Hazırlık (`/health/ready`) sunucu tarafında deploy betiği içinde doğrulanır.

Eşzamanlı çalışmalar `production-deploy` concurrency grubu ve sunucudaki `flock` ile sıraya girer.

### ci-entrypoint karar tablosu (`~/deploy-logs/` durum dosyaları)

| Durum | Sonuç |
| --- | --- |
| SHA `origin/main`'in atası/aynısı değil (başka dal, force-push ile çıkarılmış commit) | **çıkış 1**, deploy yok |
| SHA, `.hold` içindeki SHA'nın atası/aynısı (geri alınmış sürüm) | deploy yok, `::warning::`, çıkış 0 |
| `.last-success` SHA'yı içeriyor (ve `redeploy` yok) | "already live", çıkış 0 |
| SHA, çalışma ağacındaki HEAD'in atası ama `.last-success` içermiyor (daha yeni deploy başarısız olmuş) | **çıkış 1** — ileri düzeltme ya da `rollback.sh` |
| Diğer | fast-forward + `fast-deploy.sh`; başarıda `.last-success` = SHA, daha yeni bir sürüm `.hold`'u kaldırır |

`.last-success` henüz yoksa (bu betiğin ilk kurulumu) eski kural geçerlidir: HEAD'in atası olan SHA için 0.

**Sunucu 7/24 açık** (27 Ekim 2026'dan beri gece 00:30–09:00 kapanışı yok); deploy saate bakılmadan başlatılır (23:45 ertelemesi 26 Ekim 2026'da kaldırıldı). Sunucu elle durdurulursa diye
`ravencia-deploy-guard.service` kurulur: açılışta bir şey yapmaz, kapanışta `ExecStop`
(`wait-for-deploy-lock.sh`) `~/deploy-logs/.lock` serbest kalana kadar en çok 23 dk bekler. Docker'dan
önce durdurulduğu için konteynerler bu sırada çalışır; deploy'un koştuğu SSH oturum kapsamları
`/etc/systemd/system/session-.scope.d/ravencia-deploy-guard.conf` ile bu birimden sonra durdurulur.
Sunucuda doğrulama: `systemctl show -p After session-<n>.scope` çıktısında `ravencia-deploy-guard.service`
görünmeli. Sağlayıcı (Hostinger) durdurmayı kendi süre sınırından sonra zorlarsa bekleme yarıda kalır; elle durdurma sırasında
süren bir deploy yarıda kesilebilir.
`releases.tsv` her deploy/geri alma için bir satır tutar (zaman, tür, SHA, imajlar).

**`ci-entrypoint.sh` değiştiğinde** sunucuda bir kez yeniden kurulmalıdır (root'a ait kopya):
`bash deploy/scripts/install-ci-deploy.sh "$(cat deploy_key.pub)"`.

## Deploy betikleri

- `fast-deploy.sh` (CI yolu, sunucuda yerel imaj derler) ve `deploy.sh` (ilk kurulum / registry digest)
  ortak fonksiyonları `deploy/scripts/lib.sh`'tan alır: gizli dosya kontrolü, `production.env` okuma,
  doğrulanmış yedek, readiness/worker/frontend doğrulaması, sürüm kaydı.
- Deploy öncesi yedek **her zaman taze** alınır (`BACKUP_FORCE=true`); günün daha önceki seti yeniden
  kullanılmaz.
- `fast-deploy.sh` yeni sürümün ve **bir önceki** sürümün `marketplacehub-{app,edge}:manual-<rev>`
  imajlarını tutar (N-1, geri alma için); daha eskilerini siler.
- Her deploy `install-backup-timer.sh` ile sunucudaki systemd birimlerini yeniler ve zamanlayıcıları
  etkinleştirir (bkz. İşletim).
- **Otomatik geri alma**: yeni sürüm başlamaz ya da doğrulanamazsa ve migration klasörü önceki sürümden
  beri değişmediyse `rollback.sh` önceki `manual-*` imajlarıyla otomatik çalışır (yedeksiz; şema aynı).
  Deploy yine başarısız sayılır, alarm gider, sürüm `.hold`'a girer. Ayrıntı ve koşullar:
  [RUNBOOK-GERI-ALMA.md](RUNBOOK-GERI-ALMA.md) bölüm 2. Kapatmak: `RAVENCIA_AUTO_ROLLBACK=false`.
- **Migration provası** (O12): CI migration'ları yalnız boş veritabanında dener. Deploy, taze yedekten
  sonra ve üretim migration'ından önce yeni imajın `migrate` komutunu o yedeğin izole bir geri yüklemesine
  karşı çalıştırır (`restore-drill.sh --migration-only`; ayrı ağ/volume, `marketplacehub_rehearsal_*`).
  Başarısız olursa ya da süre (`RAVENCIA_MIGRATION_REHEARSAL_TIMEOUT_SECONDS`, varsayılan 1200 sn) dolarsa
  deploy üretime dokunmadan durur. Bedeli tam bir `pg_restore` + migration süresidir (veritabanı boyutuna
  göre birkaç dakika); bu yüzden varsayılan `auto` yalnız migration klasörü çalışan sürümden beri
  değiştiyse (ya da çalışan sürüm bilinmiyorsa) prova yapar; kod-yalnız deploy'lar etkilenmez. Seçim:
  `RAVENCIA_MIGRATION_REHEARSAL` (elle) ya da `production.env` içinde
  `MARKETPLACEHUB_MIGRATION_REHEARSAL=auto|always|never`. Kök disk %80'in üstündeyse (ikinci bir veritabanı
  kopyası gerekir) prova uyarıyla atlanır. Süre dolması üretimde de uzun sürecek bir migration demektir:
  bir pencere planlanıp elle `RAVENCIA_MIGRATION_REHEARSAL=never` ile deploy edilir.
- **Migration süresi**: `fast-deploy.sh` migrate konteynerini `docker wait` ile en çok 30 dakika bekler
  (`RAVENCIA_MIGRATION_TIMEOUT_SECONDS`, varsayılan 1800); süre dolarsa konteyneri durdurur (açık işlem geri
  alınır, eski yığın son tamamlanan migration'ın şemasıyla hizmete devam eder) ve deploy başarısız olur.
  `migrate` komutu (`DatabaseMigrator`) yalnız kendi oturumunda `lock_timeout=15s` ve
  `statement_timeout=20min` kullanır: eski API/Worker çalışırken `ALTER TABLE` kilit kuyruğunda bekleyip
  tüm yazmaları kilitlemez; kilit alınamazsa tüm çalıştırma 20 sn arayla 4 kez denenir. Uygulamanın normal
  sorguları bu ayarlardan etkilenmez. `CREATE INDEX CONCURRENTLY` yazmaları kilitlemediği için bu tür
  migration'lar kendi içinde `lock_timeout`'u kapatabilir. Deploy işinin GitHub süresi 90 dakikadır.

## GitHub Secrets

Deploy secret'ları 3 Kasım 2026'dan beri repo düzeyinde değil, **`production` environment'ında** (Settings →
Environments → production). Environment yalnız `main` dalından çalışan işlere açık; başka daldan tetiklenen bir iş
deploy anahtarını göremez. `main` ruleset'i ("main koruması") silmeyi ve force-push'u engeller ve commit'in
Validate `gate` kontrolünü geçmiş olmasını şart koşar (ship.sh sha'yı önce `codex/*` dalında doğruladığı için
bu kural ona engel olmaz).

| Secret (`production` environment) | Değer |
| --- | --- |
| `DEPLOY_HOST` | Sunucu IP/DNS adresi (Hostinger KVM 4, `187.126.24.163`) |
| `DEPLOY_USER` | `deploy` (3 Kasım 2026'dan beri; ayrı, kısıtlı kullanıcı, bkz. Güvenlik modeli) |
| `DEPLOY_SSH_KEY` | Deploy özel anahtarı (ed25519, tüm içerik) |
| `DEPLOY_KNOWN_HOSTS` | `ssh-keyscan` / sunucu host key satırı (`<host> ssh-ed25519 AAAA...`) |
| `ALERT_WEBHOOK_URL` | *(isteğe bağlı)* Validate (main) veya Deploy başarısız olunca bildirim; sunucudaki `alert_webhook_url.txt` ile aynı biçim |

## Güvenlik modeli

- Deploy anahtarı yalnız **`deploy`** kullanıcısında, `/home/deploy/.ssh/authorized_keys` içinde
  `restrict,command="/usr/local/bin/ravencia-ci-deploy"` ile kısıtlıdır: shell, port yönlendirme ve başka komut
  çalıştıramaz. Hem anahtar dosyası hem script root'a aittir; repo pull'u ya da `deploy` kullanıcısı bu yetkiyi
  değiştiremez. `deploy`'un şifresi kilitli ve tek `sudo` hakkı bu scripti repo sahibi olarak çalıştırmaktır
  (`/etc/sudoers.d/ravencia-deploy`: `deploy ALL=(ubuntu) NOPASSWD: /usr/local/bin/ravencia-ci-deploy`). Script
  `deploy` olarak başlayınca isteği doğrular ve `sudo -n -u ubuntu` ile kendini yeniden çalıştırır; repo,
  `~/deploy-logs` ve kilit `ubuntu`'da kalır. sshd `AllowUsers ubuntu deploy ravencia-ro`.
  Not: deploy zinciri (`fast-deploy.sh`) bugün `ubuntu`'nun şifresiz `sudo`'suyla Docker'ı yönetir; güvenlik
  planındaki A4 (`ubuntu`'dan `NOPASSWD` kaldırma) bu zincir root'a taşınmadan yapılamaz.
- **Salt okunur erişim (`ravencia-ro`)**: Claude'un `/prod-sql`, `/prod-logs` ve `ship.sh` kontrolleri ayrı bir
  kullanıcıyla ve parolasız ayrı bir anahtarla (`~/.ssh/ravencia_ro`, yalnız bu iş için) girer. Zorunlu komut
  `/usr/local/bin/ravencia-ro` (`deploy/scripts/readonly-entrypoint.sh`, root'a ait) yalnız şunları kabul eder:
  `sql`, `status`, `images`, `host` (disk, `ravencia-*` zamanlayıcıları ve journal'ı, başarısız birimler, yedek
  setleri ve boyutları, CrowdSec durumu), `logs`, `errors`, `journal`. Tek `sudo` hakkı bu scripti root olarak
  çalıştırmaktır (`/etc/sudoers.d/ravencia-ro`). Kurulum/yenileme:
  `bash deploy/scripts/install-readonly-access.sh ["$(cat ravencia_ro.pub)"]`.
  **`sql` sınırı veritabanı rolüdür:** psql `ravencia_ro` rolüyle bağlanır (`install-readonly-db-role.sh`, aynı
  kurulumda çalışır): süper kullanıcı değil, yalnız `pg_read_all_data` üyesi, rol ayarı olarak
  `default_transaction_read_only=on` ve 60 sn `statement_timeout`, şifresiz (yalnız konteynerin unix soketinden,
  yani root gerektiren `docker exec` ile girer; ağdan scram şifresi gerekir). Yazma, `COPY … TO PROGRAM` ve sunucu
  dosyası okuma rol izniyle reddedilir. İkinci katman olarak betik SQL'i `BEGIN READ ONLY … ROLLBACK` içine alır ve
  SELECT/WITH/VALUES/TABLE/EXPLAIN/SHOW dışındaki ifadeleri, `\echo`/`\x` dışındaki psql komutlarını, yalnız
  kendi satırında olmayan `--` yorumlarını, blok yorumları, string içinde ters bölüyü ve yan etkili fonksiyonları
  baştan reddeder.
- Sunucu host key'i `StrictHostKeyChecking=yes` ile sabitlenir.
- Deploy işi repo kodunu çalıştırmaz; yalnızca SSH ile SHA gönderir.
- Actions'lar commit SHA ile sabitlenmiştir (yorumda sürüm). `.github/dependabot.yml` haftalık
  (Pazartesi 06:00 TR) actions, Docker (kök + `deploy/caddy`), compose (PostgreSQL), npm ve NuGet
  güncellemesi önerir; her PR Validate'ten geçer, hiçbiri otomatik birleşmez. PostgreSQL digest'i
  değişirse `ci.yml` servis imajı ve `deploy/backup/restore-drill.sh` elle eşitlenmelidir.

## Anahtarı yenileme / kurulum

```bash
ssh-keygen -t ed25519 -N "" -C github-actions-ravencia-deploy -f deploy_key
# sunucuda, ubuntu olarak repo kökünden (deploy kullanıcısını, sudoers kuralını ve AllowUsers'ı da kurar;
# /home/deploy/.ssh/authorized_keys'i bu tek anahtarla yeniden yazar):
bash deploy/scripts/install-ci-deploy.sh "$(cat deploy_key.pub)"
```

`DEPLOY_SSH_KEY` secret'ını güncelleyin (`DEPLOY_USER` = `deploy`). Anahtar `ubuntu`'nun `authorized_keys`'inde
durmamalıdır. Argümansız `install-ci-deploy.sh` yalnız `/usr/local/bin/ravencia-ci-deploy` kopyasını yeniler.

## Veritabanı bağlantısı ve PostgreSQL

- `app_db_connection.txt`: `...;Maximum Pool Size=25;Timeout=15;Command Timeout=120`.
  API ve Worker aynı dosyayı okur; dosyadaki 25 **API'nin** havuzudur. Worker 15 şerit yuvası çalıştırır ve
  `2 × yuva + 10 = 40` bağlantıya ihtiyaç duyar (`WorkerLanes.RequiredPoolSize`); bu yüzden Worker'ın
  `appsettings.json`'undaki `Database:MaxPoolSize=45` yalnız Worker sürecinde dosyadaki değerin yerine geçer
  (ortam değişkeni `Database__MaxPoolSize` ile değiştirilebilir). Paylaşılan hız sınırlayıcının ayrı
  `NpgsqlDataSource`'u kodda 5 bağlantıyla sınırlıdır. Toplam ≈ 25 + 5 + 45 + 5 + LISTEN ≈ 82;
  `max_connections=150` altında migrate/yedek/psql için pay kalır. Gizli dosyada değişiklik gerekmez;
  mevcut sunucular yeni değerleri bir sonraki deploy'la alır. Worker açılışta havuzu loglar
  (`database pool of 45 (needs at least 40)`), küçükse uyarır. Yeni kurulumda `initialize-deployment.sh`
  dosyayı yazar; **havuz sınırı hiç olmayan eski sunucuda elle eklenir** (bkz. İşletim). Eksikse deploy
  betikleri uyarı basar.
- PostgreSQL (`compose.yaml`, 16 GB sunucuya göre): `max_connections=150`, `shared_buffers=1536MB`,
  `effective_cache_size=8GB`, `work_mem=16MB`, `maintenance_work_mem=512MB`, `log_min_duration_statement=2000`
  (2 sn üstü sorgular loglanır), `shm_size: 1g`; konteyner sınırı 6 GB / 3 CPU. API, Worker ve migrate her biri
  1,5 GB / 2 CPU. Bu ayar değişikliği ilk deploy'da postgres konteynerini yeniden yaratır
  (birkaç saniyelik bağlantı kesintisi; API/Worker yeniden bağlanır).

## Loglar (journald)

Üretimde postgres, migrate, api, worker ve caddy `journald` sürücüsüyle loglar
(`compose.production.yaml`, `!override`); konteyner her deploy'da yeniden yaratılsa da loglar kalır.
Yerel pilot (`compose.yaml` tek başına) `json-file` kullanmaya devam eder.

```bash
journalctl CONTAINER_NAME=marketplacehub-api-1 --since -1h
journalctl CONTAINER_NAME=marketplacehub-worker-1 -f
journalctl CONTAINER_NAME=marketplacehub-caddy-1 --since today -o cat | jq .   # Caddy JSON erişim logu
docker logs marketplacehub-api-1        # journald sürücüsüyle de çalışır
```

Saklama host'un journald sınırlarına bağlıdır; önerilen `/etc/systemd/journald.conf.d/ravencia.conf`:

```ini
[Journal]
SystemMaxUse=2G
MaxRetentionSec=30day
```

(`sudo systemctl restart systemd-journald`.)

## Kenar (Caddy)

- `/health/live` herkese açık (yalnız süreç canlılığı; dış uptime izleyicisi ve Deploy doğrulaması).
- `/health/ready`, `/health/queues` yalnızca özel kaynak adreslerine (`remote_ip private_ranges`) yanıt
  verir; dışarıya `404`. Sunucu üstündeki betikler loopback'ten girer:
  `curl --resolve panel.ravencia.com:443:127.0.0.1 https://panel.ravencia.com/health/ready`.
- `Clear-Site-Data: "cache"` yalnızca `/api/v1/auth/*` 401 yanıtlarına eklenir.
- `/api/*` ve `/hubs/*` yanıtları sıkıştırılmaz (BREACH sınıfı); statik dosyalar ve SPA kabuğu
  zstd/gzip ile sıkıştırılmaya devam eder. `/assets/*` altındaki betik ve stil dosyalarının brotli (`.br`) ve
  zstd (`.zst`) kopyaları derlemede en yüksek seviyede üretilir (`scripts/precompress-assets.mjs`, `npm run build`
  ve `build:assets` içinde) ve Caddy `file_server { precompressed br zstd }` ile onları olduğu gibi gönderir (ana CSS
  89 KB zstd yerine ~57 KB brotli); ikisini de kabul etmeyen istemci `encode` ile sıkıştırılır.
  İstisna (9 Kasım 2026, hız planı P4; 10 Kasım genişletildi): `GET /api/v1/products`,
  `/api/v1/products/{id}`, `/api/v1/catalog/categories/{id}/attribute-requirements`, `/api/v1/catalog/attributes`,
  pazaryeri referans listeleri (`/api/v1/reference-data/categories…`, `…/attribute-values`,
  `/api/v1/mappings/attribute-values/batch`), raporlar (`/api/v1/reports/sales`, `order-profitability`, `stock`,
  `payouts`, `payouts/lines`, `ad-report`, `price-assumptions`), `/api/v1/jobs/page`, `/api/v1/jobs/counts`,
  `/api/v1/dashboard/performance` ve komisyon tarifesi satırları (`/api/v1/connections/{id}/commission-tariffs/{id}/rows`)
  yalnız panelin kendi isteklerinde (`Sec-Fetch-Site: same-origin`) sıkıştırılır. Gövdeler tek tek incelendi: jeton,
  oturum değeri ya da taşıyıcı bağlantı yok (görseller yalnız oturumla açılan `/files/` bağlantıları ya da pazaryeri
  CDN adresleri), arama terimi geri yansıtılmaz (imleç şifreli); başka sitenin başlattığı istek sıkıştırılmadığından
  saldırgan BREACH'in gerektirdiği istekleri yaptıramaz. Siparişler/faturalar ve fatura çalışma alanı (taşıyıcı fatura
  bağlantıları), `/public/*` (erişim jetonları), `/auth/*`, `/hubs/*` ve diğer tüm uçlar sıkıştırmasız kalır. Gerekçe
  Caddyfile.production yorumunda.
- HTTP/3 0-RTT kapalı (`servers { 0rtt off }`): `remote_ip` eşleyicisi TLS el sıkışması bitmemiş (early data)
  isteğe `425 Too Early` döndürüyordu (günde ~300, tarayıcı yeniden gönderiyordu).
- Caddyfile her imaj derlemesinde `caddy adapt` ile doğrulanır (CI `image-edge` işi dahil).
- **IPv6 ziyaretçiler (G3):** `edge` ağı yalnız IPv4 olduğundan, sunucuya IPv6 ile gelen bağlantıyı
  Docker'ın `docker-proxy` süreci (userland-proxy) karşılar ve Caddy'ye köprü ağ geçidinin özel IPv4
  adresinden iletir. Bu durumda tüm IPv6 ziyaretçiler tek bir özel adres gibi görünür: hız sınırı ve
  giriş engelleri (`ClientNetwork`) hepsine ortak işler, `remote_ip private_ranges` ise `/health/ready`
  ve `/health/queues`'u onlara açar. Gerçek adresin Caddy'ye ulaşması için sunucuda `edge` ağında IPv6
  açılmalıdır (`daemon.json`: `"ipv6": true, "ip6tables": true` ve ağa `enable_ipv6`). Bunun yerine
  `"userland-proxy": false` seçilirse vekil devreden çıkar ama IPv6 bağlantılar hiç iletilmez; o zaman AAAA
  kaydı kaldırılmalıdır. Canlıda bir IPv6 istemciden istek atıp Caddy erişim
  kaydındaki `remote_ip` değerinin istemcinin adresi olduğu doğrulanmalıdır. Sunucunun AAAA kaydı yoksa
  bu durum oluşmaz. Bkz. `deploy/compose/compose.yaml` içindeki `caddy` notu.
  **Canlıda 3 Kasım 2026'dan beri `"userland-proxy": false`** (AAAA kaydı yok, ufw IPv6'da yalnız 22'yi açıyor):
  80/443 IPv6'da hiç dinlenmez; IPv4 istemcilerin adresi Caddy'de olduğu gibi görünür, sunucudaki loopback
  kontrolleri (`--resolve …:127.0.0.1`) köprü ağ geçidi adresiyle gelir ve `/health/ready` onlara 200 döner.
- `/.well-known/security.txt` (RFC 9116): `Contact: mailto:destek@ravencia.com`, `Preferred-Languages: tr, en`,
  `Canonical`, `Expires` bir yıl ileri. Süre dolmadan 30 gün önce CI `ops` işi kırmızıya döner; o zaman
  `Caddyfile.production`'daki `Expires` satırı bir yıl ileri alınır.

## Konteyner sertleştirme (güvenlik planı U3)

| Servis | Kullanıcı | Yetenekler | Kök dosya sistemi | `pids_limit` |
| --- | --- | --- | --- | --- |
| `api`, `worker`, `migrate` | imajın `APP_UID` 1654'ü | hiçbiri (`cap_drop: ALL`) | salt okunur; `/tmp` tmpfs 256 MB, iki veri volume'ü yazılabilir | 1024 |
| `caddy` | `10080:10080` (root değil) | sınır kümesinde yalnız `NET_BIND_SERVICE`; etkin küme boş | salt okunur; `/tmp` tmpfs, `/data` ve `/config` volume | 256 |
| `caddy-volumes` (tek seferlik) | root | `CHOWN`, `DAC_OVERRIDE`, `DAC_READ_SEARCH`, `FOWNER` | — (ağ yok) | 32 |
| `postgres` | entrypoint root, sonra `postgres` (gosu) | `CHOWN`, `DAC_OVERRIDE`, `FOWNER`, `SETGID`, `SETUID` | yazılabilir (soket ve kilit dosyaları) | 512 |
| `backup` (tek seferlik) | root | `DAC_READ_SEARCH` (1654'e ait özel dosyaları okumak için) | yazılabilir | 128 |

Hepsinde `no-new-privileges:true`. Notlar:

- **Caddy root olmadan 80/443'ü** `net.ipv4.ip_unprivileged_port_start=0` ile açar (konteynerin kendi ağ
  ad alanında; Docker 20.10'dan beri varsayılan, burada açıkça yazılı). `cap_add: NET_BIND_SERVICE` yine de
  durur: imajdaki `/usr/bin/caddy` dosyası `cap_net_bind_service+ep` dosya yeteneği taşır ve bu yetenek sınır
  kümesinde yoksa çekirdek dosyayı hiç çalıştırmaz (`exec caddy failed: Operation not permitted`).
  `no-new-privileges` ve root olmayan kullanıcı yüzünden süreç yeteneği almaz (`CapEff` 0).
- **`caddy-volumes`**, her Caddy başlangıcından önce `/data` ve `/config`'i 10080'e devreder. Caddy root
  çalışırken yazılan sertifikalar root'a ait ve 0600'dür; bu adım olmadan Caddy onları okuyamaz, yeni
  sertifika da yazamaz. İlk deploy'dan sonra bir şey değiştirmez. `rollback.sh` `--no-deps` kullanır;
  devir zaten yapılmış olduğundan etkilenmez.
- Bu servislerden birine yeni bir yazma yolu gerekirse (`/tmp` dışında) önce volume ya da tmpfs eklenir;
  aksi halde süreç `Read-only file system` hatası alır.
- CI `ops` işi birleştirilmiş Compose'da bu ayarların durduğunu denetler.

## İmaj güvenlik taraması (U4)

- Validate: `image-app` çalışma imajını (`docker build .`), `image-edge` kenar imajını derler ve
  Trivy ile tarar (`aquasecurity/trivy-action`, SHA sabitli; Trivy v0.70.0). **Yaması yayımlanmış
  CRITICAL** bir açık (taban imaj paketi ya da NuGet) Validate'i durdurur: digest'i ya da paketi güncelleyin.
  Yaması olmayanlar (`--ignore-unfixed`) durdurmaz.
- `Image Scan` iş akışı (`.github/workflows/image-scan.yml`) her Pazartesi 07:00 TR'de `main`'den aynı iki
  imajı derleyip tarar; bulgu varsa çalışma kırmızı olur ve "Image scan: fixable CRITICAL vulnerabilities
  in the … image" başlıklı bir issue açılır (açıksa yorum eklenir). Elle: Actions → Image Scan → Run workflow.

## Panel MFA zorunluluğu (H2)

`Security__RequireMfaForElevatedRoles` açıkken doğrulama uygulaması kurulu olmayan OWNER/ADMINISTRATOR
hesabı giriş yapabilir, okuyabilir ve MFA kurabilir (`/api/v1/auth/*` açık) ama başka hiçbir değiştirme
isteği (POST/PUT/PATCH/DELETE `/api/v1/*`) yapamaz: `403 MFA_ENROLLMENT_REQUIRED`. Webhook'lar
(`/api/v1/hooks/*`) ve diğer roller etkilenmez. MFA durumu her istekte oturum sorgusundan okunur; TOTP
kurulunca kısıt hemen kalkar. Bugün **kapalı** (`compose.production.yaml`, varsayılan `false`).

Açmak (yalnız sahip TOTP'yi kurduktan sonra):

1. Panelde sahip hesabıyla Güvenlik → iki adımlı doğrulamayı kurun, kurtarma kodlarını saklayın. Durum:
   `select "UserId", "TotpState" from iam.user_security` → sahip için `ENABLED`.
2. Diğer OWNER/ADMINISTRATOR hesapları da kurmuş olmalı (kurmayan değişiklik yapamaz).
3. Sunucuda: `echo 'MARKETPLACEHUB_REQUIRE_MFA_FOR_ELEVATED_ROLES=true' | sudo tee -a deploy/secrets/production.env`
4. Yeniden deploy (Actions → Deploy → Run workflow, `redeploy`) ya da bir sonraki deploy.
5. Doğrulama: `sudo docker exec marketplacehub-api-1 printenv Security__RequireMfaForElevatedRoles` → `true`;
   panelde bir ayar kaydı çalışır (sahipte MFA açık).
6. Kapatmak: satırı `false` yapıp yeniden deploy. Sahip doğrulama cihazını kaybederse:
   `identity reset-mfa` (RUNBOOK-OLAY.md 2.2).

## Sunucu dışı yedek (Y3, Y4)

**Panelden yönetilir (13 Kasım 2026'dan beri):** Sistem Ayarları › Veri › **Google Drive Yedeği** (yalnız sahip ve
yönetici). Sunucuda kabuk adımı, `deploy/secrets` dosyası ya da ek program gerekmez. Worker en yeni **doğrulanmış**
yedek setini (`SHA256SUMS` tutan, sağlama toplamları tutan) sahibin **age açık anahtarıyla** şifreleyip sahibin
Google Drive'ına `<set>.tar.age` olarak yükler. Sunucu şifreleyebilir ama çözemez: özel anahtar sunucuda hiç yoktur.

Nasıl çalışır:

- **Zaman:** her gün 09:30 ve 21:30 UTC (12:30 / 00:30 TR), 08:59 / 20:59 yedeğinden yarım saat sonra; panelde
  "Şimdi Gönder" bir dakika içinde başlatır. Aynı set ikinci kez yüklenmez. Kaçan bir zaman worker açılınca yapılır.
- **Akış:** `OffsiteBackupCopier` (worker) seti yedek biriminden salt okunur okur (`backup_staging` →
  `/var/lib/marketplacehub/backups:ro`), dosyaları açıp sağlama toplamlarını doğrular, aynı açık dosyalardan tar →
  `age` (uygulama imajındaki dağıtım paketi, BSD-3) → Drive API'nin sürdürülebilir (resumable) yüklemesiyle akıtır;
  diske düz ya da şifreli kopya yazılmaz. Doğrulanmamış set asla yüklenmez.
- **Okuma izni:** `backup.sh` her yeni seti uygulamanın grubuna verir (`BACKUP_READER_GID=1654`, yedek konteyneri
  `group_add: 1654`; birim kökü ve set 0750, dosyalar 0640). 13 Kasım'dan önceki setler root'a aittir; worker onları
  okuyamaz ve atlar, ilk deploy'un yeni seti okunur.
- **Google bağlantısı:** sahibin kendi Google Cloud OAuth istemcisi (Web uygulaması), kapsamlar `openid email
  https://www.googleapis.com/auth/drive.file`. "Google ile Bağlan": yetki kodu + PKCE (S256) + tek kullanımlık,
  10 dakikalık, kullanıcıya bağlı state; `access_type=offline`, `prompt=consent`. Google kişiyi panel sayfası
  `https://panel.ravencia.com/settings/google-drive-callback`'e döndürür; sayfa kodu oturum ve CSRF ile
  `POST /api/v1/settings/offsite-backup/google/complete`'e iletir (API'de kimliksiz geri dönüş ucu yoktur).
- **Sırlar nerede:** `iam.tenant_settings` "offsite-backup" (age açık anahtarı, klasör, saklama, istemci kimliği, şifreli
  istemci gizli anahtarı) ve "offsite-backup-state" (şifreli refresh token, son gönderimler). Şifreleme uygulamanın Data
  Protection anahtarlarıyla (`dp_keys`), kiracıya bağlı (`BoundSecret`); API yanıtında gizli anahtar ve token hiç yoktur.
  "Bağlantıyı Kes" token'ı Google'da iptal eder ve siler. Paneli sıfırlama bu ayarları tutar (Sistem ayarları).
- **Klasör:** varsayılan "Uygulama Klasörü" (`Ravencia Yedek`): uygulama Drive kökünde klasörü bulur ya da oluşturur ve
  ID'sini saklar. "Klasör ID'si" yalnız uygulamanın oluşturduğu bir klasörde çalışır: `drive.file` başka uygulamanın ya
  da elle açılan klasörü göremez.
- **Saklama:** son N gün (varsayılan 14) her kopya + son M ayın (varsayılan 12) her birinin ilk kopyası + her zaman en
  yeni kopya; yalnız uygulamanın yüklediği `YYYYMMDDTHHMMSSZ.tar.age` dosyaları kalıcı silinir (çöp kutusuna değil;
  Drive kotası şişmesin), başka dosyaya dokunulmaz. Silme yalnız o çalışmanın kopyası Drive'a çıktıktan sonra yapılır.
- **Hata:** geçici hatada (ağ, Drive 5xx, kota) artan bekleme ile tekrar (15 dk, 30 dk, 1 sa, 2 sa, sonra 4 sa; sıradaki
  zaman hiç atlanmaz). Google yetkisi geçersizse (`invalid_grant`, iptal, süre dolması, istemci silinmesi) gönderim
  durur, panel "Yeniden Bağlanın" der, bildirim merkezine arka plan uyarısı düşer ve sahibe **tek** alarm e-postası
  gider (Alarm Bildirimleri hesabıyla); 3 art arda başarısız denemede de tek e-posta. İlk başarılı gönderimde
  "düzeldi" bildirimi ve e-postası gelir.
- **OAuth izin ekranı "Üretimde" olmalı:** "Testing" durumundaki External uygulamada Google refresh token'ı 7 günde
  düşürür; kapsamlar hassas olmadığından yayınlamak Google doğrulaması istemez.

**Sahibin bir kerelik adımları:** Google Cloud Console'da proje → Google Drive API'yi etkinleştir → OAuth izin ekranı
(External; kapsamlar openid, email, `…/auth/drive.file`; kendini test kullanıcısı ekle; "Uygulamayı Yayınla") →
Kimlik Bilgileri › OAuth istemci kimliği (Web uygulaması), yönlendirme URI'si yukarıdaki adres → panelde istemci kimliği
ve gizli anahtarı kaydet → "Şifreleme Anahtarı Oluştur" (anahtar çifti tarayıcıda üretilir, özel anahtar yalnız
`ravencia-yedek-anahtari.txt` olarak iner; parola yöneticisi + çevrimdışı kopya) ya da var olan `age1…` açık anahtarını
yapıştır → Otomatik Gönderim açık, Kaydet (parola) → "Google ile Bağlan" → "Bağlantıyı Test Et" → "Şimdi Gönder".

**Geri açma (ek program gerekmez):** Drive'dan `<set>.tar.age` indir → panelde "Yedeği Aç": kopya ve anahtar dosyası
seçilir, tarayıcı çözüp `<set>.tar` olarak kaydeder (sunucuya bir şey gitmez). Komut satırıyla da olur:

```bash
age -d -i ravencia-yedek-anahtari.txt <set>.tar.age | tar -x     # <set>/database.dump, private-volumes.tar.gz
cd <set> && sha256sum -c SHA256SUMS                              # yollar /backup/<set>/… ise: yolları dosya adına indirin
```

Sonra RUNBOOK-GERI-ALMA.md bölüm 3. `deploy/secrets/` emaneti (Y4) aynı anahtarla şifrelenir: RUNBOOK-GERI-ALMA.md bölüm 5.

**Eski sunucu yolu (yedek, ikincil):** `deploy/backup/offsite-copy.sh` ve `ravencia-offsite-copy.timer` (09:30 / 21:30
UTC) hâlâ çalışır (geçici nesneye yükler, uzaktan geri okunan SHA-256 tutarsa son adına taşır ve `<set>.tar.age.verified`
kaydı yazar), ama yalnız `deploy/secrets/backup_age_recipient.txt` ve `backup_offsite_remote.txt` (ve `rclone.conf`)
varsa kurulur; yoksa hiçbir şey yapmaz (13 Kasım 2026: canlıda kurulu değil). **Öncelik panelde:** ikisini birlikte
kullanmayın; panel klasörde aynı adlı kopya varsa yeniden yüklemez, sunucu betiği ise yalnız kendi `.verified` kaydı
olan kopyayı sayar ve panelin kopyasını yeniden
yükler. Sunucu betiği kendi geçici nesnesi dışında silmez (saklamayı panel yapar). Panel çalıştıktan sonra bu dosyalar silinmeli
(o zaman zamanlayıcı bir sonraki deploy'da kurulmaz). Sınır: `drive.file` token'ı uygulamanın yüklediği kopyaları
silebilir; sunucuyu ele geçiren bu kopyaları da silebilir. Silinemez hedef gerekirse ileride Backblaze B2 Object Lock.

## İşletim: zamanlayıcılar, alarm, izleme

`install-backup-timer.sh` (her deploy'da çalışır) şu birimleri kurar ve zamanlayıcıları etkinleştirir:

| Birim | Ne zaman | Ne yapar |
| --- | --- | --- |
| `ravencia-backup.timer` | 08:59 ve 20:59 UTC (11:59 ve 23:59 TR), `Persistent=true` | `daily-backup.sh`: api/caddy konteynerlerini 5 dk'ya kadar bekler, taze yedek + doğrulama |
| `ravencia-restore-drill.timer` | ayın ilk Pazar'ı 19:00 UTC (22:00 TR) | `monthly-restore-drill.sh`: en yeni seti izole konteynerlere geri yükler, migration/API/Worker sağlığını doğrular |
| `ravencia-healthwatch.timer` | 5 dakikada bir, günün her saati | `healthwatch.sh`: `/health/ready`, `/health/queues`, Worker sağlığı; sağlıksız API'yi yeniden başlatır |
| `ravencia-maintenance.timer` | ayın ilk Pazar'ı 01:00 UTC (04:00 TR), kaçan pencere sonradan yapılmaz | `monthly-maintenance.sh` (root'a ait kopyadan, `/usr/local/lib/ravencia`): deploy kilidini bekler ve tutar, `apt-get full-upgrade`; yalnız `/var/run/reboot-required` varsa yeniden başlatır |
| `ravencia-alert@.service` | `OnFailure=` (yedek, tatbikat, bakım) ve healthwatch | `send-alert.sh` ile tek satırlık bildirim |
| `ravencia-deploy-guard.service` | açılışta etkin, iş kapanışta | `wait-for-deploy-lock.sh`: süren deploy'un kilidini en çok 23 dk bekler |

Yedek, tatbikat, healthwatch, bakım ve alarm birimleri 3 Kasım 2026'dan beri `ubuntu` değil **root** olarak
çalışır (`sudo` kullanmazlar; `NoNewPrivileges`, `ProtectSystem=full`, `ProtectHome=read-only`, çekirdek
ayarlarına/modüllerine erişim kapalı vb.). Betikler root iken `sudo_cmd` boş kalır, elle (ubuntu) çalıştırılınca
`sudo -n` kullanır. Tatbikat biriminde `PrivateTmp` yoktur: `restore-drill.sh` geçici dizindeki dosyaları
konteynerlere bağlar. Yalnız deploy-guard `ubuntu` olarak kalır.
Root birimleri repodaki dosyaları değil, **root'a ait kopyayı** çalıştırır: `install-backup-timer.sh` her deploy'da
`deploy/{scripts,backup,compose}`'u `/usr/local/lib/ravencia/deploy/` altına bütün olarak yeniden kopyalar (root,
yazma yalnız root); `deploy/secrets` orada repodaki root'a ait gizli klasöre bir bağlantıdır. Böylece `ubuntu`
deploy'lar arasında repoyu değiştirse bile root'un çalıştırdığı kod değişmez. Kalan açık: deploy zincirinin kendisi
hâlâ `ubuntu`'nun şifresiz `sudo`'suyla çalışır (A4 için önce bu zincir taşınmalı).

**Aylık bakım (`/etc/default/ravencia-maintenance`):** `RAVENCIA_MAINTENANCE_DOCKER=hold` (varsayılan) Docker
Engine/containerd'yi apt hold'da tutar; bir sonraki pencerede güncellenmeleri için `allow` yazılır (daemon
`live-restore` ile çalıştığından konteynerler yeniden başlamaz; pencereden sonra yine `hold`'a çekilmesi önerilir).
`docker-compose-plugin` her durumda hold'da kalır (deploy davranışı ona bağlı). `RAVENCIA_MAINTENANCE_REBOOT=never`
yeniden başlatmayı kapatır. Elle deneme: `sudo systemctl start ravencia-maintenance.service`.

**Docker daemon (`deploy/docker/daemon.json` → `/etc/docker/daemon.json`, elle kurulur):** `live-restore`,
varsayılan `no-new-privileges`, `userland-proxy: false` (IPv6'da 80/443 artık dinlenmez; IPv4 istemci adresi
Caddy'ye olduğu gibi ulaşır, loopback'ten gelen sağlık kontrolü köprü ağ geçidi adresiyle özel aralıkta kalır),
varsayılan log sürücüsü journald (`tag {{.Name}}`, compose ile aynı).

## CrowdSec (güvenlik planı B1–B8)

`deploy/scripts/install-crowdsec.sh` (ubuntu olarak, bir kez; tekrar çalıştırılabilir): CrowdSec'in kendi apt
deposu (`noble` paketi), journald kaynakları (`deploy/crowdsec/acquis-ravencia.yaml`: sshd ve Caddy JSON erişim
logu), koleksiyonlar `linux`, `sshd`, `caddy`, `http-cve`, `base-http-scenarios` ve `whitelists` ayrıştırıcısı,
nftables firewall bouncer (IPv4 + IPv6, `input` ve `forward` kancaları, öncelik -10: Docker'a giden 80/443
trafiği `forward`'dan geçer, iptables'taki `DOCKER-USER` karşılığı). İzin listeleri (`cscli allowlists`):
`github-actions` (api.github.com/meta `actions`, `ravencia-crowdsec-allowlist.timer` ile her gün) ve
`ravencia-private`. Kontrol: `sudo cscli metrics show acquisition parsers`, `sudo cscli alerts list`.

İlk kurulum **simülasyon modunda** başlar (3 Kasım 2026): uyarılar kaydedilir, hiçbir IP banlanmaz ve topluluk
blok listesi çekilmez (`/etc/crowdsec/config.yaml.local`). fail2ban bu süre boyunca çalışmaya devam eder.
12 Kasım 2026 itibarıyla canlı hâlâ simülasyonda (`/prod-logs host`: "global simulation: enabled").
**Panelin kendi istekleri (`deploy/crowdsec/ravencia-panel-whitelist.yaml`)**: sahibinin sabit IP'si yok (ev ve
mobil hat, dinamik), bu yüzden IP izin listesi kullanılmaz; ayrıştırıcı beyaz listesi
(`/etc/crowdsec/parsers/s02-enrich/ravencia-panel-whitelist.yaml`) isteği **şekline göre** senaryolardan düşürür.
12 Kasım 2026 18:20Z simülasyonda sahibin tarayıcısı (TurkNet) `crowdsecurity/http-probing` "(simul)ban" aldı: açık
bir ürün sayfası silinmiş bir ürünü yoklamaya devam etti (`GET /api/v1/products/{id}` 404) ve resimleri
(`GET /api/v1/files/product-media/{id}/content` 404); 10 sn sızıntılı kovada 10'dan fazla farklı 404 yolu ban
demektir. Beyaz liste kapsamı: panel API önekleri altındaki `GET`/`HEAD` okumaları (segmentler yalnız harf, rakam,
`-`, `_`), `/hubs/operations` (SignalR), `/assets`, `/fonts`, `/platforms`, `/pack` altındaki derlenmiş dosyalar
(deploy sonrası eski chunk 404'leri), simgeler/manifest/`robots.txt` ve SPA sayfaları. Görünür kalan: `.env`, `.git`,
`*.php`, `/wp-*`, `/phpmyadmin`, bilinmeyen `/api/*` önekleri, `/api/v1/hooks` ve `/api/v1/public`, segmentinde
nokta, `%`, tırnak geçen her yol ve API'ye her `POST`/`PUT`/`DELETE` (giriş denemeleri dahil). Caddy erişim logu
sorgu dizesini `?redacted` yaptığından sorgu ile kural atlatılamaz. Çevrimdışı kontrol:
`bash tests/ops/test-crowdsec-whitelist.sh` (CI `ops` işi de çalıştırır).

Kurmak/güncellemek (sahibin adımı, simülasyonda da `--enforce`'ta da; deploy bunu yapmaz), sunucuda repo kökünde
`git pull` sonrası:

```bash
bash deploy/scripts/install-crowdsec.sh            # simülasyon sürerken (yürürlükteyse aynı komut, işaret korunur)
# ya da yalnız beyaz listeyi koymak için:
sudo install -m 0644 deploy/crowdsec/ravencia-panel-whitelist.yaml /etc/crowdsec/parsers/s02-enrich/ravencia-panel-whitelist.yaml
sudo systemctl reload crowdsec
```

Doğrulamak:

```bash
sudo cscli parsers list | grep ravencia/panel-whitelist          # "local" olarak görünür
# Panel isteği: çıktıda "ravencia/panel-whitelist" satırı ve "whitelisted" beklenir
sudo cscli explain --type caddy -v --log '{"level":"info","ts":1794506610.3,"logger":"http.log.access.log0","msg":"handled request","request":{"remote_ip":"203.0.113.7","client_ip":"203.0.113.7","proto":"HTTP/2.0","method":"GET","host":"panel.ravencia.com","uri":"/api/v1/products/01a1cb3e-b2c9-7ff0-a46d-064b479b831c","headers":{"User-Agent":["Mozilla/5.0"]}},"status":404}'
# Tarayıcı isteği: beyaz listeden geçmez, http-probing / http-sensitive-files kovalarına düşer
sudo cscli explain --type caddy -v --log '{"level":"info","ts":1794506610.3,"logger":"http.log.access.log0","msg":"handled request","request":{"remote_ip":"203.0.113.7","client_ip":"203.0.113.7","proto":"HTTP/2.0","method":"GET","host":"panel.ravencia.com","uri":"/.env","headers":{"User-Agent":["Mozilla/5.0"]}},"status":404}'
sudo cscli metrics show acquisition whitelists     # Caddy satırında "Lines whitelisted" artar
sudo cscli alerts list                             # birkaç gün içinde kendi IP'nizle http-* uyarısı kalmamalı
```

**Banı açmak (sahibin adımı)**: beyaz liste kurulduktan sonra en az 2–3 gün normal kullanımda
`sudo cscli alerts list` içinde sahibin IP'siyle (TurkNet/mobil) yeni `http-*` uyarısı çıkmadığını görün; çıkarsa
`sudo cscli alerts inspect <ID> -d` ile tetikleyen yolları bulun ve beyaz listeye **yol kuralı** ekleyin (IP değil).
Sonra tek komut:

```bash
bash deploy/scripts/install-crowdsec.sh --enforce   # simülasyonu kapatır, topluluk blok listesini açar, işaret bırakır
sudo cscli simulation status                        # "global simulation: disabled" beklenir
sudo cscli decisions list                           # bundan sonra banlar nftables'a gider
sudo systemctl disable --now fail2ban               # CrowdSec banları doğrulandıktan sonra
```

Kendinizi yine de banlarsanız (ör. mobil hattan): başka bir bağlantıdan (ya da Hostinger VNC konsolundan)
`sudo cscli decisions list` ile kararı bulun, `sudo cscli decisions delete --ip <IP>`; yerel senaryo banı 4 saattir.
Beyaz listeyi kaldırmak: `sudo rm /etc/crowdsec/parsers/s02-enrich/ravencia-panel-whitelist.yaml` ve
`sudo systemctl reload crowdsec`.

`--enforce` `/etc/crowdsec/ravencia-enforced` işaretini bırakır; betik sonra `--enforce`'suz çalışsa da blok listesi
ayarını geri almaz. Deploy bu betiği çalıştırmaz. Geri dönmek: `sudo cscli simulation enable --global`,
`sudo rm /etc/crowdsec/ravencia-enforced` ve betiği `--enforce`'suz yeniden çalıştırın.

Yanlış ban: `sudo cscli decisions delete --ip <IP>` (gerekirse VNC konsolundan).

**Yedek saklama** (`deploy/backup/backup.sh`): son 72 saatin en yeni **5** seti + son **7 günün** her yarım
gününün en yeni seti + daha eski 4 haftanın her birinin ve 6 ayın her birinin en yeni seti; en yeni set her
koşulda kalır (`BACKUP_KEEP_RECENT`, `BACKUP_KEEP_DAILY`, `BACKUP_KEEP_WEEKLY`, `BACKUP_KEEP_MONTHLY`). Bir set ≈0,3–0,5 GB
(12 Kasım 2026); olağan durumda ~25 set ≈ 10 GB (Hostinger, 193 GB kök disk).

**Disk koruması**: yeni set yazılmadan önce, en yeni setin boyutu kadar ekleme sonrası disk doluluğu
hesaplanır. %85'i (`BACKUP_DISK_LIMIT_PERCENT`) geçecekse önce **en eski haftalık setler** silinir
(günlük ve son 72 saatlik setlere dokunulmaz). Hâlâ %85 üstündeyse yedek yine alınır ama
`DISK_GUARD_WARNING` satırı basılır ve deploy/yedek betiği `send-alert.sh` ile alarm gönderir.
%95'i (`BACKUP_DISK_HARD_LIMIT_PERCENT`) geçecekse yedek **reddedilir** (dump PostgreSQL'in diskini
doldurmasın): gece yedeği başarısız olur (`OnFailure` alarmı), deploy yedeksiz ilerlemez.
Disk kullanımı: `df -h /` ve `sudo docker run --rm -v marketplacehub_backup_staging:/b alpine du -sh /b/*`.

**Alarm kanalları** (`send-alert.sh`; ikisi de varsa ikisine birden gönderilir, biri başarısızsa çıkış 1):

- **E-posta (güvenlik planı Y1)**: ayarlar panelde, **Ayarlar → Güvenlik ve Oturumlar → Alarm Bildirimleri**
  (yalnız sahip ve yönetici; gönderen Gmail, 1–5 alıcı, uygulama şifresi, "Test E-postası Gönder"). Tasarım:
  - Panel doğruluk kaynağıdır: kiracı başına `tenant_settings` `alert-email` satırı; uygulama şifresi Data Protection
    ile şifreli (`MarketplaceHub.AlertEmail.v1`), tarayıcıya hiç dönmez (alan "Kayıtlı" + "Değiştir"), her kayıt ve
    test `audit_logs`'a yazılır (`ALERT_EMAIL_*`). Sunucu tek şirkete hizmet ettiği için host, en eski etkin kiracının
    (sahibin) ayarını kullanır.
  - Test düğmesi API'den Gmail'e `smtp.gmail.com:465` (örtük TLS) ile, host alarmlarıyla aynı sunucu, hesap ve ileti
    biçimiyle gönderir.
  - Host, API kapalıyken de gönderebilmek için kendi kopyasını tutar: `send-alert.sh` her alarmda
    `alert-settings-sync.sh --print` ile önce paneli sorar (`GET /internal/alert-settings`, loopback üzerinden;
    Caddy ve API bunu yalnız sunucunun kendi ağlarına ve `deploy/secrets/alert_sync_token.txt`'teki belirteçle
    açar), cevap gelirse `/var/lib/ravencia-alerts/alert_smtp.txt` önbelleğini yeniler; gelmezse önbelleği, o da yoksa
    `deploy/secrets/alert_smtp.txt`'i kullanır. Her deploy de eşitlemeyi çalıştırır ve secrets dosyasını panelle
    aynı yapar. Panel boşsa ve secrets dosyası varsa (ör. `sudo ravencia-alarm-eposta` ile yazılmışsa) ilk eşitleme onu
    panele aktarır (`ALERT_EMAIL_SETTINGS_IMPORTED`); panel dolduktan sonra dosyayı elle değiştirmek kalıcı olmaz.
  - Belirteç (`alert_sync_token.txt`, root:1654 0640) yoksa `lib.sh` (`ensure_alert_sync_token`) ilk deploy'da üretir;
    API onu `Alerts__HostSyncToken_FILE` Compose secret'ıyla okur.

  Host dosyası `deploy/secrets/alert_smtp.txt` (root:root 600), üç satır: gönderen Gmail adresi, alıcı adresleri
  (virgülle), 16 karakterlik Gmail uygulama şifresi (boşluksuz). Gönderen bir **@gmail.com** adresi
  olmalı (`@ravencia.com` adına gönderim SPF/DMARC yüzünden spama düşer). `curl --ssl-reqd` ile
  `smtps://smtp.gmail.com:465`'e RFC 5322 ileti (From, To, `[Ravencia] <ilk 80 karakter>` konusu, Date, UTF-8
  gövde) gider. Şifre hiçbir zaman yazdırılmaz ve komut satırına girmez: curl'e 0600 geçici bir config dosyasıyla
  verilir (`ps`'te görünmez). Test: `tests/ops/test-send-alert.sh` (sahte curl).
- **Sohbet webhook'u**: `deploy/secrets/alert_webhook_url.txt` (tek satır, root:root 600):
  Telegram `https://api.telegram.org/bot<TOKEN>/sendMessage?chat_id=<CHAT_ID>` ya da Slack (Slack uyumlu; Discord
  için webhook URL'sinin sonuna `/slack`) `https://hooks.slack.com/services/...`.

İkisi de yoksa alarm gönderilmez; `send-alert.sh` mesajı stderr'e (journal) yazar ve her deploy
`::warning::No alert channel is configured` uyarısı basar (Actions çalışmasında da görünür). Kanal
kurulmadan healthwatch, yedek ve otomatik geri alma alarmları kimseye ulaşmaz.

Deneme (gerçek birim yoluyla, root'a ait kopyadan): `sudo systemctl start ravencia-alert@deneme.service` ve
`journalctl -u ravencia-alert@deneme.service`; ya da `sudo bash deploy/scripts/send-alert.sh "test"`.

**Healthwatch**: bir sorun iki ardışık kontrolde (≈10 dk) sürerse alarm, sürdükçe saatte bir tekrar,
düzelince "Recovered" mesajı. Docker'ın `unhealthy` dediği API konteyneri iki ardışık kontrolde öyle
kalırsa ve PostgreSQL sağlıklıysa `docker restart` edilir (en çok 30 dakikada bir) ve alarm gönderilir.
Açılıştan sonraki ilk 10 dakika atlanır. Durum: `/var/lib/ravencia-healthwatch/`.
Aynı alarm akışıyla şunlar da denetlenir: kök disk doluluğu ≥ %85 (`RAVENCIA_DISK_ALERT_PERCENT`),
PostgreSQL istemci bağlantıları ≥ `max_connections`'ın %80'i (`RAVENCIA_DB_CONNECTION_ALERT_PERCENT`;
`pg_stat_activity`, postgres konteynerinde `psql`) ve `marketplacehub_backup_staging` içindeki en yeni
yedek setinin yaşı ≥ 30 saat (`RAVENCIA_BACKUP_MAX_AGE_HOURS`; kaçan bir yedek payı dahil) ya da hiç set
olmaması.

> Autoheal sidecar'ı (`willfarrell/autoheal`) bilinçli olarak kullanılmadı: Docker soketini
> (host'ta root eşdeğeri) üçüncü taraf bir imaja açmak gerekirdi; aynı işi sunucudaki healthwatch
> birimi, PostgreSQL kesintisinde gereksiz yeniden başlatma yapmadan görüyor.

**Dış izleme (elle kurulmalı, önerilen, ücretsiz)** — sunucu ya da Docker tamamen çökerse healthwatch da
çalışmaz; bunu yalnız dışarıdan bir izleyici görür: UptimeRobot/Better Stack ile `https://panel.ravencia.com/health/live`
5 dakikada bir, günün her saati. Ready/queues dışarıya kapalı olduğu için
derin kontroller sunucudaki healthwatch'tadır.

**Metrikler**: imaj yalnızca debugger/profiler eklemeyi kapatır
(`DOTNET_EnableDiagnostics_Debugger=0`, `DOTNET_EnableDiagnostics_Profiler=0`); tanılama IPC soketi
açıktır. Örnek: `dotnet-counters` aracını içeren geçici bir konteyneri aynı PID ad alanına bağlayıp
(`--pid container:marketplacehub-api-1 --user 1654`, soket `/proc/1/root/tmp` altında; `/tmp` artık konteynere özel
bir tmpfs olduğu için volume olarak paylaşılamaz) `dotnet-counters monitor -p 1 --counters MarketplaceHub`
(araç imajda yoktur).

## Elle uygulanacaklar (bu değişikliklerden sonra bir kez)

```bash
cd ~/RavenciaEntegrasyon
# 1) Bağlantı havuzu sınırı (dosya sonunda yeni satır yok; ';' ile eklenir)
sudo grep -q 'Maximum Pool Size' deploy/secrets/app_db_connection.txt || \
  sudo sh -c 'printf ";Maximum Pool Size=25;Timeout=15;Command Timeout=120" >> deploy/secrets/app_db_connection.txt'
# 2) Alarm kanalı: e-posta (üç satır: gönderen Gmail, alıcı, uygulama şifresi) ve/veya webhook
sudo install -m 600 -o root -g root /dev/null deploy/secrets/alert_smtp.txt
sudo nano deploy/secrets/alert_smtp.txt
sudo install -m 600 -o root -g root /dev/null deploy/secrets/alert_webhook_url.txt   # isteğe bağlı
sudo nano deploy/secrets/alert_webhook_url.txt
# 3) Yeni ci-entrypoint (root'a ait kopya)
bash deploy/scripts/install-ci-deploy.sh "$(cat ~/deploy_key.pub)"   # ya da aynı public key satırı
# 4) İlk .last-success kaydı: o an canlı olan commit
git rev-parse HEAD > ~/deploy-logs/.last-success
```

Birimler bir sonraki deploy'da otomatik kurulur; beklemeden kurmak için
`bash deploy/scripts/install-backup-timer.sh` (daemon-reload + enable --now dahil).

## GHCR seçeneği (sonraki adım)

Bugün imajlar sunucuda (Hostinger KVM 4: 4 vCPU, 16 GB) derlenir; CI aynı Dockerfile'ları yalnızca derleyip doğrular
(`image-app`, `image-edge`), itmez. Sonraki adım: Validate'te `docker/build-push-action` + `cache-to:
type=gha` ile `ghcr.io/<owner>/ravencia-{app,edge}:<sha>` itmek (`permissions: packages: write`),
Deploy'da digest'i SSH ile göndermek ve sunucuda `deploy.sh` yolunu (digest'li `pull`) kullanmak.
Sunucuya GHCR okuma için ince bir PAT ya da paketleri public yapmak gerekir. Böylece deploy süresi
ve sunucu yükü düşer, CI'da test edilen imaj birebir üretime gider.

## Notlar

- Sunucu Hostinger KVM 4, sabit IPv4 `187.126.24.163`; sunucu taşınırsa `DEPLOY_HOST` ve
  `DEPLOY_KNOWN_HOSTS` (`production` environment secret'ları) güncellenmelidir.
- Hostinger güvenlik duvarı ve `ufw` 22. portu GitHub runner'larına açık tutmalıdır (IP aralığı değişkendir).
- `fast-deploy.sh` doğrulama veritabanının anonim volume'ünü `--volumes` ile siler.
- `appsettings.Development.json` (API) yayın çıktısına ve imaja girmez (`CopyToPublishDirectory=Never`);
  CI `image-app` işi bunu denetler.

## Hız

- `Validate` paralel çalışır: `plan`, `ops`, `backend-format`, `backend-build`, `backend`, `backend-postgres`
  (iki şerit), `image-app`, `image-edge`, `frontend`, sonda `gate`. Çözüm bir kez derlenir (`backend-build`,
  Release); bin/obj bir `tar.zst` artefaktı olarak (1 gün) `backend` ve iki PostgreSQL şeridine geçer, onlar
  `--no-build` ile test eder (10 Kasım 2026'ya kadar üç iş ayrı ayrı 80–120 sn derliyordu). Backend testleri
  tamamlayıcı filtrelerle bölünür:
  `backend` PostgreSQL'siz testleri (`Category!=PostgreSql`), `backend-postgres (main)` diğer PostgreSQL
  testlerini, `backend-postgres (load)` `[PostgreSqlFact(Lane = PostgreSqlLane.Load)]` ile işaretli uzun
  yük testini koşar; filtreler birlikte her testi kapsar. İşlerin süre sınırı 5 ya da 15 dakika (`timeout-minutes`).
- Ön yüz denetimleri (typecheck, lint, test, build, paket bütçesi) yalnız `frontend` işinde koşar; kenar imajı
  (`Dockerfile.production`, CI `image-edge` ve sunucu) yalnızca `npm run build:assets` ile paketi üretir
  (Validate her deploy'u kapıdan geçirdiği için tekrar gerekmez; yaklaşık 100 sn kazanç).
- Backend ve frontend işleri, **aynı içerik** (git tree hash) daha önce geçtiyse atlanır; geçen içerik
  için Actions cache'ine `validated-backend-*` / `validated-frontend-*` işareti yazılır. Hash'ler
  Dockerfile'ları ve `deploy/caddy`'yi de kapsar; imaj işi de geçmeden işaret yazılmaz.
- `ops` işi deploy betiklerini `bash -n` + `shellcheck` ile denetler ve `tests/ops/run.sh`'ı çalıştırır:
  çerçevesiz bash testleri (yedek saklama kuralları, `ci-entrypoint.sh` karar tablosu,
  kapanış kilidi beklemesi). Yerelde: `bash tests/ops/run.sh` (GNU coreutils, git, flock gerekir).
- `ops` işi, sunucudaki Docker Compose sürümünü (5.3.1; sunucuda apt'ten gelir ve hold'dadır, ikili dosyanın
  sha256'sı GitHub sürümüyle aynı) checksum'la kurup sürümü doğrular; `deploy.sh` ve `install-marketplacehub.sh`
  de 5.3.1 ister. Sunucuda Compose güncellenirse bu üç yer birlikte değişir. Ayrıca
  birleşik compose dosyalarını ve journald sürücüsünü, systemd takvim ifadelerini denetler.
- Ön yüz `npm run lint` adımı her zaman çalışır.
- CI'dan gelen deploy'da (`DEPLOY_REVISION`) sunucudaki test tekrarı atlanır; elle çalıştırılan
  `fast-deploy.sh` testleri yine koşar.
- Dockerfile yalnızca backend klasörlerini kopyalar, bu yüzden frontend değişikliği .NET katmanlarını
  yeniden derletmez. Build cache 7 gün korunur (disk 15 GB altına düşerse tamamı silinir).
- Frontend CSS bundle bütçesi (`check-bundle-budget.mjs`, 672 KiB) sınırda; aşılırsa Validate kırmızı olur
  ve Deploy çalışmaz.
