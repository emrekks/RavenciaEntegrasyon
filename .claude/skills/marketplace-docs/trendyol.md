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
- Product pull speed (live 12 Nov 2026, job 01a1cff0, 1,379 read, 17 min): the import groups staged rows by model and
  rebuilds the category context per model (its cache is cleared with the change tracker), mapping every value of every
  required or observed category attribute: ~5 s and ~4,000 queries per model whatever its product count. An attribute's
  values, value mappings and value snapshot are now read once per attribute (`StoredAttributeValues`). A reference
  snapshot older than 24 h is re-read from Trendyol on first use (510 ATTRIBUTE_VALUES reads in that run, under the
  50/10 s cap). A worker restart hands the job back to the queue and it resumes at the next unfinished model.
- Product pull options (owner, 12 Nov 2026): "Mevcut Ürün Bilgilerini Güncelle" on (single lookup, Mevcut Ürünleri
  Güncelle, and a full scan whose job names `updateExistingProducts: true`) takes Trendyol's content over panel edits
  and adds the sizes Trendyol has; "Ekli Olmayan Ürünler" (`newOnly`) adds new products and new sizes of existing
  cards, never a card's own fields; any other run keeps an owner-edited card whole (stock/price only) and opens a
  `PRODUCT_NEW_SIZES_NOT_IMPORTED` issue for the sizes it did not add. Trendyol has one picture list per colour
  content (written to the colour's first size and the card gallery); a size's own list that differs from the gallery
  is the panel's and no pull replaces it (`ProductImportMergePolicy.OverwriteLocalChanges`,
  `MarketplaceJobProcessor.VariantPicturesAreOwnersAsync`).
- Pull modes and what each writes (owner, 12 Nov 2026; `ProductImportScanPolicy`, tests in
  `TrendyolImportRegressionPostgreSqlTests`):
  - Mevcut Ürünleri Güncelle (`existingOnly`) / Tek Ürün with "Mevcut Ürün Bilgilerini Güncelle" on: the full path above;
    prices/stock only if ticked.
  - Same, the content box off and prices and/or stock ticked: the focused path (`FocusedPriceStock`,
    `MarketplaceJobProcessor.ApplyFocusedPriceStock`). Card by product link, else the card owning the variants (link,
    stock code, barcode); variant by link, stock code, barcode inside the card; writes only sale+list price (price
    history `TRENDYOL_CATALOG_IMPORT`) and/or stock (ledger `MARKETPLACE_STOCK_IMPORT`) through the same
    `UpsertCatalogOfferAndInventory`. No category context, no reference read, no content/attribute/picture/desi/link/mark
    change. Sale state (on sale / archived / blacklisted / locked) is recorded at page read for every mode
    (`MarkVariantLinksSeen`). Sizes the panel lacks are reported (`PRODUCT_NEW_SIZES_NOT_IMPORTED`), never added; a
    single lookup of a product the panel lacks still adds it. Result line: "Fiyatı değişen X varyant · Stoğu değişen Y
    varyant · Değişmeyen Z · Yeni beden N". Nothing ticked: the panel disables the start, the API answers 400
    `PRODUCT_SYNC_NOTHING_SELECTED` (Trendyol only).
  - Yeni Ürünleri Aktar (`newOnly`): a listing whose every variant the panel holds (link, stock code or barcode) is
    skipped before any category context; a card the panel links gets only its missing sizes (no link made for a variant
    the panel holds under another code, no desi fill, no category, the card's product link untouched); category
    references are read on the first product that needs them. "Yeni ürün X · Yeni beden Y · Zaten panelde Z".
  - Ürün Eşleme (`mappingOnly`): links only (variant links, a missing product link); an existing product link keeps its
    state (it was re-marked MAPPED before 12 Nov 2026). Match: link holds, then barcode, stock code, model code + colour/
    size; archived panel variants are candidates. Always a whole-catalog read; archived Trendyol listings only with
    "Arşiv Ürünlerini Dahil Et" (the API forced it on before 12 Nov 2026). No reference read. "Eşlenen X · Zaten eşli Y
    · Panelde karşılığı yok Z · Çakışma W".
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
| Common label | POST/GET `sellers/{id}/common-label/{cargoTrackingNumber}`. GET returns only `data[].label` (a ZPL string) and `format`; the TEX sticker's route/sort code ("1 - 20 - 346") and branch ("BÜYÜK ŞUBE") exist only drawn inside that ZPL, in no structured field of the package or order data (offline docs `trendyol-api-son.md` getCommonLabel / CommonLabelResponse; live `sales.*` snapshot keys checked 7 Nov 2026) |
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
- Trendyol names no reason field for `onSale=false`. STOCK_ZERO is our own inference: `onSale=false` + `quantity` 0 +
  none of `locked` / `blacklisted` / `archived` set (those give LOCKED / BLACKLISTED / ARCHIVED instead). The October
  live staging never showed `onSale=false` with stock > 0, so a closure with stock and no flag (reason null, plain
  "Satışa Kapalı") has not been seen yet. Source: live staging 2026-10-07 above; `TrendyolJsonMapper.SaleStateReason`.
- Display rule (owner, 6 Nov 2026, all marketplaces): a STOCK_ZERO closure is no closure in the products list, the
  publish card or the Satışa Kapalı filter (`ListingSaleState.ClosedBeyondStock`, server `OffSaleReasonCounts`).

## Statuses
- Packages: shared mapping. The order uses the top-level package `status`, not `shipmentPackageStatus`.
- Claims read: Created, WaitingInAction, WaitingFraudCheck (accepted as a request parameter: **unverified**),
  Accepted, Cancelled, Rejected, Unresolved, InAnalysis. A claim with no deadline is auto-accepted 48 h after it
  starts waiting.
- Invoice: NotInvoiced, Received, Rejected, Invoiced.

## Quirks
- New variants of a model already there (Yayınla, owner 11.11.2026, live RYP006): only the barcodes not there yet go,
  through the normal create (`products` v2) under the same `productMainId`. A new size of a live colour joins that
  colour's content; a new colour (Renk is a slicer) opens its own content under the model code with the images and
  attributes its items carry, so each item goes with its own variant pictures (size chart last) and its own Renk value.
  A new-colour variant without pictures of its own, or one without a price, stock row, valid barcode/stock code, desi
  or required attribute, is left out with its reason and the others still go (`PublicationSkippedVariant`); such a send
  never writes a live content, so a card marked PRODUCT_APPROVAL_CONTENT_SPLIT may add barcodes and keeps the mark.
  Source: offline `trendyol-api-son.md` ~2081-2100 (slicer opens separate contents, varianter = sizes of one content).
- `products/approved?productMainId=` is case-sensitive: live 7 Nov 2026 "Ryp003t" read 0 products, "RYP003T" 7. A single
  import by a typed code that reads nothing asks once more in upper case (`ProductImportMergePolicy.UpperCaseLookupRetry`).
- One content (`contentId`) per colour: a model (`productMainId`) with several colours is several contents, each with
  its own title, description, images and content attributes (Renk is a slicer); sizes are variants inside a content.
  `content-bulk-update` takes one item per contentId, so a multi-colour panel card is updated content by content: the
  job reads each barcode's content from `products/approved?barcode=` right before the send (`TrendyolContentGroups`).
  Source: offline docs "Ürün Güncelleme - Onaylı Ürün v2" and "Ürün Filtreleme - Onaylı Ürün V2"; live RYP006
  (6.11.2026, six colours x three sizes = six contents; the panel's product link held only the first approved one).
- `content-bulk-update` (approved products) is a partial update: only `contentId` is required per item; `title`,
  `description`, `images` (max 8) and `attributes` are optional and a field left out stays as Trendyol has it. The
  exception is the attributes: if any attribute changes, the whole attribute list with every value must go. Max 1,000
  items per request. So "Platformlarda Güncelle" sends only the picked content fields (Görseller alone = `contentId` +
  `images`); ATTRIBUTES sends the full list (colour, size, Menşei); nothing picked from the content group = no content
  request. A product not yet approved still goes whole via `unapproved-bulk-update`. Source: offline
  `trendyol-api-son.md`, `updateContentBulk` / `ContentBulkUpdateRequest` / `ContentUpdateItem` (~line 31769); bug
  6.11.2026, RYP007 images-only job 01a1b1c8-6fe7-78d3-bc90-702736436c38 had sent title, description and 18 attributes.
- Several cards in one request: the colour cards one "Platformlarda Güncelle" sends to a Trendyol connection share a
  `SendGroupId`; each step (content, variants, delivery, unapproved) goes once for all ready cards (max 1,000 items)
  and the one batch id is polled once by the sending card (`TrendyolUpdateGroups`). The content batch result names
  each line by `requestItem.contentId`, so a refused content stops only its own card; a refused line no card's
  content names stops every card of the request. Each card's effect record is written before the request, so an
  unclear answer stops every card of it for review. Source: owner 7.11.2026 (RYP003T, six cards had gone in six
  content requests); `TrendyolJsonMapper.Batch` (line key: barcode, contentId or stockCode of `requestItem`).
- A content's attribute list is built for the content, not taken from its first variant (`TrendyolContentGroups
  .ContentAttributes`, owner 6.11.2026): the category's **varianter** attribute (the size, one value per variant
  inside a content) is left out of `content-bulk-update`; every other attribute, the **slicer** (Renk) included, must
  have the same value on every variant of the content, else that content is not sent (PRODUCT_CONTENT_GROUP_MISMATCH,
  "aynı içerikteki varyantların özellikleri farklı: …"). The flags come from `getCategoryAttributes` (`varianter`,
  `slicer`), read right before the send; when they cannot be read nothing is left out and any differing attribute stops
  the content. Source: offline `trendyol-api-son.md` ~2081-2100 (slicer opens separate contents, varianter = sizes of
  one content, one varianter per category), ~2135 and ~2208 ("Onaylı ürünlerde … slicer veya varianter olan attribute
  value değerleri güncellenememektedir"; updatable only when both flags are false). **Unverified on live:** the guide
  also says a content attribute update must carry "tüm attribute ve değerleri"; whether Trendyol accepts the list
  without the varianter has not been seen yet (live RYP007 6.11.2026 went with the first variant's size and was
  accepted). Check the first ATTRIBUTES content update's batch result.
- Approved-product variant update (`variant-bulk-update`) is partial too, and a null DELETES: the guide says a partial
  request is taken when you send the `barcode` plus the field to change, and that sending a field as null (e.g.
  `vatRate: null`) removes that field's current value from the product entirely. So the variant request carries
  `barcode` + only the picked field (VAT_RATE → `vatRate`, DESI → `dimensionalWeight`), never `stockCode` or `origin`
  (no pick for them; all 14 live Trendyol profiles had an empty Origin on 6.11.2026, so the old request would have
  wiped Menşei), and never a null: a value the card lacks is left out, an item left with its barcode alone is dropped
  and an empty phase makes no call. The delivery request likewise carries only `barcode` + `deliveryOptions` (an
  object). Treat the same null-deletes rule as applying to every Trendyol update body. The older API reference
  sentence "Partial update desteklenmez" is contradicted by this guide; the guide (newer) wins. Source:
  https://developers.trendyol.com/docs/%C3%BCr%C3%BCn-g%C3%BCncelleme-onayl%C4%B1-%C3%BCr%C3%BCn-v2 (Varyant
  Güncelleme tab, checked 6.11.2026); audit Y1/D1. The job drawer lists a field a stored payload sent as null as
  "(silinir)" (`JobWritePayloadReader.ClearedFields`).
- Cross-border storefronts: orders are read per `storeFrontCode` (TR, AE, SA, GR, DE, BG, QA, KW, OM, BH, AZ, SK, RO,
  CZ; QA/OM/BH/AZ only when the connection already has orders there). Listed ones probed every 6 h, others 24 h; a
  400/404 pauses one for 30 days. SA, AZ, BG, RO, GR, AE orders ship to the Istanbul export depot but count in their
  storefront's country.
  A 400/404 no longer moves the storefront's last read: the next read starts from the last good one, and the
  rejection only spaces the next try (`TrendyolStorefrontActivity.LastRejectedAt`, 6 Nov 2026).
- Invoice references count only when invoice number AND link are present; a missing field is not "not invoiced", and
  an older observation never rolls a final state back. `invoiceLink` fills only once Invoiced; 409 = already has an
  invoice or the link is used elsewhere.
- Unsupplied or partial cancel changes the package id and cargo barcode; cargo company change max once per package
  per 5 min. A split package replaces its origin (origin kept, excluded from totals).
- An order awaiting payment (`Awaiting`) carries package id `0` until Trendyol creates the real package, so several
  orders of one store hold `0` at once: the unique package index skips `0` (`ShipmentPackageSupersession.
  PlaceholderExternalId`, migration `AllowTrendyolZeroPackagePlaceholders`) and the `0` row is superseded once the
  order has a real package. Source: live ORDER_PACKAGE_ID_CONFLICT on order 11763229493, 6 Nov 2026.
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
- Product and reference pages: no `content` is empty only while the page counts nothing; a page that counts elements
  (totalElements > 0) without the list, or a `content` that is not a list, is a contract error (before 6 Nov 2026 it
  ended the catalog scan). Finance pages the same (`TrendyolJsonMapper.Content`, `TrendyolFinanceMapper.Page`).
- Finance: a 400 on the first page of a type is a refusal of the type only if the connection has no record of that
  type yet; for a type read before it fails the window, which is read again (before 6 Nov 2026 the window was
  written as read without it). Live, 6 Nov 2026: all twelve types read so far have records, RejectedTypes empty.
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
- Payout days (Ödeme Özeti, 7 Nov 2026, against live records): Türkiye-Körfez **and Azerbaycan** pay Monday and
  Thursday; Monday takes the settlements due Monday-Wednesday, Thursday those due Thursday-Sunday (`paymentDate` is
  "the earliest day it can enter a payout", yet Trendyol pays it on the payout day on or before it), a deduction the
  first payout on or after its due day. Source: `trendyol-api-son.md:12361`, the payouts of 30 Oct-5 Nov 2026 in
  `sales.marketplace_payout_orders` and their records (`PayoutBuilder.Expect`).
- Trendyol's payout calendar (seller panel, Kasım 2026): "Ödeme Günü" every Monday and Thursday, "Erken Ödeme" every
  Tuesday and Friday (an early payout the seller requests, not a regular one), nothing on Wednesday, Saturday or
  Sunday. Ödeme Özeti → Gelecek Ödemeler on 11 Nov 2026 ("Tahmini Hesaplanmıştır"): 12/11 Türkiye-Körfez 25,737.90 +
  Azerbaycan 209.08; 16/11 21,862.79 + 1,347.01; 19/11 22,025.22 + 0.00; 23/11 25,242.42 + Azerbaycan 0.00 + **Avrupa
  15,034.33**; 26/11 45,922.71. Source: the owner's Trendyol panel screenshots, 11 Nov 2026.
- Avrupa (CEE) is paid on the **fourth Monday of the month** (the Monday of the 22nd-28th): live CEE payouts 22 Jun,
  27 Jul, 24 Aug, 28 Sep, 26 Oct 2026 and 23 Nov in the Ödeme Özeti. Each takes the Avrupa settlements due up to the
  payout day (26 Oct took those due 29 Sep-26 Oct), the cargo and platform fee invoices of Avrupa parcels billed
  through the month (with the Türkiye deduction days, 98.34 + 13.19 TL a parcel), and its international fee (6 %) and
  withholding tax (1 %) of all its sales net of discounts, billed the evening before. Every Türkiye-written deduction
  the first Türkiye payout after its billing left out (55 of them Aug-Nov 2026: Avrupa parcels' cargo and platform fee
  invoices with no parcel list, some "Yurtdışı Operasyon Iade Bedeli") was paid by the next Avrupa payout. Source:
  `sales.marketplace_payout_orders` and their records, 11 Nov 2026 (`PayoutBuilder.EuropePayoutDayOnOrAfter`).
- "Yurtdışı Operasyon Iade Bedeli" (micro-export return operations) is billed every Tuesday (03:05 Istanbul), with
  no order or region on it; most weeks the Thursday Türkiye payout takes it, some weeks the Avrupa payout. The
  11 Nov 2026 Ödeme Özeti left the three billed 10 Nov (192.20, 276.46, 685.78 = 1,154.44) out of its 12/11
  Türkiye estimate (25,737.90 unchanged since 7 Nov). Source: live records and the owner's screenshots, 11 Nov 2026.
- A payout's deductions are billed the evening before it (21:30-21:40 UTC, `paymentDate` = that day) and the
  withholding tax the payout's morning (~01:40 UTC): cargo ("Kargo Fatura": the shipment and return parcels of the
  payout's own orders, 98.34 TL a 0-2 desi parcel, 106.76-128.64 TL heavier ones), platform service fee (13.19 TL a
  package; 10.99 on Azerbaycan; a few packages 5.99), international fee (6 % of the micro-export sales net of
  discounts), withholding tax (1 % of the domestic sales net of discounts and returns without VAT + 1 % of the Gulf
  sales; none on Azerbaycan). Trendyol's Ödeme Özeti estimate already takes them off the payouts to come: on 7 Nov 2026
  Azerbaycan 858.08 and 209.08 rebuilt to the kuruş, Türkiye 17,777.82 within 7.20 (one package's 5.99 fee) and
  21,862.79 within 8.42 (one 106.76 parcel); 25,737.90 was 293.29 below (cause not visible in the API: a parcel or
  return Trendyol knew and the records did not yet). Source: live `sales.marketplace_finance_transactions`,
  `sales.marketplace_cargo_invoice_items`, the owner's Ödeme Özeti of 7 Nov 2026 (`PayoutBuilder.Estimate`).
- The cargo and platform fee invoices of Avrupa orders (and an Azerbaycan order's cargo) carry `country` "Türkiye" and
  affiliate TRENDYOLTR; only the cargo invoice's parcels (`cargo-invoice/{serial}/items` → order → settlement country)
  tell the region. The Türkiye payouts leave them out (open since 29 Oct 2026 past three Türkiye payouts); an
  Azerbaycan parcel's cargo invoice says in its description that it is netted from the Azerbaycan payout, yet the
  Türkiye payout's id is stamped on it. Source: live records, 7 Nov 2026.
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
- Product `images[].url`: https only, at most 8 per barcode, pictures 1200x1800 at 96 dpi (offline docs
  `trendyol-api-son.md`, product create/update "images"). We send our own pictures as
  `https://panel.ravencia.com/api/v1/public/product-media/{assetId}/content` (`ProductPublicationComposer`). While a
  product waits for approval the seller panel shows these URLs from partner.trendyol.com itself, so the response must
  carry `Cross-Origin-Resource-Policy: cross-origin` or Chrome shows broken pictures there (live 2026-11-06, MZ028IH-10
  Zümrüt: two empty picture slots; `deploy/caddy/Caddyfile.production`).

## Job types
TRENDYOL_CONNECTION_TEST, _REFERENCE_SYNC, _ORDER_SYNC (60 s), _ORDER_RECOVERY_SYNC (15 min),
_ORDER_CARGO_INFO_RECONCILIATION, _FINANCE_SYNC (daily, 90-day first read, 3-day overlap, 15-day windows),
_WEBHOOK_INGEST, _COMMON_LABEL, _CAPABILITY_PROBE, _STAGE_TEST_ORDER; plus shared ORDER_STATUS_SYNC (3 min),
ORDER_INVOICE_RECONCILIATION (15 min), RETURN_SYNC (10 min, 20–30 min while no claim is open; ReturnCadencePolicy;
15-min overlap), RETURN_STATUS_SYNC (open claims only, claimIds batched), PRODUCT_SYNC. The webhook carries order
packages only, no claim events.

## Advertising (7 Nov 2026)
- The API has no ad performance endpoint: advertising appears only as statement records ("Reklam Bedeli", category
  ADVERTISING; the docs list AdSharingExpense / MarketingExpense invoice types). The panel's "Trendyol Reklamı" is that
  amount by transaction date.
- ROAS, ad revenue, clicks and impressions come only from the seller's export, uploaded in Raporlar (Maliyet ve Reklam
  › Reklam Raporu, `POST /connections/{id}/ad-reports`): "Ürün Reklamları Raporum - dd.MM.yyyy-HH.mm.xlsx" from Reklam
  Yönetimi (the owner's sample of 7 Nov 2026). One sheet, one row per campaign: Reklam Adı | Reklam Statüsü | Başlangıç
  Tarihi | Bitiş Tarihi | Ürün Adedi | Ürün ContentId Listesi | Toplam Bütçe | Günlük Bütçe | Kalan Bütçe | Harcanan
  Bütçe | TBM Teklifi | Gerçekleşen TBM | Tıklanma | Görüntülenme | Doğrudan / Dolaylı / Toplam Satış Adedi | Doğrudan
  / Dolaylı / Toplam Reklam Cirosu | Harcama Getirisi. Every cell is a shared string in Turkish format ("1.508,76",
  "1.000" = a thousand, "-" = none); TBM Teklifi can be a list ("1,10 - 1,30 - 1,10"). Figures are each campaign's
  totals since its start as of the export (the time in the file name, Istanbul): no per-day breakdown, so they are never
  matched to a report period. Harcama Getirisi = Toplam Reklam Cirosu ÷ Harcanan Bütçe (3.834,27 ÷ 1.000 = 3,83).

## Return cargo (7 Nov 2026, live read-only)
- Cargo invoice lines typed "İade Kargo Bedeli" name their order (210 lines, 20,396 TL to 7 Nov 2026; "Gönderi Kargo
  Bedeli" 1,798 lines). "Yurtdışı Operasyon Iade Bedeli" (OTHER_FINANCIALS, category OTHER_DEDUCTION, 14 records /
  5,018 TL in 120 days) names no order, so the reports count it by its transaction date, among the deductions tied to
  no order. The overview's İade Kargo Zararı is both, billed amounts only (an unbilled return is not estimated there).
- Returns page sort and filter (11 Nov 2026): `ReturnClaim.RequestedAt` is the claim request date the İadeler page sorts by (default "En Yeni İade Talebi") and its "İade Talep Tarihi" range filters by (Istanbul days; an undated claim is left out while a range is set); a claim without it lists last, never guessed. Trendyol: getClaims `claimDate` only (GMT epoch ms; `createdDate`/`requestDate` are not taken). Claims stored before it are dated by RETURN_LIFECYCLE, one claimIds request of 20 undated claims per run (`DateTrendyolReturnRequestsAsync`, cursor RETURN_REQUESTED_AT); claim-built orders' snapshot claimDate dated 23 older claims in migration AddReturnClaimRequestedAt (`TrendyolJsonMapper.Returns`).

## Resend packages ("Yeniden Gönderim", 11 Nov 2026, live read-only)
- A resend opened after a missing-item request on a delivered order (11805932340: original package 4326252839
  Delivered 11 Nov 11:40Z; resend 4337242198, 2 lines, Trendyol Express 7330038976334420, seller panel → Yeniden
  Gönderimler, "Sipariş Tarihi" 16:56 = 13:56Z) is **not returned by any order read**: v2 `orders?orderNumber=` (with
  and without dates), v1 `orders?orderNumber=`, v2 `shipmentPackageIds=4337242198,4326252839` and the stream window
  13:50–14:10Z all answered only 4326252839 (`createdBy` "order-creation", `originPackageIds` null). No webhook brought it,
  and getClaims `orderNumber=11805932340` answered no claim (totalElements 0, read again 17:10Z): the missing-item
  request is not a claim in the API either.
- The only trace in the API: the original package's `lastModifiedDate` moved to the resend's moment (13:56:41Z) with no
  new `packageHistories` entry and no status change; its lines carry no resend or claim marker
  (`defectiveClaimListingInsight` ""). Delivered packages are touched like this 13–57 times a day for other reasons
  too, so the bump alone does not identify a resend.
- No Trendyol package with `createdBy` other than order-creation/cancel was ever stored (live
  `sales.shipment_packages`, 11 Nov 2026); the panel's Yeniden Gönderim card (`ShipmentPackageClassification.IsResend`)
  cannot count Trendyol resends from the order API. Source: one-time diagnostic read (resolved operational issue
  TRENDYOL_RESEND_DIAGNOSTIC, `MarketplaceJobProcessor.ResendDiagnostic.cs`), 11 Nov 2026 16:10Z and 17:10Z.
