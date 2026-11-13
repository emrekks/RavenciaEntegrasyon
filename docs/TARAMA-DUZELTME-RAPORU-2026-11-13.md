# Tarama Düzeltme Raporu — 13 Kasım 2026

Kaynak: `TARAMA-DOGRULAMA.html` (main `6321b83`, 25 bulgu: 12 doğru, 13 kısmen doğru).
Dal: `claude/inspiring-noether-cbwvkm`. Bu dal **canlıya ship edilmedi** (bkz. "Yapılmayanlar").

## Özet

| Adım | Durum |
|---|---|
| 1. Drive yedek kurulumu | **Yapılmadı.** Sunucuda yazma gerektiriyor; CLAUDE.md sunucuyu salt okunur tutuyor ve iş ekipte sürüyor. B-3 (yedek bütünlüğü) kod tarafında düzeltildi. |
| 2. A-1, A-2, A-3 stok gönderimi | Düzeltildi, testli. |
| 3. F-11, D-1, D-2 fatura | Düzeltildi, testli. D-2'de sert kilit yok (gerekçe aşağıda). |
| 4. C-9, C-15, C-10 ürün verisi | Düzeltildi, testli. |
| 5. Kalanlar (raporlar, senkron, web, CSS) | B-4 dışında hepsi düzeltildi. B-4 yalnız değerlendirildi. |
| Ek: KDV %8 | Sahibin isteğiyle panele eklendi (13 Kasım). |

## Bulgu bulgu

### Para / Stok
- **A-1 — Güncelleme kartın bedenlerini "satışta" durumundan çıkarıyordu** (`7531f83c7`)
  - Kök neden: stok/fiyat gönderimi yalnız `LIVE` listing state'lerini alıyor (`TrendyolWriteComposers.cs`). Güncellemenin her sonucu (ret, engel, belirsiz, kabul) kartın bütün state'lerini `LIVE` dışına çekiyordu (`MarkPublicationResult` → `SetListingStatus` ve satır döngüleri).
  - Düzeltme: yeni `ListingSaleStatePolicy`. Güncellemenin sonucu kart ve listing satırlarında görünür kalıyor, `LIVE` state `LIVE` kalıyor. Trendyol (gruplu gönderim dahil), n11, Hepsiburada ve PTT AVM'de geçerli. Ek bulgu (başarılı güncellemenin bedenleri onaya park etmesi) de kapandı.
- **A-2 — Hepsiburada'ya yalnız yeni beden gönderilince satıştaki bedenler manuel incelemeye düşüyordu** (`7531f83c7`)
  - HB onay kontrolü artık yalnız bu gönderimin bedenlerini (aynı payload hash) karşılaştırıyor.
  - Önceki gönderimden satışta olan bedenler olduğu gibi kalıyor ve "canlı" sayılıyor.
  - `MarkApprovalResult` bir `LIVE` bedeni yalnız kimlik çakışmasında satıştan çıkarıyor.
- **A-3 — Toplu güvenlik stoğu pazaryerine yansımıyordu** (`dbcbfe575`, geri alma `bfeaa97f6`, geri getirme `3210feef4`)
  - Toplu atama, tekli düzenleme gibi teklifin stok gönderim işaretini temizliyor.
  - Toplu atama **hiçbir şeyi hemen göndermiyor**; test, hiçbir iş kuyruklanmadığını doğruluyor.
  - Yeni adedi periyodik stok mutabakatı (`ReconcileStock`) taşıyor. Sahibin kuralı (13 Kasım): toplu eşitlemeler pazaryerine anında yazmaz; stok ve fiyat periyodik uzlaştırmayla gider.
- **A-14 — Trendyol hizmet bedeli Net Kâr'dan iki kez düşülüyordu** (`62cb72660`): ödeme numarasız hizmet bedeli faturası artık "tahminle karşılandı" sayılıyor.
- **E-5 — Tutarı 0 olan Trendyol siparişine masraf yazılıyordu** (`4030e7e04`): `ReportCosts` Trendyol filtresine `Amount > 0 || Back > 0` eklendi.
- **E-6 — Ürün silindikten sonra rapor önbelleği bir saat eski kalıyordu** (`0bc9d2e26`): ürün silme aynı transaction'da outbox olayı yazıyor; önbellek düşüyor.

### Fatura
- **F-11 — Düzeltme gereken fatura listede yeşil "Faturası Mevcut" görünüyordu**
  - Web (`11b4d9599`): liste ve detay tek fonksiyonu (`invoice-workspace-status.ts`) kullanıyor. Sıra: önce düzeltme, sonra hata, en son pazaryerinin "faturalandı" bilgisi.
  - Backend filtre değeri (`59d11c156`): `WorkspaceInvoiceDisplayStatus` da aynı sırayı izliyor.
- **D-1 — Kesilmiş faturada iade ve kısmi iptal düzeltme açmıyordu** (`6630ae76b`)
  - Aday sorgusu artık satır iade/iptal adetlerini, iade edilmiş paketi ve RETURNED durumunu da görüyor.
  - Kapsam, faturalanmamış akıştaki `InvoiceSaleReturns.ReturnedUnitsAsync` ile hesaplanıyor; faturadan sonra iptal edilen adetler de ekleniyor.
  - Kısmi iptal ya da iade "İade Belgesi Gerekli" notunu açıyor (iptal adedi yazılı). Tamamı gittiyse fatura `AdjustmentRequired` oluyor.
- **D-2 — E-Faturam kendi numarasını verirse ikinci fatura riski** (`e72ed67bc`)
  - Dönen referans gönderilen UUID'den farklıysa sağlayıcı bağlantısında kalıcı bir uyarı açılıyor: `INVOICE_PROVIDER_UUID_MISMATCH`.
  - Böyle bir bağlantıda UUID ile "bulunamadı" artık "kesilmemiş görünüyor" diye okunmuyor. Fatura `EFATURAM_UUID_NOT_HONOURED` koduyla belirsiz kalıyor; metin, yeniden göndermeden önce portalda sipariş numarasıyla aranmasını istiyor.
  - **Sert kilit konmadı:** kodda ve belgelenmiş API'de sipariş numarasıyla arama yok; kilit siparişi kalıcı faturasız bırakırdı. Yeniden gönderim eskisi gibi operatör onayıyla yapılıyor.

### Ürün verisi ve senkron
- **C-9 — Model kodu değişince iki ürün birleşebiliyordu** (`80a8a102e`)
  - Editör kaydında ve Excel yüklemesinde kod gerçekten değişip başka bir ürünün koduna denk geliyorsa reddediliyor.
  - Aile kaydının kendi koduyla satır eklemesi etkilenmiyor. Pazaryeri içe aktarması da bilinçli olarak dışarıda; orada kod pazaryerinin kendi verisi.
- **C-10 — Excel'de "%10" KDV 0,1 okunuyordu** (`917493d61`)
  - 0 ile 1 arasındaki % işaretsiz değer yüzde olarak okunuyor (0,1 → 10).
  - Yalnız panelin kabul ettiği oranlar yazılıyor; diğerleri satır hatası.
- **C-15 — Shopify'da yeni ürünün bağlantısı erken siliniyordu** (`43e055d1a`)
  - İlk kontrol 3 dakika ertelendi.
  - "Bulunamadı" kartı ancak 15 dakikalık arama payından sonra ve hiçbir SKU'nun oluşturulduğu doğrulanmamışsa sıfırlıyor (Trendyol'daki koruma).
- **C-2 — Trendyol iade onayı sınırı aşılınca iade okuması da duruyordu** (`e6a11c43b`): onay/ret ayrı devrede, satıcı başına 5/dk kotayla. Finans için 100/dk.
- **C-5 — 5 dakikadan eski Shopify bildirimleri atılıyordu** (`16306370d`)
  - Bayat `products/delete` bildirimi artık hemen bağlantı doğrulama işi açıyor.
  - `products/update` panelin abone olduğu konular arasında değil, kapsam dışı.
- **C-6 — Pazarama kimliksiz yanıtta bütün bedenler gitmiş sayılıyordu** (`857167ec9`, ek `2e0f55377`)
  - Kimliksiz yanıtta bedenler "sonuç bilinmiyor" kalıyor.
  - Onay kontrolünde 24 saat bulunamayan beden `PRODUCT_CREATE_NOT_FOUND` ile incelemeye düşüyor.
  - Ek düzeltme: bu beden kartın bekleyen diğer bedenlerinin kontrolünü artık durdurmuyor.
  - **Not:** 24 saat kuralı sonucu bilinmeyen her bedene uygulanıyor, yalnız Pazarama'ya değil.
- **C-7 — Başarısız görsel güncellemesi sessizce kapanıyordu** (`02aff9102`): sonraki güncelleme eski hatayı ancak eskisinin gönderdiği her bölümü ve alanı gönderdiyse kapatıyor.
- **C-11 — Hangi eşleme şablonunun uygulanacağı rastgeleydi** (`5f3d50ea6`)
  - Özellik ve değer şablonlarında en son güncellenen uygulanıyor; eşitlikte kimlik belirliyor.
  - "Özellik başına tek şablon" (benzersiz index + veri temizliği) yapılmadı.
- **C-13 — Hepsiburada'da tekrar eden satırın durumu alfabetik seçiliyordu** (`49944353f`)
  - Satır en yeni kopyadan alınıyor.
  - Tekrar kopya ikinci kez indirim eklemiyor. Paketlerin indirimleri eskisi gibi toplanıyor.

### Altyapı
- **B-3 — Uzak yedek kopyası bozuk kalabilirdi** (`8069bfe7e`)
  - Yükleme önce geçici ada gidiyor, SHA-256 doğrulanıyor, sonra son ada taşınıyor ve `.verified` kaydı yazılıyor.
  - Canlıda bir kez olacak: kaydı olmayan en yeni kopya ilk çalıştırmada yeniden yüklenecek.
- **B-4 — Yönetici hesapları için iki adımlı doğrulama zorunlu değil:** kod değişmedi.
  - Production varsayılanına dokunulmadı; Sahip hesabında TOTP yok ve açılırsa kilitlenir.
  - Okumaları engellemek bugün güvenli değil: web panelinde `MFA_ENROLLMENT_REQUIRED` için bir işleyici yok, bütün sayfalar 403'le bozulur.
  - Öneri, ayrı bir iş olarak: middleware'de GET'leri de engelle (`/auth` ve `/hooks` muaf) ve web'e Ayarlar > Güvenlik'e yönlendiren bir interceptor ekle.

### Web paneli ve görünüm
- **F-9 — Kayıt sırasında yazılanlar kayboluyordu** (`d8df770c6`): kayıt sürerken form kilitli (`inert`); fiyat tablosu yalnız kaydedilen hücreleri temizliyor.
- **F-10 — Açıklama editöründe renk, boyut ve hizalama çalışmıyordu** (`d93c5f5b7`): temizleyici yalnız `color`, `font-size` ve `text-align`'a, sıkı doğrulamayla izin veriyor. Not: Shopify'a giden açıklamada backend bu stilleri yine siliyor.
- **G-1 / G-7 — Telefonda filtre paneli ve uzun tutar** (`29fae61dc`): Faturalar ve İşlem Takibi'nde filtre paneli üst çubuğun altında; uzun tutar üç noktayla bitiyor.
- **G-5 / G-6 — Filtre satır kırılmaları** (`3854fa627`): Temizle/Filtrele kart genişliğine göre kırılıyor (container query); Hakediş ızgarası ve akış tablosunun durum sütunu düzeldi.
- **G-8 — Ürünler özet kartları** (`e0b87d96d`): kart başına 180 px yoksa altılı yerine 3×2.
- **G-9 — Tek satırlı alanlar 43 px'ti** (`75b8e4f79`): artık 36 px. `textarea`'ya dokunulmadı.
- Kararsız bir web testi düzeltildi (`2b6242792`).

### Ek: KDV %8 (`a79a41c97`)
- Sahibin isteğiyle panel 0, 1, 8, 10 ve 20'yi kabul ediyor: editör, toplu alan ve Excel.
- **Uyarı:** %8 oranı 10 Temmuz 2023'te kaldırıldı. Pazarama, PTT AVM ve n11'in kendi oran listeleri değiştirilmedi; %8'li bir kart orada reddedilebilir.

## Testler

Tam backend test paketi bu commit sırasında çalışıyordu; sonucu bir sonraki commit bu bölüme yazacak. Bulgu başına filtreli koşular (PostgreSQL dahil) her commit öncesi geçti.

## Yapılmayanlar ve sıradaki adımlar
- **Canlıya ship:** yapılmadı. `ship.sh` Actions durumunu `gh run` ile, canlı imajları SSH anahtarıyla okuyor; bu bulut oturumunda ikisi de yok. Ship ve canlı doğrulama, betiğin çalıştığı bir ortamdan yapılmalı.
- **Canlı veritabanı boş:** 12 Kasım'da fabrika ayarlarına dönüldü. Düzeltmelerin canlı etkisi, bağlantılar açılınca rapordaki salt okunur SQL'lerle ölçülmeli.
- **Drive yedeği (adım 1):** ekipte. `systemctl list-timers 'ravencia-offsite-*'` ve Drive'daki ilk `.tar.age` ile doğrulanmalı.
- **Açık kararlar:**
  - B-4'ün iki parçalı uygulanması.
  - C-11 için özellik başına tek şablon.
  - C-6'daki 24 saat kuralının yalnız Pazarama'ya daraltılması.
  - Toplu güvenlik stoğunun mutabakat turunu beklemeden elle gönderilmesi gerekip gerekmediği.
