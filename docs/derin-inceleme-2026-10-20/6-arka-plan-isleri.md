# 6 — Arka plan işleri, zamanlayıcı, kuyruk (Q)

> origin/main 289d0b54. Ana rapor: `../PROJE-DERIN-INCELEME-RAPORU-2026-10-20.md`.
> Canlı ölçüm (son 24 saat): Trendyol `ORDER_STATUS_SYNC` en uzun 49 sn; `TRENDYOL_WEBHOOK_INGEST` p95 32 sn, en uzun 62 sn; `jobs` tablosu 92.573 satır.

## Önceki düzeltmelerin durumu
Q1 doğru; Q2 doğru (yan etkiler Q6, Q27); Q3 güvenli; Q4 doğru; Q5 doğru; Q6 yalnız siparişlerde (Trendyol iadeleri Q2'de); Q7 doğru (Q24 uyarıları); Q8 aynı anlam; Q9 kilitleme doğru, maliyet azaldı (Q15); Q10 doğru; Q11 kısmi (Q12); Q14 çalışıyor ama Q3'e yol açıyor; Q15 doğru; Q17 doğru.

## Bulgular
1. **High — Q1. Trendyol open-package scan holds the "orders" lock up to 20 min.** `MarketplaceSyncExecutionLock.cs:122`, `JobLeaseService.cs:47-58`, `MarketplaceJobProcessor.cs:3067-3131`, `JobTypeRegistry.cs:64`. New-order reads, webhook ingests and effect verifications wait; API instant actions give up after 3 s; results kept in memory, timeout loses all. Fix: own group ("order-lifecycle") with Version-free `ExecuteUpdate`, or slice with cursor + Continue.
2. **High — Q2. Trendyol `RETURN_SYNC` restarts a full read after any failure.** `MarketplaceJobProcessor.cs:8881-8883, 9606-9608`; telemetry and sync cursor same row ("RETURNS"). Fix: drop `retryAfterFailure`, resume saved state, Continue between storefronts/N pages.
3. **Medium-High — Q3. Lease/completion events cause refetch storms and dashboard rebuild every second.** `JobLeaseService.cs:83-85, 320-338`, `DashboardModels.cs:151-171`, `realtime-query-keys.ts:6, 32-34`, `OperationsRealtimeBroadcaster.cs:50-54`, `DashboardReadService.cs:160-187`; no `jobs(TenantId, CreatedAt)` index. Fix: publish as resource "jobs"; debounce rebuild 15–30 s, skip for job-only events; index; remove `AnnounceNewJobsAsync`.
4. **Medium — Q4. Failed completion write re-runs a successful job.** `Worker.cs:301-308`, `JobLeaseService.cs:131-199`: one try, 15 s timeout → LEASED → reaper LEASE_EXPIRED → rerun (dış yazma iki kez). Fix: retry with backoff while lease valid; heartbeat until confirmed.
5. **Medium — Q5. Action closed UNRESOLVED on first unclear check after downtime.** `SynchronizationPolicies.cs:751-757`, `EffectReconcile.cs:41-66`: 30 min from creation incl. 00:30–09:00. Fix: N checks over ≥30 min of actual verification time.
6. **Medium — Q6. Concurrency conflicts loop 24 h without using attempts.** `Worker.cs:607-608`, `JobLeaseService.cs:201-202`. Fix: count consecutive `CONCURRENT_UPDATE` deferrals, Retry after 3–5.
7. **Medium — Q7. Effect-guard jobs kept forever, many guard nothing.** `JobRetentionService.cs:24-33, 54-56`, `StockProjectionOutbox.cs:37-55`, `MarketplaceJobProcessor.cs:8734, 8738`. Fix: purge non-recurring guard types after ~90 days; purge Cancelled/pre-send Blocked; don't enqueue dispatch while writes disabled; partial index on finished statuses.
8. **Medium — Q8. User-waited invoice work shares one slot with multi-hour imports.** `Worker.cs:69`, `JobPriorityPolicy.cs:36`, `InvoicingEndpoints.cs:363`, `MarketplaceConnectionService.cs:845`. Fix: manual delivery UserAction (-2); due scan Default/Reconciliation; -1 only for bootstrap.
9. **Medium — Q9. `INITIAL_SYNC_PENDING` uses attempts and ends Dead.** `MarketplaceJobProcessor.cs:56`. Fix: Deferred or cancel pending incremental jobs when bootstrap queued.
10. **Medium (unverified) — Q10. Pool of 25 can run out once host step applied.** `MarketplaceSyncExecutionLock.cs:74-94`, `Worker.cs:61-70`, `compose.yaml:57-63`. Fix: worker pool ≥ 2× lane slots + 10 (~45); separate data source for heartbeats.
11. **Medium-Low — Q11.** Continue slices recorded as success → health overstated (`MarketplaceJobProcessor.cs:116, 148`, `DashboardReadService.cs:174-178`). Fix: `LastSliceAt`; success only on completion.
12. **Medium-Low — Q12.** Q11 partly fixed: direct-result failure and F3 paths still call `RecordSyncCompletion` (`MarketplaceJobProcessor.cs:116, 142`).
13. **Low-Medium — Q13.** Daily jobs drift into 09:00 catch-up (`ScheduledJobProducer.cs:128-148`). Fix: anchor to local time window (14:00–17:00 Istanbul).
14. **Low — Q14.** Retry jitter same for jobs created close together (`JobRetryPolicy.cs:36` uses timestamp byte of UUIDv7).
15. **Low — Q15.** Lease query still sorts every eligible row; every state change wakes all lanes (`JobLeaseService.cs:66-72`, `20261014130000_NotifyRunnableIntegrationJobs.cs:21-23`).
16. **Low — Q16.** Lease check vs lock disagree for return effect verifications (`JobLeaseService.cs:12-13, 96`, `MarketplaceSyncExecutionLock.cs:121, 132-133`).
17. **Low — Q17.** Coalesced read reported done without running (`MarketplaceSyncExecutionLock.cs:176-191`).
18. **Low — Q18.** HB/Shopify status syncs can occupy hot-lane slots (`Worker.cs:26`).
19. **Low — Q19.** Issue retention resolves and deletes in same pass (`Worker.cs:445-460`, `OperationalIssueRetentionService.cs:15-29`). Fix: `ResolvedAt`, delete only Resolved older than N days.
20. **Low — Q20.** Rows never cleaned: inbox rows of Blocked/Dead jobs, retired `rate_limit_usage` keys, completed `external_effect_records` (`MarketplaceJobProcessor.cs:7426`, `JobRetentionService.cs:76-81`).
21. **Low — Q21.** Queue metrics don't match how worker runs (`QueueHealthService.cs:12, 19-37`, `Worker.cs:283-286`).
22. **Low — Q22.** `InvalidOperationException` → ManualReview also for connection faults (`Worker.cs:616-617`).
23. **Low — Q23.** `AnnounceNewJobsAsync` can miss jobs; redundant (`OperationsRealtimeBroadcaster.cs:140-160`).
24. **Low — Q24.** UserDefined edge cases: migrated rows false; any later save pins forever (`MarketplaceConnectionService.cs:675`, migration `20261020105926`).
25. **Low — Q25.** Scheduler runs extra queries every 5 s before due check (`ScheduledJobProducer.cs:69-75, 132-137`).
26. **Low — Q26.** Watchdog can restart a healthy worker if health file write fails (`Worker.cs:633-645`).
27. **Info — Q27.** Nested busy-lock branch in `HydrateTrendyolReturnClaimOrder` dead code (`MarketplaceJobProcessor.cs:4054-4062`).
28. **Medium — Q28 (canlı ölçümden yeni).** Trendyol webhook ingest p95 32 s, max 62 s (normal <1 s) — likely lock/quota wait; measure and keep ingest outside quota/lock where possible.

## Doğrulanmayan
Q10 depends on host pool step; Q1 real durations; whether 00:30 stop honours 45 s grace; Q7 table size (live: 92,573 job rows).
