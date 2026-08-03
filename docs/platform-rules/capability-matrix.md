# Platform Capability Matrisi

## Kanıt sözleşmesi

Support level yalnız `SUPPORTED`, `NOT_SUPPORTED`, `UNKNOWN`, `TEMPORARILY_UNAVAILABLE` olabilir. Başlangıç değeri `UNKNOWN`dur. `SUPPORTED` için birlikte şunlar zorunludur: güncel resmî kaynak, test hesabı veya secret/PII içermeyen anonim fixture, doğrulama tarihi, kaynak sürümü, gerekli scope, tenant+connection+environment+API version+store scope ve evidence note. Read desteği write desteği değildir.

F0 başlangıcında hiçbir platform için test hesabı/anonim davranış kanıtı sağlanmamıştı. Yerel adapter/contract kanıtlarına ek olarak 2026-08-03 tarihinde üretim panelinden salt-okunur Stage bağlantı testleri çalıştırıldı: Trendyol bağlantı ve sipariş okuma, E-Faturam bağlantı, Hepsiburada bağlantı ve Hepsiburada sipariş okuma kanıtı `SUPPORTED` oldu. 2026-08-04 Trendyol tekrar testlerinde `ProductRead=SUPPORTED`, ardından resmî kategori ağacı Stage probuyla `ReferenceRead=SUPPORTED` doğrulandı. `ReturnRead=UNKNOWN (REMOTE_RESOURCE_NOT_FOUND)` kalır. Tüm dış yazmalar kapalıdır.

ADR-015 aktif teslim kapsamını `Trendyol → Hepsiburada → Trendyol E-Faturam` ile sınırlar. Shopify, N11 ve Pazarama satırları tarihsel capability envanteri olarak korunur; yeni doğrulama veya capability açılışı yapılmaz ve mevcut `UNKNOWN`/write-off durumları tamamlanmış sayılmaz.

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
| Trendyol | Bağlantı | SUPPORTED | Auth/environment dokümanı doğrulandı | 2026-08-03 üretim paneli Stage testi; connection `VERIFIED` | off |
| Trendyol | Katalog referansı | SUPPORTED | Product V2'nin kullandığı kategori ağacı doğrulandı; yalnız kanıtlanmış `CATEGORIES` kaynağı snapshot modeline alınır ve etkin leaf→leaf kategori mapping panelinde kullanılır; marka/özellik eşitlemesi açılmaz | 2026-08-04 production paneli Stage testi; job `019fc9c1-b405-759a-bf91-adbdec8c42ff`, `ReferenceRead=SUPPORTED`; idempotent snapshot `release-2026-08-04-4`, mapping API/UI `release-2026-08-04-5` CI'da geçti | off |
| Trendyol | Ürün | UNKNOWN | Product V2 create/read/batch yolları doğrulandı | 2026-08-04 production paneli Stage probu `ProductRead=SUPPORTED`; ProductWrite ve Stage safe-write `UNKNOWN` | off |
| Trendyol | Stok/fiyat | UNKNOWN | Birleşik endpoint ve partial batch doğrulandı | Partial fixture; Stage safe-write yok | off |
| Trendyol | Sipariş/teslimat | SUPPORTED | Cursor stream ve webhook auth doğrulandı | 2026-08-03 Stage connection testi `OrderRead=SUPPORTED`; webhook ve yazma capability'leri `UNKNOWN` | off |
| Trendyol | İade | UNKNOWN | Claims read dokümanı doğrulandı | 2026-08-04 Stage probu `REMOTE_RESOURCE_NOT_FOUND`; `ReturnRead=UNKNOWN`, Stage action yok | off |
| Trendyol | Fatura | UNKNOWN | Güncel invoice link yolu; package/link/date/number sözleşmesi doğrulandı | Yerel contract geçti; Stage package/delivery yok | off |
| E-Faturam | Bağlantı | SUPPORTED | API `1.0.0`, Stage/production ve sign-in sözleşmesi doğrulandı | 2026-08-03 üretim paneli Stage testi; connection `VERIFIED` | off |
| E-Faturam | Katalog referansı | UNKNOWN | Uygulanabilirlik kanıtlanmadı | Yok | off |
| E-Faturam | Ürün | UNKNOWN | Uygulanabilirlik kanıtlanmadı | Yok | off |
| E-Faturam | Stok/fiyat | UNKNOWN | Uygulanabilirlik kanıtlanmadı | Yok | off |
| E-Faturam | Sipariş/teslimat | UNKNOWN | Uygulanabilirlik kanıtlanmadı | Yok | off |
| E-Faturam | İade | UNKNOWN | Uygulanabilirlik kanıtlanmadı | Yok | off |
| E-Faturam | Fatura | UNKNOWN | Kuruş bazlı submit ve kalıcı document URL kaynakları doğrulandı; status/cancel ayrı kanıt bekliyor | Yerel 2-kalem/KDV/toplam/not/kalıcı-HTTPS contract geçti; test firma safe-write yok | off |
| Shopify | Bağlantı | UNKNOWN | 2026-07 pin ve yerel response-version guard hazır; development-store testi yok | Yok | off |
| Shopify | Katalog referansı | UNKNOWN | Kapsam kanıtlanmadı | Yok | off |
| Shopify | Ürün | UNKNOWN | Bulk/product delete dokümanı erişilebilir | Yok | off |
| Shopify | Stok/fiyat | UNKNOWN | Kapsam kanıtlanmadı | Yok | off |
| Shopify | Sipariş/teslimat | UNKNOWN | Webhook dokümanı erişilebilir | Yok | off |
| Shopify | İade | UNKNOWN | Kapsam kanıtlanmadı | Yok | off |
| Shopify | Fatura | UNKNOWN | Uygulanabilirlik kanıtlanmadı | Yok | off |
| Hepsiburada | Bağlantı | SUPPORTED | Sipariş SIT Basic Auth + User-Agent; resmî v1.0 kaynak 2026-06-04 | 2026-08-03 üretim paneli Stage testi; connection `VERIFIED` | off |
| Hepsiburada | Katalog referansı | UNKNOWN | Güncel guide erişilebilir; partner fixture yok | Yerel generic port/no-HTTP | off |
| Hepsiburada | Ürün | UNKNOWN | Async katalog guide erişilebilir; mapping fixture yok | Yerel generic port/no-write | off |
| Hepsiburada | Stok/fiyat | UNKNOWN | Listing capability/SIT kanıtı yok | Yerel generic port/no-write | off |
| Hepsiburada | Sipariş/teslimat | SUPPORTED | Sipariş SIT Basic Auth + User-Agent ve salt-okunur listeleme doğrulandı | 2026-08-03 `items=2`; `merchantSKU` canlı sözleşmesi, `ORDER_READ=SUPPORTED`, iki SIT siparişi yerel generic modele işlendi; package action/webhook `UNKNOWN` | off |
| Hepsiburada | İade | UNKNOWN | Talep guide erişilebilir; enum/action fixture yok | Yerel generic port/no-write | off |
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

- Trendyol: <https://developers.trendyol.com/changelog/changelog>, <https://developers.trendyol.com/tr/docs/sipariş-paketlerini-çekme-getshipmentpackages>, <https://developers.trendyol.com/docs/fatura-linki-gönderme-sendinvoicelink>
- E-Faturam: <https://developers.trendyolefaturam.com/OpenApi/trendyol-e-faturam-entegrasyon-dokumani>, <https://developers.trendyolefaturam.com/OpenApi/Auth/sign-in>, <https://developers.trendyolefaturam.com/OpenApi/Giden%20eFatura/create-outgoing-e-invoice>, <https://developers.trendyolefaturam.com/OpenApi/Diğer/get-permanent-document-download-url>
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
