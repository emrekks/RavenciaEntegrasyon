# 5 — Sipariş, kargo ve etiket akışı (S)

> origin/main 289d0b54. Ana rapor: `../PROJE-DERIN-INCELEME-RAPORU-2026-10-20.md`.
> 19 Ekim düzeltmeleri S1, S3, S4, S6, S7, S9, S10 doğru ve tam; S2 Trendyol/N11/HB için geçerli; S5 listede düzeltildi; S8 kısmi (PartiallyCancelled paketin NetAmount'u pazaryerinden olduğu gibi).

## Yüksek
1. **S1. Printed label has no recipient name.** `OrdersPage.tsx:366`: address block prints only `renderField('address')`, ignores its fields; Settings preview (`SystemSettingsPage.tsx:990`) shows the name. Fix: render `block.fields` (name first) + test.
2. **S2. Barcodes print blank in default browser settings.** `design-system.css:397-399, 1590-1599`: bars are `background` on `<i>`; no `print-color-adjust: exact`. Fix: add it to `.shipping-label-print-surface *` or draw SVG `<rect>`s with integer module widths.
3. **S3. Split order with one delivered + one new package shows DELIVERED.** `OrderModels.cs:303-316`; web `OrdersPage.tsx:51-58`. Package 2 drops out of Yeni/Pending, no deadline warning. Fix: if any package New/Processing/ReadyToShip/OnHold/PartiallyCancelled/PaymentPending return lowest of those; mirror in web; tests. (Live: 0 such orders today.)
4. **S4. Every open Shopify order lands in MANUAL_REVIEW.** `ShopifyHttpClient.cs:1120, 1242`; `OrderModels.cs:300`; `MarketplaceJobProcessor.cs:7778`: raw `UNFULFILLED`/`PROCESSING` unknown to `FromRemote`. Fix: Shopify mapping (UNFULFILLED→New, PROCESSING→Processing, FULFILLMENT_FAILED/UNKNOWN→ManualReview) + repair + tests. (Live: all 11 delivered today.)
5. **S5. Package line rows summed across sync events.** `MarketplaceSalesService.cs:1016-1018` (HB create), `:1311-1314` (instant picking), `~:1262` (queued picking): `PackageLineAllocation` is one row per `SourceEventId`; HB also counts cancelled/superseded. PICKING lists a line twice; HB blocks a valid second package. Fix: latest row per package/line (like `PackageLineProjectionPolicy`), skip superseded/cancelled.
6. **S6. After one ambiguous HB package creation the order can never get another package.** `MarketplaceSalesService.cs:1082, 1099`; `EffectReconcile.cs:45-97`; unique `AppDbContext.cs:359`. Fix: rename reservation to `{prefix}:reconciled:{effectId}` on settle.
7. **S7. Destructive Trendyol actions unconfirmed and offered in any state.** `ShipmentPages.tsx:101-113`; `MarketplaceSalesService.cs:945-956`: CANCEL_ITEMS, MANUAL_DELIVER, MANUAL_RETURN via free-text JSON. Fix: filter by `ValidateShipmentAction`, [] for superseded, destructive confirm, line pickers.

## Orta
8. **S8.** Single label print never records `labelPrintedAt` (`OrdersPage.tsx:376` vs 388); row style ignores server value; mark set before `window.print()`. Fix: POST on `afterprint`.
9. **S9.** Returned/in-review orders show as late (`OrdersPage.tsx:116`; `order-delivery-presentation.ts:20`; `OrderModels.cs:195-211`). Fix: warnings only for NEW/PROCESSING/READY_TO_SHIP/PARTIALLY_CANCELLED.
10. **S10.** Due-date sort labels swapped (`OrdersPage.tsx:933` vs `MarketplaceSalesService.cs:88-89`); sort uses only `ShipmentDueAt` column. Fix: swap labels, "Kargo Termini", backfill.
11. **S11.** Shopify "Faturalanacak Tutar" subtracts discount twice (`ShopifyHttpClient.cs:1180-1182` gross=net=total; `OrdersPage.tsx:481-483`). Fix: gross = subtotal.
12. **S12.** Tab counters don't match list (`MarketplaceSalesService.cs:539-616` vs 360-430; `MarketplaceEndpoints.cs:88`; `OrdersPage.tsx:785-790`): packages vs orders; summary ignores date/search/cargo/invoice filters. Fix: distinct OrderId with same predicate builder; pass all filters.
13. **S13.** Some statuses have no tab (MANUAL_REVIEW, RETURNED, RETURN_IN_TRANSIT, UNDELIVERED; PAYMENT_PENDING → NEW) (`order-status-tabs.ts`; `OrdersPage.tsx:938`).
14. **S14.** Display package can be a cancelled package (`shared.tsx:58`; list ordered by StatusOccurredAt `:102`, detail by Id `:722`); split orders print one label. Fix: prefer non-cancelled; one label per active package.
15. **S15.** Order-level "İşleme Al" targets first package by Id (`MarketplaceEndpoints.cs:291, 320`).
16. **S16.** Retrying a successful picking returns error (`MarketplaceSalesService.cs:1306` before idempotency lookup at `:1343`).
17. **S17.** Cargo change offered where impossible, misreports partial results (`OrdersPage.tsx:282-312, 486, 856`; `MarketplaceSalesService.cs:1454-1492`); no server platform check; replay writes request carrier not `ExpectedValue`.
18. **S18.** Shipments page stale rows, oldest first (`MarketplaceSalesService.cs:934-938`; `ShipmentPages.tsx:54`): raw packages incl. superseded; status filter silently dropped; English "LIVE READ" badge.
19. **S19.** Order CSV export: no formula-injection guard; raw codes; gross amount; `packages[0]`; offset paging drift; silent 20,000 cap (`OrdersPage.tsx:78-91, 818-847`).
20. **S20.** Manual Shopify status fragile (`MarketplaceEndpoints.cs:162-208`; `MarketplaceJobProcessor.cs:8044-8052`): no If-Match/lock; key unused; webhook reverts it.
21. **S21.** Pazarama totals/due date include cancelled items (`PazaramaOrderMapper.cs:83-110`).
22. **S22.** HB UNPACK leaves phantom local package (unverified).
23. **S23.** HB multi-parcel PDF/PNG/JPG labels return only first parcel (`HepsiburadaPackageOperations.cs:174`).
24. **S24.** Shopify CSV import collides with sync, rewrites invoiced orders (`ShopifyOrderCsvImportService.cs:104-214`).
25. **S25.** A4 label settings do nothing (`shipping-label.ts:88`; `OrdersPage.tsx:339, 371`): `a4-count-N` without CSS, no `@page`.
26. **S26.** Barcodes may not scan reliably (unverified) (`shipping-label.ts:56-72`): Code128 table/checksum correct, but quiet zone ~2 mm (needs 10 modules), fractional bar widths, 128B for digits (128C half width).
27. **S27.** HB stale enrichment can roll back invoice status/carrier (unverified) (`HepsiburadaStaleOrderEnrichmentPolicy.cs:9-15, 143-146`).
28. **S28.** Instant picking may trigger false "transition rejected" issues (unverified) (`MarketplaceSalesService.cs:1379-1388`, `MarketplaceJobProcessor.cs:7928-7944`).
29. **S29.** Order search case-sensitive (`MarketplaceSalesService.cs:441-443, 2995`). Fix: ILIKE + tr-TR lower.

## Düşük
30. HB package modal free-text API codes; one key reused across edited retries (`OrdersPage.tsx:581, 626`).
31. Disabled "İşleme Al" shows wrong reason (`OrdersPage.tsx:543`).
32. ACCOUNTING label prints 403 swallowed (`RoleAuthorizationMiddleware.cs:83-84`).
33. Fresh HB claim-only orders invisible (`HepsiburadaOrderVisibility.cs:17-23`).
34. N11 dates may be off 3 h (unverified) (`N11OrderMapper.cs:82, 237-241`).
35. Second N11 split leaves children unlinked (`N11OrderMapper.cs:95-127`).
36. No carrier tracking links except N11; Trendyol `cargoTrackingLink` stored but unused.
37. Label carrier check substring match (`CommonLabelCarrierPolicy.cs:5-13`).
38. Accessibility: barcode aria-label on plain div; cancelled rows empty span; detail sheet only on phones; menus arrow keys (unverified).
39. Batch label printing loose rules (any status, ignores format settings, unbounded localStorage list).
40. Sync/import housekeeping: single-order sync fresh keys; HB missing-order reads starve newer ones; micro-export TR stamp never corrected; Turkish Excel CSV misleading error.

## Özet
Label not usable as shipped (no name, blank barcode, A4 settings dead). Order status wrong for split orders and every open Shopify order. Allocation summing and HB reservation lock-up; destructive actions unconfirmed. Several UI mismatches. Order: label → Aggregate + Shopify mapping → allocation + HB reservation → action filtering → counters/tabs.
