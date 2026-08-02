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
| `F1-EV-021` Ubuntu Server taşınabilir kurulum (`2026-08-02`) | PASS_LOCAL_CONFIG / TARGET_DEFERRED | Ubuntu 24.04 LTS x86_64, 4 vCPU, 8 GB RAM, 100–120 GB NVMe, direct Docker Engine/linux-amd64 ve exact Compose v2.40.2 kapıları; immutable app/edge digest, HTTPS origin, bağımsız Data Protection PFX, bootstrap ve secret-file üretimi; fail-closed validate/pull/migrate/bootstrap/readiness akışı eklendi. v3.3 PDF 75 sayfa ve SHA-256 `AB7E...3F94`; üç Bash scripti digest-pinned Linux container içinde `bash -n` kontrolünden; solution 0 warning/error, toplam `110/110` .NET, Web typecheck `1/1` test ve production build kontrolünden geçti. Hedef systemd/reboot/volume/restore kanıtı Ubuntu sunucu kiralandığında çalıştırılacak. |

## Güvenlik bulguları

- Transitive `Microsoft.OpenApi 2.0.0` yüksek önem advisory'si restore sırasında fail-closed yakalandı; şartname F1'de OpenAPI artefaktını zorunlu kılmadığından ilgili package ve runtime yüzeyi kaldırıldı.
- 2026-08-02 production `npm audit`, şartnamenin bağladığı React Router `7.18.2` için `GHSA-qwww-vcr4-c8h2` RSC action CSRF advisory'sini tek yüksek bulgu olarak bildirir; registry yalnız breaking `8.3.0` düzeltmesi önerir. Bu web image'ı yalnız client-side `BrowserRouter` kullanır; RSC/server action/SSR yoktur ve bütün değiştiren API istekleri same-origin + custom CSRF doğrulamasından geçer. Major 8'e geçmek şartname teknoloji kararını değiştireceği için yapılmadı. Risk `RISK-F1-SEC-001` olarak açık ve upgrade kapısı yeni güvenli 7.x release veya yetkili mimari karardır.
- 2026-08-02 NuGet transitive vulnerability taraması 11/11 projede temiz geçti.
- PILOT_LOCAL key ring persistent fakat PFX ile at-rest encrypted değildir. Production mode certificate/path/password yoksa uygulama fail-closed olur.

## Yerel/production ayrımı

F1 yerel uygulama, public-TLS production edge tanımı, immutable image yayın otomasyonu ve fail-closed Ubuntu Server kurulum scriptleri sonucu `READY_LOCAL_DEPLOY_PREPARED`dır. Hedef Ubuntu systemd/reboot/volume/RTO, workflow'dan üretilecek registry-pushed immutable app/edge digest, production PFX secret ve off-host hedef kanıtı hedef ortam hazır olduğunda tamamlanır; production çıkışı o zamana kadar `BLOCKED_EXTERNAL`dır.
