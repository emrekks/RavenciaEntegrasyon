# F6A - Hepsiburada Adaptörü Planı

## Belge durumu

| Alan | Değer |
| --- | --- |
| Faz | `F6A` |
| Plan durumu | `APPROVED` (2026-08-02) |
| Uygulama durumu | `READY_LOCAL_FAIL_CLOSED / BLOCKED_EXTERNAL / BLOCKED_PHASE_GATE` |
| Yetkili şartname | Repository kökündeki v3.2 PDF; özellikle sayfa 12, 42-47, 51-58, 60, 64-65, 67-70 ve 72-73 |
| Yetkili şartname SHA-256 | v3.4 `5A652AC34574A3310B844AECE647B96D350DD7AA79FDF3AC54C080827150EC51` |
| Zorunlu sıra | `F6A Hepsiburada → F6B N11 → F6C Pazarama`; aynı anda uygulanmaz veya ilk kez canlıya alınmaz |
| Önceki platform kapısı | F5 yerel çekirdeği `8bc29f3`; development-store production reconciliation/rollback kanıtı henüz `BLOCKED_EXTERNAL` |
| Hedef sonuç | Mevcut generic portları kullanan, Hepsiburada’ya özel sözleşmeleri yalnız Infrastructure adapter sınırında tutan, SIT odaklı ve bütün dış yazmaları kapalı başlayan F6A yerel çekirdeği |

Bu belge yalnız F6A planıdır. Kullanıcı uygulama onayıyla Hepsiburada fail-closed yerel çekirdeği oluşturulmuştur; partner kanıtı olmadan HTTP/credential/mapping genişletilmez. F6B N11, F6C Pazarama ve F7+ açılmamıştır.

## Yetkili faz yorumu

- F6 tek release değildir. Önce yalnız Hepsiburada uygulanır ve ayrı capability/test/reconciliation/rollback kapısından geçer.
- N11 ancak F6A çıkışı; Pazarama ancak F6B çıkışı tamamlandıktan sonra ayrı plan ve onayla açılabilir.
- İki adapter aynı anda ilk kez canlıya alınmaz.
- F6A tam çıkışı için önceki platform olan Shopify’ın production reconciliation/rollback kanıtı zorunludur. Bu eksiklik yerel plan ve fail-closed contract çalışmalarını durdurmaz; F6A SIT safe-write ve production smoke’u durdurur.
- Güncel partner dokümanı, partner hesabında görülen version/auth/scope bilgisi ve tarihli SIT kanıtı halka açık özetten üstündür.

## F6A hedefleri

- Mevcut `IConnectionPort`, `IReferenceDataPort`, `IProductPort`, `IInventoryPricePort`, `IOrderPort`, `IReturnPort` ve webhook/inbox/job sözleşmelerini değiştirmeden yeniden kullanmak.
- Hepsiburada auth, merchant scope, reference/mapping, product/listing, price/stock, order/package/return ve async task davranışlarını yalnız Infrastructure adapter içinde tutmak.
- Katalog ürünü ile satış listing/offer durumunu yerel Product/Variant ve ChannelOffer sınırlarında ayırmak.
- Async ürün dosyası/task/tracking sonucunu job, cursor ve reconciliation zincirinde idempotent işlemek.
- Polling ve varsa webhook olaylarını aynı Inbox/dedupe/state-invariant hattında birleştirmek.
- 429, 5xx, timeout, authentication, validation, business conflict, partial result ve contract drift sınıflarını ayrı hata kanalları olarak kaydetmek.
- Package allocation toplamlarının sipariş satırı miktarını aşmamasını ve duplicate/out-of-order olayların state’i geriye götürmemesini sağlamak.
- SIT/test hesabı olmadan capability’yi `SUPPORTED` yapmamak ve dış HTTP yazması üretmemek.

## Kapsam dışı

- F6B N11, F6C Pazarama, F7 raporlama, F7B kullanıcı/RBAC ve F8 aktif multi-tenant kodu, route’u, menüsü veya placeholder’ı.
- Hepsiburada E-Faturam, muhasebe, kampanya/promosyon, Satıcıya Sor, tedarikçi, HepsiJet/Hepsilojistik veya şartnamenin F6A çekirdek portlarına girmeyen ürünler.
- Partner hesabında doğrulanmamış endpoint, request/response alanı, enum, hata kodu, status mapping, auth akışı, rate limit veya production host.
- Katalog/listing silme, fiyat/stok yazma, paketleme, kargo, iade kararı veya production webhook gibi geri alınması zor etkileri kanıtsız açmak.
- F5 Shopify blockerlarını “tamamlandı” saymak veya F6A çalışmasıyla kapatmak.
- Yeni solution, servis, database, migration zinciri, mikroservis, Redis, RabbitMQ, Kafka veya Kubernetes.

## Mevcut repository durumu

- `main`, `origin/main` ile senkron; son yaşayan faz F5 ve çalışma ağacı plan öncesinde temizdir.
- Domain/Application generic entegrasyon portları F3’ten beri mevcuttur; Shopify bunların farklı platformda yeniden kullanımını kanıtlamıştır.
- Generic `PlatformConnection`, encrypted `PlatformCredential`, `PlatformCapability`, `ConnectionSyncPolicy`, `SyncCursor`, `IntegrationJob`, `InboxMessage`, `WebhookSubscription`, reconciliation, product/listing/offer, order/package ve return modelleri vardır.
- Infrastructure adapter dizinlerinde yalnız `Trendyol`, `TrendyolEFaturam` ve `Shopify` vardır. Hepsiburada, N11 veya Pazarama production adapterı yoktur.
- F6 route/menu/worker job/migration yoktur. Mevcut `/integrations` yüzeyi generic bağlantı/capability ekranıdır.
- Dış write anahtarları kapalıdır; gerçek Hepsiburada test hesabı, credential veya fixture repository’de yoktur.

## Gereksinim ve kabul matrisi

| Kimlik | Kaynak | Kabul ölçütü | Planlanan kanıt | Gelecek dosya/alan | Dış bağımlılık | Durum |
| --- | --- | --- | --- | --- | --- | --- |
| `F6A-REQ-001` | s.60, 64-65 | Yalnız F6A açılır; F6B/F6C yüzeyi yoktur. | Repository guard | Plan/guard | Kullanıcı uygulama onayı | DONE_LOCAL |
| `F6A-REQ-002` | s.42-45, 65, 73 | Current partner docs, auth modeli, environment, merchant scope ve version tarihli kaydedilir. | Source snapshot + SIT connection | Adapter README/capability matrix | Partner hesabı ve SIT credential | PARTIAL_LOCAL / BLOCKED_EXTERNAL |
| `F6A-REQ-003` | s.42-44, 65 | Reference/category/attribute/brand verisi generic reference porttan geçer; mapping scope/version taşır. | Anonim fixture contract testi | Adapter mapping | SIT reference payload | PLANNED / BLOCKED_EXTERNAL |
| `F6A-REQ-004` | s.21-22, 42-44, 65 | Product/variant/media ile listing/offer ayrıdır; async tracking sonucu idempotent işlenir. | Upload/task/partial fixture testleri | Product port + mapper | SIT katalog ürünü | PLANNED / BLOCKED_EXTERNAL |
| `F6A-REQ-005` | s.24-25, 42-44, 65 | Price ve stock ayrı yetenek/otorite; partial satır sonucu sessiz başarı değildir. | Mixed-result/no-write testleri | InventoryPrice port | Listing/offer SIT fixture | PLANNED / BLOCKED_EXTERNAL |
| `F6A-REQ-006` | s.25-27, 43-44, 65 | Order/package polling cursor/overlap ile generic order modeline map edilir. | Duplicate/out-of-order fixture | Order port/worker | SIT order fixture | PLANNED / BLOCKED_EXTERNAL |
| `F6A-REQ-007` | s.26-28, 43-44, 65 | Package quantity invariant korunur; izin verilmeyen state/action dış etki üretmez. | State/property testleri | Existing package model | SIT package action kanıtı | PASS_LOCAL_PROPERTY / ACTION BLOCKED_EXTERNAL |
| `F6A-REQ-008` | s.27-28, 43-44, 65 | Return/claim read ve action ayrı capability’dir; karar enum’u kanıtsız kodlanmaz. | Read/action fail-closed testleri | Return port | SIT claim fixture ve otorite | PASS_LOCAL_FAIL_CLOSED / CONTRACT BLOCKED_EXTERNAL |
| `F6A-REQ-009` | s.30-31, 43, 65 | Polling/webhook duplicate ve out-of-order aynı Inbox/state hattında güvenlidir. | Auth/raw-body/dedupe testleri | Webhook verifier/job | Public SIT callback/credential | PARTIAL_LOCAL_STATE / WEBHOOK BLOCKED_EXTERNAL |
| `F6A-REQ-010` | s.31-33, 43-44, 65 | Async task/batch/polling retry aynı dış etkiyi tekrar üretmez; reconciliation açıklanabilirdir. | Worker-kill/retry/checkpoint testi | Job/cursor/reconciliation | SIT task result fixture | PASS_LOCAL_POSTGRES_RETRY_HEARTBEAT / TASK BLOCKED_EXTERNAL |
| `F6A-REQ-011` | s.43, 51-54, 65 | Auth expiry, 429, 5xx, timeout, validation ve business conflict ayrıdır. | Error mapper matrix | Adapter error mapping | Tarihli gerçek response | PASS_LOCAL_CLASSIFIER / CONTRACT BLOCKED_EXTERNAL |
| `F6A-REQ-012` | s.51-54, 65, 67 | Credential encrypted/masked; secret/PII log/API/fixture/manifest’e sızmaz. | Secret/PII scan | Security/adapter | Credential türü kararı | DONE_LOCAL_FAIL_CLOSED / CREDENTIAL BLOCKED_EXTERNAL |
| `F6A-REQ-013` | s.43, 55-58, 65 | Global + connection + capability + business-authority kapıları olmadan write job/HTTP yoktur. | No-HTTP/kill-switch testleri | Existing controls | Safe-write onayı | DONE_LOCAL_FAIL_CLOSED |
| `F6A-REQ-014` | s.43, 58, 65 | Read-only reconciliation ve rollback runbook’u açıklanmıştır. | Local dry-run + runbook review | Reconciliation/README | Önceki platform prod kanıtı | PARTIAL_LOCAL / BLOCKED_PHASE_GATE |
| `F6A-REQ-015` | s.39-41, 65 | Mevcut integrations UI loading/empty/error/UNKNOWN gösterir; yalnız kanıtlı action görünür. | Component/a11y/route guard | Existing Web surface | Stitch F6 referansı yok | DONE_BUILD |
| `F6A-REQ-016` | s.65, 67-70 | SIT safe-write ve kullanıcı onaylı düşük adet smoke audit/correlation/rollback ile yapılır. | E2E evidence log | Runbook/evidence | Test hesabı + işlem bazlı onay | BLOCKED_EXTERNAL |

## Resmî kaynak doğrulaması

Doğrulama tarihi `2026-08-02`dir. URL erişimi capability desteği veya production yetkisi sayılmaz.

| Kod | Resmî kaynak | Bu planda doğrulanan sınır |
| --- | --- | --- |
| `HB-P1` | <https://developers.hepsiburada.com/tr/companies/hepsiburada> | Portal; katalog, listing, sipariş ve muhasebe ürün ailelerinin varlığı |
| `HB-P2` | <https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=katalog-onemli-bilgiler&product=katalog-urun-entegrasyonu&view=guide> | Katalog v1.0, Basic Auth anlatımı, merchant scope, async JSON file/tracking ve status/reference davranışı |
| `HB-P3` | <https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=katalog-urun-entegrasyonu-test-sureci-adimlari&product=katalog-urun-entegrasyonu&view=guide> | SIT kullanıcı bilgisi, User-Agent ve test süreci; SIT/prod kategori ağacının yeniden okunması |
| `HB-P4` | <https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=siparis-entegrasyonu-onemli-bilgiler&product=siparis-olusturma-entegrasyonu&view=guide> | Sipariş/package SIT davranışı, Basic Auth anlatımı, 429 header ve test siparişi sınırı |
| `HB-P5` | <https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=siparis-webhook-modeli&product=siparis-olusturma-entegrasyonu&view=guide> | Webhook için ayrı test süreci, callback BaseURL ve inbound Basic Auth anlatımı |
| `HB-P6` | <https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=talep-onemli-bilgiler-2&product=talep-entegrasyonu&view=guide> | Return/claim read/action ayrımı ve SIT/prod credential gereği |
| `HB-P7` | <https://developers.hepsiburada.com/tr/search> | Güncel guide/API reference envanteri ve auth-change bildirimi |

Portal başlangıç yüzeyindeki client-credentials örneği ile marketplace guide’larındaki Basic Auth anlatımı aynı auth sözleşmesi olarak kabul edilmez. Partner hesabında servis anahtarı ekranı, ürün ailesi, environment ve gerçek SIT çağrısı birlikte doğrulanana kadar auth türü ve token endpoint’i production koduna alınmaz.

## Planlanan teslimatlar

- `docs/implementation/F6A-evidence-log.md`
- `src/MarketplaceHub.Infrastructure/Adapters/Hepsiburada/README.md`
- Hepsiburada auth/context, error mapping, contract mapper ve generic port adapterı
- Yalnız gerekli platform-local job type/dispatch yönlendirmesi; yeni queue veya service yok
- Anonim, küçültülmüş katalog/listing/order/package/return ve partial/error fixture’ları ile checksum kaydı
- Generic connection/capability/reconciliation servislerinin Hepsiburada yönlendirmesi
- Mevcut `/integrations` ekranında Hepsiburada bağlantı seçimi; F6B/F6C seçeneği yok
- Adapter/boundary/persistence/API/Web/repository guard testleri
- Capability matrix, external dependency, risk ve environment-secret katalog güncellemesi
- Migration yalnız generic mevcut modelin ölçülebilir biçimde yetmemesi halinde ve şartname veri modeli içinde; başlangıç varsayımı `NO_MIGRATION`

## Uygulama sırası

1. Partner hesabı olmadan kodlanabilen boundary, fake/fixture, error classification ve no-write testleri.
2. Auth modeli ve SIT merchant scope partner hesabında doğrulanır; credential türü kesinleştirilir.
3. Reference/category/attribute snapshot ve mapping contract’ı kanıtlanır.
4. Product/listing async task read sonucu uygulanır; write kapalı kalır.
5. Order/package/return read polling ve reconciliation uygulanır.
6. Varsa webhook, gerçek callback auth kontratıyla Inbox hattına eklenir.
7. Her write capability ayrı safe-write kanıtı ve işlem bazlı kullanıcı onayıyla açılır.
8. F6A çıkışı tamamlanmadan F6B planı başlatılmaz.

## Test ve kanıt planı

- Domain/Application sınır testi: Hepsiburada’ya özel tip, status veya DTO generic katmanlara sızmaz.
- Contract fixture: reference, product/listing task, mixed/partial result, order/package, cancelled/delivered ve return/claim.
- Auth: yanlış/expired credential; environment/merchant mismatch; token/Basic kararı kanıtlanana kadar no-HTTP.
- Resilience: 429 header, 5xx, timeout, malformed JSON, validation ve business conflict ayrı sonuç.
- Idempotency: aynı product task, order event, webhook ve action tekrarı tek yerel/dış etki.
- State: out-of-order paket/iade olayı mevcut canonical state’i geriye götürmez.
- Quantity property: package allocation toplamı ordered quantity’yi aşmaz.
- Security: secret, customer e-posta/adres/telefon ve gerçek merchant verisi fixture/log/API’de yoktur.
- Persistence: mevcut generic tablolarla fresh/upgrade ve tenant FK/UQ/check; migration yoksa model snapshot farkı sıfır.
- Web/API: yalnız mevcut generic route ailesi; loading/empty/error/UNKNOWN ve fail-closed action görünürlüğü.
- SIT: read-only reference/order/reconciliation; ardından yalnız ayrı kullanıcı onayıyla düşük adet safe-write ve rollback.

## Riskler

- `RISK-F6A-001`: Portal başlangıç auth örneği ile marketplace guide Basic Auth anlatımı farklı ürün ailelerine veya geçiş dönemine ait olabilir. Yanlış auth seçimi credential sızıntısı veya erişim hatası doğurur; partner hesabı kanıtına kadar seçim yapılmaz.
- `RISK-F6A-002`: Katalog ürünü ve listing/offer aynı şey değildir. Bunları tek state yapmak yanlış stok/fiyat/yayın etkisi doğurur.
- `RISK-F6A-003`: Async ürün import’u kabul yanıtı ile satır başarısını ayırır. Tracking sonucu alınmadan başarı ilan edilmez.
- `RISK-F6A-004`: SIT ve production kategori/reference verileri farklı olabilir. Environment değişince snapshot/mapping/capability invalid edilir.
- `RISK-F6A-005`: Sipariş/package olayları duplicate veya out-of-order olabilir. Quantity/state invariant ve reconciliation olmadan geriye gidiş oluşabilir.
- `RISK-F6A-006`: Return/claim action sözleşmeleri ve neden değerleri değişebilir. Partner fixture olmadan enum/action kodlanmaz.
- `RISK-F6A-007`: Public webhook Basic credential paylaşımı ve callback yüzeyi yeni inbound trust boundary’dir. HTTPS, secret rotation ve raw request kanıtı olmadan açılmaz.
- `RISK-F6A-008`: F5 production reconciliation/rollback tamamlanmadan F6A’yı canlıya almak şartname çıkış kapısını ihlal eder.

## Açık kararlar ve blockerlar

| Kimlik | Gerekli karar/girdi | Güvenli fallback | Etki |
| --- | --- | --- | --- |
| `BLOCK-F6A-001` | Hepsiburada partner/SIT hesabı, merchant ID ve ürün aileleri | Capability `UNKNOWN`, write off | Contract E2E ve bütün dış çağrılar |
| `BLOCK-F6A-002` | Güncel auth modeli: servis anahtarlı Basic mi, client credentials mı, ürün ailesine göre ikisi mi | Endpoint/auth kodlama yok | Connection test ve credential payload |
| `BLOCK-F6A-003` | Granted scope/permission ve SIT/production host/version kaydı | Yalnız fixture | Capability evidence |
| `BLOCK-F6A-004` | Anonim reference/product/listing/order/package/return payload’ları | Sentetik contract shape yazılmaz | Mapping testleri |
| `BLOCK-F6A-005` | Stok/fiyat/product/package/return iş otoriteleri ve rollback yöntemi | Tüm write off | Safe-write |
| `BLOCK-F6A-006` | Public HTTPS webhook callback ve inbound credential | Polling + reconciliation planı | Webhook E2E |
| `BLOCK-F6A-007` | F5 Shopify production reconciliation/rollback kanıtı | F6A production release yok | Faz çıkışı |
| `BLOCK-F6A-008` | Docker engine veya ayrılmış yerel PostgreSQL test credential’ı yok | Migration SQL/model doğrulaması; migration uygulanmaz | Fresh/upgrade ve worker-kill PostgreSQL revalidation |
| `DEC-F6A-001` | Katalog create ve listing/offer write başlangıçta birlikte mi, ayrı capability dalgalarıyla mı açılacak? | Ayrı; ikisi de off | Release kapsamı |
| `DEC-F6A-002` | Package/return dış aksiyon otoritesi MarketplaceHub mı portal operasyonu mu? | Read-only | Action UI/job |

## ADR etkisi

- ADR-001, ADR-003, ADR-004, ADR-005, ADR-006 ve ADR-007 değiştirilmeden uygulanır.
- Auth ürünü/trust boundary partner kanıtıyla netleştiğinde Adapter README’ye kaydedilir; mevcut secret modelini veya inbound trust boundary’yi esaslı değiştirirse ayrı ADR gerekir.
- Yeni servis, database, cache, queue veya deployment topolojisi önerilmez.

## F6A çıkış kriterleri

| Kimlik | Ölçülebilir koşul | Kanıt | Plan durumu |
| --- | --- | --- | --- |
| `F6A-EXIT-001` | F6B/F6C yoktur; Hepsiburada adapterı generic portlar ve ayrı capability kaydı kullanır. | Boundary/repository guard | PASS_LOCAL |
| `F6A-EXIT-002` | Contract/auth-expiry/429/5xx/timeout/validation ve partial-result testleri geçer. | Contract/resilience suite | PASS_LOCAL_CLASSIFIER / PARTIAL_RESULT BLOCKED_EXTERNAL |
| `F6A-EXIT-003` | Duplicate/out-of-order ve package quantity invariant güvenlidir. | Property/integration testleri | PASS_LOCAL_PROPERTY / SIT BLOCKED_EXTERNAL |
| `F6A-EXIT-004` | SIT safe-write düşük adet kullanıcı onayıyla; read-only reconciliation ve rollback kanıtlıdır. | Tarihli E2E/evidence log | BLOCKED_EXTERNAL |
| `F6A-EXIT-005` | Önceki Shopify production reconciliation/rollback tamamdır; açıklanamayan kritik fark yoktur. | F5 production evidence | BLOCKED_PHASE_GATE |
| `F6A-EXIT-006` | F6A tek yeni canlı adapterdır; production smoke sonrası rollback/reconciliation temizdir. | Go/No-Go kaydı | BLOCKED_EXTERNAL |

## Plan sonucu ve uygulama kapısı

F6A planı kullanıcı tarafından 2026-08-02 tarihinde onaylandı. Endpoint/auth/payload uydurmadan generic portları uygulayan no-HTTP/no-write adapter çekirdeği, draft connection/capability kaydı ve integrations UI durumu tamamlandı: sonuç `READY_LOCAL_FAIL_CLOSED`dır. Gerçek auth/endpoint mapping, SIT safe-write ve production çıkışı `BLOCKED_EXTERNAL`, F5 production reconciliation/rollback nedeniyle tam faz çıkışı ayrıca `BLOCKED_PHASE_GATE`dir.

F6B N11 ve F6C Pazarama açılmamıştır. Hepsiburada partner/SIT kanıtı gelmeden fail-closed sınırın ötesinde HTTP veya credential implementasyonu yapılmaz.
