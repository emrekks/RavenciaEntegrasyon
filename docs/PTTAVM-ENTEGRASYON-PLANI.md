# PTT AVM Entegrasyonu Planı

Durum (21 Ekim 2026): 1. aşama (bağlantı) kodda hazır: platform kodu, adaptör, bağlantı formu ve bağlantı testi. Aynı gün
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
