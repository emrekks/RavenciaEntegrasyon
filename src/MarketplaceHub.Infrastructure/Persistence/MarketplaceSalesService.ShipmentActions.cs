using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Infrastructure.Persistence.InvoiceLabelPolicy;
using static MarketplaceHub.Infrastructure.Persistence.OrderSnapshotReader;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceSalesService
{
    // The first print is kept; printing again later does not move it. A plain column update: the
    // order's version and the worker's concurrent writes are not touched. The first print is announced
    // in the same transaction ("orders" outbox event, as a saved order is), so the order lists, the
    // "Etiket Durumu" filter and the counters open on other computers read it again instead of waiting
    // for their next poll.
    public async Task<bool> MarkLabelPrintedAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var order = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == orderId)
            .Select(x => new { x.Version }).SingleOrDefaultAsync(cancellationToken);
        if (order is null) return false;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var marked = await db.Orders.Where(x => x.TenantId == tenantId && x.Id == orderId && x.LabelPrintedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LabelPrintedAt, now), cancellationToken);
        if (marked > 0)
        {
            db.IntegrationOutboxEvents.Add(LabelPrintedOutboxEvent(tenantId, orderId, order.Version, now));
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    internal static IntegrationOutboxEvent LabelPrintedOutboxEvent(Guid tenantId, Guid orderId, long version, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ResourceType = "orders",
        OperationType = "updated",
        AggregateType = "Order",
        AggregateId = orderId,
        AggregateVersion = version,
        PayloadJson = JsonSerializer.Serialize(new { aggregateType = "Order", aggregateId = orderId, version, labelPrinted = true }),
        CreatedAt = now,
        NextAttemptAt = now
    };

    public async Task<ServiceResult<Guid>> EnqueueShipmentActionAsync(Guid tenantId, Guid packageId, long expectedVersion, ShipmentActionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken); if (package is null) return NotFound<Guid>(); if (package.Version != expectedVersion) return Precondition<Guid>(package.Version);
        var action = command.Action.Trim().ToUpperInvariant();
        var commandPayload = command.PayloadJson;
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (platform == "PAZARAMA" && action is "PICKING" or "NOT_SUPPLIED" && IsEmptyJsonObject(commandPayload))
        {
            // Pazarama order items are GUIDs; the adapter sends one status request per item.
            var pazaramaLines = await PackageLinesAsync(tenantId, package.Id, cancellationToken);
            if (pazaramaLines.Count == 0)
                return ServiceResult<Guid>.Fail("PICKING_LINES_REQUIRED", "Pazarama işlemi için pakette sipariş kalemi bulunamadı.", 422);
            commandPayload = JsonSerializer.Serialize(new { lines = pazaramaLines.Select(line => new { lineId = line.ExternalLineId, quantity = (int)line.AllocatedQuantity }), status = action == "PICKING" ? "Picking" : "NotSupplied" });
        }
        if (platform != "PAZARAMA" && action == "PICKING" && IsEmptyJsonObject(commandPayload))
        {
            var packageLines = await PackageLinesAsync(tenantId, package.Id, cancellationToken);
            if (packageLines.Count == 0 || packageLines.Any(line => !long.TryParse(line.ExternalLineId, out _) || line.AllocatedQuantity != decimal.Truncate(line.AllocatedQuantity) || line.AllocatedQuantity > int.MaxValue))
                return ServiceResult<Guid>.Fail("PICKING_LINES_REQUIRED", "Paket satırları Trendyol Picking isteği için hazırlanamadı.", 422);
            commandPayload = JsonSerializer.Serialize(new { lines = packageLines.Select(line => new { lineId = long.Parse(line.ExternalLineId), quantity = (int)line.AllocatedQuantity }), @params = new { }, status = "Picking" });
        }
        var validation = platform == "HEPSIBURADA"
            ? ValidateHepsiburadaShipmentAction(package, action, commandPayload)
            : platform == "N11"
                ? ValidateN11ShipmentAction(package, action, commandPayload)
                : platform == "PAZARAMA"
                    ? ValidatePazaramaShipmentAction(package, action, commandPayload)
                    : ValidateShipmentAction(package, action, commandPayload);
        if (validation is not null) return ServiceResult<Guid>.Fail(validation.Code, validation.Message, validation.Status, validation.FieldErrors);
        var stage = await IsStageConnection(tenantId, package.ConnectionId, cancellationToken);
        if (!stage && !await IsProductionConnection(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Kargo işlemi yalnız test ya da canlı mağaza bağlantısında yapılabilir.", 422);
        if (!stage && !await WritesEnabled(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        var writePolicy = await ExternalWritePolicyAsync(tenantId, package.ConnectionId, MarketplaceExternalWritePolicies.Shipment, cancellationToken);
        if (!writePolicy.Enabled) return ServiceResult<Guid>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "Kargo dış yazma akışı kapalı.", 422);
        var normalizedKey = idempotencyKey.Trim();
        var commandHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{action}\n{commandPayload}")));
        var dedup = $"shipment-action:{package.Id}:v{package.Version}:{action}:{commandHash}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))}";
        var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.ShipmentAction && x.EffectIdempotencyKey == normalizedKey, cancellationToken); if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var jobId = Guid.CreateVersion7(); var now = timeProvider.GetUtcNow(); var payload = JsonSerializer.Serialize(new ShipmentActionJobPayload(jobId, package.Id, action, commandPayload)); var job = NewJob(tenantId, package.ConnectionId, MarketplaceJobTypes.ShipmentAction, dedup, payload, correlationId); job.Id = jobId; job.EffectIdempotencyKey = normalizedKey; job.AvailableAt = now.AddSeconds(stage ? 0 : writePolicy.IntervalSeconds); job.CreatedAt = now; db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(jobId);
    }

    public async Task<ServiceResult<ShipmentView>> ProcessShipmentInstantAsync(Guid tenantId, Guid packageId, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        const string action = "PICKING";
        var package = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
        if (package is null) return NotFound<ShipmentView>();
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (platform is not ("TRENDYOL" or "N11" or "PAZARAMA"))
            return ServiceResult<ShipmentView>.Fail("SHIPMENT_ACTION_UNSUPPORTED", "Bu pazaryerinde siparişler panelden anlık işleme alınamıyor.", 422);
        var effectType = $"{platform}_INSTANT_PICKING";
        var normalizedKey = idempotencyKey.Trim();
        // A resend of a request that already went through answers with the package as it is now,
        // before the version and state checks the first request itself moved on.
        var effects = new ExternalEffectGuard(db, timeProvider);
        var existing = await effects.FindAsync(tenantId, effectType, normalizedKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.TargetId is { } target && target != package.Id) return ServiceResult<ShipmentView>.Fail("IDEMPOTENCY_KEY_CONFLICT", "Bu istek anahtarı başka bir paket için kullanılmış; sayfayı yenileyip yeniden deneyin.", 409);
            if (ExternalEffectGuard.Gate(existing) == ExternalEffectGate.Open) return Verifying<ShipmentView>();
            var replayOrderNumber = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => x.OrderNumber).SingleOrDefaultAsync(cancellationToken);
            return replayOrderNumber is null ? NotFound<ShipmentView>() : ServiceResult<ShipmentView>.Ok(Map(package, replayOrderNumber));
        }
        if (package.Version != expectedVersion) return Precondition<ShipmentView>(package.Version);
        if (package.Status != ShipmentPackageStatus.New) return ServiceResult<ShipmentView>.Fail("SHIPMENT_ACTION_NOT_ALLOWED", "Yalnız yeni durumdaki paketler işleme alınabilir.", 409);

        var packageLines = await PackageLinesAsync(tenantId, package.Id, cancellationToken);
        string payload;
        ServiceError? validation;
        if (platform == "PAZARAMA")
        {
            // Pazarama order items are GUIDs; the adapter sends one status request per item.
            if (packageLines.Count == 0 || packageLines.Any(line => string.IsNullOrWhiteSpace(line.ExternalLineId) || line.AllocatedQuantity != decimal.Truncate(line.AllocatedQuantity) || line.AllocatedQuantity > int.MaxValue))
                return ServiceResult<ShipmentView>.Fail("PICKING_LINES_REQUIRED", "Paket satırları Pazarama işleme alma isteği için hazırlanamadı.", 422);
            payload = JsonSerializer.Serialize(new { lines = packageLines.Select(line => new { lineId = line.ExternalLineId, quantity = (int)line.AllocatedQuantity }), status = "Picking" });
            validation = ValidatePazaramaShipmentAction(package, action, payload);
        }
        else
        {
            if (packageLines.Count == 0 || packageLines.Any(line => !long.TryParse(line.ExternalLineId, out _) || line.AllocatedQuantity != decimal.Truncate(line.AllocatedQuantity) || line.AllocatedQuantity > int.MaxValue))
                return ServiceResult<ShipmentView>.Fail("PICKING_LINES_REQUIRED", "Paket satırları işleme alma isteği için hazırlanamadı.", 422);
            payload = JsonSerializer.Serialize(new { lines = packageLines.Select(line => new { lineId = long.Parse(line.ExternalLineId), quantity = (int)line.AllocatedQuantity }), @params = new { }, status = "Picking" });
            validation = platform == "N11" ? ValidateN11ShipmentAction(package, action, payload) : ValidateShipmentAction(package, action, payload);
        }
        if (validation is not null) return ServiceResult<ShipmentView>.Fail(validation.Code, validation.Message, validation.Status, validation.FieldErrors);
        var stage = await IsStageConnection(tenantId, package.ConnectionId, cancellationToken);
        if (!stage && !await IsProductionConnection(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<ShipmentView>.Fail("ENVIRONMENT_INVALID", "Kargo işlemi yalnız test ya da canlı mağaza bağlantısında yapılabilir.", 422);
        if (!stage && !await WritesEnabled(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<ShipmentView>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        if (!await ExternalWritePolicyEnabledAsync(tenantId, package.ConnectionId, MarketplaceExternalWritePolicies.Shipment, cancellationToken)) return ServiceResult<ShipmentView>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "Kargo dış yazma akışı kapalı.", 422);
        var order = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == package.OrderId, cancellationToken);
        if (order is null) return NotFound<ShipmentView>();
        var orderNumber = order.OrderNumber;

        // ISLEYICI-10: the record is saved, the request sent and its answer sorted by the guard.
        var effect = effects.New(tenantId, effectType, normalizedKey, package.ConnectionId, package.Id, "PICKING");
        ExternalEffectSend<PackageActionResult> sent;
        using (TrendyolQuotaLane.Instant())
            sent = await effects.SendAsync(effect, () => orders.ExecutePackageActionAsync(new AdapterContext(tenantId, package.ConnectionId, correlationId, normalizedKey, timeProvider.GetUtcNow().AddSeconds(30)), new(package.ExternalPackageId, action, payload, order.StoreFrontCode), cancellationToken), cancellationToken);
        if (!sent.Accepted)
        {
            var error = sent.Error;
            if (sent.Outcome == ExternalEffectSendOutcome.Uncertain) return await VerifyLaterAsync<ShipmentView>(effect, cancellationToken);
            await effects.ReleaseAsync(effect, cancellationToken);
            // The marketplace quota was full (or the panel's own circuit was open) and nothing was sent: the action waits
            // in the user lane instead.
            if (error.Class == AdapterErrorClass.RateLimit)
                return await QueueInsteadAsync<ShipmentView>(EnqueueShipmentActionAsync(tenantId, package.Id, package.Version, new ShipmentActionCommand(action, "{}"), normalizedKey, correlationId, cancellationToken));
            return ServiceResult<ShipmentView>.Fail("PICKING_FAILED", error.SafeMessage, error.HttpStatus is >= 400 and <= 599 ? error.HttpStatus.Value : 502);
        }

        // The marketplace accepted the write: record that first and on its own,
        // so a conflict while projecting it locally can never leave the effect
        // open (and the same key answering "verifying" forever).
        var appliedAt = effect.CompletedAt!.Value;
        effect.Outcome = "APPLIED";
        await db.SaveChangesAsync(cancellationToken);

        // The local projection runs under the connection's "orders" lock, as the order jobs'
        // writes do (see OrdersProjectionLockAsync); the remote call above did not hold it.
        await using var projectionLock = await OrdersProjectionLockAsync(package.ConnectionId, cancellationToken);
        for (var attempt = 0; ; attempt++)
        {
            if (package.Status == ShipmentPackageStatus.New)
            {
                package.Status = ShipmentPackageStatus.Processing;
                package.RawStatus = "Processing";
                // StatusOccurredAt stays the marketplace's own event time: a
                // local clock here would make the marketplace's Picking event
                // (with its cargo and invoice fields) look older and be dropped.
                package.UpdatedAt = appliedAt;
                package.Version++;
                db.OrderStatusHistory.Add(new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, PackageId = package.Id, CanonicalStatus = "PROCESSING", RawStatus = PackageIngestionSafety.InstantPickingRawStatus, SourceEventId = $"instant-picking:{effect.Id:N}", OccurredAt = appliedAt, RecordedAt = appliedAt });
            }
            // Orders are filtered by DerivedStatus while the counters are calculated
            // from shipment packages. Keep both read models in sync before returning
            // so the order moves between tabs in the same response.
            var orderPackages = await db.ShipmentPackages.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.OrderId == order.Id)
                .ToListAsync(cancellationToken);
            var current = orderPackages.Select(x => x.Id == package.Id ? package : x).ToList();
            if (ShipmentPackageSupersession.StatusSources(current) is { } statusSources)
            {
                var derivedStatus = Wire(ShipmentPackageStatusPolicy.Aggregate(statusSources));
                if (!string.Equals(order.DerivedStatus, derivedStatus, StringComparison.Ordinal))
                {
                    order.DerivedStatus = derivedStatus;
                    order.UpdatedAt = appliedAt;
                    order.Version++;
                }
            }
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return ServiceResult<ShipmentView>.Ok(Map(package, orderNumber));
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                // A sync read updated the package or order meanwhile; project
                // the accepted action once more on the fresh rows.
                db.ChangeTracker.Clear();
                var freshPackage = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
                var freshOrder = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == package.OrderId, cancellationToken);
                if (freshPackage is null || freshOrder is null) return NotFound<ShipmentView>();
                package = freshPackage;
                order = freshOrder;
            }
            catch (DbUpdateConcurrencyException)
            {
                // The action is applied on the marketplace; a targeted order read
                // brings the package into the same state.
                db.ChangeTracker.Clear();
                await QueueProjectionReadAsync(tenantId, package.ConnectionId, orderNumber, correlationId, cancellationToken);
                var latest = await db.ShipmentPackages.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
                return ServiceResult<ShipmentView>.Ok(Map(latest, orderNumber));
            }
        }
    }

    // How long an instant action waits for the order jobs' lock before projecting without it.
    // The remote write is already applied and recorded; holding the request longer only delays
    // the answer, and an optimistic projection (retried once, then left to a targeted order read)
    // is still safe.
    internal static readonly TimeSpan InstantProjectionLockWait = TimeSpan.FromSeconds(3);

    // The lock is taken around the local projection only, not around the marketplace call: an
    // order job may hold it for a whole page read, and the remote call itself may take up to
    // 30 seconds; waiting for both would hold either the user or the order lane far too long.
    private Task<MarketplaceSyncExecutionLock?> OrdersProjectionLockAsync(Guid connectionId, CancellationToken cancellationToken) =>
        MarketplaceSyncExecutionLock.AcquireGroupAsync(db, connectionId, MarketplaceSyncExecutionLock.OrdersGroup, InstantProjectionLockWait, timeProvider, cancellationToken);

    private async Task QueueProjectionReadAsync(Guid tenantId, Guid connectionId, string orderNumber, string correlationId, CancellationToken cancellationToken)
    {
        // Best effort: the scheduled order reads reach the same state later anyway.
        var queued = await EnqueueOrderSyncAsync(tenantId, connectionId, orderNumber, false, correlationId, cancellationToken);
        if (!queued.Succeeded) db.ChangeTracker.Clear();
    }

    public async Task<ServiceResult<ShipmentView>> ChangeCargoProviderInstantAsync(Guid tenantId, Guid packageId, long expectedVersion, ShipmentActionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        const string effectType = "TRENDYOL_INSTANT_CARGO_PROVIDER_CHANGE";
        var action = command.Action.Trim().ToUpperInvariant();
        if (action != "CHANGE_CARGO_PROVIDER") return ServiceResult<ShipmentView>.Fail("SHIPMENT_ACTION_UNSUPPORTED", "Bu anlık işlem yalnız kargo firması değişikliği için kullanılabilir.", 422);

        var package = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
        if (package is null) return NotFound<ShipmentView>();
        // Only Trendyol changes a package's carrier through this request; the other marketplaces
        // have their own flow (Hepsiburada) or none (audit 2026-10-20 S17).
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (platform != "TRENDYOL") return ServiceResult<ShipmentView>.Fail("CARGO_PROVIDER_CHANGE_UNSUPPORTED", "Kargo firması değişikliği panelden yalnız Trendyol paketlerinde yapılabilir.", 422);

        var normalizedKey = idempotencyKey.Trim();
        // A resend of a change that went through answers with the carrier that change asked for
        // (its recorded expected value), before the version check the change itself moved on.
        var effects = new ExternalEffectGuard(db, timeProvider);
        var existing = await effects.FindAsync(tenantId, effectType, normalizedKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.TargetId is { } target && target != package.Id) return ServiceResult<ShipmentView>.Fail("IDEMPOTENCY_KEY_CONFLICT", "Bu istek anahtarı başka bir paket için kullanılmış; sayfayı yenileyip yeniden deneyin.", 409);
            if (ExternalEffectGuard.Gate(existing) == ExternalEffectGate.Open) return Verifying<ShipmentView>();
            var replayOrderNumber = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => x.OrderNumber).SingleOrDefaultAsync(cancellationToken);
            if (replayOrderNumber is null) return NotFound<ShipmentView>();
            // A change closed as unresolved was never confirmed (Q-2): the package is shown as it is, not with that carrier.
            return string.IsNullOrWhiteSpace(existing.ExpectedValue) || ExternalEffectGuard.Gate(existing) == ExternalEffectGate.Unresolved
                ? ServiceResult<ShipmentView>.Ok(Map(package, replayOrderNumber))
                : await ProjectCargoProviderAsync(tenantId, package, existing.ExpectedValue, replayOrderNumber, correlationId, cancellationToken);
        }
        if (package.Version != expectedVersion) return Precondition<ShipmentView>(package.Version);

        var validation = ValidateShipmentAction(package, action, command.PayloadJson);
        if (validation is not null) return ServiceResult<ShipmentView>.Fail(validation.Code, validation.Message, validation.Status, validation.FieldErrors);

        string cargoProvider;
        try
        {
            using var document = JsonDocument.Parse(command.PayloadJson);
            cargoProvider = document.RootElement.GetProperty("cargoProvider").GetString()?.Trim() ?? string.Empty;
        }
        catch (JsonException)
        {
            return ServiceResult<ShipmentView>.Fail("SHIPMENT_ACTION_PAYLOAD_INVALID", "Kargo firması bilgisi geçerli JSON olmalıdır.", 422);
        }

        var stage = await IsStageConnection(tenantId, package.ConnectionId, cancellationToken);
        if (!stage && !await IsProductionConnection(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<ShipmentView>.Fail("ENVIRONMENT_INVALID", "Kargo işlemi yalnız test ya da canlı mağaza bağlantısında yapılabilir.", 422);
        if (!stage && !await WritesEnabled(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<ShipmentView>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        if (!await ExternalWritePolicyEnabledAsync(tenantId, package.ConnectionId, MarketplaceExternalWritePolicies.Shipment, cancellationToken)) return ServiceResult<ShipmentView>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "Kargo dış yazma akışı kapalı.", 422);

        var orderNumber = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => x.OrderNumber).SingleOrDefaultAsync(cancellationToken);
        if (orderNumber is null) return NotFound<ShipmentView>();

        // ISLEYICI-10/6: the storefront is read before the record is saved, so nothing but the request runs between the
        // save and the send; the guard saves, sends and sorts the answer.
        var storeFrontCode = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => x.StoreFrontCode).SingleOrDefaultAsync(cancellationToken);
        var effect = effects.New(tenantId, effectType, normalizedKey, package.ConnectionId, package.Id, "CHANGE_CARGO_PROVIDER", cargoProvider);
        ExternalEffectSend<PackageActionResult> sent;
        using (TrendyolQuotaLane.Instant())
            sent = await effects.SendAsync(effect, () => orders.ExecutePackageActionAsync(
                new AdapterContext(tenantId, package.ConnectionId, correlationId, normalizedKey, timeProvider.GetUtcNow().AddSeconds(30)),
                new(package.ExternalPackageId, action, command.PayloadJson, storeFrontCode),
                cancellationToken), cancellationToken);
        if (!sent.Accepted)
        {
            var error = sent.Error;
            if (sent.Outcome == ExternalEffectSendOutcome.Uncertain) return await VerifyLaterAsync<ShipmentView>(effect, cancellationToken);

            await effects.ReleaseAsync(effect, cancellationToken);
            if (error.Class == AdapterErrorClass.RateLimit)
                return await QueueInsteadAsync<ShipmentView>(EnqueueShipmentActionAsync(tenantId, package.Id, package.Version, command, normalizedKey, correlationId, cancellationToken));
            var status = error.HttpStatus is >= 400 and <= 599 ? error.HttpStatus.Value : 502;
            return ServiceResult<ShipmentView>.Fail("CARGO_PROVIDER_CHANGE_FAILED", error.SafeMessage, status);
        }

        // Record the accepted write on its own first (see ProcessShipmentInstantAsync).
        effect.Outcome = "APPLIED";
        await db.SaveChangesAsync(cancellationToken);
        return await ProjectCargoProviderAsync(tenantId, package, cargoProvider, orderNumber, correlationId, cancellationToken);
    }

    // Writes an accepted cargo provider change onto the package under the order jobs' lock
    // (see OrdersProjectionLockAsync): retried once on fresh rows, then left to a targeted order read.
    private async Task<ServiceResult<ShipmentView>> ProjectCargoProviderAsync(Guid tenantId, ShipmentPackage package, string cargoProvider, string orderNumber, string correlationId, CancellationToken cancellationToken)
    {
        if (string.Equals(package.CargoProviderExternalId, cargoProvider, StringComparison.OrdinalIgnoreCase))
            return ServiceResult<ShipmentView>.Ok(Map(package, orderNumber));
        var packageId = package.Id;
        var connectionId = package.ConnectionId;
        await using var projectionLock = await OrdersProjectionLockAsync(connectionId, cancellationToken);
        for (var attempt = 0; ; attempt++)
        {
            if (!string.Equals(package.CargoProviderExternalId, cargoProvider, StringComparison.OrdinalIgnoreCase))
            {
                package.CargoProviderExternalId = cargoProvider;
                package.UpdatedAt = timeProvider.GetUtcNow();
                package.Version++;
            }
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return ServiceResult<ShipmentView>.Ok(Map(package, orderNumber));
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                db.ChangeTracker.Clear();
                var fresh = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
                if (fresh is null) return NotFound<ShipmentView>();
                package = fresh;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                await QueueProjectionReadAsync(tenantId, connectionId, orderNumber, correlationId, cancellationToken);
                var latest = await db.ShipmentPackages.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
                return ServiceResult<ShipmentView>.Ok(Map(latest, orderNumber));
            }
        }
    }

    public async Task<ServiceResult<Guid>> EnqueueCommonLabelAsync(Guid tenantId, Guid packageId, long expectedVersion, int boxQuantity, decimal volumetricHeight, string idempotencyKey, string correlationId, CancellationToken cancellationToken, string format = "ZPL")
    {
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken); if (package is null) return NotFound<Guid>(); if (package.Version != expectedVersion) return Precondition<Guid>(package.Version);
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        var hepsiburada = platform == "HEPSIBURADA";
        var requestedFormat = hepsiburada ? (format ?? string.Empty).Trim().ToUpperInvariant() : "ZPL";
        if (hepsiburada && !HepsiburadaLabelFormats.Contains(requestedFormat, StringComparer.OrdinalIgnoreCase))
            return Invalid<Guid>("format", "Hepsiburada etiket biçimi ZPL, BASE64ZPL, PDF, PNG veya JPG olmalıdır.");
        if (string.IsNullOrWhiteSpace(package.ExternalPackageId) || !hepsiburada && string.IsNullOrWhiteSpace(package.CargoTrackingNumber)) return ServiceResult<Guid>.Fail("CARGO_TRACKING_REQUIRED", "Etiket için paket veya kargo takip numarası gerekir.", 422);
        if (hepsiburada ? !CommonLabelCarrierPolicy.SupportsHepsiburada(package.CargoProviderExternalId) : !CommonLabelCarrierPolicy.Supports(package.CargoProviderExternalId)) return ServiceResult<Guid>.Fail("COMMON_LABEL_CARRIER_UNSUPPORTED", hepsiburada ? "Hepsiburada ortak etiket yalnız HepsiJet ve Aras paketlerinde desteklenir." : "Ortak etiket yalnız Trendyol öder Aras Kargo veya TEX gönderilerinde kullanılabilir.", 422);
        if (boxQuantity is < 1 or > 50) return Invalid<Guid>("boxQuantity", "boxQuantity 1-50 arasında olmalıdır.");
        if (volumetricHeight < 0 || volumetricHeight > 10000) return Invalid<Guid>("volumetricHeight", "volumetricHeight 0-10000 arasında olmalıdır.");
        var stage = await IsStageConnection(tenantId, package.ConnectionId, cancellationToken);
        if (!stage && !await IsProductionConnection(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Ortak etiket yalnız test ya da canlı mağaza bağlantısında yapılabilir.", 422);
        if (hepsiburada)
        {
            var supportedFormats = stage
                ? HepsiburadaLabelFormats
                : await CapabilityValues(tenantId, package.ConnectionId, MarketplaceCapabilities.LabelRead, "formats", cancellationToken);
            if (!supportedFormats.Contains(requestedFormat, StringComparer.OrdinalIgnoreCase))
                return ServiceResult<Guid>.Fail("LABEL_READ_CAPABILITY_REQUIRED", $"Production Hepsiburada etiket okuması için doğrulanmış LABEL_READ/{requestedFormat} kanıtı gerekir.", 422);
        }
        if (!hepsiburada)
        {
            if (!stage && !await WritesEnabled(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
            if (!await ExternalWritePolicyEnabledAsync(tenantId, package.ConnectionId, MarketplaceExternalWritePolicies.Shipment, cancellationToken)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "Kargo dış yazma akışı kapalı.", 422);
        }
        var normalizedKey = idempotencyKey.Trim(); var existingAttempt = await db.ShipmentDocumentAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == normalizedKey, cancellationToken);
        if (existingAttempt is not null)
        {
            var existingJob = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.CommonLabel && x.EffectIdempotencyKey == normalizedKey, cancellationToken);
            return existingJob is null ? ServiceResult<Guid>.Fail("LABEL_ATTEMPT_STATE_CONFLICT", "Etiket denemesi var ancak işlem kaydı bulunamadı; etiketi yeniden isteyin.", 409) : ServiceResult<Guid>.Ok(existingJob.Id);
        }
        var now = timeProvider.GetUtcNow(); var jobId = Guid.CreateVersion7(); var payload = JsonSerializer.Serialize(new CommonLabelJobPayload(jobId, package.Id, "SUBMIT", boxQuantity, decimal.Round(volumetricHeight, 2, MidpointRounding.ToEven), now, now.AddMinutes(15), requestedFormat));
        var job = NewJob(tenantId, package.ConnectionId, MarketplaceJobTypes.CommonLabel, $"common-label:{package.Id}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))}", payload, correlationId); job.Id = jobId; job.EffectIdempotencyKey = normalizedKey; job.MaxAttempts = 30;
        db.ShipmentDocumentAttempts.Add(new ShipmentDocumentAttempt { Id = Guid.CreateVersion7(), TenantId = tenantId, PackageId = package.Id, IdempotencyKey = normalizedKey, Status = "PENDING", CreatedAt = now }); db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(jobId);
    }

    public async Task<ServiceResult<Guid>> EnqueueLabelCapabilityProbeAsync(Guid tenantId, Guid actorUserId, Guid packageId, long expectedVersion, string capabilityCode, int boxQuantity, decimal volumetricHeight, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
        if (package is null) return NotFound<Guid>();
        if (package.Version != expectedVersion) return Precondition<Guid>(package.Version);
        if (string.IsNullOrWhiteSpace(package.CargoTrackingNumber)) return ServiceResult<Guid>.Fail("CARGO_TRACKING_REQUIRED", "Etiket testi için kargo takip numarası gerekir.", 422);
        var capability = capabilityCode.Trim().ToUpperInvariant();
        if (capability is not (MarketplaceCapabilities.LabelRead or MarketplaceCapabilities.LabelWrite)) return Invalid<Guid>("capabilityCode", "Bu Stage canary yalnız LABEL_READ veya LABEL_WRITE için kullanılabilir.");
        if (boxQuantity is < 1 or > 50 || volumetricHeight <= 0 || volumetricHeight > 10000) return Invalid<Guid>("probe", "Koli adedi 1-50, desi/hacim 0-10000 arasında olmalıdır.");
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == package.ConnectionId && x.PlatformCode == "TRENDYOL", cancellationToken);
        if (connection is null || !string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase)) return ServiceResult<Guid>.Fail("STAGE_CONNECTION_REQUIRED", "Bu test işlemi yalnız Trendyol test mağazası bağlantısında çalışır.", 422);
        if (capability == MarketplaceCapabilities.LabelWrite && package.Status.ToString() is not ("ReadyToShip" or "Processing")) return ServiceResult<Guid>.Fail("STAGE_LABEL_PACKAGE_NOT_READY", "Etiket testi yalnız hazırlanan ya da gönderime hazır test paketlerinde çalışır.", 422);
        if (capability == MarketplaceCapabilities.LabelWrite && !CommonLabelCarrierPolicy.Supports(package.CargoProviderExternalId)) return ServiceResult<Guid>.Fail("COMMON_LABEL_CARRIER_UNSUPPORTED", "Etiket testi yalnız Trendyol'un ödediği Aras Kargo veya TEX test paketlerinde çalışır.", 422);
        var normalizedKey = idempotencyKey.Trim();
        var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.CapabilityProbe && x.EffectIdempotencyKey == normalizedKey, cancellationToken);
        if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var now = timeProvider.GetUtcNow(); var jobId = Guid.CreateVersion7(); var payload = JsonSerializer.Serialize(new CapabilityProbeJobPayload(jobId, package.Id, actorUserId, capability, boxQuantity, decimal.Round(volumetricHeight, 2, MidpointRounding.ToEven), now, now.AddMinutes(15)));
        var job = NewJob(tenantId, package.ConnectionId, MarketplaceJobTypes.CapabilityProbe, $"stage-capability-probe:{package.Id}:{capability}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))}", payload, correlationId);
        job.Id = jobId; job.EffectIdempotencyKey = normalizedKey; job.MaxAttempts = 1; db.IntegrationJobs.Add(job);
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = actorUserId, Action = "CAPABILITY_STAGE_PROBE_ENQUEUED", TargetType = "PlatformCapability", TargetId = package.ConnectionId.ToString("D"), Reason = $"{capability}:package:{package.Id:D}", CorrelationId = correlationId, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(jobId);
    }

    private static readonly IReadOnlyList<string> ShipmentActions = ["PICKING", "INVOICED", "TRACKING_NUMBER", "CANCEL_ITEMS", "SPLIT", "MULTI_SPLIT", "CHANGE_CARGO_PROVIDER", "ALTERNATIVE_DELIVERY", "MANUAL_DELIVER", "MANUAL_RETURN"];
    private static readonly IReadOnlyList<string> HepsiburadaShipmentActions = ["UNPACK", "CHANGE_CARGO_PROVIDER"];
    private static readonly IReadOnlyList<string> HepsiburadaLabelFormats = ["ZPL", "BASE64ZPL", "PDF", "PNG", "JPG"];
    private static IReadOnlyList<string> HepsiburadaShipmentActionsFor(ShipmentPackage package) =>
        package.Status is ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.Returned or ShipmentPackageStatus.Cancelled
            ? []
            : HepsiburadaShipmentActions;
    private static bool IsEmptyJsonObject(string value) { try { using var document = JsonDocument.Parse(value); return document.RootElement.ValueKind == JsonValueKind.Object && !document.RootElement.EnumerateObject().Any(); } catch (JsonException) { return false; } }
    private static readonly IReadOnlyList<string> StageLabelFormats = ["PDF"];
    // Whether a Trendyol package action fits the package's state; the shipment detail offers only
    // the actions that pass (audit 2026-10-20 S7), and a request is refused with the same reason.
    internal static ServiceError? ShipmentActionStateError(ShipmentPackage package, string action)
    {
        var supported = new HashSet<string>(StringComparer.Ordinal) { "PICKING", "INVOICED", "TRACKING_NUMBER", "CANCEL_ITEMS", "SPLIT", "MULTI_SPLIT", "CHANGE_CARGO_PROVIDER", "ALTERNATIVE_DELIVERY", "MANUAL_DELIVER", "MANUAL_RETURN" };
        if (!supported.Contains(action)) return new("SHIPMENT_ACTION_UNSUPPORTED", "Paket aksiyonu tanınmıyor.", 422);
        if (package.Status == ShipmentPackageStatus.PaymentPending) return new("SHIPMENT_PAYMENT_PENDING", "Trendyol ödeme onayı vermeden paket işlemi yapılamaz.", 409);
        if (action == "PICKING" && package.Status is not (ShipmentPackageStatus.New or ShipmentPackageStatus.OnHold)) return new("SHIPMENT_STATE_CONFLICT", "PICKING yalnız NEW/ON_HOLD paket için gönderilebilir.", 409);
        if (action == "INVOICED" && package.Status != ShipmentPackageStatus.Processing) return new("SHIPMENT_STATE_CONFLICT", "INVOICED yalnız PROCESSING paket için gönderilebilir.", 409);
        if (action == "TRACKING_NUMBER" && package.Status is not (ShipmentPackageStatus.Processing or ShipmentPackageStatus.ReadyToShip)) return new("SHIPMENT_STATE_CONFLICT", "TRACKING_NUMBER yalnız PICKING/INVOICED paket için gönderilebilir.", 409);
        if ((action is "CANCEL_ITEMS" or "SPLIT" or "MULTI_SPLIT" or "CHANGE_CARGO_PROVIDER" or "ALTERNATIVE_DELIVERY") && package.Status is (ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.Undelivered or ShipmentPackageStatus.ReturnInTransit or ShipmentPackageStatus.Returned or ShipmentPackageStatus.Cancelled or ShipmentPackageStatus.ManualReview)) return new("SHIPMENT_STATE_CONFLICT", "Bu aksiyon sevk edilmiş veya terminal paket için kullanılamaz.", 409);
        // A package is marked delivered or returned by hand only after it reached the carrier.
        if (action == "MANUAL_DELIVER" && package.Status is not (ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Undelivered)) return new("SHIPMENT_STATE_CONFLICT", "Elle teslim yalnız kargodaki veya teslim edilemeyen paket için yapılabilir.", 409);
        if (action == "MANUAL_RETURN" && package.Status is not (ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Undelivered or ShipmentPackageStatus.ReturnInTransit)) return new("SHIPMENT_STATE_CONFLICT", "Elle iade yalnız kargodaki, teslim edilemeyen veya iade yolundaki paket için yapılabilir.", 409);
        return null;
    }

    private static ServiceError? ValidateShipmentAction(ShipmentPackage package, string action, string payloadJson)
    {
        if (ShipmentActionStateError(package, action) is { } stateError) return stateError;
        if (action is "MANUAL_DELIVER" or "MANUAL_RETURN") return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson); var root = document.RootElement; if (root.ValueKind != JsonValueKind.Object) return new("SHIPMENT_ACTION_PAYLOAD_INVALID", "Paket aksiyonu body nesne olmalıdır.", 422);
            static bool Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());
            static bool NonEmptyArray(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0;
            static bool Object(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object;
            static bool Boolean(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            static bool OptionalPositiveNumber(JsonElement root, string name) => !root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number > 0;
            static bool StatusWithLinesAndParams(JsonElement root, string expected) => Text(root, "status") && string.Equals(root.GetProperty("status").GetString(), expected, StringComparison.OrdinalIgnoreCase) && NonEmptyArray(root, "lines") && Object(root, "params");
            var valid = action switch
            {
                "PICKING" => StatusWithLinesAndParams(root, "Picking"),
                "INVOICED" => StatusWithLinesAndParams(root, "Invoiced"),
                "TRACKING_NUMBER" => Text(root, "cargoSenderNumber") && Text(root, "providerCode") && (!root.TryGetProperty("returnTrackingNumber", out var returnTracking) || returnTracking.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(returnTracking.GetString())),
                "CANCEL_ITEMS" => TrendyolPackageActionBodies.TryBuildUnsupplied(payloadJson, out _),
                "SPLIT" => NonEmptyArray(root, "orderLineIds") && (!root.TryGetProperty("shouldKeepPreviousStatus", out var keep) || keep.ValueKind is JsonValueKind.True or JsonValueKind.False),
                "MULTI_SPLIT" => NonEmptyArray(root, "splitGroups"),
                "CHANGE_CARGO_PROVIDER" => Text(root, "cargoProvider"),
                "ALTERNATIVE_DELIVERY" => Boolean(root, "isPhoneNumber") && Text(root, "trackingInfo") && Object(root, "params") && OptionalPositiveNumber(root, "boxQuantity") && OptionalPositiveNumber(root, "deci"),
                _ => true
            };
            return valid ? null : new("SHIPMENT_ACTION_PAYLOAD_INVALID", $"{action} için zorunlu body alanları eksik.", 422);
        }
        catch (JsonException) { return new("SHIPMENT_ACTION_PAYLOAD_INVALID", "Paket aksiyonu body geçerli JSON olmalıdır.", 422); }
    }

    // n11 offers approving (Picking) and splitting through its API; a package
    // can only be split after it was approved.
    internal static ServiceError? N11ShipmentActionStateError(ShipmentPackage package, string action)
    {
        if (!MarketplaceJobProcessor.N11ShipmentActions.Contains(action, StringComparer.Ordinal))
            return new("SHIPMENT_ACTION_UNSUPPORTED", "N11 için yalnız siparişi işleme alma, paket bölme, işçilik bedeli ve lojistik toplama talebi desteklenir; diğer işlemleri N11 satıcı panelinden yapın.", 422);
        if (package.Status == ShipmentPackageStatus.PaymentPending) return new("SHIPMENT_PAYMENT_PENDING", "Ödeme onayı gelmeden paket işlemi yapılamaz.", 409);
        if (package.Status is ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.Returned or ShipmentPackageStatus.Cancelled)
            return new("SHIPMENT_STATE_CONFLICT", "Sevk edilmiş veya terminal pakette N11 işlemi yapılamaz.", 409);
        if (action is "SPLIT" or "MULTI_SPLIT" or "SPLIT_BY_QUANTITY" or "COLLECTION_REQUEST" && package.Status != ShipmentPackageStatus.Processing)
            return new("SHIPMENT_STATE_CONFLICT", "Bu N11 işlemi yalnız işleme alınmış (Picking) paketlerde yapılabilir.", 409);
        if (action == "PICKING" && package.Status is not (ShipmentPackageStatus.New or ShipmentPackageStatus.OnHold))
            return new("SHIPMENT_STATE_CONFLICT", "PICKING yalnız NEW/ON_HOLD paket için gönderilebilir.", 409);
        return null;
    }

    private static ServiceError? ValidateN11ShipmentAction(ShipmentPackage package, string action, string payloadJson)
    {
        if (N11ShipmentActionStateError(package, action) is { } stateError) return stateError;
        if (action is "SPLIT_BY_QUANTITY" or "LABOR_COST" or "COLLECTION_REQUEST")
        {
            try { return MarketplaceHub.Infrastructure.Adapters.N11.N11PackageActionMapper.Body(action, payloadJson) is null ? new("SHIPMENT_ACTION_PAYLOAD_INVALID", "N11 işlem içeriği okunamadı.", 422) : null; }
            catch (JsonException) { return new("SHIPMENT_ACTION_PAYLOAD_INVALID", $"{action} için zorunlu body alanları eksik veya geçersiz.", 422); }
        }
        return ValidateShipmentAction(package, action, payloadJson);
    }

    // Pazarama: an order in "Created" can be moved to "Preparing" (picking) or marked not supplied; once preparing, the
    // package can still be split, re-addressed, get a labor cost or have a cancel request decided. Shipped and later
    // states are Pazarama's own cargo process and take no action from here.
    internal static IReadOnlyList<string> PazaramaShipmentActionsFor(ShipmentPackage package) => package.Status switch
    {
        ShipmentPackageStatus.New or ShipmentPackageStatus.OnHold => ["PICKING", "NOT_SUPPLIED", "SPLIT", "UPDATE_PACKAGE", "LABOR_COST", "CANCEL_APPROVE", "CANCEL_REJECT"],
        ShipmentPackageStatus.Processing => ["NOT_SUPPLIED", "SPLIT", "UPDATE_PACKAGE", "LABOR_COST", "CANCEL_APPROVE", "CANCEL_REJECT"],
        _ => []
    };

    private static ServiceError? ValidatePazaramaShipmentAction(ShipmentPackage package, string action, string payloadJson)
    {
        if (!MarketplaceHub.Infrastructure.Adapters.Pazarama.PazaramaPackageActionMapper.Actions.Contains(action, StringComparer.Ordinal))
            return new("SHIPMENT_ACTION_UNSUPPORTED", "Pazarama için yalnız siparişi işleme alma, tedarik edilemedi, paket bölme/güncelleme, işçilik bedeli ve iptal talebi kararı desteklenir; diğer işlemleri Pazarama satıcı panelinden yapın.", 422);
        if (package.Status == ShipmentPackageStatus.PaymentPending) return new("SHIPMENT_PAYMENT_PENDING", "Ödeme onayı gelmeden paket işlemi yapılamaz.", 409);
        if (!PazaramaShipmentActionsFor(package).Contains(action, StringComparer.Ordinal))
            return new("SHIPMENT_STATE_CONFLICT", action == "PICKING" ? "Siparişi işleme alma yalnız yeni (Sipariş Alındı) paketlerde yapılabilir." : "Bu paketin durumunda Pazarama işlemi yapılamaz; kargo süreci Pazarama anlaşmalı kargosunda kendiliğinden ilerler.", 409);
        return MarketplaceHub.Infrastructure.Adapters.Pazarama.PazaramaPackageActionMapper.Validate(action, payloadJson) is { } invalid
            ? new("SHIPMENT_ACTION_PAYLOAD_INVALID", invalid, 422)
            : null;
    }

    private static ServiceError? ValidateHepsiburadaShipmentAction(ShipmentPackage package, string action, string payloadJson)
    {
        if (action is not ("UNPACK" or "CHANGE_CARGO_PROVIDER")) return new("SHIPMENT_ACTION_UNSUPPORTED", "Hepsiburada için yalnız paketi açma ve izin verilen kargo firmasını değiştirme desteklenir.", 422);
        if (HepsiburadaShipmentActionsFor(package).Count == 0)
            return new("SHIPMENT_STATE_CONFLICT", "Sevk edilmiş veya terminal pakette Hepsiburada kargo işlemi yapılamaz.", 409);
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return new("SHIPMENT_ACTION_PAYLOAD_INVALID", "Paket aksiyonu body nesne olmalıdır.", 422);
            if (action == "UNPACK")
                return document.RootElement.EnumerateObject().Any() ? new("SHIPMENT_ACTION_PAYLOAD_INVALID", "UNPACK payloadı boş JSON nesnesi olmalıdır.", 422) : null;
            var shortName = document.RootElement.TryGetProperty("CargoCompanyShortName", out var shortNameValue) && shortNameValue.ValueKind == JsonValueKind.String
                ? shortNameValue.GetString()
                : document.RootElement.TryGetProperty("cargoCompanyShortName", out shortNameValue) && shortNameValue.ValueKind == JsonValueKind.String
                    ? shortNameValue.GetString()
                    : null;
            return !string.IsNullOrWhiteSpace(shortName) && shortName == shortName.Trim() && shortName.Length <= 80 && !shortName.Any(char.IsWhiteSpace)
                ? null
                : new("SHIPMENT_ACTION_PAYLOAD_INVALID", "CargoCompanyShortName zorunludur.", 422);
        }
        catch (JsonException) { return new("SHIPMENT_ACTION_PAYLOAD_INVALID", "Paket aksiyonu geçerli JSON olmalıdır.", 422); }
    }
}
