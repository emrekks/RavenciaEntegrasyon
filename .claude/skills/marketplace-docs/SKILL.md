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

## Official docs, offline (search these first)

Full copies of the official developer docs, downloaded 2026-11-01, are kept outside the repository (this repository
is public; never commit them) in `C:/Users/emrek/OneDrive/Belgeler/Ravencia-API-Docs/`:

| Marketplace | File |
|---|---|
| Trendyol | `trendyol-api-son.md` |
| Hepsiburada | `hepsiburada-api-son.md` |
| Pazarama | `pazarama-api-son.md` |
| PttAVM | `pttavm-api-son.md` |
| Shopify | `shopify-admin-graphql-referans-son.md` (GraphQL Admin 2026-10), `shopify-rehberler-son.md` (guides) |

They are 0.1-1.8 MB each: grep for the endpoint, field or heading and read around the hit; never read one whole.
Look here before WebSearch/WebFetch; go to the web only when the answer is not in them or may have changed, and say
which you used. N11 has no offline copy.

**Trendyol: we use the v2 product services.** Its doc keeps v1 and v2 pages side by side (`## Ürün Yaratma v2`,
`## Ürün Filtreleme - Onaylı Ürün v2`, `## Kategori Özellik Listesi v2`, `## Paket Listeleme V2`…); read the v2
page, and treat a v1 page as history unless the code still calls that v1 endpoint.

## How to use and keep these files

- Numbers here were last checked on 2026-10-31. The code is the authority for what we do; the file says what the
  marketplace allows. When they differ, the file says so.
- Facts marked **unverified** were not confirmed in the official docs. Check them on the web before relying on them,
  and update the file with what you find and its URL.
- When you learn a new marketplace fact (a limit, a quirk, a changed endpoint), add it to the right file with its
  source in the same commit as the code change.
- Hepsiburada and PttAVM developer portals return 403 to WebFetch; use WebSearch snippets, the changelog pages or
  ask the user to open the page in the browser.
