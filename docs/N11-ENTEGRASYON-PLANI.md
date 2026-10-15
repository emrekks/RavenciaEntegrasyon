# N11 Entegrasyonu Planı

Hazırlanma: 14 Ekim 2026. Durum (15 Ekim 2026): uygulama sürüyor.

- Tamam: bağlantı, referanslar ve eşleştirme, ürün çekme/eşleştirme, sipariş okuma ve mutabakatı, sipariş onaylama ve paket bölme (ilk kabul edilen işlemden sonra SHIPMENT_WRITE kanıtı kaydedilir), fiyat/stok gönderimi ve kanallar arası stok mutabakatı, hesap başına ortak hız sınırı.
- Tamam (15 Ekim): fatura linki (SOAP), iadeler (okuma + onay; ret N11 panelinden), müşteri soruları.
- Canlıda: ürün açma (ilk canlı deneme bekliyor), miktarla bölme/kısmi iptal, işçilik bedeli, toplama talebi (üçü de canlıda denenmedi), sipariş detayı alanları. Bekliyor: ürün güncelleme/silme/satıştan kaldırma, hızlı yükleme (SearchCatalog), `ClaimCancelPartial`, iade reddi/erteleme, kampanya kodlu etiket, N11 hata mesajı eşleyici, görsel yedek akışı. Her N11 yazması `N11HttpClient.WriteBlocked` kapısından geçer. Ayrıntılar: `docs/DEVAM-NOTU.md`.

## 1. Amaç ve temel ilke

N11, panelde Trendyol ile aynı çalışma şekliyle yer alacak. Kullanıcı açısından ürün, fiyat/stok, sipariş, kargo, fatura, iade ve soru ekranları Trendyol'da nasıl çalışıyorsa N11'de de öyle çalışacak:

- Listeler yerel PostgreSQL'den okunur.
- Senkronizasyon iş kuyruğu ve zamanlayıcıyla yürür.
- Dış yazmalar bağlantı ayarı, yazma politikası ve yetenek (capability) kanıtı kapılarından geçer.
- Hatalar Türkçe ve aksiyon önerisiyle gösterilir.

Fark yalnızca adaptör katmanında olacak: her istek N11'in resmî sözleşmesine, limitlerine ve kurallarına göre yazılacak. N11'in izin vermediği işlemler panelde gizlenmeyecek. Ekranda "N11 bu işlemi API'den desteklemiyor" açıklamasıyla pasif gösterilecek.

Kaynaklar:

- [developer.n11.com](https://developer.n11.com/)
- N11'in gönderdiği "n11 RestAPI Entegrasyon Servisleri" dokümanı
- [changelog](https://developer.n11.com/documentation/changelog/)

## 2. Kullanıcı kararları (14 Ekim 2026)

| Konu | Karar |
|---|---|
| Test ortamı | N11'de sandbox yok. Canlı mağaza kullanılacak; dış yazma kapalı başlar. Stok, fiyat ve sipariş yazmaları kullanıcı onayıyla tek tek açılır. |
| Öncelik | Önce siparişler ve stok/fiyat. |
| Eşleştirme anahtarı | N11 `stockCode` = paneldeki varyant SKU'su. Tutmazsa barkod denenir. |
| Ürün çekme | Diğer platformlardaki modlarla aynı: tam taramayla içe aktarma ya da eşleştirmeyle sadece çekme. |
| Ürün açma | Diğer platformlardaki gibi panelden açılabilir. N11'in güncellemeye izin vermediği alanlar ekranda belirtilir. |

## 3. N11 API özeti

### 3.1 Kimlik doğrulama ve adresler

- Her istekte `appkey` ve `appsecret` başlıkları gönderilir. Anahtarlar N11 satıcı panelinde **Hesabım > API Hesapları** altından alınır.
- REST adresleri:
  - `https://api.n11.com/ms/...`: ürün işlemleri
  - `https://api.n11.com/cdn/...`: kategori
  - `https://api.n11.com/rest/...`: sipariş ve paket
- SOAP adresi `https://api.n11.com/ws/*.wsdl`: soru, iade, fatura linki, katalog arama, kargo firmaları, kısmi iptal.
- Kategori servisleri de 15 Ağustos 2025'ten beri anahtar istiyor.
- Webhook yok. Tüm akışlar sorgulama (polling) ile çalışır.
- Test ortamı (sandbox) yok.

### 3.2 Servisler

| Servis | Yöntem ve adres | Kural / limit |
|---|---|---|
| Kategori ağacı | `GET /cdn/categories` | Tüm ağaç tek istekte gelir. `subCategories: null` en alt (yaprak) kategoridir. Ürün yalnız yaprak kategoriye açılır. |
| Kategori özellikleri | `GET /cdn/category/{id}/attribute` | Alanlar: `isMandatory`, `isVariant`, `isSlicer`, `isCustomValue`, `attributeValues[]`. Değerler aynı yanıtta gelir. |
| Ürün listeleme | `GET /ms/product-query` | Sayfa başına en fazla 250. `stockCode` filtresi her istekte tek değer alır. Filtreler: `saleStatus`, `productStatus`, `categoryIds`, `brandName`. **Değişiklik tarihi filtresi yok.** |
| Ürün yükleme | `POST /ms/product/tasks/product-create` | En fazla 1000 SKU; asenkron, görev numarası döner. |
| Hızlı ürün yükleme | Aynı servis, `catalogId` veya `barcode` ile | `attributes` ve `images` boş dizi `[]` gönderilir. Kategori farklıysa ürün "Satıcı Onayı Bekliyor" durumuna düşer. |
| Katalog arama | SOAP `CatalogService` / `SearchCatalog` | En fazla 10 başlık, barkod veya katalog ID. Başlık en az 10 karakter. |
| Ürün güncelleme | `POST /ms/product/tasks/product-update` | Yalnız şu alanlar değişir: `status` (Active/Suspended), `preparingDay`, `shipmentTemplate`, `currencyType`, `productMainId` (`deleteProductMainId` ile), `maxPurchaseQuantity` (`deleteMaxPurchaseQuantity` ile), `description`. `vatRate` zorunlu gönderilir. **Başlık, görsel, özellik ve kategori değişmez.** |
| Ürün silme | `POST /ms/product/tasks/product-delete` | En fazla 1000 SKU; asenkron. |
| Fiyat/stok | `POST /ms/product/tasks/price-stock-update` | En fazla 1000 SKU; asenkron. Ayrıntılı kurallar 3.3'te. |
| Görev sonucu | `POST /ms/product/task-details/page-query` | Görev durumu `IN_QUEUE`, `PROCESSED` veya `REJECT`. SKU bazında `SUCCESS`/`FAIL` ve neden. |
| Sipariş/paket listeleme | `GET /rest/delivery/v1/shipmentPackages` | Ayrıntılı kurallar 3.3'te. |
| Sipariş onayı | `PUT /rest/order/v1/update` | Yalnız `status=Picking`, yalnız Created kalemler için. Birden çok satır gönderilebilir; hatalı satır diğerlerini durdurmaz. |
| Paket bölme | `POST /rest/delivery/v1/splitCombinePackage` | Yalnız Picking paketlerde. Ana paket UnPacked olur, yeni paketler Picking başlar ve her birine yeni kampanya kodu verilir. |
| Miktar bazlı bölme + kısmi iptal | `POST /rest/delivery/v1/splitPackageByQuantity` | Yalnız Picking. İptal nedenleri: 61 Stok Tükendi, 62 Kusurlu, 63 Hatalı Fiyat, 64 Mücbir Sebep, 65 Diğer. İptal yalnız bölme ile birlikte yapılabiliyor. |
| Kısmi iptal (eski servis) | SOAP `ClaimCancelService` / `ClaimCancelPartial` | "Onaylandı" ve "Geciken Kargolama" siparişler için. Yeni siparişler yalnız N11 panelindeki **Reddet** ile iptal edilir. |
| İşçilik bedeli | `PUT /rest/order/v1/labor-costs` | KDV oranı varsayılan 20. |
| Toplama talebi | `PUT /rest/delivery/v1/collectionRequest` | Yalnız lojistik firmaları (HLZ, CEVA, BL) ve Picking kalemler; paketin tüm kalemleri gönderilir. |
| Fatura linki | SOAP `SellerInvoiceService` / `SaveLinkSellerInvoice` | https bağlantılı pdf, png veya jpeg; en fazla 2048 karakter. Siparişte tek aktif link olur, yeni link eskisinin yerine geçer. Mikro ihracatta `invoiceNumber` ve `invoiceDateTime` zorunlu. |
| İadeler | SOAP `ReturnService` | Ayrıntılar 9. aşamada. |
| Sorular | SOAP `productService` | Listeleme dakikada 1 kez. Liste, detay ve cevap servisleri ayrı. Ayrıntılar 10. aşamada. |
| Kargo firmaları | SOAP `ShipmentCompanyService` | İade reddinde (kendi kargonla gönderim) kullanılır. |

### 3.3 Ayrıntılı kurallar

Fiyat/stok gönderimi:

- `listPrice` ve `salePrice` birlikte gönderilir.
- Ondalık ayracı nokta, ondalık hane sayısı tam 2.
- `listPrice` ≥ `salePrice` olmalı.
- Stok en fazla 999.999.
- Gönderilmeyen alan güncellenmez.

Sipariş/paket listeleme:

- Dakikada en fazla 1000 istek, sayfa başına en fazla 100 kayıt.
- Sorgu penceresi en fazla 15 gün. Kasım 2024 öncesi veri yok.
- `orderByField=true` ile son değişiklik tarihine göre sorgulanır.
- `status` filtresi her istekte tek değer alır.
- Tarihler milisaniye cinsinden ve GMT+3.
- Konuma özel teslimatta paket numarası (`id`) boş gelir.
- `deliveryAddressType` alanı teslimat noktası siparişlerini işaretler: KTN, EASYPOINT veya PUP.

### 3.4 Ürün yükleme doğrulama kuralları (N11 hata listesinden)

- Başlık 15–255 karakter.
- KDV oranı 0, 1, 10 veya 20.
- Görseller https, en fazla 10 MB, ilk görsel boş olamaz.
- `stockCode` mağazada benzersiz ve en fazla 255 karakter. Aynı istekte tekrarlanan stok kodu, barkod veya katalog ID olamaz.
- Zorunlu özellikler (`isMandatory`) dolu olmalı. Özellik serbest metin kabul etmiyorsa (`isCustomValue=false`) yalnız `valueId` gönderilir.
- Marka, "Marka" özelliği (id 1) olarak gönderilir ve null olamaz.
- `isSlicer` özelliği olan kategorilerde `productMainId` zorunlu.
- Kargo şablonu (`shipmentTemplate`) zorunlu ve satıcı panelindeki şablon adıyla aynı olmalı.
- Açıklama boş olamaz, kapanmamış HTML etiketi içeremez.
- Başlık ve açıklamada yasaklı kelime olamaz.

## 4. Trendyol akışı → N11 karşılığı

| Panel akışı | Trendyol'da | N11'de | Fark |
|---|---|---|---|
| Bağlantı testi | Hafif okuma | `product-query?size=1`. Yanıttaki `sellerId` ve `sellerNickname` mağaza kimliği olarak kaydedilir. | Yok |
| Yetenek keşfi | Okuma uçlarıyla kanıt | Okuma yetenekleri test çağrısıyla. Yazma yetenekleri kullanıcı onaylı ilk kontrollü yazmayla. | Sandbox olmadığı için kanıt canlıda toplanır. |
| Kategori, marka, özellik referansı | 4 ayrı kaynak | Kategori ağacı ve kategori bazlı özellikler (değerler aynı yanıtta). Ayrı marka listesi yok. | Marka, özellik eşlemesinde id 1 "Marka" olarak ele alınır. |
| Kategori/özellik/değer eşleme, şablonlar, öneriler | Var | Aynı ekranlar | Yok |
| Ürün çekme | Ürün listesi + katalog içe aktarma | `product-query` sayfa sayfa (250) | Değişiklik tarihi filtresi yok; her çekme tam tarama. |
| Ürün eşleştirme | Barkod | Önce `stockCode` = SKU, sonra barkod | Anahtar stok kodu. |
| Ürün açma | Toplu gönderim + sonuç sorgusu | `product-create` + görev sorgusu | Hızlı yükleme (katalog/barkod) seçeneği eklenir. |
| Onay takibi | Yayın durumu sorgusu | `product-query?stockCode=` ile `productStatus`/`saleStatus` | Yok |
| Ürün güncelleme | İçerik, varyant, teslimat | Yalnız açıklama, durum, hazırlık günü, kargo şablonu, KDV, model kodu, azami adet | Başlık, görsel ve özellik değişmez; ekranda uyarı. |
| Arşivleme | Arşiv | `status=Suspended`; geri açma `Active` | Silme ayrıca var (onaylı, geri alınamaz). |
| Fiyat/stok | Tek çağrı, barkod anahtarı | `price-stock-update`, `stockCode` anahtarı, sonuç görev sorgusuyla | 2 ondalık, nokta, liste ≥ satış kuralları |
| Stok uzlaştırma | Kısa/orta/günlük | Aynı zamanlayıcı, `product-query` tam taramasıyla | Değişiklik filtresi yok. |
| Sipariş çekme | Akış + kurtarma + durum | Paket listesi, `orderByField=true`, en fazla 15 günlük pencereler | Kasım 2024 öncesi yok. |
| Sipariş onayı (Picking) | Paket güncelleme | Satır bazında `Picking` | Yok |
| Faturalandı bildirimi | Var | Yok; fatura linki ayrı servisle gönderilir | — |
| Kargo takip numarası girişi | Var | Yok | N11'de kampanya koduyla anlaşmalı kargoya verilir. |
| Paket bölme | Var | Var (satır ve miktar bazlı) | Yalnız Picking'de. |
| Kalem iptali (tedarik edilemedi) | Var | Miktar bazlı bölmeyle birlikte veya `ClaimCancelPartial` | Yeni sipariş API'den reddedilemez. |
| Kargo firması değiştirme | Var | Yok | Pasif |
| Ortak etiket | ZPL servisi | Etiket servisi yok | Kendi etiket penceremiz kampanya kodu barkoduyla basar (N11'e teyit ettirilecek). |
| İşçilik bedeli | — | Var | Yeni aksiyon |
| Toplama talebi | — | Lojistik siparişlerde var | Yeni aksiyon, yalnız HLZ/CEVA/BL |
| Fatura linki | Var | `SaveLinkSellerInvoice` (SOAP) | Geri okuma servisi yok; sonuç gönderim yanıtından. |
| İadeler | Liste + onay/ret | Liste + onay/ret + **erteleme** | Erteleme yeni aksiyon. Talep doğrudan sipariş satırına bağlanmaz. |
| Sorular | Liste + cevap | Liste (dakikada 1) + detay + tek seferlik cevap | Yalnız ürün soruları. |
| Webhook | Var | Yok | Yalnız sorgulama |
| Test siparişi | Stage | Yok | Pasif |

## 5. Mimari

### 5.1 Adaptör (`src/MarketplaceHub.Infrastructure/Adapters/N11/`)

| Dosya | Görev |
|---|---|
| `N11Options.cs` | Adresler (`/ms`, `/cdn`, `/rest`, `/ws`), sayfa boyutları, toplu gönderim sınırları, görev sorgu aralıkları, hız sınırı kovaları |
| `N11Endpoints.cs` | Uç adresleri; testte doğrulanacak `internal static` kurucular |
| `N11AuthenticationHandler.cs` | Şifreli kimliği çözer, `appkey`/`appsecret` başlıklarını ekler (Trendyol/HB handler deseni) |
| `N11ResilienceHandler.cs` | Paylaşılan PostgreSQL token kovası (`n11:{kova}`), 429/5xx yeniden deneme, devre kesici (Trendyol deseni) |
| `N11HttpClient.cs` + kısmi sınıflar | Port uygulamaları: `N11Products.cs`, `N11Orders.cs`, `N11PackageOperations.cs`, `N11Returns.cs`, `N11Questions.cs`, `N11Invoices.cs` |
| `N11SoapClient.cs` | HttpClient üzerinde küçük SOAP istemcisi (WCF yok): zarf oluşturma, XML kaçışı (`&` → `&amp;`), `result.status` ve hata okuma |
| `Mapping/N11JsonMapper.cs`, `Mapping/N11SoapMapper.cs` | N11 yanıtlarını `RemoteProduct`, `RemoteOrder`, `RemotePackage`, `RemoteReturnClaim` ve `RemoteMarketplaceQuestion` modellerine çevirir |
| `ErrorMapping/N11ErrorMapper.cs` | N11 hata metinlerini kalıcı/geçici sınıfa ve Türkçe aksiyon önerisine çevirir (örn. "Satıcı stok kodu kullanılmaktadır" → eşleştir veya kodu değiştir) |

### 5.2 Uygulanacak portlar

| Port | N11 uygulaması |
|---|---|
| `IConnectionPort` | Test ve yetenek keşfi |
| `IReferenceDataPort` | CATEGORIES, CATEGORY_ATTRIBUTES, ATTRIBUTE_VALUES. BRANDS desteklenmiyor. |
| `IProductPort` | List, ListCatalog, Create, güncelleme metotları, GetOperation (görev sorgusu), GetPublicationStatus, Archive |
| `IProductVisualLookupPort` | Barkod/stok koduyla tekil ürün okuma |
| `IInventoryPricePort` | `price-stock-update` |
| `IOrderPort` | Poll, Get, GetShipmentPackage, ExecutePackageAction. Etiket, kargo firması ve test siparişi metotları "desteklenmiyor" döner. |
| `IOrderPackageReadPort` | Paket bazlı tarama |
| `IReturnPort` | Poll, IssueReasons, Execute (APPROVE, REJECT, PENDING) |
| `IQuestionPort` | List, Get, Answer |
| `IInvoiceMarketplacePort` | Deliver (SOAP). QueryDelivery yerel kayıttan döner. |

### 5.3 Kayıt ve yönlendirme

Envantere göre platform kodu birçok yerde sabit yazılı. N11 için dokunulacak yerler:

- `MarketplaceContracts.cs`: `ActiveIntegrationScope` (`Contains`, `IsMarketplace`), N11 iş tipleri, `ForPlatform`, `ProcessorJobTypes`.
- `Infrastructure/DependencyInjection.cs`: ayarlar, adlandırılmış HttpClient ("N11"), handler'lar, istemci.
- `MarketplacePortRouter.cs`: N11 kolu. `MarketplaceWebhookVerifier.cs`: N11 için "desteklenmiyor".
- `MarketplaceConnectionService.cs`:
  - N11 yetenek listesi.
  - Oluşturma ve güncelleme doğrulaması.
  - Yeni kimlik tipi `APP_KEY_SECRET` ve kimlik yenileme.
- `CapabilityEvidencePolicy.cs`: izinli kanıt sunucusu `api.n11.com`.
- `JobPriorityPolicy.cs`, `MarketplaceSyncExecutionLock.cs` (çakışma listeleri), `Worker.cs` (kuyruklar).
- `ScheduledJobProducer.cs` ve `ScheduledOrderPollingCadencePolicy.cs`.
- `SynchronizationPolicies.cs`: yeni `N11OrderHistoryPolicy` ve `N11ReturnHistoryPolicy`.
- `MarketplaceJobProcessor.cs`:
  - Varsayılan yol Trendyol yoludur.
  - N11'in farkları ayrı politika sınıflarıyla ifade edilir (aşağıda). Mevcut `isHepsiburada` dallarının yanına dağınık `isN11` kontrolleri eklenmez.
- `InvoicingJobProcessor.cs`, `MarketplaceQuestionService.cs`, `ConnectionDataResetPolicy`.

### 5.4 N11'e özel politika sınıfları

- `N11ShipmentPackageStatusPolicy`: bkz. 6.2.
- `N11ReturnStatusPolicy`: bkz. 6.3.
- `N11PriceInventoryRules`:
  - Fiyatlar 2 haneye yuvarlanır.
  - Liste fiyatı satıştan küçükse satış fiyatına eşitlenir ve uyarı verilir.
  - Stok 0–999.999 aralığına sınırlanır.
  - Gönderimler 1000'lik parçalara bölünür.
- `N11ProductValidationPolicy`: bkz. 3.4.
- `N11OrderWindowPolicy`: 15 günlük pencere bölme ve en erken tarih 1 Kasım 2024.

### 5.5 Bağlantı ayarları (`SettingsJson`)

- `Integrator`: varsayılan "Ravencia". N11 her istekte aynı adı istiyor.
- `ShipmentTemplate`: N11 panelindeki kargo şablon adı.
- `DefaultPreparingDay`
- `CurrencyType`: TL
- `ExternalWritesEnabled`: varsayılan **kapalı**

Kimlik bilgisi `{appKey, appSecret}` Data Protection ile şifrelenir. Maskeli ipucu `appKey`'in son 4 hanesidir.

### 5.6 Hız sınırı kovaları (başlangıç değerleri)

| Kova | Değer | Gerekçe |
|---|---|---|
| `n11:orders` | 600/dk | Belgelenen sınır 1000/dk; payla |
| `n11:products` | 120/dk | Belgelenmedi; temkinli |
| `n11:tasks` | 120/dk | Belgelenmedi |
| `n11:categories` | 30/dk | Belgelenmedi |
| `n11:soap` | 60/dk | Belgelenmedi |
| `n11:questions-list` | 1/dk | Belgelenen sınır |

Gerçek yanıt başlıkları ve 429 davranışı canlıda loglanıp değerler sonra ayarlanacak.

## 6. Veri eşlemeleri

### 6.1 Sipariş ve paket

| N11 alanı | Panel alanı |
|---|---|
| `orderNumber` | `ExternalOrderId` ve `OrderNumber` (13 haneye çıkabilir; metin olarak tutulur) |
| `id` (paket no) | `ExternalPackageId`. Konuma özel teslimatta boş gelir; yerine `cargoTrackingNumber` kullanılır ve işaret konur. |
| `cargoTrackingNumber` | Kargo kampanya kodu (etiket barkodu) |
| `cargoSenderNumber`, `cargoTrackingLink` | Takip numarası ve linki |
| `shipmentCompanyId`, `cargoProviderName` | Kargo firması |
| `lines[].orderLineId` | `ExternalLineId` |
| `stockCode` | `Sku` |
| `barcode` | `Barcode` |
| `productId` | Satır snapshot'ında saklanır (iade eşleştirmesi için) |
| `price` | `UnitPrice` |
| `vatRate` | KDV |
| `sellerDiscount + sellerCouponDiscount` | Satıcı indirimi |
| `mallDiscount` | N11 indirimi (bilgi amaçlı) |
| `sellerInvoiceAmount` toplamı | Faturalanacak net tutar |
| `commissionRate − sellerCampaignCommissionRate` | Gerçek komisyon oranı |
| `agreedDeliveryDate` | `ShipmentDueAt` (en geç kargolama) |
| `packageHistories` | Durum geçmişi. Baştaki boşluklar temizlenir (" Shipped"). |
| `billingAddress`, `shippingAddress` | Adres snapshot'ları. `tcId` boş olabilir. Teslimat noktası siparişinde müşteri bilgisi fatura adresinden okunur. |
| `micro`, `countryCode`, `hsCode`, `etgbNo` | Mikro ihracat işareti (yalnız gösterim; ihracat akışı kapsam dışı) |

### 6.2 Paket durumu

| N11 | Panel |
|---|---|
| Created | Yeni (`New`) |
| Picking | Hazırlanıyor (`Processing`) |
| Shipped | Kargoda |
| Delivered | Teslim edildi |
| Cancelled | İptal |
| UnSupplied | İptal (tedarik edilemedi) |
| UnPacked | Bölünmüş ana paket. İşlem dışı sayılır, yerini yeni paketler alır. Toplu durum hesabında iptal gibi davranır. |
| Bilinmeyen | Manuel inceleme |

### 6.3 İade durumu

| N11 | Panel |
|---|---|
| REQUESTED | Talep edildi, aksiyon bekliyor |
| PENDING, PENDED | Ertelendi, aksiyon bekliyor |
| APPROVED | Onaylandı |
| DENIED | Reddedildi |
| CANCELLED | İptal |
| MANUAL_REFUND | Tamamlandı |

### 6.4 Ürün durumu

| N11 | Panel |
|---|---|
| `Active` + `On_Sale` | Satışta |
| `Active` + `Out_Of_Stock` | Stok yok |
| `Active` + `Before_Sale` | Satış öncesi |
| `Sale_Closed` | Satış kapalı |
| `Suspended` | Pasif / arşiv |
| `InApproval`, `InCatalogApproval` | Onay bekliyor |
| `CatalogRejected`, `Prohibited` | Reddedildi. Neden N11 panelinde; ürün silinip düzeltilerek yeniden açılır. |
| `Unlisted` | Listelenmiyor |

### 6.5 Ürün çekme

- `n11ProductId` → dış ürün kimliği, `productMainId` → model kodu (varyant grubu), `groupId` → katalog grubu.
- `attributes[]` yalnız ad ve değer metni içerir (değer kimliği yok). Bu yüzden değer eşlemesi metinle yapılır.

## 7. İş tipleri ve zamanlama

Trendyol'daki genel iş adları korunur. Okuma senkronları `ForPlatform` ile N11'e özel adlara yönlenir (Hepsiburada deseni). Yazma işleri port üzerinden ortak işlerde çalışır.

| İş | Tür | Zamanlama |
|---|---|---|
| `N11_CONNECTION_TEST` | N11'e özel | Elle / bağlantı açılışında |
| `N11_REFERENCE_SYNC` | N11'e özel | Günde 1 (`REFERENCE_DATA`) |
| `N11_PRODUCT_SYNC` | N11'e özel | Elle + stok uzlaştırma |
| `N11_ORDER_SYNC` | N11'e özel | 60 sn (`ORDERS`), son değişiklik imleci + 10 dk güvenlik penceresi |
| `N11_ORDER_RECOVERY_SYNC` | N11'e özel | 15 dk. Geriye 15 günlük pencerelerle; ilk kurulumda 3 ay (en erken Kasım 2024). |
| `N11_ORDER_STATUS_SYNC` | N11'e özel | 3 dk (`ORDER_LIFECYCLE`), açık paketler |
| `N11_RETURN_SYNC` | N11'e özel | 3 dk (`RETURNS`) |
| `MARKETPLACE_QUESTION_SYNC` | Ortak | N11 için **2 dk** (liste sınırı 1/dk) |
| `PRICE_INVENTORY_SYNC`, `STOCK_PROJECTION_DISPATCH`, `STOCK_RECONCILIATION` | Ortak | Mevcut zamanlama |
| `PRODUCT_CREATE`, `_UPDATE`, `_ARCHIVE`, `_APPROVAL_RECONCILE` | Ortak | Mevcut |
| `SHIPMENT_ACTION`, `RETURN_ACTION`, `INVOICE_MARKETPLACE_DELIVERY` | Ortak | Kullanıcı aksiyonuyla |
| `ORDER_RECONCILIATION` (kısa/orta/günlük) | Ortak | Mevcut |

Görev (task) sorgusu Trendyol toplu gönderim takibi gibi çalışır:

- İlk sorgu 5 sn sonra, sonra artan aralıklarla.
- 30 dk içinde `PROCESSED` gelmezse "zaman aşımı, yeniden sorgulanacak" durumuna geçer.
- Sonuç SKU (`itemCode`) bazında satıra işlenir.

## 8. Güvenlik kapıları

1. Bağlantı açılışında `ExternalWritesEnabled=false`. Okuma yetenekleri test çağrısıyla kanıtlanır.
2. Her yazma yeteneği ayrı açılır ve hepsi kullanıcı onayıyla yapılır:
   - **Fiyat/stok:** önce tek SKU'ya N11'den okunan *aynı* fiyat ve stok gönderilir. Bu değişiklik yaratmayan bir yazmadır; görev sonucu `SUCCESS` gelince kanıt kaydedilir. Sonra küçük grup, sonra tümü.
   - **Sipariş onayı:** kullanıcının seçtiği ilk gerçek sipariş.
   - **Ürün açma:** kullanıcının seçtiği tek ürün.
3. Geri alınamaz işlemler (ürün silme, kısmi iptal, iade reddi) her seferinde onay penceresi ister.
4. Sunucuda yalnız salt-okunur kontrol yapılır. Canlı veritabanına ve konteynerlere yazma yapılmaz.

## 9. Aşamalar

Her aşama küçük commit'lerle ana dala gider. Her commit'in Validate ve Deploy sonucu takip edilir. Bir commit'in deploy'u bitmeden sonraki gönderilmez.

### Aşama 1 — Temel ve bağlantı

- Commit'ler:
  1. `ActiveIntegrationScope` ve yönlendiriciye N11 kolu eklenir; tüm portlar önce "desteklenmiyor" döner.
  2. Ayarlar, kimlik doğrulama handler'ı, REST istemcisi ve hata eşleyici, testleriyle.
  3. Bağlantı oluşturma, test ve yetenek keşfi.
  4. Panel bağlantı formu:
     - Alanlar: mağaza adı, API Key, API Secret, kargo şablon adı, hazırlık günü, entegratör adı.
     - Kimlik yenileme.
     - Formun mobil düzeni lazy CSS'te.
- Çıkış:
  - Canlı mağaza bağlantı testi yeşil.
  - Mağaza adı ve `sellerId` panelde görünüyor.
  - Hiçbir yazma yapılmadı.

### Aşama 2 — Siparişler (okuma) ve etiket

- Sipariş, kurtarma ve durum işleri; 15 günlük pencere politikası.
- Eşleyici ve durum politikası.
- Sipariş listesi ve detayında N11 alanları: kampanya kodu, teslimat noktası, komisyon, N11 indirimi.
- Kargo etiketi penceresi kampanya kodunu Code128 barkod olarak basar. Biçim N11'e teyit ettirilecek.
- Çıkış:
  - Son 3 ayın siparişleri tutarları N11 panelindeki değerlerle aynı.
  - Durumlar 5 dk içinde güncelleniyor.

### Aşama 3 — Referanslar ve ürün çekme/eşleştirme

- Kategori ağacı ve seçili kategorilerin özellik snapshot'ları.
- Eşleme ekranı, şablonlar ve öneriler N11 için açılır. Marka, id 1 özelliği olarak ele alınır.
- Ürün çekme modları:
  - **Tam tarama:** tüm sayfalar çekilir; eşleşmeyenler için yerel ürün önerilir (Trendyol katalog içe aktarma gibi).
  - **Sadece eşleştirme:** mevcut yerel varyantlara `stockCode` ve barkodla bağlanır, yeni yerel ürün açılmaz.
  - **Tekil:** stok kodu veya N11 ürün koduyla.
- Eşleşme raporu: bağlandı, belirsiz, eşleşmedi.
- Çıkış: N11 ürünleri listede N11 rozetiyle görünüyor; fiyat ve stok gözlemleri kaydediliyor.

### Aşama 4 — Fiyat ve stok

- Fiyat/stok bileşeninde N11 kolu ve `N11PriceInventoryRules`.
- Görev sonucu takibi; satır bazında hata.
- Stok projeksiyon tetikleyicisi ve tam taramayla uzlaştırma.
- Bölüm 8'deki kontrollü açılış.
- Çıkış: kullanıcı onayıyla açıldı; bir stok değişikliği 2 dk içinde N11'de görünüyor.

### Aşama 5 — Sipariş aksiyonları

- Onayla (Picking).
- Paketi böl (satır ve miktar bazlı).
- Kalem iptali:
  - Picking paketlerde bölmeyle birlikte.
  - Onaylanmış siparişlerde `ClaimCancelPartial` ile.
  - Yeni siparişlerde "N11 panelinden reddedin" yönlendirmesi.
- İşçilik bedeli.
- Toplama talebi (yalnız HLZ/CEVA/BL kargolu siparişlerde görünür).
- Desteklenmeyenler pasif ve açıklamalı: takip numarası, kargo firması değiştirme, faturalandı bildirimi.
- Çıkış: her aksiyon kullanıcı onaylı ilk denemeyle kanıtlandı.

### Aşama 6 — Fatura linki

- `INVOICE_MARKETPLACE_DELIVERY` N11 kolu: SOAP gönderim, link doğrulama (https, uzantı, uzunluk).
- Tekrar gönderimde "eski linkin yerini alır" uyarısı.
- Çıkış: kesilen fatura linki N11 siparişinde görünüyor.

### Aşama 7 — İadeler

- `ClaimReturnList` taraması (sayfa başı 20, durum ALL, tarih penceresi).
- Talep → sipariş satırı eşleştirmesi: sipariş no + N11 ürün kodu + varyant adı. Belirsizse kayıt bağlanmaz, sorun kaydı açılır.
- Aksiyonlar:
  - Onayla.
  - Reddet: neden listesi N11'den alınır, açıklama zorunlu, isteğe bağlı görsel ve video linki, kargo tipi.
  - **Ertele:** neden, gün sayısı, açıklama. İade ekranına yeni aksiyon.
- Çıkış: iadeler panelde. Onay, ret ve erteleme ilk denemede kullanıcı onayıyla kanıtlandı.

### Aşama 8 — Müşteri soruları

- Liste 2 dk'da bir; yeni soru için detay çağrısı (müşteri adı, tarih).
- Cevap tek seferlik ve 1–2048 karakter. Ekranda "N11'de cevap bir kez verilir, değiştirilemez" uyarısı.
- Hazır cevap şablonları mevcut ekrandan kullanılır.
- Çıkış: sorular ve cevaplar panelde; ilk cevap kullanıcı onayıyla gönderildi.

### Aşama 9 — Ürün açma ve güncelleme

- N11 yayın bileşeni:
  - Özellikler: değer kimliği ya da serbest metin.
  - Model kodu gruplaması; varyant özellikleri.
  - Kargo şablonu, hazırlık günü, KDV.
- Hızlı yükleme: `SearchCatalog` ile katalog/barkod eşleşmesi, sonra yükleme.
- Güncelleme: izin verilen alanlar. Diğer alanlarda ekran uyarısı ve "Sil ve yeniden aç" seçeneği (onaylı).
- Satıştan kaldır / satışa aç; silme (onaylı).
- Onay takibi; "Satıcı Onayı Bekliyor" durumunda "N11 panelinden onaylayın" uyarısı.
- Çıkış: kullanıcının seçtiği ilk ürün N11'de açıldı ve onay takibi çalıştı.

### Aşama 10 — Sağlamlaştırma

- Gerçek yanıt başlıklarına göre hız sınırı ayarı; devre kesici eşikleri.
- İzleme panosuna N11 metrikleri.
- `RAVENCIA-SISTEM-ISLEYIS-BELGESI.md` güncellemesi ve bu belgenin durum bölümü.
- Mobil/tablet kontrolü: yeni aksiyon pencereleri alt sayfa (bottom sheet) olarak.

## 10. Test planı

- `tests/MarketplaceHub.Application.Tests/N11AdapterTests.cs` (Hepsiburada adaptör testleri deseni):
  - Uç adresleri ve `appkey`/`appsecret` başlıkları.
  - Sayfalama.
  - Dokümandaki örnek JSON ve SOAP yanıtlarıyla eşleyici testleri.
- Politika testleri:
  - Paket, iade ve ürün durum eşlemeleri.
  - 15 günlük pencere bölme.
  - Fiyat yuvarlama, liste ≥ satış kuralı, stok sınırı, 1000'lik parçalama.
  - Ürün doğrulama kuralları.
- SOAP istemci testleri: zarf, XML kaçışı, `failure` yanıtı, bozuk XML.
- Hız sınırı ve 429 testleri (Trendyol dayanıklılık testleri deseni).
- PostgreSQL testleri: sipariş ve paket kaydı, iade eşleştirmesi, fiyat/stok görev sonucunun satırlara işlenmesi.
- Canlı doğrulama: her aşamanın çıkış maddesi, kullanıcıyla birlikte ve salt-okunur kontrol önce.

## 11. N11'e sorulacaklar (sellerintegration@n11.com)

1. Ürün, kategori, görev ve SOAP servislerinin hız sınırları nedir?
2. Kampanya koduyla kendi bastığımız etiket anlaşmalı kargoda kabul ediliyor mu? Bir etiket/barkod servisi var mı?
3. `UpdateOrder` için Shipped veya takip numarası güncellemesi ne zaman gelecek? Satıcının kendi kargoladığı (`sender=SELLER`, konuma özel) siparişlerde akış nedir?
4. `shipmentPackages` `status` gönderilmeden tüm durumları döndürüyor mu?
5. Yeni (Created) sipariş API'den reddedilebilir mi?
6. Tek kalemli pakette yalnız iptal mümkün mü? `ClaimCancelPartial` hâlâ geçerli mi?
7. İade talebindeki `skuId` ile sipariş satırı (`orderLineId`) arasında bağ var mı?
8. Fatura linkini geri okuyabileceğimiz bir servis var mı?
9. Soru detay servisinin sınırı nedir?
10. `product-query` için değişiklik tarihi filtresi var mı ya da planlanıyor mu?
11. Test mağazası verilebilir mi?

## 12. Riskler

- **Sandbox yok:** her yazma canlıda. Bölüm 8'deki kademeli açılış ve değişiklik yaratmayan ilk yazma bu riski azaltır.
- **Ürün çekmede değişiklik filtresi yok:** büyük kataloglarda tam tarama uzun sürer. Sayfa bazlı imleç ve devam ettirme (Trendyol katalog içe aktarma gibi) gerekir.
- **İade-sipariş bağı dolaylı:** eşleşmeyen talepler sorun kaydına düşer ve elle bağlanır.
- **Dokümanda tutarsızlıklar var:** örn. iade onayında `claimReturnId`/`claimCancelId` ve `orderByField`/`orderbyField` yazımları. Gerçek WSDL ve canlı yanıtla doğrulanacak.
- **Ürün güncellemesi sınırlı:** başlık, görsel veya özellik değişikliği ürünü silip yeniden açmayı gerektirir. Bu, ürünün yorum ve satış geçmişini kaybettirebilir; ekranda açıkça uyarılacak.
