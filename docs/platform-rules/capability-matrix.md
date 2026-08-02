# Platform Capability Matrisi

## Kanıt sözleşmesi

Support level yalnız `SUPPORTED`, `NOT_SUPPORTED`, `UNKNOWN`, `TEMPORARILY_UNAVAILABLE` olabilir. Başlangıç değeri `UNKNOWN`dur. `SUPPORTED` için birlikte şunlar zorunludur: güncel resmî kaynak, test hesabı veya secret/PII içermeyen anonim fixture, doğrulama tarihi, kaynak sürümü, gerekli scope, tenant+connection+environment+API version+store scope ve evidence note. Read desteği write desteği değildir.

F0 başlangıcında hiçbir platform için test hesabı/anonim davranış kanıtı sağlanmamıştı. F3'te Trendyol, F4'te E-Faturam ve F5'te Shopify yerel adapter/contract kanıtları eklendi; bunlar gerçek connection/store capability kanıtı değildir. Bu nedenle gerçek connection capability'leri varsayılan `UNKNOWN`, tüm dış yazmalar kapalıdır.

## Capability kodları

| Grup | Kodlar |
| --- | --- |
| Bağlantı | `ConnectionTest`, `CredentialRefresh`, `CapabilityDiscovery` |
| Katalog referansı | `CategoryRead`, `AttributeRead`, `AttributeValueRead`, `BrandRead`, `CargoProviderRead` |
| Ürün | `ProductRead`, `ProductCreate`, `ProductUpdate`, `ProductArchive`, `ProductDelete`, `BatchResultRead` |
| Stok/fiyat | `InventoryRead`, `InventoryWrite`, `PriceRead`, `PriceWrite` |
| Sipariş/teslimat | `OrderRead`, `OrderSingleFetch`, `OrderWebhook`, `PackageRead`, `ShipmentAction`, `LabelRead` |
| İade | `ReturnRead`, `ReturnApprove`, `ReturnReject`, `ReturnDispute`, `ReturnEvidence` |
| Fatura | `TaxpayerQuery`, `InvoiceSubmit`, `InvoiceStatusRead`, `InvoiceDocumentRead`, `InvoiceCancel`, `InvoiceDeliver` |

## Platform x capability grup durumu

Her satırın scope alanı `tenant + connection + environment + API version + store/merchant` olarak zorunludur; somut değerler test bağlantısı sağlanana kadar `UNASSIGNED`dır.

| Platform | Grup | Support level | Resmî kaynak durumu | Test/fixture kanıtı | Write switch |
| --- | --- | --- | --- | --- | --- |
| Trendyol | Bağlantı | UNKNOWN | Auth/environment dokümanı doğrulandı | Stage identity yok | off |
| Trendyol | Katalog referansı | UNKNOWN | V2 attribute/value/brand/category yolları doğrulandı | Anonim parser fixture; Stage read yok | off |
| Trendyol | Ürün | UNKNOWN | Product V2 create/read/batch yolları doğrulandı | Anonim product/partial fixture; Stage write yok | off |
| Trendyol | Stok/fiyat | UNKNOWN | Birleşik endpoint ve partial batch doğrulandı | Partial fixture; Stage safe-write yok | off |
| Trendyol | Sipariş/teslimat | UNKNOWN | Cursor stream ve webhook auth doğrulandı | Anonim order fixture; Stage/public HTTPS yok | off |
| Trendyol | İade | UNKNOWN | Claims read dokümanı doğrulandı | Anonim claim fixture; Stage action yok | off |
| Trendyol | Fatura | UNKNOWN | Invoice link/file resmî yolları doğrulandı | Stage package/delivery yok | off |
| E-Faturam | Bağlantı | UNKNOWN | API `1.0.0`, Stage/production ve sign-in sözleşmesi doğrulandı | Test credential yok | off |
| E-Faturam | Katalog referansı | UNKNOWN | Uygulanabilirlik kanıtlanmadı | Yok | off |
| E-Faturam | Ürün | UNKNOWN | Uygulanabilirlik kanıtlanmadı | Yok | off |
| E-Faturam | Stok/fiyat | UNKNOWN | Uygulanabilirlik kanıtlanmadı | Yok | off |
| E-Faturam | Sipariş/teslimat | UNKNOWN | Uygulanabilirlik kanıtlanmadı | Yok | off |
| E-Faturam | İade | UNKNOWN | Uygulanabilirlik kanıtlanmadı | Yok | off |
| E-Faturam | Fatura | UNKNOWN | Taxpayer, submit, status, document ve cancel kaynakları doğrulandı | Anonim taxpayer parser fixture; test firma yok | off |
| Shopify | Bağlantı | UNKNOWN | 2026-07 pin ve yerel response-version guard hazır; development-store testi yok | Yok | off |
| Shopify | Katalog referansı | UNKNOWN | Kapsam kanıtlanmadı | Yok | off |
| Shopify | Ürün | UNKNOWN | Bulk/product delete dokümanı erişilebilir | Yok | off |
| Shopify | Stok/fiyat | UNKNOWN | Kapsam kanıtlanmadı | Yok | off |
| Shopify | Sipariş/teslimat | UNKNOWN | Webhook dokümanı erişilebilir | Yok | off |
| Shopify | İade | UNKNOWN | Kapsam kanıtlanmadı | Yok | off |
| Shopify | Fatura | UNKNOWN | Uygulanabilirlik kanıtlanmadı | Yok | off |
| Hepsiburada | Bağlantı | UNKNOWN | Portal yönlendirme/login nedeniyle kısmi | Yok | off |
| Hepsiburada | Katalog referansı | UNKNOWN | Portal yönlendirme/login nedeniyle kısmi | Yok | off |
| Hepsiburada | Ürün | UNKNOWN | Portal yönlendirme/login nedeniyle kısmi | Yok | off |
| Hepsiburada | Stok/fiyat | UNKNOWN | Portal yönlendirme/login nedeniyle kısmi | Yok | off |
| Hepsiburada | Sipariş/teslimat | UNKNOWN | Portal yönlendirme/login nedeniyle kısmi | Yok | off |
| Hepsiburada | İade | UNKNOWN | Portal yönlendirme/login nedeniyle kısmi | Yok | off |
| Hepsiburada | Fatura | UNKNOWN | Portal yönlendirme/login nedeniyle kısmi | Yok | off |
| N11 | Bağlantı | UNKNOWN | Destek sayfası erişilebilir | Yok | off |
| N11 | Katalog referansı | UNKNOWN | Sözleşme kanıtlanmadı | Yok | off |
| N11 | Ürün | UNKNOWN | Sözleşme kanıtlanmadı | Yok | off |
| N11 | Stok/fiyat | UNKNOWN | Sözleşme kanıtlanmadı | Yok | off |
| N11 | Sipariş/teslimat | UNKNOWN | Sözleşme kanıtlanmadı | Yok | off |
| N11 | İade | UNKNOWN | Sözleşme kanıtlanmadı | Yok | off |
| N11 | Fatura | UNKNOWN | Sözleşme kanıtlanmadı | Yok | off |
| Pazarama | Bağlantı | UNKNOWN | Portal login gerekli | Yok | off |
| Pazarama | Katalog referansı | UNKNOWN | Portal login gerekli | Yok | off |
| Pazarama | Ürün | UNKNOWN | Portal login gerekli | Yok | off |
| Pazarama | Stok/fiyat | UNKNOWN | Portal login gerekli | Yok | off |
| Pazarama | Sipariş/teslimat | UNKNOWN | Portal login gerekli | Yok | off |
| Pazarama | İade | UNKNOWN | Portal login gerekli | Yok | off |
| Pazarama | Fatura | UNKNOWN | Portal login gerekli | Yok | off |

## Resmî kaynak kayıtları

Doğrulama tarihi: 2026-07-31. URL erişimi capability desteği anlamına gelmez.

- Trendyol: <https://developers.trendyol.com/v3.0/docs/product-v2-api-endpoint>, <https://developers.trendyol.com/v3.0/docs/category-attribute-list-v2>, <https://developers.trendyol.com/v3.0/docs/1-webhook-model>, <https://developers.trendyol.com/v2.0/docs/product-create-v2>, <https://developers.trendyol.com/reference/sendinvoicelink>, <https://developers.trendyol.com/reference/uploadinvoicefile>
- E-Faturam: <https://developers.trendyolefaturam.com/OpenApi/trendyol-e-faturam-entegrasyon-dokumani>, <https://developers.trendyolefaturam.com/OpenApi/Auth/sign-in>, <https://developers.trendyolefaturam.com/OpenApi/Diğer/get-application-status-by-tax-id>, <https://developers.trendyolefaturam.com/OpenApi/Giden%20eFatura/create-outgoing-e-invoice>, <https://developers.trendyolefaturam.com/OpenApi/eArşiv/get-e-archive-status>, <https://developers.trendyolefaturam.com/OpenApi/Diğer/get-temporary-document-download-url>, <https://developers.trendyolefaturam.com/OpenApi/eArşiv/cancel-e-archive>
- Shopify: <https://shopify.dev/docs/api/usage/versioning>, <https://shopify.dev/docs/api/usage/bulk-operations/queries>, <https://shopify.dev/docs/api/usage/bulk-operations/imports>, <https://shopify.dev/docs/apps/build/webhooks/verify-deliveries>, <https://shopify.dev/docs/api/admin-graphql/2026-07>
- Hepsiburada: <https://developers.hepsiburada.com>
- N11: <https://magazadestek.n11.com/faydali-dokumanlar>
- Pazarama: <https://isortagim.pazarama.com/auth/integration>

## Capability kanıt kaydı şablonu

| Alan | Değer |
| --- | --- |
| Capability code / supportLevel | |
| Tenant / connection / environment / API version / store scope | |
| VerifiedAt / verifier | |
| Source URL / source version | |
| Required scope | |
| Constraints | |
| Anonymous fixture checksum | |
| Evidence note | |
| Write switch approval | |
