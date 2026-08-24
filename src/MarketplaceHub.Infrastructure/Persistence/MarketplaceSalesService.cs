using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class MarketplaceSalesService(AppDbContext db, CursorCodec cursors, IConfiguration configuration, IProductVisualLookupPort productVisuals, IReturnPort returns, TimeProvider timeProvider) : IMarketplaceSalesService
{
    public async Task<PageResult<OrderListView>> OrdersAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken)
    {
        // The panel is a local read model. Remote reads belong to the scheduled worker.
        var afterId = Decode(after);
        var query = db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.DerivedStatus == status.Trim().ToUpperInvariant());
        var orders = await query.OrderBy(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var orderIds = orders.Select(x => x.Id).ToArray();
        var connectionIds = orders.Select(x => x.ConnectionId).Distinct().ToArray();
        var lines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).ToListAsync(cancellationToken);
        var packages = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).OrderByDescending(x => x.StatusOccurredAt).ToListAsync(cancellationToken);
        var invoices = await db.Invoices.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
        var connections = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && connectionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var variantIds = lines.Where(x => x.VariantId is not null).Select(x => x.VariantId!.Value).Distinct().ToArray();
        var lineSkus = lines.Select(x => x.Sku).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        var variantRows = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && (variantIds.Contains(x.Id) || lineSkus.Contains(x.Sku))).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var variants = variantRows.ToDictionary(x => x.Id, x => x);
        var variantsBySku = variantRows.GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var variantsByBarcode = variantRows.Where(x => !string.IsNullOrWhiteSpace(x.Barcode)).GroupBy(x => x.Barcode!, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var imageUrls = await MediaUrls(tenantId, variantRows.Select(x => (Guid?)x.Id), cancellationToken);
        var now = timeProvider.GetUtcNow();
        var rows = orders.Select(order =>
        {
            var orderLines = lines.Where(x => x.OrderId == order.Id).ToList();
            var package = packages.FirstOrDefault(x => x.OrderId == order.Id);
            var invoice = invoices.FirstOrDefault(x => x.OrderId == order.Id && x.OriginalInvoiceId == null);
            var connection = connections.GetValueOrDefault(order.ConnectionId);
            var customer = Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson);
            var dueAt = OperationalDueAt(order.CustomerSnapshotJson);
            var terminal = order.DerivedStatus is "DELIVERED" or "CANCELLED" or "RETURNED";
            var lineViews = orderLines.Select(x =>
            {
                var variant = ResolveVariant(x, variants, variantsBySku, variantsByBarcode);
                var source = SourceLine(x.SourceSnapshotJson);
                return new OrderLineView(x.Id, x.Sku, x.Barcode, x.TitleSnapshot, x.OrderedQuantity, x.CancelledQuantity, x.ShippedQuantity, x.DeliveredQuantity, x.ReturnedQuantity, x.UnitPrice, x.VatRate, x.RawStatus, x.VariantId, variant?.ModelCode ?? source.ModelCode, variant?.OptionSignature ?? source.OptionSignature, source.ImageUrl ?? (variant is null ? null : imageUrls.GetValueOrDefault(variant.Id)));
            }).ToList();
            var packageViews = packages.Where(x => x.OrderId == order.Id).Select(x => Map(x, order.OrderNumber)).ToList();
            return new OrderListView(
                order.Id, order.OrderNumber, order.DerivedStatus, order.Currency, order.NetAmount, order.OrderedAt,
                orderLines.Count, packages.Count(x => x.OrderId == order.Id), order.Version,
                order.ConnectionId, connection?.PlatformCode ?? "TRENDYOL", connection?.DisplayName ?? "Trendyol",
                customer.Name, customer.OrderType, customer.IsMicroExport, dueAt,
                !terminal && dueAt is not null && dueAt <= now.AddHours(24), InvoiceLabel(invoice, order.CustomerSnapshotJson),
                package?.CargoProviderExternalId, package?.CargoTrackingNumber,
                orderLines.Select(x => ResolveVariant(x, variants, variantsBySku, variantsByBarcode)).Where(x => x is not null).Select(x => imageUrls.GetValueOrDefault(x!.Id)).FirstOrDefault(x => x is not null),
                orderLines.Sum(x => x.OrderedQuantity), customer.Email, customer.TaxOrIdentityNumber,
                order.ShipmentAddressSnapshotJson, order.InvoiceAddressSnapshotJson, order.GrossAmount, order.DiscountAmount,
                lineViews, packageViews, invoice?.Id, InvoiceDocumentUrl(order.CustomerSnapshotJson));
        }).ToList();
        return Page(rows, limit, x => x.Id);
    }

    public async Task<OrderSummaryView> OrderSummaryAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var summary = await db.ShipmentPackages.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .GroupBy(_ => 1)
            .Select(group => new OrderSummaryView(
                group.Count(),
                group.Count(x => x.Status == ShipmentPackageStatus.New),
                group.Count(x => x.Status == ShipmentPackageStatus.Processing || x.Status == ShipmentPackageStatus.ReadyToShip),
                group.Count(x => x.Status == ShipmentPackageStatus.Shipped || x.Status == ShipmentPackageStatus.Undelivered),
                group.Count(x => x.Status == ShipmentPackageStatus.Delivered),
                group.Count(x => x.OriginExternalPackageId != null),
                group.Count(x => x.Status == ShipmentPackageStatus.OnHold)))
            .SingleOrDefaultAsync(cancellationToken);
        return summary ?? new OrderSummaryView(0, 0, 0, 0, 0, 0, 0);
    }

    public async Task<ServiceResult<OrderDetailView>> OrderAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        // Detail pages also read the persisted snapshot; they never call the marketplace.
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (order is null) return NotFound<OrderDetailView>();
        var orderLines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var variantIds = orderLines.Where(x => x.VariantId is not null).Select(x => x.VariantId!.Value).Distinct().ToArray();
        var lineSkus = orderLines.Select(x => x.Sku).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        var variantRows = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && (variantIds.Contains(x.Id) || lineSkus.Contains(x.Sku))).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var variants = variantRows.ToDictionary(x => x.Id, x => x);
        var variantsBySku = variantRows.GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var variantsByBarcode = variantRows.Where(x => !string.IsNullOrWhiteSpace(x.Barcode)).GroupBy(x => x.Barcode!, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var imageUrls = await MediaUrls(tenantId, variantRows.Select(x => (Guid?)x.Id), cancellationToken);
        var lines = orderLines.Select(x =>
        {
            var variant = ResolveVariant(x, variants, variantsBySku, variantsByBarcode);
            var source = SourceLine(x.SourceSnapshotJson);
            return new OrderLineView(x.Id, x.Sku, x.Barcode, x.TitleSnapshot, x.OrderedQuantity, x.CancelledQuantity, x.ShippedQuantity, x.DeliveredQuantity, x.ReturnedQuantity, x.UnitPrice, x.VatRate, x.RawStatus, x.VariantId, variant?.ModelCode ?? source.ModelCode, variant?.OptionSignature ?? source.OptionSignature, source.ImageUrl ?? (variant is null ? null : imageUrls.GetValueOrDefault(variant.Id)));
        }).ToList();
        var packages = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == order.ConnectionId, cancellationToken);
        var invoice = await db.Invoices.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id && x.OriginalInvoiceId == null).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        var customer = Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson);
        return ServiceResult<OrderDetailView>.Ok(new(
            order.Id, order.OrderNumber, order.DerivedStatus, order.Currency, order.GrossAmount, order.DiscountAmount, order.NetAmount, order.OrderedAt,
            lines, packages.Select(x => Map(x, order.OrderNumber)).ToList(), order.Version,
            order.ConnectionId, connection?.PlatformCode ?? "TRENDYOL", connection?.DisplayName ?? "Trendyol",
            customer.Name, customer.Email, customer.TaxOrIdentityNumber, customer.OrderType, customer.IsMicroExport,
            order.ShipmentAddressSnapshotJson, order.InvoiceAddressSnapshotJson, OperationalDueAt(order.CustomerSnapshotJson), InvoiceLabel(invoice, order.CustomerSnapshotJson),
            customer.Phone, customer.IsEInvoiceAvailable, InvoiceDocumentUrl(order.CustomerSnapshotJson)));
    }

    public async Task<ServiceResult<string>> ProductImageAsync(Guid tenantId, string? barcode, string correlationId, CancellationToken cancellationToken)
    {
        var normalizedBarcode = barcode?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedBarcode) || normalizedBarcode.Length > 128)
            return ServiceResult<string>.Fail("PRODUCT_BARCODE_INVALID", "Geçerli bir ürün barkodu gereklidir.", 400);

        var connection = await ActiveTrendyolConnection(tenantId, cancellationToken);
        if (connection is null) return NotFound<string>();

        var result = await productVisuals.FindByBarcodeAsync(
            new AdapterContext(tenantId, connection.Id, correlationId, $"order-product-image:{normalizedBarcode}", timeProvider.GetUtcNow().AddSeconds(20)),
            normalizedBarcode,
            cancellationToken);
        if (!result.IsSuccess)
            return ServiceResult<string>.Fail("LIVE_PRODUCT_IMAGE_READ_FAILED", result.Error?.SafeMessage ?? "Ürün görseli okunamadı.", result.Error?.HttpStatus is >= 400 and <= 599 ? result.Error.HttpStatus.Value : 502);

        var image = result.Value is null ? null : NormalizeImageUrl(SourceImageUrl(result.Value.RawJson));
        return image is not null ? ServiceResult<string>.Ok(image) : NotFound<string>();
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
        var stage = await IsStageConnection(tenantId, row.Package.ConnectionId, cancellationToken);
        var actions = stage
            ? ShipmentActions
            : await CapabilityValues(tenantId, row.Package.ConnectionId, MarketplaceCapabilities.ShipmentWrite, "allowedActions", cancellationToken);
        var formats = stage
            ? StageLabelFormats
            : await CapabilityValues(tenantId, row.Package.ConnectionId, MarketplaceCapabilities.LabelRead, "formats", cancellationToken);
        var documents = await db.ShipmentDocuments.AsNoTracking().Where(x => x.TenantId == tenantId && x.PackageId == id).OrderByDescending(x => x.DocumentVersion).Select(x => new ShipmentDocumentView(x.Id, x.DocumentKind, x.Format, x.Source, x.DocumentVersion, x.CreatedAt, x.ExpiresAt)).ToListAsync(cancellationToken);
        return ServiceResult<ShipmentDetailView>.Ok(new(Map(row.Package, row.OrderNumber), actions, formats, stage, documents));
    }

      public Task<ServiceResult<Guid>> EnqueueOrderSyncAsync(Guid tenantId, Guid connectionId, string? externalOrderId, bool full, string correlationId, CancellationToken cancellationToken) => EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.OrderRead, MarketplaceJobTypes.OrderSync, JsonSerializer.Serialize(new { connectionId, externalOrderId, full }), correlationId, cancellationToken);

      public Task<ServiceResult<Guid>> EnqueueProductSyncAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken) => EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.ProductRead, MarketplaceJobTypes.ProductSync, JsonSerializer.Serialize(new { connectionId }), correlationId, cancellationToken);

    public Task<ServiceResult<Guid>> EnqueueReferenceSyncAsync(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, string correlationId, CancellationToken cancellationToken)
    {
        var normalized = resourceType.Trim().ToUpperInvariant();
        var parent = string.IsNullOrWhiteSpace(parentExternalId) ? null : parentExternalId.Trim();
        var valid = normalized switch
        {
            "CATEGORIES" or "BRANDS" => parent is null,
            "CATEGORY_ATTRIBUTES" => parent is not null && !parent.Contains('/', StringComparison.Ordinal),
            "ATTRIBUTE_VALUES" => parent?.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length == 2,
            _ => false
        };
        return valid
            ? EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.ReferenceRead, MarketplaceJobTypes.ReferenceSync, JsonSerializer.Serialize(new { connectionId, resourceType = normalized, parentExternalId = parent }), correlationId, cancellationToken)
            : Task.FromResult(ServiceResult<Guid>.Fail("REFERENCE_RESOURCE_UNSUPPORTED", "CATEGORIES/BRANDS scope almaz; CATEGORY_ATTRIBUTES categoryId, ATTRIBUTE_VALUES categoryId/attributeId scope ister.", 422));
    }

    public async Task<ServiceResult<Guid>> EnqueueShipmentActionAsync(Guid tenantId, Guid packageId, long expectedVersion, ShipmentActionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken); if (package is null) return NotFound<Guid>(); if (package.Version != expectedVersion) return Precondition<Guid>(package.Version);
        var action = command.Action.Trim().ToUpperInvariant();
        var validation = ValidateShipmentAction(package, action, command.PayloadJson); if (validation is not null) return ServiceResult<Guid>.Fail(validation.Code, validation.Message, validation.Status, validation.FieldErrors);
        var stage = await IsStageConnection(tenantId, package.ConnectionId, cancellationToken);
        if (!stage && !await IsProductionConnection(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Shipment işlemi yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (!stage && !await WritesEnabled(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        var normalizedKey = idempotencyKey.Trim();
        var commandHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{action}\n{command.PayloadJson}")));
        var dedup = $"shipment-action:{package.Id}:v{package.Version}:{action}:{commandHash}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))}";
        var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.ShipmentAction && x.EffectIdempotencyKey == normalizedKey, cancellationToken); if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var jobId = Guid.CreateVersion7(); var payload = JsonSerializer.Serialize(new ShipmentActionJobPayload(jobId, package.Id, action, command.PayloadJson)); var job = NewJob(tenantId, package.ConnectionId, MarketplaceJobTypes.ShipmentAction, dedup, payload, correlationId); job.Id = jobId; job.EffectIdempotencyKey = normalizedKey; db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(jobId);
    }

    public async Task<ServiceResult<Guid>> EnqueueCommonLabelAsync(Guid tenantId, Guid packageId, long expectedVersion, int boxQuantity, decimal volumetricHeight, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken); if (package is null) return NotFound<Guid>(); if (package.Version != expectedVersion) return Precondition<Guid>(package.Version);
        if (string.IsNullOrWhiteSpace(package.CargoTrackingNumber)) return ServiceResult<Guid>.Fail("CARGO_TRACKING_REQUIRED", "Ortak etiket için kargo takip numarası gerekir.", 422);
        if (!CommonLabelCarrierPolicy.Supports(package.CargoProviderExternalId)) return ServiceResult<Guid>.Fail("COMMON_LABEL_CARRIER_UNSUPPORTED", "Ortak etiket yalnız Trendyol öder Aras Kargo veya TEX gönderilerinde kullanılabilir.", 422);
        if (boxQuantity is < 1 or > 50) return Invalid<Guid>("boxQuantity", "boxQuantity 1-50 arasında olmalıdır.");
        if (volumetricHeight < 0 || volumetricHeight > 10000) return Invalid<Guid>("volumetricHeight", "volumetricHeight 0-10000 arasında olmalıdır.");
        var stage = await IsStageConnection(tenantId, package.ConnectionId, cancellationToken);
        if (!stage && !await IsProductionConnection(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Ortak etiket yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (!stage && !await WritesEnabled(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        var normalizedKey = idempotencyKey.Trim(); var existingAttempt = await db.ShipmentDocumentAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == normalizedKey, cancellationToken);
        if (existingAttempt is not null)
        {
            var existingJob = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.CommonLabel && x.EffectIdempotencyKey == normalizedKey, cancellationToken);
            return existingJob is null ? ServiceResult<Guid>.Fail("LABEL_ATTEMPT_STATE_CONFLICT", "Etiket denemesi var ancak job kaydı bulunamadı.", 409) : ServiceResult<Guid>.Ok(existingJob.Id);
        }
        var now = timeProvider.GetUtcNow(); var jobId = Guid.CreateVersion7(); var payload = JsonSerializer.Serialize(new CommonLabelJobPayload(jobId, package.Id, "SUBMIT", boxQuantity, decimal.Round(volumetricHeight, 2, MidpointRounding.ToEven), now, now.AddMinutes(15)));
        var job = NewJob(tenantId, package.ConnectionId, MarketplaceJobTypes.CommonLabel, $"common-label:{package.Id}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))}", payload, correlationId); job.Id = jobId; job.EffectIdempotencyKey = normalizedKey; job.MaxAttempts = 30;
        db.ShipmentDocumentAttempts.Add(new ShipmentDocumentAttempt { Id = Guid.CreateVersion7(), TenantId = tenantId, PackageId = package.Id, IdempotencyKey = normalizedKey, Status = "PENDING", CreatedAt = now }); db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(jobId);
    }

    public async Task<ServiceResult<Guid>> EnqueueLabelCapabilityProbeAsync(Guid tenantId, Guid actorUserId, Guid packageId, long expectedVersion, string capabilityCode, int boxQuantity, decimal volumetricHeight, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
        if (package is null) return NotFound<Guid>();
        if (package.Version != expectedVersion) return Precondition<Guid>(package.Version);
        if (string.IsNullOrWhiteSpace(package.CargoTrackingNumber)) return ServiceResult<Guid>.Fail("CARGO_TRACKING_REQUIRED", "Stage etiket capability testi için takip numarası gerekir.", 422);
        var capability = capabilityCode.Trim().ToUpperInvariant();
        if (capability is not (MarketplaceCapabilities.LabelRead or MarketplaceCapabilities.LabelWrite)) return Invalid<Guid>("capabilityCode", "Bu Stage canary yalnız LABEL_READ veya LABEL_WRITE için kullanılabilir.");
        if (boxQuantity is < 1 or > 50 || volumetricHeight <= 0 || volumetricHeight > 10000) return Invalid<Guid>("probe", "Koli adedi 1-50, desi/hacim 0-10000 arasında olmalıdır.");
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == package.ConnectionId && x.PlatformCode == "TRENDYOL", cancellationToken);
        if (connection is null || !string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase)) return ServiceResult<Guid>.Fail("STAGE_CONNECTION_REQUIRED", "Capability canary yalnız Trendyol STAGE bağlantısında çalışır.", 422);
        if (capability == MarketplaceCapabilities.LabelWrite && package.Status.ToString() is not ("ReadyToShip" or "Processing")) return ServiceResult<Guid>.Fail("STAGE_LABEL_PACKAGE_NOT_READY", "LABEL_WRITE canary yalnız Picking/Processing veya ReadyToShip Stage paketi üzerinde çalışır.", 422);
        if (capability == MarketplaceCapabilities.LabelWrite && !CommonLabelCarrierPolicy.Supports(package.CargoProviderExternalId)) return ServiceResult<Guid>.Fail("COMMON_LABEL_CARRIER_UNSUPPORTED", "LABEL_WRITE canary yalnız Trendyol öder Aras Kargo veya TEX Stage paketi üzerinde çalışır.", 422);
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

    public async Task<ServiceResult<Guid>> EnqueueStageTestOrderAsync(Guid tenantId, Guid actorUserId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "TRENDYOL", cancellationToken);
        if (connection is null) return NotFound<Guid>();
        if (!string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase) || !string.Equals(connection.ExternalStoreId, "2738", StringComparison.Ordinal)) return ServiceResult<Guid>.Fail("STAGE_TEST_ORDER_SCOPE_REQUIRED", "Taze test siparişi yalnız Trendyol STAGE seller 2738 kapsamındadır.", 422);
        var normalizedKey = idempotencyKey.Trim();
        var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.StageTestOrder && x.EffectIdempotencyKey == normalizedKey, cancellationToken);
        if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var now = timeProvider.GetUtcNow(); var jobId = Guid.CreateVersion7(); var payload = JsonSerializer.Serialize(new StageTestOrderJobPayload(jobId, actorUserId, "9900000000486", now));
        var job = NewJob(tenantId, connectionId, MarketplaceJobTypes.StageTestOrder, $"stage-test-order:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))}", payload, correlationId);
        job.Id = jobId; job.EffectIdempotencyKey = normalizedKey; job.MaxAttempts = 1; db.IntegrationJobs.Add(job);
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = actorUserId, Action = "STAGE_TEST_ORDER_ENQUEUED", TargetType = "PlatformConnection", TargetId = connectionId.ToString("D"), Reason = "official-stage-test-order-fixture", CorrelationId = correlationId, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(jobId);
    }

    public async Task<PageResult<ReturnListView>> ReturnsAsync(Guid tenantId, int limit, string? after, string? status, bool latest, CancellationToken cancellationToken)
    {
        var hasOperationalTrendyol = await db.PlatformConnections.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId
                && x.PlatformCode == "TRENDYOL"
                && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken);
        if (!hasOperationalTrendyol) return new([], null, false);

        var afterId = latest ? Guid.Empty : Decode(after);
        var query = db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!latest && afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ReturnClaimStatus>(status, true, out var parsed)) query = query.Where(x => x.Status == parsed);
        var claims = latest
            ? await query.OrderByDescending(x => x.LastRemoteModifiedAt).ThenByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken)
            : await query.OrderBy(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var orderIds = claims.Select(x => x.OrderId).Distinct().ToArray();
        var claimIds = claims.Select(x => x.Id).ToArray();
        var orders = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var returnLines = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && claimIds.Contains(x.ClaimId)).ToListAsync(cancellationToken);
        var orderLineIds = returnLines.Select(x => x.OrderLineId).Distinct().ToArray();
        var orderLines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && orderLineIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var packages = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).OrderByDescending(x => x.StatusOccurredAt).ToListAsync(cancellationToken);
        var invoices = await db.Invoices.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId) && x.OriginalInvoiceId == null).OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
        var imageUrls = await MediaUrls(tenantId, orderLines.Values.Select(x => x.VariantId), cancellationToken);
        var rows = claims.Select(claim =>
        {
            var order = orders.GetValueOrDefault(claim.OrderId);
            var claimLines = returnLines.Where(x => x.ClaimId == claim.Id).ToList();
            var package = packages.FirstOrDefault(x => x.OrderId == claim.OrderId);
            var invoice = invoices.FirstOrDefault(x => x.OrderId == claim.OrderId);
            var firstLine = claimLines.Select(x => orderLines.GetValueOrDefault(x.OrderLineId)).FirstOrDefault(x => x is not null);
            var image = firstLine?.VariantId is { } variantId ? imageUrls.GetValueOrDefault(variantId) : null;
            var lineViews = claimLines.Select(returnLine =>
            {
                var line = orderLines.GetValueOrDefault(returnLine.OrderLineId);
                if (line is null) return null;
                var source = SourceLine(line.SourceSnapshotJson);
                return new OrderLineView(line.Id, line.Sku, line.Barcode, line.TitleSnapshot, returnLine.Quantity, line.CancelledQuantity, line.ShippedQuantity, line.DeliveredQuantity, line.ReturnedQuantity, line.UnitPrice, line.VatRate, line.RawStatus, line.VariantId, source.ModelCode, source.OptionSignature, source.ImageUrl ?? (line.VariantId is { } id ? imageUrls.GetValueOrDefault(id) : null));
            }).Where(line => line is not null).Select(line => line!).ToList();
            return new ReturnListView(claim.Id, claim.ExternalClaimId, order?.OrderNumber ?? "—", Wire(claim.Status), claim.RawStatus, claim.ReasonText, claim.ActionDueAt, claim.Version,
                order is null ? "—" : Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson).Name,
                order?.OrderedAt, order?.NetAmount ?? 0, order?.Currency ?? "TRY", package?.CargoProviderExternalId, package?.CargoTrackingNumber, image, claimLines.Count, firstLine?.Barcode,
                lineViews, package?.ExternalPackageId, order is null ? "FATURA_BEKLIYOR" : InvoiceLabel(invoice, order.CustomerSnapshotJson), order?.GrossAmount ?? 0, order?.DiscountAmount ?? 0,
                order is not null && Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson).IsMicroExport);
        }).ToList();
        return latest ? new(rows.Take(limit).ToList(), null, rows.Count > limit) : Page(rows, limit, x => x.Id);
    }

    public async Task<ServiceResult<ReturnDetailView>> ReturnAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var claim = await db.ReturnClaims.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (claim is null) return NotFound<ReturnDetailView>();
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claim.OrderId, cancellationToken);
        if (order is null) return NotFound<ReturnDetailView>();
        var actions = await IsStageConnection(tenantId, claim.ConnectionId, cancellationToken) && claim.Status == ReturnClaimStatus.ActionRequired
            ? ReturnActions
            : await CapabilityValues(tenantId, claim.ConnectionId, MarketplaceCapabilities.ReturnWrite, "allowedActions", cancellationToken);
        var sourceLines = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var orderLineIds = sourceLines.Select(x => x.OrderLineId).ToArray();
        var orderLines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && orderLineIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var dispositions = await db.ReturnStockDispositions.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == id).GroupBy(x => x.ReturnLineId).Select(x => new { ReturnLineId = x.Key, Quantity = x.Sum(y => y.Quantity) }).ToDictionaryAsync(x => x.ReturnLineId, x => x.Quantity, cancellationToken);
        var variantIds = orderLines.Values.Where(x => x.VariantId is not null).Select(x => x.VariantId!.Value).Distinct().ToArray();
        var inventoryVariants = await db.InventoryItems.AsNoTracking().Where(x => x.TenantId == tenantId && variantIds.Contains(x.VariantId) && x.LocationCode == "MAIN").Select(x => x.VariantId).ToListAsync(cancellationToken);
        var imageUrls = await MediaUrls(tenantId, variantIds.Select(x => (Guid?)x), cancellationToken);
        var lines = sourceLines.Select(line =>
        {
            var source = orderLines.GetValueOrDefault(line.OrderLineId);
            var disposed = dispositions.GetValueOrDefault(line.Id);
            return new ReturnLineView(line.Id, line.ExternalLineId, line.OrderLineId, source?.Sku ?? "—", source?.Barcode, source?.TitleSnapshot ?? "—", line.Quantity, disposed, Math.Max(0, line.Quantity - disposed), source?.UnitPrice ?? 0,
                source?.VariantId is { } variantId ? imageUrls.GetValueOrDefault(variantId) : null,
                source?.VariantId is { } mappedVariantId && inventoryVariants.Contains(mappedVariantId));
        }).ToList();
        var package = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).OrderByDescending(x => x.StatusOccurredAt).FirstOrDefaultAsync(cancellationToken);
        var customer = Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson);
        return ServiceResult<ReturnDetailView>.Ok(new(claim.Id, claim.ExternalClaimId, order.OrderNumber, Wire(claim.Status), claim.RawStatus, claim.ReasonCode, claim.ReasonText, claim.ActionDueAt, actions, claim.Version,
            customer.Name, order.OrderedAt, order.NetAmount, order.Currency, package?.CargoProviderExternalId, package?.CargoTrackingNumber, lines, claim.Status is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed));
    }

    public async Task<ServiceResult<IReadOnlyList<ReturnIssueReason>>> ReturnIssueReasonsAsync(Guid tenantId, Guid id, string correlationId, CancellationToken cancellationToken)
    {
        var claim = await db.ReturnClaims.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (claim is null) return NotFound<IReadOnlyList<ReturnIssueReason>>();
        var context = new AdapterContext(tenantId, claim.ConnectionId, correlationId, $"return-issue-reasons:{claim.ConnectionId:N}", timeProvider.GetUtcNow().AddSeconds(30));
        var result = await returns.IssueReasonsAsync(context, cancellationToken);
        return result.IsSuccess
            ? ServiceResult<IReadOnlyList<ReturnIssueReason>>.Ok(result.Value!)
            : ServiceResult<IReadOnlyList<ReturnIssueReason>>.Fail(result.Error!.Code, result.Error.SafeMessage, result.Error.HttpStatus ?? 502);
    }

    public Task<ServiceResult<Guid>> EnqueueReturnSyncAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken) => EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.ReturnRead, MarketplaceJobTypes.ReturnSync, JsonSerializer.Serialize(new { connectionId }), correlationId, cancellationToken);

    public async Task<ServiceResult<Guid>> EnqueueReturnActionAsync(Guid tenantId, Guid userId, Guid claimId, long expectedVersion, ReturnDecisionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var normalizedKey = idempotencyKey.Trim(); var prior = await db.ReturnDecisions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == normalizedKey, cancellationToken); if (prior is not null) return ServiceResult<Guid>.Ok(prior.Id);
        var claim = await db.ReturnClaims.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claimId, cancellationToken); if (claim is null) return NotFound<Guid>(); if (claim.Version != expectedVersion) return Precondition<Guid>(claim.Version);
        var action = command.Action.Trim().ToUpperInvariant(); if (action is not ("APPROVE" or "REJECT")) return Invalid<Guid>("action", "İade aksiyonu APPROVE veya REJECT olmalıdır.");
        if (claim.Status != ReturnClaimStatus.ActionRequired) return ServiceResult<Guid>.Fail("RETURN_ACTION_NOT_ALLOWED", "İade aksiyonu yalnız ACTION_REQUIRED durumunda oluşturulabilir.", 409);
        var activeDecision = await db.ReturnDecisions.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == claimId && (x.Status == "PENDING" || x.Status == "SUBMITTED" || x.Status == "RETRY_SCHEDULED" || x.Status == "MANUAL_REVIEW")).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (activeDecision is not null) return ServiceResult<Guid>.Fail("RETURN_DECISION_IN_PROGRESS", "Bu iade için tamamlanmamış bir karar zaten bulunuyor.", 409);
        if (action == "REJECT" && (string.IsNullOrWhiteSpace(command.ReasonCode) || string.IsNullOrWhiteSpace(command.Explanation) || command.Explanation.Trim().Length > 500)) return Invalid<Guid>("explanation", "REJECT için reasonCode ve en fazla 500 karakter açıklama gerekir.");
        var evidenceOptional = command.ReasonCode is "1651" or "451" or "2101";
        if (action == "REJECT" && !evidenceOptional && (command.EvidenceAssetIds is null || command.EvidenceAssetIds.Count == 0)) return Invalid<Guid>("evidenceAssetIds", "Seçilen ret nedeni için en az bir kanıt dosyası gerekir.");
        var stage = await IsStageConnection(tenantId, claim.ConnectionId, cancellationToken);
        if (!stage && !await IsProductionConnection(tenantId, claim.ConnectionId, cancellationToken)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "İade aksiyonu yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (!stage && !await WritesEnabled(tenantId, claim.ConnectionId, cancellationToken)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        var decision = new ReturnDecision { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, Action = action, ReasonCode = string.IsNullOrWhiteSpace(command.ReasonCode) ? null : command.ReasonCode.Trim(), Explanation = string.IsNullOrWhiteSpace(command.Explanation) ? null : command.Explanation.Trim(), IdempotencyKey = normalizedKey, Status = "PENDING", ActorUserId = userId, CreatedAt = timeProvider.GetUtcNow() }; db.ReturnDecisions.Add(decision);
        if (command.EvidenceAssetIds is not null) foreach (var assetId in command.EvidenceAssetIds.Distinct())
        {
            var asset = await db.FileAssets.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == assetId && x.ArchivedAt == null && x.Status == "ACTIVE", cancellationToken);
            if (asset is null) return ServiceResult<Guid>.Fail("EVIDENCE_NOT_FOUND", "İade kanıt dosyası tenant private storage içinde bulunamadı.", 422);
            if (asset.Classification != "RETURN_EVIDENCE" || asset.SizeBytes is <= 0 or > 10 * 1024 * 1024 || asset.MimeType is not ("application/pdf" or "image/jpeg" or "image/png")) return ServiceResult<Guid>.Fail("EVIDENCE_INVALID", "İade kanıtı PDF/JPEG/PNG ve en fazla 10 MiB olmalıdır.", 422);
            db.ReturnEvidence.Add(new ReturnEvidence { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, DecisionId = decision.Id, FileAssetId = asset.Id, EvidenceKind = asset.Classification, Checksum = asset.Sha256, CreatedAt = timeProvider.GetUtcNow() });
        }
        var job = NewJob(tenantId, claim.ConnectionId, MarketplaceJobTypes.ReturnAction, $"return-action:{normalizedKey}", JsonSerializer.Serialize(new { claimId, decisionId = decision.Id }), correlationId); db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(job.Id);
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

    private static readonly IReadOnlyList<string> ShipmentActions = ["PICKING", "INVOICED", "TRACKING_NUMBER", "CANCEL_ITEMS", "SPLIT", "MULTI_SPLIT", "CHANGE_CARGO_PROVIDER", "ALTERNATIVE_DELIVERY", "MANUAL_DELIVER", "MANUAL_RETURN"];
    private static readonly IReadOnlyList<string> StageLabelFormats = ["PDF"];
    private static readonly IReadOnlyList<string> ReturnActions = ["APPROVE", "REJECT"];

    private static ServiceError? ValidateShipmentAction(ShipmentPackage package, string action, string payloadJson)
    {
        var supported = new HashSet<string>(StringComparer.Ordinal) { "PICKING", "INVOICED", "TRACKING_NUMBER", "CANCEL_ITEMS", "SPLIT", "MULTI_SPLIT", "CHANGE_CARGO_PROVIDER", "ALTERNATIVE_DELIVERY", "MANUAL_DELIVER", "MANUAL_RETURN" };
        if (!supported.Contains(action)) return new("SHIPMENT_ACTION_UNSUPPORTED", "Paket aksiyonu tanınmıyor.", 422);
        if (action == "PICKING" && package.Status is not (ShipmentPackageStatus.New or ShipmentPackageStatus.OnHold)) return new("SHIPMENT_STATE_CONFLICT", "PICKING yalnız NEW/ON_HOLD paket için gönderilebilir.", 409);
        if (action == "INVOICED" && package.Status != ShipmentPackageStatus.Processing) return new("SHIPMENT_STATE_CONFLICT", "INVOICED yalnız PROCESSING paket için gönderilebilir.", 409);
        if (action == "TRACKING_NUMBER" && package.Status is not (ShipmentPackageStatus.Processing or ShipmentPackageStatus.ReadyToShip)) return new("SHIPMENT_STATE_CONFLICT", "TRACKING_NUMBER yalnız PICKING/INVOICED paket için gönderilebilir.", 409);
        if ((action is "CANCEL_ITEMS" or "SPLIT" or "MULTI_SPLIT" or "CHANGE_CARGO_PROVIDER") && package.Status is (ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.Returned or ShipmentPackageStatus.Cancelled)) return new("SHIPMENT_STATE_CONFLICT", "Bu aksiyon sevk edilmiş veya terminal paket için kullanılamaz.", 409);
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
                "CANCEL_ITEMS" => NonEmptyArray(root, "lines"),
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

    private async Task<ServiceResult<Guid>> EnqueueRead(Guid tenantId, Guid connectionId, string capability, string type, string payload, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "TRENDYOL" && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken); if (connection is null) return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Aktif veya doğrulanmış Trendyol bağlantısı gerekir.", 422); if (!IntegrationRuntimePolicy.AllowsManualRead(connection)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Read işlemi yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422); return await Enqueue(tenantId, connectionId, type, $"{type.ToLowerInvariant()}:{connectionId}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))}", payload, correlationId, cancellationToken);
    }
    private async Task<ServiceResult<Guid>> Enqueue(Guid tenantId, Guid connectionId, string type, string dedup, string payload, string correlationId, CancellationToken cancellationToken)
    {
        var recurringRead = type is MarketplaceJobTypes.ReferenceSync or MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ReturnSync;
        var active = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == type && (recurringRead ? x.JobDedupKey.StartsWith(dedup) : x.JobDedupKey == dedup) && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled), cancellationToken);
        if (active is not null) return ServiceResult<Guid>.Ok(active.Id);

        var job = NewJob(tenantId, connectionId, type, recurringRead ? $"{dedup}:{timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}" : dedup, payload, correlationId);
        db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(job.Id);
    }
    private IntegrationJob NewJob(Guid tenantId, Guid connectionId, string type, string dedup, string payload, string correlationId) => new() { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, JobType = type, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), JobDedupKey = dedup, EffectIdempotencyKey = dedup, AvailableAt = timeProvider.GetUtcNow(), CorrelationId = correlationId, Version = 1 };
    private Task<bool> Supported(Guid tenantId, Guid connectionId, string code, CancellationToken cancellationToken) => db.PlatformCapabilities.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == code && x.SupportLevel == CapabilitySupportLevel.Supported, cancellationToken);
    private Task<bool> IsStageConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.Status == "ACTIVE" && x.Environment == "STAGE", cancellationToken);
    private Task<bool> IsProductionConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.Status == "ACTIVE" && x.Environment == "PRODUCTION", cancellationToken);
    private async Task<bool> WritesEnabled(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) { if (!configuration.GetValue<bool>("FeatureFlags:ExternalWrites")) return false; var settings = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.SettingsJson).SingleOrDefaultAsync(cancellationToken); if (settings is null) return false; try { return JsonDocument.Parse(settings).RootElement.TryGetProperty("ExternalWritesEnabled", out var value) && value.ValueKind == JsonValueKind.True; } catch (JsonException) { return false; } }
    private async Task<IReadOnlyList<string>> CapabilityValues(Guid tenantId, Guid connectionId, string code, string property, CancellationToken cancellationToken) { var capability = await db.PlatformCapabilities.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == code && x.SupportLevel == CapabilitySupportLevel.Supported, cancellationToken); if (capability?.ConstraintsJson is null) return []; try { using var doc = JsonDocument.Parse(capability.ConstraintsJson); return doc.RootElement.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : []; } catch (JsonException) { return []; } }
    private static ProductVariant? ResolveVariant(OrderLine line, IReadOnlyDictionary<Guid, ProductVariant> variants, IReadOnlyDictionary<string, ProductVariant> variantsBySku, IReadOnlyDictionary<string, ProductVariant> variantsByBarcode) =>
        line.VariantId is { } variantId ? variants.GetValueOrDefault(variantId) :
        variantsBySku.GetValueOrDefault(line.Sku) ?? (!string.IsNullOrWhiteSpace(line.Barcode) ? variantsByBarcode.GetValueOrDefault(line.Barcode) : null);

    private async Task<Dictionary<Guid, string>> MediaUrls(Guid tenantId, IEnumerable<Guid?> variantIds, CancellationToken cancellationToken)
    {
        var ids = variantIds.Where(x => x is not null).Select(x => x!.Value).Distinct().ToArray();
        if (ids.Length == 0) return [];
        var rows = await (from variant in db.ProductVariants.AsNoTracking()
                          join media in db.ProductMedia.AsNoTracking() on new { variant.TenantId, variant.ProductId } equals new { media.TenantId, media.ProductId }
                          join asset in db.FileAssets.AsNoTracking() on new { media.TenantId, media.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                          where variant.TenantId == tenantId && ids.Contains(variant.Id) && (media.VariantId == null || media.VariantId == variant.Id) && media.Status == "ACTIVE" && asset.Status == "ACTIVE" && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                          orderby media.VariantId == variant.Id ? 0 : 1, media.SortOrder
                          select new { VariantId = variant.Id, asset.Id, asset.Classification, Url = asset.RelativePath }).ToListAsync(cancellationToken);
        return rows.GroupBy(x => x.VariantId).ToDictionary(x => x.Key, x => x.First().Classification == "PRODUCT_MEDIA_URL" ? x.First().Url : $"/api/v1/files/product-media/{x.First().Id:D}/content");
    }

    private static (string Name, string? Email, string? Phone, string? TaxOrIdentityNumber, string OrderType, bool IsMicroExport, bool? IsEInvoiceAvailable) Customer(string customerJson, string invoiceAddressJson, string shipmentAddressJson)
    {
        var name = ResolveCustomerName(customerJson, invoiceAddressJson, shipmentAddressJson);
        var email = JsonText(customerJson, "customerEmail", "email") ?? JsonText(invoiceAddressJson, "email") ?? JsonText(shipmentAddressJson, "email");
        var phone = JsonText(customerJson, "customerPhone", "customerPhoneNumber", "phone", "phoneNumber") ?? JsonText(invoiceAddressJson, "phone", "phoneNumber", "mobilePhone") ?? JsonText(shipmentAddressJson, "phone", "phoneNumber", "mobilePhone");
        var tax = ResolveCustomerTaxOrIdentityNumber(customerJson, invoiceAddressJson, shipmentAddressJson);
        var microText = JsonText(customerJson, "shipmentPackageType", "orderType");
        // Trendyol exports through its 3P partner model explicitly return micro=false. The documented
        // 3pByTrendyol=true signal is still an export order and must be presented as such to operators.
        // Historical Stage snapshots may predate the export flags while retaining Trendyol's
        // documented PM3/Arvato export-partner identity. Keep that narrow legacy signal so
        // existing export orders are not presented as domestic orders.
        var legacyExportPartner = IsLegacyTrendyolExportPartner(name);
        var micro = JsonBool(customerJson, "micro", "microExport", "3pByTrendyol") || microText?.Contains("MICRO", StringComparison.OrdinalIgnoreCase) == true || microText?.Contains("İHRAC", StringComparison.OrdinalIgnoreCase) == true || legacyExportPartner;
        var commercial = JsonBool(customerJson, "commercial") || !string.IsNullOrWhiteSpace(JsonText(invoiceAddressJson, "company", "companyName", "taxOffice"));
        var eInvoice = JsonNullableBool(customerJson, "eInvoiceAvailable", "isEInvoice") ?? JsonNullableBool(invoiceAddressJson, "eInvoiceAvailable", "isEInvoice");
        return (name, email, phone, tax, micro ? "MIKRO_IHRACAT" : commercial ? "KURUMSAL" : "BIREYSEL", micro, eInvoice);
    }

    internal static string ResolveCustomerName(string customerJson, string invoiceAddressJson, string shipmentAddressJson)
    {
        var customerName = NameFrom(customerJson, "customerFirstName", "customerLastName", "customerName", "customerFullName", "buyerName", "fullName", "name");
        var invoiceName = NameFrom(invoiceAddressJson, "firstName", "lastName", "invoiceFirstName", "invoiceLastName", "fullName", "name", "company", "companyName");
        var shipmentName = NameFrom(shipmentAddressJson, "firstName", "lastName", "shippingFirstName", "shippingLastName", "fullName", "name", "company", "companyName");
        return FirstMeaningful(customerName, invoiceName, shipmentName) ?? "—";
    }

    internal static string? ResolveCustomerTaxOrIdentityNumber(string customerJson, string invoiceAddressJson, string shipmentAddressJson) =>
        JsonText(customerJson, "customerTaxNumber", "taxNumber", "identityNumber", "customerIdentityNumber", "tcIdentityNumber")
        ?? JsonText(invoiceAddressJson, "taxNumber", "identityNumber", "tcIdentityNumber")
        ?? JsonText(shipmentAddressJson, "taxNumber", "identityNumber", "tcIdentityNumber");

    private static string? NameFrom(string json, string firstName, string lastName, params string[] fullNameFields)
    {
        var first = JsonText(json, firstName);
        var last = JsonText(json, lastName);
        var combined = string.Join(' ', new[] { first, last }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return !string.IsNullOrWhiteSpace(combined) ? combined : JsonText(json, fullNameFields);
    }

    private static string? FirstMeaningful(params string?[] names) => names.FirstOrDefault(name => IsMeaningfulText(name) && !IsPlaceholderCustomerName(name!));

    private static bool IsPlaceholderCustomerName(string name) => name.Trim() is "Adı Soyadı" or "Ad Soyad" or "İsim Soyisim";

    internal static bool IsLegacyTrendyolExportPartner(string name) =>
        name.Contains("PM3", StringComparison.OrdinalIgnoreCase) && name.Contains("ARVATO", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? OperationalDueAt(string json) =>
        JsonInstant(json, "agreedDeliveryDate", "estimatedDeliveryEndDate", "lastDeliveryDate", "deliveryDate", "estimatedDeliveryStartDate", "packageLastModifiedDate", "packageDeliveryDate", "packageEstimatedDeliveryDate", "dueDate", "shipmentDueDate", "deliveryDueAt");

    private static string InvoiceLabel(Invoice? invoice, string customerJson)
    {
        // Once a local invoice exists, its state is authoritative. The Trendyol
        // order snapshot keeps invoiceStatus at NOTINVOICED and is not refreshed
        // by the E-Faturam write, which otherwise makes a successfully created
        // invoice appear as still waiting in the orders screen.
        if (invoice is not null)
        {
            if (!string.IsNullOrWhiteSpace(invoice.InvoiceNumber) || invoice.Status is InvoiceStatus.Submitted or InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending or InvoiceStatus.Completed)
                return "FATURA_KESILDI";
            if (invoice.Status is InvoiceStatus.Cancelled or InvoiceStatus.CancelledLocal) return "FATURA_IPTAL";
            if (invoice.Status is InvoiceStatus.Rejected or InvoiceStatus.ValidationFailed or InvoiceStatus.ManualReview) return "FATURA_REDDEDILDI";
            return "FATURA_ISLENIYOR";
        }
        var remote = JsonText(customerJson, "invoiceStatus")?.Trim().ToUpperInvariant();
        if (remote is "INVOICED") return "FATURA_KESILDI";
        if (remote is "RECEIVED") return "FATURA_KONTROLDE";
        if (remote is "REJECTED") return "FATURA_REDDEDILDI";
        if (remote is "NOTINVOICED") return "FATURA_BEKLIYOR";
        return "FATURA_BEKLIYOR";
    }

    private static string? InvoiceDocumentUrl(string customerJson)
    {
        var value = JsonText(customerJson, "invoiceLink");
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
    }

    private static SourceLineView SourceLine(string? json)
    {
        var snapshot = json ?? "{}";
        var image = NormalizeImageUrl(SourceImageUrl(snapshot));
        var color = JsonText(snapshot, "productColor", "color", "colorName");
        var size = JsonText(snapshot, "productSize", "size", "sizeName");
        var options = new List<string>();
        if (!string.IsNullOrWhiteSpace(color)) options.Add($"Renk: {color}");
        if (!string.IsNullOrWhiteSpace(size)) options.Add($"Beden: {size}");
        return new(image, JsonText(snapshot, "productCode", "modelCode"), options.Count == 0 ? null : string.Join(" | ", options));
    }

    private static string? SourceImageUrl(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return FindImageUrl(document.RootElement) ?? JsonText(json, "productImageUrl", "imageUrl", "productImage", "image");
        }
        catch (JsonException) { return null; }
    }

    private static string? NormalizeImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        if (candidate.StartsWith("//", StringComparison.Ordinal)) candidate = "https:" + candidate;
        else if (candidate.StartsWith("/", StringComparison.Ordinal)) candidate = "https://cdn.dsmcdn.com" + candidate;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
    }

    private static string? FindImageUrl(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("images", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Array)
                    foreach (var image in property.Value.EnumerateArray())
                    {
                        var url = image.ValueKind == JsonValueKind.Object && image.TryGetProperty("url", out var value) ? value.ToString() : image.ToString();
                        if (!string.IsNullOrWhiteSpace(url)) return url;
                    }
                var nested = FindImageUrl(property.Value); if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) { var nested = FindImageUrl(item); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        return null;
    }

    private sealed record SourceLineView(string? ImageUrl, string? ModelCode, string? OptionSignature);

    private static string? JsonText(string json, params string[] names)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return FindText(document.RootElement, new HashSet<string>(names, StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException) { return null; }
    }

    private static string? FindText(JsonElement element, HashSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Contains(property.Name) && property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array))
                {
                    var value = property.Value.ToString();
                    if (IsMeaningfulText(value)) return value;
                }
                var nested = FindText(property.Value, names); if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) { var nested = FindText(item, names); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        return null;
    }

    private static bool IsMeaningfulText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Any(char.IsLetterOrDigit);

    private static bool JsonBool(string json, params string[] names)
    {
        var value = JsonText(json, names);
        return bool.TryParse(value, out var parsed) && parsed || value is "1" or "YES" or "EVET";
    }

    private static bool? JsonNullableBool(string json, params string[] names)
    {
        var value = JsonText(json, names);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (bool.TryParse(value, out var parsed)) return parsed;
        return value is "1" or "YES" or "EVET" ? true : value is "0" or "NO" or "HAYIR" ? false : null;
    }

    private static DateTimeOffset? JsonInstant(string json, params string[] names)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var namesSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            return FindInstant(document.RootElement, namesSet);
        }
        catch (JsonException) { return null; }
    }

    private static DateTimeOffset? FindInstant(JsonElement element, HashSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Contains(property.Name))
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var num) && num > 0)
                    {
                        try { return DateTimeOffset.FromUnixTimeMilliseconds(num); } catch (ArgumentOutOfRangeException) { }
                    }
                    else if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var str = property.Value.GetString();
                        if (long.TryParse(str, out var numStr) && numStr > 0)
                        {
                            try { return DateTimeOffset.FromUnixTimeMilliseconds(numStr); } catch (ArgumentOutOfRangeException) { }
                        }
                        if (DateTimeOffset.TryParse(str, out var parsed)) return parsed;
                    }
                }
                var nested = FindInstant(property.Value, names);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindInstant(item, names);
                if (nested is not null) return nested;
            }
        }
        return null;
    }
    private async Task<PlatformConnection?> ActiveTrendyolConnection(Guid tenantId, CancellationToken cancellationToken) =>
        await (from connection in db.PlatformConnections.AsNoTracking()
               where connection.TenantId == tenantId && connection.PlatformCode == "TRENDYOL" && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")
                   && db.PlatformCredentials.Any(credential => credential.TenantId == tenantId && credential.ConnectionId == connection.Id && credential.RevokedAt == null)
               orderby connection.Id
               select connection).FirstOrDefaultAsync(cancellationToken);

    private Guid Decode(string? cursor) => cursors.TryDecode(cursor, out var id) ? id : throw new ArgumentException("Cursor geçersiz veya süresi dolmuş.", nameof(cursor));
    private PageResult<T> Page<T>(List<T> rows, int limit, Func<T, Guid> id) { var hasMore = rows.Count > limit; var items = rows.Take(limit).ToList(); return new(items, hasMore ? cursors.Encode(id(items[^1])) : null, hasMore); }
    private static ShipmentView Map(ShipmentPackage x, string orderNumber) => new(x.Id, x.OrderId, orderNumber, x.ExternalPackageId, Wire(x.Status), x.RawStatus, x.CargoTrackingNumber, x.StatusOccurredAt, x.Version, x.CargoProviderExternalId);
    private static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();
    private static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Precondition<T>(long version) => ServiceResult<T>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{version}.", 412);
}
