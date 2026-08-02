# F3 - Trendyol Uçtan Uca Dikey Dilim Planı

## Belge durumu

| Alan | Değer |
| --- | --- |
| Faz | `F3` |
| Plan durumu | `APPROVED` |
| Uygulama durumu | `READY_LOCAL_CORE / BLOCKED_EXTERNAL` |
| Yetkili şartname | Repository kökündeki v3.2 PDF; özellikle sayfa 24-29, 34-47, 52, 57-58, 60 ve 63 |
| Yetkili şartname SHA-256 | v3.4 `5A652AC34574A3310B844AECE647B96D350DD7AA79FDF3AC54C080827150EC51` |
| Ön koşul | F2 `READY_LOCAL`, commit `8cbf1b9` |
| Faz başlatma kaydı | Kullanıcı 2026-07-31 tarihinde “Projeye devam edelim.” diyerek F3 planlamasını açtı. |
| Hedef sonuç | Yerel/fixture uygulaması `READY_LOCAL`; gerçek Stage/SIT ve production kanıtları tamamlanana kadar faz çıkışı `BLOCKED_EXTERNAL` |

Kullanıcı planı onayladı ve F3 yerel çekirdek uygulaması tamamlandı. Gerçek Stage/SIT ve production kanıtları dış bağımlılıklar nedeniyle kapalıdır; ayrıntı `F3-evidence-log.md` içindedir.

## F3 hedefleri

- Mevcut platformdan bağımsız portların ürün-stok-sipariş-paket-iade çevrimini ilk gerçek platform olan Trendyol üzerinde kanıtlamak.
- Bağlantı kimliği, environment, credential scope ve capability kanıtını tenant + connection + API version + mağaza kapsamında saklamak.
- Yalnız version-pinned Trendyol Product Integration V2 sözleşmesini kullanmak; V1 kodu veya fallback’i oluşturmamak.
- Güncel category/attribute/brand snapshot’larını F2 mapping ve publish-validation hattına bağlamak.
- Ürün create/update/archive/delete davranışlarını asenkron batch sonucu ve satır bazlı partial-result ile yönetmek.
- Stok/fiyat batch gönderimini yalnız değişen satırlar, publishable stok, price version ve capability kapıları üzerinden gerçekleştirmek.
- Webhook ve overlap’li cursor polling’i aynı Inbox/dedupe/ingestion hattında birleştirmek.
- Order/Line/FinancialAllocation, ShipmentPackage/Allocation ve Return aggregate’larını şartnamedeki state/miktar değişmezleriyle kurmak.
- Açıklanabilir reconciliation, connection-scoped kill switch, güvenli rollback ve anonim contract fixture setini tamamlamak.
- Stitch’in `Teal Precision` görsel dilini yalnız F3 ekranlarına, mevcut işlevleri ve erişilebilirliği bozmadan uygulamak.

## Kapsam dışı

- F4 Invoice aggregate’ı, Trendyol E-Faturam, mükellef sorgusu, fatura submit/delivery/cancel ve `/invoices` production yüzeyi.
- F5 Shopify; F6 Hepsiburada, N11 ve Pazarama adapter/controller/route/menu/placeholder’ları.
- F7 ileri dashboard, KPI, rapor/export ve optimizasyon ekranları.
- F7B çok kullanıcı/RBAC ve F8 aktif multi-tenant.
- Mikroservis, Redis, RabbitMQ, Kafka, Kubernetes veya ölçümsüz cache/broker eklemek.
- Resmî kaynağı ve tekrarlanabilir kanıtı olmayan endpoint, DTO alanı, enum, status, header, süre veya limit.
- Stitch içindeki örnek sipariş numarası, müşteri, credential, mağaza, fiyat, adet, durum veya diğer demo değerlerini iş otoritesi saymak.
- Stitch’in F4 fatura ekranını, F7 dashboard bölümünü veya sonraki platform kartlarını bu fazda görünür hale getirmek.

## Mevcut repository durumu

- Branch `master`; başlangıç commit’i `8cbf1b9 feat: implement F2 catalog import and inventory`; inceleme başlangıcında worktree temizdir.
- F1 güvenlik, session, tenant context, job/inbox/audit, private file, backup ve operasyon temeli vardır.
- F2 katalog, typed attribute, reference/mapping, import, listing profile, offer, ledger/reservation ve fail-closed publish/sync kapıları vardır.
- F2’de gerçek platform HTTP adapter’ı yoktur; bütün gerçek capability’ler `UNKNOWN`, `FeatureFlags__ExternalWrites=false` kalır.
- Test projeleri Domain, Application, Persistence, API, Adapter Contract ve EndToEnd sınırlarıyla hazırdır.
- Yerel makinede .NET 10/Node 24 ve PostgreSQL 18 doğrulaması yapılmıştır; güncel oturumda Docker CLI/engine yoktur.
- Mevcut AWS Ubuntu Server 26.04 LTS host profili (2 vCPU, 8 GB RAM sınıfı, 80 GB NVMe sınıfı) ve SSH erişimi doğrulanmıştır. Docker/Compose, DNS/public HTTPS webhook-media, x5 yük ve operasyon kanıtları production kapısıdır.

## Stitch değerlendirmesi

| Alan | Kayıt |
| --- | --- |
| Kaynak | `C:\Users\emrek\OneDrive\Masaüstü\stitch_merkezpanel_e_ticaret_y_netim_sistemi.zip` |
| SHA-256 | `3B51EBF78D7653933451E2B41D627A5281E14298844F7B7AFFAFC0B8198CE0A9` |
| Güvenlik incelemesi | 30 ZIP kaydı, 2.134.371 byte açılmış içerik, path-traversal girdisi yok |
| Tasarım sistemi | `Teal Precision`; Hanken Grotesk, 8 px ritim, 280 px sidebar, 64 px header, düşük kontrastlı outline, erişilebilir metin+ikon durumları |
| F3 referans ekranları | Platformlar, Kategori & Özellik Eşlemeleri, Siparişler, genişletilebilir Sipariş Detayı, İade Yönetimi |
| F2 referans ekranları | Ürün listesi ve yeni ürün/varyant ekranı; F2 işlevleri bozulmadan ayrı görsel iyileştirme girdisidir |
| Bu fazda kullanılmayacak | Faturalar (F4), ileri dashboard/KPI (F7), Shopify/Hepsiburada/N11/Pazarama kart ve aksiyonları (F5/F6) |

Stitch HTML’lerindeki CDN Tailwind, Google Fonts, Material Symbols ve uzak örnek görseller production bağımlılığı olarak doğrudan kopyalanmayacaktır. Tasarım tokenları mevcut React/CSS yapısına uyarlanacak; font/icon varlığı exact lock, lisans ve self-host kararı olmadan eklenmeyecektir. Demo değerler fixture veya platform sözleşmesi değildir.

## Gereksinim matrisi

Bu tablo onay anındaki plan bazını korur; `PLANNED` değerleri tarihsel plan durumudur. Uygulama sonrası gerçek sonuçlar ve açık dış kanıtlar `F3-evidence-log.md` ile aşağıdaki çıkış tablosunda bağlayıcı olarak izlenir.

| Kimlik | Kaynak bölümü | Kabul ölçütü | Planlanan kanıt | Dosya/modül | Dış bağımlılık | Plan başlangıcı |
| --- | --- | --- | --- | --- | --- | --- |
| `F3-REQ-001` | Sayfa 24, 28, 42; Tablo 20/33 | PlatformConnection identity, environment, store ve scope ayrıdır; connection allow-list state machine’i uygulanır. | Domain/persistence transition ve isolation testleri | Domain/Application/Persistence | Trendyol Stage identity | PLANNED |
| `F3-REQ-002` | Sayfa 28, 42 | Capability keşfi read/write bazında tutulur; `UNKNOWN` dış HTTP yazması üretmez. | Capability matrix + no-HTTP/no-job testleri | Application/Adapter/Persistence | Resmî kaynak + fixture/SIT | PLANNED |
| `F3-REQ-003` | Sayfa 63 | Yalnız Trendyol Product Integration V2 version-pinned adapter kullanılır; V1 implementasyonu yoktur. | Adapter README, source guard, contract test | `Infrastructure/Adapters/Trendyol` | Güncel resmî V2 dokümanı | PLANNED |
| `F3-REQ-004` | Sayfa 28, 44, 63 | Category/attribute/brand current snapshot ve leaf/required mapping publish öncesi doğrulanır. | Snapshot/mapping/publish integration testleri | Adapter + F2 ReferenceData/Catalog | Stage read credential | PLANNED |
| `F3-REQ-005` | Sayfa 28, 44, 63 | Product create/update/archive/delete capability davranışı batch/task sonucu üzerinden satır bazlı işlenir; validation sonsuz retry almaz. | success/partial/validation/auth/unknown fixture testleri | Product port + jobs + links | Stage safe-write | PLANNED |
| `F3-REQ-006` | Sayfa 47, 63 | URL-fetch medya için kısa ömürlü, amaç/tenant/asset kapsamlı imzalı HTTPS teslim vardır; kalıcı public media yoktur. | Expiry, tamper, one-use/limit, no-store ve tenant testleri | API/File/Adapter | Public HTTPS host | PLANNED |
| `F3-REQ-007` | Sayfa 28-29, 44, 63 | Stock/price yalnız değişen satırlar halinde batch’lenir; her satır sonucu ve Retry-After kaydedilir. | Partial batch, unchanged suppression, 429/retry testleri | InventoryPrice port + jobs | Stage safe-write | PLANNED |
| `F3-REQ-008` | Sayfa 29, 39, 45, 48 | Opaque hook route, raw-body doğrulama, durable Inbox ve hızlı ACK vardır; sahte/replay/duplicate reddedilir. | Raw body auth, 20 paralel duplicate, p95 ACK testi | API webhook + Adapter verifier + Inbox | Public HTTPS callback | PLANNED |
| `F3-REQ-009` | Sayfa 29, 63 | Cursor/last-modified overlap polling webhook ile aynı ingestion/dedupe use case’ini kullanır. | Webhook-poll overlap ve cursor restart testleri | Order port + sync policy + worker | Stage read credential | PLANNED |
| `F3-REQ-010` | Sayfa 24, 26-27, 29 | Order/Line/FinancialAllocation/ShipmentPackage/Allocation tenant composite guard ve append-only history ile kurulur. | Fresh PostgreSQL migration/FK/UQ/check testleri | Domain/Persistence migration | Yok | PLANNED |
| `F3-REQ-011` | Sayfa 24, 27, 29, 63 | Out-of-order olay ileri package state’ini geriye götürmez; split/partial-cancel miktar denklemleri korunur. | Permutation/property ve PostgreSQL concurrency testleri | Domain/Application/Persistence | Anonim order fixtures | PLANNED |
| `F3-REQ-012` | Sayfa 26, 38, 63 | ShipmentDocument/Attempt ve CargoProviderMapping private/sürümlü tutulur; UI yalnız kanıtlı format/aksiyonu sunar. | Label capability, MIME/file, attempt-history testleri | Shipment port/API/File/UI | Stage cargo/label capability | PLANNED |
| `F3-REQ-013` | Sayfa 25-27, 29, 38, 63 | Return read/decision/evidence/disposition ayrıdır; yalnız `PASS` tek stok ledger artışı üretir. | State, idempotent action, evidence, disposition testleri | Domain/Return port/API/UI | Stage return fixture/account | PLANNED |
| `F3-REQ-014` | Sayfa 46, 63 | Product-listing, inventory-price ve order-package-return reconciliation açıklanabilir fark üretir; sessiz overwrite yoktur. | Reconciliation fixture ve dry-run report | Application/Persistence/Worker | Stage read data | PLANNED |
| `F3-REQ-015` | Sayfa 36, 38-40 | Yalnız onaylı connection/order/shipment/return/mapping/hook endpoint ve panel rotaları işlevseldir. | API surface guard + Playwright | API/Web | Yok | PLANNED |
| `F3-REQ-016` | Sayfa 40-41 | UI loading/empty/error/stale/partial/unknown/concurrency durumlarını ve capability gerekçesini metin+ikonla gösterir. | Component/a11y/Playwright | Web + Stitch token uyarlaması | Stitch sağlandı | PLANNED |
| `F3-REQ-017` | Sayfa 45-46, 57-58 | Adapter README ve anonim fixture seti success/partial/unknown/missing/auth/429/5xx/timeout durumlarını kapsar. | Contract test paketi ve secret/PII scan | Adapter README/Fixtures/Tests | Resmî örnekler + SIT kayıtları | PLANNED |
| `F3-REQ-018` | Sayfa 48, 52 | Credential şifreli/maskeli, prod-stage ayrık, User-Agent kayıtlı; PII/secret log/API/fixture’a sızmaz. | Encryption/redaction/auth/scope testleri | Security/Connection/Adapter | Seller credential + identity kararı | PLANNED |
| `F3-REQ-019` | Sayfa 57-58 | Webhook ACK hedefi `<500 ms`, ürün/sipariş listesi p95 `<2 sn`; worker-kill/429/5xx/DB restart senaryoları güvenlidir. | PostgreSQL load/resilience ve Playwright E2E | Bütün katmanlar | Temsilî fixture | PLANNED |

## Resmî Trendyol kaynak doğrulaması

Doğrulama tarihi `2026-07-31`’dir. URL erişimi tek başına `SUPPORTED` anlamına gelmez; capability ancak anonim contract fixture veya Stage/SIT kanıtıyla kapanır.

| Alan | Resmî kaynak | Doğrulanan plan girdisi | Başlangıç capability |
| --- | --- | --- | --- |
| Genel katalog | <https://developers.trendyol.com/v2.0/> | Marketplace API; resmî doküman ve OpenAPI/LLM indeksi erişilebilir | N/A |
| Authorization | <https://developers.trendyol.com/v2.0/docs/authorization> | Basic Auth; seller/supplier kimliği; API key/secret; zorunlu User-Agent; prod-stage credential ayrımı | `ConnectionTest=UNKNOWN` |
| Stage/Prod | <https://developers.trendyol.com/v2.0/docs/3-prod-stage-environments-1> | Stage erişimi IP yetkilendirmesi gerektirir; test hesabı/paylaşımlı hesap süreci vardır | `CapabilityDiscovery=UNKNOWN` |
| Product V2 | <https://developers.trendyol.com/v2.0/docs/product-create-v2> | V2 create asenkron batch sonucuyla izlenir; istek üst sınırı resmî belgede 1.000 item’dır | Product write’lar `UNKNOWN` |
| V1 sunset | <https://developers.trendyol.com/v2.0/docs/product-api-endpoint> | Product V1, 10 Ağustos 2026’dan itibaren geçersizdir; F3 V1 üretmez | V1 `NOT_APPLICABLE` |
| Category attributes | <https://developers.trendyol.com/v2.0/docs/category-attribute-list-v2> | En alt/leaf kategori gerekir; güncel attribute snapshot önemlidir | Reference read’ler `UNKNOWN` |
| Stock/price | <https://developers.trendyol.com/v2.0/docs/stock-and-price-update-updatepriceandinventory-1> | Değişmeyen isteğin tekrarı reddedilebilir; batch sonucu izlenir; belgelenen üst sınır 1.000 satırdır | Inventory/Price write `UNKNOWN` |
| Batch result | <https://developers.trendyol.com/v2.0/docs/check-batchrequest-result-getbatchrequestresult-1> | Item-level sonuç zorunludur; batch kayıtları zaman sınırlı erişilebilir | `BatchResultRead=UNKNOWN` |
| Order cursor polling | <https://developers.trendyol.com/v2.0/docs/getshipmentpackagesstream> | Periyodik/full sync için opaque cursor stream önerilir | Order read’ler `UNKNOWN` |
| Webhook | <https://developers.trendyol.com/v2.0/docs/webhook-create>, <https://developers.trendyol.com/v2.0/docs/webhook-model> | Webhook auth Basic veya API key olabilir; `x-api-key` desteklenir; hata sonrası subscription devre dışı kalabilir | `OrderWebhook=UNKNOWN` |
| Order/package | <https://developers.trendyol.com/v2.0/docs/order-process-flow>, <https://developers.trendyol.com/v2.0/docs/cancel-order-package-item-updatepackage> | Package split ve partial cancellation yeni package kimliği üretebilir | Package/Shipment action `UNKNOWN` |
| Return | <https://developers.trendyol.com/v2.0/docs/getting-returned-orders-getclaims>, <https://developers.trendyol.com/v2.0/docs/claims-process-flow> | Return read ve action akışları ayrıdır | Return capability’leri `UNKNOWN` |
| Label | <https://developers.trendyol.com/v2.0/docs/common-label-barcode-request-createcommonlabel> | Common-label için güncel belgede yalnız ZPL kanıtlanmıştır; A4/A6/PDF uydurulmaz | `LabelRead=UNKNOWN` |
| Effective limits | <https://developers.trendyol.com/v2.0/docs/1-service-limitations> | Ürün limit modeli 14 Eylül 2026’da grup bazlı değişir; runtime policy tarihli ve yenilenebilir olmalıdır | Tüm rate profiles `UNKNOWN` |

Resmî belgede farklı tarihler için farklı limitler bulunduğundan değerler domain sabiti yapılmayacaktır. Adapter README; `verifiedAt`, `effectiveFrom`, mağaza/listing tier ve kaynak URL’siyle config/evidence profili tutacak, 429 ve varsa `Retry-After` cevabı runtime otoritesi olacaktır.

## Gerçekleşen dosya etkisi

### Oluşturulan

- `src/MarketplaceHub.Domain/OrderModels.cs`
- `src/MarketplaceHub.Domain/ShipmentModels.cs`
- `src/MarketplaceHub.Domain/ReturnModels.cs`
- `src/MarketplaceHub.Application/F3Contracts.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/Trendyol/README.md`
- `src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolOptions.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolAuthenticationHandler.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolHttpClient.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/Trendyol/Contracts/`
- `src/MarketplaceHub.Infrastructure/Adapters/Trendyol/Mapping/`
- `src/MarketplaceHub.Infrastructure/Adapters/Trendyol/Ports/`
- `src/MarketplaceHub.Infrastructure/Adapters/Trendyol/ErrorMapping/`
- `src/MarketplaceHub.Infrastructure/Adapters/Trendyol/Fixtures/`
- `src/MarketplaceHub.Infrastructure/Persistence/F3ModelConfiguration.cs`
- `src/MarketplaceHub.Infrastructure/Persistence/F3SalesService.cs`
- `src/MarketplaceHub.Infrastructure/Persistence/F3ReconciliationService.cs`
- Tarihsel tek F3 EF migration ve güncel model snapshot’ı
- `src/MarketplaceHub.Api/F3/F3Endpoints.cs`
- Webhook endpoint’i de içeren `src/MarketplaceHub.Api/F3/F3Endpoints.cs`
- `src/MarketplaceHub.Web/src/F3Pages.tsx`
- F3 test dosyaları mevcut altı test projesinin ilgili sınırlarında
- `docs/implementation/F3-evidence-log.md`

### Değiştirilen

- `AppDbContext`, dependency registration, Worker F3 job allow-list’i, API composition ve environment/secret kataloğu.
- Mevcut F2 catalog/reference/inventory servisleri yalnız port entegrasyonu ve gerekli state/job bağlantıları kadar genişletilecek.
- Web navigation/CSS; yalnız `/orders`, `/orders/:id`, `/shipments`, `/returns`, `/returns/:id`, `/integrations`, `/integrations/:id`, `/mappings/categories`, `/mappings/attributes` açılacak.
- Capability matrix, traceability matrix, risk/external-dependency kayıtları ve runbook’lar.

### Dokunulmayacak / oluşturulmayacak

- Yeni solution, mikroservis veya ayrı database/migration zinciri.
- `/invoices`, `/billing`, `/reports`, diğer platform adapter’ları, kullanıcı/rol/tenant yönetimi ve bunlara ait placeholder’lar.

## Capability doğrulama ve güvenli çalışma planı

1. Tüm Trendyol capability’leri başlangıçta `UNKNOWN`, dış yazmalar global ve connection düzeyinde kapalı kalır.
2. Resmî doküman örneklerinden PII/secret içermeyen, checksum’lı contract fixture seti hazırlanır; dokümanda olmayan değer eklenmez.
3. Parser/mapping ve hata sınıflandırması fixture üzerinde doğrulanır; bu kanıt yalnız ilgili read/parse davranışını açar.
4. Credential yalnız secret-file/şifreli credential store üzerinden yüklenir; plan veya chat içine yazılmaz.
5. Stage bağlantı testi identity, seller/store, environment, API version ve scope snapshot’ını kaydeder.
6. Read capability’leri Stage read testinden sonra; write capability’leri ayrıca kullanıcı onaylı safe-write testinden sonra `SUPPORTED` olabilir.
7. Product, inventory, price, shipment ve return write anahtarları ayrıdır; birinin açılması diğerini açmaz.
8. Webhook doğrulaması yalnız resmî Basic/API-key sözleşmesine göre yapılır; Trendyol için belgelenmemiş HMAC/timestamp uydurulmaz. Route token, constant-time secret karşılaştırması, Inbox UQ ve replay/dedupe ayrıca uygulanır.
9. Connection identity/API version/scope değişirse capability cache invalid olur ve dış yazma tekrar kapanır.
10. Production smoke her capability için düşük adet, açık etki özeti, rollback ve ayrı kullanıcı onayıyla yapılır.

## Test ve kanıt planı

| Kanıt | Komut/senaryo | Beklenen sonuç | Artefakt | Durum |
| --- | --- | --- | --- | --- |
| `F3-EV-001` | Format + warnings-as-errors solution build | 0 warning, 0 error | Test log | PASS |
| `F3-EV-002` | Fresh PostgreSQL 18 migration + F2→F3 upgrade SQL | Tek tarihsel zincir; demo/credential seed yok | Migration SQL/hash | PASS |
| `F3-EV-003` | Connection state/identity/scope/capability tests | Prod-stage karışmaz; UNKNOWN write yapmaz | Domain/persistence log | PASS_LOCAL |
| `F3-EV-004` | Product/reference contract fixtures | V2 success/partial/unknown/missing/validation güvenli map edilir | Fixture checksums | PASS_LOCAL |
| `F3-EV-005` | Stock/price batch + batch-result | Değişmeyen satır gönderilmez; partial tek başarı sayılmaz | Contract/persistence log | PARTIAL_LOCAL / SAFE_WRITE BLOCKED_EXTERNAL |
| `F3-EV-006` | 20 paralel aynı webhook | Tek Inbox, Order ve StockLedger etkisi | PostgreSQL concurrency log | PARTIAL_LOCAL / LOAD OPEN |
| `F3-EV-007` | Out-of-order + webhook/poll overlap | İleri state gerilemez; duplicate yok | State permutation log | PASS_LOCAL_CORE |
| `F3-EV-008` | Split/partial-cancel sıralama varyasyonları | Quantity denklemleri daima korunur | Domain/property log | PASS_DOMAIN / STAGE FIXTURE BLOCKED_EXTERNAL |
| `F3-EV-009` | Webhook auth/replay/body/size/ACK | Geçersiz istek reddedilir; durable Inbox sonrası p95 `<500 ms` | API/performance log | PARTIAL_LOCAL / PUBLIC P95 BLOCKED_EXTERNAL |
| `F3-EV-010` | 401/403/429/5xx/timeout/worker-kill | Auth bloklanır; Retry-After korunur; başarılı dış etki tekrarlanmaz | Resilience log | PASS_LOCAL_CORE |
| `F3-EV-011` | Shipment label/manual document | Yalnız kanıtlı format; private/no-store/tenant-safe | File/API log | MODEL_READY / STAGE BLOCKED_EXTERNAL |
| `F3-EV-012` | Return action/evidence/disposition | Decision idempotent; yalnız PASS tek ledger artışı | Domain/E2E log | PASS_LOCAL_CORE / STAGE ACTION BLOCKED_EXTERNAL |
| `F3-EV-013` | Reconciliation + kill switch + rollback | Açıklanabilir fark; dış write kapalı dry-run; rollback çalışır | Report/runbook | PASS_LOCAL_DRY / REMOTE BLOCKED_EXTERNAL |
| `F3-EV-014` | API surface/OpenAPI guard | Yalnız F3 rotaları; F4+ yüzeyi yok | Route snapshot | PASS |
| `F3-EV-015` | Stitch uyarlamalı UI component/Playwright | Loading/empty/stale/partial/unknown/concurrency ve erişilebilirlik geçer | Web test log | PASS_BUILD / BROWSER POLICY BLOCKED |
| `F3-EV-016` | 5x hacim ve cursor listeleri | Ürün/sipariş p95 `<2 sn`; memory-bounded polling | Performance log | OPEN_EXTERNAL_DATA |
| `F3-EV-017` | Secret/PII/fixture/repository scan | Credential, TCKN/VKN, adres ve raw PII sızıntısı yok | Scan log | PASS |
| `F3-EV-018` | Stage/SIT read + safe-write | Ürün, stok/fiyat, order/package ve return akışı kanıtlı | Redacted SIT record | BLOCKED_EXTERNAL |
| `F3-EV-019` | Kullanıcı onaylı düşük adet production smoke | Etki/rollback kayıtlı veya açık dış blocker | Smoke record | BLOCKED_EXTERNAL |
| `F3-EV-020` | Test-only deterministik Fake adapter senaryoları | Generic portlar; success/empty/partial/error/replay; PostgreSQL job/worker-kill/retry ve Chromium→API→job→gerçek Worker→Fake→UI; no-network/no-secret | EndToEnd test log | PASS_FULL_LOCAL_FAKE_RC / SANDBOX OPEN |

## Dış bağımlılıklar, riskler ve blockerlar

| Kimlik | Kayıt | Güvenli fallback | Kapanış kanıtı | Yerel uygulama blocker? |
| --- | --- | --- | --- | --- |
| `BLOCK-F3-001` | Trendyol Stage seller/supplier ID, API key ve secret henüz yok. | Fixture/Fake adapter; bütün external write off | Secret sızdırmadan Stage connection test | Hayır; Stage/SIT için evet |
| `BLOCK-F3-002` | Stage IP yetkilendirmesi yapılmadı; yerel public IP kalıcı olmayabilir. | Contract test; Ubuntu sunucunun statik IP'si veya onaylı sabit IP geldiğinde Stage | Trendyol Stage başarılı 2xx identity/read | Hayır; Stage/SIT için evet |
| `BLOCK-F3-003` | SelfIntegration mı kayıtlı entegratör kimliği mi kullanılacağı kesinleşmedi. | User-Agent gönderilmez ve gerçek çağrı yapılmaz | Kullanıcı/Trendyol tarafından onaylı kimlik değeri | Gerçek çağrı için evet |
| `BLOCK-F3-004` | AWS Ubuntu hostu hazır; public HTTPS webhook ve imzalı media callback domain/DNS kaydı yok. | Yerel raw webhook/expiry contract testleri | Caddy arkasında public HTTPS Stage callback | Hayır; webhook/media SIT için evet |
| `BLOCK-F3-005` | Mağazaya ait güvenli test ürün/kategori/marka/order/return kayıtları yok. | Resmî örnekten anonim fixture | Redacted Stage fixture/checksum | Hayır; E2E SIT için evet |
| `BLOCK-F3-006` | Label/cargo kapsamı mağaza ve kargo modelinde doğrulanmadı. | `LabelRead=UNKNOWN`; manuel upload sahte dış başarı üretmez | Stage label/cargo capability record | Hayır |
| `BLOCK-F3-007` | Production smoke için kayıt/adet/etki onayı henüz verilmedi. | Production writes off | Operasyon başına açık kullanıcı onayı + rollback | Hayır; F3 çıkışı için açık blocker kabul edilebilir |
| `RISK-F3-001` | 14 Eylül 2026’da ürün servis limit modeli değişiyor. | Effective-dated config/evidence; 429/Retry-After; SIT öncesi yeniden doğrulama | Güncel README/capability record | Hayır |
| `RISK-F3-002` | Webhook hata sürecinde subscription platform tarafından devre dışı bırakılabilir. | Health/issue + subscription check + overlap polling | Deactivation/recovery fixture ve Stage test | Hayır |
| `RISK-F3-003` | Stitch HTML dış CDN/font/icon/görseller içeriyor. | Yalnız token/layout referansı; production’a doğrudan kopyalama yok | Lock/license/self-host veya sistem fallback kararı | Hayır |
| `RISK-F3-004` | Stitch demo ekranları F4/F5/F6/F7 işlevlerini aynı menüde gösteriyor. | Faz filtresi; yalnız Trendyol ve F3 rotaları | Route/menu guard | Hayır |

## Açık kullanıcı kararları

- `DEC-F3-001`: Trendyol User-Agent kimliği `SelfIntegration` mı yoksa kayıtlı entegratör adı mı olacak? Credential edinilirken kesinleşmelidir.
- `DEC-F3-002`: Stage IP yetkisi yerel sabit IP ile mi, daha sonra kiralanacak Ubuntu sunucunun statik IP’siyle mi alınacak? Yerel contract geliştirmesi bu kararı beklemez.
- `DEC-F3-003`: Public HTTPS webhook/media Stage testi Ubuntu sunucu sonrasına mı bırakılacak, yoksa ayrıca onaylanan kontrollü geçici test ingress’i mi kullanılacak? Varsayılan Ubuntu sunucuyu beklemektir.
- `DEC-F3-004`: Her production smoke işlemi; platform, kayıt, adet, beklenen etki ve rollback özetiyle ayrıca onaylanacaktır. Bu plan genel production-write onayı değildir.

## ADR etkisi

- Yeni mimari ADR gerekmiyor. F3; ADR-001 modüler monolit, ADR-003 tek PostgreSQL/migration zinciri, ADR-004 job/inbox/idempotency, ADR-005 capability kanıtı, ADR-006 güvenli iş otoriteleri, ADR-007 secret güvenliği, ADR-008 private file, ADR-010 backup/restore, ADR-012 Linux container runtime ve ADR-013 AWS Ubuntu host profili kararlarına uyar.
- Trendyol limit tarihçesi adapter configuration/evidence kaydıdır; yeni altyapı kararı değildir.
- Resmî/test davranışı mevcut port sözleşmesiyle çelişirse production kodu uydurulmaz; değişiklik kapısı ve gerekirse yeni ADR kullanıcıya sunulur.
- Birden çok physical inventory location, broker/cache/mikroservis veya farklı deployment topolojisi gözlenmiş ihtiyaç ve ayrı ADR olmadan açılmaz.

## F3 çıkış kriterleri

| Kimlik | Ölçülebilir koşul | Kanıt | Durum |
| --- | --- | --- | --- |
| `F3-EXIT-001` | Stage/SIT ürün, stok/fiyat, order/package ve return akışları geçer. | `F3-EV-018` | BLOCKED_EXTERNAL |
| `F3-EXIT-002` | Reconciliation ürün-listing, inventory-price ve order-package-return için açıklanabilir fark raporu üretir. | `F3-EV-013` | PASS_LOCAL_DRY / REMOTE BLOCKED_EXTERNAL |
| `F3-EXIT-003` | Connection kill switch ve rollback senaryosu dış yazmayı durdurur ve güvenli tekrar başlatmayı kanıtlar. | `F3-EV-013` | PASS_LOCAL_FAIL_CLOSED / RESTART BLOCKED_EXTERNAL |
| `F3-EXIT-004` | Production smoke kanıtı vardır veya erişim/onay eksikliği açık dış blocker olarak kayıtlıdır. | `F3-EV-019` | BLOCKED_EXTERNAL |
| `F3-EXIT-005` | Contract, duplicate/out-of-order/split, webhook, partial-result, resilience, security ve performans kanıtları geçer. | `F3-EV-001–017`, `F3-EV-020` | PARTIAL_LOCAL / PERFORMANCE BLOCKED_EXTERNAL |
| `F3-EXIT-006` | Yalnız F3 API/UI/adapter yüzeyi vardır; F4+ production kodu, route, menü veya placeholder yoktur. | Surface/repository guard | PASS |

## Sonuç

F3 yerel çekirdeği `READY_LOCAL_CORE` durumundadır: domain/persistence/migration, Trendyol V2 adapter sınırı, connection/capability/credential, webhook+poll ingestion, order/package/return, local dry reconciliation, Worker, API ve faz filtreli UI uygulanmıştır. Gerçek Trendyol credential, Stage IP allow-list, public HTTPS callback ve safe-write verisi olmadan yalnız gerçek testle kanıtlanan capability `SUPPORTED` yapılabilir; dış yazma çift kill switch ile kapalıdır.

Stage/SIT, label/media, gerçek split/partial, p95 performans ve production smoke kanıtları nedeniyle şartname F3 çıkışı `BLOCKED_EXTERNAL`dır. F4 açılmamıştır.
