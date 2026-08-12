# F4 Trendyol E-Faturam Kanıt Günlüğü

## 2026-08-12 - Sağlayıcı yetki sözleşmesi hizalaması

| Kanıt | Durum | Not |
| --- | --- | --- |
| Partner → müşteri auth | CODED_TARGETED_VALIDATED | Resmî provider akışı geri eklendi: partner sign-in token'ı yalnız customerSignIn'da kullanılır; gerçek fatura çağrıları customer access token, companyId ve userId ile yapılır. Credential UI/persistence partner + Stage test müşteri/VKN alanlarını şifreli saklar. |
| Yerel doğrulama | PASS_LOCAL | E-Faturam contract testleri 38/38, Infrastructure build 0 hata/uyarı ve web typecheck geçti. |
| Gerçek Stage kabulü | BLOCKED_PROVIDER_API_ACCOUNT | Partner ve Stage test müşteri credential'ı henüz sağlanmadı; mevcut tekil hesap credential'ı yeniden kullanılmadı. |

## 2026-08-12 - VERIFIED Stage manual gate düzeltmesi

| Kanıt | Durum | Not |
| --- | --- | --- |
| Gate ayrımı | DEPLOYED_AND_STAGE_ENQUEUE_SUCCEEDED | Başarılı connection testinin bıraktığı `VERIFIED` Stage bağlantısı manuel submit için operasyonel kabul edildi; `READY` taslak normal endpointten parolasız kuyruğa alındı. Otomatik iş, DRAFT bağlantı ve Production `VERIFIED` bağlantısı fail-closed kalır. |
| Gerçek Stage submit kabulü | BLOCKED_PROVIDER_API_ACCOUNT | `#1177219188` E-Arşiv taslağının ilk ve tek submit denemesi provider `POST /api/invoice/documents/earchive` çağrısında `401` / `EFATURAM_AUTHENTICATION_FAILED` aldı. Resmî pazaryeri entegrasyon rehberi fatura çağrıları için partner `signIn` + `customerSignIn` müşteri token'ını zorunlu kılar; tekil hesap sign-in'i bu API yetkisini sağlamaz. Aynı taslak yeniden gönderilmedi. |

## 2026-08-12 - Tekil E-Faturam hesap kimlik doğrulaması

| Kanıt | Durum | Not |
| --- | --- | --- |
| Direct sign-in scope | STAGE_SCHEMA_DISCOVERED_VALIDATED | Partner/customer çalışma yolu kaldırıldı. Gerçek Stage tokenı değer gösterilmeden doğrulandı: sayısal `sub` kullanıcı, tek `privs` sayısal anahtarı firma kapsamıdır. Çoklu firma kapsamı fail-closed kalır. Contract testleri 32/32 ve Infrastructure build geçti. |
| Gerçek Stage kabulü | SUCCEEDED | `release-2026-08-12-v10.52` sonrası panelden başlatılan `EFATURAM_CONNECTION_TEST` işi `ae2c1681d72240d08d556f6be87777da` ilk denemede `SUCCEEDED`; bağlantı `VERIFIED`, hata kodu yok. Token/credential gösterilmedi. |
| Fatura işlemi Stage kabulü | NOT_RUN | Uygun test siparişi/oluşturma girdisiyle submit, status, PDF ve cancel akışları henüz başlatılmadı. |

## 2026-08-12 — Stage operator real-reason messaging

| Kanıt | Durum | Not |
| --- | --- | --- |
| Kullanıcı nedeni | CODED_TARGETED_VALIDATED | F4 write gate olumsuz sonucu artık capability desteği yok diye sunulmaz; dış yazmanın bağlantıda etkin olmadığı gerçek nedeni döner. Infrastructure build 0 hata/uyarı ve Web typecheck geçti. |

## 2026-08-12 — Manual runtime capability query removal

| Kanıt | Durum | Not |
| --- | --- | --- |
| Runtime dependency removal | CODED_TARGETED_VALIDATED | Capability sonucu policy tarafından kullanılmadığı halde yapılan F4 capability sorgusu kaldırıldı. Read/write policy aktif connection ve environment sınırına dayanır; Production write switch kontrolü korunur. `IntegrationRuntimePolicyTests` 3/3 ve Infrastructure build 0 hata/uyarı geçti. |

## 2026-08-12 — E-Faturam status endpoint configuration refactor

| Kanıt | Durum | Not |
| --- | --- | --- |
| Runtime gate ayrımı | CODED_TARGETED_VALIDATED | E-Fatura outgoing status path boşluğu `EVIDENCE_REQUIRED` değil `PATH_NOT_CONFIGURED` olarak döner. Manuel Stage işlemleri capability/evidence sebebiyle bloke edilmez; bilinmeyen endpoint tahmin edilmez. Infrastructure build 0 hata/uyarı geçti. |
| Gerçek Stage kabulü | NOT_RUN | Partner/müşteri Stage credential rotasyonu ve sağlayıcının güncel relative status endpoint yolu olmadan gerçek isteği başlatılamaz. |

## 2026-08-12 — v10.45 renewal UI live acceptance

- **Release/deploy:** `f9aa981` source CI PASS; `release-2026-08-12-v10.45` immutable publish PASS. App `sha256:3d17517d9271cde298c4d96ec70066ab7264a810ae52877e1ab565ee0f4681af`, edge `sha256:38edd3d7c8704d1a55bf82defbf4937d14db1c9b6f331f1110035bf28cc2fd36` Ubuntu hedefte healthy çalışıyor.
- **Canlı UI smoke:** E-Faturam STAGE detail ekranı `Yenileme gerekli`, partner + müşteri credential açıklaması ve mevcut `EFATURAM_CONFIGURATION_UNAVAILABLE` kodunu gösterdi. Secret veya eski credential içeriği görünmedi.
- **Kalan kabul:** Güncel Stage partner ve müşteri credential rotation'ı sağlanmadan gerçek provider connection/submit/status/PDF/cancel smoke `BLOCKED_CONFIGURATION` kalır.

## 2026-08-12 — Stage credential renewal UI

- **Davranış:** Son test kodu `EFATURAM_CONFIGURATION_UNAVAILABLE` olan E-Faturam kaydında credential kartı `Yenileme gerekli` ve Stage partner + müşteri credential açıklamasını gösterir.
- **Güvenlik:** Credential değerleri, eski payload alanları veya sağlayıcı tokenı gösterilmez. UI yalnız mevcut API hata kodunu kullanıcıya anlaşılır aksiyon olarak çevirir.
- **Doğrulama:** `F3Pages.test.tsx` 7/7 ve TypeScript `PASS_LOCAL`.

## 2026-08-12 — Stage connection smoke sonucu

- **Çalıştırılan iş:** Paneldeki normal `Bağlantıyı test et` eylemi `EFATURAM_CONNECTION_TEST` olarak enqueue edildi.
- **Sonuç:** `BLOCKED_CONFIGURATION` / `EFATURAM_CONFIGURATION_UNAVAILABLE`. Aktif şifreli `EMAIL_PASSWORD` kaydının varlığı ve `2026-08-02` oluşturulma zamanı secret içeriği okunmadan doğrulandı; kayıt, güncel partner + müşteri oturum şemasından öncedir.
- **Karar:** Stage manuel runtime kapıları kaldırılmıştır; sağlayıcının zorunlu partner e-posta/parola ve müşteri e-posta/parola/VKN credential alanları olmadan güvenli auth yapılmaz. Bu alanlarla credential rotation sonrası connection smoke ve mali submit kabulü yeniden koşulacaktır. Production korumaları değişmedi.

## 2026-08-12 — Stage normal submit web doğrulaması

- **Kapsam:** Invoice detail ekranında Production hassas onay göstergesi ve STAGE manuel normal submit davranışı.
- **Kanıt:** Production fixture `requiresSensitiveConfirmation: true` ile parola + açık onay kapısını korur. Stage fixture aynı normal `submit-jobs` yolunda bu alanı `false` taşır; istek boş parola/açık-onayla, ETag ve idempotency başlıklarıyla kuyruğa alınır.
- **Durum:** Yerel hedefli web testi ve source CI yeniden doğrulanacaktır; gerçek Stage provider smoke ayrı kabul kaydı olarak bekler.

## 2026-08-11 — Stage manual runtime refactor

- **Kapsam:** Trendyol E-Faturam STAGE/ACTIVE bağlantısında validate, submit, reconcile, cancel ve delivery akışları.
- **Kanıt:** normal Stage submit artık özel capability-probe job’una bağlı değildir; service, endpoint ve adapter katmanlarında capability/evidence, fiscal-policy, connection write-switch, `AUTO_*`, parola/re-auth ve açık onay runtime blocker değildir. Production’da bu korumalar korunur.
- **Korunanlar:** canonical mali payload doğrulama, ETag, idempotency, audit, provider response/error handling, permanent-link ve state-machine kontrolleri.
- **Doğrulama:** solution build ve web typecheck PASS; gerçek Stage E-Faturam smoke henüz çalıştırılmadı.

## 2026-08-09 — v10.15.1 CI biçimlendirme kaydı

| Kanıt | Durum | Not |
| --- | --- | --- |
| Repository formatter | PASS_LOCAL | Yeni manuel fatura belgesi endpointindeki import sırası `dotnet format MarketplaceHub.sln --verify-no-changes --no-restore` ile doğrulandı. Davranış veya dış etki değişmez. |
| Tam GitHub release doğrulaması | PENDING | Kaynak ve belge transaction'ı yeniden CI hattında doğrulanmalıdır. |

**Güncelleme:** 2026-08-05

## 2026-08-09 — v10.15 güvenli manuel fatura belgesi yükleme

| Kanıt | Durum | Not |
| --- | --- | --- |
| API ve dosya güvenliği | PASS_LOCAL | `POST /invoices/{id}/documents/manual`, aktif tenant ve idempotency anahtarı ister; PDF/JPEG/PNG dosya imzasını, 10 MiB sınırını ve tenant fatura sahipliğini doğrular. |
| Private arşiv ve audit | PASS_LOCAL | Belge `INVOICE_DOCUMENT_MANUAL` private asset olarak saklanır; SHA-256 ile yinelenen yükleme ikinci belge üretmez ve `INVOICE_DOCUMENT_MANUAL_UPLOAD` audit kaydı yazar. |
| Dış etki sınırı | CODED | Manuel belge yükleme E‑Faturam submit, iptal veya Trendyol fatura-link delivery job'u oluşturmaz; elle yüklenen belge kalıcı dış URL sayılmaz. |
| Yerel doğrulama | PASS_LOCAL | .NET solution build, API yüzey testi, TypeScript, 16/16 Vitest ve production web build geçti. |
| Stage mali E2E | REVALIDATION_REQUIRED | Bu private upload için dış yazma yoktur; mevcut F4 Stage mali kabul kapıları değişmez. |

## 2026-08-08 yerel eşitleme doğrulaması

- E-Faturam connection görünüm kontratına `hasCredential` alanı eklendi; frontend typecheck `PASS`.
- Fatura politikası yüklenirken kaydetme kilidi ve güncel bağlantı fixture sözleşmesi düzeltildi; frontend typecheck, production build ve 13 Vitest davranış testi `PASS`.
- Stage ve production durumu yükseltilmedi; Docker/PostgreSQL ve gerçek mali Stage kabulü bekliyor.

| Kanıt | Durum | Not |
| --- | --- | --- |
| Provider-managed connection | CODED_STATIC_VERIFIED / DYNAMIC_NOT_RUN | Mali hesap/seri/senaryo/kargo/ödeme ayarları kaldırıldı; connection settings yalnız dış-yazma anahtarını taşır. Eski JSON alanları data migration ve runtime sanitization ile temizlenir. |
| Partner → customer sign-in | CODED_STATIC_VERIFIED / STAGE_REVALIDATION_REQUIRED | Partner `signIn` tokenı yalnız `customerSignIn` isteğinde kullanılır. Şifreli credential kaydı partner e-posta/parolası ile müşteri e-posta/parolası ve 10/11 haneli müşteri VKN/TCKN'sini taşır; hiçbir değer yanıtta veya ayarda gösterilmez. |
| Customer fiscal scope | CODED_STATIC_VERIFIED / STAGE_REVALIDATION_REQUIRED | `companyId`, `userId` ve müşteri `accessToken` yalnız resmi `customerSignIn` yanıtından alınır. Eksik/biçimsiz yanıt `EFATURAM_CUSTOMER_SIGNIN_CONTRACT_INVALID` ile fail-closed durur. |
| Automatic invoice type | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | `commercial && eInvoiceAvailable` => `TEMELFATURA`; diğer siparişler => `EARSIVFATURA`. Ayrı taxpayer sorgusu veya senaryo ayarı yoktur. |
| E-Fatura/E-Arşiv create | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Kuruş payload, deterministic hash, provider varsayılan serisi ve `source=WEB` uygulanmıştır. |
| E-Arşiv internet satışı | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Payment/delivery kullanıcı ayarı değildir; Trendyol siparişi ve resmî carrier kataloğundan otomatik üretilir. Bilinmeyen sağlayıcı bloklanır. |
| E-Arşiv status | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Resmî UUID status endpoint'i ve numeric code catalog uygulanmıştır. |
| Giden E-Fatura status | FAIL_CLOSED_CONFIGURATION_REQUIRED | Public sözleşmede exact endpoint kesinleştirilmedi; deploy ayarı boşken adapter `EFATURAM_EINVOICE_STATUS_EVIDENCE_REQUIRED` döndürür. |
| Permanent PDF | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Permanent URL, HTTPS host allow-list, public DNS/IP, redirect, size, MIME ve `%PDF-` guardları vardır. |
| E-Arşiv cancellation | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Submit sonucu terminal sayılmaz; 305 görülene kadar reconciliation gerekir. |
| E-Fatura cancellation | NOT_SUPPORTED_BY_AUTOMATION | Mevzuata uygun itiraz/iptal süreci manuel inceleme olarak korunur. |
| Trendyol invoice-link | CODED_SUBMITTED_CONFIRMATION_REQUIRED | Duplicate teslim engellenir; kesin terminal query kanıtı yoksa manuel inceleme. |
| Operator UI | CODED_STATIC_VERIFIED / VITEST_PLAYWRIGHT_NOT_RUN | Yalnız E-Faturam credential, otomatik belge türü açıklaması, manuel paket policy, submit/reconcile/deliver/cancel, PDF ve filtre ekranları vardır. |
| Contract fixtures | CODED_STATIC_VERIFIED / DOTNET_NOT_RUN | JWT scope, resmî carrier alias, 205/305/105 status, ASCII tax-id, otomatik payload ve unknown-code testleri vardır. |
| Capability evidence policy | CODED_STATIC_VERIFIED / DOTNET_NOT_RUN | `TRENDYOL_EFATURAM` yalnız `developers.trendyolefaturam.com` kaynağını kabul eder; submit/cancel/deliver için 64 haneli Stage fixture SHA-256 zorunludur. |
| Exact runtime | BLOCKED_ENVIRONMENT | Bu çalışma ortamında .NET SDK/Docker ve pinli frontend bağımlılıkları yoktur. |
| Stage mali E2E | BLOCKED_EXTERNAL | Credential, kontrollü corporate/individual order/package ve safe-write onayı yoktur. |

## Production kararı

Kod kapanışı production kabulü değildir. Capability evidence, exact runtime suite ve Stage E2E olmadan global/connection write anahtarları kapalı kalır.

## 2026-08-12 — Manuel runtime capability/evidence ayrımı

| Kanıt | Durum | Not |
| --- | --- | --- |
| F4 enqueue/runtime | CODED_TARGETED_VALIDATED | Fatura submit, cancel, reconcile ve marketplace delivery için `UNKNOWN` capability/evidence artık manuel Stage veya Production işlemini tek başına kapatmaz. Production master + connection write switch, aktif connection/credential, input/ETag/idempotency, provider response/reconciliation ve audit kontrolleri korunur. |
| Stage onay sınırı | CODED_TARGETED_VALIDATED | Stage endpointleri parola ve açık onay istemez; credential, teknik mali doğrulama ve provider hata işleme korunur. Fiscal policy yalnız Production doğrulamasında ek şarttır. |
| Hedefli doğrulama | PASS_LOCAL | `IntegrationRuntimePolicyTests` 3/3 ve Infrastructure build 0 hata/uyarı geçti. Gerçek E-Faturam credential rotation ve provider E2E `NOT_RUN`dır. |
| Repository formatter | BLOCKED_REPOSITORY_LINE_ENDINGS | Solution formatter, değiştirilmeyen dosyalar dahil repository-geneli CRLF→LF `ENDOFLINE` ihlalleri nedeniyle çalışmadı. Bu refactor kapsamı dışında geniş satır-sonu dönüşümü yapılmadı. |
| Otomatik read-back capability ayrımı | CODED_TARGETED_VALIDATED | Submit/kabul/iptal sonrası reconciliation ve PDF read-back işleri artık capability evidence yokluğunda atlanmaz. İşler salt-okunurdur; durable dedup korunur, dış write veya `AUTO_*` kapsamı genişlemez. Infrastructure derlemesi 0 hata/uyarı geçti. |

## 2026-08-11 — auditli E-Faturam Stage canary hazırlığı

| Kanıt | Durum | Not |
| --- | --- | --- |
| Canary kapsamı | CODED_STATIC_VERIFIED | İş yalnız sabitlenmiş E-Faturam `STAGE` test hesabındaki mali doğrulaması geçmiş, gönderilmemiş `Ready` E-Arşiv taslağını kabul eder. Production bağlantısı hedef dışıdır. Taze Test Order sıfır tutarlı olduğundan mali doğrulama gevşetilmemiştir. |
| Güvenlik sınırı | CODED_STATIC_VERIFIED | Genel/connection dış-yazma ve otomatik-fatura anahtarları kapalı kalır; Stage istisnası yalnız canary `AdapterContext` işaretinde uygulanır. Normal submit, iptal ve Trendyol link delivery davranışı değişmez. |
| Operatör yüzeyi | PASS_LOCAL | Canary yalnız sabitlenmiş Stage test hesabındaki uygun `Ready` taslakta görünür; kullanıcı onayıyla parola/açık-onay istemez. ETag/idempotency ve Stage hesap sınırı korunur; normal mali işlem endpointleri parola/açık-onay ister. |
| Submit/status/PDF | RUNTIME_AND_STAGE_EVIDENCE_REQUIRED | Gerçek Stage canary başarılı olmadan `INVOICE_SUBMIT`, `INVOICE_STATUS_READ` veya `INVOICE_DOCUMENT_READ` `SUPPORTED` yapılmayacaktır. |
| Cancel/delivery | OUT_OF_SCOPE | İptal ve marketplace invoice-link delivery ayrı dış-yazma kabul senaryolarıdır; bu canary onları çalıştırmaz veya yükseltmez. |

## 2026-08-11 — Stage canary auth sözleşmesi düzeltmesi

| Kanıt | Durum | Not |
| --- | --- | --- |
| Sağlayıcı sözleşmesi | OFFICIAL_DOCUMENTATION_VERIFIED | Resmî Trendyol E-Faturam marketplace rehberi, partner `signIn` sonrası müşteri adına `customerSignIn` kullanılmasını; müşteri yanıtındaki `companyId`/`userId` değerlerinin sonraki mali çağrılarda zorunlu olmasını belirtir. |
| Kod düzeltmesi | CODED_STATIC_VERIFIED | Eski JWT token kapsamı çıkarımı kaldırıldı. Partner tokenı müşteri oturumu için header'da kalır; mali çağrılar yalnız müşteri `accessToken` ile yapılır. |
| Credential rotasyonu | REQUIRED_BEFORE_STAGE_RETRY | Önceki tek e-posta/parola kaydı artık sözleşmeye yeterli değildir. Stage bağlantısına partner ve müşteri test hesapları ile müşteri VKN/TCKN şifreli olarak yeniden kaydedilmeden canary tekrar çalıştırılmaz. |
| Önceki canary sonucu | SAFE_PRE_SUBMIT_FAILURE | `EFATURAM_TOKEN_SCOPE_MISSING` sağlayıcı mali create isteğinden önce oluştu; dış referans/ETTN/belge üretilmedi. Aynı Stage taslak yalnız bu hata kodunda ve dış referans boşken denetlenebilir tekil replay kabul eder. |
| Capability durumu | UNKNOWN | Başarılı gerçek submit → status → PDF zinciri henüz oluşmadı; `INVOICE_SUBMIT`, `INVOICE_STATUS_READ` ve `INVOICE_DOCUMENT_READ` yükseltilmedi. |
## 2026-08-10 — v10.32 faturalama ayar yüzeyi sadeleştirmesi

- Kullanılmayan genel faturalama ayarları kullanıcı menüsünden kaldırıldı; eski `/settings/billing` adresi sistem ayarlarına yönlenir.
- Fatura oluşturma, yükleme ve provider submit onay kapıları değiştirilmedi; dış yazma açılmadı.
- Web TypeScript ve Vitest PASS; provider/Stage mali akışı `NOT_RUN`.
- Uygulama kabugu E2E kontrolu kaldirilan Faturalama menusuyle esitlendi; r1 dokumantasyon kapisinda durdu ve canliya cikmadi. r2 exact release `PENDING`.

## 2026-08-09 — v10.19 fatura taslak ön izlemesi

- Sipariş satırındaki “Fatura Oluştur”, API kaynaklı müşteri, fatura adresi, satır, KDV ve tutar özetini modalde gösterir.
- Devam adımı yalnız mevcut idempotent `/invoices` taslak endpoint'ini çağırır; gerçek E-Faturam submit parola + açık onay akışında kalır.
- Doğrulama: ilgili web davranış testi dahil Vitest 18/18 PASS, TypeScript ve production build PASS.
- Stage/provider gerçek fatura gönderimi `NOT_RUN`; canlı görsel testte mali dış yazma başlatılmayacaktır.
# 2026-08-10 — v10.33 siparişten manuel fatura belgesi yükleme

- Sipariş menüsündeki Fatura Yükle, gerektiğinde yalnız yerel idempotent taslak oluşturur ve dosyayı mevcut `/invoices/{id}/documents/manual` endpointine gönderir.
- PDF/JPEG/JPG/PNG ve 10 MB istemci sınırı uygulanır; provider submit, müşteri gönderimi ve pazaryeri dış yazması tetiklenmez.
- TypeScript PASS; mevcut backend dosya imzası/depolama davranışı değiştirilmedi. Stage/provider ve tam mali suite `NOT_RUN`.

## 2026-08-11 — v10.35 eski paket allocation geri kazanımı

- Fatura taslağında paket-sipariş sahipliği zaten doğrulandıktan sonra allocation kaydı yoksa pozitif, iptal edilmemiş sipariş satırları kullanılır. Bu yalnız eski eşitleme verisi için geri uyumluluktur.
- Allocation var olduğunda mevcut miktar paylaştırma kuralları değişmeden korunur. İptal edilmiş veya pozitif miktarı olmayan satırlar taslağa girmez.
- Hedefli API build `PASS`; E-Faturam provider submit ve Stage mali kabulü `NOT_RUN`.
## 2026-08-12 — Partner/müşteri credential form regresyonu

| Kanıt | Durum | Not |
| --- | --- | --- |
| Form sözleşmesi | PASS_LOCAL | E-Faturam bağlantı formu, provider sözleşmesindeki partner e-posta/parola ile Stage test müşteri e-posta/parola ve 10/11 haneli VKN/TCKN alanlarını gönderir. Eski tekil credential alanları artık geçerli payload üretmez. |
| Hedefli web doğrulaması | PASS_LOCAL | `npm.cmd test -- --run` ile 5 dosyada 21/21 Vitest, `npm.cmd run typecheck` ve `npm.cmd run build` geçti. Playwright 3/3 senaryo geçti; yerel başlatıcı süreci süre aşımında kapanmadığı için komut sonucu `NOT_RUN` olarak ayrı tutulur. |
| Stage provider kabulü | BLOCKED_EXTERNAL_CREDENTIALS | Önceki tekil hesap kaydı, resmi partner → müşteri API zinciri için yeterli değildir. Şifreli partner ve Stage test müşteri API credential'ları ile VKN/TCKN yenilenmeden bağlantı smoke veya mali submit tekrarlanmaz. |
