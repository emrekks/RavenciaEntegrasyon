# Shopify

Docs: https://shopify.dev/docs/api/admin-graphql (Context7 or the Shopify MCP can also answer schema questions).
Numbers below were not re-checked on the web on 2026-10-31.

Code: `Adapters/Shopify/` — `ShopifyHttpClient.cs` (+ `.Webhooks.cs`), `ShopifyAuthenticationHandler.cs`,
`ShopifyWebhookVerifier.cs`, `ShopifyOptions.cs`, `ShopifyOrderHistoryPolicy.cs`.

## Connection
- `https://{shop}.myshopify.com/admin/api/{ApiVersion}/graphql.json`, ApiVersion `2026-10`.
- `X-Shopify-Access-Token`. New connections store an app token; legacy rows use client credentials
  (POST `https://{shop}.myshopify.com/admin/oauth/access_token`, `grant_type=client_credentials`, cached).

## Limits
- GraphQL cost: on `THROTTLED` wait (requested − currentlyAvailable) / restoreRate, ≥ 1 s, default 5 s.
  Repo doc: Standard 100 points/s, Advanced 200, Plus 1000.
- HTTP 429 → `SHOPIFY_RATE_LIMITED`, Retry-After or 5 s. Single-query cost ceiling 1000; on `MAX_COST_EXCEEDED`
  the page size is quartered down to 1. Timeout 45 s.

## Windows and paging
- Cursor paging, `first` ≤ 250 (setting 100), filter `updated_at:>='…Z'` in UTC.
- One query pages through at most 25,000 results — the code restarts after 200 pages.
- First read 3 months, daily baseline re-read, watermark − 10 min. Without `read_all_orders` only the last 60 days
  are visible.

## Time
UTC, ISO with Z.

## Operations
- Queries: `orders`, `order`, `products`, `productVariants`, `productByIdentifier`, `locations`, `publications`,
  `return`.
- Mutations: `productSet` (synchronous), `productVariantsBulkUpdate` (price), `inventorySetQuantities` (needs an
  `@idempotent` key since 2026-04), `publishablePublish`, `metafieldsSet`, `fulfillmentTrackingInfoUpdate`,
  `returnApproveRequest`, `returnDeclineRequest`, `webhookSubscriptionCreate` / `Delete`.

## Statuses
- Fulfillment: CANCELLED → CANCELLED; OPEN/PENDING/IN_PROGRESS → PROCESSING; SUCCESS/FULFILLED → SHIPPED;
  ERROR/FAILURE → FULFILLMENT_FAILED; a DELIVERED event → DELIVERED.
- Refund from `displayFinancialStatus`: REFUNDED, PARTIALLY_REFUNDED, NOT_REFUNDED.

## Quirks
- Since 2026-07 `Order.fulfillments` is a plain list.
- `returnDeclineRequest` notifies the customer only with `notifyCustomer: true`.
- Webhooks: stale `X-Shopify-Triggered-At` deliveries are dropped; 8 consecutive failures delete the subscription.
- Invoice data lives in order metafields `ravencia.invoice_number` and `ravencia.invoice_pdf_url`.
- No label API.

## Job types
SHOPIFY_CONNECTION_TEST, _PRODUCT_SYNC, _ORDER_SYNC, _ORDER_RECOVERY_SYNC, _ORDER_STATUS_SYNC, _ORDER_RECONCILIATION,
_ORDER_INVOICE_RECONCILIATION, _WEBHOOK_INGEST, _ORDER_CSV_IMPORT.
