# F0 Kanıt Günlüğü

Doğrulama tarihi: 2026-07-31. Sonuçlar gerçek komut çıktılarından kaydedilmiştir; çalıştırılmayan kontrol geçmiş gösterilmez.

| Kanıt | Komut / yöntem | Gerçek sonuç | Durum |
| --- | --- | --- | --- |
| Canonical şartname | SHA-256, pypdf ve görsel render | v3.4 iki bağlayıcı AWS revizyon sayfası + eksiksiz 75 sayfalık v3.3 tabanı; toplam 77 sayfa; SHA-256 `5A652AC34574A3310B844AECE647B96D350DD7AA79FDF3AC54C080827150EC51` | PASSED_V3_4 |
| NPM lock üretimi | `npm install --package-lock-only --ignore-scripts --no-audit --no-fund` | Exit 0; local Node 24.15.0 hedef engine 24.18.1'den eski olduğu için beklenen `EBADENGINE` uyarısı | PASSED_WITH_ENV_WARNING |
| NPM locked çözüm | `npm ci --ignore-scripts --dry-run --no-audit --no-fund` | Exit 0; 46 resolved package; `node_modules` oluşmadı | PASSED_WITH_ENV_WARNING |
| NPM lock checksum | SHA-256 | `B3D19C0F1D64A6CE2236EB52F2CC48A483729565B002FD7B16535AD24EF3A923` | PASSED |
| NuGet lock üretimi | `dotnet restore F0.DependencyVerification.csproj --use-lock-file` | Sandbox ağ engeli sonrası onaylı resmî NuGet erişimiyle exit 0 | PASSED |
| NuGet locked çözüm | `dotnet restore F0.DependencyVerification.csproj --locked-mode` | Exit 0 | PASSED |
| NuGet lock checksum | SHA-256 | `E8B3D6DB5AADF51E88B945BDF3B9CCC23E1443CB041493C59CB86D80FEED84BF` | PASSED |
| Registry digest | Docker Hub/MCR v2 manifest header | PostgreSQL, Caddy, Node, .NET SDK ve ASP.NET index digest'leri kaydedildi | PASSED |
| Compose checksum | Resmî v2.40.2 `checksums.txt` | Windows/Linux x86_64 ve ARM64 checksum'ları kaydedildi | PASSED |
| Capability matrisi | Satır ve support-level taraması | 42 platform/grup satırı; 0 `SUPPORTED` veri satırı | PASSED |
| F0 kimlikleri | Traceability completeness taraması | 21/21 kimlik mevcut | PASSED |
| Production kapsamı | Repository-root dizin/uzantı taraması | `src`, `tests`, `deploy`, solution, migration, endpoint veya UI yok | PASSED |
| F0 Git baseline | `git commit` | `00c7b78591f158babb040070bf0aa0f04acace8e` | PASSED |
| Yerel host kapasitesi | Windows/CIM incelemesi | Windows 11 Pro build `26200`, 64-bit, `31,1 GiB` RAM; firmware sanallaştırma açık | PASSED_FOR_LOCAL |
| Yerel Docker Desktop | Winget metadata + `docker version` | Docker Desktop `4.84.0` (`234817`), Engine/CLI `29.6.2`; server `linux/amd64`, driver `overlayfs`, context `desktop-linux` | PASSED_FOR_LOCAL |
| Yerel Compose drift kontrolü | Bundled ve exact binary sürümleri | Bundled `v5.3.1` şartname hattı dışı ve kullanıcı plugin'ini yeniden yazıyor; kabul kanıtı sayılmadı | DETECTED_AND_MITIGATED |
| Yerel Compose v2 pin | Resmî release binary + SHA-256 + `version` + `ls` | `v2.40.2`; SHA-256 `1f7f20b91e0564147dc58b3a58a22a8f64a787e060ce3c25789f408beacc0c4d`; `C:\Users\emrek\AppData\Local\Ravencia\tools\docker-compose-v2.40.2.exe`; engine bağlantısı geçti | PASSED_FOR_LOCAL |
| Yerel WSL2 | Windows özellikleri + Microsoft paketi + `wsl --version/status` | WSL `2.7.11.0`, kernel `6.18.33.2-2`, varsayılan sürüm `2`; Windows build `26200.8875` | PASSED_FOR_LOCAL |
| Linux image smoke | Digest-pinned Caddy 2.11.3 | `linux/amd64`; index digest `sha256:ec18...c7d9`; `caddy version` = `v2.11.3` | PASSED_FOR_LOCAL |
| Named-volume kalıcılığı | İki container + Docker/WSL restart | `ravencia-f0-local-volume`; marker SHA-256 `8301a7bc232ece67bfb630ae783ba883148bc2f9a87ca9b3356a693ef2ac7289` restart öncesi/sonrası eşit; `unless-stopped` container yeniden başladı | PASSED_FOR_LOCAL |
| PostgreSQL 18 mount guard | İlk source container logu | Eski `/var/lib/postgresql/data` mount'u 18+ imaj tarafından exit `1` ile reddedildi; `/var/lib/postgresql` kök mount'u kullanıldı | FAILED_SAFE_THEN_CORRECTED |
| PostgreSQL dump/restore | Digest-pinned PostgreSQL 18.4, ayrı source/restore/backup volume'ları | Dump SHA-256 `51a6a9df0065b7e346e137cac77aa6208e7989b380fe885d7282a7f5c165fd3f`; source/restore `2|fb4200bade7730f8239ef795f97ee6fc`; restore `0,147 sn` | PASSED_FOR_LOCAL |
| Hedef AWS Ubuntu Server 26.04 LTS | SSH + hedef runbook salt-okunur kontrolleri | x86_64; 2 vCPU; 8.153.141.248 byte RAM; 80.530.636.800 byte NVMe; root filesystem 76.878.503.936 byte; SSH aktif; repository `main`/`6fd049b` temiz | HOST_PROFILE_PASSED / RUNTIME_PENDING |
| Hedef Docker Engine | Resmî Docker apt repository, exact paket kurulumu, `systemctl`, `docker version/info` | Engine/CLI `29.7.1`; containerd `2.2.6`; Buildx `0.36.0`; Linux/x86_64 `overlayfs`; Docker enabled/active. `ubuntu` hesabı root-eşdeğeri Docker grubuna eklenmedi. | PASSED_TARGET |
| Hedef Compose | Installer checksum ve `docker compose version --short` | Dağıtım paketi `5.3.1` kurulu; proje exact `2.40.2` root plugin'i host-only çalışmasında indirip doğrulayacak | INSTALLER_PENDING |
| Restore/RTO | Hedef PostgreSQL + files restore | Hedef volume/restore henüz çalıştırılmadı | NOT_RUN / BLOCKED_EXTERNAL |

## Lock içeriği notları

- Npgsql EF provider 10.0.3 lock kaydı EF Core bağımlılık aralığını `[10.0.4, 11.0.0)` olarak çözmüş; seçilen EF Core 10.0.10 bu aralıktadır.
- NPM lockfile integrity ve license metadata'sı içerir.
- Yerel Node engine uyarısı hedef `node:24.18.1` index pin'iyle kayıtlıdır; yerel makine production kanıtı değildir.
