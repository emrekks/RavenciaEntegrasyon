# Trendyol Product Integration V2 adapter

## Sınır

Bu adapter yalnız F3 Trendyol dikey dilimidir. Product V1 fallback, fatura, diğer marketplace'ler ve dış yazmayı kendiliğinden açan davranış içermez. Bağlantı environment'ı, mağaza kimliği, V2 API seçimi, şifreli credential ve User-Agent her istekten önce connection scope içinde çözülür.

## Doğrulanmış resmî endpoint şablonları

Doğrulama tarihi `2026-07-31`'dir. Production kökü `https://apigw.trendyol.com/integration/`, Stage kökü `https://stageapigw.trendyol.com/integration/` olarak resmî belgede yayınlanmıştır.

| İşlem | Method + relative path | Resmî kaynak |
| --- | --- | --- |
| Product V2 create | `POST product/sellers/{sellerId}/v2/products` | <https://developers.trendyol.com/v2.0/docs/product-create-v2> |
| Approved product read | `GET product/sellers/{sellerId}/products/approved` | <https://developers.trendyol.com/v2.0/docs/product-filtering-approved-products-v2> |
| Unapproved product read | `GET product/sellers/{sellerId}/products/unapproved` | <https://developers.trendyol.com/v2.0/docs/product-filtering-unapproved-products-v2> |
| Batch result | `GET product/sellers/{sellerId}/products/batch-requests/{batchRequestId}` | <https://developers.trendyol.com/v2.0/docs/check-batchrequest-result-getbatchrequestresult-1> |
| Stock + price | `POST inventory/sellers/{sellerId}/products/price-and-inventory` | <https://developers.trendyol.com/v2.0/docs/stock-and-price-update-updatepriceandinventory-1> |
| Category tree | `GET product/product-categories` | <https://developers.trendyol.com/v2.0/docs/trendyol-category-list-getcategorytree> |
| Category attributes V2 | `GET product/categories/{categoryId}/attributes` | <https://developers.trendyol.com/v2.0/docs/category-attribute-list-v2> |
| Attribute values V2 | `GET product/categories/{categoryId}/attributes/{attributeId}/values` | <https://developers.trendyol.com/v2.0/docs/category-attribute-values-list-v2> |
| Brands | `GET product/brands` | <https://developers.trendyol.com/v2.0/docs/trendyol-brand-list-getbrands-1> |
| Order cursor stream | `GET order/sellers/{sellerId}/orders/stream` | <https://developers.trendyol.com/v2.0/docs/getshipmentpackagesstream> |
| Return claims | `GET order/sellers/{sellerId}/claims` | <https://developers.trendyol.com/v2.0/docs/getting-returned-orders-getclaims> |
| Send invoice link | `POST sellers/{sellerId}/seller-invoice-links` | <https://developers.trendyol.com/v2.0/reference/sendinvoicelink> |

Basic Auth ve zorunlu User-Agent, <https://developers.trendyol.com/v2.0/docs/authorization> kaynağına göre uygulanır. Runtime rate policy sabit bir tahmin kullanmaz; `429` ve varsa `Retry-After` otoritedir.

Kategori özellik snapshot'ı `categoryId`, özellik değeri snapshot'ı `categoryId/attributeId` scope'u ile saklanır. Yeni bir kategorinin eşitlenmesi başka kategorinin güncel snapshot'ını geçersiz kılmaz. `required`, `allowCustom` ve `allowMultipleAttributeValues` alanları yayınlama doğrulamasına taşınır; güncel scoped snapshot/mapping veya zorunlu değer eksikse ürün yazma işi oluşturulmaz.

## Product Create onay uzlaştırması

Create batch kabulü canlı yayın kanıtı değildir. Tam veya kısmi batch içinde kabul edilen satırlar onay read-back akışına girer; batch aşamasında reddedilen satırlar korunur. Worker her barkodu önce `products/approved`, sonuçta bire bir barkod yoksa `products/unapproved` endpoint'inde sorgular. Approved cevapta `contentId` ve `variantId` zorunludur; bunlar tenant + connection kapsamında benzersiz linklere fail-closed biçimde yazılır. Unapproved `pendingApproval` yeniden denenir, `rejected` ret nedeni ile terminal blocked olur. İki listede henüz görünmeyen barkod görünürlük gecikmesi kabul edilerek pending kalır. Mevcut bir yerel linkin farklı uzak kimlikle çakışması otomatik rewire edilmez ve manuel incelemeye gider. Bu akış salt-okunur uzak çağrı yapar; yeni marketplace write effect üretmez. Yedi günlük deadline sağlayıcı SLA'sı değil, sonsuz polling'i durduran yerel operasyon sınırıdır.

## Fail-closed davranış

- Capability satırları bağlantı oluşturulduğunda `UNKNOWN` başlar.
- Başarılı gerçek connection test yalnız test ettiği `CONNECTION_TEST` ve `ORDER_READ` kanıtını `SUPPORTED` yapar.
- Global `FeatureFlags:ExternalWrites` ile connection içi `ExternalWritesEnabled` birlikte açık değilse write portu HTTP üretmez.
- Connection içi write anahtarını açan API özellikle yoktur; Stage safe-write kanıtı ve ayrı kullanıcı kararı beklenir.
- Archive, ayrık stock/price, package action ve return action için exact Stage sözleşmesi kanıtlanmadığından adapter endpoint uydurmaz ve `CAPABILITY_NOT_VERIFIED` döner.
- Trendyol’a gönderilen fatura bağlantısı yalnız HTTPS olmakla yetinmez; resmî sözleşmedeki sekiz yıllık erişilebilirlik şartı provider/operasyon kanıtı olmadan production-ready sayılmaz.
- Webhook auth yalnız resmî `API_KEY` (`x-api-key`) veya `BASIC_AUTHENTICATION` modelidir. Trendyol için belgelenmemiş HMAC/timestamp uygulanmaz.

## Fixture standardı

`Fixtures/` dosyaları secret, gerçek mağaza kimliği, müşteri adı, adres, telefon ve gerçek sipariş/claim kimliği içermez. `ORDER-ANON-*`, `SKU-ANON-*` ve benzeri değerler yalnız parser contract testidir; resmî enum veya iş varsayımı değildir. HTTP 401/429/5xx sınıflandırması response-body alanı uydurulmadan boş JSON fixture ve HTTP status/header üzerinden test edilir. Timeout, transport seviyesinde test edilir.

Fixture checksum'ları `docs/implementation/F3-evidence-log.md` içinde kaydedilir. Fixture kanıtı gerçek Stage/SIT capability kanıtının yerine geçmez.
