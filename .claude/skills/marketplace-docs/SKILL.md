---
name: marketplace-docs
description: The marketplace API facts this project already knows - base URLs, auth, rate limits, order-query windows, page sizes, time zones, endpoints, status mappings, quirks and job types for Trendyol, Hepsiburada, N11, Pazarama, PttAVM, Shopify and the Trendyol E-Faturam invoice provider, each with its source in the code or official docs. Read before searching the web for a marketplace API detail, before changing an adapter, a sync window or a quota, and when explaining to the user why a marketplace behaves as it does.
argument-hint: "[trendyol | hepsiburada | n11 | pazarama | pttavm | shopify | efaturam]"
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
| Trendyol E-Faturam (invoices) | `efaturam.md` | REST, JWT sign-in |

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
- Sale state (Satışta / Satışa Kapalı / unknown): every adapter fills `RemoteCatalogVariant.SaleState/SaleStateReason`
  (`ListingSaleState` in `CatalogModels.cs`); the nightly LISTING_VERIFY scan and product imports store it on
  `catalog.marketplace_variant_links` (`MarkVariantLinksSeen`). Shown beside the publication status, never part of it
  or of any price/stock rule. The field per marketplace is in its file ("Sale state").

## Listing profiles of imported products (owner decisions 6 Nov 2026)

- Who makes them: a product sync that reads the products' information (full scan, the incremental scan, Mevcut Ürünleri
  Güncelle, a single lookup) makes a `catalog.channel_listing_profiles` row with `Source = MARKETPLACE_IMPORT` for every
  product it matched (variant links), with one listing variant per linked variant (the link's barcode and stock code).
  New-only, mapping-only and options-only runs do not. "Platformlarda Güncelle" never makes one: a card without a
  profile is refused with the sync to run. Trendyol, Hepsiburada, n11, Pazarama, PTT AVM; not Shopify.
  Code: `ImportedListingProfiles.cs`, `MarketplaceJobProcessor.CompleteImportedListingProfiles`.
- The panel card wins: the profile holds no marketplace value (no title, description, delivery days, origin, warranty,
  VAT override; no category or brand id, which would stop updates as LISTING_MAPPING_CONFLICT). Profile, listing variants and one
  `marketplace_listing_states` row per listing start UNKNOWN (the update and approval jobs need the state; live 7 Nov
  2026 PRODUCT_UPDATE_STATE_INCOMPLETE on RYP003T, repaired by migration AddImportedListingStates). UNKNOWN is never
  read as live: imported price/stock keeps going by the link until a marketplace read records LIVE.
- Where an update would read the profile first, the card's value goes and the panel's notification center says so
  (`ImportedProfileCardFallbacks`): Teslimat Süresi (Trendyol, Hepsiburada, PTT AVM), Garanti Süresi (Hepsiburada; an
  empty card warranty is the card's 0). A Teslimat Süresi the card lacks too stops the update ("RYP003T – Trendyol:
  Teslimat Süresi boş; ürün kartında doldurun.").
- What each marketplace's product read returns (for reference; none of it is written to the profile):
  - Trendyol `products/approved` v2 (`trendyol-api-son.md` 10410-10500): `category.id`, `brand.id`, per variant
    `origin` (10492), `deliveryOption.deliveryDuration` (10480), `vatRate`; no desi, cargo or warranty. Unapproved
    (10033-10128): `dimensionalWeight`, `cargoProviders`, `origin`.
  - Hepsiburada listings (`hepsiburada-api-son.md` 3378-3470): DispatchTime, CargoCompany1-3, ShippingProfileName; no
    category, brand, VAT or warranty. `all-products-of-merchant` (1581, schema 2410-2480): categoryId, brand (name
    only), tax, baseAttributes.
  - n11 `ms/product-query` (no offline copy; doc sample in `N11ProductMapperTests`): categoryId, vatRate, preparingDay,
    shipmentTemplate, brand as attribute 1 (name only); no origin or warranty.
  - Pazarama product lists (`pazarama-api-son.md` 1176-1280): categoryId (may be an empty GUID), brandName (no id),
    vatRate, deliveryTypes; no days, origin or warranty.
  - PTT AVM `get-by-barcodes` (`pttavm-api-son.md` 455-512): yeniKategoriId, KDVOran, Desi, GarantiSuresi,
    KargoProfilId, TahminiKargoSuresi; no brand or origin.

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
| Trendyol E-Faturam | `efaturam-api-son.md` (direct and marketplace/partner sections, 26 endpoints, error model) |

They are 0.1-1.8 MB each: grep for the endpoint, field or heading and read around the hit; never read one whole.
Look here before WebSearch/WebFetch; go to the web only when the answer is not in them or may have changed, and say
which you used. N11 has no offline copy. E-Faturam: the remaining-credit endpoints are partner-only (`UserIsNotPartnerProblem`); a direct API user sees running out only as 409 `insufficient-credit` (see `efaturam.md`).

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
