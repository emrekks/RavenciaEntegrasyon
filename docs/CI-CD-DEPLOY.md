# Otomatik Deploy (GitHub Actions → AWS Ubuntu)

`main` dalına push → `Validate` workflow'u geçer → `Deploy` workflow'u sunucuda
`deploy/scripts/fast-deploy.sh` çalıştırır (imaj derleme, taze yedek, migration, health doğrulaması).
Geri alma: [RUNBOOK-GERI-ALMA.md](RUNBOOK-GERI-ALMA.md).

## Akış

1. `.github/workflows/ci.yml` (`Validate`) derleme, test, format, imaj derlemesi ve güvenlik denetimlerini çalıştırır.
2. Deploy üç yoldan tetiklenir:
   - `main` üzerindeki başarılı bir push'un Validate'i bittiğinde (`workflow_run`), o commit;
   - her sabah **06:20 UTC (09:20 TR)** `schedule` ile — sunucu 00:30–09:00 kapalıyken gelen push'lar
     sabah yakalanır;
   - elle: Actions → Deploy → Run workflow (yalnızca `main`; isteğe bağlı `redeploy` kutusu).
   Zamanlanmış ve elle çalıştırmalar **son yeşil Validate SHA'sını** (`main`'e push, `success`) çözer
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

**Gece kapanışı (00:30 TR)**: kilit alındıktan sonra saat **23:45–00:30 (Europe/Istanbul)** arasındaysa
deploy başlatılmaz; `::warning::Deploy of <sha> deferred to morning` yazılır ve çıkış 0'dır. Sabah 09:20
çalışması son yeşil SHA'yı deploy eder. Sunucudaki root kopyası henüz güncellenmediyse aynı kural
`fast-deploy.sh` içinde de (yalnız CI yolu, `DEPLOY_REVISION`) uygulanır; elle çalıştırılan deploy
ertelenmez. Kapanışın kendisi depoda değil (AWS tarafında durdurma); bunun için
`ravencia-deploy-guard.service` kurulur: açılışta bir şey yapmaz, kapanışta `ExecStop`
(`wait-for-deploy-lock.sh`) `~/deploy-logs/.lock` serbest kalana kadar en çok 23 dk bekler. Docker'dan
önce durdurulduğu için konteynerler bu sırada çalışır; deploy'un koştuğu SSH oturum kapsamları
`/etc/systemd/system/session-.scope.d/ravencia-deploy-guard.conf` ile bu birimden sonra durdurulur.
Sunucuda doğrulama: `systemctl show -p After session-<n>.scope` çıktısında `ravencia-deploy-guard.service`
görünmeli. AWS durdurmayı kendi süre sınırından sonra zorlarsa bekleme yarıda kalır; kesin koruma
23:45 kuralıdır.
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
- **Migration süresi**: `fast-deploy.sh` migrate konteynerini `docker wait` ile en çok 30 dakika bekler
  (`RAVENCIA_MIGRATION_TIMEOUT_SECONDS`, varsayılan 1800); süre dolarsa konteyneri durdurur (açık işlem geri
  alınır, eski yığın son tamamlanan migration'ın şemasıyla hizmete devam eder) ve deploy başarısız olur.
  `migrate` komutu (`DatabaseMigrator`) yalnız kendi oturumunda `lock_timeout=15s` ve
  `statement_timeout=20min` kullanır: eski API/Worker çalışırken `ALTER TABLE` kilit kuyruğunda bekleyip
  tüm yazmaları kilitlemez; kilit alınamazsa tüm çalıştırma 20 sn arayla 4 kez denenir. Uygulamanın normal
  sorguları bu ayarlardan etkilenmez. `CREATE INDEX CONCURRENTLY` yazmaları kilitlemediği için bu tür
  migration'lar kendi içinde `lock_timeout`'u kapatabilir. Deploy işinin GitHub süresi 90 dakikadır.

## GitHub Secrets (Settings → Secrets and variables → Actions)

| Secret | Değer |
| --- | --- |
| `DEPLOY_HOST` | Sunucu IP/DNS adresi |
| `DEPLOY_USER` | `ubuntu` |
| `DEPLOY_SSH_KEY` | Deploy özel anahtarı (ed25519, tüm içerik) |
| `DEPLOY_KNOWN_HOSTS` | `ssh-keyscan` / sunucu host key satırı (`<host> ssh-ed25519 AAAA...`) |
| `ALERT_WEBHOOK_URL` | *(isteğe bağlı)* Validate (main) veya Deploy başarısız olunca bildirim; sunucudaki `alert_webhook_url.txt` ile aynı biçim |

## Güvenlik modeli

- Deploy anahtarı `authorized_keys` içinde `restrict,command="/usr/local/bin/ravencia-ci-deploy"` ile
  kısıtlıdır: shell, port yönlendirme ve başka komut çalıştıramaz. Script root'a aittir; repo pull'u
  bu anahtarın yetkisini değiştiremez.
- Sunucu host key'i `StrictHostKeyChecking=yes` ile sabitlenir.
- Deploy işi repo kodunu çalıştırmaz; yalnızca SSH ile SHA gönderir.
- Actions'lar commit SHA ile sabitlenmiştir (yorumda sürüm). `.github/dependabot.yml` haftalık
  (Pazartesi 06:00 TR) actions, Docker (kök + `deploy/caddy`), compose (PostgreSQL), npm ve NuGet
  güncellemesi önerir; her PR Validate'ten geçer, hiçbiri otomatik birleşmez. PostgreSQL digest'i
  değişirse `ci.yml` servis imajı ve `deploy/backup/restore-drill.sh` elle eşitlenmelidir.

## Anahtarı yenileme / kurulum

```bash
ssh-keygen -t ed25519 -N "" -C github-actions-ravencia-deploy -f deploy_key
# sunucuda, repo kökünden:
bash deploy/scripts/install-ci-deploy.sh "$(cat deploy_key.pub)"
```

Eski anahtarı `~/.ssh/authorized_keys` içinden silin ve `DEPLOY_SSH_KEY` secret'ını güncelleyin.

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
- PostgreSQL (`compose.yaml`): `max_connections=150`, `shared_buffers=256MB`,
  `effective_cache_size=1GB`, `work_mem=16MB`, `log_min_duration_statement=2000` (2 sn üstü sorgular
  loglanır), `shm_size: 256m`. Bu ayar değişikliği ilk deploy'da postgres konteynerini yeniden yaratır
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

(`sudo systemctl restart systemd-journald`.) IAM rolü eklendiğinde `awslogs` sürücüsü düşünülebilir.

## Kenar (Caddy)

- `/health/live` herkese açık (yalnız süreç canlılığı; dış uptime izleyicisi ve Deploy doğrulaması).
- `/health/ready`, `/health/queues` yalnızca özel kaynak adreslerine (`remote_ip private_ranges`) yanıt
  verir; dışarıya `404`. Sunucu üstündeki betikler loopback'ten girer:
  `curl --resolve panel.ravencia.com:443:127.0.0.1 https://panel.ravencia.com/health/ready`.
- `Clear-Site-Data: "cache"` yalnızca `/api/v1/auth/*` 401 yanıtlarına eklenir.
- `/api/*` ve `/hubs/*` yanıtları sıkıştırılmaz (BREACH sınıfı); statik dosyalar ve SPA kabuğu
  zstd/gzip ile sıkıştırılmaya devam eder.
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

## İşletim: zamanlayıcılar, alarm, izleme

`install-backup-timer.sh` (her deploy'da çalışır) şu birimleri kurar ve zamanlayıcıları etkinleştirir:

| Birim | Ne zaman | Ne yapar |
| --- | --- | --- |
| `ravencia-backup.timer` | 20:30 UTC (23:30 TR) ±10 dk, `Persistent=true` | `daily-backup.sh`: api/caddy konteynerlerini 5 dk'ya kadar bekler, taze yedek + doğrulama |
| `ravencia-restore-drill.timer` | ayın ilk Pazar'ı 19:00 UTC (22:00 TR) | `monthly-restore-drill.sh`: en yeni seti izole konteynerlere geri yükler, migration/API/Worker sağlığını doğrular |
| `ravencia-healthwatch.timer` | 5 dakikada bir, 09:00–00:25 TR | `healthwatch.sh`: `/health/ready`, `/health/queues`, Worker sağlığı; sağlıksız API'yi yeniden başlatır |
| `ravencia-alert@.service` | `OnFailure=` (yedek, tatbikat) ve healthwatch | `send-alert.sh` ile tek satırlık bildirim |
| `ravencia-deploy-guard.service` | açılışta etkin, iş kapanışta | `wait-for-deploy-lock.sh`: süren deploy'un kilidini en çok 23 dk bekler |

**Yedek saklama** (`deploy/backup/backup.sh`): son 72 saatteki tüm setler + son **7 günün** her gününün
en yeni seti + daha eski 4 ISO haftasının her birinin en yeni seti; en yeni set her koşulda kalır
(`BACKUP_KEEP_DAILY`, `BACKUP_KEEP_WEEKLY` ile değiştirilebilir). Bir set ≈1,1 GB; olağan durumda
~12–14 set ≈ 15 GB (72 GB kök diskte).

**Disk koruması**: yeni set yazılmadan önce, en yeni setin boyutu kadar ekleme sonrası disk doluluğu
hesaplanır. %85'i (`BACKUP_DISK_LIMIT_PERCENT`) geçecekse önce **en eski haftalık setler** silinir
(günlük ve son 72 saatlik setlere dokunulmaz). Hâlâ %85 üstündeyse yedek yine alınır ama
`DISK_GUARD_WARNING` satırı basılır ve deploy/yedek betiği `send-alert.sh` ile alarm gönderir.
%95'i (`BACKUP_DISK_HARD_LIMIT_PERCENT`) geçecekse yedek **reddedilir** (dump PostgreSQL'in diskini
doldurmasın): gece yedeği başarısız olur (`OnFailure` alarmı), deploy yedeksiz ilerlemez.
Disk kullanımı: `df -h /` ve `sudo docker run --rm -v marketplacehub_backup_staging:/b alpine du -sh /b/*`.

**Alarm kanalı**: `deploy/secrets/alert_webhook_url.txt` (tek satır, root:root 600). Dosya yoksa alarm
gönderilmez; `send-alert.sh` mesajı stderr'e (journal) yazar ve her deploy
`::warning::No alert channel is configured` uyarısı basar (Actions çalışmasında da görünür). Kanal
kurulmadan healthwatch, yedek ve otomatik geri alma alarmları kimseye ulaşmaz.

- Telegram: `https://api.telegram.org/bot<TOKEN>/sendMessage?chat_id=<CHAT_ID>`
- Slack (ya da Slack uyumlu; Discord için webhook URL'sinin sonuna `/slack`): `https://hooks.slack.com/services/...`

Deneme: `bash deploy/scripts/send-alert.sh "test"`.

**Healthwatch**: bir sorun iki ardışık kontrolde (≈10 dk) sürerse alarm, sürdükçe saatte bir tekrar,
düzelince "Recovered" mesajı. Docker'ın `unhealthy` dediği API konteyneri iki ardışık kontrolde öyle
kalırsa ve PostgreSQL sağlıklıysa `docker restart` edilir (en çok 30 dakikada bir) ve alarm gönderilir.
Açılıştan sonraki ilk 10 dakika atlanır. Durum: `/var/lib/ravencia-healthwatch/`.
Aynı alarm akışıyla şunlar da denetlenir: kök disk doluluğu ≥ %85 (`RAVENCIA_DISK_ALERT_PERCENT`),
PostgreSQL istemci bağlantıları ≥ `max_connections`'ın %80'i (`RAVENCIA_DB_CONNECTION_ALERT_PERCENT`;
`pg_stat_activity`, postgres konteynerinde `psql`) ve `marketplacehub_backup_staging` içindeki en yeni
yedek setinin yaşı ≥ 30 saat (`RAVENCIA_BACKUP_MAX_AGE_HOURS`; gece kapanışı payı dahil) ya da hiç set
olmaması.

> Autoheal sidecar'ı (`willfarrell/autoheal`) bilinçli olarak kullanılmadı: Docker soketini
> (host'ta root eşdeğeri) üçüncü taraf bir imaja açmak gerekirdi; aynı işi sunucudaki healthwatch
> birimi, PostgreSQL kesintisinde gereksiz yeniden başlatma yapmadan görüyor.

**Dış izleme (elle kurulmalı, önerilen, ücretsiz)** — sunucu ya da Docker tamamen çökerse healthwatch da
çalışmaz; bunu yalnız dışarıdan bir izleyici görür: UptimeRobot/Better Stack ile `https://panel.ravencia.com/health/live`
5 dakikada bir; bakım penceresi 00:30–09:00 Europe/Istanbul. Ready/queues dışarıya kapalı olduğu için
derin kontroller sunucudaki healthwatch'tadır.

**Metrikler**: imaj yalnızca debugger/profiler eklemeyi kapatır
(`DOTNET_EnableDiagnostics_Debugger=0`, `DOTNET_EnableDiagnostics_Profiler=0`); tanılama IPC soketi
açıktır. Örnek: `dotnet-counters` aracını içeren geçici bir konteyneri aynı PID ad alanına ve `/tmp`'ye
bağlayıp `dotnet-counters monitor -p 1 --counters MarketplaceHub` (araç imajda yoktur).

## Elle uygulanacaklar (bu değişikliklerden sonra bir kez)

```bash
cd ~/RavenciaEntegrasyon
# 1) Bağlantı havuzu sınırı (dosya sonunda yeni satır yok; ';' ile eklenir)
sudo grep -q 'Maximum Pool Size' deploy/secrets/app_db_connection.txt || \
  sudo sh -c 'printf ";Maximum Pool Size=25;Timeout=15;Command Timeout=120" >> deploy/secrets/app_db_connection.txt'
# 2) Alarm kanalı (isteğe bağlı)
sudo install -m 600 -o root -g root /dev/null deploy/secrets/alert_webhook_url.txt
sudo nano deploy/secrets/alert_webhook_url.txt
# 3) Yeni ci-entrypoint (root'a ait kopya)
bash deploy/scripts/install-ci-deploy.sh "$(cat ~/deploy_key.pub)"   # ya da aynı public key satırı
# 4) İlk .last-success kaydı: o an canlı olan commit
git rev-parse HEAD > ~/deploy-logs/.last-success
```

Birimler bir sonraki deploy'da otomatik kurulur; beklemeden kurmak için
`bash deploy/scripts/install-backup-timer.sh` (daemon-reload + enable --now dahil).

## GHCR seçeneği (sonraki adım)

Bugün imajlar sunucuda (2 vCPU) derlenir; CI aynı Dockerfile'ları yalnızca derleyip doğrular
(`image-app`, `image-edge`), itmez. Sonraki adım: Validate'te `docker/build-push-action` + `cache-to:
type=gha` ile `ghcr.io/<owner>/ravencia-{app,edge}:<sha>` itmek (`permissions: packages: write`),
Deploy'da digest'i SSH ile göndermek ve sunucuda `deploy.sh` yolunu (digest'li `pull`) kullanmak.
Sunucuya GHCR okuma için ince bir PAT ya da paketleri public yapmak gerekir. Böylece deploy süresi
ve sunucu yükü düşer, CI'da test edilen imaj birebir üretime gider.

## Notlar

- Sunucu IP'si AWS'de Elastic IP değilse durdur/başlat sonrası değişir; `DEPLOY_HOST` ve
  `DEPLOY_KNOWN_HOSTS` güncellenmelidir.
- Security group 22. portu GitHub runner'larına açık olmalıdır (IP aralığı değişkendir).
- `fast-deploy.sh` doğrulama veritabanının anonim volume'ünü `--volumes` ile siler.
- `appsettings.Development.json` (API) yayın çıktısına ve imaja girmez (`CopyToPublishDirectory=Never`);
  CI `image-app` işi bunu denetler.

## Hız

- `Validate` paralel çalışır: `plan`, `ops`, `backend-format`, `backend`, `image-app`, `image-edge`,
  `frontend`, sonda `gate`. Her iş en çok 30 dakika (`timeout-minutes`).
- Backend ve frontend işleri, **aynı içerik** (git tree hash) daha önce geçtiyse atlanır; geçen içerik
  için Actions cache'ine `validated-backend-*` / `validated-frontend-*` işareti yazılır. Hash'ler
  Dockerfile'ları ve `deploy/caddy`'yi de kapsar; imaj işi de geçmeden işaret yazılmaz.
- `ops` işi, sunucunun gerektirdiği Docker Compose 2.40.2'yi checksum'la kurup sürümü doğrular,
  birleşik compose dosyalarını ve journald sürücüsünü, systemd takvim ifadelerini denetler.
- Ön yüz `npm run lint` adımı, `package.json`'da `lint` betiği varsa çalışır.
- CI'dan gelen deploy'da (`DEPLOY_REVISION`) sunucudaki test tekrarı atlanır; elle çalıştırılan
  `fast-deploy.sh` testleri yine koşar.
- Dockerfile yalnızca backend klasörlerini kopyalar, bu yüzden frontend değişikliği .NET katmanlarını
  yeniden derletmez. Build cache 7 gün korunur (disk 15 GB altına düşerse tamamı silinir).
- Frontend CSS bundle bütçesi (`check-bundle-budget.mjs`, 655 KiB) sınırda; aşılırsa Validate kırmızı olur
  ve Deploy çalışmaz.
