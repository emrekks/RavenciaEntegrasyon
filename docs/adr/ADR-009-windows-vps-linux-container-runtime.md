# ADR-009: Windows VPS Üzerinde Linux Container Runtime (Superseded)

- Güncel durum: Superseded by ADR-012 ve v3.3 Ubuntu Server revizyonu
- Supersede tarihi: 2026-08-02

Bu kayıt tarihsel F0 kararını ve yerel Windows ön kanıtını korur. Production hedefi olarak artık uygulanmaz; güncel bağlayıcı hedef [ADR-012](ADR-012-ubuntu-server-container-runtime.md) içindedir.

- Durum: Accepted; hedef kanıtı bekliyor
- Tarih: 2026-07-31
- Faz: F0

## Bağlam

Bağlayıcı dağıtım hedefinin Linux container çalıştırma ve kalıcı volume davranışı hedef Windows VPS üzerinde ayrıca kanıtlanmalıdır.

## Karar

Dağıtım hedefi Windows VPS üzerinde Linux container'lardır. API ve Worker ayrı container/process, PostgreSQL ve private dosyalar kalıcı volume ile çalışır; Caddy 2.11 hattı reverse proxy'dir. Docker/Compose kullanımı hedef hostta production desteği, mimari, restart ve volume davranışıyla doğrulanır.

Kullanıcının 2026-07-31 tarihli kararıyla geliştirme ve ön runtime doğrulaması önce yerel Windows bilgisayarda yapılır; taşınabilir artefaktlar hedef VPS kiralandığında aktarılır ve aynı doğrulama seti hedef hostta tekrarlanır. Bu uygulama sırası dağıtım mimarisini veya production kabul kapısını değiştirmez.

## Sonuçlar

Yerel makine sonucu hedef kanıtı değildir. Hyper-V/WSL2/Docker uygunluğu, Linux mode, restart, volume ve backup kanıtlanmadan production dağıtım onayı verilmez.

Yerel ön doğrulama 2026-07-31 tarihinde WSL `2.7.11`, Docker Desktop `4.84.0`, Linux/amd64 Engine `29.6.2`, exact Compose `v2.40.2`, digest-pinned Caddy smoke, Docker/WSL restart ve named-volume checksum eşitliğiyle geçmiştir. Bu sonuç F1 yerel geliştirme runtime kapısını açar; hedef VPS kanıtını kapatmaz.

## Açık kanıt ve değişiklik kapısı

`BLOCK-HOST-001` açıktır. Runtime yaklaşımı sessizce değiştirilmez; hedef sağlayıcı kısıtı varsa kullanıcı ve şartname kararı gerekir.
