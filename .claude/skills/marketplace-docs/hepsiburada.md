# Hepsiburada

Docs: https://developers.hepsiburada.com/tr/companies/hepsiburada (older: `/hepsiburada/reference/...`,
changelog `/hepsiburada/changelog/...`). Both return 403 to WebFetch; use WebSearch snippets or the browser.

Code: `Adapters/Hepsiburada/` — `HepsiburadaHttpClient.cs`, `HepsiburadaPackageOperations.cs`,
`HepsiburadaQuestions.cs`, `HepsiburadaJsonMapper.cs`, `HepsiburadaAuthenticationHandler.cs`,
`HepsiburadaRateLimitHandler.cs`, `HepsiburadaWebhookVerifier.cs`, `HepsiburadaInvoiceDeliveryPolicy.cs`.

## Connection
| Service | Prod | SIT |
|---|---|---|
| OMS (orders, packages, claims) | `https://oms-external.hepsiburada.com/` | `https://oms-external-sit.hepsiburada.com/` |
| Test order stub | — | `https://oms-stub-external-sit.hepsiburada.com/` |
| Listing (price, stock, shipping) | `https://listing-external.hepsiburada.com/` | `https://listing-external-sit.hepsiburada.com/` |
| Catalog | `https://mpop.hepsiburada.com/product/` | `https://mpop-sit.hepsiburada.com/product/` |
| Questions | `https://api-asktoseller-merchant.hepsiburada.com/` | `...-sit...` |

- `Authorization: Basic base64(integratorUsername:serviceKey)`, `User-Agent` = integrator name, merchantId =
  `ExternalStoreId` in the path. If the username fails the code retries with merchantId as username and saves it.
- Service key: Satıcı Paneli › Bilgilerim › Entegrasyon › Entegratör Bilgileri.
- `Hepsiburada:AuthenticationMode` defaults to `UNVERIFIED` (blocks calls); production sets `BASIC`.
- ticket-api (product update) names Bearer auth; a 401 there is `HEPSIBURADA_PRODUCT_UPDATE_AUTH_REJECTED`.

## Limits
- Code buckets: catalog (mpop) 180/min is one bucket per egress IP shared by every account (`hepsiburada:egress:catalog`;
  official docs, Katalog Ürün Giriş Önemli Bilgiler: "180 istek/1 dakika" for each IP); if the shared limiter is down a
  catalog call waits 1 s per call per process instead of going out unthrottled. Per account: oms 600/min, listing
  100/min, asktoseller 60/min (undocumented). Money text in the catalog uses a decimal comma ("130,50").
  (`HepsiburadaRateLimitHandler`, 2026-11-05)
- Repo docs say orders 1000/s and catalog 180/min per IP; a May 2024 changelog snippet says 10k per 10 s
  (**unverified** directly). Jan 2026 changelog: catalog burst measured in 5 s windows, excess blocks the IP and
  unblocking needs a ticket with a static IP (number **unverified**).
- Reads `X-RateLimit-Limit/Remaining/Reset` and `Retry-After`; retry waits Retry-After, else the reset time.
  Request id header `X-Request-Id`. Timeout 30 s.
- Seller cancellations max 100 per day. Inventory upload: adapter caps 1000 items ("4000 was never confirmed");
  `MarketplaceJobProcessor.cs` still says 4000.

## Windows and paging (offset + limit, always both)
- Paid orders `GET orders/merchantid/{m}`: `begindate`/`enddate` max 24 h (else `HEPSIBURADA_ORDER_WINDOW_TOO_LARGE`),
  limit ≤ 100, no dates = last day only. Imports only OPEN/UNPACKED orders inside 1 month.
- Packages `GET packages/merchantid/{m}`: 24 h window, limit ≤ 10, **Open packages only**. Shipped / delivered /
  undelivered come from `.../{status}` with limit 50 and a 1-month lookback (**unverified** on the web).
- Cancelled orders and missing-invoice lists: limit ≤ 50; missing-invoice has no date filter, covers the last month.
- Claims `GET claims/merchantId/{m}/status/{Status}`: limit ≤ 100; `beginDate`, `endDate`, `statusBeginDate`,
  `statusEndDate`. Status must be PascalCase ("Wrong Claim Status" otherwise). First read 30 days; incremental passes
  look back ≥ 2 days because the status filter misses claims; first pass each hour is full.
- Order history first read: 1 month (the package feeds only cover a month). Catalog `page` from 0, `size` ≤ 100.

## Time
Istanbul local time without an offset (`2026-10-19T14:19:58`), parsed as UTC+3. Queries sent as `yyyy-MM-dd HH:mm`
Turkey time (`HepsiburadaJsonMapper.QueryTime`). Migration `20261019140000_CorrectHepsiburadaTurkeyTimes` fixed old rows.

## Endpoints
| Purpose | Call |
|---|---|
| Orders / detail / cancelled | GET `orders/merchantid/{m}`, `.../ordernumber/{n}` (has `hasInvoice`), `.../cancelled` |
| Packages | GET `packages/merchantid/{m}`, `.../{status}`, `.../packagenumber/{p}`, `.../missing-invoice`; POST `packages/merchantid/{m}` (create) |
| Package actions | POST `.../packagenumber/{p}/unpack`; GET `.../changablecargocompanies`; PUT `.../changecargocompany`; GET `.../labels?format=zpl|base64zpl|pdf|png|jpg` |
| Packageable lines | GET `lineitems/merchantid/{m}/packageablewith/lineitemid/{id}` |
| Invoice link | PUT `packages/merchantid/{m}/packagenumber/{p}/invoice`, then read back until `INVOICED` (2, 3, 5 s) |
| Claims | GET `claims/merchantId/{m}/status/{s}`; POST `claims/number/{n}/accept`, `/preapprovalconfirm`, `/reject` |
| Listings, price and stock | GET `listings/merchantid/{m}`; POST `.../inventory-uploads` (+ GET `/id/{id}`); POST `.../shipping-info-uploads` |
| Product import | POST `api/products/import?version=1` (multipart → trackingId); GET `api/products/status/{trackingId}?version=1` |
| Product update | POST `/ticket-api/api/integrator/import?version=1`; GET `/ticket-api/api/integrator/status/{id}` |
| Categories | GET `api/categories/get-all-categories?leaf=true&status=ACTIVE&available=true&version=1`, `api/categories/{c}/attributes?version=2`, `.../attribute/{a}/values?version=5` |
| Questions | `api/v1.0/issues[/{id}[/answer|/reject]]` (1 waiting, 2 answered, 3 rejected, 4 expired) |
| Stage test order | POST `orders/merchantId/{m}` on the stub host |

## Sale state (T2-D30, 2026-11-05)
- Listing API `isSalable` ("Listingin satış durumunu belirtir. True: satışta / False: satışta değil"), with
  `isSuspended`, `isLocked`, `isFrozen`, `deactivationReasons`. Source: offline doc `#### Listing Bilgilerini Çekme`.
- Live reasons (import staging 2026-10-22, 2,590 listings): `StockIsLessThanOrEqualToZero` alone (1,634),
  `PriceIsLessThanOrEqualToZero`+stock (47), `ByMerchant`+stock (1); `isSalable=false` exactly when stock is 0.
  Code: `HepsiburadaJsonMapper.SaleStateReason`.

## Statuses (own mapping in `OrderModels.cs`)
OPEN/UNPACKED → New; PROCESSING → Processing; PACKAGED/INVOICED/READY_TO_SHIP → ReadyToShip; SHIPPED/INTRANSIT →
Shipped; DELIVERED; UNDELIVERED → Undelivered (Teslim Edilemedi, as elsewhere; it was OnHold/Askıda before 5 Nov 2026,
so a sale invoice was not stopped); CANCELLED* (BYMERCHANT/BYCUSTOMER/BYSAP) → Cancelled; RETURNED.
An order without packages whose lifecycle status is unknown (or a new order read without one) is MANUAL_REVIEW, never
NEW; a read of a known order carrying no status keeps its status; the lifecycle sweep re-reads unpackaged
MANUAL_REVIEW orders. Code: `HepsiburadaOrderLifecycleStatusPolicy.UnknownUnpackagedStatus`.
Defaults kept on purpose: a package of the open-packages list without a status is "Open" (the list returns only open
packages), and a paid-list order without paymentStatus is "Received" (only Shopify uses the payment status).
A line in `ClaimCreated` must not put the order on hold.
Claims: NewRequest, AwaitingAction, InDispute, Accepted, Rejected, Refunded, Cancelled, AwaitingPreApproval.

## Quirks
- The listing service calls the SKU `hepsiburadaSku`, not `hbSku`; stage uploads map merchantSku to hbSku first.
- The live-proven invoice PUT body sends a MIME `contentType` and no serialNumber/rowNumber; the schema copy differs
  and was never confirmed.
- No single-claim GET; action results are confirmed by the periodic status scan.
- Accounting record (`transactions/merchantid`): `id`, `amount {value, currencyCode}` (949 = TRY), `isIncome`, and
  the days `invoiceDate` / `orderDate` / `dueDate` / `paymentDate` (Istanbul time). Source:
  `HepsiburadaFinance.cs` and the guide's Kayıt Bazlı Muhasebe Servisi. A record without `id`, a readable
  `amount.value` or any of invoice/order/due day is not stored and opens a FINANCE_RECORD_INCOMPLETE issue; the rest
  of the 60-day read goes on (before 5 Nov 2026 one such record failed the whole job).
- Money objects carry `"currency": "TRY"` (e.g. `hepsiburada-api-son.md:8371`, `:12479`). Since 5 Nov 2026 an order
  with another, unknown currency value is refused with an issue and read again; a read without any currency is lira.
  The paid-order list (one item per line) no longer fails the page on one unreadable line: that order is read by
  number instead (its detail's packages count the line), and an order without its own `orderDate` is refused, never
  dated by a package event. With an unreadable line the order keeps Hepsiburada's stated amount instead of the lines'
  sum (`HepsiburadaHttpClient.PollAsync`, `HepsiburadaJsonMapper.PaidOrderLine`, `MarketplaceJobProcessor`).
- Webhooks: Basic auth only; answer within 5 s — more than 20 slow answers in an hour blacklists the feed. Changes
  made through the API are not echoed back as webhooks.

## Job types
HEPSIBURADA_CONNECTION_TEST, _REFERENCE_SYNC, _PRODUCT_SYNC, _ORDER_SYNC, _ORDER_RECOVERY_SYNC (also reads packages,
cursor `PACKAGES`), _ORDER_STATUS_SYNC, _WEBHOOK_INGEST, _RETURN_SYNC, _ORDER_INVOICE_RECONCILIATION.
