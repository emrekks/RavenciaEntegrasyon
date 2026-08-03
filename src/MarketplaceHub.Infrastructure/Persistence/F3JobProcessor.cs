using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class F3JobProcessor(AppDbContext db, IConnectionPort connections, IReferenceDataPort references, IOrderPort orders, IReturnPort returns, ShopifyGraphQlClient shopify, TimeProvider timeProvider, HepsiburadaAdapter? hepsiburada = null) : IF3JobProcessor
{
    public async Task<bool> ProcessAsync(Guid tenantId, Guid? connectionId, string jobType, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        if (connectionId is null) return false;
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId.Value).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (!ActiveIntegrationScope.Contains(platform)) return false;
        return jobType switch
        {
            F3JobTypes.ConnectionTest => await TestConnection(tenantId, connectionId.Value, correlationId, cancellationToken),
            ShopifyContract.ConnectionTestJob => await TestConnection(tenantId, connectionId.Value, correlationId, cancellationToken),
            HepsiburadaContract.ConnectionTestJob => await TestConnection(tenantId, connectionId.Value, correlationId, cancellationToken),
            F3JobTypes.ReferenceSync => await SyncReferences(tenantId, connectionId.Value, correlationId, cancellationToken),
            ShopifyContract.WebhookIngestJob => await MarkShopifyWebhookProcessed(tenantId, payloadJson, cancellationToken),
            ShopifyContract.OrderSyncJob => await SyncOrders(tenantId, connectionId.Value, correlationId, cancellationToken),
            F3JobTypes.OrderSync => await SyncOrders(tenantId, connectionId.Value, correlationId, cancellationToken),
            F3JobTypes.ReturnSync => await SyncReturns(tenantId, connectionId.Value, correlationId, cancellationToken),
            F3JobTypes.WebhookIngest => await IngestWebhook(tenantId, connectionId.Value, payloadJson, cancellationToken),
            F3JobTypes.ShipmentAction => await ShipmentAction(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
            F3JobTypes.ReturnAction => await ReturnAction(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
            _ => false
        };
    }

    private async Task<bool> SyncReferences(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        const string resourceType = "CATEGORIES";
        var items = new List<RemoteReferenceItem>();
        var visitedCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var result = await references.ReadAsync(Context(tenantId, connectionId, correlationId, $"reference-sync:{resourceType}:{cursor}"), new(resourceType, null), new(cursor, 1000), cancellationToken);
            if (!result.IsSuccess) return false;
            items.AddRange(result.Value!.Items);
            if (items.Count > 100_000) return false;
            cursor = result.Value.NextCursor;
            if (!result.Value.HasMore) break;
            if (string.IsNullOrWhiteSpace(cursor) || !visitedCursors.Add(cursor)) return false;
        } while (!cancellationToken.IsCancellationRequested);

        cancellationToken.ThrowIfCancellationRequested();
        if (items.Count == 0 || items.Any(x => !string.Equals(x.ResourceType, resourceType, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(x.ExternalId) || string.IsNullOrWhiteSpace(x.Name))) return false;
        var ordered = items.OrderBy(x => x.ExternalId, StringComparer.Ordinal).ToList();
        if (ordered.Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).Count() != ordered.Count) return false;
        var canonical = JsonSerializer.Serialize(ordered.Select(x => new { x.ExternalId, x.ParentExternalId, x.Name, x.Path, x.Depth, x.IsLeaf, x.IsActive }));
        var contentHash = Hash(canonical);
        var now = timeProvider.GetUtcNow();
        var sourceVersion = await db.PlatformCapabilities.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == F3Capabilities.ReferenceRead).Select(x => x.SourceVersion).SingleOrDefaultAsync(cancellationToken)
            ?? await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.ApiVersion).SingleAsync(cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var snapshots = await db.ReferenceSnapshots.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType).ToListAsync(cancellationToken);
        var snapshot = snapshots.SingleOrDefault(x => x.ContentHash == contentHash);
        foreach (var current in snapshots.Where(x => x.IsCurrent && x.Id != snapshot?.Id)) current.IsCurrent = false;
        if (snapshot is null)
        {
            snapshot = new ReferenceSnapshot { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ResourceType = resourceType, SourceVersion = sourceVersion, ContentHash = contentHash, FetchedAt = now, IsCurrent = true, ItemCount = ordered.Count };
            db.ReferenceSnapshots.Add(snapshot);
            for (var index = 0; index < ordered.Count; index++)
            {
                var item = ordered[index];
                db.ReferenceItems.Add(new ReferenceItem { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = snapshot.Id, ResourceType = resourceType, ExternalId = item.ExternalId, ParentExternalId = item.ParentExternalId, Name = item.Name, NormalizedName = item.Name.Trim().ToUpperInvariant(), Path = item.Path, Depth = item.Depth, IsLeaf = item.IsLeaf, IsActive = item.IsActive, PayloadHash = Hash(item.RawJson), SortOrder = index });
            }
        }
        else
        {
            snapshot.IsCurrent = true;
            snapshot.FetchedAt = now;
            snapshot.SourceVersion = sourceVersion;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> TestConnection(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == ShopifyContract.PlatformCode || x.PlatformCode == HepsiburadaContract.PlatformCode), cancellationToken); if (connection is null) return false; var now = timeProvider.GetUtcNow(); connection.LastTestedAt = now;
        IConnectionPort port = connection.PlatformCode == ShopifyContract.PlatformCode ? shopify : connection.PlatformCode == HepsiburadaContract.PlatformCode ? hepsiburada ?? new HepsiburadaAdapter() : connections;
        var context = Context(tenantId, connectionId, correlationId, "connection-test"); var result = await port.TestAsync(context, cancellationToken); if (!result.IsSuccess) { connection.LastErrorCode = result.Error!.Code; connection.Version++; await db.SaveChangesAsync(cancellationToken); return false; }
        var discovery = await port.DiscoverCapabilitiesAsync(context, cancellationToken); if (!discovery.IsSuccess) { connection.LastErrorCode = discovery.Error!.Code; connection.Version++; await db.SaveChangesAsync(cancellationToken); return false; }
        foreach (var evidence in discovery.Value!)
        {
            var capability = await db.PlatformCapabilities.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == evidence.Code, cancellationToken); if (capability is null) continue;
            capability.SupportLevel = string.Equals(evidence.SupportLevel, "SUPPORTED", StringComparison.Ordinal) ? CapabilitySupportLevel.Supported : CapabilitySupportLevel.Unknown; capability.SourceUrl = evidence.SourceUrl; capability.SourceVersion = evidence.SourceVersion; capability.RequiredScope = evidence.RequiredScope; capability.ConstraintsJson = evidence.ConstraintsJson; capability.EvidenceNote = evidence.EvidenceNote; capability.FixtureChecksum = evidence.FixtureChecksum; capability.VerifiedAt = evidence.VerifiedAt; capability.Version++;
        }
        connection.LastSuccessAt = now; connection.LastErrorCode = null; if (connection.Status == "DRAFT") connection.Status = "VERIFIED"; connection.Version++; await db.SaveChangesAsync(cancellationToken); return true;
    }

    private async Task<bool> MarkShopifyWebhookProcessed(Guid tenantId, string payloadJson, CancellationToken cancellationToken)
    {
        string externalMessageId; try { using var payload = JsonDocument.Parse(payloadJson); externalMessageId = payload.RootElement.GetProperty("externalMessageId").GetString() ?? ""; } catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
        var inbox = await db.InboxMessages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Source == "SHOPIFY_WEBHOOK" && x.ExternalMessageId == externalMessageId, cancellationToken);
        if (inbox is null) return false;
        inbox.ProcessedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return true;
    }

    private async Task<bool> SyncOrders(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "ORDERS", cancellationToken); var policy = await db.ConnectionSyncPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ORDERS", cancellationToken); var modifiedAfter = cursor.LastModifiedWatermark?.Subtract(TimeSpan.FromSeconds(policy?.OverlapSeconds ?? 0)); var next = cursor.OpaqueCursor; var orderPort = await OrderPort(tenantId, connectionId, cancellationToken);
        do
        {
            var result = await orderPort.PollAsync(Context(tenantId, connectionId, correlationId, $"order-sync:{next}"), new(modifiedAfter, null), new(next, 200), cancellationToken); if (!result.IsSuccess) return false;
            foreach (var order in result.Value!.Items) await UpsertOrder(tenantId, connectionId, order, cancellationToken);
            next = result.Value.NextCursor; cursor.OpaqueCursor = next; cursor.LastModifiedWatermark = result.Value.Items.Select(x => (DateTimeOffset?)x.LastModifiedAt).Max() ?? cursor.LastModifiedWatermark; cursor.LastSuccessAt = timeProvider.GetUtcNow(); cursor.Version++; await db.SaveChangesAsync(cancellationToken); if (!result.Value.HasMore) break;
        } while (!cancellationToken.IsCancellationRequested);
        return true;
    }

    private async Task<bool> IngestWebhook(Guid tenantId, Guid connectionId, string payloadJson, CancellationToken cancellationToken)
    {
        string raw; string externalMessageId; try { using var payload = JsonDocument.Parse(payloadJson); raw = payload.RootElement.GetProperty("rawJson").GetString() ?? ""; externalMessageId = payload.RootElement.GetProperty("externalMessageId").GetString() ?? ""; } catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
        AdapterPageResult<RemoteOrder> page; try { page = TrendyolJsonMapper.Orders(raw); } catch (JsonException) { return false; }
        foreach (var order in page.Items) await UpsertOrder(tenantId, connectionId, order, cancellationToken); var inbox = await db.InboxMessages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Source == "TRENDYOL_WEBHOOK" && x.ExternalMessageId == externalMessageId, cancellationToken); if (inbox is not null) inbox.ProcessedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return true;
    }

    private async Task UpsertOrder(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken)
    {
        if (!PackageIngestionSafety.TryGetOrderedQuantities(remote.Lines, out var remoteLineQuantities)) { await RecordIssue(tenantId, $"order-lines:{connectionId}:{remote.ExternalOrderId}:{remote.LastModifiedAt.ToUnixTimeMilliseconds()}", "ORDER_LINE_QUANTITY_INVARIANT_REJECTED", "Sipariş satır kimliği veya miktarı geçersizdi; olayın hiçbir parçası uygulanmadı.", cancellationToken); await db.SaveChangesAsync(cancellationToken); return; }
        foreach (var remotePackage in remote.Packages) if (!PackageIngestionSafety.TryNormalizeAll(remoteLineQuantities, remotePackage.Allocations, CanonicalPackage(remotePackage.RawStatus), out _)) { var rejectedEventId = PackageIngestionSafety.EventId(remotePackage.ExternalPackageId, remotePackage.OccurredAt); await RecordIssue(tenantId, $"package-quantity:{connectionId}:{rejectedEventId}", "PACKAGE_QUANTITY_INVARIANT_REJECTED", "Package miktarları sipariş satırı bütünlüğünü sağlamadı; olayın hiçbir parçası uygulanmadı.", cancellationToken); await db.SaveChangesAsync(cancellationToken); return; }
        var now = timeProvider.GetUtcNow(); var order = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalOrderId == remote.ExternalOrderId, cancellationToken);
        if (order is not null && remote.Packages.Count > 0) { var candidateEventIds = remote.Packages.Select(x => PackageIngestionSafety.EventId(x.ExternalPackageId, x.OccurredAt)).ToHashSet(StringComparer.Ordinal); var recordedEventIds = (await db.OrderStatusHistory.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id && candidateEventIds.Contains(x.SourceEventId)).Select(x => x.SourceEventId).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal); foreach (var localEvent in db.OrderStatusHistory.Local.Where(x => x.TenantId == tenantId && x.OrderId == order.Id && candidateEventIds.Contains(x.SourceEventId))) recordedEventIds.Add(localEvent.SourceEventId); if (remote.Lines.Count == 0 && PackageIngestionSafety.AllEventsRecorded(remote.Packages, recordedEventIds)) return; }
        if (order is null) { order = new Order { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ExternalOrderId = remote.ExternalOrderId, OrderNumber = remote.OrderNumber, Currency = remote.Currency, CustomerSnapshotJson = remote.CustomerSnapshotJson, ShipmentAddressSnapshotJson = remote.ShipmentAddressSnapshotJson, InvoiceAddressSnapshotJson = remote.InvoiceAddressSnapshotJson, DerivedStatus = "NEW", CreatedAt = now, Version = 1 }; db.Orders.Add(order); }
        else if (remote.LastModifiedAt < order.LastRemoteModifiedAt) return;
        order.OrderNumber = remote.OrderNumber; order.Currency = remote.Currency; order.GrossAmount = remote.GrossAmount; order.DiscountAmount = remote.DiscountAmount; order.NetAmount = remote.NetAmount; order.OrderedAt = remote.OrderedAt; order.LastRemoteModifiedAt = remote.LastModifiedAt; order.CustomerSnapshotJson = remote.CustomerSnapshotJson; order.ShipmentAddressSnapshotJson = remote.ShipmentAddressSnapshotJson; order.InvoiceAddressSnapshotJson = remote.InvoiceAddressSnapshotJson; order.UpdatedAt = now; if (db.Entry(order).State != EntityState.Added) order.Version++;
        var lines = new Dictionary<string, OrderLine>(StringComparer.Ordinal);
        foreach (var remoteLine in remote.Lines)
        {
            var line = await db.OrderLines.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.OrderId == order.Id && x.ExternalLineId == remoteLine.ExternalLineId, cancellationToken); if (line is null) { line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, ExternalLineId = remoteLine.ExternalLineId, Sku = remoteLine.Sku, TitleSnapshot = remoteLine.Title, RawStatus = remoteLine.RawStatus, Version = 1 }; db.OrderLines.Add(line); }
            line.Sku = remoteLine.Sku; line.Barcode = remoteLine.Barcode; line.TitleSnapshot = remoteLine.Title; line.OrderedQuantity = remoteLine.Quantity; line.UnitPrice = remoteLine.UnitPrice; line.VatRate = remoteLine.VatRate; line.RawStatus = remoteLine.RawStatus; if (db.Entry(line).State != EntityState.Added) line.Version++; lines[remoteLine.ExternalLineId] = line;
        }
        foreach (var remotePackage in remote.Packages)
        {
            var target = CanonicalPackage(remotePackage.RawStatus); var eventId = PackageIngestionSafety.EventId(remotePackage.ExternalPackageId, remotePackage.OccurredAt); var orderedQuantities = lines.ToDictionary(x => x.Key, x => x.Value.OrderedQuantity, StringComparer.Ordinal);
            if (!PackageIngestionSafety.TryNormalizeAll(orderedQuantities, remotePackage.Allocations, target, out var safeAllocations)) { await RecordIssue(tenantId, $"package-quantity:{connectionId}:{eventId}", "PACKAGE_QUANTITY_INVARIANT_REJECTED", "Package miktarları sipariş satırı bütünlüğünü sağlamadı; olayın hiçbir parçası uygulanmadı.", cancellationToken); continue; }
            if (db.OrderStatusHistory.Local.Any(x => x.TenantId == tenantId && x.OrderId == order.Id && x.SourceEventId == eventId) || await db.OrderStatusHistory.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.OrderId == order.Id && x.SourceEventId == eventId, cancellationToken)) continue;
            var package = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalPackageId == remotePackage.ExternalPackageId, cancellationToken); var accept = package is null || PackageIngestionSafety.ShouldAccept(package.Status, package.StatusOccurredAt, target, remotePackage.OccurredAt);
            if (package is null) { package = new ShipmentPackage { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, OrderId = order.Id, ExternalPackageId = remotePackage.ExternalPackageId, Status = target, RawStatus = remotePackage.RawStatus, StatusOccurredAt = remotePackage.OccurredAt, CreatedAt = now, Version = 1 }; db.ShipmentPackages.Add(package); }
            else if (accept) { package.Status = target; package.RawStatus = remotePackage.RawStatus; package.StatusOccurredAt = remotePackage.OccurredAt; package.Version++; }
            else if (remotePackage.OccurredAt >= package.StatusOccurredAt && package.Status != target) await RecordIssue(tenantId, $"package-transition:{package.Id}:{remotePackage.RawStatus}", "PACKAGE_TRANSITION_REJECTED", "Out-of-order veya izin verilmeyen package geçişi mevcut durumu geriye götürmedi.", cancellationToken);
            if (accept)
            {
                package.OriginExternalPackageId = remotePackage.OriginExternalPackageId; package.CargoProviderExternalId = remotePackage.CargoProviderExternalId; package.CargoTrackingNumber = remotePackage.CargoTrackingNumber; package.GrossAmount = remotePackage.GrossAmount; package.DiscountAmount = remotePackage.DiscountAmount; package.NetAmount = remotePackage.NetAmount; package.UpdatedAt = now; db.OrderStatusHistory.Add(new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, PackageId = package.Id, CanonicalStatus = Wire(target), RawStatus = remotePackage.RawStatus, SourceEventId = eventId, OccurredAt = remotePackage.OccurredAt, RecordedAt = now });
                foreach (var remoteAllocation in remotePackage.Allocations) if (lines.TryGetValue(remoteAllocation.ExternalLineId, out var line) && safeAllocations.TryGetValue(remoteAllocation.ExternalLineId, out var safe)) { var allocation = await db.PackageLineAllocations.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.PackageId == package.Id && x.OrderLineId == line.Id && x.SourceEventId == eventId, cancellationToken); if (allocation is null) { allocation = new PackageLineAllocation { Id = Guid.CreateVersion7(), TenantId = tenantId, PackageId = package.Id, OrderLineId = line.Id, SourceEventId = eventId, AllocatedQuantity = safe.ActiveAllocatedQuantity, CancelledQuantity = safe.CancelledQuantity, ShippedQuantity = safe.ShippedQuantity, DeliveredQuantity = safe.DeliveredQuantity, ReturnedQuantity = safe.ReturnedQuantity }; db.PackageLineAllocations.Add(allocation); line.CancelledQuantity = Math.Max(line.CancelledQuantity, allocation.CancelledQuantity); line.ShippedQuantity = Math.Max(line.ShippedQuantity, allocation.ShippedQuantity); line.DeliveredQuantity = Math.Max(line.DeliveredQuantity, allocation.DeliveredQuantity); line.ReturnedQuantity = Math.Max(line.ReturnedQuantity, allocation.ReturnedQuantity); } }
            }
        }
        var persistedStatuses = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).Select(x => new { x.Id, x.Status }).ToListAsync(cancellationToken); var acceptedStatuses = persistedStatuses.ToDictionary(x => x.Id, x => x.Status); foreach (var tracked in db.ShipmentPackages.Local.Where(x => x.TenantId == tenantId && x.OrderId == order.Id)) acceptedStatuses[tracked.Id] = tracked.Status; order.DerivedStatus = Wire(acceptedStatuses.Count == 0 ? ShipmentPackageStatus.New : acceptedStatuses.Values.OrderByDescending(StatusRank).First()); await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> SyncReturns(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "RETURNS", cancellationToken); var pageNumber = cursor.OpaqueCursor;
        do
        {
            var result = await returns.PollAsync(Context(tenantId, connectionId, correlationId, $"return-sync:{pageNumber}"), new(cursor.LastModifiedWatermark, null), new(pageNumber, 200), cancellationToken); if (!result.IsSuccess) return false; foreach (var claim in result.Value!.Items) await UpsertReturn(tenantId, connectionId, claim, cancellationToken); pageNumber = result.Value.NextCursor; cursor.OpaqueCursor = pageNumber; cursor.LastModifiedWatermark = result.Value.Items.Select(x => (DateTimeOffset?)x.LastModifiedAt).Max() ?? cursor.LastModifiedWatermark; cursor.LastSuccessAt = timeProvider.GetUtcNow(); cursor.Version++; await db.SaveChangesAsync(cancellationToken); if (!result.Value.HasMore) break;
        } while (!cancellationToken.IsCancellationRequested); return true;
    }

    private async Task UpsertReturn(Guid tenantId, Guid connectionId, RemoteReturnClaim remote, CancellationToken cancellationToken)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ExternalOrderId == remote.ExternalOrderId || x.OrderNumber == remote.ExternalOrderId), cancellationToken);
        if (order is null)
        {
            await RecordIssue(tenantId, $"return-order:{connectionId}:{remote.ExternalOrderId}", "RETURN_ORDER_NOT_FOUND", "Return claim yerel order ile eşleşmedi; sessiz kayıt oluşturulmadı.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        var now = timeProvider.GetUtcNow(); var target = CanonicalReturn(remote.RawStatus); var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalClaimId == remote.ExternalClaimId, cancellationToken);
        if (claim is null) { claim = new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, OrderId = order.Id, ExternalClaimId = remote.ExternalClaimId, Status = target, RawStatus = remote.RawStatus, LastRemoteModifiedAt = remote.LastModifiedAt, CreatedAt = now, UpdatedAt = now, Version = 1 }; db.ReturnClaims.Add(claim); }
        else { if (remote.LastModifiedAt < claim.LastRemoteModifiedAt || !ReturnClaimStateMachine.CanTransition(claim.Status, target)) return; claim.Status = target; claim.RawStatus = remote.RawStatus; claim.LastRemoteModifiedAt = remote.LastModifiedAt; claim.UpdatedAt = now; claim.Version++; }
        claim.ReasonCode = remote.ReasonCode; claim.ReasonText = remote.ReasonText; claim.ActionDueAt = remote.ActionDueAt;
        foreach (var remoteLine in remote.Lines) { var orderLine = await db.OrderLines.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.OrderId == order.Id && x.ExternalLineId == remoteLine.ExternalOrderLineId, cancellationToken); if (orderLine is null) continue; var line = await db.ReturnLines.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ClaimId == claim.Id && x.ExternalLineId == remoteLine.ExternalLineId, cancellationToken); if (line is null) db.ReturnLines.Add(new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claim.Id, OrderLineId = orderLine.Id, ExternalLineId = remoteLine.ExternalLineId, Quantity = remoteLine.Quantity }); else line.Quantity = remoteLine.Quantity; }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> ShipmentAction(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        try { using var payload = JsonDocument.Parse(payloadJson); var packageId = payload.RootElement.GetProperty("packageId").GetGuid(); var action = payload.RootElement.GetProperty("Action").GetString()!; var body = payload.RootElement.GetProperty("PayloadJson").GetString()!; var package = await db.ShipmentPackages.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == packageId && x.ConnectionId == connectionId, cancellationToken); var orderPort = await OrderPort(tenantId, connectionId, cancellationToken); var result = await orderPort.ExecutePackageActionAsync(Context(tenantId, connectionId, correlationId, $"shipment:{packageId}:{action}"), new(package.ExternalPackageId, action, body), cancellationToken); return result.IsSuccess; } catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return false; }
    }

    private async Task<bool> ReturnAction(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        try { using var payload = JsonDocument.Parse(payloadJson); var decisionId = payload.RootElement.GetProperty("decisionId").GetGuid(); var decision = await db.ReturnDecisions.SingleAsync(x => x.TenantId == tenantId && x.Id == decisionId, cancellationToken); var claim = await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == decision.ClaimId && x.ConnectionId == connectionId, cancellationToken); var evidence = await db.ReturnEvidence.AsNoTracking().Where(x => x.TenantId == tenantId && x.DecisionId == decisionId).Select(x => x.FileAssetId).ToListAsync(cancellationToken); var result = await returns.ExecuteAsync(Context(tenantId, connectionId, correlationId, decision.IdempotencyKey), new(claim.ExternalClaimId, decision.Action, decision.ReasonCode, decision.Explanation, evidence), cancellationToken); decision.Status = result.IsSuccess ? "SUCCEEDED" : "FAILED"; decision.ExternalOperationId = result.Value?.ExternalOperationId; decision.ErrorCode = result.Error?.Code; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return result.IsSuccess; } catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return false; }
    }

    private async Task<SyncCursor> Cursor(Guid tenantId, Guid connectionId, string resource, CancellationToken cancellationToken) { var cursor = await db.SyncCursors.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resource, cancellationToken); if (cursor is not null) return cursor; cursor = new SyncCursor { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ResourceType = resource, Version = 1 }; db.SyncCursors.Add(cursor); return cursor; }
    private async Task<IOrderPort> OrderPort(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleAsync(cancellationToken);
        return platform == ShopifyContract.PlatformCode ? shopify : platform == HepsiburadaContract.PlatformCode ? hepsiburada ?? new HepsiburadaAdapter() : orders;
    }
    private async Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken) { var now = timeProvider.GetUtcNow(); var issue = await db.OperationalIssues.SingleOrDefaultAsync(x => x.DedupeKey == key, cancellationToken); if (issue is null) db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = code, Summary = summary, Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 }); else { issue.LastSeenAt = now; issue.OccurrenceCount++; } }
    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotency) => new(tenantId, connectionId, correlationId, idempotency, timeProvider.GetUtcNow().AddMinutes(2));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ShipmentPackageStatus CanonicalPackage(string raw) => raw.ToUpperInvariant() switch { "CREATED" => ShipmentPackageStatus.New, "PICKING" => ShipmentPackageStatus.Processing, "INVOICED" => ShipmentPackageStatus.ReadyToShip, "SHIPPED" => ShipmentPackageStatus.Shipped, "DELIVERED" => ShipmentPackageStatus.Delivered, "CANCELLED" or "UNSUPPLIED" => ShipmentPackageStatus.Cancelled, "UNDELIVERED" => ShipmentPackageStatus.Undelivered, "RETURNED" => ShipmentPackageStatus.Returned, "AWAITING" or "UNPACKED" or "AT_COLLECTION_POINT" => ShipmentPackageStatus.OnHold, _ => ShipmentPackageStatus.ManualReview };
    private static ReturnClaimStatus CanonicalReturn(string raw) => raw.ToUpperInvariant() switch { "CREATED" => ReturnClaimStatus.Requested, "WAITINGINACTION" or "INANALYSIS" or "WAITINGFRAUDCHECK" => ReturnClaimStatus.ActionRequired, "ACCEPTED" => ReturnClaimStatus.Approved, "REJECTED" => ReturnClaimStatus.Rejected, "UNRESOLVED" => ReturnClaimStatus.Disputed, "COMPLETED" => ReturnClaimStatus.Completed, "CANCELLED" => ReturnClaimStatus.Cancelled, _ => ReturnClaimStatus.ActionRequired };
    private static int StatusRank(ShipmentPackageStatus status) => status switch { ShipmentPackageStatus.New => 1, ShipmentPackageStatus.Processing => 2, ShipmentPackageStatus.OnHold => 3, ShipmentPackageStatus.ReadyToShip => 4, ShipmentPackageStatus.Shipped => 5, ShipmentPackageStatus.Undelivered => 6, ShipmentPackageStatus.Delivered => 7, ShipmentPackageStatus.ReturnInTransit => 8, ShipmentPackageStatus.Returned => 9, ShipmentPackageStatus.PartiallyCancelled => 2, ShipmentPackageStatus.Cancelled => 9, _ => 10 };
    private static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();
}
