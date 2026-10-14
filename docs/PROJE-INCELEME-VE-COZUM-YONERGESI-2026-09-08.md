# Ravencia — Proje incelemesi ve çözüm yönergesi

Tarih: 8 Eylül 2026

## Kapsam ve kanıt sınırı

Kaynak kod incelemesi, frontend üretim derlemesi, bundle kontrolü, backend testleri ve canlı tarayıcı DOM ölçümleri yapıldı. Canlı İadeler ve Dashboard incelendi. Bu rapor bütün sayfa, popup, rol ve veri kombinasyonlarının eksiksiz test edildiği veya güvenlik denetiminin tamamlandığı anlamına gelmez. İnceleme sırasında uygulama kodu ve canlı kayıtlar değiştirilmedi.

Dosya yolları depo köküne göredir. Satır numaraları incelenen sürüme aittir.

## Kontrol sonuçları

- Frontend `npm run build`: başarılı.
- `npm run check:bundle`: başarılı; ana JS 415,7 KiB / 450 KiB; CSS 215,5 KiB / 650 KiB. Bütçe geçişi gerçek kullanıcı performansının kanıtı değildir.
- `dotnet test MarketplaceHub.sln -c Release --no-restore`: 118 başarılı, 0 başarısız, 0 atlanan.
- Debug testi çalışan yerel API'nin DLL kilidi nedeniyle derleme aşamasında durdu. Yerel servis kesilmeden Release ile doğrulandı; bu bir test assertion hatası değildir.
- Canlı İadeler ekranında belge düzeyinde yatay taşma görülmedi. Bu, iç bileşenlerin kesilmediğini veya tüm popup'ların doğru olduğunu kanıtlamaz.

## Öncelikli bulgular

### 1. Grafik sıfır çizgisi ve çubuk tabanı farklı — P1, canlıda doğrulandı

Kanıt: Dashboard, 1910 CSS piksel genişlikte gridlines alt sınırı 875,70; bar-wrap alt sınırı 861,73. Yaklaşık 13,97 px fark var. Çubuklar sıfır çizgisinin üzerinde başlıyor.

Kaynak: `src/MarketplaceHub.Web/src/styles/design-system.css:291–302`. Eksen, grid ve çubuklar farklı yükseklik/padding hesapları kullanıyor.

Çözüm: Tek bir çizim alanı yüksekliği tanımlayın. Eksen ve çubuklar aynı plot dikdörtgenini kullansın; tarih satırı bunun dışında kalsın. Değer ölçeklemesini de aynı yükseklikten hesaplayın. Sorunu negatif margin ile maskelemeyin.

Kabul: 0, düşük ve maksimum değerlerle çubuk tabanı–sıfır çizgisi farkı en fazla 1 px; günlük/7/30 günlük ve özel tarih aralıklarında aynı sonuç.

### 2. Grafik tarihleri kesiliyor — P1, canlıda doğrulandı

Kanıt: “10 Ağu” etiketi 35 px gerektirirken hücresi 21 px; “1 Eyl” 23 px gerektirirken hücresi 21 px. `overflow:hidden` ve `text-overflow:clip` içeriği gizliyor.

Kaynak: `src/MarketplaceHub.Web/src/styles/design-system.css:303`.

Çözüm: Tarihleri çubuk hücrelerine zorla sıkıştırmak yerine kullanılabilir genişliğe göre eksen işaret sıklığı belirleyin. Bütün veri noktaları korunsun; seyrek eksen etiketleri kullanılıyorsa tam tarih tooltip ve erişilebilir açıklamada bulunsun. Ay geçişi açıkça gösterilsin.

Kabul: 1280/1440/1920 genişliklerde yatay grafik scrollbar'ı ve kırpılan görünür etiket olmasın; ilk/son tarih ve ay geçişi anlaşılır olsun.

### 3. Grafik tooltip kesilme riski — P2, kaynak kod riski

Kaynak: aynı CSS dosyası `296,306–307`. Plot `overflow:hidden`; tooltip çubuğun üstüne ve iki yanına taşacak şekilde konumlanıyor. Kenar ve yüksek değerlerde kesilme ihtimali var; bu incelemede hover senaryosu canlıda doğrulanmadı.

Çözüm: Tooltip'i sınır hesabı yapan ortak popover/portal ile gösterin. Sol/sağ ve üst sınıra göre yön değiştirsin. Klavye odağıyla da erişilsin.

Kabul: İlk, son ve maksimum çubukta tooltip'in tamamı görünür ve metni okunur.

### 4. Dar menüde pin hâlâ gösteriliyor — P2, kaynakta doğrulandı

Kaynak: `src/MarketplaceHub.Web/src/styles/design-system.css:101`, `src/MarketplaceHub.Web/src/app/App.tsx:43`. Collapsed pin açıkça `display:inline-grid`.

Çözüm: Kullanıcının dar durumda görünmemesi isteğine uygun olarak pin yalnızca geniş durumda gösterilsin. Marka ve ikon için sabit kolon/ankraj kullanın; metni genişlik değiştirerek ikonları itmeden açın.

Kabul: Dar durumda pin görünmez; hover ile açıldığında görünür; açılış/kapanışta ikonların x konumu değişmez.

### 5. Menü fare ayrıldıktan sonra odak nedeniyle açık kalabilir — P2, kaynakta doğrulanan davranış yolu

Kaynak: `src/MarketplaceHub.Web/src/app/App.tsx:16–19,42`. Genişleme `pinned || hovered || focusWithin`; fare ayrılınca yalnızca hovered temizleniyor. Tıklanan link odağı içeride tuttuğunda menü açık kalabilir.

Çözüm: Fare etkileşimi ile klavye odak davranışını ayırın. Klavyeyle gezinme erişilebilirliğini koruyun; sadece fare tıklamasından kalan odak menüyü kalıcı açmasın. Kullanıcı isteğiyle uyumlu durum makinesi tanımlayın.

Kabul: Pinsiz durumda fare ayrılınca kapanır; klavyeyle gezinirken kullanılabilir kalır; pinli durumda fare ayrılması kapatmaz. Canlı etkileşim testiyle ayrıca doğrulanmalı.

### 6. Sipariş fiyatları ürün satırlarıyla aynı düzeni paylaşmıyor — P1, kaynakta doğrulandı

Kaynak: `src/MarketplaceHub.Web/src/features/marketplace/pages.tsx:645,976`; `src/MarketplaceHub.Web/src/styles/design-system.css:459–461`. Ürün fiyatları ayrı hücrede iki kolona diziliyor. Birden fazla ürünün dikey bilgi satırlarıyla fiyatların yatay dizilimi eşleşmiyor.

Çözüm: Ürün ve fiyat satırlarını ortak satır modeli/subgrid ile ilişkilendirin. Her fiyat kendi ürününün yüksekliği boyunca ortalansın. İade tablosu ortak sınıfı kullandığı için birlikte test edin.

Kabul: 1/2/3 ürünlü siparişte hangi fiyatın hangi ürüne ait olduğu açık; uzun ürün başlığı satır yüksekliğini artırdığında hizalama korunur.

### 7. Hızlı fiyat/stok düzenleme klavyeyle erişilemiyor — P1, kaynakta doğrulandı

Kaynak: `src/MarketplaceHub.Web/src/features/catalog/pages.tsx:573–574`. Etkileşimli hücreler yalnızca `onClick` içeren div; doğal klavye davranışı yok.

Çözüm: Görsel olarak hücreyle uyumlu gerçek button kullanın. Anlamlı erişilebilir ad ve görünür focus stili ekleyin. Popup kapanınca odağı tetikleyiciye geri verin.

Kabul: Tab ile erişilir; Enter ve Space ile aynı düzenleyici açılır; Escape ile kapanır; fare gerektirmez.

### 8. Varyant formunda alan kimliği ve sıralama erişilebilirliği eksik — P2, kaynakta doğrulandı

Kaynak: `src/MarketplaceHub.Web/src/features/catalog/pages.tsx:1968,1971`. Çok sayıda input görsel kolon başlığına bağlı ancak programatik label ilişkisi yok; sıralama tutacağı pointer olayıyla çalışıyor.

Çözüm: Her girdinin erişilebilir adı varyant + alan içersin. Semantik tablo veya uygun başlık ilişkileri kullanın. Sürüklemeye ek olarak yukarı/aşağı taşıma kontrolleri sağlayın. Satır hatasını ilgili alana bağlayın.

Kabul: Ekran okuyucu “Mor / L, stok” gibi ayrıştırılabilir ad okur; sıralama fare olmadan yapılır.

### 9. Tarayıcı prompt/confirm pencereleri tasarım bütünlüğünü bozuyor — P2, kaynakta doğrulandı

Kaynak: `src/MarketplaceHub.Web/src/app/App.tsx:372,377,382,413,505`; `src/MarketplaceHub.Web/src/features/catalog/pages.tsx:919,924,2029`.

Çözüm: Ortak onay dialog'u ve alanlı form dialog'u oluşturun. Tehlikeli eylemde kayıt/etki açıkça yazılsın; iptal güvenli varsayılan olsun. Focus trap, Escape, odağı geri verme, loading ve hata durumları ortak çözülsün. Tema default kaydının silinememesi yalnızca dialog'a değil işleme de bağlansın.

Kabul: Stok nedeni, link/görsel ekleme, tema ve oturum silme aynı bileşen ailesini kullanır; çift gönderim engellenir.

### 10. HTTPS görsel girişi ile üretim CSP politikası uyuşmayabilir — P2, yapılandırma/UI uyumsuzluğu

Kaynak: `src/MarketplaceHub.Web/src/features/catalog/pages.tsx:1885` HTTPS adresleri kabul eden arayüz; `deploy/caddy/Caddyfile.production:13` dış görseller için yalnızca `https://cdn.dsmcdn.com` izni.

Etki: Başka bir HTTPS alanından verilen görselin tarayıcıda yüklenmesi üretimde CSP tarafından engellenir. Kaydetme API'sinin kabul politikası ayrıca doğrulanmalıdır.

Çözüm: Desteklenen kaynakları açıkça tanımlayın ve girişte doğrulayın; tercihen kontrollü medya yükleme/depolama kullanın. Proxy tercih edilirse SSRF, boyut, MIME ve yönlendirme kontrolleri zorunlu olsun. CSP'yi gerekçesiz wildcard'a çevirmeyin.

Kabul: Desteklenen görsel yerel ve canlıda aynı görünür; desteklenmeyen adres kullanıcıya kaydetmeden açıklanır.

### 11. Zengin HTML açıklama güven sınırı doğrulanmalı — P1 inceleme önceliği, güvenlik açığı kanıtlanmadı

Kaynak: `src/MarketplaceHub.Web/src/features/catalog/pages.tsx:944`: `dangerouslySetInnerHTML` ile açıklama render ediliyor; HTML düzenleme de açık. Bu taramada açıklamaya uygulanan bir HTML sanitizer doğrulanamadı. Bu, tek başına çalışır XSS kanıtı değildir.

Çözüm: API yazma, pazar yeri import ve mevcut kayıt okuma yollarını izleyin. İzinli HTML etiket/öznitelik/URL protokolü politikası oluşturun; güvenilir sanitizer kullanın. CSP'yi tek savunma kabul etmeyin.

Kabul: İzole test ortamında event handler, tehlikeli URL, iframe/form ve bozuk HTML örnekleri güvenli hale gelir; normal ürün biçimlendirmesi korunur. Canlı kayıtlara saldırı örnekleri yazılmamalı.

### 12. Frontend regresyon kontrolü eksik — P1 süreç bulgusu

Kaynak: `src/MarketplaceHub.Web/package.json`: build/typecheck/bundle var; frontend test komutu yok. Backend testlerinin geçmesi menü, modal ve grafik davranışlarını kapsamıyor. Bu bulgu repo dışında hiçbir test bulunmadığı iddiası değildir.

Çözüm: UI davranış ve görsel regresyon paketi ekleyin. CI'da kritik akışları çalıştırın. Yalnızca sayfa açılmasını veya belge scrollWidth ölçümünü başarı kabul etmeyin; iç bileşenleri ve durum değişimlerini kontrol edin.

Kabul: Grafik tabanı/tarihleri, sidebar hover/pin, modal focus, çok ürünlü fiyat hizası ve klavye hızlı düzenleme bozulduğunda test başarısız olur.

## Uygulama sırası

1. Grafik geometrisi, fiyat–ürün eşleşmesi ve klavye erişimini düzeltin; HTML güven sınırını araştırın.
2. Sidebar durum modelini ve ortak dialog/popover/input bileşenlerini düzenleyin.
3. Medya kaynak politikasını yerel–üretim arasında eşitleyin; varyant formunu erişilebilir hale getirin.
4. Aşağıdaki ekran matrisini tamamlayıp görsel regresyon testlerini yayın kapısı yapın.

## Kalan ekran doğrulama matrisi

Her ekran 1280/1440/1920 genişlik, %100 ve %125 zoom, pinli/pinsiz menü ile test edilmeli. Mobil kapsam ayrıca kararlaştırılmalı.

| Ekran | Kontrol senaryoları |
| --- | --- |
| Giriş | Focus/autofill renkleri, yerel logo, büyük logo, dikey ortalama, hata/loading |
| Dashboard | Tüm tarih aralıkları, 0/tek/çok veri, taban çizgisi, etiket/tooltip |
| Ürün listesi | Metrik ikonları, durum/aksiyon hizası, eşleşme noktaları, hızlı düzenleme |
| Ürün detayı | Özellik dropdown'ları, 2:3 medya, seçenek grupları, 42+ varyant, hata durumları |
| Siparişler | Filtre ve toplu seçim kapsamı, çok ürün, fiyat eşleşmesi, kargo logosu, yazdırma/dropdown |
| İadeler | Ortak sipariş CSS regresyonu, çok ürün, filtre ve durum menüleri |
| Platformlar | Sola hizalı menü, modal iç boşlukları, uzun içerik, güvenli iptal |
| Eşleştirmeler | Sekmeler, uzun platform adları, form gap/hizası, boş/dolu ve validation |
| Ayarlar | Tema kaydet/sil, default koruması, yeniden yüklemede kalıcılık |
| Faturalar / İşlem Takibi | Uzun satırlar, filtre, detay popup, loading/boş/hata durumları |

Bu matrisin tamamı bu incelemede çalıştırılmadı; kalan doğrulama kapsamıdır. Önceki yorumlar otomatik olarak kapatılmış sayılmamalı.

## Uygulama için kullanılacak talimat

“Bu rapordaki bulguları öncelik sırasıyla ele al. Önce her bulgu için başarısız durumu yeniden üret ve kanıtını kaydet. Mevcut tema tokenlarını ve iş kurallarını koru; geniş kapsamlı CSS override yığmak yerine ilgili bileşenin düzen modelini düzelt. Veri silme, pazar yerine yazma veya canlı işlem tetikleme. Her değişiklikte belirtilen kabul kriterlerini ve aynı sınıfı kullanan diğer ekranları test et. Build ve backend testlerine ek olarak tarayıcı davranışını doğrula. Doğrulanmayan maddeyi tamamlandı gösterme. Canlıya alma ayrı bir adım olsun.”
