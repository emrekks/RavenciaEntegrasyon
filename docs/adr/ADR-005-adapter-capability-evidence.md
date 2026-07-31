# ADR-005: Adapter Capability Kanıtı

- Durum: Accepted — yetkili şartname kararı
- Tarih: 2026-07-31
- Faz: F0

## Bağlam

Platform davranışları ortam, API sürümü, mağaza scope'u ve yetkiye göre değişebilir.

## Karar

Her adapter capability bazında fail-closed çalışır. Support level yalnız `SUPPORTED`, `NOT_SUPPORTED`, `UNKNOWN`, `TEMPORARILY_UNAVAILABLE` olabilir; başlangıç `UNKNOWN`dur. `SUPPORTED` için resmî kaynak ve test hesabı/anonim fixture kanıtı birlikte zorunludur. Read ve write ayrı capability'dir.

Kanıt; tenant, connection, environment, API version, store scope, doğrulama tarihi, source URL/version, required scope, constraints ve evidence note içerir.

## Sonuçlar

Kanıtsız endpoint, enum, alan veya limit üretilmez. `UNKNOWN` ve geçici kullanılamaz durumda dış yazma kapalı kalır.

## Değişiklik kapısı

Capability yalnız yeni resmî kaynak ve tekrar üretilebilir test kanıtıyla güncellenir.
