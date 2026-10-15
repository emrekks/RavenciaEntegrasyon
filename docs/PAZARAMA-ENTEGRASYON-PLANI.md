# Pazarama Entegrasyonu Planı

Durum (15 Ekim 2026): kararlar alındı (bölüm 2). Aşama 1 (bağlantı) ve Aşama 2 (siparişler, okuma) canlıda; son 3 ayın 3 Pazarama siparişi içeri alındı ve doğrulandı. Aşama 3 (referanslar, ürün çekme/eşleme) kodlandı ve yayında; Aşama 3 canlıda doğrulandı (3.169 varyantın 3.153ü bağlandı). Aşama 4 (fiyat-stok) kodlandı; dış yazma kapalı, ilk gönderim kullanıcı onayıyla tek barkodla yapılacak.

Bu plan N11 entegrasyonunda izlenen yolu izler (`docs/N11-ENTEGRASYON-PLANI.md`). N11'den öğrenilenler ve yeni platform ekleme kontrol listesi (bölüm 5.3) bu deneyimden çıkarıldı.

## 1. Amaç ve temel ilke

- Pazarama, panelde Trendyol ve N11 gibi çalışacak; ama Pazarama'nın kurallarına göre yazılacak.
- Aynı ekranlar, aynı iş kuyruğu, aynı port sözleşmeleri. Pazarama'ya özgü olan yalnız adaptör, eşlemeler ve platform politikaları.
- **Canlı mağaza, dış yazma kapalı başlar.** Dokümanda test ortamı yok. Her yazma yeteneği kullanıcı onayıyla, tek kayıtla, izlenerek açılır.
- Her yazma, N11'de kurulan desende adaptör içindeki tek kapıdan geçer: sunucu genel anahtarı + bağlantı anahtarı + canlı ortam + doğrulanmış/aktif bağlantı (+ ilgili yazma politikası üst katmanda).
- Okuma yetenekleri bağlantı testinde kanıtlanır; yazma yetenekleri ilk kabul edilen canlı yazmayla kanıtlanır.

## 2. Kullanıcı kararları (15 Ekim 2026)

- **Mağaza:** canlı mağaza; dış yazma kapalı başlar, her yazma yeteneği kullanıcı onayıyla tek tek açılır.
- **Kargo modeli: Pazarama anlaşmalı kargo.** Statü 12 (Hazırlanıyor) sonrasında kargo süreci Pazarama tarafından otomatik ilerler; panel "Kargoya verildi" (5) göndermez, takip bilgisini siparişten okur. Etiket/gönderi kodu `shipmentCode` (PZ...). Kendi kargo akışı (5 + takip no) kod yolunda kapalı tutulur.
- **Eşleşme anahtarı:** barkod (Pazarama'nın fiyat/stok anahtarı), SKU (`stockCode`) çapraz kontrol.
- **Öncelik:** önce siparişler + fiyat/stok (N11'deki sıra).
- **Ürün çekme:** mevcut modlar (tam tarama / yalnız eşleme).
- **Başlangıç fiyatı:** Pazarama'daki mevcut fiyat.
- **Sipariş servisi:** `getOrdersForApi` (V1); iadelerde `SplitItems=false`.
- **API bilgisi:** kullanıcı Pazarama panelinde üretip bağlantı formuna kendisi girer (anahtar bir kez gösterilir; yeni anahtar eskisini anında iptal eder).

## 3. Pazarama API özeti

### 3.1 Kimlik doğrulama ve adresler

- Token: `POST https://isortagimgiris.pazarama.com/connect/token`
  - Basic Auth: `clientId:clientSecret` (panel: Hesabım › Entegrasyon Bilgileri).
  - Gövde (form-urlencoded): `grant_type=client_credentials`, `scope=merchantgatewayapi.fullaccess`.
  - Token 1 saat geçerli. Bağlantı başına önbellekte tutulur, süresi dolmadan 5 dk önce yenilenir; 401'de bir kez yenilenip tekrar denenir.
- API tabanı: `https://isortagimapi.pazarama.com/` ve her istekte `Authorization: Bearer <token>`.
- Satıcı "Yeni API Key Üret" derse eski anahtar **anında** iptal olur. Panelde kimlik yenileme (rotate) bu yüzden zorunlu ve bağlantı testi hemen yeni anahtarla yapılmalı.
- Ortak yanıt zarfı: `{ data, success, messageCode, message, userMessage, fromCache }`. `success=false` ise `userMessage`/`message` kullanıcıya gösterilir, `messageCode` (ör. `CAT_133`, `ORD0`, `MER0`, `BSK0`) hata eşlemesinde kullanılır.
- Para alanları siparişte nesne olarak gelir: `{ currency, value, valueInt, valueString }` → `value` kullanılır.
- Kimlikler GUID'dir (sipariş `orderId`, kalem `orderItemId`, kategori, özellik, değer, marka, kargo firması, iade `refundId`). Sipariş numarası `orderNumber` sayıdır (long).

### 3.2 Servisler

| İş | Servis | Not |
|---|---|---|
| Marka listesi | `GET brand/getBrands?Page&Size&name` | Ürün açmada `brandId` (GUID) zorunlu. |
| Kategori ağacı | `GET category/getCategoryTree` | Ürün yalnız `leaf=true` kategoriye açılır. |
| Kategori özellikleri | `GET category/getCategoryWithAttributes?Id=` | `isRequired`, `isVariantable`, değer listesi (GUID). Serbest metin yok. |
| Şehirler | `GET parameter/cities` | Teslimat tipi şehir seçimi için. |
| Teslimat tipleri / kargo firmaları | `GET sellerRegister/getSellerDelivery` | Kargo firması GUID'leri, `contracted` (anlaşmalı) bilgisi. |
| Ürün açma | `POST product/create` | En fazla 500 ürün/istek, istekler arası 10 sn. Yanıt `batchRequestId`. |
| Ürün açma sonucu | `GET product/getProductBatchResult?BatchRequestId=` | Durum 1 İşleniyor, 2 Bitti, 3 Hata; `failedProducts[].errorReason`. 4 saat sorgulanabilir. |
| Onaylı ürünler | `GET product/products/approved?Size=100&Cursor&Code&StartDate&EndDate` | İmleç (cursor) sayfalama, en fazla 100/istek. |
| Onaysız ürünler | `GET product/products/unapproved?pageIndex&pageSize&Code&StartDate&EndDate` | Onay bekleyen ve reddedilenler. |
| Tek ürün ayrıntı | `POST product/getProductDetail {Code}` | Durum (`state`, `stateDescription`), özellikler (GUID), görseller. |
| Fiyat güncelleme | `POST product/updatePrice-v2 {items:[{code,listPrice,salePrice}]}` | Anahtar **barkod** (`code`). %70 ve üzeri indirim onaya düşer. |
| Stok güncelleme | `POST product/updateStock-v2 {items:[{code,stockCount}]}` | Anahtar barkod. |
| Fiyat/stok sonucu | `GET listing-state/batch-id/{dataId}/lake-projections?page&pageSize` | Kod bazında `operationStatus`: 0 Başarılı, 1 Tamamlanamadı, 2 Hata, 3 İşleniyor, 5 Onaya gönderildi. Sonuç 4 saate kadar gecikebilir. |
| Katalog arama | `POST product/getProductTitleCodeSearch` | Pazarama kataloğunda barkod/ad arama (`CAT_133`: barkod zaten sizde). |
| Hızlı ürün ekleme | `POST product/addProductsWithBarcodes` | Katalogdaki ürüne fiyat/stokla teklif açar. |
| Satışa kapat/aç | `POST product/external-status` (tekli), `POST product/bulkUpdateProductStatusFromApi` (toplu) | `productStatus` 1 = aç, 10 = kapat. Toplu sonuç `lake-projections?lakeType=1`. |
| KDV güncelleme | `PUT product/vatRate/bulk` | |
| Sipariş başına en fazla adet | `POST product/upsertSellerProductSaleLimit` | |
| Fiyat kilitleme | `POST price-lock/save-price-lock-api`, sonuç `GET product/price-lock/result` | 1000 satır/istek, en fazla 1 ay. Kapsam dışı (isteğe bağlı). |
| Temin bilgileri (yasal) | `product-commercials`, `product/upsertSellerProductCommercial`, `product-security-descriptions`, `product/upsertSellerProductBatchInfo`, `product/upsertSellerProductSecurityDocuments` | Ürün güvenliği yönetmeliği bilgileri. Aşama 10. |
| Siparişler | `POST order/getOrdersForApi {pageSize,pageNumber,startDate,endDate,orderNumber}` | Tarih aralığı ≤ 1 ay; `endDate` günü **dahil değil**; sipariş numarasıyla 6 aya kadar. |
| Siparişler (bölünmüş) | `POST order/getOrdersForApiV2` | Aynı üründen her adet ayrı `orderItemId`. |
| Kalem durumu | `PUT order/updateOrderStatus {orderNumber,item:{orderItemId,status,...}}` | 12 Hazırlanıyor, 13 Tedarik edilemedi, 5 Kargoya verildi (+takip), 11/14 dijital. |
| Toplu durum | `PUT order/updateOrderStatusList {orderNumber,status}` | Siparişin tüm kalemleri (3 → 12). |
| Kalemleri tek seferde | `POST order/api/bulk-status-update` | Aynı takip bilgisiyle birden çok kalem. |
| Paketler | `GET order/api/shipment-packages/orderId=` | Paket durumu 0 Yok, 10 Paketli, 20 Eskimiş, 30 Tedarik edilemedi, 40 İptal. |
| Paket bölme | `POST order/api/packages-split` | Kalem grupları, depo adresi, kargo firması. |
| Paket güncelleme | `PUT order/update-packages` | Paketin kargo firması veya depo adresi. |
| İşçilik bedeli | `PUT order/api/{orderId}/items/labor-costs` | Yalnız stopajlı/altın kategorisi. |
| İptal talepleri | `POST order/api/cancel/items`, `PUT order/api/cancel {refundId,status}` | Alıcı Hazırlanıyor'da iptal ister (statü 18); satıcı 2 onay / 3 ret. |
| İadeler | `POST order/getRefund {pageSize,pageNumber,refundStatus,requestStartDate,requestEndDate,SplitItems}` | İade kargo kodu `shipmentCode` (anlaşmalı kargoda). |
| İade kararı | `POST order/updateRefund {refundId,status,refundRejectType,documentObjects,description,refundReviewType}` | 2 onay, 3 ret (gerekçe 1–12 zorunlu; 10 ve 11 dışında görsel zorunlu, ≤3 dosya, ≤5 MB); incelemeye gönderme. |
| İade revizyonu | `POST order/api/refund/revision` | Pazarama ek bilgi isterse. |
| Fatura linki | `POST order/invoice-link`, `POST order/multiple-invoice-link` | Sipariş, paket (kargo firması + takip no) veya kalem bazında. |
| Finans | `POST order/paymentAgreement {startDate,endDate}` | Komisyon, hakediş, aktarım tarihi, kargo borcu, stopaj. |
| Sorular | `GET QuestionAnswer/getQuestionStatus`, `POST QuestionAnswer/questionTopics`, `POST QuestionAnswer/getApprovalAnswersByMerchantSearch`, `GET QuestionAnswer/getApprovalAnswerById?questionId`, `PUT QuestionAnswer/sellerAnswer {questionId,text}` | Durum 0 Cevap bekliyor, 1 Cevaplandı, 2 Onay bekliyor, 3 Reddedildi. Cevap Pazarama onayına düşer. |

### 3.3 Hız ve boyut sınırları (dokümandan)

| Alan | Sınır |
|---|---|
| Fiyat/stok | Satıcı başına istekler arası **10 sn**; istek başına en fazla **3000** ürün. |
| Ürün açma | Satıcı başına istekler arası **10 sn**; istek başına en fazla **500** ürün. |
| Fiyat kilitleme | İstek başına 1000 satır. |
| Sipariş, iade, soru, referans | Dokümanda yazmıyor → temkinli başlangıç değerleri (5.6), Pazarama'ya sorulacak. |

### 3.4 Ürün açma kuralları

- Zorunlu: `Name` (≤100), `DisplayName` (≤250), `Description`, `brandId` (GUID), `Desi` (tam sayı), `Code` (barkod, ≤500, istekte benzersiz), `GroupCode` (varyant grubu), `StockCount`, `StockCode` (≤100), `VatRate`, `ListPrice`, `SalePrice` (2 ondalık), `CategoryId` (yaprak), `attributes[{attributeId,attributeValueId}]`, `images[{imageurl}]`.
- Özellikler yalnız değer kimliğiyle gönderilir; **serbest metin yok**. Marka özellik değil, ayrı `brandId`.
- Teslimat tipleri (`deliveries`) panelden tanımlanır; boş gönderilebilir.
- Tek ürün açmada `batchRequestId` boş GUID döner; sonuç onay sürecinden okunur.
- `GroupCode` uzunluğu dokümanda nvarchar(10), örnekte 12 karakter: Pazarama'ya sorulacak.

## 4. Trendyol akışı → Pazarama karşılığı

| Panel akışı | Trendyol | Pazarama |
|---|---|---|
| Bağlantı testi | Satıcı bilgisi | Token alma + marka listesinden 1 kayıt + son 1 günün sipariş sayfası |
| Sipariş çekme | Değişiklik zamanına göre | **Yalnız sipariş tarihine göre** (≤1 ay pencereler). Durum değişikliği için açık siparişler numarayla ayrıca okunur (7). |
| Paket | Paket = gönderim | Kalem (orderItem) bazlı durum + ayrı paket servisi |
| Sipariş onayı | Picking | Statü 12 (Hazırlanıyor), sipariş veya kalem bazında |
| Kargo bildirimi | Takip no / anlaşmalı | Anlaşmalı kargoda otomatik; anlaşmasızda statü 5 + takip no + takip linki + kargo firması GUID |
| Tedarik edilemedi | Kalem iptali | Statü 13 (alıcıya para iadesi) |
| Alıcı iptal talebi | — | Statü 18 → `order/api/cancel` ile onay/ret |
| Etiket | ZPL servisi | Etiket servisi yok; anlaşmalı kargoda `shipmentCode` (PZ...) ile kargo süreci; kendi etiketimiz basılabilir (doğrulanacak) |
| Fiyat/stok | Tek istekte ikisi | **Ayrı iki servis**, her biri 10 sn aralıklı; sonuç `dataId` ile |
| Ürün açma | Toplu + batch sorgu | Toplu (≤500) + `batchRequestId` |
| Satışa kapat/aç | Arşiv | `external-status` (1/10) |
| İade | Onay/ret | Onay; ret gerekçe + görsel + açıklama; incelemeye gönderme |
| Fatura | Link/dosya | Link (sipariş, paket veya kalem) |
| Sorular | Cevap | Cevap → Pazarama onayı |
| Komisyon | Finans servisi | `paymentAgreement` |

## 5. Mimari

### 5.1 Adaptör (`src/MarketplaceHub.Infrastructure/Adapters/Pazarama/`)

N11 düzeninde, port başına bir `partial` dosya:

- `PazaramaHttpClient.cs`: `IConnectionPort`, `IOrderPort`; tek yazma kapısı `WriteBlocked<T>`; REST gönderim, zarf çözümü (`success`, `messageCode`), hata eşleme.
- `PazaramaTokenHandler.cs` (DelegatingHandler): bağlantı başına token önbelleği (1 saat, 5 dk erken yenileme, 401'de tek yenileme). Token ve sır loglanmaz; kayıtların `ToString`'i gizlenir.
- `PazaramaAuthenticationHandler.cs`: şifreli kimliği çözer (`clientId`, `clientSecret`).
- `PazaramaRateLimitHandler.cs`: paylaşılan PostgreSQL kovaları (N11 deseni), kovalar 5.6'da.
- `PazaramaReferences.cs` (`IReferenceDataPort`): kategori ağacı, kategori özellikleri, markalar, şehirler, kargo firmaları.
- `PazaramaProducts.cs` (`IProductPort`): onaylı (imleç) + onaysız liste, ürün ayrıntı, ürün açma, batch sonucu, satışa kapat/aç (`ArchiveAsync`), durum (`GetPublicationStatusAsync`).
- `PazaramaPriceInventory.cs` (`IInventoryPricePort`): fiyat ve stok ayrı istekler, `dataId` sonuç takibi.
- `PazaramaOrders.cs` + `PazaramaOrderMapper.cs`: sipariş okuma ve eşleme.
- `PazaramaPackageActions.cs`: durum güncelleme (12/13/5), paket bölme/güncelleme, iptal talebi kararı, işçilik bedeli.
- `PazaramaReturns.cs` (`IReturnPort`): iade listesi, onay, ret (gerekçe/görsel), incelemeye gönderme.
- `PazaramaQuestions.cs` (`IQuestionPort`), `PazaramaInvoices.cs` (`IInvoiceMarketplacePort`).
- `PazaramaErrorAdvice.cs`: bilinen hata mesajlarına çözüm önerisi (N11 deseni).
- `PazaramaOptions.cs` (`SectionName = "Pazarama"`): adresler, zaman aşımı, kova sınırları.

### 5.2 Uygulanacak portlar

`IConnectionPort`, `IReferenceDataPort`, `IProductPort`, `IInventoryPricePort`, `IOrderPort`, `IReturnPort`, `IQuestionPort`, `IInvoiceMarketplacePort`. `IProductVisualLookupPort` (sipariş satırı görseli için; sipariş yanıtında `imageURL` geldiği için isteğe bağlı).

Desteklenmeyenler açık hatayla döner: ortak ZPL etiketi, test siparişi, paketlenebilir kalem listesi.

### 5.3 Kayıt ve yönlendirme (yeni platform kontrol listesi — N11'den)

Arka uç:
1. `DependencyInjection.cs`: `Configure<PazaramaOptions>`, `AddHttpClient("Pazarama")` + token ve hız handler'ları, istemci kayıtları.
2. `MarketplacePortRouter.cs`: kurucu + `SelectPort` içinde `"PAZARAMA"`.
3. `MarketplaceContracts.cs`: `ActiveIntegrationScope`, `MarketplaceJobTypes` (5.7), `ProcessorJobTypes`, `ForPlatform("PAZARAMA")`, sipariş çakışma politikaları (`FullOrderSyncConflictPolicy`, `TargetedOrderSyncConflictPolicy`), `CreateConnectionCommand` (`PazaramaClientId`, `PazaramaClientSecret`), `ConnectionView`/`UpdateConnectionCommand` ayarları.
4. `JobPriorityPolicy`, `MarketplaceSyncExecutionLock`, `Worker.OrderReadSyncJobTypes`.
5. `ScheduledJobProducer`: platform filtreleri, `SupportsPazaramaScheduledPolicy`, yazma politikaları varsayılan kapalı.
6. `MarketplaceConnectionService`: oluşturma (yalnız PRODUCTION), güncelleme, kimlik yenileme, ayar kaydı, `PazaramaCapabilityCodes`, `CapabilityCodesFor`, ilk senkron planı (referanslar + sipariş geçmişi + iade başlangıcı), platform filtreli ~12 sorgu.
7. Politikalar: `CapabilityEvidencePolicy` (resmi doküman hostu `isortagim.pazarama.com`), `MarketplaceInvoiceCreationPolicy`, `InvoiceWorkspaceMarketplacePolicy`, `ConnectionDataResetPolicy`, `SynchronizationPolicies` (`PazaramaOrderHistoryPolicy`).
8. `MarketplaceJobProcessor` dalları: bağlantı testi, sipariş, iade, ürün senkronu/eşleme, ürün açma ve onay takibi, fiyat-stok sonucu.
9. `MarketplaceSalesService`: okuma kuyruğu, paket aksiyonu doğrulaması (`ValidatePazaramaShipmentAction`), iade aksiyonları, `ReturnsAsync` bağlantı filtresi, sipariş detay bilgileri (`PazaramaOrderInfo`).
10. `MarketplaceQuestionService`, `CatalogService` (rozet, filtre, yayın), `InventoryService`, `TrendyolWriteComposers` (`BuildPazaramaAsync`), `InvoicingJobProcessor`, `DashboardReadService`, `JobOperationsService`.

Panel:
11. `marketplace-platform-support.ts` (sipariş platformları, etiketler, `pazaramaReadSyncPolicies`), bağlantı formu ve detay (`pages.tsx`), `product-import-platforms.ts`, `product-platform-filter.ts`, `product-publication-channels.ts`, `mapping-platforms.ts` + `supportedAttributeMappingPlatforms`, `operations/pages.tsx`, `questions/QuestionsPage.tsx`, `return-sync.ts`, `connection-data-reset.ts`, `connection-capability-query.ts`, `App.tsx` (giriş ekranı listeleri).
12. Hazır olanlar: logolar (`public/platforms/pazarama.png`, `orders/pazarama.png`), kare işaret, CSS rengi, bildirim adı, eşleştirme sekmesi tanımı.

### 5.4 Pazarama'ya özel politika sınıfları

- `PazaramaOrderHistoryPolicy`: ≤1 aylık pencereler, `endDate` hariç olduğu için bitiş +1 gün; numarayla okumada 6 ay.
- `PazaramaPriceInventoryRules`: 2 ondalık, `listPrice >= salePrice > 0`, istek başına 3000, fiyat ve stok ayrı yük.
- `PazaramaItemStatusPolicy`: kalem durum makinesi ve izinli geçişler (6.2).
- `PazaramaRefundRejectPolicy`: gerekçe 1–12, görsel zorunluluğu (10 ve 11 hariç), ≤3 dosya, ≤5 MB.

### 5.5 Bağlantı ayarları (`SettingsJson`)

- `ExternalWritesEnabled` (varsayılan kapalı), `InvoiceCreationEnabled`.
- `PazaramaCargoModel`: `CONTRACTED` (kullanıcı kararı; `OWN` yalnız ileride gerekirse açılır) — durum akışını ve "Kargoya verildi" aksiyonunu belirler.
- `PazaramaDefaultCargoCompanyId`, `PazaramaDefaultSellerAddressId` (paket işlemleri için; boşsa Pazarama varsayılanı).
- `PazaramaDefaultDesi` (üründe desi yoksa).
- `SellerId` (bağlantı testinde okunur, katalog aramada gerekir).

### 5.6 Hız sınırı kovaları (başlangıç değerleri)

| Kova | Değer | Kaynak |
|---|---|---|
| Fiyat | 1 istek / 12,5 sn | Doküman 10 sn + %25 pay (N11 dersi) |
| Stok | 1 istek / 12,5 sn | Aynı |
| Ürün açma | 1 istek / 12,5 sn | Doküman 10 sn + pay |
| Siparişler | 60 / dk | Belgesiz, temkinli |
| İade, iptal, soru | 30 / dk | Belgesiz, temkinli |
| Referans | 30 / dk | Belgesiz |
| Token | 10 / dk | Önbellek nedeniyle nadiren |

Fiyat ve stok ayrı kovalardır, ama aynı satıcı için "her stok-fiyat isteği arasında 10 sn" yazdığından başta **tek ortak kova** kullanılır; Pazarama'dan teyit gelirse ayrılır.

### 5.7 İş tipleri

Okuma işleri Pazarama'ya özel adlarla (`ForPlatform`), yazma işleri ortak tiplerle:

| İş | Tür | Zamanlama |
|---|---|---|
| `PAZARAMA_CONNECTION_TEST` | Özel | Elle / açılışta |
| `PAZARAMA_REFERENCE_SYNC` | Özel | Günde 1 (`REFERENCE_DATA`) |
| `PAZARAMA_ORDER_SYNC` | Özel | 60 sn (`ORDERS`): son 2 günün sipariş tarihi penceresi + 15 dk örtüşme |
| `PAZARAMA_ORDER_RECOVERY_SYNC` | Özel | 15 dk; geriye 1 aylık pencereler, ilk kurulumda 3 ay |
| `PAZARAMA_ORDER_STATUS_SYNC` | Özel | 3 dk (`ORDER_LIFECYCLE`): açık siparişler numarayla (sipariş tarihi filtresi durum değişikliğini yakalamaz) |
| `PAZARAMA_RETURN_SYNC` | Özel | 3 dk (`RETURNS`) + iptal talepleri |
| `MARKETPLACE_QUESTION_SYNC` | Ortak | 2 dk |
| `PRICE_INVENTORY_SYNC`, `STOCK_PROJECTION_DISPATCH`, stok uzlaştırma | Ortak | Mevcut |
| Ürün açma / onay takibi / arşiv (satışa kapat) | Ortak | Mevcut |
| Paket aksiyonu, iade aksiyonu, fatura iletimi | Ortak | Kullanıcı aksiyonuyla |

## 6. Veri eşlemeleri

### 6.1 Sipariş ve kalem

| Pazarama | Panel |
|---|---|
| `orderNumber` (long) | `ExternalOrderId`, `OrderNumber` |
| `orderId` (GUID) | Sipariş anlık görüntüsü (paket ve fatura servisleri ister) |
| `items[].orderItemId` | `ExternalLineId` |
| `items[].quantity` | Adet (V1); V2'de her adet ayrı kalem |
| `product.code` | Barkod |
| `product.stockCode` | SKU |
| `product.productId`, `variantOptionDisplay`, `imageURL` | Satır anlık görüntüsü |
| `salePrice.value`, `listPrice.value`, `discountAmount`, `taxAmount` | Birim fiyat, indirim, KDV |
| `items[].orderItemStatus` + `orderItemStatusHistory` | Kalem durumu ve geçmişi |
| `estimatedShippingDate` | `ShipmentDueAt` |
| `cargo{companyId, companyName, trackingNumber, trackingUrl}` | Kargo firması, takip no/link |
| `shipmentCode` | Anlaşmalı kargo gönderi kodu (etiket barkodu) |
| `shipmentCost` | Anlaşmalı kargo maliyeti (bilgi) |
| `deliveryType` | Teslimat tipi (1 kargo, 2 kurye, 3 mağaza, 4 dijital, 5 bağış, 10001 teslimat noktası) |
| `shipmentAddress`, `billingAddress` | Adres anlık görüntüleri; `identityNumber`, `invoiceType` (1 bireysel / 2 kurumsal), `taxNumber`, `taxOffice`, `isEInvoiceObliged` |
| `laborCostPerItem`, `laborCostUpdateable` | İşçilik bedeli |
| `packageNumber` | Paket numarası (paket servisiyle zenginleştirilir) |

Paket: Pazarama'da durum kalem bazlı. Panelde **paket = Pazarama paketi**; paket servisi `PACKED (10)` paketlerini verir. Paket oluşturulmamış siparişte tüm kalemler sanal tek pakette toplanır (`order:{orderNumber}`), N11'deki `UnPacked` bağlama desenine benzer şekilde eski paketler (`20/30/40`) işlem dışı sayılır.

### 6.2 Kalem durumu

| Pazarama | Panel | Not |
|---|---|---|
| 3 Siparişiniz Alındı | Yeni | Alıcı bu durumda doğrudan iptal edebilir |
| 12 Hazırlanıyor | Hazırlanıyor | Sonrasında kargoya verilmezse otomatik iade + satıcı puanı düşer |
| 18 İptal Süreci Başlatıldı | İptal talebi | Satıcı onay/ret verir |
| 13 Tedarik Edilemedi | İptal (satıcı) | |
| 6 İptal Edildi | İptal | |
| 5 Kargoya Verildi | Kargoda | Anlaşmalı kargoda otomatik; anlaşmasızda satıcı gönderir |
| 16 Mağazada, 19 Teslimat Noktasında | Kargoda (alt durum) | |
| 11 Teslim Edildi | Teslim edildi | |
| 14 Teslim Edilemedi | Teslim edilemedi | |
| 7 İade Süreci Başlatıldı, 8 İade Onaylandı, 9 İade Reddedildi, 10 İade Edildi | İade durumları | `getRefund` ile eşlenir (6.3) |

İzinli satıcı geçişleri: 3→12, 12→13, 12→5 (yalnız kendi kargo), 12→11/14 (yalnız dijital), 5→11/14 (yalnız kendi kargo).

### 6.3 İade durumu

| `getRefund` | Panel | Sipariş kalemi |
|---|---|---|
| 1 Onay bekliyor | Karar bekliyor | 7 |
| 2 Tedarikçi onayladı | Onaylandı | 8 |
| 3 Tedarikçi reddetti | Reddedildi (Pazarama incelemesinde) | 9 |
| 4 Backoffice onayladı | Onaylandı | 8 |
| 5 Backoffice reddetti | Reddedildi | 9 |
| 6 Otomatik onay | Onaylandı | 8 |
| 7 Talep iptal edildi | Kapandı | |
| 8 Direkt onay | Onaylandı | 8 |

İade satırı siparişe `orderNumber` + `productCode` (barkod) ile bağlanır; eşleşmezse sorun kaydı açılır (`PAZARAMA_RETURN_LINE_UNMATCHED`).

### 6.4 Ürün durumu ve çekme

- Onaylı liste → `APPROVED`; onaysız liste → `PENDING_APPROVAL` veya `REJECTED` (`waitingApproveExp` gerekçe); satışa kapalı (`productStatus=10`) → `ARCHIVED`.
- Eşleme anahtarı barkod (`code`); SKU (`stockCode`) çapraz kontrol. `groupCode` → model kodu (varyant grubu).
- Ürün listesi özellikleri ad/değer metni olarak verir; ayrıntı servisi GUID verir. Değer eşlemesi metin + GUID birlikte.
- Mapping-only modunda N11 deseni: eşleşmeyenler `PRODUCT_MAPPING_UNMATCHED` sorunu, eşleşenlerde Pazarama fiyatıyla teklif tohumlanır.

## 7. Siparişleri eksiksiz okuma (kritik tasarım)

Pazarama'da "değişen siparişler" filtresi yok; sorgu yalnız sipariş tarihine göre. Bu yüzden:

1. **Yeni siparişler**: her 60 sn son 2 günün penceresi (`endDate` = yarın, çünkü bitiş günü hariç), sayfa sayfa.
2. **Durum değişiklikleri**: açık siparişler (Yeni, Hazırlanıyor, Kargoda, İptal talebi) 3 dk'da bir numarayla yeniden okunur; kapanana veya 45 gün geçene kadar.
3. **Geçmiş tarama**: 15 dk'da bir geriye doğru 1 aylık pencereler; ilk kurulumda 3 ay.
4. **Mutabakat**: mevcut kısa/orta/günlük sipariş mutabakatı Pazarama'ya açılır.

## 8. Güvenlik kapıları

1. Bağlantı `ExternalWritesEnabled=false` açılır. Okuma yetenekleri bağlantı testiyle kanıtlanır.
2. Tüm yazmalar adaptördeki tek kapıdan geçer (`WriteBlocked`) ve üst katmanda ilgili politika (PRICE/STOCK/SHIPMENT/RETURN_WRITE) aranır.
3. İlk yazmalar kullanıcıyla, tek kayıtla:
   - Fiyat/stok: tek barkoda Pazarama'dan okunan **aynı** fiyat/stok (değişiklik yaratmayan yazma), sonuç `lake-projections` ile doğrulanır.
   - Sipariş: ilk gerçek siparişte 3→12.
   - Ürün açma / satışa kapat: tek ürün.
4. Geri alınamazlar (tedarik edilemedi, iade reddi, iptal talebi reddi) her seferinde onay penceresi ister.
5. Pazarama'da %70 ve üzeri indirim onaya düşer; panel bu durumu "onaya gönderildi" olarak gösterir, hata saymaz.
6. Sunucuda yalnız salt-okunur kontrol.

## 9. Aşamalar

Her aşama küçük commit'lerle `main`'e gider; her commit'in Validate ve Deploy sonucu izlenir. Her yeni mantık birim testiyle gelir (eşleyiciler, politika sınıfları, kapı).

### Aşama 0 — Kararlar ve hazırlık
- Bölüm 2'deki kararlar. Kullanıcı Pazarama panelinde API bilgilerini üretir (anahtar yalnız bir kez gösterilir).
- Pazarama'ya sorular gönderilir (bölüm 11).

### Aşama 1 — Temel ve bağlantı
1. Kapsam, yönlendirici, DI; tüm portlar "desteklenmiyor".
2. Seçenekler, kimlik çözücü, token handler (önbellek, yenileme), hız handler'ı, zarf/hata eşleyici, yazma kapısı; testleriyle.
3. Bağlantı oluşturma/güncelleme/kimlik yenileme, ayar kaydı, yetenek kodları, bağlantı testi (token + marka + sipariş okuma), `SellerId` okuma.
4. Panel bağlantı formu (Client Id, Client Secret, mağaza adı; ortam "Canlı" sabit) ve detay sayfası.
- Çıkış: bağlantı "Doğrulandı".

### Aşama 2 — Siparişler (okuma)
1. Sipariş eşleyici (para nesneleri, kalem durumları, adresler, kargo), paket servisiyle zenginleştirme; testler.
2. `PAZARAMA_ORDER_SYNC`, `_RECOVERY_SYNC`, `_STATUS_SYNC`, zamanlayıcı politikaları, stok rezervasyonu.
3. Panel: sipariş listesi/detayı (Pazarama logosu, `shipmentCode`, teslimat tipi, tahmini kargolama), filtreler.
- Çıkış: son 3 ayın siparişleri panelde; yeni sipariş 2 dk içinde düşüyor.

### Aşama 3 — Referanslar ve ürün çekme/eşleme
1. Kategori ağacı, kategori özellikleri (istek üzerine, kategori başına), markalar, şehirler, kargo firmaları.
2. Ürün çekme (onaylı imleç + onaysız), tam tarama / yalnız eşleme modları, barkod eşleşmesi, eşleşmeyen raporu, teklif tohumlama.
3. Eşleştirme ekranında Pazarama sekmesi etkin (`supportedAttributeMappingPlatforms`), marka eşleme.
- Çıkış: Pazarama ürünleri panel varyantlarına bağlı.
- Uygulama notu (15 Ekim 2026): `PazaramaReferences.cs` (kategori ağacı düz liste -> kök/yol/derinlik, kategori özellikleri + değerleri tek `getCategoryWithAttributes` çağrısından, 2 dk önbellekli; markalar sayfa+ilk-id imleciyle), `PazaramaProducts.cs` (imleç: `A:` onaylı, `U:{sayfa}:{ilkKod}` onaysız sayfa-indeksli, `C:` onaysız imleçli; sayfa indeksi 0/1 tabanı `hasPreviousPage` ile otomatik ayrılır). Ürün = barkod (`code`); `groupCode` yalnız gruplama (`group:{kod}`), model kodu değildir. İçe aktarma N11 ile aynı "düz katalog" dalı: onay bekleyenler dahil, yalnız gözlem, barkod kimliği, mapping-only modunda barkod->stok kodu çapraz eşleşme ve teklif tohumlama (Pazarama fiyatından başlar). Marka referansı ürün içe aktarmada okunmaz (Aşama 9). Fiyat-stok değişikliği zaten kapalı (`updateExistingPricesAndStock` Pazarama için hep false).

### Aşama 4 — Fiyat ve stok
- Uygulama notu (15 Ekim 2026): `PazaramaPriceInventory.cs`. `PriceInventoryComposer` Pazarama dalı barkoda bağlı ACTIVE teklifleri `{price:[{code,listPrice,salePrice}],stock:[{code,stockCount}]}` olarak hazırlar (3000 sınırı, listPrice>=salePrice, 2 ondalık). Adaptör fiyatı sonra stoku ayrı iki istekle gönderir (aynı 10 sn kovası; ikinci istek için 90 sn zaman aşımı) ve tek işlem numarası olarak `p:{dataId}:{n}|s:{dataId}:{n}` döner. Sonuç `lake-projections` sayfalarından okunur; işlem, beklenen tüm barkodlar gelene ve hiçbiri İşleniyor olmayana kadar IN_PROGRESS kalır; barkod ancak fiyat ve stok ikisi de başarılıysa başarılı sayılır (durum 5 Onaya gönderildi başarılıdır). Panelde Pazarama bağlantı sayfasında dış yazma anahtarı + otomatik fiyat/stok grubu + tek barkod gönderim paneli var. Yazma yolları: global bayrak + bağlantı anahtarı + PRICE_WRITE ve STOCK_WRITE politikaları; hepsi varsayılan kapalı.
1. Bileşen (`BuildPazaramaAsync`), fiyat ve stok ayrı yükler, 3000 parça, `dataId` sonuç takibi (5 = onaya gönderildi).
2. Bağlantı sayfasında "Pazarama'ya fiyat-stok gönder" paneli (tek barkod / değişenler), otomatik/manuel dış yazma grupları.
- Çıkış: kullanıcı onayıyla ilk değişmeyen yazma, sonra tek değişiklik.

### Aşama 5 — Sipariş aksiyonları
1. İşleme al (3→12, sipariş veya kalem), tedarik edilemedi (13), kendi kargoda kargoya verildi (5 + takip + firma).
2. Paket bölme ve paket güncelleme (kargo firması, depo adresi).
3. İptal taleplerini listeleme ve onay/ret.
4. İşçilik bedeli (yalnız uygun kategoride).
5. Panel aksiyonları ve onay pencereleri.
- Çıkış: ilk gerçek siparişte işleme alma kanıtlandı.

### Aşama 6 — Fatura linki
- `order/invoice-link` (paket bazında: kargo firması + takip no) ve gerekirse kalem bazlı. Link uzantı ve erişilebilirlik doğrulaması.

### Aşama 7 — İadeler
1. İade listesi ve durum eşlemesi, sipariş satırı bağlama.
2. Onay, ret (gerekçe + görsel yükleme + açıklama), incelemeye gönderme; revizyon taleplerinin gösterimi.
- Panelde ret penceresi: gerekçe listesi, görsel zorunluluğu, dosya sınırları.

### Aşama 8 — Müşteri soruları
- Liste, ayrıntı, cevap. Cevap Pazarama onayına düştüğü için "Onay bekliyor" durumu panelde ayrı gösterilir.

### Aşama 9 — Ürün açma ve yönetim
1. Ürün açma bileşeni (yaprak kategori, zorunlu özellik değer kimlikleri, `brandId`, `Desi`, `GroupCode`, görseller), `batchRequestId` takibi, onay takibi.
2. Hızlı ekleme: katalog araması + `addProductsWithBarcodes`.
3. Satışa kapat/aç (tekli ve toplu), KDV güncelleme, sipariş başına adet sınırı.
- Profil olmayan (eşleme ile gelen) ürünler için satışa kapat/aç, bağlantı sayfasında barkodla yapılır (N11'de karar bekleyen soruya Pazarama'da baştan çözüm).

### Aşama 10 — Finans, yasal bilgiler, sağlamlaştırma
1. `paymentAgreement` ile sipariş detayında komisyon, hakediş, aktarım tarihi.
2. Temin bilgileri (ithalatçı/imalatçı şablonları, uyarı görselleri, parti/seri, belgeler): ihtiyaç kararıyla.
3. Hata öneri eşleyicisi, hız kovalarının gerçek değerlerle ayarı, izleme.

## 10. Test planı

- Birim: token önbelleği ve yenileme, zarf/hata eşleme, sipariş eşleyici (para nesneleri, durum geçmişi, adresler), paket birleştirme, iade eşleyici, fiyat/stok yükleri ve parçalama, ürün açma gövdesi, yazma kapısı, durum geçiş politikası, iade reddi politikası.
- Mevcut testler: router, bağlantı testi dağıtımı, veri sıfırlama kapsamı, platform listeleri (web).
- Canlı doğrulama: her aşamada salt-okunur sunucu kontrolü (iş durumu, loglar).

## 11. Pazarama'ya sorulacaklar

1. Sipariş, iade, soru ve referans servislerinin hız sınırları nedir?
2. Fiyat ve stok servisleri aynı 10 sn kovasını mı paylaşıyor?
3. `GroupCode` uzunluk sınırı (dokümanda 10, örnekte 12 karakter).
4. Değişen siparişleri (durumu güncellenen) tarih filtresiyle almanın yolu var mı?
5. Anlaşmalı kargoda etiket/barkod servisi var mı? `shipmentCode` ile kendi bastığımız etiket kabul ediliyor mu?
6. Test (sandbox) ortamı var mı?
7. Webhook/bildirim desteği var mı?
8. Tarihlerin saat dilimi (Türkiye saati varsayılıyor).

## 12. Riskler

- **Değişiklik filtresi yok**: durum değişiklikleri açık siparişlerin numarayla tekrar okunmasına dayanıyor; açık sipariş sayısı büyürse istek yükü artar.
- **Hazırlanıyor (12) sonrası süre**: kargoya verilmezse otomatik iade ve puan kaybı. İşleme alma yalnız kullanıcı aksiyonuyla.
- **Özellikte serbest metin yok**: ürün açmada her zorunlu özelliğin değer kimliği eşlenmiş olmalı.
- **Fiyat onayı**: %70 indirim onaya düşer, sonuç 4 saate kadar gecikebilir; panelde "beklemede" görünür.
- **Anahtar yenileme**: Pazarama panelinde yeni anahtar üretilirse eski anında ölür; tüm işler kimlik hatasına düşer. Panel kimlik hatasında açık uyarı vermeli.
- **Doküman tutarsızlıkları**: alan adı büyük/küçük harf farkları (`Code`/`code`, `orderid`/`orderId`), `GroupCode` uzunluğu. Canlı yanıtla doğrulanacak.
