# F5 - Shopify Adaptörü Planı

## Belge durumu

| Alan | Değer |
| --- | --- |
| Faz | `F5` |
| Plan durumu | `DEFERRED_BY_ADR_015` (2026-08-03; tarihsel plan korunur) |
| Uygulama durumu | `READY_LOCAL_CORE / DEFERRED / BLOCKED_EXTERNAL` |
| Yetkili şartname | v3.5 PDF içindeki ADR-015 revizyonu ve devamında korunan v3.2 F5 tabanı |
| Yetkili şartname SHA-256 | v3.5 `DDA0DBE58555EB323A84A6E2C5449133FAF8584979BD8DB795DFEE587AED8B58` |
| Ön koşul | F4 yerel çekirdeği `5a1be44`; localhost giriş düzeltmesi `237c15d`; F4 dış kanıtları açık blocker olarak kayıtlı |
| Hedef sonuç | Generic Domain/Application portlarının Shopify üzerinde yeniden kullanılabildiğini kanıtlayan, development-store odaklı ve dış yazmaları kapalı başlayan F5 adaptörü |

Bu belge F5'in tarihsel uygulama planıdır. ADR-015 uyarınca Shopify aktif kapsam dışındadır; yeni production kodu, migration, endpoint, menü, worker dispatch, doğrulama veya safe-write çalışması yeni işletme sahibi kararı olmadan başlatılmaz. Mevcut yerel kod ve kanıt korunur; eksik blocker'lar tamamlanmış sayılmaz.

## F5 hedefleri

- Mevcut katalog, ürün/variant/media, fiyat/stok, sipariş ve fulfillment portlarını değiştirmeden Shopify Admin GraphQL API ile çalıştırmak.
- Shopify'a özgü GraphQL tiplerini, GID'leri, `userErrors`, HMAC header'larını, staged upload ve JSONL ayrıntılarını yalnız Infrastructure adapter sınırında tutmak.
- Admin GraphQL API sürümünü `2026-07` olarak pinlemek; istek ve yanıtta sürüm eşleşmesini kanıtlamak ve upgrade notunu kaydetmek.
- Product/Variant/Media, bulk import, InventoryItem/Location, price/stock, Order/Fulfillment ve webhook capability'lerini ayrı ayrı kanıtlamak.
- Bulk JSONL giriş/sonuç akışını tüm dosyayı belleğe almadan işlemek; restart sonrasında aynı dış etkiyi tekrar üretmeden devam etmek.
- Webhook'ta raw body HMAC doğrulaması, duplicate/out-of-order toleransı, inbox dedupe ve periyodik reconciliation uygulamak.
- Location mapping eksikse stok/fulfillment yazmasını güvenli biçimde durdurmak.
- Development store fixture ve E2E kanıtı olmadan hiçbir Shopify capability'sini `SUPPORTED` veya dış yazmayı açık saymamak.

## Kapsam dışı

- F6 Hepsiburada/N11/Pazarama, F7 ileri raporlama, F7B kullanıcı/RBAC ve F8 aktif multi-tenant production kodu veya görünür yüzeyi.
- Shopify tema, Liquid, Storefront API, Checkout extension, ödeme, abonelik/billing, POS, Shopify Functions veya App Store dağıtımı.
- Doğrulanmamış GraphQL query/mutation, field, enum, scope, webhook topic, limit veya hata eşlemesi.
- Test kanıtı olmadan ürün silme, publication açma, stok/fiyat yazma, fulfillment oluşturma veya production webhook aboneliği.
- Shopify staged-upload modelini Domain/Application'a taşımak veya generic `FileAsset`/private storage sınırını değiştirmek.
- Mikroservis, Redis, RabbitMQ, Kafka, Kubernetes, yeni solution, yeni database veya ikinci migration zinciri.

## Mevcut repository durumu

- Branch `main`, upstream `origin/main`; plan başlangıcında worktree temiz ve son commit `237c15d`'dir.
- Tek solution, modüler monolit, API + Worker ve tek PostgreSQL migration zinciri korunmaktadır.
- Generic `PlatformConnection`, `PlatformCredential`, `PlatformCapability`, `WebhookSubscription`, `SyncCursor`, `IntegrationJob`, `InboxMessage`, `ExternalEffectRecord`, `ReconciliationRun/Difference` modelleri hazırdır.
- Product/Variant/Media, `MarketplaceProductLink`, `MarketplaceVariantLink`, `ChannelListingProfile`, `ConnectionLocationMapping`, `ChannelOffer`, Order ve Shipment/Fulfillment'a karşılık gelen generic iş modelleri hazırdır.
- Application katmanında `IConnectionPort`, `IReferenceDataPort`, `IProductPort`, `IInventoryPricePort`, `IOrderPort` ve ilgili generic adapter sonuç/hata sözleşmeleri bulunmaktadır.
- Shopify adapter klasörü, Shopify credential türü, GraphQL contract/fixture, F5 worker akışı ve Shopify seçeneği UI'da henüz yoktur.
- F4 external blocker'ları açık kalmaktadır. F5 planı bunları kapatmaz ve F0-F4 production kabulünü `PASSED` yapmaz.

## Gereksinim matrisi

| Kimlik | Kaynak | Kabul ölçütü | Planlanan kanıt | Dosya/modül | Dış bağımlılık | Durum |
| --- | --- | --- | --- | --- | --- | --- |
| `F5-REQ-001` | s.45, 64 | Domain/Application'da Shopify tipi, enum'u veya GraphQL DTO'su yoktur; mevcut generic portlar kullanılır. | Boundary/source guard | Domain/Application/Adapter | Yok | PLANNED |
| `F5-REQ-002` | s.42, 45-46, 64 | Admin GraphQL sürümü `2026-07` pinlidir; istek URL'si ve `X-Shopify-API-Version` kanıtı eşleşir, fall-forward başarı sayılmaz. | Contract/header testi | Adapter/Capability | Resmî sürüm kaynağı | PLANNED |
| `F5-REQ-003` | s.42, 51-52, 64 | Shop scope, auth modeli, granted scope ve credential şifreli/versiyonlu tutulur; token log/API/UI'ya dönmez. | Secret/redaction + auth fixture | Connection/Security/Adapter | Dev Dashboard app/store | BLOCKED_EXTERNAL |
| `F5-REQ-004` | s.22-23, 45, 64 | Product/Variant/Media generic modele deterministic çevrilir; Shopify GID yalnız external link/adapter sınırındadır. | GraphQL fixture/contract mapping | Adapter/Catalog | Development store fixture | PLANNED |
| `F5-REQ-005` | s.47, 64 | Shopify staged media akışı adapter içinde kalır; yalnız doğrulanmış HTTPS/private signed delivery girdisi kullanılır. | Media/staged-upload fixture ve URL guard | Adapter/Files | Public HTTPS veya dev tunnel | BLOCKED_EXTERNAL |
| `F5-REQ-006` | s.23, 25, 57, 64 | Bulk JSONL satır-satır üretilir/okunur; operation id, input hash, line sonucu ve restart checkpoint'i korunur. | Büyük fixture, restart ve partial-result testi | Adapter/Worker/Jobs | Development store | PLANNED |
| `F5-REQ-007` | s.22-23, 29, 64 | InventoryItem/Location eşlemesi yoksa price/stock/fulfillment yazması dış HTTP üretmeden bloklanır. | Missing-location ve cross-location testleri | Adapter/Inventory | Onaylı Shopify location | BLOCKED_INPUT |
| `F5-REQ-008` | s.29, 45-46, 64 | Stok ve fiyat generic otorite/safety-stock/version kurallarıyla publish edilir; stale projection veya currency uyuşmazlığı bloklanır. | Projection/version/idempotency testi | Inventory/Application/Adapter | Yazma capability kanıtı | PLANNED |
| `F5-REQ-009` | s.25, 45-46, 64 | Order read mevcut Order aggregate'ına idempotent ingest edilir; raw Shopify state domain enum'u yapılmaz. | Duplicate/out-of-order order fixture | Adapter/Sales | Development store test order | PLANNED |
| `F5-REQ-010` | s.25, 45-46, 64 | Fulfillment yalnız doğrulanmış fulfillment order/location/scope ile ve external-effect idempotency altında çalışır. | Duplicate, timeout, `userErrors` ve reconciliation testi | Adapter/Worker/Sales | Development store fulfillment | BLOCKED_EXTERNAL |
| `F5-REQ-011` | s.23, 25, 51, 57, 64 | Webhook raw body üzerinde HMAC doğrulanır; invalid HMAC reddedilir, webhook/event kimlikleri dedupe/correlation için saklanır. | HMAC valid/invalid/rotation fixture | API/Inbox/Adapter | App client secret | PLANNED |
| `F5-REQ-012` | s.25, 46, 64 | Duplicate ve out-of-order webhook sessiz overwrite üretmez; watermark ve periyodik read reconciliation eksik olayı yakalar. | Shuffle/replay/reconciliation testi | Inbox/Worker/Reconciliation | Development store webhook | PLANNED |
| `F5-REQ-013` | s.45-46, 64 | Transport GraphQL errors, mutation `userErrors`, auth, throttle, remote 5xx ve contract ihlali ayrı safe error sınıflarına çevrilir. | Error fixture matrisi | Adapter/ErrorMapping | Resmî schema | PLANNED |
| `F5-REQ-014` | s.42, 46, 57, 64 | Capability'ler başlangıçta `UNKNOWN`; read ve write ayrı; credential/version/scope değişince kanıtlar invalid olur. | Capability transition/no-HTTP testi | Connection/Capability | Development store | PLANNED |
| `F5-REQ-015` | s.38-41, 64 | Mevcut connection API/UI Shopify bağlantısını ve kanıt durumunu gösterir; F6+ rota/menü yoktur. | API surface, component ve route guard | API/Web | Stitch yeni ekran gerektirmez | PLANNED |
| `F5-REQ-016` | s.55-58, 64, 67 | Tek migration zinciri korunur; generic tablo yeterliyse migration üretilmez, gerekiyorsa yalnız kanıtlı minimum F5 migration fresh/upgrade geçer. | EF model diff + PostgreSQL fresh/upgrade | Persistence | Model diff sonucu | PLANNED |
| `F5-REQ-017` | s.46, 64 | Product/inventory/order/fulfillment reconciliation farkları açıklanabilir; rollback dış yazmayı kapatır ve veriyi sessiz değiştirmez. | Dry-run + kill-switch/rollback testi | Reconciliation/Operations | Development store | PLANNED |
| `F5-REQ-018` | s.64, 67-70 | Adapter README, version/upgrade notu, runbook, anonim fixture checksum'ları ve faz kanıt günlüğü tamamlanır. | Doküman/secret/source guard | Docs/Adapter | Yok | PLANNED |

## Resmî kaynak ve sürüm doğrulaması

Doğrulama tarihi `2026-08-02`'dir. Kod ve contract fixture'ları `latest`, release-candidate veya `unstable` kullanmayacak; açıkça `2026-07` hedefleyecektir. Shopify'ın resmî sürüm tablosuna göre `2026-07`, 1 Temmuz 2026'da yayımlanmış stabil sürümdür ve 16 Temmuz 2027 15:00 UTC'ye kadar erişilebilirdir.

| Alan | Resmî kaynak | Plan girdisi | Başlangıç durumu |
| --- | --- | --- | --- |
| API versioning | <https://shopify.dev/docs/api/usage/versioning> | Admin GraphQL `2026-07`; response version header doğrulaması; quarterly review | VERIFIED_SOURCE / CAPABILITY_UNKNOWN |
| Dev Dashboard | <https://shopify.dev/docs/apps/build/dev-dashboard> | API-only app ve credential yönetimi | ACCOUNT_REQUIRED |
| Development store | <https://shopify.dev/docs/apps/build/dev-dashboard/stores/development-stores> | Yalnız geliştirme/test; gerçek production kanıtı değildir | STORE_REQUIRED |
| Dev Dashboard token | <https://shopify.dev/docs/apps/build/dev-dashboard/get-api-access-tokens> | App/store aynı organization ise client-credentials uygulanabilir; aksi auth modeli ayrıca doğrulanır | OPEN_DECISION |
| GraphQL bulk query | <https://shopify.dev/docs/api/usage/bulk-operations/queries> | Streaming export, operation-id ve sonuç JSONL | UNKNOWN |
| GraphQL bulk import | <https://shopify.dev/docs/api/usage/bulk-operations/imports> | `stagedUploadsCreate`, JSONL ve bulk mutation akışı | UNKNOWN |
| Product model | <https://shopify.dev/docs/api/admin-graphql/2026-07/objects/Product> | Product/Variant/Media contract kaynağı | UNKNOWN |
| Staged upload | <https://shopify.dev/docs/api/admin-graphql/2026-07/mutations/stagedUploadsCreate> | Media ve bulk upload adapter içi | UNKNOWN |
| Inventory set | <https://shopify.dev/docs/api/admin-graphql/2026-07/mutations/inventorySetQuantities> | Location ve concurrency/compare davranışı fixture ile doğrulanır | UNKNOWN |
| Orders | <https://shopify.dev/docs/api/admin-graphql/2026-07/queries/orders> | Cursor/filter read ve reconciliation kaynağı | UNKNOWN |
| Fulfillment | <https://shopify.dev/docs/api/admin-graphql/2026-07/mutations/fulfillmentCreate> | Scope/location/fulfillment-order kapısı | UNKNOWN |
| Webhook doğrulama | <https://shopify.dev/docs/apps/build/webhooks/verify-deliveries> | Raw body HMAC, duplicate delivery kimliği, hızlı ack ve retry davranışı | UNKNOWN |
| Webhook davranışı | <https://shopify.dev/docs/apps/build/webhooks> | Ordering garantisi yok; reconciliation zorunlu | UNKNOWN |

Exact access scope listesi tahmin edilmez. Seçilen `2026-07` query/mutation field setinin resmî reference gereksinimleri çıkarılır, development app'e minimum olarak verilir ve granted scope response'u capability kanıtına kaydedilir.

## Capability ve güvenli çalışma planı

1. `ConnectionTest`, `ProductRead`, `ProductWrite`, `MediaWrite`, `BulkRead`, `BulkWrite`, `InventoryRead`, `InventoryWrite`, `PriceWrite`, `OrderRead`, `OrderWebhook`, `FulfillmentRead` ve `FulfillmentWrite` başlangıçta `UNKNOWN` kalır.
2. Source URL + pinned version + minimum scope + anonim fixture tek başına write capability'yi `SUPPORTED` yapmaz; development-store safe-write ve sonucu geri okuma kanıtı gerekir.
3. Global external-write ve connection external-write anahtarlarından biri kapalıysa mutation, staged upload, webhook subscription veya fulfillment dış HTTP üretmez.
4. Product delete capability F5 başlangıcında `UNKNOWN` ve kapalıdır. Arşiv/unpublish/delete politikası açıkça onaylanmadan delete mutation planlanmaz.
5. Location mapping yoksa inventory ve fulfillment işleri job oluştursa bile adapter çağrısı öncesi güvenli hata/OperationalIssue ile durur.
6. GraphQL HTTP 200 yanıtı başarı sayılmaz; top-level errors ve mutation payload `userErrors` boş değilse işlem başarısız/partial olarak kaydedilir.
7. Bulk input hash + operation id + satır kimliği idempotency kapsamıdır. Worker restart yeni bulk operation başlatmadan önce mevcut operation'ı sorgular.
8. Webhook HMAC doğrulanmadan body parse veya inbox business dispatch yapılmaz. Duplicate teslimat başarıyla ack edilip ikinci iş etkisi üretmez.
9. Webhook ordering'e güvenilmez; remote update zamanı/watermark ve scheduled reconciliation ile geriye giden state sessiz uygulanmaz.
10. Credential, shop scope veya pinned API version değişince bütün capability kanıtları `UNKNOWN` olur ve dış yazmalar kapanır.

## Planlanan dosya etkisi

### Oluşturulacak

- `src/MarketplaceHub.Infrastructure/Adapters/Shopify/README.md`
- `src/MarketplaceHub.Infrastructure/Adapters/Shopify/ShopifyOptions.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/Shopify/ShopifyAuthenticationHandler.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/Shopify/ShopifyGraphQlClient.cs`
- Adapter `Contracts/`, `Mapping/`, `Ports/`, `ErrorMapping/`, `Bulk/` ve anonim `Fixtures/` yolları
- Gerekirse yalnız Infrastructure/Worker içinde Shopify adapter dispatch bileşenleri; Domain/Application'da Shopify adlı tip yok
- F5 adapter contract, worker restart, HMAC/inbox, API surface, persistence ve E2E test dosyaları
- `docs/implementation/F5-evidence-log.md`
- `docs/runbooks/shopify-operations.md`

### Değiştirilecek

- Infrastructure DI adapter seçimi, connection service platform allow-list'i ve credential koruması.
- Worker'ın generic connection/product/inventory/order/fulfillment işlerini connection platformuna göre adapter'a yönlendirmesi.
- Mevcut `/connections` API/UI yüzeyi; Shopify shop scope, pinned version ve capability görünürlüğü kadar.
- Capability matrix, traceability matrix, risk/external dependency ve environment-secret kataloğu.
- AppDbContext/model snapshot yalnız EF model diff gerçekten gerekli bir kalıcı alan gösterirse; aksi halde migration yok.

### Oluşturulmayacak

- Domain veya Application içinde `Shopify*` sınıfı/enum/DTO; ayrı Shopify controller/domain aggregate'i.
- F6+ adapter, route, menü veya placeholder; yeni solution/service/database; aktif tenant/user yönetimi.
- Shopify tema, storefront, ödeme veya App Store yüzeyi.

## Test ve kanıt planı

| Kanıt | Senaryo | Beklenen sonuç | Durum |
| --- | --- | --- | --- |
| `F5-EV-001` | Format + warnings-as-errors build | 0 warning, 0 error | PLANNED |
| `F5-EV-002` | Repository/domain/application source guard | Domain/Application'da Shopify tipi yok; F6+ yok | PLANNED |
| `F5-EV-003` | Pinned version/header contract | Request/response `2026-07`; fall-forward fail-closed | PLANNED |
| `F5-EV-004` | Product/variant/media fixture mapping | Generic model deterministic; GID adapter sınırında | PLANNED |
| `F5-EV-005` | GraphQL errors + `userErrors` matrisi | Partial/validation/auth/throttle ayrışır; sahte başarı yok | PLANNED |
| `F5-EV-006` | Büyük JSONL streaming + worker restart | Sınırlı bellek; aynı operation devam eder; satır sonucu izlenebilir | PLANNED |
| `F5-EV-007` | Missing location | Inventory/fulfillment dış HTTP yok; açıklanabilir block | PLANNED |
| `F5-EV-008` | Stock/price stale version ve duplicate | Tek dış etki; eski projection reddedilir | PLANNED |
| `F5-EV-009` | HMAC valid/invalid + raw body | Invalid reddedilir; valid yalnız bir inbox kaydı üretir | PLANNED |
| `F5-EV-010` | Duplicate/out-of-order webhook | Sessiz state geri alma yok; reconciliation farkı kaydeder | PLANNED |
| `F5-EV-011` | Secret/token/PII/source scan | Token, secret, müşteri PII ve gerçek store verisi fixture/log/API'da yok | PLANNED |
| `F5-EV-012` | Fresh/upgrade EF doğrulaması | Migration yoksa model pending değil; varsa tek zincir ve fresh/upgrade geçer | PLANNED |
| `F5-EV-013` | API/UI/route/a11y guard | Mevcut connection yüzeyi; F6+ görünmez | PLANNED |
| `F5-EV-014` | Development store Product/Bulk/Inventory/Order/Fulfillment E2E | Her capability ayrı kanıtlanır veya açık blocker kalır | BLOCKED_EXTERNAL |
| `F5-EV-015` | Reconciliation + kill switch/rollback | Farklar açıklanabilir; write-off sonrası dış etki yok | PLANNED |
| `F5-EV-016` | Version/upgrade note ve README | 2026-07 destek sonu, review tarihi ve upgrade testi kayıtlı | PLANNED |

## Dış bağımlılıklar ve blockerlar

| Kimlik | Kayıt | Güvenli fallback | Yerel blocker? |
| --- | --- | --- | --- |
| `BLOCK-F5-001` | Shopify Dev Dashboard organization/app ve development store yok. | Fake/contract; tüm capability `UNKNOWN`; write off | Hayır; E2E için evet |
| `BLOCK-F5-002` | Auth modeli kesinleşmedi: aynı organization API-only app mi, başka merchant OAuth dağıtımı mı? | Credential production kodu yalnız doğrulanmış model kadar açılır | Auth E2E için evet |
| `BLOCK-F5-003` | Shop'ın canonical `*.myshopify.com` scope'u ve granted scope listesi sağlanmadı. | Shop domain/endpoint tahmin edilmez | Connection test için evet |
| `BLOCK-F5-004` | Shopify Location ile yerel tek depo eşlemesi yok. | Inventory/Fulfillment write kapalı | Bu write'lar için evet |
| `BLOCK-F5-005` | Public HTTPS/dev tunnel yok. | HMAC fixture + reconciliation; gerçek webhook subscription kapalı | Webhook E2E için evet |
| `BLOCK-F5-006` | Product publish/archive/delete ve Shopify fulfillment otoritesi onaylanmadı. | Read-only; delete/fulfillment off | İlgili write için evet |

## Riskler

- `RISK-F5-001`: Shopify `latest` doküman URL'leri zamanla başka schema'ya döner. Kod, fixture ve kanıt `2026-07` ile pinlenir; quarterly review yapılır.
- `RISK-F5-002`: GraphQL HTTP 200 içinde top-level veya mutation-level hata bulunabilir. `userErrors` incelenmeden başarı kaydetmek veri kaybı ve sessiz partial sonuç üretir.
- `RISK-F5-003`: Bulk operation kısmen tamamlanabilir ve sonuç URL'leri geçicidir. Satır sonucu indirimi/checksum ve checkpoint kaydı yapılmadan retry yeni dış etki üretebilir.
- `RISK-F5-004`: Product delete geri döndürülemez ve bağlı variant/media/inventory verisini etkiler. Açık policy ve safe-write kanıtı olmadan kapalıdır.
- `RISK-F5-005`: Webhook teslim sırası garanti değildir ve duplicate olabilir. Inbox dedupe + watermark + reconciliation olmadan state geriye gidebilir.
- `RISK-F5-006`: Development-store client credentials yalnız app ve store aynı organization içindeyse uygulanabilir. Bu koşulu production auth modeli sanmak yanlış credential/tenant scope'una yol açar.
- `RISK-F5-007`: Store/customer/order fixture'larında PII bulunabilir. Yalnız anonim, küçültülmüş fixture ve hash/redaction kanıtı repository'ye alınır.

## Açık kararlar

- `DEC-F5-001`: Uygulama yalnız işletmenin kendi Shopify store'u için aynı-organization API-only app mi, yoksa başka merchant kurulumunu gerektiren OAuth dağıtımı mı kullanacak?
- `DEC-F5-002`: Development store ve canonical `*.myshopify.com` shop scope'u hangisi olacak?
- `DEC-F5-003`: İstenen minimum query/mutation setine göre granted Admin API scope listesi nedir?
- `DEC-F5-004`: Yerel `MAIN` depo hangi Shopify Location GID'sine eşlenecek; missing/inactive location operasyon kararı nedir?
- `DEC-F5-005`: Ürün yayınlama, arşivleme ve silme otoritesi nedir? F5 başlangıç fallback'i delete/write off'tur.
- `DEC-F5-006`: Fulfillment oluşturma otoritesi MarketplaceHub mı, Shopify/başka fulfillment service mi? Kanıtlanana kadar write off'tur.
- `DEC-F5-007`: Development webhook E2E için Ubuntu sunucu beklenmeden geçici güvenli tunnel kullanılacak mı, yoksa public HTTPS gelene kadar fixture ile mi kalınacak?

## ADR etkisi

- ADR-001, ADR-003, ADR-004, ADR-005, ADR-006, ADR-007 ve ADR-008 değişmeden uygulanır.
- Shopify adapterının auth/distribution seçimi yalnız mevcut tek işletme sınırında kalırsa version/upgrade ve credential kararı Adapter README/runbook'ta kaydedilir.
- `DEC-F5-001` başka merchant OAuth dağıtımını seçerse trust boundary ve credential yaşam döngüsü için ayrı ADR gerekir; bu karar aktif multi-tenant yetkisi vermez.
- Yeni servis, database, queue, cache veya deployment topolojisi önerilmez.

## F5 çıkış kriterleri

| Kimlik | Ölçülebilir koşul | Kanıt | Plan durumu |
| --- | --- | --- | --- |
| `F5-EXIT-001` | Domain/Application'da Shopify tipi yoktur. | Source/boundary guard | PASS_LOCAL |
| `F5-EXIT-002` | Bulk/product/inventory/order/fulfillment generic portlardan geçer. | Contract core; development-store E2E açık blocker | PARTIAL_LOCAL / BLOCKED_EXTERNAL |
| `F5-EXIT-003` | GraphQL `userErrors`, JSONL restart, HMAC duplicate/out-of-order ve missing-location güvenlidir. | `F5-EV-005–010`; yazmalar kapalı | PASS_LOCAL_FAIL_CLOSED / BLOCKED_EXTERNAL |
| `F5-EXIT-004` | Pinned version, granted scope, capability ve upgrade notu kanıtlıdır. | `F5-EV-003`, capability matrix, README/runbook | PARTIAL_LOCAL / BLOCKED_EXTERNAL |

## Plan sonucu ve uygulama kapısı

F5 planı kullanıcı tarafından 2026-08-02 tarihinde onaylandı. Yerel adapter/contract çekirdeği `READY_LOCAL_CORE`; development-store E2E, granted scope, gerçek location, public webhook ve write authority kanıtları gelene kadar faz çıkışı `BLOCKED_EXTERNAL`dır. F6 açılmamıştır.
