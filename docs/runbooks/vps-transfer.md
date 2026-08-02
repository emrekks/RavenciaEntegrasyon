# Windows VPS'e Taşıma Runbook'u

Bu akış mevcut modüler monolit, Docker Compose, PostgreSQL, API/Worker ve Caddy sınırlarını değiştirmez. Yerel bilgisayarda üretilen aynı immutable Linux/amd64 imajları hedef Windows VPS'te çalıştırır. VPS doğrulaması production kabulünün yerini tutmaz; önce `windows-vps-runtime-validation.md` hedef kanıtıyla tamamlanır.

## Taşınabilir parçalar

- Kod ve dağıtım tanımları GitHub repository'sinden alınır.
- Uygulama ve edge imajları GitHub Container Registry'den yalnız `name@sha256:...` referansıyla çekilir.
- Secret ve Data Protection PFX Git'e veya imaja eklenmez; yalnız hedef VPS'te `deploy/secrets/` altında hazırlanır.
- PostgreSQL, private dosyalar ve Data Protection key ring kalıcı Docker volume'larında tutulur.
- Mevcut veri taşınacaksa repository kopyalamak yerine doğrulanmış backup seti kullanılır.

## Bir defalık hedef hazırlığı

1. VPS'in Linux/amd64 container, WSL2/nested virtualization, kalıcı volume, reboot ve exact Compose `v2.40.2` kontrollerini hedef runbook ile tamamla.
2. Repository'yi VPS'e clone et ve onaylı commit'e geç.
3. GHCR paketleri private ise read-only package token ile VPS'te registry oturumu aç. Token'ı komut satırı geçmişine veya repository'ye yazma.
4. Production domaininin DNS kaydını VPS'e yönlendir; dışarıdan yalnız `80/443` aç. API, Worker veya PostgreSQL portu yayınlama.
5. Otomatik oluşturulan Data Protection PFX, parolası ve metadata kaydını kurulumdan hemen sonra onaylı şifreli secret/yedek hedefine kopyala. Bu PFX Caddy'nin public TLS sertifikası değildir; yalnız kalıcı Data Protection key ring'i şifreler.

## En kolay kurulum

Repository kökünde aşağıdaki tek komutu çalıştır:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\deploy\scripts\Install-MarketplaceHub.ps1
```

Sihirbaz eksikse exact Compose `v2.40.2` binary'sini resmî GitHub release adresinden indirip kayıtlı SHA-256 ile doğrular. Ardından yalnız şu dört bilgiyi sorar:

- GitHub Actions özetindeki uygulama `name@sha256:...` değeri.
- GitHub Actions özetindeki edge `name@sha256:...` değeri.
- Panelin `https://...` adresi.
- İlk Owner e-posta adresi.

Owner parolası ekranda görünmeden iki kez alınır. PostgreSQL parolası, credential key, PFX ve PFX parolası kriptografik olarak ayrı üretilir; secret içerikleri ekrana basılmaz. İlk çalıştırma yalnız hazırlık ve doğrulama yapar, servisleri değiştirmez.

Doğrulama başarılı olduktan sonra ilk boş kurulumu tek komutla başlat:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\deploy\scripts\Install-MarketplaceHub.ps1 -Deploy -Bootstrap
```

Sonraki sürümlerde `-Bootstrap` kullanma. Var olan `deploy/secrets/production.env` ve secret dosyaları korunur.

## Gelişmiş: hazır PFX ile kurulum

Kuruma ait hazır bir Data Protection PFX kullanılacaksa düşük seviyeli initializer doğrudan çağrılabilir:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\deploy\scripts\Initialize-VpsDeployment.ps1 `
  -ApplicationImage 'ghcr.io/OWNER/marketplacehub-app@sha256:...' `
  -EdgeImage 'ghcr.io/OWNER/marketplacehub-edge@sha256:...' `
  -SiteAddress 'https://panel.example.com' `
  -OwnerEmail 'approved-owner@example.com' `
  -DataProtectionCertificatePath 'C:\secure-transfer\marketplacehub-dp.pfx'
```

Script Owner ve PFX parolalarını görünmeden ister; PostgreSQL parolası ile 32-byte credential key'i ayrı ayrı üretir. Var olan secret dosyalarını hiçbir koşulda overwrite etmez. `deploy/secrets/` Git tarafından ignore edilir.

## Doğrulama ve ilk kurulum

Önce yalnız doğrula:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\deploy\scripts\Invoke-VpsDeployment.ps1 -ValidateOnly
```

İlk boş kurulumda migration, servisler, açık operatör kararıyla Owner bootstrap ve HTTPS readiness sırası:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\deploy\scripts\Invoke-VpsDeployment.ps1 -Bootstrap
```

`ExecutionPolicy Bypass` yalnız bu yeni PowerShell işlemi için geçerlidir; makinenin kalıcı script politikasını değiştirmez.

Sonraki immutable sürümlerde `deploy/secrets/production.env` içindeki iki digest'i onaylı yeni değerlerle değiştir ve `-Bootstrap` kullanmadan çalıştır. Bootstrap tekrarı varsayılan değildir. Dış platform yazmaları ayrıca kanıtlanıp açılmadıkça kapalı kalır.

## Mevcut yerel veriyi taşıma

1. Kaynak ortamda `backup-and-restore.md` ile `database.dump`, `private-volumes.tar.gz`, `SHA256SUMS` ve `manifest.json` üret.
2. Backup setini şifreli kanalla hedefe aktar; `restore-verify.sh` ile checksum ve archive bütünlüğünü doğrula.
3. Hedefte boş, izole PostgreSQL/volume setine restore et. Var olan production volume üzerine açma ve `down -v` çalıştırma.
4. Restore edilen DB, private files ve Data Protection keys birlikte doğrulanmadan DNS trafiğini açma.
5. Readiness, kontrollü login, backup ve ölçülmüş restore kanıtından sonra Go/No-Go kaydı oluştur.

## Rollback ve sınırlar

- Migration uyumluysa yalnız önceki onaylı app/edge digest çiftine dön.
- Şema uyumsuzsa write kapalı kalır ve onaylı backup yeni boş hedefe restore edilir; tahmini in-place downgrade yapılmaz.
- Secret rotasyonu bu ilk kurulum scriptinin işi değildir. Mevcut secret dosyalarını silip initializer'ı yeniden çalıştırma.
- Otomatik üretilen `dp_certificate.pfx`, `dp_certificate_password.txt` ve `dp_certificate_metadata.txt` dosyalarını repository dışında şifreli ve erişim kontrollü hedefte sakla; kaybolmaları restore edilen key ring'in açılamamasına yol açar.
- `down -v`, prune, floating tag, `latest`, public DB/API portu veya native Windows container dönüşümü yasaktır.
- Bu hazırlık F6B/F6C/F7 faz kapılarını, platform capability'lerini ya da production onayını kendiliğinden açmaz.
