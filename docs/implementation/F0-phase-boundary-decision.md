# F0/F1 Lock ve Digest Faz Sınırı Kararı

- Karar tarihi: 2026-07-31
- Yetki: Kullanıcının kalan blockerları kapatmak için verdiği açık onay
- Etkilenen kayıtlar: `OPEN-F0-002`, `BLOCK-VERSION-001`, `F0-VAL-006`, `F0-EXIT-004`

## Gerilim

Yetkili şartname F0 çıkışında verified versions kaydının lockfile/image digest'leriyle tutarlı ve commit edilmiş olmasını ister; repository-root `global.json`, central packages, Web `package-lock.json` ve production container yapısını ise F1 teslimatı olarak sıralar.

## Kullanıcı onaylı uygulama

Faz sırası ve production yapı değiştirilmeden, yalnız F0 doğrulama kanıtları `docs/dependencies/verification/` altında tutulur:

- SDK pin'i ve NuGet central manifesti ile `packages.lock.json`.
- NPM exact manifesti ile `package-lock.json`.
- Resmî registry multi-platform image index digest'leri.
- Compose v2.40.2 resmî release checksum'ları.

Bu kanıtlar F1 production scaffold'u değildir. `MarketplaceHub.sln`, root build/lock dosyaları, `src/`, `tests/`, `deploy/`, migration, controller, endpoint veya UI oluşturulmaz.

## Çıkış yorumu

`BLOCK-VERSION-001`, F0 adaylarının locked restore ile çözümlenmesi, image index digest'lerinin doğrulanması ve bu kayıtların Git commit'ine alınmasıyla kapanabilir. F1 başladığında root production lockfile'ları ve application image digest'leri yeniden üretilip F0 kanıtlarıyla karşılaştırılacaktır; uyumsuzluk fail-closed kabul edilir.

Hedef mimariye özgü child image digest'i ve Docker Engine/Compose kurulum kanıtı `BLOCK-HOST-001` kapsamındadır; F0 dependency çözümleme blocker'ını yeniden açmaz.
