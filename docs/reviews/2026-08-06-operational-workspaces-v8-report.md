# Ravencia Entegrasyon v8 — Operasyon Ekranları Tamamlama ve Doğrulama Raporu

**Tarih:** 6 Ağustos 2026  
**Dal:** `feature/operational-workspaces-v8`  
**Kapsam:** Siparişler, iadeler, faturalar, ürünler, ana sayfa raporları ve Trendyol/Trendyol E‑Faturam operasyon akışları

## 1. Sonuç özeti

Projedeki temel servis ve ekran iskeletleri korunarak operasyon alanları gerçek yerel verilere bağlandı. Sipariş, iade, fatura ve ürün kartları ortak kurumsal görünüm altında ayrıntılandırıldı; eksik veri sözleşmeleri genişletildi; fatura tekrarını engelleyen sunucu tarafı koruma eklendi; iade ret nedenleri Trendyol’dan canlı çekilecek biçimde bağlandı; iade sonrası stok kabulü kontrollü hale getirildi.

Dış platforma yazan işlemler mevcut güvenlik modeline uygun olarak **connection + capability kanıtı + yazma anahtarı + açık kullanıcı onayı** olmadan çalışmaz. Kod içinde doğrulanmamış bir dış işlem çalışıyormuş gibi gösterilmedi.

## 2. Tamamlanan alanlar

### 2.1 Siparişler

- Beş operasyon sekmesi eklendi: **Yeni, İşleme Alınmış, Kargoya Verilmiş, Teslim Edilmiş, İptal Edilmiş**.
- Platform logosu/kodu, müşteri, sipariş türü, mikro ihracat etiketi, sipariş zamanı, termin, 24 saat kritik uyarısı, tutar, ürün adedi, fatura durumu, kargo firması ve takip numarası tek kartta toplandı.
- İptal edilmiş siparişler kırmızı; mikro ihracat siparişleri mavi sol sınırla ayrıldı.
- Kart içi açılır detay ve ayrı sipariş detay sayfası; müşteri adı, e-posta, TCKN/VKN, teslimat ve fatura adresi, ürün görseli, SKU, barkod, model kodu, seçenek imzası, miktar, fiyat ve paket bilgilerini gösterir.
- Paket üzerinden **Fatura kes** akışı bağlandı.
- Barkod/etiket ve kargo işlemleri yalnız ilgili capability kanıtlandığında mevcut shipment işlem yüzeyinden sunulur.
- Trendyol yanıtında termin alanı bulunmazsa tarih üretilmez; “Termin bilgisi gelmedi” gösterilir.

### 2.2 İadeler

- Altı sekme eklendi: **İade Talepleri, Kargo Aşamasında, Aksiyon Bekleyen, Onaylanmış, Reddedilmiş, İnceleniyor**.
- Müşteri, sipariş, sipariş tarihi, otomatik işlem süresi, kargo, takip no, ürün görseli, ürün adedi, tutar ve durum bilgileri kartlara taşındı.
- Aksiyon bekleyen iadelerde Trendyol **onay** ve **ret** iş akışları bağlandı.
- Ret nedenleri sabit liste yerine Trendyol `claim-issue-reasons` servisinden çekilir.
- Ret açıklaması ve gerekli durumlarda güvenli özel depoya kanıt yükleme desteklenir.
- İade onayı yerel stoğu doğrudan artırmaz. Fiziksel kontrolden sonra satır bazında:
  - **Satılabilir:** stoğu artırır.
  - **Karantina:** stoğu artırmaz.
  - **Hasarlı:** stoğu artırmaz.
  - **Teslim alınmadı:** stoğu artırmaz.
- Bu stok kararı audit edilebilir stok hareketi olarak kaydedilir.

### 2.3 Faturalar

- Üç sekmeli yeni çalışma alanı oluşturuldu: **Faturalandırılmamışlar, Faturalandırılmışlar, Süresi Yaklaşanlar**.
- Paket, sipariş, müşteri, sipariş zamanı, teslim durumu, teslim zamanı, yasal son tarih, ürün sayısı/görseli, tutar, kargo ve takip bilgileri aynı kartta gösterilir.
- Teslimden itibaren yedinci gün son tarih olarak hesaplanır; beşinci gününü dolduran ve faturası olmayan kayıtlar “Süresi Yaklaşanlar” alanına girer.
- Aynı sipariş paketine ikinci satış faturası oluşturulması sunucu tarafında `INVOICE_ALREADY_EXISTS` ile engellenir.
- Faturası bulunan kayıtta yeniden “Fatura kes” yerine mevcut faturayı açma bağlantısı gösterilir.
- Trendyol’a kalıcı fatura linki gönderme akışı mevcut capability ve güvenlik kapılarıyla korunmaya devam eder.

### 2.4 Ürünler

- Ürün kartlarına görsel, ad, model kodu, genel stok, başlangıç fiyatı, varyant sayısı ve aktif platformlar eklendi.
- Arama; ürün adı, model, SKU ve barkod üzerinden çalışır.
- Durum, platform ve stok filtreleri eklendi.
- Üç nokta menüsünden ürün düzenleme ve hızlı stok/fiyat alanına erişilir.
- Varyant bazında hızlı stok düzenleme mutlak değer yerine stok hareketi/delta üreterek audit izi bırakır.
- Varyant fiyatı sürüm kontrollü kanal teklifi olarak oluşturulur veya güncellenir; eşzamanlı değişikliklerin birbirini ezmesi önlenir.
- Yeni ürün formuna yaprak kategori, marka, görsel URL, SKU, barkod, model kodu, renk, beden, ağırlık, en/boy/yükseklik, başlangıç stoğu, platform fiyatı, KDV ve güvenlik stoğu eklendi.
- Kısmi oluşturma hatasında ürünün oluşturulduğu fakat görsel/stok/fiyat adımlarından birinin tamamlanmadığı kullanıcıya bildirilir.

### 2.5 Ana sayfa ve raporlar

- Bekleyen sipariş sayısı ve platform dağılımı.
- Geciken sipariş sayısı.
- Bugünkü ve bu ayki sipariş sayısı.
- Bekleyen iade, faturalandırılmamış ve süresi yaklaşan fatura sayısı.
- Kargo bazlı operasyon dağılımı.
- Düşük/yok stok sayısı ve en düşük stoklu ürün listesi.

“Ürün bazlı rapor” mevcut veriyle stok riski şeklinde uygulanmıştır. Ürün satış kârlılığı için alış maliyeti, pazaryeri komisyonu, kargo maliyeti, indirim katkıları ve iade maliyetleri ayrıca modellenmelidir.

## 3. Trendyol’da desteklenen ve yerel sorumluluk olan işlemler

| İşlem | Durum | Uygulama kararı |
|---|---|---|
| Sipariş/paket okuma ve durum takibi | Trendyol API’de var | Yerel sipariş/paket tablolarından operasyon ekranına taşındı. |
| İade kayıtlarını çekme | Trendyol API’de var | Sekmeler ve ayrıntılar yerel snapshot üzerinden gösterilir. |
| İade onaylama | Trendyol API’de var | Capability ve yazma kapıları arkasında kuyruğa alınır. |
| İade reddetme | Trendyol API’de var | Canlı reason ID, açıklama ve gerekli kanıtla gönderilir. |
| İade sonrası stoğu artırma | Trendyol tarafından otomatik yapılmaz | Fiziksel kalite kontrol sonrası yerel stok kararıdır. |
| Fatura linki gönderme | Trendyol API’de var | Kalıcı HTTPS belge bağlantısı ve paket kimliğiyle gönderilir. |
| Aynı pakete ikinci fatura | Trendyol 409 döndürebilir | Yerelde daha erken engellenir. |
| Kargo etiketi / paket aksiyonları | Taşıyıcı ve capability’ye bağlı | Doğrulanmamış buton gösterilmez; destek kanıtı gerekir. |
| Kargo firması değiştirme | Paket durumu/servis desteğine bağlı | Genel shipment action yüzeyinde capability kanıtı varsa kullanılmalıdır; sabit ve sahte bir işlem eklenmedi. |
| Ürün yayını | Product V2 kullanılmalı | Bağlantı V2 ve güvenli yayın işi korunmuştur; üretim öncesi tüm ürün rotaları V2 için tekrar doğrulanmalıdır. |

## 4. Gözden kaçabilecek ek ihtiyaçlar

1. **Toplu işlemler:** Çoklu sipariş seçimi, toplu fatura, toplu etiket ve toplu stok/fiyat güncelleme.
2. **Sunucu tarafı filtreleme ve sayfalama:** Ekranlar son 200 kaydı kullanır; yüksek hacimde API filtreleri ve cursor sayfalama UI’a taşınmalı.
3. **Rol bazlı yetki:** İade reddi, stok kabulü, fatura kesme ve kargo değişimi ayrı izinlere bağlanmalı.
4. **PII güvenliği:** TCKN/VKN, adres ve e-posta için maskeleme, görüntüleme audit’i ve saklama süresi politikası.
5. **Fatura yaşam döngüsü:** İptal/iade faturası, temel fatura itirazı, yeniden iletim, belge arşivi ve mutabakat ekranları.
6. **Mali raporlama:** Komisyon, maliyet, kargo, kampanya katkısı, iadeler ve net kâr.
7. **Kargo operasyonu:** Taşıyıcı servis kesintisi, yeniden etiket, çok kolili paket, desi aşımı ve teslim edilememe nedenleri.
8. **SLA ve takvim:** Hafta sonu/resmî tatil, platform termin kuralları ve saat dilimi testleri.
9. **Erişilebilirlik ve mobil:** Klavye navigasyonu, ekran okuyucu etiketleri ve küçük ekran operasyon testi.
10. **Gözlemlenebilirlik:** Job gecikmesi, başarısız dış istek, rate-limit, fatura son tarih riski ve webhook gecikme alarmları.

## 5. Doğrulama sonuçları

| Kontrol | Sonuç |
|---|---|
| `git diff --check` | **Geçti** |
| TypeScript sözdizimi dönüşümü: `App.tsx`, `F2Pages.tsx`, `F3Pages.tsx`, `F4Pages.tsx` | **Geçti** |
| Değişen 13 C# dosyasında lexical delimiter kontrolü | **Geçti** |
| `scripts/validate-operational-workspaces.py` — 8 kabul grubu | **Geçti** |
| Tam `.NET` build ve test | **Çalıştırılamadı:** çalışma ortamında `dotnet` kurulu değil. |
| Tam React `npm ci`, typecheck ve Vitest | **Çalıştırılamadı:** ortam Node `22.16.0`/npm `10.9.2`; proje Node `24.18.1`/npm `11.12.1` bekliyor ve iç paket önbelleğinde `zod-4.4.3` bulunmuyor. |
| Trendyol Stage dış yazma E2E testi | **Çalıştırılmadı:** Stage credential çalışma ortamına güvenli biçimde enjekte edilmedi ve dış ağ erişimi yok. |

Bu nedenle “tüm testler geçti” iddiası yoktur. Kaynak seviyesi ve statik kabul kontrolleri geçti; gerçek build, veritabanı, browser ve Stage yazma senaryoları uygun CI/Stage ortamında tamamlanmalıdır.

## 6. Üretim öncesi zorunlu kontrol listesi

- Node `24.18.1`, npm `11.12.1` ve proje lock dosyasıyla `npm ci`.
- `npm run typecheck`, `npm test`, `npm run build`.
- .NET SDK ile `dotnet restore`, `dotnet build --no-restore`, `dotnet test`.
- PostgreSQL migration/schema testleri.
- Fake adapter ve browser E2E testleri.
- Trendyol Stage’de salt-okunur sipariş/iade çekme.
- Stage’de ayrı test paketinde iade onay, iade ret, kanıt yükleme ve sonuç reconciliation.
- Stage’de fatura taslağı, provider gönderimi, kalıcı PDF, Trendyol link teslimi ve tekrar gönderim 409 senaryosu.
- Etiket/kargo aksiyonları için taşıyıcı bazlı capability fixture SHA-256 kaydı.
- Product V2 create/update/archive/price-inventory uçtan uca doğrulaması.

## 7. Değişiklik alanları

- Application sözleşmeleri: F2/F3/F4.
- Trendyol mapper, endpoint tanımları ve HTTP adapter.
- Catalog, inventory, sales ve billing servisleri.
- F2/F3/F4 API rotaları.
- React sipariş, iade, fatura, ürün ve dashboard ekranları.
- Ortak operasyon kartı stilleri.
- Kaynak seviyesi kabul testi: `scripts/validate-operational-workspaces.py`.

## 8. Resmî kaynaklar

- Trendyol Returned Orders Integration: approve, reject, claim issue reasons ve get claims servisleri.
- Trendyol Fatura Entegrasyonu: `sendInvoiceLink` ve 409 çakışma koşulları.
- Trendyol Changelog/Product V2 geçiş duyuruları: legacy Product V1 servislerinin 10 Ağustos 2026 itibarıyla kullanım dışı kalması.
- Gelir İdaresi Başkanlığı, VUK 231/5: mal teslimi veya hizmet tarihinden itibaren azami yedi gün içinde fatura düzenlenmesi.
