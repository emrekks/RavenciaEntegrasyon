# Güncel Faz ve Devralma Durumu

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

**Ana plan sürümü:** 7.6

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

## Production blockerları

- Exact backend ve frontend dinamik suite sonucu yok.
- Docker/Compose ve gerçek PostgreSQL Testcontainers sonucu yok.
- Trendyol Stage credential, kontrollü barkod/SKU/claim/package ve açık safe-write onayı yok.
- Capability satırları gerçek evidence olmadan `SUPPORTED` yapılamaz; global ve connection write switch kapalı kalır.
- LUXE/uluslararası storefront kapsam dışıdır.
- F4 kod kapsamı tamamlandı; exact runtime/Stage mali E2E ve off-host restore kanıtı tamamlanmamıştır.
