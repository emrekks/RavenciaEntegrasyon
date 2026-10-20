# 2 — Ürün kataloğu, yayın ve içe aktarım (K)

> origin/main 289d0b54. Ana rapor: `../PROJE-DERIN-INCELEME-RAPORU-2026-10-20.md`.

## Kritik / Yüksek

**K1. SKU, barcode and option keys normalised two different ways → duplicate variants/options**
- Where: `CatalogService.cs:2038` (`Trim().ToUpperInvariant()`, used 705-709, 714, 1038-1047, 1893-1908); `Imports/ImportJobProcessor.cs:304`; `MarketplaceJobProcessor.cs:7252-7262` (`NormalizeCatalogKey`: strips accents, non-alphanumeric → `-`, used 5694-5695, 6415-6416, 6609-6612, 7137-7150).
- All write `SkuNormalized`, `BarcodeNormalized`, option `NormalizedKey`; unique indexes only protect identical strings.
- Failure: panel SKU `ABC_01` stored `ABC_01`; Trendyol import looks for `ABC-01`, creates a second variant → stock on two variants, oversell. `Beden/Yaş` vs `BEDEN-YAS`, `Gümüş` vs `GUMUS` → two option groups. `ToUpperInvariant` keeps `İ` but maps `i`→`I`, so `İpek`/`ipek` never match; HB title-prefix check (`HepsiburadaProductPublicationComposer.cs:110`) same.
- Fix: one `CatalogKeyNormalizer` (FormD, strip marks, fold İ/ı/i→I, consistent separators) used everywhere; migration recomputing normalised columns with a collision report before rebuilding unique index; parity test.

**K2. Variant option signatures in two incompatible formats**
- `CatalogService.cs:2046` writes `KEY=VALUE|KEY=VALUE` ordered by raw key; `MarketplaceJobProcessor.cs:7206` writes `Key: Value | Key: Value`; `SameColorAndSize` (7024-7036) parses only the latter (used 5785).
- Failure: N11/Pazarama model-code mapping never resolves panel variants differing by colour/size → `PRODUCT_MAPPING_UNMATCHED`; panel signature unstable (`{Renk,beden}` vs `{renk,Beden}`).
- Fix: one builder (normalise, sort by normalised key, one separator); parser accepts both; backfill from `VariantOptionValues`.

**K3. Catalog import overwrites user-managed media**
- `MarketplaceJobProcessor.cs:6269-6274, 6434-6435` call `UpsertCatalogMedia` (~7164-7200): archives rows with `SortOrder >= urls.Count`, swaps `FileAssetId`, resets `ORDERED_*` roles. `CatalogSnapshotAlreadyApplied` (6473-6498) false once a user uploaded; `updateExistingProducts` defaults true (4392).
- Failure: user uploads 3 photos, reorders; next scan replaces them; uploads archived, order lost.
- Fix: skip media upsert when scope has `ORDERED_*` roles, uploaded `PRODUCT_MEDIA`, or `DirtyFields` contains `media`; mark link dirty on every media edit.

**K4. Hepsiburada publish always fails for URL-registered images**
- `HepsiburadaProductPublicationComposer.cs:132` requires `image/jpeg|png`; URL registration stores `image/remote` (`CatalogEndpoints.cs:381, 776`).
- Fix: MIME check only for `Classification == "PRODUCT_MEDIA"`; test.

**K5. Editing a model-code family changes siblings without protecting them** — `CatalogService.cs:1000-1003, 1044-1050, 1060, 1176, 1190, 1198`. Only primary product's Version bumped and links marked dirty; siblings' edits overwritten by next import (`MarketplaceJobProcessor.cs:6197, 6657`); a sibling can be left with zero variants (check at 1009 counts family); siblings stay ACTIVE with ARCHIVED variants. Fix: group per product, bump each, mark dirty, enforce ≥1 variant per product.

**K6. Failed publish can't be retried with same payload** — `CatalogService.cs:1266-1272, 1337-1343, 1591`; unique `(TenantId, JobType, JobDedupKey)` `AppDbContext.cs:337`. Blocked/Cancelled → new row hits unique index → old Blocked returned forever; update/archive paths (1357-1371, 1383-1395) uncaught → 500. Fix: retire terminal job's dedup key (`…:retired:{id}`), reuse only Pending/Leased/RetryScheduled, catch `DbUpdateException`.

**K7. Product list and summary load the whole catalog per request** — `CatalogService.cs:396-404, 478-490, 770-772`. Fix: indexed `FamilyKey`, SQL paging on (CreatedAt, Id), SQL EXISTS/aggregates, SQL COUNTs, targeted AnyAsync in Duplicate.

**K8. Archive destroys media; un-archive restores neither media nor variants** — `CatalogService.cs:1206-1207`; `BulkSetStatusAsync` 515-534 changes only Product.Status. Fix: archive only flags; leave media to retention; BulkSetStatus reuses per-product logic incl. variants and dirty links.

**K9. CSV/XLSX import breaks on normal Turkish files** — `Imports/ImportFileReader.cs`: line-based (multi-line quoted fields throw, 18-20, 95-99), strict UTF-8 (14), comma only (98), rejects values starting `-`/`+` (93). Fix: record-aware parser, delimiter sniffing, cp1254 fallback, neutralise formulas (`'` prefix) and reject only `=`, `@`, `+`/`-` followed by non-numeric.

**K10. Failed import stuck in "Applying"/"Matching"** — `ImportJobProcessor.cs:227-236, 112-118`: catch re-saves the same failing tracked entities. Fix: `ChangeTracker.Clear()`, reload, set Failed; batch 500 rows in a transaction.

**K11. Parallel uploads of the same image → permanent 500** — `CatalogEndpoints.cs:308` `SingleOrDefaultAsync` on non-unique `(TenantId, Sha256, Classification)` (`AppDbContext.cs:401`). Fix: FirstOrDefault by CreatedAt + partial unique index + catch-and-reload.

## Orta
- **K12.** Media edits don't bump Version or mark links dirty — `CatalogEndpoints.cs:318, 798, 802-831`; reconcile/reorder bump but don't mark dirty. Fix: shared helper adding `media` to `DirtyFieldsJson`.
- **K13.** Importer rewrites user brands/categories — `MarketplaceJobProcessor.cs:6501-6519, 6585-6604`: brands renamed/re-activated; matching root categories forced `IsLeaf=true, IsActive=true, Path=leaf` even with children; manual records use other normaliser → `TOMMY HILFIGER` vs `TOMMY-HILFIGER`. Fix: unified key, never rename/reactivate, recompute IsLeaf.
- **K14.** Barcode-less imported variants stored `""` not NULL (unverified) — `MarketplaceJobProcessor.cs:6612, 6649`; unique `(TenantId, BarcodeNormalized)` `CatalogModelConfiguration.cs:73` → second one violates index. Fix: NULL; data fix.
- **K15.** Image proxy serves any `image/*` incl. SVG from API origin (unverified) — `PublicNetwork.cs:76-77`, `CatalogEndpoints.cs:166-169`. Fix: allow jpeg/png/webp/gif, `CSP: sandbox`, nosniff.
- **K16.** Approval reconciliation polls one barcode at a time every 5 min up to 7 days — `MarketplaceJobProcessor.cs:530-546`, `CatalogService.cs:1511`; 100 variants ≈ 28.8k Trendyol calls/day. Fix: productMainId/batch query, exponential backoff, only pending barcodes.
- **K17.** Archived/deleted product images never cleaned — `CatalogService.cs:640-642, 1207`. Fix: retention sweep deleting unreferenced assets + storage file.
- **K18.** Model-code family merges unrelated products — `CatalogService.cs:912-918, 1594-1609`; `CatalogEndpoints.cs:493-510, 888-905`; SQL `upper()` vs C# disagree on i/İ. Fix: scope by brand + connection or explicit family id.
- **K19.** Deleting a product drops live stock reservations — `CatalogService.cs:615-617, 581-584`; pending ProductCreate/Update jobs stay. Fix: block while reservations active or jobs pending, or cancel them in same transaction.
- **K20.** Trendyol payload sends `dimensionalWeight = 1` when desi missing — `ProductPublicationComposer.cs:282`. Fix: require desi > 0 (`DESI_REQUIRED`).
- **K21.** CSV/XLSX apply produces unpublishable products — `ImportJobProcessor.cs:184-197`: `OptionSignature="DEFAULT"`, no option values/offer/price/category/brand; N+1 duplicate check (163) and preview (`MatchAsync`). Fix: map option columns, create MAIN offer, batch lookups.
- **K22.** Publish validation accepts non-ASCII barcodes — `ProductPublicationComposer.cs:428`, `ShopifyProductPublicationComposer.cs:46` (`char.IsLetterOrDigit`). Fix: `char.IsAsciiLetterOrDigit`.

## Düşük
- **K23.** Search treats `%`/`_` as wildcards (`CatalogService.cs:1975`); escape + `ESCAPE '\'`.
- **K24.** Old option values linger after edits (`CatalogService.cs:1057-1060`); prune unused.
- **K25.** Second category/brand mapping returns 500 (`ReferenceDataService.cs:89-100`, unique `CatalogModelConfiguration.cs:276`); return 409 `MAPPING_EXISTS`.
- **K26.** Offers seeded from N11/Pazarama/Shopify get VAT 0 when API omits it (unverified) (`MarketplaceJobProcessor.cs:5876, 6799`).
- **K27.** URL media skips custom-order logic (`CatalogEndpoints.cs:785-797` vs 313-314).
- **K28.** Pazarama sale-status lookup case-sensitive (`CatalogService.cs:1407-1408`).
- Missing tests: HB composer success with URL media; normaliser parity; `SameColorAndSize` with panel signature; family-edit preserve; multi-line/`;` CSV; retry after Blocked; import media preserve.

## Özet
Per-marketplace publishing rules are thorough; data identity is the weak point (two normalisers → duplicates and failed matches); local edits not fully protected from re-import (media, family siblings) and archive is destructive; several dead-ends (no republish after failure, HB URL images blocked, stuck imports); list/summary and approval polling unbounded. Fix K1 and K3 first, then K5 and K6, then K7.
