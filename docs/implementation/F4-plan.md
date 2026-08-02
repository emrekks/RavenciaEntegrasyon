# F4 - Fatura ve Mali Belge Planı

## Belge durumu

| Alan | Değer |
| --- | --- |
| Faz | `F4` |
| Plan durumu | `APPROVED` |
| Uygulama durumu | `READY_LOCAL_CORE / BLOCKED_EXTERNAL` |
| Yetkili şartname | Repository kökündeki v3.2 PDF; özellikle sayfa 5, 9-10, 22-23, 25, 29, 38, 42-46, 51-52, 57-58, 60, 63-64, 67, 70 ve 72-73 |
| Yetkili şartname SHA-256 | v3.3 `AB7E5D26497EDC6D24E8CE0E7111CF44BB782819CD047C93DCBEE7E401BE3F94` |
| Ön koşul | F3 yerel çekirdek commit’i `5ba830d`; gerçek Stage/SIT kanıtları açık dış blocker olarak kayıtlı |
| Hedef sonuç | Yerel domain/persistence/fake-contract uygulaması `READY_LOCAL_CORE`; test firma, mali karar ve dış erişim gelene kadar gerçek submit/delivery `BLOCKED_EXTERNAL` |

Bu plan kullanıcı tarafından onaylandı ve F4 yerel çekirdeği uygulandı. F3’ün şartname çıkışı gerçek smoke kanıtı **veya açık dış blocker** kabul ettiği için kayıtlı F3 dış blocker’ları yerel F4 uygulamasını durdurmadı; canlı fatura akışını açmayı durdurur.

## F4 hedefleri

- Faturayı sipariş ve sevkiyat durumlarından bağımsız, idempotent ve denetlenebilir bir mali aggregate olarak kurmak.
- Legal entity, fatura policy’si, belge snapshot’ı, submit attempt’i ve pazaryeri delivery’sini ayrı sorumluluklarda tutmak.
- `UNKNOWN_RESULT` durumunda provider sorgusu yapılmadan ikinci submit’i teknik olarak engellemek.
- E-Fatura/E-Arşiv seçimini yalnız doğrulanmış mükellef sonucu ve onaylı InvoicePolicy ile yapmak; VKN/TCKN biçimini tek başına mükellefiyet sonucu saymamak.
- XML/PDF belgelerini private FileAsset üzerinde immutable, checksum’lı ve tenant-korumalı saklamak.
- Kısmi iptal/iade/düzeltmede eski fatura ve belgeyi değiştirmeden yeni adjustment/cancellation kaydı veya manuel mali issue üretmek.
- Trendyol’a belge/link/numara iletimini fatura oluşturmadan ayrı job, capability ve idempotency kapsamında yürütmek.
- `AUTO_INVOICE_ENABLED=false` güvenli varsayılanını mali onay ve gerçek test kanıtı olmadan değiştirmemek.

## Kapsam dışı

- F5 Shopify, F6 Hepsiburada/N11/Pazarama, F7 ileri raporlama, F7B kullanıcı/RBAC ve F8 aktif multi-tenant production kodu veya görünür yüzeyi.
- Genel muhasebe, cari hesap, tahsilat, banka, kampanya, e-İrsaliye ve bağımsız ERP modülü.
- Mali müşavir onayı olmadan otomatik kesim, due süresi, iptal/iade belgesi veya rounding politikası varsaymak.
- Resmî kaynağı ve tekrarlanabilir fixture/test firma kanıtı olmayan provider endpoint’i, auth biçimi, enum, alan, limit veya durum eşlemesi.
- Provider ham status’unu domain `InvoiceStatus` enum’una taşımak veya lojistik state’e `INVOICED` eklemek.
- Açık internet yolu üzerinden kalıcı fatura belgesi yayınlamak; private dosya modelini atlamak.
- Mikroservis, Redis, RabbitMQ, Kafka, Kubernetes, yeni solution veya ikinci migration zinciri.

## Mevcut repository durumu

- Branch `master`; başlangıç commit’i `5ba830d feat: implement F3 Trendyol vertical slice`; plan başlangıcında worktree temizdir.
- Tek solution, modüler monolit, API + Worker ve tek PostgreSQL migration zinciri korunmaktadır.
- F1 güvenlik/session/tenant/job/inbox/audit/private-file, F2 katalog-stok ve F3 Trendyol order/package/return çekirdeği hazırdır.
- `Order` mali ve adres snapshot’larını; `FileAsset`, `OperationalIssue`, `FeatureFlag`, `IntegrationJob` ve dış etki idempotency altyapısı mevcut fazlardan taşır.
- Plan hazırlanırken invoice aggregate’ı, billing tabloları, F4 portları, E-Faturam adapter’ı, F4 endpoint/UI ve F4 migration yoktu; onay sonrası bu yerel çekirdek oluşturuldu.
- `AUTO_INVOICE_ENABLED` katalog/runbook düzeyinde `false`; gerçek E-Faturam credential/test firma ve mali müşavir kararı yoktur.
- Ubuntu sunucu/domain yokluğu domain, migration, fake/contract ve yerel UI geliştirmesini engellemez; public belge linki ve gerçek delivery/SIT kanıtını engeller.

## Gereksinim matrisi

| Kimlik | Kaynak | Kabul ölçütü | Planlanan kanıt | Dosya/modül | Dış bağımlılık | Durum |
| --- | --- | --- | --- | --- | --- | --- |
| `F4-REQ-001` | s.22-23, 63 | LegalEntityProfile ve InvoicePolicy tenant/connection kapsamlı, sürümlü ve sorgulanabilir kolonlarla kurulur; auto-submit kapalıdır. | PostgreSQL FK/UQ/check/concurrency testleri | Domain/Persistence | Mali profil girdisi | DONE_LOCAL |
| `F4-REQ-002` | s.25, 63-64 | Invoice allow-list state machine tam uygulanır; mali ve lojistik state ayrıdır. | Tüm izinli/yasak transition unit testleri | Domain | Yok | DONE_LOCAL |
| `F4-REQ-003` | s.22-23, 29 | InvoiceLine ve PartySnapshot işlem anını immutable saklar; sonraki order/customer değişikliği belgeyi etkilemez. | Snapshot immutability testi | Domain/Persistence | Anonim fixture | DONE_MODEL |
| `F4-REQ-004` | s.29, 63-64 | Para/KDV/indirim/rounding satır ve toplamları deterministic policy ile doğrulanır. | Money/rounding/property testleri | Domain/Application | Mali rounding kararı | BLOCKED_DECISION |
| `F4-REQ-005` | s.45 | `IInvoiceProviderPort` ve `IInvoiceMarketplacePort` platformdan bağımsız exact sözleşmeyle tamamlanır; eksik production placeholder yoktur. | Boundary/source guard | Application | Yok | DONE_LOCAL |
| `F4-REQ-006` | s.42, 45-46 | Provider environment/auth/scope/version capability evidence taşır; tüm invoice capability’leri başlangıçta `UNKNOWN`dır. | Capability/no-HTTP testleri | Application/Adapter/Persistence | Test credential/firma | DONE_LOCAL / BLOCKED_EXTERNAL |
| `F4-REQ-007` | s.29, 45-46, 64 | Taxpayer sonucu saklanır; E-Fatura/E-Arşiv seçimi biçim kontrolü değil sonuç + policy ile yapılır. | Tax ID format + taxpayer/type fixture testleri | Provider adapter/Application | Test firma | PARTIAL_LOCAL / BLOCKED_EXTERNAL |
| `F4-REQ-008` | s.23, 25, 29, 57, 64 | Submit request hash/idempotency tekildir; timeout `UNKNOWN_RESULT` olur ve query öncesi ikinci submit yoktur. | Worker-kill/timeout/retry/concurrency testleri | Persistence/Worker/Adapter | Provider query fixture | PARTIAL_LOCAL / BLOCKED_EXTERNAL |
| `F4-REQ-009` | s.23, 29, 46, 64 | ETTN/UUID, provider id, numara ve XML/PDF hash’i ayrı saklanır; document immutable/private/no-store’dur. | Checksum, MIME, traversal, cross-tenant ve streaming testleri | Persistence/File/API | Anonim XML/PDF | DONE_LOCAL_CORE |
| `F4-REQ-010` | s.23, 25, 29, 38, 45-46 | MarketplaceDelivery submit’ten ayrı job/idempotency kapsamıdır; delivery hatası yeni invoice oluşturmaz. | Duplicate/delivery retry testleri | Application/Persistence/Worker | Trendyol Stage | DONE_LOCAL_CORE / BLOCKED_EXTERNAL |
| `F4-REQ-011` | s.25, 29, 46, 64 | İptal/iade/düzeltme eski invoice/document’ı değiştirmez; `original_invoice_id` ile yeni kayıt veya issue üretir. | Partial return/cancel/adjustment testleri | Domain/Application | Mali policy + provider capability | BLOCKED_DECISION |
| `F4-REQ-012` | s.23, 63-64 | Due/late dedupe OperationalIssue üretir; mali onay yoksa otomatik fatura oluşturmaz/göndermez. | Clock/dedupe/kill-switch testleri | Application/Persistence/Worker | Deadline/trigger kararı | PARTIAL_LOCAL / BLOCKED_DECISION |
| `F4-REQ-013` | s.38 | Yalnız şartnamedeki billing/invoice endpointleri CSRF, re-auth, If-Match, idempotency ve no-store korumalarıyla açılır. | API surface/security testleri | API | Yok | DONE_LOCAL |
| `F4-REQ-014` | s.39-41, 64, 67 | `/invoices` ve billing policy UI loading/empty/error/unknown/manual-review durumlarını gösterir; otomatik submit varsayılan kapalıdır. | Component/a11y/route guard | Web | Stitch F4 referansı mevcut | DONE_BUILD |
| `F4-REQ-015` | s.46, 64 | Invoice reconciliation provider status, ETTN/numara, document hash ve marketplace delivery farklarını açıklanabilir gösterir; sessiz overwrite yoktur. | Fake/fixture dry-run testi | Application/Persistence | Stage read | PARTIAL_LOCAL / BLOCKED_EXTERNAL |
| `F4-REQ-016` | s.9, 23, 51-52, 57, 64 | Credential/PII encrypted-minimized; secret, TCKN/VKN, adres ve belge içeriği log/API/fixture/manifest’e sızmaz. | Secret/PII scan ve redaction testleri | Security/Adapter/API | KVKK/mali retention kararı | DONE_LOCAL_CORE / BLOCKED_DECISION |
| `F4-REQ-017` | s.17, 22-23, 56-58 | Tek tarihsel migration; composite tenant FK/UQ/check/version/idempotency DB’de uygulanır ve fresh/upgrade geçer. | PostgreSQL 18 migration/upgrade testleri | Persistence | Yok | DONE_LOCAL |
| `F4-REQ-018` | s.55, 57-58, 64 | XML/PDF ve DB birlikte backup/restore edilir; restore sonrası checksum ve read-only reconciliation geçer. | İzole restore smoke | Runbook/Persistence/File | Backup profili | BLOCKED_EXTERNAL |

## Veri modeli ve değişmezler

Planlanan F4 tabloları mevcut `billing` schema’sında, tek migration zincirinde oluşturulur:

- `legal_entity_profiles`: `tenant_id`, başlık, protected tax identity, adres/iletişim snapshot policy’si, status ve version; active tenant+title tekilliği.
- `invoice_policies`: tenant + provider connection + policy scope tekilliği; trigger, package scope, due rule, rounding rule ve `auto_submit=false` ayrı kolonlarda.
- `invoices`: order, optional package, provider connection, legal entity, policy, type/purpose/sequence, status, currency/totals, idempotency, external reference, due/issued zamanları, optional original invoice ve version.
- `invoice_lines`: order line bağlantısı ve açıklama/SKU/unit/quantity/price/discount/VAT/total snapshot’ı; stabil line sequence.
- `invoice_party_snapshots`: seller/receiver rolleri, minimized/protected mali kimlik/adres alanları ve content hash.
- `invoice_documents`: document kind, private FileAsset, SHA-256, optional provider id ve created time; invoice+kind+hash tekilliği.
- `invoice_submission_attempts`: immutable attempt number, request hash, outcome, güvenli error sınıf/kodu, remote id ve zamanlar.
- `marketplace_deliveries`: invoice/package/connection scope, ayrı idempotency, request hash, status, remote reference ve immutable attempt geçmişi.

Invoice primary key dış kimlik değildir. Order başına tek invoice varsayılmaz; package/scope/purpose/sequence idempotency anahtarına dahildir. Fatura state’i ShipmentPackage durumunu değiştiremez. Kesilmiş snapshot veya document update/delete edilemez.

## Planlanan API yüzeyi

Şartname Tablo 29 dışına çıkılmaz:

- `GET/POST /api/v1/invoices`
- `GET/PUT /api/v1/billing/legal-entity-profile`
- `GET/PUT /api/v1/billing/invoice-policies/{connectionId}`
- `GET /api/v1/invoices/{id}`
- `POST /api/v1/invoices/{id}/validate`
- `POST /api/v1/invoices/{id}/submit-jobs`
- `POST /api/v1/invoices/{id}/reconcile-jobs`
- `POST /api/v1/invoices/{id}/marketplace-delivery-jobs`
- `POST /api/v1/invoices/{id}/cancellation-jobs`
- `GET /api/v1/invoices/{id}/documents/{documentId}/content`

POST/PUT işlemleri şartnamedeki CSRF, If-Match, re-auth ve Idempotency-Key kurallarını taşır. Document content yalnız yetkili tenant üzerinden private streaming ve `no-store` ile döner.

## Resmî kaynak ve capability doğrulaması

Doğrulama tarihi `2026-07-31`’dir. Dokümanın erişilebilir olması tek başına `SUPPORTED` kanıtı değildir; gerçek test firma veya anonim, sözleşmeye sadık fixture kanıtı ayrıca gerekir.

| Alan | Resmî kaynak | Plan girdisi | Başlangıç durumu |
| --- | --- | --- | --- |
| Entegrasyon modeli/ortam | <https://developers.trendyolefaturam.com/OpenApi/trendyol-e-faturam-entegrasyon-dokumani> | Doküman `1.0.0`; API kullanıcısı ve pazaryeri entegratörü ayrımı; Stage ve production gateway kayıtları | Model seçimi `OPEN_DECISION` |
| Token auth | <https://developers.trendyolefaturam.com/OpenApi/Auth/sign-in> | `signIn` ile token; access/refresh token response header’larında | `ConnectionTest=UNKNOWN` |
| Pazaryeri entegratörü auth | <https://developers.trendyolefaturam.com/marketplace-docs/OpenApi/trendyol-e-faturam-entegrasyon-dokumani> | Başvuru ve `x-access-token` kullanımı; alt mükellef akışı ayrı | `UNKNOWN` |
| Taxpayer query | <https://developers.trendyolefaturam.com/OpenApi/Diğer/get-application-status-by-tax-id> | VKN/TCKN ile hizmet/aktivasyon sonucu; biçim kontrolünün yerine geçmez | `TaxpayerQuery=UNKNOWN` |
| E-Fatura create | <https://developers.trendyolefaturam.com/OpenApi/Giden%20eFatura/create-outgoing-e-invoice> | Stage create sözleşmesi; monetary alanların kuruş temsili entegrasyon rehberinde belirtilir | `InvoiceSubmit=UNKNOWN` |
| E-Arşiv status | <https://developers.trendyolefaturam.com/OpenApi/eArşiv/get-e-archive-status> | UUID ile ayrı status query sözleşmesi | `InvoiceStatusRead=UNKNOWN` |
| Document download | <https://developers.trendyolefaturam.com/OpenApi/Diğer/get-temporary-document-download-url> | Provider document read için ayrı sözleşme; kalıcı public saklama otoritesi değildir | `InvoiceDocumentRead=UNKNOWN` |
| E-Arşiv cancel | <https://developers.trendyolefaturam.com/OpenApi/eArşiv/cancel-e-archive> | UUID/company kapsamlı iptal sözleşmesi | `InvoiceCancel=UNKNOWN` |
| Trendyol invoice link | <https://developers.trendyol.com/reference/sendinvoicelink> | Seller/package kapsamlı link delivery ve 409 conflict davranışı | `InvoiceDeliver=UNKNOWN` |
| Trendyol invoice file | <https://developers.trendyol.com/reference/uploadinvoicefile> | PDF/JPEG/PNG ve güncel belgede azami 10 MB; package kapsamlı upload | `InvoiceDeliver=UNKNOWN` |

Provider raw status kodları ve request enum’ları adapter contract/fixture içinde tutulur; domain state veya mali policy olarak doğrudan kullanılmaz. Dokümanlar arası retention, bölge ve API yüzeyi farklılıkları kodlama gününde yeniden doğrulanır.

## Capability ve güvenli çalışma planı

1. `TaxpayerQuery`, `InvoiceSubmit`, `InvoiceStatusRead`, `InvoiceDocumentRead`, `InvoiceCancel`, `InvoiceDeliver` başlangıçta `UNKNOWN` kalır.
2. Provider connection; tenant, environment, firma/VKN scope’u, API sürümü ve credential türüyle ayrı tutulur; Trendyol marketplace connection’ıyla karıştırılmaz.
3. Credential şifreli saklanır; token API/UI/log’da gösterilmez. Credential/scope/version değişince bütün F4 capability evidence invalid olur.
4. Fake/contract adapter yalnız checksum’lı anonim fixture ile deterministic sonuç üretir; gerçek başarı gibi audit edilmez.
5. Read capability ancak resmî kaynak + fixture/test firma kanıtıyla; write capability ayrıca kullanıcı onaylı safe-write ile `SUPPORTED` olabilir.
6. Global external write, provider connection write ve `AUTO_INVOICE_ENABLED` üçü birlikte açık değilse otomatik submit job’u dış HTTP üretmez.
7. Manuel DRAFT/validation yerel çalışabilir; provider submit, cancellation ve marketplace delivery ayrı anahtarlarla kapalı kalır.
8. Submit timeout/connection loss `UNKNOWN_RESULT` üretir; query sonucu olmadan retry veya yeni invoice number alınmaz.
9. Marketplace delivery fatura submit sonucundan ayrıdır; 409 gibi remote conflict sonucu sorgu/reconciliation gerektirir, sahte başarı sayılmaz.
10. Production smoke; tek invoice, etki özeti, test edilebilir rollback/reconciliation ve ayrı kullanıcı onayıyla yapılır.

## Planlanan dosya etkisi

### Oluşturulacak

- `src/MarketplaceHub.Domain/InvoiceModels.cs`
- `src/MarketplaceHub.Application/F4Contracts.cs`
- `src/MarketplaceHub.Infrastructure/Persistence/F4ModelConfiguration.cs`
- `src/MarketplaceHub.Infrastructure/Persistence/F4BillingService.cs`
- `src/MarketplaceHub.Infrastructure/Persistence/F4JobProcessor.cs`
- `src/MarketplaceHub.Infrastructure/Persistence/F4ReconciliationService.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/TrendyolEFaturam/README.md`
- `src/MarketplaceHub.Infrastructure/Adapters/TrendyolEFaturam/TrendyolEFaturamOptions.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/TrendyolEFaturam/TrendyolEFaturamAuthenticationHandler.cs`
- `src/MarketplaceHub.Infrastructure/Adapters/TrendyolEFaturam/TrendyolEFaturamHttpClient.cs`
- Adapter `Contracts/`, `Mapping/`, `Ports/`, `ErrorMapping/` ve anonim `Fixtures/`
- Tek tarihsel F4 EF migration ve güncel model snapshot’ı
- `src/MarketplaceHub.Api/F4/F4Endpoints.cs`
- `src/MarketplaceHub.Web/src/F4Pages.tsx`
- `docs/adr/ADR-011-fiscal-rounding-deadline-adjustment.md`
- `docs/runbooks/invoice-operations.md`
- F4 domain/application/persistence/API/adapter/E2E test dosyaları
- `docs/implementation/F4-evidence-log.md`

### Değiştirilecek

- `AppDbContext`, DI, Worker F4 allow-list/dispatch, API composition, Web navigation/CSS.
- Capability/traceability/risk/external-dependency/environment-secret kayıtları.
- Private file/backup doğrulamaları yalnız InvoiceDocument sahipliği ve restore kapsamı kadar genişletilecek.

### Oluşturulmayacak

- F5+ adapter, route, menü veya placeholder; yeni solution/service/database; aktif tenant/user yönetimi; muhasebe/ERP modülü.

## Test ve kanıt planı

| Kanıt | Senaryo | Beklenen sonuç | Durum |
| --- | --- | --- | --- |
| `F4-EV-001` | Format + warnings-as-errors build | 0 warning, 0 error | PASS_BUILD |
| `F4-EV-002` | Fresh PostgreSQL 18 + F3→F4 upgrade | Tek migration zinciri; business/credential seed yok | PASS |
| `F4-EV-003` | Invoice state transition matrisi | Yalnız allow-list; lojistik state etkilenmez | PASS_LOCAL |
| `F4-EV-004` | VAT/discount/rounding/line-total property testleri | Onaylı policy ile deterministic toplam | BLOCKED_DECISION |
| `F4-EV-005` | VKN/TCKN + taxpayer + type fixture’ları | Biçim tek başına taxpayer sonucu olmaz | PARTIAL_LOCAL / BLOCKED_EXTERNAL |
| `F4-EV-006` | Aynı submit 20 paralel + worker-kill/timeout | Tek dış etki; unknown query’siz ikinci submit yok | PARTIAL_LOCAL / BLOCKED_EXTERNAL |
| `F4-EV-007` | XML/PDF checksum/private streaming | Immutable, tenant-safe, no-store ve restore edilebilir | PASS_LOCAL_CORE |
| `F4-EV-008` | Snapshot mutation | Customer/order değişikliği kesilmiş snapshot’ı değiştirmez | PASS_MODEL |
| `F4-EV-009` | Delivery failure/retry | Yeni invoice yok; yalnız delivery retry/reconcile | PARTIAL_LOCAL / BLOCKED_EXTERNAL |
| `F4-EV-010` | Partial cancel/return | Eski belge değişmez; policy’ye göre adjustment/cancel/manual issue | BLOCKED_DECISION |
| `F4-EV-011` | Secret/PII/log/API/fixture/manifest scan | Credential, token, TCKN/VKN, adres ve belge içeriği sızmaz | PASS_LOCAL |
| `F4-EV-012` | AUTO_INVOICE kill switch | Mali onay yokken dış submit job/HTTP yok | PASS_LOCAL |
| `F4-EV-013` | Invoice reconciliation | Status/ETTN/numara/hash/delivery farkı açıklanabilir | PARTIAL_LOCAL / BLOCKED_EXTERNAL |
| `F4-EV-014` | API/UI surface ve a11y | Yalnız F4 rotaları; unknown/manual states görünür; F5+ yok | PASS_BUILD |
| `F4-EV-015` | DB + files backup/restore | Document checksum ve read-only reconciliation geçer | BLOCKED_EXTERNAL |
| `F4-EV-016` | E-Faturam test firma E2E | Taxpayer→submit→query→document; veya açık dış blocker | BLOCKED_EXTERNAL |
| `F4-EV-017` | Kullanıcı onaylı delivery/production smoke | Düşük adet, audit/correlation ve rollback kanıtlı | BLOCKED_EXTERNAL |

## Dış bağımlılıklar ve blockerlar

| Kimlik | Kayıt | Güvenli fallback | Yerel blocker? |
| --- | --- | --- | --- |
| `BLOCK-F4-001` | Trendyol E-Faturam test firması/credential’ı yok. | Fake/contract; capability `UNKNOWN`; submit off | Hayır; SIT için evet |
| `BLOCK-F4-002` | API kullanıcısı mı pazaryeri entegratörü mü olunacağı provider hesabıyla kesinleşmedi. | Auth/connection production implementasyonu kanıt kapısında tutulur | Provider HTTP için evet |
| `BLOCK-F4-003` | Legal entity mali profilinin doğrulanmış girdileri sağlanmadı. | Şemasal model + masked validation; gerçek belge yok | Gerçek submit için evet |
| `BLOCK-F4-004` | Rounding, trigger, package scope, due başlangıcı/süresi, cancel/adjustment policy’si mali müşavirce onaylanmadı. | Auto-submit false; manuel mali issue | Otomatik mali işlem için evet |
| `BLOCK-F4-005` | KVKK/mali retention ve belge erişim politikası kesinleşmedi. | Minimum veri, private/immutable, hard-delete yok | Production yaşam döngüsü için evet |
| `BLOCK-F4-006` | Ubuntu sunucu/domain/public HTTPS yok. | Yerel private document; link delivery kapalı | Link delivery/SIT için evet |
| `BLOCK-F4-007` | Trendyol Stage package/test order ve invoice-delivery capability kanıtı yok. | Fixture; `InvoiceDeliver=UNKNOWN` | Delivery SIT için evet |

## Riskler

- `RISK-F4-001`: Güncel Trendyol link dokümanlarında saklama süresi bölge/sürüm bağlamına göre farklı görünebilir. Retention değeri domain sabiti yapılmayacak; ülke, güncel resmî kaynak ve mali/KVKK kararıyla effective-dated policy olacaktır.
- `RISK-F4-002`: E-Faturam bireysel API kullanıcısı ile pazaryeri entegratörü auth/onboarding akışları farklıdır. Hesap modeli doğrulanmadan iki yolu aynı adapter’da varsaymak yanlış tenant/firma scope’una neden olabilir.
- `RISK-F4-003`: Provider parasal alanları kuruş tabanlı taşıyabilir; domain decimal değerleri adapter sınırında checked dönüşüm ve fixture ile kanıtlanmazsa yüz katı tutar riski vardır.
- `RISK-F4-004`: Invoice XML/PDF ve party snapshot mali/KVKK hassas veridir; fixture, log, backup manifest ve API hata gövdesine sızma release blocker’ıdır.
- `RISK-F4-005`: F3 Stage kanıtlarının açık olması invoice’ın bağlı olduğu order/package verisinin gerçek platform E2E kanıtını sınırlar; bu bağımlılık F4 yerel çekirdeğini değil F4 çıkışını bloke eder.

## Açık kararlar

- `DEC-F4-001`: E-Faturam hesap/entegrasyon modeli API kullanıcısı mı, pazaryeri entegratörü mü? Provider onboarding ile doğrulanacak.
- `DEC-F4-002`: Invoice trigger state’i ve order mı package mı scope kullanılacağı mali/operasyon onayıyla belirlenecek.
- `DEC-F4-003`: Satır ve belge rounding kuralı, para birimi hassasiyeti ve kuruş dönüşüm politikası mali müşavirce onaylanacak.
- `DEC-F4-004`: Azami düzenleme süresinin başlangıç olayı, due hesabı ve geç kalma davranışı mali müşavirce onaylanacak.
- `DEC-F4-005`: Kısmi iptal/iade için cancellation, adjustment/iade belgesi veya manuel issue seçimi onaylı policy’ye bağlanacak.
- `DEC-F4-006`: Trendyol’a link mi dosya mı gönderileceği capability, ülke/paket türü, erişilebilirlik ve retention kanıtıyla seçilecek.
- `DEC-F4-007`: `AUTO_INVOICE_ENABLED` F4 boyunca varsayılan `false`; açılması ayrıca gerçek test ve kullanıcı onayı gerektirir.

## ADR etkisi

- Şartnamenin istediği mali rounding, deadline ve adjustment kararı için `ADR-011-fiscal-rounding-deadline-adjustment.md` oluşturulacaktır. Karar verilene kadar güvenli fallback’ler ve açık seçenekler kaydedilir; mali değer uydurulmaz.
- Mevcut ADR-001, ADR-003, ADR-004, ADR-005, ADR-006, ADR-007, ADR-008 ve ADR-010 uygulanmaya devam eder.
- Yeni servis, database, queue veya deployment topolojisi ADR-011’in konusu değildir ve önerilmez.

## F4 çıkış kriterleri

| Kimlik | Ölçülebilir koşul | Kanıt | Plan durumu |
| --- | --- | --- | --- |
| `F4-EXIT-001` | Mali ve lojistik state tamamen ayrıdır. | State/API/schema guard | DONE_LOCAL |
| `F4-EXIT-002` | Duplicate ve `UNKNOWN_RESULT` akışı ikinci belge üretmeden güvenlidir. | Job/attempt/idempotency + state testleri; gerçek timeout concurrency dış | PARTIAL_LOCAL / BLOCKED_EXTERNAL |
| `F4-EXIT-003` | XML/PDF immutable, checksum’lı, tenant-protected ve restore edilebilirdir. | File/schema/API guard; hedef restore smoke dış | PARTIAL_LOCAL / BLOCKED_EXTERNAL |
| `F4-EXIT-004` | Mali onay yoksa otomatik kesim ve dış submit kapalıdır. | Dörtlü kill-switch ve fail-closed adapter | DONE_LOCAL |

## Plan sonucu ve uygulama kapısı

Plan onaylanmış ve yerel domain/persistence/contract/API/UI çekirdeği `READY_LOCAL_CORE` durumuna getirilmiştir. Gerçek provider HTTP çağrıları yalnız resmî sözleşme + test firma/fixture kanıtı kadar açılır. Test firma, mali/KVKK kararları, Ubuntu sunucu/domain, backup/restore ve Stage package verisi nedeniyle gerçek submit/delivery ve F4 faz çıkışı `BLOCKED_EXTERNAL` kalır. F5 açılmamıştır.
