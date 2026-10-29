# PTT AVM Entegrasyonu Planı

Durum (22 Ekim 2026; ayrıntı §6.1): 1. aşama (bağlantı) kodda hazır: platform kodu, adaptör, bağlantı formu ve bağlantı testi. Aynı gün
kimlik **SOAP web servisine** çevrildi (`feat/pttavm-soap`, §2): satıcı paneli API Key/Token değil, Mağaza ID + API
kullanıcı adı + API şifresi veriyor. Canlı mağazayla doğrulama kullanıcının bilgileriyle yapılacak. Dış yazma kapalı başlar; her yazma
yeteneği kullanıcı onayıyla, tek kayıtla açılır.

Bu plan Pazarama (`docs/PAZARAMA-ENTEGRASYON-PLANI.md`) ve N11 (`docs/N11-ENTEGRASYON-PLANI.md`) planlarında izlenen
yolu izler. Yeni platform kontrol listesi Pazarama planı §5.3'tedir; burada yalnız PTT AVM'ye özgü olanlar yazılır.

## 1. Amaç ve ilke

- PTT AVM panelde Trendyol, Hepsiburada, N11, Pazarama ve Shopify gibi çalışır; ama PTT AVM'nin kurallarına göre yazılır.
- Aynı ekranlar, aynı iş kuyruğu, aynı port sözleşmeleri. PTT AVM'ye özgü olan yalnız adaptör, eşlemeler ve politikalar.
- **Canlı mağaza, test ortamı yok.** Her yazma adaptördeki tek kapıdan geçer: sunucu genel anahtarı + bağlantının
  dış yazma anahtarı + canlı ortam + doğrulanmış/aktif bağlantı (+ ilgili yazma politikası).
- Okuma yetenekleri bağlantı testinde, yazma yetenekleri ilk kabul edilen canlı yazmayla kanıtlanır.

## 2. Kullanıcı kararları (21 Ekim 2026)

- **Kimlik (21 Ekim 2026, düzeltildi):** PTT AVM satıcı panelinin Hesap Yönetimi › Entegrasyon Bilgileri ekranı
  yalnız üç değer veriyor: **MAĞAZA ID, API KULLANICI ADI, API ŞİFRESİ**. API Key / Access Token yok. Bu, SOAP web
  servisinin (`ws.pttavm.com:93`) WS-Security UsernameToken kimliğidir → **adaptör SOAP kullanır**, REST yolu
  kaldırıldı. Mağaza ID bağlantının `ExternalStoreId`'si (yalnız rakam); kullanıcı adı ve şifre şifreli
  `{Username, Password}` olarak saklanır (`USERNAME_PASSWORD`, maske = kullanıcı adının son 4 karakteri). Eski
  `{ApiKey, AccessToken}` kaydı bulunursa "kimlik reddedildi" + yeniden girme mesajı döner (canlıda böyle bağlantı
  yoktu, taşıma gerekmedi).
- **Kargo: PTT AVM'nin PTT Kargo akışı** (depo → barkod oluşturma → etiket). Kendi kargo anlaşması yolu yok.
- **Ürünler PTT AVM'de yayında.** Klasik içe aktarma modları kullanılır: tam çekme, yalnız eşleştirme, yalnız yeni,
  yalnız mevcut. Eşleşme anahtarı barkod; başlangıç fiyatı PTT AVM'deki fiyat.
- **Öncelik:** önce bağlantı + siparişler + ürün çekme/eşleme + fiyat/stok (Pazarama sırası).

## 3. PTT AVM API özeti (developers.pttavm.com, 21 Ekim 2026)

Portal Cloudflare arkasında: içerik tarayıcıda okunur (curl/WebFetch 403). `/tr` yeni REST'i, `/en` SOAP'ı da anlatır
(`*_soap` sayfaları). WSDL ve XSD'ler (`service.svc?wsdl`, `?xsd=xsd0..8`) doğrudan indirilebiliyor. §3.2–3.4 REST
alan adlarıyla yazıldı; SOAP karşılıkları aynı adların PascalCase hâli (ör. `SiparisNo`, `LineItemId`).

### 3.1 Adresler ve kimlik

| Yüzey | Adres | Kimlik |
|---|---|---|
| SOAP (**kullanılan**) | `https://ws.pttavm.com:93/service.svc` (WCF, binding `WsSecured_IService`, SOAP 1.1) | WS-Security: `Timestamp` + `UsernameToken` (Username + Password, `#PasswordText`), HTTPS |
| REST (kullanılmıyor) | `https://integration-api.pttavm.com/api/v1/...` (JSON) | `Api-Key`, `Access-Token`: satıcı paneli bu değerleri vermiyor |
| Kargo REST (5. aşamada bakılacak) | `https://shipment.pttavm.com/api/v1/...` | Belgede tutarsız; SOAP'ta `KargoBilgiListesi`, `GetCargoProfiles` var |

- SOAP zarfı: hedef ad alanı `http://tempuri.org/` (önek `tem`, alt öğeler nitelikli: `elementFormDefault="qualified"`),
  `SOAPAction: "http://tempuri.org/IService/<İşlem>"`, `Content-Type: text/xml; charset=utf-8`. Başlıkta
  `wsse:Security soapenv:mustUnderstand="1"` içinde `wsu:Timestamp` (Created/Expires, 5 dk) ve `wsse:UsernameToken`.
- **Null değer her zaman `i:nil="true"`** gider (`xmlns:i` zarfta tanımlı). WCF bozuk bir öğeye de yanlış şifreye de aynı
  hatayı verir: "An error occurred when verifying security for the message" (`a:InvalidSecurity`). Adaptör bu hatayı
  `PTTAVM_CREDENTIALS_REJECTED` sayar; kendi isteklerimizin nil kuralı testle korunur.
- Mağaza ID, `KullaniciTedarikciBilgisiGetir` yanıtındaki `TedarikciId` ile eşleşmeli; bağlantı testi bunu denetler
  (eşleşmezse `PTTAVM_STORE_MISMATCH`).
- Test/sandbox ortamı ve genel hız sınırı belgelenmemiş.

### 3.2 Servisler (REST)

| Alan | Uç | Not |
|---|---|---|
| Kategori | `GET /categories/main`, `/categories/category-tree`, `/categories/{id}` | id, name, parent_id, updated_at, children. **Özellik ve marka listesi yok.** |
| Ürün okuma | `GET /products/search?categoryId&subCategoryId&isActive&isInStock&merchantCategoryId&searchPage` | SOAP karşılığı StokKontrolListesi (`RowCount`, sayfa 0–1000) |
| Barkodla okuma | `POST /products/get-by-barcodes` `{barcodes:[...]}` | |
| Ürün ekle/güncelle | `POST /products/upsert` | Asenkron (`TrackingId`). Anahtar barkod. **Varyant listesi gönderilmezse tüm varyantlar silinir.** |
| Fiyat/stok | `POST /products/stock-prices` | Asenkron. Varyant gönderilmemesi silmez; varyant eşleşmesi özellik kombinasyonuyla |
| İşlem sonucu | `POST /products/tracking-result/{trackingId}` | Waiting / InProgress / Completed / Cancelled; ürün bazında Türkçe FailureReasons |
| Aktif/pasif | `PUT /products/{productId}/status` `{isActive}` | |
| Hatalı görseller | `POST /products/get-faulty-images` | |
| Sipariş listesi | `GET /orders/search?startDate&endDate&isActiveOrders` | ≤40 gün, `yyyy-MM-dd`, **sayfalama yok**; `isActiveOrders=true` yalnız `kargo_yapilmasi_bekleniyor` |
| Sipariş detayı | `GET /orders/{orderId}` | |
| Kargo bilgisi | `GET /orders/{orderId}/cargo-infos` | CurrentState, ReferenceCode (barkod), DeliveryInfo, InCargo |
| Fatura | `POST /orders/{orderId}/invoice` `{lineItemId:[], content(base64) | url}` | Satır bazında; PDF ya da HTML |
| Kargo profilleri | `GET /shipping/cargo-profiles` | |
| Kargo (shipment) | `POST /get-warehouse`, `/create-barcode` (toplu, asenkron), `/barcode-status`, `/get-barcode-tag` (ZPL/HTML) | |

**API'de olmayanlar:** iade (claim) işlemleri, müşteri soruları, webhook, hakediş, kategori özellikleri, marka listesi,
ürün onay durumu, sipariş kabul/iptal/tedarik edememe bildirimi.

### 3.3 Kurallar

- Ürün ve fiyat/stok isteği başına en çok **1000 ürün**; **aynı istek 5 dakika içinde tekrar gönderilemez**; boş liste yok.
- KDV yalnız **0, 1, 10, 20**. Stok 0–9999 (kırpılır). İndirim ≤70. Fiyat > 1. Ürün başına ≤100 varyant.
- KDV dahil ve hariç fiyat birlikte gönderilirse upsert'te KDV dahil, stock-prices'ta KDV hariç öncelikli → **yalnız
  birini gönder**.
- Desi 0–300; ad ≤200; barkod ≤250 karakter.
- Fiyat veya stok kilidi olan ürün güncellenmez; sonuç yalnız tracking sonucunda görünür.

### 3.4 Sipariş

- Sipariş no örneği `PTT-A1712HP2E-071024`; satır kimliği `LineItemId`; durum **satır bazında**:
  `kargo_yapilmasi_bekleniyor`, `havale_onayi_bekleniyor`, `onay_surecinde`, `gonderilmiş`,
  `gondericisine_teslim_edildi`, `tamamlandi`, `iptal`, `iade`, `odeme_gecersiz`.
- Satırda komisyon oranı, PTT AVM/tedarikçi indirimleri, KDV hariç tutar, KDV dahil toplam, `IsInvoice`.
- Siparişte `KargoBarkod`, `Barcodes[]` (type=KARGO), `KargoTutari`, fatura tipi (Bireysel/Kurumsal), TCKN/VKN.
- `IslemTarihi` saat dilimsiz (`2024-01-01T14:57:12`) → İstanbul saati varsayılır, UTC'ye çevrilir; canlıda doğrulanır.

## 4. Trendyol akışı → PTT AVM karşılığı

| Panel akışı | PTT AVM |
|---|---|
| Yeni sipariş okuma | `orders/search` son 2 gün + örtüşme, 60 sn |
| Açık sipariş durumu | `isActiveOrders=true` + açık siparişler `orders/{id}` ile |
| Geçmiş taraması | 40 günlük pencereler, ilk aktarım 3 ay |
| İşleme al | API'de yok → panelde gizli |
| Kargo barkodu / etiket | shipment API: depo → `create-barcode` → `barcode-status` → `get-barcode-tag` |
| Fatura linki | `orders/{id}/invoice` satır bazında |
| İadeler | API yok: satır `iade` olunca iade olarak gösterilir; karar PTT AVM panelinde |
| Sorular | API yok |
| Ürün çekme/eşleme | `products/search` sayfa sayfa; barkod anahtarı; varyantlar `VariantListesi` |
| Fiyat/stok | `products/stock-prices` + tracking sonucu |
| Ürün yayınlama | `products/upsert` + tracking sonucu; aktif/pasif `products/{id}/status` |

## 5. Mimari

### 5.1 Adaptör (`src/MarketplaceHub.Infrastructure/Adapters/PttAvm/`)

Pazarama düzeninde, port başına bir `partial` dosya:
- `PttAvmHttpClient.cs`: `IConnectionPort`; tek yazma kapısı `WriteBlocked<T>`; başlıklar, hata eşleme.
- `PttAvmSoap.cs`: SOAP zarfı (WS-Security), nil kuralı, fault eşleme (güvenlik → kimlik reddi, HTML → geçit engeli).
- `PttAvmOptions.cs` (`SectionName = "PttAvm"`, `SoapBaseAddress`), `PttAvmAuthenticationHandler.cs` (şifreli
  `{Username, Password}`), `PttAvmRateLimitHandler.cs` (paylaşılan PostgreSQL kovaları; kova SOAP işlem adından seçilir).
- `PttAvmOrders.cs` + `PttAvmOrderMapper.cs` (`IOrderPort`), `PttAvmReferences.cs` (`IReferenceDataPort`),
  `PttAvmProducts.cs` (`IProductPort`), `PttAvmPriceInventory.cs` (`IInventoryPricePort`),
  `PttAvmShipments.cs` (kargo barkodu/etiket), `PttAvmInvoices.cs` (`IInvoiceMarketplacePort`),
  `PttAvmErrorAdvice.cs`.
- Desteklenmeyenler açık hatayla döner: iade aksiyonları, sorular, işleme alma, test siparişi.

### 5.2 Kayıt

Pazarama planı §5.3 kontrol listesi birebir uygulanır; platform kodu `PTTAVM` (panelde logo, kare işaret, CSS rengi,
eşleştirme sekmesi zaten `PTTAVM` adıyla var).

### 5.3 Bağlantı ayarları (`SettingsJson`)

`ExternalWritesEnabled` (kapalı), `InvoiceCreationEnabled`, `PttAvmWarehouseId` (kargo barkodu için; ilk kurulumda
depo listesinden seçilir).

### 5.4 Hız sınırı kovaları (belgesiz → temkinli başlangıç)

| Kova | Değer |
|---|---|
| Sipariş | 30 / dk |
| Ürün okuma, kategori | 30 / dk |
| Ürün yazma, fiyat/stok | 1 / 15 sn ve aynı içerik 5 dk içinde tekrar yok |

Kova anahtarı API kullanıcı adının SHA-256 özeti (kullanıcı adı mağazaya özel). Kargo kovası 5. aşamada eklenir.

### 5.5 İş tipleri

`PTTAVM_CONNECTION_TEST`, `PTTAVM_REFERENCE_SYNC` (günde 1), `PTTAVM_ORDER_SYNC` (60 sn),
`PTTAVM_ORDER_RECOVERY_SYNC` (15 dk), `PTTAVM_ORDER_STATUS_SYNC` (3 dk); ürün, fiyat/stok, kargo ve fatura ortak
tiplerle (`PRODUCT_SYNC`, `PRICE_INVENTORY_SYNC`, `SHIPMENT_ACTION`, `INVOICE_MARKETPLACE_DELIVERY`).

## 6. Aşamalar

| # | Aşama | Çıktı |
|---|---|---|
| 0 | Ön koşul | Kullanıcı Entegrasyon Bilgileri'nden Mağaza ID + API kullanıcı adı + API şifresi alır |
| 1 | Bağlantı | Platform kodu, kayıt listesi, adaptör iskeleti, form alanları, bağlantı testi |
| 2 | Sipariş okuma | Sipariş/geçmiş/açık durum işleri, satır durum eşlemesi, ayrı şerit |
| 3 | Kategori + ürün çekme | Kategori ağacı, klasik içe aktarma modları, barkod eşleşmesi |
| 4 | Fiyat/stok | stock-prices + tracking sonucu, 5 dk kuralı |
| 5 | Kargo | Depo seçimi, barkod oluşturma, etiket (ZPL/HTML) |
| 6 | Fatura | Satır bazında fatura linki |
| 7 | Ürün yayınlama | upsert (varyant silme koruması), aktif/pasif |
| 8 | İade görünümü | Satır `iade` → iade kaydı (salt okunur) |
| 9 | Sağlamlaştırma | Hata önerileri, veri eşitleme ekranı, iş adları, pano matrisi |

### 6.1 Durum (21 Ekim 2026)

- **1. aşama canlı:** SOAP (`ws.pttavm.com:93`, WS-Security). Mağaza ID, API kullanıcısının tedarikçi bilgisinden
  (`KullaniciTedarikciBilgisiGetir` → `TedarikciId`) doğrulanır ve bağlantıya yazılır. Bağlantı testi ile kategori, sipariş ve stok
  okumaları canlıda kanıtlandı.
- **2. aşama (sipariş okuma):** `SiparisKontrolListesiV2` (`AktifSiparisler=0`, sayfalama yok) ile 35 günlük pencereler okunur.
  `PTTAVM_ORDER_SYNC` (2 dk, son 2 gün), `PTTAVM_ORDER_RECOVERY_SYNC` (ilk aktarım 3 ay) ve `PTTAVM_ORDER_STATUS_SYNC` (15 dk) işleri
  `pttavm-orders` şeridinde çalışır. Açık siparişlerin bulunduğu pencereler bütün olarak yeniden okunur.
  Satır durumları Trendyol paket durumlarına çevrilir: `kargo_yapilmasi_bekleniyor` → Created, `havale_onayi_bekleniyor`/`onay_surecinde` → Awaiting,
  `gonderilmiş` → Shipped, `gondericisine_teslim_edildi` → Undelivered, `tamamlandi` → Delivered, `iptal`/`odeme_gecersiz` → Cancelled, `iade` → Returned.
  Durum değişim saati gelmediği için paket olay zamanı `IslemTarihi + durum adımı (sn)` olarak hesaplanır.
  Tek sipariş, numaradaki gün (`PTT-…-ddMMyy`) ±1 gün okunarak bulunur.
- **3. aşama (kategori + ürün çekme):** `GetCategoryTree(parent_id=0)` tüm ağacı tek çağrıda verir. Ağaç `PTTAVM_REFERENCE_SYNC` ile
  ilk aktarımda ve günde bir okunur. Özellik ve marka servisi yok; özellik okumaları boş liste döner.
  `StokKontrolListesi` sayfa sayfa okunur (`SearchAktifPasif=0`): `RowCount` taramayı bitirir, ilk sayfayı tekrar eden sayfa durdurur.
  Barkod anahtardır. Fiyat `KDVli` (0 ise `KDVsiz`+KDV), stok `Miktar`, `Aktif=false` pasif ilandır.
  `VariantListesi` belgede tanımsız; yazma tarafındaki adlarla ve Türkçe karşılıklarıyla okunur.
  İçe aktarma n11/Pazarama'nın düz katalog yolunu kullanır (tam, yalnız eşleme, yeni, mevcut).
- **4. aşama (fiyat/stok, yazma kapısı arkasında):** `UpdateProductsStockPrice` en çok 1000 barkod alır. Gönderilenler: KDV dahil fiyat,
  KDV oranı (yalnız 0/1/10/20; başka oranda alan gönderilmez) ve 0–9999 stok. Sonuç `GetProductsTrackingResult` ile barkod barkod okunur.
  Belirsiz sonuç 6 dk sonra yeniden gönderilir (aynı istek 5 dk içinde reddedilir). İçe aktarılan teklifler "gönderilmiş" sayılır.
  Panelden gönderim elle başlatılır ve yalnız dış yazma anahtarına bağlıdır.
  Sınırlama: PTT AVM varyantları özellikle eşler; varyant barkodu tek başına gönderilir, kabul edilmezse takip sonucu gösterir.
- **5. aşama (kargo): engelli.** Kargo servisi ayrı bir REST API (`shipment.pttavm.com`); `Api-Key` ve `Access-Token` ister. Satıcı panelinde
  yalnız Mağaza ID + API kullanıcı adı + şifre var. Bu bilgiler PTT AVM'den alınınca açılır.
- **6. aşama (fatura linki, yazma kapısı arkasında):** `SaveInvoince`, belgeye bağlı PDF linkiyle sipariş numarasını ve canlı satırların
  `LineItemId` listesini alır. Belgedeki çelişkili cevap (`Success=false` + "işlem gerçekleştirilmiştir") belirsiz sayılır, sipariş okunarak
  doğrulanır. Geri okuma: canlı satırların hepsinde `IsInvoice=true`. Bağlantıda fatura oluşturma kapalı başlar.
- **7. aşama (ürün gönderimi, yazma kapısı arkasında):** `UpdateProductsV3` barkoda göre ürünü açar veya günceller. Sonuç
  `GetProductsTrackingResult` ile, yayın durumu `StokKontrolListesi` + `SearchBarkod` ile okunur (aktif → LIVE, pasif → bekliyor).
  Varyantsız gönderim PTT AVM'deki varyantları sildiği için **tek varyantlı** ürün (model ailesinde başka varyant yoksa) varyantsız
  gönderilir. Bu da iki koşula bağlıdır:
  1. Panel bağı: ürünün bu bağlantıdaki bütün bağları gönderilen barkod olmalıdır.
  2. PTT AVM okuması: gönderimden hemen önce barkod okunur; PTT AVM o barkodu varyantlı bir ürün altında göstermemelidir.

  Kategori yaprak olmalıdır; marka ve özellik eşlemesi istenmez (PTT AVM'de marka listesi yok; panel markasının adı
  `UpdateProductsV3`'ün isteğe bağlı `Brand` metin alanında gider).
- **7. aşama, varyantlı ürün (28 Ekim 2026, birleştirerek gerçek gönderim; canlıda henüz denenmedi):** Varyantlı ürünün PTT AVM karşılığı panelin **model ailesidir**
  (aynı marka ve model kodlu bütün renk kartları). Kart yayınlanınca ailenin bütün etkin varyantları tek `UpdateProductsV3`
  isteğinde hazırlanır (`PttAvmProductPublicationComposer.PlanFamily`):
  - Üst barkod: ailenin bu bağlantıdaki `MarketplaceProductLink.ExternalCode`'u, yoksa model kodu; ikisi farklıysa
    `PTTAVM_PARENT_BARCODE_MISMATCH`. Ailede tek model kodu olmalıdır (`PTTAVM_MODEL_CODE_REQUIRED` / `PTTAVM_MODEL_CODE_MISMATCH`).
    Ailenin kartları aynı kategoride olmalıdır; ad ve açıklama ailenin ilk kartından, görseller ürün düzeyindedir (`VariantV3`'te görsel
    yok): her renk kartından en çok 8 (PTT AVM paneli renk başına 8 görsel alıyor), önce her renkten birer tane, sonra her
    rengin diğerleri, tekrarsız; toplam sınır 8 × renk sayısı (varyantsız üründe 8). Marka, `ProductV3Request`'in isteğe bağlı
    `Brand` alanında panel markasının adıyla gider (marka yoksa öğe gönderilmez).
  - Her varyant barkod, `Renk`, `Beden`, MAIN stok ve etkin teklif taşır; biri eksikse **bütün gönderim** durur (varyant atlanmaz,
    atlanan varyantı PTT AVM silerdi). Fiyat farkı 0: ailenin satış fiyatı ve KDV oranı tek olmalıdır. En çok 100 varyant; ürün
    düzeyi stok varyantların toplamıdır (en çok 9999).
  - Birleştirme (kullanıcı kuralı, 28 Ekim 2026): gönderimden hemen önce `GetProductsWithVariants` önbelleksiz, ardından üst
    barkod `StokKontrolListesi` ile okunur. İstek = PTT AVM'nin üst barkod altında tuttuğu **her varyant olduğu gibi** (kendi
    barkodu, Renk/Beden değeri ve PTT AVM'nin bildirdiği stoğu) + panelde olup PTT AVM'de olmayan varyantlar (barkodla, harf
    duyarsız). PTT AVM'de zaten olan panel varyantı panel verisiyle yeniden gönderilmez (fiyat/stok eşitlemesi günceller); böylece
    gönderim PTT AVM'deki hiçbir varyantı silemez. Saf fonksiyon: `PttAvmProductPayloadRules.MergeVariants`.
    - Eklenecek varyant yoksa gönderim yapılmaz: iş `PTTAVM_VARIANTS_ALREADY_PRESENT` ile **başarılı** biter ("PTT AVM'de bu
      modelin bütün varyantları zaten var; gönderim yapılmadı."), kart `ALREADY_PRESENT` ("PTT AVM'de zaten var, gönderilmedi",
      bilgi tonu, red kodu gösterilmez).
    - Barkodu, Renk/Beden değeri veya stoğu okunamayan (ya da barkodu yinelenen) bir PTT AVM varyantı varsa gönderim durur
      (`PTTAVM_REMOTE_VARIANT_UNREADABLE`, varyantlar kartta listelenir); okunamayan kayıt (`PRODUCT_VARIANT_INVALID`) da durdurur.
    - Birleşik set yine en çok 100 varyant (`PTTAVM_VARIANT_LIMIT_EXCEEDED`), toplam stok en çok 9999
      (`PTTAVM_STOCK_TOTAL_TOO_HIGH`); aynı Renk/Beden iki farklı barkodla olamaz (`PTTAVM_VARIANT_OPTION_CONFLICT`). Ürün düzeyi
      stok birleşik varyantların toplamıdır.
    - Yazma tarafındaki `VariantV3`'te aktiflik alanı yok, `Price`'ın anlamı doğrulanmadı (gönderilmiyor, 0 okunur): PTT AVM'de sıfırdan farklı fiyatı (`Fiyat`/`FiyatFarki`;
      `FiyatFarkiMi` false ise kendi fiyatı) olan ya da `Aktif = false` olan bir varyant varsa gönderim durur
      (`PTTAVM_REMOTE_VARIANT_NOT_CARRIABLE`, ör. "FK453001: 25 TL fiyat farkı"). Alan hiç gelmediyse durdurmaz (bilinmeyen
      bilinmeyen kalır). Eklenecek varyant yoksa önce "zaten var" sonucu döner (hiçbir şey gönderilmez).
    - Eski `PTTAVM_REMOTE_VARIANT_MISSING` reddi kaldırıldı (PTT AVM'deki fazla varyantlar artık taşınır).
  - Kuru çalıştırma anahtarı `PttAvmProductPayloadRules.VariantSendDryRun` **`false`** (birleştirmeyle silme olmadığı için gerçek
    gönderim açık). Birleşik istek her durumda gönderimden önce worker günlüğüne `PTT AVM variant product request` satırıyla
    (Information) XML olarak yazılır. Anahtar `true` yapılırsa istek gönderilmez, kart `DRY_RUN` ("Kuru çalıştırma, gönderilmedi")
    olur. Tek varyantlı ürünün gönderimi değişmedi.
  - İş işleyici: kuyruktaki aile isteği, ailenin varyant barkodları sonradan değiştiyse gönderilmez (`PRODUCT_PUBLICATION_SUPERSEDED`);
    sonuç satırları yalnız kartın kendi barkodlarıyla eşlenir (Shopify gibi); taşınan PTT AVM varyantlarının satırları sayılmaz.
  - **İlk canlı gönderim (28 Ekim 2026 13:48 UTC, MZ059YNC, 8 renk / 24 varyant):** PTT AVM isteği kabul etti ama ürünü
    **tek varyantlı** açtı; iş `PRODUCT_BATCH_CONTRACT_INVALID` ile `MANUAL_REVIEW`'e düştü. Neden: varyant kısmı
    `ePttAVMService` ad alanındaki eski `Variant` tipinin adlarıyla (`Variant{Attributes/VariantAttr{Deger, Tanim}, Miktar,
    VariantBarkod}`) gönderilmişti; DataContract okuyucusu tanımadığı öğeleri sessizce atlar.
  - **Yayın durumu (28 Ekim 2026, canlı MZ059YNC):** PTT AVM'de onay adımı yok, ürün gönderilince satışta. Varyant barkodu kendi
    başına ürün olmadığından `SearchBarkod` ile aranınca bulunmaz; kartlar bu yüzden "Onay bekliyor"da kaldı. Artık aile kartının
    varyantları üst barkodun ürünü üzerinden okunur (`GetFamilyPublicationStatusAsync`; üst barkod bu yayının ürün işinin
    yükünden): ürün varyantı barkoduyla listeliyorsa (büyük/küçük harf farkı gözetilmez) o, listelemiyorsa varyant barkodu
    `BarkodKontrol` ile önce yazıldığı gibi sonra küçük harfle sorulur; PTT AVM kodu bu ürüne (aynı `UrunId` ya da üst barkod)
    bağlarsa varyant ürün aktifse LIVE, pasifse bekliyor; kod tanınmazsa NOT_FOUND (bekler), başka ürüne aitse MANUAL_REVIEW.
    Varyant bağı PTT AVM'nin tanıdığı yazılışla kurulur. `BarkodKontrol`'ün varyant barkoduna model ürününü döndürdüğü hâlâ
    canlıda görülecek (worker logu: "PTT AVM family status read").
    **Canlı sonuç (aynı gün):** ürün okuması varyant barkodu vermiyor (`ListedVariantBarcodes: 0`), `BarkodKontrol` varyant
    barkodunu tanımıyor; 24 varyant NOT_FOUND kaldı. Bu yüzden ikinci kaynak eklendi: `GetProductsWithVariants`'in
    `VariantName`'i ("Gri / L") varyantın gönderildiği Renk/Beden ile eşleşirse (Türkçe büyük/küçük harf, boşluklar
    yok sayılır; tek parçalı ad ya da iki kez listelenen çift eşleşmez) varyant `{üst barkod}/{VariantId}` bağıyla LIVE
    olur. Sıra: barkod, ad, `BarkodKontrol`. Ürün adlı varyant listelemiyorsa varyant listesi bağlantı başına en çok
    5 dakikada bir yeniden okunur. Log her barkodun kaynağını ve PTT'nin listelediği adları yazar.
    **İkinci canlı sonuç:** PTT, `UpdateProductsV3` ile açılan aileyi "Renk / Beden" çiftleri olarak değil, her seçenek değeri
    için bir kayıtla listeliyor ("Lacivert, Siyah, Mürdüm, L, 2XL, XL, Gri, Kahverengi, Haki, Bordo, Açık Kahverengi"). Bu
    liste hangi çiftin var olduğunu söylemez; bu yüzden ürün aktifse, bütün adlar tek değerse, varyantın gönderilen rengi
    ve bedeni ikisi de listede varsa ve yayının başarılı bir ürün işi (aynı yük hash'i, takip sonucu kabul) bu barkodu
    gönderdiyse varyant LIVE olur (kaynak `values`, bağ gönderilen barkod). Değerlerden biri yoksa NOT_FOUND kalır.
  - **WSDL ile doğrulandı** (`https://ws.pttavm.com:93/service.svc?singleWsdl`, 28 Ekim 2026): `UpdateProductsV3(items:
    ArrayOfProductV3Request)`, bütün öğeler `http://schemas.datacontract.org/2004/07/ePttAVMService.Model.Requests` ad alanında,
    üyeler alfabetik sırada. `ProductV3Request`: Active, Barcode, BasketMaxQuantity, Brand, CargoProfileId, CatalogBarcode,
    CategoryId, Desi, Discount, Ean, EstimatedCourierDelivery, Gtin, Images, IsCargoFromSupplier, LongDescription, Name,
    NoShippingProduct, Parts, PriceWithVat, PriceWithoutVat, ProductCode, Quantity, ShortDescription, SingleBox, VATRate,
    **Variants (ArrayOfVariantV3)**, WarrantyDuration, WarrantySupplier (hepsi isteğe bağlı). Varyant:
    `Variants/VariantV3{Attributes/VariantAttrV3{Definition, Value}, CatalogBarcode, Price, Quantity, VariantBarcode}`;
    `Price` ve `Quantity` boş geçilemeyen sayı (gönderilmezse 0 okunur). Artık bu biçim gönderiliyor (`Definition` = Renk/Beden);
    `CatalogBarcode` ve `Price` gönderilmiyor (fiyat farkı 0). `Combination`/aktiflik alanı V3'te yok.
  - **Takip sonucu (WSDL):** `ProductsTrackingResult{…, ProductsSubTrackingResult{Count…, ProductBasedInfos/ProductBasedInfo
    {FailureReasons, Message, Status, Barcode, ProductId}}}`; satır **ürün başına** tek `Barcode` taşır, varyant kısmı yoktur.
    Aile gönderiminde üst barkod satırı, kendi satırı olmayan her kart varyantının sonucu sayılır
    (`PttAvmProductPayloadRules.FamilyResultLines`); varyant barkoduyla satır gelirse o kullanılır. Hiçbiri yoksa yine `MANUAL_REVIEW`.
  - **Hâlâ canlıda doğrulanacak:** `VariantV3.Price`'ın fiyat farkı mı mutlak fiyat mı olduğu (gönderilmiyor, 0 okunur); varyant barkodunun
    büyük/küçük harf duyarlılığı; ürün düzeyi `Quantity`'nin kabulü; takip sonucunun gerçekte hangi barkodla döndüğü;
    `GetProductsWithVariants`'in barkodsuz kayıtlarında `VariantName`'in "Renk / Beden" biçimi; taşınan bir PTT AVM varyantının
    fiyat farkı ve pasif durumu istekte yokken korunuyor mu (şimdilik böyle varyantlar gönderimi durdurur).
- **8. aşama (iade görünümü, salt okunur):** Satırı `iade` olan her sipariş, yalnız o satırlarla tamamlanmış bir iade kaydıdır (`iade:{sipariş no}`).
  Kaydedilmesi stoğu değiştirmez; iade kararları PTT AVM paneline bırakılır ve panelden gönderilmez.
- **Varyantlar (22 Ekim 2026, canlı mağazadan):** İlk eşleştirmede 198 üründen 25'i eşleşti; eşleşmeyen 172 barkod panelin
  model kodlarıydı. PTT AVM bu ürünleri model olarak tutuyor, `StokKontrolListesi` varyantlarını vermiyor.
  - Varyantlar `GetProductsWithVariants(page, size)` ile okunur; sayfalar **1'den** başlar (canlı: "Page 0'dan büyük olmalıdır").
    Yanıttaki `TotalPage`'e kadar her sayfa okunur (29 Ekim, M-1); 100 sayfa sınırını aşan ya da sınırda bitmeyen liste
    eksik sayılır, önbelleğe yazılmaz; aile gönderimi bu durumda `PTTAVM_REMOTE_VARIANT_UNREADABLE` ile durur.
  - Bu servis varyantı barkodsuz verir: `VariantId`, `VariantName` ("S"), `Quantity`, `PriceDifference`, `Limit`.
    Varyant `FK4530/7101` kimliğiyle, `FK4530-S` stok koduyla, `FK4530` model koduyla okunur.
  - **Eşleme (22 Ekim, değişti):** beden adı ve başlıktaki renkle tahmin yapılmaz. "Ürün Eşleme" modelin panel
    varyantlarının barkodunu, tanımazsa stok kodunu `BarkodKontrol` ile PTT AVM'ye sorar; PTT AVM kodu bu model ürünü
    (aynı `UrunId` ya da model barkodu) altında gösterirse varyant o kodla bağlanır. Fiyat, stok ve siparişler bu kodla
    yürür. Tanınmayan varyant bağlanmaz, "Ürün eşlenemedi" uyarısına düşer. Daha önce `FK4530/7101` ile bağlanmış
    varyantın bağı doğrulanan kodla değiştirilir; doğrulanmış bağlar yeniden sorulmaz. Sorgu okuma kotasını (30/dk)
    kullanır; ilk eşleme büyük katalogda uzun sürebilir. Bir sorgu hata verirse çalıştırmanın kalanında sorulmaz
    (`PTTAVM_CODE_CHECK_FAILED`).
  - `BarkodKontrol`'ün bir varyant barkodu için model ürününü döndürdüğü canlıda doğrulanacak; döndürmezse varyantlar
    eşlenmez (yanlış bağ kurulmaz).
  - Sipariş satırında `UrunBarkod` modelin barkodu, `VariantBarkod` satılan varyantın barkodudur (canlı: `RYP001` / `RYP00105`);
    satır `VariantBarkod` ile panel varyantına bağlanır.
  - Fiyat/stok gönderimi `FK4530/7101` gibi kimlikle bağlı varyantta panel barkodunu, barkodu olmayan varyantta stok kodunu
    gönderir (PTT AVM barkod alanında barkod yoksa stok kodu kullanır; siparişlerdeki `VariantBarkod` da böyle). PTT AVM barkodu kabul etmezse takip sonucunda satır hatası görünür.
  - Ürün gönderimi bu varyantlara model ailesi olarak gider; şimdilik kuru çalıştırmadır (7. aşama, varyantlı ürün).
- **Canlıda doğrulanacak:**
  - Varyant eşleştirmesinin yeniden çalıştırılmış sonucu (25/198'den ne kadar yükseldiği).
  - Model varyantında panel barkoduyla fiyat/stok gönderiminin kabul edildiği (tek ürünle).
  - `IslemTarihi` gerçekten sipariş tarihi mi.
  - `BitisTarihi` sonuca dahil mi.
  - `KdvDahilToplamTutar` indirim sonrası mı (brüt = ödenen + `IndirimToplam` varsayımı).
  - `StokKontrolListesi` sayfa boyu ve `VariantListesi` yapısı.
  - `IsInvoice` alanı gerçekten geliyor mu. **Geliyor (23 Ekim 2026), ama yalnız API ile gönderilen faturada true oluyor:** panelden faturalanmış teslim edilmiş siparişlerde false. SOAP'ta `SaveInvoince` dışında fatura işlemi yok, REST'te fatura GET ucu yok (`siparisUrunler[].isInvoice` aynı bayrak). false bu yüzden "fatura yok" değil UNVERIFIED sayılır; panelde "Doğrulanamadı" sekmesi, satıcı elle işaretler. REST `isInvoice`'ın panel yüklemesinde farklı davranıp davranmadığı canlı bir okumayla henüz denenmedi.

## 7. Riskler

- **SOAP kimliğinin ömrü:** REST dokümanı kullanıcı adı/şifrenin "geçiş süresince" çalıştığını yazıyordu; panel bugün
  yalnız bunu veriyor. PTT AVM API Key'e geçerse REST yolu yeniden açılır.
- **Varyant silme:** upsert'e varyantsız gönderim tüm varyantları siler → yayınlamada katı kontrol.
- **Test ortamı yok:** yazmalar tek barkodla ve kullanıcı onayıyla açılır.
- **Sayfalamasız sipariş listesi ve saat dilimsiz tarih:** canlı veriyle doğrulanır.
- **Kargo kimliği belgede tutarsız:** canlıda denenir.

## 8. Doğrulananlar ve doğrulanacaklar

### 8.1 Resmi kaynaklardan doğrulananlar (21 Ekim 2026)

WSDL/XSD (`https://ws.pttavm.com:93/service.svc?wsdl`, doğrudan indirildi) ve developers.pttavm.com/en SOAP sayfaları
(tarayıcıyla okundu):

- **Kimlik:** portalın giriş sayfası "SOAP Web Services Security, username and password" yazıyor. WSDL politikası:
  `TransportBinding` (HTTPS), `IncludeTimestamp`, `SignedSupportingTokens` › `UsernameToken` (`WssUsernameToken10`).
- **Adres ve bağlama:** `soap:address https://ws.pttavm.com:93/service.svc`, port `WsSecured_IService`, SOAP 1.1
  (`soap:binding transport=http`), `soapAction="http://tempuri.org/IService/<İşlem>"`, şema `elementFormDefault=
  "qualified"`, hedef ad alanı `http://tempuri.org/`; veri sözleşmeleri `http://schemas.datacontract.org/2004/07/ePttAVMService`.
- **`KullaniciTedarikciBilgisiGetir`** (bağlantı testi): parametresiz; yanıt `Tedarikci {TedarikciAdi, TedarikciId}`
  (dokümanda "mağaza adı" ve "mağaza ID"). Örnek yanıt testlerde birebir kullanıldı.
- **`GetMainCategories`**: parametresiz; `categoryMainResponse {success, main_category[category{id, name, updated_at}], error{error_code, error_message}}`.
- **`SiparisKontrolListesiV2`**: `BaslangicTarihi`, `BitisTarihi` (şemada `xs:dateTime`, doküman örneği
  `2024-10-05` gönderiyor → adaptör `yyyy-MM-dd` gönderir), `AktifSiparisler` (0 = tümü, 1 = kargo bekleyen);
  aralık ≤ 40 gün. Yanıt `ArrayOfTedarikciSiparisKontrolV2`; örnek yanıt (kısaltılmış) testlerde kullanıldı.
- **`StokKontrolListesi`**: `ShopId`, `SearchKategoriId`, `SearchAltKategoriId`, `SearchUrunAdi`, `SearchBarkod`,
  `SearchAktifPasif`, `SearchMevcut`, `SearchTedarikciKategoriId`, `SearchYeniKategoriId`, `SearchPage`; yanıt
  `ArrayOfStokKontrolDetay` (kayıtta `RowCount`, `ShopId`, `Barkod`, `UrunId`, `VariantListesi`). Probe dokümandaki
  örnek filtreleri gönderir.
- **`BarkodKontrol`** / **`BarkodKontrolBulk`**: tek barkod / `ArrayOfstring`; yanıt `StokKontrolDetay`. Bilinmeyen
  barkodun yanıtı dokümanda yok → ürün okuma kanıtı bunun yerine StokKontrolListesi ilk sayfasıyla alınır.
- Yanıtlarda null alanlar `i:nil="true"` olarak geliyor (ör. `AdminCode`, `Resim1Stream`).

### 8.2 1. aşamada varsayılanlar (canlıda doğrulanacak)

- SOAP fault `InvalidSecurity` / `FailedAuthentication` ya da "verifying security" metni → `PTTAVM_CREDENTIALS_REJECTED`
  (yanlış kullanıcı/şifre, süresi geçmiş Timestamp ve bozuk XML aynı yanıtı verir; bizim zarflarımız nil kuralına
  uyar). Örnek fault metni WCF'nin standart metninden alındı, PTT AVM'den görülmedi.
- Diğer fault'lar → `PTTAVM_REQUEST_REJECTED` (PTT AVM metni + öneri). HTML yanıt (Cloudflare) → `PTTAVM_GATEWAY_BLOCKED`
  (geçici, 15 dk); fault'suz 5xx/408/zaman aşımı → geçici; 429 → `PTTAVM_RATE_LIMITED`.
- Timestamp ömrü 5 dk (PTT AVM yanıtlarındaki kendi Timestamp'i 5 dk); sunucu saat farkına toleransı bilinmiyor.
- Sipariş probe'u dün–bugün (Türkiye saati), `AktifSiparisler=0`; `BitisTarihi`nin dahil olup olmadığı bilinmiyor.
- Ürün probe'u: `StokKontrolListesi` `ShopId`=Mağaza ID, `SearchAktifPasif=1`, `SearchMevcut=0`, `SearchPage=0`; boş
  liste de SUPPORTED sayılır. `SearchAktifPasif=1`'in "yalnız aktif" mi olduğu canlıda görülecek.
- `TedarikciId` ile Mağaza ID'nin aynı sayı olduğu (doküman ikisine de "store ID" diyor).
- Devre kesici anahtarı ana makine + yol olduğundan tüm SOAP işlemleri tek devre paylaşır (hesap başına).

### 8.3 Hâlâ doğrulanacaklar

- Gerçek bir kimlik reddinin fault metni ve HTTP durumu; Cloudflare'in ws.pttavm.com:93 önünde olup olmadığı.
- `SiparisKontrolListesiV2` sayfalaması (şemada yok); `IslemTarihi` saat dilimi; StokKontrolListesi sayfa boyutu.
- Kargo akışının (barkod/etiket) SOAP'taki karşılığı ya da shipment REST kimliği.
