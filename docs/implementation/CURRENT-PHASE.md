# Güncel Faz ve Devralma Durumu

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

**Ana plan sürümü:** 8.4

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
