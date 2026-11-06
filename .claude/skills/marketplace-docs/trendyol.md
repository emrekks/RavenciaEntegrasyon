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
- A statement record carries both `debt` and `credit` (one of them 0.0), `id` and `transactionDate` (epoch ms).
  Source: official docs `trendyol-api-son.md:12410-12437`. A record without `id`, `transactionDate` or any readable
  debt/credit is not stored (the page's other records are) and opens a FINANCE_RECORD_INCOMPLETE issue (5 Nov 2026).
- Platform service fee ("Platform Hizmet Bedeli", DeductionInvoices/PlatformServiceFee), international service fee
  ("Uluslararası Hizmet Bedeli") and e-commerce withholding tax (Stoppage, "E-ticaret Stopajı") come as invoices of a
  payment day: `orderNumber`, `shipmentPackageId`, `orderLineItemId` and `orderDate` are null; only `paymentOrderId`
  ties them to the payout, and the settlement lines of the orders that payout paid carry the same `paymentOrderId`
  once the payout is read (Trendyol lists a payout after paying it; its lines are read a day later). A few fee invoices
  carry no `paymentOrderId` either. Live, 30 payouts Aug–Nov 2026: platform fee = 13.19 TL (10.99 + VAT) × the payout's
  sale packages (10.99 on AZ payouts), international fee ≈ 5.1–5.9 % of its sales abroad, withholding tax ≈ 0.7–0.9 %
  of its sales less returns. The report splits them over the payout's orders (`ReportFeeAllocation`). Source: live
  `sales.marketplace_finance_transactions`, 5 Nov 2026.
- Return reconciliation (RETURN_RECONCILIATION, 3/90 days) follows the per-bucket cursor `s:Accepted=2` like
  RETURN_SYNC; before 6 Nov 2026 it asked all eight buckets page + 1, finished ones too (live Accepted had 188 claims
  in 90 days, page size 200) (`MarketplaceJobProcessor.NextReconcilePage`).
- Products: the PRODUCTS watermark is the moment a full catalog read began, less 60 s (the next read starts 2 min
  before it). Before 6 Nov 2026 it was the job's end (a scan took 11 min on 31 Oct, its own changes were lost) and a
  scoped read (newOnly, existingOnly, mappingOnly, optionsOnly) moved it too, so products it skipped were missed by
  the next incremental read (`ProductImportScanPolicy.CompletedWatermark`; shared by every marketplace).
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

## Sale state (T2-D30, 2026-11-05)
- Approved v2 list `variants[].onSale` (boolean) is the sale state; `locked`, `blacklisted`, `archived` sit beside it.
  Source: offline doc `## Ürün Filtreleme - Onaylı Ürün v2` (sample response; the list also filters `status=onSale|notOnSale`).
- Live (import staging 2026-10-07, 3,605 variants): `onSale=false` on every variant with stock 0 and on none with
  stock, so most closures are stock-outs. Code: `TrendyolJsonMapper.SaleState/SaleStateReason` (STOCK_ZERO when the same
  answer has quantity 0). The unapproved list has no sale state.

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
- Every shipment package carries `orderDate` (Turkey wall-clock epoch ms) and `currencyCode`; live never had a 1970
  order. Since 5 Nov 2026 a package without `orderDate` is refused (no `lastModifiedDate`/1970 fallback) and one
  without a three-letter `currencyCode` is skipped with an issue instead of failing the whole save (Order.Currency is
  char(3)); both join the re-read list (`TrendyolJsonMapper.Orders`, `RefusedOrders`).
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
- Catalog import and brands (owner decision 28 Oct 2026; report item 19, 5 Nov 2026): a listing carries `brandId` and
  `brand`; the import never looks a panel brand up by name, never opens one and never writes a brand mapping. The
  product takes the panel brand whose VERIFIED mapping on the connection points at that `brandId` (exactly one active
  brand); otherwise a new product has no brand, an existing one keeps its own, and the brand is listed as unmapped
  (BRAND_MAPPING_MISSING issue, `GET /reference-data/brands/unmapped`, Marka Eşleme card 2). The import no longer
  syncs the brand list itself. It does not change the panel product's or variant's status either; the listing's sale
  state stays on its link (`SaleState`) and offer. Source: `MarketplaceJobProcessor.MappedImportBrandId`.
- Order and claim pages always carry `content` (an empty list when nothing matches); a 200 without it is a contract
  error, never "no orders" (`TrendyolJsonMapper.RequiredContent`, report item 15, 5 Nov 2026).
- Exception, claims: since 5 Nov 2026 22:43 (live, main store) an empty `claimItemStatus` bucket of getClaims can answer
  without `content`; it counts as empty only when it says `totalElements` 0 (`TrendyolJsonMapper.ClaimContent`). Any
  other claim page without the list is still a contract error and its top-level keys are logged per bucket.
- Per-sale fees by storefront (live payouts Oct-Nov 2026, 6 Nov reconciliation): the platform service fee is 13.19
  TL (10.99 + VAT) a sale package on Türkiye-Körfez and Avrupa payouts and 10.99 TL without VAT on the Azerbaijan ones
  ("AZ-Platform Hizmet Bedeli", Trendyol's AZ company); the international fee is exactly 6.00 % of the micro-export
  sale net of the seller's discounts and coupons; the e-commerce withholding tax is cut on every storefront's sales
  (0.855-0.886 % of all sales on each TR and CEE payout, Bulgarian, Greek, Romanian and Gulf sales included) but on
  none of the Azerbaijan payouts (`ReportCosts.TrendyolServiceFee`, `TrendyolStoppageBase`).
- Settlement lines vs the panel: for all 453 October 2026 orders with a settlement, Sale − Discount − Coupon − Return
  equals the report's kept amount, and the commission the report's, to the kuruş.
- A payout's `paymentOrderId` is sometimes stamped on a record another payout of the same day netted (±196.68 between
  the AZ and TR payouts of 5, 12 and 19 Oct; ±413.91 between 30 Oct and 2 Nov): the payouts of a day add up, one alone
  may not. Its lines are read a day after the payout (`LinesReadAt`); until then the Hakediş shows no records total.
- Webhook package body names `supplierId` (`trendyol-api-son.md:6047`); a delivery whose `supplierId` is not the
  connection's seller id is answered 200, recorded (WEBHOOK_MERCHANT_MISMATCH) and dropped, as on Hepsiburada
  (`MarketplaceWebhookService.ForeignSupplier`, report item 17).
- A request whose quota wait ran out before it got its token was never sent: LOCAL_QUOTA_WAIT (RateLimit, the job
  keeps its attempt). One that was sent and timed out stays REMOTE_TIMEOUT (outcome unknown on a write)
  (`TrendyolHttpClient.SendAsync`, report item 16).
- A product-create line refused as "Aynı barkodlu bir ürününüz bulunduğundan…" is not a refusal: it stays
  CREATE_RESULT_UNKNOWN and the approval check reads the barcode; the same now holds for any marketplace's
  "zaten mevcut/kayıtlı/var", "already exist" wording (`ProductBatchResultPolicy.SaysBarcodeExists`, report item 14).

## Job types
TRENDYOL_CONNECTION_TEST, _REFERENCE_SYNC, _ORDER_SYNC (60 s), _ORDER_RECOVERY_SYNC (15 min),
_ORDER_CARGO_INFO_RECONCILIATION, _FINANCE_SYNC (daily, 90-day first read, 3-day overlap, 15-day windows),
_WEBHOOK_INGEST, _COMMON_LABEL, _CAPABILITY_PROBE, _STAGE_TEST_ORDER; plus shared ORDER_STATUS_SYNC (3 min),
ORDER_INVOICE_RECONCILIATION (15 min), RETURN_SYNC (10 min, 20–30 min while no claim is open; ReturnCadencePolicy;
15-min overlap), RETURN_STATUS_SYNC (open claims only, claimIds batched), PRODUCT_SYNC. The webhook carries order
packages only, no claim events.
