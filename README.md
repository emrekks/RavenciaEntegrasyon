# MarketplaceHub

Ravencia MarketplaceHub, yetkili v3.2 şartnamenin F1 güvenli temel uygulamasıdır. Bu repository şu anda yalnız F1 kapsamını içerir: modüler monolit solution, API, Worker, React yönetim kabuğu, PostgreSQL migration/bootstrap, IAM güvenliği, operasyonel job altyapısı ve Compose dağıtım tabanı. Ürün, sipariş veya gerçek platform entegrasyonu henüz yoktur.

## Gereksinimler

- .NET SDK `10.0.302`
- Node.js `24.18.1` ve npm `11.12.1` (yerel makinede daha eski patch yalnız geliştirme uyarısı üretir)
- Linux container destekli Docker Engine
- Şartname gereği Compose CLI `v2.40.2`

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

## Yerel container çalıştırma

`deploy/secrets/` altındaki beş yerel secret dosyası oluşturulmadan Compose başlatılmaz. Secret değerleri Git'e, image'a veya Compose YAML'ına yazılmaz. Ayrıntılı sıra [local-development.md](docs/runbooks/local-development.md) dosyasındadır.

```powershell
& "$env:LOCALAPPDATA\Ravencia\tools\docker-compose-v2.40.2.exe" -f deploy/compose/compose.yaml up -d
```

Yalnız Caddy `80/443` host portlarını açar. API, Worker ve PostgreSQL yalnız internal backend ağındadır. Production işlemleri için [deployment-and-rollback.md](docs/runbooks/deployment-and-rollback.md), kimlik işlemleri için [identity-operations.md](docs/runbooks/identity-operations.md), backup/restore için [backup-and-restore.md](docs/runbooks/backup-and-restore.md) bağlayıcı çalışma notlarıdır.

## Faz sınırı

F2 veya sonrası production kodu, migration'ı, controller'ı, endpoint'i, menüsü veya placeholder'ı yoktur. Gerçek platform capability'leri `UNKNOWN`, bütün dış yazma anahtarları kapalıdır.
