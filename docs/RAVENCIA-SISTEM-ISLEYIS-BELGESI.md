# Ravencia MarketplaceHub Sistem İşleyiş Belgesi

## 1. Belgenin amacı

Bu belge, Ravencia MarketplaceHub sisteminin işlevsel kapsamını, veri akışlarını, modüllerini, entegrasyon davranışlarını ve operasyon kurallarını tanımlar. Görsel tasarım, tema, renk, tipografi ve yerleşim kararları bu belgenin kapsamı dışındadır.

Ravencia MarketplaceHub; pazaryeri mağazalarının ürün, varyant, kategori, özellik, marka, fiyat, stok, sipariş, paket, kargo, iade, fatura ve entegrasyon işlemlerini tek merkezden yönetir. Sistem bir tüketici mağazası değil, yoğun operasyon verisi işleyen çok kiracılı bir back-office ve entegrasyon platformudur.

Aktif entegrasyonlar:

- Trendyol Marketplace
- Trendyol E-Faturam

Planlanan entegrasyonlar:

- Hepsiburada
- N11
- Amazon
- Pazarama
- PttAVM
- Shopify

Planlanan platformlar, ilgili adapter ve yetenekleri tamamlanmadan çalışan entegrasyon olarak kabul edilmez. Sistem yalnız doğrulanmış yetenekleri kullanıma açar.

## 2. Temel çalışma ilkeleri

### 2.1. Yerel veritabanı esaslı okuma

Ürün, sipariş, iade ve fatura listelemeleri doğrudan pazaryeri API’sinden okunmaz. Bu ekranların veri kaynağı Ravencia’nın PostgreSQL veritabanıdır.

Bu kuralın amaçları şunlardır:

- Pazaryeri API limitlerine takılmamak.
- Liste ekranlarında tutarlı ve hızlı sonuç üretmek.
- Filtre, arama, sıralama ve raporlama işlemlerini dış servisten bağımsız çalıştırmak.
- Dış servis geçici olarak erişilemez olsa bile mevcut operasyon verisini görüntüleyebilmek.
- Aynı verinin gereksiz biçimde tekrar tekrar indirilmesini engellemek.
- Değişiklik geçmişi ve audit kaydı tutabilmek.

Arayüzde gösterilen toplamlar, aktif sayfanın satırlarından değil veritabanındaki ilgili kapsamın tamamından hesaplanır.

### 2.2. Arka plan senkronizasyonu

Pazaryerinden veri alma ve pazaryerine veri gönderme işlemleri kuyruk tabanlı arka plan işleri üzerinden yürütülür. Kullanıcı bir dış işlem başlattığında sistem işlemi anında tamamlanmış kabul etmez.

Standart işlem yaşam döngüsü:

1. İstek doğrulanır.
2. Benzersiz ve tekrar çalıştırmaya dayanıklı bir iş kaydı oluşturulur.
3. İş `Kuyrukta` durumuna alınır.
4. Worker işi kiralar ve `Çalışıyor` durumuna geçirir.
5. Gerekli pazaryeri isteği gönderilir.
6. Uzak sistem yanıtı kaydedilir.
7. Gerekirse ilgili kayıt pazaryerinden yeniden okunur.
8. Yerel veritabanı doğrulanmış son duruma göre güncellenir.
9. İş `Tamamlandı`, `Yeniden denenecek`, `Kısmi başarılı`, `Başarısız` veya `İptal edildi` durumlarından biriyle sonuçlanır.

Başarılı HTTP yanıtı tek başına işin işlevsel olarak başarılı olduğu anlamına gelmez. Uzak sistemdeki sonuç mümkün olduğunda yeniden okunarak doğrulanır.

### 2.3. Güncellik ve kaynak bilgisi

Her ana veri grubu için aşağıdaki bilgiler tutulur:

- Verinin kaynak platformu.
- İlgili bağlantı ve mağaza kimliği.
- Son başarılı eşitleme zamanı.
- Son deneme zamanı.
- Bir sonraki planlı kontrol zamanı.
- Kullanılan watermark veya zaman aralığı.
- Son iş durumu.
- İşlenen, eklenen, güncellenen, atlanan ve hatalı kayıt sayıları.
- Son güvenli hata kodu ve kısa açıklaması.

Veri belirlenen güncellik eşiğini aşmışsa kayıtlar silinmez veya gizlenmez; veri güncelliğini yitirmiş olarak işaretlenir.

### 2.4. Tekrar çalıştırma güvenliği

Dış yazma, veri içe alma ve uzun süren işlemlerde idempotency anahtarı kullanılır. Aynı kullanıcı isteği ağ problemi, sayfa yenileme veya tekrar tıklama nedeniyle yeniden gönderilirse ikinci bir bağımsız iş ya da mükerrer dış işlem oluşturmamalıdır.

Sipariş, paket, fatura, iade ve ürünlerde pazaryeri kimlikleri tenant ve bağlantı kapsamında benzersiz tutulur. Upsert işlemleri aynı kaydı günceller; kopya kayıt üretmez.

### 2.5. Tenant ve bağlantı izolasyonu

Her operasyon kaydı bir tenant’a aittir. Pazaryeri verileri ayrıca kaynak bağlantı ile ilişkilendirilir. Bir tenant’ın veya bağlantının verisi başka tenant ya da bağlantı sorgularına karışamaz.

Filtreleme, sayım, senkronizasyon, silme ve audit işlemleri tenant kapsamını zorunlu olarak kullanır.

## 3. Ana veri akışları

### 3.1. Pazaryerinden veri alma

Standart veri alma akışı:

1. Scheduler, webhook veya kullanıcı talebi senkronizasyon ihtiyacı oluşturur.
2. Sistem bağlantının aktif, doğrulanmış ve gerekli okuma yeteneğine sahip olduğunu kontrol eder.
3. Aynı kapsamda çalışan eşdeğer bir iş varsa yeni mükerrer iş oluşturulmaz.
4. Son başarılı watermark ve güvenlik örtüşme aralığı belirlenir.
5. Pazaryeri API’si sayfa sayfa okunur.
6. Her sayfanın ham referansları ve dönüştürülmüş alanları doğrulanır.
7. Kayıtlar toplu biçimde yerel veritabanına eklenir veya güncellenir.
8. Silinmiş, iptal edilmiş ya da durum değiştirmiş kayıtlar iş kurallarına göre işaretlenir.
9. Sayfa ilerlemesi ve sayaçlar iş kaydına yazılır.
10. Bütün sayfalar başarıyla tamamlandığında watermark ilerletilir.

Watermark yalnız işlem güvenli biçimde tamamlandıysa ilerletilir. Kısmi başarısızlıkta hatalı sayfaların atlanarak watermark’ın ileri taşınması veri kaybına yol açmamalıdır.

### 3.2. Pazaryerine veri gönderme

Standart dış yazma akışı:

1. Kullanıcı işlemi ve hedef kayıt doğrulanır.
2. Bağlantının ortamı, credential durumu ve ilgili yazma yeteneği kontrol edilir.
3. Yerel kaydın güncel sürümü ve işlem ön koşulları doğrulanır.
4. İdempotent iş oluşturulur.
5. Worker, sağlayıcıya uygun isteği hazırlar ve gönderir.
6. Uzak request ID, batch ID veya işlem referansı kaydedilir.
7. Sağlayıcı asenkron sonuç veriyorsa sonuç belirli aralıklarla takip edilir.
8. Hedef kayıt yeniden okunarak uzlaştırılır.
9. Yerel durum yalnız doğrulanmış dış sonuca göre güncellenir.

Uzak işlem başarısızsa sistem yerel kaydı olmuş gibi ilerletmez. Önceki doğrulanmış durum korunur ve hata iş kaydına yazılır.

### 3.3. Webhook işleyişi

Webhook, yeni sipariş veya durum değişikliği gibi olayların hızlı alınmasını sağlar; periyodik eşitlemenin yerine geçmez.

Webhook akışı:

- İmza ve kaynak doğrulanır.
- Olay kimliğiyle mükerrer teslim engellenir.
- Ham payload güvenli biçimde kaydedilir.
- İşlenmesi için kuyruk işi oluşturulur.
- İlgili sipariş, paket veya iade API’den yeniden okunur.
- Yerel kayıt doğrulanmış son durumla güncellenir.
- Başarısız webhook olayları kontrollü biçimde yeniden denenir.

Webhook kaçırılmış, gecikmiş veya sıra dışı gelmiş olabilir. Bu nedenle periyodik tarama güvenlik ağı olarak devam eder.

### 3.4. Uzlaştırma

Yerel durum ile pazaryeri durumu arasında fark bulunduğunda sistem uzlaştırma kaydı oluşturur. Farklar şu gruplara ayrılabilir:

- Yerelde bulunup kaynakta bulunmayan kayıt.
- Kaynakta bulunup yerelde bulunmayan kayıt.
- Durum uyuşmazlığı.
- Tutar veya miktar uyuşmazlığı.
- Paket, kargo veya takip numarası uyuşmazlığı.
- Ürün, varyant, fiyat ya da stok uyuşmazlığı.
- Fatura teslimi veya provider durumu uyuşmazlığı.

Otomatik düzeltmeye uygun farklar iş kurallarıyla güncellenebilir. Finansal veya yıkıcı sonuç doğurabilecek farklar insan incelemesine bırakılır.

## 4. Sayım, filtreleme ve sayfalama

### 4.1. Gerçek toplam kuralı

Toplam ürün, sipariş, iade ve fatura sayıları veritabanındaki bütün ilgili kayıtları kapsar. `200`, yalnızca izin verilen en yüksek sayfa boyutudur; toplam kayıt sınırı değildir.

Her liste şu değerleri ayrı hesaplar:

- Genel toplam.
- Aktif filtrelere uyan toplam.
- Geçerli sayfada gösterilen kayıt aralığı.
- Geçerli sayfa numarası.
- Toplam sayfa sayısı veya cursor devam bilgisi.

Örnek sonuç: `Toplam 12.640 sipariş · Filtre sonucu 328 · 201–300 arası gösteriliyor`.

### 4.2. Sayfa boyutları

Desteklenen sayfa boyutları:

- 20
- 50
- 100
- 200

Sayfa boyutu değiştirildiğinde filtreler korunur ve sonuç ilk geçerli sayfaya döner. Geçersiz veya sınırı aşan değerler kabul edilmez.

### 4.3. Filtreleme ve sıralama

Filtreler veritabanı sorgusuna uygulanır. Yalnız ekranda yüklenmiş satırlar üzerinde istemci taraflı filtreleme yapılarak yanlış toplam üretilemez.

Arama alanlarında mümkün olduğunda şu kimlikler desteklenir:

- Sipariş numarası.
- Paket numarası.
- Takip numarası.
- Ürün adı.
- Model kodu.
- SKU.
- Barkod.
- Müşteri adı.
- Fatura numarası.
- İade veya claim numarası.

## 5. Kimlik doğrulama, yetkilendirme ve güvenlik

### 5.1. Oturum açma

Sistem e-posta veya kullanıcı adı ve parola ile oturum açılmasını destekler. Başarısız denemeler rate-limit ve güvenlik politikalarına tabidir.

İlk girişte veya yönetici zorlamasında parola değiştirme işlemi uygulanabilir. Yeni parola en az 15 karakter olmalı ve mevcut parola politikalarını karşılamalıdır.

### 5.2. Çok faktörlü doğrulama

Authenticator tabanlı altı haneli kod desteklenir. Kurulum sırasında kullanıcıya QR verilir, ardından üretilen kodla doğrulama tamamlanır. Kurtarma kodları yalnız bir kez açık biçimde gösterilir ve daha sonra tekrar okunamaz.

### 5.3. Oturum yönetimi

Kullanıcı:

- Mevcut cihazını ayırt edebilir.
- Diğer aktif oturumlarını görebilir.
- Tek bir uzak oturumu sonlandırabilir.
- Mevcut oturum hariç bütün oturumları kapatabilir.
- Süresi bitmiş oturum kayıtlarını temizleyebilir.

### 5.4. Credential güvenliği

API key, API secret, parola, token ve benzeri hassas bilgiler şifrelenmiş biçimde saklanır. Kaydedildikten sonra açık değer tekrar gösterilmez. Sistem yalnız credential’ın mevcut, eksik, geçersiz veya doğrulanmış olduğunu bildirir.

Log, hata mesajı, iş payload’ı ve audit kaydında secret bulunamaz.

### 5.5. Yetkilendirme

İşlemler rol ve izin denetimine tabidir. Özellikle şu işlemler ayrı yetki gerektirir:

- Production ortamında dış yazma.
- Bağlantı credential’ı değiştirme.
- Sipariş veya ürün durumunu değiştirme.
- Kargo firması değiştirme.
- İade kararı verme.
- Fatura oluşturma veya iptal etme.
- Veritabanı temizliği.
- Bağlantıyı ilişkili verileriyle silme.
- Audit ve teknik iş detaylarını görüntüleme.

## 6. Dashboard işleyişi

Dashboard verilerini yerel veritabanından toplar. Metrikler geçerli tenant’ın bütün kayıtlarına göre hesaplanır ve liste sayfa boyutundan etkilenmez.

Temel metrikler:

- Bekleyen sipariş.
- Geciken sipariş.
- Bugünkü ve bu ayki sipariş.
- Bekleyen iade.
- Süresi yaklaşan fatura.
- Faturalandırılmamış paket.
- Düşük veya tükenmiş stok.
- Aktif ve sorunlu bağlantı sayıları.
- Bekleyen ve başarısız arka plan işi sayıları.

Operasyon özeti aşağıdaki kaynaklardan oluşur:

- Sipariş ve paket durumları.
- Kargo firması dağılımı.
- Takip numarası veya etiket bekleyen paketler.
- Ürün bazlı stok riskleri.
- Senkronizasyon sağlık bilgileri.
- Son önemli iş ve kullanıcı aksiyonları.

İleride satış, iade, iptal, fatura ve kargo performans raporları aynı yerel veri modeli üzerinden üretilecektir.

## 7. Ürün ve katalog yönetimi

### 7.1. Ürün listesi

Ürün listesi yerel ürün, varyant, stok, fiyat ve platform bağlantı kayıtlarını birleştirir.

Desteklenen bilgiler:

- Ürün kimliği ve adı.
- Model kodu.
- Ana SKU ve barkod.
- Marka ve kategori.
- Varyant sayısı.
- Liste ve satış fiyatı.
- Toplam, rezerve, güvenlik ve satılabilir stok.
- Satış durumu.
- Platform yayın ve eşleştirme durumu.
- Son eşitleme zamanı.
- Son dış işlem sonucu.

Ürün metrikleri en az toplam, aktif, stoksuz, düşük stoklu, eşleşmemiş, onay bekleyen ve reddedilmiş kayıtları ayrı hesaplar.

### 7.2. Trendyol’dan ürün alma

Ürün alma iki modda çalışır:

- Artımlı tarama: yalnız son watermark’tan sonra yeni veya değişmiş ürünler alınır.
- Tam tarama: erişilebilir bütün onaylı ürünler sayfa sayfa yeniden okunur.

İşlem sırasında şu sayaçlar tutulur:

- Okunan sayfa.
- Okunan ürün.
- Yeni ürün.
- Güncellenen ürün.
- Değişmeyen ürün.
- Eşleşemeyen ürün.
- Hatalı ürün.

Ürün, varyant, seçenek, görsel adresi, stok, fiyat ve platform kimlikleri yerel modele dönüştürülür. Aynı ürünün tekrar okunması kopya oluşturmaz.

### 7.3. Ürün oluşturma ve düzenleme

Ürün kaydı şu veri gruplarını kapsar:

- Temel ürün bilgileri.
- Marka ve panel kategorisi.
- Model, SKU ve barkod kimlikleri.
- Açıklama.
- Fiyat, para birimi, KDV ve stok.
- Kargo ölçüleri ve hesaplanan desi.
- En fazla sekiz ürün görseli ve ana görsel seçimi.
- Kategoriye bağlı ürün özellikleri.
- En fazla iki varyant ekseni.
- Varyant bazlı SKU, barkod, stok ve fiyat.
- Yayınlanacak platform bağlantıları.

Varyant kombinasyonları oluşturulmadan önce tahmini kombinasyon sayısı hesaplanır. Aynı seçenek imzasına sahip iki varyant oluşturulamaz.

### 7.4. Ürün dış yazma işlemleri

Desteklenebilir dış işlemler:

- Yeni ürün yayınlama.
- Mevcut ürün bilgisini güncelleme.
- Fiyat güncelleme.
- Stok güncelleme.
- Satışa açma veya kapatma.
- Arşivleme veya arşivden çıkarma.

Her işlem bağlantı yeteneği, eşleştirme tamamlanma durumu ve zorunlu alanlar bakımından doğrulanır. Batch tabanlı işlemlerde batch sonucu ayrıca takip edilir. Varyant bazlı ret kodları ürünün tamamından ayrı saklanır.

### 7.5. Toplu ürün işlemleri

Toplu işlemler yalnız seçilmiş ve yetkili kapsama uygulanır. İşlem başlamadan önce hedef kayıt sayısı gösterilir. Sonuçta başarılı, başarısız ve atlanan kayıtlar ayrı raporlanır.

## 8. Kategori, marka ve özellik eşleştirme

Eşleştirme sistemi yerel katalog kimlikleri ile pazaryeri referans kimliklerini bağlantı ve kategori kapsamında ilişkilendirir. Trendyol referans verileri salt okunur olarak yerel snapshot’lara alınır.

### 8.1. Referans verileri

Sistem aşağıdaki referansları ayrı kaynak türleri olarak eşitler:

- Kategoriler.
- Markalar.
- Kategori özellikleri.
- Özellik değerleri.

Her snapshot kaynak bağlantı, kapsam kimliği, alınma zamanı ve sürüm bilgisi taşır. Eşleştirme kayıtları kullandıkları snapshot’ı referans eder.

### 8.2. Dört adımlı kategori eşleştirme

1. Kategori eşleme: Panel yaprak kategorisi Trendyol yaprak kategorisiyle eşlenir.
2. Ürün özellikleri: Materyal, kol boyu ve benzeri ürün bilgisi başlıkları kategoriye bağlanır.
3. Seçenekler: Beden ve renk gibi varyant üreten en fazla iki başlık ayrı olarak belirlenir.
4. Kategori özellikleri: Yerel ürün özellikleri ve seçenekler, seçili Trendyol kategorisinin alanları ve değerleriyle eşlenir.

Ürün özelliği ve seçenek tek bir rol seçicisi içinde karışık yönetilmez. Bir özellik iki adım arasında taşınabilir; seçenek adımı iki başlık sınırını aşamaz.

Binlerce değeri olan özelliklerde bütün değerler aynı anda yüklenmez. Değerler aranır, sınırlı sayıda gösterilir ve gerektiğinde sayfalanır veya sanal liste üzerinden okunur.

### 8.3. Zorunlu alan doğrulaması

Trendyol kategorisinin zorunlu alanları ayrı takip edilir. Ürün yayına gönderilmeden önce:

- Zorunlu alanların yerel karşılıkları bulunmalıdır.
- Seçim listeli alanların gerekli değer eşlemeleri tamamlanmalıdır.
- Varyant seçeneklerinin kaynak ürünlerde gerçek değerleri bulunmalıdır.
- Eşleştirme snapshot’ı güncel olmalıdır.

Eksik eşleştirmeler dış yayın işini engeller ve hangi alanların eksik olduğu raporlanır.

### 8.4. Marka eşleştirme

Yerel marka, bağlantı kapsamında Trendyol marka kimliğiyle eşlenir. Eşleme kaldırılabilir veya güncel snapshot kullanılarak yenilenebilir. İleride isim benzerliğiyle öneri, güven skoru, çakışma listesi ve toplu onay desteklenebilir.

## 9. Sipariş yönetimi

### 9.1. Sipariş alma

Yeni ve değişmiş siparişler artımlı zaman aralığıyla alınır. Güvenlik örtüşmesi, sınır zamanlarında kaçabilecek kayıtların yeniden okunmasını sağlar. Tekil sipariş numarasıyla yenileme ve tam erişilebilir dönem taraması ayrıca desteklenir.

Sipariş verisi şu alt kayıtlarla birlikte saklanır:

- Sipariş üst bilgisi.
- Müşteri ve adres snapshot’ları.
- Sipariş satırları.
- Ürün kaynak snapshot’ları.
- Paketler.
- Paket-satır dağılımları.
- Kargo ve takip bilgileri.
- Finansal dağılımlar.
- Durum geçmişi.

### 9.2. Durum yönetimi

Temel sipariş ve paket durumları:

- Yeni.
- İşleme alındı.
- Kargoda.
- Teslim edildi.
- İptal edildi.
- İade edildi.
- Yeniden gönderim.
- Askıda.
- Kontrol gerekli.

Kaynak platformdaki durum ayrı, Ravencia’nın normalize edilmiş operasyon durumu ayrı tutulabilir. Kaynak durum kaybolmadan normalize durum üzerinden filtreleme yapılır.

### 9.3. İşleme alma

`İşleme Al` işlemi gerçek Trendyol paket güncellemesi olarak yürütülür:

1. Paket mevcut durumu ve işlem uygunluğu doğrulanır.
2. Kullanıcı onayı alınır.
3. İş kuyruğa alınır.
4. Trendyol durum değiştirme isteği gönderilir.
5. Paket yeniden okunur.
6. Doğrulanan durum yerel veritabanına yazılır.
7. Başarısızlıkta eski doğrulanmış durum korunur.

### 9.4. Kargo firması değiştirme

Kargo değişikliği yalnız ilgili bağlantının shipment-write yeteneği varsa çalışır.

İşlem:

- Sipariş ve paket kimliğini doğrular.
- Mevcut kargo firmasını belirler.
- Trendyol’un o paket için kabul ettiği yeni firmayı seçer.
- Değişikliği kuyruk üzerinden Trendyol’a gönderir.
- Paket bilgisini yeniden okur.
- Yeni kargo ve takip durumunu yerel veritabanına kaydeder.

Kaynak tarafından reddedilen bir kargo seçimi yerelde başarılı gösterilemez.

### 9.5. İptaller ve teslim durumu

Sipariş iptali, satır veya ürün bazlı iptal ve teslim durumları kaynak API’nin desteklediği kapsamda işlenir. İptal edilen miktar sipariş satırı seviyesinde saklanır. Tam sipariş iptali ile kısmi ürün iptali birbirine dönüştürülmez.

### 9.6. Kargo belgeleri

Paket için A4, sticker veya ZPL etiket talebi ayrı iş olarak yürütülür. Belge üretim denemeleri, dosya türü, oluşturma zamanı, hata ve güvenli dosya referansı saklanır.

## 10. İade yönetimi

İade talepleri periyodik ve gerektiğinde manuel senkronizasyonla alınır. İade kaydı sipariş, bağlantı ve claim kimliğiyle ilişkilendirilir.

Saklanan bilgiler:

- Talep ve claim numarası.
- Sipariş ve paket bağlantısı.
- İade satırları ve miktarlar.
- İade sebebi ve açıklaması.
- Kargo hareketleri.
- Kanıt dosyaları.
- Karar geçmişi.
- Stok geri alma kararı.
- Kaynak ve normalize durum.

İade kararları:

- Onaylama.
- Reddetme.
- Uyuşmazlık oluşturma.
- İncelemeye alma.

Stok sonucu:

- Satılabilir stoğa döndürme.
- Hasarlı stoğa alma.
- İnceleme stoğuna alma.
- Stok hareketi oluşturmama.

Her karar yetki, mevcut durum ve sağlayıcı yeteneği bakımından doğrulanır. Dış kararın sonucu alınmadan yerel talep kesinleşmiş sayılmaz.

## 11. Fatura yönetimi

### 11.1. Faturalandırılabilir kapsam

Faturalandırma siparişten çok paket bazlı değerlendirilir. Aynı paket için ikinci bir satış faturası oluşturulamaz. Bu kontrol hem iş kuralı hem veritabanı benzersizlik kuralı ile korunur.

Fatura bekleyen toplamı ilk 200 pakete göre değil, veritabanındaki bütün uygun ve henüz faturalandırılmamış paketlere göre hesaplanır.

### 11.2. Fatura oluşturma

İşlem adımları:

1. Sipariş ve paket uygunluğu kontrol edilir.
2. Mevcut fatura ve devam eden fatura işi aranır.
3. Müşteri ve adres snapshot’ları hazırlanır.
4. Fatura satırları, miktar, indirim, KDV ve toplamlar hesaplanır.
5. Müşteri uygunluğuna göre E-Fatura veya E-Arşiv belge tipi seçilir.
6. İdempotent fatura kaydı ve gönderim işi oluşturulur.
7. E-Faturam provider’ına gönderilir.
8. Provider sonucu ve ETTN/UUID kaydedilir.
9. Belge hazır olana kadar durum takip edilir.
10. Gerekliyse güvenli fatura bağlantısı Trendyol’a iletilir.

### 11.3. Fatura durumları

- Fatura bekliyor.
- Hazırlanıyor.
- Provider’a gönderiliyor.
- Provider yanıtı bekleniyor.
- Oluşturuldu.
- Belge bekleniyor.
- Teslim edildi.
- Reddedildi.
- Yeniden denenecek.
- İnceleme gerekli.
- İptal istendi.
- İptal edildi.

### 11.4. Belgeler

PDF, JPEG ve PNG belgeleri güvenli dosya alanında tutulur. Dosya bağlantıları yetkilendirme olmadan doğrudan dışarı açılmaz. Manuel yükleme yapıldığında dosya tipi, boyut, hash ve kaynak kaydedilir.

## 12. İşlem Takibi

İşlem Takibi bütün asenkron işlemlerin merkezi kayıt alanıdır.

Her işte en az şu bilgiler bulunur:

- Tenant ve bağlantı.
- İş tipi ve hedef kayıt.
- Oluşturulma, başlama ve bitiş zamanı.
- Durum.
- Öncelik.
- Deneme ve azami deneme sayısı.
- Sonraki deneme zamanı.
- İlerleme sayaçları.
- Güvenli hata kodu ve açıklaması.
- Correlation ID.
- Uzak request veya batch ID.
- Idempotency/dedup bilgisi.
- İşlenen kayıt ve fark sayıları.

Yeniden deneme yalnız geçici olarak sınıflandırılmış hatalarda otomatik yapılır. Kimlik bilgisi hatası, doğrulama hatası veya desteklenmeyen işlem sürekli tekrar edilmez; kullanıcı müdahalesi bekler.

İptal işlemi yalnız henüz dış etkisi gerçekleşmemiş veya güvenli biçimde durdurulabilir işlerde kullanılabilir.

## 13. Platform bağlantıları

### 13.1. Bağlantı kaydı

Bir bağlantı şu bilgileri taşır:

- Platform kodu.
- Bağlantı adı.
- Stage veya Production ortamı.
- Mağaza kimliği.
- Platforma özel ayarlar.
- Credential durumu.
- Son test zamanı.
- Son başarı zamanı.
- Son hata kodu.
- Bağlantı durumu ve sürümü.

### 13.2. Bağlantı doğrulama

Bağlantı testi gerçek sağlayıcı isteğiyle credential ve erişim yeteneğini doğrular. Başarılı test bağlantıyı doğrulanmış duruma getirebilir. Test sonucu tarihi ve güvenli hata koduyla saklanır.

### 13.3. Senkronizasyon politikaları

Her bağlantı için kaynak türü bazında bağımsız politika tanımlanır:

- Sipariş ve paket durumları.
- İadeler.
- Ürünler.
- Kategori ve marka referansları.
- Faturalar ve provider durumları.

Politika şunları içerir:

- Aktif/pasif.
- Kontrol aralığı.
- Güvenlik örtüşme süresi.
- Jitter.
- Son başarılı çalışma.
- Sonraki planlı çalışma.
- Tam tarama ve artımlı tarama desteği.

Jitter, bütün bağlantıların aynı saniyede dış servise yük bindirmesini engeller. Rate-limit yanıtında sağlayıcının belirttiği bekleme süresi dikkate alınır.

### 13.4. Yetenek yönetimi

Her bağlantı için desteklenen yetenekler kaydedilir:

- Connection read.
- Product read/write.
- Inventory write.
- Price write.
- Order read.
- Webhook.
- Shipment write.
- Label read/write.
- Return read/write.
- Invoice delivery.

Yetenek durumu `Destekleniyor`, `Desteklenmiyor` veya `Bilinmiyor` olabilir. Bilinmeyen yetenek destekleniyor kabul edilmez.

## 14. Veritabanı temizliği ve bağlantı silme

### 14.1. Liste bazlı temizlik

Yetkili kullanıcı aşağıdaki kapsamları ayrı seçebilir:

- Ürünler.
- Siparişler.
- İadeler.
- Faturalar.

Temizlik başlamadan önce:

- Seçili kapsamlar doğrulanır.
- Tahmini kayıt sayıları hesaplanır.
- Bağımlı kayıtların da silineceği açıklanır.
- Kullanıcıdan tam olarak `VERİLERİ SİL` onayı alınır.

Siparişler seçilirse bağlı iadeler ve faturalar yabancı anahtar sırasına uygun biçimde temizlenir. İşlem tek veritabanı transaction’ında yürür. Herhangi bir adım başarısız olursa bütün işlem geri alınır.

Başarı sonunda kapsam bazında silinen kayıt özeti ve audit kaydı oluşturulur.

### 14.2. Bağlantıyı derin silme

Bir platform bağlantısının derin silinmesi şu sonuçları doğurur:

- Otomatik senkronizasyon durur.
- Credential ve yetenek kayıtları kaldırılır.
- Bağlantıya ait işler, cursor’lar, politikalar ve webhook kayıtları temizlenir.
- Bağlantıya ait sipariş, paket, iade ve fatura kayıtları bağımlılıklarıyla silinir.
- Yalnız o bağlantıya ait ürün bağlantıları ve güvenle ayrıştırılabilen yerel ürünler temizlenir.
- Kategori, marka, özellik ve değer eşlemeleri kaldırılır.
- Referans snapshot’ları kaldırılır.
- Fatura politikaları ve bağlantıya bağlı teslim kayıtları temizlenir.
- Bağlantı `DELETED` durumuna alınır ve normal listelerden çıkarılır.

Başka bağlantılar tarafından paylaşılan yerel ürün veya ortak kayıtlar yanlışlıkla silinmez. Doğrudan bağlantı ilişkisi olmayan tenant genelindeki kayıtlar yalnızca “derin silme” gerekçesiyle topluca kaldırılmaz.

İşlem için bağlantı adı birebir yazılarak onay verilir. İşlem transaction içinde yürür ve audit kaydı bırakır.

## 15. Katalog yönetimi ve içe aktarım

### 15.1. Yerel katalog

Yerel katalog şu ana varlıklardan oluşur:

- Kategori ağacı.
- Markalar.
- Özellik tanımları.
- Özellik değerleri.
- Ürünler.
- Ürün seçenekleri ve varyantlar.
- Ürün görselleri.
- Platform bağlantıları ve eşlemeler.

Parent kategoriler sınıflandırma için, leaf kategoriler ürün atama ve pazaryeri eşleştirme için kullanılır.

### 15.2. Dosyadan içe aktarım

CSV ve XLSX içe aktarım akışı:

1. Dosya yüklenir ve güvenlik doğrulamasından geçirilir.
2. Kolonlar algılanır.
3. Kaynak kolonlar Ravencia alanlarıyla eşlenir.
4. Önizleme ve doğrulama sonucu oluşturulur.
5. Her satır için yeni oluşturma, mevcut ürünle eşleme veya atlama kararı verilir.
6. Uygulama işi kuyruğa alınır.
7. Satırlar toplu ve idempotent biçimde uygulanır.
8. Hatalar indirilebilir raporda sunulur.

İçe aktarım toplam, geçerli, hatalı, eşleşen, yeni ve inceleme gereken satır sayılarını ayrı tutar.

### 15.3. Stok ve fiyat

Stok modeli en az şu değerleri ayırır:

- Elde olan stok.
- Rezerve stok.
- Güvenlik stoğu.
- Satılabilir stok.

Satılabilir stok iş kuralına göre hesaplanır ve negatif gönderilmez. Manuel stok düzeltmesinde miktar, neden, kullanıcı ve zaman audit kaydına yazılır.

Fiyat ve stok dış yazmaları yalnız değişen kayıtları kapsayabilir. Her batch sonucu varyant seviyesinde takip edilir.

## 16. Hata, yeniden deneme ve kısmi başarı

Hatalar şu sınıflara ayrılır:

- Geçici ağ veya timeout hatası.
- Rate-limit.
- Credential veya yetki hatası.
- Sağlayıcı doğrulama hatası.
- İş kuralı ihlali.
- Eşleştirme eksikliği.
- Veri dönüştürme hatası.
- Çakışma veya eski sürüm hatası.
- Kalıcı desteklenmeyen işlem.
- İç sistem veya veritabanı hatası.

Geçici hatalar exponential backoff ve jitter ile yeniden denenir. Kalıcı hatalar kullanıcı müdahalesi olmadan sonsuz kez tekrarlanmaz.

Toplu işte bazı kayıtlar başarılı, bazıları hatalıysa iş `Kısmi başarılı` olarak sonuçlanır. Başarılı kayıtlar, hatalı kayıtlar ve yeniden denenebilir kayıtlar ayrı listelenir.

Kullanıcıya ham stack trace, SQL, credential veya hassas provider payload’ı gösterilmez. Correlation ID üzerinden teknik loga ulaşılır.

## 17. Audit ve izlenebilirlik

Önemli kullanıcı ve sistem aksiyonları audit kaydı üretir:

- Oturum ve güvenlik değişiklikleri.
- Credential ekleme veya değiştirme.
- Bağlantıyı etkinleştirme, pasifleştirme ve silme.
- Ürün, fiyat ve stok değişiklikleri.
- Sipariş durumu ve kargo değişikliği.
- İade kararı.
- Fatura oluşturma, gönderme ve iptal işlemleri.
- Eşleştirme değişiklikleri.
- Veritabanı temizliği.
- İş yeniden deneme veya iptali.

Audit kaydı aktör, tenant, hedef türü, hedef kimliği, işlem, zaman, correlation ID ve güvenli gerekçe bilgisini içerir. Secret veya kişisel verinin gereksiz açık kopyası audit kaydına yazılmaz.

## 18. Ortam kuralları

Stage ve Production bağlantıları birbirinden kesin olarak ayrılır.

- Stage credential’ı Production isteğinde kullanılamaz.
- Stage verisi Production verisiyle aynı bağlantı kapsamında birleşmez.
- Production dış yazmaları ayrıca yetki ve özellik bayrağı kontrolünden geçer.
- Desteklenmeyen Stage senaryoları gerçek işlem yapılmış gibi raporlanmaz.
- Test siparişi veya fixture yalnız resmî Stage koşullarında oluşturulur.

## 19. Planlanan fonksiyonların yönetimi

Henüz adapter, API sözleşmesi veya doğrulanmış sağlayıcı desteği olmayan fonksiyonlar aktif işlem olarak sunulmaz. Planlanan özellikler kod seviyesinde feature flag ve yetenek denetimi arkasında tutulur.

Planlanan başlıca geliştirmeler:

- Yeni pazaryeri adapter’ları.
- Otomatik kategori, marka ve özellik eşleme önerileri.
- Güven skorları ve toplu eşleme onayı.
- Toplu sipariş iptali ve satır bazlı iptal.
- Toplu iade kararları.
- Toplu ve otomatik faturalandırma politikaları.
- Satış, iade, iptal ve kargo performans raporları.
- Bildirim ve alarm merkezi.
- Kaydedilmiş filtre görünümleri.
- Gelişmiş organizasyon ve rol yönetimi.

## 20. Kabul kriterleri

Sistem aşağıdaki koşullar sağlandığında bu belgeye uygun kabul edilir:

1. Liste ekranları açılışta doğrudan Trendyol API’sine bağımlı değildir.
2. Ürün, sipariş, iade ve fatura listeleri yerel PostgreSQL veritabanından okunur.
3. Toplam kayıt sayıları 200 ile sınırlandırılmaz.
4. 20, 50, 100 ve 200 sayfa boyutları yalnız gösterilecek satır miktarını değiştirir.
5. Genel toplam ve filtrelenmiş toplam ayrı hesaplanır.
6. Yeni ve değişmiş kayıtlar watermark ve güvenlik örtüşmesiyle artımlı alınır.
7. Webhook olayları idempotent işlenir ve periyodik eşitleme devam eder.
8. Kullanıcı kaynak, son eşitleme, sonraki kontrol ve iş sonucunu görebilir.
9. Dış işlemler doğrulanmadan başarılı sayılmaz.
10. `İşleme Al` ve kargo değiştirme işlemleri gerçek sağlayıcı akışına bağlanır.
11. Sipariş, paket, ürün ve satır iptalleri birbirinden doğru ayrılır.
12. Aynı paket için ikinci satış faturası oluşturulamaz.
13. Ürün özellikleri ve seçenekler ayrı adımlarda yönetilir; en fazla iki seçenek bulunur.
14. Büyük kategori ve özellik değer kümeleri tek seferde sınırsız yüklenmez.
15. Credential ve secret değerleri kaydedildikten sonra açık biçimde gösterilmez.
16. Veritabanı temizliği ve bağlantı derin silme transaction içinde çalışır.
17. Başarısız yıkıcı işlem kısmi veri kaybı bırakmaz.
18. Bütün önemli işlemler correlation ID ve audit kaydıyla izlenebilir.
19. Stage ve Production işlemleri birbirine karışmaz.
20. Desteklenmeyen veya planlanan özellikler çalışan işlem gibi davranmaz.

