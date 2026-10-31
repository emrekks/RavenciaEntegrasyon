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
- Code buckets per account: oms 600/min, catalog (mpop) 180/min, listing 100/min, asktoseller 60/min (undocumented).
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

## Statuses (own mapping in `OrderModels.cs`)
OPEN/UNPACKED → New; PROCESSING → Processing; PACKAGED/INVOICED/READY_TO_SHIP → ReadyToShip; SHIPPED/INTRANSIT →
Shipped; DELIVERED; UNDELIVERED → **OnHold**; CANCELLED* (BYMERCHANT/BYCUSTOMER/BYSAP) → Cancelled; RETURNED.
A line in `ClaimCreated` must not put the order on hold.
Claims: NewRequest, AwaitingAction, InDispute, Accepted, Rejected, Refunded, Cancelled, AwaitingPreApproval.

## Quirks
- The listing service calls the SKU `hepsiburadaSku`, not `hbSku`; stage uploads map merchantSku to hbSku first.
- The live-proven invoice PUT body sends a MIME `contentType` and no serialNumber/rowNumber; the schema copy differs
  and was never confirmed.
- No single-claim GET; action results are confirmed by the periodic status scan.
- Webhooks: Basic auth only; answer within 5 s — more than 20 slow answers in an hour blacklists the feed. Changes
  made through the API are not echoed back as webhooks.

## Job types
HEPSIBURADA_CONNECTION_TEST, _REFERENCE_SYNC, _PRODUCT_SYNC, _ORDER_SYNC, _ORDER_RECOVERY_SYNC (also reads packages,
cursor `PACKAGES`), _ORDER_STATUS_SYNC, _WEBHOOK_INGEST, _RETURN_SYNC, _ORDER_INVOICE_RECONCILIATION.
