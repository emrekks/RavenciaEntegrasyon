# Trendyol

Docs: https://developers.trendyol.com (`/docs/1-servis-limitleri`, `/v2.0/docs/authorization`,
`/v2.0/docs/getshipmentpackagesstream`, `/v2.0/docs/getshipmentpackages`, `/v2.0/docs/getting-returned-orders-getclaims`,
`/v2.0/docs/current-account-statement-integration.md`).

Code: `Adapters/Trendyol/` — `TrendyolHttpClient.cs` (+ `.Webhooks.cs`), `TrendyolFinance.cs`, `TrendyolQuestions.cs`,
`TrendyolAuthenticationHandler.cs`, `TrendyolResilienceHandler.cs`, `TrendyolOrderTier.cs`, `TrendyolOptions.cs`,
`TrendyolStockRules.cs`, `Mapping/TrendyolJsonMapper.cs`.

## Connection
- Prod `https://apigw.trendyol.com/integration/`, stage `https://stageapigw.trendyol.com/integration/`.
- `Authorization: Basic base64(ApiKey:ApiSecret)`; `User-Agent: "{sellerId} - {IntegratorName}"`, ASCII only — a
  missing User-Agent is a 403; `storeFrontCode` header, default `TR`. Seller id = `ExternalStoreId`, in the path.

## Limits
- Docs: 50 requests per endpoint per 10 s, the 51st gets 429 `too.many.requests`. Code is stricter: 50 per 10 s
  across all calls, concurrency 8, timeout 30 s.
- Order packages (stream and V2): 30/40/50/100/100 per minute by listing tier (50k/75k/150k/500k/unlimited listings).
  Code learns the tier (`TrendyolOrderTier`): floor 30, steps up after 20 quota waits in 10 min, a 429 steps back and
  holds a day. 40% of the order quota is reserved for foreground work.
- Stream: at least 5 s between pages of one query (docs); code waits 1 s between new queries.
- Group limits since 14 Sep 2026 (not modelled in code; the 50/10 s cap is tighter): Product Read 1000-2000/min,
  Product Write 200-600/min, Inventory & Price Write 350-2000/min, per-barcode price 30/min, claim approve/reject
  5/min, finance 100/min, brand/category lists 50/min.
- Price and inventory: an identical request is refused for 15 min (`TRENDYOL_REPEAT_REQUEST_WAIT`); stock max 20,000
  per barcode; max 1000 items per request.
- 429 handling: circuit opens for `Retry-After` (clamped 5-300 s, default 30 s; circuit cap 15 min); 5 failures
  (408/429/5xx) open it for 30 s; local quota wait over 10 min → `LOCAL_QUOTA_BUSY`. Retries 2, 5, 15, 30, 60 s
  + 0-20% jitter, 6 attempts.

## Windows and paging
- Stream `GET order/sellers/{id}/orders/stream`: `lastModifiedStartDate`/`EndDate` epoch ms, max 14 days per request,
  data 3 months back, no dates = last 2 weeks. `size` ≤ 200 (default 50). Cursor `nextCursor`/`hasMore`; changing a
  filter mid-cursor is a 400. Initial lookback 3 months; hot sync overlaps 10 min.
- V2 `GET order/sellers/{id}/v2/orders`: `startDate`, `endDate`, `orderNumber`, `shipmentPackageIds` (≤ 50), `page`,
  `size` (≤ 200). **Unverified:** 1 month back, ≤ 14 days per request, ≤ 10,000 records per query. An orderNumber
  that finds nothing is retried quoted.
- Package lookup by status time: ±6 days, 3-month cutoff.
- Claims `GET order/sellers/{id}/claims`: dates filter the claim's lastModifiedDate; one cursor bucket per
  `claimItemStatus`; page 200 and 3-month depth **unverified**; no storeFrontCode sent (TR only).
- Finance `finance/che`: max 15-day window, page 500 or 1000, `supplierId` required.
- Questions: default from now − 7 days; ≤ 14 days per request, page ≤ 50, older ranges silently trimmed.

## Time
Docs: stream epochs are GMT+3; claims `claimDate` is GMT (+3 h to match the panel). Code uses plain UTC epochs on
purpose (`TurkeyTime.cs`).

## Endpoints (`TrendyolOptions.cs`)
| Purpose | Call |
|---|---|
| Product create / updates | POST `product/sellers/{id}/v2/products`; `.../products/unapproved-bulk-update`, `content-bulk-update`, `variant-bulk-update`, `delivery-info-bulk-update` |
| Archive | PUT `.../products/archive-state` |
| Products | GET `.../products/approved`, `.../products/unapproved` (`dateQueryType=VARIANT_MODIFIED_DATE`) |
| Batch status | GET `product/sellers/{id}/products/batch-requests/{batchId}` |
| Price and stock | POST `inventory/sellers/{id}/products/price-and-inventory` |
| Package status | PUT `order/sellers/{id}/shipment-packages/{pkg}`; sub-actions `/items/unsupplied`, `/tracking-details`, `/cargo-providers`, `/alternative-delivery`, `/manual-deliver`, `/manual-return` (PUT), `/split`, `/multi-split` (POST) |
| Common label | POST/GET `sellers/{id}/common-label/{cargoTrackingNumber}` |
| Claims | GET `.../claims`, `.../claims/items/{itemId}/audit`; PUT `.../claims/{claimId}/items/approve`; POST `.../claims/{claimId}/issue` (multipart); GET `order/claim-issue-reasons` |
| Invoice link | POST `sellers/{id}/seller-invoice-links`, `.../delete` (202 done, 404 none) |
| Finance | GET `finance/che/sellers/{id}/settlements`, `/otherfinancials`, `/cargo-invoice/{serial}/items` |
| Webhooks | `webhook/sellers/{id}/webhooks[/{webhookId}]` |
| Stage test order | POST `test/order/orders/core` (header `sellerId`) |
| Reference | GET `product/product-categories`, `product/brands`, `product/categories/{c}/attributes[/{a}/values]` |

## Statuses
- Packages: shared mapping. The order uses the top-level package `status`, not `shipmentPackageStatus`.
- Claims read: Created, WaitingInAction, WaitingFraudCheck (accepted as a request parameter: **unverified**),
  Accepted, Cancelled, Rejected, Unresolved, InAnalysis. A claim with no deadline is auto-accepted 48 h after it
  starts waiting.
- Invoice: NotInvoiced, Received, Rejected, Invoiced.

## Quirks
- Cross-border storefronts: orders are read per `storeFrontCode` (TR, AE, SA, GR, DE, BG, QA, KW, OM, BH, AZ, SK, RO,
  CZ; QA/OM/BH/AZ only when the connection already has orders there). Listed ones probed every 6 h, others 24 h; a
  400/404 pauses one for 30 days. SA, AZ, BG, RO, GR, AE orders ship to the Istanbul export depot but count in their
  storefront's country.
- Invoice references count only when invoice number AND link are present; a missing field is not "not invoiced", and
  an older observation never rolls a final state back. `invoiceLink` fills only once Invoiced; 409 = already has an
  invoice or the link is used elsewhere.
- Unsupplied or partial cancel changes the package id and cargo barcode; cargo company change max once per package
  per 5 min. A split package replaces its origin (origin kept, excluded from totals).
- A line without `vatRate` gets the rate of its siblings; no invoice until the rate is known.
- Price-inventory batches report only per-item SUCCESS/FAILED.
- Webhooks: max 15 per seller; URL may not contain "Trendyol", "Dolap", "Localhost"; retried every 5 min then set
  PASSIVE; payload has `orderCountryCode`, no storefront. The stream stays the source of truth.
- Product update polling: every 2 min for 10 min, then 5, 15, 30 min; stops after 4 h.
- Commission tariff workbook ("KomisyonTarifeleriÜrünleri", seller panel → Ürün Komisyon Tarifeleri; no API): one
  row per barcode, band limits `1.Fiyat Alt Limit`, `2.Fiyat Üst/Alt Limit`, `3.Fiyat Üst/Alt Limit`,
  `4.Fiyat Üst Limit`, both ends included and a band's lower limit one kuruş over the next band's upper one (all 641 rows
  of the 29 Oct 2026 file). The week is two periods with the same limits and their own rates: "Tarih aralığı (3 Gün)"
  (Thu 08.00 – Sun 07.59) and "Tarih aralığı (4 Gün)" (Sun 08.00 – Thu 07.59), each with its 1.–4.KOMİSYON columns
  (export of 31 Oct 2026; `CommissionTariffFileParser`). Uploaded back, Trendyol reads `YENİ TSF (FİYAT GÜNCELLE)` and
  `Tarife Seçimi` ("3 Günlük Fiyat", "4 Günlük Fiyat", "7 Günlük Fiyat", "Trendyol Satış Fiyatı"): one price per row,
  so a product priced alike in both periods is "7 Günlük Fiyat" (`TrendyolTariffWorkbook`). A file uploaded before
  31 Oct 2026 03:02 kept only its first period and not the file itself; uploading the same file again adds the second.

## Job types
TRENDYOL_CONNECTION_TEST, _REFERENCE_SYNC, _ORDER_SYNC (60 s), _ORDER_RECOVERY_SYNC (15 min),
_ORDER_CARGO_INFO_RECONCILIATION, _FINANCE_SYNC (daily, 90-day first read, 3-day overlap, 15-day windows),
_WEBHOOK_INGEST, _COMMON_LABEL, _CAPABILITY_PROBE, _STAGE_TEST_ORDER; plus shared ORDER_STATUS_SYNC (3 min),
ORDER_INVOICE_RECONCILIATION (15 min), RETURN_SYNC (3 min, 15-min overlap), PRODUCT_SYNC.
