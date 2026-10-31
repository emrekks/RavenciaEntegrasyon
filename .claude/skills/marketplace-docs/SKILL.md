---
name: marketplace-docs
description: The marketplace API facts this project already knows - base URLs, auth, rate limits, order-query windows, page sizes, time zones, endpoints, status mappings, quirks and job types for Trendyol, Hepsiburada, N11, Pazarama, PttAVM and Shopify, each with its source in the code or official docs. Read before searching the web for a marketplace API detail, before changing an adapter, a sync window or a quota, and when explaining to the user why a marketplace behaves as it does.
argument-hint: "[trendyol | hepsiburada | n11 | pazarama | pttavm | shopify]"
---

# Marketplace API facts

One file per marketplace, next to this one. Read the one you need, not all of them:

| Marketplace | File | Protocol |
|---|---|---|
| Trendyol | `trendyol.md` | REST, Basic auth |
| Hepsiburada | `hepsiburada.md` | REST on several hosts, Basic auth |
| N11 | `n11.md` | REST + SOAP, appkey headers |
| Pazarama | `pazarama.md` | REST, OAuth2 client credentials |
| PttAVM | `pttavm.md` | SOAP (WCF), WS-Security |
| Shopify | `shopify.md` | GraphQL Admin API |

Shared across all of them:
- Job type constants: `src/MarketplaceHub.Application/MarketplaceContracts.cs` (`ForPlatform()` maps the shared
  types such as PRICE_INVENTORY_SYNC, SHIPMENT_ACTION, RETURN_ACTION to the platform ones).
- Package status mapping: `ShipmentPackageStatusPolicy.FromRemote` in `src/MarketplaceHub.Domain/OrderModels.cs`
  (Hepsiburada has its own variant in the same file). Anything unknown goes to ManualReview.
- Quotas: one PostgreSQL token bucket shared by the API and every worker (`integration.take_rate_token`,
  `Adapters/SharedRateLimiter.cs`), keyed `"{platform}:{account-hash}:{bucket}"`. If it is unreachable traffic
  continues and the marketplace's own 429 becomes a retry.
- Time: a text timestamp without an offset is Turkey time, UTC+3 with no daylight saving (`Adapters/TurkeyTime.cs`).
  Trendyol and n11 epoch-ms dates are documented as GMT+3 but are deliberately sent and read as plain UTC epochs.
- Sync windows and lookbacks live in `SynchronizationPolicies.cs`; quotas and URLs in each adapter's `*Options.cs`.

## How to use and keep these files

- Numbers here were last checked on 2026-10-31. The code is the authority for what we do; the file says what the
  marketplace allows. When they differ, the file says so.
- Facts marked **unverified** were not confirmed in the official docs. Check them on the web before relying on them,
  and update the file with what you find and its URL.
- When you learn a new marketplace fact (a limit, a quirk, a changed endpoint), add it to the right file with its
  source in the same commit as the code change.
- Hepsiburada and PttAVM developer portals return 403 to WebFetch; use WebSearch snippets, the changelog pages or
  ask the user to open the page in the browser.
