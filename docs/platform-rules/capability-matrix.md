# Platform Capability Matrisi

## Kanıt sözleşmesi

Support level yalnız `SUPPORTED`, `NOT_SUPPORTED`, `UNKNOWN`, `TEMPORARILY_UNAVAILABLE` olabilir. Başlangıç değeri `UNKNOWN`dur. `SUPPORTED` için birlikte şunlar zorunludur: güncel resmî kaynak, test hesabı veya secret/PII içermeyen anonim fixture, doğrulama tarihi, kaynak sürümü, gerekli scope, tenant+connection+environment+API version+store scope ve evidence note. Read desteği write desteği değildir.

Bu F0 incelemesinde resmî sayfa erişimi bazı platformlarda doğrulanmış, fakat hiçbir platform için test hesabı/anonim davranış kanıtı sağlanmamıştır. Bu nedenle bütün capability'ler `UNKNOWN`, tüm dış yazmalar kapalıdır.

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
| Trendyol | Bağlantı | UNKNOWN | Doküman erişilebilir | Yok | off |
| Trendyol | Katalog referansı | UNKNOWN | Doküman erişilebilir | Yok | off |
| Trendyol | Ürün | UNKNOWN | Doküman erişilebilir | Yok | off |
| Trendyol | Stok/fiyat | UNKNOWN | Kapsam kanıtlanmadı | Yok | off |
| Trendyol | Sipariş/teslimat | UNKNOWN | Webhook dokümanı erişilebilir | Yok | off |
| Trendyol | İade | UNKNOWN | Kapsam kanıtlanmadı | Yok | off |
| Trendyol | Fatura | UNKNOWN | Fatura bağlantı dokümanı erişilebilir | Yok | off |
| E-Faturam | Bağlantı | UNKNOWN | Doküman erişilebilir | Yok | off |
| E-Faturam | Katalog referansı | UNKNOWN | Uygulanabilirlik kanıtlanmadı | Yok | off |
| E-Faturam | Ürün | UNKNOWN | Uygulanabilirlik kanıtlanmadı | Yok | off |
| E-Faturam | Stok/fiyat | UNKNOWN | Uygulanabilirlik kanıtlanmadı | Yok | off |
| E-Faturam | Sipariş/teslimat | UNKNOWN | Uygulanabilirlik kanıtlanmadı | Yok | off |
| E-Faturam | İade | UNKNOWN | Uygulanabilirlik kanıtlanmadı | Yok | off |
| E-Faturam | Fatura | UNKNOWN | Doküman erişilebilir | Yok | off |
| Shopify | Bağlantı | UNKNOWN | Doküman erişilebilir | Yok | off |
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

- Trendyol: <https://developers.trendyol.com/v3.0/docs/product-v2-api-endpoint>, <https://developers.trendyol.com/v3.0/docs/category-attribute-list-v2>, <https://developers.trendyol.com/v3.0/docs/1-webhook-model>, <https://developers.trendyol.com/v2.0/docs/product-create-v2>, <https://developers.trendyol.com/v3.0/docs/2-delete-invoice-link>
- E-Faturam: <https://developers.trendyolefaturam.com/docs>
- Shopify: <https://shopify.dev/docs/api/usage/bulk-operations/queries>, <https://shopify.dev/docs/apps/build/webhooks/verify-deliveries>, <https://shopify.dev/docs/api/admin-graphql/latest/mutations/productDelete>
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
