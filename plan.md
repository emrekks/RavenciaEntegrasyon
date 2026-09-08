# Ravencia UI/UX tasarım planı

Bu dosya, Ravencia’nın yoğun operasyon ekranlarını ortak, okunaklı ve responsive bir SaaS arayüzüne dönüştürmek için kaynak plandır. Her madde kod, gerçek tarayıcı görünümü ve yayın doğrulamasıyla ayrı ayrı kontrol edilmeden tamamlanmış kabul edilmez.

## 1. Görsel yön ve hedef

Referansların ortak yaklaşımı Ravencia’nın yoğun operasyon ekranlarına uyarlanacaktır:

- Düzenli uygulama çerçevesi, okunaklı metrikler ve belirgin içerik hiyerarşisi.
- Yoğun verinin sakin satırlar, ince ayırıcılar ve kontrollü durum renkleriyle sunulması.
- Bütünlüğü sağlayan ortak bileşenler, tipografi ve mobil uyarlama.
- Ferah kart yerleşimi, güçlü ana değerler ve sade ikincil aksiyonlar.
- Grafikler ve özetler arasında dengeli yerleşim ve tutarlı vurgu kullanımı.

Varsayılan tasarım koyu grafit zemin, ölçülü nötr yüzeyler ve mor vurgu üzerine kurulacaktır. Yeşil, amber ve kırmızı işlevsel durumları gösterecektir. Derinlik dekoratif parlamalar yerine okunabilirlik, yüzey ayrımı ve boşluklarla sağlanacaktır.

Başarı ölçütü: Kullanıcı sayfa değiştirdiğinde kontrolleri yeniden öğrenmeden ana aksiyonu, durumu ve önemli bilgiyi hızlıca bulabilmelidir.

## 2. Bütünlüğü sağlayan tasarım sistemi

### Uygulama çerçevesi

- Sol menü, üst bar, sayfa başlığı ve içerik başlangıcı bütün sayfalarda aynı ölçülere uyacaktır.
- Masaüstünde menü daraltılabilir ve sabitlenebilir; mobilde açılır menü kullanılacaktır.
- Menüde aktiflik, ikon ve metin tek bir bütün olarak gösterilecektir.

### Tipografi ve boşluk

- Tek font ailesi ve belirlenmiş başlık, bölüm başlığı, gövde ve yardımcı metin ölçeği kullanılacaktır.
- Ana metin 14 px, yardımcı metin 12–13 px olacaktır.
- Boşluklar 4/8/12/16/24/32 px ölçeğine bağlanacaktır.
- Finansal değerlerde eşit genişlikli rakamlar kullanılacaktır.

### Kontroller

- Normal alanlar ve butonlar 40 px; tablo içi kontroller 32–36 px olacaktır.
- Dokunmatik kullanımda hedef alanı en az 44 px olacaktır.
- Ana, ikincil, sade ve tehlikeli aksiyonların görünümleri ortak olacaktır.
- Her ekranın baskın ana aksiyonu açıkça ayrılacaktır.

### Sekmeler

- Aynı gezinme seviyesindeki sekmeler bütün sayfalarda aynı bileşeni kullanacaktır.
- Bölüm sekmeleri belirgin seçili çizgi ve sakin arka planla gösterilecektir.
- Adetler ayrı sayaç olarak hizalanacaktır.
- Grafik dönemi gibi kısa seçimlerde kompakt segmented control kullanılacaktır.

### Tablolar ve durumlar

- Metin sola, tutarlar sağa hizalanacaktır.
- İşlemler sağda sabit bir grup oluşturacaktır.
- Durum rozetleri içerik genişliğinde, açık metinli ve anlamına uygun renkte olacaktır.
- Hover etkisi hafif kalacak; rozet ve aksiyonların okunabilirliği korunacaktır.

### Formlar ve açılır yapılar

- Etiketler alanların üstünde, yardım metni altında olacaktır.
- Kısa ayar ve onaylar dialog; liste bağlamını koruyan detaylar drawer; küçük aksiyon grupları menu kullanacaktır.
- Uzun düzenleyicilerde başlık ve alt aksiyonlar görünür kalacak, yalnız içerik alanı kayacaktır.
- App Shell, Page, Field, Tabs, Table, Status, Dialog ve Drawer davranışları SaaS UI referansı alınarak mevcut React uygulamasındaki ortak bileşenlerle kurulacaktır. Uygulama başka bir UI kütüphanesine taşınmayacaktır.

## 3. Sayfaların tasarım karşılığı

| Alan | Tasarım kararı |
| --- | --- |
| Dashboard | Önem sırasına göre metrikler; gelir özeti grafik başlığında; gerçek verilere göre ölçeklenen eksen; dengeli özet ve işlem kartları. |
| Ürünler | Ortak filtre çubuğu; iki satıra ayrılmış ürün adı ve altında sabit model kodu; metinli durum rozetleri; hizalı fiyat ve işlemler. |
| Siparişler | Düzenli etiket ve sayaçlara sahip sekmeler; hizalı arama/filtreler; tutarların birlikte gösterimi; çerçeveli takip ve fatura aksiyonları. |
| İadeler | Siparişlerle aynı tablo dili; okunaklı durumlar; sade detay aksiyonu; bilgiyi bastırmayan hover. |
| Entegrasyonlar | Eş yapıda kartlar; bağlantı ayarları dialog içinde; dış yazma kontrolü ayarlar içinde; veri gizleme için açıklamalı onay dialogu. |
| Eşleştirmeler | Hizalı form satırları; sola hizalı, masaüstünde iki sütunlu özellik listeleri; başlık altında platform sekmeleri; sağda düzenli işlemler. |
| Gelişmiş eşleştirme | Kompakt platform görselleri; kategori seçimi ve ilerleme özeti üstte; alan eşleştirmeleri okunaklı satırlarda; kontrollü kaydırma. |
| Diğer sayfalar | Aynı başlık, filtre, tablo, form ve geri bildirim bileşenlerine geçiş. |

Veri eksikliği, yüklenme, hata ve işlem başarısı tasarımın parçasıdır. Boş alanlar açıklayıcı durumlarla gösterilecek; uzun metinler ve çok sayıda kayıt yerleşimi bozmayacaktır.

## 4. Responsive davranış ve kabul koşulları

- 1920×1080: Dengeli içerik genişliği, gereksiz boşluk bırakmayan listeler ve aynı çizgide başlayan kontroller.
- Tablet — 1024 ve 768 px: Azalan kart sütunları, yeniden yerleşen filtreler, erişilebilir menü ve aksiyonlar.
- Telefon — 390 ve 360 px: Tek sütunlu formlar, açılır filtreler, ekrana uyan dialoglar ve dokunmaya uygun kontroller. Yoğun tablolar gerektiğinde kendi alanında yatay kayacaktır.

Tamamlanma koşulları:

- Yerel uygulamada ana sayfalar, detaylar ve ilgili açılır yapılar gerçek tarayıcıda incelenecektir.
- Sayfa genelinde yatay taşma, kesilen aksiyon, üst üste binen etiket veya okunmayan durum bulunmayacaktır.
- Uzun ürün adı, çok basamaklı tutar, boş liste ve yoğun kayıt senaryoları kontrol edilecektir.
- Klavye odağı, dialog kapatma ve dar ekran davranışları doğrulanacaktır.
- Yeni `!important` eklenmeyecek; stil çakışmaları ortak kurallarda çözülecektir.
- Kullanıcının kaydettiği özel tema tercihleri korunacak; yeni varsayılan palet ve bileşenler bu tercihlerle de okunabilir olacaktır.

## 5. Yapım ve yayın planı

1. Yerel oturumla eksik sayfa, detay ve açılır yapı incelemesini tamamla.
2. Ortak token, App Shell, sayfa başlığı, kontrol, sekme, tablo, durum, dialog, drawer ve geri bildirim dilini kur.
3. Dashboard, ürünler, siparişler, iadeler, entegrasyonlar, eşleştirmeler ve gelişmiş eşleştirmeyi sırayla dönüştür.
4. Boş, yükleniyor, hata ve başarı durumlarını bütün dönüşümlerde kontrol et.
5. 1920, 1024, 768, 390 ve 360 px görünümlerinde gerçek tarayıcı kontrolü yap; klavye ve dialog davranışlarını doğrula.
6. Frontend production build, API build/test ve `git diff --check` çalıştır; `!important` taramasını doğrula.
7. Commit, `origin/main` push, production deploy ve `/health/ready` kontrolünü tamamla.

API ve iş kurallarında değişiklik öngörülmez; çalışma arayüz bileşenleri, yerleşim ve etkileşimler üzerindedir.

## 6. Uygulama kanıtı

Bu bölüm, 8 Eylül 2026 tarihli uygulama turunun durumunu kaydeder. Bir madde yalnızca kod ve gerçek tarayıcı kanıtı birlikte varsa `[x]` yapılır.

- [x] Görsel yön ve varsayılan palet
- [x] Ortak uygulama çerçevesi ve responsive menü (masaüstü kontrolü tamam; dar ekran maddesi aşağıda açık)
- [x] Tipografi, boşluk ve kontrol ölçüleri
- [x] Ortak sekme ve sayaç davranışı
- [x] Tablo, durum ve işlem hizaları
- [x] Dialog, drawer, menu ve klavye davranışları
- [x] Dashboard
- [x] Ürünler ve ürün detayı
- [x] Siparişler
- [x] İadeler
- [x] Entegrasyonlar ve bağlantı ayarları diyaloğu
- [x] Eşleştirmeler ve gelişmiş eşleştirme
- [x] Diğer sayfalar ve veri durumları
- [ ] 1920/1024/768/390/360 responsive tarayıcı kontrolü
- [x] Frontend/API build, test ve stil doğrulaması
- [x] Commit, push, production deploy ve sağlık kontrolü

### Denetim notları

- Kaynak proje `V3-Ravencia-Entegrasyon`; masaüstündeki Git dışı kopya bu çalışmanın kaynağı değildir.
- Yerel frontend `http://127.0.0.1:5197`, API `http://127.0.0.1:5192` üzerinden, gerçek oturumla kontrol edildi.
- Kontrol edilen rotalar: dashboard, ürün listesi ve detayı, siparişler, iadeler, entegrasyonlar, kategori/marka eşleştirmeleri, faturalar, arka plan işlemleri, ayarlar ve katalog/aktarımı/stok/gönderi sayfaları.
- Yerel tarayıcı kanıtı `1280×720` ve `devicePixelRatio: 1` ölçüsündedir. Bu ortamda viewport değiştirme yetkisi olmadığı için 1920, 1024, 768, 390 ve 360 px gerçek tarayıcı doğrulaması tamamlanmış sayılmamıştır; CSS kırılım kuralları uygulanmış ve statik olarak taranmıştır.
- Tüm kontrol edilen rotalarda `document.body.scrollWidth <= innerWidth` ve ana içerik taşması kontrolü sağlandı; açılışta hata/uyarı görünmedi. Sekmelerde ok tuşları, Home ve End; entegrasyon ayarlarında Escape ve gövde kaydırma kilidi doğrulandı.
- `d8b2a53` commit'i `origin/main` üzerinde yayınlandı; sunucu aynı commit'i çalıştırıyor. `https://panel.ravencia.com/health/ready` HTTP 200 döndü, kök HTML ve güncel JavaScript asset'i HTTP 200 ile alındı; API, worker, Caddy ve PostgreSQL container'ları sağlıklı.
