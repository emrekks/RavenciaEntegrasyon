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
    TimeProvider timeProvider) : IInvoicingBillingService
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
            .Select(x => new { x.InvoiceId, x.Status, x.ExternalReference, x.UpdatedAt })
            .ToListAsync(cancellationToken);
        var deliveryAttempts = await db.MarketplaceDeliveries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && invoiceIds.Contains(x.InvoiceId))
            .Select(x => new { x.InvoiceId, x.Status, x.ExternalReference, x.AttemptNumber })
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
            var invoiceStatus = package.ManualInvoiceStatus?.Trim().ToUpperInvariant() switch
            {
                "UPLOADED" => "FATURA_YUKLENDI",
                "PENDING" => "FATURA_BEKLIYOR",
                _ => MarketplaceSalesService.InvoiceLabelForPlatform(invoice, package.MarketplaceInvoiceStatus, order.CustomerSnapshotJson, [package.RawStatus], connection?.PlatformCode)
            };
            if (!DashboardMetricPolicy.IsInvoiceEligiblePackage(package.Status)
                || !DashboardMetricPolicy.IsInvoiceEligibleOrder(order.DerivedStatus)) return null;
            var deliveredAt = package.Status == ShipmentPackageStatus.Delivered ? package.StatusOccurredAt : (DateTimeOffset?)null;
            var dueAt = deliveredAt?.AddDays(7);
            var dueSoon = invoiceStatus == "FATURA_BEKLIYOR" && deliveredAt is not null && now >= deliveredAt.Value.AddDays(5);
            var image = orderLines.Select(line => ResolveVariantId(line) is { } variantId ? mediaByVariant.GetValueOrDefault(variantId) ?? mediaByProduct.GetValueOrDefault(variantProductIds.GetValueOrDefault(variantId)) : null).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            var customerName = InvoiceWorkspaceCustomerName(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson);
            var workspaceLines = orderLines.Select(line => new InvoiceWorkspaceLineView(line.Sku, line.Barcode, line.TitleSnapshot, OrderLinePresentationPolicy.ActiveQuantity(line.OrderedQuantity, line.CancelledQuantity), line.UnitPrice, line.VatRate, ResolveVariantId(line) is { } variantId ? mediaByVariant.GetValueOrDefault(variantId) ?? mediaByProduct.GetValueOrDefault(variantProductIds.GetValueOrDefault(variantId)) : null)).ToList();
            var deliveryState = invoice is null
                ? null
                : deliveryStates.Where(x => x.InvoiceId == invoice.Id).OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
            var deliveryAttempt = invoice is null
                ? null
                : deliveryAttempts.Where(x => x.InvoiceId == invoice.Id).OrderByDescending(x => x.AttemptNumber).FirstOrDefault();
            var invoiceReadIssue = invoiceReadIssues.GetValueOrDefault($"trendyol-invoice-package-read:{package.ConnectionId}:{package.Id}")
                ?? invoiceReadIssues.GetValueOrDefault($"order-invoice-reconciliation:{order.ConnectionId}:{order.ExternalOrderId}");
            return new InvoiceWorkspaceItemView(order.Id, package.Id, order.OrderNumber, customerName, order.OrderedAt, package.Status.ToString().ToUpperInvariant(), deliveredAt, dueAt, dueSoon, order.Currency, package.NetAmount > 0 ? package.NetAmount : order.NetAmount, orderLines.Count, image, package.CargoProviderExternalId, package.CargoTrackingNumber, invoice?.Id, invoiceStatus, ResolveInvoiceNumber(invoice?.InvoiceNumber, package.MarketplaceInvoiceNumber), invoiceStatus == "FATURA_BEKLIYOR", order.ShipmentAddressSnapshotJson, order.InvoiceAddressSnapshotJson, workspaceLines, invoice?.LastErrorCode, deliveryState?.Status ?? deliveryAttempt?.Status, deliveryState?.ExternalReference ?? deliveryAttempt?.ExternalReference, invoice is not null && invoiceDocumentIds.Contains(invoice.Id), connection?.PlatformCode ?? "TRENDYOL", connection?.DisplayName ?? "Trendyol", connection is not null && MarketplaceInvoiceCreationPolicy.IsEnabled(connection.PlatformCode, connection.SettingsJson), invoiceReadIssue?.Code, invoiceReadIssue?.Summary);
        }).Where(x => x is not null).Select(x => x!).ToList();
    }

    public async Task<InvoiceWorkspaceSummaryView> WorkspaceSummaryAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var workspaceConnections = await ReadWorkspaceConnectionsAsync(tenantId, cancellationToken);
        if (workspaceConnections.Count == 0) return new(0);

        var now = timeProvider.GetUtcNow();
        var dueSoonCount = 0;
        WorkspaceScanCursor? cursor = null;
        while (true)
        {
            var candidates = await ReadWorkspaceCandidateBatchAsync(tenantId, workspaceConnections, cursor, now, includeCustomerName: false, cancellationToken);
            if (candidates.Count == 0) break;
            foreach (var candidate in candidates)
            {
                if (candidate.Package.Status == ShipmentPackageStatus.Delivered
                    && now >= candidate.Package.StatusOccurredAt.AddDays(DashboardMetricPolicy.InvoiceReminderStartDays)
                    && candidate.InvoiceStatus == "FATURA_BEKLIYOR") dueSoonCount++;
            }
            var last = candidates[^1];
            cursor = new(last.Package.StatusOccurredAt, last.Package.Id);
        }

        return new(dueSoonCount);
    }

    public async Task<InvoiceWorkspacePageView> WorkspacePageAsync(Guid tenantId, InvoiceWorkspacePageQuery request, CancellationToken cancellationToken)
    {
        var pageSize = request.PageSize is 20 or 50 or 100 or 200 ? request.PageSize : 20;
        var requestedPage = Math.Max(1, request.PageNumber);
        var normalizedTab = request.Tab.Trim().ToUpperInvariant() is "INVOICED" or "DUE_SOON"
            ? request.Tab.Trim().ToUpperInvariant()
            : "UNINVOICED";
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
        WorkspaceScanCursor? cursor = null;

        while (true)
        {
            var candidates = await ReadWorkspaceCandidateBatchAsync(tenantId, workspaceConnections, cursor, now, includeCustomerName: searchKey is not null, cancellationToken);
            if (candidates.Count == 0) break;

            foreach (var candidate in candidates)
            {
                var platformSelected = selectedPlatforms.Count == 0 || selectedPlatforms.Contains(candidate.Connection.PlatformCode);
                if (platformSelected)
                {
                    selectedTotal++;
                    if (candidate.CanCreateInvoice || candidate.InvoiceStatus == "FATURA_REDDEDILDI") selectedUninvoiced++;
                    else selectedInvoiced++;
                    if (candidate.IsDueSoon) selectedDueSoon++;
                    if (!string.Equals(candidate.Connection.PlatformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase)
                        && (candidate.CanCreateInvoice || candidate.InvoiceStatus == "FATURA_REDDEDILDI"))
                        hasPendingMarketplaceInvoices = true;
                }

                shipmentStatuses.Add(candidate.Package.Status.ToString().ToUpperInvariant());
                if (!string.IsNullOrWhiteSpace(candidate.Package.CargoProviderExternalId))
                    cargoProviders.Add(candidate.Package.CargoProviderExternalId.Trim());
                invoiceStatuses.Add(WorkspaceInvoiceDisplayStatus(candidate));

                if (!MatchesWorkspacePageRequest(candidate, normalizedTab, request, searchKey, from, to)) continue;
                var resultIndex = filteredCount++;
                if (resultIndex >= offset && pageCandidates.Count < pageSize) pageCandidates.Add(candidate);
                lastPageCandidates.Enqueue(candidate);
                if (lastPageCandidates.Count > pageSize) lastPageCandidates.Dequeue();
            }

            var last = candidates[^1];
            cursor = new(last.Package.StatusOccurredAt, last.Package.Id);
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(filteredCount / (double)pageSize));
        var pageNumber = Math.Min(requestedPage, totalPages);
        if (pageNumber != requestedPage)
        {
            var lastPageSize = filteredCount % pageSize;
            if (lastPageSize == 0 && filteredCount > 0) lastPageSize = pageSize;
            pageCandidates = lastPageCandidates.TakeLast(lastPageSize).ToList();
        }
        var items = await MaterializeWorkspacePageAsync(tenantId, pageCandidates, cancellationToken);
        return new(items, filteredCount, pageNumber, pageSize, totalPages, selectedUninvoiced, selectedInvoiced, selectedDueSoon, selectedTotal, hasPendingMarketplaceInvoices,
            shipmentStatuses.Order(StringComparer.Ordinal).ToArray(), cargoProviders.Order(StringComparer.OrdinalIgnoreCase).ToArray(), invoiceStatuses.Order(StringComparer.Ordinal).ToArray());
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
                    source.package.OrderId,
                    source.package.ConnectionId,
                    source.package.CargoProviderExternalId,
                    includeCustomerName ? source.package.CargoTrackingNumber : null,
                    source.package.Status,
                    source.package.RawStatus,
                    source.package.StatusOccurredAt,
                    source.package.MarketplaceInvoiceStatus,
                    includeCustomerName ? source.package.MarketplaceInvoiceNumber : null,
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
            .Where(invoice => invoice.TenantId == tenantId && invoice.OriginalInvoiceId == null
                && ((invoice.PackageId != null && packageIds.Contains(invoice.PackageId.Value))
                    || (invoice.PackageId == null && orderIds.Contains(invoice.OrderId)))
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId
                    && connection.Id == invoice.ProviderConnectionId
                    && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))
            .OrderByDescending(invoice => invoice.CreatedAt)
            .Select(invoice => new WorkspaceInvoiceProjection(invoice.Id, invoice.OrderId, invoice.PackageId, invoice.Status, invoice.InvoiceNumber, invoice.LastErrorCode, invoice.CreatedAt))
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
            var invoice = invoicesByPackage.GetValueOrDefault(package.Id) ?? invoicesByOrder.GetValueOrDefault(order.Id);
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
            .Select(connection => new { connection.Id, connection.PlatformCode, connection.DisplayName, connection.SettingsJson })
            .ToListAsync(cancellationToken);

        return connections.ToDictionary(
            connection => connection.Id,
            connection => new WorkspaceConnectionProjection(
                connection.Id,
                connection.PlatformCode,
                connection.DisplayName,
                MarketplaceInvoiceCreationPolicy.IsEnabled(connection.PlatformCode, connection.SettingsJson)));
    }

    private static bool MatchesWorkspacePageRequest(
        WorkspaceCandidate candidate,
        string normalizedTab,
        InvoiceWorkspacePageQuery request,
        string? searchKey,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        var tabMatch = normalizedTab switch
        {
            "INVOICED" => !(candidate.CanCreateInvoice || candidate.InvoiceStatus == "FATURA_REDDEDILDI"),
            "DUE_SOON" => candidate.IsDueSoon,
            _ => candidate.CanCreateInvoice || candidate.InvoiceStatus == "FATURA_REDDEDILDI"
        };
        var shipmentMatch = string.IsNullOrWhiteSpace(request.ShipmentStatus) || request.ShipmentStatus == "ALL"
            || string.Equals(candidate.Package.Status.ToString(), request.ShipmentStatus, StringComparison.OrdinalIgnoreCase);
        var cargoMatch = string.IsNullOrWhiteSpace(request.CargoProviderName) || request.CargoProviderName == "ALL"
            || string.Equals(candidate.Package.CargoProviderExternalId?.Trim(), request.CargoProviderName.Trim(), StringComparison.OrdinalIgnoreCase);
        var invoiceMatch = string.IsNullOrWhiteSpace(request.InvoiceStatus) || request.InvoiceStatus == "ALL"
            || string.Equals(WorkspaceInvoiceDisplayStatus(candidate), request.InvoiceStatus, StringComparison.OrdinalIgnoreCase);
        var actionMatch = WorkspaceMatchesInvoiceAction(candidate, request.InvoiceAction, request.ProviderHasCredential);
        var dateMatch = (!from.HasValue || candidate.Order.OrderedAt >= from.Value)
            && (!to.HasValue || candidate.Order.OrderedAt <= to.Value);
        var searchMatch = searchKey is null || new[] { candidate.Order.OrderNumber, candidate.CustomerName, candidate.InvoiceNumber, candidate.Package.CargoTrackingNumber }
            .Any(value => value?.ToLower(WorkspaceSearchCulture).Contains(searchKey, StringComparison.Ordinal) == true);
        return tabMatch && shipmentMatch && cargoMatch && invoiceMatch && actionMatch && dateMatch && searchMatch;
    }

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
            .Select(package => new { package.Id, package.NetAmount, package.CargoTrackingNumber, package.MarketplaceInvoiceNumber })
            .ToDictionaryAsync(package => package.Id, cancellationToken);
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
        var deliveryStates = invoiceIds.Length == 0
            ? []
            : await db.MarketplaceDeliveryStates.AsNoTracking()
                .Where(state => state.TenantId == tenantId && invoiceIds.Contains(state.InvoiceId))
                .Select(state => new { state.InvoiceId, state.Status, state.ExternalReference, state.UpdatedAt })
                .ToListAsync(cancellationToken);
        var deliveryAttempts = invoiceIds.Length == 0
            ? []
            : await db.MarketplaceDeliveries.AsNoTracking()
                .Where(attempt => attempt.TenantId == tenantId && invoiceIds.Contains(attempt.InvoiceId))
                .Select(attempt => new { attempt.InvoiceId, attempt.Status, attempt.ExternalReference, attempt.AttemptNumber })
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
                .Where(issue => issue.TenantId == tenantId && issue.Status == IssueStatus.Open && invoiceReadIssueKeys.Contains(issue.DedupeKey))
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
                .Where(state => state.InvoiceId == candidate.Invoice.Id).OrderByDescending(state => state.UpdatedAt).FirstOrDefault();
            var deliveryAttempt = candidate.Invoice is null ? null : deliveryAttempts
                .Where(attempt => attempt.InvoiceId == candidate.Invoice.Id).OrderByDescending(attempt => attempt.AttemptNumber).FirstOrDefault();
            var issue = invoiceReadIssues.GetValueOrDefault($"trendyol-invoice-package-read:{candidate.Package.ConnectionId}:{candidate.Package.Id}");
            if (issue == default
                && orderSnapshotsForItem is not null
                && !string.IsNullOrWhiteSpace(orderSnapshotsForItem.ExternalOrderId))
                invoiceReadIssues.TryGetValue($"order-invoice-reconciliation:{orderSnapshotsForItem.ConnectionId}:{orderSnapshotsForItem.ExternalOrderId}", out issue);
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
                deliveryState?.Status ?? deliveryAttempt?.Status,
                deliveryState?.ExternalReference ?? deliveryAttempt?.ExternalReference,
                candidate.Invoice is not null && invoiceDocumentIds.Contains(candidate.Invoice.Id),
                candidate.Connection.PlatformCode,
                candidate.Connection.DisplayName,
                candidate.InvoiceCreationEnabled,
                issue == default ? null : issue.Code,
                issue == default ? null : issue.Summary);
        }).ToList();
    }

    private static InvoiceWorkspacePageView EmptyWorkspacePage(int pageNumber, int pageSize) =>
        new([], 0, pageNumber, pageSize, 1, 0, 0, 0, 0, false, [], [], []);

    private static string WorkspaceInvoiceDisplayStatus(WorkspaceCandidate candidate)
    {
        if (candidate.Invoice is null || candidate.CanCreateInvoice || candidate.InvoiceStatus == "FATURA_BEKLIYOR") return "FATURA_BEKLIYOR";
        if (candidate.InvoiceStatus is "FATURA_REDDEDILDI" or "REJECTED" or "VALIDATION_FAILED" or "MANUAL_REVIEW" or "MARKETPLACE_FAILED") return candidate.InvoiceStatus;
        if (candidate.InvoiceStatus is "FATURA_YUKLENDI" or "FATURA_KESILDI" or "COMPLETED") return candidate.InvoiceStatus;
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
            case "UPLOADED": return "FATURA_YUKLENDI";
            case "PENDING": return "FATURA_BEKLIYOR";
        }

        if (invoice is null)
        {
            switch (package.MarketplaceInvoiceStatus)
            {
                case MarketplaceInvoiceStatus.Invoiced: return "FATURA_KESILDI";
                case MarketplaceInvoiceStatus.Received: return "FATURA_KONTROLDE";
                case MarketplaceInvoiceStatus.Rejected: return "FATURA_REDDEDILDI";
                case MarketplaceInvoiceStatus.NotInvoiced: return "FATURA_BEKLIYOR";
            }
        }

        return MarketplaceSalesService.InvoiceLabelForPlatform(
            invoice?.Status,
            invoice?.InvoiceNumber,
            package.MarketplaceInvoiceStatus,
            order.CustomerSnapshotJson,
            [package.RawStatus],
            connection.PlatformCode);
    }

    private static bool WorkspaceMatchesInvoiceAction(WorkspaceCandidate candidate, string? filter, bool providerHasCredential)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Equals("ALL", StringComparison.OrdinalIgnoreCase)) return true;
        var retryable = candidate.InvoiceStatus is "FATURA_REDDEDILDI" or "REJECTED" or "VALIDATION_FAILED" or "MANUAL_REVIEW" or "MARKETPLACE_FAILED";
        var available = candidate.InvoiceCreationEnabled
            && (candidate.Connection.PlatformCode == "SHOPIFY"
                ? candidate.CanCreateInvoice
                : providerHasCredential && (candidate.Invoice is null ? candidate.CanCreateInvoice : retryable));
        return filter.Equals("CREATABLE", StringComparison.OrdinalIgnoreCase) ? available
            : filter.Equals("NOT_CREATABLE", StringComparison.OrdinalIgnoreCase) && !available;
    }

    private sealed record WorkspacePackageProjection(
        Guid Id, Guid OrderId, Guid ConnectionId, string? CargoProviderExternalId, string? CargoTrackingNumber,
        ShipmentPackageStatus Status, string RawStatus, DateTimeOffset StatusOccurredAt,
        MarketplaceInvoiceStatus MarketplaceInvoiceStatus, string? MarketplaceInvoiceNumber, string? ManualInvoiceStatus);
    private sealed record WorkspaceOrderProjection(
        Guid Id, string OrderNumber, DateTimeOffset OrderedAt,
        string CustomerSnapshotJson, string ShipmentAddressSnapshotJson, string InvoiceAddressSnapshotJson);
    private sealed record WorkspaceConnectionProjection(Guid Id, string PlatformCode, string DisplayName, bool InvoiceCreationEnabled);
    private sealed record WorkspaceInvoiceProjection(Guid Id, Guid OrderId, Guid? PackageId, InvoiceStatus Status, string? InvoiceNumber, string? LastErrorCode, DateTimeOffset CreatedAt);
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
        !string.IsNullOrWhiteSpace(invoiceNumber) ? invoiceNumber : !string.IsNullOrWhiteSpace(marketplaceInvoiceNumber) ? marketplaceInvoiceNumber : null;

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
        var existing = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null) return await GetAsync(tenantId, existing.Id, cancellationToken);
        if (command.OriginalInvoiceId is null)
        {
            var duplicate = command.PackageId is { } requestedPackageId
                ? await db.Invoices.AsNoTracking().Where(x => x.TenantId == tenantId && x.OriginalInvoiceId == null && x.PackageId == requestedPackageId).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken)
                : await db.Invoices.AsNoTracking().Where(x => x.TenantId == tenantId && x.OriginalInvoiceId == null && x.PackageId == null && x.OrderId == command.OrderId).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            if (duplicate is not null) return ServiceResult<InvoiceDetailView>.Fail("INVOICE_ALREADY_EXISTS", "Bu sipariş paketi için daha önce fatura oluşturulmuş; ikinci satış faturası oluşturulamaz.", 409);
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
        var provider = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == command.ProviderConnectionId && (x.PlatformCode == "TRENDYOL_EFATURAM" || x.PlatformCode == "SHOPIFY"), cancellationToken);
        if (provider is null) return Invalid<InvoiceDetailView>("billing", "Aktif fatura bağlantısı bulunamadı.");
        var orderConnection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == order.ConnectionId, cancellationToken);
        if (orderConnection is null) return Invalid<InvoiceDetailView>("billing", "Siparişin pazaryeri bağlantısı bulunamadı.");
        var orderPlatform = orderConnection.PlatformCode;
        if (ActiveIntegrationScope.IsMarketplace(orderPlatform) && !MarketplaceInvoiceCreationPolicy.IsEnabled(orderPlatform, orderConnection.SettingsJson))
            return ServiceResult<InvoiceDetailView>.Fail(MarketplaceInvoiceCreationPolicy.DisabledErrorCode, MarketplaceInvoiceCreationPolicy.DisabledMessage, 422);
        var isShopifyOrder = string.Equals(orderPlatform, "SHOPIFY", StringComparison.OrdinalIgnoreCase);
        if (isShopifyOrder && (provider.Id != order.ConnectionId || !string.Equals(provider.PlatformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase)))
            return Invalid<InvoiceDetailView>("billing", "Shopify siparişleri yalnız Shopify bağlantısına bağlı manuel belge takibinde kullanılabilir; E-Faturam taslağı oluşturulamaz.");
        if (!isShopifyOrder && string.Equals(provider.PlatformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
            return Invalid<InvoiceDetailView>("billing", "Shopify belge takibi yalnızca Shopify bağlantısına ait siparişlerde kullanılabilir.");
        var profile = await ProviderManagedProfile(tenantId, provider.Id, cancellationToken);
        var policy = await ManualPackagePolicy(tenantId, provider.Id, cancellationToken);
        if (command.OriginalInvoiceId is { } originalId && !await db.Invoices.AnyAsync(x => x.TenantId == tenantId && x.Id == originalId, cancellationToken)) return Invalid<InvoiceDetailView>("originalInvoiceId", "Orijinal fatura bulunamadı.");

        if (isShopifyOrder)
        {
            if (command.OriginalInvoiceId is not null)
                return Invalid<InvoiceDetailView>("originalInvoiceId", "Shopify manuel belge kaydı düzeltme faturası oluşturamaz.");

            // Shopify invoices are uploaded by the user, not fiscally composed or submitted here.
            // Keep a status/document tracker instead of making an invalid draft from partial fulfillment lines.
            var manualCreatedAt = timeProvider.GetUtcNow();
            var manualRecord = new Invoice
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OrderId = order.Id,
                PackageId = command.PackageId,
                ProviderConnectionId = provider.Id,
                LegalEntityProfileId = profile.Id,
                InvoicePolicyId = policy.Id,
                InvoiceType = ShopifyManualInvoicePolicy.TrackingSequencePurpose,
                SequencePurpose = ShopifyManualInvoicePolicy.TrackingSequencePurpose,
                Currency = order.Currency,
                TaxExclusiveTotal = 0,
                DiscountTotal = 0,
                TaxTotal = 0,
                PayableTotal = ShopifyManualInvoicePolicy.TrackingAmount(order.NetAmount, selectedPackage?.NetAmount),
                Note = "Shopify manuel belge kaydı. Bu kayıt mali fatura oluşturmaz veya dış sağlayıcıya gönderilmez.",
                IdempotencyKey = idempotencyKey,
                Status = InvoiceStatus.Draft,
                CreatedAt = manualCreatedAt,
                UpdatedAt = manualCreatedAt,
                Version = 1
            };
            db.Invoices.Add(manualRecord);
            var manualReceiverJson = JsonSerializer.Serialize(new { order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson });
            db.InvoicePartySnapshots.Add(Snapshot(manualRecord, "RECEIVER", manualReceiverJson, manualCreatedAt));
            await db.SaveChangesAsync(cancellationToken);
            return await GetAsync(tenantId, manualRecord.Id, cancellationToken);
        }

        var orderLines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        if (orderLines.Count == 0) return Invalid<InvoiceDetailView>("orderId", "Fatura taslağı için sipariş satırı gerekir.");
        Dictionary<Guid, decimal>? packageQuantities = null;
        if (command.PackageId is { } selectedPackageId)
        {
            var packageAllocations = await db.PackageLineAllocations.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.PackageId == selectedPackageId)
                .ToListAsync(cancellationToken);
            var allocatedQuantities = packageAllocations.GroupBy(x => x.OrderLineId)
                .ToDictionary(x => x.Key, x => x.OrderByDescending(y => EventSequence(y.SourceEventId)).First().AllocatedQuantity);
            var allocatedLines = orderLines.Where(x => allocatedQuantities.GetValueOrDefault(x.Id) > 0).ToList();
            if (allocatedLines.Count > 0)
            {
                packageQuantities = allocatedQuantities;
                orderLines = allocatedLines;
            }
            else
            {
                // Legacy package syncs may predate allocation persistence. The package/order ownership
                // was verified above, so retain the positive order lines rather than creating an empty draft.
                orderLines = orderLines.Where(x => x.OrderedQuantity - x.CancelledQuantity > 0).ToList();
                if (orderLines.Count == 0) return Invalid<InvoiceDetailView>("packageId", "Seçilen pakette faturalanabilir sipariş kalemi bulunamadı.");
            }
        }
        else
        {
            orderLines = orderLines.Where(x => x.OrderedQuantity - x.CancelledQuantity > 0).ToList();
            if (orderLines.Count == 0) return Invalid<InvoiceDetailView>("orderId", "Siparişte faturalanabilir pozitif miktarlı kalem bulunamadı.");
        }
        var now = timeProvider.GetUtcNow(); var invoice = new Invoice
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OrderId = order.Id,
            PackageId = command.PackageId,
            ProviderConnectionId = provider.Id,
            LegalEntityProfileId = profile.Id,
            InvoicePolicyId = policy.Id,
            InvoiceType = InvoiceAmounts.TrendyolInvoiceType(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson),
            SequencePurpose = command.OriginalInvoiceId is null ? "SALE" : "ADJUSTMENT",
            Currency = order.Currency,
            Note = string.Empty,
            IdempotencyKey = idempotencyKey,
            OriginalInvoiceId = command.OriginalInvoiceId,
            Status = InvoiceStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
        var lines = orderLines.Select((line, index) =>
        {
            var quantity = packageQuantities?.GetValueOrDefault(line.Id) ?? line.OrderedQuantity - line.CancelledQuantity;
            var includedTotal = decimal.Round(quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
            var amounts = InvoiceAmounts.FromVatIncluded(includedTotal, line.VatRate);
            return new InvoiceLine
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                InvoiceId = invoice.Id,
                OrderLineId = line.Id,
                LineSequence = index + 1,
                DescriptionSnapshot = line.TitleSnapshot,
                SkuSnapshot = line.Sku,
                UnitSnapshot = "ADET",
                Quantity = quantity,
                UnitPrice = decimal.Round(amounts.TaxExclusiveAmount / quantity, 4, MidpointRounding.AwayFromZero),
                DiscountAmount = 0,
                VatRate = line.VatRate,
                VatAmount = amounts.VatAmount,
                LineTotal = amounts.PayableAmount
            };
        }).ToList();
        var calculatedPayable = lines.Sum(x => x.LineTotal);
        var remotePayable = order.NetAmount;
        if (command.PackageId is { } billedPackageId)
        {
            var billedPackage = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == billedPackageId).Select(x => new { x.NetAmount, x.OrderId }).SingleAsync(cancellationToken);
            if (billedPackage.NetAmount > 0) remotePayable = billedPackage.NetAmount;
            else if (provider.PlatformCode != "SHOPIFY" && await db.ShipmentPackages.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.OrderId == billedPackage.OrderId, cancellationToken) != 1)
                return Invalid<InvoiceDetailView>("packageId", "Paket toplamı henüz Trendyol'dan doğrulanmadı; siparişi yeniden eşitleyin.");
        }
        var targetPayable = decimal.Round(remotePayable, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(calculatedPayable - targetPayable) > 0.01m)
            return Invalid<InvoiceDetailView>("orderId", $"Sipariş kalem toplamı ({calculatedPayable:0.00}) ile {(provider.PlatformCode == "SHOPIFY" ? "Shopify sipariş toplamı" : "Trendyol sipariş toplamı")} ({targetPayable:0.00}) eşleşmiyor.");
        if (lines.Count > 0 && calculatedPayable != targetPayable)
        {
            var last = lines[^1]; var difference = targetPayable - calculatedPayable;
            last.LineTotal += difference; last.VatAmount += difference;
        }
        invoice.TaxExclusiveTotal = lines.Sum(x => decimal.Round(x.LineTotal - x.VatAmount, 2, MidpointRounding.AwayFromZero));
        invoice.DiscountTotal = 0; invoice.TaxTotal = lines.Sum(x => x.VatAmount); invoice.PayableTotal = targetPayable;
        invoice.Note = InvoiceAmounts.TurkishInvoiceNote(invoice.PayableTotal);
        db.Invoices.Add(invoice); db.InvoiceLines.AddRange(lines);
        var receiverJson = JsonSerializer.Serialize(new { order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson });
        db.InvoicePartySnapshots.Add(Snapshot(invoice, "RECEIVER", receiverJson, now));
        await db.SaveChangesAsync(cancellationToken);
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
        return ServiceResult<InvoiceDetailView>.Ok(new(invoice.Id, invoice.OrderId, orderNumber, invoice.PackageId, invoice.ProviderConnectionId, invoice.InvoiceType, invoice.SequencePurpose, Status(invoice.Status), invoice.Currency, invoice.TaxExclusiveTotal, invoice.DiscountTotal, invoice.TaxTotal, invoice.PayableTotal, invoice.Note, ResolveInvoiceNumber(invoice.InvoiceNumber, marketplaceInvoiceNumber), invoice.EttnUuid, invoice.DueAt, invoice.IssuedAt, invoice.LastErrorCode, lines, documents, attempts, deliveries, await AllowedActions(invoice, connection, cancellationToken), invoice.Version, connection is null || IntegrationRuntimePolicy.RequiresSensitiveConfirmation(connection)));
    }

    public async Task<ServiceResult<InvoiceDetailView>> ValidateAsync(Guid tenantId, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (invoice is null) return NotFound<InvoiceDetailView>(); if (invoice.Version != expectedVersion) return Precondition<InvoiceDetailView>(invoice.Version);
        if (await IsShopifyManualInvoiceAsync(tenantId, invoice, cancellationToken))
            return ServiceResult<InvoiceDetailView>.Fail("SHOPIFY_MANUAL_INVOICE_ONLY", "Shopify belge kayıtları mali taslak olarak doğrulanamaz; panelde yalnızca yüklenen belge ve manuel durum izlenir.", 422);
        if (!InvoiceStateMachine.CanTransition(invoice.Status, InvoiceStatus.Validating)) return ServiceResult<InvoiceDetailView>.Fail("INVOICE_STATE_INVALID", "Fatura mevcut durumdan doğrulanamaz.", 409);
        invoice.Status = InvoiceStatus.Validating; invoice.Version++; invoice.UpdatedAt = timeProvider.GetUtcNow();
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
        if (invoice.InvoiceType == "EARSIVFATURA")
        {
            if (invoice.PackageId is null) failures.Add("EFATURAM_INTERNET_SALE_PACKAGE_REQUIRED");
            else
            {
                var packageCarrier = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == invoice.PackageId).Select(x => x.CargoProviderExternalId).SingleOrDefaultAsync(cancellationToken);
                if (!TrendyolCarrierCatalog.TryResolve(packageCarrier, out _)) failures.Add("EFATURAM_CARRIER_CATALOG_MISS");
            }
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
        var safePreProviderRetry = CanRetryPreProviderFailure(invoice.Status, invoice.LastErrorCode, invoice.ExternalReference);
        if (invoice.Status != InvoiceStatus.Ready && !safePreProviderRetry)
            return ServiceResult<Guid>.Fail("INVOICE_STATE_INVALID", "Fatura mevcut durumdan E-Faturam gönderimine geçemez.", 409);
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
        return await EnqueueWrite(tenantId, id, expectedVersion, idempotencyKey, correlationId, InvoicingJobTypes.InvoiceCancellation, InvoicingCapabilities.InvoiceCancel, [InvoiceStatus.Accepted, InvoiceStatus.Completed], InvoiceStatus.CancellationPending, cancellationToken);
    }

    public async Task<ServiceResult<Guid>> EnqueueReconcileAsync(Guid tenantId, Guid id, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (invoice is null) return NotFound<Guid>();
        if (await IsShopifyManualInvoiceAsync(tenantId, invoice, cancellationToken)) return ServiceResult<Guid>.Fail("SHOPIFY_MANUAL_INVOICE_ONLY", "Shopify manuel belge kayıtları için dış provider eşitlemesi kullanılamaz.", 422);
        if (invoice.Status is not (InvoiceStatus.UnknownResult or InvoiceStatus.Submitted or InvoiceStatus.MarketplacePending or InvoiceStatus.MarketplaceFailed or InvoiceStatus.CancellationPending)) return ServiceResult<Guid>.Fail("INVOICE_STATE_INVALID", "Bu durumda provider reconciliation çalıştırılamaz.", 409);
        if (!await ReadGate(tenantId, invoice.ProviderConnectionId, InvoicingCapabilities.InvoiceStatusRead, cancellationToken)) return CapabilityUnknown<Guid>(InvoicingCapabilities.InvoiceStatusRead);
        return await AddJob(invoice, InvoicingJobTypes.InvoiceReconcile, idempotencyKey, correlationId, cancellationToken);
    }

    public async Task<ServiceResult<Guid>> EnqueueDeliveryAsync(Guid tenantId, Guid id, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (invoice is null) return NotFound<Guid>();
        if (await IsShopifyManualInvoiceAsync(tenantId, invoice, cancellationToken)) return ServiceResult<Guid>.Fail("SHOPIFY_MANUAL_INVOICE_ONLY", "Shopify manuel belge kayıtları pazaryeri fatura aktarımına gönderilemez.", 422);
        if (invoice.Status is not (InvoiceStatus.Accepted or InvoiceStatus.MarketplaceFailed)) return ServiceResult<Guid>.Fail("INVOICE_STATE_INVALID", "Fatura pazaryerine iletime hazır değil.", 409);
        if (invoice.PackageId is null) return Invalid<Guid>("packageId", "Pazaryeri fatura iletimi için paket zorunludur.");
        if (!await db.InvoiceDocuments.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id && x.PermanentUrl != null, cancellationToken)) return ServiceResult<Guid>.Fail("INVOICE_PERMANENT_LINK_REQUIRED", "Trendyol iletimi için kalıcı HTTPS fatura bağlantısı henüz hazır değil.", 409);
        var marketplaceConnectionId = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == invoice.PackageId).Select(x => (Guid?)x.ConnectionId).SingleOrDefaultAsync(cancellationToken);
        if (marketplaceConnectionId is null) return Invalid<Guid>("packageId", "Faturaya bağlı pazaryeri paketi bulunamadı.");
        if (!await WriteGates(tenantId, marketplaceConnectionId.Value, InvoicingCapabilities.InvoiceDeliver, cancellationToken)) return CapabilityUnknown<Guid>(InvoicingCapabilities.InvoiceDeliver);
        invoice.Status = InvoiceStatus.MarketplacePending; invoice.UpdatedAt = timeProvider.GetUtcNow(); invoice.Version++;
        return await AddJob(invoice, InvoicingJobTypes.MarketplaceDelivery, idempotencyKey, correlationId, cancellationToken, marketplaceConnectionId.Value);
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

    private async Task<ServiceResult<Guid>> AddJob(Invoice invoice, string jobType, string idempotencyKey, string correlationId, CancellationToken cancellationToken, Guid? connectionId = null)
    {
        var dedup = $"{jobType}:{invoice.Id}:{idempotencyKey}"; var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == invoice.TenantId && x.JobType == jobType && x.JobDedupKey == dedup, cancellationToken); if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var payload = JsonSerializer.Serialize(new { invoiceId = invoice.Id }); var job = new IntegrationJob { Id = Guid.CreateVersion7(), TenantId = invoice.TenantId, ConnectionId = connectionId ?? invoice.ProviderConnectionId, JobType = jobType, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = $"{jobType}:{invoice.Id}:{idempotencyKey}", AvailableAt = timeProvider.GetUtcNow(), CorrelationId = correlationId, Version = 1 };
        db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(job.Id);
    }

    private async Task<IReadOnlyList<string>> AllowedActions(Invoice invoice, PlatformConnection? connection, CancellationToken cancellationToken)
    {
        if (ShopifyManualInvoicePolicy.IsManualOnly(connection?.PlatformCode, invoice.SequencePurpose)
            || await IsShopifyManualInvoiceAsync(invoice.TenantId, invoice, cancellationToken)) return [];
        var actions = new List<string>(); if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.ValidationFailed) actions.Add("VALIDATE");
        if ((invoice.Status == InvoiceStatus.Ready || CanRetryPreProviderFailure(invoice.Status, invoice.LastErrorCode, invoice.ExternalReference)) && await WriteGates(invoice.TenantId, invoice.ProviderConnectionId, InvoicingCapabilities.InvoiceSubmit, cancellationToken)) actions.Add("SUBMIT");
        if (AllowsStageCapabilityProbe(invoice.Status, invoice.LastErrorCode, invoice.ExternalReference, invoice.InvoiceType, connection)) actions.Add("STAGE_CAPABILITY_PROBE");
        if (invoice.Status is (InvoiceStatus.UnknownResult or InvoiceStatus.Submitted) && await ReadGate(invoice.TenantId, invoice.ProviderConnectionId, InvoicingCapabilities.InvoiceStatusRead, cancellationToken)) actions.Add("RECONCILE");
        if (invoice.Status is (InvoiceStatus.Accepted or InvoiceStatus.MarketplaceFailed) && invoice.PackageId is not null)
        {
            var marketplaceConnectionId = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == invoice.TenantId && x.Id == invoice.PackageId).Select(x => (Guid?)x.ConnectionId).SingleOrDefaultAsync(cancellationToken);
            var permanentLinkReady = await db.InvoiceDocuments.AsNoTracking().AnyAsync(x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && x.PermanentUrl != null, cancellationToken);
            if (permanentLinkReady && marketplaceConnectionId is not null && await WriteGates(invoice.TenantId, marketplaceConnectionId.Value, InvoicingCapabilities.InvoiceDeliver, cancellationToken)) actions.Add("DELIVER");
        }
        if (invoice.InvoiceType == "EARSIVFATURA" && invoice.Status is (InvoiceStatus.Accepted or InvoiceStatus.Completed) && await WriteGates(invoice.TenantId, invoice.ProviderConnectionId, InvoicingCapabilities.InvoiceCancel, cancellationToken)) actions.Add("CANCEL");
        if (invoice.Status == InvoiceStatus.CancellationPending && await ReadGate(invoice.TenantId, invoice.ProviderConnectionId, InvoicingCapabilities.InvoiceStatusRead, cancellationToken)) actions.Add("RECONCILE");
        return actions;
    }

    private async Task<bool> IsShopifyManualInvoiceAsync(Guid tenantId, Invoice invoice, CancellationToken cancellationToken)
    {
        if (string.Equals(invoice.SequencePurpose, ShopifyManualInvoicePolicy.TrackingSequencePurpose, StringComparison.OrdinalIgnoreCase)) return true;
        var isShopifyOrder = await db.Orders.AsNoTracking().AnyAsync(order => order.TenantId == tenantId && order.Id == invoice.OrderId
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == order.ConnectionId && connection.PlatformCode == "SHOPIFY"), cancellationToken);
        if (isShopifyOrder) return true;
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

    internal static bool CanRetryPreProviderFailure(InvoiceStatus status, string? lastErrorCode, string? externalReference) =>
        string.IsNullOrWhiteSpace(externalReference)
        && (CanRetryLocalPayloadFailure(status, lastErrorCode, externalReference)
            || (status == InvoiceStatus.Submitting && lastErrorCode is "EFATURAM_AUTHENTICATION_FAILED" or "EFATURAM_ACCESS_TOKEN_REJECTED" or "EFATURAM_INVOICE_CREATE_PRIVILEGE_MISSING" or MarketplaceInvoiceCreationPolicy.DisabledErrorCode));

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
        if (string.Equals(connection.PlatformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase))
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
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Precondition<T>(long version) => ServiceResult<T>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{version}.", 412);
    private static ServiceResult<T> PreconditionRequired<T>() => ServiceResult<T>.Fail("PRECONDITION_REQUIRED", "Mevcut kayıt için If-Match gereklidir.", 428);
    private static ServiceResult<T> CapabilityUnknown<T>(string capability) => ServiceResult<T>.Fail("EXTERNAL_WRITE_NOT_ENABLED", "Bu bağlantıda dış yazma işlemi etkin değil.", 422);

}
