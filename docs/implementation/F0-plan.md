# F0 — Temel Mimari ve Güvenlik Planı

## Amaç

Tek işletme için güvenli, geri alınabilir ve yalnız Trendyol/E-Faturam kapsamına açık temel oluşturmak.

## Teslimler

1. Domain/Application/Infrastructure/API/Worker/Web katman sınırları.
2. PostgreSQL migration, idempotency, inbox, job lease ve audit kayıtları.
3. File-backed secret, Data Protection, MFA altyapısı ve güvenli session.
4. Caddy arkasında internal API/Worker/PostgreSQL ağ ayrımı.
5. Immutable digest image release ve production TLS ayarları.
6. Backup/restore runbook ve temiz paket denetimi.
7. ADR-016 kapsam kapısı.

## Çıkış kapısı

- Locked restore/build/test/format CI geçer.
- Source tree temizlik kontrolü geçer.
- Secret veya runtime DB verisi pakette bulunmaz.
- `/health/ready` PostgreSQL bağımlılığını doğrular.
- Production AllowedHosts, site address ve image digest değişkenleri eksikse deployment başlamaz.
- Off-host backup restore kanıtı tamamlanmadan production kabulü verilmez.
