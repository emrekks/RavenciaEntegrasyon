# F0 Kanıt Günlüğü

Doğrulama tarihi: 2026-07-31. Sonuçlar gerçek komut çıktılarından kaydedilmiştir; çalıştırılmayan kontrol geçmiş gösterilmez.

| Kanıt | Komut / yöntem | Gerçek sonuç | Durum |
| --- | --- | --- | --- |
| Canonical şartname | SHA-256 ve pypdf sayfa sayısı karşılaştırması | Kaynak ve kök kopya aynı hash; 73/73 sayfa | PASSED |
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
| Hedef Windows VPS | Hedef runbook | VPS henüz kiralanmadı | NOT_RUN / BLOCKED_EXTERNAL |
| Restore/RTO | Hedef PostgreSQL + files restore | Hedef VPS/volume yok | NOT_RUN / BLOCKED_EXTERNAL |

## Lock içeriği notları

- Npgsql EF provider 10.0.3 lock kaydı EF Core bağımlılık aralığını `[10.0.4, 11.0.0)` olarak çözmüş; seçilen EF Core 10.0.10 bu aralıktadır.
- NPM lockfile integrity ve license metadata'sı içerir.
- Yerel Node engine uyarısı hedef `node:24.18.1` index pin'iyle kayıtlıdır; yerel makine production kanıtı değildir.
