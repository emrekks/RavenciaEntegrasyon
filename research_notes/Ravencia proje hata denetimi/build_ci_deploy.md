# Build, test, CI ve dağıtım denetimi (Ravencia Marketplace Hub)

Kapsam: Dockerfile, deploy/ (compose, caddy, crowdsec, backup, scripts, systemd, docker), .github/workflows,
.claude/skills/ship/ship.sh, Directory.Build.props, Directory.Packages.props, global.json. Depo HEAD `3787b73`
(5 Kas 2026). Kaynaklar depo içi dosya:satır olarak verildi (URL yerine). Canlı sunucuya bağlanılmadı.
GitHub ayarları yalnız salt okunur `gh api` ile okundu.

## 1) Proje özeti ve veri akışı (rapor girişi için)

### Takeaway
Çok kiracılı bir pazaryeri back-office sistemi: .NET 10 API ile Worker aynı imajdan çalışır ve PostgreSQL 18'i
paylaşır. React SPA'yı Caddy sunar, Caddy `/api`, `/hubs`, `/health` ve `/internal` isteklerini API'ye iletir.
Pazaryeri API'leriyle yalnız Worker ve API, `egress` ağı üzerinden konuşur. Dağıtım GitHub Actions'ta
"Validate → Deploy" zinciriyle yürür. Deploy, forced-command SSH ile sunucuda imajı yeniden kurar, önce
yedek alır, sonra migrate eder.

### Cited Findings
- Sistem; Trendyol, Hepsiburada, N11, Pazarama, PttAVM, Shopify ve Trendyol E-Faturam için ürün, sipariş, iade,
  fatura ve stok işlemlerini tek merkezden yönetir. Liste ekranları pazaryerinden değil yerel PostgreSQL'den okur.
  Dış işlemler kuyruklu arka plan işleriyle yürür (Kuyrukta → Worker kiralar → istek → yeniden okuma →
  sonuç). — [docs/RAVENCIA-SISTEM-ISLEYIS-BELGESI.md:1-60](docs/RAVENCIA-SISTEM-ISLEYIS-BELGESI.md)
- Katmanlar: `MarketplaceHub.Api`, `Application`, `Domain`, `Infrastructure` (Adapters, Jobs, Persistence,
  Identity, Alerts…), `Worker` ve `Web` (React 19/Vite). — `src/` dizin listesi
- Tek uygulama imajı: aynı imaj API'yi (`api/MarketplaceHub.Api.dll`), Worker'ı (`worker/...Worker.dll`) ve
  tek seferlik `migrate`'i çalıştırır. Runtime non-root `APP_UID` kullanıcısıyla, 8080 portunda koşar.
  — [Dockerfile:30-45](Dockerfile), [deploy/compose/compose.yaml:106-139](deploy/compose/compose.yaml)
- Ağlar: `backend` iç ağdır (`internal: true`, 172.30.10.0/24): postgres, api, worker ve caddy bu ağdadır.
  `egress` ağına api ile worker bağlıdır (pazaryeri çağrıları için). `edge` ağında caddy durur ve 80, 443 ile
  443/udp portlarını yayınlar. Postgres'in yayınlanmış portu yoktur.
  — [deploy/compose/compose.yaml:53-58,116,131,167,190,263-272](deploy/compose/compose.yaml)
- Akış: tarayıcı → Caddy (TLS, güvenlik başlıkları, SPA `/srv`) → `api:8080`. SignalR `/hubs/*` yolundan
  gider. API ve Worker, PostgreSQL'e `app_db_connection` secret'ı ile bağlanır. Worker işleri kiralar ve
  heartbeat dosyası yazar. — [deploy/caddy/Caddyfile.production:105-121](deploy/caddy/Caddyfile.production),
  [src/MarketplaceHub.Worker/Worker.cs:14-17,723-740](src/MarketplaceHub.Worker/Worker.cs)
- API, `X-Forwarded-For` başlığına yalnız backend alt ağından güvenir (ForwardLimit 1).
  — [src/MarketplaceHub.Api/Program.cs:66-71](src/MarketplaceHub.Api/Program.cs)
- CI/CD zinciri:
  - Bir push önce Validate'i (`ci.yml`) tetikler. Deploy, `workflow_run` ile yalnız main'e yapılan push'un
    başarılı Validate koşusundan sonra çalışır.
  - Deploy, `ssh deploy@host '<sha>'` çağırır. Sunucuda forced command `ci-entrypoint.sh` çalışır, bu da
    `fast-deploy.sh` betiğini başlatır.
  - `fast-deploy.sh` sırası: imaj build → doğrulanmış yedek → (gerekirse) yedeğin kopyası üzerinde migration
    provası → migrate → api/worker/caddy → verify_stack. Doğrulama başarısız olursa otomatik rollback dener.
  - Kaynak: [.github/workflows/deploy.yml:1-60](.github/workflows/deploy.yml),
    [deploy/scripts/ci-entrypoint.sh](deploy/scripts/ci-entrypoint.sh),
    [deploy/scripts/fast-deploy.sh:253-470](deploy/scripts/fast-deploy.sh)
- main için GitHub ruleset ("main koruması", aktif) şunları uygular: silme ve non-fast-forward yasak, gerekli
  durum kontrolü `gate`, bypass aktörü yok. — `gh api repos/emrekks/RavenciaEntegrasyon/rulesets/25508142`
  (salt okunur sorgu, 5 Kas 2026)
- Sunucu tarafı zamanlayıcılar:
  - Yedek günde iki kez (08:59 ve 20:59 UTC).
  - Aylık restore tatbikatı.
  - 5 dakikada bir healthwatch (API'yi kendi kendine yeniden başlatır).
  - Aylık bakım.
  - CrowdSec izin listesi yenileme.
  - Kaynak: [deploy/scripts/install-backup-timer.sh:21-38](deploy/scripts/install-backup-timer.sh)

### Inferences
- Mimari, tek sunuculu ve Docker Compose ile yönetilen bir modüler monolit. Sunucu kaybı tek hata noktası.
  Veritabanı, yedekler ve imajlar aynı makinede duruyor (bkz. bölüm 3).

### Gaps
- Canlı konteynerlerin gerçek durumu (çalışan sha, sağlık) okunmadı. Görev sunucuya dokunmayı yasaklıyor.

## 2) Somut, doğrulanmış kusurlar (dosya:satır, senaryo, şiddet, güven)

### Takeaway
Altyapı iyi sertleştirilmiş: non-root uygulama, cap_drop ALL, read-only kök dosya sistemi, digest ile
sabitlenmiş imajlar, forced-command deploy, ruleset ile korunan main, gerçek restore tatbikatı ve migration
provası. En önemli açıklar üç tane:
- (a) Sunucu dışı yedek otomatik değil. Yedekler aynı diskte ve şifresiz duruyor.
- (b) Yarım kalan bir yedek seti "en yeni ve geçerli" sayılabiliyor.
- (c) ship.sh'nin kilit devralma mantığı başka bir oturumun kilidini silebiliyor.

### Cited Findings

**Yedekleme**
- **[Yüksek][Kesin, depo düzeyinde] Sunucu dışı yedek zamanlanmamış.**
  - Kurulum betiği `offsite-copy.sh` için ne bir systemd birimi ne de bir timer kuruyor. Kurulan birimler
    şunlar: alert, backup, restore-drill, healthwatch, maintenance, deploy-guard ve crowdsec.
  - Belge bu timer için "sonra eklenecek" diyor ve birimi yalnız örnek olarak veriyor.
  - Senaryo: sunucu diski kaybolur, Hostinger hesabı kapanır veya fidye yazılımı bulaşır. Elle
    çalıştırılmış son kopya dışındaki bütün yedekler `marketplacehub_backup_staging` volume'üyle birlikte
    kaybolur.
  - Kaynak: [deploy/scripts/install-backup-timer.sh:21-33](deploy/scripts/install-backup-timer.sh),
    [docs/CI-CD-DEPLOY.md:325-347](docs/CI-CD-DEPLOY.md), depoda `deploy/systemd/ravencia-offsite-copy.*`
    dosyası yok.
- **[Orta][Kesin] Yapılandırılmamış offsite kopya sessizce 0 ile çıkıyor.** Betik bunu "not configured"
  yazıp 0 dönerek yapıyor, yani alarm üretmiyor. Bir timer kurulsa bile anahtar veya remote eksikse
  sessizce hiçbir şey kopyalanmaz. Belge bunu açıkça kabul ediyor ("alarm üretmez").
  — [deploy/backup/offsite-copy.sh:36-39,41-56](deploy/backup/offsite-copy.sh),
  [docs/CI-CD-DEPLOY.md:345-346](docs/CI-CD-DEPLOY.md)
- **[Orta][Kesin] Offsite kopya silinmeye karşı korumasız ve yüklenen nesne yalnız boyutla doğrulanıyor.**
  - `rclone.conf` sunucuda duruyor ve drive.file kapsamı, yüklenen dosyaları silme yetkisi de veriyor.
    Sunucuyu ele geçiren biri offsite kopyaları da silebilir (betik yorumunda kabul ediliyor).
  - Yükleme sonrası kontrol yalnız "boyut > 0". Uzaktaki nesnenin checksum'ı doğrulanmıyor.
  - Kaynak: [deploy/backup/offsite-copy.sh:16-19,100-107](deploy/backup/offsite-copy.sh)
- **[Düşük][Kesin] Yerel yedek setleri şifresiz.** `pg_dump` çıktısı ve `files` ile `dp-keys` (Data
  Protection anahtarları) arşivi düz dosya olarak docker volume'üne yazılıyor. Tek koruma `umask 077`.
  Kök disk imajı sızarsa bu dosyalar okunabilir.
  — [deploy/backup/backup.sh:2-3,206-209](deploy/backup/backup.sh)
- **[Orta][Kesin, kod okuması; tetikleyici Muhtemel] Yarım kalan yedek seti kalıcı olarak "geçerli" sayılıyor.**
  - Senaryo: `pg_dump` veya `tar` set ortasında hata verir (disk, bağlantı, OOM). `set -eu` betiği hemen
    sonlandırır ve boş ya da yarım `$target` dizini (geçerli bir zaman damgası adıyla) silinmeden kalır.
  - Sonuç 1: `prune_expired_backups` setleri doğrulamıyor. 72 saat sonra yarım set, aynı yarım gündeki
    sağlam setin yerine "o yarım günün en yenisi" olarak saklanır ve sağlam set silinir.
  - Sonuç 2: `guard_disk_space` yer tahminini en yeni setin boyutundan yapıyor, yani küçük yarım setten.
    Bu yüzden ihtiyacı düşük tahmin eder.
  - Sonuç 3: `healthwatch.sh` "en yeni yedek yaşı" kontrolünde yalnız dizin adına bakıyor, yarım set taze
    sayılıyor. `monthly-restore-drill.sh` de en yeni dizini doğrulamadan seçiyor (tatbikat başarısız olur,
    alarm gelir).
  - Kaynak: [deploy/backup/backup.sh:199-212,79-123,131-167](deploy/backup/backup.sh),
    [deploy/scripts/healthwatch.sh:99-111](deploy/scripts/healthwatch.sh),
    [deploy/scripts/monthly-restore-drill.sh:26-28](deploy/scripts/monthly-restore-drill.sh)
- **[Orta][Kesin] Alarm kanalı zorunlu değil.** Alarm kanalı yoksa yedek, restore ve deploy hataları yalnız
  journal'a yazılıyor. `warn_when_alerts_unconfigured` yalnız uyarı veriyor ve deploy'u durdurmuyor.
  `ravencia-backup.service` başarısız olunca `OnFailure` → `send-alert.sh` çalışıyor, ama kanal yoksa
  hata sessiz kalıyor. — [deploy/scripts/lib.sh:73-83](deploy/scripts/lib.sh),
  [deploy/systemd/ravencia-backup.service](deploy/systemd/ravencia-backup.service)
- Olumlu bulgular, doğrulandı:
  - Her deploy ve rollback öncesinde taze bir yedek alınıyor, ardından `sha256sum -c` ve
    `pg_restore --list` ile doğrulanıyor. — [deploy/scripts/lib.sh:93-119](deploy/scripts/lib.sh)
  - Aylık restore tatbikatı gerçekten geri yüklüyor: izole iç ağda `pg_restore --exit-on-error`, şema,
    migration ve tenant sayımı, özel volume'ün güvenli yollarla açılması, `ExternalWrites=false` ile API
    ve Worker sağlığı. — [deploy/backup/restore-drill.sh:94-214](deploy/backup/restore-drill.sh)
  - Rotasyon: son 72 saatte 5 set, 7 gün boyunca yarım gün başına 1, 4 haftalık ve 6 aylık set; disk
    koruması %85/%95. — [deploy/backup/backup.sh:19-37](deploy/backup/backup.sh)

**ship.sh (yarış durumları)**
- **[Orta][Kesin] Kilit devralma, sahibinin kilidini siliyor.**
  - Kilit `mkdir` ile alınıyor ve 60 dakikadan eskiyse "bayat" sayılıp siliniyor (`lock_stale_min=60`).
  - Kilit sahibi, main ilerlediğinde kilidi tutarken `validate()` çalıştırıyor. Bu adım koşunun bulunmasını
    en fazla 10 dakika, sonucunu ise en fazla 90 dakika bekleyebiliyor. SKILL.md bunu "15–60 dk"
    olarak veriyor. Kilidin mtime'ı tutulduğu süre boyunca yenilenmiyor.
  - Senaryo: A'nın yeniden doğrulaması 60 dakikayı geçer. B kilidi "bayat" sayıp siler ve alır. A bitince
    `release()` koşulsuz olarak `rm -rf "$lock"` çalıştırır ve B'nin kilidini siler. C de içeri girer.
  - Sonuç: aynı anda birden fazla merge ve doğrulama yürür. Non-ff push'u ruleset reddettiği için main
    bozulmaz, ama "not pushed: main moved outside ship" ile döngüsel boşa Validate koşuları oluşur.
  - Kaynak: [.claude/skills/ship/ship.sh:18-19,33-46,55-74,93,98](.claude/skills/ship/ship.sh)
- **[Düşük][Kesin] Aynı sha'nın Validate koşusu yeniden başlatılamıyor.** `validate()` koşuyu
  `branch+head_sha` ile arıyor. Aynı sha'yı yeniden ship etmek yeni bir koşu tetiklemez (push no-op).
  Önceki koşu iptal edilmiş veya flaky ise ship, yeni bir commit gelene kadar hep `validate=1` döner. Aynı
  topic'i kullanan iki oturum `cancel-in-progress` nedeniyle birbirinin koşusunu da iptal eder.
  — [.claude/skills/ship/ship.sh:30-35](.claude/skills/ship/ship.sh), [.github/workflows/ci.yml:13-15](.github/workflows/ci.yml)
- **[Düşük][Kesin] "live" kontrolü yalnız tam sha'yı arıyor.** Kontrol, `ravencia-ro images` çıktısında
  sha'nın 12 karakterlik önekinin tam 3 kez geçmesini bekliyor. Bu arada başka bir ship daha yeni bir
  commit'i canlıya taşırsa (ki bu commit bizimkini içerir) betik 40 dakika bekler ve "not live yet" ile
  hata verir. — [.claude/skills/ship/ship.sh:100-107](.claude/skills/ship/ship.sh)
- **[Düşük][Kesin] SIGKILL kilidi 60 dakika asılı bırakıyor.** Kilit yalnız EXIT, INT ve TERM'de
  bırakılıyor. Arka plan görevi SIGKILL ile durdurulursa kilit 60 dakika kalır ve diğer ship'ler bekler.
  — [.claude/skills/ship/ship.sh:56-58](.claude/skills/ship/ship.sh)

**CI (ci.yml) ve dağıtım**
- **[Düşük-Orta][Kesin] Aynı içerikte audit ve Trivy adımları atlanıyor.** İçerik hash'i aynıysa (cache
  marker veya önceki başarılı koşu) backend, frontend, image-app ve image-edge işleri tümüyle atlanıyor.
  `npm audit`, `dotnet list package --vulnerable` ve Trivy taraması da bu işlerin içinde olduğu için
  değişmeyen içerikte hiç çalışmıyor.
  - Haftalık `image-scan.yml` yalnız imajları tarıyor. Edge imajında `package-lock` yok, yalnız `dist` var,
    bu yüzden npm prod bağımlılıkları için periyodik bir audit yok. Bu açığı yalnız Dependabot kapatıyor.
  - Kaynak: [.github/workflows/ci.yml:20-34,251-253,457-459](.github/workflows/ci.yml),
    [.github/workflows/image-scan.yml](.github/workflows/image-scan.yml)
- **[Düşük][Muhtemel] NuGet audit adımı hiçbir şeyi engellemiyor.** `dotnet list ... package --vulnerable`
  bulgu olsa bile 0 ile çıkıyor, `grep` veya `--format json` kontrolü yok. Gerçek engel büyük olasılıkla
  restore sırasındaki NuGetAudit uyarıları (`TreatWarningsAsErrors=true`).
  — [.github/workflows/ci.yml:252-253](.github/workflows/ci.yml), [Directory.Build.props:7](Directory.Build.props)
- **[Düşük][Muhtemel] `gate` kontrolü, değiştirilmiş bir ci.yml ile geçilebilir.** Ruleset yalnız `gate`
  adlı kontrol bağlamını şart koşuyor. Bir commit kendi ci.yml'ini değiştirirse (`gate` işi her zaman
  geçecek şekilde), kendi codex/** koşusu `gate=success` üretir ve main'e girebilir. Deploy yalnız
  "Validate success" koşulunu arıyor. Bu, kod incelemesi olmayan ve otomatik ship eden çok oturumlu bir
  akışta testleri atlamanın tek yolu. — ruleset (salt okunur `gh api`),
  [.github/workflows/deploy.yml:24-30](.github/workflows/deploy.yml)
- **[Düşük][Kesin] CI'nin test ettiği araç sürümleri, sunucunun build ettikleriyle aynı değil.**
  - .NET: CI testleri SDK 10.0.302 ile derliyor ve koşuyor. Üretim imajı ise `sdk:10.0.402` ile
    derleniyor (global.json `rollForward: latestFeature` buna izin veriyor). Sevk edilen ikili, testi
    yapılan derleyiciyle üretilmiyor. image-app işi Dockerfile'ı build ettiği için derleme kırılması
    yine de yakalanır.
  - Frontend: CI ve `engines` Node 24.18.1 kullanıyor, edge imajı ise `node:26.12.0-alpine` ile build
    ediliyor. `.npmrc` yok, yani `engine-strict` yok ve uyumsuzluk yalnız uyarı veriyor.
  - Kaynak: [global.json](global.json), [Dockerfile:2](Dockerfile), [.github/workflows/ci.yml:190,224,429](.github/workflows/ci.yml),
    [deploy/caddy/Dockerfile.production:2](deploy/caddy/Dockerfile.production),
    [src/MarketplaceHub.Web/package.json:6-9](src/MarketplaceHub.Web/package.json)
- **[Düşük][Kesin] fast-deploy'da işlevsiz bir bayrak ve sabit bir site adresi var.**
  - `--verify` bayrağı işlevsiz, çünkü `verify=true` zaten varsayılan.
  - `site_address`, production.env'den değil süreç ortamından okunuyor, varsayılanı da
    `https://panel.ravencia.com`. Alan adı değişirse doğrulama yanlış host'a yapılır.
  - Kaynak: [deploy/scripts/fast-deploy.sh:7-13,258](deploy/scripts/fast-deploy.sh),
    [deploy/scripts/rollback.sh:97](deploy/scripts/rollback.sh)
- **[Düşük][Kesin] Deploy yetkisi kökte root yetkisine eşdeğer.**
  - Deploy anahtarı yalnız sha kabul ediyor (iyi). Ama dağıtılan commit'in `fast-deploy.sh` ve compose
    dosyaları, `ubuntu` kullanıcısının parolasız sudo yetkisiyle çalışıyor. Her deploy
    `install-backup-timer.sh` ile root birimlerini de yeniliyor.
  - Sonuç: main'e girebilen her commit (yukarıdaki `gate` notuyla birlikte) sunucuda root olur. Bu durum
    betikte "Residual … A4" olarak kabul ediliyor.
  - Kaynak: [deploy/scripts/install-backup-timer.sh:40-44](deploy/scripts/install-backup-timer.sh),
    [deploy/scripts/install-ci-deploy.sh:40-45](deploy/scripts/install-ci-deploy.sh)
- **[Orta][Kesin] Salt okunur hesap bütün veritabanını okuyabiliyor.**
  - `ravencia_ro` rolüne `pg_read_all_data` verilmiş, yani bütün tabloları okuyabiliyor: parola hash'leri,
    oturum veya MFA verileri, şifreli pazaryeri kimlik bilgileri.
  - SSH anahtarı parolasız tutuluyor (ship.sh yorumu: "key without passphrase").
  - Geliştirici makinesindeki anahtar sızarsa üretim verisinin tamamı okunabilir.
  - Kaynak: [deploy/scripts/install-readonly-db-role.sh:18-25](deploy/scripts/install-readonly-db-role.sh),
    [.claude/skills/ship/ship.sh:9](.claude/skills/ship/ship.sh)

**Konteyner, healthcheck ve Caddy**
- Root ile çalışan konteynerler [Kesin][Düşük, belgelenmiş tasarım]:
  - `postgres`: resmi entrypoint root ile başlayıp gosu ile yetkiyi düşürüyor.
  - `caddy-volumes`: tek seferlik chown işi, ağı yok.
  - `backup`: root olarak çalışıyor, DAC_READ_SEARCH yetkisi var.
  - API, Worker ve migrate APP_UID (1654) ile, caddy 10080 ile çalışıyor. CI bu kuralları jq ile zorluyor.
  - Kaynak: [deploy/compose/compose.yaml:65-70,144-157,207-223](deploy/compose/compose.yaml),
    [.github/workflows/ci.yml:156-167](.github/workflows/ci.yml)
- Healthcheck'ler: postgres, api, worker ve caddy'de var. migrate ve backup tek seferlik işler.
  - **[Düşük][Muhtemel]** Docker "unhealthy" durumuna yeniden başlatmayla tepki vermiyor. Healthwatch
    yalnız API'yi yeniden başlatıyor.
  - Worker yalnız DB heartbeat'i bayatlarsa kendini kapatıyor. DB'ye dokunan ama işlemeyen bir Worker
    "unhealthy" kalır ve yalnız alarm üretir.
  - Kaynak: [deploy/scripts/healthwatch.sh:12-14,114](deploy/scripts/healthwatch.sh),
    [src/MarketplaceHub.Worker/Worker.cs:723-740](src/MarketplaceHub.Worker/Worker.cs)
- Caddy:
  - Eksiksiz güvenlik başlıkları var: HSTS 1 yıl ve includeSubDomains (preload yok), CSP,
    frame-ancestors none, nosniff, COOP/CORP, Permissions-Policy ve `-Server`.
  - `/health/*` ile `/internal/*` (`/health/live` hariç) özel adreslere kısıtlı. Admin API kapalı.
    Erişim günlüğünde CSRF ve idempotency başlıkları ile sorgu dizesi siliniyor.
  - BREACH'e karşı API ve hub yanıtları sıkıştırılmıyor.
  - TLS, Caddy varsayılanlarıyla otomatik ACME kullanıyor. Özel bir TLS ayarı yok, bu bir kusur değil.
  - **[Düşük][Kesin]** CSP'de `style-src 'unsafe-inline'` var.
  - Kaynak: [deploy/caddy/Caddyfile.production:1-136](deploy/caddy/Caddyfile.production)
- **[Düşük][Muhtemel] Özel adres kısıtı IPv6 ayarına bağlı.** `/health/ready`, `/health/queues` ve
  `/internal/*` erişimi `remote_ip private_ranges` ile sınırlı. Bu yalnız `userland-proxy: false` olduğu
  ve AAAA kaydı bulunmadığı sürece güvenli.
  - `daemon.json` elle kuruluyor. Unutulursa IPv6 ziyaretçiler bridge gateway'in özel IPv4 adresiyle gelir
    ve hazırlık ile kuyruk uçlarını görür. `/internal/*` ayrıca token istiyor.
  - Compose yorumu bu riski anlatıyor. Belge canlıda false olduğunu söylüyor.
  - Kaynak: [deploy/compose/compose.yaml:182-190](deploy/compose/compose.yaml),
    [deploy/docker/daemon.json](deploy/docker/daemon.json), [docs/CI-CD-DEPLOY.md:207,380](docs/CI-CD-DEPLOY.md)
- Postgres dışarıya açık değil [Kesin]: `ports` tanımı yok, `backend` ağı `internal: true`. CI'daki 5432
  yalnız runner içinde. — [deploy/compose/compose.yaml:54-104,266-270](deploy/compose/compose.yaml)
- Secret'lar [Kesin]: compose ve workflow'larda gerçek secret yok. CI'daki `development-only` ve
  `validation-only` parolaları test DB'leri için. Secret'lar dosya olarak (`*_FILE`) veriliyor,
  `deploy/secrets/` gitignore'da ve `git ls-files` ile pfx, env veya rclone dosyası bulunmadı.
  — [.gitignore:8-13](.gitignore), [deploy/compose/compose.yaml:280-283](deploy/compose/compose.yaml)

**Paket sürümleri**
- **[Düşük][Kesin] Paket sürümleri birbirini tutmuyor.** EF Core, Identity.EFCore ve EF Design 10.0.10
  sürümünde. Hosting, Mvc.Testing, aspnet runtime imajı (10.0.13) ve `dotnet-ef` aracı (10.0.13) daha
  ileride. EF paketleri üç yama geride; bu yamaların güvenlik içerip içermediği doğrulanmadı [Tahmin].
  — [Directory.Packages.props:6-8,9,17](Directory.Packages.props), [.config/dotnet-tools.json](.config/dotnet-tools.json),
  [Dockerfile:30](Dockerfile)
- `npm audit --omit=dev`: 0 açık (30 prod bağımlılığı). Tam `npm audit`: 7 orta seviye, hepsi dev:
  `typescript-eslint` zinciri ve `minimatch` 7.0.0–11.0.0. Sevk edilen pakete etkisi yok.
  — `npm audit` çıktısı, 5 Kas 2026 (src/MarketplaceHub.Web)
- `dompurify` (^3.4.17), `jsdom` ve `vitest` caret aralığıyla tanımlı, diğerleri tam sürümle. Yine de
  `npm ci` lock dosyasına bağlı kalıyor, bu bir kusur değil.
  — [src/MarketplaceHub.Web/package.json:21-40](src/MarketplaceHub.Web/package.json)

### Inferences
- En ağır gerçek risk felaket kurtarma. Bütün yedekler aynı VPS diskinde duruyor. Offsite kopya için
  hazır bir betik ve test var, ama zamanlayıcı kurulmamış.
- ship.sh'deki yarış main'i bozamıyor (ruleset non-ff reddediyor). Etkisi verimlilik ve gürültü düzeyinde.

### Gaps
- Sahibin offsite timer'ı sunucuda elle kurup kurmadığı ve `alert_*` dosyalarının var olup olmadığı yalnız
  canlıda görülebilir (`/prod-logs` host komutu). Bu denetimde sunucuya bağlanılmadı.
- `production` environment'ında onaylayıcı gerekip gerekmediği okunamadı: proxy `environments` API'sini
  403 ile engelledi.

## 3) Validate'i atlayan bir deploy yolu var mı?

### Takeaway
Hayır, doğrudan bir atlama yolu bulunmadı. Üç katman var: ruleset (main'e push için `gate` şart), Deploy'un
yalnız başarılı Validate ve `event == push` koşulunda çalışması, ve sunucuda yalnız `origin/main`'in
içerdiği sha'nın kabul edilmesi. Sıra dışı gelen veya eski bir sha da reddediliyor. Kalan risk dolaylı:
bir commit kendi ci.yml'ini zayıflatabiliyor.

### Cited Findings
- Deploy yalnız iki durumda çalışıyor: main üzerinde `workflow_dispatch`, ya da `workflow_run`'ın
  `conclusion == success` ve `event == push` olması (branches: [main]). Manuel çalıştırma, main'e push ile
  tetiklenen en son başarılı Validate'in sha'sını seçiyor.
  — [.github/workflows/deploy.yml:3-9,24-30,46-50](.github/workflows/deploy.yml)
- Sunucu tarafında `ci-entrypoint.sh`:
  - Yalnız 40 hex karakterlik bir sha kabul ediyor.
  - `git merge-base --is-ancestor <sha> origin/main` koşulunu arıyor.
  - `.last-success` sha'yı zaten içeriyorsa çalışmayı atlıyor. Bu, sıra dışı gelen Validate
    tamamlanmalarında eski sürüme dönülmesini önlüyor.
  - Rollback sonrası `.hold` koyuyor.
  - Kaynak: [deploy/scripts/ci-entrypoint.sh:21,49-53,57-84](deploy/scripts/ci-entrypoint.sh)
- main push'unda Validate, aynı sha daha önce bir koşuda başarılı olduysa ağır işleri atlıyor. `gate`
  atlanan işleri geçmiş sayıyor. İçerik aynı olduğu için bu bir atlama değil.
  — [.github/workflows/ci.yml:67-84,461-499](.github/workflows/ci.yml)
- `fast-deploy.sh`, CI yolunda sunucudaki izole test koşusunu atlıyor ("Validate already passed"). Edge
  imajı ise build sırasında typecheck, test, build ve check:bundle adımlarını yeniden çalıştırıyor.
  — [deploy/scripts/fast-deploy.sh:66-84](deploy/scripts/fast-deploy.sh),
  [deploy/caddy/Dockerfile.production:8-11](deploy/caddy/Dockerfile.production)
- `deploy.sh` (registry digest yolu) elle çalıştırılan bir ilk kurulum yolu ve hiçbir test koşmuyor. Ama
  yalnız sunucuda sudo ile, insan tarafından çalıştırılabiliyor. — [deploy/scripts/deploy.sh:1-83](deploy/scripts/deploy.sh)

### Inferences
- `fast-deploy.sh`, `DEPLOY_REVISION` olmadan elle çalıştırılırsa `git pull` ile main'in HEAD'ini alıyor
  ve izole validation-tests koşusunu yapıyor. Bu yol da testli.

### Gaps
- Yok. Ruleset bypass listesinin boş olduğu ve `current_user_can_bypass: never` olduğu doğrulandı.

## 4) ci.yml typecheck, lint, vitest, build, check:bundle ve dotnet testlerini koşuyor mu?

### Takeaway
Evet, hepsini koşuyor. Ek olarak dotnet format, EF bekleyen migration kontrolü, NuGet ve npm audit, Trivy
(CRITICAL ve fixable), shellcheck, ops bash testleri, compose doğrulaması ve sertleştirme kuralları,
systemd takvim doğrulaması ve security.txt son tarih kontrolü de var. Eksik: Playwright görsel testleri
(`test:visual`) ve `test:security`'nin ayrı bir adımı yok (ikincisi vitest kapsamında).

### Cited Findings
- frontend işi: `npm ci --ignore-scripts`, typecheck (`tsc -b`), lint (`--max-warnings=0`), `npm test`
  (vitest run), build, check:bundle ve `npm audit --omit=dev --audit-level=high`.
  — [.github/workflows/ci.yml:417-459](.github/workflows/ci.yml)
- backend: restore (locked) → build Release → `dotnet test --filter Category!=PostgreSql` → EF
  `has-pending-model-changes` → `list package --vulnerable`. backend-postgres, PostgreSQL 18.6 servisiyle
  iki şeritte koşuyor (main ve load). backend-format `dotnet format --verify-no-changes` çalıştırıyor.
  — [.github/workflows/ci.yml:178-312](.github/workflows/ci.yml)
- ops: `bash -n`, shellcheck, `tests/ops/run.sh`, `systemd-analyze calendar`, compose 5.3.1 config ve
  jq ile sertleştirme kontrolü. — [.github/workflows/ci.yml:86-176](.github/workflows/ci.yml)
- `tests/ops/run.sh` bu ortamda çalıştırıldı ve bütün ops testleri geçti (backup retention, ci-entrypoint,
  deploy-lock, offsite-copy, send-alert). shellcheck yerelde kurulu değildi. — yerel çalıştırma, 5 Kas 2026
- `test:visual` (Playwright) CI'da hiç çalışmıyor. — [src/MarketplaceHub.Web/package.json:16](src/MarketplaceHub.Web/package.json)

### Inferences
- CI kapsamı güçlü. Asıl zayıf nokta, içerik hash'i değişmediğinde audit ve tarama adımlarının
  atlanması (bölüm 2).

### Gaps
- Backend kontrolleri burada çalıştırılamadı: ortamda `dotnet` yok (`/usr/share/dotnet`, `~/.dotnet`
  bulunamadı). Bu yüzden `dotnet build` uyarıları ve başarısız test listesi raporlanamıyor. Test kaynağında
  yaklaşık 2840 `Fact`/`Theory` eşleşmesi var (grep sayımı, kesin test sayısı değil).

## 5) Eski AWS sunucusuna (63.180.140.51) kalan referanslar

### Takeaway
Yapılandırmalarda, betiklerde ve belgelerde eski AWS IP'si veya AWS adı geçmiyor; yalnız CLAUDE.md
"retired" notunda geçiyor. Yeni IP doğru yerlerde kullanılıyor. Ancak eski makinenin donanımına ait
bayat yorumlar kalmış: 2 vCPU, 8 GiB, 72 GB disk.

### Cited Findings
- `63.180.140.51|AWS|amazonaws|ec2-|Lightsail` araması yalnız [CLAUDE.md:9](CLAUDE.md) satırında eşleşti.
- `187.126.24.163` şu dosyalarda geçiyor:
  [.claude/skills/ship/ship.sh:8](.claude/skills/ship/ship.sh), `.claude/skills/prod-logs/run.sh:8`,
  `.claude/skills/prod-sql/run.sh:7`, [docs/CI-CD-DEPLOY.md:94,518](docs/CI-CD-DEPLOY.md),
  [docs/RUNBOOK-OLAY.md:4](docs/RUNBOOK-OLAY.md) ve [docs/RUNBOOK-GERI-ALMA.md:167](docs/RUNBOOK-GERI-ALMA.md).
- Bayat donanım ve yerleşim yorumları [Düşük][Kesin]:
  - [.github/workflows/ci.yml:354](.github/workflows/ci.yml): "the 2-vCPU host"
  - [deploy/backup/backup.sh:19](deploy/backup/backup.sh): "sized for the 72 GB root disk". Retention ve
    disk yüzdeleri bu varsayımla anlatılıyor.
  - [docs/CI-CD-DEPLOY.md:413](docs/CI-CD-DEPLOY.md): "72 GB kök diskte"
  - [docs/RUNBOOK-GERI-ALMA.md:49](docs/RUNBOOK-GERI-ALMA.md): "2 vCPU'da ~10 dk"
  - [deploy/compose/compose.yaml:182-189](deploy/compose/compose.yaml): IPv6/docker-proxy açıklaması.
    Canlıda `userland-proxy: false` olduğu için bu açıklama artık varsayılan durumu anlatmıyor.
  - compose.yaml'ın 74-77. satırları 16 GiB host'a göre güncel.

### Inferences
- KVM 4 planının çekirdek ve disk değerleri doğrulanmadı [Tahmin: 4 vCPU, 16 GB, ~200 GB NVMe]. Disk
  daha büyükse backup.sh'nin %85 koruması daha çok set tutar. Davranış doğru, yalnız yorumlar yanıltıcı.

### Gaps
- Hostinger diskinin gerçek boyutu sunucuya bağlanılmadan doğrulanamadı.
