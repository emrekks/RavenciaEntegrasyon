# F1 Kanıt Günlüğü

## 2026-08-17 - Immutable release Buildx manifest 429 remediation

- Source CI `32043779256` accepted commit `9f159c5`, but release runs `32043947714` and `32044093802` failed before registry authentication because the setup action received `429 Too Many Requests` while reading its Buildx release manifest from GitHub raw content.
- The release workflow now downloads the same pinned Buildx `v0.34.1` binary from the official Docker Buildx release URL using bounded curl retries, verifies SHA-256 `f1332ddb9010bd0b72628266c3a906d9a6979848033df4c8d9bd2cd113bae12b`, then creates the existing docker-container builder.
- No image was published and no Ubuntu deployment ran from the failed release attempts. Targeted repository guard, source CI and immutable publish validation remain pending.

## 2026-08-12 - F1-EV-032 v10.56 hedef izole restore tatbikatı

- Backup manifest: `20260812T132906Z`; DB ve private archive SHA-256: `PASS`; PostgreSQL image: pinned `18.4` digest.
- Production kaynaklarından ayrık timestamp-scope internal network, temiz DB volume ve private volume oluşturuldu. Restore sonucu `iam/integration/ops=3`, migration `13`, tenant `1`.
- Private archive absolute/parent traversal kontrolü ve `files`/`dp-keys` dizinleri: `PASS`.
- Restore kopyasında scheduler policy ve pending/running/retry işleri dış çağrıdan önce devre dışı; external writes `false`, ağ egress'siz.
- v10.56 immutable app `sha256:389f288e88b835be617c9a26548fe553bf085d833e4a3fab02570e965441184e`: migration up-to-date, API ready, Worker heartbeat `PASS`; süre `14 sn`.
- Cleanup sonrası timestamp-scope container/volume/network sayısı `0`; production API/Worker/Caddy/PostgreSQL healthy ve dış readiness `PASS`.
- Otomasyon: `deploy/backup/restore-drill.sh`; shell syntax ve izolasyon guard'ları CI'a eklendi. Off-host şifreli kopya kanıtı bu tatbikatın kapsamında değildir ve açık kalır.
- Commit `d9d841c` source CI `31604226847`: documentation transaction, deployment shell syntax, tam .NET solution ve web doğrulaması `PASS`.
- Hedef repository committed sürüme fast-forward edildikten sonra restore drill aynı backup/digest ile 13 saniyede tekrar `PASS`; cleanup sonrası eşleşen container, volume ve network listeleri boş, production health/readiness `PASS`.

## 2026-08-12 - F1-EV-031 v10.49 immutable deploy kabulü

- `84ba728` source CI `31548069815` ve `release-2026-08-12-v10.49` immutable publish `31548345363` `PASS`.
- App `sha256:c2698b0666ea3948260c41b450ec774b81a4cf83cb1ac1ccecb227a99b17d7cd`, edge `sha256:35673e1db13d8f302ffeade17e709c088dd55a5355d2e1a41d895fb7a3a35ad7` digestleri remote manifestte doğrulandı.
- Deploy öncesi backup `20260812T000007Z` üretildi; database ve private volume arşivi SHA-256 `PASS`, manifest PostgreSQL 18 + files + DP keys bilgisini doğruladı.
- Onaylı root Compose `2.40.2` ile fail-closed config, migration, API readiness, Worker/Caddy health, frontend asset ve dış `https://panel.ravencia.com/health/ready` HTTP 200 `PASS`.

## 2026-08-12 - F1-EV-030 fail-closed deploy Compose runtime

- Hedef Ubuntu'da kullanıcı Docker Compose `5.3.1`, root Docker Compose ise onaylı `2.40.2` sürümünü raporladı.
- `deploy.sh --validate-only`, kullanıcı ikilisini gördüğünde exact-version kapısında fail-closed durdu; immutable image kaydı ve çalışan servisler değiştirilmedi.
- Script compose pull/up/config ve worker inspect için tutarlı `sudo docker` kullanımına geçirildi. Hedefte `sudo docker compose ... config --quiet` `PASS` verdi.
- Kaynak CI, immutable release ve deploy/readiness kabulü bu değişiklikten sonra yeniden çalıştırılmalıdır.

## 2026-08-11 - F1-EV-028 immutable release source gate r2

- `release-2026-08-11-v10.37` source kapısı, GitHub Checks API'nin workflow adı yerine `verify` job adını döndürmesi nedeniyle imaj buildinden önce fail-closed durdu.
- Kapı, exact `verify.yml` workflow koşularında aynı SHA + `main` + `push` + `success` şartlarını GitHub Actions API ile doğrulayacak şekilde düzeltildi.
- Canlı GitHub Actions API sorgusu `31499027863` source koşusunu doğru SHA/branch/event/conclusion ile döndürdü; repository guard testleri `5/5` geçti.
- Required status check adı, image build/push, provenance/SBOM, digest doğrulaması ve release concurrency davranışı değiştirilmedi; r2 CI/release kanıtı bekleniyor.

## 2026-08-11 - F1-EV-029 immutable release token r3

- R2 job logu source-gate adımında `GITHUB_TOKEN: unbound variable` hatasını kanıtladı; build/push adımları çalışmadı.
- Yerleşik `github.token` yalnız source-gate adımına aktarıldı; workflow izinleri `actions: read`, `contents: read`, `packages: write` ile sınırlıdır.
- Main source CI `#125` `PASS`; r3 immutable release `#116` source kapısı, provenance/SBOM ve digest doğrulamasıyla `PASS`. App digest `sha256:69fba5c25a395cb0fe449040677c37c9c60842c8d6e6d73fc3fc48f2bacca6ed`, edge digest `sha256:02de6da2a569282ce033d72bedf1e391709f6f88168aefc66f2097a6fb6185dd`.
- Hedef `panel.ravencia.com` / `63.180.140.51`; yerel SSH agent/anahtarı ve AWS Console oturumu yok, public-key SSH reddedildi. Deployment `BLOCKED_TARGET_ACCESS`; mevcut runtime değiştirilmedi.

Doğrulama tarihi: 2026-07-31. Ortam: Windows geliştirme makinesi üzerinde Docker Desktop Linux/amd64; hedef Ubuntu Server değildir.

| Kanıt | Sonuç | Ölçüm |
| --- | --- | --- |
| `F1-EV-001` exact restore/lock | PASS | 11 .NET projesi restore; tüm project `packages.lock.json`; web `package-lock.json` |
| `F1-EV-002` warnings-as-errors build | PASS | 11/11 proje, 0 warning, 0 error |
| `F1-EV-003` migration no-seed | PASS | Fresh PostgreSQL 18.4; bootstrap/user/tenant tabloları boş |
| `F1-EV-004` bootstrap repeat/concurrency | PASS | İki eşzamanlı + tekrar çağrı; 1 marker, 1 tenant, 1 user, 1 membership; force-password-change true |
| `F1-EV-005` security unit tests | PASS | keyed token digest, TOTP ±1/replay, private-file traversal/cross-tenant reject |
| `F1-EV-006` repository/faz guards | PASS | Domain dependency-free; F2+ route/platform adapter yok |
| `F1-EV-007` .NET test seti | PASS | 11 test, 0 failed, PostgreSQL Testcontainers dahil |
| `F1-EV-008` web doğrulama | PASS | TS strict typecheck, 1 component test, Vite production build, Playwright Chromium smoke |
| `F1-EV-009` Compose v2 config | PASS | v2.40.2 config; yalnız Caddy 80/443 host publish |
| `F1-EV-010` Linux image build | PASS | app `sha256:97ebfa...93e6`, edge `sha256:6d9e3f...80b9`; linux/amd64; app user 1654 |
| `F1-EV-011` runtime smoke | PASS | migration exit 0; API/PostgreSQL/Caddy healthy; Worker running; HTTPS SPA/ready/CSRF 200 |
| `F1-EV-012` network/security smoke | PASS | API/Worker/DB host port yok; Caddy yalnız sabit internal subnet üzerinden trusted proxy; HTTPS Origin + CSRF + secure cookie login başarılı; correlation/security header'ları var |
| `F1-EV-013` persistent state | PASS_LOCAL | PostgreSQL/app files/Data Protection named volume; DP key `/var/lib/marketplacehub/dp-keys` altında |
| `F1-EV-014` backup/restore | PASS_LOCAL | DB + files + DP keys + SHA manifest; boş PostgreSQL 18.4 restore; 21 F1 tablosu |
| `F1-EV-015` capability/write guard | PASS | gerçek platform adapter/secret yok; `external-writes=false`; capability matrisi `UNKNOWN` |
| `F1-EV-016` auth/MFA HTTPS zinciri | PASS_LOCAL | `PASSWORD_CHANGE_REQUIRED`, allowlist 403, 10 recovery code, replay 400, TOTP challenge, 3 session, revoke-others 204, disable 204 |
| `F1-EV-017` break-glass | PASS_LOCAL | OS authorization environment + reason ile CLI exit 0; append-only audit count 1 |
| `F1-EV-018` job retry contract amendment (`2026-08-02`) | PASS_POSTGRES_LOCAL | Şartname s.30–32 state/max-attempt/backoff/expired-attempt sözleşmesi ile lease süresinin dörtte birinde ayrı scope heartbeat, lease-loss iptali, completion fencing ve correlation aktarımı tamamlandı; worker-kill/retry/dead-letter/heartbeat/stale-token zinciri izole PostgreSQL 18.4 üzerinde geçti |
| `F1-EV-019` production image/TLS release hazırlığı (`2026-08-02`) | PASS_LOCAL_BUILD / PUBLISH_DEFERRED | PILOT_LOCAL internal CA'dan ayrı public automatic HTTPS Caddyfile'ı, production edge Dockerfile'ı ve exact action/Buildx pinli manuel GHCR app/edge yayın akışı eklendi; exact Compose v2.40.2 production config, `3/3` repository guard, local app/edge image build, app `linux/amd64` non-root user `1654`, `caddy fmt` ve `caddy validate` geçti. Gerçek registry digest'i workflow çalıştırılana, Ubuntu sunucu/DNS kanıtı hedef kuruluma kadar ertelendi |
| `F1-EV-020` Windows VPS taşınabilir kurulum (`2026-08-02`) | HISTORICAL_SUPERSEDED_V3_3 | Eski PowerShell/WSL hedef akışı v3.3 ve ADR-012 ile yürürlükten kaldırıldı; üretim kurulumunda kullanılmaz. |
| `F1-EV-021` Ubuntu Server taşınabilir kurulum (`2026-08-02`) | HISTORICAL_SUPERSEDED_V3_4 | v3.3 Ubuntu 24.04/4 vCPU/100–120 GB host kapıları v3.4 ve ADR-013 ile değiştirilmiştir; immutable image, HTTPS, secret-file ve fail-closed dağıtım sözleşmeleri korunur. |
| `F1-EV-022` AWS Ubuntu host/runtime (`2026-08-02`) | HOST_RUNTIME_REBOOT_VOLUME_PASS / SUPERSEDED_BY_F1_EV_023 | v3.4 PDF 77 sayfa ve SHA-256 `5A652A...0EC51`; gerçek host Ubuntu 26.04 LTS x86_64, 2 vCPU, 8.153.141.248 byte RAM, 80.530.636.800 byte NVMe aygıt ve 76.878.503.936 byte root filesystem. Docker Engine/CLI `29.7.1`, containerd `2.2.6`, Buildx `0.36.0`, systemd enabled/active ve exact Compose `2.40.2` geçti. Kontrollü host reboot sonrası digest-pinned test container otomatik başladı ve volume marker SHA-256 `5d6ae2...e49` değişmedi; test kaynakları temizlendi. Sonraki production image, DNS/TLS ve restore kanıtı `F1-EV-023` içinde tamamlandı; x5 yük ayrı blocker olarak açıktır. |
| `F1-EV-023` AWS production deployment (`2026-08-03`) | PASS_TARGET / USER_AND_OFF_HOST_GATES_PENDING | Production image kaynak commit'i `6332c26`, deployment düzeltme commit'i `274ad4d`; kanıt güncellemesinden önce sunucu/GitHub/yerel dokümantasyon HEAD'i `d33c258`; app `sha256:ccc100...39c5`, edge `sha256:29c73d...27be`. PostgreSQL/API/Caddy healthy, Worker running, migration exit `0`; gerçek Let's Encrypt sertifikası ve yenileme kaydı alındı; public SPA ve `/health/ready` HTTP `200`, HTTP→HTTPS `308`, HSTS/CSP/frame/content-type/referrer başlıkları geçti. Production backup `20260802T223309Z` dış ağa kapalı boş PostgreSQL 18.4 ve geçici private/DP volume'larına `3 sn` içinde restore edildi; checksum, migration `5`, bootstrap `1`, Owner membership `1` ve iki DP key doğrulandı. Gerçek host reboot sonrası Docker enabled/active ve dört kalıcı servis otomatik döndü; readiness yeniden `200`; API/Worker `Error/Fatal=0`, beklenmeyen container restart `0`. Güncel solution build `0` uyarı/`0` hata ve test seti `111/111` geçti. `external-writes=false` korunur. Boş izlenmeyen sunucu dosyası kullanıcı onayıyla kaldırıldı ve çalışma ağacı temizlendi. Yönetici login smoke, şifreli off-host kopya ve x5 kapasite bekler. |

## Güvenlik bulguları

- Transitive `Microsoft.OpenApi 2.0.0` yüksek önem advisory'si restore sırasında fail-closed yakalandı; şartname F1'de OpenAPI artefaktını zorunlu kılmadığından ilgili package ve runtime yüzeyi kaldırıldı.
- 2026-08-02 production `npm audit`, şartnamenin bağladığı React Router `7.18.2` için `GHSA-qwww-vcr4-c8h2` RSC action CSRF advisory'sini tek yüksek bulgu olarak bildirir; registry yalnız breaking `8.3.0` düzeltmesi önerir. Bu web image'ı yalnız client-side `BrowserRouter` kullanır; RSC/server action/SSR yoktur ve bütün değiştiren API istekleri same-origin + custom CSRF doğrulamasından geçer. Major 8'e geçmek şartname teknoloji kararını değiştireceği için yapılmadı. Risk `RISK-F1-SEC-001` olarak açık ve upgrade kapısı yeni güvenli 7.x release veya yetkili mimari karardır.
- 2026-08-02 NuGet transitive vulnerability taraması 11/11 projede temiz geçti.
- PILOT_LOCAL key ring persistent fakat PFX ile at-rest encrypted değildir. Production mode certificate/path/password yoksa uygulama fail-closed olur.

## Yerel/production ayrımı

F1 yerel uygulama ve AWS production runtime'ı deployment açısından hazırdır. Host profili, SSH, Docker/Compose, immutable registry digestleri, production PFX, public TLS, reboot/volume kalıcılığı ve hedef restore/RTO kanıtlanmıştır. Yönetici login smoke, şifreli off-host backup aktarımı ve x5 kapasite kanıtı tamamlanana kadar tam production kabulü `BLOCKED_EXTERNAL` kalır; dış platform yazmaları kapalıdır.
## 2026-08-05 production sertleştirme v7

| Kanıt | Sonuç | Ölçüm |
| --- | --- | --- |
| `F1-EV-024` job retry ve operatör takip modeli | CODED_STATIC_VERIFIED / DYNAMIC_NOT_RUN | Typed job sonucu, transient backoff, `MANUAL_REVIEW`, max-attempt `DEAD`, tenant-scope liste/ayrıntı/retry/cancel API ve panel ekranı eklendi. Exact .NET/PostgreSQL testleri bu ortamda çalıştırılmadı. |
| `F1-EV-025` kimlik ve yetki sertleştirmesi | CODED_STATIC_VERIFIED / DYNAMIC_NOT_RUN | MFA için parola + ikinci faktörlü reauthentication, `ReauthenticatedAt`, rol bazlı write sınırı, CSRF token yenileme ve idempotency expiry temizliği eklendi. Migration/build/integration testleri bekliyor. |
| `F1-EV-026` CI, bootstrap ve deployment sağlık kapıları | CODED_STATIC_VERIFIED / WORKFLOW_DOCKER_NOT_RUN | PR/push verify workflow'u, Git-base belge transaction kontrolü, one-shot bootstrap secret, Worker heartbeat ve frontend/API smoke eklendi. GitHub Actions ve Docker Compose koşusu bekliyor. |
| `F1-EV-027` CI trigger tekrarı azaltma (`2026-08-11`) | WORKFLOW_REMEDIATION_PENDING | Kaynak doğrulaması yalnız PR ve `main` push'larında çalışacak şekilde daraltıldı; .NET/web doğrulama setine Playwright E2E ana kapıda eklendi. `release-*`/manuel immutable publish, `main` erişilebilirliği ve aynı SHA için başarılı `Verify source changes` check kaydını GitHub Checks API üzerinden fail-closed doğrular. İmaj build/push, provenance/SBOM, digest kaydı ve iptal edilmeyen release concurrency korunur. İlk GitHub koşusu, eski guard testinin release içindeki kaldırılmış yinelenen doğrulama komutlarını beklemesi nedeniyle başarısız oldu; guard yeni source-gate sözleşmesine güncellendi, yeniden koşu bekliyor. |

## 2026-08-09 — v10.20 MFA ve oturum yönetimi operatör ekranı

| Kanıt | Durum | Not |
| --- | --- | --- |
| Authenticator kurulum akışı | PASS_LOCAL_WEB | Mevcut parola ile `/reauthenticate`, ardından `/mfa/setup` QR verisi ve `/mfa/confirm` kurtarma kodları çalışan bileşen testiyle doğrulandı. |
| Oturum sonlandırma | PASS_LOCAL_WEB | Mevcut oturum korunur; diğer oturum için teyitli tekil revoke ve toplu revoke denetimleri mevcut server-side session API'lerine bağlıdır. |
| Dinamik backend/PostgreSQL suite | BLOCKED_ENVIRONMENT | Yerel makinede Docker engine yok; Testcontainers suite başarılı sayılmadı ve full CI kanıtı beklenir. |
## 2026-08-12 — v10.58 immutable deployment

- `a95909d` source CI `31611581747` ve immutable publish `31612027079` `PASS`.
- Backup `20260812T152558Z`: database/private volume checksum ve `pg_restore --list` `PASS`; rollback kopyası `deploy/backups/20260812T152558Z-v10.58` içinde yeniden checksum doğrulandı.
- App `sha256:be4ff60e41aaf675154711612c2e20d8e841fe026f2eaf8c458da3228d846e33`, edge `sha256:a6c27ff3fc76cc69a5508b11cce77dcb30ad540cf9631a0283c202be53e0c6d7`; migration exit `0`, API/Worker/Caddy/PostgreSQL healthy, dış readiness `200`.
- Şifreli off-host aktarım ayrı dış kapı olarak kalır; Production write güvenlikleri değişmedi.
