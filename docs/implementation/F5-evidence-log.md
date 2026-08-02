# F5 Shopify Adapter Kanıt Kaydı

Doğrulama tarihi: `2026-08-02`. Ortam: Windows geliştirme makinesi. Development store, gerçek Shopify credential, granted scope, Location GID, public HTTPS veya dış yazma kullanılmadı.

## Sonuç özeti

| Kanıt | Sonuç | Yerel kanıt / açık sınır |
| --- | --- | --- |
| `F5-EV-001` build | PASS | Solution `0 warning / 0 error`; strict TypeScript/Vite production build geçti |
| `F5-EV-002` boundary | PASS | Domain/Application altında Shopify’a özel tip yok; Shopify kodu Infrastructure adapter ve mevcut Web yüzeyiyle sınırlı |
| `F5-EV-003` version | PASS_LOCAL | Admin GraphQL `2026-07` sabit; canonical `*.myshopify.com` doğrulaması ve response version guard mevcut; canlı response `BLOCKED_EXTERNAL` |
| `F5-EV-004` secret | PASS_LOCAL | Access token ve app client secret mevcut Data Protection zincirinde şifreli; yalnız maskeli hint tutulur |
| `F5-EV-005` GraphQL errors | PASS | Top-level `errors` ve mutation `userErrors` ayrı contract testleriyle doğrulandı |
| `F5-EV-006` bulk JSONL | PASS_LOCAL | UTF-8 satır akışı, bounded memory ve tamamlanan satır checkpoint’inden devam testi geçti; gerçek staged upload/bulk result `BLOCKED_EXTERNAL` |
| `F5-EV-007` product/order ports | PASS_FAIL_CLOSED | Genel portlar uygulanıyor; scope + fixture kanıtı gelmeden read çağrıları ve tüm write çağrıları güvenli hata döndürüyor |
| `F5-EV-008` inventory/fulfillment | PASS_FAIL_CLOSED | Location/authority kanıtı yokken stok, fiyat ve fulfillment HTTP etkisi üretmiyor |
| `F5-EV-009` webhook | PASS_LOCAL | Değişmemiş raw body üzerinde HMAC-SHA256/base64/fixed-time doğrulama; Inbox external message dedupe ve ayrı worker job hattı mevcut; public teslim `BLOCKED_EXTERNAL` |
| `F5-EV-010` persistence/UI | PASS_LOCAL | Yeni migration yok; generic connection/capability/job/inbox tabloları kullanılıyor; integrations ekranına Shopify seçimi ve secret alanları eklendi |

## Test sonucu

- .NET build: başarılı, `0` uyarı / `0` hata.
- Docker gerektirmeyen testler: `48/48` başarılı (Domain 12, Application 18, Adapter 14, API 2, Repository guard 2).
- PostgreSQL Testcontainers seti: Docker engine çalışmadığı için bu turda çalıştırılamadı; F5 migration üretmediği için schema değişikliği yok.
- Web: strict TypeScript ve Vite production bundle başarılı.

## Faz durumu

F5 yerel çekirdek `READY_LOCAL_CORE` durumundadır. Development-store kimliği/credential, granted scopes, Location mapping, anonim gerçek fixture, public HTTPS webhook, write authority ve dış E2E kanıtları tamamlanana kadar F5 tam çıkışı `BLOCKED_EXTERNAL` kalır. Bütün Shopify capability’leri başlangıçta `UNKNOWN`; yalnız başarılı canlı bağlantı ve dönen API version testi `CONNECTION_TEST` yeteneğini destekli duruma getirebilir. F6 açılmamıştır.
