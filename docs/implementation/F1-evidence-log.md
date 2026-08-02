# F1 Kanıt Günlüğü

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
| `F1-EV-023` AWS production deployment (`2026-08-03`) | PASS_TARGET / USER_AND_OFF_HOST_GATES_PENDING | Sunucu, GitHub ve yerel HEAD `274ad4d`; app `sha256:ccc100...39c5`, edge `sha256:29c73d...27be`. PostgreSQL/API/Caddy healthy, Worker running, migration exit `0`; gerçek Let's Encrypt sertifikası ve yenileme kaydı alındı; public SPA ve `/health/ready` HTTP `200`, HTTP→HTTPS `308`, HSTS/CSP/frame/content-type/referrer başlıkları geçti. Production backup `20260802T223309Z` dış ağa kapalı boş PostgreSQL 18.4 ve geçici private/DP volume'larına `3 sn` içinde restore edildi; checksum, migration `5`, bootstrap `1`, Owner membership `1` ve iki DP key doğrulandı. Gerçek host reboot sonrası Docker enabled/active ve dört kalıcı servis otomatik döndü; readiness yeniden `200`; API/Worker `Error/Fatal=0`, beklenmeyen container restart `0`. Güncel solution build `0` uyarı/`0` hata ve test seti `111/111` geçti. `external-writes=false` korunur. Yönetici login smoke, şifreli off-host kopya, x5 kapasite ve sunucudaki boş izlenmeyen dosyanın kullanıcı onaylı temizliği bekler. |

## Güvenlik bulguları

- Transitive `Microsoft.OpenApi 2.0.0` yüksek önem advisory'si restore sırasında fail-closed yakalandı; şartname F1'de OpenAPI artefaktını zorunlu kılmadığından ilgili package ve runtime yüzeyi kaldırıldı.
- 2026-08-02 production `npm audit`, şartnamenin bağladığı React Router `7.18.2` için `GHSA-qwww-vcr4-c8h2` RSC action CSRF advisory'sini tek yüksek bulgu olarak bildirir; registry yalnız breaking `8.3.0` düzeltmesi önerir. Bu web image'ı yalnız client-side `BrowserRouter` kullanır; RSC/server action/SSR yoktur ve bütün değiştiren API istekleri same-origin + custom CSRF doğrulamasından geçer. Major 8'e geçmek şartname teknoloji kararını değiştireceği için yapılmadı. Risk `RISK-F1-SEC-001` olarak açık ve upgrade kapısı yeni güvenli 7.x release veya yetkili mimari karardır.
- 2026-08-02 NuGet transitive vulnerability taraması 11/11 projede temiz geçti.
- PILOT_LOCAL key ring persistent fakat PFX ile at-rest encrypted değildir. Production mode certificate/path/password yoksa uygulama fail-closed olur.

## Yerel/production ayrımı

F1 yerel uygulama ve AWS production runtime'ı deployment açısından hazırdır. Host profili, SSH, Docker/Compose, immutable registry digestleri, production PFX, public TLS, reboot/volume kalıcılığı ve hedef restore/RTO kanıtlanmıştır. Yönetici login smoke, şifreli off-host backup aktarımı ve x5 kapasite kanıtı tamamlanana kadar tam production kabulü `BLOCKED_EXTERNAL` kalır; dış platform yazmaları kapalıdır.
