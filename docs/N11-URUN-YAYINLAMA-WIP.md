# N11 ürün yükleme — yarım iş notu (dal: `wip/n11-urun-yayinlama`)

Bu dal `main`'e alınmadı. İçindeki kod hiç derlenmedi; bir alt ajan yazdı ve oturum kapanırken (15 Ekim 2026) yarıda durduruldu. Genel çalışma kuralları ve kurulum: `docs/DEVAM-NOTU.md`.

Yeni oturumda: "docs/N11-URUN-YAYINLAMA-WIP.md dosyasını oku, wip/n11-urun-yayinlama dalında kaldığın yerden devam et" yazın.

## Nasıl doğrulanır

- CI bu dala push'ta kendiliğinden çalışmaz (yalnız `main` ve `codex/**`). Derleme ve testleri çalıştırmak için: `gh workflow run ci.yml -R emrekks/RavenciaEntegrasyon --ref wip/n11-urun-yayinlama`, sonuç: `gh run list -R emrekks/RavenciaEntegrasyon --branch wip/n11-urun-yayinlama`. Elle tetiklenen Validate deploy etmez (Deploy yalnız `main` push'undan sonra çalışır).
- Validate geçince ve aşağıdaki eksikler tamamlanınca dalı `main`'e birleştir; deploy otomatik olur.

## Dalda yapılmış olanlar

| Dosya | Durum |
|---|---|
| `Infrastructure/Persistence/N11ProductPublicationComposer.cs` (yeni, ~360 satır) | N11 `product-create` gövdesini kuruyor: kategori/özellik/değer eşlemeleri, marka = özellik id 1 (customValue), KDV yalnız 0/1/10/20, `productMainId` = model kodu, kargo şablonu ve hazırlık günü bağlantı ayarından, özellik varsayılanları. `Payload`, `VatRate`, `PublishSettings`, `SelectValueId` statik/internal (test edilebilir). |
| `Infrastructure/Persistence/ProductPublicationComposer.cs` | N11 bağlantısında `N11ProductPublicationComposer`'a yönlendiriyor. |
| `Infrastructure/Adapters/N11/N11Products.cs` | `CreateAsync` (`POST ms/product/tasks/product-create`, integrator adı eklenir, 1–1000 SKU, dış yazma kapalıysa gönderilmez) ve `GetPublicationStatusAsync` (`ms/product-query?stockCode=`; ACTIVE→APPROVED, InApproval/InCatalogApproval→PENDING_APPROVAL, CatalogRejected/Prohibited→REJECTED, Suspended→ARCHIVED). Güncelleme ve arşivleme hâlâ desteklenmiyor. |
| `Infrastructure/Adapters/N11/N11PriceInventory.cs` | `N11TaskMapper.Submitted` ürün açma görevi için de kullanılabilir (işlem adı ve varsayılan tip parametreli). |
| `Infrastructure/Persistence/MarketplaceJobProcessor.cs` | Yayın sonucu: N11 görev satırları stok koduyla eşleşiyor, tüm görev reddi `CREATE_REJECTED`. Onay takibi: N11'de anahtar stok kodu; yeni ürün sorguda henüz görünmüyorsa yayın sıfırlanmıyor, beklemede kalıyor; onaylanan stok kodu varyantın N11 bağlantısı oluyor. |
| `Infrastructure/Persistence/CatalogService.cs` | N11 yayın kanalı olarak kabul ediliyor; global ve bağlantı dış yazma anahtarı kapalıysa reddediliyor. Boş barkod `null` kaydediliyor. |
| `Infrastructure/Persistence/MarketplaceConnectionService.cs` + `Application/MarketplaceContracts.cs` | N11 bağlantı ayarına `N11ShipmentTemplate` ve `N11PreparingDay` (varsayılan 3, 1–30) eklendi; `UpdateConnectionCommand` ve `ConnectionView` bunları taşıyor. |
| `Infrastructure/Persistence/TrendyolWriteComposers.cs` | `TwoDecimals` internal yapıldı (composer kullanıyor). |
| `Web/src/features/catalog/product-publication-channels.ts` (+ test) | N11 yayın kartı: dış yazma açıksa PUBLISH, kapalıysa READ_ONLY. |
| `Web/src/features/catalog/pages.tsx` | N11 kartında marka özelliği (id 1) eşleme eksiği sayılmıyor; READ_ONLY kartta N11'e özel açıklama. |

## Eksik / yapılacaklar

1. **Panelde kargo şablonu ve hazırlık günü girişi yok.** `Web/src/features/marketplace/pages.tsx` N11 bağlantı detayına iki alan ekle (fatura oluşturma anahtarının yakınına) ve `PATCH /connections/{id}` ile `n11ShipmentTemplate` / `n11PreparingDay` gönder; `Connection` tipine bu alanları ekle. Şablon boşken composer `N11_SHIPMENT_TEMPLATE_REQUIRED` ile durur.
2. **Testler yok.** `tests/MarketplaceHub.Application.Tests/` altına: `N11ProductPublicationComposer.Payload` JSON şekli, `VatRate`, `SelectValueId`, `PublishSettings`, `N11HttpClient.CreatePayload` (integrator ekleme, SKU sınırı), `N11ProductMapper.PublicationStatus` durum eşlemesi, `N11TaskMapper.Submitted` (PRODUCT_CREATE, REJECT).
3. **Derleme hiç denenmedi.** Önce CI'ı elle çalıştır; özellikle composer'daki tür/imza kullanımlarını (ServiceResult, ProductPublicationDraft, ProductAttributeAssignment, ReferenceItem alanları) kontrol et.
4. **Uçtan uca kontrol:** yayın işi (`PRODUCT_CREATE` tipi, öncelik 2) → `CreateAsync` → görev sorgusu (`GetOperationAsync`) → onay takibi (`PRODUCT_APPROVAL_RECONCILE`) → `MarketplaceVariantLinks.ExternalId` = stok kodu (fiyat/stok gönderimi bu anahtarı kullanır, bkz. `TrendyolWriteComposers.BuildN11Async`).
5. **Yapılmadı (sonraki adım):** ürün güncelleme (`POST ms/product/tasks/product-update`; yalnız status, preparingDay, shipmentTemplate, currencyType, productMainId, maxPurchaseQuantity, description değişir, vatRate zorunlu), satıştan kaldırma/açma (status Suspended/Active), silme (`product-delete`), hızlı yükleme (SOAP `SearchCatalog`).
6. **İlk canlı deneme:** N11'de test ortamı yok. İlk ürünü kullanıcıyla birlikte, onaylı ve izleyerek aç; "Satıcı Onayı Bekliyor" durumunda N11 panelinden onay gerekir.

## N11 sözleşmesi (developer.n11.com "Ürün Yükleme")

`POST https://api.n11.com/ms/product/tasks/product-create`, başlıklar `appkey`/`appsecret`, gövde:

```json
{"payload":{"integrator":"Ravencia","skus":[{"title":"...","description":"<HTML>","categoryId":0,"currencyType":"TL","productMainId":"...","preparingDay":3,"shipmentTemplate":"...","maxPurchaseQuantity":null,"stockCode":"...","catalogId":null,"barcode":"...","quantity":0,"images":[{"url":"https://...","order":0}],"attributes":[{"id":1,"valueId":null,"customValue":"Marka"}],"salePrice":0,"listPrice":0,"vatRate":20}]}}
```

- En fazla 1000 SKU; görseller https; stok kodu ≤255; adet ≤999.999.
- Zorunlu özellikler gönderilmeli; özel değere izin yoksa `valueId`, varsa `customValue`.
- Aynı `productMainId` = aynı ürünün varyantları.
- Barkod N11 kataloğuyla eşleşirse ürün "Satıcı Onayı Bekliyor" durumuna düşer.
- Yanıt `{id, type: PRODUCT_CREATE, status: IN_QUEUE|REJECT, reasons}`; sonuç `POST ms/product/task-details/page-query` ile.
