# MarketplaceHub

Bağlayıcı uygulama şartnamesi [v3.4 Nihai Uygulama Sürümü](Ravencia_Entegrasyon_v3_4_Nihai_Uygulama_Surumu.pdf)'dür. v3.3 ve v3.2 tarihsel taban olarak korunur; v3.4 mevcut AWS Ubuntu Server hedef revizyonu dağıtım-host hükümlerinde üstündür.

Ravencia MarketplaceHub, yetkili v3.4 şartnamesine göre geliştirilen modüler monolit e-ticaret yönetim sistemidir. Repository’de F1–F6A yerel çekirdekleri bulunur:

- F1: kimlik, güvenli oturum, tenant sınırı, job/inbox/idempotency, private file ve operasyon altyapısı.
- F2: ürün, varyant, katalog referansları, CSV/XLSX içe aktarım, stok projection/ledger ve fiyat geçmişi.
- F3: Trendyol V2 adapter sınırı, bağlantı/capability yönetimi, sipariş, paket, gönderi, iade, webhook ve reconciliation.
- F4: fatura/mali belge çekirdeği, E-Faturam adapter sınırı, private belge saklama ve marketplace delivery ayrımı.
- F5: Shopify Admin GraphQL `2026-07` adapter çekirdeği, HMAC webhook ve streaming bulk JSONL sözleşmesi.
- F6A: Hepsiburada draft bağlantısı ve generic portları kullanan no-HTTP/no-write güvenlik çekirdeği; partner/SIT kanıtı bekleniyor.

Yerel çekirdek durumu `READY_LOCAL_CORE`dır. Gerçek platform test hesapları, granted capability/scope kanıtları, hedef Ubuntu Server, public HTTPS, backup/restore hedefi ve iş otoritesi kararları tamamlanmadığından production kabulü `BLOCKED_EXTERNAL`dır. Bütün dış yazma anahtarları varsayılan olarak kapalıdır.

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

Persistence integration testleri varsayılan olarak Testcontainers kullanır. Docker yoksa `MARKETPLACEHUB_TEST_POSTGRES` ile verilen, geçici veritabanı oluşturma/silme yetkili ayrı bir yerel PostgreSQL yönetici bağlantısını kullanabilir; test runner benzersiz test veritabanını kendisi oluşturur ve sonunda siler. Docker’sız Windows geliştirme seçeneği [local-development.md](docs/runbooks/local-development.md) içinde açıklanmıştır.

## Yerel container çalıştırma

`deploy/secrets/` altındaki yerel secret dosyaları oluşturulmadan Compose başlatılmaz. Secret değerleri Git’e, image’a veya Compose YAML’ına yazılmaz.

```powershell
& "$env:LOCALAPPDATA\Ravencia\tools\docker-compose-v2.40.2.exe" -f deploy/compose/compose.yaml up -d
```

Yalnız Caddy `80/443` host portlarını açar. API, Worker ve PostgreSQL internal backend ağındadır. PILOT_LOCAL edge ayrı internal CA kullanır; production edge public DNS için otomatik HTTPS kullanır. Ubuntu Server'a ilk kurulum veya mevcut veriyi taşıma için [Ubuntu dağıtım runbook'u](docs/runbooks/ubuntu-server-deployment.md), production işlemleri için [immutable image release](docs/runbooks/image-release.md), [deployment-and-rollback.md](docs/runbooks/deployment-and-rollback.md), kimlik işlemleri için [identity-operations.md](docs/runbooks/identity-operations.md), fatura işlemleri için [invoice-operations.md](docs/runbooks/invoice-operations.md) ve kurtarma için [backup-and-restore.md](docs/runbooks/backup-and-restore.md) kullanılır.

Ubuntu Server üzerinde etkileşimli hazırlık ve fail-closed doğrulama:

```bash
chmod +x deploy/scripts/*.sh
sudo -H ./deploy/scripts/install-marketplacehub.sh --host-only
```

## Faz ve güvenlik sınırı

Aktif ve onaylanmış son yerel uygulama alt fazı F6A’dır. F6B N11, F6C Pazarama veya F7+ route, menü, migration ya da placeholder bulunmaz. Trendyol, E-Faturam, Shopify ve Hepsiburada adapter kodlarının bulunması gerçek mağaza capability’sinin kanıtlandığı anlamına gelmez:

- Capability’ler bağlantı/environment/store/API-version kapsamında başta `UNKNOWN`dır.
- Token ve secret değerleri şifreli saklanır, API/UI/log çıktısında geri gösterilmez.
- Shopify ürün, stok, fiyat ve fulfillment yazmaları development-store kanıtları tamamlanana kadar fail-closed’dur.
- Hepsiburada auth modeli partner hesabında doğrulanana kadar credential, bağlantı testi ve bütün dış read/write çağrıları fail-closed’dur.
- Fatura otomasyonu mali kararlar ve test firma kanıtı olmadan kapalıdır.
- AWS Ubuntu Server host profili ve Docker Engine doğrulanmıştır; reboot/volume/restore, domain/TLS ve RTO kanıtları tamamlanana kadar sonuç production kabulü sayılmaz.

Ubuntu Server kurulumu ve Stage/SIT hesap kanıtları ertelenmiş olsa da production aday imajlarını GitHub Container Registry'ye digest ile üreten manuel ve fail-closed yayın akışı repository'dedir. Bu hazırlık faz kapılarını açmaz: F6B/F6C/F7+ üretim kodu, gerçek platform write ve canlı deploy hâlâ ilgili dış kanıt ve ayrı onayları bekler.

Güncel faz durumu [F6A planında](docs/implementation/F6A-plan.md), kanıtlar [F6A evidence logunda](docs/implementation/F6A-evidence-log.md), bütün faz izi ise [traceability matrixte](docs/implementation/traceability-matrix.md) tutulur.
