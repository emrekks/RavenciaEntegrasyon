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

## Statuses
- Item codes: 3 Created; 12 Picking; 18 CancelRequested (OnHold); 6/13 Cancelled; 5/16/19 Shipped; 11/7/9 Delivered;
  14 Undelivered; 8/10 Returned.
- Refund codes: 1 WaitingInAction; 2/4/6/8 Accepted; 3/5 Rejected; 7 Cancelled.

## Quirks
- Envelope `{data, success, messageCode, message, userMessage}`; money fields are objects (use `value`); ids are GUIDs.
- A price or stock equal to the current value is refused — send only changed values.
- A discount of 70% or more goes to approval. Price/stock results can take up to 4 h; product batch results are
  queryable for 4 h.
- Orders without a package get a virtual package `order:{orderNumber}`.

## Job types
PAZARAMA_CONNECTION_TEST, _REFERENCE_SYNC, _ORDER_SYNC (60 s), _ORDER_RECOVERY_SYNC (15 min), _ORDER_STATUS_SYNC,
_FINANCE_SYNC (issue code PAZARAMA_FINANCE_SYNC_FAILED).
