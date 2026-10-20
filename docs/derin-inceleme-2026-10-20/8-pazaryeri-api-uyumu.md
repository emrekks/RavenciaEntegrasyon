# 8 — Pazaryeri API sözleşme uyumu (C)

> origin/main 289d0b54. Ana rapor: `../PROJE-DERIN-INCELEME-RAPORU-2026-10-20.md`. Hiçbir pazaryeri API'si çağrılmadı.
> Okunamayan: developers.hepsiburada.com (403/401) — HB için açık kaynaklı `zanaat-dev/lonca` deposundaki portal tanımları (2026-05) kullanıldı; HB talep/listing/webhook için resmi şema yok. Trendyol `updateTrackingNumber` sayfası açılmadı.
> Önceki kararlar geçerli: A2 (Shopify `@idempotent` + `changeFromQuantity`) 2026-10 dokümanıyla uyumlu.

| # | Ö | Platform / uç | Yer | Uyumsuzluk → arıza | Çözüm |
|---|---|---|---|---|---|
| C1 | Y | Shopify `returns` / `return(id)` | `ShopifyHttpClient.cs:618,655` | `returnLineItems.nodes` = `ReturnLineItemType` arayüzü; `fulfillmentLineItem` yalnız `ReturnLineItem` nesnesinde → sorgu reddediliyor, Shopify iadesi hiç gelmiyor. Doc: shopify.dev/docs/api/admin-graphql/2026-10/interfaces/ReturnLineItemType | `... on ReturnLineItem { fulfillmentLineItem { id lineItem { id } } }` |
| C2 | Y | Trendyol paket yazmaları ve fatura linki gönder/sil | `TrendyolHttpClient.cs:630,763,821,1046` | `storeFrontCode` başlığı varsayılan `TR`; v3 doküman paketin kendi vitrinini istiyor → mikro ihracat/DE/AE paketlerinde 400/404 veya yanlış mağaza. Doc: developers.trendyol.com/v3.0/reference/updatepackagestatus.md | `PackageActionCommand`/`InvoiceDeliveryCommand`'a saklı paketin `StoreFrontCode`'u |
| C3 | Y | Trendyol createClaimIssue | `TrendyolHttpClient.cs:728` | Kanıt multipart alanı `file`; doküman (2026-10-14) `files` → kanıt gereken red başarısız/kanıtsız. Doc: developers.trendyol.com/v2.0/docs/create-a-rejection-request-on-returned-orders-createclaimissue.md | `files` |
| C4 | Y (d) | HB inventory-upload durumu | `HepsiburadaJsonMapper.cs:343-349` | `Ready/InProgress/Done` → `READY/INPROGRESS/DONE` eşlenmemiş → her yoklama `CONTRACT_INVALID`. Kaynak: lonca `sdks/hepsiburada/src/types/listing.ts:192-203` | Eşle; `errors[]` oku |
| C5 | Y | HB fatura linki (PUT `.../invoice`) | `HepsiburadaInvoiceDeliveryPolicy.cs:68-80`, `HepsiburadaHttpClient.cs:995-1009` | `contentType` `application/pdf`/`text/html`; şema `pdf`/`html`; `serialNumber`/`rowNumber` gönderilmiyor | `pdf`/`html`; seri/sıra ayır; SIT teyidi |
| C6 | Y | Shopify iade durumu | `ReturnModels.cs:17-34`, `ShopifyHttpClient.cs:1208` | OPEN/CLOSED/DECLINED `ActionRequired`'a düşüyor | OPEN→Approved, CLOSED→Completed, DECLINED→Rejected |
| C7 | O (olası) | HB paket listesi | `HepsiburadaHttpClient.cs:754-763` | Şemada `Offset` büyük harf; kod `offset` → her sayfa ilk 10 olabilir | SIT'te ilk sayfa ötesi çağrı |
| C8 | O | N11 SaveLinkSellerInvoice | `N11Invoices.cs:33-39` | Doğrulama `invoiceNumber` yankısını bekliyor (yalnız e-ihracatta) → yurt içi hep UNKNOWN | `success` + eşleşen `url`/`orderNumber` |
| C9 | O | N11 ClaimReturnList | `N11Returns.cs:30-33` | `sender` yok → SELLER; n11depom iadeleri gelmiyor | `<sender>ALL</sender>` |
| C10 | O | Trendyol fiyat/stok, ürün oluşturma | `TrendyolWriteComposers.cs:178`, `ProductPublicationComposer.cs:280` | Stok tavanı 20.000; kod int max | 20.000'e kırp, hash'e dahil |
| C11 | O | Trendyol iade kanıtı boyutu | `MarketplaceSalesService.cs:2182`, `MarketplaceJobProcessor.cs:9923` | Yerel 10 MiB; doküman 5 MB | 5 MB |
| C12 | O | Trendyol User-Agent | `MarketplaceConnectionService.cs:118,276`; `TrendyolAuthenticationHandler.cs:144` | `{sellerId} - {Ad≤30 alfanümerik}` doğrulanmıyor; ASCII olmayan ad `ParseAdd` istisnası | Kayıtta doğrula; `TryAddWithoutValidation` |
| C13 | O (d) | Trendyol orderDate/stream; N11 tarih ve invoiceDateTime | `TrendyolJsonMapper.cs:79,1032`; `TrendyolHttpClient.cs:233-234`; `N11HttpClient.cs:49-50`; `N11Orders.cs:298-299`; `N11OrderMapper.cs:480-483` | Doküman "epoch ms, GMT+3"; kod UTC sayıyor (plan 0.8) | Canlı değerle karşılaştır; platform başına tek dönüştürücü |
| C14 | O | Trendyol kalem iptali | `MarketplaceSalesService.cs:2401` | Serbest JSON; `reasonId` (500–506) ve satır biçimi doğrulanmıyor | Sunucuda gövde oluştur/doğrula |
| C15 | O | Trendyol `/products/approved?contentId=` | `TrendyolHttpClient.cs:111` | `contentId` belgelenmemiş filtre | Yanıtı yerelde süz |
| C16 | O | E-Faturam giden e-Fatura durumu | `TrendyolEFaturamOptions.cs:14`, `TrendyolEFaturamHttpClient.cs:113-131` | Uç belgelenmiş: GET `/api/invoice/documents/outgoing-einvoice/status/{invoiceUuid}`; kod "yayınlanmamış" sayıp yapılandırılabilir POST bekliyor → TEMELFATURA durumu okunamıyor. Doc: developers.trendyolefaturam.com/OpenApi/Giden eFatura/get-outgoing-e-invoice-status | Belgelenmiş GET'i ekle |
| C17 | O | HB ortak etiket | `HepsiburadaPackageOperations.cs:196,231-235` | Accept'te `application/json` yok; "ortak barkod yok" 400'ü genel hata | Accept + anlaşılır mesaj |
| C18 | O | HB Satıcıya Sor host'u | `HepsiburadaRateLimitHandler.cs:41-47` | Hız sınırı kovası yok | Kova ekle |
| C19 | O (d) | HB yükleme partisi | `HepsiburadaHttpClient.cs:1124` | 4.000; bilinen istemciler 1.000 | 1.000 |
| C20 | O | HB User-Agent | `HepsiburadaHttpClient.cs:1319-1327` | `TryParse` boşluk/Türkçe harfli adı reddediyor → tüm istekler yerelde engellenir | ASCII kontrolü + `TryAddWithoutValidation` |
| C21 | O (d) | HB mpop ticket-api | `HepsiburadaHttpClient.cs:1100-1101,1324` | Şema Bearer; kod Basic | 401'e ayrı kod; SIT teyidi |
| C22 | O (d) | E-Faturam `recipientInfo` | `TrendyolEFaturamCanonicalPayload.cs:129-132`, `TrendyolEFaturamInvoicePayload.cs:75` | Posta kodu 5, telefon deseni, küçük harf e-posta, adres ≤255, ad ≤127, il ≥2, ilçe ≥1 (boş gidiyor) | Normalleştir/kırp/geçersizse gönderme |
| C23 | O | N11 ürün sorgu ve n11depom fiyat/stok | `N11Products.cs:21-27`, `TrendyolWriteComposers.cs:300-315` | `sender` SELLER → depo SKU'ları görünmez; yalnız fiyat değişebilen SKU'ya `quantity` | `sender=ALL`; `sender=N11`'de stok gönderme |
| C24 | O | Trendyol fatura linki gövdesi | `InvoicingJobProcessor.cs:547-551,1028-1032` | `shipmentPackageId` string (doc int64); fazladan `micro`; numara deseni `^[A-Za-z0-9]{3}\d{13}$` kontrolsüz | Sayı gönder, `micro` çıkar, desen kontrolü |
| C25 | O | Shopify `returnDeclineRequest` | `ShopifyHttpClient.cs:680` | `notifyCustomer` varsayılan false → müşteriye not gitmiyor | `notifyCustomer: true` |
| C26 | D | Shopify kullanımdan kalkanlar | `ShopifyHttpClient.cs:34,239,618,655`; `ShopifyProductPublicationComposer.cs:133` | `Customer.email/phone`, `Product.images`, `priceRange`, `returnReason`, varyant `barcode` | Yeni alanlara geç |
| C27 | D | Trendyol soru filtresi | `MarketplaceQuestionService.cs:13` | `UNANSWERED` belgelenmemiş | Çıkar |
| C28 | D | Trendyol vitrin listesi | `TrendyolOptions.cs:77` | QA/OM/BH/AZ belgelenmemiş, kota harcıyor | Belgelenmiş listeye indir |
| C29 | D | Trendyol ürün özellikleri | `ProductPublicationComposer.cs:211-327` | Rehber/şema çelişkisi | Stage'de bir parti |
| C30 | D | Trendyol elle teslim yolu | `TrendyolOptions.cs:52` | `/manual-invoice-delivery` vs `/manual-deliver` | Stage'de teyit |
| C31 | D (d) | HB talep durumu | `HepsiburadaHttpClient.cs:1207` | Yolda küçük harf; istemciler PascalCase | Olduğu gibi gönder |
| C32 | D | HB soru listesi toplamı | `HepsiburadaQuestions.cs:84` | `totalItemCount` aranmıyor | Ekle |
| C33 | D | HB soru durumu | `HepsiburadaQuestions.cs:115` | Durum 7 eşlenmemiş; `stockCode` okunmuyor | Eşle/oku |
| C34 | D | N11 ret nedenleri | `N11Returns.cs:48` | Düz liste biçimi parse edilmeyebilir | İki biçimi kabul et |
| C35 | D (d) | N11 ClaimReturnApprove | `N11Returns.cs:67` | Örnek `claimCancelId`, `splitQuantity` yok | İlk canlı reddi logla |
| C36 | D | N11 bölme yanıtı | `N11PackageActions.cs:43-46,188-203` | `splitPackageOrderItems` kullanılmıyor | Bağları yanıttan kaydet |
| C37 | D | N11 SaveProductAnswer | `N11Questions.cs:214` | 2.048 karakter sınırı kontrolsüz | Önce kontrol |
| C38 | D | N11 küçük alanlar | `N11Invoices.cs:17`; `N11ProductPublicationComposer.cs:226`; `N11Orders.cs:299` | `.jpg`; `images[].order` sayı; `orderByField` yazımı | Hizala/teyit |
| C39 | D | Pazarama dataId | `PazaramaPriceInventory.cs:237-240` | Durum 5 "Onaya gönderildi" başarı sayılıyor | "Onay bekliyor" durumu |
| C40 | D | E-Faturam `source` | `TrendyolEFaturamHttpClient.cs:75` | Doküman pazaryeri entegratörü için `PARTNER`; kod `PORTAL` (bilinçli) | entegrasyon@trendyolefaturam.com ile teyit |
| C41 | D | E-Faturam `CreateEArchiveV2` (kullanılmıyor) | `TrendyolEFaturamInvoicePayload.cs:96-203` | Belgelenmemiş biçim | Sil |
| C42 | D | HB etiket istemcisi | `HepsiburadaPackageOperations.cs:227` | `CreateClient` | `CreateBoundedClient` |
| C43 | D (d) | Shopify `updated_at` araması | `ShopifyHttpClient.cs:1483-1484` | Zaman damgaları tırnaksız | Tırnakla |

## Uyumlu bulunanlar
Trendyol: apigw/stageapigw, `/sapigw/` yok; sipariş v2 (≤200, ≤50 id, 14 gün) ve stream; iade liste/onay/nedenler; Picking/Invoiced, kargo değişimi, bölme; webhook; fatura linki silme; sorular; ürün filtre ve parti sonucu; 10 sn'de 50 istek.
HB: sipariş liste/detay; paket oluştur/aç/kargo değiştir; kargoda/teslim/iptal ve faturası eksik listeleri; katalog; soru liste/cevap; Basic auth.
N11: host ve başlıklar; `shipmentPackages` (≤100, 15 gün), sipariş kalemi güncelleme, fiyat-stok (1.000 SKU), ürün oluşturma, task-details, product-query, kategoriler; kullanımdan kalkan SOAP yok.
Pazarama: token, siparişler (bitiş hariç, ≤1 ay), `updateOrderStatus`, `updateRefund`, fatura linki, fiyat/stok v2 gövdeleri, 10 sn aralık (kodda 12,5), 3.000 sınırı, iade durumları 1–8.
Shopify: sipariş alanları, `displayFulfillmentStatus`, fulfillment, `fulfillmentTrackingInfoUpdate`, `productSet`, `productVariantsBulkUpdate`, webhook girişi ve HMAC, `inventorySetQuantities`.
E-Faturam: oturum (`x-access-token`), e-Arşiv oluşturma (zorunlu alanlar, kuruş, vergi/tip enumları), e-Arşiv durumu (205 nihai), iptal, kalıcı belge URL'si, stage/prod host'ları.

Kaynaklar: shopify.dev Admin GraphQL 2026-10; developers.trendyol.com; developers.trendyolefaturam.com; isortagim.pazarama.com/auth/integration; developer.n11.com; github.com/zanaat-dev/lonca (HB şema kopyası).
