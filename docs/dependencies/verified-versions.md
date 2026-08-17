# Doğrulanmış Teknoloji Sürümleri

Doğrulama tarihi: 2026-07-31. Durum gözden geçirme tarihi: 2026-08-02. v3.4 ile dağıtım hedefi ölçülen AWS Ubuntu Server 26.04 LTS x86_64 hostuna revize edilmiştir; uygulama major/minor kararları değiştirilmemiştir. Docker'ın resmî Ubuntu belgesi Resolute 26.04 LTS'yi desteklenen hedef olarak listeler. Exact patch değerleri F0 doğrulama lock'ları, repository-root F1+ lock'ları ve resmî registry index digest'leriyle sabitlenir; `latest` ve floating image kabul edilmez.

## Yerel araç kanıtı

| Araç | Yerel sonuç | Durum |
| --- | --- | --- |
| .NET SDK | `10.0.302` | INSTALLED |
| .NET / ASP.NET Core runtime | `10.0.10` | INSTALLED |
| Node.js | `24.15.0` | INSTALLED; current LTS patch adayıyla aynı değil |
| npm | `11.12.1` | INSTALLED |
| Docker Desktop / Engine / CLI | Desktop `4.84.0` (`234817`), Engine/CLI `29.6.2`; Linux/amd64, `overlayfs`, `desktop-linux` | VERIFIED_LOCAL_HISTORICAL; TARGET_SUPERSEDED |
| Docker Compose | Hedefte root plugin `v2.40.2`; SHA-256 `6c964d9655cd629ef43c5dc75d9612c2da319237debee54a7aef217e9f362b88` | VERIFIED_TARGET_2026_08_02 |
| Caddy | Digest-pinned `2.11.3`, Linux/amd64 smoke geçti | VERIFIED_LOCAL_RECHECK_TARGET |
| PostgreSQL / psql | Digest-pinned `18.4`; psql, dump ve temiz-volume restore geçti | VERIFIED_LOCAL_RECHECK_TARGET |
| WSL | `2.7.11.0`, kernel `6.18.33.2-2`, varsayılan sürüm `2` | VERIFIED_LOCAL_HISTORICAL; NOT_APPLICABLE_TARGET |

## AWS Ubuntu production hedefi

| Bileşen | Bağlayıcı hedef | Durum |
| --- | --- | --- |
| İşletim sistemi | Ubuntu Server 26.04 LTS (Resolute) x86_64 | VERIFIED_TARGET_HOST |
| Kapasite | 2 vCPU; 8.153.141.248 byte RAM; 80.530.636.800 byte NVMe | VERIFIED_TARGET_HOST / PERFORMANCE_PENDING |
| Container runtime | Docker Engine/CLI `29.7.1`; containerd `2.2.6`; Buildx `0.36.0`; Linux/x86_64 `overlayfs`; systemd enabled/active | VERIFIED_TARGET_2026_08_02 |
| Compose | Docker paket bağımlılığı `5.3.1`; proje tarafından seçilen exact root plugin `v2.40.2`, Linux x86_64 SHA-256 `6c964d9655cd629ef43c5dc75d9612c2da319237debee54a7aef217e9f362b88` | VERIFIED_TARGET_2026_08_02 |
| Host yönetimi | SSH anahtarı + yönetici IP/VPN allow-list | SSH_KEY_VERIFIED / AWS_SECURITY_GROUP_REVIEW_PENDING |

## Backend ve altyapı

| Bileşen | Bağlayıcı hat | Exact aday | Resmî kaynak | Destek notu | Lock/digest | Durum |
| --- | --- | --- | --- | --- | --- | --- |
| .NET SDK | 10 / C# 14 | `10.0.302` | <https://dotnet.microsoft.com/en-us/download/dotnet/10.0> | .NET 10 LTS; EOL 2028-11-14 | F0 `verification/global.json`; SDK index digest kayıtlı | VERIFIED_LOCK |
| .NET runtime | 10 | `10.0.10` | <https://dotnet.microsoft.com/en-us/download/dotnet/10.0> | 2026-07-14 patch | ASP.NET runtime index digest kayıtlı | VERIFIED_DIGEST |
| EF Core | 10 | `10.0.10` | <https://www.nuget.org/packages/Microsoft.EntityFrameworkCore/10.0.10> | .NET 10 hattı | F0 NuGet central manifest + lock | VERIFIED_LOCK |
| Npgsql | 10 | `10.0.3` | <https://www.nuget.org/packages/Npgsql/10.0.3> | 10.x hattı | F0 NuGet central manifest + lock | VERIFIED_LOCK |
| Npgsql EF provider | 10 | `10.0.3` | <https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/10.0.3> | Lock, EF Core aralığını `[10.0.4, 11.0.0)` doğruladı | F0 NuGet central manifest + lock | VERIFIED_LOCK |
| PostgreSQL | 18 | `18.4` | <https://www.postgresql.org/docs/18/> | Desteklenen 18.x | Multi-platform index digest kayıtlı | VERIFIED_DIGEST |
| Caddy | 2.11 | `2.11.3` | <https://github.com/caddyserver/caddy/releases/tag/v2.11.3> | Resmî release | Multi-platform index digest kayıtlı | VERIFIED_DIGEST |
| Compose CLI | v2 | `2.40.2` | <https://github.com/docker/compose/releases/tag/v2.40.2> / <https://docs.docker.com/compose/intro/history/> | Resmî Docker kaynağı v2 ve v5'i birlikte desteklenen CLI hatları olarak tanımlar; şartname gereği v2 korunur | Windows/Linux x86_64/ARM64 checksum kayıtlı | VERIFIED_CHECKSUM |
| GitHub checkout Action | 6 | `6.1.0` / `d23441a48e516b6c34aea4fa41551a30e30af803` | <https://github.com/actions/checkout/releases/tag/v6.1.0> | Release workflow yalnız tam commit SHA kullanır | `.github/workflows/publish-release-images.yml` | VERIFIED_WORKFLOW_PIN |
| GitHub Setup .NET Action | 6 | `6.0.0` / `a98b56852c35b8e3190ac28c8c2271da59106c68` | <https://github.com/actions/setup-dotnet/releases/tag/v6.0.0> | Action tam commit SHA; SDK girdisi `10.0.302` | `.github/workflows/verify.yml` | VERIFIED_WORKFLOW_PIN |
| GitHub Setup Node Action | 6 | `6.4.0` / `48b55a011bda9f5d6aeb4c2d9c7362e8dae4041e` | <https://github.com/actions/setup-node/releases/tag/v6.4.0> | Action tam commit SHA; Node girdisi `24.18.1`; otomatik package-manager cache kapalı | `.github/workflows/verify.yml` | VERIFIED_WORKFLOW_PIN |
| Docker Buildx | 0 | `v0.34.1`; SHA-256 `f1332ddb9010bd0b72628266c3a906d9a6979848033df4c8d9bd2cd113bae12b` | <https://github.com/docker/buildx/releases/tag/v0.34.1> | Official release binary bounded retry ile indirilir ve checksum doğrulanmadan builder oluşturulmaz; GitHub raw manifest 429 bağımlılığı yoktur | `.github/workflows/publish-release-images.yml` | VERIFIED_WORKFLOW_PIN |
| Serilog.AspNetCore | 10 | `10.0.0` | <https://www.nuget.org/packages/Serilog.AspNetCore/10.0.0> | Şartname hattı | F0 NuGet central manifest + lock | VERIFIED_LOCK |
| HTTP Resilience | 10 | `10.8.0` | <https://www.nuget.org/packages/Microsoft.Extensions.Http.Resilience/10.8.0> | Şartname hattı | F0 NuGet central manifest + lock | VERIFIED_LOCK |
| xUnit v3 | 3 | `3.2.2` | <https://www.nuget.org/packages/xunit.v3/3.2.2> | Test hattı | F0 NuGet central manifest + lock | VERIFIED_LOCK |
| Testcontainers PostgreSQL | 4 | `4.13.0` | <https://www.nuget.org/packages/Testcontainers.PostgreSql/4.13.0> | Test hattı | F0 NuGet central manifest + lock | VERIFIED_LOCK |
| ASP.NET Identity EF | 10 | `10.0.10` | <https://www.nuget.org/packages/Microsoft.AspNetCore.Identity.EntityFrameworkCore/10.0.10> | F1 IAM persistence | Root central manifest + project lock | VERIFIED_F1_LOCK |
| EF Design / dotnet-ef | 10 | `10.0.10` | <https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Design/10.0.10> | Migration design/tool zinciri | Root central manifest + tool manifest + lock | VERIFIED_F1_LOCK |
| QRCoder | 1 | `1.8.0` | <https://www.nuget.org/packages/QRCoder/1.8.0> | Google Authenticator uyumlu QR SVG | Root central manifest + lock | VERIFIED_F1_LOCK |
| Serilog Console / Compact | 6 / 3 | `6.1.1` / `3.0.0` | <https://www.nuget.org/packages/Serilog.Sinks.Console/6.1.1> / <https://www.nuget.org/packages/Serilog.Formatting.Compact/3.0.0> | JSON console log | Root central manifest + lock | VERIFIED_F1_LOCK |

## Web ve test araçları

| Bileşen | Bağlayıcı hat | Exact aday | Resmî kaynak | Lock/integrity | Durum |
| --- | --- | --- | --- | --- | --- |
| Node.js | 24 LTS | `24.18.1` | <https://nodejs.org/dist/latest-v24.x/> | NPM engine pin + Node index digest; yerel 24.15.0 nedeniyle engine warning gözlendi | VERIFIED_LOCK_TARGET_RUNTIME_PENDING |
| React / React DOM | 19.2 | `19.2.8` | <https://www.npmjs.com/package/react/v/19.2.8> / <https://www.npmjs.com/package/react-dom/v/19.2.8> | F0 npm lock + integrity | VERIFIED_LOCK |
| TypeScript | 6 | `6.0.3` | <https://www.npmjs.com/package/typescript/v/6.0.3> | F0 npm lock + integrity | VERIFIED_LOCK |
| Vite | 8.1 | `8.1.5` | <https://www.npmjs.com/package/vite/v/8.1.5> | F0 npm lock + integrity | VERIFIED_LOCK |
| React Router | 7 | `7.18.2` | <https://www.npmjs.com/package/react-router/v/7.18.2> | F0 npm lock + integrity | VERIFIED_LOCK |
| TanStack Query | 5 | `5.101.4` | <https://www.npmjs.com/package/@tanstack/react-query/v/5.101.4> | F0 npm lock + integrity | VERIFIED_LOCK |
| TanStack Table | 8 | `8.21.3` | <https://www.npmjs.com/package/@tanstack/react-table/v/8.21.3> | F0 npm lock + integrity | VERIFIED_LOCK |
| React Hook Form | 7 | `7.83.0` | <https://www.npmjs.com/package/react-hook-form/v/7.83.0> | F0 npm lock + integrity | VERIFIED_LOCK |
| Zod | 4 | `4.4.3` | <https://www.npmjs.com/package/zod/v/4.4.3> | F0 npm lock + integrity | VERIFIED_LOCK |
| Tailwind CSS / Vite plugin | 4.3 | `4.3.3` | <https://www.npmjs.com/package/tailwindcss/v/4.3.3> / <https://www.npmjs.com/package/@tailwindcss/vite/v/4.3.3> | F0 npm lock + integrity | VERIFIED_LOCK |
| Playwright | 1 | `1.62.1` | <https://www.npmjs.com/package/@playwright/test/v/1.62.1> | F0 npm lock + integrity | VERIFIED_LOCK |
| Vitest / Testing Library / jsdom | 4 / 16 / 30 | `4.1.10` / `16.3.2` / `30.0.1` | Resmî npm package kayıtları | F1 web component test | VERIFIED_F1_LOCK |
| React/Vite types and plugin | 19 / 6 | `@types/react 19.2.18`, `@types/react-dom 19.2.4`, `@vitejs/plugin-react 6.0.5` | Resmî npm package kayıtları | F1 web build | VERIFIED_F1_LOCK |

## Uyumluluk, destek/EOL ve bakım kaydı

| Küme | Doğrulanan uyumluluk | Destek/EOL | Lisans/bakım kapanışı | Durum |
| --- | --- | --- | --- | --- |
| .NET / C# / EF Core | .NET 10, C# 14 ve EF Core 10 aynı bağlayıcı hat; locked restore geçti | .NET 10 LTS EOL: 2028-11-14 | NuGet resolved tree/content hash lock'ta | VERIFIED_F0 |
| Npgsql / PostgreSQL | Npgsql/EF provider 10 ve EF 10.0.10 aralığı locked restore ile uyumlu; PostgreSQL 18 index pinli | PostgreSQL 18 resmî dokümanda supported; bu kayıtta exact EOL kanıtı yok | NuGet content hash ve image digest kayıtlı | VERIFIED_F0 |
| Node / Vite / Playwright | NPM locked dry-run geçti; Node 24.18.1 engine pinli | Node 24 LTS takvimi resmî release sayfasında; bağımsız npm paketleri formal EOL yayımlamıyor | Lock license/integrity alanları mevcut; hedef runtime bekliyor | VERIFIED_F0 |
| React ekosistemi | React 19.2 ve şartnamedeki package hatları aynı npm lock'ta çözümlendi | Cited package kaynaklarında formal EOL yok: `UNKNOWN` | Peer dependency, license ve integrity resolved tree'de | VERIFIED_F0 |
| Caddy / Compose / container images | Caddy index digest ve Compose v2.40.2 dört platform checksum'u kayıtlı; hedef Compose doğrulandı | Caddy release ve Compose v2 desteği doğrulandı | Production app/edge child digest testi `BLOCK-HOST-001` kapsamında | VERIFIED_F0_TARGET_COMPOSE |

## F0 kanıt konumları ve repository-root aktarımı

- SDK: `docs/dependencies/verification/global.json`.
- NuGet: `docs/dependencies/verification/nuget/Directory.Packages.props` ve `packages.lock.json`; locked restore geçti.
- NPM: `docs/dependencies/verification/npm/package.json` ve `package-lock.json`; locked dry-run geçti.
- Images/Compose: `docs/dependencies/verification/container-image-digests.md`.
- Aynı seçimler F1 ile repository-root ve production proje konumlarına aktarılmış, yeniden lock edilmiştir; sonraki fazlarda fark varsa fail-closed durulur.

## Sonuç

Kullanıcı faz sınırı onayıyla non-production F0 doğrulama lock'ları oluşturulmuş, NuGet locked restore ve npm locked dry-run başarıyla çalışmış, resmî image index digest'leri ile Compose checksum'ları kaydedilmiştir. Bu belge ve kanıtlar baseline commit `00c7b78591f158babb040070bf0aa0f04acace8e` ile Git'e alınmış; repository-root lock ve yerel application image kanıtları F1 ile eklenmiştir. AWS Ubuntu hostunda Docker Engine/systemd, exact Compose, kontrollü reboot ve named-volume checksum kalıcılığı doğrulanmıştır. `BLOCK-VERSION-001` ve `F0-EXIT-004` F0 kapsamında kapalıdır; registry-pushed production digest ile restore/DNS-TLS/yük kanıtları `BLOCKED_EXTERNAL` kalır.
