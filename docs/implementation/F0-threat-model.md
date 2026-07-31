# F0 Threat Modeli

## Varlıklar ve güven sınırları

Kritik varlıklar: platform credential'ları, kullanıcı kimliği, tenant/connection kapsamı, stok ve fiyat otoritesi, sipariş/iade/fatura verisi, private dosyalar, PostgreSQL, job/inbox/idempotency kayıtları, backup ve signing keys.

Güven sınırları: internet–Caddy, Caddy–API, API/Worker–PostgreSQL, uygulama–platformlar, uygulama–private file volume, runtime–backup hedefi ve kullanıcı–yetkili işlevler.

| Tehdit | Zorunlu kontrol | Kanıt / durum |
| --- | --- | --- |
| Credential veya PII sızıntısı | Secret redaction, repository dışında secret, anonim fixture, private dosya | Tasarım kaydı DONE; uygulama F1+ |
| Tenant/connection karışması | Her capability ve işlemde tenant+connection+environment+store scope | ADR-002/005; uygulama F1+ |
| Replay/çift işlem | Inbox, idempotency key, unique constraint ve uzlaştırma | ADR-004; uygulama F1+ |
| Sahte webhook | İmza doğrulama, timestamp/replay kontrolü, raw body'nin hassas loglardan çıkarılması | Capability `UNKNOWN`; write off |
| Yetkisiz dış yazma | Least privilege, ayrı read/write capability, kill switch ve explicit enable | Tüm write switch'leri off |
| Path traversal/public dosya | `IFileStorage`, opaque id, allowlist, private volume, auth kontrollü indirme | ADR-008; uygulama F1+ |
| Supply-chain/image drift | Exact sürüm, F0 verification lock, immutable index digest, resmî kaynak; F1 aktarımı fail-closed | MITIGATED_F0 |
| Veri kaybı/ransomware | Şifreli off-host backup, restore testi, ayrık failure domain | BLOCK-DR-001 |
| Log enjeksiyonu/hassas veri | Yapısal JSON, allowlist alanlar, redaction, correlation id | Uygulama F1+ |
| Kaynak tüketimi/queue taşması | PostgreSQL durable queue, backoff, retry limiti, dead-letter/operasyon alarmı | Kapasite ölçümü açık |

## Kabul edilmeyen varsayım

Resmî sayfanın erişilebilir olması capability'nin güvenli veya desteklenir olduğunu kanıtlamaz. Test hesabı/anonim fixture yoksa durum `UNKNOWN`, dış yazma kapalıdır.
