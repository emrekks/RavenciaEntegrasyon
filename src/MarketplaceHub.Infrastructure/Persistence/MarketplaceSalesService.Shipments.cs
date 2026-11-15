using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceSalesService
{
    // The shipments page: live packages only (no split parent, resent original or placeholder),
    // the latest status change first, paged by (StatusOccurredAt, Id) (audit 2026-10-20 S18).
    private const string ShipmentCursorSort = "SHIPMENTS";

    public async Task<PageResult<ShipmentView>> ShipmentsAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken)
    {
        var query = CurrentShipmentPackages().Where(x => x.TenantId == tenantId && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")));
        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status.Trim(), "ALL", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ShipmentStatusFilter(status) ?? throw new ArgumentException("Gönderi durumu filtresi tanınmıyor.", nameof(status));
            query = query.Where(x => x.Status == parsed);
        }
        if (!string.IsNullOrWhiteSpace(after))
        {
            if (!cursors.TryDecodeOrder(after, out var cursorSort, out _, out var cursorAt, out var cursorId) || cursorSort != ShipmentCursorSort)
                throw new ArgumentException("Cursor geçersiz veya süresi dolmuş.", nameof(after));
            query = query.Where(x => x.StatusOccurredAt < cursorAt || x.StatusOccurredAt == cursorAt && x.Id.CompareTo(cursorId) < 0);
        }
        var rows = await (from package in query
                          join order in db.Orders.AsNoTracking() on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                          orderby package.StatusOccurredAt descending, package.Id descending
                          select new { Package = package, order.OrderNumber }).Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        var items = rows.Take(limit).ToList();
        var last = items.LastOrDefault();
        return new(items.Select(x => Map(x.Package, x.OrderNumber)).ToList(), hasMore && last is not null ? cursors.EncodeOrder(ShipmentCursorSort, null, last.Package.StatusOccurredAt, last.Package.Id) : null, hasMore);
    }

    // The page's filter values are the wire names (READY_TO_SHIP); the enum names are accepted too.
    internal static ShipmentPackageStatus? ShipmentStatusFilter(string value)
    {
        var compact = value.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse<ShipmentPackageStatus>(compact, true, out var parsed) && Enum.IsDefined(parsed) && !compact.All(char.IsDigit) ? parsed : null;
    }

    public async Task<ServiceResult<ShipmentDetailView>> ShipmentAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var row = await (from package in db.ShipmentPackages.AsNoTracking() where package.TenantId == tenantId && package.Id == id && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == package.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")) join order in db.Orders.AsNoTracking() on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id } join connection in db.PlatformConnections.AsNoTracking() on new { package.TenantId, ConnectionId = package.ConnectionId } equals new { connection.TenantId, ConnectionId = connection.Id } select new { Package = package, order.OrderNumber, connection.PlatformCode }).SingleOrDefaultAsync(cancellationToken); if (row is null) return NotFound<ShipmentDetailView>();
        var stage = await IsStageConnection(tenantId, row.Package.ConnectionId, cancellationToken);
        var actions = stage
            ? row.PlatformCode == "HEPSIBURADA" ? HepsiburadaShipmentActionsFor(row.Package) : ShipmentActions
            : await CapabilityValues(tenantId, row.Package.ConnectionId, MarketplaceCapabilities.ShipmentWrite, "allowedActions", cancellationToken);
        if (row.Package.Status == ShipmentPackageStatus.PaymentPending)
            actions = [];
        // n11's capability is proven by its first accepted action; once proven, every n11 package action is offered.
        if (!stage && row.PlatformCode == "N11" && actions.Count > 0)
            actions = MarketplaceJobProcessor.N11ShipmentActions;
        // Pazarama has no sandbox and nothing to probe without writing; the actions follow the package state and every
        // send still passes the write gate (global flag, connection switch, shipment policy).
        if (row.PlatformCode == "PAZARAMA")
            actions = row.Package.Status == ShipmentPackageStatus.PaymentPending ? [] : PazaramaShipmentActionsFor(row.Package);
        if (!stage && row.PlatformCode == "HEPSIBURADA")
        {
            var evidencedActions = actions;
            actions = HepsiburadaShipmentActionsFor(row.Package)
                .Where(action => evidencedActions.Contains(action, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }
        // Only the actions the package's state allows are offered; a superseded package (a split
        // parent, a resent original) takes none.
        if (row.PlatformCode is "TRENDYOL" or "N11")
            actions = actions.Where(action => (row.PlatformCode == "N11" ? N11ShipmentActionStateError(row.Package, action) : ShipmentActionStateError(row.Package, action)) is null).ToArray();
        if (!await CurrentShipmentPackages().AnyAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken))
            actions = [];
        IReadOnlyList<string> formats;
        if (row.PlatformCode == "HEPSIBURADA")
        {
            formats = !CommonLabelCarrierPolicy.SupportsHepsiburada(row.Package.CargoProviderExternalId)
                ? []
                : stage
                    ? HepsiburadaLabelFormats
                    : (await CapabilityValues(tenantId, row.Package.ConnectionId, MarketplaceCapabilities.LabelRead, "formats", cancellationToken))
                        .Where(format => HepsiburadaLabelFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
                        .ToArray();
        }
        else
        {
            formats = stage
                ? StageLabelFormats
                : await CapabilityValues(tenantId, row.Package.ConnectionId, MarketplaceCapabilities.LabelRead, "formats", cancellationToken);
        }
        var documents = await db.ShipmentDocuments.AsNoTracking().Where(x => x.TenantId == tenantId && x.PackageId == id).OrderByDescending(x => x.DocumentVersion).Select(x => new ShipmentDocumentView(x.Id, x.DocumentKind, x.Format, x.Source, x.DocumentVersion, x.CreatedAt, x.ExpiresAt)).ToListAsync(cancellationToken);
        return ServiceResult<ShipmentDetailView>.Ok(new(Map(row.Package, row.OrderNumber), actions, formats, stage, documents, row.PlatformCode));
    }

    public async Task<ServiceResult<IReadOnlyList<RemoteCargoCompany>>> ChangeableCargoCompaniesAsync(Guid tenantId, Guid packageId, string correlationId, CancellationToken cancellationToken)
    {
        var row = await (from package in db.ShipmentPackages.AsNoTracking()
                         join connection in db.PlatformConnections.AsNoTracking() on new { package.TenantId, package.ConnectionId } equals new { connection.TenantId, ConnectionId = connection.Id }
                         where package.TenantId == tenantId && package.Id == packageId && connection.PlatformCode == "HEPSIBURADA" && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")
                         select new { package.ConnectionId, package.ExternalPackageId }).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return ServiceResult<IReadOnlyList<RemoteCargoCompany>>.Fail("HEPSIBURADA_PACKAGE_NOT_FOUND", "Etkin Hepsiburada paketi bulunamadı.", 404);
        var result = await orders.GetChangeableCargoCompaniesAsync(
            new AdapterContext(tenantId, row.ConnectionId, correlationId, $"hb-carriers:{packageId:N}", timeProvider.GetUtcNow().AddSeconds(30)),
            row.ExternalPackageId,
            cancellationToken);
        return result.IsSuccess
            ? ServiceResult<IReadOnlyList<RemoteCargoCompany>>.Ok(result.Value!)
            : ServiceResult<IReadOnlyList<RemoteCargoCompany>>.Fail(result.Error?.Code ?? "HEPSIBURADA_CARGO_COMPANIES_FAILED", result.Error?.SafeMessage ?? "Hepsiburada kargo firmaları okunamadı.", result.Error?.HttpStatus is >= 400 and <= 599 ? result.Error.HttpStatus.Value : 502);
    }

    public async Task<ServiceResult<CreateOrderPackageResult>> CreateOrderPackageInstantAsync(Guid tenantId, Guid orderId, long expectedVersion, OrderPackageCreateRequest command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 256) return Invalid<CreateOrderPackageResult>("idempotencyKey", "1 ile 256 karakter arasında Idempotency-Key başlığı gereklidir.");
        if (command is null) return Invalid<CreateOrderPackageResult>("package", "Paket alanları gereklidir.");
        var order = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == orderId, cancellationToken);
        if (order is null) return NotFound<CreateOrderPackageResult>();
        if (order.Version != expectedVersion) return Precondition<CreateOrderPackageResult>(order.Version);
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == order.ConnectionId && x.PlatformCode == "HEPSIBURADA" && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken);
        if (connection is null) return ServiceResult<CreateOrderPackageResult>.Fail("HEPSIBURADA_CONNECTION_REQUIRED", "Sipariş etkin Hepsiburada bağlantısına bağlı olmalıdır.", 422);
        if (!PackageCreateFieldsValid(command)) return Invalid<CreateOrderPackageResult>("package", "Paket başlığı ve en az bir geçerli sipariş kalemi gereklidir.");

        var selectedIds = command.LineItems.Select(line => line.OrderLineId).ToArray();
        if (selectedIds.Distinct().Count() != selectedIds.Length) return Invalid<CreateOrderPackageResult>("lineItems", "Aynı sipariş kalemi pakette bir kez bulunabilir.");
        var lines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == orderId && selectedIds.Contains(x.Id)).ToListAsync(cancellationToken);
        if (lines.Count != selectedIds.Length) return ServiceResult<CreateOrderPackageResult>.Fail("ORDER_PACKAGE_LINES_INVALID", "Seçili sipariş kalemlerinden biri bu siparişe ait değil.", 422);
        var allocations = await ActiveAllocatedQuantitiesAsync(tenantId, selectedIds, cancellationToken);
        var remoteLines = new List<RemotePackageCreateLineRequest>();
        foreach (var requested in command.LineItems)
        {
            var line = lines.Single(item => item.Id == requested.OrderLineId);
            var remaining = Math.Max(0m, line.OrderedQuantity - line.CancelledQuantity - (allocations.GetValueOrDefault(line.Id)));
            if (requested.Quantity <= 0 || requested.Quantity > remaining || requested.Quantity != decimal.Truncate(requested.Quantity))
                return ServiceResult<CreateOrderPackageResult>.Fail("ORDER_PACKAGE_QUANTITY_INVALID", $"{line.Sku} için kalan paketlenebilir miktar {remaining} adetten fazla olamaz.", 422);
            if (string.IsNullOrWhiteSpace(line.ExternalLineId) || line.ExternalLineId.Any(char.IsWhiteSpace))
                return ServiceResult<CreateOrderPackageResult>.Fail("ORDER_PACKAGE_LINE_ID_INVALID", $"{line.Sku} Hepsiburada kalem kimliği geçersiz.", 422);
            remoteLines.Add(new(line.ExternalLineId, requested.Quantity));
        }

        var stage = string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase);
        if (!stage && !string.Equals(connection.Environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase)) return ServiceResult<CreateOrderPackageResult>.Fail("ENVIRONMENT_INVALID", "Paket oluşturma yalnız test ya da canlı mağaza bağlantısında yapılabilir.", 422);
        if (!stage && !await WritesEnabled(tenantId, connection.Id, cancellationToken)) return ServiceResult<CreateOrderPackageResult>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        var policy = await ExternalWritePolicyAsync(tenantId, connection.Id, MarketplaceExternalWritePolicies.Shipment, cancellationToken);
        if (!policy.Enabled) return ServiceResult<CreateOrderPackageResult>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "Kargo dış yazma akışı kapalı.", 422);

        var normalizedKey = idempotencyKey.Trim();
        var effectPrefix = $"HEPSIBURADA_PACKAGE_CREATE:{connection.Id:N}:{order.Id:N}";
        var existing = await db.ExternalEffectRecords.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == normalizedKey && x.EffectType.StartsWith(effectPrefix), cancellationToken);
        if (existing is not null)
        {
            if (existing.CompletedAt is null) return ServiceResult<CreateOrderPackageResult>.Fail("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki paket oluşturma isteğinin sonucu kesinleşmedi; sipariş eşitlemesini bekleyin.", 409);
            var packageNumber = existing.EffectType[(effectPrefix.Length + 1)..];
            return packageNumber.Length > 0
                ? ServiceResult<CreateOrderPackageResult>.Ok(new(Uri.UnescapeDataString(packageNumber)))
                : ServiceResult<CreateOrderPackageResult>.Fail("PACKAGE_CREATE_READBACK_REQUIRED", "Paket oluşturma daha önce tamamlandı; sipariş eşitlemesinden paket numarasını kontrol edin.", 409);
        }
        var completedPackageEffects = await db.ExternalEffectRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EffectType.StartsWith(effectPrefix + ":") && x.CompletedAt != null)
            .Select(x => x.EffectType)
            .ToListAsync(cancellationToken);
        if (completedPackageEffects.Count > 0)
        {
            var knownPackageNumbers = await db.ShipmentPackages.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connection.Id && x.OrderId == orderId)
                .Select(x => x.ExternalPackageId)
                .ToListAsync(cancellationToken);
            var readbackPending = completedPackageEffects.Any(effectType =>
            {
                var encodedPackageNumber = effectType[(effectPrefix.Length + 1)..];
                var packageNumber = Uri.UnescapeDataString(encodedPackageNumber);
                return !knownPackageNumbers.Contains(packageNumber, StringComparer.Ordinal);
            });
            if (readbackPending) return ServiceResult<CreateOrderPackageResult>.Fail("PACKAGE_CREATE_READBACK_REQUIRED", "Önceki paket Hepsiburada’da oluşturuldu; sipariş/paket eşitlemesi tamamlanmadan başka paket açılamaz.", 409);
        }
        // A reservation the effect check (or the sweep) settled keeps its row as history under its
        // own name; left under the reservation key it would make every later package request on
        // this order collide with it.
        var settledReservations = await db.ExternalEffectRecords
            .Where(x => x.TenantId == tenantId && x.EffectType == effectPrefix && x.IdempotencyKey == effectPrefix && x.CompletedAt != null)
            .ToListAsync(cancellationToken);
        if (settledReservations.Count > 0)
        {
            foreach (var settled in settledReservations) settled.IdempotencyKey = HepsiburadaPackageReservationSettledKey(effectPrefix, settled.Id);
            await db.SaveChangesAsync(cancellationToken);
        }
        var pendingReservation = await db.ExternalEffectRecords.AsNoTracking().AnyAsync(
            x => x.TenantId == tenantId && x.IdempotencyKey == effectPrefix && x.EffectType == effectPrefix && x.CompletedAt == null,
            cancellationToken);
        if (pendingReservation) return ServiceResult<CreateOrderPackageResult>.Fail("EXTERNAL_EFFECT_AMBIGUOUS", "Bu sipariş için önceki paket oluşturma isteğinin sonucu kesinleşmedi; yeni paket isteği engellendi.", 409);

        var seed = remoteLines[0];
        var eligible = await orders.GetPackageableLineItemsAsync(new AdapterContext(tenantId, connection.Id, correlationId, $"{normalizedKey}:eligible", timeProvider.GetUtcNow().AddSeconds(30)), seed.LineItemId, cancellationToken);
        if (!eligible.IsSuccess && eligible.Error?.Class != AdapterErrorClass.NotFound)
            return ServiceResult<CreateOrderPackageResult>.Fail(eligible.Error?.Code ?? "PACKAGEABLE_LINES_READ_FAILED", eligible.Error?.SafeMessage ?? "Paketlenebilir kalemler okunamadı.", eligible.Error?.HttpStatus is >= 400 and <= 599 ? eligible.Error.HttpStatus.Value : 502);
        // Hepsiburada returns 404 when no *additional* lines can join the seed.
        // The seed itself can still be packaged, so preserve it as eligible.
        var eligibleLines = eligible.IsSuccess ? eligible.Value! : Array.Empty<RemotePackageableLine>();
        var compatibleIds = eligibleLines.Select(line => line.LineItemId).Append(seed.LineItemId).ToHashSet(StringComparer.Ordinal);
        if (remoteLines.Skip(1).Any(line => !compatibleIds.Contains(line.LineItemId))) return ServiceResult<CreateOrderPackageResult>.Fail("HEPSIBURADA_PACKAGE_LINES_NOT_COMPATIBLE", "Seçili kalemlerden biri Hepsiburada yanıtına göre aynı pakete eklenemez.", 422);

        var packagesBefore = await db.ShipmentPackages.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.OrderId == order.Id && x.Status != ShipmentPackageStatus.Cancelled, cancellationToken);
        var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = effectPrefix, IdempotencyKey = effectPrefix, CreatedAt = timeProvider.GetUtcNow(), ConnectionId = connection.Id, TargetId = order.Id, Action = "CREATE_PACKAGE", ExpectedValue = packagesBefore.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        db.ExternalEffectRecords.Add(effect);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return ServiceResult<CreateOrderPackageResult>.Fail("EXTERNAL_EFFECT_AMBIGUOUS", "Bu paket isteği başka bir işlemde ele alınıyor; sipariş durumunu kontrol edin.", 409);
        }

        var result = await orders.CreateOrderPackageAsync(
            new AdapterContext(tenantId, connection.Id, correlationId, normalizedKey, timeProvider.GetUtcNow().AddSeconds(30)),
            new(command.Barcode?.Trim(), command.CargoCompany?.Trim(), command.Carrier?.Trim(), command.CreationReason?.Trim(), command.Deci, command.ParcelQuantity,
                command.ShippingAddressLabel?.Trim(), command.ShippingModel?.Trim(), remoteLines),
            cancellationToken);
        if (!result.IsSuccess)
        {
            var error = result.Error;
            if (error is null || IsAmbiguous(error)) return await VerifyLaterAsync<CreateOrderPackageResult>(effect, cancellationToken);
            db.ExternalEffectRecords.Remove(effect);
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<CreateOrderPackageResult>.Fail(error.Code, error.SafeMessage, error.HttpStatus is >= 400 and <= 599 ? error.HttpStatus.Value : 502);
        }

        // No local projection here (only the effect row is written): the new package reaches the
        // order through the queued order read, which takes the "orders" lock itself.
        effect.EffectType = $"{effectPrefix}:{Uri.EscapeDataString(result.Value!.PackageNumber)}";
        effect.IdempotencyKey = normalizedKey;
        effect.CompletedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        _ = await EnqueueOrderSyncAsync(tenantId, connection.Id, order.OrderNumber, false, correlationId, cancellationToken);
        return ServiceResult<CreateOrderPackageResult>.Ok(result.Value!);
    }

    // A panel status for a Shopify order (Shopify has no shipping workflow of its own here). It is
    // written on the order version the operator saw, under the order jobs' lock, once per request
    // key, and it names the status the marketplace data gave at that moment: it stays until that
    // marketplace status changes (ShopifyManualStatusPolicy).
    public async Task<ServiceResult<ShopifyOrderStatusResult>> SetShopifyOrderStatusAsync(Guid tenantId, Guid userId, Guid orderId, long expectedVersion, string? status, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var normalizedStatus = status?.Trim().ToUpperInvariant();
        if (normalizedStatus is not ("NEW" or "PROCESSING" or "ON_HOLD" or "READY_TO_SHIP" or "SHIPPED" or "UNDELIVERED" or "DELIVERED" or "RETURN_IN_TRANSIT" or "RETURNED" or "CANCELLED"))
            return ServiceResult<ShopifyOrderStatusResult>.Fail("SHOPIFY_ORDER_STATUS_INVALID", "Shopify sipariş durumu geçersiz.", 422);
        var connectionId = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == orderId).Select(x => (Guid?)x.ConnectionId).SingleOrDefaultAsync(cancellationToken);
        if (connectionId is null) return NotFound<ShopifyOrderStatusResult>();
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (platform != "SHOPIFY") return ServiceResult<ShopifyOrderStatusResult>.Fail("SHOPIFY_ORDER_ONLY", "Bu manuel durum akışı yalnız Shopify siparişleri için kullanılabilir.", 422);

        await using var ordersLock = await OrdersProjectionLockAsync(connectionId.Value, cancellationToken);
        var order = await db.Orders.SingleAsync(x => x.TenantId == tenantId && x.Id == orderId, cancellationToken);
        var sourceEventId = ShopifyManualStatusPolicy.SourceEventId(RemoteDerivedStatus(await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == orderId).ToListAsync(cancellationToken), await RemoteOrderStatusAsync(order, cancellationToken)), idempotencyKey);
        // The same request again (same key): it was applied; answer with the order as it is.
        var replay = await db.OrderStatusHistory.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == orderId && x.SourceEventId.EndsWith(ShopifyManualStatusPolicy.KeySuffix(idempotencyKey)))
            .Select(x => x.CanonicalStatus).FirstOrDefaultAsync(cancellationToken);
        if (replay is not null) return ServiceResult<ShopifyOrderStatusResult>.Ok(new(order.Id, order.DerivedStatus, order.Version));
        if (order.Version != expectedVersion) return Precondition<ShopifyOrderStatusResult>(order.Version);

        var now = timeProvider.GetUtcNow();
        order.DerivedStatus = normalizedStatus;
        order.UpdatedAt = now;
        order.Version++;
        db.OrderStatusHistory.Add(new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, CanonicalStatus = normalizedStatus, RawStatus = $"{ShopifyManualStatusPolicy.RawStatusPrefix}{normalizedStatus}", SourceEventId = sourceEventId, OccurredAt = now, RecordedAt = now });
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = userId, Action = "SHOPIFY_ORDER_STATUS_MANUAL", TargetType = "Order", TargetId = orderId.ToString("D"), Reason = normalizedStatus, CorrelationId = correlationId, CreatedAt = now });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var latest = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == orderId).Select(x => x.Version).SingleAsync(cancellationToken);
            return Precondition<ShopifyOrderStatusResult>(latest);
        }
        return ServiceResult<ShopifyOrderStatusResult>.Ok(new(order.Id, normalizedStatus, order.Version));
    }

    // The order status the marketplace packages give (what the order would show without a manual status).
    private static string RemoteDerivedStatus(IReadOnlyCollection<ShipmentPackage> packages, string fallback) =>
        ShipmentPackageSupersession.StatusSources(packages) is { } sources ? Wire(ShipmentPackageStatusPolicy.Aggregate(sources)) : fallback;

    // The order status the marketplace gave, for an order without package statuses. While a panel
    // status holds, order.DerivedStatus is that panel status: the marketplace status is the one its
    // history row recorded, else the latest marketplace history status. Recording the old panel
    // status as the marketplace's let the next marketplace read drop the new panel status although
    // the marketplace had not changed (ShopifyManualStatusPolicy.StillHolds).
    private async Task<string> RemoteOrderStatusAsync(Order order, CancellationToken cancellationToken)
    {
        var manual = await db.OrderStatusHistory.AsNoTracking()
            .Where(x => x.TenantId == order.TenantId && x.OrderId == order.Id && x.RawStatus.StartsWith(ShopifyManualStatusPolicy.RawStatusPrefix)
                && x.CanonicalStatus == order.DerivedStatus && x.OccurredAt >= order.LastRemoteModifiedAt)
            .OrderByDescending(x => x.OccurredAt).Select(x => x.SourceEventId).FirstOrDefaultAsync(cancellationToken);
        if (manual is null) return order.DerivedStatus;
        return ShopifyManualStatusPolicy.RemoteStatusAt(manual)
            ?? await db.OrderStatusHistory.AsNoTracking()
                .Where(x => x.TenantId == order.TenantId && x.OrderId == order.Id && !x.RawStatus.StartsWith(ShopifyManualStatusPolicy.RawStatusPrefix))
                .OrderByDescending(x => x.OccurredAt).Select(x => x.CanonicalStatus).FirstOrDefaultAsync(cancellationToken)
            ?? order.DerivedStatus;
    }

    internal static string HepsiburadaPackageReservationSettledKey(string effectPrefix, Guid effectId) => $"{effectPrefix}:reconciled:{effectId:N}";

    internal sealed record PackageLineAllocationRow(Guid PackageId, Guid OrderLineId, string SourceEventId, decimal AllocatedQuantity, decimal CancelledQuantity);

    // An order read stores a package line's allocation once per source event; the line's current
    // allocation in that package is the latest of them, never their sum.
    internal static IReadOnlyList<PackageLineAllocationRow> LatestPackageLineAllocations(IEnumerable<PackageLineAllocationRow> rows) => rows
        .GroupBy(row => (row.PackageId, row.OrderLineId))
        .Select(group => group.OrderByDescending(row => SourceEventInstant(row.SourceEventId)).ThenByDescending(row => row.SourceEventId, StringComparer.Ordinal).First())
        .ToList();

    // Source events are "{packageId}:{unix ms}"; anything else sorts first.
    private static long SourceEventInstant(string sourceEventId)
    {
        var separator = sourceEventId.LastIndexOf(':');
        return separator >= 0 && long.TryParse(sourceEventId.AsSpan(separator + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds) ? milliseconds : long.MinValue;
    }

    // Units of each line already in a live package of the order: cancelled and superseded
    // packages (split parents, resent originals) hold none.
    private async Task<Dictionary<Guid, decimal>> ActiveAllocatedQuantitiesAsync(Guid tenantId, Guid[] orderLineIds, CancellationToken cancellationToken)
    {
        if (orderLineIds.Length == 0) return [];
        var livePackages = CurrentShipmentPackages().Where(package => package.TenantId == tenantId && package.Status != ShipmentPackageStatus.Cancelled);
        var rows = await db.PackageLineAllocations.AsNoTracking()
            .Where(x => x.TenantId == tenantId && orderLineIds.Contains(x.OrderLineId) && livePackages.Any(package => package.Id == x.PackageId))
            .Select(x => new PackageLineAllocationRow(x.PackageId, x.OrderLineId, x.SourceEventId, x.AllocatedQuantity, x.CancelledQuantity))
            .ToListAsync(cancellationToken);
        return LatestPackageLineAllocations(rows).GroupBy(row => row.OrderLineId)
            .ToDictionary(group => group.Key, group => group.Sum(row => Math.Max(0m, row.AllocatedQuantity - row.CancelledQuantity)));
    }

    // The lines of one package with their current allocated units, as a picking request sends them.
    private async Task<List<(string ExternalLineId, decimal AllocatedQuantity)>> PackageLinesAsync(Guid tenantId, Guid packageId, CancellationToken cancellationToken)
    {
        var rows = await db.PackageLineAllocations.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PackageId == packageId)
            .Select(x => new PackageLineAllocationRow(x.PackageId, x.OrderLineId, x.SourceEventId, x.AllocatedQuantity, x.CancelledQuantity))
            .ToListAsync(cancellationToken);
        var latest = LatestPackageLineAllocations(rows).Where(row => row.AllocatedQuantity > 0).ToList();
        var lineIds = latest.Select(row => row.OrderLineId).ToArray();
        var externalIds = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && lineIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.ExternalLineId, cancellationToken);
        return latest.Where(row => externalIds.ContainsKey(row.OrderLineId))
            .OrderBy(row => externalIds[row.OrderLineId], StringComparer.Ordinal)
            .Select(row => (externalIds[row.OrderLineId], row.AllocatedQuantity)).ToList();
    }

    private static bool PackageCreateFieldsValid(OrderPackageCreateRequest command)
    {
        // Only the lines, desi and parcel count are required; the other fields are checked when given.
        static bool Text(string? value, int maxLength) => string.IsNullOrEmpty(value) || value == value.Trim() && value.Length <= maxLength;
        return Text(command.Barcode, 128) && command.Barcode?.Any(char.IsWhiteSpace) != true
            && Text(command.CargoCompany, 100) && Text(command.Carrier, 100) && Text(command.CreationReason, 100)
            && Text(command.ShippingAddressLabel, 100) && Text(command.ShippingModel, 100)
            && command.Deci is >= 0 and <= 10000 && command.ParcelQuantity is >= 1 and <= 100
            && command.LineItems is { Count: > 0 and <= 100 }
            && command.LineItems.All(line => line.OrderLineId != Guid.Empty && line.Quantity is > 0 and <= 10000);
    }
}
