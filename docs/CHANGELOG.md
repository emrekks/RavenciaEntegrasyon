# Ravencia MarketplaceHub Değişiklik Kaydı

## 2026-08-17 - E-Faturam JWT işlem privilege teşhisi

- Başarılı `signIn` sonrasında create endpointinin verdiği açıklamasız `401`, token değeri saklanmadan JWT içindeki firma kapsamına ait `INVOICE_CREATE` privilege'ı açısından sınıflandırılır.
- Privilege açıkça yoksa hata `EFATURAM_INVOICE_CREATE_PRIVILEGE_MISSING` olarak ayrılır; claim yoksa veya biçim bilinmiyorsa mevcut fail-closed `EFATURAM_ACCESS_TOKEN_REJECTED` korunur. Bu yalnız teşhistir, token claim'i yetkilendirme bypass'ı olarak kullanılmaz.
- Resmî bireysel API_USER gateway, `signIn` ve create sözleşmesiyle mevcut endpoint/header kullanımı yeniden karşılaştırıldı. Hedefli adapter contract `38/38` PASS; release/deploy ve Stage tekrar `NOT_RUN`.

## 2026-08-17 - E-Faturam korumalı endpoint teşhisinin güvenli derinleştirilmesi

- `v10.65` Stage replay düzeltmesi immutable olarak yayımlandı ve sağlıklı deploy edildi. Bağlantı testi yeniden başarılı olurken mali doğrulaması geçen eski taslağın güncel kodla tekrarı korumalı create endpointinde `EFATURAM_ACCESS_TOKEN_REJECTED / 401` aldı; bu sonuç hesap giriş hatası değildir.
- Provider `x-request-id` vermediğinde RFC problem gövdesinden yalnız `/problem/...` biçimindeki `instance` referansı, query değerleri ve serbest metinler atılarak güvenli teşhis alanına alınır. Ham cevap, token, parola ve müşteri verisi saklanmaz.
- Kökten başlayan problem yollarının Linux'ta `file:` URI sayılmasına bağlı platform farkı giderildi; aynı allowlist Windows ve Linux'ta eşit çalışır.
- Hedefli adapter contract testleri `37/37` PASS. Source CI `32033455000`, immutable publish `32033891198`, doğrulanmış `20260817T131624Z` backup ve v10.66 deploy tamamlandı. Üçüncü gerçek denemede provider yine `401` verdi ve bu kez de `x-request-id`/problem `instance` göndermedi; dış referans oluşmadı. Endpoint/header sözleşmesi, Stage mali doğrulama ve Production güvenlik zinciri değiştirilmedi.

## 2026-08-17 - E-Faturam Stage replay aksiyonunun açılması

- Güncel panel bağlantı testi başarılı oldu; hesap/credential sorunu olmadığı doğrulandı.
- Dış referans oluşmadan `EFATURAM_TOKEN_SCOPE_MISSING` ile manuel incelemeye düşen sabit Stage taslağı için backend'de zaten bulunan güvenli canary replay endpointi artık fatura detayının `allowedActions` yanıtında görünür.
- Kapsam yalnız sabitlenmiş E-Faturam Stage hesabı, E-Arşiv ve dış referansı olmayan güvenli taslak/replay ile sınırlıdır. Production ve başka bağlantılar negatif testle kapalı kalır.
- Hedefli backend `2/2`, F4 web `5/5` PASS. Source CI `32031018630`, immutable publish `32031420205`, doğrulanmış `20260817T125012Z` backup ve sağlıklı deploy tamamlandı. Replay zinciri girişten geçti; bir eksik mali fixture input validation'da, mali doğrulaması geçen eski taslak ise provider korumalı endpointindeki `401` ile güvenli biçimde durdu.

## 2026-08-17 - v10.64 immutable deployment ve iade bütünlüğü

- `c18ca94` source CI `32026532850` ile; `release-2026-08-17-v10.64` immutable image publish `32026931375` ile başarıyla tamamlandı.
- App `sha256:ecb225ea20a3f6759e25e5038939d6a600b6943c7eeadb3f1a33e94aba5d370f`, edge `sha256:8ab2e7e46a08a6ada482893559a14a657c6d54e0c0ac59455d5ad86c47ac4d43` digestleri checksum ve PostgreSQL restore-list doğrulanmış `20260817T115450Z` backup sonrasında deploy edildi.
- API, Worker, Caddy ve PostgreSQL healthy; dış readiness `Healthy` ve frontend asset smoke geçti. PII açığa çıkarmayan aggregate kontrolde 26/26 iadenin bağlı siparişi, müşteri/adres snapshot'ı, ad alanı kaynağı ve iade satırı bulundu; son üç return-sync işi `SUCCEEDED`.

## 2026-08-17 - Dashboard güvenlik e2e beklentisi

- Uygulama kabuğu Playwright senaryosu, Stage manuel operasyonları ile Production dış-yazma güvenlik zincirini doğru ayıran güncel dashboard metnine eşitlendi.
- Hedefli Playwright paketi `3/3 PASS` verdi. Bu yalnız test beklentisi uyumudur; provider çağrısı, Stage sınırı ve Production güvenlik kapıları değişmedi.

## 2026-08-17 - Stage fatura taslağı işlem rehberi hizalaması

- Fatura taslağı penceresi, aktif E-Faturam bağlantısının ortamını doğru açıklar: `STAGE` için gerçek gönderimin ek parola veya açık onay olmadan fatura ekranından başlatılabileceğini; `PRODUCTION` için parola ve açık onayın korunduğunu gösterir.
- Bu yalnız kullanıcı rehberi düzeltmesidir. Stage'de connection/credential, mali input doğrulaması, idempotency ve provider yanıt kontrolü; Production'da mevcut dış-yazma güvenlik zinciri korunur.
- Dashboard ve toplu etiket bildirimi de aynı ayrımı kullanır: Stage manuel işlemleri capability kanıtı veya açık onayla yanlış yönlendirilmez; Production güvenlik zinciri açıkça korunur.
- F3 kabul planındaki eski “açık onaylı Stage write” ifadesi de güncel runtime politikasıyla eşitlendi.
- API yüzey testi, Stage’deki parola/açık-onay kısa devresinin Production açık-onay ve yeniden-doğrulama kapılarından önce kalmasını regresyona karşı korur.
- Gönderi detayındaki teknik Stage etiket testi Production paketlerinden kaldırıldı; normal etiket işlemi korunurken test yüzeyi yalnız Stage bağlantısında sade biçimde görünür.

## 2026-08-13 - v10.63 immutable deployment

- `c555c28` source CI `31650747089` ve `release-2026-08-13-v10.63` immutable publish `31650999736` geçti.
- App `sha256:551eaa9cb4adab5bdab2e2662edf4aeec3d7cbd7ee99ef8b64d51b9e6e128e8e`, edge `sha256:4bfeee466b9c9f13a2bd4468e312658e716c4946ea130cf9840a2501b4b403f5` digestleri checksum/`pg_restore --list` doğrulanmış rollback backup sonrasında deploy edildi. API, Worker, Caddy, PostgreSQL health; dış readiness ve frontend asset smoke geçti.

## 2026-08-13 - Eksik termin tarihi gösterim düzeltmesi

- Sağlayıcının eksik termin alanını .NET varsayılan tarihiyle (`0001-01-01`) göndermesi halinde sipariş listesi artık uydurma gecikme günü hesaplamaz. Panel, resmi termin bulunmadığını açıkça gösterir.
- Taze Stage salt-okunur sipariş eşitlemesi ilk denemede başarılı oldu; panel 189 yerel sipariş kaydını gösterdi. Bu işlem dış platforma yazmaz.

## 2026-08-13 - v10.62 immutable deployment

- `88b9ca2` source CI `31649708460` ve `release-2026-08-13-v10.62` immutable publish `31649967620` geçti. `20260812T231433Z` rollback backup seti checksum ve `pg_restore --list` ile doğrulandı.
- App `sha256:920be4db528bf20dc5785b5b8514425cc0f6193a67e8706737e4a6ced660ed43`, edge `sha256:550b772f2cb10e52f120b42bf7937fa4b56bb77a75b2cb66660fc9630a0724b6` immutable digestleriyle deploy edildi. API, Worker, Caddy ve PostgreSQL healthy; dış readiness `200` ve frontend asset smoke geçti.

## 2026-08-13 - Test altyapısı SSH bağımlılığı güvenlik düzeltmesi

- Testcontainers'ın geçişli `SSH.NET 2025.1.0` bağımlılığı, güvenlik advisory'sindeki düzeltme sürümü `2026.0.0` ile merkezi olarak pinlendi. CI'ın vulnerability-as-error denetimi korunur; güvenlik uyarısı bastırılmadı.
- Kilitli restore, çözüm build/test ve formatter doğrulaması yeniden çalıştırılacaktır. Production runtime paketi ve dış yazma güvenlikleri etkilenmez.
- E-Faturam contract testindeki yeni `using` satırları merkezi formatter sırasına getirildi; değişiklik yalnız test kaynak biçimlendirmesidir.

## 2026-08-13 - E-Faturam taze token teşhis düzeltmesi

- Başarılı doğrudan `signIn` sonrasında korumalı endpointten gelen `401`, artık giriş hatası gibi değil `EFATURAM_ACCESS_TOKEN_REJECTED` olarak kaydedilir. Bu, sağlayıcıdaki hesap API yetkisinin doğrulanması gerektiğini doğru biçimde gösterir; istek tekrar gönderilmez.
- Resmî bireysel API_USER sözleşmesindeki Stage gateway ve `x-access-token` kullanımı doğrulandı. Adapter contract `62/62` ve Infrastructure build geçti; Stage/Production güvenlik zinciri değişmedi.

## 2026-08-13 - v10.61 iade operasyon nedeni deployment

- `cb56898` source CI ve `release-2026-08-12-v10.61` immutable publish geçtikten sonra checksum/`pg_restore --list` doğrulanmış rollback backup ile v10.61 deploy edildi. Migration, API/Worker/Caddy/PostgreSQL health, dış readiness ve frontend asset smoke geçti.
- İade detayında `Created`/`REQUESTED` sağlayıcı durumu artık onay/ret işleminin henüz desteklenmediğini doğru biçimde bildirir; hiçbir Stage veya Production write kapısı azaltılmadı ve sağlayıcıya karar isteği gönderilmedi.

## 2026-08-13 - Stage iade eşitleme ve doğru operasyon nedeni

- Panelden çalıştırılan Stage `TRENDYOL_RETURN_SYNC` ilk denemede başarılı oldu; iade listesi 26 güncel kaydı alıcı ve ürün satırlarıyla gösterdi.
- `Created`/`REQUESTED` sağlayıcı durumunda karar aksiyonu açılmadığında panel artık yanlışlıkla Production write ayarlarını suçlamaz; sağlayıcının mevcut durumunun onay/ret kabul etmediğini bildirir. Stage ve Production güvenlik zincirleri değişmedi.

## 2026-08-13 - Kapanış regresyon doğrulaması

- Güncel solution build `0` hata/uyarı ile geçti; Domain `32/32`, Application `66/66`, adapter contract `61/61`, frontend typecheck ve Vitest `21/21` PASS.
- Uygulama kaynaklı kapanabilir işler tamamlandı. Dış Trendyol ürün onayı `BLOCKED_PROVIDER_APPROVAL`, E-Faturam fatura API kapsamı `BLOCKED_PROVIDER_API_SCOPE`, uygun Stage iade/etiket fixture'ı `BLOCKED_REMOTE_FIXTURE` olarak korunur; bunlar capability veya güvenlik kapısı bypass edilerek başarı sayılmadı.
- Yerel Docker CLI/engine yokluğuna rağmen Linux source CI `31644381310`, tam solution testlerini (Docker-backed Testcontainers dahil), formatter'ı, locked web build'i ve Playwright'ı başarıyla tamamladı.

## 2026-08-12 - v10.60 immutable deployment

- `163327f` source CI `31620941782` ve `release-2026-08-12-v10.60` immutable publish `31621351060` geçti. Checksum/doğrulanmış `20260812T171539Z-v10.60` rollback seti sonrasında digest-pinned app/edge imajları deploy edildi.
- Migration `0`; API, Worker, Caddy ve PostgreSQL healthy; dış readiness `200` ve deploy script frontend asset smoke `PASS`.
- Mevcut Stage ürün approval read-back işi deployment boyunca korundu ve `PRODUCT_APPROVAL_PENDING` olarak yeniden denendi. Trendyol terminal onayı gelene kadar bu kabul `BLOCKED_PROVIDER_APPROVAL`; E-Faturam mali E2E de sağlayıcı invoice API kapsamı beklediği için `BLOCKED_PROVIDER_API_SCOPE` kalır.

## 2026-08-12 - Trendyol ürün approval deadline hizalaması

- Yedi günlük approval deadline'ı scheduler ayrıntısından bağımsızlaştırıldı. Worker'ın mevcut jitter'lı geri çekilmesiyle `200` deneme zaten deadline'dan önce tükenmez; yeni reconciliation işleri nominal beş dakikalık pencereyi de kapsayan `2017` limitini kullanır. Deadline sonunda mevcut `PRODUCT_APPROVAL_DEADLINE_EXPIRED / MANUAL_REVIEW` akışı çalışır.
- Product create yazması, idempotency/effect fence, provider read-back, Stage endpoint/credential boundary ve Production external-write kontrolleri değişmedi.
- Infrastructure build `0` hata/uyarı ile geçti. Ubuntu'daki ayrı Docker build contextinde Release API/Worker publish ve ağsız Testcontainers test-projesi derlemesi de `PASS` verdi. Gerçek Testcontainers yürütmesi production Docker socket'i/host network erişimi gerektirdiği için `NOT_RUN_SCOPED_RUNNER_REQUIRED`; immutable CI/release/deploy henüz `NOT_RUN`.

## 2026-08-12 - v10.59 doğrudan E-Faturam hesap Stage kabulü

- Doğrudan hesap auth düzeltmesi source CI ve immutable release kapılarından geçti; checksum ve `pg_restore --list` doğrulanmış backup sonrasında v10.59 app/edge digestleri deploy edildi. Migration `0`, servis health ve dış readiness `200`.
- Normal panel connection testi mevcut şifreli bireysel credential ile ilk denemede geçti. Panel artık partner/test müşteri alanı istemiyor.
- Eksik alıcı VKN/TCKN'li yeni fixture provider çağrısından önce fail-closed durdu. Gerçek mali kimlik formatı bulunan ayrı fixture yerel doğrulamayı geçti ancak korumalı E-Arşiv create endpointi direct tokenı `401` ile reddetti; tekrar gönderilmedi.
- Mevcut Stage hesabına sağlayıcı tarafında fatura API kapsamı tanımlanana kadar mali E2E `BLOCKED_PROVIDER_API_SCOPE`. Authorization, alıcı kimliği doğrulaması ve Production güvenlikleri azaltılmadı.

## 2026-08-12 - E-Faturam doğrudan hesap auth düzeltmesi

- Aktif tek işletme `API_USER` kapsamına aykırı partner + test müşteri credential zorunluluğu kaldırıldı. Panel ve API artık yalnız E-Faturam hesap e-posta/parolasını alır; secret değerleri yeniden gösterilmez.
- Adapter tek `signIn` access tokenından firma/kullanıcı kapsamını okur. Eksik veya çoklu kapsam fail-closed kalır; Stage/Production endpoint ve credential sınırı ile Production dış-yazma kontrolleri korunur.
- Gate–route–service–job envanteri kaydedildi. Solution build, 38 E-Faturam contract testi, 7 ilgili web testi ve TypeScript kontrolü geçti; release/deploy ve gerçek Stage mali E2E `NOT_RUN`.

## 2026-08-12 - v10.58 immutable deployment

- Stage/Production yayın yardım metni source CI `31611581747` ve immutable publish `31612027079` kapılarından geçti. Checksumlı `20260812T152558Z` rollback seti sonrasında v10.58 app/edge digestleri deploy edildi.
- Migration `0`; API, Worker, Caddy ve PostgreSQL healthy; dış readiness `200`. Normal panel smoke kontrolünde Stage bağlantı/auth/input/idempotency zinciri ve Production master + connection switch ayrımı doğru gösterildi.
- Stage ürün create başarı kaydı ve approval reconciliation job'u deployment boyunca korundu; provider approval hâlâ `PENDING` ve otomatik readback aktif.

## 2026-08-12 - v10.57 Stage ürün create kabulü

- Kategori özelliği zorunluluk kontrolü source CI ve immutable release kapılarından geçti; checksumlı rollback seti sonrasında v10.57 app/edge digestleri deploy edildi. Migration `0`, bütün servis health kontrolleri ve dış readiness `200` geçti.
- Normal panelde opsiyonel `Kol Boyu`, güncel Bluz category snapshot/mapping, ayrı Stage test markası, zorunlu özellik/değer mapping'leri, geçerli EAN-13, stok/fiyat ve HTTPS medya ile güvenli ürün fixture'ı oluşturuldu.
- Trendyol create batch correlation `f9c945309efe4bf9acdd13dcd246b2aa` `SUCCEEDED`; approval reconciliation provider'da halen `PRODUCT_APPROVAL_PENDING` ve otomatik readback yapıyor. Duplicate create gönderilmedi.
- Ürün formunun güvenli yayın yardım metni Stage'de capability/evidence veya write switch'i blocker gibi göstermeyecek; Production master + connection switch zorunluluğunu açıkça koruyacak şekilde düzeltildi. Değişiklik v10.58 ile deploy edilip normal panelde doğrulandı.

## 2026-08-12 - Yerel kategori özelliği zorunluluk kontrolü

- Kategori eşleme ekranındaki özellik kartlarına `Zorunlu / İsteğe bağlı` kontrolü eklendi; değişiklik optimistic concurrency kullanan mevcut kategori requirement endpointinden kaydedilir.
- Sağlayıcıda opsiyonel bir özellik artık yanlış yerel zorunluluk nedeniyle ürün oluşturma ve manuel Stage yayınını bloke etmek zorunda değildir. Provider input, mapping, idempotency, active connection/auth ve Stage/Production credential boundary kontrolleri korunur.
- Hedefli web testi 2/2 geçti. Release/deploy ve gerçek Trendyol Stage ürün create/readback kabulü `NOT_RUN`.

## 2026-08-12 - İzole backup restore drill

- Backup checksum, dump restore, private archive safe-path/extract, schema/migration/tenant aggregate, migration, API readiness ve Worker heartbeat adımlarını production kaynaklarına bağlanmayan timestamp-scope internal Docker ortamında çalıştıran `restore-drill.sh` eklendi.
- Restore kopyasında otomatik scheduler ve bekleyen işler Worker smoke öncesinde devre dışı bırakılır; external writes kapalı ve ağ egress'sizdir. Cleanup yalnız scriptin oluşturduğu adlandırılmış container/volume/network kaynaklarını kaldırır.
- `20260812T132906Z` backup seti v10.56 immutable app ile hedef Ubuntu'da 14 saniyede geçti; cleanup ve production health tekrar doğrulandı. CI artık tüm deployment/backup shell scriptlerinde `bash -n` çalıştırır.
- Commit `d9d841c` source CI `31604226847` tamamen geçti. Hedef repository committed sürüme güncellendikten sonra tatbikat 13 saniyede yeniden geçti; geçici kaynak listeleri boş ve production readiness sağlıklı kaldı.

## 2026-08-12 - Trendyol kategori ağacı scope doğrulaması

- Kategori referans ağacındaki child satırlar artık kök request scope'u ile yanlış karşılaştırılmaz. Her non-root parent kimliğinin aynı normalize cevap kümesinde bulunması ve satırın kendisine parent olmaması zorunludur.
- Brand, category-attribute ve attribute-value exact scope kontrolleri; duplicate/orphan/boş zorunlu alan fail-closed davranışı korunur.
- v10.55 canlı kabulünde connection, sipariş ve marka senkronları ilk denemede başarılı oldu; kategori tanı kodu hatalı yerel gate'i ayırdı. Hiyerarşi düzeltmesi source CI `31601096910` ve immutable publish `31601437442` sonrasında v10.56 olarak checksumlı backup ile deploy edildi. Migration/health/readiness geçti; panel category sync `6d8661a3467d4814a57820beaea0a7b9` ilk denemede başarılı oldu.

## 2026-08-12 - Trendyol sipariş stream fallback ve referans tanı kodları

- Salt-okunur sipariş stream'inde gelen kayıt, opsiyonel exact hydration isteği `NotFound` veya `Validation/400` döndürürse kaybedilmez; stream kaydı saklanır ve iş başına tek audit üretilir. Diğer provider hataları mevcut fail-closed/retry davranışını korur.
- Zorunlu kategori/marka referansı boş, aşırı büyük, geçersiz cursor/sözleşme veya duplicate kimlik içerdiğinde genel job reddi yerine operasyonel olarak ayırt edilebilir hata kodları döner. Başarısız sonuç mevcut referans snapshot'ını değiştirmez.
- Infrastructure build geçti. Yeni PostgreSQL uçtan uca testleri yerel Docker motoru kapalı olduğundan `BLOCKED_DOCKER`; merkezi CI bekleniyor. Production kontrollerinde değişiklik yapılmadı.

## 2026-08-12 - v10.54 E-Faturam yetki zinciri immutable deploy

- Partner → müşteri provider yetki zinciri için source CI ve immutable image yayını başarıyla tamamlandı. Ubuntu hedefte taze backup sonrası app `sha256:1bd4399e09e896be38c0eb9db512e00bb2e4314c2d58352448fe592f6245321c` ve edge `sha256:fa08b7dbc96001967a3e4e00142d7b40bcf8d5249de95beac892c54846200cfe` çalışıyor; API/Worker/Caddy/PostgreSQL health ve dış readiness `200`.
- Paneldeki normal Stage bağlantı testi, eski tekil credential'ı provider'a göndermeden `EFATURAM_CONFIGURATION_UNAVAILABLE` ile güvenli biçimde reddetti; kullanıcı yalnız partner + Stage test müşteri credential yenileme gereksinimini görüyor. Gerçek submit/status/PDF kabulü partner ve test müşteri API hesabı ile VKN/TCKN sağlanana kadar başlatılmayacak.

## 2026-08-12 - E-Faturam partner/müşteri API yetki zinciri

- Gerçek Stage E-Arşiv submitindeki 401, tekil kullanıcı oturumunun fatura API token'ı olmadığını doğruladı. Adapter ve panel, sağlayıcının zorunlu partner `signIn` → `customerSignIn` modeline geri hizalandı.
- Partner ve Stage test müşteri credential'ı (e-posta/parola/VKN-TCKN) şifreli rotasyon formunda tutulur; customer token ve firma/kullanıcı kapsamı yalnız provider yanıtından okunur. Stage manuel gate sade kalır, Production korumaları değişmez.
- Credential formunun provider modeliyle eşleşmesini doğrulayan web regresyonu güncellendi; Vitest 21/21 ve TypeScript typecheck geçti.

## 2026-08-12 - VERIFIED Stage E-Faturam manuel işlemleri

- Başarılı connection testinden sonra `VERIFIED` kalan Stage E-Faturam bağlantısı, F4 fatura gate'inde yanlışlıkla pasif sayılıyordu. `VERIFIED` artık yalnız Stage manuel read/write akışında operasyonel kabul edilir; normal submit, status/reconcile ve E-Arşiv cancel görünür/kuyruğa alınabilir.
- Production `VERIFIED` bağlantı yine fail-closed kalır. Production read/write için `ACTIVE`; write için ayrıca global ve bağlantı dış-yazma switch'leri, yeniden doğrulama, idempotency, input/provider validation ve audit zinciri korunur.

## 2026-08-12 - Gerçek Stage E-Arşiv yetki sonucu

- v10.53'te `VERIFIED` Stage gate düzeltmesi dağıtıldı; hazır E-Arşiv taslağı normal manuel submit yolundan ek parola/onay olmadan bir kez kuyruğa alındı. Sağlayıcı submit endpoint'i `401` döndürdü ve deneme `EFATURAM_AUTHENTICATION_FAILED` olarak audite edildi; duplicate koruması nedeniyle aynı fatura yeniden gönderilmedi.
- Resmî Trendyol E-Faturam pazaryeri rehberi, fatura API'lerinin partner `signIn` ve `customerSignIn` ile alınan müşteri token'ını gerektirdiğini belirtir. Tekil kullanıcı hesabının giriş yapabilmesi provider fatura API yetkisi anlamına gelmez; partner + test müşteri API hesabı olmadan Stage submit/status/PDF/cancel kabulü blokludur. Production güvenlikleri azaltılmadı.

## 2026-08-12 - E-Faturam tekil hesap credential akışı

- Partner/alt müşteri credential sözleşmesi kaldırıldı. E-Faturam tekil hesap e-postası ve parolasıyla doğrudan oturum açar; firma ve kullanıcı kapsamı yalnız sağlayıcı token'ından fail-closed okunur.
- Eski payload dönüştürülmez, secret gösterilmez. Production dış-yazma zinciri değişmedi. Contract testleri 30/30, Infrastructure build, web testleri 7/7 ve TypeScript kontrolü geçti; gerçek Stage smoke `NOT_RUN`.

## 2026-08-12 - E-Faturam Stage token kapsamı

- Gerçek Stage sign-in tokenı yalnız claim adları/tipleri üzerinden incelendi: kullanıcı kapsamı sayısal `sub`, firma kapsamı tek `privs` sayısal anahtarıdır. Token veya credential loglanmadı.
- Direct-account parser bu sağlayıcı sözleşmesini destekleyecek şekilde güncellendi; çoklu firma kapsamı fail-closed bırakıldı. Contract testleri `32/32` ve Infrastructure build geçti. Güncel parser release edilip aynı Stage connection test yeniden çalıştırılacak.

## 2026-08-12 - E-Faturam doğrudan hesap Stage kabulü

- Immutable `release-2026-08-12-v10.52` dağıtımından sonra tekil Stage hesap credential'ı panelin şifreli formuyla yenilendi. Panelden başlatılan `EFATURAM_CONNECTION_TEST` işi `ae2c1681d72240d08d556f6be87777da` ilk denemede `SUCCEEDED`; bağlantı `VERIFIED` ve hata kodu yok.
- Token, parola, e-posta veya firma/kullanıcı değerleri loglanmadı ya da gösterilmedi. Gerçek fatura submit/status/PDF/cancel Stage kabulü uygun test siparişi ile `NOT_RUN` kalır; Production güvenlik zinciri değişmedi.

## 2026-08-12 - v10.50 iade alıcı API fallback deployment

- İade alıcı bilgisi fallback'i, checksum doğrulanmış `20260812T093713Z` backup sonrasında immutable v10.50 app/edge imajlarıyla deploy edildi. API, Worker, Caddy ve PostgreSQL healthy; iç ve dış `/health/ready` yanıtı `200`.
- Canlı aggregate kontrolü, 26 iadenin tamamında bağlı sipariş ile müşteri/adres adı snapshot'ı bulunduğunu doğruladı; hiçbir kişisel veri okunmadı veya kayda yazılmadı.
- Girişli iade tablosu smoke kontrolü oturum olmadığından `NOT_RUN`; Stage iade sync'in önceki başarılı sonucu korunuyor.

## 2026-08-12 - İade alıcı bilgisini API'den tamamlama

- İade liste/detay alıcı adı artık Trendyol sipariş snapshot'ındaki üst seviye ad/soyad placeholder ise API'nin invoice address, o da yoksa shipment address ad/soyadını kullanır. Gerçek müşteri adı önceliğini korur; API anlamlı değer göndermiyorsa isim uydurulmaz.
- E-posta, telefon ve vergi kimliği için de mevcut sipariş adresi fallback'i eklendi. Hedefli backend testi `3/3`, web TypeScript kontrolü `PASS`.

## 2026-08-12 - v10.49 immutable deploy ve canlı Stage smoke

- v10.49 immutable app `sha256:c2698b0666ea3948260c41b450ec774b81a4cf83cb1ac1ccecb227a99b17d7cd` ve edge `sha256:35673e1db13d8f302ffeade17e709c088dd55a5355d2e1a41d895fb7a3a35ad7` digestleri checksumlı backup sonrasında Ubuntu hedefe deploy edildi. Fail-closed compose validation, migration, API/Worker/Caddy health, frontend asset ve `/health/ready` 200 geçti.
- Canlı panel smoke testi Trendyol ve E-Faturam Stage bağlantılarında sade `Stage işlemleri / Hazır` görünümünü doğruladı. E-Faturam eski credential şeması nedeniyle teknik `EFATURAM_CONFIGURATION_UNAVAILABLE` durumunda kalır; secret tahmin edilmedi veya gösterilmedi.

## 2026-08-12 - Fail-closed deploy Compose runtime

- Deploy scripti exact Docker Compose sürümünü kontrol ederken compose/config/pull/up/inspect çağrılarını aynı onaylı root Docker ikilisi üzerinden çalıştıracak şekilde düzeltildi. Kullanıcı ve root eklentileri farklıysa sürüm kapısı artık yanlış ikiliyi seçmez.
- Hedef Ubuntu'da kullanıcı Compose `5.3.1`, root Compose `2.40.2` idi; eski script güvenli olarak fail-closed durdu ve çalışan servisler değiştirilmedi. Yeni immutable release/deploy kabulü bekleniyor.

## 2026-08-12 - Stage operasyon yüzeyi ve taze sipariş kabulü

- Stage bağlantı özeti artık dış yazmayı kapalı veya capability kanıtına bağlı göstermez; manuel denemelerin aktif bağlantı, credential, teknik doğrulama, tekrar koruması ve sağlayıcı yanıt denetimiyle çalıştığını açıkça gösterir. Teknik capability/evidence ayrıntıları normal kullanıcı ekranından kaldırılıp İşlem Takibi/diagnostics yüzeyinde tutulur. Production write switch korumaları değişmedi.
- Panelden oluşturulan taze Stage siparişi `1507428594` ve buna bağlı scoped order sync ilk denemede başarıyla tamamlandı. Paket `92287436` Yurtiçi Kargo Marketplace olduğu için common-label sözleşmesi kapsamına girmez; desteklenmeyen taşıyıcıya `LABEL_WRITE` çağrısı gönderilmedi ve capability elle yükseltilmedi.

## 2026-08-12 - Stage operator real-reason messaging

- Fatura ve ürün yayın yüzeylerindeki capability temelli engel metinleri, Stage manuel çalışma ve Production write switch davranışını doğru ifade edecek şekilde güncellendi.

## 2026-08-12 - Stage operator action visibility

- Gönderi ve iade ekranları Stage manuel aksiyonlarını capability kaydı eksikliği yüzünden gizlemeyi bıraktı. Production dış-yazma switch ve ilgili teknik/mali doğrulamalar değişmedi.

## 2026-08-12 - Manual runtime capability query removal

- F4 fatura read/write ve Trendyol manuel read akışları, karar sonucunu etkilemeyen capability deposu sorgusundan ayrıldı. Stage capability/evidence eksikliği veya bu deponun erişilebilirliği runtime blocker değildir; production dış-yazma switch davranışı değişmedi.

## 2026-08-12 - E-Faturam status endpoint configuration refactor

- Giden e-Fatura durum sorgusunda eksik endpoint yolu capability/evidence runtime kapısı olmaktan çıkarıldı; teknik `EFATURAM_EINVOICE_STATUS_PATH_NOT_CONFIGURED` olarak ayrıştırıldı.
- Adapter Stage veya production ortamında belgelenmemiş bir endpoint tahmin etmez. Geçerli sağlayıcı yolu ve credential olmadan işlem teknik nedenle fail-closed kalır; production yazma sınırları değişmedi.

## 2026-08-12 - Fatura otomatik read-back capability ayrımı

- Fatura submit/kabul/iptal sonrası reconciliation ve PDF read-back işleri capability evidence yokluğunda artık atlanmaz. Bu salt-okunur zincir external write veya `AUTO_*` davranışını değiştirmez; durable dedup korunur.

## 2026-08-12 - Scheduled salt-okunur capability ayrımı

- Scheduler, aktif Trendyol bağlantılarının `ORDERS`, `RETURNS` ve `REFERENCE_DATA` salt-okunur sync işlerini `UNKNOWN` capability nedeniyle artık sessizce atlamaz. Policy interval/jitter, dedup ve mevcut hata/retry davranışı korunur; dış write veya `AUTO_*` kapsamı genişlemez.

## 2026-08-12 - Manuel runtime capability/evidence ayrımı

- Catalog, fiyat-stok, Trendyol shipment/label/return/sync ve E-Faturam fatura yollarında capability/evidence, fixture SHA ve release kaydı normal manuel runtime kapısı olmaktan çıkarıldı. `UNKNOWN` kayıtlar diagnostics ve release kabulü için korunur.
- Production manuel dış yazmada master + connection switch, aktif connection/credential, doğrulama, idempotency, provider response/reconciliation ve audit korunur. Stage manuel akışı switch, fiscal policy, re-auth ve açık onay istemez. Hedefli policy testi 3/3 ve Infrastructure build geçti.
- Repository-geneli formatter, değiştirilmemiş dosyalardaki CRLF→LF `ENDOFLINE` ihlalleri nedeniyle `BLOCKED_REPOSITORY_LINE_ENDINGS` kaldı; geniş mekanik dönüşüm yapılmadı.

## 2026-08-12 - Ortak etiket taşıyıcı uygunluk koruması

- Gerçek Stage Test Order paketinin `Yurtiçi Kargo Marketplace` taşıyıcısı, Trendyol'un yalnız Aras Kargo veya TEX için tanımladığı common-label sözleşmesine uygun değildir. `LABEL_WRITE` capability canary ve normal common-label kuyruğu artık uyumsuz taşıyıcıda uzak sağlayıcı çağrısı yapmadan `COMMON_LABEL_CARRIER_UNSUPPORTED` ile fail-closed sonlanır.
- Bu koruma Stage manuel çalışma kolaylığını veya Production güvenliklerini gevşetmez. Uygun taşıyıcılı gerçek Stage fixture başarılı create/read-back kanıtı üretmeden `LABEL_WRITE` ve `SHIPMENT_WRITE` `UNKNOWN` kalır.
- Taşıyıcı sınıflandırması hedefli sözleşme testinde `7/7` geçti.

## 2026-08-12 - v10.45 E-Faturam Stage renewal UI deployment

- Başarılı source CI ve immutable publish sonrasında credential yenileme görünürlüğü Ubuntu hedefe deploy edildi. Taze backup, migration, API/Worker/Caddy health, frontend asset ve `/health/ready` kontrolleri geçti.
- Canlı E-Faturam Stage ekranı eski credential şeması için `Yenileme gerekli` durumunu ve güvenli yönlendirmeyi gösteriyor. Production dış yazma kapıları veya secret görünürlüğü değişmedi.

## 2026-08-12 - E-Faturam Stage credential yenileme görünürlüğü

- E-Faturam bağlantısında `EFATURAM_CONFIGURATION_UNAVAILABLE` son test kodu varsa UI, mevcut şifreli kaydın kullanılabilir olduğu izlenimini vermez. Stage için gerekli partner ve müşteri credential rotation adımını açıklar; secret veya kişisel veri göstermez.

## 2026-08-12 - E-Faturam Stage yapılandırma tespiti

- Yeni runtime'daki normal `EFATURAM_CONNECTION_TEST`, kayıtlı eski `EMAIL_PASSWORD` payload'ının güncel partner + müşteri oturum sözleşmesi için yeterli olmadığını `EFATURAM_CONFIGURATION_UNAVAILABLE` ile fail-closed gösterdi. Bu durum Stage onay/capability kapısı değildir; şifreli credential, partner ve müşteri kimliğiyle yenilenmelidir. Secret okunmadı veya loglanmadı.

## 2026-08-12 - v10.44 immutable yayın, deploy ve Stage iade kabulü

- Başarılı main source CI sonrasında `release-2026-08-12-v10.44` yalnız immutable app/edge imajlarını yayımladı. Ubuntu hedefte taze geri dönüş yedeği, fail-closed Compose doğrulaması, migration, readiness ve worker health kontrolleri geçti.
- Panelden başlatılan normal salt-okunur `TRENDYOL_RETURN_SYNC` işi `8542af70a19c4464b78273ee54c9fd16` ilk denemede başarılı oldu; İadeler ekranı 25 paketi ve ürün satırlarını gösteriyor. Production dış yazma kapıları değiştirilmedi.

## 2026-08-12 - Stage normal fatura gönderim testi

- Web testi Production fatura gönderimindeki parola/açık-onay koşulunu explicit olarak taşır. Ayrı Stage fixture'ı normal `submit-jobs` endpointinin parola veya açık onay olmadan kullanılabildiğini; ETag ve idempotency başlıklarının korunduğunu doğrular.

## 2026-08-12 - CI policy testi biçimlendirmesi

- Stage runtime policy testindeki yalnız biçimsel satır düzeni repository formatter beklentisine göre düzeltildi. Davranış, güvenlik sınırı ve provider çağrıları değişmedi.

## 2026-08-11 - Stage manuel çalışma yolu

- Ortak runtime politikası Stage-manual, automatic ve Production akışlarını ayırdı. Aktif Stage bağlantısındaki normal manuel read/write, capability/evidence/fixture SHA, connection write switch, fiscal-policy, `AUTO_*`, re-auth ve ek onay kapılarına takılmaz.
- Normal E-Faturam submit/iptal/teslim endpointleri Stage’de parola ve açık onay istemez; Production endpointleri aynı parola + açık onay korumasını sürdürür. Teknik doğrulama, ETag, idempotency, audit ve provider cevabı kontrolleri kaldırılmadı.
- Trendyol read, iade aksiyonu, shipment/label, ürün ve fiyat-stok manuel işleri için Stage istisnası uygulandı; Production capability ve write-switch kapıları korundu. Capability ekranı Stage’de kanıt formunu normal operatör akışından kaldırır.
- Adapter credential yükleyicileri yalnız `STAGE` veya `PRODUCTION` environment değerini kabul eder; Stage/Production HTTPS endpointleri aynı olamaz. Böylece yanlış environment veya endpoint yapılandırması fail-closed kalır.

## 2026-08-11 - Denetlenebilir E-Faturam Stage mali canary

- E-Arşiv için ayrı, durable Stage canary eklendi. Yalnız sabitlenmiş E-Faturam Stage test hesabındaki mali doğrulaması geçmiş gönderilmemiş `Ready` taslağı kabul eder; sıfır tutarlı Test Order mali fixture kabul edilmez.
- Canary gerçek submit → status → private PDF zincirini kanıtlamadan capability yükseltmez; başarılı kanıt yalnız `INVOICE_SUBMIT`, `INVOICE_STATUS_READ` ve `INVOICE_DOCUMENT_READ` için SHA-256/audit kaydıyla saklanır.
- Genel dış-yazma, bağlantı dış-yazma ve otomatik fatura anahtarları açılmadı. Normal submit, iptal ve Trendyol invoice-link delivery yolları değişmedi; iptal/delivery bu canary kapsamı dışındadır.
- Kullanıcı onayıyla canary eylemi panelde yalnız uygun sabitlenmiş Stage taslakta parola/açık-onay istemeden çalışır; ETag/idempotency ve test hesabı sınırı korunur. Normal ve production mali endpointlerindeki parola/açık-onay kapısı değişmedi.

## 2026-08-11 - CI zaman aşımı güvenlik sınırı

- Kaynak doğrulama işi için 45 dakikalık fail-closed zaman sınırı eklendi. Bağımlılık veya tarayıcı kurulumu takılırsa iş belirsiz biçimde devam etmez; concurrency yeni ana dal commitinde eski CI'ı iptal etmeye devam eder.

## 2026-08-11 - Denetlenebilir Stage etiket capability canary

- Kanıtsız `SUPPORTED` işaretleme yerine yalnız Owner/Admin için dar kapsamlı Stage etiket canary akışı eklendi.
- `LABEL_READ`, gerçek ortak etiket read-back'ini; `LABEL_WRITE`, yalnız `ReadyToShip` Stage paketinde create → read-back zincirini çalıştırır.
- Başarılı testte gerçek etiket içeriğinin SHA-256 kanıtı, format kısıtı, resmi kaynak ve audit kaydı capability’ye eklenir. Production ve normal dış-yazma davranışı değişmedi.
- Gerçek Stage read-back canary başarılı oldu ve `LABEL_READ` destekli kaydedildi. `LABEL_WRITE` aynı kaydın uzaktaki `Invoiced` durumu nedeniyle fail-closed kaldı; capability kanıtsız olarak yükseltilmedi.
- Trendyol dokümanındaki tarihsel `Picking` Stage fixture'ı da create isteğinde reddedildi; güncel write kanıtı için resmi Stage Test Order API ile taze fixture oluşturma yolu ayrıca uygulanacaktır.
- Taze fixture, yalnız `STAGE/2738` ve resmî test barkodunda tek denemelik durable job olarak eklendi; normal dış yazma ve production anahtarları bu istisnadan ayrı tutuldu.
- Stage Test Order yanıtındaki sipariş numarası string veya sayı olsa da güvenli sözleşme okumasıyla işlenebilir.
- İlk gerçek Test Order işinde Worker dispatch allow-list'inin yeni job tipini içermediği görüldü. İş dış çağrı yapmadan fail-closed durdu; `TRENDYOL_STAGE_TEST_ORDER` F3 yönlendirmesine eklendi. Capability ve dış-yazma güvenlik kapıları değişmedi.
- Worker düzeltmesinden sonraki ilk gerçek Stage Test Order isteği `REMOTE_SERVER_ERROR` döndürdü. Yalnız dar test fixture'ı, Trendyol'un resmî örnek sözleşmesindeki tam adres alanları ve `9900000000486` test barkoduyla hizalandı; capability yükseltilmedi.
- Resmî fixture ile yeni Stage siparişi ve salt-okunur order-sync başarıyla tamamlandı. Uzak `ReadyToShip` durumunun canonical eşlemesi eklendi; daha önce bu açık yazım fail-closed `ManualReview` üretiyordu. Normal dış yazma ve production davranışı değişmedi.
- Daha önce kaydedilmiş aynı raw kaynak olayındaki yanlış `ManualReview` projeksiyonu, yalnız tanınan canonical duruma idempotent olarak iyileştirilebilir hale getirildi. Bu yol dış çağrı veya yeni history olayı üretmez.
- Sağlayıcının aynı paket/raw durumda olay zamanını değiştirebildiği gerçek Stage eşitlemesiyle görüldü. Dar yerel projeksiyon onarımı event kimliğinden bağımsız, ancak yalnız aynı paket/raw durum ve `ManualReview` kaydıyla sınırlı hale getirildi.
- Boş order-line listeli tekrar yanıtlarındaki erken dönüş, güvenli paket canonical projeksiyon onarımını engelliyordu. Optimizasyon kaldırıldı; idempotency ve miktar kontrolleri yerinde bırakıldı.
- Boş satırlı tekrarlar ayrıca ilk satır-miktar korumasından da dönüyordu. Bu cevaplar yalnız mevcut paketin yerel canonical onarımı için işlenir; satırlı cevaplarda miktar bütünlüğü doğrulaması değişmedi.
- Resmî ortak etiket sözleşmesi create çağrısından önce `Picking` veya `Invoiced` ister. İlk taze `ReadyToShip` denemesi bu nedenle platform tarafından reddedildi. Canary yalnız son auditli `STAGE/2738` Test Order fixture’ında tek geçerli satırla önce resmî `Picking` isteğini, sonra label create/read-back zincirini çalıştıracak şekilde daraltıldı. Bu yol genel veya production dış-yazma anahtarlarını açmaz; gerçek kabul olmadan capability yükseltmez.
- Taze fixture’daki resmî `Picking` çağrısı da uzak platformda fail-closed reddedildi. Teşhis için başarısız Trendyol JSON yanıtından yalnız `code`/`errorCode` içindeki harfli ve güvenli karakterli sağlayıcı kodu alınır; ham gövde, hata mesajı, takip numarası ve credential saklanmaz.

## 2026-08-11 - v10.41 iade ürün satırı eşlemesi

- v10.40 Stage eşitlemesi `SUCCEEDED` oldu ve panelde 23 iade gösterildi; claim satırları yerel sipariş satırına bağlanmadığı için ürün adedi 0 kaldı.
- Gerçek Stage şeması nested `items[].orderLine.id=10524304` ile `claimItems[].orderLineItemId=57322050` değerlerinin farklı kimlikler olduğunu kanıtladı.
- Claim aksiyon kimliği `claimItems[].id` olarak korunurken yerel sipariş satırı eşlemesi parent `orderLine.id` üzerinden yapılır; tarihsel düz cevap geri uyumluluğu korunur.
- CI `#130`, immutable release `#120`, production deployment/readiness ve tam Stage backfill geçti. Panel 25 iade paketini tümünde 1–5 ürünle gösterir; `0 ürün` kalan kayıt yoktur.

## 2026-08-11 - v10.40 Stage iade okuma süre sınırı

- v10.39 Stage capability kabulü `RETURN_READ=SUPPORTED` üretti; ilk tam return-sync sekiz durum çağrısı sırayla çalışırken `REMOTE_TIMEOUT` ile güvenli retry'ye girdi.
- Yalnız Stage 404 fallback'indeki bağımsız salt-okunur durum çağrıları aynı anda başlatılır; production, yazma kapıları ve retry davranışı değişmez.

## 2026-08-11 - v10.39 Trendyol Stage durum bazlı iade okuması

- Gerçek Stage probu, filtresiz getClaims isteğinin `SupplierApiDomainNotFoundException/order.not.found` ile 404 verdiğini; aynı resmî endpoint'in `claimItemStatus=Created` ile 200 ve claim içeriği döndürdüğünü kanıtladı.
- Production canonical tek çağrı korunur. Yalnız Stage filtresiz çağrısı 404 verdiğinde sekiz resmî claim durumu salt-okunur olarak ayrı ayrı sorgulanır; durum bazında 404 boş sonuç sayılır, başarılı claim'ler kimliğe göre tekilleştirilir.
- Trendyol adapter sözleşme testleri `50/50` geçti. Yazma çağrıları, secrets ve production davranışı değiştirilmedi.

## 2026-08-11 - v10.38 Trendyol Türkiye claims Stage fallback

- Güncel r3 paketinin gerçek Stage capability testi, claims GET için `REMOTE_RESOURCE_NOT_FOUND`/HTTP 404 kanıtladı.
- Canonical `storeFrontCode=TR` GET korunarak yalnız claims okumasında 404 sonrası aynı resmî V2 Türkiye endpoint'i başlıksız bir kez denenir; yazma çağrıları ve diğer endpointler değişmedi.

## 2026-08-11 - v10.37-r3 release token bağlamı

- R2 release kapısının `GITHUB_TOKEN: unbound variable` ile imaj buildinden önce durduğu job logundan doğrulandı.
- Yerleşik GitHub tokenı yalnız source-gate adımına aktarıldı; `actions: read` en az izni, exact workflow doğrulaması ve tüm immutable image güvenlik kontrolleri korundu.
- Main source CI `#125` ve immutable release `#116` başarılı tamamlandı; app/edge imajları digest ile yayımlandı. Çalışan sunucuya deployment, bu çalışma ortamında SSH/AWS oturumu bulunmadığı için uygulanmadı.

## 2026-08-11 - v10.37-r2 release kaynak kapısı düzeltmesi

- Immutable release kapısı, Checks API'deki job adını workflow adı sanan hatalı eşleşmeden çıkarıldı.
- Release artık exact `verify.yml` workflow'unda aynı SHA için `main` + `push` + `success` koşullarını GitHub Actions API üzerinden fail-closed doğrular; required status check ve image/digest güvenliği değişmedi.

## 2026-08-11 - v10.37 Trendyol iade claimItems uyumluluğu

- Trendyol getClaims resmî cevabındaki `items[].claimItems[]`, tarihsel düz `items[]` ve doğrudan `claimItems[]` desteği korunarak iade satırı, durum ve neden eşlemesine alındı.
- Resmî satırda miktar bulunmadığında claim item tek ürün satırı kabul edilerek `1` adet saklanır; sıfır miktarlı görünmez iade satırı üretilmez.
- Yeni Stage bağlantı testi başarılı tamamlandı; dağıtılmış eski sürümde `RETURN_READ` hâlâ `UNKNOWN`, güncel paketle Stage yeniden kabulü bekleniyor. Dış yazma kapıları değişmedi.

## 2026-08-11 - Stage iade probe teşhisi

- Başarısız salt-okunur capability proplarının evidence notu artık capability API ve bağlantı ekranında görünür; `RETURN_READ` gibi `UNKNOWN` kayıtların hata kodu saklanmaz.
- Bağlı Trendyol Stage hesabında bağlantı testi başarılı, ancak claims endpointi probu `RETURN_READ=UNKNOWN` bıraktı. Güvenlik kapısı kaldırılmadı veya capability kanıtsız biçimde destekli yapılmadı.

## 2026-08-11 - İade eşitleme ve referans çalışma alanı v10.36

- Yerelde bulunmayan siparişe ait uzak iade claim'i, sipariş salt-okunur exact read ile başarıyla alındıktan sonra ilişkilendirilir; uzak sipariş bulunamazsa sahte/bağlantısız iade kaydı oluşturulmaz ve audit kaydı korunur.
- İadeler ekranı referanstaki durum sekmeleri, müşteri/sipariş/iade kodu/barkod/sebep/tarih filtreleri ve sipariş–alıcı–ürün–kargo–fatura–neden–durum sütunlarıyla yenilendi.
- External write, onay/ret kapıları ve capability gereksinimleri değişmedi. Infrastructure build, Trendyol adapter sözleşme testi `50/50`, iade operasyonları web testi `4/4` ve web typecheck `PASS`; Stage return-read kabulü `NOT_RUN`.

## 2026-08-11 - CI ve immutable release tetikleyici sadeleştirmesi

- `Verify source changes` push tetikleyicisi yalnız `main` ile sınırlandı; aynı commit'e eklenen `release-*` etiketi artık kaynak testlerini ikinci kez çalıştırmaz.
- Locked .NET/web doğrulaması, format ve Playwright E2E kontrolü `main` kapısında tek seferde korunur.
- Immutable image yayın akışı, `main` üzerinde bulunan ve başarılı `Verify source changes` check'i olan commit olmadan registry'ye giriş/publish yapmaz; SHA etiketli app/edge imajları, provenance/SBOM ve digest doğrulaması korunur.
- Normal CI concurrency yeni `main` commit'i geldiğinde eski koşuyu iptal etmeye devam eder; release publish koşuları iptal edilmez.
- Release repository guard testi, artık release'te tekrar .NET/web test komutu beklemek yerine zorunlu `main` soy-ağacı ve başarılı kaynak check kapısını denetler; ilk GitHub koşusunun bu eski beklentiden kaynaklanan başarısızlığı için yeniden doğrulama beklenir.

## 2026-08-11 - v10.35-r3 yayın kapısı kaydı

- Fatura işlemleri menüsünün görünen başlığı ve erişilebilir adı aynı tutuldu: `Fatura işlemleri`.
- Hedefli `TrendyolOperationsPages.test.tsx` kontrolü `4/4 PASS`.
- Tam suite, Stage ve canlı kabul `NOT_RUN`; bu kayıt yalnızca yayın kapısındaki test hizalamasını belgeler.

## 2026-08-11 - Hedefli eşleştirme, iade ve fatura taslağı iyileştirmesi v10.35

- Kategori özellik kartları seçeneklerini kalıcı olarak gösterir; seçilen özelliğe değer ekleme ve tekil değeri pasifleştirme akışı sadeleştirildi.
- Eşleştirme çalışma alanındaki gereksiz başlıklar, durum etiketleri ve iç içe görsel çerçeveler azaltıldı.
- İade listesine yalnız okuma eşitleme işini kuyruğa alan kullanıcı aksiyonu eklendi.
- Allocation kaydı eksik eski paketlerde, doğrulanmış sipariş satırlarıyla fatura taslağı geri kazanılır; provider submit kapıları değişmedi.
- Hedefli API build ve web typecheck `PASS`; tam suite, Stage ve canlı kabul `NOT_RUN`.

## 2026-08-11 - Ürün çalışma alanı ve referans veri sağlamlaştırması v10.34

- Yeni ürün ekranında açıklama alanı HTML araçları ve güvenli ön izleme ile zenginleştirildi; dosyadan ürün görseli yükleme, varsayılan 50 sipariş ve stok/desi yerleşimi güncellendi.
- Değersiz oluşturulan metin özelliklerine ilk seçenek eklendiğinde özellik seçim tipine geçirilerek seçili özelliğe değer ekleme akışı onarıldı.
- Trendyol kategori zorunluluk bayraklarının alternatif gösterimleri ve atanan kargo sağlayıcı/takip alanlarının belgeli geri uyumlu adları okunur hale getirildi.
- Marka aramasında sorgulu sonuç sınırı genişletildi. Web ve hedefli Infrastructure derlemeleri `PASS`; tam suite, Stage ve canlı kabul `NOT_RUN`.

## 2026-08-10 - Eşleştirme ve manuel fatura yükleme v10.33

- Kategori özellik eşlemesinde hedef panel kategorisi açıkça seçilir; Trendyol teknik `[A-TDG]`/`[TDG]` önekleri yalnız sunumda temizlenir.
- Özellik oluşturma ile seçili özelliğe değer ekleme ayrı butonlara ayrıldı; yinelenen seçim alanı ve ön izleme kaldırıldı.
- Marka eşleştirmesi kategoriyle aynı platform odaklı düzene alındı; yerel marka ekleme ve kaldırılabilir seçim baloncukları eklendi.
- Sipariş satırındaki Fatura Yükle işlemi PDF/JPEG/JPG/PNG dosyasını özel depolamadaki mevcut manuel belge endpointine gönderir; provider submit veya pazaryeri dış yazması başlatmaz.
- Hızlı kontrol: TypeScript PASS, hedefli F3 Vitest 7/7 PASS. Tam suite, Stage ve canlı kabul `NOT_RUN`.

## 2026-08-10 - Hızlı geliştirme doğrulama politikası v8.3

- Günlük UI ve olağan işlevsel geliştirmelerde otomatik test/build zorunluluğu kaldırıldı; kısa önizleme veya manuel smoke kontrol varsayılan oldu.
- Hedefli doğrulama yalnız somut hata/derleme riski ile güvenlik, migration, mali işlem, dosya yükleme, veri kaybı ve dış yazma alanlarında korunur.
- Tam doğrulama kullanıcı talebi veya release/production kapısında mevcut CI hattına bırakıldı.

## 2026-08-10 - Guvenlik, eslestirme ve siparis gorseli v10.32

- Sonlandirilmis oturum kayitlari kullanici bazinda tek tek veya toplu silinebilir; aktif ve mevcut oturumlar silme endpointlerinden korunur.
- Kullanilmayan faturalama ayarlari menuden kaldirildi ve eski adres guvenli bicimde sistem ayarlarina yonlenir.
- Trendyol kategori yollarindaki `[TDG]` sunum onekleri temizlendi; yerel ozellik kartlari tiklanarak secilir, secilen ozellige deger eklenebilir ve eksik kategori ozellik snapshot'i ayni ekrandan esitlenebilir.
- Siparis urun gorselleri buyutulebilir; fatura bekleme rozetindeki unlem ikonu kaldirildi.
- Uygulama kabugu E2E beklentisi, kaldirilan Faturalama menusuyle ayni kapsama getirildi.
- Hizli dogrulama: API hedefli build PASS, web TypeScript PASS, Vitest 19/19 PASS. Stage/canli kabul ve tam release hatti `NOT_RUN`.

## 2026-08-10 - Siparis adet rozeti gorsel cercevesi v10.31

- Urun miktar rozeti satir koordinatindan ayrilarak dogrudan urun gorseli kapsayicisinin sag ust kosesine baglandi.
- Varsayilan `Yeni` filtresi ve ikonsuz mikro ihracat fatura etiketi korunur.

## 2026-08-10 - Full-stack kanit atomik CSRF v10.30-r6

- CI siparis esitleme kaniti, dashboard yuklenirken olusan eszamanli CSRF yenileme yarisindan ayrildi; istek tek request context ve tek token ile gonderilir.
- Production guvenlik kurallari degismedi; exact release dogrulamasi yeniden calistirilacaktir.

## 2026-08-10 - Full-stack kanit CSRF bootstrap v10.30-r5

- CI tarayici kaniti, oturum acma isteginden once uygulamanin CSRF anahtarini alip ayni guvenlik akisiyla giris yapar; production davranisi degismez.
- Hizli kontrol: E2E betigi soz dizimi ve repository diff kontrolu gecti; exact release dogrulamasi yeniden calistirilacaktir.

## 2026-08-10 - Siparis gorsel CSP ve kompakt tablo v10.29

- Panel guvenlik politikasinda yalniz resmi Trendyol gorsel kaynagi `https://cdn.dsmcdn.com` icin `img-src` izni eklendi; diger dis kaynaklar kapali kalir.
- Fatura rozetleri dar kolonda tek satira sigacak bicimde kompaktlastirildi ve tablo basligi ilk siparise bitisik hale getirildi.

## 2026-08-10 - Trendyol dogrudan urun satiri uyumlulugu v10.27

- Onayli urun okumasinda hem eski `variants[]` yapisi hem de canli API'nin barkod ve gorseli dogrudan `content[]` satirinda verdigi yapi desteklenir.
- Siparis zenginlestirmesi, bulunan barkodun HTTPS urun gorselini artik bos saymadan kaynak snapshot'a aktarir.

## 2026-08-10 - Sipariş kaynak görseli ve durum hücreleri v10.26

- Trendyol sipariş satırının resmi kaynak snapshot'ı saklandı; renk/beden/model siparişten, görsel aynı barkodun salt-okunur onaylı ürün snapshot'ından alınarak yerel katalog eşleşmesinden bağımsız hale getirildi.
- Nullable migration mevcut sipariş verisini korur; eski kayıtlar deployment sonrası salt-okunur sipariş eşitlemesiyle zenginleşir.
- Fatura hücresi referans görseline yaklaştırıldı, kargodaki gereksiz İşlemler menüsü kaldırıldı ve tablo başlığı sabitlendi.
- Doğrulama: solution build, Docker gerektirmeyen testler, TypeScript, 19/19 web testi ve production web build geçti; Docker/Testcontainers yerelde `BLOCKED_ENVIRONMENT`.

## 2026-08-10 - Eşleştirme yayın doğrulaması v10.25

- Kategori kapsamlı özellik/değer eşleştirmesinin uçtan uca testi, gerçek kayıt gövdelerindeki `scopeExternalId` ile hizalandı.
- Hızlı doğrulama: Playwright 3/3 geçti; yeni immutable image release hattı bu commit için tekrar çalıştırılacaktır.

## 2026-08-10 - Eşleştirme merkezi v10.24

- Kategori eşleştirme deneyimi hesap seçimi yerine Trendyol platform kapsamına alındı; yerel kategoriler tek seviyede eklenir, baloncuklardan seçilir veya arşivlenir.
- Mevcut bir özelliğe seçenek değeri ekleme ve seçili özelliğin anlık özetini gösterme akışı düzeltildi.
- Kategoriye bağlı özellik ve değer eşlemelerinde kapsam kimliği korunarak kayıtların görünür kalması sağlandı.
- Hızlı doğrulama: TypeScript, 19/19 Vitest ve production web derlemesi geçti; exact GitHub toolchain release hattı yeniden çalıştırılacak.

## 2026-08-10 - Sipariş görseli ve operasyon satırı v10.22

- Sipariş ürün görsellerinde barkod eşleştirmesi ve ürün ana görseline geri düşme eklendi.
- Varyant seçenekleri için katalogdaki renk/beden imzası sipariş satırına taşındı.
- Mikro ihracat fatura rozeti, kargoda aksiyon alanı ve sipariş filtre yüzeyi operasyon referanslarına göre sadeleştirildi.
- Hızlı doğrulama: `MarketplaceHub.Infrastructure` derlemesi geçti; web typecheck yerel paketler olmadığı için `NOT_RUN`.

## 2026-08-09 - Sipariş tam liste yükleme

- Sipariş ekranının yalnız ilk 200 API kaydını sayma hatası giderildi. Cursor ile gelen bütün yerel sipariş sayfaları birleştirilir; sekmeler, filtreler ve sayfalama tam yerel havuz üzerinden çalışır.
- Bu değişiklik salt-okunur panel listelemesidir; Trendyol'a yazma yapmaz. Ayrıntılı test, hızlı doğrulama politikası gereği `NOT_RUN` durumundadır.

## 2026-08-09 - Günlük hızlı doğrulama politikası

- Günlük UI/metin/CSS değişikliklerinde tam solution ve tam web testleri otomatik koşul olmaktan çıkarıldı; ekran önizlemesi ve gerektiğinde en küçük ilgili build/hedefli test uygulanır.
- Güvenlik, migration, mali işlem ve dış API yazmalarında ilgili hedefli doğrulama korunur. Tam doğrulama kullanıcı talebi, faz kapanışı, release/tag veya production deploy öncesinde yürütülür.
- Çalıştırılmayan ayrıntılı kontroller `NOT_RUN` olarak kalır ve başarılı kabul edilmez.

## 2026-08-09 - v10.20 güvenlik, ürün/desi ve sipariş görünümü

- Güvenlik ekranına mevcut API'lerle çalışan Authenticator etkinleştirme akışı, QR/kod onayı, tek gösterimli kurtarma kodları ve diğer oturumları tekil/toplu sonlandırma kontrolleri eklendi.
- Yeni ürün ekranında kategori arama alanı kaldırıldı, temel ürün alanları hizalandı ve “Tek ürün barkodu” etiketi “Barkod” olarak sadeleştirildi.
- Desi doğrudan girildiğinde varsayılan `1` olur; ölçü hesabı açılırsa ağırlık ve en/boy/yükseklik alanları görünür ve desi hesaplanır. Değer geriye uyumlu nullable migration ile varyantta saklanır.
- Sipariş ürün adedi rozeti kartın sağ üst köşesine taşındı.
- Yerel doğrulama: 19/19 Vitest, TypeScript, Vite production build, .NET solution build ve Docker gerektirmeyen 142 .NET testi PASS. Docker/Testcontainers suite yerel ortamda `BLOCKED_ENVIRONMENT`; full CI beklenir.

## 2026-08-09 - v10.19 sipariş fatura ön izlemesi ve operasyon menüsü

- Ayrı sipariş detay ekranı kaldırıldı; eski detay URL'leri sipariş listesine güvenli biçimde döner.
- “Fatura Oluştur” müşteri, adres, satır, KDV ve toplamları API'den alan bir taslak özeti açar; gerçek provider gönderimi parola ve açık onay kapısında kalır.
- Toplu işlemler menüsüne İşleme Al, Kargo Firmasını Değiştir, Toplu Fatura Kes ve Kargo Stickerlarını Yazdır seçenekleri eklendi; capability doğrulanmayan dış işlem başarı gibi gösterilmez.
- Sipariş sekmeleri “İşleme Alınanlar” ve “İptal” olarak sadeleştirildi; ürün görselleri ortalandı, sidebar SVG ikonları ve orta kenar daraltma kontrolü yenilendi.
- Yerel doğrulama: 18/18 Vitest, TypeScript, Vite production build ve .NET solution build PASS. Full CI/canlı kabul beklenir.

## 2026-08-09 - v10.18 kategori ve marka eşleştirme merkezi

- Eşleştirme ekranı “Kategori” ve “Marka” olmak üzere iki tutarlı sekmeye ayrıldı; kapsam dışı pazaryeri düğmeleri kaldırıldı.
- Panel ve Trendyol tarafları karşılıklı kartlarda hizalandı. Bağlantı, kategori ve marka aramaları klavye destekli seçim kutularının içine alındı.
- Kategori sekmesine kategori adı ve isteğe bağlı üst kategori alanlarıyla yerel panel kategorisi oluşturma eklendi; yeni kayıt otomatik seçilir ve Trendyol'a dış yazma yapılmaz.

## 2026-08-09 - v10.17 kapanabilir menü ve termin veri şeffaflığı

- Masaüstü yan menü kullanıcı tercihini saklayarak ikon görünümüne daraltılabilir ve tekrar genişletilebilir; mobil navigasyon korunur.
- Sipariş detay sayfasındaki büyük, tekrarlayan sipariş özet kartı kaldırıldı; müşteri, adres, ürün ve paket bölümleri korunur.
- Mikro ihracat termin alanı boşsa “Termin zamanı bekleniyor” yerine “Trendyol termin bilgisi göndermedi” yazılır. Resmî tarih geldiğinde kalan süre/gecikme hesabı değişmeden çalışır; sahte tarih üretilmez.

## 2026-08-09 - v10.16.1 mikro ihracat etiketi yerleşimi

- “Mikro ihracat” rozeti sipariş bilgileri sütunundan kaldırılıp fatura sütununa taşındı; uzun “Mikro İhracat Faturası” metni kaldırıldı.
- Mikro ihracat satırının mavi çizgisi ve algılama davranışı korundu.

## 2026-08-09 - v10.16 sipariş menüsü ve mikro ihracat görünümü

- Sipariş satırı menüleri görünür pencere boşluğuna göre aşağı/yukarı açılır; açık satır komşu siparişlerin altında kalmaz.
- Ürün bilgi metinleri dikey ortalandı ve “Fatura Bilgileri” adı “Fatura & Adres Bilgileri” olarak güncellendi.
- Resmî mikro ihracat alanlarını taşımayan eski Stage snapshotları için yalnız PM3–Arvato ihracat partneri kimliğine dayalı dar geri uyumluluk eklendi; mavi satır ve fatura etiketi bu türetilmiş değeri kullanır.

## 2026-08-09 - v10.15.1 CI biçimlendirme kaydı

- Manuel fatura belgesi endpointinin import sırası repository formatter kurallarıyla hizalandı. Uygulama davranışı, veri şeması ve dış yazma kapıları değişmedi.

## 2026-08-09 - v10.15 sipariş filtreleri ve güvenli fatura belgesi yükleme

- Sipariş filtreleri; geniş arama alanı, platform ve durum seçimleri ile görünür kaldı; listeleme durumu, tarih aralığı, kargo, fatura ve sayfa boyutu “Gelişmiş Filtreler” altında toplandı. Uygula/Temizle kontrolleri ve responsive düzen eklendi.
- Sipariş satırları görsel olarak küçük boşlukla ayrıldı; fatura menüsündeki “Fatura Yükle” artık taslak fatura akışından gerçek güvenli yükleme alanına ulaşır.
- Manuel belge yükleme yalnız PDF/JPEG/PNG imzasını kabul eder, 10 MiB sınırı ve SHA-256 tekrar koruması uygular, private storage ile audit kaydı oluşturur. Bu işlem E‑Faturam'a gönderim veya Trendyol'a fatura linki iletimi başlatmaz.

## 2026-08-09 - v10.14 tekil sipariş salt-okunur yenileme

- Trendyol bağlantı ekranına, yalnız girilen sipariş numarasını resmî API’den tekrar okuyan ve dış platforma yazmayan denetimli yenileme eklendi.
- Canlı Stage read-back'inde `1238693012` bulunamadığından kayıt değiştirilmedi; sonuç denetlenebilir `REMOTE_ORDER_NOT_FOUND` olarak tutuldu.

## 2026-08-09 - v10.13 Trendyol İhracat Partnerliği mikro etiketi

- `3pByTrendyol=true` dönen Trendyol İhracat Partnerliği paketleri, API `micro=false` dönse de operasyon ekranında mikro ihracat olarak ayırt edilir.

## 2026-08-09 - v10.12 Stream cursor geçerlilik kurtarması

- Süresi dolmuş Trendyol Stream imleci HTTP 400 döndürürse sipariş eşitleme, son kalıcı zaman damgasından bir kez güvenli başlangıç yapar; diğer platform hataları gizlenmez.

## 2026-08-09 - v10.11 CI tarayıcı doğrulama kararlılığı

- Tam uygulama tarayıcı kanıtı, soğuk Vite modül derlemesinde giriş ekranının hazır olmasını güvenli 60 saniyelik sınırla bekler.

## 2026-08-08 - v10.10 Stream cursor uyumluluğu

- Trendyol sipariş Stream devam imleci kullanılırken ilk isteğe ait tarih filtreleri yeniden gönderilmez; Stage endpointinin 400 yanıtı engellenir.

## 2026-08-08 - v10.9 sipariş eşitleme dayanıklılığı

- Salt-okunur sipariş eşitlemesinde, akış kaydından sonra tam paket sorgusunda bulunamayan tekil siparişler artık bütün işi bloklamaz; mevcut akış snapshot’ı idempotent olarak kaydedilir.
- Kimlik doğrulama, hız sınırı, uzak sunucu ve sözleşme hataları gizlenmez; retry/audit için işi başarısız yapmaya devam eder.

## 2026-08-08 - v10.8 tam sipariş veri zenginleştirme

- Sipariş akış özetlerinin mikro ihracat, termin ve fatura kontrol verisini taşımadığı production teşhisiyle doğrulandı.
- Her akış siparişi, kaydedilmeden önce resmî tam paket sorgusuyla zenginleştirilir; `micro`, `agreedDeliveryDate`, `invoiceStatus` ve güvenli `invoiceLink` alanları saklanır.
- Fatura durumu gerçek Trendyol kontrol sonucuna göre gösterilir; fatura linki mevcutsa doğrudan belgeye yönlendirilir.

## 2026-08-08 - v10.7 sipariş verisi, termin ve mikro ihracat görünümü

- Sipariş numarası bağlantı olmaktan çıkarıldı; turuncu paket göstergesi ve tek tıklamayla kopyalama eklendi.
- Trendyol snapshot’ındaki iç içe teslimat/fatura adresleri ve iletişim/mükellef alanları fatura penceresinde doğrudan sipariş ayrıntısı API’sinden çözülür.
- Teslim terminini aşmış siparişlerde kalan süre yerine gün hesabı ve açık kargoya teslim uyarısı gösterilir.
- Mikro ihracat siparişleri satır ve fatura alanında mavi etiketle ayrılır. Kesilmiş faturada “Faturayı Gör” bağlantısı ile fatura bilgileri/pasif silme menüsü sunulur.

## 2026-08-08 - v10.6 sipariş operasyon etkileşimleri

- Sipariş satırına fatura işlemleri menüsü, fatura bilgileri penceresi, kesilmiş faturaya yönlendirme ve güvenli yükleme kontrolü eklendi.
- İşlemler menüsü; işleme alma, görsel kargo firması değişim penceresi ve dış yazma başlatmayan pasif iptal seçeneğiyle düzenlendi.
- İptal/mikro ihracat görsel vurguları, zaman metinleri, alıcı ve fatura alanı hizaları iyileştirildi; içerik yatay alanı azaltıldı.
- Sipariş ürün görselleri artık doğrudan varyant bağı olmasa da yerel katalogda aynı stok koduyla eşleştirilir; görsel yoksa yanıltıcı bir görsel üretilmez.
- Locked .NET restore + solution build, TypeScript, 14 Vitest ve frontend production build geçti. Stage dış yazma kapıları değişmedi.

## 2026-08-08 - v10.5 sipariş operasyon düzeni

- Sipariş listesi tek, taşmasız operasyon tablosuna dönüştürüldü; açılır hızlı ayrıntı satırı ve Eşitleme merkezi bağlantısı kaldırıldı.
- Sipariş/paket/takip/müşteri/stok/model/barkod araması ile tarih, kargo firması, fatura durumu, sayfa boyutu ve seçimli toplu işlem kontrolleri eklendi.
- Varyant seçenekleri ayrı satırlarda, model kodu açık alan adıyla; fatura durumları da kırmızı/yeşil okunur etiketlerle gösterilir.
- Frontend TypeScript, 14 Vitest ve production build başarıyla doğrulandı.

## 2026-08-08 — v10 birleşik profesyonel panel arayüzü

- v10.4 sipariş listesi gerçek operasyon tablosuna dönüştürüldü. Liste API’si ürün satırları, SKU, barkod, model/seçenek, görsel, alıcı iletişim/vergi bilgisi, adres snapshotları, brüt/indirim tutarı ve paketleri tek sorguda döndürür; arayüz referanstaki sipariş/alıcı/bilgiler/fiyat/kargo/fatura/durum kolonlarını gerçek verilerle doldurur.
- v10.3 sipariş ekranı referans düzene taşındı: durum sekmeleri, kompakt filtre çubuğu, kolon çizgileri, alıcı/sipariş/ürün/fiyat/fatura/işlem hizası ve satır açılımı yeniden düzenlendi.
- v10.2 geri bildirim düzenlemesinde sol navigasyon açık/beyaz temaya döndürüldü; aktif menü, hover ve ikon vurguları yeni tasarım diliyle korunur.
- v10.1 görsel revizyonunda uygulama kabuğu belirgin biçimde yenilendi: koyu kurumsal sol navigasyon, yarı saydam üst bar, kart biçimli sayfa başlığı, vurgulu metrikler, modern rapor yüzeyleri ve mobil alt navigasyon eklendi.
- Panel genelinde sayfa başlıkları, yüzeyler, formlar, butonlar, durum kartları, liste kartları ve responsive kırılımlar tek bir görsel sistemde hizalandı.
- Sipariş kartlarının açılır ayrıntısı müşteri kimliği, teslimat ve fatura adresleri, ürün/SKU/barkod satırları, finansal özet ile kargo/takip bilgilerini tek çalışma alanında gösterir; ayrıntı yalnız açıldığında yüklenir.
- Ürün yayın kanalı alanı aktif kapsamla sınırlandı; kapsam dışı pazaryerleri kullanıcı arayüzünden kaldırıldı.
- 14 Vitest davranış testi, TypeScript denetimi, production build ve 3 Playwright tarayıcı testi geçti. Stage ve dış yazma kabul kapıları değişmedi.

## 2026-08-08 — Yerel sürümün GitHub eşitleme öncesi derleme düzeltmeleri

- Production v9 yenilemesinde saptanan eksik/null sayfalı API koleksiyonlarına karşı dashboard ve ürün çalışma alanı fail-safe hale getirildi; boş koleksiyonlar arayüzü düşürmeden gösterilir.
- Tam CI kapısında saptanan eksik Chromium kurulumu doğrulama iş akışına eklendi; çözüm genelindeki mevcut C# whitespace ihlalleri `dotnet format` ile giderildi.
- Release E2E paketindeki güncel dashboard güvenlik metni, özellik-değeri liste fixture'ı ve özellik eşleme yükleme yarışı v9 arayüzüyle hizalandı.
- Playwright web sunucusu yeniden kullanım ayarı frontend Node tiplerine ek bağımlılık oluşturmadan sabitlendi.
- F3 shipment hash metnindeki hatalı satır sonu, common-label hata akışındaki nullable erişim ve return evidence stream çağrısı düzeltildi.
- Production hardening migration sınıfındaki yinelenen migration metadata kaldırıldı; tarihsel migration kimliği değiştirilmedi.
- E-Faturam bağlantı görünüm modeli, fake adapter ve sözleşme testleri güncel uygulama kontratlarıyla hizalandı.
- Backend build, frontend typecheck, 137 Docker gerektirmeyen backend testi, 13 Vitest davranış testi ve frontend production build geçti. Docker/PostgreSQL testleri yerel Docker bulunmadığı için `NOT_RUN`; Stage ve production blokajı korunmuştur.
- Kategori/özellik eşleme seçim yarışları, fatura politikası optimistic-concurrency yükleme kilidi ve güncel dönüş fixture sözleşmesi düzeltildi.

## 2026-08-06 — v9 katalog, kategori eşleme ve varyant çalışma alanı

- Panel kategorisi → Trendyol yaprak kategorisi → kategori özelliği → özellik değeri eşleme zinciri tek çalışma alanında birleştirildi.
- Kategoriye bağlı panel özellik başlıkları oluşturma, mevcut özelliği bağlama, zorunlu/özel değer kuralları ve yeni seçenek değeri ekleme akışları tamamlandı.
- Kategori kapsamındaki özellik eşlemelerini tek istekte döndüren toplu mapping endpoint'i eklendi; özellik kartı başına yapılan N+1 sorgular kaldırıldı.
- Ürün oluşturma ekranı referans arayüze göre yeniden geliştirildi: fiyat/stok/vergi, ölçü-desi, görseller, kategori özellikleri, varyant matrisi, toplu stok/fiyat ve yayın kanalları aynı akışta toplandı.
- Varyant özellikleri varyant seviyesinde, normal özellikler ürün seviyesinde kalıcılaştırıldı; çoklu seçim, zorunlu özellik, aynı kombinasyon, SKU ve barkod kontrolleri güçlendirildi.
- Seçilen ACTIVE Trendyol kanalında kanal teklifleri ve listing profile hazırlanıp güvenli Product V2 yayın işi kuyruğuna bağlandı; capability ve dış-yazma kapıları korunur.
- Kaynak kabul betikleri, frontend sözleşme testleri, API yüzey testleri ve C# kaynak testleri eklendi. Exact Node/.NET ve gerçek Trendyol Stage doğrulaması ortam/credential eksikliği nedeniyle beklemektedir.

## 2026-08-05 — Trendyol E-Faturam provider-managed sade akış

- Paneldeki mali hesap, company/user, seri/prefix, Temel/Ticari senaryo, ödeme ve manuel kargo tüzel kimlik ayarları kaldırıldı.
- Eski bağlantılarda kalmış bu alanlar `SanitizeEfaturamProviderManagedSettings` veri migrasyonuyla geri döndürülemez biçimde silinir; yalnız dış-yazma anahtarı korunur.
- Bağlantı yalnız doğrudan E-Faturam e-posta/parolası kullanır; companyId/userId sign-in tokenından okunur ve varsayılan seri provider hesabından gelir.
- Belge türü Trendyol siparişindeki `commercial` ve `eInvoiceAvailable` alanlarından otomatik `TEMELFATURA`/`EARSIVFATURA` seçilir; ayrı mükellef sorgusu kaldırıldı.
- E-Arşiv internet satışı için provider sözleşmesinin istediği ödeme/taşıyıcı alanları kullanıcı ayarı olmadan sipariş/paket verisi ve resmî Trendyol carrier kataloğundan otomatik üretilir.
- API, UI, adapter, persistence, contract/frontend testleri ve F4 belgeleri sade akışla eşlendi; Stage ve exact runtime blokajları korunmuştur.

## 2026-08-05 — Trendyol E-Faturam F4 kod kapanışı

- API_USER ve MARKETPLACE (`signIn → customerSignIn`) kimlik doğrulaması, scope doğrulaması ve şifreli credential modeli tamamlandı.
- Mükellef sorgusu, Temel/Ticari E-Fatura seçimi, internet satışı E-Arşiv payment/delivery ve kargo tüzel kimlik eşlemesi eklendi.
- Numeric provider status kataloğu, durable submit/reconcile/document/cancel zinciri ve unknown durumda manuel inceleme uygulandı.
- Permanent PDF güvenli indirme/private storage, E-Arşiv iptal read-back ve Trendyol fatura link teslimi korundu.
- Giden E-Fatura status endpoint'i tahmin edilmedi; exact Stage/SIT relative path yapılandırılmadıkça fail-closed tutuldu.
- Şifre içermeyen mali ayar read-back endpoint'i, mevcut alanları koruyan PATCH ve çoklu kargo VKN/TCKN-yasal unvan paneli eklendi.
- Fatura paneli submit/reconcile/deliver/cancel, belge erişimi, filtre ve taxpayer sorgusuyla tamamlandı.
- Capability evidence resmî E-Faturam hostuna ayrıldı; mali write capability'lerde Stage fixture SHA-256 zorunlu kılındı.
- Exact .NET/Docker/frontend suite ve gerçek Stage mali E2E bulunmadığından production durumu `BLOCKED` kaldı.

Bu dosya kullanıcı ve geliştirici açısından anlamlı proje değişikliklerini kronolojik olarak kaydeder. Commit geçmişinin yerine geçmez; Git geçmişini anlaşılır bir iş özetiyle destekler.

## 2026-08-05 — Trendyol Türkiye CORE kod kapsamı

- Product V2 create, approval reconciliation, approved/unapproved update fazları ve archive/unarchive durable job akışları tamamlandı.
- Birleşik fiyat-stok batch, stale offer/projection koruması ve satır bazlı sonuç uzlaştırması eklendi.
- Order V2/stream, capability-gated paket aksiyonları, takip numarası, ortak etiket, iade approve/reject/evidence/read-back ve invoice-link sınırı tamamlandı.
- Capability evidence, dış yazma anahtarları, idempotency, ETag, audit ve external-effect fence birlikte fail-closed hale getirildi.
- Panel operatör yüzeyleri ve contract/frontend test kaynakları güncellendi; exact .NET/Docker/Stage doğrulaması production blocker olarak korundu.

## 2026-08-05 - Trendyol Product Create onay uzlaştırması

- Product Create batch içinde en az bir kabul edilen varyant bulunduğunda otomatik `TRENDYOL_PRODUCT_APPROVAL_RECONCILE` durable işi eklendi; batch reddi alan satırlar korunarak yalnız kabul edilen satırlar read-back’e alınır.
- Adapter barkodu önce onaylı, bulunmazsa onaysız ürün servisinde sorgulayarak approved, pending, rejected ve özel uzak durumları ayırır.
- Onaylanan satırlarda Trendyol `contentId` ve `variantId` kimlikleri yerel ürün/varyant linklerine idempotent kaydedilir; mevcut kimlik uyuşmazlığı otomatik değiştirilmeden `MANUAL_REVIEW` olur. Daha yeni yayın payload’ı bulunan eski approval job, uzak sorgu veya listing mutasyonu yapmadan `PRODUCT_APPROVAL_SUPERSEDED` olur.
- Pending veya iki listede henüz görünmeyen barkod yeniden denenir; ret nedeni satırda korunur; tam onay `LIVE`, tam ret `REJECTED`, kısmi ret `PARTIAL_REJECTED` olarak kaydedilir. Onay uzlaştırma hataları daha önce `CREATE_REJECTED` olmuş satırların kanıtını ezmez.
- Tam onay, kısmi ret, görünürlük gecikmesi ve kimlik çatışması PostgreSQL testleri ile approved/unapproved contract fixture testleri kodlandı. Exact .NET/Docker ve gerçek Trendyol Stage ortamı olmadığı için sonuç `DYNAMIC_NOT_RUN / BLOCKED_ENVIRONMENT` olarak tutuldu.
- Ana işleyiş belgesi 6.3'e; F3 plan/evidence, capability, risk ve izlenebilirlik kayıtları güncel koda göre yükseltildi.

## 2026-08-05 - Trendyol Product Create durable job ve batch sonucu

- Genel `UpsertAsync` ürün yazma adı `CreateAsync` olarak ayrıldı; update/archive yanlışlıkla create endpoint'ine yönlendirilmez.
- Product Create için capability + çift write-switch kapısı, güncel mapping ve ürün/teklif/stok/media doğrulayan deterministic payload composer eklendi.
- API durable `TRENDYOL_PRODUCT_CREATE` job üretir; Worker `SUBMIT -> POLL` akışında external-effect fence, batch sonucu, 4 saatlik sonuç penceresi, satır bazlı kabul/red ve partial failure kaydı uygular.
- Tam batch kabulü canlı yayın olarak değil `APPROVAL_PENDING` olarak kaydedilir; approved-products reconciliation sıradaki F3 işidir.
- Kalıcı HTTPS ürün görseli kaydetmek için `/api/v1/files/product-media-url`, yayın durumunu okumak için publication-status endpoint'i eklendi.
- PostgreSQL başarı/replay ve kısmi batch testleri kodlandı; mevcut ortamda .NET SDK/Docker olmadığı için dinamik çalıştırma sonucu `NOT_RUN / BLOCKED_ENVIRONMENT` olarak tutuldu.
- Ana işleyiş belgesi 6.2'ye, F3 plan/evidence, capability, risk ve izlenebilirlik kayıtları güncel koda göre yükseltildi.

## 2026-08-05 - F3 eşleme çalışma alanı test ve belge uyumu

- `F3Pages.test.tsx`, doğrudan eski özellik bileşenini test etmek yerine uygulamanın kullandığı `MappingPage kind="attributes"` giriş noktasına taşındı.
- Vitest senaryosu kategori kapsamı, özellik eşleme ve özellik değeri eşleme zincirini; request URL'lerini ve JSON payload'larını birlikte doğrulayacak şekilde genişletildi.
- Playwright kabuk testi güncel `İşlem Takibi` menüsü, Ayarlar alt menüsü ve `OWNER` rolü için Faturalama görünürlüğüyle hizalandı; ayrıca birleşik kategori-kapsamlı özellik/değer akışı için ayrı browser senaryosu eklendi.
- Ana işleyiş belgesi 6.1'e çıkarıldı; birleşik kategori-kapsamlı özellik/değer ekranı ile marka eşlemesinin ayrı görünümü açıklandı.
- İşleyiş belgesindeki F4 güvenli PDF, Trendyol link teyidi ve `ManualReview` job durumu anlatımları production sertleştirme v7 koduyla eşitlendi.
- Exact Node/npm, .NET, Docker ve Stage ortamları bu çalışma ortamında bulunmadığından dinamik sonuçlar başarı olarak işaretlenmedi.
- Statik kontrol sonuçları, çevresel blokajlar ve kalan production kapıları `docs/reviews/2026-08-05-f3-mapping-validation-report.md` raporunda toplandı.

## 2026-08-05 - Production sertleştirme v7

- Job sonuçları geçici, kalıcı, deneme limiti ve manuel inceleme durumlarına ayrıldı; backoff retry ve operatör job takip/retry/cancel API'leri eklendi.
- Panelde arka plan işlemlerini ve attempt geçmişini gösteren İşlem Takibi ekranı eklendi.
- E-Faturam PDF indirme exact HTTPS host, public IP, redirect, boyut, MIME ve PDF imza doğrulamasıyla sınırlandırıldı.
- Webhook gerçek byte sınırı ve rate limit ile korundu; gizli route tokenının Caddy ve ASP.NET request loglarına sızması engellendi.
- Fatura linki 2xx sonrası doğrudan tamamlanmak yerine `SUBMITTED`, teyit, retry veya `MANUAL_REVIEW` durumlarına geçirildi.
- CSRF token yenileme, idempotency süre temizliği, MFA yeniden doğrulama ve rol bazlı yazma yetkileri uygulandı.
- Periyodik sipariş/iade/reference job üreticisi, Worker heartbeat, frontend asset smoke ve one-shot bootstrap secret ayrımı eklendi.
- Worker sağlığı yalnız proses canlılığına değil başarılı veritabanı döngüsü/lease heartbeat sonucuna bağlandı; tenant dışı operasyon issue çakışması ve geçici iade aksiyonu retry durumu düzeltildi.
- Pull request/ana dal verify workflow'u ve Git base'li dokümantasyon transaction kapısı eklendi.
- Bu ortamda .NET, npm exact toolchain, Docker ve Stage testleri çalıştırılamadığından production durumu `BLOCKED` tutuldu.

## 2026-08-04 - Ana proje planı v6.0

- Ana proje belgesi yalnız yürürlükteki nihai planı anlatacak biçimde yeniden düzenlendi.
- Karar geçmişi, önceki seçenekler ve vazgeçilen mimari anlatıları ana belgeden kaldırıldı.
- Kapsam başlangıçtan itibaren Trendyol ve Trendyol E-Faturam ile başlayıp diğer platformları sonraki fazlarda tek tek ekleyen kademeli model olarak tanımlandı.
- Git geçmişi, evidence logları ve değişiklik kaydı teknik izlenebilirlik amacıyla korunmaya devam eder; ana ürün planının parçası sayılmaz.

## 2026-08-04 - Ana proje planı v5.0 ve Git geçmişi politikası

- Nihai belge, proje öncesi planlama ve karar geçmişini içeren yaşayan ana plana dönüştürüldü.
- Kullanıcı panelindeki ürün, sipariş, paket, kargo, etiket, fatura ve iade işleyişleri ayrıntılandırıldı.
- “Kodlandı”, “test edildi”, “Stage doğrulandı” ve “production hazır” durumları kesin olarak ayrıldı.
- Token tasarruflu hedefli test döngüsü tanımlandı; tam test suite faz/release kapılarında zorunlu tutuldu.
- `PROJECT-STATUS.yaml` makinece okunabilir durum kaynağı olarak eklendi.
- Dokümantasyon transaction ve otomatik tutarlılık kontrolü tanımlandı.
- Ana geliştirme repository'sinde `.git` geçmişinin korunmasına, temiz release/deployment paketinde çıkarılmasına karar verildi.
- Orijinal Git commit, tag ve remote geçmişi geliştirme paketine geri bağlandı.

## 2026-08-04 - Trendyol ve E-Faturam odaklı temizlik

- Aktif kapsam yalnız `TRENDYOL` ve `TRENDYOL_EFATURAM` olarak sınırlandı.
- Hepsiburada ve Shopify'ın yarım adapter/UI/test yüzeyleri aktif kaynak ağacından çıkarıldı.
- Ortak platform portları, veri modeli, job, mapping, audit ve migration zinciri korundu.
- Production AllowedHosts, readiness, Compose ve kaynak temizliği kontrolleri iyileştirildi.

## Önceki tarihsel kararlar

Önceki ayrıntılı kararlar `docs/adr/`, faz evidence logları ve Git commit/tag geçmişinde korunur.

## 2026-08-10 - Siparis varsayilan Yeni sekmesi ve rozet hizasi v10.30

- Siparis listesi ilk acilista `Yeni` durumunu secili getirir; kullanici diger sekmelere ve tum kayitlara gecmeye devam edebilir.
- Urun adet rozeti urun gorseli cercevesinin sag ust kosesine sabitlendi ve mikro ihracat fatura etiketindeki bilgi ikonu kaldirildi.
- Degisiklik yalniz listeleme varsayimi ve CSS sunumudur; siparis verisi veya dis platform yazma kapilari degismedi.

## 2026-08-10 - Uctan uca test port izolasyonu v10.30-r3

- Tam yigin tarayici kaniti sabit `5173` portu yerine calisma aninda ayrilan bos localhost portunu kullanir; paralel CI islerinin yanlis Vite oturumuna baglanma riski kaldirildi.

## 2026-08-10 - Full-stack kanit oturum sadelestirmesi v10.30-r4

- Full-stack tarayici kaniti oturumu UI alanlarini beklemek yerine ayni gercek auth endpointi uzerinden acar; ardindan dashboard, siparis kuyrugu, veritabani, worker ve liste gorunurlugunu tarayicida dogrular.
## 2026-08-11 - E-Faturam partner → müşteri Stage auth düzeltmesi

- Sağlayıcının resmî marketplace sözleşmesine uygun olarak mali kapsam, `signIn` JWT'sinden çıkarılmak yerine partner `signIn` ardından `customerSignIn` yanıtından alınır. Müşteri `companyId`, `userId` ve access token yalnız bu yanıtla kullanılır.
- E-Faturam credential kaydı partner ve müşteri Stage hesaplarını, ayrıca müşteri VKN/TCKN'sini şifreli saklayacak şekilde genişletildi. Değerler API/UI yanıtlarında yeniden gösterilmez; eski tek-credential kaydı fail-closed kalır ve Stage retry öncesi rotasyon gerekir.
- Canary dış-yazma istisnası yalnız sabit Stage hesabına bağlı kalır. Önceki token kapsamı hatası provider create çağrısından önce gerçekleştiği ve dış referans oluşmadığı için yalnız bu hata kodundaki aynı taslağa tekil denetlenebilir replay tanımlandı. Capability yükseltmesi yapılmadı.
