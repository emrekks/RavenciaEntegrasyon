# MarketplaceHub

Ravencia MarketplaceHub, yetkili v3.2 şartnamesine göre geliştirilen modüler monolit e-ticaret yönetim sistemidir. Repository’de F1–F5 yerel çekirdekleri bulunur:

- F1: kimlik, güvenli oturum, tenant sınırı, job/inbox/idempotency, private file ve operasyon altyapısı.
- F2: ürün, varyant, katalog referansları, CSV/XLSX içe aktarım, stok projection/ledger ve fiyat geçmişi.
- F3: Trendyol V2 adapter sınırı, bağlantı/capability yönetimi, sipariş, paket, gönderi, iade, webhook ve reconciliation.
- F4: fatura/mali belge çekirdeği, E-Faturam adapter sınırı, private belge saklama ve marketplace delivery ayrımı.
- F5: Shopify Admin GraphQL `2026-07` adapter çekirdeği, HMAC webhook ve streaming bulk JSONL sözleşmesi.

Yerel çekirdek durumu `READY_LOCAL_CORE`dır. Gerçek platform test hesapları, granted capability/scope kanıtları, hedef VPS, public HTTPS, backup/restore hedefi ve iş otoritesi kararları tamamlanmadığından production kabulü `BLOCKED_EXTERNAL`dır. Bütün dış yazma anahtarları varsayılan olarak kapalıdır.

## Gereksinimler

- .NET SDK `10.0.302`
- Node.js hedefi `24.18.1` ve npm `11.12.1` (doğrulanan yerel Node `24.15.0` yalnız engine uyarısı üretir)
- PostgreSQL `18.4`
- Container çalıştırmada Linux container destekli Docker Engine
- Şartname gereği Compose CLI `v2.40.2`

Kesin sürüm ve digest kayıtları [verified-versions.md](docs/dependencies/verified-versions.md) içindedir.

## Doğrulama

```powershell
dotnet restore MarketplaceHub.sln --locked-mode
dotnet build MarketplaceHub.sln --no-restore
dotnet test MarketplaceHub.sln --no-build
Set-Location src/MarketplaceHub.Web
npm ci
npm run typecheck
npm test
npm run build
```

Persistence integration testleri Testcontainers kullanır ve çalışan Docker engine gerektirir. Docker’sız Windows geliştirme seçeneği [local-development.md](docs/runbooks/local-development.md) içinde açıklanmıştır.

## Yerel container çalıştırma

`deploy/secrets/` altındaki yerel secret dosyaları oluşturulmadan Compose başlatılmaz. Secret değerleri Git’e, image’a veya Compose YAML’ına yazılmaz.

```powershell
& "$env:LOCALAPPDATA\Ravencia\tools\docker-compose-v2.40.2.exe" -f deploy/compose/compose.yaml up -d
```

Yalnız Caddy `80/443` host portlarını açar. API, Worker ve PostgreSQL internal backend ağındadır. Production işlemleri için [deployment-and-rollback.md](docs/runbooks/deployment-and-rollback.md), kimlik işlemleri için [identity-operations.md](docs/runbooks/identity-operations.md), fatura işlemleri için [invoice-operations.md](docs/runbooks/invoice-operations.md) ve kurtarma için [backup-and-restore.md](docs/runbooks/backup-and-restore.md) kullanılır.

## Faz ve güvenlik sınırı

Aktif ve onaylanmış son yerel uygulama fazı F5’tir. F6 veya sonrası platform, route, menü, migration ya da placeholder bulunmaz. Trendyol, E-Faturam ve Shopify adapter kodlarının bulunması gerçek mağaza capability’sinin kanıtlandığı anlamına gelmez:

- Capability’ler bağlantı/environment/store/API-version kapsamında başta `UNKNOWN`dır.
- Token ve secret değerleri şifreli saklanır, API/UI/log çıktısında geri gösterilmez.
- Shopify ürün, stok, fiyat ve fulfillment yazmaları development-store kanıtları tamamlanana kadar fail-closed’dur.
- Fatura otomasyonu mali kararlar ve test firma kanıtı olmadan kapalıdır.
- Hedef VPS kiralanana kadar yerel sonuç production runtime/RTO kabulü sayılmaz.

Güncel faz durumu [F5 planında](docs/implementation/F5-plan.md), kanıtlar [F5 evidence logunda](docs/implementation/F5-evidence-log.md), bütün faz izi ise [traceability matrixte](docs/implementation/traceability-matrix.md) tutulur.
