# ADR-009: Windows VPS Üzerinde Linux Container Runtime

- Durum: Accepted; hedef kanıtı bekliyor
- Tarih: 2026-07-31
- Faz: F0

## Bağlam

Bağlayıcı dağıtım hedefinin Linux container çalıştırma ve kalıcı volume davranışı hedef Windows VPS üzerinde ayrıca kanıtlanmalıdır.

## Karar

Dağıtım hedefi Windows VPS üzerinde Linux container'lardır. API ve Worker ayrı container/process, PostgreSQL ve private dosyalar kalıcı volume ile çalışır; Caddy 2.11 hattı reverse proxy'dir. Docker/Compose kullanımı hedef hostta production desteği, mimari, restart ve volume davranışıyla doğrulanır.

## Sonuçlar

Yerel makine sonucu hedef kanıtı değildir. Hyper-V/WSL2/Docker uygunluğu, Linux mode, restart, volume ve backup kanıtlanmadan production dağıtım onayı verilmez.

## Açık kanıt ve değişiklik kapısı

`BLOCK-HOST-001` açıktır. Runtime yaklaşımı sessizce değiştirilmez; hedef sağlayıcı kısıtı varsa kullanıcı ve şartname kararı gerekir.
