# F6A Hepsiburada Adapter Kanıt Kaydı

İlk doğrulama tarihi: `2026-08-02`; üretim paneli yeniden doğrulaması ve dolu Sipariş SIT okuma kanıtı: `2026-08-03`. Ortamlar: Windows geliştirme makinesi ve AWS Ubuntu Server. Credential yalnız salt-okunur bağlantı ve sipariş okuma testlerinde kullanıldı; secret log/repository/kanıta yazılmadı. Public callback veya adapter kaynaklı dış yazma kullanılmadı.

## Sonuç özeti

| Kanıt | Sonuç | Yerel kanıt / açık sınır |
| --- | --- | --- |
| `F6A-EV-001` build | PASS | Solution `0 warning / 0 error`; strict TypeScript/Vite production build geçti |
| `F6A-EV-002` phase boundary | PASS | Yalnız Hepsiburada F6A adapterı; N11/Pazarama adapter, route veya menüsü yok |
| `F6A-EV-003` generic ports | PASS_LOCAL | Connection/reference/product/inventory-price/order/return portlarının tamamı Infrastructure adapterda uygulanıyor |
| `F6A-EV-004` auth gate | PASS_ORDER_SIT | Resmî Sipariş SIT Basic Auth + zorunlu User-Agent modeli partner credential ile doğrulandı; credential yalnız STAGE bağlantısında şifreli kaydedilir, production kapalıdır |
| `F6A-EV-005` read gate | PASS_FAIL_CLOSED | Salt-okunur bağlantı probu dışında bütün read yöntemleri dolu partner fixture/capability yokken no-HTTP `HEPSIBURADA_CAPABILITY_UNVERIFIED` döndürür |
| `F6A-EV-006` write gate | PASS_FAIL_CLOSED | Product/listing, archive, stock, price, package ve return action yöntemleri `EXTERNAL_WRITE_DISABLED` döndürür |
| `F6A-EV-007` error classes | PASS_LOCAL | 401/403 authentication, 409 business conflict, 429 rate limit, 5xx remote ve diğer 4xx validation olarak ayrı test edildi |
| `F6A-EV-008` connection model | PASS_LOCAL | Draft merchant scope, `v1.0` guide kaydı, User-Agent ve capability UNKNOWN mevcut generic tablolarda tutulur; migration yok |
| `F6A-EV-009` UI | PASS_LOCAL | Mevcut integrations formunda Merchant ID/Secret Key girişi ve salt-okunur bağlantı testi açıktır; sipariş aktarımı/write sınırı görünür |
| `F6A-EV-010` contract/SIT | PASS_ORDER_SIT_READ | 2026-08-02 AWS çağrısı HTTP 200 ve boş zarf döndürdü. 2026-08-03'te merchant'a ait gerçek HBSKU ile SIT test siparişi oluşturuldu; sipariş listeleme çağrısı `totalCount=2/items=2` döndürdü. Canlı yanıttaki `merchantSKU` alan yazımı geriye uyumlu kabul edilerek mapper düzeltildi; üretim paneli 23:46'da `ConnectionTest=SUPPORTED` ve `ORDER_READ=SUPPORTED` kaydetti. Bütün write capability'leri `UNKNOWN`/kapalı kaldı |
| `F6A-EV-011` previous-platform gate | SUPERSEDED_BY_ADR_015 | Shopify production reconciliation/rollback kanıtı tamamlanmış değildir ve `DEFERRED` kalır; ADR-015 uyarınca Hepsiburada'nın kendi kanıtlarını ilerletmesini artık bloke etmez |
| `F6A-EV-012` local reconciliation/rollback | PASS_LOCAL_DRY | Ortak servis Hepsiburada bağlantısını no-write kuru kontrolde kabul eder; N11/Pazarama reddedilir; geri dönüş rehberi kayıt koruma ve yeniden açma kapılarını tanımlar |
| `F6A-EV-013` secret/auth boundary | PASS_LOCAL | Basic Auth yalnız istek anında oluşturulur; credential Data Protection ile şifrelenir ve API’de maskelenir; source/docs secret signature taraması temiz; production/write kapıları kapalı |
| `F6A-EV-014` package event safety | PASS_LOCAL_PROPERTY | Sipariş satırları ve miktar zinciri persistence öncesi doğrulanır; fazla/negatif/duplicate/bilinmeyen satır olayı atomik reddedilir; canonical ilerleme yalnız kabul edilen yerel paketlerden türetilir; eski zaman veya geriye durum geçişi uygulanmaz; event kimliği deterministiktir |
| `F6A-EV-015` durable job retry | PASS_POSTGRES_LOCAL | Bağlayıcı `PENDING/LEASED/RETRY_SCHEDULED/BLOCKED/DEAD/CANCELLED`, max-attempt, 15s/1m/5m/20m/1h + %10–20 deterministik jitter, expired-attempt kapanışı ve stale-token fencing PostgreSQL 18.4 üzerinde geçti |
| `F6A-EV-016` worker heartbeat fencing | PASS_BUILD_POSTGRES_LOCAL | İki dakikalık lease için 30 saniyelik heartbeat ayrı ve kısa DbContext scope’larında çalışır; lease kaybı yerel yürütmeyi iptal eder, completion yeni scope ile token/expiry fencing’den geçer ve kayıtlı correlation korunur; lease heartbeat/stale-token zinciri gerçek PostgreSQL testinde geçti |
| `F6A-EV-017` fail-closed port/error completeness | PASS_LOCAL | Connection, capability, reference, product, operation, order ve return read yöntemlerinin tamamı partner kanıtı olmadan aynı güvenli kapıda; archive dahil bütün write yöntemleri kapalıdır. 401/403, 404, 409, 429, 5xx, validation ve timeout sınıfları ayrı test edildi |

| `F6A-EV-018` order-read implementation | PASS_LOCAL_AND_SIT | Zorunlu kimlik, miktar, fiyat, KDV, müşteri, durum ve teslimat adresi alanları eksiksiz map edilir; eksik satırlar reddedilir. Canlı SIT `merchantSKU` alan yazımı için geriye uyumlu contract testi eklendi ve adapter contract seti `54/54` geçti. Kullanıcı tarafından oluşturulan `HB20260803203308` ile ikinci SIT siparişi `HB20260803203348` salt-okunur eşitlemede yerel sipariş kayıtlarına işlendi |
| `F6A-EV-019` immutable release and target read | PASS_TARGET_READ_ONLY | `c889ce9` commit'i için GitHub Actions run `30851224934` başarılı oldu; app `sha256:d4f2d79a…8c9a0`, edge `sha256:c6bff035…76d0` digest'leri AWS Ubuntu üretim hostuna dağıtıldı. API ve Caddy healthy, HTTPS readiness `200`; Hepsiburada bağlantı testi ve sipariş eşitleme işleri `SUCCEEDED`, hata kodu yok. Dış yazma anahtarları kapalı kaldı |

## Test sonucu

- .NET build: başarılı, `0` uyarı / `0` hata.
- Tam yerel test seti: `95/95` başarılı (Domain 20, Application 19, Adapter 45, API 2, Repository guard 2, PostgreSQL 7).
- PostgreSQL entegrasyonu: repository içindeki loopback-only izole PostgreSQL `18.4` cluster’ında test runner’ın oluşturup sildiği geçici veritabanıyla `7/7` geçti; kullanıcı/panel veritabanı değiştirilmedi.
- EF migration: modelde bekleyen değişiklik yok; F4 → F6A idempotent SQL üretimi ve güncel migration zincirinin boş geçici veritabanına uygulanması başarılı.
- Web: strict TypeScript/Vite production build ve component testi.
- `F6AJobRetryContract` migrationı, şartnamedeki eksik job state/max-attempt/index sözleşmesi ölçülebilir biçimde mevcut modelde bulunmadığı için oluşturuldu.
- `c889ce9` mapper düzeltmesi: adapter contract testleri `54/54`, solution build `0` uyarı / `0` hata. Tam suite içindeki container testleri yerel Docker engine bulunmadığı için bu değişiklikte yeniden koşturulamadı; hedef dağıtım health/readiness ve gerçek SIT read ile ayrıca doğrulandı.

## Faz durumu

F6A Sipariş SIT salt-okunur hattı `READY_ORDER_READ` durumundadır: bağlantı ve `ORDER_READ` capability'leri `SUPPORTED`, iki dolu SIT siparişi yerel generic sipariş modeline işlenmiştir. Bu kanıt katalog, stok/fiyat, package action, return veya webhook capability'lerini açmaz; bütün dış yazmalar kapalıdır. ADR-015 ile Shopify önceki-platform kapısı kaldırılmıştır; F6A tam çalışma için kendi SIT safe-write, target reconciliation, rollback ve production smoke kanıtlarını tamamlamalıdır. F6B ve F6C açılmamıştır.
