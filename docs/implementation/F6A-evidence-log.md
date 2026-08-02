# F6A Hepsiburada Adapter Kanıt Kaydı

Doğrulama tarihi: `2026-08-02`. Ortam: Windows geliştirme makinesi. Hepsiburada partner/SIT hesabı, merchant credential, anonim partner payload’ı, public callback veya dış yazma kullanılmadı.

## Sonuç özeti

| Kanıt | Sonuç | Yerel kanıt / açık sınır |
| --- | --- | --- |
| `F6A-EV-001` build | PASS | Solution `0 warning / 0 error`; strict TypeScript/Vite production build geçti |
| `F6A-EV-002` phase boundary | PASS | Yalnız Hepsiburada F6A adapterı; N11/Pazarama adapter, route veya menüsü yok |
| `F6A-EV-003` generic ports | PASS_LOCAL | Connection/reference/product/inventory-price/order/return portlarının tamamı Infrastructure adapterda uygulanıyor |
| `F6A-EV-004` auth gate | PASS_FAIL_CLOSED | Portal auth anlatımı çelişkisi nedeniyle credential kaydı ve connection test job’u `HEPSIBURADA_AUTH_MODEL_UNVERIFIED` ile kapanır |
| `F6A-EV-005` read gate | PASS_FAIL_CLOSED | Partner fixture/capability yokken bütün dış read yöntemleri no-HTTP `HEPSIBURADA_CAPABILITY_UNVERIFIED` döndürür |
| `F6A-EV-006` write gate | PASS_FAIL_CLOSED | Product/listing, archive, stock, price, package ve return action yöntemleri `EXTERNAL_WRITE_DISABLED` döndürür |
| `F6A-EV-007` error classes | PASS_LOCAL | 401/403 authentication, 409 business conflict, 429 rate limit, 5xx remote ve diğer 4xx validation olarak ayrı test edildi |
| `F6A-EV-008` connection model | PASS_LOCAL | Draft merchant scope, `v1.0` guide kaydı, User-Agent ve capability UNKNOWN mevcut generic tablolarda tutulur; migration yok |
| `F6A-EV-009` UI | PASS_LOCAL | Mevcut integrations formunda yalnız F6A draft seçimi; auth blocker görünür ve credential/test düğmeleri kapalı |
| `F6A-EV-010` contract/SIT | BLOCKED_EXTERNAL | Gerçek payload olmadığı için mapping/status enum/endpoint oluşturulmadı |
| `F6A-EV-011` previous-platform gate | BLOCKED_PHASE_GATE | F5 production reconciliation/rollback kanıtı yok |
| `F6A-EV-012` local reconciliation/rollback | PASS_LOCAL_DRY | Ortak servis Hepsiburada bağlantısını no-HTTP/no-write kuru kontrolde kabul eder; N11/Pazarama reddedilir; geri dönüş rehberi kayıt koruma ve yeniden açma kapılarını tanımlar |
| `F6A-EV-013` secret/auth boundary | PASS_LOCAL_FAIL_CLOSED | Adapterda HTTP/auth/credential implementasyonu ve fixture dosyası yok; credential/test-job kapıları kapalı; source/docs secret signature taraması temiz |
| `F6A-EV-014` package event safety | PASS_LOCAL_PROPERTY | Sipariş satırları ve miktar zinciri persistence öncesi doğrulanır; fazla/negatif/duplicate/bilinmeyen satır olayı atomik reddedilir; canonical ilerleme yalnız kabul edilen yerel paketlerden türetilir; eski zaman veya geriye durum geçişi uygulanmaz; event kimliği deterministiktir |
| `F6A-EV-015` durable job retry | PASS_MODEL_SQL / POSTGRES BLOCKED_RUNTIME | Bağlayıcı `PENDING/LEASED/RETRY_SCHEDULED/BLOCKED/DEAD/CANCELLED`, max-attempt, 15s/1m/5m/20m/1h + %10–20 deterministik jitter, expired-attempt kapanışı ve fencing uygulandı; idempotent migration SQL üretildi; Docker engine yok |
| `F6A-EV-016` worker heartbeat fencing | PASS_BUILD_POLICY / POSTGRES BLOCKED_RUNTIME | İki dakikalık lease için 30 saniyelik heartbeat ayrı ve kısa DbContext scope’larında çalışır; lease kaybı yerel yürütmeyi iptal eder, completion yeni scope ile token/expiry fencing’den geçer ve kayıtlı correlation korunur |

## Test sonucu

- .NET build: başarılı, `0` uyarı / `0` hata.
- Docker gerektirmeyen testler: `85/85` başarılı (Domain 20, Application 19, Adapter 42, API 2, Repository guard 2).
- PostgreSQL Testcontainers: `0/7` assertion çalıştı; Docker engine bulunmadığı için test sınıfı başlangıçta `BLOCKED_RUNTIME` oldu. Bu sonuç kod başarısızlığı olarak PASS’e çevrilmedi.
- EF migration: modelde bekleyen değişiklik yok; F4 → F6A idempotent SQL üretimi başarılı. Migration yerel kullanıcı veritabanına uygulanmadı.
- Web: strict TypeScript/Vite production build ve component testi.
- `F6AJobRetryContract` migrationı, şartnamedeki eksik job state/max-attempt/index sözleşmesi ölçülebilir biçimde mevcut modelde bulunmadığı için oluşturuldu.

## Faz durumu

F6A no-HTTP/no-write yerel güvenlik çekirdeği ve yerel kuru mutabakat/geri dönüş sınırı `READY_LOCAL_FAIL_CLOSED`dır. Partner/SIT hesabı, auth modeli, merchant scope/permission, tarihli endpoint/version, anonim fixture ve safe-write onayı olmadan capability `SUPPORTED` yapılmaz. F5 production reconciliation/rollback tamamlanmadan F6A production çıkışı yapılamaz. F6B ve F6C açılmamıştır.
