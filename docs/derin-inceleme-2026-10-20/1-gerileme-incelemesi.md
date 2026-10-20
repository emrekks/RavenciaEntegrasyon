# 1 — Son iki günün gerileme incelemesi (G)

> Kapsam `3a2b9ab5..origin/main` (289d0b54). İnceleme kolunun ayrıntılı bulguları. Ana rapor: `../PROJE-DERIN-INCELEME-RAPORU-2026-10-20.md`.

## Yüksek

**G1. Delivery job still runs after its invoice is cancelled, which breaks link revocation** (`3e5a1fea`, `6f53eca7`)
- Where: `InvoicingJobProcessor.cs:704-760` (`DeliverSinglePackage`), `:984` (proof path to `SetInvoiceDeliveryOutcome` at `:1462`), `InvoiceAdjustmentTracking.cs:57-70`.
- Problem: delivery never checks invoice status (Cancelled, CancellationPending, AdjustmentRequired) nor whether the delivery state is `REVOCATION_*`/`LINK_REMOVED`. `SetInvoiceDeliveryOutcome` skips `CanTransition`. Cancelling an invoice does not cancel pending delivery jobs.
- Failure: invoice cancelled while a delivery retry is queued → retry sees our link, writes CONFIRMED and moves Cancelled → Completed; the revocation row disappears. After `LINK_REMOVED`, the retry posts the cancelled invoice's link to Trendyol again.
- Fix: guard delivery start on invoice/state status; outcome setter respects `CanTransition`; `EnqueueCancellationAsync`/`MarkLinkRevocationRequiredAsync` cancel the invoice's Pending/Leased/RetryScheduled delivery jobs.

**G2. Any write conflict retries a job almost without limit for 24 h** (`861b7444`) — verified
- Where: `Worker.cs:604-607`, `JobLeaseService.RefundsAttempt` (`:201`).
- Problem: `DbUpdateConcurrencyException` → Deferred with refunded attempt, rerun in 10–30 s; refund applies to any job younger than 24 h; attempt row deleted.
- Failure: a job that conflicts every time reruns thousands of times a day, repeating marketplace reads and spending quota; history shows nothing.
- Fix: defer only the first 1–2 conflicts (counter), then normal Retry using an attempt.

**G3. VAT can be added twice to existing VAT-excluded offers** (`58def1ab`) — unverified (live: 0 EXCLUDED offers)
- Where: `TrendyolWriteComposers.cs:155-160, 276`; `MarketplaceJobProcessor.cs:~6844`.
- Problem: EXCLUDED offers now sent as price × (1+VAT) and the sent-price marker is computed on that value; no data migration.
- Fix: one-time recompute/re-import of EXCLUDED offers, or keep old marker until edited.

**G4. A failed action cannot be retried for 24 h** (`70bb75fe`, web)
- Where: `shared/idempotency.ts:24` (`versionedIdempotencyKey`) used by return approve/bulk, shipments, returns, questions, imports.
- Problem: retry sends the same key; a 5xx leaves the key UNKNOWN → later clicks get 409 `IDEMPOTENCY_RESULT_UNKNOWN`; a 4xx is replayed.
- Failure: a marketplace 429 becomes 503 and the user can't approve that return until the claim version changes.
- Fix: versioned key only for a resend after a network error with no answer; after any HTTP answer use `newIdempotencyKey()`.

**G5. Wrong password on fiscal confirmation signs the user out** (web `shared/api/client.ts:82`, `invoicing/pages.tsx:730`)
- Problem: `onUnauthorized()` fires on any 401; server answers wrong password with 401 `REAUTHENTICATION_FAILED` while the session is valid.
- Fix: sign out only on `AUTHENTICATION_REQUIRED`, or server answers 403/422.

**G6. Single label prints are never saved on the server** (`8c86b6c6`)
- Where: `orders/OrdersPage.tsx:376, 534, 784/817, 987`.
- Problem: only the batch modal posts `/orders/{id}/label-printed`; with server filter active the local filter is skipped; row "printed" ignores `item.labelPrintedAt`.
- Fix: post from single-label modal too, invalidate `['orders']`, read `labelPrintedAt` for row state.

**G7. Jobs that lose a duplicate-insert race stop for good** (`861b7444`)
- Where: `Worker.cs:609-610`. 23505 now maps to ManualReview (terminal); before it was retried.
- Failure: webhook ingest and poll insert the same link/snapshot row; loser parks in MANUAL_REVIEW and webhook data is lost until next poll.
- Fix: retry 23505 once or twice; ManualReview only for check-constraint/truncation.

## Orta

- **G8.** Returns list/summary scan every return per request (`MarketplaceSalesService.ReturnList.cs:89-148, 263-305`): all claims + lines with correlated `OrderLines.Any`; search builds full views for every match; `/returns/summary` scans up to twice; web polls every 30 s. Fix: push tab/platform/cargo filters and paging into SQL; cache summary briefly or GROUP BY.
- **G9.** Returns tab filter leaks whole cards (`ReturnList.cs:121-123`): APPROVED tab's DB filter includes ActionRequired claims with an ACCEPTED line; non-splitting ones stay WholeCard ACTION_REQUIRED and pass `card.Kind == WholeCard || …`. Fix: filter every card by `ReturnTabGroup(card.Status) == tab`.
- **G10.** Trendyol package scan cap → permanent 404 (`TrendyolHttpClient.cs:545`): after 20 pages returns null → `REMOTE_PACKAGE_NOT_FOUND`; for >~4k packages in window, readback/revocation Blocked. Fix: retryable `TRENDYOL_PACKAGE_SCAN_INCOMPLETE` or narrow window.
- **G11.** Circuit half-open probe waits for quota while blocking everyone (`TrendyolResilienceHandler.cs:34`; `DependencyInjection.cs:96/104/113`): probe takes the slot then waits 5–10 min; others get 503 Retry-After 1 s, each retry uses an attempt. Fix: claim probe only after quota token.
- **G12.** Terminal-status conflict overwrites Cancelled/Returned with ManualReview (`MarketplaceJobProcessor.cs:7930-7935`, unverified). Fix: skip when current status already terminal / incoming behind terminal.
- **G13.** HB packages first seen Delivered/Undelivered never get a cargo company (`HepsiburadaHttpClient.cs:642`). Fix: skip tracking read only when provider and tracking known.
- **G14.** Trendyol invoice reconciliation waits behind order reads and blocks other background order jobs (`MarketplaceSyncExecutionLock.cs:~114-121`, `ScheduledJobProducer.cs:111-123`). Fix: own lock group or exclude from slot.
- **G15.** Effect sweep closes records UNRESOLVED within minutes when check is merely blocked (`ExternalEffectSweep.cs:25-90`); sweep never settles linked ReturnDecision. Fix: don't count CONNECTION_INACTIVE/EXTERNAL_WRITES_DISABLED blocks; settle decision.
- **G16.** Resent question answer marked REJECTED right away (`MarketplaceQuestionAnswerReconciliationPolicy.cs:22,36`). Fix: reject only when rejection newer than submission.
- **G17.** Workspace confirm runs whole batch under 90 s inline budget (`InvoicingEndpoints.cs:111-125`) → 503 after some items queued. Fix: budget token only inside `budget.RunAsync`.
- **G18.** Link-removal POST has no server budget and no step-up (`InvoicingEndpoints.cs:134-142`); tab close leaves `REVOCATION_STARTED` locked 5 min, no audit. Fix: `.WithServerBudget()`, password confirmation.
- **G19.** `DueAt` almost never set (`InvoicingBillingService.cs:1213`) → due scan never fires. Fix: compute in `ScanDue` or update on delivered sync.
- **G20.** Orders page labels/filters disagree with invoice workspace for pre-provider refusals (`MarketplaceSalesService.cs:2777/2815`, filters `:303-318`); AdjustmentRequired falls to FATURA_KONTROLDE; filters use "any invoice", label uses latest. Fix: share `FiscalSubmissionLabel`, add ADJUSTMENT label/filter.
- **G21.** Role widening (`RoleAuthorizationMiddleware.cs:79-83`): `orders/*` and `invoice-workspace/*` writes now open to OPERATIONS (and ACCOUNTING for workspace) incl. `replace-cancelled-invoice`, `confirm-no-remote-invoice`, `confirm-marketplace-delivered`, `orders/{id}/instant-process`; ACCOUNTING gets 403 on `label-printed`. Fix: keep fiscal-override routes elevated; WorkingRoles on label-printed.
- **G22.** Jobs list resets to page 1 when job-type set changes (`operations/pages.tsx:476-487`): key includes `categoryJobTypesKey` from `/jobs/counts` polled every 5 s. Fix: derive types from static tab definitions.

## Düşük

- **G23.** Polls on open pages keep idle session alive (only `App.tsx` sends `X-Background-Poll`; jobs 5 s, returns 30 s don't).
- **G24.** Jobs infinite query refetches every loaded page every 5 s.
- **G25.** Shipments pager can store the same cursor twice (`ShipmentPages.tsx:59-66`) with keepPreviousData double-click.
- **G26.** One failed order detail blocks batch label print (`shipping-label-data.ts:24-35`); use allSettled.
- **G27.** CSV export applies localStorage label filter on top of server filter (`OrdersPage.tsx:821-825`).
- **G28.** `HoldsThisInvoice` guard never fires (`TrendyolHttpClient.cs:867-868`): requires both link and number mismatch; a same-number different-link package gets its live link deleted. Fix: false on either mismatch.
- **G29.** TurkeyTime misreads `+03` offset (`TurkeyTime.cs:20`); extend regex `[+-]\d{2}(:?\d{2})?$` or RoundtripKind.
- **G30.** Trendyol lines without `vatRate` dropped (`TrendyolJsonMapper.cs:43`), unverified.
- **G31.** HB name priority may change line identity (`HepsiburadaJsonMapper.cs:1699`), unverified.
- **G32.** Shopify returns cut off silently (`ShopifyHttpClient.cs:618`): 20 returns/50 lines, no pageInfo check.
- **G33.** "Cancelled claim kept" issue reopens on every re-read (`CancelledReturnClaimCleanup.cs:40`).
- **G34.** One stale policy row loses the whole scheduler pass (`ScheduledJobProducer.cs:128-173`); write `LastScheduledAt` with ExecuteUpdate.
- **G35.** Every lease writes an outbox event and bumps Version (`JobLeaseService.cs:83-85`).
- **G36.** HB verify race 500 (`InvoicingLinkRevocation.cs:116`); refused credentials error stays on ACTIVE connection and Version bump causes 412 (`MarketplaceJobProcessor.cs:240-260`); per-row queries in `QueuePendingMarketplaceDeliveryRecovery` and `LinkRevocationViewsAsync`; `IX_orders_TenantId_ConnectionId_CreatedAt` built without CONCURRENTLY; `IsStoppedBeforeProvider` moved to "any error code".

## Doğru bulunanlar
All `ShouldAccept` call sites pass `incomingRawStatus` (HB omits deliberately); FATURA_REDDEDILDI/FATURA_BELIRSIZ split consistent in workspace; idempotency middleware expiry/replay headers correct; `label-printed` idempotent and tenant-filtered; `/jobs/page` keyset paging correct; both migrations match snapshot with correct Down(); web request shapes match API; pages.tsx replay merge lost nothing; A2/A13 reverts clean.

## En riskli commit'ler
`861b7444` (exception mapping, lease outbox), `3e5a1fea`/`6f53eca7` (invoice lifecycle, link removal), `58def1ab` (prices), `70bb75fe` (web idempotency keys), `6ecb9b81` (returns server paging), `c88893f3` (package supersession), `073cd606`/`52fd07ac` (Trendyol caps, circuit order).
