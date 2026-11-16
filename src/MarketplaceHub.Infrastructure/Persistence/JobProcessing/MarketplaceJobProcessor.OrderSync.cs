using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    // KUYRUK-9 (16 Nov 2026): an order read whose request body cannot be read names its cause instead of the generic
    // F3_JOB_REJECTED. It stays blocked as before: the body is the same on every run, and nothing was read.
    private static JobProcessingException OrderSyncPayloadInvalid() =>
        new(JobExecutionResult.Blocked("ORDER_SYNC_PAYLOAD_INVALID", "Sipariş okuma isteği okunamadı; pazaryerinden sipariş okunmadı. Okumayı mağaza ekranından yeniden başlatın."));

    // ISLEYICI-12 point 1 (16 Nov 2026): one order read by its number, for the readers whose single-order branch was the
    // same copy (Shopify, n11, Pazarama, PttAVM): a failure ends the job with the marketplace's error, nothing is stored.
    // Trendyol (a package fallback for orders its number lookup no longer shows) and Hepsiburada (package reads around the
    // order) keep their own branches; they read the request through the same JobPayloadReader.OrderRead.
    private async Task<RemoteOrder> ReadSingleOrderAsync(Guid tenantId, Guid connectionId, string correlationId, string idempotencyPrefix, string externalOrderId, CancellationToken cancellationToken)
    {
        TrackRequest();
        var single = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"{idempotencyPrefix}:{externalOrderId}"), externalOrderId.Trim(), cancellationToken);
        if (!single.IsSuccess) { TrackResultFailure(single.Error); throw JobProcessingException.FromAdapter(single.Error!); }
        TrackReceived();
        return single.Value!;
    }

    // A finished order read also matches the connection's lines that still have no panel variant
    // (RematchUnmatchedOrderLines): the variant or its link may have been created after the line was read.
    private async Task<bool> SyncOrdersAndMatchLines(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, string cursorResourceType, bool allowBaseline, CancellationToken cancellationToken)
    {
        run.OrderRereadRan = false;
        var synced = await SyncOrders(tenantId, connectionId, payloadJson, correlationId, cursorResourceType, allowBaseline, cancellationToken);
        // Every marketplace: orders stored with an unreadable product line are read again by number (UnreadableOrderLines).
        // The Trendyol stream read does this itself once its read completed (it also re-reads the orders it refused).
        if (synced && !run.OrderRereadRan && !cancellationToken.IsCancellationRequested) await RereadOrders(tenantId, connectionId, correlationId, cancellationToken);
        if (synced) await RematchUnmatchedOrderLines(tenantId, connectionId, cancellationToken);
        if (synced && cursorResourceType == "ORDERS_RECOVERY") await RunResendDiagnosticOnce(tenantId, connectionId, correlationId, cancellationToken);
        return synced;
    }

    private async Task<bool> SyncOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, string cursorResourceType, bool allowBaseline, CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Marketplace:PersistOrderSnapshots", true))
            return true;

        var platform = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
        if (platform == "SHOPIFY")
            return await SyncShopifyOrders(tenantId, connectionId, payloadJson, correlationId, cursorResourceType, allowBaseline, cancellationToken);
        if (platform == "N11")
            return await SyncN11Orders(tenantId, connectionId, payloadJson, correlationId, cursorResourceType, allowBaseline, cancellationToken);
        if (platform == "PAZARAMA")
            return await SyncPazaramaOrders(tenantId, connectionId, payloadJson, correlationId, cursorResourceType, allowBaseline, cancellationToken);
        if (platform == "PTTAVM")
            return await SyncPttAvmOrders(tenantId, connectionId, payloadJson, correlationId, cursorResourceType, allowBaseline, cancellationToken);
        if (platform == "HEPSIBURADA")
        {
            // A full read whose order part finished and whose month of packages is still being read (a slice ended, a
            // timeout) goes on with the packages: the order baseline is not read again for every package day.
            var ordersSynced = await HepsiburadaPackageBaselinePendingAsync(tenantId, connectionId, payloadJson, cursorResourceType, cancellationToken)
                || await SyncHepsiburadaOrders(tenantId, connectionId, payloadJson, correlationId, cursorResourceType, allowBaseline, cancellationToken);
            if (!ordersSynced || cursorResourceType is not ("ORDERS_HOT" or "ORDERS_RECOVERY")) return ordersSynced;
            if ((JobPayloadReader.OrderRead(payloadJson) ?? throw OrderSyncPayloadInvalid()).IsSingleOrder) return true;
            return await SyncHepsiburadaPackages(tenantId, connectionId, payloadJson, correlationId, cancellationToken);
        }

        var request = JobPayloadReader.OrderRead(payloadJson) ?? throw OrderSyncPayloadInvalid();
        var externalOrderId = request.ExternalOrderId;
        var packageNumber = request.PackageNumber;
        var full = request.Full;
        if (!string.IsNullOrWhiteSpace(externalOrderId))
        {
            var requestedPackageNumber = packageNumber?.Trim();
            var hasRequestedPackageNumber = !string.IsNullOrWhiteSpace(requestedPackageNumber)
                && requestedPackageNumber.Length <= 100
                && !requestedPackageNumber.Any(char.IsControl);

            TrackRequest();
            var single = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"order-get:{externalOrderId}"), externalOrderId.Trim(), await OrderHint(tenantId, connectionId, externalOrderId.Trim(), cancellationToken), cancellationToken);
            if (single.IsSuccess)
            {
                TrackReceived();
                await UpsertOrder(tenantId, connectionId, single.Value!, cancellationToken);
                return true;
            }

            if (single.Error?.Class != AdapterErrorClass.NotFound && single.Error?.HttpStatus != 404)
            {
                TrackResultFailure(single.Error);
                throw JobProcessingException.FromAdapter(single.Error!);
            }

            // Trendyol's order-number lookup stops exposing older orders, while
            // their package endpoint can still return the complete order snapshot.
            // Use package IDs already linked to this order as a read-only fallback.
            var knownPackages = await (from package in db.ShipmentPackages.AsNoTracking()
                                       join order in db.Orders.AsNoTracking()
                                           on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                                       where package.TenantId == tenantId
                                           && package.ConnectionId == connectionId
                                           && order.ExternalOrderId == externalOrderId
                                       select new { package.ExternalPackageId, package.StatusOccurredAt })
                .ToListAsync(cancellationToken);
            var packagesToRead = new List<(string ExternalPackageId, DateTimeOffset? StatusOccurredAt)>();
            var packageIdsToRead = new HashSet<string>(StringComparer.Ordinal);
            if (hasRequestedPackageNumber && packageIdsToRead.Add(requestedPackageNumber!))
                packagesToRead.Add((requestedPackageNumber!, null));
            foreach (var knownPackage in knownPackages)
            {
                if (packageIdsToRead.Add(knownPackage.ExternalPackageId))
                    packagesToRead.Add((knownPackage.ExternalPackageId, knownPackage.StatusOccurredAt));
            }
            var packageSnapshots = new List<RemoteOrder>();
            AdapterError? packageNotAsked = null;
            foreach (var packageToRead in packagesToRead)
            {
                TrackRequest();
                var packageRead = await orders.GetShipmentPackageAsync(
                    Context(tenantId, connectionId, correlationId, $"targeted-order-package-read:{packageToRead.ExternalPackageId}"),
                    packageToRead.ExternalPackageId,
                    new PackageLookup(packageToRead.StatusOccurredAt, await OrderHint(tenantId, connectionId, externalOrderId, cancellationToken)),
                    cancellationToken);
                if (!packageRead.IsSuccess)
                {
                    // ADAPTOR-TY-HB-3: a package outside every readable window was not asked for; remembered so the
                    // job does not end as if Trendyol had answered that it does not know the order.
                    if (TrendyolPackageNotAsked(packageRead.Error))
                    {
                        packageNotAsked ??= packageRead.Error;
                        continue;
                    }
                    if (packageRead.Error?.Class == AdapterErrorClass.NotFound || packageRead.Error?.HttpStatus == 404)
                        continue;
                    TrackResultFailure(packageRead.Error);
                    throw JobProcessingException.FromAdapter(packageRead.Error!);
                }

                TrackReceived();
                if (packageRead.Value?.OrderSnapshot is { Packages.Count: > 0 } orderSnapshot)
                    packageSnapshots.Add(orderSnapshot);
            }

            var recoveredOrder = TrendyolJsonMapper.MergeOrderPackages(packageSnapshots, externalOrderId);
            if (recoveredOrder is { Packages.Count: > 0 })
            {
                await UpsertOrder(tenantId, connectionId, recoveredOrder, cancellationToken);
                return true;
            }

            TrackResultFailure(single.Error);
            throw JobProcessingException.FromAdapter(packageNotAsked ?? single.Error!);
        }

        var cursor = await Cursor(tenantId, connectionId, cursorResourceType, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var configuredOverlapSeconds = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ORDERS")
            .Select(x => (int?)x.OverlapSeconds)
            .SingleOrDefaultAsync(cancellationToken) ?? DefaultOrderSyncOverlapSeconds;
        var overlap = TimeSpan.FromSeconds(Math.Clamp(configuredOverlapSeconds, 0, (int)OrderStreamWindowSpan.TotalSeconds - 1));
        // A reset/empty snapshot store must always get a complete baseline, even if
        // an older cursor survived the reset. This keeps the first import idempotent
        // without requiring any direct database mutation.
        var hasSnapshots = await db.Orders.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        // A failed run does not force a three-month baseline: the watermark only moves once a run
        // completes, so the next incremental read starts before the gap the failure left.
        var storefrontCursor = await Cursor(tenantId, connectionId, TrendyolStorefrontPolicy.CursorResource, cancellationToken);
        var storefrontActivity = ReadStorefrontActivity(storefrontCursor);
        var readStorefronts = await TrendyolReadStorefronts.ForConnectionAsync(db, tenantId, connectionId, cancellationToken);
        void NoteStorefront(string code, TrendyolStorefrontActivity seen)
        {
            storefrontActivity[code] = seen;
            storefrontCursor.OpaqueCursor = JsonSerializer.Serialize(storefrontActivity);
            storefrontCursor.Version++;
        }
        var state = ReadOrderSyncState(cursor, now, overlap, allowBaseline, forceBaseline: allowBaseline && (full || !hasSnapshots),
            activeStorefronts: TrendyolStorefrontPolicy.Hot(readStorefronts, storefrontActivity, now),
            readableStorefronts: TrendyolStorefrontPolicy.Readable(readStorefronts, storefrontActivity, now));
        var resetExpiredCursor = false;
        var completed = false;
        var insertedBeforePoll = run.InsertedCount;
        var streamWalk = new PageWalk(TrendyolOrderStreamPageGuard);
        do
        {
            var (modifiedAfter, modifiedBefore) = OrderWindow(state);
            var storefronts = OrderSyncStorefronts(state);
            var storefront = storefronts[state.StoreFrontIndex];
            TrackRequest();
            var result = await orders.PollAsync(Context(tenantId, connectionId, correlationId, $"order-sync:{storefront}:{state.NextCursor ?? state.WindowIndex.ToString()}"), new(modifiedAfter, modifiedBefore, storefront), new(state.NextCursor, 200), cancellationToken);
            if (!result.IsSuccess && !resetExpiredCursor && !string.IsNullOrWhiteSpace(state.NextCursor) && result.Error is { Class: AdapterErrorClass.Validation, HttpStatus: 400 })
            {
                // Stream cursors can expire. Restart once from the durable watermark; preserve all other
                // validation failures for audit/retry.
                state = state with { NextCursor = null };
                cursor.OpaqueCursor = null;
                resetExpiredCursor = true;
                continue;
            }
            if (!result.IsSuccess && state.StoreFrontIndex > 0 && state.NextCursor is null && result.Error?.HttpStatus is 400 or 404)
            {
                // Some seller accounts do not expose every international storefront. A
                // rejected optional storefront must not prevent TR or another storefront
                // from being imported and persisted.
                NoteStorefront(storefront, TrendyolStorefrontPolicy.Rejected(storefront, storefrontActivity.GetValueOrDefault(storefront), now));
                if (state.StoreFrontIndex + 1 < storefronts.Count)
                {
                    state = state with { StoreFrontIndex = state.StoreFrontIndex + 1, NextCursor = null };
                    cursor.OpaqueCursor = SerializeOrderSyncState(state);
                    cursor.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }
                if (state.Mode == OrderSyncMode.Baseline && HasEarlierOrderWindow(state))
                {
                    state = state with { WindowIndex = state.WindowIndex + 1, StoreFrontIndex = 0, NextCursor = null };
                    cursor.OpaqueCursor = SerializeOrderSyncState(state);
                    cursor.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                    // Recovery is a low-priority backfill. Persist the next
                    // window and release the orders lane between windows so
                    // lifecycle/reconciliation jobs can repair current orders.
                    if (cursorResourceType == "ORDERS_RECOVERY") throw new JobProcessingException(JobExecutionResult.Continue("Sipariş geçmişinin sonraki penceresi birkaç saniye içinde okunacak."));
                    continue;
                }
                cursor.OpaqueCursor = null;
                cursor.LastModifiedWatermark = state.AnchorEnd;
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                completed = true;
                break;
            }
            if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
            await IngestOrderPageAsync(tenantId, connectionId, result.Value!, "order-contract", cancellationToken);
            NoteStorefront(storefront, TrendyolStorefrontPolicy.Read(storefrontActivity.GetValueOrDefault(storefront), now, result.Value!.Items.Count > 0));
            // ISLEYICI-11: a stream that names the page just read as the next one, or never ends, stops here with its own
            // code. The position saved after the previous page stays, the watermark does not move, and the next run
            // resumes from there.
            switch (streamWalk.After(state.NextCursor, result.Value.HasMore ? result.Value.NextCursor : null))
            {
                case PagedReadStop.PageCap:
                    throw PagedReadLoop.GuardFailure("TRENDYOL_ORDER_STREAM_PAGE_CAP", $"Trendyol sipariş akışı bir okumada {TrendyolOrderStreamPageCap} sayfayı aştı; okuma kaldığı yerde durduruldu ve bu dönem okunmuş sayılmadı.");
                case PagedReadStop.RepeatedCursor:
                    throw PagedReadLoop.GuardFailure("TRENDYOL_ORDER_STREAM_PAGE_REPEATED", "Trendyol sipariş akışı okunan sayfayı yeniden sonraki sayfa olarak verdi; okuma durduruldu ve bu dönem okunmuş sayılmadı.");
            }
            if (result.Value.HasMore)
            {
                if (string.IsNullOrWhiteSpace(result.Value.NextCursor)) throw new InvalidOperationException("Trendyol stream hasMore=true ancak nextCursor boş döndü.");
                state = state with { NextCursor = result.Value.NextCursor };
            }
            else if (state.StoreFrontIndex + 1 < storefronts.Count)
            {
                state = state with { StoreFrontIndex = state.StoreFrontIndex + 1, NextCursor = null };
            }
            else if (state.Mode == OrderSyncMode.Baseline && HasEarlierOrderWindow(state))
            {
                state = state with { WindowIndex = state.WindowIndex + 1, StoreFrontIndex = 0, NextCursor = null };
                if (cursorResourceType == "ORDERS_RECOVERY")
                {
                    cursor.OpaqueCursor = SerializeOrderSyncState(state);
                    cursor.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                    throw new JobProcessingException(JobExecutionResult.Continue("Sipariş geçmişinin sonraki penceresi birkaç saniye içinde okunacak."));
                }
            }
            else
            {
                cursor.OpaqueCursor = null;
                cursor.LastModifiedWatermark = state.AnchorEnd;
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                completed = true;
                break;
            }
            cursor.OpaqueCursor = SerializeOrderSyncState(state);
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
        } while (!cancellationToken.IsCancellationRequested);

        // An order this poll had to insert while notifications are on, that was old enough for its
        // notification to have come and was placed before the notifications' last delivery, is one the
        // notifications missed. Orders placed while nothing was delivered (the server was off at night,
        // the webhook passive) are an outage the silence rule covers, not a miss.
        if (completed && run.InsertedCount > insertedBeforePoll)
        {
            var lastNotificationAt = await db.WebhookSubscriptions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "ACTIVE" && x.ExternalSubscriptionId != null)
                .MaxAsync(x => x.LastReceivedAt, cancellationToken);
            if (WebhookPollingPolicy.MissedOrderCutoff(now, lastNotificationAt) is { } missedOrderCutoff
                && await db.Orders.AsNoTracking().AnyAsync(order => order.TenantId == tenantId && order.ConnectionId == connectionId
                    && order.CreatedAt >= now && order.OrderedAt <= missedOrderCutoff, cancellationToken))
                await NoteWebhookGapAsync(tenantId, connectionId, now, cancellationToken);
        }

        // A full scan already read every storefront. An incremental run read only the hot storefronts above; it
        // then reads at most one quiet active storefront (TrendyolStorefrontPolicy.DueQuiet) and probes at most one
        // inactive storefront, each over the time since its last read, so the orders lock is not held waiting for
        // stream tokens (audit 2026-10-24 Q2).
        if (completed && state.Mode == OrderSyncMode.Incremental && !cancellationToken.IsCancellationRequested)
        {
            var separateReads = TrendyolStorefrontPolicy.DueQuiet(readStorefronts, storefrontActivity, now).ToList();
            if (TrendyolStorefrontPolicy.NextProbe(readStorefronts, storefrontActivity, now) is { } probe) separateReads.Add(probe);
            foreach (var storefront in separateReads)
            {
                if (cancellationToken.IsCancellationRequested || !await ReadStorefrontSinceLastRead(storefront)) break;
            }
        }
        if (completed && !cancellationToken.IsCancellationRequested)
            await RereadOrders(tenantId, connectionId, correlationId, cancellationToken);
        return true;

        // False when the read failed for another reason than a rejected storefront: it is retried on the next run
        // (its last read stays where it was) and the completed read above stays saved.
        async Task<bool> ReadStorefrontSinceLastRead(string storefront)
        {
            var previous = storefrontActivity.GetValueOrDefault(storefront);
            var start = TrendyolStorefrontPolicy.ProbeStart(previous, now, overlap);
            var received = false;
            string? nextCursor = null;
            var probeWalk = new PageWalk(TrendyolStorefrontProbePageGuard);
            var probeIssueKey = $"trendyol-storefront-read-guard:{connectionId}:{storefront}";
            do
            {
                TrackRequest();
                var page = await orders.PollAsync(Context(tenantId, connectionId, correlationId, $"order-sync-probe:{storefront}:{nextCursor ?? "0"}"), new(start, now, storefront), new(nextCursor, 200), cancellationToken);
                if (!page.IsSuccess)
                {
                    if (nextCursor is null && page.Error?.HttpStatus is 400 or 404)
                    {
                        NoteStorefront(storefront, TrendyolStorefrontPolicy.Rejected(storefront, previous, now));
                        await db.SaveChangesAsync(cancellationToken);
                        return true;
                    }
                    TrackResultFailure(page.Error);
                    return false;
                }
                await IngestOrderPageAsync(tenantId, connectionId, page.Value!, "order-contract", cancellationToken);
                received |= page.Value!.Items.Count > 0;
                var readWith = nextCursor;
                nextCursor = page.Value.HasMore && !string.IsNullOrWhiteSpace(page.Value.NextCursor) ? page.Value.NextCursor : null;
                // ISLEYICI-11: a storefront read that repeats its page or never ends stops like a failed read: its last
                // read stays where it was (so it is read again next run) and an issue names the stop.
                if (probeWalk.After(readWith, nextCursor) is { } stop)
                {
                    await RecordIssue(tenantId, probeIssueKey,
                        stop == PagedReadStop.PageCap ? "TRENDYOL_STOREFRONT_READ_PAGE_CAP" : "TRENDYOL_STOREFRONT_READ_PAGE_REPEATED",
                        stop == PagedReadStop.PageCap
                            ? $"Trendyol {storefront} vitrininin sipariş okuması {TrendyolStorefrontProbePageCap} sayfayı aştı; okuma durduruldu, vitrin sonraki okumada yeniden okunacak."
                            : $"Trendyol {storefront} vitrininin sipariş okuması okunan sayfayı yeniden sonraki sayfa olarak verdi; okuma durduruldu, vitrin sonraki okumada yeniden okunacak.",
                        cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                    return false;
                }
            } while (nextCursor is not null && !cancellationToken.IsCancellationRequested);
            if (cancellationToken.IsCancellationRequested) return false;
            NoteStorefront(storefront, TrendyolStorefrontPolicy.Read(previous, now, received));
            await ResolveIssue(tenantId, probeIssueKey, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
    }

    // What the panel knows about an order, so a targeted read asks its storefront and date window first.
    private Task<OrderLookupHint?> OrderHint(Guid tenantId, Guid connectionId, string externalOrderId, CancellationToken cancellationToken) =>
        db.Orders.AsNoTracking()
            .Where(order => order.TenantId == tenantId && order.ConnectionId == connectionId && order.ExternalOrderId == externalOrderId)
            .Select(order => (OrderLookupHint?)new OrderLookupHint(order.OrderedAt, order.LastRemoteModifiedAt, order.StoreFrontCode))
            .FirstOrDefaultAsync(cancellationToken);

    // T2-O8: a Trendyol order an integrity check refused stays behind the stream watermark once the window moves past
    // it. At the end of an order read it is read again by its number, a few times and far apart
    // (RejectedOrderRereadPolicy): an order Trendyol has completed since then lands, one still refused after the last
    // try is left to its open issue. An order of any marketplace stored with an unreadable product line is read again the
    // same way until a read fills the line in (UnreadableOrderLines). A read that fails for another reason waits for the
    // next run and counts no try.
    private async Task RereadOrders(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        run.OrderRereadRan = true;
        var now = timeProvider.GetUtcNow();
        var saved = await db.SyncCursors.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == RejectedOrderRereadPolicy.CursorResource, cancellationToken);
        var entries = RejectedOrderRereadPolicy.Note(RejectedOrderRereadPolicy.Parse(saved?.OpaqueCursor), run.RereadOrderIds, now, out var dropped);
        run.RereadOrderIds.Clear();
        // Orders an open refusal notice names and no entry holds (a refusal from before every marketplace re-read its
        // refused new orders, or one whose tries ran out a week ago) are read again now.
        var seeded = await RefusedOrdersToRereadAsync(tenantId, connectionId, entries.Select(entry => entry.OrderId).ToHashSet(StringComparer.Ordinal), now, cancellationToken);
        if (seeded.Count > 0) entries = RejectedOrderRereadPolicy.Seed(entries, seeded, now);
        if (dropped.Count > 0)
            await RecordIssue(tenantId, $"order-reread-full:{connectionId}", RejectedOrderRereadPolicy.OverflowIssueCode, RejectedOrderRereadPolicy.OverflowSummary(dropped), cancellationToken);
        if (saved is null && entries.Count == 0)
        {
            if (dropped.Count > 0 || db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(cancellationToken);
            return;
        }
        var cursor = saved ?? await Cursor(tenantId, connectionId, RejectedOrderRereadPolicy.CursorResource, cancellationToken);
        foreach (var entry in RejectedOrderRereadPolicy.Due(entries, now))
        {
            if (cancellationToken.IsCancellationRequested) break;
            TrackRequest();
            var read = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"order-reread:{entry.OrderId}"), entry.OrderId, await OrderHint(tenantId, connectionId, entry.OrderId, cancellationToken), cancellationToken);
            bool stillRefused;
            if (read.IsSuccess)
            {
                TrackReceived();
                await UpsertOrder(tenantId, connectionId, read.Value!, cancellationToken);
                stillRefused = run.RereadOrderIds.Remove(entry.OrderId);
            }
            // Not found, or found but refused again by the mapper (a contract error naming the refusal, e.g. still no
            // order date): a try is spent, so a record that stays broken leaves the list after its last try.
            else if (read.Error?.Class is AdapterErrorClass.NotFound or AdapterErrorClass.ContractViolation || read.Error?.HttpStatus == 404) stillRefused = true;
            else break;
            entries = RejectedOrderRereadPolicy.AfterRead(entries, entry, stillRefused, now);
        }
        run.RereadOrderIds.Clear();
        var serialized = RejectedOrderRereadPolicy.Serialize(entries);
        if (serialized == cursor.OpaqueCursor && db.Entry(cursor).State != EntityState.Added)
        {
            if (dropped.Count > 0 || db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(cancellationToken);
            return;
        }
        cursor.OpaqueCursor = serialized;
        cursor.LastSuccessAt = now;
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    // ISLEYICI-12 (16 Nov 2026): a read that runs no RereadOrders afterwards (a webhook ingest) still puts the orders it
    // refused on the re-read list, for their first try later (RejectedOrderRereadPolicy.Delays); the next order read
    // reads them when due. Nothing is read here.
    private async Task SaveRefusedOrdersForRereadAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        if (run.RereadOrderIds.Count == 0) return;
        var now = timeProvider.GetUtcNow();
        var cursor = await Cursor(tenantId, connectionId, RejectedOrderRereadPolicy.CursorResource, cancellationToken);
        var entries = RejectedOrderRereadPolicy.Note(RejectedOrderRereadPolicy.Parse(cursor.OpaqueCursor), run.RereadOrderIds, now, out var dropped);
        run.RereadOrderIds.Clear();
        if (dropped.Count > 0)
            await RecordIssue(tenantId, $"order-reread-full:{connectionId}", RejectedOrderRereadPolicy.OverflowIssueCode, RejectedOrderRereadPolicy.OverflowSummary(dropped), cancellationToken);
        var serialized = RejectedOrderRereadPolicy.Serialize(entries);
        if (serialized != cursor.OpaqueCursor)
        {
            cursor.OpaqueCursor = serialized;
            cursor.Version++;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task NoteWebhookGapAsync(Guid tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var registeredAt = await db.WebhookSubscriptions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "ACTIVE" && x.ExternalSubscriptionId != null)
            .Select(x => x.VerifiedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (!WebhookPollingPolicy.CountsAsGap(registeredAt, now)) return;
        var health = await Cursor(tenantId, connectionId, WebhookPollingPolicy.HealthCursorResource, cancellationToken);
        var gaps = ScheduledJobProducer.WebhookGaps(health.OpaqueCursor).Where(gap => now - gap <= WebhookPollingPolicy.GapWindow).Append(now).ToArray();
        health.OpaqueCursor = ScheduledJobProducer.WebhookGapsCursor(gaps);
        health.LastErrorAt = now;
        health.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record InvoiceDiscoveryCounts(int Stream, int Reconciliation, DateTimeOffset Since, string[] RecentReconciliationPackages);

    private async Task NoteInvoiceDiscoveryAsync(ShipmentPackage package, string source, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // One job may note several packages before it saves; reuse the cursor it already tracks.
        var cursor = db.SyncCursors.Local.FirstOrDefault(x => x.TenantId == package.TenantId && x.ConnectionId == package.ConnectionId && x.ResourceType == "TRENDYOL_INVOICE_DISCOVERY")
            ?? await Cursor(package.TenantId, package.ConnectionId, "TRENDYOL_INVOICE_DISCOVERY", cancellationToken);
        InvoiceDiscoveryCounts? counts = null;
        if (!string.IsNullOrWhiteSpace(cursor.OpaqueCursor))
        {
            try { counts = JsonSerializer.Deserialize<InvoiceDiscoveryCounts>(cursor.OpaqueCursor); }
            catch (JsonException) { }
        }
        counts ??= new(0, 0, now, []);
        counts = source == "reconciliation"
            ? counts with { Reconciliation = counts.Reconciliation + 1, RecentReconciliationPackages = [.. counts.RecentReconciliationPackages.Append(package.ExternalPackageId).TakeLast(20)] }
            : counts with { Stream = counts.Stream + 1 };
        cursor.OpaqueCursor = JsonSerializer.Serialize(counts);
        cursor.LastSuccessAt = now;
        cursor.Version++;
    }

    internal static string? TrendyolWebhookStorefront(string rawPackageJson)
    {
        try
        {
            using var package = JsonDocument.Parse(rawPackageJson);
            return package.RootElement.TryGetProperty("orderCountryCode", out var country) && country.ValueKind == JsonValueKind.String
                && country.GetString() is { Length: 2 } code && TrendyolReadStorefronts.Codes.Contains(code.ToUpperInvariant())
                ? code.ToUpperInvariant()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static Dictionary<string, TrendyolStorefrontActivity> ReadStorefrontActivity(SyncCursor cursor)
    {
        if (!string.IsNullOrWhiteSpace(cursor.OpaqueCursor))
        {
            try
            {
                if (JsonSerializer.Deserialize<Dictionary<string, TrendyolStorefrontActivity>>(cursor.OpaqueCursor) is { } saved)
                    return new(saved, StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException) { }
        }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    // Bump the durable state version so the next order sync starts a bounded
    // three-month baseline instead of continuing the old incremental cursor.
    private const string OrderSyncStateVersion = "orders-v4";
    private static readonly TimeSpan OrderStreamWindowSpan = TimeSpan.FromDays(14);

    // ISLEYICI-11 (16 Nov 2026): stream pages of 200 packages in one run (one million packages: far above a three-month
    // baseline of a large store), and pages of one storefront read; guards against a stream that never ends.
    internal const int TrendyolOrderStreamPageCap = 5_000;
    private static readonly PageGuard TrendyolOrderStreamPageGuard = new(MaxPages: TrendyolOrderStreamPageCap, StopOnRepeatedCursor: true);
    internal const int TrendyolStorefrontProbePageCap = 1_000;
    private static readonly PageGuard TrendyolStorefrontProbePageGuard = new(MaxPages: TrendyolStorefrontProbePageCap, StopOnRepeatedCursor: true);
    // Stream reads are spaced (5 s between starts) and capped (30/min) per seller by
    // TrendyolResilienceHandler; sleeping here as well only added idle time to every page.
    private const int DefaultOrderSyncOverlapSeconds = 120;
    private enum OrderSyncMode { Baseline, Incremental }
    // StoreFronts is the list this run visits; a state saved before the list existed visits every storefront.
    private sealed record OrderSyncState(string Version, OrderSyncMode Mode, DateTimeOffset AnchorEnd, DateTimeOffset StartAt, int WindowIndex, string? NextCursor, int StoreFrontIndex = 0, string[]? StoreFronts = null);

    private static IReadOnlyList<string> OrderSyncStorefronts(OrderSyncState state) =>
        state.StoreFronts is { Length: > 0 } storefronts ? storefronts : TrendyolReadStorefronts.Codes;

    private static OrderSyncState ReadOrderSyncState(SyncCursor cursor, DateTimeOffset now, TimeSpan overlap, bool allowBaseline, bool forceBaseline = false,
        IReadOnlyList<string>? activeStorefronts = null, IReadOnlyList<string>? readableStorefronts = null)
    {
        // A baseline already under way continues even when a baseline is asked for again (an
        // empty store, a retry after a failure); restarting it would read its first window forever.
        if (ResumableCursor.Read<OrderSyncState>(cursor.OpaqueCursor, state => state is { Version: OrderSyncStateVersion, WindowIndex: >= 0, StoreFrontIndex: >= 0 } && state.StoreFrontIndex < OrderSyncStorefronts(state).Count
                && (!forceBaseline || state.Mode == OrderSyncMode.Baseline && now - state.AnchorEnd < TimeSpan.FromDays(1))) is { } resumed)
            return resumed;

        var baseline = allowBaseline && (forceBaseline || (cursor.LastSuccessAt is null && cursor.LastModifiedWatermark is null && string.IsNullOrWhiteSpace(cursor.OpaqueCursor)));
        var anchor = now;
        var oldestAvailable = TrendyolOrderHistoryPolicy.StreamInitialStart(anchor);
        var watermark = cursor.LastModifiedWatermark ?? cursor.LastSuccessAt ?? anchor.Subtract(OrderStreamWindowSpan);
        if (watermark > anchor) watermark = anchor;
        var start = baseline ? oldestAvailable : watermark.Subtract(overlap);
        if (start < oldestAvailable) start = oldestAvailable;

        // A normal run reads only the changes since the last completed anchor,
        // plus the configured safety overlap. A single stream request may span
        // at most 14 days, so a larger gap reuses durable multi-window state.
        var mode = baseline || anchor - start > OrderStreamWindowSpan
            ? OrderSyncMode.Baseline
            : OrderSyncMode.Incremental;
        // A multi-window scan reads every storefront the seller may have; a normal run only the active ones.
        var storefronts = mode == OrderSyncMode.Baseline ? readableStorefronts : activeStorefronts;
        return new(OrderSyncStateVersion, mode, anchor, start, 0, null, 0, storefronts is { Count: > 0 } ? [.. storefronts] : null);
    }

    private static (DateTimeOffset Start, DateTimeOffset End) OrderWindow(OrderSyncState state)
    {
        if (state.Mode == OrderSyncMode.Incremental)
            return (state.StartAt, state.AnchorEnd);

        var end = state.AnchorEnd - TimeSpan.FromTicks(state.WindowIndex * (OrderStreamWindowSpan.Ticks + TimeSpan.TicksPerMillisecond));
        var start = end - OrderStreamWindowSpan;
        return (start < state.StartAt ? state.StartAt : start, end);
    }

    private static bool HasEarlierOrderWindow(OrderSyncState state)
    {
        if (state.Mode != OrderSyncMode.Baseline) return false;
        var nextEnd = state.AnchorEnd - TimeSpan.FromTicks((state.WindowIndex + 1L) * (OrderStreamWindowSpan.Ticks + TimeSpan.TicksPerMillisecond));
        return nextEnd >= state.StartAt;
    }

    private static string SerializeOrderSyncState(OrderSyncState state) => ResumableCursor.Serialize(state);
}
