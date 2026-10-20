# 4 — Pano, raporlama, sorular, bildirimler (P)

> origin/main 289d0b54. Ana rapor: `../PROJE-DERIN-INCELEME-RAPORU-2026-10-20.md`. Canlı doğrulama: P1 (%4 fazla), P3 (%23 Trendyol siparişi ertesi güne).

## Yüksek
1. **P1. Revenue counts price before discounts.** `DashboardReadService.cs:141` (vs 310): `GrossAmount > 0 ? GrossAmount : NetAmount`; Trendyol gross = `packageGrossAmount`, net = `packageTotalPrice`. 1,000 TL package with 200 TL discounts shows 1,000. Fix: one `DashboardRevenuePolicy.Amount(order)` (net first) for series, projection, CSV + test.
2. **P2. Different currencies added into one TRY total.** `DashboardReadService.cs:145-153`; `DashboardPage.tsx:201-212, 239`. Fix: group by currency per day; TRY main + separate foreign lines or TCMB conversion. (Live: all TRY today.)
3. **P3. Trendyol orders after 21:00 Istanbul land on next day.** `DashboardReadService.cs:116-118, 139, 193-205`, late check `:60`; Trendyol `OrderedAt` stored as TR wall time labelled UTC (`SynchronizationPolicies.cs:683` compensates with `storedClockAhead`). Fix: shared `StoredClockAhead(platform)` in day bucketing/late checks; long-term data migration (owner go-ahead).
4. **P4. Full dashboard rebuild ~1/s reading whole history.** `OperationsRealtimeBroadcaster.cs:54`; `DashboardReadService.cs:196-198, 232-245, 274-301, 303, 344-352`: every outbox batch → `RebuildTenantAsync` loads all non-cancelled orders/packages (incl. JSON snapshots), invoices, products, variants, inventory, then delete/reinsert projections, in the API process. Fix: debounce 30–60 s per tenant, select needed columns, SQL counts, move to worker, delete dead parts (P19).
5. **P5. Each dashboard refresh = 12 heavy requests; realtime triggers constantly.** `DashboardPage.tsx:32, 193-196`; `realtime-query-keys.ts:6,12`; `DashboardReadService.cs:114-136`: 2 + 2N revenue-series calls, each loading all orders in range with correlated subqueries then filtering by name in memory; every orders/connections event invalidates `['dashboard']`; hidden tabs poll. Fix: one `/dashboard/performance` endpoint grouped in SQL by day × connection; debounce or drop `['dashboard']` from orders mapping; no background refetch.

## Orta-yüksek
6. **P6. One failing tenant makes dispatcher rebuild every tenant every second.** `OperationsRealtimeBroadcaster.cs:84-109, 169-180`: `nextReconciliation` set after loop; failed SaveChanges leaves tracked changes in shared DbContext. Fix: set before loop, per-tenant try/catch and scope, `ChangeTracker.Clear()`.
7. **P7. Realtime messages depend on rebuild succeeding.** `OperationsRealtimeBroadcaster.cs:54-77`. Fix: send to hub first, queue debounced rebuild.
8. **P8. Uncertain answer locks the question forever.** `MarketplaceQuestionService.cs:227, 236-251, 267-274, 490-492`; `MarketplaceQuestionAnswerReconciliationPolicy.cs:25-38`: UNKNOWN stays while remote WAITING; cancellation before request also UNKNOWN; re-read every 5 min forever. Fix: `SubmittedAt`; after 15–30 min still WAITING → `NOT_DELIVERED`, status back to WAITING, keep draft; pre-send cancellation not UNKNOWN.
9. **P9. Rejected answer cannot be rewritten from page.** `QuestionsPage.tsx:209, 257, 279`; `MarketplaceQuestionService.cs:34`. Fix: answerable when WAITING or REJECTED; count REJECTED in waiting filter or separate filter; show reason.

## Orta
10. **P10.** Question errors show as green "İşlem Başarılı" (`QuestionsPage.tsx:190,196,198,215,296`; `NoticeToast.tsx:50-58`; `notifications.ts:44`). Fix: explicit kind.
11. **P11.** Three different "pending orders" numbers; card links mismatch (`DashboardReadService.cs:91-97`; `dashboard-operational-links.ts:2`; `order-navigation-count.ts:8`; `DashboardPage.tsx:231`). Fix: one package-based definition + "Bekleyen"/"Geciken" filter.
12. **P12.** Pending returns count ignores stale-HB rule (`DashboardReadService.cs:63-66, 223-226` vs `MarketplaceSalesService.cs:675-686, 1678-1681`). Fix: shared `ReturnBaseQuery`.
13. **P13.** Period comparisons compare partial with full (`DashboardPage.tsx:50-68`; `DashboardReadService.cs:106-110`): "Bugün" at 10:00 at same pace shows ≈ −60%. Fix: previous period = same elapsed time; backend respects exact `to`; label "dün aynı saate kadar".
14. **P14.** Low-stock numbers disagree on same page (`DashboardReadService.cs:274-294` vs `CatalogService.cs:478-511`; `DashboardPage.tsx:235`). Fix: one query; link `/products?stock=LOW`.
15. **P15.** Chart tooltip product/package counts wrong (`DashboardReadService.cs:128-132`). Fix: `Ordered − Cancelled`; count from `CurrentShipmentPackages()`.
16. **P16.** Revenue status rule loose and changes past periods (`DashboardReadService.cs:118, 198`): awaiting-payment, CANCEL_REQUESTED, RETURN_IN_TRANSIT, UNDELIVERED count; returns remove old orders retroactively; partial returns never deducted. Fix: define "Brüt satış"; show returns/payment-pending separately.
17. **P17.** "Raporu İndir" can export wrong data (`DashboardPage.tsx:237-248, 253`; `DashboardContracts.cs:41`): placeholder data, no period/platform in name, mixed currencies, missing counts, timezone-less day. Fix: disable while placeholder/fetching; `ravencia-ciro-{from}_{to}-{platform}.csv`; `day.slice(0,10)`; `DateOnly`.
18. **P18.** Channels keyed by display name (`DashboardReadService.cs:111-113, 135-136`; `DashboardPage.tsx:27-29, 189, 195`). Fix: connectionId + platformCode.
19. **P19.** Large share of snapshot computed and never used (`DashboardReadService.cs:203-212, 228-272, 303-314, 344-365`; `App.tsx:55, 150`). Fix: delete; small `/navigation/summary` for sidebar.
20. **P20.** Revenue range limit bypassable with reversed range (`DashboardEndpoints.cs:17-20`). Fix: reject `from > to`, absolute span.
21. **P21.** Integration matrix hides/mislabels failures (`DashboardIntegrationMatrix.cs:24, 43-48, 57-61`; `DashboardPage.tsx:286`): `Take(300)` across connections; policy blocks shown red; warn labelled "Durdu". Fix: DISTINCT ON per connection/type; exclude policy blocks.
22. **P22.** Card totals and hover breakdowns from different sources (`DashboardReadService.cs:51-73`).

## Düşük-orta / Düşük
23. **P23.** Failing question sync shows healthy (`QuestionsPage.tsx:118-126`): `historyImported` checked before FAILED.
24. **P24.** Every question sync failure → Blocked `F3_JOB_REJECTED` with generic message (`QuestionSyncJobHandler.cs:10, 28-29`). Fix: map adapter error class to Retry/Deferred.
25. **P25.** "Yenile" does nothing for N11/Pazarama (`QuestionEndpoints.cs:31,36`; `QuestionsPage.tsx:220-224`); every visit starts a sync.
26. **P26.** Pazarama "awaiting approval" shown as answered (`PazaramaQuestions.cs:91-97`; `MarketplaceQuestionService.cs:471-472`). Fix: 2 → WAITING_FOR_APPROVE; unknown codes as raw + issue.
27. **P27.** Notification history shared by everyone on the browser (`notifications.ts:35`).
28. **P28.** Realtime connection doesn't follow login session (`OperationsHub.cs:8-17`).
29. **P29.** Never-synced store ignored by "Son güncelleme" (`DataFreshnessService.cs:32`).

## Özet
Revenue numbers not trustworthy yet (discounts included, currencies mixed, Trendyol late-evening shift, biased comparisons). Cards/sidebar/pages use different definitions. Biggest risk is load: full rebuild almost every tick, one failure → every-second rebuild of all tenants; page issues 12 heavy requests per refresh. Questions: cancelled/timed-out answer can lock a question; rejected answers can't be rewritten; errors show as success.
