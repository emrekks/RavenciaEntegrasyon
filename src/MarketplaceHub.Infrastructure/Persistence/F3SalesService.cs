using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class F3SalesService(AppDbContext db, CursorCodec cursors, IConfiguration configuration, TimeProvider timeProvider) : IF3SalesService
{
    public async Task<PageResult<OrderListView>> OrdersAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken)
    {
        var afterId = Decode(after); var query = db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId); if (afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0); if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.DerivedStatus == status.Trim().ToUpperInvariant());
        var rows = await query.OrderBy(x => x.Id).Take(limit + 1).Select(x => new OrderListView(x.Id, x.OrderNumber, x.DerivedStatus, x.Currency, x.NetAmount, x.OrderedAt, db.OrderLines.Count(line => line.TenantId == tenantId && line.OrderId == x.Id), db.ShipmentPackages.Count(package => package.TenantId == tenantId && package.OrderId == x.Id), x.Version)).ToListAsync(cancellationToken);
        return Page(rows, limit, x => x.Id);
    }

    public async Task<ServiceResult<OrderDetailView>> OrderAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (order is null) return NotFound<OrderDetailView>();
        var lines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == id).OrderBy(x => x.Id).Select(x => new OrderLineView(x.Id, x.Sku, x.Barcode, x.TitleSnapshot, x.OrderedQuantity, x.CancelledQuantity, x.ShippedQuantity, x.DeliveredQuantity, x.ReturnedQuantity, x.UnitPrice, x.VatRate, x.RawStatus)).ToListAsync(cancellationToken);
        var packages = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        return ServiceResult<OrderDetailView>.Ok(new(order.Id, order.OrderNumber, order.DerivedStatus, order.Currency, order.GrossAmount, order.DiscountAmount, order.NetAmount, order.OrderedAt, lines, packages.Select(x => Map(x, order.OrderNumber)).ToList(), order.Version));
    }

    public async Task<PageResult<ShipmentView>> ShipmentsAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken)
    {
        var afterId = Decode(after); var query = db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId); if (afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0); if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ShipmentPackageStatus>(status, true, out var parsed)) query = query.Where(x => x.Status == parsed);
        var rows = await (from package in query orderby package.Id join order in db.Orders.AsNoTracking() on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id } select new { Package = package, order.OrderNumber }).Take(limit + 1).ToListAsync(cancellationToken);
        return Page(rows.Select(x => Map(x.Package, x.OrderNumber)).ToList(), limit, x => x.Id);
    }

    public async Task<ServiceResult<ShipmentDetailView>> ShipmentAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var row = await (from package in db.ShipmentPackages.AsNoTracking() where package.TenantId == tenantId && package.Id == id join order in db.Orders.AsNoTracking() on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id } select new { Package = package, order.OrderNumber }).SingleOrDefaultAsync(cancellationToken); if (row is null) return NotFound<ShipmentDetailView>();
        var actions = await CapabilityValues(tenantId, row.Package.ConnectionId, F3Capabilities.ShipmentWrite, "allowedActions", cancellationToken); var formats = await CapabilityValues(tenantId, row.Package.ConnectionId, F3Capabilities.LabelRead, "formats", cancellationToken);
        var documents = await db.ShipmentDocuments.AsNoTracking().Where(x => x.TenantId == tenantId && x.PackageId == id).OrderByDescending(x => x.DocumentVersion).Select(x => new ShipmentDocumentView(x.Id, x.DocumentKind, x.Format, x.Source, x.DocumentVersion, x.CreatedAt, x.ExpiresAt)).ToListAsync(cancellationToken);
        return ServiceResult<ShipmentDetailView>.Ok(new(Map(row.Package, row.OrderNumber), actions, formats, documents));
    }

    public Task<ServiceResult<Guid>> EnqueueOrderSyncAsync(Guid tenantId, Guid connectionId, string? externalOrderId, string correlationId, CancellationToken cancellationToken) => EnqueueRead(tenantId, connectionId, F3Capabilities.OrderRead, F3JobTypes.OrderSync, JsonSerializer.Serialize(new { connectionId, externalOrderId }), correlationId, cancellationToken);

    public Task<ServiceResult<Guid>> EnqueueReferenceSyncAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken) => EnqueueRead(tenantId, connectionId, F3Capabilities.ReferenceRead, F3JobTypes.ReferenceSync, JsonSerializer.Serialize(new { connectionId, resourceType = "CATEGORIES" }), correlationId, cancellationToken);

    public async Task<ServiceResult<Guid>> EnqueueShipmentActionAsync(Guid tenantId, Guid packageId, long expectedVersion, ShipmentActionCommand command, string correlationId, CancellationToken cancellationToken)
    {
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken); if (package is null) return NotFound<Guid>(); if (package.Version != expectedVersion) return Precondition<Guid>(package.Version);
        var actions = await CapabilityValues(tenantId, package.ConnectionId, F3Capabilities.ShipmentWrite, "allowedActions", cancellationToken); if (!actions.Contains(command.Action, StringComparer.Ordinal)) return ServiceResult<Guid>.Fail("CAPABILITY_UNKNOWN", "Bu shipment aksiyonu için Stage/SIT capability kanıtı yok.", 422); if (!await WritesEnabled(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        return await Enqueue(tenantId, package.ConnectionId, F3JobTypes.ShipmentAction, $"shipment-action:{package.Id}:v{package.Version}:{command.Action}", JsonSerializer.Serialize(new { packageId, command.Action, command.PayloadJson }), correlationId, cancellationToken);
    }

    public async Task<PageResult<ReturnListView>> ReturnsAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken)
    {
        var afterId = Decode(after); var query = db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId); if (afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0); if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ReturnClaimStatus>(status, true, out var parsed)) query = query.Where(x => x.Status == parsed);
        var rows = await (from claim in query orderby claim.Id join order in db.Orders.AsNoTracking() on new { claim.TenantId, claim.OrderId } equals new { order.TenantId, OrderId = order.Id } select new { Claim = claim, order.OrderNumber }).Take(limit + 1).ToListAsync(cancellationToken);
        return Page(rows.Select(x => new ReturnListView(x.Claim.Id, x.Claim.ExternalClaimId, x.OrderNumber, Wire(x.Claim.Status), x.Claim.RawStatus, x.Claim.ReasonText, x.Claim.ActionDueAt, x.Claim.Version)).ToList(), limit, x => x.Id);
    }

    public async Task<ServiceResult<ReturnDetailView>> ReturnAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var row = await (from claim in db.ReturnClaims.AsNoTracking() where claim.TenantId == tenantId && claim.Id == id join order in db.Orders.AsNoTracking() on new { claim.TenantId, claim.OrderId } equals new { order.TenantId, OrderId = order.Id } select new { Claim = claim, order.OrderNumber }).SingleOrDefaultAsync(cancellationToken); if (row is null) return NotFound<ReturnDetailView>();
        var actions = await CapabilityValues(tenantId, row.Claim.ConnectionId, F3Capabilities.ReturnWrite, "allowedActions", cancellationToken); return ServiceResult<ReturnDetailView>.Ok(Map(row.Claim, row.OrderNumber, actions));
    }

    public Task<ServiceResult<Guid>> EnqueueReturnSyncAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken) => EnqueueRead(tenantId, connectionId, F3Capabilities.ReturnRead, F3JobTypes.ReturnSync, JsonSerializer.Serialize(new { connectionId }), correlationId, cancellationToken);

    public async Task<ServiceResult<Guid>> EnqueueReturnActionAsync(Guid tenantId, Guid userId, Guid claimId, long expectedVersion, ReturnDecisionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var prior = await db.ReturnDecisions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, cancellationToken); if (prior is not null) return ServiceResult<Guid>.Ok(prior.Id);
        var claim = await db.ReturnClaims.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claimId, cancellationToken); if (claim is null) return NotFound<Guid>(); if (claim.Version != expectedVersion) return Precondition<Guid>(claim.Version); var actions = await CapabilityValues(tenantId, claim.ConnectionId, F3Capabilities.ReturnWrite, "allowedActions", cancellationToken); if (!actions.Contains(command.Action, StringComparer.Ordinal)) return ServiceResult<Guid>.Fail("CAPABILITY_UNKNOWN", "Bu return aksiyonu için Stage/SIT capability kanıtı yok.", 422); if (!await WritesEnabled(tenantId, claim.ConnectionId, cancellationToken)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        var decision = new ReturnDecision { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, Action = command.Action, ReasonCode = command.ReasonCode, Explanation = command.Explanation, IdempotencyKey = idempotencyKey, Status = "PENDING", ActorUserId = userId, CreatedAt = timeProvider.GetUtcNow() }; db.ReturnDecisions.Add(decision);
        if (command.EvidenceAssetIds is not null) foreach (var assetId in command.EvidenceAssetIds.Distinct()) { var asset = await db.FileAssets.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == assetId && x.ArchivedAt == null, cancellationToken); if (asset is null) return ServiceResult<Guid>.Fail("EVIDENCE_NOT_FOUND", "İade kanıt dosyası tenant private storage içinde bulunamadı.", 422); db.ReturnEvidence.Add(new ReturnEvidence { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, DecisionId = decision.Id, FileAssetId = asset.Id, EvidenceKind = asset.Classification, Checksum = asset.Sha256, CreatedAt = timeProvider.GetUtcNow() }); }
        var job = NewJob(tenantId, claim.ConnectionId, F3JobTypes.ReturnAction, $"return-action:{idempotencyKey}", JsonSerializer.Serialize(new { claimId, decisionId = decision.Id }), correlationId); db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(job.Id);
    }

    public async Task<ServiceResult<ReturnDetailView>> ApplyDispositionAsync(Guid tenantId, Guid userId, Guid claimId, ReturnDispositionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var existing = await db.ReturnStockDispositions.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, cancellationToken); if (existing) return await ReturnAsync(tenantId, claimId, cancellationToken);
        if (!Enum.TryParse<ReturnStockDispositionKind>(command.Disposition, true, out var disposition)) return Invalid<ReturnDetailView>("disposition", "Disposition PASS, QUARANTINE, DAMAGED veya NOT_RECEIVED olmalıdır."); if (command.Quantity <= 0 || string.IsNullOrWhiteSpace(command.Reason)) return Invalid<ReturnDetailView>("quantity", "Pozitif quantity ve açıklama zorunludur.");
        var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claimId, cancellationToken); if (claim is null) return NotFound<ReturnDetailView>(); if (claim.Status is not (ReturnClaimStatus.Approved or ReturnClaimStatus.Completed)) return ServiceResult<ReturnDetailView>.Fail("RETURN_DISPOSITION_NOT_ALLOWED", "Stok disposition yalnız onaylanmış veya tamamlanmış iade için uygulanabilir.", 409);
        var line = await db.ReturnLines.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ClaimId == claimId && x.Id == command.ReturnLineId, cancellationToken); if (line is null) return NotFound<ReturnDetailView>(); var already = await db.ReturnStockDispositions.Where(x => x.TenantId == tenantId && x.ReturnLineId == line.Id).SumAsync(x => x.Quantity, cancellationToken); if (already + command.Quantity > line.Quantity) return ServiceResult<ReturnDetailView>.Fail("RETURN_QUANTITY_EXCEEDED", "Disposition toplamı iade satırı miktarını aşamaz.", 409);
        var variantId = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == line.OrderLineId).Select(x => x.VariantId).SingleAsync(cancellationToken); if (variantId is null) return ServiceResult<ReturnDetailView>.Fail("INVENTORY_MAPPING_REQUIRED", "İade satırı yerel varyantla eşleşmiyor.", 422); var item = await db.InventoryItems.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variantId && x.LocationCode == "MAIN", cancellationToken); if (item is null) return ServiceResult<ReturnDetailView>.Fail("INVENTORY_MAPPING_REQUIRED", "MAIN inventory item bulunamadı.", 422);
        var now = timeProvider.GetUtcNow(); db.ReturnStockDispositions.Add(new ReturnStockDisposition { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, ReturnLineId = line.Id, InventoryItemId = item.Id, Disposition = disposition, Quantity = command.Quantity, IdempotencyKey = idempotencyKey, Reason = command.Reason.Trim(), ActorUserId = userId, CreatedAt = now });
        if (disposition == ReturnStockDispositionKind.Pass) { item.OnHand += command.Quantity; item.Available = Math.Max(0, item.OnHand - item.Reserved); item.ProjectionVersion++; item.Version++; db.StockLedgerEntries.Add(new StockLedgerEntry { Id = Guid.CreateVersion7(), TenantId = tenantId, InventoryItemId = item.Id, MovementType = "RETURN_PASS", QuantityDelta = command.Quantity, SourceType = "RETURN_CLAIM", SourceId = claim.ExternalClaimId, SourceEventId = idempotencyKey, IdempotencyKey = idempotencyKey, OccurredAt = now, RecordedAt = now, ActorUserId = userId, CorrelationId = correlationId }); }
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = userId, Action = "RETURN_STOCK_DISPOSITION", TargetType = "ReturnClaim", TargetId = claimId.ToString("D"), Reason = command.Reason.Trim(), CorrelationId = correlationId, CreatedAt = now }); await db.SaveChangesAsync(cancellationToken); return await ReturnAsync(tenantId, claimId, cancellationToken);
    }

    private async Task<ServiceResult<Guid>> EnqueueRead(Guid tenantId, Guid connectionId, string capability, string type, string payload, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && ((x.PlatformCode == "TRENDYOL" || x.PlatformCode == ShopifyContract.PlatformCode) && x.Status == "ACTIVE" || x.PlatformCode == HepsiburadaContract.PlatformCode && x.Environment == "STAGE" && x.Status == "VERIFIED"), cancellationToken); if (connection is null) return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Aktif marketplace bağlantısı veya doğrulanmış Hepsiburada SIT bağlantısı gerekir.", 422); if (!await Supported(tenantId, connectionId, capability, cancellationToken)) return ServiceResult<Guid>.Fail("CAPABILITY_UNKNOWN", "Read capability Stage/SIT kanıtı olmadan sync işi oluşturulmaz.", 422); var selectedType = connection.PlatformCode == ShopifyContract.PlatformCode && type == F3JobTypes.OrderSync ? ShopifyContract.OrderSyncJob : type; return await Enqueue(tenantId, connectionId, selectedType, $"{selectedType.ToLowerInvariant()}:{connectionId}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))}", payload, correlationId, cancellationToken);
    }
    private async Task<ServiceResult<Guid>> Enqueue(Guid tenantId, Guid connectionId, string type, string dedup, string payload, string correlationId, CancellationToken cancellationToken)
    {
        var recurringRead = type is F3JobTypes.ReferenceSync or F3JobTypes.OrderSync or F3JobTypes.ReturnSync or ShopifyContract.OrderSyncJob;
        var active = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == type && (recurringRead ? x.JobDedupKey.StartsWith(dedup) : x.JobDedupKey == dedup) && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled), cancellationToken);
        if (active is not null) return ServiceResult<Guid>.Ok(active.Id);

        var job = NewJob(tenantId, connectionId, type, recurringRead ? $"{dedup}:{timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}" : dedup, payload, correlationId);
        db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(job.Id);
    }
    private IntegrationJob NewJob(Guid tenantId, Guid connectionId, string type, string dedup, string payload, string correlationId) => new() { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, JobType = type, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), JobDedupKey = dedup, EffectIdempotencyKey = dedup, AvailableAt = timeProvider.GetUtcNow(), CorrelationId = correlationId, Version = 1 };
    private Task<bool> Supported(Guid tenantId, Guid connectionId, string code, CancellationToken cancellationToken) => db.PlatformCapabilities.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == code && x.SupportLevel == CapabilitySupportLevel.Supported, cancellationToken);
    private async Task<bool> WritesEnabled(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) { if (!configuration.GetValue<bool>("FeatureFlags:ExternalWrites")) return false; var settings = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.SettingsJson).SingleOrDefaultAsync(cancellationToken); if (settings is null) return false; try { return JsonDocument.Parse(settings).RootElement.TryGetProperty("ExternalWritesEnabled", out var value) && value.ValueKind == JsonValueKind.True; } catch (JsonException) { return false; } }
    private async Task<IReadOnlyList<string>> CapabilityValues(Guid tenantId, Guid connectionId, string code, string property, CancellationToken cancellationToken) { var capability = await db.PlatformCapabilities.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == code && x.SupportLevel == CapabilitySupportLevel.Supported, cancellationToken); if (capability?.ConstraintsJson is null) return []; try { using var doc = JsonDocument.Parse(capability.ConstraintsJson); return doc.RootElement.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : []; } catch (JsonException) { return []; } }
    private Guid Decode(string? cursor) => cursors.TryDecode(cursor, out var id) ? id : throw new ArgumentException("Cursor geçersiz veya süresi dolmuş.", nameof(cursor));
    private PageResult<T> Page<T>(List<T> rows, int limit, Func<T, Guid> id) { var hasMore = rows.Count > limit; var items = rows.Take(limit).ToList(); return new(items, hasMore ? cursors.Encode(id(items[^1])) : null, hasMore); }
    private static ShipmentView Map(ShipmentPackage x, string orderNumber) => new(x.Id, x.OrderId, orderNumber, x.ExternalPackageId, Wire(x.Status), x.RawStatus, x.CargoTrackingNumber, x.StatusOccurredAt, x.Version);
    private static ReturnDetailView Map(ReturnClaim x, string orderNumber, IReadOnlyList<string> actions) => new(x.Id, x.ExternalClaimId, orderNumber, Wire(x.Status), x.RawStatus, x.ReasonCode, x.ReasonText, x.ActionDueAt, actions, x.Version);
    private static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();
    private static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Precondition<T>(long version) => ServiceResult<T>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{version}.", 412);
}
