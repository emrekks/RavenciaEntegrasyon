using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class InvoicingBillingService(
    AppDbContext db,
    CursorCodec cursors,
    IDataProtectionProvider dataProtection,
    IPrivateFileStorage files,
    IConfiguration configuration,
    TimeProvider timeProvider,
    IInvoiceProviderPort invoiceProvider) : IInvoicingBillingService
{
    private const int WorkspaceCandidateScanBatchSize = 100_000;
    private static readonly CultureInfo WorkspaceSearchCulture = CultureInfo.GetCultureInfo("tr-TR");
    private readonly IDataProtector _taxProtector = dataProtection.CreateProtector("MarketplaceHub.InvoiceTaxIdentity.v1");
    private readonly IDataProtector _partyProtector = dataProtection.CreateProtector("MarketplaceHub.InvoicePartySnapshot.v1");

    public async Task<ServiceResult<InvoicePolicyView>> GetPolicyAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var policy = await db.InvoicePolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProviderConnectionId == connectionId, cancellationToken);
        return policy is null ? NotFound<InvoicePolicyView>() : ServiceResult<InvoicePolicyView>.Ok(Map(policy));
    }


    public async Task<ServiceResult<InvoicePolicyView>> UpsertPolicyAsync(Guid tenantId, Guid connectionId, long? expectedVersion, UpsertInvoicePolicyCommand command, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "TRENDYOL_EFATURAM", cancellationToken);
        if (connection is null) return Invalid<InvoicePolicyView>("connectionId", "Trendyol E-Faturam provider bağlantısı bulunamadı.");
        if (new[] { command.TriggerState, command.PackageScope, command.DueRule, command.RoundingRule, command.AdjustmentRule }.Any(string.IsNullOrWhiteSpace)) return Invalid<InvoicePolicyView>("policy", "Policy alanları boş olamaz.");
        var policyValues = new[] { Normalize(command.TriggerState), Normalize(command.PackageScope), Normalize(command.DueRule), Normalize(command.RoundingRule), Normalize(command.AdjustmentRule) };
        var approvedManualPolicy = policyValues.SequenceEqual(["MANUAL_CONFIRMED", "SHIPMENT_PACKAGE", "IMMEDIATE", "LINE_HALF_AWAY_FROM_ZERO", "REJECT_OVER_ONE_KURUS"]);
        if (!approvedManualPolicy && policyValues.Any(x => x != "UNAPPROVED")) return ServiceResult<InvoicePolicyView>.Fail("FISCAL_POLICY_DECISION_REQUIRED", "Yalnız doğrulanmış manuel paket faturası politikası veya tüm alanlarda UNAPPROVED kabul edilir.", 422);
        if (command.AutoSubmit) return ServiceResult<InvoicePolicyView>.Fail("AUTO_INVOICE_DISABLED", "Onaylı mali karar kaydı olmadan otomatik fatura açılamaz.", 422);

        var now = timeProvider.GetUtcNow(); var policy = await db.InvoicePolicies.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProviderConnectionId == connectionId, cancellationToken);
        if (policy is null)
        {
            if (expectedVersion is not null) return NotFound<InvoicePolicyView>();
            policy = new InvoicePolicy { Id = Guid.CreateVersion7(), TenantId = tenantId, ProviderConnectionId = connectionId, TriggerState = Normalize(command.TriggerState), PackageScope = Normalize(command.PackageScope), DueRule = Normalize(command.DueRule), RoundingRule = Normalize(command.RoundingRule), AdjustmentRule = Normalize(command.AdjustmentRule), AutoSubmit = command.AutoSubmit, CreatedAt = now, UpdatedAt = now, Version = 1 };
            db.InvoicePolicies.Add(policy);
        }
        else
        {
            if (expectedVersion is null) return PreconditionRequired<InvoicePolicyView>();
            if (policy.Version != expectedVersion) return Precondition<InvoicePolicyView>(policy.Version);
            policy.TriggerState = Normalize(command.TriggerState); policy.PackageScope = Normalize(command.PackageScope); policy.DueRule = Normalize(command.DueRule); policy.RoundingRule = Normalize(command.RoundingRule); policy.AdjustmentRule = Normalize(command.AdjustmentRule); policy.AutoSubmit = command.AutoSubmit; policy.UpdatedAt = now; policy.Version++;
        }
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<InvoicePolicyView>.Ok(Map(policy));
    }

    public async Task<PageResult<InvoiceListView>> ListAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken)
    {
        var afterId = Decode(after); var query = db.Invoices.AsNoTracking().Where(x => x.TenantId == tenantId
            && db.Orders.Any(order => order.TenantId == tenantId && order.Id == x.OrderId
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == order.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")));
        if (afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<InvoiceStatus>(status, true, out var parsed)) query = query.Where(x => x.Status == parsed);
        var rows = await query.OrderBy(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken); var orderIds = rows.Select(x => x.OrderId).Distinct().ToList();
        var numbers = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.OrderNumber, cancellationToken);
        var packageIds = rows.Where(x => x.PackageId.HasValue).Select(x => x.PackageId!.Value).Distinct().ToArray();
        var marketplaceNumbers = packageIds.Length == 0
            ? new Dictionary<Guid, string?>()
            : await db.ShipmentPackages.AsNoTracking()
                .Where(x => x.TenantId == tenantId && packageIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.MarketplaceInvoiceNumber, cancellationToken);
        var hasMore = rows.Count > limit; var items = rows.Take(limit).Select(x => new InvoiceListView(x.Id, numbers.GetValueOrDefault(x.OrderId, "—"), x.InvoiceType, Status(x.Status), x.Currency, x.PayableTotal, ResolveInvoiceNumber(x.InvoiceNumber, x.PackageId is { } packageId ? marketplaceNumbers.GetValueOrDefault(packageId) : null), x.DueAt, x.CreatedAt, x.Version)).ToList();
        return new(items, hasMore ? cursors.Encode(rows[limit - 1].Id) : null, hasMore);
    }

    public async Task<IReadOnlyList<InvoiceWorkspaceItemView>> WorkspaceAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var hasOperationalMarketplace = await db.PlatformConnections.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId
                && InvoiceWorkspaceMarketplacePolicy.PlatformCodes.Contains(x.PlatformCode)
                && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken);
        if (!hasOperationalMarketplace) return [];

        var packages = await db.ShipmentPackages.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))
            .OrderByDescending(x => x.StatusOccurredAt)
            .ToListAsync(cancellationToken);
        if (packages.Count == 0) return [];

        var packageConnectionIds = packages.Select(package => package.ConnectionId).Distinct().ToArray();
        var packagePlatforms = await db.PlatformConnections.AsNoTracking()
            .Where(connection => connection.TenantId == tenantId && packageConnectionIds.Contains(connection.Id))
            .ToDictionaryAsync(connection => connection.Id, connection => connection.PlatformCode, cancellationToken);
        var ordersWithRealTrendyolPackage = packages
            .Where(package => string.Equals(packagePlatforms.GetValueOrDefault(package.ConnectionId), "TRENDYOL", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(package.ExternalPackageId)
                && !string.Equals(package.ExternalPackageId, "0", StringComparison.Ordinal))
            .Select(package => package.OrderId)
            .ToHashSet();
        packages = packages.Where(package => InvoiceWorkspacePackagePolicy.ShouldIncludeTrendyolPaymentPlaceholder(
            packagePlatforms.GetValueOrDefault(package.ConnectionId),
            package.ExternalPackageId,
            ordersWithRealTrendyolPackage.Contains(package.OrderId))).ToList();
        if (packages.Count == 0) return [];

        var statusFeedPackageIds = packages
            .Where(package => string.Equals(package.CreatedBy, "HEPSIBURADA_STATUS_FEED", StringComparison.Ordinal))
            .Select(package => package.Id)
            .ToArray();
        var allocatedStatusFeedPackageIds = statusFeedPackageIds.Length == 0
            ? new HashSet<Guid>()
            : (await db.PackageLineAllocations.AsNoTracking()
                .Where(allocation => allocation.TenantId == tenantId && statusFeedPackageIds.Contains(allocation.PackageId))
                .Select(allocation => allocation.PackageId)
                .Distinct()
                .ToListAsync(cancellationToken))
                .ToHashSet();
        var packageCountsByOrder = packages
            .GroupBy(package => package.OrderId)
            .ToDictionary(group => group.Key, group => group.Count());
        packages = packages
            .Where(package => InvoiceWorkspacePackagePolicy.ShouldInclude(
                package.CreatedBy,
                allocatedStatusFeedPackageIds.Contains(package.Id),
                packageCountsByOrder.GetValueOrDefault(package.OrderId)))
            .ToList();
        if (packages.Count == 0) return [];

        var orderIds = packages.Select(x => x.OrderId).Distinct().ToArray();
        var packageIds = packages.Select(x => x.Id).ToArray();
        var orders = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var connectionIds = orders.Values.Select(x => x.ConnectionId).Distinct().ToArray();
        var connections = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && connectionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var lines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).ToListAsync(cancellationToken);
        var invoices = await db.Invoices.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OriginalInvoiceId == null
                && x.SequencePurpose == "SALE"
                && ((x.PackageId != null && packageIds.Contains(x.PackageId.Value)) || (x.PackageId == null && orderIds.Contains(x.OrderId)))
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        var invoiceIds = invoices.Select(x => x.Id).ToArray();
        var invoiceDocumentIds = (await db.InvoiceDocuments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && invoiceIds.Contains(x.InvoiceId))
            .Select(x => x.InvoiceId)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();
        var deliveryStates = await db.MarketplaceDeliveryStates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && invoiceIds.Contains(x.InvoiceId))
            .Select(x => new { x.InvoiceId, x.PackageId, x.Status, x.ExternalReference, x.UpdatedAt })
            .ToListAsync(cancellationToken);
        var deliveryAttempts = await db.MarketplaceDeliveries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && invoiceIds.Contains(x.InvoiceId))
            .Select(x => new { x.InvoiceId, x.PackageId, x.Status, x.ExternalReference, x.AttemptNumber })
            .ToListAsync(cancellationToken);
        var invoiceReadIssues = await db.OperationalIssues.AsNoTracking()
            .Where(issue => issue.TenantId == tenantId
                && issue.Status == IssueStatus.Open
                && (issue.DedupeKey.StartsWith("trendyol-invoice-package-read:")
                    || issue.DedupeKey.StartsWith("order-invoice-reconciliation:")))
            .Select(issue => new { issue.DedupeKey, issue.Code, issue.Summary })
            .ToDictionaryAsync(issue => issue.DedupeKey, cancellationToken);

        var variantIds = lines.Where(x => x.VariantId != null).Select(x => x.VariantId!.Value).Distinct().ToArray();
        var lineSkus = lines.Select(x => x.Sku).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var lineBarcodes = lines.Select(x => x.Barcode).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var variantRows = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (variantIds.Contains(x.Id) || lineSkus.Contains(x.Sku) || (x.Barcode != null && lineBarcodes.Contains(x.Barcode))))
            .Select(x => new { x.Id, x.ProductId, x.Sku, x.Barcode })
            .ToListAsync(cancellationToken);
        var variantProductIds = variantRows.ToDictionary(x => x.Id, x => x.ProductId);
        var variantBySku = variantRows.Where(x => !string.IsNullOrWhiteSpace(x.Sku)).GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First().Id, StringComparer.OrdinalIgnoreCase);
        var variantByBarcode = variantRows.Where(x => !string.IsNullOrWhiteSpace(x.Barcode)).GroupBy(x => x.Barcode!, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First().Id, StringComparer.OrdinalIgnoreCase);
        Guid? ResolveVariantId(OrderLine line) => line.VariantId ?? (line.Barcode is not null && variantByBarcode.TryGetValue(line.Barcode, out var barcodeVariantId) ? barcodeVariantId : variantBySku.GetValueOrDefault(line.Sku));
        var resolvedVariantIds = lines.Select(ResolveVariantId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        foreach (var row in variantRows) variantProductIds.TryAdd(row.Id, row.ProductId);
        variantIds = resolvedVariantIds;
        var productIds = variantProductIds.Values.Distinct().ToArray();
        var mediaRows = await (from media in db.ProductMedia.AsNoTracking()
                               join asset in db.FileAssets.AsNoTracking() on new { media.TenantId, media.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                               where media.TenantId == tenantId && ((media.VariantId != null && variantIds.Contains(media.VariantId.Value)) || (media.VariantId == null && productIds.Contains(media.ProductId))) && media.Status == "ACTIVE" && asset.Status == "ACTIVE" && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                               orderby media.SortOrder
                               select new { media.VariantId, media.ProductId, asset.Id, asset.Classification, asset.RelativePath }).ToListAsync(cancellationToken);
        var mediaByVariant = mediaRows.Where(x => x.VariantId.HasValue).GroupBy(x => x.VariantId!.Value).ToDictionary(x => x.Key, x => MediaUrl(x.First().Id, x.First().Classification, x.First().RelativePath));
        var mediaByProduct = mediaRows.Where(x => x.VariantId == null).GroupBy(x => x.ProductId).ToDictionary(x => x.Key, x => MediaUrl(x.First().Id, x.First().Classification, x.First().RelativePath));
        var linesByOrder = lines.GroupBy(x => x.OrderId).ToDictionary(x => x.Key, x => x.ToList());
        var now = timeProvider.GetUtcNow();

        return packages.Select(package =>
        {
            if (!orders.TryGetValue(package.OrderId, out var order)) return null;
            var connection = connections.GetValueOrDefault(order.ConnectionId);
            if (connection?.PlatformCode == "SHOPIFY" && package.ExternalPackageId.StartsWith("order:", StringComparison.OrdinalIgnoreCase) && package.ExternalPackageId.EndsWith(":remainder", StringComparison.OrdinalIgnoreCase)) return null;
            var orderLines = (linesByOrder.GetValueOrDefault(order.Id) ?? [])
                .Where(line => OrderLinePresentationPolicy.HasActiveQuantity(line.OrderedQuantity, line.CancelledQuantity))
                .ToList();
            var invoice = invoices.FirstOrDefault(x => x.PackageId == package.Id) ?? invoices.FirstOrDefault(x => x.PackageId == null && x.OrderId == order.Id);
            var currentDeliveryState = invoice is null ? null : deliveryStates
                .Where(state => state.InvoiceId == invoice.Id && state.PackageId == package.Id).OrderByDescending(state => state.UpdatedAt).FirstOrDefault();
            var hasConfirmedHistoricalAttempt = invoice is not null
                && deliveryAttempts.Any(attempt => attempt.InvoiceId == invoice.Id && attempt.PackageId == package.Id && attempt.Status == "CONFIRMED");
            var deliveryConfirmed = MarketplaceInvoiceDeliveryEvidencePolicy.HasConfirmedDelivery(currentDeliveryState?.Status, hasConfirmedHistoricalAttempt);
            var invoiceStatus = package.ManualInvoiceStatus?.Trim().ToUpperInvariant() switch
            {
                "UPLOADED" => "FATURA_YUKLENDI",
                "PENDING" => "FATURA_BEKLIYOR",
                _ => MarketplaceSalesService.InvoiceLabelForPlatform(invoice, package.MarketplaceInvoiceStatus, order.CustomerSnapshotJson, [package.RawStatus], connection?.PlatformCode, deliveryConfirmed)
            };
            if (!DashboardMetricPolicy.IsInvoiceEligiblePackage(package.Status)
                || !DashboardMetricPolicy.IsInvoiceEligibleOrder(order.DerivedStatus)) return null;
            var deliveredAt = package.Status == ShipmentPackageStatus.Delivered ? package.StatusOccurredAt : (DateTimeOffset?)null;
            var dueAt = deliveredAt?.AddDays(7);
            var dueSoon = invoiceStatus == "FATURA_BEKLIYOR" && deliveredAt is not null && now >= deliveredAt.Value.AddDays(5);
            var image = orderLines.Select(line => ResolveVariantId(line) is { } variantId ? mediaByVariant.GetValueOrDefault(variantId) ?? mediaByProduct.GetValueOrDefault(variantProductIds.GetValueOrDefault(variantId)) : null).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            var customerName = InvoiceWorkspaceCustomerName(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson);
            var workspaceLines = orderLines.Select(line => new InvoiceWorkspaceLineView(line.Sku, line.Barcode, line.TitleSnapshot, OrderLinePresentationPolicy.ActiveQuantity(line.OrderedQuantity, line.CancelledQuantity), line.UnitPrice, line.VatRate, ResolveVariantId(line) is { } variantId ? mediaByVariant.GetValueOrDefault(variantId) ?? mediaByProduct.GetValueOrDefault(variantProductIds.GetValueOrDefault(variantId)) : null)).ToList();
            var deliveryState = currentDeliveryState;
            var deliveryAttempt = invoice is null
                ? null
                : deliveryAttempts.Where(x => x.InvoiceId == invoice.Id && x.PackageId == package.Id).OrderByDescending(x => x.AttemptNumber).FirstOrDefault();
            var invoiceReadIssue = invoiceReadIssues.GetValueOrDefault($"trendyol-invoice-package-read:{package.ConnectionId}:{package.Id}")
                ?? invoiceReadIssues.GetValueOrDefault($"order-invoice-reconciliation:{order.ConnectionId}:{order.ExternalOrderId}");
            if (invoiceReadIssue is not null && !InvoiceWorkspaceReadIssuePolicy.ShouldDisplay(invoiceReadIssue.Code))
                invoiceReadIssue = null;
            return new InvoiceWorkspaceItemView(order.Id, package.Id, order.OrderNumber, customerName, order.OrderedAt, package.Status.ToString().ToUpperInvariant(), deliveredAt, dueAt, dueSoon, order.Currency, package.NetAmount > 0 ? package.NetAmount : order.NetAmount, orderLines.Count, image, package.CargoProviderExternalId, package.CargoTrackingNumber, invoice?.Id, invoiceStatus, ResolveInvoiceNumber(invoice?.InvoiceNumber, package.MarketplaceInvoiceNumber), invoiceStatus == "FATURA_BEKLIYOR", order.ShipmentAddressSnapshotJson, order.InvoiceAddressSnapshotJson, workspaceLines, invoice?.LastErrorCode, deliveryState?.Status ?? deliveryAttempt?.Status, deliveryState?.ExternalReference ?? deliveryAttempt?.ExternalReference, invoice is not null && invoiceDocumentIds.Contains(invoice.Id), connection?.PlatformCode ?? "TRENDYOL", connection?.DisplayName ?? "Trendyol", connection is not null && MarketplaceInvoiceCreationPolicy.IsEnabled(connection.PlatformCode, connection.SettingsJson), invoiceReadIssue?.Code, invoiceReadIssue?.Summary, connection?.Environment ?? "UNKNOWN", package.MarketplaceInvoiceUrl);
        }).Where(x => x is not null).Select(x => x!).ToList();
    }

    public async Task<InvoiceWorkspaceSummaryView> WorkspaceSummaryAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var workspaceConnections = await ReadWorkspaceConnectionsAsync(tenantId, cancellationToken);
        if (workspaceConnections.Count == 0) return new(0);

        var hiddenSetting = await db.TenantSettings.AsNoTracking()
            .SingleOrDefaultAsync(setting => setting.TenantId == tenantId && setting.Key == "invoice-workspace-hidden-packages", cancellationToken);
        var hiddenPackageIds = hiddenSetting is null
            ? new HashSet<Guid>()
            : JsonSerializer.Deserialize<HashSet<Guid>>(hiddenSetting.ValueJson) ?? [];

        var now = timeProvider.GetUtcNow();
        var dueSoonCount = 0;
        var uninvoicedCount = 0;
        WorkspaceScanCursor? cursor = null;
        while (true)
        {
            var candidates = await ReadWorkspaceCandidateBatchAsync(tenantId, workspaceConnections, cursor, now, includeCustomerName: false, cancellationToken);
            if (candidates.Count == 0) break;
            foreach (var candidate in candidates)
            {
                // Same rule as the Faturalar page: hidden packages are not pending work.
                if (!hiddenPackageIds.Contains(candidate.Package.Id)
                    && IsWorkspaceInvoiceUninvoiced(candidate.InvoiceStatus, candidate.CanCreateInvoice, HasMarketplaceInvoiceObservation(candidate))) uninvoicedCount++;
                if (!hiddenPackageIds.Contains(candidate.Package.Id)
                    && candidate.Package.Status == ShipmentPackageStatus.Delivered
                    && now >= candidate.Package.StatusOccurredAt.AddDays(DashboardMetricPolicy.InvoiceReminderStartDays)
                    && candidate.InvoiceStatus == "FATURA_BEKLIYOR") dueSoonCount++;
            }
            var last = candidates[^1];
            cursor = new(last.Package.StatusOccurredAt, last.Package.Id);
        }

        return new(dueSoonCount, uninvoicedCount);
    }

    public async Task<ServiceResult<Guid>> EnqueueOrderInvoiceReconciliationAsync(
        Guid tenantId,
        Guid orderId,
        string idempotencyKey,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.Id == orderId, cancellationToken);
        if (order is null) return NotFound<Guid>();

        var connection = await db.PlatformConnections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.Id == order.ConnectionId, cancellationToken);
        if (connection is null || connection.PlatformCode is not ("TRENDYOL" or "HEPSIBURADA" or "SHOPIFY"))
            return ServiceResult<Guid>.Fail("INVOICE_MARKETPLACE_UNSUPPORTED", "Bu sipariş için pazaryeri fatura kontrolü desteklenmiyor.", 422);
        if (connection.Status is not ("ACTIVE" or "VERIFIED") || !IntegrationRuntimePolicy.AllowsManualRead(connection))
            return ServiceResult<Guid>.Fail("INVOICE_MARKETPLACE_READ_UNAVAILABLE", "Pazaryeri bağlantısı şu anda salt okunur durum kontrolüne uygun değil.", 409);
        if (!await db.ShipmentPackages.AsNoTracking().AnyAsync(package => package.TenantId == tenantId
                && package.OrderId == orderId && package.Status != ShipmentPackageStatus.Cancelled,
                cancellationToken))
            return ServiceResult<Guid>.Fail("INVOICE_PACKAGE_REQUIRED", "Siparişte kontrol edilecek etkin paket bulunamadı.", 422);
        if (string.IsNullOrWhiteSpace(order.ExternalOrderId))
            return ServiceResult<Guid>.Fail("INVOICE_EXTERNAL_ORDER_ID_MISSING", "Pazaryerindeki sipariş kimliği bulunamadığı için durum kontrolü kuyruğa alınamadı.", 409);

        var normalizedKey = idempotencyKey.Trim();
        if (normalizedKey.Length is < 1 or > 200)
            return ServiceResult<Guid>.Fail("IDEMPOTENCY_KEY_INVALID", "Idempotency-Key uzunluğu geçersiz.", 400);
        var jobType = MarketplaceJobTypes.ForPlatform(connection.PlatformCode, MarketplaceJobTypes.OrderInvoiceReconciliation);
        var dedup = $"workspace-invoice-check:{connection.Id:N}:{order.Id:N}:{Hash(normalizedKey)}";
        var existing = await db.IntegrationJobs.AsNoTracking()
            .SingleOrDefaultAsync(job => job.TenantId == tenantId && job.JobType == jobType && job.JobDedupKey == dedup, cancellationToken);
        if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);

        var now = timeProvider.GetUtcNow();
        var payload = JsonSerializer.Serialize(new { connectionId = connection.Id, targetOrderId = order.Id, batchSize = 1 });
        var job = new IntegrationJob
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connection.Id,
            JobType = jobType,
            PayloadJson = payload,
            PayloadVersion = 1,
            PayloadHash = Hash(payload),
            JobDedupKey = dedup,
            EffectIdempotencyKey = dedup,
            Priority = JobPriorityPolicy.UserAction,
            TriggerType = "user",
            AvailableAt = now,
            CorrelationId = correlationId,
            CreatedAt = now,
            Version = 1
        };
        db.IntegrationJobs.Add(job);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<Guid>.Ok(job.Id);
        }
        catch (DbUpdateException)
        {
            db.Entry(job).State = EntityState.Detached;
            existing = await db.IntegrationJobs.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.JobType == jobType && candidate.JobDedupKey == dedup, cancellationToken);
            return existing is not null
                ? ServiceResult<Guid>.Ok(existing.Id)
                : ServiceResult<Guid>.Fail("INVOICE_RECONCILIATION_QUEUE_FAILED", "Fatura durum kontrolü kuyruğa alınamadı. Yenileyip tekrar deneyin.", 409);
        }
    }

    public async Task<InvoiceWorkspacePageView> WorkspacePageAsync(Guid tenantId, InvoiceWorkspacePageQuery request, CancellationToken cancellationToken)
    {
        var pageSize = request.PageSize is 20 or 50 or 100 or 200 ? request.PageSize : 20;
        var requestedPage = Math.Max(1, request.PageNumber);
        var normalizedTab = request.Tab.Trim().ToUpperInvariant() is "INVOICED" or "DUE_SOON" or "HIDDEN"
            ? request.Tab.Trim().ToUpperInvariant()
            : "UNINVOICED";
        var hiddenSetting = await db.TenantSettings.AsNoTracking()
            .SingleOrDefaultAsync(setting => setting.TenantId == tenantId && setting.Key == "invoice-workspace-hidden-packages", cancellationToken);
        var hiddenPackageIds = hiddenSetting is null
            ? new HashSet<Guid>()
            : JsonSerializer.Deserialize<HashSet<Guid>>(hiddenSetting.ValueJson) ?? [];
        var selectedPlatforms = (request.PlatformCodes ?? [])
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var search = request.Search?.Trim();
        if (search?.Length > 200) search = search[..200];
        var searchKey = string.IsNullOrWhiteSpace(search) ? null : search.ToLower(WorkspaceSearchCulture);

        var workspaceConnections = await ReadWorkspaceConnectionsAsync(tenantId, cancellationToken);
        if (workspaceConnections.Count == 0) return EmptyWorkspacePage(requestedPage, pageSize);

        var now = timeProvider.GetUtcNow();
        var from = request.From;
        var to = request.To;
        var selectedTotal = 0;
        var selectedUninvoiced = 0;
        var selectedInvoiced = 0;
        var selectedDueSoon = 0;
        var hasPendingMarketplaceInvoices = false;
        var filteredCount = 0;
        var pageCandidates = new List<WorkspaceCandidate>(pageSize);
        var lastPageCandidates = new Queue<WorkspaceCandidate>(pageSize);
        var shipmentStatuses = new HashSet<string>(StringComparer.Ordinal);
        var cargoProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invoiceStatuses = new HashSet<string>(StringComparer.Ordinal);
        var offset = (requestedPage - 1) * pageSize;
        var sortKey = request.Sort?.Trim().ToUpperInvariant();
        var sortedPool = sortKey is null or "" or "DELIVERY_DESC" ? null : new List<WorkspaceCandidate>();
        WorkspaceScanCursor? cursor = null;

        while (true)
        {
            var candidates = await ReadWorkspaceCandidateBatchAsync(tenantId, workspaceConnections, cursor, now, includeCustomerName: searchKey is not null, cancellationToken);
            if (candidates.Count == 0) break;

            foreach (var candidate in candidates)
            {
                var isHidden = hiddenPackageIds.Contains(candidate.Package.Id);
                var platformSelected = selectedPlatforms.Count == 0 || selectedPlatforms.Contains(candidate.Connection.PlatformCode);
                if (platformSelected && !isHidden)
                {
                    selectedTotal++;
                    if (IsWorkspaceInvoiceUninvoiced(candidate.InvoiceStatus, candidate.CanCreateInvoice, HasMarketplaceInvoiceObservation(candidate))) selectedUninvoiced++;
                    else selectedInvoiced++;
                    if (candidate.IsDueSoon) selectedDueSoon++;
                    if (!string.Equals(candidate.Connection.PlatformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase)
                        && (candidate.CanCreateInvoice || candidate.InvoiceStatus is "FATURA_REDDEDILDI" or "FATURA_BELIRSIZ"))
                        hasPendingMarketplaceInvoices = true;
                }

                shipmentStatuses.Add(candidate.Package.Status.ToString().ToUpperInvariant());
                if (!string.IsNullOrWhiteSpace(candidate.Package.CargoProviderExternalId))
                    cargoProviders.Add(candidate.Package.CargoProviderExternalId.Trim());
                invoiceStatuses.Add(WorkspaceInvoiceDisplayStatus(candidate));

                if (!MatchesWorkspacePageRequest(candidate, normalizedTab, request, searchKey, from, to, isHidden)) continue;
                var resultIndex = filteredCount++;
                if (sortedPool is not null) { sortedPool.Add(candidate); continue; }
                if (resultIndex >= offset && pageCandidates.Count < pageSize) pageCandidates.Add(candidate);
                lastPageCandidates.Enqueue(candidate);
                if (lastPageCandidates.Count > pageSize) lastPageCandidates.Dequeue();
            }

            var last = candidates[^1];
            cursor = new(last.Package.StatusOccurredAt, last.Package.Id);
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(filteredCount / (double)pageSize));
        var pageNumber = Math.Min(requestedPage, totalPages);
        if (sortedPool is not null)
        {
            pageCandidates = OrderWorkspaceCandidates(sortedPool, sortKey!).Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
        }
        else if (pageNumber != requestedPage)
        {
            var lastPageSize = filteredCount % pageSize;
            if (lastPageSize == 0 && filteredCount > 0) lastPageSize = pageSize;
            pageCandidates = lastPageCandidates.TakeLast(lastPageSize).ToList();
        }
        var items = await MaterializeWorkspacePageAsync(tenantId, pageCandidates, cancellationToken);
        return new(items, filteredCount, pageNumber, pageSize, totalPages, selectedUninvoiced, selectedInvoiced, selectedDueSoon, hiddenPackageIds.Count, selectedTotal, hasPendingMarketplaceInvoices,
            shipmentStatuses.Order(StringComparer.Ordinal).ToArray(), cargoProviders.Order(StringComparer.OrdinalIgnoreCase).ToArray(), invoiceStatuses.Order(StringComparer.Ordinal).ToArray());
    }

    // Ties always fall back to the shipment date and package id so pages stay stable between requests.
    private static IEnumerable<WorkspaceCandidate> OrderWorkspaceCandidates(List<WorkspaceCandidate> candidates, string sort)
    {
        var byDelivery = candidates.OrderByDescending(candidate => candidate.Package.StatusOccurredAt).ThenBy(candidate => candidate.Package.Id);
        return sort switch
        {
            "DELIVERY_ASC" => candidates.OrderBy(candidate => candidate.Package.StatusOccurredAt).ThenBy(candidate => candidate.Package.Id),
            "ORDER_DESC" => candidates.OrderByDescending(candidate => candidate.Order.OrderedAt).ThenBy(candidate => candidate.Package.Id),
            "ORDER_ASC" => candidates.OrderBy(candidate => candidate.Order.OrderedAt).ThenBy(candidate => candidate.Package.Id),
            "DUE_ASC" => candidates.OrderBy(candidate => candidate.InvoiceDueAt == null).ThenBy(candidate => candidate.InvoiceDueAt).ThenBy(candidate => candidate.Package.Id),
            "AMOUNT_DESC" => candidates.OrderByDescending(candidate => candidate.Amount).ThenBy(candidate => candidate.Package.Id),
            "AMOUNT_ASC" => candidates.OrderBy(candidate => candidate.Amount).ThenBy(candidate => candidate.Package.Id),
            _ => byDelivery
        };
    }

    private async Task<IReadOnlyList<WorkspaceCandidate>> ReadWorkspaceCandidateBatchAsync(
        Guid tenantId,
        IReadOnlyDictionary<Guid, WorkspaceConnectionProjection> workspaceConnections,
        WorkspaceScanCursor? cursor,
        DateTimeOffset now,
        bool includeCustomerName,
        CancellationToken cancellationToken)
    {
        var operationalConnectionIds = workspaceConnections.Keys.ToArray();
        var shopifyConnectionIds = workspaceConnections.Values
            .Where(connection => string.Equals(connection.PlatformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
            .Select(connection => connection.Id)
            .ToArray();
        var trendyolConnectionIds = workspaceConnections.Values
            .Where(connection => string.Equals(connection.PlatformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase))
            .Select(connection => connection.Id)
            .ToArray();
        var sourceQuery =
            from package in db.ShipmentPackages.AsNoTracking()
            join order in db.Orders.AsNoTracking()
                on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
            where package.TenantId == tenantId
                && operationalConnectionIds.Contains(package.ConnectionId)
                && package.Status != ShipmentPackageStatus.Cancelled
                && !DashboardMetricPolicy.InvoiceExcludedOrderStatuses.Contains((order.DerivedStatus ?? string.Empty).Trim().ToUpper())
                && !(shopifyConnectionIds.Contains(package.ConnectionId)
                    && package.ExternalPackageId.StartsWith("order:")
                    && package.ExternalPackageId.EndsWith(":remainder"))
                && !(trendyolConnectionIds.Contains(package.ConnectionId)
                    && package.ExternalPackageId == "0"
                    && db.ShipmentPackages.Any(realPackage => realPackage.TenantId == tenantId
                        && realPackage.ConnectionId == package.ConnectionId
                        && realPackage.OrderId == package.OrderId
                        && realPackage.ExternalPackageId != "0"
                        && realPackage.ExternalPackageId != string.Empty))
                && (package.CreatedBy != "HEPSIBURADA_STATUS_FEED"
                    || db.PackageLineAllocations.Any(allocation => allocation.TenantId == tenantId && allocation.PackageId == package.Id)
                    || !db.ShipmentPackages.Any(otherPackage => otherPackage.TenantId == tenantId
                        && otherPackage.OrderId == package.OrderId
                        && otherPackage.Id != package.Id
                        && db.PlatformConnections.Any(otherConnection => otherConnection.TenantId == tenantId
                            && otherConnection.Id == otherPackage.ConnectionId
                            && (otherConnection.Status == "ACTIVE" || otherConnection.Status == "VERIFIED"))))
            select new { package, order };

        if (cursor is not null)
        {
            sourceQuery = sourceQuery.Where(source => source.package.StatusOccurredAt < cursor.StatusOccurredAt
                || (source.package.StatusOccurredAt == cursor.StatusOccurredAt && source.package.Id.CompareTo(cursor.PackageId) < 0));
        }

        var sourceRows = await sourceQuery
            .OrderByDescending(source => source.package.StatusOccurredAt)
            .ThenByDescending(source => source.package.Id)
            .Take(WorkspaceCandidateScanBatchSize)
            .Select(source => new WorkspaceCandidateSource(
                new WorkspacePackageProjection(
                    source.package.Id,
                    source.package.ExternalPackageId,
                    source.package.OrderId,
                    source.package.ConnectionId,
                    source.package.CargoProviderExternalId,
                    includeCustomerName ? source.package.CargoTrackingNumber : null,
                    source.package.Status,
                    source.package.RawStatus,
                    source.package.StatusOccurredAt,
                    source.package.MarketplaceInvoiceStatus,
                    includeCustomerName ? source.package.MarketplaceInvoiceNumber : null,
                    source.package.MarketplaceInvoiceUrl,
                    source.package.MarketplaceInvoiceObservedAt,
                    source.package.ManualInvoiceStatus),
                new WorkspaceOrderProjection(
                    source.order.Id,
                    includeCustomerName ? source.order.OrderNumber : string.Empty,
                    source.order.OrderedAt,
                    includeCustomerName || source.package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Unknown
                        ? source.order.CustomerSnapshotJson
                        : "{}",
                    includeCustomerName ? source.order.ShipmentAddressSnapshotJson : "{}",
                    includeCustomerName ? source.order.InvoiceAddressSnapshotJson : "{}")))
            .ToListAsync(cancellationToken);
        if (sourceRows.Count == 0) return [];

        var packageIds = sourceRows.Select(source => source.Package.Id).ToArray();
        var orderIds = sourceRows.Select(source => source.Order.Id).Distinct().ToArray();
        var invoices = await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.TenantId == tenantId && invoice.OriginalInvoiceId == null && invoice.SequencePurpose == "SALE"
                && ((invoice.PackageId != null && packageIds.Contains(invoice.PackageId.Value))
                    || (invoice.PackageId == null && orderIds.Contains(invoice.OrderId)))
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId
                    && connection.Id == invoice.ProviderConnectionId
                    && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))
            .OrderByDescending(invoice => invoice.CreatedAt)
            .Select(invoice => new WorkspaceInvoiceProjection(invoice.Id, invoice.OrderId, invoice.PackageId, invoice.Status, invoice.InvoiceNumber, invoice.LastErrorCode, invoice.ExternalReference, invoice.CreatedAt,
                null,
                false))
            .ToListAsync(cancellationToken);
        var selectedInvoiceIds = invoices.Select(invoice => invoice.Id).Distinct().ToArray();
        var packageDeliveryStates = selectedInvoiceIds.Length == 0
            ? []
            : await db.MarketplaceDeliveryStates.AsNoTracking()
                .Where(state => state.TenantId == tenantId && selectedInvoiceIds.Contains(state.InvoiceId)
                    && state.PackageId != null && packageIds.Contains(state.PackageId.Value))
                .Select(state => new { state.InvoiceId, PackageId = state.PackageId!.Value, state.Status, state.UpdatedAt })
                .ToListAsync(cancellationToken);
        var packageDeliveryAttempts = selectedInvoiceIds.Length == 0
            ? []
            : await db.MarketplaceDeliveries.AsNoTracking()
                .Where(attempt => attempt.TenantId == tenantId && selectedInvoiceIds.Contains(attempt.InvoiceId)
                    && attempt.PackageId != null && packageIds.Contains(attempt.PackageId.Value))
                .Select(attempt => new { attempt.InvoiceId, PackageId = attempt.PackageId!.Value, attempt.Status, attempt.AttemptNumber })
                .ToListAsync(cancellationToken);
        var invoicesByPackage = invoices.Where(invoice => invoice.PackageId.HasValue)
            .GroupBy(invoice => invoice.PackageId!.Value)
            .ToDictionary(group => group.Key, group => group.First());
        var invoicesByOrder = invoices.Where(invoice => !invoice.PackageId.HasValue)
            .GroupBy(invoice => invoice.OrderId)
            .ToDictionary(group => group.Key, group => group.First());

        return sourceRows.Select(source =>
        {
            var package = source.Package;
            var order = source.Order;
            var connection = workspaceConnections[package.ConnectionId];
            var selectedInvoice = invoicesByPackage.GetValueOrDefault(package.Id) ?? invoicesByOrder.GetValueOrDefault(order.Id);
            var invoice = selectedInvoice is null ? null : selectedInvoice with
            {
                CurrentDeliveryStateStatus = packageDeliveryStates
                    .Where(state => state.InvoiceId == selectedInvoice.Id && state.PackageId == package.Id)
                    .OrderByDescending(state => state.UpdatedAt).Select(state => state.Status).FirstOrDefault(),
                HasConfirmedHistoricalAttempt = packageDeliveryAttempts.Any(attempt => attempt.InvoiceId == selectedInvoice.Id
                    && attempt.PackageId == package.Id && attempt.Status == "CONFIRMED")
            };
            var invoiceStatus = WorkspaceInvoiceStatus(package, order, invoice, connection);
            var deliveredAt = package.Status == ShipmentPackageStatus.Delivered ? package.StatusOccurredAt : (DateTimeOffset?)null;
            var dueSoon = invoiceStatus == "FATURA_BEKLIYOR" && deliveredAt is not null
                && now >= deliveredAt.Value.AddDays(DashboardMetricPolicy.InvoiceReminderStartDays);
            return new WorkspaceCandidate(
                package,
                order,
                connection,
                invoice,
                invoiceStatus,
                string.Equals(invoiceStatus, "FATURA_BEKLIYOR", StringComparison.Ordinal),
                dueSoon,
                deliveredAt,
                deliveredAt?.AddDays(DashboardMetricPolicy.InvoiceDueDays),
                0m,
                includeCustomerName ? InvoiceWorkspaceCustomerName(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson) : string.Empty,
                connection.InvoiceCreationEnabled);
        }).ToList();
    }

    private async Task<IReadOnlyDictionary<Guid, WorkspaceConnectionProjection>> ReadWorkspaceConnectionsAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var connections = await db.PlatformConnections.AsNoTracking()
            .Where(connection => connection.TenantId == tenantId
                && InvoiceWorkspaceMarketplacePolicy.PlatformCodes.Contains(connection.PlatformCode)
                && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
            .Select(connection => new { connection.Id, connection.PlatformCode, connection.DisplayName, connection.Environment, connection.SettingsJson })
            .ToListAsync(cancellationToken);

        return connections.ToDictionary(
            connection => connection.Id,
            connection => new WorkspaceConnectionProjection(
                connection.Id,
                connection.PlatformCode,
                connection.DisplayName,
                MarketplaceInvoiceCreationPolicy.IsEnabled(connection.PlatformCode, connection.SettingsJson),
                connection.Environment));
    }

    private static bool MatchesWorkspacePageRequest(
        WorkspaceCandidate candidate,
        string normalizedTab,
        InvoiceWorkspacePageQuery request,
        string? searchKey,
        DateTimeOffset? from,
        DateTimeOffset? to,
        bool isHidden)
    {
        var tabMatch = normalizedTab switch
        {
            "HIDDEN" => isHidden,
            "INVOICED" => !isHidden && !IsWorkspaceInvoiceUninvoiced(candidate.InvoiceStatus, candidate.CanCreateInvoice, HasMarketplaceInvoiceObservation(candidate)),
            "DUE_SOON" => !isHidden && candidate.IsDueSoon,
            _ => !isHidden && IsWorkspaceInvoiceUninvoiced(candidate.InvoiceStatus, candidate.CanCreateInvoice, HasMarketplaceInvoiceObservation(candidate))
        };
        var shipmentMatch = string.IsNullOrWhiteSpace(request.ShipmentStatus) || request.ShipmentStatus == "ALL"
            || string.Equals(candidate.Package.Status.ToString(), request.ShipmentStatus, StringComparison.OrdinalIgnoreCase);
        var cargoMatch = string.IsNullOrWhiteSpace(request.CargoProviderName) || request.CargoProviderName == "ALL"
            || string.Equals(candidate.Package.CargoProviderExternalId?.Trim(), request.CargoProviderName.Trim(), StringComparison.OrdinalIgnoreCase);
        var invoiceMatch = string.IsNullOrWhiteSpace(request.InvoiceStatus) || request.InvoiceStatus == "ALL"
            || string.Equals(WorkspaceInvoiceDisplayStatus(candidate), request.InvoiceStatus, StringComparison.OrdinalIgnoreCase);
        var actionMatch = WorkspaceMatchesInvoiceAction(candidate, request.InvoiceAction, request.ProviderHasCredential);
        var platformMatch = MatchesWorkspacePlatformFilter(request.PlatformCodes, candidate.Connection.PlatformCode);
        var dateMatch = (!from.HasValue || candidate.Order.OrderedAt >= from.Value)
            && (!to.HasValue || candidate.Order.OrderedAt <= to.Value);
        var searchMatch = searchKey is null || new[] { candidate.Order.OrderNumber, candidate.CustomerName, candidate.InvoiceNumber, candidate.Package.CargoTrackingNumber }
            .Any(value => value?.ToLower(WorkspaceSearchCulture).Contains(searchKey, StringComparison.Ordinal) == true);
        return tabMatch && platformMatch && shipmentMatch && cargoMatch && invoiceMatch && actionMatch && dateMatch && searchMatch;
    }

    // An invoice that was issued sits with the invoiced ones while it is being sent to the marketplace; one that could
    // not be issued or could not be delivered (its reason shown) stays with those that have no invoice. So does a
    // "Faturası Mevcut" the marketplace has not confirmed: that label comes from the customer snapshot alone, a
    // confirmed one carries the marketplace observation and is handled by the first condition.
    internal static bool IsWorkspaceInvoiceUninvoiced(string invoiceStatus, bool canCreateInvoice, bool marketplaceInvoicePresent = false) =>
        !marketplaceInvoicePresent && (canCreateInvoice
            || invoiceStatus is "FATURA_REDDEDILDI" or "FATURA_BELIRSIZ" or "FATURA_KONTROLDE" or "FATURA_PLATFORMA_AKTARILMADI");

    private static bool HasMarketplaceInvoiceObservation(WorkspaceCandidate candidate) =>
        candidate.Package.MarketplaceInvoiceStatus is MarketplaceInvoiceStatus.Invoiced or MarketplaceInvoiceStatus.Received;

    internal static bool MatchesWorkspacePlatformFilter(IReadOnlyList<string>? platformCodes, string candidatePlatformCode) =>
        platformCodes is null || platformCodes.Count == 0 || platformCodes.Any(platform =>
            string.Equals(platform?.Trim(), candidatePlatformCode, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<InvoiceWorkspaceItemView>> MaterializeWorkspacePageAsync(Guid tenantId, IReadOnlyList<WorkspaceCandidate> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return [];
        var orderIds = candidates.Select(candidate => candidate.Order.Id).Distinct().ToArray();
        var packageIds = candidates.Select(candidate => candidate.Package.Id).ToArray();
        var lines = await db.OrderLines.AsNoTracking()
            .Where(line => line.TenantId == tenantId && orderIds.Contains(line.OrderId))
            .ToListAsync(cancellationToken);
        var orderSnapshots = await db.Orders.AsNoTracking()
            .Where(order => order.TenantId == tenantId && orderIds.Contains(order.Id))
            .Select(order => new
            {
                order.Id,
                order.ConnectionId,
                order.ExternalOrderId,
                order.OrderNumber,
                order.OrderedAt,
                order.Currency,
                order.NetAmount,
                order.CustomerSnapshotJson,
                order.InvoiceAddressSnapshotJson,
                order.ShipmentAddressSnapshotJson
            })
            .ToDictionaryAsync(order => order.Id, cancellationToken);
        var packageDetails = await db.ShipmentPackages.AsNoTracking()
            .Where(package => package.TenantId == tenantId && packageIds.Contains(package.Id))
            .Select(package => new { package.Id, package.ExternalPackageId, package.NetAmount, package.CargoTrackingNumber, package.MarketplaceInvoiceNumber, package.MarketplaceInvoiceUrl })
            .ToDictionaryAsync(package => package.Id, cancellationToken);
        var previousPlatformReferences = await db.MarketplaceInvoiceReferenceHistories.AsNoTracking()
            .Where(reference => reference.TenantId == tenantId && packageIds.Contains(reference.PackageId))
            .OrderByDescending(reference => reference.ChangedAt)
            .Select(reference => new { reference.PackageId, reference.InvoiceNumber, reference.InvoiceUrl, reference.ChangeReason, reference.ChangedAt })
            .ToListAsync(cancellationToken);
        var previousPlatformReferencesByPackage = previousPlatformReferences
            .GroupBy(reference => reference.PackageId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<MarketplaceInvoiceReferenceHistoryView>)group
                .Select(reference => new MarketplaceInvoiceReferenceHistoryView(reference.InvoiceNumber, reference.InvoiceUrl, reference.ChangeReason, reference.ChangedAt))
                .ToArray());
        var variantIds = lines.Where(line => line.VariantId != null).Select(line => line.VariantId!.Value).Distinct().ToArray();
        var lineSkus = lines.Select(line => line.Sku).Where(sku => !string.IsNullOrWhiteSpace(sku)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var lineBarcodes = lines.Select(line => line.Barcode).Where(barcode => !string.IsNullOrWhiteSpace(barcode)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var variantRows = await db.ProductVariants.AsNoTracking()
            .Where(variant => variant.TenantId == tenantId
                && (variantIds.Contains(variant.Id) || lineSkus.Contains(variant.Sku) || (variant.Barcode != null && lineBarcodes.Contains(variant.Barcode))))
            .Select(variant => new { variant.Id, variant.ProductId, variant.Sku, variant.Barcode })
            .ToListAsync(cancellationToken);
        var variantProductIds = variantRows.ToDictionary(variant => variant.Id, variant => variant.ProductId);
        var variantBySku = variantRows.Where(variant => !string.IsNullOrWhiteSpace(variant.Sku))
            .GroupBy(variant => variant.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);
        var variantByBarcode = variantRows.Where(variant => !string.IsNullOrWhiteSpace(variant.Barcode))
            .GroupBy(variant => variant.Barcode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);
        Guid? ResolveVariantId(OrderLine line) => line.VariantId
            ?? (line.Barcode is not null && variantByBarcode.TryGetValue(line.Barcode, out var barcodeVariantId)
                ? barcodeVariantId
                : variantBySku.GetValueOrDefault(line.Sku));
        var resolvedVariantIds = lines.Select(ResolveVariantId).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        foreach (var variant in variantRows) variantProductIds.TryAdd(variant.Id, variant.ProductId);
        var productIds = variantProductIds.Values.Distinct().ToArray();
        var mediaRows = await (from media in db.ProductMedia.AsNoTracking()
                               join asset in db.FileAssets.AsNoTracking() on new { media.TenantId, media.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                               where media.TenantId == tenantId
                                   && ((media.VariantId != null && resolvedVariantIds.Contains(media.VariantId.Value)) || (media.VariantId == null && productIds.Contains(media.ProductId)))
                                   && media.Status == "ACTIVE" && asset.Status == "ACTIVE"
                                   && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                               orderby media.SortOrder
                               select new { media.VariantId, media.ProductId, asset.Id, asset.Classification, asset.RelativePath })
            .ToListAsync(cancellationToken);
        var mediaByVariant = mediaRows.Where(media => media.VariantId.HasValue).GroupBy(media => media.VariantId!.Value)
            .ToDictionary(group => group.Key, group => MediaUrl(group.First().Id, group.First().Classification, group.First().RelativePath));
        var mediaByProduct = mediaRows.Where(media => media.VariantId == null).GroupBy(media => media.ProductId)
            .ToDictionary(group => group.Key, group => MediaUrl(group.First().Id, group.First().Classification, group.First().RelativePath));
        var linesByOrder = lines.GroupBy(line => line.OrderId).ToDictionary(group => group.Key, group => group.ToList());

        var invoiceIds = candidates.Where(candidate => candidate.Invoice is not null).Select(candidate => candidate.Invoice!.Id).Distinct().ToArray();
        var invoiceDocumentIds = invoiceIds.Length == 0
            ? new HashSet<Guid>()
            : (await db.InvoiceDocuments.AsNoTracking().Where(document => document.TenantId == tenantId && invoiceIds.Contains(document.InvoiceId))
                .Select(document => document.InvoiceId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var deliveredLinks = invoiceIds.Length == 0
            ? new Dictionary<Guid, string>()
            : (await db.InvoiceDocuments.AsNoTracking()
                .Where(document => document.TenantId == tenantId && invoiceIds.Contains(document.InvoiceId) && document.PermanentUrl != null)
                .OrderByDescending(document => document.CreatedAt)
                .Select(document => new { document.InvoiceId, document.PermanentUrl })
                .ToListAsync(cancellationToken))
                .GroupBy(document => document.InvoiceId)
                .ToDictionary(group => group.Key, group => group.First().PermanentUrl!);
        var manualInvoiceDocuments = await db.ManualOrderInvoiceDocuments.AsNoTracking()
            .Where(document => document.TenantId == tenantId && orderIds.Contains(document.OrderId) && document.Status == "ACTIVE")
            .OrderByDescending(document => document.CreatedAt)
            .Select(document => new { document.Id, document.OrderId, document.DocumentType, document.DocumentUrl, document.InvoiceNumber, document.IssuedAt })
            .ToListAsync(cancellationToken);
        var manualInvoiceDocumentIds = manualInvoiceDocuments.Select(document => document.Id).ToArray();
        var manualInvoiceDeliveryStates = manualInvoiceDocumentIds.Length == 0
            ? []
            : await db.ManualOrderInvoiceDeliveryStates.AsNoTracking()
                .Where(state => state.TenantId == tenantId && manualInvoiceDocumentIds.Contains(state.DocumentId) && packageIds.Contains(state.PackageId))
                .Select(state => new { state.DocumentId, state.PackageId, state.Status, state.ErrorCode, state.UpdatedAt })
                .ToListAsync(cancellationToken);
        var deliveryStates = invoiceIds.Length == 0
            ? []
            : await db.MarketplaceDeliveryStates.AsNoTracking()
                .Where(state => state.TenantId == tenantId && invoiceIds.Contains(state.InvoiceId))
                .Select(state => new { state.InvoiceId, state.PackageId, state.Status, state.ExternalReference, state.UpdatedAt })
                .ToListAsync(cancellationToken);
        var deliveryAttempts = invoiceIds.Length == 0
            ? []
            : await db.MarketplaceDeliveries.AsNoTracking()
                .Where(attempt => attempt.TenantId == tenantId && invoiceIds.Contains(attempt.InvoiceId))
                .Select(attempt => new { attempt.InvoiceId, attempt.PackageId, attempt.Status, attempt.ExternalReference, attempt.AttemptNumber })
                .ToListAsync(cancellationToken);
        var invoiceReadIssueKeys = candidates.SelectMany(candidate =>
        {
            var keys = new List<string> { $"trendyol-invoice-package-read:{candidate.Package.ConnectionId}:{candidate.Package.Id}" };
            if (orderSnapshots.TryGetValue(candidate.Order.Id, out var order)
                && !string.IsNullOrWhiteSpace(order.ExternalOrderId))
                keys.Add($"order-invoice-reconciliation:{order.ConnectionId}:{order.ExternalOrderId}");
            return keys;
        }).Distinct().ToArray();
        var invoiceReadIssues = invoiceReadIssueKeys.Length == 0
            ? new Dictionary<string, (string Code, string Summary)>(StringComparer.Ordinal)
            : (await db.OperationalIssues.AsNoTracking()
                .Where(issue => issue.TenantId == tenantId && issue.Status == IssueStatus.Open
                    && invoiceReadIssueKeys.Contains(issue.DedupeKey))
                .Select(issue => new { issue.DedupeKey, issue.Code, issue.Summary })
                .ToListAsync(cancellationToken))
                .ToDictionary(issue => issue.DedupeKey, issue => (issue.Code, issue.Summary), StringComparer.Ordinal);

        return candidates.Select(candidate =>
        {
            var orderSnapshotsForItem = orderSnapshots.GetValueOrDefault(candidate.Order.Id);
            var packageDetailsForItem = packageDetails.GetValueOrDefault(candidate.Package.Id);
            var customerName = orderSnapshotsForItem is null
                ? candidate.CustomerName
                : InvoiceWorkspaceCustomerName(orderSnapshotsForItem.CustomerSnapshotJson, orderSnapshotsForItem.InvoiceAddressSnapshotJson, orderSnapshotsForItem.ShipmentAddressSnapshotJson);
            var orderLines = (linesByOrder.GetValueOrDefault(candidate.Order.Id) ?? [])
                .Where(line => OrderLinePresentationPolicy.HasActiveQuantity(line.OrderedQuantity, line.CancelledQuantity))
                .ToList();
            var workspaceLines = orderLines.Select(line => new InvoiceWorkspaceLineView(
                line.Sku,
                line.Barcode,
                line.TitleSnapshot,
                OrderLinePresentationPolicy.ActiveQuantity(line.OrderedQuantity, line.CancelledQuantity),
                line.UnitPrice,
                line.VatRate,
                ResolveVariantId(line) is { } variantId
                    ? mediaByVariant.GetValueOrDefault(variantId) ?? mediaByProduct.GetValueOrDefault(variantProductIds.GetValueOrDefault(variantId))
                    : null)).ToList();
            var primaryImage = workspaceLines.Select(line => line.ImageUrl).FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
            var deliveryState = candidate.Invoice is null ? null : deliveryStates
                .Where(state => state.InvoiceId == candidate.Invoice.Id && state.PackageId == candidate.Package.Id).OrderByDescending(state => state.UpdatedAt).FirstOrDefault();
            var deliveryAttempt = candidate.Invoice is null ? null : deliveryAttempts
                .Where(attempt => attempt.InvoiceId == candidate.Invoice.Id && attempt.PackageId == candidate.Package.Id).OrderByDescending(attempt => attempt.AttemptNumber).FirstOrDefault();
            var manualDocument = manualInvoiceDocuments.FirstOrDefault(document => document.OrderId == candidate.Order.Id);
            var manualDeliveryState = manualDocument is null ? null : manualInvoiceDeliveryStates
                .Where(state => state.DocumentId == manualDocument.Id && state.PackageId == candidate.Package.Id)
                .OrderByDescending(state => state.UpdatedAt).FirstOrDefault();
            var issue = invoiceReadIssues.GetValueOrDefault($"trendyol-invoice-package-read:{candidate.Package.ConnectionId}:{candidate.Package.Id}");
            if (issue == default
                && orderSnapshotsForItem is not null
                && !string.IsNullOrWhiteSpace(orderSnapshotsForItem.ExternalOrderId))
                invoiceReadIssues.TryGetValue($"order-invoice-reconciliation:{orderSnapshotsForItem.ConnectionId}:{orderSnapshotsForItem.ExternalOrderId}", out issue);
            var hasReadIssue = issue != default;
            var shouldDisplayReadIssue = hasReadIssue && InvoiceWorkspaceReadIssuePolicy.ShouldDisplay(issue.Code);
            if (!shouldDisplayReadIssue) issue = default;
            var hasFiscalDocument = candidate.Invoice is not null && invoiceDocumentIds.Contains(candidate.Invoice.Id);
            var deliveryStatus = manualDeliveryState?.Status
                ?? deliveryState?.Status
                ?? deliveryAttempt?.Status
                ?? (candidate.Package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Invoiced ? "REMOTE_PRESENT_UNMATCHED" : "NOT_SENT");
            var currentPlatformInvoiceUrl = packageDetailsForItem?.MarketplaceInvoiceUrl ?? candidate.Package.MarketplaceInvoiceUrl;
            // Hepsiburada does not return the invoice link, but once our own delivery is confirmed the platform holds exactly the link we sent.
            if (string.IsNullOrWhiteSpace(currentPlatformInvoiceUrl) && candidate.Invoice is not null && deliveryState?.Status == "CONFIRMED"
                && deliveredLinks.TryGetValue(candidate.Invoice.Id, out var deliveredLink))
                currentPlatformInvoiceUrl = deliveredLink;
            var allowedActions = InvoiceWorkspaceActionPolicy.Resolve(
                candidate.Connection.PlatformCode,
                candidate.Package.MarketplaceInvoiceStatus,
                candidate.Invoice is not null,
                candidate.Invoice?.Status,
                hasFiscalDocument,
                manualDocument is not null,
                candidate.CanCreateInvoice,
                hasReadIssue);
            return new InvoiceWorkspaceItemView(
                candidate.Order.Id,
                candidate.Package.Id,
                orderSnapshotsForItem?.OrderNumber ?? candidate.Order.OrderNumber,
                customerName,
                orderSnapshotsForItem?.OrderedAt ?? candidate.Order.OrderedAt,
                candidate.Package.Status.ToString().ToUpperInvariant(),
                candidate.DeliveredAt,
                candidate.InvoiceDueAt,
                candidate.IsDueSoon,
                orderSnapshotsForItem?.Currency ?? string.Empty,
                packageDetailsForItem is null
                    ? candidate.Amount
                    : packageDetailsForItem.NetAmount > 0 ? packageDetailsForItem.NetAmount : orderSnapshotsForItem?.NetAmount ?? candidate.Amount,
                orderLines.Count,
                primaryImage,
                candidate.Package.CargoProviderExternalId,
                packageDetailsForItem?.CargoTrackingNumber ?? candidate.Package.CargoTrackingNumber,
                candidate.Invoice?.Id,
                candidate.InvoiceStatus,
                ResolveInvoiceNumber(candidate.Invoice?.InvoiceNumber, packageDetailsForItem?.MarketplaceInvoiceNumber ?? candidate.Package.MarketplaceInvoiceNumber),
                candidate.CanCreateInvoice,
                orderSnapshotsForItem?.ShipmentAddressSnapshotJson ?? candidate.Order.ShipmentAddressSnapshotJson,
                orderSnapshotsForItem?.InvoiceAddressSnapshotJson ?? candidate.Order.InvoiceAddressSnapshotJson,
                workspaceLines,
                candidate.Invoice?.LastErrorCode,
                deliveryStatus,
                deliveryState?.ExternalReference ?? deliveryAttempt?.ExternalReference,
                hasFiscalDocument,
                candidate.Connection.PlatformCode,
                candidate.Connection.DisplayName,
                candidate.InvoiceCreationEnabled,
                issue == default ? null : issue.Code,
                issue == default ? null : issue.Summary,
                candidate.Connection.Environment,
                currentPlatformInvoiceUrl,
                manualDocument?.Id,
                manualDocument?.DocumentType,
                manualDocument?.DocumentUrl,
                manualDocument?.InvoiceNumber,
                manualDocument?.IssuedAt,
                manualDeliveryState?.Status,
                manualDeliveryState?.ErrorCode,
                candidate.Invoice?.Status.ToString().ToUpperInvariant() ?? (manualDocument is null ? "NONE" : "MANUAL_DOCUMENT"),
                candidate.Package.MarketplaceInvoiceStatus.ToString().ToUpperInvariant(),
                deliveryStatus,
                currentPlatformInvoiceUrl,
                candidate.Package.MarketplaceInvoiceObservedAt,
                allowedActions,
                previousPlatformReferencesByPackage.GetValueOrDefault(candidate.Package.Id) ?? [],
                orderSnapshotsForItem?.ConnectionId,
                orderSnapshotsForItem?.ExternalOrderId,
                packageDetailsForItem?.ExternalPackageId ?? candidate.Package.ExternalPackageId);
        }).ToList();
    }

    private static InvoiceWorkspacePageView EmptyWorkspacePage(int pageNumber, int pageSize) =>
        new([], 0, pageNumber, pageSize, 1, 0, 0, 0, 0, 0, false, [], [], []);

    private static string WorkspaceInvoiceDisplayStatus(WorkspaceCandidate candidate)
    {
        if (HasMarketplaceInvoiceObservation(candidate)) return "FATURA_KONTROLDE";
        if (candidate.Invoice is null || candidate.CanCreateInvoice || candidate.InvoiceStatus == "FATURA_BEKLIYOR") return "FATURA_BEKLIYOR";
        if (candidate.InvoiceStatus is "FATURA_REDDEDILDI" or "FATURA_BELIRSIZ" or "REJECTED" or "VALIDATION_FAILED" or "MANUAL_REVIEW" or "MARKETPLACE_FAILED") return candidate.InvoiceStatus;
        if (candidate.InvoiceStatus is "FATURA_YUKLENDI" or "FATURA_KESILDI" or "COMPLETED") return "FATURA_KONTROLDE";
        if (candidate.InvoiceStatus == "FATURA_PLATFORMA_BEKLIYOR") return "FATURA_ISLENIYOR";
        return string.IsNullOrWhiteSpace(candidate.InvoiceStatus) ? "FATURA_BILINMIYOR" : candidate.InvoiceStatus;
    }

    private static string WorkspaceInvoiceStatus(
        WorkspacePackageProjection package,
        WorkspaceOrderProjection order,
        WorkspaceInvoiceProjection? invoice,
        WorkspaceConnectionProjection connection)
    {
        switch (package.ManualInvoiceStatus?.Trim().ToUpperInvariant())
        {
            case "UPLOADED": return "FATURA_KONTROLDE";
            case "PENDING": return "FATURA_BEKLIYOR";
        }

        if (invoice is null)
        {
            switch (package.MarketplaceInvoiceStatus)
            {
                case MarketplaceInvoiceStatus.Invoiced: return "FATURA_KONTROLDE";
                case MarketplaceInvoiceStatus.Received: return "FATURA_KONTROLDE";
                case MarketplaceInvoiceStatus.Rejected: return "FATURA_REDDEDILDI";
                case MarketplaceInvoiceStatus.NotInvoiced: return "FATURA_BEKLIYOR";
            }
            return MarketplaceSalesService.InvoiceLabelForPlatform(
                null,
                null,
                package.MarketplaceInvoiceStatus,
                order.CustomerSnapshotJson,
                [package.RawStatus],
                connection.PlatformCode,
                invoice?.HasConfirmedMarketplaceDelivery == true);
        }

        if (invoice.Status is InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending or InvoiceStatus.MarketplaceFailed or InvoiceStatus.Submitted
            || invoice.Status == InvoiceStatus.ManualReview && !string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
        {
            if (invoice.HasConfirmedMarketplaceDelivery) return "FATURA_KONTROLDE";
            // The invoice exists at the provider. It is a delivery problem once the platform leg failed, needs review, or
            // stopped with an error while waiting; until then it is simply on its way.
            var deliveryProblem = invoice.Status is InvoiceStatus.MarketplaceFailed or InvoiceStatus.ManualReview
                || invoice.Status == InvoiceStatus.MarketplacePending && !string.IsNullOrWhiteSpace(invoice.LastErrorCode)
                || InvoiceMarketplaceRetryPolicy.IsDeliveryReview(invoice.Status, invoice.LastErrorCode)
                || InvoiceMarketplaceRetryPolicy.CanRetryDelivery(invoice.Status, invoice.LastErrorCode);
            return deliveryProblem ? "FATURA_PLATFORMA_AKTARILMADI" : "FATURA_ISLENIYOR";
        }
        if (invoice.Status is InvoiceStatus.Submitting or InvoiceStatus.UnknownResult)
            return FiscalSubmissionLabel(invoice.Status, invoice.LastErrorCode, invoice.ExternalReference);
        if (invoice.Status == InvoiceStatus.Completed)
            return "FATURA_KONTROLDE";

        return MarketplaceSalesService.InvoiceLabelForPlatform(
            invoice?.Status,
            invoice?.InvoiceNumber,
            package.MarketplaceInvoiceStatus,
            order.CustomerSnapshotJson,
            [package.RawStatus],
            connection.PlatformCode,
            invoice?.HasConfirmedMarketplaceDelivery == true);
    }

    // A submission the provider refused before creating anything is an error the operator can retry, not
    // work in progress; a submission whose answer was lost is uncertain, not rejected.
    internal static string FiscalSubmissionLabel(InvoiceStatus status, string? lastErrorCode, string? externalReference) => status switch
    {
        InvoiceStatus.UnknownResult => "FATURA_BELIRSIZ",
        InvoiceStatus.Submitting when IsStoppedBeforeProvider(status, lastErrorCode, externalReference) => "FATURA_REDDEDILDI",
        _ => "FATURA_ISLENIYOR"
    };

    private static bool WorkspaceMatchesInvoiceAction(WorkspaceCandidate candidate, string? filter, bool providerHasCredential)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Equals("ALL", StringComparison.OrdinalIgnoreCase)) return true;
        var retryable = candidate.InvoiceStatus is "FATURA_REDDEDILDI" or "FATURA_PLATFORMA_AKTARILMADI" or "REJECTED" or "VALIDATION_FAILED" or "MANUAL_REVIEW" or "MARKETPLACE_FAILED";
        var available = candidate.InvoiceCreationEnabled
            && (candidate.Connection.PlatformCode == "SHOPIFY"
                ? candidate.CanCreateInvoice
                : providerHasCredential && (candidate.Invoice is null ? candidate.CanCreateInvoice : retryable));
        return filter.Equals("CREATABLE", StringComparison.OrdinalIgnoreCase) ? available
            : filter.Equals("NOT_CREATABLE", StringComparison.OrdinalIgnoreCase) && !available;
    }

    private sealed record WorkspacePackageProjection(
        Guid Id, string ExternalPackageId, Guid OrderId, Guid ConnectionId, string? CargoProviderExternalId, string? CargoTrackingNumber,
        ShipmentPackageStatus Status, string RawStatus, DateTimeOffset StatusOccurredAt,
        MarketplaceInvoiceStatus MarketplaceInvoiceStatus, string? MarketplaceInvoiceNumber, string? MarketplaceInvoiceUrl,
        DateTimeOffset? MarketplaceInvoiceObservedAt, string? ManualInvoiceStatus);
    private sealed record WorkspaceOrderProjection(
        Guid Id, string OrderNumber, DateTimeOffset OrderedAt,
        string CustomerSnapshotJson, string ShipmentAddressSnapshotJson, string InvoiceAddressSnapshotJson);
    private sealed record WorkspaceConnectionProjection(Guid Id, string PlatformCode, string DisplayName, bool InvoiceCreationEnabled, string Environment);
    private sealed record WorkspaceInvoiceProjection(Guid Id, Guid OrderId, Guid? PackageId, InvoiceStatus Status, string? InvoiceNumber, string? LastErrorCode, string? ExternalReference, DateTimeOffset CreatedAt, string? CurrentDeliveryStateStatus, bool HasConfirmedHistoricalAttempt)
    {
        public bool HasConfirmedMarketplaceDelivery => MarketplaceInvoiceDeliveryEvidencePolicy.HasConfirmedDelivery(CurrentDeliveryStateStatus, HasConfirmedHistoricalAttempt);
    }
    private sealed record WorkspaceCandidateSource(WorkspacePackageProjection Package, WorkspaceOrderProjection Order);
    private sealed record WorkspaceScanCursor(DateTimeOffset StatusOccurredAt, Guid PackageId);
    private sealed record WorkspaceCandidate(
        WorkspacePackageProjection Package, WorkspaceOrderProjection Order, WorkspaceConnectionProjection Connection,
        WorkspaceInvoiceProjection? Invoice, string InvoiceStatus, bool CanCreateInvoice, bool IsDueSoon, DateTimeOffset? DeliveredAt,
        DateTimeOffset? InvoiceDueAt, decimal Amount, string CustomerName, bool InvoiceCreationEnabled)
    {
        public string? InvoiceNumber => ResolveInvoiceNumber(Invoice?.InvoiceNumber, Package.MarketplaceInvoiceNumber);
    }
    private sealed record WorkspacePageCounts(int Uninvoiced, int Invoiced, int DueSoon, int Total);

    private static string MediaUrl(Guid assetId, string classification, string relativePath) => classification == "PRODUCT_MEDIA_URL"
        ? relativePath
        : $"/api/v1/files/product-media/{assetId:D}/content";

    internal static string? ResolveInvoiceNumber(string? invoiceNumber, string? marketplaceInvoiceNumber) =>
        !string.IsNullOrWhiteSpace(marketplaceInvoiceNumber) ? marketplaceInvoiceNumber : !string.IsNullOrWhiteSpace(invoiceNumber) ? invoiceNumber : null;

    // The invoice detail is about the fiscal document itself: its own number comes first, so a different
    // number seen on the marketplace is not hidden behind it (the workspace lists the marketplace's).
    internal static string? DetailInvoiceNumber(string? invoiceNumber, string? marketplaceInvoiceNumber) =>
        !string.IsNullOrWhiteSpace(invoiceNumber) ? invoiceNumber : !string.IsNullOrWhiteSpace(marketplaceInvoiceNumber) ? marketplaceInvoiceNumber : null;

    // The issuing deadline of an order-level sale invoice: InvoiceDueDays after the first delivered package
    // (the stricter one); none before anything is delivered.
    internal static DateTimeOffset? InvoiceIssueDueAt(IEnumerable<(ShipmentPackageStatus Status, DateTimeOffset StatusOccurredAt)> activePackages)
    {
        var delivered = activePackages.Where(item => item.Status == ShipmentPackageStatus.Delivered).Select(item => item.StatusOccurredAt).ToArray();
        return delivered.Length == 0 ? null : delivered.Min().AddDays(DashboardMetricPolicy.InvoiceDueDays);
    }

    internal static string InvoiceWorkspaceCustomerName(string customerJson, string invoiceAddressJson, string shipmentAddressJson = "{}")
    {
        static string? Find(JsonElement element, params string[] names)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) && property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.Array or JsonValueKind.Object))
                    {
                        var text = property.Value.ToString(); if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                    var nested = Find(property.Value, names); if (!string.IsNullOrWhiteSpace(nested)) return nested;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) { var nested = Find(item, names); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
            return null;
        }
        static JsonDocument ParseOrEmpty(string json)
        {
            try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
            catch (JsonException) { return JsonDocument.Parse("{}"); }
        }
        static string? AddressName(JsonElement address)
        {
            var first = Find(address, "recipientFirstName", "customerFirstName", "firstName");
            var last = Find(address, "recipientLastName", "customerLastName", "lastName");
            var full = string.Join(' ', new[] { first, last }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return !string.IsNullOrWhiteSpace(full)
                ? full
                : Find(address, "fullName", "recipientName", "customerName", "buyerName", "name", "company", "companyName");
        }
        using var customer = ParseOrEmpty(customerJson);
        var first = Find(customer.RootElement, "customerFirstName", "firstName");
        var last = Find(customer.RootElement, "customerLastName", "lastName");
        var full = string.Join(' ', new[] { first, last }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (!string.IsNullOrWhiteSpace(full)) return full;
        var customerName = Find(customer.RootElement, "fullName", "name", "customerName", "recipientName", "buyerName");
        if (!string.IsNullOrWhiteSpace(customerName)) return customerName;
        using var invoiceAddress = ParseOrEmpty(invoiceAddressJson);
        var invoiceName = AddressName(invoiceAddress.RootElement);
        if (!string.IsNullOrWhiteSpace(invoiceName)) return invoiceName;
        using var shipmentAddress = ParseOrEmpty(shipmentAddressJson);
        return AddressName(shipmentAddress.RootElement) ?? "—";
    }

    public async Task<ServiceResult<InvoiceDetailView>> CreateDraftAsync(Guid tenantId, CreateInvoiceCommand command, string idempotencyKey, CancellationToken cancellationToken)
    {
        // An adjustment would still be sent as a full SATIS e-Archive outside the order's one-invoice guard,
        // so a second fiscal invoice for the same goods could reach the tax authority. Refused until a real
        // IADE flow (IADE type, original number/date, accepted original) exists.
        if (command.OriginalInvoiceId is not null)
            return Refuse<InvoiceDetailView>(InvoiceIssuePolicy.AdjustmentFlowUnavailable);
        var existing = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null) return await GetAsync(tenantId, existing.Id, cancellationToken);
        await using var mutationTransaction = command.OriginalInvoiceId is null && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;
        if (command.OriginalInvoiceId is null)
        {
            if (!await InvoiceOrderMutationLock.AcquireAsync(db, tenantId, command.OrderId, cancellationToken))
                return ServiceResult<InvoiceDetailView>.Fail("ORDER_NOT_FOUND", "Sipariş bulunamadı.", 404);
            existing = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existing is not null)
            {
                if (mutationTransaction is not null) await mutationTransaction.CommitAsync(cancellationToken);
                return await GetAsync(tenantId, existing.Id, cancellationToken);
            }
            if (await db.ManualOrderInvoiceDocuments.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.OrderId == command.OrderId && x.Status == "ACTIVE", cancellationToken))
                return ServiceResult<InvoiceDetailView>.Fail("MANUAL_INVOICE_DOCUMENT_ALREADY_EXISTS", "Bu sipariş için manuel fatura belgesi zaten kayıtlı. İkinci mali fatura oluşturulamaz; mevcut belgeyi platforma iletin.", 409);
            var duplicateCount = await db.Invoices.AsNoTracking().CountAsync(x => x.TenantId == tenantId
                && x.OrderId == command.OrderId && x.OriginalInvoiceId == null && x.SequencePurpose == "SALE", cancellationToken);
            if (duplicateCount > 0)
                return ServiceResult<InvoiceDetailView>.Fail("INVOICE_ALREADY_EXISTS", duplicateCount == 1
                    ? "Bu sipariş için mali satış faturası zaten kayıtlı; ikinci fatura oluşturulamaz."
                    : "Bu siparişte birden fazla eski mali fatura kaydı var. Kayıtlar korunuyor ve yeni kesim inceleme tamamlanana kadar kapalı.", 409);
        }
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == command.OrderId, cancellationToken);
        if (order is null) return Invalid<InvoiceDetailView>("orderId", "Sipariş bulunamadı.");
        ShipmentPackage? selectedPackage = null;
        if (command.PackageId is { } packageId)
        {
            selectedPackage = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId && x.OrderId == command.OrderId, cancellationToken);
            if (selectedPackage is null) return Invalid<InvoiceDetailView>("packageId", "Paket siparişe ait değil.");
            if (command.OriginalInvoiceId is null
                && (DashboardMetricPolicy.InvoiceExcludedOrderStatuses.Contains(order.DerivedStatus)
                    || !DashboardMetricPolicy.IsInvoiceEligiblePackage(selectedPackage.Status))) return ServiceResult<InvoiceDetailView>.Fail("INVOICE_PACKAGE_NOT_ELIGIBLE", "İptal edilmiş sipariş/paket için satış faturası oluşturulamaz.", 422);
        }
        else if (command.OriginalInvoiceId is null
            && (DashboardMetricPolicy.InvoiceExcludedOrderStatuses.Contains(order.DerivedStatus)
                || !await db.ShipmentPackages.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.OrderId == command.OrderId && x.Status != ShipmentPackageStatus.Cancelled, cancellationToken)))
        {
            return ServiceResult<InvoiceDetailView>.Fail("INVOICE_PACKAGE_NOT_ELIGIBLE", "Satış faturası için siparişte uygun paket bulunamadı.", 422);
        }
        var provider = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == command.ProviderConnectionId && x.PlatformCode == "TRENDYOL_EFATURAM", cancellationToken);
        if (provider is null) return Invalid<InvoiceDetailView>("billing", "Aktif fatura bağlantısı bulunamadı.");
        var orderConnection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == order.ConnectionId, cancellationToken);
        if (orderConnection is null) return Invalid<InvoiceDetailView>("billing", "Siparişin pazaryeri bağlantısı bulunamadı.");
        var orderPlatform = orderConnection.PlatformCode;
        var activePackagesForOrder = await db.ShipmentPackages.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OrderId == order.Id && x.Status != ShipmentPackageStatus.Cancelled)
            .ToListAsync(cancellationToken);
        var invoiceType = InvoiceAmounts.TrendyolInvoiceType(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson);
        if (InvoiceIssuePolicy.Refusal(invoiceType, order.Currency, order.CustomerSnapshotJson) is { } refusal)
            return Refuse<InvoiceDetailView>(refusal);
        if (command.OriginalInvoiceId is null && invoiceType == "EARSIVFATURA")
        {
            if (!IsValidFiscalDeliveryPackage(selectedPackage is null ? null : new(
                    selectedPackage.ExternalPackageId,
                    selectedPackage.CargoProviderExternalId,
                    selectedPackage.StatusOccurredAt)))
                return ServiceResult<InvoiceDetailView>.Fail("INVOICE_DELIVERY_PACKAGE_REQUIRED", "E-Arşiv için gönderim tarihi ve E-Faturam kataloğunda doğrulanmış kargo firması gerekli. Sipariş paketini yeniden eşitleyin.", 422);
        }
        if (command.OriginalInvoiceId is null && activePackagesForOrder.Any(item => item.ConnectionId != order.ConnectionId))
            return ServiceResult<InvoiceDetailView>.Fail("INVOICE_PACKAGE_CONNECTION_MISMATCH", "Sipariş paketleri farklı pazaryeri bağlantılarına bağlı; yeni mali fatura kesilmedi.", 409);
        if (command.OriginalInvoiceId is null && activePackagesForOrder.Any(item => item.MarketplaceInvoiceStatus is MarketplaceInvoiceStatus.Invoiced or MarketplaceInvoiceStatus.Received))
            return ServiceResult<InvoiceDetailView>.Fail("MARKETPLACE_INVOICE_ALREADY_PRESENT", "Pazaryerinde fatura bulundu veya işleniyor. Yeni mali fatura kesimi kapatıldı.", 409);
        if (ActiveIntegrationScope.IsMarketplace(orderPlatform) && !MarketplaceInvoiceCreationPolicy.IsEnabled(orderPlatform, orderConnection.SettingsJson))
            return ServiceResult<InvoiceDetailView>.Fail(MarketplaceInvoiceCreationPolicy.DisabledErrorCode, MarketplaceInvoiceCreationPolicy.DisabledMessage, 422);
        var profile = await ProviderManagedProfile(tenantId, provider.Id, cancellationToken);
        var policy = await ManualPackagePolicy(tenantId, provider.Id, cancellationToken);
        if (command.OriginalInvoiceId is { } originalId && !await db.Invoices.AnyAsync(x => x.TenantId == tenantId && x.Id == originalId, cancellationToken)) return Invalid<InvoiceDetailView>("originalInvoiceId", "Orijinal fatura bulunamadı.");

        var orderLines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        if (orderLines.Count == 0) return Invalid<InvoiceDetailView>("orderId", "Fatura taslağı için sipariş satırı gerekir.");
        Dictionary<Guid, decimal>? packageQuantities = null;
        if (command.OriginalInvoiceId is not null && command.PackageId is { } selectedPackageId)
        {
            var activePackageIds = await db.ShipmentPackages.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.OrderId == order.Id && x.Status != ShipmentPackageStatus.Cancelled)
                .Select(x => x.Id).ToListAsync(cancellationToken);
            var allPackageAllocations = await db.PackageLineAllocations.AsNoTracking()
                .Where(x => x.TenantId == tenantId && activePackageIds.Contains(x.PackageId)).ToListAsync(cancellationToken);
            if (!TryResolvePackageInvoiceLines(orderLines, allPackageAllocations, selectedPackageId, activePackageIds.Count,
                    out orderLines, out packageQuantities, out var allocationError))
                return ServiceResult<InvoiceDetailView>.Fail("INVOICE_PACKAGE_LINE_ALLOCATION_INVALID", allocationError!, 422);
        }
        else
        {
            var allocations = await db.PackageLineAllocations.AsNoTracking()
                .Where(x => x.TenantId == tenantId && activePackagesForOrder.Select(item => item.Id).Contains(x.PackageId)).ToListAsync(cancellationToken);
            if (!TryResolveWholeOrderInvoiceLines(orderLines, allocations, activePackagesForOrder.Select(item => item.Id).ToArray(), out orderLines, out var allocationError))
                return ServiceResult<InvoiceDetailView>.Fail("INVOICE_PACKAGE_LINE_ALLOCATION_INVALID", allocationError!, 422);
        }
        var now = timeProvider.GetUtcNow(); var invoice = new Invoice
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OrderId = order.Id,
            PackageId = command.OriginalInvoiceId is null ? null : command.PackageId,
            OrderFiscalGuarded = command.OriginalInvoiceId is null,
            ProviderConnectionId = provider.Id,
            LegalEntityProfileId = profile.Id,
            InvoicePolicyId = policy.Id,
            InvoiceType = invoiceType,
            SequencePurpose = command.OriginalInvoiceId is null ? "SALE" : "ADJUSTMENT",
            Currency = order.Currency,
            Note = string.Empty,
            IncludeInternetSalesInfo = command.IncludeInternetSalesInfo,
            DueAt = InvoiceIssueDueAt(activePackagesForOrder.Select(item => (item.Status, item.StatusOccurredAt))),
            IdempotencyKey = idempotencyKey,
            OriginalInvoiceId = command.OriginalInvoiceId,
            Status = InvoiceStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
        var activePackages = activePackagesForOrder;
        var orderTotals = InvoiceAmounts.ResolveOrderTotals(
            order.NetAmount,
            order.GrossAmount,
            order.DiscountAmount,
            activePackages.Select(item => new InvoicePackageTotalSource(item.NetAmount, item.GrossAmount, item.DiscountAmount)).ToArray());
        var remotePayable = orderTotals.PayableAmount;
        var packageGrossAmount = orderTotals.GrossAmount;
        var packageDiscountAmount = orderTotals.DiscountAmount;
        if (command.OriginalInvoiceId is not null && command.PackageId is { } billedPackageId)
        {
            var billedPackage = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == billedPackageId).Select(x => new { x.GrossAmount, x.DiscountAmount, x.NetAmount, x.OrderId }).SingleAsync(cancellationToken);
            if (billedPackage.NetAmount > 0) remotePayable = billedPackage.NetAmount;
            packageGrossAmount = billedPackage.GrossAmount;
            packageDiscountAmount = billedPackage.DiscountAmount;
            if (billedPackage.NetAmount <= 0 && provider.PlatformCode != "SHOPIFY" && await db.ShipmentPackages.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.OrderId == billedPackage.OrderId, cancellationToken) != 1)
                return Invalid<InvoiceDetailView>("packageId", "Paket toplamı henüz Trendyol'dan doğrulanmadı; siparişi yeniden eşitleyin.");
        }
        else if (remotePayable <= 0 && provider.PlatformCode != "SHOPIFY")
        {
            return Invalid<InvoiceDetailView>("orderId", "Sipariş toplamı ve paket toplamları doğrulanamadı; mali fatura kesilmeden önce siparişi yeniden eşitleyin.");
        }
        var sourceLines = orderLines.Select(line => new InvoicePackageLineSource(
            line.Id,
            line.TitleSnapshot,
            line.Sku,
            packageQuantities?.GetValueOrDefault(line.Id) ?? line.OrderedQuantity - line.CancelledQuantity,
            line.UnitPrice,
            line.VatRate)).ToArray();
        if (!InvoiceAmounts.TryCalculatePackage(sourceLines, packageGrossAmount, packageDiscountAmount, remotePayable, out var amounts, out var calculationError))
            return Invalid<InvoiceDetailView>("orderId", calculationError ?? "Fatura kalemleri paket tutarıyla uyuşmuyor.");
        var lines = amounts!.Lines.Select((line, index) => new InvoiceLine
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            InvoiceId = invoice.Id,
            OrderLineId = line.OrderLineId,
            LineSequence = index + 1,
            DescriptionSnapshot = line.Description,
            SkuSnapshot = line.Sku,
            UnitSnapshot = "ADET",
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
            DiscountAmount = line.DiscountAmount,
            VatRate = line.VatRate,
            VatAmount = line.VatAmount,
            LineTotal = line.PayableAmount
        }).ToList();
        invoice.TaxExclusiveTotal = amounts.TaxExclusiveTotal;
        invoice.DiscountTotal = amounts.DiscountTotal;
        invoice.TaxTotal = amounts.TaxTotal;
        invoice.PayableTotal = amounts.PayableTotal;
        invoice.Note = InvoiceAmounts.TurkishInvoiceNote(invoice.PayableTotal);
        db.Invoices.Add(invoice); db.InvoiceLines.AddRange(lines);
        var receiverJson = JsonSerializer.Serialize(new
        {
            order.OrderNumber,
            order.OrderedAt,
            order.CustomerSnapshotJson,
            order.InvoiceAddressSnapshotJson,
            order.ShipmentAddressSnapshotJson,
            FiscalDeliveryPackage = selectedPackage is null ? null : new InvoiceFiscalDeliveryPackageSnapshot(
                selectedPackage.ExternalPackageId,
                selectedPackage.CargoProviderExternalId,
                selectedPackage.StatusOccurredAt)
        });
        db.InvoicePartySnapshots.Add(Snapshot(invoice, "RECEIVER", receiverJson, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException postgres
            && postgres.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation
            && postgres.ConstraintName == "IX_invoices_TenantId_OrderId_ActiveSaleFiscal")
        {
            db.ChangeTracker.Clear();
            return ServiceResult<InvoiceDetailView>.Fail("INVOICE_ALREADY_EXISTS",
                "Bu sipariş için eşzamanlı bir mali fatura işlemi oluştu. Kayıt korunuyor; güncel durumu yenileyin ve yeni fatura oluşturmayın.", 409);
        }
        if (mutationTransaction is not null) await mutationTransaction.CommitAsync(cancellationToken);
        return await GetAsync(tenantId, invoice.Id, cancellationToken);
    }

    public async Task<ServiceResult<InvoiceDetailView>> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id
            && db.Orders.Any(order => order.TenantId == tenantId && order.Id == x.OrderId
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == order.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")), cancellationToken);
        if (invoice is null) return NotFound<InvoiceDetailView>();
        var orderNumber = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == invoice.OrderId).Select(x => x.OrderNumber).SingleAsync(cancellationToken);
        var marketplaceInvoiceNumber = await db.ShipmentPackages.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (invoice.PackageId.HasValue ? x.Id == invoice.PackageId.Value : x.OrderId == invoice.OrderId))
            .OrderByDescending(x => x.StatusOccurredAt)
            .Select(x => x.MarketplaceInvoiceNumber)
            .FirstOrDefaultAsync(cancellationToken);
        var lines = await db.InvoiceLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.InvoiceId == id).OrderBy(x => x.LineSequence).Select(x => new InvoiceLineView(x.Id, x.LineSequence, x.DescriptionSnapshot, x.SkuSnapshot, x.UnitSnapshot, x.Quantity, x.UnitPrice, x.DiscountAmount, x.VatRate, x.VatAmount, x.LineTotal)).ToListAsync(cancellationToken);
        var documents = await db.InvoiceDocuments.AsNoTracking().Where(x => x.TenantId == tenantId && x.InvoiceId == id).OrderBy(x => x.CreatedAt).Select(x => new InvoiceDocumentView(x.Id, x.DocumentType, x.Sha256, x.CreatedAt)).ToListAsync(cancellationToken);
        var attempts = await db.InvoiceSubmissionAttempts.AsNoTracking().Where(x => x.TenantId == tenantId && x.InvoiceId == id).OrderBy(x => x.AttemptNumber).Select(x => new InvoiceAttemptView(x.AttemptNumber, x.Outcome, x.ErrorCode, x.StartedAt, x.CompletedAt)).ToListAsync(cancellationToken);
        var deliveries = await db.MarketplaceDeliveries.AsNoTracking().Where(x => x.TenantId == tenantId && x.InvoiceId == id).OrderBy(x => x.AttemptNumber).Select(x => new MarketplaceDeliveryView(x.Id, x.DeliveryType, x.Status, x.ExternalReference, x.ErrorCode, x.CreatedAt)).ToListAsync(cancellationToken);
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invoice.ProviderConnectionId, cancellationToken);
        return ServiceResult<InvoiceDetailView>.Ok(new(invoice.Id, invoice.OrderId, orderNumber, invoice.PackageId, invoice.ProviderConnectionId, invoice.InvoiceType, invoice.SequencePurpose, Status(invoice.Status), invoice.Currency, invoice.TaxExclusiveTotal, invoice.DiscountTotal, invoice.TaxTotal, invoice.PayableTotal, invoice.Note, DetailInvoiceNumber(invoice.InvoiceNumber, marketplaceInvoiceNumber), invoice.EttnUuid, invoice.DueAt, invoice.IssuedAt, invoice.LastErrorCode, lines, documents, attempts, deliveries, await AllowedActions(invoice, connection, cancellationToken), invoice.Version, connection is null || IntegrationRuntimePolicy.RequiresSensitiveConfirmation(connection)));
    }

    public async Task<ServiceResult<InvoiceDetailView>> ValidateAsync(Guid tenantId, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (invoice is null) return NotFound<InvoiceDetailView>(); if (invoice.Version != expectedVersion) return Precondition<InvoiceDetailView>(invoice.Version);
        if (await IsShopifyManualInvoiceAsync(tenantId, invoice, cancellationToken))
            return ServiceResult<InvoiceDetailView>.Fail("SHOPIFY_MANUAL_INVOICE_ONLY", "Shopify belge kayıtları mali taslak olarak doğrulanamaz; panelde yalnızca yüklenen belge ve manuel durum izlenir.", 422);
        if (!InvoiceStateMachine.CanTransition(invoice.Status, InvoiceStatus.Validating)) return ServiceResult<InvoiceDetailView>.Fail("INVOICE_STATE_INVALID", "Fatura mevcut durumdan doğrulanamaz.", 409);
        invoice.Status = InvoiceStatus.Validating; invoice.Version++; invoice.UpdatedAt = timeProvider.GetUtcNow();
        var orderCustomerJson = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == invoice.OrderId)
            .Select(x => x.CustomerSnapshotJson).SingleOrDefaultAsync(cancellationToken);
        var issueRefusal = invoice.OriginalInvoiceId is not null
            ? InvoiceIssuePolicy.AdjustmentFlowUnavailable
            : InvoiceIssuePolicy.Refusal(invoice.InvoiceType, invoice.Currency, orderCustomerJson);
        if (issueRefusal is not null)
        {
            // The draft keeps the reason so the workspace shows it; nothing reaches the provider.
            invoice.Status = InvoiceStatus.ValidationFailed; invoice.LastErrorCode = issueRefusal.Code; invoice.Version++; invoice.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            return Refuse<InvoiceDetailView>(issueRefusal);
        }
        var policy = await db.InvoicePolicies.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == invoice.InvoicePolicyId, cancellationToken);
        var lines = await db.InvoiceLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.InvoiceId == id).ToListAsync(cancellationToken);
        var failures = new List<string>();
        if (lines.Count == 0 || lines.Any(x => x.Quantity <= 0 || x.LineTotal < 0)) failures.Add("INVOICE_LINES_INVALID");
        if (lines.Sum(x => x.LineTotal) != invoice.PayableTotal || invoice.TaxExclusiveTotal + invoice.TaxTotal != invoice.PayableTotal) failures.Add("INVOICE_TOTAL_MISMATCH");
        if (lines.Any(x => x.UnitSnapshot == "UNSPECIFIED" || x.VatRate != 0 && x.VatAmount <= 0)) failures.Add("FISCAL_CALCULATION_AUTHORITY_REQUIRED");
        if (invoice.Note != InvoiceAmounts.TurkishInvoiceNote(invoice.PayableTotal)) failures.Add("INVOICE_NOTE_MISMATCH");
        var providerConnection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invoice.ProviderConnectionId, cancellationToken);
        if (providerConnection is null) failures.Add("ACTIVE_CONNECTION_REQUIRED");
        else if (!IntegrationRuntimePolicy.IsStage(providerConnection) && (Unapproved(policy.RoundingRule) || Unapproved(policy.DueRule) || Unapproved(policy.AdjustmentRule))) failures.Add("FISCAL_POLICY_UNAPPROVED");
        if (invoice.InvoiceType is not ("TEMELFATURA" or "EARSIVFATURA")) failures.Add("INVOICE_TYPE_INVALID");
        InvoiceFiscalDeliveryPackageSnapshot? deliveryPackage = null;
        var receiverSnapshot = await db.InvoicePartySnapshots.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id && x.Role == "RECEIVER", cancellationToken);
        if (receiverSnapshot is not null)
        {
            try
            {
                using var snapshot = JsonDocument.Parse(_partyProtector.Unprotect(receiverSnapshot.ProtectedContent));
                deliveryPackage = ReadFiscalDeliveryPackage(snapshot.RootElement);
                // The identifier checked here is the one the provider payload will carry.
                if (!RecipientTaxIdentityIsValid(invoice.InvoiceType, SnapshotString(snapshot.RootElement, "CustomerSnapshotJson"), SnapshotString(snapshot.RootElement, "InvoiceAddressSnapshotJson")))
                    failures.Add(InvoiceIssuePolicy.RecipientTaxIdInvalidCode);
            }
            catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException)
            {
                failures.Add("INVOICE_PARTY_SNAPSHOT_INVALID");
            }
        }
        if (invoice.InvoiceType == "EARSIVFATURA")
        {
            // Order-level invoices intentionally keep PackageId null so the
            // order's one-invoice guard and all-package marketplace delivery
            // remain intact. E-Archive delivery data is frozen in the receiver
            // snapshot when the invoice is drafted.
            if (deliveryPackage is null && invoice.PackageId is { } packageId)
            {
                var package = await db.ShipmentPackages.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.Id == packageId)
                    .Select(x => new { x.ExternalPackageId, x.CargoProviderExternalId, x.StatusOccurredAt })
                    .SingleOrDefaultAsync(cancellationToken);
                if (package is not null)
                    deliveryPackage = new(package.ExternalPackageId, package.CargoProviderExternalId, package.StatusOccurredAt);
            }

            if (deliveryPackage is null)
                failures.Add("EFATURAM_INTERNET_SALE_PACKAGE_REQUIRED");
            else if (!IsValidFiscalDeliveryPackage(deliveryPackage))
                failures.Add("EFATURAM_CARRIER_CATALOG_MISS");
        }
        invoice.Status = failures.Count == 0 ? InvoiceStatus.Ready : InvoiceStatus.ValidationFailed; invoice.LastErrorCode = failures.FirstOrDefault(); invoice.Version++; invoice.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(tenantId, id, cancellationToken);
    }

    public async Task<ServiceResult<Guid>> EnqueueSubmitAsync(Guid tenantId, Guid id, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (invoice is null) return NotFound<Guid>();
        if (invoice.Version != expectedVersion) return Precondition<Guid>(invoice.Version);
        if (await IsShopifyManualInvoiceAsync(tenantId, invoice, cancellationToken))
            return ServiceResult<Guid>.Fail("SHOPIFY_MANUAL_INVOICE_ONLY", "Shopify manuel belge kayıtları E-Faturam’a gönderilemez.", 422);
        if (await IsMarketplaceInvoiceCreationDisabledAsync(tenantId, invoice.OrderId, cancellationToken))
            return ServiceResult<Guid>.Fail(MarketplaceInvoiceCreationPolicy.DisabledErrorCode, MarketplaceInvoiceCreationPolicy.DisabledMessage, 422);
        // Drafts validated before these gates existed must not reach the provider either.
        var submitCustomerJson = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == invoice.OrderId)
            .Select(x => x.CustomerSnapshotJson).SingleOrDefaultAsync(cancellationToken);
        if ((invoice.OriginalInvoiceId is not null ? InvoiceIssuePolicy.AdjustmentFlowUnavailable : InvoiceIssuePolicy.Refusal(invoice.InvoiceType, invoice.Currency, submitCustomerJson)) is { } submitRefusal)
            return Refuse<Guid>(submitRefusal);
        var safePreProviderRetry = CanRetryPreProviderFailure(invoice.Status, invoice.LastErrorCode, invoice.ExternalReference);
        if (invoice.Status != InvoiceStatus.Ready && !safePreProviderRetry)
            return ServiceResult<Guid>.Fail("INVOICE_STATE_INVALID", "Fatura mevcut durumdan E-Faturam gönderimine geçemez.", 409);
        if (invoice.Status == InvoiceStatus.Submitting)
        {
            // A retryable refusal (sign-in, quota) leaves its own job scheduled; a second job would race it.
            var needle = invoice.Id.ToString();
            var activeSubmitJobId = await db.IntegrationJobs.AsNoTracking()
                .Where(job => job.TenantId == tenantId && (job.JobType == InvoicingJobTypes.InvoiceSubmit || job.JobType == InvoicingJobTypes.StageCapabilityProbe)
                    && job.PayloadJson.Contains(needle) && (job.Status == JobStatus.Pending || job.Status == JobStatus.Leased || job.Status == JobStatus.RetryScheduled))
                .Select(job => (Guid?)job.Id).FirstOrDefaultAsync(cancellationToken);
            if (activeSubmitJobId is { } activeJobId) return ServiceResult<Guid>.Ok(activeJobId);
        }
        if (!await WriteGates(tenantId, invoice.ProviderConnectionId, InvoicingCapabilities.InvoiceSubmit, cancellationToken)) return CapabilityUnknown<Guid>(InvoicingCapabilities.InvoiceSubmit);
        var now = timeProvider.GetUtcNow();
        var previousErrorCode = invoice.LastErrorCode;
        invoice.Status = InvoiceStatus.Submitting;
        invoice.LastErrorCode = null;
        invoice.IssuedAt ??= now;
        invoice.UpdatedAt = now;
        invoice.Version++;
        if (safePreProviderRetry)
            db.AuditLogs.Add(new AuditLog { TenantId = tenantId, Action = "EFATURAM_PRE_PROVIDER_RETRY_ENQUEUED", TargetType = "Invoice", TargetId = invoice.Id.ToString("D"), Reason = $"no-external-reference:{previousErrorCode}", CorrelationId = correlationId, CreatedAt = now });
        return await AddJob(invoice, InvoicingJobTypes.InvoiceSubmit, idempotencyKey, correlationId, cancellationToken);
    }
    public async Task<ServiceResult<Guid>> EnqueueStageCapabilityProbeAsync(Guid tenantId, Guid id, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (invoice is null) return NotFound<Guid>();
        if (invoice.Version != expectedVersion) return Precondition<Guid>(invoice.Version);
        if (await IsShopifyManualInvoiceAsync(tenantId, invoice, cancellationToken)) return ServiceResult<Guid>.Fail("SHOPIFY_MANUAL_INVOICE_ONLY", "Shopify manuel belge kayıtlarında provider testi yapılamaz.", 422);
        if (await IsMarketplaceInvoiceCreationDisabledAsync(tenantId, invoice.OrderId, cancellationToken))
            return ServiceResult<Guid>.Fail(MarketplaceInvoiceCreationPolicy.DisabledErrorCode, MarketplaceInvoiceCreationPolicy.DisabledMessage, 422);
        var safeReplay = IsSafeStageReplay(invoice.Status, invoice.LastErrorCode, invoice.ExternalReference);
        if (invoice.Status != InvoiceStatus.Ready && !safeReplay || invoice.InvoiceType != "EARSIVFATURA" || !string.IsNullOrWhiteSpace(invoice.ExternalReference)) return ServiceResult<Guid>.Fail("STAGE_INVOICE_FIXTURE_INVALID", "Canary yalnız gönderilmemiş Ready taslakta veya kesin dış referanssız Stage kimlik doğrulama sonucuyla duran aynı taslakta çalışır.", 409);
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invoice.ProviderConnectionId && x.PlatformCode == "TRENDYOL_EFATURAM", cancellationToken);
        if (connection is null || !string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase) || !string.Equals(connection.ExternalStoreId, "Ravencia - Ravencia", StringComparison.Ordinal)) return ServiceResult<Guid>.Fail("STAGE_INVOICE_FIXTURE_REQUIRED", "Canary yalnız sabitlenmiş E-Faturam Stage test hesabındaki faturada çalışır.", 422);
        invoice.Status = InvoiceStatus.Submitting; invoice.IssuedAt ??= timeProvider.GetUtcNow(); invoice.UpdatedAt = timeProvider.GetUtcNow(); invoice.Version++;
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, Action = "EFATURAM_STAGE_CAPABILITY_PROBE_ENQUEUED", TargetType = "Invoice", TargetId = invoice.Id.ToString("D"), Reason = safeReplay ? "no-external-reference-authentication-replay" : "auditli-stage-test-order", CorrelationId = correlationId, CreatedAt = timeProvider.GetUtcNow() });
        return await AddJob(invoice, InvoicingJobTypes.StageCapabilityProbe, idempotencyKey, correlationId, cancellationToken);
    }
    public async Task<ServiceResult<Guid>> EnqueueCancellationAsync(Guid tenantId, Guid id, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (invoice is null) return NotFound<Guid>();
        if (await IsShopifyManualInvoiceAsync(tenantId, invoice, cancellationToken)) return ServiceResult<Guid>.Fail("SHOPIFY_MANUAL_INVOICE_ONLY", "Shopify manuel belge kayıtlarında dış iptal işlemi yapılamaz.", 422);
        if (invoice.InvoiceType != "EARSIVFATURA") return ServiceResult<Guid>.Fail("EINVOICE_CANCELLATION_WORKFLOW_REQUIRED", "Bu otomatik iptal servisi yalnız E-Arşiv faturalar içindir; E-Fatura için mevzuata uygun itiraz/iptal süreci manuel yürütülmelidir.", 422);
        return await EnqueueWrite(tenantId, id, expectedVersion, idempotencyKey, correlationId, InvoicingJobTypes.InvoiceCancellation, InvoicingCapabilities.InvoiceCancel, [InvoiceStatus.Accepted, InvoiceStatus.Completed, InvoiceStatus.AdjustmentRequired], InvoiceStatus.CancellationPending, cancellationToken);
    }

    public async Task<ServiceResult<Guid>> EnqueueReconcileAsync(Guid tenantId, Guid id, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (invoice is null) return NotFound<Guid>();
        if (await IsShopifyManualInvoiceAsync(tenantId, invoice, cancellationToken)) return ServiceResult<Guid>.Fail("SHOPIFY_MANUAL_INVOICE_ONLY", "Shopify manuel belge kayıtları için dış provider eşitlemesi kullanılamaz.", 422);
        if (invoice.Status is not (InvoiceStatus.UnknownResult or InvoiceStatus.Submitted or InvoiceStatus.MarketplacePending or InvoiceStatus.MarketplaceFailed or InvoiceStatus.CancellationPending)) return ServiceResult<Guid>.Fail("INVOICE_STATE_INVALID", "Bu durumda provider reconciliation çalıştırılamaz.", 409);
        // Without a provider reference the status query cannot run; the job would only be rejected.
        if (!CanReconcileWithProvider(invoice.Status, invoice.ExternalReference))
            return ServiceResult<Guid>.Fail("INVOICE_REMOTE_REFERENCE_MISSING", "Sağlayıcıdan fatura referansı alınmadığı için durum sorgulanamaz. E-Faturam portalında bu faturanın olmadığını doğruladıysanız 'Sağlayıcıda Fatura Yok, Yeniden Gönder' işlemini kullanın.", 409);
        if (!await ReadGate(tenantId, invoice.ProviderConnectionId, InvoicingCapabilities.InvoiceStatusRead, cancellationToken)) return CapabilityUnknown<Guid>(InvoicingCapabilities.InvoiceStatusRead);
        return await AddJob(invoice, InvoicingJobTypes.InvoiceReconcile, idempotencyKey, correlationId, cancellationToken);
    }

    public async Task<ServiceResult<Guid>> EnqueueDeliveryAsync(Guid tenantId, Guid id, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (invoice is null) return NotFound<Guid>();
        if (await IsShopifyManualInvoiceAsync(tenantId, invoice, cancellationToken)) return ServiceResult<Guid>.Fail("SHOPIFY_MANUAL_INVOICE_ONLY", "Shopify manuel belge kayıtları pazaryeri fatura aktarımına gönderilemez.", 422);
        if (!InvoiceMarketplaceRetryPolicy.CanRetryDelivery(invoice.Status, invoice.LastErrorCode)) return ServiceResult<Guid>.Fail("INVOICE_STATE_INVALID", "Fatura pazaryerine iletime hazır değil.", 409);
        if (!await db.InvoiceDocuments.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id && x.PermanentUrl != null, cancellationToken)) return ServiceResult<Guid>.Fail("INVOICE_PERMANENT_LINK_REQUIRED", "Trendyol iletimi için kalıcı HTTPS fatura bağlantısı henüz hazır değil.", 409);
        var packages = await db.ShipmentPackages.AsNoTracking()
            .Where(item => item.TenantId == tenantId && (invoice.PackageId == null
                ? item.OrderId == invoice.OrderId && item.Status != ShipmentPackageStatus.Cancelled
                : item.Id == invoice.PackageId.Value))
            .OrderBy(item => item.Id).ToListAsync(cancellationToken);
        if (packages.Count == 0) return Invalid<Guid>("packageId", "Faturaya bağlı etkin pazaryeri paketi bulunamadı.");
        var marketplaceConnectionIds = packages.Select(item => item.ConnectionId).Distinct().ToArray();
        var environments = await db.PlatformConnections.AsNoTracking()
            .Where(connection => connection.TenantId == tenantId && (connection.Id == invoice.ProviderConnectionId || marketplaceConnectionIds.Contains(connection.Id)))
            .Select(connection => new { connection.Id, connection.Environment })
            .ToDictionaryAsync(connection => connection.Id, connection => connection.Environment, cancellationToken);
        environments.TryGetValue(invoice.ProviderConnectionId, out var providerEnvironment);
        foreach (var connectionId in marketplaceConnectionIds)
        {
            environments.TryGetValue(connectionId, out var marketplaceEnvironment);
            if (!InvoiceDeliveryEnvironmentPolicy.IsCompatible(providerEnvironment, marketplaceEnvironment))
                return ServiceResult<Guid>.Fail(InvoiceDeliveryEnvironmentPolicy.MismatchErrorCode,
                    InvoiceDeliveryEnvironmentPolicy.DescribeMismatch(providerEnvironment, marketplaceEnvironment), 409);
            if (!await WriteGates(tenantId, connectionId, InvoicingCapabilities.InvoiceDeliver, cancellationToken)) return CapabilityUnknown<Guid>(InvoicingCapabilities.InvoiceDeliver);
        }
        var manualDeliveryRetry = invoice.LastErrorCode == InvoiceMarketplaceRetryPolicy.RepeatedRemoteFailure;
        var packageIds = packages.Select(item => item.Id).ToArray();
        var deliveryStates = await db.MarketplaceDeliveryStates
            .Where(item => item.TenantId == tenantId && item.InvoiceId == id && packageIds.Contains(item.PackageId!.Value))
            .ToListAsync(cancellationToken);
        if (manualDeliveryRetry)
        {
            foreach (var state in deliveryStates.Where(state => state.Status == "FAILED" && state.ErrorCode == InvoiceMarketplaceRetryPolicy.RepeatedRemoteFailure))
            {
                state.Status = "UNKNOWN";
                state.UpdatedAt = timeProvider.GetUtcNow();
                state.Version++;
            }
        }
        invoice.Status = InvoiceStatus.MarketplacePending; invoice.UpdatedAt = timeProvider.GetUtcNow(); invoice.Version++;
        return await AddJob(invoice, InvoicingJobTypes.MarketplaceDelivery, idempotencyKey, correlationId, cancellationToken, packages[0].ConnectionId, manualDeliveryRetry);
    }

    public async Task<ServiceResult<(Stream Content, string MimeType, string FileName)>> OpenDocumentAsync(Guid tenantId, Guid invoiceId, Guid documentId, CancellationToken cancellationToken)
    {
        var row = await (from document in db.InvoiceDocuments.AsNoTracking()
                         join asset in db.FileAssets.AsNoTracking() on new { document.TenantId, Id = document.FileAssetId } equals new { asset.TenantId, asset.Id }
                         where document.TenantId == tenantId && document.InvoiceId == invoiceId && document.Id == documentId
                            && db.Invoices.Any(invoice => invoice.TenantId == tenantId && invoice.Id == document.InvoiceId
                                && db.Orders.Any(order => order.TenantId == tenantId && order.Id == invoice.OrderId
                                    && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == order.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))
                                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == invoice.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))
                         select new { asset.RelativePath, asset.MimeType, asset.OriginalNameSafe }).SingleOrDefaultAsync(cancellationToken);
        return row is null ? NotFound<(Stream, string, string)>() : ServiceResult<(Stream, string, string)>.Ok((await files.OpenReadAsync(tenantId, row.RelativePath, cancellationToken), row.MimeType, row.OriginalNameSafe ?? $"invoice-{documentId:N}"));
    }

    private async Task<ServiceResult<Guid>> EnqueueWrite(Guid tenantId, Guid id, long expectedVersion, string idempotencyKey, string correlationId, string jobType, string capability, InvoiceStatus[] states, InvoiceStatus next, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (invoice is null) return NotFound<Guid>(); if (invoice.Version != expectedVersion) return Precondition<Guid>(invoice.Version);
        if (await IsShopifyManualInvoiceAsync(tenantId, invoice, cancellationToken)) return ServiceResult<Guid>.Fail("SHOPIFY_MANUAL_INVOICE_ONLY", "Shopify manuel belge kayıtlarında dış fatura işlemi yapılamaz.", 422);
        if (!states.Contains(invoice.Status) || !InvoiceStateMachine.CanTransition(invoice.Status, next)) return ServiceResult<Guid>.Fail("INVOICE_STATE_INVALID", "Fatura mevcut durumdan bu işleme geçemez.", 409);
        if (!await WriteGates(tenantId, invoice.ProviderConnectionId, capability, cancellationToken)) return CapabilityUnknown<Guid>(capability);
        invoice.Status = next; invoice.UpdatedAt = timeProvider.GetUtcNow(); if (jobType == InvoicingJobTypes.InvoiceSubmit) invoice.IssuedAt ??= invoice.UpdatedAt; invoice.Version++; return await AddJob(invoice, jobType, idempotencyKey, correlationId, cancellationToken);
    }

    private async Task<ServiceResult<Guid>> AddJob(Invoice invoice, string jobType, string idempotencyKey, string correlationId, CancellationToken cancellationToken, Guid? connectionId = null, bool manualDeliveryRetry = false)
    {
        var dedup = $"{jobType}:{invoice.Id}:{idempotencyKey}"; var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == invoice.TenantId && x.JobType == jobType && x.JobDedupKey == dedup, cancellationToken); if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var payload = JsonSerializer.Serialize(new { invoiceId = invoice.Id, manualDeliveryRetry }); var job = new IntegrationJob { Id = Guid.CreateVersion7(), TenantId = invoice.TenantId, ConnectionId = connectionId ?? invoice.ProviderConnectionId, JobType = jobType, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = $"{jobType}:{invoice.Id}:{idempotencyKey}", Priority = InvoiceJobPriority(jobType), AvailableAt = timeProvider.GetUtcNow(), CorrelationId = correlationId, Version = 1 };
        db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(job.Id);
    }

    internal static int InvoiceJobPriority(string jobType) => jobType is InvoicingJobTypes.InvoiceSubmit or InvoicingJobTypes.MarketplaceDelivery ? JobPriorityPolicy.UserAction : 0;

    private async Task<IReadOnlyList<string>> AllowedActions(Invoice invoice, PlatformConnection? connection, CancellationToken cancellationToken)
    {
        if (ShopifyManualInvoicePolicy.IsManualOnly(connection?.PlatformCode, invoice.SequencePurpose)
            || await IsShopifyManualInvoiceAsync(invoice.TenantId, invoice, cancellationToken)) return [];
        var actions = new List<string>(); if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.ValidationFailed) actions.Add("VALIDATE");
        if ((invoice.Status == InvoiceStatus.Ready || CanRetryPreProviderFailure(invoice.Status, invoice.LastErrorCode, invoice.ExternalReference)) && await WriteGates(invoice.TenantId, invoice.ProviderConnectionId, InvoicingCapabilities.InvoiceSubmit, cancellationToken)) actions.Add("SUBMIT");
        if (AllowsStageCapabilityProbe(invoice.Status, invoice.LastErrorCode, invoice.ExternalReference, invoice.InvoiceType, connection)) actions.Add("STAGE_CAPABILITY_PROBE");
        if (invoice.Status is (InvoiceStatus.UnknownResult or InvoiceStatus.Submitted) && CanReconcileWithProvider(invoice.Status, invoice.ExternalReference) && await ReadGate(invoice.TenantId, invoice.ProviderConnectionId, InvoicingCapabilities.InvoiceStatusRead, cancellationToken)) actions.Add("RECONCILE");
        if (invoice.Status is (InvoiceStatus.Accepted or InvoiceStatus.MarketplaceFailed))
        {
            var packageConnections = await db.ShipmentPackages.AsNoTracking()
                .Where(item => item.TenantId == invoice.TenantId && (invoice.PackageId == null
                    ? item.OrderId == invoice.OrderId && item.Status != ShipmentPackageStatus.Cancelled
                    : item.Id == invoice.PackageId.Value))
                .Select(item => item.ConnectionId).Distinct().ToListAsync(cancellationToken);
            var permanentLinkReady = await db.InvoiceDocuments.AsNoTracking().AnyAsync(x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && x.PermanentUrl != null, cancellationToken);
            var providerEnvironment = await db.PlatformConnections.AsNoTracking().Where(item => item.TenantId == invoice.TenantId && item.Id == invoice.ProviderConnectionId).Select(item => item.Environment).SingleOrDefaultAsync(cancellationToken);
            var allConnectionsReady = packageConnections.Count > 0;
            foreach (var packageConnectionId in packageConnections)
            {
                var environment = await db.PlatformConnections.AsNoTracking().Where(item => item.TenantId == invoice.TenantId && item.Id == packageConnectionId).Select(item => item.Environment).SingleOrDefaultAsync(cancellationToken);
                allConnectionsReady &= InvoiceDeliveryEnvironmentPolicy.IsCompatible(providerEnvironment, environment)
                    && await WriteGates(invoice.TenantId, packageConnectionId, InvoicingCapabilities.InvoiceDeliver, cancellationToken);
            }
            if (permanentLinkReady && allConnectionsReady) actions.Add("DELIVER");
        }
        if (invoice.InvoiceType == "EARSIVFATURA" && invoice.Status is (InvoiceStatus.Accepted or InvoiceStatus.Completed or InvoiceStatus.AdjustmentRequired) && await WriteGates(invoice.TenantId, invoice.ProviderConnectionId, InvoicingCapabilities.InvoiceCancel, cancellationToken)) actions.Add("CANCEL");
        if (invoice.Status == InvoiceStatus.CancellationPending && await ReadGate(invoice.TenantId, invoice.ProviderConnectionId, InvoicingCapabilities.InvoiceStatusRead, cancellationToken)) actions.Add("RECONCILE");
        return actions;
    }

    private async Task<bool> IsShopifyManualInvoiceAsync(Guid tenantId, Invoice invoice, CancellationToken cancellationToken)
    {
        if (string.Equals(invoice.SequencePurpose, ShopifyManualInvoicePolicy.TrackingSequencePurpose, StringComparison.OrdinalIgnoreCase)) return true;
        return await db.PlatformConnections.AsNoTracking().AnyAsync(connection => connection.TenantId == tenantId && connection.Id == invoice.ProviderConnectionId && connection.PlatformCode == "SHOPIFY", cancellationToken);
    }

    internal static bool AllowsStageCapabilityProbe(InvoiceStatus status, string? lastErrorCode, string? externalReference, string invoiceType, PlatformConnection? connection)
    {
        return (status == InvoiceStatus.Ready || IsSafeStageReplay(status, lastErrorCode, externalReference))
            && string.Equals(invoiceType, "EARSIVFATURA", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(externalReference)
            && connection is not null
            && string.Equals(connection.PlatformCode, "TRENDYOL_EFATURAM", StringComparison.Ordinal)
            && string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase)
            && string.Equals(connection.ExternalStoreId, "Ravencia - Ravencia", StringComparison.Ordinal);
    }

    private static bool IsSafeStageReplay(InvoiceStatus status, string? lastErrorCode, string? externalReference) =>
        string.IsNullOrWhiteSpace(externalReference)
        && ((status == InvoiceStatus.ManualReview && string.Equals(lastErrorCode, "EFATURAM_TOKEN_SCOPE_MISSING", StringComparison.Ordinal))
            || (status == InvoiceStatus.Submitting && lastErrorCode is "EFATURAM_AUTHENTICATION_FAILED" or "EFATURAM_ACCESS_TOKEN_REJECTED" or "EFATURAM_INVOICE_CREATE_PRIVILEGE_MISSING"));

    internal static bool CanRetryLocalPayloadFailure(InvoiceStatus status, string? lastErrorCode, string? externalReference) =>
        status == InvoiceStatus.Rejected
        && lastErrorCode is "EFATURAM_FISCAL_PAYLOAD_INVALID" or "EFATURAM_REQUEST_REJECTED" or "EFATURAM_APPLICATION_NOT_ACTIVE"
        && string.IsNullOrWhiteSpace(externalReference);

    internal static bool IsValidFiscalDeliveryPackage(InvoiceFiscalDeliveryPackageSnapshot? package) =>
        package is not null
        && package.StatusOccurredAt is { } sentAt
        && sentAt != default
        && TrendyolCarrierCatalog.TryResolve(package.CargoProviderExternalId, out _);

    // A consumer e-Archive carries the fixed 11111111111 identifier; every other recipient must hold a
    // TCKN or VKN whose check digits are right, otherwise the provider or the tax authority rejects it later.
    internal static bool RecipientTaxIdentityIsValid(string invoiceType, string customerSnapshotJson, string invoiceAddressSnapshotJson)
    {
        var (taxId, isCorporate) = TrendyolEFaturamCanonicalPayload.ResolveRecipientTaxIdentity(customerSnapshotJson, invoiceAddressSnapshotJson, invoiceType);
        if (invoiceType == "EARSIVFATURA" && !isCorporate) return true;
        return TurkishTaxIdentity.IsValid(taxId);
    }

    private static string SnapshotString(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "{}"
            : "{}";

    internal static InvoiceFiscalDeliveryPackageSnapshot? ReadFiscalDeliveryPackage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("FiscalDeliveryPackage", out var package)
            || package.ValueKind != JsonValueKind.Object)
            return null;

        var externalPackageId = package.TryGetProperty("ExternalPackageId", out var externalId) && externalId.ValueKind == JsonValueKind.String
            ? externalId.GetString()
            : null;
        var cargoProviderExternalId = package.TryGetProperty("CargoProviderExternalId", out var cargoProvider) && cargoProvider.ValueKind == JsonValueKind.String
            ? cargoProvider.GetString()
            : null;
        DateTimeOffset? statusOccurredAt = package.TryGetProperty("StatusOccurredAt", out var statusDate)
            && statusDate.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(statusDate.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
        return new(externalPackageId, cargoProviderExternalId, statusOccurredAt);
    }

    internal static bool CanRetryPreProviderFailure(InvoiceStatus status, string? lastErrorCode, string? externalReference) =>
        string.IsNullOrWhiteSpace(externalReference)
        && (CanRetryLocalPayloadFailure(status, lastErrorCode, externalReference)
            || IsStoppedBeforeProvider(status, lastErrorCode, externalReference)
            || (status == InvoiceStatus.ManualReview && lastErrorCode is "MARKETPLACE_INVOICE_PRECHECK_FAILED" or "PLATFORM_INVOICE_STATUS_NOT_CLEAR" or OperatorConfirmedNoRemoteInvoiceCode));

    // Submit leaves an invoice in Submitting with an error code only when the provider definitely did not
    // create it: a refusal before or at the fiscal request (authorization, capability, sign-in, quota, gate).
    // Outcome-unknown answers move to UnknownResult instead, and the code is cleared while a request is in flight.
    internal static bool IsStoppedBeforeProvider(InvoiceStatus status, string? lastErrorCode, string? externalReference) =>
        status == InvoiceStatus.Submitting
        && string.IsNullOrWhiteSpace(externalReference)
        && !string.IsNullOrWhiteSpace(lastErrorCode);

    internal static bool CanReconcileWithProvider(InvoiceStatus status, string? externalReference) =>
        !string.IsNullOrWhiteSpace(externalReference)
        && status is InvoiceStatus.UnknownResult or InvoiceStatus.Submitted or InvoiceStatus.MarketplacePending or InvoiceStatus.MarketplaceFailed or InvoiceStatus.CancellationPending;

    internal const string OperatorConfirmedNoRemoteInvoiceCode = "OPERATOR_CONFIRMED_NO_REMOTE_INVOICE";

    private async Task<bool> IsMarketplaceInvoiceCreationDisabledAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        var settings = await db.Orders.AsNoTracking()
            .Where(order => order.TenantId == tenantId && order.Id == orderId)
            .Join(db.PlatformConnections.AsNoTracking(), order => order.ConnectionId, connection => connection.Id,
                (_, connection) => new { connection.PlatformCode, connection.SettingsJson })
            .SingleOrDefaultAsync(cancellationToken);
        return settings is not null
            && ActiveIntegrationScope.IsMarketplace(settings.PlatformCode)
            && !MarketplaceInvoiceCreationPolicy.IsEnabled(settings.PlatformCode, settings.SettingsJson);
    }

    private async Task<bool> WriteGates(Guid tenantId, Guid connectionId, string capability, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        if (connection is null) return false;
        if (string.Equals(capability, InvoicingCapabilities.InvoiceDeliver, StringComparison.Ordinal))
        {
            if (!configuration.GetValue<bool>("FeatureFlags:InvoiceMarketplaceDeliveryWrites")) return false;
            if (string.Equals(connection.PlatformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
            {
                var evidence = await db.PlatformCapabilities.AsNoTracking()
                    .Where(item => item.TenantId == tenantId && item.ConnectionId == connectionId && item.Code == capability)
                    .ToListAsync(cancellationToken);
                if (evidence.Count != 1 || !CapabilityEvidencePolicy.IsVerifiedWriteCapability(evidence[0], connection, capability)) return false;
            }
        }
        // Hepsiburada invoice delivery is gated by the connection's external write switch like the other marketplaces; the adapter itself
        // reads the package before and after the write, so it needs no separate hand-entered capability evidence.
        if (string.Equals(connection.PlatformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(capability, InvoicingCapabilities.InvoiceDeliver, StringComparison.Ordinal))
        {
            var evidence = await db.PlatformCapabilities.AsNoTracking().AnyAsync(x =>
                x.TenantId == tenantId
                && x.ConnectionId == connectionId
                && x.Code == capability
                && x.SupportLevel == CapabilitySupportLevel.Supported
                && x.Environment == connection.Environment
                && x.StoreScope == connection.ExternalStoreId
                && x.VerifiedAt != null
                && x.FixtureChecksum != null,
                cancellationToken);
            if (!evidence) return false;
        }
        var enabled = ConnectionWritesEnabled(connection.SettingsJson);
        var manual = new AdapterContext(tenantId, connectionId, "runtime-gate", "runtime-gate", timeProvider.GetUtcNow());
        return IntegrationRuntimePolicy.AllowsManualWrite(connection, manual, configuration.GetValue<bool>("FeatureFlags:ExternalWrites"), enabled);
    }
    private async Task<string> DeliveryBlockedReasonAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        if (connection is null) return "Pazaryeri bağlantısı bulunamadı.";
        if (!configuration.GetValue<bool>("FeatureFlags:InvoiceMarketplaceDeliveryWrites")) return "Sunucuda pazaryerine fatura iletme anahtarı kapalı.";
        if (!configuration.GetValue<bool>("FeatureFlags:ExternalWrites")) return "Sunucudaki genel dış yazma anahtarı kapalı.";
        if (!IntegrationRuntimePolicy.IsActive(connection)) return $"{connection.DisplayName} bağlantısı aktif değil.";
        if (!ConnectionWritesEnabled(connection.SettingsJson)) return $"{connection.DisplayName} bağlantısında dış yazma kapalı. Entegrasyonlar sayfasından dış yazma anahtarını açın.";
        return "Bu pazaryeri için fatura iletme izni doğrulanamadı.";
    }
    private async Task<bool> ReadGate(Guid tenantId, Guid connectionId, string capability, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        return connection is not null && IntegrationRuntimePolicy.AllowsManualRead(connection);
    }
    private static bool ConnectionWritesEnabled(string settings)
    {
        try { return JsonDocument.Parse(settings).RootElement.TryGetProperty("ExternalWritesEnabled", out var value) && value.ValueKind == JsonValueKind.True; }
        catch (JsonException) { return false; }
    }
    private InvoicePartySnapshot Snapshot(Invoice invoice, string role, string content, DateTimeOffset now) => new() { Id = Guid.CreateVersion7(), TenantId = invoice.TenantId, InvoiceId = invoice.Id, Role = role, ProtectedContent = _partyProtector.Protect(content), ContentHash = Hash(content), CreatedAt = now };
    private Guid Decode(string? cursor) => cursors.TryDecode(cursor, out var id) ? id : throw new ArgumentException("Cursor geçersiz veya süresi dolmuş.", nameof(cursor));
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static bool Unapproved(string value) => value is "UNKNOWN" or "UNAPPROVED";
    private static string Status(InvoiceStatus value) => value.ToString().ToUpperInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static long EventSequence(string sourceEventId) => long.TryParse(sourceEventId[(sourceEventId.LastIndexOf(':') + 1)..], out var value) ? value : 0;
    private static bool TryResolvePackageInvoiceLines(
        IReadOnlyList<OrderLine> sourceLines,
        IReadOnlyList<PackageLineAllocation> allocationEvents,
        Guid selectedPackageId,
        int activePackageCount,
        out List<OrderLine> invoiceLines,
        out Dictionary<Guid, decimal>? quantities,
        out string? error)
    {
        quantities = null;
        error = null;
        var remainingByLine = sourceLines.ToDictionary(line => line.Id, line => line.OrderedQuantity - line.CancelledQuantity);
        if (remainingByLine.Values.Any(remaining => remaining < 0))
        {
            invoiceLines = [];
            error = "Sipariş satırındaki iptal miktarı satın alınan adetten büyük. Paket dağılımı düzeltilmeden fatura kesilmedi.";
            return false;
        }

        var latest = allocationEvents
            .GroupBy(item => new { item.PackageId, item.OrderLineId })
            .Select(group => group.OrderByDescending(item => EventSequence(item.SourceEventId))
                .ThenByDescending(item => item.SourceEventId, StringComparer.Ordinal).First())
            .ToArray();
        if (latest.Any(item => !remainingByLine.ContainsKey(item.OrderLineId)))
        {
            invoiceLines = [];
            error = "Paket dağılımında siparişe ait olmayan ürün satırı var. Yeniden eşitlemeden fatura kesilmedi.";
            return false;
        }
        if (latest.Any(item => item.AllocatedQuantity < 0 || item.AllocatedQuantity > remainingByLine[item.OrderLineId]))
        {
            invoiceLines = [];
            error = "Paket ürün adedi kalan sipariş adediyle uyuşmuyor. Dağılım düzeltilmeden fatura kesilmedi.";
            return false;
        }
        if (latest.GroupBy(item => item.OrderLineId).Any(group => group.Sum(item => item.AllocatedQuantity) > remainingByLine[group.Key]))
        {
            invoiceLines = [];
            error = "Paketler arasındaki ürün adetleri sipariş miktarını aşıyor. Dağılım düzeltilmeden fatura kesilmedi.";
            return false;
        }

        var selectedQuantities = latest.Where(item => item.PackageId == selectedPackageId)
            .ToDictionary(item => item.OrderLineId, item => item.AllocatedQuantity);
        var selectedLines = sourceLines.Where(line => remainingByLine[line.Id] > 0 && selectedQuantities.GetValueOrDefault(line.Id) > 0).ToList();
        if (selectedLines.Count > 0)
        {
            quantities = selectedQuantities;
            invoiceLines = selectedLines;
            return true;
        }
        if (activePackageCount > 1)
        {
            invoiceLines = [];
            error = "Sipariş birden fazla pakete ayrılmış ancak seçili paketin ürün dağılımı kayıtlı değil. Tekrar eşitlemeden fatura kesilmedi.";
            return false;
        }

        invoiceLines = sourceLines.Where(line => remainingByLine[line.Id] > 0).ToList();
        if (invoiceLines.Count == 0)
        {
            error = "Seçilen pakette faturalanabilir ürün satırı bulunamadı.";
            return false;
        }
        return true;
    }

    private static bool TryResolveWholeOrderInvoiceLines(
        IReadOnlyList<OrderLine> sourceLines,
        IReadOnlyList<PackageLineAllocation> allocationEvents,
        IReadOnlyCollection<Guid> activePackageIds,
        out List<OrderLine> invoiceLines,
        out string? error)
    {
        error = null;
        var remainingByLine = sourceLines.ToDictionary(line => line.Id, line => line.OrderedQuantity - line.CancelledQuantity);
        if (remainingByLine.Values.Any(remaining => remaining < 0))
        {
            invoiceLines = [];
            error = "Sipariş satırındaki iptal miktarı satın alınan adetten büyük. Paket dağılımı düzeltilmeden fatura kesilmedi.";
            return false;
        }

        var activeIds = activePackageIds.ToHashSet();
        var latest = allocationEvents.Where(item => activeIds.Contains(item.PackageId))
            .GroupBy(item => new { item.PackageId, item.OrderLineId })
            .Select(group => group.OrderByDescending(item => EventSequence(item.SourceEventId))
                .ThenByDescending(item => item.SourceEventId, StringComparer.Ordinal).First()).ToArray();
        if (latest.Any(item => !remainingByLine.ContainsKey(item.OrderLineId)
            || item.AllocatedQuantity < 0 || item.AllocatedQuantity > remainingByLine.GetValueOrDefault(item.OrderLineId)))
        {
            invoiceLines = [];
            error = "Paket ürün adetleri sipariş satırlarıyla uyuşmuyor. Dağılım düzeltilmeden fatura kesilmedi.";
            return false;
        }
        if (activeIds.Count > 1 && sourceLines.Any(line =>
            latest.Where(item => item.OrderLineId == line.Id).Sum(item => item.AllocatedQuantity) != remainingByLine[line.Id]))
        {
            invoiceLines = [];
            error = "Çok paketli siparişte paketlere dağıtılan miktarlar sipariş satırlarını tam karşılamıyor. Yeniden eşitlemeden fatura kesilmedi.";
            return false;
        }

        invoiceLines = sourceLines.Where(line => remainingByLine[line.Id] > 0).ToList();
        if (invoiceLines.Count > 0) return true;
        error = "Siparişte faturalanabilir pozitif miktarlı kalem bulunamadı.";
        return false;
    }

    private async Task<LegalEntityProfile> ProviderManagedProfile(Guid tenantId, Guid providerConnectionId, CancellationToken cancellationToken)
    {
        var existing = await db.LegalEntityProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Title == $"EFATURAM_PROVIDER:{providerConnectionId:N}", cancellationToken);
        if (existing is not null) return existing;
        var now = timeProvider.GetUtcNow(); var profile = new LegalEntityProfile { Id = Guid.CreateVersion7(), TenantId = tenantId, Title = $"EFATURAM_PROVIDER:{providerConnectionId:N}", ProtectedTaxId = _taxProtector.Protect("PROVIDER_MANAGED"), MaskedTaxId = "E-Faturam", AddressSnapshotJson = "{}", ContactSnapshotJson = "{}", Status = "ACTIVE", CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.LegalEntityProfiles.Add(profile); await db.SaveChangesAsync(cancellationToken); return profile;
    }
    private async Task<InvoicePolicy> ManualPackagePolicy(Guid tenantId, Guid providerConnectionId, CancellationToken cancellationToken)
    {
        var existing = await db.InvoicePolicies.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProviderConnectionId == providerConnectionId, cancellationToken);
        if (existing is not null) return existing;
        var now = timeProvider.GetUtcNow(); var policy = new InvoicePolicy { Id = Guid.CreateVersion7(), TenantId = tenantId, ProviderConnectionId = providerConnectionId, TriggerState = "MANUAL_CONFIRMED", PackageScope = "SHIPMENT_PACKAGE", DueRule = "IMMEDIATE", RoundingRule = "LINE_HALF_AWAY_FROM_ZERO", AdjustmentRule = "REJECT_OVER_ONE_KURUS", AutoSubmit = false, CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.InvoicePolicies.Add(policy); await db.SaveChangesAsync(cancellationToken); return policy;
    }
    private static InvoicePolicyView Map(InvoicePolicy value) => new(value.Id, value.ProviderConnectionId, value.TriggerState, value.PackageScope, value.DueRule, value.RoundingRule, value.AdjustmentRule, value.AutoSubmit, value.Version);
    private static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    private static ServiceResult<T> Refuse<T>(InvoiceIssueRefusal refusal) => ServiceResult<T>.Fail(refusal.Code, refusal.Message, 422);
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Precondition<T>(long version) => ServiceResult<T>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{version}.", 412);
    private static ServiceResult<T> PreconditionRequired<T>() => ServiceResult<T>.Fail("PRECONDITION_REQUIRED", "Mevcut kayıt için If-Match gereklidir.", 428);
    private static ServiceResult<T> CapabilityUnknown<T>(string capability) => ServiceResult<T>.Fail("EXTERNAL_WRITE_NOT_ENABLED", "Bu bağlantıda dış yazma işlemi etkin değil.", 422);

}

internal sealed record InvoiceFiscalDeliveryPackageSnapshot(string? ExternalPackageId, string? CargoProviderExternalId, DateTimeOffset? StatusOccurredAt);
