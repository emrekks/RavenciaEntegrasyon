# F1 - Güvenli Temel ve Çalıştırılabilir Sistem Planı

## Belge durumu

| Alan | Değer |
| --- | --- |
| Faz | `F1` |
| Durum | `READY_LOCAL_DEPLOY_PREPARED`; production/Ubuntu Server kabulü `BLOCKED_EXTERNAL` |
| Yetkili şartname | Repository kökü `Ravencia_Entegrasyon_v3_4_Nihai_Uygulama_Surumu.pdf`, v3.4, 77 sayfa; v3.3/v3.2 tarihsel |
| Şartname SHA-256 | v3.4 `5A652AC34574A3310B844AECE647B96D350DD7AA79FDF3AC54C080827150EC51` |
| Kaynak sayfalar | 5-6, 11-19, 23-24, 34-36, 39-41, 48-59, 61-62 |
| Onay kaydı | Kullanıcı 2026-07-31 tarihinde F1 başlangıcını açıkça onayladı. |
| F0 yerel runtime kapısı | `READY_HISTORICAL`; hedef Ubuntu Server kanıtı production kapısına ertelendi. |

## Hedefler

F1'in hedefi migration uygulanabilir, gözlemlenebilir, yedeklenebilir ve dokümante komutla ayağa kalkan güvenli temel sistemi kurmaktır:

- Yetkili solution/repository yapısı, bağımlılık yönü korumaları, merkezi build/package ayarları ve lockfile'lar.
- Ayrı API ve Worker süreçleri, tek AppDbContext ve PostgreSQL 18 üzerinde tek migration zinciri.
- Schema migration'dan ayrı, advisory-lock korumalı, idempotent ve fail-closed initial Owner bootstrap'ı.
- ASP.NET Identity parola/lockout tabanı; custom server-side session, secure cookie, CSRF, Origin ve rate-limit kontrolleri.
- PasswordChangeOnly ve MFA_CHALLENGE kısıtlı session durumları; tenant/OWNER/business claim'lerinin yalnız ACTIVE session'da bulunması.
- Başlangıçta kapalı, isteğe bağlı ve replay-aware TOTP; süreli pending enrollment; atomik, tek kullanımlık recovery code akışı.
- IAM, integration job/inbox ve ops/file/audit/issue/feature-flag başlangıç şemaları.
- Private local file storage, Data Protection key ring, credential/TOTP encryption ve `_FILE` secret desteği.
- ProblemDetails, correlation, JSON logging, live/ready health ve güvenli browser/proxy başlıkları.
- Caddy, API, Worker, PostgreSQL, migrate ve backup Compose topolojisi.
- Stitch olmadan işlevsel ve erişilebilir `/login`, `/dashboard` ve `/settings/security` paneli.

## Kapsam dışı

- F2 ürün, varyant, kategori, marka, attribute, mapping, import, offer, inventory ve bunların API/UI'ları.
- F3+ gerçek platform adaptörü, endpoint, credential kullanımı, webhook davranışı veya dış read/write çağrısı.
- F7B kullanıcı/RBAC/impersonation; F8 aktif multi-tenant, tenant CRUD/switcher/kota/RLS.
- `/products`, `/catalog`, `/imports`, `/inventory`, `/orders`, `/shipments`, `/returns`, `/invoices`, `/integrations`, `/mappings`, `/reports`, `/tenants`, `/users` veya `/roles` için placeholder route/controller/menü.
- Mikroservis, Redis, RabbitMQ, Kafka, Kubernetes, ikinci ORM, generic repository, MediatR veya AutoMapper.
- Production deploy, gerçek secret rotasyonu, gerçek platform yazması veya hedef Ubuntu Server kabulü.

## Gereksinim matrisi

| Kimlik | Kaynak bölümü | Kabul ölçütü | Planlanan kanıt | Dosya/modül | Dış bağımlılık | Durum |
| --- | --- | --- | --- | --- | --- | --- |
| `F1-REQ-001` | Tablo 06-09; F1 teslimatlar | Yetkili solution/proje/test/deploy yapısı ve bağımlılık yönleri vardır; nullable ve warnings-as-errors açıktır. | Restore/build, project-reference guard testi | Root, `src/`, `tests/`, `deploy/` | Yok | DONE |
| `F1-REQ-002` | Tablo 06; F1 teslimatlar | Exact SDK/NuGet/NPM/container seçimleri production lock/digest konumlarına aktarılmıştır; floating/latest yoktur. | Locked restore, npm ci, digest/config kontrolü | Root lock/config dosyaları, manuel GHCR yayın workflow'u | Workflow çalıştırması ve registry digest'i production release'te | DONE_LOCAL_DEPLOY_PREPARED |
| `F1-REQ-003` | Tablo 12; Tablo 37; F1 teslimatlar | Migration gerçek kullanıcı/parola üretmez; bootstrap tek Tenant, RavenciaAdmin, OWNER membership ve marker'ı tek transaction'da oluşturur. | Fresh DB, repeat ve concurrent bootstrap integration testleri | IAM, Persistence, bootstrap CLI | PostgreSQL 18 | DONE |
| `F1-REQ-004` | Sayfa 49-50; Tablo 38 | Read-only secret file, PILOT_LOCAL/public ayrımı, PasswordChangeOnly allowlist, zorunlu parola değişimi ve session rotation uygulanır. | Auth/API integration testleri | Identity/Application/Api | Secret file test fixture | DONE_LOCAL |
| `F1-REQ-005` | Tablo 12, 26, 39; sayfa 50-51 | TOTP varsayılan kapalı; 10 dk pending; setup/confirm/challenge/disable ve ±1 skew/replay kuralları uygulanır. | Unit + PostgreSQL/API integration testleri | IAM security + auth endpoints | Yok | DONE_LOCAL |
| `F1-REQ-006` | Sayfa 48, 51; F1 teslimatlar | 10 recovery code yalnız bir kez gösterilir, salted/keyed digest ile saklanır, atomik tüketilir ve regenerate eski batch'i iptal eder. | Parallel-consume ve regenerate testleri | IAM security | Yok | DONE_LOCAL |
| `F1-REQ-007` | Tablo 12, 26 | Server-side session list/revoke/revoke-others; parola/MFA değişikliğinde session_version ve revoke etkisi anlıktır. | Session integration testleri | Identity/Application/Api | Yok | DONE_LOCAL |
| `F1-REQ-008` | Sayfa 51; F1 teslimatlar | Yetkili OS bağlamlı `identity reset-mfa` CLI reason ve audit ister; remote endpoint yoktur, secret basmaz. | CLI unit/integration testi ve runbook | Api command mode, runbook | Hedef OS kullanıcı yapılandırması production'da | DONE_LOCAL |
| `F1-REQ-009` | Tablo 10, 12, 14, 19 | Tenant context server-side çözülür; composite tenant guard'ları ve cross-tenant read/write/file/job reddi vardır. | PostgreSQL/API integration testleri | Domain/Application/Persistence | PostgreSQL 18 | DONE_F1 |
| `F1-REQ-010` | Tablo 14; job algoritması; F1 teslimatlar | Job, attempt, inbox ve external-effect tekillikleri; lease/reaper/heartbeat ve stale-token koruması uygulanır. | Duplicate/parallel lease/worker-kill testleri | Infrastructure Jobs + Worker | PostgreSQL 18 | DONE_LOCAL_POSTGRES_HEARTBEAT |
| `F1-REQ-011` | Tablo 19; F1 teslimatlar | OperationalIssue dedupe, append-only Audit ve güvenli-kapalı feature flag kayıtları vardır. | Persistence testleri | Operations/Persistence | PostgreSQL 18 | DONE |
| `F1-REQ-012` | Tablo 19; sayfa 47-54 | Private IFileStorage yalnız safe relative path kullanır; MIME/size/traversal ve tenant guard'ı uygular. | Unit + integration security testleri | Files/Application/Infrastructure | Yerel volume | DONE |
| `F1-REQ-013` | Sayfa 51-54 | Persistent Data Protection key ring, protected TOTP/credential material, `_FILE` secrets ve log/API redaction vardır. | Restart/rotation/leak testleri | Security/Infrastructure | Production PFX hedef deploy'da | DONE_LOCAL |
| `F1-REQ-014` | Tablo 25-26, 40 | ProblemDetails, correlation, JSON log, secure headers, same-origin cookie, CSRF, Origin, lockout ve rate limit tabanı uygulanır. | API integration/security tests | Api middleware/endpoints | Yok | DONE_LOCAL |
| `F1-REQ-015` | Tablo 41-43 | Caddy/API/Worker/PostgreSQL/migrate/backup servisleri; explicit health/restart/volume/log politikaları ve yalnız Caddy host portları vardır. | Exact Compose v2 config + container smoke | `deploy/` | Docker runtime | DONE_LOCAL |
| `F1-REQ-016` | Tablo 31; F1 teslimatlar | Auth/security paneli loading/error/locked/password-only/MFA/recovery/session durumlarıyla işlevsel ve erişilebilirdir. | Typecheck, unit/component, production build, Playwright smoke | `MarketplaceHub.Web` | Stitch engelleyici değil | DONE_LOCAL |
| `F1-REQ-017` | Sayfa 55-56; F1 teslimatlar | Backup/restore/deploy/rollback ve break-glass runbook'ları gerçek komut/sınırlarla vardır. | Doküman ve script guard testleri | `docs/runbooks/`, `deploy/backup/` | Hedef Ubuntu Server production kanıtı sonra | DONE_LOCAL |

## Dosya etkisi

Oluşturulacak ana yollar şartnamedeki isimlerle sınırlıdır:

- Root: `MarketplaceHub.sln`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `.env.example`, `README.md`.
- Production projeleri: `src/MarketplaceHub.Domain`, `MarketplaceHub.Application`, `MarketplaceHub.Infrastructure`, `MarketplaceHub.Api`, `MarketplaceHub.Worker`, `MarketplaceHub.Web`.
- Test projeleri: `tests/MarketplaceHub.Domain.Tests`, `MarketplaceHub.Application.Tests`, `MarketplaceHub.Persistence.IntegrationTests`, `MarketplaceHub.Api.IntegrationTests`, `MarketplaceHub.Adapters.ContractTests`, `MarketplaceHub.EndToEnd.Tests`.
- Deployment: `deploy/compose/compose.yaml`, `compose.production.yaml`, `deploy/caddy/Caddyfile`, `deploy/backup/` ve gerekli Dockerfile'lar.
- Dokümantasyon: bu plan, F1 evidence/traceability güncellemeleri ve F1 runbook'ları.

F2+ modül klasörlerine production entity/use-case/endpoint/route veya placeholder eklenmez. Adapter contract test projesi yalnız dependency boundary ve fake/secret-free fixture standardını doğrular; gerçek platform adaptörü oluşturmaz.

## Teknoloji ve capability kapıları

- .NET/C#/EF/Npgsql/PostgreSQL/Node/React/TypeScript/Vite/Caddy/Compose major-minor kararları değiştirilmez.
- Root lockfile'lar F0 verification setiyle karşılaştırılır; yeni zorunlu paket yalnız resmî kaynağı, exact sürümü ve somut F1 kullanımı kaydedilerek eklenir.
- Docker images tag + immutable digest ile referanslanır; production compose içinde `latest` bulunmaz.
- Capability matrisi değişmez; tüm gerçek platform capability'leri `UNKNOWN`, dış write flag'leri `false` kalır.
- Fake adapter dış HTTP çağrısı yapmaz ve F2+ davranışı üretmez.

## Test ve kanıt planı

| Kanıt kimliği | Komut/senaryo | Beklenen sonuç | Artefakt | Durum |
| --- | --- | --- | --- | --- |
| `F1-EV-001` | `dotnet restore/build/test` locked zinciri | Tüm .NET projeleri warning olmadan geçer | Test çıktısı | PASS |
| `F1-EV-002` | Dependency guard testleri | Yasak proje/package yönü yoktur | Domain/Application tests | PASS |
| `F1-EV-003` | Fresh/repeat/concurrent bootstrap | Tek Tenant/Owner/membership/marker; tekrar no-op; partial state fail-closed | PostgreSQL integration | PASS |
| `F1-EV-004` | Auth/cookie/CSRF/Origin/lockout/register-absence | Yetkisiz ve unsafe akışlar fail-closed | API integration | PASS_LOCAL |
| `F1-EV-005` | PasswordChangeOnly ve password rotation | Allowlist dışı red; eski session/parola geçersiz | API integration | PASS_LOCAL |
| `F1-EV-006` | TOTP pending/confirm/replay/skew/disable/reset | Bütün zorunlu MFA invariants geçer | Unit/API integration | PASS_LOCAL |
| `F1-EV-007` | Recovery parallel consume/regenerate | Tek tüketim; eski batch invalid | PostgreSQL integration | PASS |
| `F1-EV-008` | Tenant A -> B read/write/file/job | F1'de business read/write endpoint'i yok; file ve tenant context fail-closed | Persistence/API integration | PASS_F1 |
| `F1-EV-009` | Duplicate job/parallel lease/stale token/worker kill | Tekillik ve recovery korunur | Persistence integration | PASS |
| `F1-EV-010` | File traversal/MIME/oversize | Güvensiz dosya kabul edilmez | Unit/integration | PASS |
| `F1-EV-011` | `npm ci`, typecheck, unit, build, Playwright | Strict TS, component, production bundle ve Chromium smoke geçer | Web lock/build | PASS_LOCAL |
| `F1-EV-012` | Exact Compose v2 `config` | Altı servis, ağ/port/volume/secret politikası geçerli | Compose çıktısı | PASS |
| `F1-EV-013` | Fresh compose migrate/bootstrap/health/restart | Temiz ortam ayağa kalkar; API/Worker bağımsızdır | Smoke/evidence log | PASS_LOCAL |
| `F1-EV-014` | DB dump checksum + clean restore; file/key volume | Restore ve bütünlük kontrolü geçer | Restore kanıtı | PASS_LOCAL |
| `F1-EV-015` | Kapsam taraması | F2+ endpoint/route/menu/placeholder ve gerçek platform kodu yoktur | `rg`/Git diff | PASS |

## Dış bağımlılıklar, riskler ve blockerlar

| Kimlik | Kayıt | Güvenli davranış / kapanış |
| --- | --- | --- |
| `F1-RISK-HOST-001` | Mevcut AWS Ubuntu 26.04 LTS host profili (2 vCPU, 8 GB RAM sınıfı, 80 GB NVMe sınıfı) doğrulandı; Docker/Compose, x5 yük, reboot/volume/restore kanıtları henüz tamamlanmadı. | Host-only kurulum ve runtime runbook'u yürütülür; production kabulü operasyon kanıtları olmadan verilmez. |
| `F1-RISK-SECRET-001` | Gerçek production bootstrap/certificate/DB secret'ları yoktur. | Yalnız secret-file contract ve sentetik test secret'ı; repo/image/Compose içinde gerçek veya sabit production secret yoktur. |
| `F1-RISK-STITCH-001` | Stitch tasarımı yoktur. | İşlevsel/erişilebilir varsayılan F1 UI; markalı fidelity ertelenir. |
| `F1-RISK-PLATFORM-001` | Platform test hesabı/fixture yoktur. | Capability `UNKNOWN`, dış HTTP ve write kapalı; F1 blocker'ı değildir. |
| `F1-RISK-DR-001` | Hedef DB/files/key-ring restore kanıtı Ubuntu Server'a bağlıdır. | Yerel clean restore yapılır; target RTO ilan edilmez. |
| `RISK-F1-SEC-001` | React Router 7.18.2 için client uygulamanın kullanmadığı RSC action yüzeyinde `GHSA-qwww-vcr4-c8h2` yüksek advisory'si vardır; 2026-08-02 registry düzeltmesi breaking 8.3.0'dır. | RSC/SSR/server action yok; API same-origin+CSRF kapılı. Major 8'e izinsiz geçilmez; güvenli 7.x release veya yetkili mimari karar izlenir. |

## ADR etkisi

F1, ADR-001-010 kararlarını uygular; alternatif mimari seçmez. Yalnız şartnamedeki minimum fiziksel alanlara güvenlik/operasyon için ek kolon gerekirse migration incelemesi ve bu planda gerekçe kaydı kullanılır. Tablo adı, aggregate sınırı, tenant kapsamı, teknoloji hattı veya faz sınırı değiştirilmez.

## Çıkış kriterleri

| Kimlik | Ölçülebilir koşul | Kanıt | Durum |
| --- | --- | --- | --- |
| `F1-EXIT-001` | Temiz ortam dokümante komutla ayağa kalkar; migration ve bootstrap deterministiktir. | Compose/migrate/bootstrap smoke | DONE_LOCAL |
| `F1-EXIT-002` | Başlangıç parolası repo/migration/image/Compose içinde değildir; marker/flag/secret/public-ingress kapıları kanıtlıdır. | Secret scan + bootstrap tests | DONE |
| `F1-EXIT-003` | Auth, PasswordChangeOnly, TOTP, recovery, session ve reset güvenlik testleri geçer. | Unit + API/Persistence integration | DONE_LOCAL |
| `F1-EXIT-004` | Tenant iskeleti, duplicate job, lease ve worker-kill testleri geçer. | PostgreSQL integration | DONE_LOCAL |
| `F1-EXIT-005` | DB/files/key-ring yerel restore kanıtı vardır; hedef production kanıtı açıkça ayrıdır. | Restore smoke + risk kaydı | DONE_LOCAL |
| `F1-EXIT-006` | Build/format/analyzer/unit/integration/frontend/Compose ve kapsam kontrolleri geçer. | F1 evidence log | DONE_LOCAL |

## Sonuç

F1 uygulaması, yerel kanıt seti, ayrı production public-TLS edge tanımı ve immutable image yayın otomasyonu tamamlanmıştır: sonuç `READY_LOCAL_DEPLOY_PREPARED`dır. Hedef Ubuntu Server runtime/systemd/reboot/volume/RTO, production PFX secret, off-host backup hedefi ve workflow çalıştırmasıyla oluşacak registry-pushed immutable application/edge digest'i hedef ortam/release hazır olduğunda kanıtlanacağından production kabulü `BLOCKED_EXTERNAL` kalır. Bu ayrım sonraki faz kapılarını kendiliğinden açmaz.
