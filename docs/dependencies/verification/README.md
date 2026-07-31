# F0 Dependency Verification

Bu klasör production solution veya F1 uygulama scaffold'u değildir. Kullanıcının 2026-07-31 tarihli blocker-kapatma onayıyla yalnız F0 sürüm adaylarının birlikte çözümlenebildiğini ve lock bütünlüğünü kanıtlamak için oluşturulmuştur.

- `global.json`: doğrulama SDK pin'i.
- `npm/package.json` ve üretilecek `package-lock.json`: Web bağımlılık çözümleme kanıtı.
- `nuget/Directory.Packages.props`, doğrulama projesi ve üretilecek `packages.lock.json`: NuGet çözümleme kanıtı.
- `container-image-digests.md`: resmî registry index digest'leri ve Compose checksum'ları.

Doğrulanan lock hash'leri:

- `npm/package-lock.json`: SHA-256 `B3D19C0F1D64A6CE2236EB52F2CC48A483729565B002FD7B16535AD24EF3A923`
- `nuget/packages.lock.json`: SHA-256 `E8B3D6DB5AADF51E88B945BDF3B9CCC23E1443CB041493C59CB86D80FEED84BF`

Bu dosyalar F1'in repository-root `global.json`, central package yönetimi, Web manifesti veya production lockfile teslimatlarının yerine geçmez. F1 başladığında production konumlarındaki lock'lar bu kanıtla karşılaştırılır ve yeniden doğrulanır.
