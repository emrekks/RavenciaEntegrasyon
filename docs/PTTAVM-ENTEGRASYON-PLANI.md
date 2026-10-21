# PTT AVM Entegrasyonu Planı

Durum (21 Ekim 2026): 1. aşama (bağlantı) kodda hazır (`feat/pttavm`): platform kodu, adaptör iskeleti, bağlantı formu ve
bağlantı testi. Canlı mağazayla doğrulama kullanıcının anahtarlarıyla yapılacak. Dış yazma kapalı başlar; her yazma
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

- **Kimlik:** kullanıcı PTT AVM satıcı panelinde (Hesap Yönetimi › Entegrasyon Bilgileri) üretilen API Key ve
  Access Token'ı bağlantı formuna kendisi girer.
- **Kargo: PTT AVM'nin PTT Kargo akışı** (depo → barkod oluşturma → etiket). Kendi kargo anlaşması yolu yok.
- **Ürünler PTT AVM'de yayında.** Klasik içe aktarma modları kullanılır: tam çekme, yalnız eşleştirme, yalnız yeni,
  yalnız mevcut. Eşleşme anahtarı barkod; başlangıç fiyatı PTT AVM'deki fiyat.
- **Öncelik:** önce bağlantı + siparişler + ürün çekme/eşleme + fiyat/stok (Pazarama sırası).

## 3. PTT AVM API özeti (developers.pttavm.com, 21 Ekim 2026)

Portal Cloudflare arkasında: içerik tarayıcıda okunur (curl/WebFetch 403). `/tr` yeni REST'i, `/en` eski SOAP'ı anlatır.

### 3.1 Adresler ve kimlik

| Yüzey | Adres | Kimlik |
|---|---|---|
| REST (kullanılacak) | `https://integration-api.pttavm.com/api/v1/...` (JSON) | `Api-Key`, `Access-Token`, `X-Correlation-Id` başlıkları |
| Kargo REST | `https://shipment.pttavm.com/api/v1/...` | Doküman aynı başlıkları yazıyor; Postman örneği Basic + `Integration-Key` gösteriyor → canlıda denenir |
| SOAP (yedek) | `https://ws.pttavm.com:93/service.svc` (WCF, WS-Security) | `wsse:ApikeyToken` (Apikey + Integrationtoken) ya da eski UsernameToken |

- Kimlik bilgileri satıcı panelinde **seçilen entegratör firmaya ve mağazaya özel** üretilir; entegratörü listeye PTT
  personeli ekler. Kullanıcı adı/şifre yöntemi geçiş sürecinde paralel çalışıyor, kaldırılacak.
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
- `PttAvmOptions.cs` (`SectionName = "PttAvm"`), `PttAvmAuthenticationHandler.cs` (şifreli `{ApiKey, AccessToken}`),
  `PttAvmRateLimitHandler.cs` (paylaşılan PostgreSQL kovaları).
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
| Kargo | 30 / dk |

### 5.5 İş tipleri

`PTTAVM_CONNECTION_TEST`, `PTTAVM_REFERENCE_SYNC` (günde 1), `PTTAVM_ORDER_SYNC` (60 sn),
`PTTAVM_ORDER_RECOVERY_SYNC` (15 dk), `PTTAVM_ORDER_STATUS_SYNC` (3 dk); ürün, fiyat/stok, kargo ve fatura ortak
tiplerle (`PRODUCT_SYNC`, `PRICE_INVENTORY_SYNC`, `SHIPMENT_ACTION`, `INVOICE_MARKETPLACE_DELIVERY`).

## 6. Aşamalar

| # | Aşama | Çıktı |
|---|---|---|
| 0 | Ön koşul | Kullanıcı API Key + Access Token üretir (entegratör seçimi) |
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

- **Entegratör kaydı:** Ravencia PTT AVM'nin entegratör listesinde yoksa anahtar üretilemez; PTT AVM'den eklenmesi istenir.
- **Varyant silme:** upsert'e varyantsız gönderim tüm varyantları siler → yayınlamada katı kontrol.
- **Test ortamı yok:** yazmalar tek barkodla ve kullanıcı onayıyla açılır.
- **Sayfalamasız sipariş listesi ve saat dilimsiz tarih:** canlı veriyle doğrulanır.
- **Kargo kimliği belgede tutarsız:** canlıda denenir.

## 8. Doğrulananlar ve doğrulanacaklar

### 8.1 Resmi dokümandan doğrulananlar (developers.pttavm.com/tr, 21 Ekim 2026, tarayıcıyla okundu)

- **Başlıklar:** her uç için `Api-Key` (zorunlu), `access-token` (zorunlu; curl örneğinde `Access-Token`; HTTP başlık
  adı büyük/küçük harfe duyarsız), `X-Correlation-Id` (zorunlu), `Content-Type: application/json`. Adaptör her
  isteğe yeni bir GUID korelasyon kimliği koyar; GET isteklerinde gövde olmadığı için Content-Type yalnız gövdeli
  isteklerde gider (curl örnekleri de GET'te göndermiyor).
- **`GET categories/main`** (bağlantı testi): `{"success": true, "main_category": [{"id", "name", "updated_at"}],
  "error": {"error_code", "error_message"}}`. Hata zarfı `error.error_code` / `error.error_message`.
- **`GET orders/search?startDate=&endDate=&isActiveOrders=`** (Sipariş Kontrol V2): üçü de zorunlu; aralık ≤ 40 gün,
  bitiş başlangıçtan önce olamaz; `isActiveOrders=false` tüm siparişler. Yanıt **çıplak dizi**; sipariş alanları
  `siparisNo`, `islemTarihi`, `kargoBarkod`, `barcodes[{barcode,type}]`, `siparisUrunler[]` (`lineItemId`,
  `siparisDurumu`, `urunBarkod`, `variantBarkod`, `kdvDahilToplamTutar`, `komisyon`, `isInvoice`...). Sayfalama
  parametresi yok. Doküman tarih biçimini yazmıyor (`string / date-time`); probe `yyyy-MM-dd` gönderir.
- **`GET products/search`** (Stok Kontrol Listesi): `categoryId`, `subCategoryId`, `isActive`, `isInStock`,
  `merchantCategoryId`, `searchPage` **hepsi zorunlu** olarak işaretli; "tüm kategoriler" için hangi değerin
  gönderileceği yazmıyor. Yanıt çıplak dizi, kayıtta `rowCount`, `barkod`, `urunId`, `variantListesi[]`. Bu yüzden
  ürün okuma kanıtı bu uçla değil, aşağıdaki barkod sorgusuyla alınır; ürün çekme (aşama 3) parametreleri canlıda
  denenerek belirlenecek.
- **`POST products/get-by-barcodes`** (Barkod Kontrol Bulk): gövde `{"barcodes": ["..."]}`, yanıt ürün dizisi (aynı
  alanlar). Ayrıca `GET products` (Barkod Kontrol) tek ürün döndürüyor; barkod parametresinin adı dokümanda yok.
- Kimlik: anahtarlar satıcı panelinde Hesap Yönetimi › Entegrasyon Bilgileri'nde, seçilen entegratöre ve mağazaya özel
  üretilir (Duyurular: "API Key Entegrasyonu"). Kullanıcı adı/şifre yöntemi geçiş süresince paralel çalışıyor.

### 8.2 1. aşamada varsayılanlar (canlıda doğrulanacak)

- 401 ve 403 → `PTTAVM_CREDENTIALS_REJECTED` (HTML 403 sayfası → `PTTAVM_GATEWAY_BLOCKED`, geçici). Dokümanda hata
  durum kodları yazmıyor.
- Ürün okuma kanıtı: hiçbir mağazada olmayan bir barkodla (`RAVENCIA-CONNECTION-PROBE`) `get-by-barcodes`; yanıt
  dizi ise SUPPORTED, başka bir şeyse (ör. 400/404) UNKNOWN. UNKNOWN kalırsa etkinleştirme kuralı (ürün + sipariş
  okuma kanıtı) bağlantıyı etkinleştirmez; o zaman canlı yanıta göre probe düzeltilir.
- Sipariş probe'u dün–bugün (Türkiye saati) `isActiveOrders=false`; `endDate`in dahil olup olmadığı bilinmiyor.
- Hız kovaları mağaza başına (Access Token özeti ile); API Key entegratöre ortak olabileceği için anahtar değil.
- Devre kesici anahtarı ana makine + ilk iki yol parçası olduğundan PTT AVM'de tüm uçlar (`api/v1`) tek devre
  paylaşır; 2. aşamada aile bazına indirilebilir.

### 8.3 Hâlâ doğrulanacaklar

- `products/search` için "tümü" değerleri ve sayfa boyutu; `IslemTarihi` saat dilimi; kargo API kimliği.
