# Proje hata tespiti ve düzeltme planı

Tarih: 6 Ekim 2026. Canlı gözlem aralığı: yaklaşık 22:15–22:20, Europe/Istanbul.

**Güncel doğrulama — 8 Ekim 2026, commit `e426d29f`:** Ürün düzenleme ekranındaki varyant etiketi kaydedilmiş seçenekleri önceliklendiriyor; eski `|`, `·` ve `•` imzalarını da tanıyor. Belirsiz imza ham hâliyle gösterilmediğinden astar/desen/menşei gibi kategori özellikleri seçenek alanına sızmıyor. Canlı hedef üründe satır etiketleri `Beden: M - Renk: Siyah` biçiminde tire kullanıyor; yedi satırın tümü kontrol edildi. Kayıtlı ürün nitelikleri değiştirilmedi. İlgili testler 14/14, tam web suite'i 58/58 dosyada 234/234; TypeScript, production build ve bundle bütçesi geçti. Fast-deploy içindeki izole PostgreSQL suite'i 787/787 geçti. Yedek `20261007T223759Z` oluşturuldu ve iki dosyanın checksum'ı `OK`; migration başarılı. Sunucu `e426d29f5bf3` üzerinde; API, worker, Caddy ve PostgreSQL sağlıklı, readiness HTTP 200. Validation testlerinde `FeatureFlags__ExternalWrites=false`; canlı API/worker ayarı `true` olarak korundu. İlk deploy denemesi disk doluluğunda yedek üretirken durdu; eski yedeklere dokunulmadı ve yalnızca kullanılmayan Docker build cache temizlendi (5.274 GB). B10, B11, B12, B13, B16 ve B18'in aşağıdaki kanıt açıkları sürüyor; plan tamamlanmış sayılmıyor.

**Son durum — 7 Ekim 2026, commit `d154fec2`:** B01–B18 ana düzeltmelerine ek olarak `/orders`, iade satırları ve ürün düzenleme varyant alanı yalnızca gerçek renk/beden seçeneklerini gösteriyor; astar, desen, menşei gibi kategori özellikleri bu sunum alanlarından gizleniyor. 100.000 kayıtlı fatura aday tarama grubu uygulandı. [Validate #822](https://github.com/emrekks/RavenciaEntegrasyon/actions/runs/37676468559) başarılı oldu. En son ürün varyantı sunum düzeltmesi için [Validate #825](https://github.com/emrekks/RavenciaEntegrasyon/actions/runs/37678251689) geçti. `deploy/scripts/fast-deploy.sh` içindeki son izole PostgreSQL doğrulaması 778/778 geçti; güncel web suite'i 58/58 dosyada 230/230 test, typecheck, production build ve paket bütçesinden geçti. Yedek checksum'ları, migration ve container yenilemesi tamamlandı. Sunucu `d154fec225a6` üzerinde; API, worker, Caddy ve PostgreSQL sağlıklı, `/health/ready` HTTP 200. Testler `FeatureFlags__ExternalWrites=false` ve dış ağa kapalı izole validation ağıyla çalıştı; canlı API/worker ayarı `FeatureFlags__ExternalWrites=true` olarak korundu. Önceki restore drill `20261007T095508Z` yedeği ve immutable app image digest'iyle izole ağda başarılıydı (274 sn; 3 şema, 47 migration, 1 tenant). Canlı arayüzden Hepsiburada `#4146480888` iadesi Onaylanan'da, Aksiyon Bekleyen'de değil; iş izleme ekranında Trendyol sipariş eşitlemesi başarılı ve worker kuyruğu aktif gözlendi. Ürün varyantlarında gerçek `Renk` değeri `Web Color` ve eski imza değerlerine öncelikli; `MZ005S26` için `XL / İndigo` doğrulandı. Ürün düzenleme ekranındaki mevcut varyant adı/özellik verisi değişmeden bırakılarak tablo sunumu beden ve renkle sınırlandırıldı ve hedef ürün canlı panelde doğrulandı. Uygulama/deploy tamamlandı; B10'un 1 saniye p95 hedefi ve eşzamanlı bellek etkisi, B11 uzun dönem hacim kanıtı, B12 gerçek bloke iş uçtan uca kanıtı, B13 onaylı off-host yedek hedefi ve CI-image bağı, B16 ek viewport regresyonu ve B18 sandbox yanıt teyidi açık kalıyor.

**Takip — 7 Ekim 2026, commit `0608e65f`:** Trendyol `Renk` değerini esas alan varyant eşlemesi ve yalnızca yerel varyant seçeneklerini yenileme modu [Validate #808](https://github.com/emrekks/RavenciaEntegrasyon/actions/runs/37652370058) PostgreSQL hizmetiyle başarıyla doğrulandı; CI'da `FeatureFlags__ExternalWrites=false` olduğundan test pazaryerine yazmadı. Commit `origin/main`'e alındı ve `deploy/scripts/fast-deploy.sh` ile üretime dağıtıldı. Canlı dış yazma ayarı önceki `true` değerinde bırakıldı. Mevcut katalog verisini düzeltmek için başlatılan ilk salt-okunur Trendyol seçenek aktarımı aşağıdaki 512 karakterlik imza hatasına takıldı; katalog güncellemesi tamamlanmış sayılmaz.

**Takip — 7 Ekim 2026, seçenek yenileme aktarımı:** Panelden başlatılan Trendyol seçenek-yenileme işi `cfca2d36d43e4b798b6ceb9df1a511c0` 947 kaydı okudu. İlk 457 üründe 6 atlama ve 394 hata oluştu; iş, kullanıcı tarafından durdurulunca `CANCELLED_BY_OPERATOR` durumuna geçti. Kayıt hataları PostgreSQL `22001` (`value too long for type character varying(512)`) gösterdi. Kaynakta aynı sınırın `ProductVariant.OptionSignature` sütununa ait olduğu ve imza üretiminin seçenekleri 512 karakter sınırı olmadan birleştirdiği doğrulandı. İşin özeti pazaryerinden yalnızca okuma yapıldığını ve dış yazma yapılmadığını gösterdi; 457 yerel güncelleme kısmi olarak kaydedilmişti. Düzeltme, imza parçalarını renk ve beden önceliğiyle 512 karaktere sığdırıyor; başarılı seçenek yenilemesi aynı ürün için önceki aktarım hata kaydını çözümlüyor.

**Son takip — 7 Ekim 2026, commit `349d6e0d`:** İmza uzunluğu düzeltmesi ve aktarım hatası temizliği [Validate #810](https://github.com/emrekks/RavenciaEntegrasyon/actions/runs/37668050715) ile izole PostgreSQL üzerinde geçti; CI testleri dış yazmayı kapalı tuttu. `349d6e0d058373a759d674757aaa6c5d7dec90a7` `main`'e alındı ve `deploy/scripts/fast-deploy.sh` ile üretime dağıtıldı. Dağıtım doğrulamasındaki izole PostgreSQL suite'i 778/778 geçti; yedek arşivi ve özel volume checksum'ları, migration, API/worker sağlığı ve readiness kontrolü başarılıydı. Üretim API ve worker ayarı `FeatureFlags__ExternalWrites=true` olarak doğrulandı.

Hotfix sonrasında panelden tekrar çalıştırılan salt-okunur Trendyol seçenek aktarımı `623bf67104224fa99dd8405a1d259b76` başarılı tamamlandı: 965 kayıt okundu, 941 işlendi, 7 atlandı, 0 hata oluştu. İş özeti ürünlerin pazaryerinden okunduğunu ve dış yazma yapılmadığını doğruladı. Ürün detayında `MZ005S26` stok kodu/barkodu için seçenek değeri `Beden: XL`, `Renk: İndigo` olarak doğrulandı; canlı Trendyol'a ürün içeriği gönderilmedi.

**İlk sürümün tarihsel doğrulama notu (`0608e65f`, hotfix öncesi):** Yerel backend/test projesi derlemesi başarılıydı; yerel test çalıştırıcısı testhost'a 90 saniye içinde bağlanamadığından bu denemede tam yerel test sonucu alınmamıştı. Bu not, sonraki [Validate #810](https://github.com/emrekks/RavenciaEntegrasyon/actions/runs/37668050715) ve dağıtım doğrulamasından öncedir; güncel doğrulama ve canlı durum yukarıdaki `349d6e0d` takip kaydında yer alır.

**Takip — 7 Ekim 2026, `7f8a935f`:** B12 durgun imleç alarmı bağlantı kartında ve ayar penceresinde görünür hâle getirildi ve canlıya alındı. Yayın kapısında web 222/222, backend 776/776 test geçti; API, worker, Caddy ve PostgreSQL sağlıklı, readiness HTTP 200 ve API/worker dış yazma ayarı `true`. B12'nin gerçek bloke akış ile browser uçtan uca kanıtı hâlâ alınmadı. B11 için canlı salt-okunur outbox hacmi/retention gözlemi yapıldı; B10 performans ölçümü aşağıdaki 15. bölümde güncellendi. Bu üç başlık tamamen kapanmış sayılmıyor.

Durum güncellemesi: 7 Ekim 2026. Fatura özet ve sayfa sorguları, listeye gereken sınırlı fatura alanlarını projekte edecek şekilde daraltıldı; sayfa adayları artık 2.000 kayıtlık anahtar-imleçli partilerle taranıyor ve ayrıntı verileri yalnız seçilen sayfa için yükleniyor. 20 kayıtlık sayfada 21. adaydan taşan sayfa sınırı hatası düzeltildi. Son olarak iş akışı sağlık durumu beklenen çalışma aralığı ve jitter sonrasına göre hesaplanacak şekilde düzeltildi. Önceki tam backend suite izole UTF-8 PostgreSQL kümesinde 765/765 geçti; cursor telemetrisi değişikliğinden sonraki veritabanısız koşuda 742 test geçti, 31 PostgreSQL testi atlandı. Arayüz suite'i 209/209, TypeScript kontrolü, üretim derlemesi ve paket bütçesi de geçti. Üretim Compose'ındaki dış yazma bayrağı artık deployment env dosyasından `MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED=false` ile acil durdurulabiliyor; varsayılan mevcut davranışı korumak için `true`. İki dağıtım betiği değeri doğruluyor ve aynı değeri Compose'a iletiyor. Docker/Compose çalıştırması olmadığı için birleştirilmiş Compose doğrulaması açık. DOMPurify minimum sürümü `^3.4.16` yapıldı; güvenlik testleri 2/2 geçti ve kurulu `source-map-js` 1.2.2. Güncel npm audit kayıt servisine erişemedi; NuGet audit de açık. Önceki koddaki yerel 100.000 aday denemesi 992 ms sürdü; o denemedeki sayfa toplamı hatası son sayfa taşma düzeltmesiyle kapatıldı. Bu tek sentetik örnek production p95/indeks kanıtı değildir; güncel 100.000 aday tekrarı, `EXPLAIN ANALYZE` ve staging ölçümü açık.

Tarayıcı doğrulaması: 7 Ekim'de canlı `/orders` ekranında Hepsiburada, Trendyol ve Shopify satırlarından birer seçim ayrı ayrı denendi. Her platformda seçim sayacı ve o platforma uygun toplu işlem araçları göründü; seçim temizlendi ve hiçbir sipariş işlemi gönderilmedi. Önceki “yalnız Shopify seçilebiliyor” şikâyeti bu oturumda tekrarlanamadı. Bu, mevcut canlı arayüzün gözlemidir; yerel taslakların dağıtıldığını kanıtlamaz ve sipariş seçimi için otomatik `OrdersPage` etkileşim testi bulunmadı.

Kod doğrulaması: İade sağlayıcı durumlarını anlık read-back ve arka plan eşitlemesinde farklı normalleştiren iki ayrı fonksiyon bulundu. Resmî Hepsiburada talep dokümanında `NewRequest`, `AwaitingAction`, `InDispute`, `Accepted`, `Rejected`, `Refunded` ve `Cancelled` durumları listeleniyor; anlık okuma `NewRequest` ile `Refunded` durumlarını `ActionRequired` olarak sınıflandırıyordu. Her iki yol ortak bir normalleştiriciye geçirildi. Bu kaynak bulgusu önceki üretim kayıtlarının kesin kök nedeni olarak kanıtlanmış değildir.

İncelenen yerel ve sunucu revizyonu: `25ceb0115dbebad5d4a3685223c3a076128bfb24`.

## 1. Kapsam ve sonuç

Kaynak kod, senkronizasyon ve yayın akışları, API sözleşmeleri, testler, CI, dağıtım dosyaları ve üretimdeki toplu veritabanı sonuçları incelendi. Üretim sorguları `BEGIN READ ONLY`, kısa sorgu zaman aşımı ve `ROLLBACK` ile çalıştırıldı. Pazaryerine yazma, cevap gönderme, ürün yayınlama, iade kararı veya fatura oluşturma işlemi yapılmadı. Uygulama kodu ve canlı yapılandırma değiştirilmedi.

En önemli aktif sorun, Trendyol fatura uzlaştırmasının kısmi iade siparişlerini tamamlarken sipariş senkronizasyon kilidine takılmasıdır. İlk gözlemde son 24 saatte 59, ikinci gözlemde 60 iş `DEAD` durumundaydı; hepsi altı denemeyi tüketmişti. Hata kodu `RETURN_ORDER_HYDRATION_BUSY`. Normal sipariş aktarımından bazı fatura durumları güncelleniyor olsa da ayrı fatura uzlaştırma akışı çalışmasını tamamlayamıyor.

Soru aktarımında canlı taramanın ilk sayfada kalması, belirsiz cevap gönderimlerinin kurtarma akışının eksikliği ve idempotency başlıklarının uyuşmaması kaynakta doğrulandı. Eşleme özeti ile yayın kontrolünün güncel snapshot değerlendirmesi de tutarlı değil. CI mevcut biçim kontrolünü geçemiyor ve arayüz testlerinin tamamını çalıştırmıyor.

Bu çalışma tüm olası hataların yokluğunu kanıtlamaz. Aşağıdaki ayrım özellikle korunmalıdır:

- **Canlıda doğrulandı:** üretim durumu veya çalıştırılan kontrolün sonucu.
- **Kaynakta doğrulandı:** belirtilen koşulda hataya yol açan kod yolu; üretimde o koşulun gerçekleştiği ayrıca belirtilmedikçe iddia edilmez.
- **Risk / karar:** ölçüm, iş kuralı veya operasyonel kanıt gerektiren alan.

Öncelikler: **P1** veri doğruluğu, işin tamamlanması veya yayın doğrulamasını etkileyen konu; **P2** kapsamı sınırlı tutarsızlık veya büyümeyle artan risk; **P3** sonraki bakım işi. İnceleme sırasında aktif veri kaybı veya tenantlar arası veri sızıntısı kanıtlanmadığı için P0 atanmadı.

## 2. Çalıştırılan kontroller

| Kontrol | Sonuç | Sınır |
|---|---|---|
| Backend: `dotnet test MarketplaceHub.sln --no-restore --verbosity minimal` | 765 geçti, 0 atlandı, 0 başarısız | Geçici PostgreSQL 18 UTF-8 kümesi kullanıldı; gerçek sağlayıcı API'si ve çalışan worker kapsam dışı |
| Arayüz: `npm test` | 54 dosyada 209 test geçti | Gerçek tarayıcı yerleşimi, üretim verisi ve gerçek sağlayıcı bağlantısı kanıtı değildir |
| `npm run typecheck` ve `npm run build` | İkisi de başarılı | Derleme iş akışının tamamlandığını kanıtlamaz |
| `npm run check:bundle` | Başarılı | Ana JS 172,6 / 450 KiB; ana CSS 632,6 / 650 KiB; CSS bütçesine 17,4 KiB kaldı |
| `dotnet format MarketplaceHub.sln --verify-no-changes --no-restore --verbosity quiet` | Başarılı | Önceki biçim hataları düzeltildi |
| `git diff --check` | Başarılı | Mevcut çalışma ağacı farklarında boşluk hatası yok |
| EF bekleyen model değişikliği kontrolü | Başarılı | Migration sonrası bekleyen model farkı yok |
| `npm audit --omit=dev --audit-level=high --registry=https://registry.npmjs.org` | Bu çalıştırmada sonuç üretmedi | npm bulk advisory endpointine erişilemedi; exit sonucu temiz audit olarak yorumlanamaz |
| Canlı readiness ve container durumu | HTTP 200; API, worker, Caddy ve PostgreSQL healthy | Veri güncelliği garantisi değildir |
| Yerel / sunucu revizyonu | Aynı SHA | Bu revizyonun bütün iş akışlarının doğru olduğu anlamına gelmez |

NuGet için güncel güvenlik danışma taraması tamamlanmadı. 7 Ekim npm audit tekrarı registry'nin bulk advisory endpointine erişemedi. Yerel kurulum DOMPurify 3.4.16 ve source-map-js 1.2.2 gösteriyor. DOMPurify için iki düşük önem dereceli bildirim 3.4.15 ve öncesini etkiliyor, 3.4.16'da düzeltilmiş; kaynakta `IN_PLACE` ve kaldırma hook'u kullanımı bulunmadı. `source-map-js` bildirimi `<1.2.2` sürümlerini etkiliyor ve 1.2.2'de düzeltilmiş; yerel ağaçta görülen sürüm 1.2.2. Kaynaklar: [DOMPurify GHSA-p98j-92pf-mc4p](https://github.com/cure53/DOMPurify/security/advisories/GHSA-p98j-92pf-mc4p), [DOMPurify GHSA-6688-9rhm-gjv2](https://github.com/cure53/DOMPurify/security/advisories/GHSA-6688-9rhm-gjv2), [source-map-js GHSA-68fv-2mgg-jv7q](https://github.com/advisories/GHSA-68fv-2mgg-jv7q). Tam bağımlılık audit'i, gerçek tarayıcı regresyonu, yük testi ve yedekten geri dönüş tatbikatı açık doğrulama işleri olarak kalıyor.

## 3. Üretim bulguları

| Gözlem | Kanıt | Yorum |
|---|---|---|
| Trendyol fatura uzlaştırması kilit hatasında denemeleri tüketiyor | Son 24 saatte 60 `DEAD`, her birinde 6 deneme; `RETURN_ORDER_HYDRATION_BUSY` | Aktif P1 hata |
| Kısmi iade siparişi projeksiyonları var | `_ravenciaReadModelSource=RETURN_CLAIM` olan 201 sipariş | Tam sipariş verisinden tamamlanmaları gerekiyor |
| Trendyol fatura durumu bilinmeyen paketler var | Aktif bağlantıda 201 `Unknown`; bu 201 pakette `MarketplaceInvoiceObservedAt` boş | Kısmi sipariş sayısı ile eşit sayıda; kayıt bazında eşleştirme düzeltme aşamasında yapılmalı |
| Bazı fatura gözlemleri çalışıyor | Trendyol 104 `Invoiced`, 33 `NotInvoiced`; son gözlemler inceleme sırasında yenilenmiş | Tüm fatura yolları durmuş gibi genelleme yapılmamalı |
| Hepsiburada iade durumu artık onaylı | 6 `Approved`, 2 `Rejected`, 137 `Completed`; `ActionRequired` satırı yok | Önceki ekran görüntüsündeki Hepsiburada aksiyonda kalma şikâyeti bu örneklemde halen sürüyor diye gösterilemez |
| Soru geçmişi aktarılmış | Trendyol ve Hepsiburada `HistoryImported=true`, son çalışmaları `IDLE`; imported count 694 ve 39 | Geçmişin tamamlanması canlı sayfalama sorununu ortadan kaldırmıyor |
| Belirsiz cevap gönderimi mevcut örneklemde görülmedi | Soru grupları `ANSWERED/CONFIRMED` ve `REJECTED`; `SUBMITTING/UNKNOWN` grubu yok | Kurtarma eksikliği kaynak bulgusudur; mevcut kullanıcı cevabının takıldığı iddiası değildir |
| Snapshot uyuşmazlığı mevcut eşlemelerde görülmedi | Kaydedilmiş kategori/özellik eşlemeleri güncel Trendyol snapshotlarına bağlı | Snapshot değişince oluşan risk mevcut durumdan ayrı ele alınmalı |
| Seçili ürünün Hepsiburada kategori eşlemesi eksik | Üründe yerel kategori var; Hepsiburada için 0, Trendyol için 1 kategori eşlemesi | Eksik kurulum bilgisi; doğrudan API arızası diye sunulmamalı |
| Outbox hacmi yüksek, kuyruk boş | 761.606 satır, tablo ve indeksler toplam 358 MB; ilk gözlemde yayımlanmamış olay 0 | Birikmiş geçmiş var; mevcut yayın gecikmesi kanıtı yok |
| İş geçmişi büyümüş | 49.355 iş, tablo ve indeksler toplam 47 MB | Eski başarısız işler ile bugünkü aktif hataları ayırmak gerekiyor |
| Yedek altyapısı var, güncel tatbikat kanıtı yok | `backup_staging` volume var; Compose backup servisi operations profiliyle tek çalışımlık | Yedek yok sonucuna varılamaz; güncel başarılı yedek ve geri dönüş kanıtı ayrıca alınmalı |

## 4. Öncelik tablosu

| ID | Öncelik | Konu | Kanıt türü | İlk iş |
|---|---|---|---|---|
| B01 | P1 | Fatura uzlaştırmasında kilit beklemesinin işi öldürmesi | Canlı + kaynak | Kısmi sipariş tamamlama ile fatura taramasını ayır |
| B02 | P1 | Fatura detay okuma hatasının kaydı güncellemeden başarıyla bitmesi | Kaynak | Kayıt bazında sonuç/güncellik takibi ekle |
| B03 | P1 | Soru canlı aktarımının ilk sayfada kalması | Kaynak | Sınırlı fakat devam edebilen sayfalama kur |
| B04 | P1 | Belirsiz / kesilmiş cevap gönderiminin kurtarılamaması | Kaynak | Kalıcı gönderim kaydı ve okuma ile uzlaştırma |
| B05 | P1 | Soru idempotency başlığının middleware ile uyuşmaması | Kaynak | Tek başlık sözleşmesine geç |
| B06 | P1 | CI biçim hatası ve eksik arayüz test kapısı | Çalıştırılmış kontrol + kaynak | Formatı ve CI test kapsamını düzelt |
| B07 | P2 | İade detay taramasında bazı kayıtların sıra alamaması | Kaynak, koşullu | LastCheckedAt / devam imleci ve adil parti |
| B08 | P2 | Eşleme özetinin eski snapshot eşlemesini geçerli sayması | Kaynak | Ortak güncellik kuralı kullan |
| B09 | P2 | Referans değişiminin sağlam eşlemeleri de bloke etmesi | Tasarım riski | Kimlik ve sözleşmeye göre yeniden doğrulama |
| B10 | P2 | Fatura ekranı ve menü sayacı için tüm verinin yüklenmesi | Kaynak + izole yük ölçümü | Aday taraması 10.000 kayıtlık anahtar-imleçli partilerde; 100.000 faturasız aday testinde p95 3.037 ms. Örnek sorgu planı indeksi kullanıyor; tam karışık veri ve staging p95 açık |
| B11 | P2 | Outbox büyümesi ve çoklu API yayını | Canlı hacim + kaynak riski | Yerel taslak tek dispatcher kilidi ve yayımlanmış olaylarda 30 günlük saklama ekliyor; entegrasyon testleri geçti |
| B12 | P2 | Hazır olma kontrolü sağlıklı olsa da modül eşitlemesi durmuş olabilir | Kaynak + kısmi API/UI telemetrisi | Bloke worker koşulunda uyarının kullanıcıya görünmesini browser’da uçtan uca doğrula |
| B13 | P1 | Yayında CI/image SHA ve geri dönüş kanıtının eksik olması | Kaynak + kanıt açığı | Hızlı akış test/yedek kapılarını kullanıyor; dış yazma kill switch'i env ile ayarlanabilir; Compose doğrulaması, immutable image CI/SHA bağı ve restore drill açık |
| B14 | P2 | Soru işlemleri için rol sözleşmesinin belirsizliği | Kaynak + iş kararı | Soru yanıtlama/aktarım/şablon API'leri operasyon rolüne açıldı; muhasebe ve salt-okunur reddediliyor |
| B15 | P2 | Bağımlılık güvenlik sürüm tabanı ve tam audit kanıtı | Üretici/GitHub advisory + yerel paket ağacı | DOMPurify 3.4.16 ve source-map-js 1.2.2 bilinen kayıtları gideriyor; tam npm/NuGet audit kanıtı açık |
| B16 | P2 | CSS ve büyük modüllerde görsel regresyon / bakım yükü | Ölçüm + test açığı | Gerçek tarayıcı testleri; sonra parçalara ayır |
| B17 | P2 | İade durumunun anlık read-back ve arka plan eşitlemesinde farklı eşlenmesi | Kaynak + resmî sağlayıcı dokümanı | Tek durum normalleştiricisi ve iki akış için regresyon testi |
| B18 | P2 | Trendyol eski durum-filtreli sipariş yolunda geçersiz 6 aylık tarih sınırı | Kaynak + güncel resmî API sözleşmesi | Eski endpoint penceresini 30 günle sınırla; 3 aylık ilk aktarımı stream API'de tut |

## 5. Bulguların ayrıntıları ve düzeltme tasarımı

### B01 — Fatura uzlaştırması, kısmi sipariş tamamlama kilidinde tükeniyor

**Kanıt:** `MarketplaceJobProcessor.cs:2819`, `:2871`, `:2937`, `:2949`; `JobLeaseService.cs:31`, `:46`.

Kısmi iade siparişleri varsa yalnızca bu adaylar seçiliyor; normal fatura imleci ilerlemiyor. Tamamlama, normal sipariş hattının kilidini almaya çalışıyor. Kilit alınamazsa bütün fatura işi 30 saniye sonra tekrar denenecek şekilde kesiliyor. Her lease deneme sayısını artırıyor; altıncı denemede iş ölüyor. Canlıda bunun tekrar tekrar gerçekleştiği doğrulandı. Sadece deneme sayısını yükseltmek kilit çakışmasını ve normal taramanın sıra alamamasını çözmez.

**Düzeltme:** Kısmi sipariş tamamlama ayrı, sipariş hattı ile uyumlu bir okuma işi olmalı. Fatura taraması bu işi kuyruklayıp diğer adayları işlemeye devam etmeli. Kilit beklemesi ile sağlayıcı hatası ayrı sonuçlar olarak izlenmeli. Parti içerisindeki başarılı kayıtlar korunmalı; imleç, tamamlanan veya ayrı kuyruğa devredilen kayıtların ardından güvenli şekilde ilerlemeli.

**Test:** Gerçek PostgreSQL ve sahte pazaryeri portuyla normal sipariş kilidi tutulurken kısmi + normal siparişlerden oluşan parti çalıştırılmalı. Normal fatura gözlemi yenilenmeli, tamamlama görevi en fazla bir kez kuyruklanmalı, kilit açıldıktan sonra kısmi sipariş tek kez tamamlanmalı. Worker yeniden başlatma ve yinelenen tetikleme de kapsanmalı.

**Kabul:** Kilit dolu olduğu için yeni fatura işleri `DEAD` olmamalı; tüm uygun paketler sınırlı sürede kontrol edilmeli. 201 kısmi kayıt için her birinin sonucu tamamlandı / uzaktaki kayıt bulunamadı / sözleşme hatası / tekrar denenecek biçiminde açık olmalı. Bilinmeyen fatura durumu kanıt olmadan faturası kesildi veya bekliyor durumuna çevrilmemeli.

### B02 — Fatura okuma hatası, başarılı iş sonucu arkasında kalabiliyor

**Kanıt:** `MarketplaceJobProcessor.cs:2895`, `:2999`, `:3010`, `:2934`.

Trendyol paket detay okuması başarısızsa kayıt atlanıyor. Bazı hatalarda hata sayacı tutulsa da paket bazında açık bir hata ve son kontrol sonucu kaydedilmiyor; üst iş yine başarılı bitebiliyor. Hepsiburada sipariş okumasının başarısız olması bazı durumlarda operational issue üretse de üst iş başarı döndürebiliyor. Kullanıcı “iş başarılı” gördüğünde ilgili faturanın gerçekten doğrulandığını anlayamıyor.

**Düzeltme:** İş sonucu toplam taranan / güncellenen / doğrulanamayan / atlanan sayıları içermeli. Son başarılı gözlem, son deneme ve son hata ayrı tutulmalı. Kısmi başarı korunabilir; tamamen başarısız bir partinin tam başarılı olarak gösterilmemesi gerekiyor. Retry kayıt bazında yapılmalı.

**Test / kabul:** Aynı partide 200, 404, 429, 5xx ve sözleşmeye uymayan yanıtlar üret; 200 kaydı güncellensin, diğerlerinin eski verisi korunsun ve güncellik bilgisi yanlış ilerlemesin. Panelde hangi kayıtların halen doğrulanamadığı görünmeli.

### B03 — Soru canlı senkronizasyonu ilk sayfayı okuyor

**Kanıt:** `MarketplaceQuestionService.cs:98–109`, `:123–125`; `TrendyolQuestions.cs:59`.

Her tür/durum için canlı okumada Trendyol sayfa 0 / 50 kayıt, Hepsiburada sayfa 1 / 25 kayıt isteniyor. Devam sayfasına geçilmiyor. Üçer sayfalık devam yalnızca geçmiş aktarımı henüz tamamlanmamışken çalışıyor. Geçmiş tamamlandıktan sonra yoğun bir durum grubunun ikinci ve sonraki sayfaları okunmaz. Canlı pencere Trendyol için 14, Hepsiburada için 7 gün; eski ve halen açık yerel kayıtlar için ayrıca periyodik detay okuması bulunmuyor.

**Düzeltme:** Geçmiş ve canlı tarama ayrı kalıcı imleçler kullanmalı. Canlı tarama belli sayıda sayfa işleyip sonraki çalışmada devam etmeli; tarama sonuna ulaştığında overlap ile yeniden başlamalı. Açık ve belirsiz sorular ayrıca kimlik üzerinden yenilenmeli. Sağlayıcının tarih filtresinin oluşturulma mı güncelleme mi tarihine uygulandığı kontrat testinde doğrulanmalı.

**Test / kabul:** Trendyol 120, Hepsiburada 60 kayıtlık çok sayfalı yanıt; geçmiş tamamlandıktan sonra ikinci sayfadaki durum değişimi; tekrarlı/sabit imleç; sayfa arası yeni kayıt; eski açık soru. Tüm kayıtlar belirlenen tarama süresi içinde güncellenmeli ve geçmiş tamamlandı bayrağı canlı kapsamın tamamı gibi sunulmamalı.

### B04 — Kesilmiş veya belirsiz cevap gönderimi kalıcı kilit bırakabilir

**Kanıt:** `MarketplaceQuestionService.cs:185`, `:197–215`, `:268–272`; `QuestionEndpoints.cs:57`.

Servis gönderimden önce `SUBMITTING` durumunu kaydediyor. Sonra dış çağrı yapılıyor; iptal veya süreç kesintisinden sonra bu durumu uzlaştıran ayrı bir akış yok. Geçici ağ/5xx durumları `UNKNOWN` oluyor. Bu durumlarda yeniden gönderim bloklanıyor; yalnızca uzak durum `ANSWERED` olduğunda `CONFIRMED` yapılıyor. Uzak cevap reddedilmesi ve kabul edilmemiş bir gönderimin güvenli tekrarına ilişkin açık durum geçişi eksik. Canlı örneklemde şu anda takılmış gönderim bulunmadı.

**Düzeltme:** Cevap gönderimini kalıcı command/attempt kaydıyla yönet. İstek iptali iş sonucunun kaybolmasına yol açmamalı. Belirsiz sonucu önce uzak detaydan doğrula; kabul edilmişse kesinleştir, reddedilmişse nedeni göster ve yeni cevap hakkını iş kuralına göre aç. Kabul edilmediği kanıtlanmadan otomatik yeniden gönderme yapma. Timeout, worker çökmesi ve kayıt sürümü değişimi ayrı ele alınmalı.

**Test / kabul:** Commit sonrası iptal; uzak kabulden sonra bağlantı kopması; cevap reddi; iki kullanıcı aynı anda; yeniden başlatma. Her senaryoda tek dış etki ve açıklanabilir yerel sonuç sağlanmalı. Süresiz `SUBMITTING` kalmamalı; bilinmeyen sonuç kullanıcıya açıkça gösterilmeli.

### B05 — Idempotency başlık sözleşmesi uyuşmuyor

**Kanıt:** `QuestionEndpoints.cs:55`, `:95`; `IdempotencyMiddleware.cs:19`; `QuestionsPage.tsx:106`, `:117`; `shared/api/client.ts:16`.

Soru endpointleri ve arayüz `X-Idempotency-Key` kullanıyor. Genel middleware yalnızca `Idempotency-Key` görüyor; API client da bu iki başlığı birbirine çevirmiyor. Dolayısıyla mevcut soru çağrıları genel kalıcı replay/çatışma korumasına girmiyor. Soru satırındaki gönderim koruması bunun tüm işlevlerini sağlamaz.

**Düzeltme:** API genelinde tek başlık belirle. Gerekirse kısa bir uyumluluk döneminde eski başlığı da kabul et; iki farklı değer gelirse reddet. Kullanıcı aynı mantıksal isteği tekrar denediğinde anahtar korunmalı; yeni işlem için yeni anahtar üretilmeli. Tenant + yöntem + yol + gövde eşleşmesi sürdürülmeli.

**Test / kabul:** Aynı anahtar/gövde aynı yanıtı üretmeli; farklı gövde 409 olmalı; eşzamanlı istek ve timeout tekrarında bir command oluşmalı. Başlık sözleşmesi arayüz ve API entegrasyon testiyle doğrulanmalı.

### B06 — Mevcut CI kontrolü kırık; arayüz test kapsamı eksik

**Kanıt:** `.github/workflows/ci.yml:54`, `:78–80`; Web `package.json:15`; çalıştırılan format sonucu.

CI biçim doğrulaması yerelde altı boşluk hatasıyla başarısız oldu. CI arayüz için yalnızca `sanitizeHtml.test.ts` çalıştırıyor; 54 dosyalık tam suite yayın kapısında yok. Yerel tam suite başarılı olsa da sonraki regresyonlar bu kapıdan kaçabilir. Yerelde DB testlerinin atlanması ayrıca sessiz bir “tüm testler geçti” ifadesine dönüştürülmemeli.

**Düzeltme / kabul:** Format hatalarını düzelt, tam arayüz test komutu ve CI adımı ekle. İzole PostgreSQL zorunlu test job'ı olsun; beklenen DB testleri atlanırsa bu job başarısız olsun. CI sonucu ve doğrulanan SHA yayının girdisi olsun. Geçici olarak bir arayüz testini bozan değişiklik CI'da yayını durdurmalı.

### B07 — İade detay taramasında adil sıra eksik

**Kanıt:** `MarketplaceJobProcessor.cs:6989–7028`, özellikle `:6997`, `:7001`, `:7015–7019`.

Açık iadelerde kargo bilgisi eksik olanlar önce, ardından `UpdatedAt` sırasıyla ilk 25 kayıt seçiliyor. Sürekli eksik kalan en az 25 kayıt varsa kargo bilgisi tam olan açık kayıtlar seçilemeyebilir. 404 sonucunda `UpdatedAt` veya ayrı kontrol zamanı ilerlemediği için aynı eski kayıtlar yeniden seçilebilir. Canlı Trendyol örnekleminde eksik kargolu onaylı kayıt sayısı 13; 25 eşiğinin aşıldığı üretim durumu bu incelemede gösterilmedi.

**Düzeltme:** İş önceliği ile kontrol adaletini ayır; parti kotası veya kalıcı imleç uygula. `LastCheckedAt/NextCheckAt` kullan; değişmeyen başarılı yanıtta da kontrol zamanı ilerlesin. 404 kaydına ayrı geri deneme aralığı ve inceleme sonucu ver.

**Test / kabul:** 30 sürekli eksik kayıt + 10 dolu kayıt; 25 adet 404; değişmeyen yanıt; restart. Her uygun kayıt belirlenen çevrim içinde kontrol edilmeli, kalıcı sorunlu kayıtlar diğerlerini bloke etmemeli.

### B08 — Eşleme özeti ile yayın kontrolü farklı güncellik kuralları kullanıyor

**Kanıt:** `ReferenceDataService.cs:33–53`; `ProductPublicationComposer.cs:59`; `HepsiburadaProductPublicationComposer.cs:47`; katalog `pages.tsx:2022`.

Kategori özetindeki eksik zorunlu özellik sayacı, `VERIFIED` özellik eşlemelerinin snapshot kimliğini kontrol etmiyor. Yayın kontrolü ise güncel snapshotla birebir eşleşme istiyor. Bu nedenle eşleme merkezi eksik yok derken yayın engellenebilir.

**Düzeltme / kabul:** Özellik aktifliği, gereklilik, kapsam, yerel veri ve snapshot güncelliği ortak bir readiness değerlendirmesinden gelmeli. Aynı fixture hem eşleme özeti hem yayın kontrolüne verildiğinde aynı geçerli/eksik sonucu üretsin. API ve arayüz eksik nedenini aynı kodla göstersin.

### B09 — Referans yenilemesi geçerli eşlemeleri de eski bırakabilir

**Kanıt:** `MarketplaceJobProcessor.cs:1425–1458`; `ProductPublicationComposer.cs:41–48`; katalog `pages.tsx:2010`.

Tüm koleksiyonun içerik hash'i değişince yeni snapshot oluşuyor ve eskisi current olmaktan çıkıyor. Genel reference sync, eşlemeleri yeni snapshot üzerinde tekrar doğrulamıyor. Başka bir kategorinin veya değerin değişmesi dahi mevcut eşlemenin snapshotını eski yapabilir. Bazı ürün aktarımı yardımcıları eşlemeyi yeniden bağlayabilir; genel yenilemenin garanti ettiği bir davranış değil. Mevcut canlı eşlemelerin eski snapshotta kaldığı gözlemlenmedi.

**Düzeltme:** Eşlenen kimliği yeni snapshot içinde kontrol et. Kimlik aktif, kapsamı aynı ve yayın sözleşmesi uyumluysa denetlenebilir yeniden doğrulama yap. Silinen, pasifleşen veya anlamı/sözleşmesi değişen öğeyi otomatik geçerli sayma. Yeniden doğrulama sonucu kullanıcıya gösterilmeli.

**Test / kabul:** İlgisiz öğe eklenmesi; eşlenen öğenin silinmesi; zorunluluk/çoklu değer/kapsam değişimi. Sağlam eşleme gereksiz engellenmemeli; hatalı eşleme yayın korumasını aşmamalı.

### B10 — Fatura workspace ve menü sayacı sınırsız veri okuyor

**Kanıt:** `InvoicingBillingService.cs:78–149`; invoicing `pages.tsx:213`; `app/App.tsx:52`, `:616`.

Başlangıçta aktif bağlantıların tüm paketleri ve ilişkili sipariş, satır, fatura, belge/delivery bilgileri tek seferde yükleniyor; sayfalama arayüzde yapılıyordu ve menü sayacı aynı kapsamlı endpointi çağırıyordu. Bu akış ayrı özet/sayfa endpointlerine ayrıldı. Sayfa servisi artık adayları 2.000'lik anahtar-imleçli partilerde okuyup filtre/sayaç seçeneklerini tarama sırasında hesaplıyor; sadece istenen sayfanın ürün, belge ve teslimat detaylarını materyalize ediyor. Bellek kullanımı parti ve sayfa boyutuyla sınırlı, fakat filtre/toplam hesapları hâlâ tüm uygun adayları tarıyor; tarama bütünüyle SQL filtreli sayfalama değildir. Fatura ekranı 30 saniyede bir yenilemeye devam ediyor.

**Düzeltme:** Filtre ve sayfalama API'da olsun; sayı/özet endpointi detaylardan ayrı olsun. Liste sadece ekranın gerektirdiği alanları dönsün; ürün/sipariş detayları açılınca alınsın. Yeni query modeline göre indeksleri `EXPLAIN ANALYZE` ile değerlendir.

**Test / kabul:** İzole PostgreSQL'de 10 bin aday testi ve son sayfayı taşırma testi geçti; arama/platform/durum/tarih filtreleri ve kararlı sıralama testi de geçti. 100 bin paketlik güncel tekrar, sorgu planı (`EXPLAIN ANALYZE`), indeks değerlendirmesi ve tekrarlı staging p95 ölçümü açık. Başlangıç hedefi, seçilmiş staging donanımında sayfa başına en çok 100 kayıt ve p95 liste süresi 1 saniyenin altında; mevcut ölçümle hedef yeniden değerlendirilebilir. Menü sayacı ayrıntılı fatura verisi taşımamalı.

### B11 — Outbox geçmişi büyüyor; çoklu API sahipliği tanımlı değil

**Kanıt:** Canlıda 761.606 olay / 358 MB; `OperationsRealtimeBroadcaster.cs:30–59`, `:41`, `:70–74`.

Dispatcher olayları kilit veya sahiplik almadan okuyor, yayımladıktan sonra işaretliyor. Çoklu API instance aynı olayları yayımlayabilir. Her tenant olay partisinde dashboard projeksiyonu yeniden kuruluyor; periyodik tam yeniden kurulum da var. İncelenen kaynakta yayımlanmış outbox olayları için temizleme akışı bulunmadı. Tek API çalıştığı için çoklu yayın üretimde kanıtlanmış hata değil.

**Düzeltme:** Dispatcher lease/claim veya tek lider kuralı; olay kimliğiyle tüketici dedup; dashboardu yalnızca ilgili kaynak değişimlerinde veya kısa birleştirme aralığında yenile. Yayımlanmış olaylar için saklama/arşiv politikası oluştur. Yayımlanmamış olaylar temizlenmemeli; iş/audit geçmişiyle farklı saklama kuralları kullanılmalı.

**Test / kabul:** İki API dispatcher, gönderim sonrası işaretlemeden önce kesinti, backlog. Olay kaybolmamalı; tekrar yayın tüketicide tekrar işlem yaratmamalı. Hacim büyüme grafiği ve temizleme etkisi ölçülmeli; silme işi küçük partilerle ve koruma koşullarıyla doğrulanmalı.

### B12 — Readiness sağlıklı olsa da modül çalışmıyor olabilir

**Kanıt:** `PostgresHealthCheck.cs`, canlı HTTP 200 ile aynı anda fatura işlerinin `DEAD` olması.

Readiness PostgreSQL'e bağlanmayı kontrol ediyor; veri taraması kapsamı veya kuyruk sonucunu göstermiyor. Container healthy oluşu da fatura güncelliğini kanıtlamıyor.

**Düzeltme:** Son başarılı çevrimin gecikme eşiği, ilgili akışın yapılandırılmış `interval + jitter` süresini geçtikten sonra başlasın; daha sonra gecikme, bozulma ve çevrim dışı eşikleri uygulansın. Platform/modül için son deneme, en eski kontrol edilmemiş kayıt, cursor ilerleme zamanı, backlog ve terminal hata hızı ölçülsün. Readiness ayrı kalsın; iş kalitesi bir operasyon göstergesi ve alarm olsun. Panelde son başarılı gözlem ve yenileme hatası gösterilsin.

**Kabul:** 24 saatlik ve 3 günlük akışlar planlı aralık dolmadan gecikmiş/çevrim dışı sayılmamalı; aralık+jitter ve tanımlı tolerans geçince gecikme kademeli artmalı. Testte iş akışı bloke edildiğinde API healthy kalabilir, fakat ilgili modül alarmı üretmeli ve kullanıcıya eski veri olduğu gösterilmeli. Sadece yeniden sorgulama, gerçek uzak gözlem zamanı gibi gösterilmemeli.

### B13 — Hızlı yayın ve geri dönüş kanıtı

**Kanıt:** `deploy/scripts/fast-deploy.sh:5–6`; production Compose; backup/restore betikleri.

İlk incelemede hızlı dağıtım betiği test ve yedek çalıştırmıyordu. Yerel taslak, migration öncesi izole PostgreSQL backend testini ve doğrulanmış yedeği şart koşuyor; readiness, worker ve asset kontrolleri de mevcut. Immutable image kullanan ayrı betik ve restore drill altyapısı var; bu altyapının son başarılı çalışması incelenen kanıtlar arasında yok. Production override'daki global dış yazma bayrağı artık `MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED` değerini alıyor (varsayılan `true`); iki dağıtım betiği yalnız `true/false` kabul edip aynı değeri Compose'a geçiriyor. Böylece operatör yayın öncesi env dosyasından dış yazmayı kapatabilir; ayarın production üzerindeki etkisi henüz uygulanmadı veya doğrulanmadı.

**Düzeltme:** Yayının girdi SHA'sı, başarılı CI ve image kimliği kaydedilsin. Şema değiştiren yayında doğrulanmış yedek ve ileri/geri uyumluluk planı olsun. İzole ortamda restore drill çalıştırılıp RPO/RTO hedefleri tanımlansın. Global dış yazma anahtarı env dosyasından işletilebilir acil durdurma olarak bağlandı; test dağıtımları dış yazmayı kapalı tutmalı.

**Kabul:** Başarısız test veya migration uygulamayı değiştirmeden yayını durdursun. Tatbikat gerçek dump, dosyalar ve gerekli anahtarlarla izole sistemin açıldığını kanıtlasın. Son çalışan image korunmalı; veritabanını yalnızca uygulama image'ını geri alarak eski şemaya dönmüş gibi kabul etme.

### B14 — Soru işlemlerinin rol sözleşmesi

**Kanıt:** `RoleAuthorizationMiddleware.cs:16–21`, `:46–62`.

Soru/cevap şablonu yolları operasyon rolü grubunda sayılmıyor; mutasyonlar varsayılan owner/admin grubuna düşüyor. `OPERATIONS` kullanıcısı soru cevaplama ve sync'te 403 alır. Bunun istenen iş kuralı olup olmadığı netleştirilmeli. GET/HEAD rol middleware'i tarafından atlanıyor; tenant doğrulaması ayrı bir mekanizma. Bu tek başına tenant sızıntısı kanıtı değildir.

**Düzeltme / kabul:** Modül ve işlem bazında rol matrisi oluştur. Operasyon çalışanı cevaplayacaksa API ve UI bunu birlikte desteklesin. Readonly mutasyonları kapalı; muhasebe, ürün ve operasyon erişimleri iş kararıyla ayrı test edilsin. Uygun olmayan işlem UI'da açık nedenle kapalı olsun; API doğrudan çağrıyı da reddetsin.

### B15 — DOMPurify ve source-map-js güvenlik sürüm tabanı

İki DOMPurify bildirimi de `3.4.15` ve önceki sürümleri etkiliyor, `3.4.16` ile düzeltilmiş. Uygulama `DOMPurify.sanitize(value, config)` üzerinden HTML string temizliyor; kaynakta `IN_PLACE` veya node kaldıran hook kullanımı bulunmadı. Bu nedenle mevcut kaynak kodda bildirilen IN_PLACE saldırı yolları etkin görünmüyor. `source-map-js` bildirimi `<1.2.2` sürümlerini etkiliyor; yerel kurulumda 1.2.2 mevcut. Bu paket ağaçta geliştirme bağımlılıkları üzerinden görünüyor; tam `npm audit` ve NuGet audit ağ erişimi nedeniyle doğrulanmadı.

**Kaynaklar:** [DOMPurify hook danışması](https://github.com/cure53/DOMPurify/security/advisories/GHSA-p98j-92pf-mc4p), [DOMPurify rawtext danışması](https://github.com/cure53/DOMPurify/security/advisories/GHSA-6688-9rhm-gjv2), [source-map-js yüksek önem dereceli danışma](https://github.com/advisories/GHSA-68fv-2mgg-jv7q); `shared/security/sanitizeHtml.ts:23`.

**Düzeltme / kabul:** DOMPurify paket ve lockfile tabanı 3.4.16'ya güncellendi. Güvenlik testi ve production build geçti. Tam registry tabanlı npm audit yeniden çalışmalı; NuGet dahil tüm bağımlılıklar taranmalı ve CI'da yüksek önem dereceli bulgular için kapı bulunmalı. Ağ erişimi gelmeden audit'in temiz olduğu varsayılmamalı.

### B16 — Görsel regresyon ve büyük modüller

**Ölçüm:** CSS 11.952 satır; job processor 7.549; katalog sayfaları 4.031; marketplace sayfaları 3.053. Güncel bundle denetiminde CSS 632,6/650 KiB; kalan pay 17,4 KiB. Repository'de Playwright/Cypress/e2e isimli bir gerçek tarayıcı test altyapısı bulunmadı; mevcut Vitest testleri değerli olsa da CSS yerleşimini gerçek viewportta doğrulamaz.

Önceki kullanıcı ekranları başlık, checkbox hizası, ürün görsel oranı, adet rozetinin kesilmesi ve dar sütunlarda sıkışma gibi sorunları gösteriyor. Kaynakta sipariş bilgisi başlığı, iade liste/modal görsel oranı, onay sıralaması, Hepsiburada barkod biçimi, geri sayımın yalnız aksiyon bekleyen duruma ait olması ve etiketin yalnız reddedilen iadede görünmesi için düzeltmeler mevcut. Ürün tümünü seç onay kutusu hizası ve fatura seçim kutusu boyutu CSS'te sabitlendi; soru ekranındaki platform uyarı bandı kaldırıldı, kontrol aralığı sıkılaştırıldı ve iş izleme ekranındaki İngilizce soru işi adı Türkçeleştirilip kapsam açıklaması eklendi. Bu turda iade satırı adet rozeti çerçevenin içine taşındı; metin sütununa taşarak kesilmemeli. CSS/React düzeltmeleri Vitest/typecheck/build ile birlikte geçti, ancak gerçek tarayıcıda piksel düzeyinde doğrulanmadı.

**Düzeltme:** Önce testlerle mevcut davranışı sabitle. Sonra CSS'i modüllere, katalog sayfasını küçük bileşenlere ve job processorı alan bazlı işleyicilere ayır. Bu refactor, veri doğruluğu düzeltmelerinden önce yapılmamalı; aynı sürümde geniş davranış değişikliğiyle karıştırılmamalı.

**Kabul:** 1299, 1537/1543 ve dar desteklenen viewportlar; yatay kaydırma, modal, klavye erişimi, uzun başlık ve çok ürünlü satır. Görseller liste ve modalda 2:3; adet rozetleri kırpılmıyor; fatura/ürün checkboxları boyut değiştirmiyor ve hizalı; başlık ve filtre etiketleri açık. Görsel baz çizgi, inceleyerek onaylanan örneklerle kurulmalı.

### B17 — İade durumu anlık okuma ile arka plan eşitlemesinde farklı normalleştiriliyor

**Kanıt:** `MarketplaceSalesService.cs` ve `MarketplaceJobProcessor.cs` içerisinde birbirinden ayrı `CanonicalReturn` eşlemeleri vardı. Arka plan eşitlemesi Hepsiburada'nın `NewRequest`, `InDispute` ve `Refunded` durumlarını tanırken anlık karar read-back'i bunları tanımıyordu ve varsayılan `ActionRequired` sonucuna düşürüyordu. Hepsiburada'nın [resmî Talep Entegrasyonu durum listesi](https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=talep-onemli-bilgiler-2&product=talep-entegrasyonu&view=guide) bu değerleri belgeler.

Bu tutarsızlık aynı sağlayıcı yanıtının iki yürütme yolunda farklı yerel duruma dönüşmesine neden olabilir. Kaynak kodda sorun doğrulandı; eski üretim şikâyetindeki belirli Hepsiburada kaydının bu nedenle aksiyonda kaldığı kanıtlanmadı.

**Düzeltme:** Her iki yol ortak `MarketplaceReturnStatus.Canonicalize` fonksiyonunu kullanıyor. Normalleştirme boşluk, tire ve alt çizgileri yok sayıyor; belgelenen durumları tek enum sonucuna çeviriyor. Bilinmeyen değer mevcut koruyucu davranışla `ActionRequired` olarak kalıyor.

**Test / kabul:** Durum listesindeki her değer, ayraçlı yazımlar ve `Created` + kargo bağlantısı kanıtı saf birim testinde doğrulanmalı. Anlık read-back ile arka plan senkronizasyonu aynı ham değer için aynı canonical durumu kullanmalı; sağlayıcıya erişim veya dış yazma yapılmadan test edilmeli.

### B18 — Trendyol eski sipariş endpoint'inde 6 aylık pencere sınırı sözleşmeyi aşıyor

**Kanıt:** `TrendyolHttpClient.PollAsync` durum filtreli `/v2/orders` yolunda `Delivered` ve `Awaiting` için 6 ay geriye gidiyordu. Güncel Trendyol dokümanı bu sayfa tabanlı endpoint'in 30 günle sınırlandığını, büyük taramalar için cursor'lı stream'in kullanılmasını söylüyor. Aynı kod tabanında ilk veri çekme işi `PackageItemStatuses` göndermeden stream endpoint'ini çağırıyor; dolayısıyla bu 6 aylık yol mevcut bootstrap çağrısı değildi, fakat bu kola giren herhangi bir durum filtreli okuma geçersiz tarih aralığı isteyebilirdi. Akış endpoint'i son 3 ayı destekliyor ve her istek en çok 14 gün olmalı. Kaynaklar: [Trendyol Stream dokümanı](https://developers.trendyol.com/tr/v3.0/docs/get-shipment-packages-stream), [Trendyol sipariş API dokümanı](https://developers.trendyol.com/tr/docs/sipari%C5%9F-paketlerini-%C3%A7ekme-getshipmentpackages), [Trendyol changelog](https://developers.trendyol.com/v3.0/changelog/changelog).

**Düzeltme:** Paylaşılan `TrendyolOrderHistoryPolicy` stream ilk taramasını 3 takvim ayıyla, eski durum filtreli endpoint'i 30 günle sınırlar. İlk veri çekme akışı cursor ve en çok 14 günlük pencerelerle stream kullanmaya devam eder. Shopify ilk tam taramada güncelleme tarih filtresi koymadan tokenın erişebildiği kayıtları ister; Shopify varsayılan olarak 60 günlük sipariş erişimi verir, `read_all_orders` onayı olan bağlantı daha eski siparişlere de erişebilir ([Shopify erişim kapsamları](https://shopify.dev/docs/api/usage/access-scopes)). Hepsiburada ilk sipariş taraması yerel politikada bir ay olarak tanımlı ve API istekleri 24 saatlik parçalara bölünüyor; bu bir aylık üst sınırın her Hepsiburada sipariş kaynağı için sağlayıcı tarafından garanti edildiğine dair ayrı bir sözleşme teyidi değildir.

**Test / kabul:** Stream için 3 ay, eski Trendyol yolu için 30 gün tarih sınırlarını doğrulayan iki politika testi ve sağlayıcı bazlı geçmiş politikalarının hedefli suite'i geçti (10/10). Sonraki iki PostgreSQL + sahte HTTP adaptör testi istek URL'si, sorgu aralıkları ve 30 günlük sınır davranışını doğruladı; güncel tam backend suite 765 geçti, 0 atlandı. Sağlayıcı sandbox/read-only doğrulaması açık.

## 6. Önceki şikâyetler için regresyon kapsamı

| Kullanıcı akışı | Doğrulama işi | Beklenen sonuç |
|---|---|---|
| Entegrasyonlardan “Verileri çek” | Platform bazında izin verilen geçmiş aralığı ve pencere bölme sözleşmesini test et | Tek global tarih aralığı kullanılmasın; sağlayıcı sınırı + cursor ile ilerlesin; yinelenen veri üretmesin |
| Sipariş seçiminde platform farkı | Hepsiburada, Trendyol ve Shopify satır seçimleri; sayfa seçimi; filtre/sayfa değişimi; iptal sipariş | Tüm uygun platformlar seçilebilsin; iptal sipariş seçilemesin; platforma özgü toplu araçlar doğru sayıyla çıksın; seçim temizleme tutarlı olsun |
| Hepsiburada iade durumu | `NewRequest`, `AwaitingAction`, `InDispute`, `Accepted`, `Rejected`, `Refunded`, `Cancelled`; anlık read-back ve planlı sync | İki yol aynı canonical durumu üretmeli; terminal kayıt aksiyon bekliyor görünmemeli |
| Faturalarda seçim | Platform ve fatura yeteneği ayrı değerlendirilerek tüm uygun satırlar seçilebilir mi? | Seçim ve izin verilen toplu işlem tutarlı olsun; desteklenmeyen dış işlem yanlış açılmasın |
| Hepsiburada onaylı iade | Uzak karar sonrası incremental, detay yenileme ve liste filtresi testi | Onaylı kayıt aksiyon listesinde kalmasın; badge/sayaçlar aynı sonucu göstersin |
| Faturası kesilmiş / yeni eklenmiş sipariş | B01/B02 sonrası paket bazlı uzak gözlem, durum birleştirme ve filtre | Faturası kesilmiş kayıt bekleyen listeden çıksın; yeni kayıt uygun listeye girsin |
| İade etiket düğmesi | Tüm kanonik iade durumları | Etiket yazdır yalnız reddedilenlerde |
| İade otomatik onay süresi | Requested, ActionRequired, Approved, Rejected, Completed | Sayaç yalnız ActionRequired; normal yazı ağırlığı |
| Onaylı iade sıralaması | Sipariş/onay tarihi; artan/azalan; aynı tarih; sayfa geçişi | Filtre üst satırında etiketli kontrol; kararlı sıralama |
| Hepsiburada barkodu | Ham barkod, merchantSku, başında sıfır olan gerçek barkod örnekleri | Keyfi sıfır eklenmesin; gerçek barkodun başındaki sıfırlar da körlemesine silinmesin |
| Ürün görselleri | Liste, modal, katalog kartı ve iç boşluk | 2:3 çerçeve/içerik kuralı tutarlı; adet rozeti görünür |
| Sipariş ve iş takip metinleri | Başlıklar, iş türü ve açıklaması | “Sipariş bilgileri”; Türkçe iş adı ve açık işlem açıklaması |
| Mikro ihracat faturası | Normal ve mikro ihracat durumları | Mikro ihracat badge'i mavi; erişilebilir renk karşıtlığı |
| Kargo etiketi | Adres metni, iki barkod ekleme, önizleme/yazdırma | Adres normal ağırlık; iki barkodun tasarımda ve çıktıda korunması |
| Ürün çekim ilerlemesi | Poll yenilenirken belirsiz progress animasyonu | Animation yeniden başlatma/takılma yok; azaltılmış hareket tercihi destekleniyor |
| Soru ekranı | Gereksiz not, filtre boşluğu, ürün/sipariş türleri | Kompakt ve açık UI; sağlayıcı yeteneği olmayan veri sahte şekilde destekleniyor gösterilmesin |
| Yayın eksikleri | Kategori eşlemesi yok, snapshot yok, 401/429/5xx, zorunlu alan eksik | Gerçek neden ve ilgili düzeltme adımı; belirsiz “özellik listesi alınamadı” genellemesi yok |

Trendyol adaptörünün mevcut soru listesi `PRODUCT` üretmektedir. Trendyol sipariş sorusu talebi ayrı bir yetenek araştırmasıdır: resmî erişim/kontrat kanıtı olmadan ürün sorularını sipariş sorusu gibi sınıflandırmak veya destek varmış gibi bir endpoint eklemek doğru olmaz. Bu rapor sağlayıcının tüm olası erişim kanallarını araştırdığı iddiasında değildir.

## 7. Uygulama sırası, bağımlılıklar ve tahmini efor

Eforlar tek geliştirici için mühendislik tahminidir; sağlayıcı erişimi ve staging hazırlanması süreyi değiştirebilir. Bazı ortak altyapı işleri aynı değişiklikte tamamlanabileceği için günler mekanik olarak toplanmamalı.

**Durum notu (7 Ekim):** Aşağıdaki aşama ve efor tablosu ilk teşhis sırasında çıkarılan başlangıç planıdır. Bölüm 11'deki yerel taslaklarla Aşama 0'ın backend/web test zemini ve EF kontrolü tamamlandı; B01–B11 ile B14'ün önemli kod düzeltmeleri ve B16'nın bir kısmı uygulandı. Bu son turdan sonra veritabanısız backend suite'inde 742 test geçti, 31 PostgreSQL testi ayrılmış bağlantı olmadığı için atlandı; web suite'i 209/209 geçti. Önceki izole PostgreSQL tam turunda 765/765 geçmişti. B12'de sağlık eşikleri beklenen aralığa uyarlandı; şimdi imleç değişimi ve veri gelip imlecin sabit kalması kalıcı ölçülüyor. Başarılı boş turlar durgunluk başlangıcını temizliyor; eski bağlantılardaki henüz gözlenmemiş ilerleme bilinmiyor olarak kalıyor. Yedi saf politika testi geçti; PostgreSQL kalıcılık ve servis testi son DB'siz koşuda ayrılmış bağlantı olmadığı için atlandı. Gerçek worker durgunluk uyarısı ve üretim kaydının uçtan uca doğrulanması açık. Diğer açık işler; restart/çökme testleri, sağlayıcı sandbox/read-only davranışı ve gerçekçi yük kontrolleri, browser QA, B13/B15 kanıtları ve sonrasında kontrollü operasyon doğrulamasıdır. Dolayısıyla 14–24 iş günü ilk plan tahminidir; yerelde tamamlanan işler tekrar tahmin edilmemelidir.

| Aşama | İşler | Bağımlılık | Tahmin | Çıkış ölçütü |
|---|---|---|---|---|
| 0 — Doğrulama zemini | B06; izole PostgreSQL; EF uyumu; CI'da tüm testler | Yok | 1–2 iş günü | Build, format, tam testler ve migration kontrolü yeşil; DB testleri atlanmıyor |
| 1 — Aktif fatura sorunu | B01 + B02; 201 kısmi kaydın okuma ile uzlaştırılması; B12 temel metrikleri | Aşama 0 | 2–4 gün | Kilit baskısı altında tarama ilerliyor; kayıt başına açık sonuç; yeni BUSY kaynaklı DEAD yok |
| 2 — Soru bütünlüğü | B03 + B04 + B05; B14 yetki kararı | Aşama 0 | 3–4 gün | Çok sayfalı canlı tarama ve belirsiz gönderim kurtarma testleri geçiyor |
| 3 — İade ve eşleme doğruluğu | B07 + B08 + B09; seçili ürünün eksik kurulum nedeninin gösterimi | Aşama 0; ortak metrikler | 2–4 gün | Her iade sıra alıyor; eşleme özeti ile yayın kontrolü aynı |
| 4 — Ölçülen performans | B10 + B11; staging veri yükü, indeks ve saklama ölçümleri | Önce doğru veri akışları | 2–4 gün | Liste/özet yükü sınırlı; outbox büyüme/sahiplik davranışı doğrulanmış |
| 5 — UI regresyonu | B16; önceki şikâyet matrisi; gerçek tarayıcı suite | İlgili veri düzeltmeleri | 2–3 gün | Desteklenen viewport ve durum matrisi geçiyor |
| 6 — Operasyon ve güvenlik | B12 tamamı + B13 + B15; NuGet/dev audit; restore drill | Aşama 0; izole ortam | 2–3 gün | Kanıtlı geri dönüş, alarm ve bağımlılık raporu |
| 7 — Kontrollü modülerleştirme | Büyük CSS/React/processor dosyalarını böl | Tüm davranış testleri | 3–5 gün | Kullanıcı davranışı aynı; değişim alanı ve CSS yükü küçülmüş |

İlk teşhis planındaki **Aşama 0** yerel çalışma ağacında tamamlandı; build, biçim, tam backend/web suite ve EF kontrolü geçti. **Aşama 1–3** için ana kod düzeltmeleri yerelde mevcut ve test edildi; bunların gerçek worker/sandbox davranışı ile canlı kayıtlar üzerindeki sonucu hâlâ kanıtlanmadı. Bu nedenle sıradaki yürütme sırası şöyledir:

1. **Canlı veri doğruluğu (read-only):** B01/B02 fatura akışında DEAD ve Unknown kayıtlarını sipariş/paket bazında uzlaştır; B17 iade durumunu Hepsiburada'dan yeniden okuyup onaylı/aksiyon bekleyen listeleri karşılaştır. Uzak sisteme yazma yapmadan her kaydı doğrulanmış / bulunamadı / yeniden denenmeli / sözleşme hatası olarak sınıflandır.
2. **B12 iş ilerlemesi alarmı:** Her kaynak için son başarılı çevrim ile cursor/watermark ilerlemesini ayır; ilerleme durduğu halde API sağlıklı kalan duruma modül alarmı ekle. Başarılı fakat boş sorguyu yanlışlıkla arıza saymayan PostgreSQL testleri ve worker durdurma testi ekle.
3. **B13 dağıtım kanıtı:** CI'da test edilen commit ile üretilen image SHA'sını bağla; Compose birleşik yapılandırmasını doğrula; test ortamında dış yazmalar kapalıyken yayın kapılarını çalıştır. İzole yedek kopyasında restore drill ve RPO/RTO ölçümü al.
4. **B10/B11 ölçüm:** 100.000 kayıtlı gerçekçi fixture ile toplam/filtre/sayfa isteklerini tekrarlı ölç; PostgreSQL `EXPLAIN (ANALYZE, BUFFERS)` planını ve p95'i sakla. Outbox büyüme/temizlik davranışını da staging benzeri koşulda gözle.
5. **B16 tarayıcı regresyonu:** Playwright tabanlı smoke/e2e suite kur; sipariş, iade, fatura ve ürün ekranlarını 1299/1537/1543 px ile dar görünümde doğrula. Yorumlardaki checkbox, ürün görseli, rozet, durum etiketi, arama alanı ve tablo sıkışması örneklerini kabul kriteri yap.
6. **B15 tam güvenlik taraması:** Registry erişimi düzeldiğinde tüm npm bağımlılıklarını (dev dahil) ve NuGet bağımlılıklarını tara; yüksek önem dereceli bulgular için CI kapısını doğrula. Mevcut yerel sürüm düzeltmelerini yeniden teyit et.

Bu kanıtlar tamamlanmadan yayına alma yapılmamalı. Aşamalar birbirine karıştırılmadan yürütülsün; geniş modülerleştirme tüm davranış testleri ve ölçümler sabitlendikten sonra ayrı bir iş olarak planlansın. İlk efor tahmini 14–24 iş günüdür; yerelde tamamlanmış işler bu tahmine yeniden eklenmemelidir.

## 8. Test ve yayın sözleşmesi

1. Her hata için kullanıcı davranışını veya veri değişmezini sınayan başarısız test önce hazırlanır. Yalnızca uygulama detayını tekrar eden assertion kullanılmaz.
2. Politikalar/sayfalama/sözleşmeler unit testlerle; transaction, lease, cursor, concurrency ve outbox gerçek izole PostgreSQL ile sınanır.
3. Dış portlar deterministik sahte yanıtlar kullanır. Gecikme, 404, 429, 5xx, bozuk JSON, tekrarlı sayfa, aynı anahtar ve iptal örnekleri kapsanır. Testlerde dış yazma kapalıdır.
4. Arayüz için tam Vitest suite, typecheck ve production build; görsel/durum akışları için gerçek tarayıcı testi çalışır.
5. Format, diff check, EF model/migration uyumu ve dependency audit sonucu sürüm kaydına eklenir. Testleri atlanan bir sonuç tam doğrulama diye sunulmaz.
6. Kod değişikliklerinde repository yayın kuralına göre test edilmiş commit push edilir, sunucu aynı SHA'yı çeker ve sağlık kontrolü yapılır. Bu rapor bir teşhis/plan çıktısıdır; kod yayını yapılmadı.
7. Veri düzeltmesi açık kayıt listesiyle ve tekrar çalıştırılabilir okuma/uzlaştırma işiyle yürütülür. “Hepsini onaylandı/faturalandı yap” biçiminde toplu durum düzeltmesi yapılmaz.
8. Yayın sonrası sadece HTTP 200 kontrol edilmez: ilgili tarama imleci ilerlemesi, son başarılı gözlem, hata sınıfı, bilinmeyen kayıt sayısı ve kuyruk sonucu ölçülür. İki tam çevrim ilk kontrol; 24 saatlik gözlem tekrarlayan hata kontrolü için önerilir.

## 9. Açık doğrulama ve kararlar

- İlk teşhis revizyonunda backend/PostgreSQL testleri geçici PostgreSQL 18 kümesinde çalıştırıldı: 724 geçti, 0 atlandı. Sonraki yerel taslakların güncel doğrulama sonuçları Bölüm 11'dedir. Kalan DB doğrulaması, özellikle süreç yeniden başladıktan sonra iade cursor'larının adil biçimde sürmesini ve sağlayıcı dış yanıtlarını kapsamalı.
- EF model/migration kontrolü yapıldı; bekleyen model değişikliği yok. NPM advisory erişim hatası tekrar denenmeli ve NuGet güvenlik taraması ayrıca tamamlanmalı.
- Soru cevaplama/şablon/sync için OPERATIONS ve diğer rollerin iş gereksinimini netleştir.
- “Verileri çek” tarih aralığının kaynak kod karşılaştırması tamamlandı: Hepsiburada bir aylık taramayı 24 saatlik alt pencerelere bölüyor; Trendyol stream üç takvim ayını en çok 14 günlük isteklerle tarıyor; Shopify tam taraması tokenın erişebildiği siparişlerle sınırlı (varsayılan 60 gün, `read_all_orders` onayıyla daha eski kayıtlar da erişilebilir). Trendyol için eski durum filtreli endpoint'in 30 günlük sınırı ve stream endpoint'inin artımlı tarih parametreleri sahte HTTP + PostgreSQL sözleşme testleriyle doğrulandı. Hepsiburada, Shopify ve Trendyol sağlayıcı sandbox/read-only davranışı bu turda çağrılmadı.
- Paket bazında uzak fatura gözlemlerini, 201 kısmi siparişle birebir eşleştir; uzaktan bulunamayanları ayrı inceleme durumuna al.
- Seçili ürün için Hepsiburada kategori/özellik eşlemelerini kullanıcıya açık eksik kurulum olarak göster; doğru kategori kararı olmadan otomatik eşleme yapma.
- Güncel başarılı yedek, dosya/anahtar kapsamı, off-host saklama ve restore drill kaydını temin et; otomatik yedek var/yok sonucunu yalnız sınırlı timer listesinden çıkarma.
- Liste gecikmesi ve dispatcher maliyeti için staging ölçümü yap; önerilen performans hedeflerini ölçüm sonucuyla kesinleştir.
- Mevcut ekranlara gerçek tarayıcıyla regresyon çalıştır; eski ekran görüntülerini bugünkü durumun tek kanıtı sayma.
- Sipariş seçimindeki eski “yalnız Shopify” şikâyeti canlı tarayıcıda tekrarlanmadı; yerelde hedefli `OrdersPage` etkileşim testi ekle/çalıştır ve platform filtresi, sayfalama, iptal sipariş ve toplu işlem kapsamını sabitle.
- Hepsiburada'nın daha önce aksiyon bekleyende kaldığı bildirilen belirli kaydını, sağlayıcıdan salt-okunur güncel durum ve `LastRemoteModifiedAt` ile yerel durum karşılaştırması üzerinden yeniden doğrula. Ortak durum eşlemesi düzeltmesi canlıya alınmadığı için mevcut üretim satırına etkisi kanıtlanmış değildir.

## 10. Kaynak haritası

Belirtilen yollar repository köküne göredir; satırlar inceleme SHA'sı içindir.

| Alan | Temel kaynaklar |
|---|---|
| Fatura ve iade senkronizasyonu | `src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs:2819`, `:2937`, `:2986`, `:6989` |
| Lease/deneme tüketimi | `src/MarketplaceHub.Infrastructure/Persistence/JobLeaseService.cs:31`, `:46` |
| Soru aktarım/gönderim | `src/MarketplaceHub.Infrastructure/Persistence/MarketplaceQuestionService.cs:98`, `:179`, `:260` |
| Soru HTTP sözleşmesi | `src/MarketplaceHub.Api/Marketplace/QuestionEndpoints.cs:55`, `:95`; `src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolQuestions.cs:59` |
| Idempotency / rol | `src/MarketplaceHub.Api/Catalog/IdempotencyMiddleware.cs:19`; `src/MarketplaceHub.Api/Security/RoleAuthorizationMiddleware.cs:46` |
| Eşleme / referans | `src/MarketplaceHub.Infrastructure/Persistence/ReferenceDataService.cs:33`; `MarketplaceJobProcessor.cs:1425`; `ProductPublicationComposer.cs:41`; `HepsiburadaProductPublicationComposer.cs:47` |
| Fatura workspace | `src/MarketplaceHub.Infrastructure/Persistence/InvoicingBillingService.cs:78`; `src/MarketplaceHub.Web/src/features/invoicing/pages.tsx:213`; `src/MarketplaceHub.Web/src/app/App.tsx:52` |
| Realtime / sağlık | `src/MarketplaceHub.Api/Realtime/OperationsRealtimeBroadcaster.cs:30`; `src/MarketplaceHub.Api/Operations/PostgresHealthCheck.cs` |
| CI / güvenlik | `.github/workflows/ci.yml`; `src/MarketplaceHub.Web/package.json`; `src/MarketplaceHub.Web/src/shared/security/sanitizeHtml.ts` |
| Yayın / yedek | `deploy/scripts/fast-deploy.sh`; `deploy/scripts/deploy.sh`; `deploy/compose/compose.production.yaml`; `deploy/backup/restore-drill.sh` |
| İade canonical durumları | `src/MarketplaceHub.Domain/ReturnModels.cs`; `MarketplaceSalesService.cs`; `MarketplaceJobProcessor.cs`; `ReturnLifecyclePolicyTests.cs` |
| Sipariş geçmişi sınırları | `src/MarketplaceHub.Application/SynchronizationPolicies.cs`; `src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs`; `src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolHttpClient.cs`; Shopify ve Hepsiburada HTTP client'ları |

## 11. Yerel çalışma ağacı için güncelleme

Bu bölüm ilk teşhis raporundaki üretim kanıtını değiştirmez. Aşağıdaki değişiklikler incelenen `25ceb0115dbebad5d4a3685223c3a076128bfb24` revizyonunun üstünde, yerel çalışma ağacında bulunan taslak kodlardır; commit edilmedi ve canlıya alınmadı. Dış sistemlere yazma yapılmadı.

| Bulgu | Yerel taslak değişikliği | Durum |
|---|---|---|
| B01–B02 | Fatura taraması kısmi iade siparişi kilidine takıldığında yalnız o adayı erteliyor ve cursor ilerliyor. Fatura paket/sipariş okuma hataları kayıt bazlı operasyon issue olarak tutuluyor; açık hata ilgili fatura satırı ve ayrıntısında gösteriliyor. | İki PostgreSQL entegrasyon testi geçiyor: kilitli kısmi siparişten sonra normal paket okunup cursor ilerliyor; açık okuma hatası workspace satırında görünüyor. Hatalı durumun üretim kayıtlarıyla uzlaştırılması ayrıca gerekli. |
| B03 | Geçmiş aktarımı tamamlandıktan sonra canlı soru taraması kalıcı status/kind/page cursor'ıyla iş başına en çok 6 sayfa ilerliyor. Trendyol sıfır tabanlı son tam sayfada fazladan sayfa açılması ve `totalPages` bilinmediğinde durma riskleri de politika düzeltmesiyle giderildi. | Politika ve PostgreSQL entegrasyon testi geçti. Geçmişi tamamlanmış Trendyol bağlantısında 10x50 kayıtlık iki durumun sayfaları iki çalışmaya yayıldı; ikinci çalışma ilk çalışmanın kaldığı sayfadan devam etti. |
| B04 | Timeout/iptal/ağ kesintisi sonrası durum `UNKNOWN` oluyor. Yinelenen cevap girişi POST yapmadan önce detay GET'iyle uzlaştırılıyor. Arka plan eşitlemesi kalıcı cursor'la parti başına en çok 10 `SUBMITTING`/`UNKNOWN` kaydı okuyor; başarılı detay okumaları arasında en az 5 dakika, okuma hatasından sonra da en az 5 dakika bekliyor. `ANSWERED` ve aynı satıcı metninin uzak konuşmada görünmesi kanıt kabul ediliyor; kanıt yoksa yeni POST engelleniyor. | 6 saf politika ve 3 PostgreSQL service testi geçti. Testler uzak eşleşme/eşleşmeme ve eski kayıt için detay okuması sırasında yeni cevap POST edilmediğini doğruluyor. |
| B05 | `Idempotency-Key` ve eski `X-Idempotency-Key` ortak politikada okunuyor; çelişen değer reddediliyor. | Politika testleri ile PostgreSQL üzerinden tenant kapsamı, replay ve belirsiz durum testleri geçti. |
| B06 | CI tam Vitest komutunu çağırıyor; workflow PostgreSQL 18.4 servisi başlatıp test bağlantısını veriyor; yerel format kontrolü temiz. | Yerel tam suite tüm testlerle geçti; workflow'un kendisi bu oturumda çalıştırılmadı. |
| B07 | Eksik ve mevcut kargo bilgili iadeler ayrı cursor'larla taranıyor, parti kapasitesi iki kümeye ayrılıyor. | Parti politikası ve PostgreSQL entegrasyon testi geçti; 30 eksik + 10 tam kargolu kayıtla iki turda her iki grubun da ilerlediği ve cursor'ların korunduğu doğrulandı. Restart sonrası ayrı test hâlâ gerekli. |
| B08 | Eşleme özeti ve her iki pazaryeri yayın composer'ı ortak zorunlu özellik politikasını kullanıyor; sayım yalnız güncel snapshot ile aynı snapshot'taki doğrulanmış eşlemeleri kabul ediyor. | Saf politika testi ve PostgreSQL entegrasyon testi geçti. Eski snapshot eşlemesi hem özette eksik sayıldı hem Trendyol yayın denetiminde reddedildi; aynı eşleme güncel snapshot'a taşınınca özet 0 eksik gösterdi ve yayın zorunlu özellik kontrolünü geçti. |
| B09 | Referans snapshot'ı değişince kategori, marka, kategori özelliği ve özellik değeri eşlemeleri yalnızca aynı aktif kimlik ve değişmemiş sözleşmeyle yeni snapshot'a yeniden bağlanıyor; local kayıt da etkin olmalı. Sözleşmesi değişen eşleme eski snapshot'ta kalıyor ve yayın/özet tarafından eksik görülüyor. | Politika testleri ve PostgreSQL entegrasyon testi geçti. Test; ilgisiz yeni kayıtla dört eşleme türünün taşınmasını, zorunlu özellik kuralı değişince otomatik taşınmanın reddedilmesini ve eksik sayacının 1 kalmasını doğruluyor. |
| B10 | Menü ve kontrol paneli sayacı `/invoice-workspace/summary` endpointini kullanıyor. Fatura ekranı `/invoice-workspace/page` ile arama, platform, kargo, sipariş/fatura/işlem durumu ve tarih filtrelerini; sabit sıralı sayfa numarası/sayfa boyutunu sunucuda uyguluyor. Aday sorgusu 2.000'lik anahtar-imleçli partilerle çalışıyor; yanıtta yalnız seçilen sayfanın satır, ürün görseli, belge ve delivery detayları var. Özet ve sayfa akışları, tüm adaylara ait fatura entity'si yerine gereken ID, sipariş/paket bağı, durum, numara, hata kodu ve oluşturma zamanını projekte ediyor. Eski sınırsız `GET /invoice-workspace` route'u kaldırıldı; fatura özet/işlem sonuçları ve realtime olayları yeni query anahtarlarını geçersiz kılıyor. | PostgreSQL filtre/sıralama, hata görünürlüğü, 10.000 aday testi ve 20 kayıtlık sayfada tek kayıtlı son sayfa/taşma testleri değişiklikten sonra geçti. 10.000 aday isteği 385 ms ölçüldü (tek yerel sentetik ölçüm). Önceki koddaki 100.000 sentetik, faturasız aday ölçümünde bir istek 992 ms sürdü; o koşudaki sayfa numarası sabit beklentisi testi başarısız etti. Mevcut kodla 100.000 aday tekrarı henüz alınmadı. Adaylar, filtre ve toplamlar bellek içinde olsa da yalnız 2.000'lik partiler saklanıyor; `EXPLAIN ANALYZE`, invoice içeren gerçekçi yük, indeks ve tekrarlı staging p95 ölçümü açık. |
| B11 | Realtime outbox dağıtıcısı artık PostgreSQL oturum advisory lock'u tutuyor; birden fazla API örneği aynı anda olay seçip yayınlamıyor. Başarılı yayımlanan teknik invalidation olayları 30 gün saklanıyor, 1.000 satırlık sınırlı partilerle siliniyor; başarısız ve yayımlanmamış olaylara dokunulmuyor. | Gerçek PostgreSQL testleri iki ayrı bağlantıda tek sahipli kilidi ve 1.000 kayıt sınırını, 30 günden yeni yayımlanmış ve yayımlanmamış kayıtların korunmasını doğruluyor (2 test geçti). Yeniden yayın, API çökmesi aralığı ve temizlik hacim gözlemi ayrıca izlenmeli. |
| B12 | Bağlantı detayında son deneme/başarı, API istek ve kayıt sayıları, kurtarma watermark yaşı ve bağlantı kuyruğu metrikleri gösteriliyor; sağlık eşiği beklenen interval + jitter süresi dolmadan gecikme üretmiyor. SyncCursor.LastCursorAdvancedAt gerçek opaque cursor veya ileri watermark değişince güncelleniyor. CursorStagnantSince, başarılı ve kayıt getiren turda checkpoint değişmezse başlıyor; boş başarılı tur bunu temizliyor. API son ilerleme, durgunluk başlangıcı ve UNKNOWN / NODATA / CURRENT / STALLED durumunu yayımlıyor; bağlantı ayar penceresi bunları gösteriyor. Durgunluk eşiği en az 3 planlı tur ve varsayılan 15 dakikadan uzun olanı kullanıyor. | Yeni EF migration iki nullable zaman alanı ekliyor; politika testleri geçti. B12 kartında `İmleç durgun` durumu ve ekran okuyucu alarmı, ayar penceresinde zaman damgalı açıklama gösteriliyor; yeni frontend testleri 6/6, canlı yayın suite'i web 222/222 ve backend 776/776 geçti. Sağlıklı canlı akışlar izlendi; üretimde akış bilerek bloke edilmedi. Gerçek bloke akışla browser uçtan uca kanıtı hâlâ yok, bu yüzden B12 kısmen açık. |
| B13 | Hızlı yayın akışı migration'dan önce izole PostgreSQL üzerinde backend suite'i çalıştırıyor; web image build'i typecheck, Vitest, production build ve bundle bütçesini şart koşuyor. Hızlı ve immutable dağıtım yolları migration öncesi yedek alıp manifest/dump/özel dosya arşivini, checksum'ı ve `pg_restore --list` sonucunu doğruluyor. Hızlı akışta runtime doğrulaması varsayılan açık. Production dış yazma anahtarı env dosyasından kapatılabiliyor ve dağıtım betikleri değeri doğruluyor. | Restore drill başarılı: `20261007T095508Z` yedeği, `marketplacehub-app@sha256:e1dd5e5420949f66bfdd05a3191e6bb51628e9f7b172767fb1744e9ddb81e795` image'ı; 274 sn, 3 şema, 47 migration, 1 tenant, API ve worker sağlıklı. Bu hostta Docker/Compose CLI ve ShellCheck yok; birleştirilmiş Compose config, yerel image build ve ShellCheck doğrulanamadı. Off-host şifreli kopya ve image SHA'sının CI sonucuyla bağlanması açık. |
| B14 | `/api/v1/questions` ve `/api/v1/question-templates` mutasyonları `OPERATIONS` rolünü de kabul ediyor; erişim kümesi `OWNER/ADMINISTRATOR/OPERATIONS` ile sınırlı. | Middleware rol testleri 7/7 geçti; soru yanıtı, aktarım ve şablon oluşturma izinli; muhasebe ve salt-okunur mutasyonları 403. |
| B15 | DOMPurify doğrudan bağımlılık alt sınırı `^3.4.16` olarak güncellendi; lockfile ve kurulu sürüm 3.4.16. `source-map-js` kurulu 1.2.2. DOMPurify üretici/GitHub danışmaları 3.4.15 ve öncesini etkilenmiş, 3.4.16'yı düzeltilmiş gösteriyor; `source-map-js` GitHub bildirimi `<1.2.2` sürümlerini etkilenmiş, 1.2.2'yi düzeltilmiş gösteriyor. Uygulama `IN_PLACE` sanitization kullanmıyor. | `npm run test:security`: 2/2 geçti; `npm ls` gerçek ağaçta DOMPurify 3.4.16 ve source-map-js 1.2.2 gösterdi. 7 Ekim `npm audit --omit=dev --audit-level=high` bulk endpoint erişim hatası nedeniyle sonuç üretemedi; NuGet advisory taraması da açık. Bilinen bildirimlere karşı yerel sürümler düzeltilmiş; tam dependency audit kapanmadı. |
| B16 | Kaynak kontrollerinde sipariş bilgisi başlığı, iade ürünlerinde 2:3 çerçeve, Hepsiburada alfasayısal barkod başındaki yapay sıfırların gösterilmemesi, onaylı iade sıralama seçenekleri, yalnız aksiyon bekleyenlerde geri sayım ve yalnız reddedilenlerde etiket görünürlüğü doğrulandı. Adet rozeti görsel çerçevesinin dışına taşmayacak şekilde içeri alındı. Ürün seçim başlığı/fatura seçim kutusu hizaları, soru ekranı uyarısının kaldırılması ve iş izleme soru işi Türkçe açıklaması da kaynakta mevcut. | İlgili saf politika ve bileşen testleri ile tam frontend suite geçti; typecheck/build/bundle kontrolü de geçti. Canlı iade ekranı 1270×712 tarayıcı görünümünde incelendi; sekmelerde yatay taşma çubuğu var ve içerik/filtreler bu görünümde okunuyor. 1299, 1537/1543 ve dar viewport matrisi ile eski yorumlardaki sıkışma/etiket yerleşimi regresyonu henüz doğrulanmadı; B16 kısmen açık. |
| B17 | `MarketplaceSalesService` anlık read-back'i ile `MarketplaceJobProcessor` arka plan taraması ortak `MarketplaceReturnStatus.Canonicalize` eşlemesini kullanıyor. Hepsiburada `NewRequest`, `AwaitingAction`, `InDispute`, `Accepted`, `Rejected`, `Refunded`, `Cancelled` durumları ve ayraçlı alias'lar tek yerde tanımlı. | `ReturnLifecyclePolicyTests`: 35/35 geçti. Yeni PostgreSQL testi sahte Hepsiburada `Accepted` yanıtından sonra yerel kaydın `Approved` olduğunu, onaylı listede yer aldığını ve aksiyon bekleyen filtresinden çıktığını doğruluyor. Güncel tam backend turu 765 geçti, 0 atlandı. Canlı arayüzde `#4146480888` siparişi Onaylanan'da listelendi; arama sonucu Aksiyon Bekleyen'de görünmüyor. B17 doğrulandı. |
| B18 | `TrendyolOrderHistoryPolicy` eklendi. İlk senkronizasyon 3 aylık stream sınırını, eski durum-filtreli endpoint 30 günlük üst sınırı kullanıyor; önceki 6 aylık `Delivered/Awaiting` hesabı kaldırıldı. | İki gerçek adaptör sözleşme testi sahte HTTP yanıtları ve PostgreSQL bağlantı/credential akışıyla geçti: eski endpoint 13 günlük istek pencereleriyle 30 günlük tabanda duruyor; stream isteği artımlı tarihleri kullanıp eski `startDate/endDate/status` parametrelerini eklemiyor. Tam backend 765 geçti, 0 atlandı; format kontrolü temiz. Canlı/sandbox okuması ve sağlayıcıdaki gerçek tarih sınırlarının read-only teyidi açık. |

### Taslak değişikliklerin doğrulaması

10.000 adaylık B10 stres testi, B11 outbox, B12 bağlantı kuyruğu/cursor telemetrisi, B14 rol testleri, B17 iade durumu listesi ve B18 adaptör sözleşme testleriyle önceki tam backend suite 765 geçti, 0 atlandı. Cursor durgunluğu eklemesinden sonraki son DB'siz koşuda 742 geçti, 31 PostgreSQL testi atlandı, 0 başarısız oldu; yeni PostgreSQL entegrasyon testleri bu nedenle henüz doğrulanmadı. Önceki tam koşu geçici yerel PostgreSQL 18 UTF-8 kümesini kullandı; küme testten sonra kapatılıp silindi. B13 Docker/Compose dağıtım kapıları Docker CLI olmadığından image/Compose çalıştırmasıyla doğrulanmadı.

| Kontrol | Sonuç | Kalan sınır |
|---|---|---|
| `dotnet build MarketplaceHub.sln --no-restore --configuration Release` | Başarılı; 0 uyarı, 0 hata | — |
| `dotnet test` (son DB'siz koşu) | 742 geçti, 31 atlandı, 0 başarısız; önceki tam izole PG koşusu 765/765 | Yeni cursor persistence/service testleri ayrılmış test bağlantısı olmadığı için atlandı; production worker kapsam dışı |
| `dotnet format MarketplaceHub.sln --verify-no-changes --no-restore --verbosity quiet` | Başarılı | — |
| `npm run typecheck` | Başarılı | — |
| `npm test` | 54 dosyada 209 test geçti | Tarayıcı uçtan uca testi değil |
| `npm run build` | Başarılı | Gerçek ekran yerleşimini kanıtlamaz |
| `npm run check:bundle` | Başarılı; ana JS 172,6 KiB, ana CSS 632,6 KiB | CSS bütçesine yaklaşık 17,4 KiB kaldı |
| `dotnet-ef migrations has-pending-model-changes` | Başarılı; son migration'dan beri model değişikliği yok | EF aracı geçici yerel CLI dizininde restore edilip işlem sonunda temizlendi |
| `git diff --check` | Başarılı | — |
| İade durum haritalama hedefli suite | `ReturnLifecyclePolicyTests`: 35 geçti | Saf durum normalleştirmesi; sağlayıcıya bağlanmaz |
| Önceki DB'siz backend suite | 730 geçti, 27 atlandı, 0 başarısız | Bu tarihsel koşuda `MARKETPLACEHUB_TEST_CONNECTION` ayarlı değildi; PostgreSQL entegrasyon testleri çalışmadı. Güncel tam koşu üst satırda. |
| `npm audit --audit-level=low` | Bu tekrar çalıştırmada başarısız: npm advisory endpoint erişim hatası | Temiz audit sonucu olarak kabul edilmemeli; bağlantı geldiğinde yeniden çalıştırılmalı. `source-map-js@1.2.2` yalnızca `jsdom` ve `vite` geliştirme ağaçlarında |

Bu doğrulama seti artık fatura kilit ilerlemesi, fatura hata görünürlüğü, soru cevabı uzlaştırması, Trendyol soru sayfa cursor'ının çalışma aralarında sürmesi, tenant idempotency, iade cursor adaleti ve PostgreSQL kalıcılığı testlerini kapsıyor. İade cursor süreç yeniden başlatma, snapshot eşleme tutarlılığı ve gerçek sağlayıcı yanıtları için ayrı test/çalışma kanıtı hâlâ gerekli. Geçici PostgreSQL kümesi test sonunda durdurulup silindi; kurulu PostgreSQL servisine dokunulmadı. Üretim verisi değişmedi ve deploy yapılmadı. Kullanıcının dış yazma yapılmaması şartı gereği bu taslaklar commit/push/deploy adımlarına geçirilmedi; üretim Compose ayarında dış yazmalar etkin olduğundan canlı worker dağıtımı dış etki oluşturabilir.

## 12. Son uygulama ve canlıya alma doğrulaması

Bu bölüm 7 Ekim 2026'daki son uygulama durumunu kaydeder ve yukarıdaki dağıtım öncesi “yerel taslak” notlarının yerine geçer.

| Kontrol | Sonuç |
|---|---|
| GitHub CI | `37602730610` başarılı; build/test, format, web test/build ve npm/NuGet güvenlik kontrolleri geçti |
| Tam backend doğrulaması | Üretim fast-deploy kapısındaki izole PostgreSQL koşusu: 773 geçti, 0 atlandı, 0 başarısız |
| Dağıtım güvenlik betikleri | Güncel `fast-deploy.sh` ikinci çalıştırmada korumalı env dosyasını hatasız okudu; izin hatası/fallback yok |
| Yedek | `20261007T095508Z` üretim veritabanı dökümü ve özel volume arşivi oluşturuldu; iki SHA-256 checksum da doğrulandı |
| Migration ve servisler | Migration tamamlandı; API, worker, Caddy ve PostgreSQL `running/healthy` |
| Canlı readiness | `https://panel.ravencia.com/health/ready`: HTTP 200, `Healthy` |
| Uygulama revizyonu | `1ebd7499` üretimde çalışıyor; rapor güncellemesi kodu değiştirmez. Bu takipte canlı panel açıldı; dış DNS/health URL kontrolü masaüstü kabuğundan istemci tarafından engellendi |
| Dış sistem etkisi | Pazaryerlerine yazma veya fatura/iade/yanıt işlemi yapılmadı; mevcut üretim `FeatureFlags__ExternalWrites=true` ayarı değişmedi |

**Açık kalanlar kodun deploy edilmesini engelleyen maddeler değildir; tamamlanma kanıtı için ortam/sağlayıcı erişimi gerektirir:**

- **B10:** Önceki 2.000 kayıtlık partili ölçüm nearest-rank p95 6.210 ms idi. 10.000 kayıtlık parti ve sayfa dışı snapshot yükünü azaltan değişiklik sonrası aynı 100.000 faturasız aday testinde beş örneğin p95'i 3.037 ms'ye indi (1.810, 1.864, 1.992, 2.238, 3.037 ms). Gerçek faturalar içeren staging yükündeki p95 ve hedef gecikme ayrıca kararlaştırılmalı.
- **B11:** İzole PostgreSQL'de dispatcher lease, tekrar yayın ve bounded retention testleri geçti; web event-ID dedup regresyonu 5/5 geçti. 7 Ekim canlı gözleminde outbox yaklaşık 540.543 tahmini canlı satır ve 370 MB toplam alan kullandı; yayımlanmamış kayıt yoktu. API retention logu 237 eski kaydın silindiğini gösterdi; sonraki salt-okunur sayımda 198 yayımlanmış kayıt 30 günlük eşiği geçmişti. Tabloda 86.489 tahmini ölü tuple vardı; 7 günlük oluşturma hacmi 1.507–71.167/gün arasında değişti. Bu, retention'ın çalıştığını doğrular ama büyüme eğrisini veya silme sonrası disk geri kazanımını kanıtlamaz.
- **B12:** `STALLED` uyarısı bağlantı kartı ve ayar penceresine eklendi, test edildi ve canlıya alındı. Normal Trendyol worker ilerlemesi de doğrulandı. Gerçek bloke iş koşulunda browser uçtan uca teyidi hâlâ açık; üretimde iş bilerek bloke edilmedi.
- **B13:** Şifreli off-host yedek aktarımı ile deploy image SHA'sının CI kaydıyla bağlanması. İzole geri yükleme tatbikatı başarıyla tamamlandı; dış yedek hedefi tanımlı değil.
- **B16:** 1299, 1537/1543 ve dar viewportlarda görsel regresyon; özellikle eski yorumlardaki sıkışma ve etiket yerleşimleri. 1270×712 tek canlı görünüm incelendi.
- **B18:** Trendyol tarih penceresi politikasının sağlayıcı sandbox'ında veya salt-okunur gerçek bağlantıda teyidi. Adaptör sözleşme testleri geçti; sağlayıcıya canlı istek gönderilmedi.

Önceki raporda açık yazılan npm/NuGet audit ve tam backend/PostgreSQL testi bu son CI/deploy koşularıyla kapatıldı. B17 canlı kayıt ekranında doğrulandı; B13 restore drill'i tamamlandı. Uygulama kodu üretimde çalışıyor, ancak B10, B11, B12, B13, B16 ve B18 için kalan ortam/ölçüm/sağlayıcı kanıtları nedeniyle planın operasyonel doğrulama durumu **kısmen açık**.

## 13. 7 Ekim B10 kök neden düzeltmesi ve canlı doğrulaması

100.000 aday üzerinde çalışan `WorkspacePageAsync` ölçümünde her 2.000 satırlık tur kaynak paketleri `TenantId, StatusOccurredAt, Id` anahtarına göre sıralıyordu; veritabanında bu erişim ve sıralama biçimine uygun indeks yoktu. Aynı test indeksi ekleyince sayfa isteği yaklaşık 48,8 saniyeden 3,7 saniyeye indi. Yeni test beş istek örneği alıyor, nearest-rank p95'i raporluyor ve `EXPLAIN (ANALYZE, BUFFERS)` planında yeni indeksin kullanıldığını doğruluyor.

| Kontrol | Sonuç |
|---|---|
| Kaynak revizyonu | İndeks/migration commit'i `ba80b9ffb39d`; güncel `origin/main` ve sunucu çalışma ağacı `204e56db9694` |
| Migration | `20261007104323_AddInvoiceWorkspaceKeysetIndex`; `CREATE INDEX CONCURRENTLY` ile `sales.shipment_packages(TenantId, StatusOccurredAt, Id)` eklendi |
| Doğrulama | İzole, dış bağlantısız PostgreSQL üzerinde son tam suite 773/773 geçti. 100.000 aday testi 5/5 istekte 20 satır ve doğru sayfa toplamlarını döndürdü; örnekler 2.327, 2.531, 2.578, 2.626 ve 6.210 ms (median 2.578 ms, nearest-rank p95 6.210 ms). |
| Sorgu planı | `EXPLAIN (ANALYZE, BUFFERS)` 2.000 kayıtlık anahtar-imleç okumasında yeni indekste `Index Only Scan Backward` kullandı; 18 shared buffer hit, 0 heap fetch ve 0,493 ms execution time ölçüldü. Bu test, servis sorgusunun joins/filtreleri dâhil toplam gecikme ölçümü değildir. |
| Yedek ve şema uygulaması | `20261007T105109Z` yedeğinin iki SHA-256 özeti ve `pg_restore --list` doğrulandı. Migration ayrı container'da çalıştırıldı; production API/worker yeniden başlatılmadı. |
| Canlı durum | İndeks PostgreSQL kataloğunda görünüyor. API ve worker sağlıklı, readiness `HTTP 200`. Migration container'ında dış yazmalar kapalıydı; pazaryeri isteği veya dış yazma yapılmadı. |
| Kalan B10 kapsamı | Tek indeksli tablo okuması hızlı; uçtan uca sayfa çağrısı örneklerinde 6,2 sn p95 gözlendi. Test hostunda canlı worker çalışırken CPU ve gerçek veri dağılımı sonucu etkileyebilir. Gerçek invoice/delivery karması, staging p95 ve ürün için kabul edilecek gecikme hedefi hâlâ açık. |

Dolayısıyla B10'daki yavaş sıralama erişiminin kök nedeni giderilip canlı şemaya alınmıştır; 100.000 kayıt için uygulama düzeyi p95 hâlâ hedefin altında değildir ve veri karışımı/staging doğrulaması açık kalır. Tüm proje planı tamamlanmış değildir. B11 çökme/yeniden-yayın senaryosu, B12 bloke worker uyarısının uçtan uca görünmesi, B13 onaylı off-host yedek hedefi ve CI image bağı, B16 çoklu viewport regresyonu ile B18 sağlayıcı sandbox/read-only teyidi açık kalır. Bunlar ilgili ortam, onaylı yedek hedefi veya sağlayıcı erişimi olmadan güvenli biçimde kapatılamaz.

## 14. 7 Ekim B10 takip ölçümü — commit `7f73f839`

Fatura çalışma alanının aday okuma partisi 2.000'den 10.000'e çıkarıldı. Boş arama metni normalize edilerek müşteri JSON'u tüm tarama boyunca okunmuyor; müşteri/adres snapshotları sadece döndürülecek sayfa için yükleniyor. Seçilen sayfanın müşteri adı, adresleri ve müşteri adı araması için PostgreSQL regresyon kontrolleri eklendi.

| Kontrol | Sonuç |
|---|---|
| Üretim sürümü | API ve worker `marketplacehub-app:manual-7f73f8392cf4` kullanıyor; API, worker, Caddy ve PostgreSQL sağlıklı; readiness HTTP 200 |
| Dış yazma ayarı | Canlı API ve worker `FeatureFlags__ExternalWrites=true`; bu, canlı operasyon ayarıdır |
| İzole ölçüm | 100.000 faturasız aday, `DUE_SOON`, 20 kayıtlık sayfa; beş tekrar 1.810, 1.864, 1.992, 2.238 ve 3.037 ms; median 1.992 ms, nearest-rank p95 3.037 ms |
| Test güvenliği | Tekrarlı yük testi dış bağlantısız doğrulama Docker ağında `ExternalWrites=false` ve ayrı PostgreSQL ile geçti (1/1). Test herhangi bir pazaryeri isteği/yazması göndermedi; geçici PostgreSQL konteyneri kaldırıldı |
| Erişim planı | `EXPLAIN (ANALYZE, BUFFERS)` anahtar-imleçli örnek sorguda `IX_shipment_packages_TenantId_StatusOccurredAt_Id` indeksini kullandı; 2.000 satırda 0,623 ms SQL yürütme görüldü. Bu, join/filtre/fatura okuma maliyetinin tamamını ölçen bir servis planı değildir |
| Açık ölçüm | 3.037 ms p95 önceki 6.210 ms'den iyidir, ancak 1 saniyelik başlangıç hedefini karşılamaz. Test fixture'ı faturasız ve tek platformlu; gerçek fatura/delivery dağılımı, farklı tenant büyüklükleri ve staging donanımında tekrarlı p95 ölçümü hâlâ gereklidir |

B10 bu nedenle çözülmüş sayılmıyor. Sadece indeks erişim yolu düzeltildi; uygulama düzeyinde 100.000 kayıt performansı ve hedef gecikme kararını kapatmak için daha gerçekçi yük ve staging ölçümü gerekiyor.

## 15. 7 Ekim canlı B11/B12 takibi ve B10 yeniden ölçümü

### B11 — canlı outbox ve retention gözlemi

Canlı PostgreSQL sorguları `BEGIN READ ONLY` transaction'ında çalıştırıldı ve `ROLLBACK` ile kapatıldı. Pazaryeri çağrısı veya veri değiştiren SQL çalıştırılmadı.

| Kontrol | Sonuç |
|---|---|
| Satır ve alan tahmini | `pg_stat_user_tables` yaklaşık 538.809 canlı, 86.489 ölü tuple; outbox 233 MB heap + 137 MB indeks = 370 MB |
| Bekleyen yayın | `PublishedAt IS NULL` kayıt sayısı 0 |
| Retention | API logunda 13:39 UTC'de süresi dolmuş 237 yayımlanmış kaydın silindiği görüldü. Sonraki salt-okunur sorguda 30 günden eski 198 kayıt kaldı; eşik civarındaki kayıtların zamanla süresi dolmaya devam ettiği için tek sayım retention'ın tüm hacmi temizlediği anlamına gelmez |
| Hacim dağılımı | Son 8 `CreatedAt` gününde 1.507–71.167 kayıt/gün; 32 günlük yayımlanma dağılımında bazı günlerde 94.187 ve 71.167 kayıt görüldü |
| Sınır | Tek anlık örnek büyüme hızını, uzun dönem alan eğrisini veya dead tuple temizliği sonrası dosya boyutu geri kazanımını kanıtlamıyor. B11 kodu çalışıyor; izleme/etki kanıtı açık |

### B12 — kullanıcıya görünür durgunluk alarmı

`STALLED` durumu, senkronizasyon kartında kırmızı sağlık durumu ve erişilebilir alarm metniyle; ayar penceresinde durgunluk başlangıcı ve son cursor ilerleme zamanıyla gösteriliyor. Kapalı/boş veri akışlarında yanlış alarm üretilmiyor. Politika/bileşen testleri 6/6 geçti. Commit `7f8a935f` canlıya alındı; yayın kapısında frontend 222/222 ve backend 776/776 geçti. Üretimde API/worker `FeatureFlags__ExternalWrites=true`, tüm servisler sağlıklı ve readiness HTTP 200. Üretim işini bilerek bloke etmediğimiz için gerçek bloke worker akışında browser uçtan uca kanıtı ayrıca gerekli.

### B10 — 100.000 aday performans tekrarları

Aynı izole, iç ağı dışa kapalı PostgreSQL benchmark'ı güncel 10.000 batch ile ve yalnız deneysel geçici worktree'de 25.000 batch ile çalıştırıldı. Her koşu doğru 20 satır ve 100.000 toplam sayıyı döndürdü; geçici veritabanları ve deney worktree'si test sonunda kaldırıldı.

| Aday batch boyutu | 5 istek süresi (ms) | nearest-rank p95 |
|---|---|---|
| 10.000 (canlı sürüm) | 2.291, 2.832, 2.879, 3.222, 3.447 | 3.447 ms |
| 25.000 (yalnız deney) | 2.229, 2.376, 2.491, 2.979, 4.242 | 4.242 ms |

25.000 batch p95'i düşürmedi ve en yavaş örnek daha yüksek çıktı; bu nedenle canlı 10.000 ayarı korunuyor. Her iki ölçüm tek tenantlı, faturasız sentetik fixture'dır. 1 saniye başlangıç hedefi karşılanmıyor; karışık fatura/delivery verisiyle staging ölçümü ve ürün için kabul edilecek gecikme hedefi açık kalıyor.

**Proje planı hâlâ kısmen açık.** B10 ölçüm/hedef, B11 uzun dönem hacim etkisi, B12 bloke işte uçtan uca alarm, B13 off-host yedek ve CI-image bağı, B16 çoklu viewport görsel regresyonu ve B18 sağlayıcı sözleşmesi için ortam kanıtı henüz tamamlanmadı.

## 16. B10 100.000 aday tekrar ölçümü ve test izolasyonu

7 Ekim'de hedefli `InvoiceWorkspacePage_ProcessesOneHundredThousandCandidatesAndReturnsOnlyOnePage` PostgreSQL testi tekrar çalıştırıldı. Test yalnız `validation` profiline ait izole PostgreSQL konteynerini kullandı; Docker `validation` ağı `internal: true`, test sürecinin `FeatureFlags__ExternalWrites` değeri `false` idi. Test fixture'ı 100.000 sentetik kaydı doğrulama veritabanına ekleyip temizledi. Pazaryeri/provider HTTP çağrısı veya dış yazma yapılmadı.

| Ölçüm | Sonuç |
|---|---|
| Doğruluk | 1/1 test geçti; 100.000 toplam, 20 satır dönen sayfa ve 5.000 sayfa sayısı doğrulandı |
| Uygulama isteği | 2.212, 2.314, 2.320, 2.607 ve 3.873 ms; median 2.320 ms, nearest-rank p95 3.873 ms |
| Aday tarama SQL'i | EF komut günlüğünde 10.000 satırlı aday sorgularının örnekleri 116–641 ms aralığındaydı; toplam sürede asıl maliyet basit indeksli sıralama değil, `JOIN`/filtre/projeksiyonlu aday taraması olarak görünüyor |
| Basit indeks planı | Aynı fixture'da `EXPLAIN (ANALYZE, BUFFERS)` 2.000 satırlı yalın keyset okumasında indeksi kullandı ve 0,879 ms ölçtü. Bu plan, servis sorgusundaki joins ve filtreleri kapsamaz; yeni eklenen satırlar nedeniyle 2.000 heap fetch görüldü |

B10'un indeks kök nedeni giderilmiş olsa da uçtan uca p95 hedefi karşılanmıyor. Sonraki teknik adım, gerçek servis sorgusunun `EXPLAIN (ANALYZE, BUFFERS)` planını ve `JOIN`/filtre sürelerini tenant veri dağılımına yakın bir staging fixture'ında ayrıştırıp pahalı adayı erken eleyen sorgu biçimini belirlemektir. 10.000 batch ayarı şimdilik korunuyor; bu tek sentetik tekrar ayar değişikliğini desteklemiyor.

## 17. 7 Ekim B10 dar projeksiyon ve karışık fixture takibi

Fatura workspace aday taraması platform bağlantısı join'ini kaldırıp etkin bağlantı kimliklerini önceden okuyor; aday projeksiyonu daraltıldı ve tam sipariş/paket ayrıntısı yalnız dönecek sayfa için yükleniyor. `Unknown` pazaryeri fatura durumunda, etiketin doğru hesaplanması için müşteri snapshot'ı korunuyor. Bu son değişikliklerden sonra izole benchmark'ta 10.000 batch p95'i 1.997 ms, yalnız deneysel 100.000 batch p95'i 1.693 ms ölçüldü. 100.000 batch deneyi 768 MB bellek ve 1,5 CPU sınırı altında geçti; ancak test verisi tek tenant, tek platform, faturasız ve `NotInvoiced` paketlerden oluşuyordu. Bu sonuçlar 1 saniye hedefini karşılamıyor.

Üretim PostgreSQL istatistikleri salt okunur incelendi: paket durumlarının yaklaşık %11,47'si `Unknown`, `orders.CustomerSnapshotJson` ortalama genişliği 274 bayt olarak tahmin edildi. Bu dağılıma yaklaşmak için 100.000 aday test fixture'ı yaklaşık %11,46 `Unknown` paket ve 274 baytlık sentetik snapshot oluşturacak şekilde güncellendi. Yerel doğrulamada Release derlemesi 0 uyarı/0 hatayla tamamlandı; dış yazma kapalı ve PostgreSQL bağlantısı tanımsız backend koşusu 743 geçti, 34 PostgreSQL testi atlandı. Biçim denetimi ve `git diff --check` temiz. Makinede Docker/PostgreSQL test sunucusu olmadığından güncellenmiş karışık fixture çalıştırılmadı; uzak doğrulama için kaynak kod kopyalama otomatik güvenlik incelemesinden onay almadı. Bu nedenle 100.000 batch ayarı seçilmedi ve canlıya alınmadı.

**B10 açık:** 10.000 batch korunuyor. Sonraki ölçüm karışık durum fixture'ıyla, yalnız dış ağa kapalı geçici doğrulama PostgreSQL'inde yapılmalı; 1 saniye hedefi ve bellek sınırı doğrulanmadan batch ayarı değiştirilmemeli veya performans düzeltmesi tamamlandı sayılmamalı.

## 18. 7 Ekim B16 ürün seçme kutusu hizası ve yerel doğrulama

Ürün kataloğunda başlıktaki “tümünü seç” kutusu satır kutularından sola kayıyordu. Grid sütunu 44 px iken gereken offset 1,5 px; sütun 34 px ve kompakt başlık dolgusu 4 px iken 4,5 px; sütun 34 px kalıp başlık dolgusu 12 px olduğunda -3,5 px. İlk iki düzen container genişliğine, üçüncü düzen 1201–1400 px pencere kuralına bağlı. Başlık kutusu bu kombinasyonlara göre konumlanıyor; çakışan eski `margin-left` kuralları kaldırıldı.

Bu değişiklikten sonraki frontend doğrulaması: `npm test -- --reporter=dot` 57/57 dosya ve 222/222 test geçti; `npm run typecheck`, `npm run build` ve `npm run check:bundle` başarılı. Derlenen CSS 633,9/650 KiB bütçede. Yerel tarayıcıda uygulamanın stylesheet'ini yükleyen minimal fixture'ı 390, 1299, 1537 ve 1600 px iframe viewportlarında ölçtüm: her genişlikte başlık ve satır kutularının x farkı 0 px. Katalog genişlikleri sırasıyla 309, 946, 1184 ve 1247 px; kural geçişi 1537 ile 1600 px arasındaki örneklerde de doğru. Ek 1280 px pencere/1494 px genişletilmiş içerik kombinasyonunda da iki kutu x=62,5 px oldu. İlk sandboxlı test denemesinde Windows geçici dosya erişimi yedi test dosyasını yükleyemedi; testler yerel test çalıştırıcısında tekrarlandığında tamamı geçti. Pazaryeri veya canlı veritabanına istek ya da yazma yapılmadı. Değişen CSS canlıda dağıtılmadı; B16'nın diğer ekran/yerleşim kabul kriterleri hâlâ açık.

## 19. B18 Trendyol tarih sınırı kaynak teyidi

7 Ekim'de Trendyol'un resmî sipariş [changelog'u](https://developers.trendyol.com/v3.0/changelog/changelog) yeniden kontrol edildi. 5 Mart 2026 duyurusu `getShipmentPackages` için geçmişe dönük erişimi en fazla 1 ay/30 gün olarak belirtiyor. Resmî [sipariş paketi dokümanı](https://developers.trendyol.com/tr/docs/sipari%C5%9F-paketlerini-%C3%A7ekme-getshipmentpackages) tarih parametreleriyle sorguda en fazla iki haftalık aralık tarif ediyor. Kodun durum filtreli `/v2/orders` yolu 30 günlük tabanı koruyor ve istekleri 13 günlük aralıklara ayırıyor; böylece toplam geçmiş sınırını ve dokümante edilen iki haftalık aralık tavanını aşmıyor.

`TrendyolOrderHistoryPolicyTests` yerel çalıştırmada 2/2 geçti. Bu saf politika testi dış API veya veritabanı kullanmıyor. Testhost'un yerel loopback soketi sandbox tarafından engellendiği için test yalnızca localhost test iletişimine izin veren komutla çalıştırıldı; pazaryeri uç noktasına istek gönderilmedi. B18'in tarih politikası ve dokümantasyon kanıtı doğrulandı. Sağlayıcı sandbox'ında gerçek yanıt davranışı hâlâ ayrıca gözlemlenmedi; bu çalışma için gerekli olmayan bu doğrulama açık kalıyor.

## 20. 7 Ekim yerel backend regresyonu ve B10 test sınırı

Güncel çalışma ağacıyla backend test projesi derlendi. `FeatureFlags__ExternalWrites=false` yalnızca test sürecine verildi ve `MARKETPLACEHUB_TEST_CONNECTION` kaldırıldı. Tam yerel backend suite'i 743 test geçti, 35 PostgreSQL testi atlandı, 0 test başarısız oldu. Böylece değişen fatura servisi ve test kodunun derlemesi ile veritabanından bağımsız regresyonlar doğrulandı; B10'un PostgreSQL sorgu/performans davranışı doğrulanmadı.

Bu makinede `docker`, `podman`, `psql`, `postgres` ve `pg_ctl` komutları bulunmuyor; `MARKETPLACEHUB_TEST_CONNECTION` ayarlanmamış ve localhost 5432'de dinleyici yok. Bu nedenle karışık `Unknown` durumlu 100.000 kayıt fixture'ı, ayrı bir dışa kapalı PostgreSQL test kümesi sağlanana kadar bekliyor. Canlı veritabanına bağlanılmadı, canlı veri değiştirilmedi ve pazaryerine yazma yapılmadı. B10 performans değişikliği bu kanıt alınmadan dağıtılmamalı.

## 21. Trendyol varyant renk adlarını güvenli düzeltme

İstenen katalog düzeltmesi, `MZ005S26` gibi varyantlarda `Renk=İndigo` iken `Web Color=Lacivert` değerinin varyant adına taşınmamasıdır. Trendyol mapper'ı aynı `Renk` adının hem serbest değer hem `attributeValueId` ile gelen sınıflandırılmış değer olarak tekrarlandığı yanıtta ID'siz gerçek rengi seçiyor. Mevcut normal ürün aktarımının tüm ürün alanlarını yenileyebilmesi nedeniyle, canlı katalogdaki adları düzeltmek için ayrı **Yalnızca varyant seçeneklerini düzelt** modu eklendi.

Bu mod yalnızca Trendyol bağlantısında sunulur; katalog taraması pazaryerinden salt-okunur ürün verisi alır. Yerelde yalnızca zaten eşleşmiş ürünlerin tüm varyantları güvenle eşleştiğinde `ProductVariant.OptionSignature` ve renk/beden seçenek atamalarını yeniler. Başlık, açıklama, marka, kategori, fiyat, stok, medya ve pazaryerindeki ürün içeriği bu akışta değişmez. Herhangi bir varyant eşleşmiyorsa ürün için kısmi düzeltme yapılmaz. PostgreSQL entegrasyon testi bu davranışı ve sağlayıcı yazma çağrısı olmadığını doğrulayacak şekilde eklendi.

İlk sürüm doğrulaması: frontend testleri 224/224 geçti; typecheck, üretim derlemesi ve bundle bütçesi başarılı (CSS 633,9/650 KiB). Backend derlemesi 0 uyarı/0 hatayla tamamlandı; yerel, bağlantısız suite 743 geçti ve 35 PostgreSQL testi atlandı. Mapper odaklı testler 20/20 geçti. GitHub Validate #808 iş akışı tüm backend testlerini ayrı PostgreSQL 18 hizmetinde çalıştırarak başarıyla tamamlandı; seçenek düzeltme entegrasyon testi de bu suite'e dahildi. Yerel ve CI testlerinde `FeatureFlags__ExternalWrites=false`; entegrasyon testi pazaryeri yazma çağrılarını yasaklıyor. Bu paragraf `0608e65f` ilk sürümünün durumunu kaydeder; canlıya alım ve katalog eşitlemesinin güncel durumu aşağıdaki takip kaydında tamamlandı olarak işaretlenmiştir.

**Durum:** İlk yarıda kalan 457 yerel güncelleme korunarak aktarım tekrar çalıştırıldı. 512 karakter sınırını gözeten takip düzeltmesi canlıda; ikinci aktarım 941 ürünü başarıyla güncelledi, 7 kaydı atladı ve hata üretmedi. `MZ005S26` için panelde `XL / İndigo` doğrulandı. Aktarım yalnızca Trendyol'dan okudu ve yerel varyant seçeneklerini değiştirdi; uzak ürünlerde dış yazma yapılmadı. İlk başarısız denemenin tarihsel kaydı ve 7 atlanan ürün ayrıca izlenebilir.

## 22. Test ortamlarında dış yazmayı açıkça kapatma

CI backend test adımına ve `validation` Compose profilindeki test servisine `FeatureFlags__ExternalWrites=false` açıkça eklendi. Doğrulama PostgreSQL ağı `internal: true` olarak kalıyor; böylece dağıtım doğrulamasında test konteynerinin dış ağa çıkışı da engelleniyor. Canlı Compose'daki `${MARKETPLACEHUB_EXTERNAL_WRITES_ENABLED:-true}` ayarına dokunulmadı.

Güncel yerel backend suite'i `FeatureFlags__ExternalWrites=false` ve `MARKETPLACEHUB_TEST_CONNECTION` olmadan çalıştırıldı: 743 geçti, 35 PostgreSQL testi atlandı, 0 başarısız. GitHub Actions Validate #808 ise `MARKETPLACEHUB_TEST_CONNECTION` ile izole PostgreSQL 18 hizmetini kullanarak başarıyla tamamlandı; `FeatureFlags__ExternalWrites=false` test adımında açıkça ayarlıydı. Trendyol mapper testleri ayrıca 20/20 geçti. Frontend suite'i 57/57 dosya ve 224/224 testle geçti; typecheck ve üretim derlemesi başarılı; bundle kontrolü ana CSS'i 633,9/650 KiB olarak doğruladı. Bu testler üretim veritabanına veya pazaryeri yazma uçlarına bağlanmadı. Bu paragraf ilk sürümdeki doğrulama anını kaydeder; son CI, dağıtım ve salt-okunur aktarım sonucu yukarıdaki `349d6e0d` takip kaydında yer alır.

Commit `0608e65f` ilk mapper ve seçenek yenileme sürümünü; `349d6e0d` imza sınırı düzeltmesini içerir. Validate #808 ilk sürümü, Validate #810 takip düzeltmesini izole PostgreSQL ortamında doğruladı. CI ve dağıtım testlerinde `FeatureFlags__ExternalWrites=false`; testler pazaryeri yazma uçlarına bağlanmadı. İki sürüm de `main`'e alındı ve canlıya dağıtıldı. Canlı API ve worker ayarı önceki `FeatureFlags__ExternalWrites=true` değerinde doğrulandı. Başarılı ikinci salt-okunur katalog aktarımı ve `MZ005S26` için `XL / İndigo` doğrulaması yukarıda kaydedildi.

## 23. 7 Ekim B13 yayın kapısı ve canlı doğrulaması

`d7e6f21d` ile CI, production Compose dosyalarının varsayılan, `validation` ve `operations` profillerini birleştirip doğruluyor. CI doğrulaması boş geçici secret dosyaları kullanır; mevcut secret dosyasını değiştirmeyi reddeder ve geçici dosyaları temizler. `deploy/scripts/fast-deploy.sh` aynı üç yapılandırmayı üretimde, `production.env` ayarlarıyla, image oluşturmadan veya migration/değişiklik sınırına geçmeden önce doğrular. Bu değişiklik B13'ün Compose yapılandırması kaynaklı deploy hatalarını önleme kısmını kapatır.

CI ve fast-deploy içindeki 778/778 PostgreSQL suite'i güncel `Unknown` durumlu 100.000 aday fixture'ını da çalıştırıp geçti; böylece önceki yerel “fixture çalıştırılamadı” notu güncel durumu yansıtmıyor. Deploy'un normal test özeti bu testin ölçüm satırlarını göstermedi; yeni p95 değeri kaydedilmedi. Fixture hâlâ tek bağlantılı/tek platformlu ve faturasız olduğundan karışık fatura/delivery verisiyle staging p95 ölçümü B10 için açık kalıyor.

| Kontrol | Sonuç |
|---|---|
| CI | Validate #813 başarılı; backend/PostgreSQL ve frontend suite'leri geçti, Compose profilleri doğrulandı |
| Ana dal ve sunucu | `d7e6f21d` `main`'e gönderildi; sunucu `349d6e0d` → `d7e6f21d` fast-forward yaptı |
| İzole yayın testleri | `FeatureFlags__ExternalWrites=false`, dış ağa kapalı validation ağı ve ayrı PostgreSQL; 778/778 geçti, test konteyneri kaldırıldı |
| Yedek ve migration | Yedek kümesi `20261007T191157Z`; `database.dump` ve `private-volumes.tar.gz` SHA-256 kontrolleri `OK`; migration ve servis yenilemesi tamamlandı |
| Canlı sağlık | API ve worker `marketplacehub-app:manual-d7e6f21dabc5` ile `healthy`; `/health/ready` HTTP 200 |
| Canlı dış yazma ayarı | API ve worker `FeatureFlags__ExternalWrites=true`; mevcut üretim ayarı korundu |
| B16 katalog seçim kutusu | Hizalama CSS'i `0608e65f` içinde ve canlı `d7e6f21d` commit'inin atası; düzeltme üretimde. 390, 1299, 1537 ve 1600 px fixture ölçümlerinde başlık/satır kutusu farkı 0 px; geniş sayfa/regresyon matrisi açık |
| Kalan B13 | Yedeğin onaylı, şifreli off-host hedefe aktarılması ve CI imaj digest'inin kaynak commit'e bağlanması henüz kanıtlanmadı |

Yedek kümesi yalnız sunucunun yerel yedek alanında oluşturuldu ve checksum'ları kontrol edildi; off-host kopya yapılmadı. Bu adıma geçmek için onaylanmış hedef deposu ve erişim yöntemi gerekir. Tam proje planı bu nedenle **tamamlandı** sayılmaz; önceki bölümlerdeki B10, B11, B12, B16 ve B18 kanıtları da açık kalır.

## 24. 7 Ekim B10 karışık fixture performans tekrarları

100.000 adaylı PostgreSQL testi, yalnız geçici `validation` veritabanında ve `internal: true` Docker ağı içinde çalıştırıldı. Test sürecinde `FeatureFlags__ExternalWrites=false` idi; hiçbir pazaryeri/provider yazma çağrısı yapılmadı. Her adayın toplam sayısı, `DUE_SOON`/faturasız sayıları, 20 satırlı sayfası ve 5.000 sayfa sonucu doğrulandı. Test sonunda fixture tenant'ı temizlendi; geçici veritabanı, ağ, imajlar ve sunucu worktree'si kaldırıldı.

| Tarama grubu | Beş istek (ms) | nearest-rank p95 | Gözlenen konteyner belleği |
|---:|---|---:|---:|
| 10.000 (mevcut başlangıç) | 1.189, 1.264, 1.603, 2.018, 2.146 | 2.146 ms | Ölçülmedi |
| 50.000 (geçici deney) | 1.106, 1.202, 1.309, 1.498, 2.375 | 2.375 ms | 688,2 MiB / 768 MiB |
| 100.000 (geçici deney) | 883, 1.053, 1.127, 1.402, 1.700 | 1.700 ms | 665,9 MiB / 768 MiB |

10.000 grubunda 100.000 satır on ayrı sorgu grubuyla taranıyor; 100.000 grubu bunu tek sorguya indirdi. 10.000 satırlı servis sorgusunun `EXPLAIN ANALYZE` çalışma süresi 36,7 ms idi. 100.000 satırlı planda indeksten geriye doğru tarama ve sipariş birincil anahtarına 100.000 lookup görüldü; ölçülen plan 533,8 ms, bunun JIT kısmı 332,3 ms idi. Bu, doğruluk sınırlarının geçildiğini ancak sürenin yalnız basit indeks sıralamasından gelmediğini gösteriyor.

50.000 deneyi 10.000 tabanından daha yavaş, 100.000 deneyi ise p95 bakımından yaklaşık %21 daha hızlıydı. 100.000 tarama grubu `b36a480c` ile seçildi ve üretime dağıtıldı. GitHub Validate #822 başarılı; fast-deploy doğrulamasında izole PostgreSQL suite'i 778/778 geçti, yedek checksum'ları, migration, servis sağlığı ve readiness kontrolü tamamlandı. Canlı dış yazma ayarı `true` kaldı. 768 MiB test konteynerinde gözlenen kullanım sınıra yakın olduğu için eşzamanlı istek etkisi ayrıca değerlendirilmelidir. 1 saniye p95 hedefi karşılanmadı; B10 açık kalır ve performans tamamlandı sayılmaz.

## 25. Sipariş satırında varyant olmayan özellikleri gizleme

7 Ekim tarihli `/orders` ekran görüntüsünde `Renk` ve `Beden` satırlarının ardından Astar Durumu, Baskı/Nakış, Cep, Desen, Kumaş Tipi, Menşei ve benzeri tüm kategori özellikleri varyant satırı gibi gösteriliyordu. Kaynakta sipariş ve iade satırlarının `optionSignature` alanındaki her `Etiket: Değer` çiftinin listelendiği doğrulandı. Yeni sunum filtresi yalnızca renk ve beden boyutlarını gösteriyor; `Web Color` ayrı bir renk boyutu sayılmıyor. İade durumu alanlarını bulmak için kullanılan tam imza ayrıştırması korunuyor.

Yeni yardımcı için 3/3 birim testi, tam web paketi için 58/58 dosyada 227/227 test geçti. İlk sandboxlı tam-suite çalıştırmasında 9 dosya geçici `AppData` yoluna erişemedi; aynı paket sandbox dışı yerel çalıştırmada başarılı oldu. `npm run typecheck`, `npm run build`, `npm run check:bundle` ve `git diff --check` başarılı. [Validate #822](https://github.com/emrekks/RavenciaEntegrasyon/actions/runs/37676468559) geçti; düzeltme `b36a480c1ce9` olarak üretime dağıtıldı. Fast-deploy sonrasında API/worker/Caddy/PostgreSQL sağlıklı, `/health/ready` HTTP 200 ve API/worker `FeatureFlags__ExternalWrites=true` doğrulandı. Pazaryeri dış yazmaları testlerde kapalıydı.

## 26. Ürün düzenleme varyant alanında yalnızca beden ve rengi gösterme

Ürün düzenleme ekranındaki varyant tablosunda `Beden` ve `Renk` değerlerinin yanında `Astar Durumu` gibi kategori nitelikleri de seçenek imzası içinde görünüyordu. Tablo sunumundaki imza artık gerçek beden/renk eksenlerini seçiyor; `Web Color` ile yinelenen renk/beden adları kullanılmıyor. Kategori nitelikleri ve kaynağı olan ürün verisi değiştirilmedi; yalnızca salt-okunur varyant etiketi sadeleştirildi. Beden veya renk içermeyen çözümlenebilir bir imza `—` gösteriyor; eski biçimde ayrıştırılamayan değerler geriye dönük uyumluluk için olduğu gibi kalıyor.

Eklenen regresyon testleri kategori niteliğinin gizlendiğini, gerçek `Renk`/`Beden` adlarının `Web Color`/`Color`/`Size` karşısında tercih edildiğini ve yalnız kategori özelliği içeren imzanın boyut göstermediğini kapsıyor. Yerel frontend doğrulamasında 58/58 dosyada 230/230 test geçti; typecheck, production build ve bundle bütçesi başarılı. [Validate #825](https://github.com/emrekks/RavenciaEntegrasyon/actions/runs/37678251689) geçti. Fast-deploy doğrulaması dışa kapalı validation ağı ve `FeatureFlags__ExternalWrites=false` ile 778/778 backend testini çalıştırdı; üretimde API/worker ayarı `true` olarak korundu. `d154fec2` dağıtımı sonrası API, worker, Caddy ve PostgreSQL sağlıklı, `/health/ready` HTTP 200; istenen ürünün canlı panelindeki varyant alanları `Beden: M · Renk: Siyah` biçiminde doğrulandı. Pazaryerine dış yazma yapılmadı.

## 27. B12 — İmleç ilerlemesi alarmındaki yanlış durgunluk

7 Ekim canlı Trendyol yaşam döngüsü telemetry'sinde 25 kayıt alınmış, son imleç ilerlemesi 19:57:33 ve son başarılı çalışma 19:57:34 görünüyordu; arayüz buna rağmen “Kayıt geliyor, imleç ilerlemedi” diyordu. Kaynakta `SyncOpenOrders` ilerleyen watermark'ı iş ortasında kaydediyor; `RecordSyncCompletion` daha sonra `LastSuccessAt` değerini ayrı kaydediyor. İkinci kayıtta `AppDbContext.TrackSyncCursorProgress` aynı başarılı çalışmayı görmeyip yanlışlıkla durgunluk başlangıcı yazıyordu.

Tracker, mevcut denemenin başlangıcından sonra imlecin ilerlediğini `LastAttemptAt` ve `LastCursorAdvancedAt` üzerinden algılıyor; bu çalışmanın ayrı tamamlanma kaydı durgunluk başlatmıyor. Daha sonraki başarılı çalışmada kayıt alınıp ilerleme olmazsa alarm korunuyor. Regresyon testi iki kayıt aşamasını ve sonraki gerçek durgunluk durumunu kapsıyor.

Doğrulama: regresyon testi 1/1 geçti. Tam yerel backend suite'i `FeatureFlags__ExternalWrites=false` ve üretim veritabanı bağlantısı olmadan 744 geçti, 35 PostgreSQL testi atlandı, 0 başarısız oldu. Pazaryerine yazma yapılmadı. Canlı kuyruk görünümünde bu inceleme sırasında 390 açık iş ve son 24 saatte 44 ölü iş bulundu; bu imleç alarmı düzeltmesi kuyruk gecikmelerinin kök nedenini çözmüyor. Bu nedenle B12, yeni sürümün canlıya dağıtılması ve başarılı ilerleyen bir çalışmadan sonra yanlış alarmın kaybolmasının doğrulanmasına; uzun kuyruk/ölen işlerin ayrıca giderilmesine kadar açık kalır.

## 28. Varyant etiketi için ek seçenek-imzası biçimleri

8 Ekim'deki yeni ekran görüntüsünde varyant seçenek alanında beden ve rengin yanında kategori özellikleri yeniden göründü. Kaynaktaki açık sorun yolu şuydu: salt-okunur alan yalnızca `optionSignature` metnine bağlıydı ve imza ayrıştırılamadığında metnin tamamını geriye dönük uyumluluk adına gösteriyordu. Yeni sürüm, bu ham imza fallback'ini kaldırıyor; ayrıca `•` ayıracını tanıyor.

Etiket artık önce varyantın yapılandırılmış `options` değerlerinden oluşturuluyor, imza yalnızca eksik değerleri tamamlıyor. İmza ayrıştırıcısı eski `|`, `·` ve `•` ayraçlarını tanıyor; görüntülenen seçenekler ise `Beden: M - Renk: Siyah` biçiminde `-` ile ayrılıyor. Tanınmayan değer ham metin olarak gösterilmiyor (`Tek Ürün` korunuyor). Sunum değişikliği kayıtlı ürün niteliği veya pazaryeri verisini değiştirmiyor. Canlı panelde ürün `01a115f5-d7a9-7cea-a084-43cfc2c62532` açılıp 7 satırın her biri kontrol edildi; görünen etiketler yalnızca `Beden` ve `Renk` içeriyor.

Doğrulama: separator regresyon testi dahil yardımcı testleri 14/14; tam web suite'i 58/58 dosyada 234/234; `npm run typecheck`, production build, bundle bütçesi ve `git diff --check` başarılı. [Commit `e426d29f`](https://github.com/emrekks/RavenciaEntegrasyon/commit/e426d29f5bf3e63b271ac99a9feeb2df7895ed1a) `main` dalına gönderildi ve sunucuya fast-forward edilip canlıya alındı. Fast-deploy izole PostgreSQL suite'i 787/787 geçti; yeni yedek dosyalarının checksum'ları `OK`, migration ve servis yenilemesi tamamlandı. API/worker/Caddy/PostgreSQL sağlıklı; readiness HTTP 200. Validation boyunca pazaryeri dış yazmaları kapalıydı; canlı API/worker dış yazma ayarı önceki `true` değerinde bırakıldı. Canlı panelde yedi varyantın her biri tireli ayıraçla gösterildi. Başka katalog verisi düzenlenmedi.
