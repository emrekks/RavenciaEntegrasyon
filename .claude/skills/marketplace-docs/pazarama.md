# Pazarama

Docs: https://isortagim.pazarama.com/auth/integration (e.g. `/iade-durum-guncelleme`,
`/saticiya-sorulan-urun-soru-ve-cevap`). Numbers below were not re-checked on the web on 2026-10-31.

Code: `Adapters/Pazarama/` — `PazaramaHttpClient.cs`, `PazaramaOrders.cs`, `PazaramaOrderMapper.cs`,
`PazaramaPackageActions.cs`, `PazaramaReturns.cs`, `PazaramaInvoices.cs`, `PazaramaFinance.cs`,
`PazaramaPriceInventory.cs`, `PazaramaProducts.cs`, `PazaramaProductWrites.cs`, `PazaramaQuestions.cs`,
`PazaramaReferences.cs`, `PazaramaVatRate.cs`, `PazaramaAuthenticationHandler.cs`, `PazaramaRateLimitHandler.cs`.

## Connection
- API `https://isortagimapi.pazarama.com/`, token `https://isortagimgiris.pazarama.com/connect/token`. No sandbox.
- OAuth2 client credentials: POST token URL with `Authorization: Basic clientId:clientSecret`, form
  `grant_type=client_credentials&scope=merchantgatewayapi.fullaccess`; then `Authorization: Bearer <token>`.
  Token lives 1 h; cached per credential, refreshed 5 min early and once on 401.
- Generating a new API key invalidates the old one at once.

## Limits
- Price/stock and product create: documented 10 s between requests per seller; code keeps 12.5 s, price and stock
  share one bucket, 90 s timeout for spaced requests.
- Orders 60/min, other reads 30/min (both undocumented guesses).
- Items per request: price/stock ≤ 3000, product create ≤ 500, VAT bulk ≤ 1000.

## Windows and paging
- `POST order/getOrdersForApi {pageSize, pageNumber, startDate, endDate, orderNumber}` filters by **order date** only.
  Max 1 month per request (code 28 days), end day exclusive; by order number it looks back 6 months. Page 100
  (docs sample uses 500). First read 3 months; incremental re-reads 2 days.
- Status changes of older orders: open orders re-read by number every 3 min until closed or 45 days old.
- Page sizes: refunds 100, approved products 100 (cursor), brands 500, price/stock batch result 1000.

- Finance `order/paymentAgreement` line: `trxCode`, `trxId`, `orderId`, `amount`, `commissionAmount` (negative on a
  sale), `allowanceAmount` (hakediş: what the payout pays), `couponDiscount`, `integrationAmount`, `merchantCargoDebt`,
  `stoppageAmount`, `status`, `transactionDate`, `transferredDate` (null until paid). Source: official docs
  `pazarama-api-son.md:3666-3716` (Muhasebe ve Finans Servisi). A line without a code, `amount`, `allowanceAmount` or
  `transactionDate` is not stored and opens a FINANCE_RECORD_INCOMPLETE issue; coupon/integration/cargo/stoppage left
  out are 0; a missing commission is stored null and the report keeps the order's commission estimate (5 Nov 2026).
  An order whose movements all carry it is costed at their net and labelled Gerçek Komisyon, no longer "tahmini"
  (`ReportMarketplaceCommissions`, 5 Nov 2026).

## Time
Turkey time without offset; finance dates sent in Turkey time with milliseconds.

## Endpoints
| Purpose | Call |
|---|---|
| Orders | POST `order/getOrdersForApi` |
| Item status (12 preparing, 13 unsupplied, 5 shipped) | PUT `order/updateOrderStatus` |
| Packages | POST `order/api/packages-split`; PUT `order/update-packages` |
| Labor costs | PUT `order/api/{orderId}/items/labor-costs` |
| Cancel requests / decision (2 approve, 3 reject) | POST `order/api/cancel/items`; PUT `order/api/cancel {refundId, status}` |
| Refunds / decision | POST `order/getRefund`, `order/updateRefund` |
| Invoice link | POST `order/invoice-link` |
| Finance | POST `order/paymentAgreement` |
| Price / stock / result | POST `product/updatePrice-v2`, `product/updateStock-v2`; GET `listing-state/batch-id/{dataId}/lake-projections` |
| Product create / result | POST `product/create`; GET `product/getProductBatchResult?BatchRequestId=` |
| Open / close sale (1 open, 10 closed) | POST `product/external-status` |
| VAT | PUT `product/vatRate/bulk` |
| Products | GET `product/products/approved`, `product/products/unapproved` |
| Reference | GET `brand/getBrands`, `category/getCategoryTree`, `category/getCategoryWithAttributes?Id=` |
| Questions | POST `QuestionAnswer/getApprovalAnswersByMerchantSearch`; GET `getApprovalAnswerById`; PUT `sellerAnswer` |

## Sale state (T2-D30, 2026-11-05)
- Approved list `productStatus`: 1 open, 10 closed, the values `product/external-status` writes (offline doc
  `## Kataloglu Ürünleri Satışa Kapatıp-Açma`). That the read field uses the same enum is **unverified** (the doc
  sample shows 0); 0 or another value stays unknown. Code: `PazaramaProductMapper.SaleState`.

## Statuses
- Item codes: 3 Created; 12 Picking; 18 CancelRequested (OnHold); 6/13 Cancelled; 5/16/19 Shipped; 11/7/9 Delivered;
  14 Undelivered; 8/10 Returned.
- Package status: an active item of unknown code makes the package ManualReview (İnceleme Gerekli), before an item
  being cancelled (18) holds it; otherwise the least advanced known item decides. Code: `PazaramaOrderMapper.PackageStatus`.
- Refund codes: 1 WaitingInAction; 2/4/6/8 Accepted; 3/5 Rejected; 7 Cancelled.

## Quirks
- Envelope `{data, success, messageCode, message, userMessage}`; money fields are objects (use `value`); ids are GUIDs.
- A price or stock equal to the current value is refused — send only changed values.
- A discount of 70% or more goes to approval. Price/stock results can take up to 4 h; product batch results are
  queryable for 4 h.
- Orders without a package get a virtual package `order:{orderNumber}`.
- Order level: `orderDate` ("2026-02-17 17:23"), `orderAmount` (a number: "Siparişin tutarı"; the field table calls it
  datetime, the sample sends 7), `discountAmount`, `shipmentAmount`, `currency: "TL"`; item money objects carry
  `currency: "TL"` too (`pazarama-api-son.md:2310-2420`). The panel reads "TL" as TRY; another value refuses the order
  with an issue and a re-read; a read without the field is lira. An order with an unreadable item takes `orderAmount`
  as its amount; an order without `orderDate` is refused and read again, never dated by an item's history (5 Nov 2026,
  `PazaramaOrderMapper.cs`).
- Unapproved list (`product/products/unapproved`): "onay sürecinde olan veya reddedilen" products; the documented
  sample sends `status` and `waitingApproveExp` null (`pazarama-api-son.md:1160-1200`, no value list). The panel reads
  no text as waiting, "red…" (without "bekl…") as REJECTED, "bekl…"/"incele…" as waiting, and any other text as
  UNKNOWN (review), never as waiting (`PazaramaProductWriteMapper.Publication`, report item 13, 5 Nov 2026).
- Product create batch (status 2 done / 3 error) names its failed and, sometimes, its done products. A sent barcode
  it names neither way is no longer taken as created: it is CREATE_RESULT_UNKNOWN at once (the batch will not name it
  later) and the approval check reads it from the lists (`PazaramaProductWriteMapper.BatchStatus`, report item 14).
- Order list `data: null` is still read as an empty page (`PazaramaOrderMapper`): whether Pazarama answers an empty
  window with `null` or `[]` needs a read-only look at live logs first (report item 15, left as is on 5 Nov 2026).

## Job types
PAZARAMA_CONNECTION_TEST, _REFERENCE_SYNC, _ORDER_SYNC (60 s), _ORDER_RECOVERY_SYNC (15 min), _ORDER_STATUS_SYNC,
_FINANCE_SYNC (issue code PAZARAMA_FINANCE_SYNC_FAILED).

## Returns, invoices, buyer (stage 5, 5 Nov 2026)
- `order/getRefund` rows name the product as `productCode` (barcode) and `productStockCode` (the field table spells it
  `ProductStockCode`); `refundAmount` is an object `{value, valueInt, valueString, currency}`
  (`pazarama-api-son.md:3195-3226`, sample `:3240-3290`). Quantity: the field table has none, the SplitItems note says a
  `quantity` exists (`:2846-2850`); the panel reads `quantity` when present, else 1 for a one-unit order line, else
  refundAmount / order-line unit price when whole, else records "adet bilinmiyor" (`PazaramaReturnMapper.ResolveQuantity`).
- Refund states 1-8 are mapped explicitly; any other code is Unknown (no Onayla/Reddet, issue RETURN_STATUS_UNKNOWN).
- Invoice link: "Fatura Linki Güncelleme" (`:3540-3556`) — a new link replaces the earlier one and there is no
  read-back, so an uncertain delivery goes to the automatic retry schedule (`InvoiceDeliveryAutoRetryPolicy`).
- Buyer kind: billingAddress `invoiceType` 1 Bireysel, 2 Kurumsal (`:2305`, `:2333`).
- E-Arşiv internet sale: purchaseUrl `https://www.pazarama.com`, paymentMeans MEDIATOR.
