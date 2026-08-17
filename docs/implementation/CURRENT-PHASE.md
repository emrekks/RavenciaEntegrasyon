# Güncel Faz ve Devralma Durumu

## 2026-08-17 - Ürün düzenlemede varyant kalıcılığı

Ürün düzenleme `PATCH /products/{id}` akışı artık yeni oluşturulan varyantları `variantsToCreate` ile append-only olarak kaydeder. Mevcut satış satırları, envanterleri veya dış liste bağlantıları silinmez. API SKU/barkod tekilliğini, kategori zorunlu özelliklerini, en fazla 1000 varyant sınırını ve ana envanter oluşturmayı doğrular; `If-Match` sürüm koruması devam eder. Varyant oluşturma ekranı mevcut satırları koruyarak yeni kombinasyonları ekler. .NET solution build ve hedefli katalog Vitest senaryoları geçti; tarayıcı/Stage kabulü `NOT_RUN`.

## 2026-08-17 - Varyant sıralama ve seçenek kontrastı

Ürün ekleme ve düzenleme ortak çalışma alanında, her varyant satırının solunda üç çizgili tutma kolu bulunur; kullanıcı bu kolu sürükleyerek satırları dikey sıraya koyabilir. Yeni ürün kaydında oluşan varyant dizisi bu görünür sırayı kullanır. Tema katmanındaki genel buton renginin boş/açık seçenek çiplerini görünmez yapması engellendi: pasif değerler koyu metin, seçili değerler beyaz metinle gösterilir. Hedefli web typecheck ile katalog ve ürün çalışma alanı testleri `9/9 PASS`; tarayıcı/Stage kabulü `NOT_RUN`.

## 2026-08-17 - Ürün düzenleme çalışma alanı eşitliği

Ürün düzenleme ekranı, ayrı bir JSX düzeni taşımadan ürün ekleme bileşeninin düzenleme modunu kullanır: temel ürün bilgileri, kategori özellikleri, varyant bazlı fiyat/stok, ölçü/desi, görsel ve Trendyol yayın alanları aynı render kaynağındadır. Kayıtlı ürün seviyesi özellik değerleri `ProductView` ile güvenli biçimde okunur ve mevcut optimistic-concurrency `PATCH /products/{id}` işlemiyle kaydedilir. Varyant stok/fiyat işlemleri mevcut idempotency ve sürüm korumalı endpointlerinde kaldı; yayın güvenliği ve Production kontrolleri değişmedi. Web typecheck ve hedefli ürün yayın Vitest dosyası geçti; tarayıcı/Stage kabulü `NOT_RUN`.

## 2026-08-17 - Trendyol approval polling interval correction

The durable approval processor explicitly requests a five-minute delay for `PRODUCT_APPROVAL_PENDING`, but the generic retry policy was applying its one-hour terminal backoff after repeated reads. The lease scheduler now preserves the five-minute interval only for that logical, read-only product-approval state; provider/network/rate-limit failures retain normal backoff and provider Retry-After semantics. Infrastructure and the test project build with zero errors. The targeted PostgreSQL lease test is `NOT_RUN_LOCAL_DOCKER_UNAVAILABLE` because local Testcontainers cannot reach `npipe://./pipe/docker_engine`. Source CI `32045775540`, immutable publish `32046114208`, backup `20260817T163708Z` checksum/restore-list, and v10.77 Stage deploy all passed. API, Worker, Caddy and PostgreSQL are healthy and external readiness is `Healthy`; the polling has reached attempt `30` and scheduled its next pending read-back for `18:30:59 UTC`.

## 2026-08-17 - Trendyol Stage approval reconciliation follow-up

The durable `TRENDYOL_PRODUCT_APPROVAL_RECONCILE` job was checked read-only at `18:30 UTC`. It remains `RETRY_SCHEDULED / PRODUCT_APPROVAL_PENDING` after attempt `30`; its next provider read-back is scheduled for `18:30:59 UTC`. No duplicate create, manual approval promotion, or provider write was issued. F3 remains pending the provider's terminal listing result.

## 2026-08-17 - Post-connection real Stage invoice-create result

After the v10.76 connection job succeeded, the panel queued the bounded E-Arşiv Stage canary on invoice `019ff6b4-f556-79d7-82cc-928709382389`. The worker completed `EFATURAM_STAGE_CAPABILITY_PROBE` at `2026-08-17 16:17:35 UTC` with `EFATURAM_ACCESS_TOKEN_REJECTED`. The invoice remains without an external reference. This confirms the application does not impose the previous synthetic-read blocker and that the remaining failure is the provider rejecting the fresh token on the actual create endpoint. No capability/evidence/fiscal-policy/user-approval/connection-switch gate blocked the manual Stage request; Production controls remain unchanged.

## 2026-08-17 - v10.76 release and Stage connection acceptance

Source CI `32044345183` and immutable publish `32044534115` passed for commit `21ceb57`. The release builder installed exact Buildx `v0.34.1` with SHA-256 verification, proving the manifest-429 remediation. Backup `20260817T161235Z` passed database/private-volume SHA checks, manifest presence and `pg_restore --list` before deployment. Ubuntu API, Worker, Caddy and PostgreSQL are healthy; external readiness is `Healthy`. The first new panel-triggered `EFATURAM_CONNECTION_TEST` after deployment completed `SUCCEEDED` at `2026-08-17 16:14:39 UTC`. This accepts encrypted credential loading, direct sign-in and validated single-company/user scope without a synthetic document request. It is not evidence of invoice-create acceptance; the provider protected invoice-write result remains a separate Stage requirement.

## 2026-08-17 - Immutable release Buildx download hardening

Source CI `32043779256` passed for commit `9f159c5`. Two release-tag runs then failed before registry authentication, image construction or deployment because `docker/setup-buildx-action` received GitHub raw-content `429` while resolving its Buildx manifest. The release workflow now downloads the same Buildx `v0.34.1` binary from its official release URL with bounded retries and verifies SHA-256 `f1332ddb9010bd0b72628266c3a906d9a6979848033df4c8d9bd2cd113bae12b` before creating the builder. This preserves the exact version and digest-only release path while removing the unauthenticated manifest dependency. Targeted workflow guard validation and a new source CI/release run are pending; no deployment occurred after either failed release run.

## 2026-08-17 - E-Faturam connection-test false-negative correction

The same direct Stage connection completed successful `signIn` tests at 12:34 and 12:36 UTC. Later failures began only after the connection test called the permanent-document endpoint with `Guid.Empty`. That endpoint requires a real document UUID, so the synthetic request can turn a valid sign-in into a false negative. The connection test now validates encrypted credential loading, direct `signIn`, and the single company/user scope only. Real document download, invoice submission, idempotency, audit, the Stage boundary, and every Production write safeguard are unchanged. Targeted adapter contract verification is pending.

## 2026-08-17 - E-Faturam protected-endpoint result visibility

v10.72 source CI `32040097876`, immutable publish `32040366761`, backup `20260817T145206Z` and Ubuntu API/Worker/Caddy/PostgreSQL/HTTPS health checks passed. The live Stage connection page now distinguishes a valid direct-account sign-in from provider protected-endpoint authorization rejection. `EFATURAM_ACCESS_TOKEN_REJECTED` is shown as a provider rejection of a fresh token, not as a password, capability, evidence or user-approval failure. The Stage manual operation remains available and Production controls are unchanged.

The protected permanent-document URL request now uses the media type documented for that endpoint (`Accept: text/plain`), while invoice create remains `Accept: application/json`. v10.73 source CI `32040768596`, immutable publish `32040993423`, backup `20260817T150421Z` and healthy deployment passed. A fresh no-effect Stage preflight still returned `401 / EFATURAM_ACCESS_TOKEN_REJECTED`, so content negotiation is excluded and the remaining blocker is the provider's token authorization on protected endpoints.

## 2026-08-17 - E-Faturam authorized-read connection preflight runtime result

The direct API_USER connection test previously proved only that `signIn` returned a token. v10.70 follows sign-in with a harmless protected permanent-document read using the all-zero UUID, company id, EARCHIVE type and PDF extension. The Stage runtime test received a fresh token from `signIn`, then the protected read returned `401 / EFATURAM_ACCESS_TOKEN_REJECTED`. This proves the credential/login path is valid and the create payload is not the root cause; the provider gateway rejects the fresh token on protected endpoints. No provider write or external reference was created. Capability, evidence, fixture, approval and write-switch gates did not block this manual Stage test; Production safeguards remain unchanged.

## 2026-08-17 - E-Faturam Stage replay runtime result

`v10.69` source CI and immutable release succeeded, and backup `20260817T141016Z` passed SHA/restore-list verification before a healthy Ubuntu deployment. The panel replay action queued and executed the bounded Stage canary. Attempt 5 reached the provider create endpoint but received `EFATURAM_ACCESS_TOKEN_REJECTED / 401`, with no external reference, ETTN, request id, or ambiguous external effect. The direct account `signIn` remains successful and the application did not block the request with capability/evidence/JWT-claim gating. The remaining blocker is the provider's opaque authorized-endpoint response; Production safeguards remain unchanged.

## 2026-08-17 - Safe Stage authentication replay

The Stage canary had a non-ambiguous provider 401 with no external reference but remained in `SUBMITTING`, so the newly corrected adapter could not be retried from the normal panel. The replay action now permits exactly this bounded case on the pinned Stage E-Faturam account. It does not open Production, does not replay a record with an external reference, and retains idempotency, validation, audit and response checks. Targeted Stage-probe tests are pending before a fresh immutable release and runtime retry.

## 2026-08-17 - E-Faturam Stage 401 classification correction

The direct API_USER `signIn` and connection test remain successful; the account is not the diagnosed fault. The public API contract documents the Stage gateway, `signIn`, `x-access-token`, companyId/userId and create endpoint, but not an `INVOICE_CREATE` JWT authorization contract. The claim-derived runtime branch was removed. Manual Stage create reaches the same provider endpoint without a claim/evidence gate and a genuine provider 401 remains safely visible as `EFATURAM_ACCESS_TOKEN_REJECTED`. The remaining investigation is the provider's opaque protected-endpoint response, not an account claim. Production fail-closed writes are unchanged.

## 2026-08-17 - E-Faturam token privilege ayrımı

Resmî API_USER belgesi mevcut `stage-apigateway`, `signIn`, `x-access-token`, companyId/userId ve create endpoint kullanımını doğruladı. Provider'ın gövdesiz/request-id'siz `401` sonucunu daraltmak için taze JWT yalnız yerel olarak incelenir: seçili firma privilege listesi açıkça `INVOICE_CREATE` içermiyorsa `EFATURAM_INVOICE_CREATE_PRIVILEGE_MISSING`, claim bilinmiyorsa mevcut `EFATURAM_ACCESS_TOKEN_REJECTED` üretilir. Token veya ham claim loglanmaz; bu sınıflandırma işlem yetkisi vermez ve Stage/Production güvenlik zincirini değiştirmez. Adapter contract `38/38`, source CI `32035385747` ve immutable publish `32035738058` PASS. `20260817T133652Z` doğrulanmış backup sonrası v10.67 deploy healthy oldu. Dördüncü gerçek Stage submit seçili firma privilege listesinde `INVOICE_CREATE` bulunmadığını gösterdi; 401 ile durdu ve dış referans/ETTN oluşmadı. Kalan dış adım Stage API_USER hesabı için provider tarafında invoice-create işlem kapsamının etkinleştirilmesidir.

İlk source CI davranıştan bağımsız formatter whitespace kuralında durdu; switch-expression yerleşimi merkezi formatter ile eşitlendi ve sonraki source CI geçti.

## 2026-08-17 - E-Faturam provider problem referansı teşhisi

`release-2026-08-17-v10.66` source CI `32033455000` ve immutable publish `32033891198` ile geçti; checksum/restore-list doğrulanmış `20260817T131624Z` backup sonrasında app `sha256:e261eab355ba8a44e17cf257dc4f9d4dbb62f1ee0591b4066c79106f3e497b1a`, edge `sha256:6fde74c33cf67972aebcd9203a04fbcba60780970c5bafdfb3ee19bfdd423509` deploy edildi. API/Worker/Caddy/PostgreSQL healthy ve readiness/frontend smoke geçti. Güncel bağlantı testi başarılıdır. Mali doğrulaması geçen eski Stage taslağının üçüncü denemesi, taze `signIn` sonrasında provider korumalı create endpointinde yine `EFATURAM_ACCESS_TOKEN_REJECTED / 401` ile durdu; provider `x-request-id` veya problem `instance` vermedi ve dış referans oluşmadı. Güvenli problem teşhisi deploy edilmiş ve çalışmıştır ancak provider ayrıntı göndermediğinden kalan durum `BLOCKED_PROVIDER_AUTHORIZED_ENDPOINT`tir; uygulama capability/onay/gate blocker'ı değildir.

## 2026-08-17 - E-Faturam Stage güvenli replay aksiyonu

Normal panelden güncel E-Faturam bağlantı testi yeniden çalıştırıldı ve `SUCCEEDED` oldu; hesap/credential ve `signIn` token üretimi sağlıklıdır. Eski canary taslağı `EFATURAM_TOKEN_SCOPE_MISSING / MANUAL_REVIEW` durumundayken backend aynı dış referanssız taslağın sabitlenmiş Stage hesapta güvenli replay'ine izin verdiği halde detay API'si `STAGE_CAPABILITY_PROBE` aksiyonunu üretmiyordu. Aksiyon üretimi backend doğrulamasıyla eşitlendi. Yalnız `TRENDYOL_EFATURAM + STAGE + Ravencia - Ravencia`, E-Arşiv, dış referansı olmayan `Ready` veya güvenli pre-submit scope replay kaydı kapsam içindedir. Production ve başka bağlantılar kapalı kalır. Hedefli backend `2/2`, F4 web `5/5` PASS; v10.65 release/deploy tamamlandı. Gerçek replay giriş aşamasını geçti; provider create çağrısı güncel kodla `401` döndürdüğü için submit capability yükseltilmedi.

## 2026-08-17 - v10.64 release ve iade veri smoke

`c18ca94` için source CI (`32026532850`) ve `release-2026-08-17-v10.64` immutable publish (`32026931375`) başarıyla tamamlandı. Ubuntu hedefte checksum ve PostgreSQL restore-list doğrulanmış `20260817T115450Z` backup sonrasında app `sha256:ecb225ea20a3f6759e25e5038939d6a600b6943c7eeadb3f1a33e94aba5d370f`, edge `sha256:8ab2e7e46a08a6ada482893559a14a657c6d54e0c0ac59455d5ad86c47ac4d43` ile deploy edildi. API/Worker/Caddy/PostgreSQL healthy, dış `/health/ready` `Healthy` ve frontend asset smoke geçti. PII göstermeyen aggregate kontrol, 26 iadenin tamamında bağlı sipariş, müşteri/adres snapshot'ı, anlamlı ad kaynağı ve en az bir iade satırı bulunduğunu; son üç `TRENDYOL_RETURN_SYNC` işinin `SUCCEEDED` olduğunu gösterdi. Yeni provider write başlatılmadı; F3/F4 dış kabul blocker'ları değişmedi.

## 2026-08-17 - Dashboard e2e güvenlik metni eşitliği

Dashboard Playwright kabuk testi eski "bağlantı bazında" metin beklentisi nedeniyle source CI'da başarısız oldu. Beklenti, görünür güncel "Environment sınırı ve dış yazma korumaları korunur" metnine güncellendi; `npm.cmd run test:e2e` `3/3 PASS` verdi. Bu test eşitliği provider iş akışını, Stage runtime sınırlarını veya Production korumalarını değiştirmez. Yeni source CI sonucu bekleniyor.

## 2026-08-17 - Stage fatura taslağı rehberinin runtime ile eşitlenmesi

Siparişten fatura taslağı açıldığında açıklama artık bağlantı ortamını esas alır. `STAGE` manuel gönderiminde ek parola/açık onay istemi yoktur; connection/credential, teknik mali input, idempotency ve provider response sınırları devam eder. `PRODUCTION` açıklaması parola/açık onayı korur. Dashboard da Stage manuel operasyonlarını capability kanıtı/açık onay ile yanlış bağlamaz; Production yazma zincirini açıkça korur. API yüzey testi Stage kısa devresinin Production açık-onay ve yeniden-doğrulama kapılarından önce kalmasını ayrıca korur. Gönderi detayındaki Stage etiket testi yalnız Stage paketinde görünür; Production normal ekranı teknik canary içermez. Bu değişiklikler provider isteği başlatmaz; hedefli web `6/6`, API `3/3` ve TypeScript typecheck geçti.

## 2026-08-13 - v10.63 eksik termin tarihi deployment kabulü

`c555c28` source CI `31650747089` .NET çözüm, Docker-backed Testcontainers, formatter, web test/build ve Playwright paketini geçti. `release-2026-08-13-v10.63` immutable publish `31650999736`, app `sha256:551eaa9cb4adab5bdab2e2662edf4aeec3d7cbd7ee99ef8b64d51b9e6e128e8e` ve edge `sha256:4bfeee466b9c9f13a2bd4468e312658e716c4946ea130cf9840a2501b4b403f5` üretti. Checksum ve `pg_restore --list` doğrulanmış rollback backup sonrasında deploy edildi; fail-closed config, migration, API/Worker/Caddy/PostgreSQL health, dış readiness `200` ve frontend asset smoke geçti. Bu sürüm eksik termin alanını uydurma gecikme olarak göstermeden açıkça eksik veri sayar.

## 2026-08-13 - Stage sipariş eşitleme ve eksik termin doğruluğu

Normal panelden başlatılan salt-okunur `TRENDYOL_ORDER_SYNC` correlation `d93a995127d24fd1a12e54db3769464c` ilk denemede `SUCCEEDED` oldu; panel 189 Stage siparişini gösterdi. Bu kabulde bazı sağlayıcı satırlarında termin alanının .NET varsayılan tarihiyle geldiği görüldü ve arayüzde uydurma binlerce günlük gecikme ürettiği doğrulandı. UI artık `0001-01-01` veya geçersiz tarihi eksik veri sayar: mikro ihracatta açık sağlayıcı mesajını, diğer siparişlerde `Termin zamanı bekleniyor` durumunu gösterir. Hedefli Vitest `6/6` ve TypeScript typecheck geçti. Dış yazma, fatura, idempotency veya Production güvenlik zinciri değişmedi.

## 2026-08-13 - v10.62 E-Faturam teşhis ve CI güvenlik düzeltmesi deployment kabulü

`88b9ca2` source CI `31649708460` ile .NET çözüm, Docker-backed Testcontainers, formatter, web build ve Playwright kontrollerini geçti. `release-2026-08-13-v10.62` immutable publish `31649967620` app `sha256:920be4db528bf20dc5785b5b8514425cc0f6193a67e8706737e4a6ced660ed43` ve edge `sha256:550b772f2cb10e52f120b42bf7937fa4b56bb77a75b2cb66660fc9630a0724b6` digestlerini üretti. `20260812T231433Z` backup seti checksum ve `pg_restore --list` ile doğrulandı; fail-closed compose validation, migration, API/Worker/Caddy/PostgreSQL health, dış readiness `200` ve frontend asset smoke geçti. Yeni sürüm, taze-token `401` teşhisini doğru kodla gösterir ve Testcontainers'ın düzeltme SSH.NET sürümünü kullanır; Stage/Production işlem güvenlikleri değişmedi.

## 2026-08-13 - CI güvenlik bağımlılığı düzeltmesi

CI, Testcontainers'ın geçişli `SSH.NET 2025.1.0` paketini yeni yayımlanan yüksek önem dereceli advisory nedeniyle hata olarak durdurdu. Paket merkezi `2026.0.0` düzeltme sürümüne yükseltildi; uyarıyı bastırmak yerine locked restore ve tam doğrulama yeniden çalıştırılacaktır. Bu yalnız test altyapısı bağımlılığıdır; deploy edilen runtime ve Stage/Production işlem kapıları değişmez.

İlk yeniden doğrulamada merkezi formatter, yeni E-Faturam contract testindeki import sırasını bildirdi; importlar düzenlendi. Bu test-kaynak biçimlendirme düzeltmesi için de transaction kaydı aynı değişiklikle tutulur.

## 2026-08-13 - E-Faturam taze-token teşhis doğruluğu

Resmî bireysel `API_USER` dokümanı, aynı Stage gateway'de `signIn` tokenı ile create/iptal/sorgu yapılacağını; `x-access-token` header'ının kullanılacağını doğrular. Mevcut uygulama bu sözleşmeyi zaten kullanır ve connection testi başarılıdır. Buna rağmen taze `signIn` ardından korumalı E-Arşiv endpointinden gelen `401`, artık yanlışlıkla giriş hatası değil `EFATURAM_ACCESS_TOKEN_REJECTED` olarak kaydedilir; bu provider tarafında hesabın işlem endpointi yetkisinin doğrulanması gerektiğini belirtir. İstek tekrar gönderilmez; Stage/Production write zinciri, idempotency ve fiscal validation değişmez. Hedefli adapter contract `62/62` ve Infrastructure build geçti.

## 2026-08-13 - v10.61 iade operasyon nedeni deployment kabulü

`cb56898` source CI `31646490936` ve `release-2026-08-12-v10.61` immutable publish `31646826162` geçti. `20260812T222805Z` PostgreSQL/private-volume backup setinin checksumları ve `pg_restore --list` doğrulandı; rollback kopyası `deploy/backups/20260812T222805Z-v10.61` altında saklandı. App `sha256:d5f8f83f6ef3c7367ee5a0b149970be04f8031b189f82e836319ca8bf608aafe`, edge `sha256:7c4efdf9477e5e54467415d8edf842d63f44216eb07a402df7050d69dc0170f2` olarak deploy edildi. Fail-closed config validation, migration, API/Worker/Caddy/PostgreSQL health, dış readiness `200` ve frontend asset smoke geçti. Girişli panelde `Created`/`REQUESTED` return detail, artık yanlış Production switch açıklaması yerine sağlayıcı durumunun onay/ret kabul etmediğini gösterdi.

## 2026-08-13 - Stage iade eşitleme yeniden kabulü

Normal paneldeki `İadeleri eşitle` komutu Stage Trendyol bağlantısında `TRENDYOL_RETURN_SYNC` olarak kuyruğa alındı ve ilk denemede `SUCCEEDED` oldu. Panel 26 iade kaydını, alıcı ve ürün satırı bilgilerini gösteriyor. Tek bekleyen claim uzak `Created`/yerel `REQUESTED` durumunda olduğundan karar endpointi bilinçli olarak açılmadı: bu sağlayıcı durum uygunluğu, capability/evidence ya da Production write switch'i değildir. İade detayındaki operatör metni bu gerçek nedeni gösterecek şekilde düzeltildi; Stage manuel karar akışının connection/auth/input/idempotency/provider-response sınırları ve Production write korumaları değişmedi.

## 2026-08-13 - Kapanış regresyon doğrulaması

Güncel `f810b31` çalışma ağacında `dotnet build MarketplaceHub.sln --no-restore` `0` hata/uyarı ile geçti. Docker gerektirmeyen çekirdek testler Domain `32/32`, Application `66/66` ve Trendyol/E-Faturam adapter sözleşme testleri `61/61` geçti. Frontend `npm.cmd run typecheck` ve tüm Vitest paketi `21/21` geçti. Yerel Docker CLI/engine olmadığı için PostgreSQL Testcontainers doğrudan çalıştırılmadı; ancak `845f351` için Linux source CI `31644381310` tam `dotnet test MarketplaceHub.sln`, formatter, web build ve Playwright paketini başarıyla çalıştırdı. Böylece Docker-backed integration/worker pipeline ve tam browser E2E CI'da `PASS` kanıtına sahiptir.

Güncel Stage kanıtı ürün create correlation `f9c945309efe4bf9acdd13dcd246b2aa` için create `SUCCEEDED`, approval reconcile `10/200` denemede `PRODUCT_APPROVAL_PENDING / RETRY_SCHEDULED` durumudur. Bu, dış Trendyol terminal onayını bekleyen `BLOCKED_PROVIDER_APPROVAL` durumudur; duplicate create yapılmadı. E-Faturam submit/status/PDF/cancel akışı mevcut doğrudan API_USER hesabında provider `401` ile durduğu için `BLOCKED_PROVIDER_API_SCOPE`; return action ve common-label write için uygun remote Stage fixture da `BLOCKED_REMOTE_FIXTURE` kalır. Bunların hiçbiri capability/evidence veya uygulama switch'i ile bypass edilmedi.

## 2026-08-12 - v10.60 ürün approval retry alignment deployment kabulü

`163327f` source CI `31620941782` ve `release-2026-08-12-v10.60` immutable publish `31621351060` geçti. `20260812T171539Z` PostgreSQL/private-volume backup seti için iki SHA-256 kaydı ve `pg_restore --list` doğrulandı; rollback kopyası `deploy/backups/20260812T171539Z-v10.60` olarak saklandı. App `sha256:4365d0b97db9ac56e9b4c0356a91e6c3900e32c62db0b7b2805e654145854d18`, edge `sha256:2edf42576cad4b2c646cf2cbfc206510daab40ef780c52080169d211d459fdcb` olarak deploy edildi. Migration exit `0`; API, Worker, Caddy ve PostgreSQL healthy; dış readiness `200`; deploy script'in frontend asset doğrulaması geçti.

Yeni ürün approval reconciliation işleri, mevcut retry backoff davranışı daha sık polling'e değişse dahi yedi günlük payload deadline'ından önce deneme limitine ulaşmayacak `2017` üst sınırını kullanır. Önceden oluşturulmuş Stage ürün approval işi `f9c945309efe4bf9acdd13dcd246b2aa`, deployment boyunca korundu ve dokuzuncu read-back sonrasında `PRODUCT_APPROVAL_PENDING / RETRY_SCHEDULED` durumunda kaldı; duplicate create yapılmadı. Terminal ürün onayı dış Trendyol kabulüne bağlıdır. Production endpoint/credential boundary, authorization, master + connection external-write switch, input validation, idempotency, reconciliation ve audit değişmedi.

## 2026-08-12 - Ürün approval poll deadline hizalaması

Ürün approval reconciliation işi yedi günlük yerel operasyon deadline'ı ile çalışır. Worker'ın genel retry politikası beşinci denemeden sonra en az bir saatlik jitter'lı geri çekilme uyguladığından mevcut `MaxAttempts=200` de pratikte deadline'dan önce tükenmez; canlı işte altıncı deneme `18:11 UTC` için planlandı. Yeni işler yine de scheduler ayrıntısından bağımsız kalmak için nominal beş dakikalık pencereye göre `(7 × 24 × 12) + 1 = 2017` deneme üst sınırını kullanır. Böylece ileride daha sık polling seçilirse yedinci gün sonunda normal `PRODUCT_APPROVAL_DEADLINE_EXPIRED / MANUAL_REVIEW` yolu korunur. Yeni dış yazma, retry bypass'ı veya Production güvenlik değişikliği yoktur.

Infrastructure build `0` hata/uyarı ile geçti. Ubuntu'da production compose'tan ayrı `/tmp/ravencia-product-approval-208ee30` kopyası ve ağsız SDK build container'ı ile Docker `--target build` (Release API/Worker publish) ve `MarketplaceHub.EndToEnd.Tests` compile `0` hata/uyarı ile geçti. PostgreSQL Testcontainers kullanan hedefli `FakeWorkerPipelineTests` yerel Docker motoru çalışmadığı için başlatılamadı; Ubuntu'da ise test runner'a production Docker socket'i ve host network verilmesi güvenlik politikasıyla reddedildi. Bu nedenle gerçek Testcontainers yürütmesi `NOT_RUN_SCOPED_RUNNER_REQUIRED`; test hatası başarı gibi gösterilmez. Güncel Stage approval işi mevcut deploy'da eski `MaxAttempts=200` ile `PRODUCT_APPROVAL_PENDING / RETRY_SCHEDULED` durumunda kalır; düzeltmenin bu işte etkinleşmesi için normal immutable release/deploy gerekir.

## 2026-08-12 - v10.59 E-Faturam doğrudan hesap deployment ve Stage kabulü

`30a7b53` source CI `31614008516` ve `release-2026-08-12-v10.59` immutable publish `31614469658` geçti. `20260812T155329Z` PostgreSQL/private-volume backup setinin iki SHA-256 kaydı ve `pg_restore --list` doğrulandı; rollback kopyası `deploy/backups/20260812T155329Z-v10.59` olarak saklandı. App `sha256:913c590fc0ecc1f3b837106f157d9a560448dde576d289f895ed39092b382efa`, edge `sha256:5c85c44d8740c827dc8dffe39c5df0678b708a66db3e694ac093c73a9e7d8aff` deploy edildi. Migration exit `0`; API, Worker ve Caddy healthy; dış readiness `200`.

Normal panelde bireysel hesap e-posta/parola formu görüldü. Mevcut şifreli credential ile `EFATURAM_CONNECTION_TEST` correlation `021ac1ba75204ef4b9e010f103c00edb` ilk denemede `SUCCEEDED`; partner veya müşteri VKN alanı gerekmedi. İlk yeni Stage E-Arşiv fixture'ı `#1238711676`, alıcı mali kimliği olmadığı için provider çağrısından önce `EFATURAM_RECIPIENT_TAX_ID_REQUIRED` ile doğru biçimde fail-closed durdu. API snapshotında gerçek 10 haneli alıcı mali kimliği bulunan `#698232919` için ayrı taslak yerel doğrulamadan geçti; kimlik değeri okunmadı. Tek submit denemesi korumalı `POST /api/invoice/documents/earchive` endpointinde `401 / EFATURAM_AUTHENTICATION_FAILED` aldı. Aynı taslak yeniden gönderilmedi.

Sonuç: doğrudan `signIn`, token claim scope parserı, Stage boundary ve payload üretimi çalışıyor; mevcut Stage hesabının tokenı korumalı fatura API yetkisine sahip değil. Resmî pazaryeri rehberi çoklu müşteri adına fatura için partner `customerSignIn` tokenını şart koşuyor. Aktif tek işletme doğrudan hesap modeli değiştirilmedi; E-Faturam tarafından bu hesaba fatura API kapsamı tanımlanmadan submit/status/PDF/cancel kabulü `BLOCKED_PROVIDER_API_SCOPE` kalır. Production güvenlikleri değiştirilmedi.

## 2026-08-12 - E-Faturam doğrudan hesap sözleşmesi düzeltmesi

Zorunlu gate–route–service–job envanteri `F4-stage-direct-account-gate-inventory.md` içinde çıkarıldı. Ana proje belgesindeki tek işletme `API_USER` kapsamına aykırı partner `signIn` → `customerSignIn` zorunluluğu route, persistence, adapter ve panelden kaldırıldı. E-Faturam bağlantısı artık yalnız hesap e-posta/parolasını şifreli saklar; `companyId` ve `userId` doğrudan `signIn` access token claimlerinden okunur. Token tek firma ve kullanıcı kapsamı vermiyorsa adapter fail-closed kalır. Stage manuel işlem politikası ile Production endpoint/credential boundary, master + connection write switch, authorization, input validation, idempotency, reconciliation ve audit kontrolleri değişmedi.

Solution build 0 hata/uyarı, hedefli E-Faturam contract testleri 38/38, ilgili web form testleri 7/7 ve TypeScript kontrolü `PASS`. Immutable release, deployment, normal panel connection testi ve provider mali E2E henüz `NOT_RUN`.

## 2026-08-12 - v10.58 Stage yayın açıklaması deployment kabulü

`a95909d` source CI `31611581747` ve `release-2026-08-12-v10.58` immutable publish `31612027079` tamamen geçti. Güncel Stage ürün/job kayıtlarını içeren `20260812T152558Z` backup setinin checksumları ve PostgreSQL dump listesi doğrulandı. App `sha256:be4ff60e41aaf675154711612c2e20d8e841fe026f2eaf8c458da3228d846e33`, edge `sha256:a6c27ff3fc76cc69a5508b11cce77dcb30ad540cf9631a0283c202be53e0c6d7` deploy edildi; migration exit `0`, API/Worker/Caddy/PostgreSQL healthy ve dış readiness `200`. Normal ürün formu smoke kontrolü Stage manuel yayının bağlantı, doğrulanmış kimlik bilgisi, geçerli ürün verisi ve tekrar korumasıyla çalıştığını; Production'ın master + bağlantı dış-yazma anahtarlarını ayrıca zorunlu tuttuğunu gösterdi. Approval reconciliation durable job'u deployment boyunca korundu ve halen provider `PRODUCT_APPROVAL_PENDING` sonucuyla otomatik readback yapıyor.

## 2026-08-12 - v10.57 Trendyol Stage ürün create kabulü

`27084f6` source CI `31608375061` ve `release-2026-08-12-v10.57` immutable publish `31608783961` tamamen geçti. Checksum ve `pg_restore --list` doğrulanmış `20260812T145942Z` rollback setinden sonra app `sha256:c45f2ece1e150fe042a02d96c711e9c9e55837f95bb000564557026c9277ce51`, edge `sha256:8c659ac2fa6982dcb10fc4203e74f19c2c56f9e11cf1792976eb715ee9b5c85d` deploy edildi. Migration exit `0`; API, Worker, Caddy ve PostgreSQL healthy; dış readiness `200`.

Normal panelde Bluz kategorisinin yerel `Kol Boyu` alanı sağlayıcıdaki opsiyonel sözleşmeyle hizalanarak `İsteğe bağlı` yapıldı; kategori mapping güncel snapshot'a `v4` olarak yeniden doğrulandı. Ayrı `Ravencia Stage Test` marka mapping'i, zorunlu Beden/Renk/Web Color değer mapping'leri, geçerli EAN-13, stok/fiyat ve HTTPS medya içeren ürün `019ff682-a871-7246-b778-7e2bcec261ae` olarak oluşturuldu. İlk fail-closed uyarı güncel kategori snapshot'ı gereksinimini doğru bildirdi; mapping yenilendikten sonra aynı ürün için tek create işi gönderildi. `TRENDYOL_PRODUCT_CREATE` correlation `f9c945309efe4bf9acdd13dcd246b2aa` ikinci batch poll'da `SUCCEEDED`; provider create batch'i kabul etti. `TRENDYOL_PRODUCT_APPROVAL_RECONCILE` işi provider durumunu beş dakikalık aralıklarla okuyor ve halen `PRODUCT_APPROVAL_PENDING / RETRY_SCHEDULED`; terminal onay readback'i bekleniyor. Duplicate create gönderilmedi. Production endpoint/credential boundary, master + bağlantı write switch, authorization, validation, idempotency, reconciliation ve audit kontrolleri değişmedi.

Ürün oluşturma ekranındaki eski capability/evidence veya Stage dış-yazma switch'ini runtime blocker gibi anlatan yardım metni de gerçek davranışla hizalandı: Stage manuel yayın bağlantı/auth/input/duplicate korumasını; Production ise bunlara ek master + bağlantı switch'lerini açıkça gösterir. Bu metin düzeltmesi v10.58 ile deploy edilip normal panelde doğrulandı.

## 2026-08-12 - Kategori özelliği zorunluluk yönetimi

Kategori eşleme çalışma alanındaki yerel ürün özellikleri artık kart üzerinden `Zorunlu` veya `İsteğe bağlı` olarak değiştirilebilir. Böylece sağlayıcı kategorisinde opsiyonel olan bir alan, eski yerel katalog tanımı nedeniyle ürün formunda ve manuel Stage yayınında gereksiz zorunlu alan oluşturmaz. Güncelleme mevcut version/`If-Match` sözleşmesi üzerinden yapılır; uzaktaki Trendyol verisini veya Production write güvenlik zincirini değiştirmez. `CatalogWorkspacePages.test.tsx` hedefli Vitest kontrolü 2/2 geçti. Immutable release, deployment ve gerçek Stage ürün create/readback kabulü henüz `NOT_RUN`.

## 2026-08-12 - v10.56 hedef Ubuntu izole restore tatbikatı

Checksum doğrulanmış `20260812T132906Z` backup seti, çalışan production DB/volume/network kaynaklarına bağlanmadan timestamp-scope internal Docker ağına ve temiz PostgreSQL/private volume'larına restore edildi. Database dump ve private archive SHA-256, archive safe-path, `iam/integration/ops` şemaları, 13 migration ve 1 tenant aggregate kontrolü geçti. Restore kopyasında scheduler policy'leri ve bekleyen işler Worker başlatılmadan önce devre dışı bırakıldı; `FeatureFlags__ExternalWrites=false` ve egress'siz internal ağ korundu. v10.56 app `sha256:389f288e88b835be617c9a26548fe553bf085d833e4a3fab02570e965441184e` ile no-op migration, API readiness ve Worker heartbeat `PASS`; ilk tatbikat 14 saniye sürdü. Trap yalnız `marketplacehub_restore_20260812t132906z_*` kaynaklarını kaldırdı; sonrasında production container health ve dış readiness yeniden `PASS`. Tekrarlanabilir `deploy/backup/restore-drill.sh`, shell syntax CI kapısı ve repository guard eklendi. Commit `d9d841c` için source CI `31604226847` tamamen geçti; sunucu committed sürüme fast-forward edildikten sonra aynı tatbikat 13 saniyede yeniden `PASS` verdi ve geçici container/volume/network listeleri boş doğrulandı. Şifreli off-host backup aktarımı hâlâ ayrı dış kapıdır; production dış yazmaları değiştirilmedi.

## 2026-08-12 - v10.55 Stage kabulü ve kategori hiyerarşisi düzeltmesi

`fed63f0` source CI `31599817178` ve immutable publish `31600192757` tamamen geçti. Checksum doğrulanmış `20260812T131446Z` backup sonrasında app `sha256:3bfd359b1406cbe2218f64705e67a3cb17b7535295da6aa6f6c5396e31a52f0e`, edge `sha256:7f4f8c9782a81a271560c0009be0fc5e56279c27ce8f2da1b25527f3bb3735d8` olarak deploy edildi; migration exit `0`, bütün servisler healthy ve dış readiness `200`. Normal panel yeniden kabulünde connection test `daca33547a604b58ba9f3b2efb32daef`, order sync `ff2fa68c4dbb441bab5253a3326c2522` ve brand sync `5ed2142619fd4657a040006ca6024705` ilk denemede başarılı oldu. Category sync `0882d3d6fa004f47978b5e7876a10b55`, genel hata yerine `REFERENCE_CONTRACT_INVALID` ile gerçek normalize nedenini açığa çıkardı: mapper'ın resmi kategori ağacından ürettiği child `ParentExternalId` değerleri sonraki scope gate'inde yanlışlıkla kök scope ile karşılaştırılıyordu. Gate artık kategorilerde her child parent'ının aynı cevap kümesinde bulunmasını ve self-parent olmamasını doğrular; scoped attribute/value ve brand eşitlikleri korunur. Düzeltmenin source CI'ı `31601096910`, immutable publish'i `31601437442` geçti. Checksum doğrulanmış `20260812T132906Z` backup sonrasında v10.56 app `sha256:389f288e88b835be617c9a26548fe553bf085d833e4a3fab02570e965441184e`, edge `sha256:a7bf1f83ec28e7de23d613dc97de650c80c39f7a84d462094a9c87f07a3ce34f` deploy edildi; migration exit `0`, servis health ve readiness `200`. Panel category sync `6d8661a3467d4814a57820beaea0a7b9` ilk denemede `SUCCEEDED`. Production kontrolleri değişmedi.

## 2026-08-12 - Trendyol Stage salt-okunur sipariş ve referans teşhisi

Normal panel operasyonlarıyla çalıştırılan Trendyol Stage bağlantı testi ilk denemede başarılı oldu (`8838053f02264cf1aa13ea675b4262e6`). Marka referansı da başarılı olurken (`f5ef22cb565f4ef6a28bb5803d48a142`), sipariş eşitleme (`1e2075ec26694ee2a83b45b6de5526d3`) ve kategori referansı (`3482afc1a86e415e9322bd20b61e54c9`) provider/runtime hatasıyla bloklandı. Sipariş stream sayfasının cursor güncellemesi yaptığı fakat opsiyonel exact-hydration çağrısının HTTP 400 ile tüm salt-okunur senkronizasyonu durdurabildiği doğrulandı. Yalnız exact hydration için `NotFound` veya `Validation/400` durumunda authoritative stream kaydı kullanılacak ve `ORDER_STREAM_HYDRATION_FALLBACK` audit'i yazılacak şekilde dar fallback kodlandı; diğer hatalar görünür ve retryable kalır. Kategori/marka zorunlu koleksiyonu boşsa artık genel `F3_JOB_REJECTED` yerine `REFERENCE_EMPTY_RESPONSE` üretilir ve mevcut snapshot değiştirilmez. Infrastructure build geçti. PostgreSQL Testcontainers kullanan iki hedefli test yerel Docker motoru kapalı olduğundan `BLOCKED_DOCKER`; merkezi CI sonucu bekleniyor. Production endpoint/credential boundary ve tüm dış-yazma kontrolleri değişmedi.

## 2026-08-12 - v10.54 immutable Stage deployment ve credential sınırı

Partner → müşteri E-Faturam yetki zinciri, source CI `31597532585` ve immutable release `31597903394` sonrası v10.54 olarak Ubuntu hedefe dağıtıldı. App `sha256:1bd4399e09e896be38c0eb9db512e00bb2e4314c2d58352448fe592f6245321c`, edge `sha256:fa08b7dbc96001967a3e4e00142d7b40bcf8d5249de95beac892c54846200cfe` ile çalışıyor; taze `20260812T124726Z` backup sonrası migration, API/Worker/Caddy health, frontend asset ve dış readiness `200` geçti. Panelde normal Stage bağlantı testi ayrıca denenerek eski tekil credential payload'ının provider isteği yapmadan `EFATURAM_CONFIGURATION_UNAVAILABLE` ile fail-closed kaldığı, kullanıcıya yalnız `Yenileme gerekli` ve partner + Stage test müşteri alanlarının gösterildiği doğrulandı. Gerçek provider kabulü için gerekli partner/test müşteri API credential'ları ve VKN/TCKN hâlâ dış önkoşuldur; Production kontrolleri değiştirilmedi.

## 2026-08-12 - VERIFIED Stage fatura operasyon kapısı

Başarılı E-Faturam bağlantı testi bir `DRAFT` bağlantıyı `VERIFIED` durumuna getirir. Bu durum Stage'de credential doğrulanmış operasyonel bağlantıdır; önceki F4 gate yalnız `ACTIVE` aradığı için normal manuel submit/reconcile/cancel eylemlerini yanlışlıkla gizliyordu. Runtime policy artık `VERIFIED` bağlantıyı yalnız manuel Stage read/write için kabul eder. Production'da `VERIFIED` yeterli değildir: read/write için `ACTIVE`, write için buna ek olarak global + bağlantı switch'i ve mevcut Production zinciri zorunludur. Hedefli policy testi ve gerçek Stage submit kabulü bekleniyor.

## 2026-08-12 - Gerçek Stage E-Arşiv submit sonucu

`release-2026-08-12-v10.53` sonrasında `#1177219188` için oluşturulmuş `READY` E-Arşiv taslağı normal manuel submit yolundan parolasız kuyruğa alındı; bu, `VERIFIED` Stage gate düzeltmesinin etkin olduğunu kanıtladı. Worker, provider'ın `POST /api/invoice/documents/earchive` isteğini `401` ile reddetti ve denemeyi `EFATURAM_AUTHENTICATION_FAILED` olarak kaydetti; aynı fatura tekrar gönderilmedi. Resmî E-Faturam pazaryeri rehberi, fatura API'lerinde partner `signIn` token'ı üzerinden `customerSignIn` ile alınan müşteri token'ını zorunlu kılar. Tekil hesap credential'ı sign-in'i geçse de bu provider API yetkisini vermez. Bu dış sağlayıcı yetki/hesap ön koşuludur; uygulama Stage gate'i, idempotency ve Production korumaları sağlam kalır. Partner + test müşteri API hesabı olmadan submit/status/PDF/cancel kabulü `BLOCKED_PROVIDER_API_ACCOUNT` durumundadır.

## 2026-08-12 - Partner / müşteri E-Faturam yetki zinciri geri hizalaması

Gerçek 401 kabulünden sonra adapter ve panel, resmî sözleşmedeki partner `signIn` → `customerSignIn` modeline geri hizalandı. Credential rotation artık partner e-posta/parolası ile Stage test müşteri e-posta/parolası ve VKN/TCKN ister; firma/kullanıcı kapsamı ve dış çağrılarda kullanılan token yalnız customer sign-in yanıtından gelir. Stage manuel write'da capability/evidence, switch, re-auth veya ek onay kapısı eklenmedi. Hedefli E-Faturam contract testleri 38/38, Infrastructure build, web Vitest 21/21 ve typecheck geçti. Yeni credential olmadan gerçek provider kabulü başlatılmadı.

## 2026-08-12 - E-Faturam tekil hesap oturumu

E-Faturam bağlantısı artık partner/alt müşteri akışına bağlı değildir. Tekil hesap e-postası ve parolası şifreli credential olarak saklanır. Gerçek Stage sign-in tokenı token/secret değerleri açıklanmadan incelendi: kullanıcı kimliği sayısal `sub`, tek firma kapsamı ise `privs` içindeki sayısal anahtardır. Parser bu şekli ve önceki açık `companyId/userId` sözleşmesini destekler; çoklu firma kapsamı fail-closed kalır. `release-2026-08-12-v10.52` ile yayımlanan parser sonrasında panelden başlatılan `EFATURAM_CONNECTION_TEST` işi `ae2c1681d72240d08d556f6be87777da` ilk denemede `SUCCEEDED`; bağlantı `VERIFIED` ve hata kaydı yok. Eski partner payload'ları güvenli biçimde yapılandırılamadı durumunda kalır. Stage teknik kontrolleri ile Production dış-yazma güvenlik zinciri korunur. Fatura oluşturma/status/PDF/cancel gerçek Stage kabulü uygun test siparişi ile hâlâ `NOT_RUN`.

## 2026-08-12 - İade alıcı bilgisi API fallback'i

İade liste ve detay API görünümleri alıcı adını sipariş snapshot'ından üretir. Trendyol Stage örneği üst seviye `customerFirstName/customerLastName` alanlarını `Adı Soyadı` placeholder'ı olarak döndürdüğünde, uygulama artık aynı siparişin API'den gelen fatura adresi; o da uygun değilse teslimat adresi ad/soyad alanını kullanır. Gerçek müşteri adı varsa önceliği korunur; anlamlı bir API değeri yoksa değer uydurulmaz ve `—` gösterilir. E-posta/telefon/vergi numarası için de adres fallback'i genişletildi. Hedefli `F3ReturnCustomerNameTests` 3/3 ve web TypeScript kontrolü geçti. Normal panelden başlatılan güncel `TRENDYOL_RETURN_SYNC` işi `3cb60e4e9e674ed39e1b091020af6ad1` ilk denemede `SUCCEEDED`. `release-2026-08-12-v10.50` immutable app `sha256:65d00b4158f860d85b1c582ff00a919baf84f04ef3335d81eda5dba593802f0e` ve edge `sha256:01a27dd0ca3b1c13878533eb3d68d60a67c469c4c0c854774bb99f06c36ece9d` deployment'ı checksumlı `20260812T093713Z` backup sonrasında tamamlandı; API/Worker/Caddy/PostgreSQL healthy ve iç/dış readiness `200`. Canlı aggregate kontrolünde 26/26 iade bağlı siparişe, müşteri adı snapshot'ına ve en az bir adres adı snapshot'ına sahip; kişisel veri okunmadı. Girişli iade tablosu görsel smoke kontrolü oturum yokluğu nedeniyle `NOT_RUN`.

## 2026-08-12 - v10.49 immutable deployment ve canlı Stage UI smoke

`84ba728` source CI `31548069815` ve `release-2026-08-12-v10.49` immutable publish `31548345363` başarıyla tamamlandı. App `sha256:c2698b0666ea3948260c41b450ec774b81a4cf83cb1ac1ccecb227a99b17d7cd`, edge `sha256:35673e1db13d8f302ffeade17e709c088dd55a5355d2e1a41d895fb7a3a35ad7` digestleri taze `20260812T000007Z` checksumlı backup sonrasında hedefe deploy edildi. Compose validation, migration, API readiness, Worker/Caddy health, frontend asset ve dış `/health/ready` 200 geçti.

Normal panel smoke doğrulaması Trendyol Stage bağlantısında `Stage işlemleri / Hazır`, Stage salt-okunur sync açıklamaları ve teknik capability listesinin operasyon yüzeyinden kaldırıldığını doğruladı. E-Faturam Stage paneli de aynı Stage yüzeyini gösteriyor ancak mevcut credential güncel partner + müşteri oturum şemasında değil: test işi teknik `EFATURAM_CONFIGURATION_UNAVAILABLE` ile fail-closed kalıyor. Secret tahmin edilmedi veya gösterilmedi. Gerçek Stage ürün/fiyat-stok write kabulü de mevcut yalnız eksik alanlı yerel `DENE/dene` ürününde güvenli fixture bulunmadığından `NOT_RUN` kalır.

## 2026-08-12 - Fail-closed deploy Compose runtime düzeltmesi

Ubuntu hedefte kullanıcı Compose eklentisi `5.3.1`, root'un onaylı eklentisi ise `2.40.2` olarak ayrıştı. Deploy scripti exact `2.40.2` şartını kontrol ederken kullanıcı eklentisini çağırdığı için `--validate-only` doğru biçimde fail-closed durdu; image kaydı veya çalışan servis değiştirilmedi. Script artık compose ve worker inspect çağrılarını tutarlı olarak `sudo docker` üzerinden yapar. Hedefte bu onaylı ikili ile compose configuration `PASS` verdi. Değişiklik kaynak CI/release/deploy kabulü bekliyor.

## 2026-08-12 - Stage operasyon yüzeyi ve taze sipariş kabulü

Panelde Stage bağlantısı artık yanlış biçimde kapalı dış yazma veya zorunlu safe-write kanıtı göstermez. Manuel Stage işlemleri aktif bağlantı, credential, teknik girdi doğrulaması, tekrar koruması ve sağlayıcı yanıt doğrulamasıyla çalışır; teknik capability/evidence kayıtları normal kullanıcı yüzeyinden kaldırılıp İşlem Takibi/diagnostics yüzeyinde tutulur. Production kartı master + bağlantı write switch korumasını göstermeye devam eder.

Normal panel akışından yeni `TRENDYOL_STAGE_TEST_ORDER` işi `2a51b03fb93a4815ac872e75bc2ff42b`, ardından yalnız o sipariş için `TRENDYOL_ORDER_SYNC` işi `81789fc3d09c47bc971f01348f2a8a8d` ilk denemede `SUCCEEDED` oldu. Oluşturulan sipariş `1507428594`, paket `92287436`, takip numarası `7250000170858397` ve yerel/uzak durum `ReadyToShip` olarak doğrulandı. Taşıyıcı `Yurtiçi Kargo Marketplace` olduğundan common-label sözleşmesine uymuyor; güvenli `LABEL_WRITE` canary gönderilmedi ve capability yükseltilmedi. Uygun Aras/TEX Stage fixture kabulü `NOT_RUN` kalır.

## 2026-08-12 - Stage operator real-reason messaging

Fatura ve ürün yayın yüzeyleri capability kanıtı eksikliğini işlem engeli gibi göstermez. Stage manuel işlemlerinin doğrudan sağlayıcıya gittiği, Production’ın ise aktif bağlantı ve dış-yazma anahtarlarıyla korunduğu kullanıcıya açık biçimde gösterilir.

## 2026-08-12 - Stage operator action visibility

Gönderi/iade detayları, manuel Stage işlemlerini artık capability kaydı eksik diye gizlemez. Geçerli Stage bağlantısında paket işlemleri, PDF etiket denemesi ve `ACTION_REQUIRED` iadede onay/ret görünür; input, idempotency, provider yanıtı ve iade ret nedeni/kanıt kuralları korunur. Production görünümü mevcut write güvenlik davranışında kalır.

## 2026-08-12 - Manual runtime capability query removal

F4 read/write policy ve Trendyol read enqueue yolu artık karar sonucu kullanılmayan `PlatformCapabilities` sorgusunu da yapmaz. Stage manuel akışında capability/evidence kayıt deposu erişimi runtime blocker değildir; Production için environment, aktif bağlantı, global/connection write switch ve diğer işlem güvenlikleri korunur.

## 2026-08-12 - E-Faturam status endpoint configuration ayrımı

`INVOICE_STATUS_READ` için eksik outgoing e-Fatura sorgu yolu capability/evidence kapısı değil, sağlayıcı endpoint konfigürasyonudur. Manuel Stage istek geçerli göreli yol kaydedildiğinde ek onay veya evidence olmadan sağlayıcıya gider. Yol boş ya da geçersizken adapter endpoint tahmini yapmaz; bu teknik konfigürasyon eksikliği fail-closed kalır. Production environment/credential sınırı ve write kontrolleri değişmez.

## 2026-08-12 - Fatura otomatik read-back capability ayrımı

Fatura submit, kabul ve iptal sonrasında oluşan reconciliation/PDF read-back işleri capability kanıtı yok diye artık atlanmaz. İşler yalnız salt-okunur provider çağrılarıdır; durable dedup korunur ve dış write/`AUTO_*` kapsamı genişlemez. Infrastructure derlemesi 0 hata/uyarı geçti.

## 2026-08-12 - Scheduled salt-okunur capability ayrımı

Scheduler içindeki `ORDERS`, `RETURNS` ve `REFERENCE_DATA` read politikaları artık `UNKNOWN` capability nedeniyle sessizce atlanmaz. Aktif bağlantı, policy interval/jitter ve durable dedup korunur; provider read hataları mevcut retry/audit akışına gider. Bu sadece salt-okunur otomatik sync kapsamıdır; dış write, `AUTO_*` ve Production switch davranışını açmaz. Infrastructure derlemesi 0 hata/uyarı geçti.

## 2026-08-12 - Manuel runtime capability/evidence ayrımı

Gate–route–service–job envanteri sonrasında Catalog, Inventory, F3 Sales ve F4 Billing manuel enqueue yollarındaki capability/evidence runtime kapıları kaldırıldı. `UNKNOWN` capability artık Stage veya Production manuel read/write işini durdurmaz; destek kaydı diagnostics ve release kabulü olarak korunur. Production dış yazmasında master + connection switch, etkin connection/credential, input doğrulama, idempotency, provider response/reconciliation ve audit korunur; Stage’de switch, fiscal policy, re-auth ve açık onay aranmaz. Hedefli `IntegrationRuntimePolicyTests` 3/3 ve Infrastructure build 0 hata/uyarı geçti. Otomatik işlerin `AUTO_*` ayrımı ve gerçek Stage kabul senaryoları ayrıca `NOT_RUN` durumundadır.

Repository-geneli `dotnet format MarketplaceHub.sln --verify-no-changes --no-restore` bu değişiklikten bağımsız mevcut CRLF→LF `ENDOFLINE` ihlalleri nedeniyle çalışmadı; ilk hata değiştirilmemiş `src/MarketplaceHub.Domain/CatalogModels.cs` dosyasındadır ve hata aynı biçimde çok sayıda değiştirilmemiş dosyaya yayılır. Geniş satır-sonu dönüşümü bu refactor kapsamına alınmadı; formatter sonucu `BLOCKED_REPOSITORY_LINE_ENDINGS` olarak kaydedildi.

## 2026-08-12 - Ortak etiket Stage fixture taşıyıcı sınırı

Taze Trendyol Stage Test Order `1265633895` paketinin güvenli metaverisi taşıyıcının `Yurtiçi Kargo Marketplace` olduğunu doğruladı. Trendyol'un resmî common-label sözleşmesi yalnız Trendyol öder Aras Kargo veya TEX gönderilerinde geçerlidir; bu nedenle geçmiş `LABEL_WRITE` denemelerindeki `REMOTE_REQUEST_REJECTED` sonucu bir capability kanıtı değildir. Kuyruğa alma ve worker katmanı artık uyumsuz taşıyıcıyı uzak `Picking`/label çağrısından önce `COMMON_LABEL_CARRIER_UNSUPPORTED` ile fail-closed durdurur. `CommonLabelCarrierPolicyTests` 7/7 geçti. `LABEL_WRITE` ile `SHIPMENT_WRITE` elle yükseltilmedi; uygun taşıyıcılı gerçek Stage fixture kabulü `NOT_RUN`dır. Production capability, global/connection write-switch, idempotency ve audit kontrolleri değişmedi.

## 2026-08-12 - v10.45 E-Faturam credential renewal UI deployment

`f9aa981` source CI PASS ve `release-2026-08-12-v10.45` immutable publish PASS sonrasında app `sha256:3d17517d9271cde298c4d96ec70066ab7264a810ae52877e1ab565ee0f4681af`, edge `sha256:38edd3d7c8704d1a55bf82defbf4937d14db1c9b6f331f1110035bf28cc2fd36` digestleri deploy edildi. Taze backup, fail-closed Compose kontrolü, migration, API/Worker/Caddy health ve `/health/ready` 200 geçti. Canlı E-Faturam Stage ekranı `Yenileme gerekli`, credential şeması açıklaması ve `EFATURAM_CONFIGURATION_UNAVAILABLE` hata kodunu gösteriyor; secret görünürlüğü veya production dış yazma davranışı değişmedi.

## 2026-08-12 - E-Faturam Stage credential durum görünürlüğü

E-Faturam bağlantısında şifreli credential kaydı bulunup güncel müşteri oturum şemasına uymadığında panel artık yanıltıcı `Şifreli kayıtlı` etiketi göstermez. `Yenileme gerekli` durumu ve partner + müşteri bilgileriyle Stage credential rotation açıklaması görünür; hiçbir secret veya eski credential alanı gösterilmez. Hedefli web testi ve TypeScript kontrolü geçti.

## 2026-08-12 - E-Faturam Stage credential payload sonucu

Yeni deploy'da normal `EFATURAM_CONNECTION_TEST` çalıştırıldı ve `EFATURAM_CONFIGURATION_UNAVAILABLE` ile güvenli biçimde bloke oldu. İçeriği okunmadan doğrulanan aktif şifreli credential kaydı `2026-08-02` tarihli `EMAIL_PASSWORD` payload'ıdır; güncel müşteri oturum sözleşmesinin partner e-posta/parola ile müşteri e-posta/parola/VKN alanlarını içermeden geçerli sayılması mümkün değildir. Bu bir capability veya onay kapısı değildir; Stage hesabının güncel beş alanla credential rotation'ı gerekir. Credential içeriği gösterilmedi, üretim güvenlikleri değiştirilmedi.

## 2026-08-12 - v10.44 immutable deployment ve Stage iade kabulü

`ed2dfc9` kaynak CI'si başarıyla tamamlandı; `release-2026-08-12-v10.44` immutable release'i app `sha256:214a3bc4614c0573a3915eba3705abff4814b6c7b6a8d2607bf6300f3742334a` ve edge `sha256:1dcd2fd246d71b80a817cf690f7b5c6309995eb72fc3a8bc96ab2b0fa8722ad3` digestleriyle yayımlandı. Hedef Ubuntu sunucusunda taze geri dönüş yedeği ve checksum kontrolü sonrası migration, API/Worker/Caddy deploy edildi; `/health/ready` 200 ve bütün servisler healthy. Panelde normal `TRENDYOL_RETURN_SYNC` manuel Stage read işi `8542af70a19c4464b78273ee54c9fd16` ilk denemede `SUCCEEDED`; iade ekranı 25 paketi ürün satırlarıyla gösterdi. Production dış yazma güvenlikleri değiştirilmedi; E-Faturam gerçek Stage provider smoke hâlâ `NOT_RUN`dır.

## 2026-08-12 - v10.44 Stage submit web regression testi

Invoice detay testi, Production fatura gönderiminde parola ve açık onayın gerekli olduğunu açıkça işaretler. Ayrı Stage fixture'ı normal `submit-jobs` yolunun parola/onay olmadan kuyruğa alındığını doğrular; eski özel canary yolu normal Stage operasyonunun ön koşulu değildir. Yeni source CI doğrulaması bekleniyor.

## 2026-08-12 - v10.43 CI biçimlendirme düzeltmesi

Stage manuel runtime refactorunun ilk main CI koşusu, yalnız yeni policy testindeki satır biçiminden başarısız oldu; uygulama kodu veya runtime davranışı hatası yoktu. Test, repository formatter beklentisine göre düzenlendi. Solution build ve ilgili policy testleri tekrar geçti; yeni source CI koşusu bekleniyor.

## 2026-08-11 - Stage manuel runtime sınırı

Stage bağlantılarında normal manuel read/write artık capability evidence, fixture SHA, connection write switch, mali policy onayı, `AUTO_*`, parola yeniden doğrulaması veya ek açık onay nedeniyle bloke olmaz. Bu istisna yalnız `STAGE` + `ACTIVE` bağlantı ve manuel job bağlamı için geçerlidir; teknik payload doğrulama, idempotency, concurrency, audit ve provider hata işleme korunur. Endpoint seçimi `STAGE`/`PRODUCTION` dışındaki environment değerlerinde fail-closed’dur; Stage ve Production base URL’leri HTTPS ve birbirinden farklı olmak zorundadır. Production read/write, capability, global/connection write switch, mali policy, parola/onay ve mevcut otomatik akış korumalarını sürdürür. Gerçek Stage kabulü bu değişiklikten sonra yeniden çalıştırılacaktır.

## 2026-08-11 - Stage capability canary sonucu

Resmî ortak etiket sözleşmesi, create çağrısının paket `Picking` veya `Invoiced` durumuna beslendikten sonra yapılmasını ister. Taze test paketi `ReadyToShip` döndüğü için ilk create isteği doğru endpoint ve gövdeyle `400` reddi aldı; bu kanıt `LABEL_WRITE` için yeterli değildi. Canary artık yalnız en son auditli Stage Test Order, `STAGE/2738` ve tek doğrulanmış satır üzerinde önce resmî `Picking` payloadını gönderir, sonra ortak etiket create → read-back zincirini çalıştırır. Bu dar istisna genel/production dış-yazma anahtarlarını açmaz; başarılı olursa yalnız `PICKING` shipment-write aksiyonu ile label kanıtları güncellenir.

İlk dar `Picking` canary’si de uzak platformda `REMOTE_REQUEST_REJECTED` ile sonlandı. Bir sonraki teşhis yürütmesi için non-success gövdesinden yalnız şemadaki `code`/`errorCode` alanı, harf içeren ve güvenli karakter setiyle sınırlanarak alınır; ham hata gövdesi, mesaj, takip numarası veya credential kaydedilmez.

Taze paketin ilk sync'i, eski canonical eşleme nedeniyle aynı kaynak olayını `ManualReview` olarak saklamıştı. İdempotent upsert, geçmişte kaydedilmiş aynı raw event için yalnız `ManualReview → tanınan canonical durum` projeksiyonunu güvenle iyileştirir; yeni dış çağrı, yeni durum olayı veya sıralama bypass'ı üretmez. Eşitleme ardından label write canary tekrar bekleniyor.

Gerçek yeniden eşitleme, sağlayıcının aynı paket/raw durum için olay zamanını değiştirdiğini gösterdi; event kimliği bu nedenle farklılaştı ve normal sıralama kapısı `ManualReview` durumunu korudu. Onarım, yalnız aynı paket ve aynı raw durum için `ManualReview → tanınan canonical durum` projeksiyonuna genişletildi; yeni dış etki veya history kaydı üretilmez.

Sonraki gerçek re-sync, boş order-line listeli tekrar yanıtında erken dönüş optimizasyonunun paket projeksiyonuna ulaşmayı engellediğini gösterdi. Bu optimizasyon kaldırıldı; mevcut idempotent history, miktar bütünlüğü ve dar `ManualReview` onarım koşulları korunuyor.

Taze paketin yeniden eşitlemesi, boş satırlı cevapların en baştaki satır-miktar korumasından da geri döndüğünü gösterdi. Boş satır yalnız mevcut siparişin local projection onarımı için kabul edilir; satır varsa mevcut miktar bütünlüğü kapısı değişmeden çalışır. Onarım, order'ın önceki remote zamanı nedeniyle update edilmese dahi yalnız tanınan local raw package durumunu `ManualReview`den canonical duruma dönüştürür.

Resmî örnek sözleşmesiyle oluşturulan yeni Stage Test Order `1265633895`, salt-okunur order-sync ile paket `92286944` / takip `7250000170847858` olarak alındı. Uzak durum `ReadyToShip` idi; yerel canonical durum eşlemesinde bu açık yazım eksik olduğu için paket fail-closed `ManualReview` kaldı. `ReadyToShip` eşlemesi yalnız `ReadyToShip` yerel durumuna eklendi; dış yazma kapsamı değişmedi. Eşitleme ve label create → read-back kabulü tekrar bekleniyor.

İlk gerçek Test Order işi, Worker F3 dispatch allow-list'inde yeni job tipi eksik olduğundan `UNSUPPORTED_JOB_TYPE` ile dış çağrı yapmadan terminal kaldı. Allow-list düzeltildi. İkinci gerçek Stage isteği `REMOTE_SERVER_ERROR` ile döndü; resmi Test Order örneğiyle karşılaştırılarak adres sözleşmesindeki alanlar ve resmi örnek test barkodu tamamlandı. Capability durumu değişmedi ve yeni Stage yürütmesi bekleniyor.

Capability `UNKNOWN` kayıtları kanıtsız biçimde `SUPPORTED` yapılmadı. Owner/Admin tarafından başlatılan Trendyol `STAGE` etiket canary'si, paket `92257909` / takip `7250000170335942` üzerinde gerçek ortak etiket read-back'iyle başarılı oldu: `LABEL_READ=SUPPORTED`, resmi kaynak URL'si, Stage/store kapsamı, format kısıtı, audit kaydı ve 64 karakterlik SHA-256 fixture kanıtı saklandı. `LABEL_WRITE` için tarihsel fixture'lar uzaktan reddedildi ve `UNKNOWN` kaldı. Bunu çözmek için yalnız Stage seller `2738` kapsamında, resmî test barkoduyla, tek denemelik ve auditli taze Test Order job'u eklendi. Yeni sipariş read-sync ile alınıp dönen güncel takip numarasında create → read-back geçmeden `LABEL_WRITE` destekli yapılmayacak. Normal production/external-write anahtarları değişmedi.

Test Order yanıtındaki `orderNumber` alanı resmi Stage servisinde JSON metni veya sayısı olarak dönse de kayıpsız okunacak şekilde uyumlu hale getirildi; Infrastructure hedefli derlemesi geçti.

## 2026-08-11 - v10.41 iade satırı bağlantısı

v10.40 CI `#129`, release `#119`, deployment/readiness ve return-sync kabulü geçti; panelde 23 gerçek Stage iadesi görünür. Nested claim satırındaki `orderLine.id` ile `claimItem.orderLineItemId` farklı olduğundan ürünler 0 görünüyordu. Gerçek şema ve yerel DB karşılaştırmasıyla sipariş bağı parent `orderLine.id` alanına düzeltildi; claim action kimliği değişmedi. Hedefli test/release/deploy ve idempotent yeniden eşitleme bekleniyor.

**Kabul sonucu:** v10.41 CI `#130`, release `#120`, deployment/readiness ve tam Stage backfill geçti. `RETURN_READ=SUPPORTED`; return-sync işi `92dc03f1bca241e49687a5aad9987dcd` ilk denemede başarılı. Panel 25 paket gösteriyor, tümünde 1–5 ürün var ve sıfır ürünlü kayıt kalmadı. Dış yazmalar kapalı kaldı.

## 2026-08-11 - v10.40 Stage return-sync süre düzeltmesi

v10.39 CI `#128`, immutable release `#118`, deployment/readiness ve Stage capability kabulü geçti; `RETURN_READ` gerçek kanıtla `SUPPORTED`. İlk tam iade eşitlemesi sekiz status çağrısını sırayla beklerken `REMOTE_TIMEOUT` ile güvenli retry'ye girdi. Yalnız Stage 404 fallback'indeki bağımsız GET çağrıları paralelleştirildi; production ve dış yazma davranışı değişmedi. Hedefli test/release/Stage sync yeniden kabulü bekleniyor.

## 2026-08-11 - v10.39 Stage claims durum filtresi

v10.38 release `#117` başarıyla üretildi ve app `sha256:64dd84776130d8d7a04ca134cd9bbd43c5437fa8f271094e4af67a30c1d8b90d`, edge `sha256:532a406a07cdf076d30881ca2d1375729004467437f362bf349c7b02a6565b7a` digestleriyle sunucuya dağıtıldı; readiness HTTP 200. Stage testi header fallback'in 404'ü değiştirmediğini gösterdi. Credential göstermeyen hedefli probe, aynı endpoint'in `claimItemStatus=Created` ile HTTP 200 ve gerçek claim cevabı verdiğini kanıtladı. Adapter yalnız Stage filtresiz 404 durumunda resmî claim statülerini ayrı sorgulayacak şekilde düzeltildi; production ve dış yazmalar değişmedi. Adapter testleri `50/50 PASS`; yeni CI/release/deploy ve Stage kabulü bekleniyor.

## 2026-08-11 - v10.38 Trendyol Türkiye claims Stage fallback

R3 production deployment başarıyla tamamlandı; yeni app/edge digestleri, migration, API readiness, Worker health ve frontend asset kontrolleri geçti. Güncel paketle Stage bağlantı testi `17:34:46` tarihinde tekrarlandı ve yalnız `RETURN_READ`, resmî claims GET çağrısında `REMOTE_RESOURCE_NOT_FOUND`/HTTP 404 bıraktı. Türkiye V2 getClaims referansında `storeFrontCode` headerı tanımlanmadığından canonical TR başlıklı GET korunup yalnız 404 durumunda aynı salt-okunur endpoint başlıksız bir kez yeniden denenir. Yazma çağrıları, secrets ve dış yazma kapıları değişmedi. Trendyol adapter sözleşme testleri `50/50` geçti; CI/release/deployment ve Stage yeniden kabulü bekleniyor.

## 2026-08-11 - v10.37-r3 release token bağlamı

`release-2026-08-11-v10.37-r2` koşusunun gerçek job logu, source-gate curl çağrısından önce `GITHUB_TOKEN: unbound variable` hatasını gösterdi. GitHub'ın yerleşik tokenı yalnız bu adıma `github.token` bağlamından aktarıldı; workflow düzeyindeki yetki `actions: read`, `contents: read`, `packages: write` ile sınırlı kaldı. Main source CI `#125` başarıyla tamamlandı; `release-2026-08-11-v10.37-r3` immutable publish `#116` source kapısı, app/edge build-push, provenance/SBOM ve digest doğrulamalarını geçirdi. App `sha256:69fba5c25a395cb0fe449040677c37c9c60842c8d6e6d73fc3fc48f2bacca6ed`, edge `sha256:02de6da2a569282ce033d72bedf1e391709f6f88168aefc66f2097a6fb6185dd`. `panel.ravencia.com` hedefi `63.180.140.51` olarak çözüldü; yerel SSH anahtarı/agent ve AWS Console oturumu bulunmadığından deployment `BLOCKED_TARGET_ACCESS`, Stage return-read yeniden kabulü `NOT_RUN` kaldı. Dış yazmalar değiştirilmedi.

## 2026-08-11 - v10.37-r2 immutable release kapısı

`release-2026-08-11-v10.37` etiketi, başarılı source CI'a rağmen Checks API'nin workflow adı yerine `verify` job adını döndürmesi nedeniyle imaj buildinden önce fail-closed durdu. Kapı; exact `verify.yml` workflow koşularında aynı SHA, `main`, `push` ve `success` şartlarını Actions API ile doğrulayacak biçimde düzeltildi. Canlı GitHub Actions API sorgusu doğru source koşusunu döndürdü ve repository guard testleri `5/5` geçti. Required status check adı, immutable imaj, provenance/SBOM ve digest kontrolleri değişmedi. Yeni main CI ve `v10.37-r2` release koşusu bekleniyor.

## 2026-08-11 - v10.37 Trendyol resmi iade satırı sözleşmesi

Trendyol getClaims cevabının güncel resmî `items[].claimItems[]` alanı, tarihsel düz `items[]` ve doğrudan `claimItems[]` geri uyumluluğu korunarak iade eşleyicisine eklendi. Resmî cevap miktar alanı göndermediğinde her claim item tek iade satırı olarak `1` adet kabul edilir; durum ve iade nedeni de aynı resmî satır koleksiyonundan okunur. Solution build, Trendyol adapter sözleşme testleri `50/50`, web testleri `19/19`, typecheck ve production web build `PASS`; Docker motoru kapalı olduğundan Testcontainers grupları yerelde `BLOCKED_TOOLING`, Linux CI bekleniyor. Bağlı Stage hesabında yeni bağlantı testi `SUCCEEDED` (correlation `b9738ca506b74e9fb4f44045ad77c12f`), fakat dağıtılmış eski sürüm `RETURN_READ=UNKNOWN` bırakmıştır. Güncel sürümün Stage dağıtım/yeniden kabulü beklenir; dış yazmalar kapalıdır.

## 2026-08-11 - v10.36 iade eşitleme ve referans çalışma alanı

Uzak Trendyol iade claim'i yerelde henüz bulunmayan siparişe bağlı olduğunda, iade kaydı sessizce kaybedilmez: aynı sipariş önce salt-okunur exact read ile içeri alınır, başarılı ilişkilendirmeden sonra claim saklanır. Sipariş uzakta da yoksa mevcut audit kaydı ve fail-closed davranış korunur. İade ekranı; tüm iade, talep, kargo, aksiyon, onay/red, analiz, ihtilaf ve askı sekmeleri; müşteri/sipariş/iade kodu/barkod/sebep/tarih filtreleri ve referans sütun düzeniyle yenilendi. Dış iade aksiyonu veya capability kapıları değiştirilmedi. Infrastructure build, Trendyol adapter sözleşme testi `50/50`, iade operasyonları web testi `4/4` ve web typecheck `PASS`; gerçek Stage return-read kabulü `NOT_RUN`.

**2026-08-11 Stage teşhisi:** Bağlı Trendyol Stage hesabında `TRENDYOL_CONNECTION_TEST` işi `SUCCEEDED` oldu; ORDER/PRODUCT/REFERENCE read destekli kalırken `RETURN_READ` gerçek claims probu başarısız olduğundan `UNKNOWN` kaldı. Read capability kapısı kaldırılmadı. Capability API/UI artık probe evidence notunu da taşır; dağıtılan sürümde hata kodu operatöre görünür olacaktır. Bu teşhis değişikliğinin Stage tekrar koşusu `NOT_RUN`.

## 2026-08-11 - CI trigger tekrarı azaltma

`Verify source changes` artık yalnız pull request ve `main` push'larında çalışır; `release-*` tag push'u aynı kaynak doğrulamasını ikinci kez başlatmaz. Tam doğrulama setine Playwright E2E de `main` kapısında eklendi. Immutable image yayın akışı, tag/manuel commit'in `main` üzerinde olduğunu ve aynı SHA için başarılı `Verify source changes` GitHub check kaydı bulunduğunu doğrulamadan registry oturumu açmaz. İki immutable image build/push, provenance/SBOM ve digest doğrulaması korunur; release concurrency iptal edilmez. İlk GitHub doğrulama koşusu, eski release doğrulama testinin kaldırılan yinelenen .NET/web komutlarını beklemesi nedeniyle başarısız oldu; test, yeni fail-closed source-gate sözleşmesini doğrulayacak biçimde güncellendi ve yeniden koşu bekliyor.

## 2026-08-11 - v10.35-r3 yayın kapısı kaydı

Fatura işlemleri menüsünün erişilebilir adı görünen başlıkla eşitlendi; hedefli F3 bileşen testi `4/4 PASS` verdi. Tam regresyon, Stage ve canlı kabul `NOT_RUN` durumundadır; bu kayıt yalnızca yayın kapısındaki test hizalamasını belgeler.

**2026-08-10 eşleştirme ve manuel fatura yükleme v10.33:** Kategori özellik eşlemesinde panel kategorisi açıkça seçilebilir ve teknik Trendyol başlık önekleri arayüzden temizlenir. Özellik/değer ekleme aksiyonları ayrıldı; marka alanı hesap seçimi göstermeyen kategori tipi oluşturma/çip düzenine geçti. Sipariş fatura menüsü, yerel taslak gerektiğinde idempotent oluşturup PDF/JPEG/JPG/PNG dosyasını mevcut güvenli manuel belge endpointine yükleyen ayrı pencereye bağlandı. Provider submit ve pazaryeri dış yazma kapıları değişmedi. TypeScript ve hedefli 7/7 F3 testi geçti; tam suite, Stage ve canlı kabul `NOT_RUN`.

**2026-08-10 siparis gorsel CSP v10.29:** Trendyol kaynak snapshot'inda bulunan HTTPS gorsellerin tarayicida CSP nedeniyle kirik gorunmesi giderildi. `img-src` yalniz resmi `https://cdn.dsmcdn.com` kaynagina acildi. Fatura rozetleri dar kolona sigacak sekilde kompaktlastirildi ve baslik-ilk siparis boslugu kaldirildi. Degisiklik CSS/Caddy kapsamlidir; detayli test `NOT_RUN`, hedefli header ve tarayici gorsel kabulu release sonrasinda yapilacaktir.

**2026-08-10 siparis kaynak gorseli v10.27:** Trendyol onayli urun cevabinin canlida kullandigi dogrudan varyant satiri sekli, eski ic ice `variants[]` sekliyle birlikte okunur. Barkodla bulunan kaydin `images[]` alani siparis satiri kaynak snapshot'ina aktarilir; boylece API'de var olan urun gorseli yer tutucu olarak kalmaz. Dar kapsamli adapter sozlesme testi eklendi; canli kabul yeniden esitleme sonrasinda tamamlanacaktir.

**2026-08-10 sipariş kaynak satırı v10.26:** Trendyol paket satırının ham snapshot'ı sipariş satırında kalıcı saklanır. Renk, beden ve model kodu sipariş kaynağından; görsel ise aynı barkodun salt-okunur Trendyol onaylı ürün snapshot'ından alınır ve yalnız HTTPS URL kabul edilir. Fatura hücresi referans düzenine hizalandı, kargodaki satırın ikincil işlem menüsü kaldırıldı ve tablo başlığı kaydırmada sabitlendi. Migration mevcut sipariş tutarlarına/durumlarına dokunmaz; eski satırların zenginleşmesi için deployment sonrası salt-okunur yeniden eşitleme gerekir. Solution build, Docker gerektirmeyen testler, TypeScript, 19/19 web testi ve production web build geçti; yerel Docker/Testcontainers testleri `BLOCKED_ENVIRONMENT`.

**2026-08-10 eşleştirme yayın doğrulaması v10.25:** Kategori kapsamlı özellik ve değer eşleştirmesi için Playwright senaryosu, kalıcı kayıt gövdesindeki `scopeExternalId` alanını doğrulayacak biçimde güncellendi; 3/3 tarayıcı testi geçti. Yeni immutable-image hattı bu commit için çalıştırılacaktır.

**2026-08-10 eşleştirme merkezi v10.24:** Kategori ekranı hesap seçimi göstermeden yalnız Trendyol kapsamıyla çalışır; referans snapshotı güvenilir arka plan bağlantısından alınır. Yerel kategori tek adla oluşturulur, eklenen kategoriler seçim/çıkarma baloncuklarıyla yönetilir. Mevcut bir kategori özelliğine yeni seçenek değeri ekleme engeli kaldırıldı ve seçim ön izlemesi anlık yenilenir. Kategori altındaki özellik/değer eşlemeleri eksik kapsam kimliğiyle kaydedildiği için görünmüyordu; artık kategori/özellik kapsamıyla kalıcı kaydedilir. TypeScript, 19/19 web testi ve production web derlemesi geçti; GitHub exact-toolchain release hattı yeniden çalıştırılacaktır.

**2026-08-10 sipariş görseli ve operasyon satırı v10.22:** Sipariş satırı katalog varyantını artık önce ID/SKU, ardından barkod ile çözer; aktif varyant görseli yoksa aynı ürünün aktif ana görseline güvenli biçimde geri düşer. Böylece ürün görseli bulunan katalog kayıtları sipariş ekranında boş kutu yerine görselini gösterir; renk/beden gibi imza seçenekleri aynı eşleşmeden sunulur. Mikro ihracat fatura rozeti “Mikro İhracat Faturası” olarak netleştirildi; kargodaki siparişte etiket aksiyonları yerine yalnız Kargo Takip ve işlem yapılamaz bilgisi gösterilir. Filtre alanı arama odaklı, daha sade yüzey/boşluk/durum tasarımına alındı. İlgili Infrastructure derlemesi **0 hata** ile geçti; web typecheck yerel `node_modules` bulunmadığından `NOT_RUN`.

**2026-08-09 sipariş tam liste yükleme:** Sipariş ekranı artık API'nin ilk `limit=200` sayfasıyla sınırlı kalmaz; cursor devam sayfalarını güvenli biçimde birleştirerek yerel sipariş havuzunun tamamını sayar, filtreler ve sayfalar. Eksik/tekrarlanan cursor hatasında sonuç uydurmak yerine hata gösterir. Bu yalnız yerel, salt-okunur listeleme davranışıdır; Trendyol'a dış yazma yapmaz. Hızlı doğrulama politikası gereği ayrıntılı test `NOT_RUN` durumundadır.

**2026-08-09 hızlı doğrulama politikası:** Günlük geliştirmede UI/metin/CSS değişiklikleri için ekran önizlemesi; işlevsel değişiklikler için en küçük ilgili build veya hedefli test kullanılır. Tam solution/web/entegrasyon doğrulaması yalnız kullanıcı açıkça istediğinde, faz kapanışında, release/tag veya production deploy öncesinde çalıştırılır. Çalıştırılmayan ayrıntılı kontroller `NOT_RUN` sayılır; güvenlik, migration, mali işlem ve dış API yazmalarında hedefli doğrulama zorunluluğu korunur.

**2026-08-09 v10.20 güvenlik, ürün/desi ve sipariş görünümü:** Sistem Ayarları ekranı mevcut MFA ve server-side session API'lerine bağlandı. Authenticator etkinleştirme parola yeniden doğrulaması, QR/kod onayı ve tek seferlik kurtarma kodlarını; oturum alanı tekil ve toplu bağlantı sonlandırmayı sunar. Yeni ürün formundaki ayrı kategori araması kaldırıldı, temel alanlar hizalandı, “Barkod” adı kullanıldı ve desi doğrudan varsayılan `1` olarak veya ölçülerden hesaplanarak kaydedilir. Nullable `ProductVariant.Desi` migration'ı mevcut veriyi değiştirmez. Sipariş ürün adedi rozeti sağ üste taşındı. 19/19 web testi, TypeScript, production web build, .NET build ve Docker gerektirmeyen 142 .NET testi geçti; Docker bulunmadığı için PostgreSQL Testcontainers/full-stack suite yerelde `BLOCKED_ENVIRONMENT`, tam CI beklenir.

**2026-08-09 v10.19 sipariş operasyonu:** Ayrı sipariş detay sayfası kaldırıldı ve eski `/orders/:id` adresi sipariş listesine yönlendirildi. Satırdaki “Fatura Oluştur” işlemi API müşteri/adres/ürün/vergi verileriyle gönderim öncesi taslak özeti açar; devam yalnız yerel idempotent taslağı oluşturur, gerçek E-Faturam gönderimi mevcut parola + açık onay kapısında kalır. Toplu işlem menüsü istenen dört operasyonu uygunluk bilgileriyle sunar; desteklenmeyen dış yazmalar sahte başarı üretmez. Sekmeler, ürün görsel hizası, SVG navigasyon ikonları ve orta konumlu menü daraltma kontrolü yenilendi. 18/18 web testi, TypeScript, web production build ve .NET solution build geçti; tam CI ve canlı kabul beklenir.

**2026-08-09 v10.18 eşleştirme merkezi:** Kategori ve marka eşleştirmeleri aynı iki sekmeli çalışma alanında hizalandı. Aktif kapsam dışındaki pazaryeri seçenekleri kaldırıldı; bağlantı, panel kaydı ve Trendyol referansı araması seçim kutularının içine taşındı. Kategori görünümünde panel kategorisi, isteğe bağlı üst kategoriyle aynı ekrandan oluşturulup otomatik seçilebilir. Bu yerel katalog yazımı Trendyol'a dış işlem başlatmaz. Hedefli 9/9 web testi ve TypeScript kontrolü geçti; tam CI ve canlı görsel kabul beklenir.

**2026-08-09 v10.17 menü ve termin görünürlüğü:** Sol menü masaüstünde kalıcı tercihle 82 px ikon görünümüne daraltılıp tekrar açılabilir; mobil alt menü değişmez. Sipariş detayındaki yinelenen büyük özet kartı kaldırıldı. Resmî Trendyol paket sözleşmesi mikro ihracatta `agreedDeliveryDate`/`estimatedDeliveryEndDate` alanlarını tanımlar; ancak canlı Stage `1238693012` ve `1238692471` snapshotlarında desteklenen beş termin alanının tamamı `null` olduğundan tarih üretilmez ve “Trendyol termin bilgisi göndermedi” açıklaması gösterilir. Hedefli 6/6 web testi geçti; tam CI ve Stage gerçek tarih read-back'i beklenir.

**2026-08-09 v10.16.1 mikro ihracat etiketi yerleşimi:** Sipariş bilgileri sütunundaki yinelenen “Mikro ihracat” rozeti kaldırıldı. Aynı kısa etiket yalnız fatura sütununda gösterilir; uzun “Mikro İhracat Faturası” metni kullanılmaz. Mavi mikro ihracat satır çizgisi ve veri algılama kuralları değişmez.

**2026-08-09 v10.16 sipariş menüsü ve mikro ihracat geri uyumluluğu:** Sipariş satırındaki fatura/işlem menüleri görünür alana göre aşağı veya yukarı açılır; açık satır taşma sırasında üst katmanda tutulur. Ürün metinleri dikey ortalanır ve “Fatura Bilgileri” adı “Fatura & Adres Bilgileri” olarak netleştirilir. Resmî `micro`/`3pByTrendyol` alanları bulunmayan tarihsel Stage kayıtlarında yalnız tam PM3–Arvato partner kimliği dar bir geri uyumluluk sinyali olarak kabul edilir; sipariş numarası sabitlenmez. .NET build, hedefli 5/5 backend testi, TypeScript ve 4/4 sipariş Vitest testi geçti; tam CI/Stage yeniden doğrulaması beklenir.

**2026-08-09 v10.15.1 CI işlem kaydı:** v10.15 fatura belgesi endpointindeki import sırası repository formatter'ıyla hizalandı. Davranış, veri şeması ve dış yazma kapıları değişmedi. Yerelde formatter doğrulaması geçti; tam GitHub release doğrulaması yeniden bekleniyor.

**2026-08-09 v10.15 sipariş filtre çalışma alanı ve güvenli manuel fatura belgesi:** Sipariş ekranındaki sık kullanılan arama/platform/durum filtreleri kompakt yüzeyde, tarih/kargo/fatura/listeleme ve sayfa boyutu ise açılır gelişmiş filtrelerde toplandı; Uygula/Temizle davranışı ile mevcut client-side filtreleme korunur. Sipariş satırları küçük görsel boşlukla ayrıldı. `Fatura Yükle`, önce yerel taslağı açan sonra fatura detayında PDF/JPEG/PNG belgesini özel arşive alan gerçek akışa bağlandı. Sunucu MIME bilgisini dosya imzasıyla doğrular, SHA-256 ile tekrar kaydı engeller ve audit oluşturur. Bu işlem E‑Faturam submit veya Trendyol fatura link teslimi başlatmaz. .NET build, API yüzey testi, TypeScript, 16/16 Vitest ve production web build geçti; Stage mali/operasyon kabulü hâlâ gereklidir.

**2026-08-09 v10.14 tekil sipariş salt-okunur yenileme:** Aktif ve `ORDER_READ=SUPPORTED` Trendyol bağlantısında, operatör sipariş numarasını girerek yalnız o paketin resmî API’den yeniden okunmasını kuyruğa alabilir. Bu denetim dış platforma yazmaz; eski snapshotlardaki `3pByTrendyol`, fatura ve termin bilgisinin kontrollü read-back'i için kullanılır. Hedefli Vitest (6/6), TypeScript, production web build ve dokümantasyon transaction doğrulaması geçti. Canlı Stage bağlantısında `1238693012` için iş güvenli biçimde `REMOTE_ORDER_NOT_FOUND` ile bloklandı; yerel veri uydurulmadı.

**2026-08-09 v10.13 Trendyol İhracat Partnerliği tanımı:** Resmî `getShipmentPackages` belgesine göre `3pByTrendyol=true` olduğunda API `micro=false` döner. Bu yine ihracat siparişidir; snapshot `3pByTrendyol` alanını saklar ve operasyon ekranında mikro ihracat etiketiyle gösterir. .NET build, 49 adapter contract testi ve dokümantasyon transaction doğrulaması geçti; production read-back yeniden çalıştırılmalıdır.

**2026-08-09 v10.12 Stream cursor geçerlilik kurtarması:** Stage, daha önce saklanmış `nextCursor` değerini tarih filtresi olmadan da HTTP 400 ile reddetti. Salt-okunur eşitleme yalnız bu tanımlı durumda imleci bir kez temizler ve son kalıcı watermark’tan yeniden başlar; diğer validation/uzak hata durumları denetlenebilir biçimde başarısız kalır. .NET build, 49 adapter contract testi ve dokümantasyon transaction doğrulaması geçti. Production salt-okunur job başarıyla tamamlandı; 204 siparişte termin/fatura/mikro alanları saklandı. Bu Stage örneğinde `micro=true` kayıt yoktur; etiket yalnız gerçek değer geldiğinde gösterilir.

**2026-08-09 v10.11 CI tarayıcı kararlılığı:** v10.10 kaynak doğrulamasında, soğuk Vite modül derlemesi giriş formunun 30 saniyelik varsayılan Playwright süresini aştı. Tam uygulama kanıtı ağ sakinliğini ve 60 saniyelik sınırı bekler; işlevsel sipariş kodu değişmez. Node syntax, TypeScript ve dokümantasyon transaction doğrulaması geçti; tam CI ve production read-back yeniden çalıştırılmalıdır.

**2026-08-08 v10.10 cursor uyumluluğu:** Production read-back, ilk 204 tam paket güncellemesinden sonra Stream `nextCursor` ile tarih filtresinin tekrar gönderilmesinde Trendyol Stage’in HTTP 400 döndürdüğünü gösterdi. İlk sayfa tarih aralığıyla, devam sayfaları yalnız cursor ile istenir. .NET build, 49 adapter contract testi ve dokümantasyon transaction doğrulaması geçti; production read-back yeniden çalıştırılmalıdır.

**2026-08-08 v10.9 eşitleme dayanıklılığı:** Tam paket zenginleştirmesinde, akış sayfasından sonra pazar yeri tarafında kaldırılan tek bir sipariş bulunamazsa salt-okunur eşitleme artık tüm işi kesmez; akıştaki mevcut snapshot idempotent olarak korunur. Diğer uzak hata sınıfları retry/audit için işi başarısız yapmaya devam eder. .NET build, 49 adapter contract testi ve dokümantasyon transaction doğrulaması geçti; production read-back yeniden çalıştırılmalıdır.

**2026-08-08 v10.8 veri düzeltmesi:** Üretim teşhisinde akış sipariş özetlerinin `micro`, `agreedDeliveryDate`, `invoiceStatus` ve `invoiceLink` alanlarını taşımadığı görüldü. Senkronizasyon her akış satırını resmî tam paket okumasıyla zenginleştirir; mikro ihracat, teslim termin/gün gecikmesi ve Trendyol fatura kontrol durumu/linki bu veriden gösterilir. Yerel .NET build, 49 adapter contract testi, TypeScript, 14/14 Vitest ve production frontend build geçti. Gerçek kaynak read-back ve Stage kabulü devam eder.

**2026-08-08 v10.7 UI notu:** Sipariş numarası turuncu paket ve kopyalama denetimiyle bağlantısız gösterilir. Trendyol paket snapshot’ındaki iç içe teslimat/fatura adresleri ile iletişim/mükellef alanları doğrudan sipariş API’sinden fatura penceresine taşınır. Teslim terminini aşan açık siparişler gün sayılı gecikme uyarısı verir; mikro ihracat hem satırda hem fatura alanında mavi etiketlidir. Kesilmiş faturanın menüsü fatura bilgisi ve güvenli, pasif silme denetimine geçer. .NET build, TypeScript, 14/14 Vitest ve production frontend build geçti; Stage ve dış yazma blokajları değişmez.

**Son güncelleme:** 2026-08-06

**Ana plan sürümü:** 10.0

**2026-08-10 hızlı geliştirme politikası v8.3:** Günlük UI ve olağan işlevsel değişikliklerde otomatik test/build kaldırıldı; kısa önizleme veya manuel smoke kontrol varsayılandır. Hedefli kontrol yalnız somut sorun/derleme riski ya da güvenlik, migration, mali işlem, dosya yükleme, veri kaybı ve dış yazma gibi riskli alanlarda çalıştırılır. Tam doğrulama kullanıcı talebi veya release/production kapısına bırakılır.

**Aktif ürün kapsamı:** `TRENDYOL Türkiye CORE` + `TRENDYOL_EFATURAM`

**Genel durum:** `F3_CORE_CODE_COMPLETE_VALIDATION_PENDING / F4_CODE_COMPLETE_VALIDATION_PENDING / PRODUCTION_BLOCKED`

**2026-08-08 doğrulama notu:** Önceki tam kaynak doğrulama iş akışı `PASS`. Release hattında .NET/PostgreSQL/full-stack, format, 13 Vitest, Playwright 3/3 ve production build kapıları doğrulandı; Playwright sunucu ayarındaki Node tipi bağımlılığı kaldırıldı. Yeniden release doğrulaması bekleniyor; gerçek Stage kabulü bulunmadığından production blokajı değişmemiştir.

**2026-08-08 production hotfix notu:** v9 paketi canlıya alındıktan sonraki tam sayfa yenilemesinde dashboard, bir sayfalı API cevabındaki eksik/null `items` alanında çalışma zamanı hatası verdi. Dashboard ve ürün çalışma alanı boş koleksiyona güvenli düşecek şekilde düzeltildi; production yeniden doğrulaması bekliyor.

**2026-08-08 v10.4 UI notu:** Beyaz kurumsal navigasyon korunarak sipariş ekranı referans operasyon tablosuna taşındı. Liste API’si ürün, SKU, barkod, varyant, görsel, alıcı, adres, tutar ve paket bilgilerini toplu döndürür; tablo kolonları gerçek verilerle doldurulur ve ayrıntı açılımı korunur. Backend build, 14 Vitest, TypeScript ve production build geçti; Stage kabulü ve dış yazma blokajları değişmedi.

## 2026-08-08 — v10.6 sipariş operasyon etkileşimleri

- Sipariş satırındaki fatura işlemleri; fatura oluşturma geçidi, fatura bilgileri penceresi ve yükleme için henüz etkin olmayan güvenli kontrol ile ayrıştırıldı. Kesilmiş faturalar ilgili fatura kaydına yönlendirilir.
- İşlemler menüsüne işleme alma bağlantısı, görsel kargo firması değişim penceresi ve dış yazma başlatmayan pasif iptal seçeneği eklendi.
- İptal ve mikro ihracat siparişleri sırasıyla kırmızı ve mavi sol vurguyla ayrılır; iptal, taşıma ve teslim zamanları operasyonel metinlerle gösterilir. Alıcı adı ve fatura alanı hizaları sıkılaştırıldı.
- Sipariş satırları, doğrudan varyant bağının yanında yerel katalogdaki aynı stok kodunu da kullanarak ürün görseli/model/seçenek eşleştirmesi yapar. Katalogda eşleşen görsel yoksa yer tutucu bilinçli olarak korunur.
- Yerel doğrulama: .NET locked restore + solution build, TypeScript, 14/14 Vitest ve frontend production build geçti. Frontend Node sürümü `24.15.0`, hedef `24.18.1` olduğundan exact-toolchain kanıtı değildir; Stage ve dış yazma kapıları değişmedi.

## Faz özeti

| Faz | Durum | Açıklama |
| --- | --- | --- |
| F0 | `BASELINE_COMPLETE` | Mimari, bağımlılık, risk ve doğrulama temeli hazır. |
| F1 | `HARDENING_CODED_DYNAMIC_REVALIDATION_REQUIRED` | Güvenlik/job/deployment sertleştirmesi kodlandı; exact runtime doğrulaması bekler. |
| F2 | `V9_CATALOG_WORKSPACE_CODED_STATIC_VERIFIED` | Yerel katalog çekirdeğine birleşik kategori/özellik/değer eşleme, kategori özellikleri, varyant matrisi ve güvenli yayın hazırlığı eklendi; exact runtime doğrulaması bekler. |
| F3 | `CORE_CODE_COMPLETE_STATIC_VERIFIED` | Trendyol Türkiye CORE bağlantı, referans, mapping, Product V2 create/update/archive/approval, birleşik fiyat-stok, Order V2/stream, paket aksiyonu, takip numarası, ortak etiket, iade aksiyonu/evidence/read-back, webhook ve invoice-link sınırı kodlandı. Dynamic, Docker ve Stage kabulü bekler. |
| F4 | `CODE_COMPLETE_STATIC_VERIFIED_DYNAMIC_AND_STAGE_REVALIDATION_REQUIRED` | Doğrudan API_USER auth, token kaynaklı mali kapsam, otomatik E-Fatura/E-Arşiv seçimi, provider-managed hesap, numeric status, PDF, E-Arşiv cancel, Trendyol link teslimi ve sade operatör UI kodlandı; exact runtime ve Stage kabulü bekler. |
| F5 | `PLANNED_BLOCKED_BY_F3_F4_AND_REVALIDATION` | Production pilot, F3/F4 dış kabul kapıları geçmeden başlamaz. |
| F6+ | `PLANNED` | Stabilizasyon, adapter registry ve sonraki platformlar. |

## 2026-08-08 — v10.5 sipariş operasyon görünümü

- Sipariş listesi yatay taşmayı önleyen sıkı kolon ölçüleriyle yenilendi; açılır hızlı ayrıntı satırı kaldırıldı.
- Arama; sipariş, paket, takip, müşteri, stok/model kodu, barkod ve ürün bilgilerini kapsar. Tarih aralığı, kargo firması, fatura durumu, sayfa boyutu ve seçimli toplu işlem yüzeyi eklendi.
- Satırda renk/beden gibi varyant alanları ayrı gösterilir; model kodu açık adıyla, kargo durumu ise operasyonel kargo bilgisiyle gösterilir. Fatura durumları okunur kırmızı/yeşil etiketlere dönüştürüldü.
- Frontend doğrulaması: TypeScript, Vitest 14/14 ve production build geçti. Node sürümü proje hedefinden `24.18.1` yerine `24.15.0` olduğundan bu sonuç exact-toolchain kanıtı değildir.

## Bu teslimde kapanan v9 katalog işleri

- Panel yaprak kategorisi ile Trendyol yaprak kategorisi eşleme ekranı referans görünüme göre yenilendi.
- Kategoriye panel özellik başlığı bağlama, yeni özellik/seçenek oluşturma ve zorunlu/özel değer kuralları eklendi.
- Trendyol kategori özellikleri ve değerleri, kategori kapsamında panel özellik/değerleriyle eşlenir; zorunlu eşleme ilerlemesi gösterilir.
- Toplu mapping okuma endpoint'iyle kart başına N+1 API çağrıları kaldırıldı.
- Ürün oluşturma ekranında kategori özellikleri doğrudan yüklenir; seçilen varyant özelliklerinin Kartezyen kombinasyonları oluşturulur.
- Varyant satırlarında SKU, barkod, stok, satış/liste fiyatı düzenlenir; toplu değer uygulama ve tekrar kontrolü vardır.
- Ürün ve varyant özellikleri doğru kapsamda kalıcılaştırılır; ACTIVE Trendyol kanalı seçildiğinde listing profile, teklifler ve güvenli yayın job'u hazırlanır.
- Kaynak kabul kontrolleri statik olarak geçti; exact Node/.NET, PostgreSQL ve Stage kabulü bekler.

## Bu teslimde kapanan Trendyol işleri

- Product Update: unapproved veya approved content/variant/delivery fazlarına ayrılan durable state machine.
- Archive/unarchive: batch submit, poll ve publication read-back.
- Fiyat-stok: tek batch payload, offer/projection version kanıtı ve stale-result koruması.
- Sipariş: `/v2/orders` tekil read, stream cursor ve 2026 alan adları.
- Shipment: capability listesine bağlı paket aksiyonları, takip numarası, read-back ve ortak etiket.
- Return: `claimId` sözleşmesi, exact claim read-back, approve/reject, private evidence ve karar uzlaştırması.
- Capability: mevcut bağlantılara yeni capability backfill ve Owner/Administrator evidence kaydı.
- UI: ürün yayın yönetimi, fiyat-stok sync, shipment detail/actions/label, return decision/evidence ve capability evidence formu.
- Product read: 100 kayıt üst sınırı, ilk 10.000 kayıtta page, sonrasında `nextPageToken` cursor geçişi.

## Bu teslimde kapanan Trendyol E-Faturam işleri

- E-Faturam bağlantısı yalnız doğrudan API_USER `signIn` modeline sadeleştirildi; panel yalnız e-posta/parolayı şifreli saklar.
- `companyId` ve `userId` sign-in tokenından otomatik okunur; mali hesap, kullanıcı kimliği ve seri/prefix ayarları panel/API/persistence yüzeyinden çıkarıldı. Eski bağlantı `SettingsJson` kayıtları veri migrasyonuyla yalnız `ExternalWritesEnabled` kalacak biçimde temizlenir.
- Belge türü `commercial + eInvoiceAvailable` snapshotına göre otomatik `TEMELFATURA` veya `EARSIVFATURA` seçilir; ayrı mükellef sorgusu ve kullanıcı senaryo seçimi kaldırıldı.
- Ödeme ve taşıyıcı kullanıcı ayarları kaldırıldı. E-Arşiv internet satışı için gereken teknik alanlar Trendyol siparişi ve resmî kargo sağlayıcı kataloğundan otomatik üretilir; bilinmeyen taşıyıcı fail-closed bloklanır.
- Canonical payload, ASCII VKN/TCKN, iptal edilmiş satır filtresi ve kuruş denklemleri korunur.
- Durable submit, numeric status reconciliation, private PDF, E-Arşiv cancellation ve Trendyol package invoice-link teslimi korunur.
- Resmî E-Faturam evidence hostu ve mali write capability'lerde Stage fixture SHA-256 zorunluluğu korunur.
- Giden E-Fatura status yolu tahmin edilmeden deployment ayarında fail-closed bırakıldı.

## Kalan zorunlu doğrulamalar

1. Exact .NET `10.0.302`, Node `24.18.1`, npm `11.12.1` ve PostgreSQL/Docker ortamında locked restore, build, test, format, Vitest, Playwright ve Compose smoke.
2. Trendyol Stage'de tarihli fixture checksum ile capability evidence; açık onaylı create/update/archive/fiyat-stok/paket/etiket/iade yazma senaryoları.
3. Duplicate, timeout, rate-limit, partial batch, visibility delay, stale payload ve read-back uyuşmazlığı testleri.
4. Invoice-link için resmî terminal query kanıtlanırsa reconciliation; kanıtlanmazsa onaylı manuel teyit prosedürü.
5. F4 exact runtime/Stage mali E2E, backup/restore ve production pilotu.

## 2026-08-11 — E-Faturam Stage canary hazırlığı

- E-Arşiv submit/status/PDF kabulü için ayrı durable canary eklendi. İş yalnız sabitlenmiş `TRENDYOL_EFATURAM` `STAGE` test hesabındaki gönderilmemiş, mali doğrulaması geçmiş `Ready` E-Arşiv taslağını kabul eder. Taze Stage Test Order'ın sıfır tutarlı mali fixture üretemediği gerçek doğrulamayla görüldü; mali hesap kuralları gevşetilmedi.
- Canary, normal `ExternalWrites`, bağlantı dış-yazma ve otomatik-fatura anahtarlarını açmaz; normal submit/iptal/delivery yolları değişmeden fail-closed kalır.
- Paneldeki `Stage mali canary çalıştır` eylemi yalnız bu Stage hesabı ve uygun taslakta görünür; kullanıcı onayıyla parola/açık-onay istemez. ETag/idempotency ve sabit test hesabı denetimleri kalır. Normal mali işlem yolları parola + açık-onay kapısından geçmeye devam eder.
- Başarı ancak gerçek submit → status → private PDF zincirinden sonra ilgili üç capability’yi kanıtla yükseltir. İptal ve Trendyol invoice-link delivery bu canary kapsamı dışındadır.

## Production blockerları

- Exact backend ve frontend dinamik suite sonucu yok.
- Docker/Compose ve gerçek PostgreSQL Testcontainers sonucu yok.
- Trendyol Stage credential, kontrollü barkod/SKU/claim/package ve açık safe-write onayı yok.
- Capability satırları gerçek evidence olmadan `SUPPORTED` yapılamaz; global ve connection write switch kapalı kalır.
- LUXE/uluslararası storefront kapsam dışıdır.
- F4 kod kapsamı tamamlandı; exact runtime/Stage mali E2E ve off-host restore kanıtı tamamlanmamıştır.

**2026-08-10 siparis liste sunumu v10.30:** Siparis ekrani ilk acilista `Yeni` durumunu gosterir. Urun miktar rozeti gorselin sag ust kosesine sabitlendi; mikro ihracat fatura rozetindeki bilgi ikonu kaldirildi. Degisiklik salt-okunur liste sunumudur; detayli test `NOT_RUN`, hizli hedefli web kontrolu uygulanacaktir.

**2026-08-10 CI port izolasyonu v10.30-r3:** Full-stack tarayici kaniti sabit Vite portu yerine bos localhost portu kullanir. Iki yayin denemesinde giris ekraninin bulunamamasina yol acan paralel port cakismasi dar kapsamli olarak giderildi.

**2026-08-10 full-stack kanit oturumu v10.30-r4:** CI tarayici kanitindaki giris formu locator zaman asimi kaldirildi. Test ayni auth endpointiyle oturum acip asil full-stack siparis akisini tarayicida surdurur; production giris davranisi degismez.

**2026-08-10 full-stack kanit CSRF bootstrap v10.30-r5:** CI kaniti auth isteginden once CSRF cookie/token ciftini uygulamanin resmi endpointinden alir. Degisiklik yalniz test bootstrap akisini production guvenlik sozlesmesiyle hizalar.

**2026-08-10 full-stack kanit atomik CSRF v10.30-r6:** Oturum kaniti gectikten sonra siparis esitleme POST istegi, dashboard istemcisinin eszamanli token yenilemesinden ayrilarak ayni request context icinde tek CSRF ciftini kullanir. Production guvenlik davranisi degismez.

**2026-08-10 siparis adet rozeti gorsel cercevesi v10.31:** Canli geometri kontrolunde rozet yatayda dogru olsa da dikeyde satir koordinatina bagliydi. Gorsel ve rozet ayni konumlu kapsayiciya alinarak adet rozeti gorsel cercevesinin sag ustune sabitlendi.

**2026-08-10 guvenlik, eslestirme ve siparis gorseli v10.32:** Sonlandirilmis oturumlar icin kullanici kapsamli tekil/toplu silme eklendi; aktif ve mevcut oturum silinemez. Kullanilmayan faturalama ayarlari menuden kaldirildi. Trendyol kategori sunumundaki TDG onekleri temizlendi, ozellik karti secimi ve kategori ozellik snapshot esitleme kurtarma akisi eklendi. Siparis gorselleri buyutulebilir ve fatura bekleme ikonu kaldirildi. Hedefli API build, TypeScript ve Vitest PASS; Stage/canli kabul `NOT_RUN`.

**2026-08-10 v10.32-r2 belge kapisi duzeltmesi:** Uygulama kabugu E2E beklentisi kaldirilan Faturalama menusuyle esitlendi. Ilk r1 etiketi dokumantasyon transaction kapisinda durdu; canliya cikmadi. Yeni exact release sonucu bekleniyor.
# 2026-08-11 hızlı geliştirme notu

Ürün oluşturma ve Trendyol eşleştirme çalışma alanındaki kullanıcı geri bildirimleri hedefli olarak uygulanmaktadır. Tam regresyon çalıştırılmadı (`NOT_RUN`); F3/F4 kapanış durumu ve dış yazma kapıları değişmedi.

## 2026-08-11 — v10.35 hedefli eşleştirme, iade ve fatura taslağı iyileştirmesi

- Eşleştirme ekranında kategoriye bağlı özellik değerleri kalıcı gösterilir; seçilen özelliğe üstteki alan üzerinden değer eklenir ve tekil değerler pasifleştirilebilir.
- İade ekranına yalnız okuma amaçlı eşitleme işini kuyruğa alan kullanıcı aksiyonu eklendi. Dış platforma yazma yapılmaz.
- Eski paket eşitlemelerinde allocation kaydı bulunmadığında, sipariş-paket sahipliği doğrulandıktan sonra pozitif sipariş satırları fatura taslağına geri kazanımlı biçimde eklenir.
- Hedefli API build ve web typecheck `PASS`; Stage/canlı kabul ile provider işlemleri `NOT_RUN` durumundadır.
## 2026-08-11 — E-Faturam Stage customerSignIn düzeltmesi

Gerçek Stage mali canary, eski `signIn` tokenından mali kapsam çıkarımı denemesinde create çağrısından önce `EFATURAM_TOKEN_SCOPE_MISSING` ile durdu; dış fatura, ETTN, belge veya delivery oluşmadı. Resmî sağlayıcı sözleşmesi incelendi: partner `signIn` tokenı ile müşteri `customerSignIn` çağrısı yapılır; `companyId`, `userId` ve mali access token yalnız bu müşteri yanıtından alınır. Kod buna göre düzeltildi. Partner/müşteri Stage credential'ları ve müşteri VKN/TCKN'si yalnız şifreli credential kaydında tutulacak, arayüzde tekrar gösterilmeyecektir. Önceki tek-credential kaydı sözleşmeye yeterli değildir; doğru Stage test hesapları kaydedilip bağlantı testi geçmeden canary yeniden çalıştırılmaz. Normal ve production submit/iptal/delivery kapıları değişmedi. Capability satırları `UNKNOWN` kalır.
