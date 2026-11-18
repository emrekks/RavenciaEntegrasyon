# Trendyol

Docs: https://developers.trendyol.com (`/docs/1-servis-limitleri`, `/v2.0/docs/authorization`,
`/v2.0/docs/getshipmentpackagesstream`, `/v2.0/docs/getshipmentpackages`, `/v2.0/docs/getting-returned-orders-getclaims`,
`/v2.0/docs/current-account-statement-integration.md`).

Code: `Adapters/Trendyol/` — `TrendyolHttpClient.cs` (+ the `TrendyolHttpClient.*.cs` partials: Catalog, Connection, Diagnostics, Invoices, Orders, Packages, ProductWrites, Returns, Transport, Webhooks), `TrendyolFinance.cs`, `TrendyolQuestions.cs`,
`TrendyolAuthenticationHandler.cs`, `TrendyolResilienceHandler.cs`, `TrendyolOrderTier.cs`, `TrendyolOptions.cs`,
`TrendyolStockRules.cs`, `Mapping/TrendyolJsonMapper.cs` (+ `.Orders.cs`, `.Products.cs`, `.References.cs`, `.Returns.cs`).

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
- Group limits since 14 Sep 2026: Product Read 1000-2000/min, Product Write 200-600/min, Inventory & Price Write
  350-2000/min, per-barcode price 30/min, claim approve/reject 5/min, finance 100/min. Brand and category lists
  (Marka Listesi, Kategori Listesi, its attributes and values) count in Product Integration Read (1000-2000/min); the
  "TY Marka/Kategori Listesi 50 req/min" rows are the table valid until 14 Sep 2026 (offline doc
  `trendyol-api-son.md:503-526` vs `:572-575`; corrected 14 Nov 2026).
  Code models claim approve/reject (5/min per seller, every non-GET under `claims/`) and finance (`finance/che`,
  100/min per seller) as their own quotas (`TrendyolResilienceHandler.GroupQuotaFor`, 13 Nov 2026); the others are
  covered by the tighter 50/10 s cap. Claim approve/reject also has its own circuit (`order:claim-actions`): before
  13 Nov 2026 it shared the claim read circuit, so a 429 on the 6th approval in a minute stopped claim reads too.
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
- Package lookup by status time: ±6 days, 3-month cutoff. A package without a status time is looked for ±6 days
  around its order's last change, else its order date (`TrendyolHttpClient.PackageStreamWindow`, since 16 Nov 2026);
  outside V2's month and the stream's 3 months nothing is asked: TRENDYOL_PACKAGE_OUT_OF_READ_WINDOW (unknown, not 404-gone).
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
- Products waiting for approval ("Onay Bekleyen Ürünleri Dahil Et"): after the approved pass the import reads GET
  `.../products/unapproved` (page ≤ 1000, page × size ≤ 10,000, `nextPageToken` beyond; rows are per barcode, keyed by
  `productMainId`, no `contentId`/`variantId`, `status` = `pendingApproval` or `rejected`, `docNeeded`,
  `rejectReasonDetails`). The docs list `status=rejected|pendingApproval` as a filter, but live 14 Nov 2026 the full
  import (job 01a1d77e, 12 pages = 11 approved + 1 unapproved) and LISTING_VERIFY both read **0 rows** with
  `status=pendingApproval` while the owner had products waiting in the seller panel. Since 14 Nov the pass sends no
  status and drops only rows marked `rejected` (`TrendyolHttpClient.CatalogQuery`,
  `TrendyolJsonMapper.CatalogProducts`); the worker logs each unapproved page's status counts ("Trendyol unapproved
  catalog page"). Whether `status=pendingApproval` itself is broken or the panel's waiting products carry another
  status is still **unverified**: read that log line after the next import. Source: offline docs "Ürün Filtreleme -
  Onaysız Ürün v2" and the filterUnapprovedProducts OpenAPI; live `catalog.product_import_sessions` and
  `LISTING_VERIFY_STATE` cursor, 14 Nov 2026.
- The unapproved pass takes no date window either (since 14 Nov 2026). An incremental run ("Yeni Ürünleri Aktar",
  job 01a1da88, 14:49Z, 13 approved rows) sent `startDate=<PRODUCTS watermark>&dateQueryType=LAST_MODIFIED_DATE` to
  `/products/unapproved` and got `total 0`, while products put up on the 13th waited unchanged in "Onay Bekleyenler":
  a waiting product's `lastUpdateDate` does not move, so a "since last read" window never sees it. The pass now reads
  the whole list every time (barcode / productMainId lookups keep their filter). The offline docs name no other list
  of waiting products: `/products/unapproved` is the only one (v1 `filterProducts?approved=false` was retired 15 Oct
  2026; "Temel Bilgiler v2" `.../product/{barcode}` answers one barcode only). Host, path and seller id are those of
  the approved list that reads fine. Source: offline docs "Ürün Filtreleme - Onaysız Ürün v2", "Ürün Filtreleme V1",
  "Ürün Filtreleme - Temel Bilgiler v2"; live job 01a1da88 and its worker log line, 14 Nov 2026.
- Approved list `dateQueryType=VARIANT_MODIFIED_DATE` filters `sellerModifiedDate`, "the date the seller last updated
  the barcode" (offline docs, approved products v2 parameter table). Whether a stock fall from a sale, or a stock or
  price change in the seller panel, moves it is **not documented** (unknown). Live hint, 18 Nov 2026: "Fiyat ve
  Stokları Güncelle" (job 01a1f0bc, 22:17Z) sent `startDate=<PRODUCTS watermark 17 Nov 17:00:41Z less 2 min>` and got
  13 of 1,013 contents; MZ040RCY (panel 44, read 17 Nov 17:15Z by a full scan; owner: 50 on Trendyol) was not among
  them. Since 19 Nov 2026 the existing-only run reads the whole approved list without a date
  (`ProductImportScanPolicy.IncrementalReadStart`); only the plain incremental read takes the window. Source: offline
  docs "Ürün Filtreleme - Onaylı Ürün v2"; live `integration.jobs`, `integration.sync_cursors`,
  `inventory.inventory_items`, 19 Nov 2026.
- Live 14 Nov 2026 15:33Z, the whole list without status or date: `statuses "rejected=6", total 6`. The seller panel's
  "Onay Bekleyenler" held products the API names `rejected`, not `pendingApproval`. Owner: rejected ones are imported
  too. Since 14 Nov every unapproved row is imported; its `status` becomes the imported listing profile's state
  (`pendingApproval` -> APPROVAL_PENDING "Onay Bekliyor", `rejected` -> REJECTED "Reddedildi"; another or none is
  unknown and writes nothing), and a rejected row's reasons are each `rejectReasonDetails[].rejectReason` (its
  `rejectReasonDetail` when the title is empty), joined by " · ", kept in `LastRejectionCode` and shown in the products
  list tooltip and the editor ("Red nedeni: …"). No reason is made up for a row without one. When a later read finds the
  code on the approved list the state goes back to UNKNOWN; panel-made profiles are never touched
  (`ImportedApprovalStates`, `TrendyolJsonMapper.UnapprovedStatus/RejectionReasons`). Source: offline doc "Ürün
  Filtreleme - Onaysız Ürün v2" sample response; live worker log of job at 15:33Z, 14 Nov 2026.
- An unapproved row has no `contentId`, so the import links the product by its `productMainId` (live MZ056: product
  link `MZ056`, variant links = barcodes). Such a product is not on sale: a variant whose listing is REJECTED or
  APPROVAL_PENDING does not count as matched (products list, publish card, filters, bulk publish), and a model group
  with any refused card shows red. A Trendyol contentId is numeric, so a non-numeric product link means "not approved":
  its update and its Yayınla go whole to `unapproved-bulk-update` (the service Trendyol names for sending a rejected
  product back to review), never a second create (`ProductUpdateComposer.ApprovedContentId`,
  `CatalogService.IsTrendyolUnapprovedImportAsync`). Source: offline doc "Ürün Filtreleme - Onaysız Ürün v2"
  ("gerekli güncellemeleri yapmanız halinde, ürününüz tekrar onay sürecine girecektir"); live SQL 14 Nov 2026.
- Not matched is not "not there" (15 Nov 2026): a REJECTED / APPROVAL_PENDING card is still on Trendyol, so every
  write path reaches it through `unapproved-bulk-update`: the editor's family update (Hızlı Güncelle / Platformlarda
  Güncelle) sends it instead of naming it "bu mağazada yok" (`family-update-plan.ts`, `trendyol-unapproved.ts`), the
  product list's bulk update sends it its product information (no price/stock job), Yayınla re-sends any card with a
  held listing and no numeric content link as an update, and a held variant link never makes a card a "split content"
  (`ProductUpdateComposer.HeldVariantIdsAsync`). Live 14 Nov 22:00Z: a family update of MZ056 queued 9 cards and left
  out the rejected Kahverengi/Mürdüm card. The unapproved update carries product information only: no
  `quantity` / `listPrice` / `salePrice` / `currencyType`, and no field the card has no value for, since Trendyol
  deletes a field sent as null (`ProductUpdateComposer.UnapprovedUpdatePayload`). Source: offline
  `trendyol-api-son.md` ~9036 "Ürün Güncelleme - Onaysız Ürün v2".
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
  PASSIVE; payload has `orderCountryCode`, no storefront. The stream stays the source of truth. The list
  (`GET webhook/sellers/{id}/webhooks`, offline docs `trendyol-api-son.md` getWebhook ~6280: `id`, `url`, `status`)
  returns each webhook's `url`, so an opening that timed out is settled by the
  route token in it (adopted or removed; SKILL.md, 13 Nov 2026).
- Webhook passive/active services: PUT `webhook/sellers/{id}/webhooks/{webhookId}/deactivate` and `.../activate`, both
  answer 200 OK (offline docs `trendyol-api-son.md` 174-175 list, ~6430-6470 "Webhook Aktife/Pasife Alma"). Owner,
  13 Nov 2026: Pasife Al sets the connection's webhook passive with deactivate (same id and address, row PAUSED);
  activation calls activate and opens the same row again. A webhook Trendyol no longer has (404) or that is refused (4xx)
  on activate is removed (404 skipped) and a new one is made; a quota, network or 5xx failure keeps it PAUSED and the
  hourly upkeep activates the same one again. A replaced passive webhook is deleted at Trendyol before its row closes
  (one that cannot be deleted stays PAUSED with a pending record, retried hourly). Webhook'u Kapat and Bağlantıyı Sil delete a passive one too
  (`InstantNotificationService.PauseForInactiveConnectionAsync`, `ActivatePausedTrendyolAsync`, `DisableAsync`).
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
- Catalog import and brands (owner decision 12 Nov 2026, replacing the 28 Oct rule): a listing carries `brand.id` and
  `brand.name` (`TrendyolJsonMapper.ProductSnapshot`), and the import maps it like the category: the brand mapping on
  the connection for that id (re-verified if it was not VERIFIED); else the panel brand with the same catalog key
  (`CatalogKeyNormalizer`), mapped now; else a new panel brand under the listing's name, mapped now (VERIFIED,
  `brand_mappings.Source = IMPORT`, SnapshotId = the current BRANDS snapshot, or empty when none was read). No
  marketplace call is made for it (no per-brand lookup; the list is synced from the mapping page only). Left for the
  user (BRAND_MAPPING_MISSING issue, `GET /reference-data/brands/unmapped`, Marka Eşleme card 2): a mapping whose panel
  brand is switched off, a brand id mapped to two panel brands, a same-named panel brand that is switched off (product
  without brand) or already mapped to another id on the connection (product takes it, no mapping written). Applied in
  full scans and "Yeni Ürünleri Aktar" for products they write; a run that only adds sizes to a linked card reads an
  existing mapping and writes nothing; the focused price/stock path and Ürün Eşleme never touch brands. An existing
  product the run preserves (local edits, Mevcut Ürün Bilgilerini Güncelle off) keeps its brand. It does not change the
  panel product's or variant's status either; the listing's sale state stays on its link (`SaleState`) and offer.
  Source: `MarketplaceJobProcessor.ImportBrandId`, `ImportedBrandRule`.
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
- The unapproved list (`products/unapproved`) carries no `contentId` and no `variantId`, so a card imported from it is
  linked under its `productMainId` and each variant under its barcode. Those links are placeholders: once the card is
  sent again and approved, the approval check replaces them with the numeric `contentId` / `variantId` it reads (or drops
  the product link when the content is a sibling card's of the same model); a link already holding a numeric id that
  differs still stops for review (`ProductApprovalReconciliationPolicy.IsTrendyolPlaceholderId`). Source: live MZ056
  Mürdüm/Kahverengi, approval job 01a1dc5a-d922-7bf6-84b0-dd4d2e49fc6d went to PRODUCT_APPROVAL_IDENTITY_CONFLICT on
  14.11.2026 because the product link was "MZ056".
- A product-create line refused as "Aynı barkodlu bir ürününüz bulunduğundan…" is not a refusal: it stays
  CREATE_RESULT_UNKNOWN and the approval check reads the barcode; the same now holds for any marketplace's
  "zaten mevcut/kayıtlı/var", "already exist" wording (`ProductBatchResultPolicy.SaysBarcodeExists`, report item 14).
- Product `images[].url`: https only, at most 8 per barcode, pictures 1200x1800 at 96 dpi (offline docs
  `trendyol-api-son.md`, product create/update "images"). We send our own pictures as
  `https://panel.ravencia.com/api/v1/public/product-media/{assetId}/content` (`ProductPublicationComposer`). While a
  product waits for approval the seller panel shows these URLs from partner.trendyol.com itself, so the response must
  carry `Cross-Origin-Resource-Policy: cross-origin` or Chrome shows broken pictures there (live 2026-11-06, MZ028IH-10
  Zümrüt: two empty picture slots; `deploy/caddy/Caddyfile.production`).

## Micro export flags (16 Nov 2026)
- The order package carries `micro` (true for a micro-export order; independent of `countryCode`) and `3pByTrendyol`
  (Trendyol export partnership: when true, `micro` is false and `invoiceAddress` holds Trendyol's company). Both are
  package top-level fields (offline docs `trendyol-api-son.md` ~l.403, 802, 3822, 4100).
- Live 16 Nov 2026 (read-only SQL): 216 of 1845 Trendyol orders have `micro: true`, always with `3pByTrendyol: false`;
  none has `3pByTrendyol: true`, `microExport`, a micro `shipmentPackageType`/`orderType`, or `micro: true` anywhere but
  the top level. The 216 are exactly the SA/AZ/BG/RO/GR/AE storefront orders.
- The panel's one rule is `OrderMicroExportRules` (any positive indicator, any field order); the invoice keeps its own
  `InvoiceIssuePolicy.IsMicroExport`.

## Job types
TRENDYOL_CONNECTION_TEST, _REFERENCE_SYNC, _ORDER_SYNC (60 s), _ORDER_RECOVERY_SYNC (15 min),
_ORDER_CARGO_INFO_RECONCILIATION, _FINANCE_SYNC (every 6 h since 4 Nov 2026, `MarketplaceSync:TrendyolFinance:IntervalSeconds` 21600 in `ScheduledJobProducer`; 90-day first read, 3-day overlap, 15-day windows),
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

## Model link (Model Kaydı Faz 2, 14 Nov 2026)
- `catalog.marketplace_model_links.ExternalId` = the `productMainId` of the import's rows (one content per colour, the
  model groups them); card links stay contentIds. Written after each product import/match from the variant links just
  saved (`MarketplaceModelLinks.ModelListings` / `RecordAsync`), never backfilled (productMainId was stored nowhere).
- Create payload `productMainId` (`ProductPublicationComposer.ProductMainIdFor`, owner 14 Nov 2026 "eşlenmiş olduğuna
  eklesin"): a card with nothing on Trendyol and never sent takes the verified model link's productMainId even when the
  panel model code differs, so Trendyol adds it as a new colour of the linked product. A card already on Trendyol (product
  or variant link) keeps the panel code, or the link's spelling when it is the same code key (Trendyol compares
  case-sensitively: "Ryp003t" / "RYP003T"); a card sent before but not linked yet keeps the code its last create job
  carried. No link, a link no variant link holds, a code over 40 characters or an open `PRODUCT_MODEL_AMBIGUOUS` for
  the model's key: the panel model code. (Since 17 Nov 2026 a model is brand + model code, the same code under two
  brands is two models, `ProductModelLinks` resolves open `PRODUCT_MODEL_AMBIGUOUS` issues and opens none, so the last
  case no longer occurs; the composer check stays.)
- The composer reads the model code from the model record (KATALOG-14 D5b, as the Hepsiburada, n11 and Pazarama ones do). The update composer builds on the same payload, so an update never moves a
  live card's productMainId.

## Claim-built orders (15 Nov 2026, ADAPTOR-TY-HB-1)
- getClaims carries `orderDate` and each `orderLine.price` but **no currency field** (trendyol-api-son.md, getClaims
  sample around :6830). An order built from a claim (`TrendyolJsonMapper.OrderFromReturnClaim`) takes lira when the
  claim names no currency, refuses an unknown one, and builds no order without `orderDate` (the claim date is not the
  order date) or with a line whose price is unreadable (never priced 0); the claim then waits as RETURN_ORDER_NOT_FOUND
  until the order itself is read. Live 15 Nov 2026: 20 Trendyol orders still carry the RETURN_CLAIM mark.

## Approved product update: what a batch SUCCESS says (18 Nov 2026)
- `content-bulk-update` (Ürün Güncelleme - Onaylı Ürün v2, Content): partial update (only `contentId` required),
  attributes all or none, a field sent as `null` is deleted, "slicer/varianter" attributes, barcode, productMainId,
  brandId and categoryId cannot change; the result is read with `getBatchRequestResult` by `batchRequestId`.
  Source: offline `trendyol-api-son.md` ~9106-9130.
- The documentation says nothing of whether the changed content is reviewed before it shows on the storefront, and it
  does not define a batch item `SUCCESS` as "applied on the storefront". Only product creation is said to enter the
  approval process ("ürün onay sürecine girer", ~1304 / ~8426). The panel itself sets a listing to `APPROVAL_PENDING`
  after an update (`ProductJobService.CompleteProductUpdate`) and polls approval.
- So a value accepted by a batch `SUCCESS` is recorded only as "last sent" (`MarketplaceKnownListingValueStore`, Source
  SENT) and the update summary never calls a panel value equal to it "no change" (state `SENT_SAME`, "Son Gönderilenle
  Aynı · Pazaryerinde Uygulandığı Bilinmiyor"); only a value read back from the catalog (Source IMPORT) is.

## Exchange claims ("Değişim", 18 Nov 2026)
- getClaims has no claim-type field and no refund/exchange request field. Its only documented exchange datum is
  `replacementOutboundpackageinfo` ("Değişim giden paket bilgisi (değişim yoksa boş)": cargo, `packageid`, `items` =
  claim item ids; trendyol-api-son.md:6850-6858 sample, :17450-17478 schema). The "İade Sebepleri" table
  (trendyol-api-son.md:6996-7037) lists ids and names only, no `code` strings and no customer exchange reason; 2006
  "Analiz Değişim" is a Trendyol (analysis) reason. The seller's reject reasons 2101 "Sipariş sorusundan gelen değişim
  talebi" and 1851 "Ürün değişimi yapıldı" (getClaimsIssueReasons, :7150-7162) are what the seller sends, not what the
  claim carries.
- Live code: `customerClaimItemReason.code` WRONGCHANGEREQUEST (customer note "Trendyol Oto Not: Müşteri, yanlış ürünün
  değişimini istiyor."), order 11805950639, claim of 12 Nov 2026, WaitingInAction; the owner called it an exchange
  (18 Nov 2026). It is the only one among 255 live Trendyol claims (18 Nov 2026: BIGSIZE 100, SMALLSIZE 68, DISLIKE 51,
  UNDELIVERED 9, WRONGORDER 6, ABANDON 6, WRONGITEM 6, UNDELIVEREDINT 3, OTHER 2, DAMAGEDITEM 2, FAKEPRODUCT 1).
  The claim's raw JSON is not stored, so whether live claims carry `replacementOutboundpackageinfo` is **unverified**.
- Code: `ReturnCustomerRequestPolicy.FromTrendyol` sets `CustomerRequest` CHANGE for that code only; a replacement package
  may cover one item, so it never makes the whole claim an exchange (review, 18 Nov 2026), and the reason text is never
  searched. Shown as the "Değişim Talebi" tag; the reason reads "Yanlış gelen ürünümü değiştirmek istiyorum" (Trendyol's
  seller panel). Nothing infers "exchange made": `ResolvedWith` stays null on Trendyol unless the owner marks it.
- Invoice: only Hepsiburada's exchanges take no units back (`ExchangeNoReturnPlatform`). A Trendyol exchange request
  Trendyol accepted (48-hour auto-acceptance, an approval in its panel) may be a refund, so it counts as a return as
  before; its detail tells the owner to check in Trendyol's panel. The reports also count exchange requests apart, for
  information (`ReportSummaryView.ExchangeClaims`).
- No exchange action in the API: Trendyol's seller panel handles it with "Değişim Yap" (owner's screenshot, 18 Nov
  2026). approveClaimLineItems (trendyol-api-son.md:7044-7084, "İade Siparişleri Onaylama") is documented for returns
  only, so the panel never approves an exchange request (`ReturnCustomerRequestPolicy.ApprovalBlocked`, error
  TRENDYOL_EXCHANGE_APPROVE_IN_SELLER_PANEL; no bulk selection; a queued approval is not sent). createClaimIssue stays.
- A made exchange comes back **Rejected** (11805950639, exchanged with "Değişim Yap", live Rejected 13:55Z 18 Nov 2026);
  a rejected claim never counts as a return. The owner (owner or admin) marks it "Değişim Onaylandı Olarak İşaretle"
  (POST `returns/{id}/exchange-approved`, panel only): ResolvedWith CHANGE, `ResolvedWithSource` MANUAL, audit
  RETURN_EXCHANGE_MARKED_APPROVED. It then lists in Onaylananlar (wire EXCHANGE_APPROVED, TabGroup APPROVED; one rule in
  `ReturnQueryService.ReturnStatusWire`, repeated by `ReturnCardColumns` and the tab filters/counts), leaves the
  refused-return cards and keeps Stoğa Ekle. "İşareti Kaldır" undoes it; a read that shows the claim no longer rejected
  drops it and opens RETURN_EXCHANGE_MARK_LAPSED.
- An open claim is read again by RETURN_STATUS_SYNC; a closed one only while RETURN_SYNC / RETURN_RECONCILIATION
  (3/90 days by lastModifiedDate) brings it back. `CustomerRequest` is kept once known.
- Exchange resend (owner, 18 Nov 2026): Siparişler › Yeniden Gönderimler listed only packages with `createdBy`
  transfer/resend/replacement and an origin package (`OrderQueryService.OrderList` RESENT, `ShipmentPackageClassification`),
  which Trendyol's order reads never return (11 Nov diagnostic above), so 11805950639's exchange was not there. Now the
  claim's `replacementOutboundpackageinfo` (package id not 0, carrier, tracking number and link) is stored on the claim
  (`ReturnClaim.Replacement*`, kept once read) and an order is in that tab when its exchange claim names the package or
  the owner marked it "Değişim Onaylandı"; it waits for the carrier until the package's tracking link is known. The
  row reads "Değişim". Whether live claims carry the block is **unverified**: the worker logs one Information line per
  exchange claim read ("replacementOutboundpackageinfo present=…, package id=…", no content).
- Exchange package as an order package (18 Nov 2026): the 11 Nov diagnostic asked v2 `shipmentPackageIds=<resend>,<first>`
  with a date window and got only the first package; whether an exchange package (11805950639 → 4360616611) comes back
  is **unverified**. After a Trendyol recovery read each exchange package is read once read-only
  (`OrderReadService.RunExchangePackageDiagnosticOnce`, same queries as the 11 Nov one) and an Information line says per
  query whether the answer holds the package id ("Exchange package … diagnostic: …=200:package/none"); answers, buyer
  fields removed, are kept as a resolved TRENDYOL_EXCHANGE_PACKAGE_DIAGNOSTIC issue. Nothing is written to orders or
  packages; until it is known, the Yeniden Gönderimler row shows the claim's package data (no label print, no invoice
  actions on it).
