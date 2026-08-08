# Ravencia MarketplaceHub Değişiklik Kaydı

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
