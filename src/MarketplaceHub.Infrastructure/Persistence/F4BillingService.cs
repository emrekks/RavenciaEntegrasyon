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

public sealed partial class F4BillingService(
    AppDbContext db,
    CursorCodec cursors,
    IDataProtectionProvider dataProtection,
    IPrivateFileStorage files,
    IConfiguration configuration,
    TimeProvider timeProvider) : IF4BillingService
{
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
        var afterId = Decode(after); var query = db.Invoices.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<InvoiceStatus>(status, true, out var parsed)) query = query.Where(x => x.Status == parsed);
        var rows = await query.OrderBy(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken); var orderIds = rows.Select(x => x.OrderId).Distinct().ToList();
        var numbers = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.OrderNumber, cancellationToken);
        var hasMore = rows.Count > limit; var items = rows.Take(limit).Select(x => new InvoiceListView(x.Id, numbers.GetValueOrDefault(x.OrderId, "—"), x.InvoiceType, Status(x.Status), x.Currency, x.PayableTotal, x.InvoiceNumber, x.DueAt, x.CreatedAt, x.Version)).ToList();
        return new(items, hasMore ? cursors.Encode(rows[limit - 1].Id) : null, hasMore);
    }

    public async Task<IReadOnlyList<InvoiceWorkspaceItemView>> WorkspaceAsync(Guid tenantId, int limit, CancellationToken cancellationToken)
    {
        var packages = await db.ShipmentPackages.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.StatusOccurredAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        if (packages.Count == 0) return [];

        var orderIds = packages.Select(x => x.OrderId).Distinct().ToArray();
        var packageIds = packages.Select(x => x.Id).ToArray();
        var orders = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var lines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).ToListAsync(cancellationToken);
        var invoices = await db.Invoices.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OriginalInvoiceId == null && ((x.PackageId != null && packageIds.Contains(x.PackageId.Value)) || (x.PackageId == null && orderIds.Contains(x.OrderId))))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        var variantIds = lines.Where(x => x.VariantId != null).Select(x => x.VariantId!.Value).Distinct().ToArray();
        var mediaRows = await (from media in db.ProductMedia.AsNoTracking()
                               join asset in db.FileAssets.AsNoTracking() on new { media.TenantId, media.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                               where media.TenantId == tenantId && media.VariantId != null && variantIds.Contains(media.VariantId.Value) && media.Status == "ACTIVE" && asset.Status == "ACTIVE" && asset.Classification == "PRODUCT_MEDIA_URL"
                               orderby media.SortOrder
                               select new { VariantId = media.VariantId!.Value, Url = asset.RelativePath }).ToListAsync(cancellationToken);
        var mediaByVariant = mediaRows.GroupBy(x => x.VariantId).ToDictionary(x => x.Key, x => x.First().Url);
        var linesByOrder = lines.GroupBy(x => x.OrderId).ToDictionary(x => x.Key, x => x.ToList());
        var now = timeProvider.GetUtcNow();

        return packages.Select(package =>
        {
            if (!orders.TryGetValue(package.OrderId, out var order)) return null;
            var orderLines = linesByOrder.GetValueOrDefault(order.Id) ?? [];
            var invoice = invoices.FirstOrDefault(x => x.PackageId == package.Id) ?? invoices.FirstOrDefault(x => x.PackageId == null && x.OrderId == order.Id);
            var deliveredAt = package.Status == ShipmentPackageStatus.Delivered ? package.StatusOccurredAt : (DateTimeOffset?)null;
            var dueAt = deliveredAt?.AddDays(7);
            var dueSoon = invoice is null && deliveredAt is not null && now >= deliveredAt.Value.AddDays(5);
            var image = orderLines.Where(x => x.VariantId != null).Select(x => mediaByVariant.GetValueOrDefault(x.VariantId!.Value)).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            var customerName = InvoiceWorkspaceCustomerName(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson);
            var status = invoice is null ? "FATURALANDIRILMADI" : Status(invoice.Status);
            return new InvoiceWorkspaceItemView(order.Id, package.Id, order.OrderNumber, customerName, order.OrderedAt, package.Status.ToString().ToUpperInvariant(), deliveredAt, dueAt, dueSoon, order.Currency, package.NetAmount > 0 ? package.NetAmount : order.NetAmount, orderLines.Count, image, package.CargoProviderExternalId, package.CargoTrackingNumber, invoice?.Id, status, invoice?.InvoiceNumber, invoice is null);
        }).Where(x => x is not null).Select(x => x!).ToList();
    }

    private static string InvoiceWorkspaceCustomerName(string customerJson, string invoiceAddressJson)
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
        try
        {
            using var customer = JsonDocument.Parse(string.IsNullOrWhiteSpace(customerJson) ? "{}" : customerJson);
            var first = Find(customer.RootElement, "customerFirstName", "firstName");
            var last = Find(customer.RootElement, "customerLastName", "lastName");
            var full = string.Join(' ', new[] { first, last }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(full)) return full;
            using var address = JsonDocument.Parse(string.IsNullOrWhiteSpace(invoiceAddressJson) ? "{}" : invoiceAddressJson);
            return Find(address.RootElement, "fullName", "name", "company", "companyName") ?? "—";
        }
        catch (JsonException) { return "—"; }
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
        if (command.PackageId is { } packageId && !await db.ShipmentPackages.AnyAsync(x => x.TenantId == tenantId && x.Id == packageId && x.OrderId == command.OrderId, cancellationToken)) return Invalid<InvoiceDetailView>("packageId", "Paket siparişe ait değil.");
        var provider = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == command.ProviderConnectionId && x.PlatformCode == "TRENDYOL_EFATURAM", cancellationToken);
        if (provider is null) return Invalid<InvoiceDetailView>("billing", "Trendyol E-Faturam bağlantısı zorunludur.");
        var profile = await ProviderManagedProfile(tenantId, provider.Id, cancellationToken);
        var policy = await ManualPackagePolicy(tenantId, provider.Id, cancellationToken);
        if (command.OriginalInvoiceId is { } originalId && !await db.Invoices.AnyAsync(x => x.TenantId == tenantId && x.Id == originalId, cancellationToken)) return Invalid<InvoiceDetailView>("originalInvoiceId", "Orijinal fatura bulunamadı.");

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
            else if (await db.ShipmentPackages.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.OrderId == billedPackage.OrderId, cancellationToken) != 1)
                return Invalid<InvoiceDetailView>("packageId", "Paket toplamı henüz Trendyol'dan doğrulanmadı; siparişi yeniden eşitleyin.");
        }
        var targetPayable = decimal.Round(remotePayable, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(calculatedPayable - targetPayable) > 0.01m)
            return Invalid<InvoiceDetailView>("orderId", $"Sipariş kalem toplamı ({calculatedPayable:0.00}) ile Trendyol sipariş toplamı ({targetPayable:0.00}) eşleşmiyor.");
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
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (invoice is null) return NotFound<InvoiceDetailView>();
        var orderNumber = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == invoice.OrderId).Select(x => x.OrderNumber).SingleAsync(cancellationToken);
        var lines = await db.InvoiceLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.InvoiceId == id).OrderBy(x => x.LineSequence).Select(x => new InvoiceLineView(x.Id, x.LineSequence, x.DescriptionSnapshot, x.SkuSnapshot, x.UnitSnapshot, x.Quantity, x.UnitPrice, x.DiscountAmount, x.VatRate, x.VatAmount, x.LineTotal)).ToListAsync(cancellationToken);
        var documents = await db.InvoiceDocuments.AsNoTracking().Where(x => x.TenantId == tenantId && x.InvoiceId == id).OrderBy(x => x.CreatedAt).Select(x => new InvoiceDocumentView(x.Id, x.DocumentType, x.Sha256, x.CreatedAt)).ToListAsync(cancellationToken);
        var attempts = await db.InvoiceSubmissionAttempts.AsNoTracking().Where(x => x.TenantId == tenantId && x.InvoiceId == id).OrderBy(x => x.AttemptNumber).Select(x => new InvoiceAttemptView(x.AttemptNumber, x.Outcome, x.ErrorCode, x.StartedAt, x.CompletedAt)).ToListAsync(cancellationToken);
        var deliveries = await db.MarketplaceDeliveries.AsNoTracking().Where(x => x.TenantId == tenantId && x.InvoiceId == id).OrderBy(x => x.AttemptNumber).Select(x => new MarketplaceDeliveryView(x.Id, x.DeliveryType, x.Status, x.ExternalReference, x.ErrorCode, x.CreatedAt)).ToListAsync(cancellationToken);
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invoice.ProviderConnectionId, cancellationToken);
        return ServiceResult<InvoiceDetailView>.Ok(new(invoice.Id, invoice.OrderId, orderNumber, invoice.PackageId, invoice.ProviderConnectionId, invoice.InvoiceType, invoice.SequencePurpose, Status(invoice.Status), invoice.Currency, invoice.TaxExclusiveTotal, invoice.DiscountTotal, invoice.TaxTotal, invoice.PayableTotal, invoice.Note, invoice.InvoiceNumber, invoice.EttnUuid, invoice.DueAt, invoice.IssuedAt, invoice.LastErrorCode, lines, documents, attempts, deliveries, await AllowedActions(invoice, connection, cancellationToken), invoice.Version, connection is null || IntegrationRuntimePolicy.RequiresSensitiveConfirmation(connection)));
    }

    public async Task<ServiceResult<InvoiceDetailView>> ValidateAsync(Guid tenantId, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (invoice is null) return NotFound<InvoiceDetailView>(); if (invoice.Version != expectedVersion) return Precondition<InvoiceDetailView>(invoice.Version);
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

    public Task<ServiceResult<Guid>> EnqueueSubmitAsync(Guid tenantId, Guid id, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken) => EnqueueWrite(tenantId, id, expectedVersion, idempotencyKey, correlationId, F4JobTypes.InvoiceSubmit, F4Capabilities.InvoiceSubmit, [InvoiceStatus.Ready], InvoiceStatus.Submitting, cancellationToken);
    public async Task<ServiceResult<Guid>> EnqueueStageCapabilityProbeAsync(Guid tenantId, Guid id, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (invoice is null) return NotFound<Guid>();
        if (invoice.Version != expectedVersion) return Precondition<Guid>(invoice.Version);
        var safeScopeReplay = invoice.Status == InvoiceStatus.ManualReview
            && string.Equals(invoice.LastErrorCode, "EFATURAM_TOKEN_SCOPE_MISSING", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(invoice.ExternalReference);
        if (invoice.Status != InvoiceStatus.Ready && !safeScopeReplay || invoice.InvoiceType != "EARSIVFATURA" || !string.IsNullOrWhiteSpace(invoice.ExternalReference)) return ServiceResult<Guid>.Fail("STAGE_INVOICE_FIXTURE_INVALID", "Canary yalnız gönderilmemiş Ready taslakta veya provider isteğinden önce token kapsamı eksikliğiyle duran aynı Stage taslakta çalışır.", 409);
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invoice.ProviderConnectionId && x.PlatformCode == "TRENDYOL_EFATURAM", cancellationToken);
        if (connection is null || !string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase) || !string.Equals(connection.ExternalStoreId, "Ravencia - Ravencia", StringComparison.Ordinal)) return ServiceResult<Guid>.Fail("STAGE_INVOICE_FIXTURE_REQUIRED", "Canary yalnız sabitlenmiş E-Faturam Stage test hesabındaki faturada çalışır.", 422);
        invoice.Status = InvoiceStatus.Submitting; invoice.IssuedAt ??= timeProvider.GetUtcNow(); invoice.UpdatedAt = timeProvider.GetUtcNow(); invoice.Version++;
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, Action = "EFATURAM_STAGE_CAPABILITY_PROBE_ENQUEUED", TargetType = "Invoice", TargetId = invoice.Id.ToString("D"), Reason = safeScopeReplay ? "pre-submit-token-scope-replay" : "auditli-stage-test-order", CorrelationId = correlationId, CreatedAt = timeProvider.GetUtcNow() });
        return await AddJob(invoice, F4JobTypes.StageCapabilityProbe, idempotencyKey, correlationId, cancellationToken);
    }
    public async Task<ServiceResult<Guid>> EnqueueCancellationAsync(Guid tenantId, Guid id, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (invoice is null) return NotFound<Guid>();
        if (invoice.InvoiceType != "EARSIVFATURA") return ServiceResult<Guid>.Fail("EINVOICE_CANCELLATION_WORKFLOW_REQUIRED", "Bu otomatik iptal servisi yalnız E-Arşiv faturalar içindir; E-Fatura için mevzuata uygun itiraz/iptal süreci manuel yürütülmelidir.", 422);
        return await EnqueueWrite(tenantId, id, expectedVersion, idempotencyKey, correlationId, F4JobTypes.InvoiceCancellation, F4Capabilities.InvoiceCancel, [InvoiceStatus.Accepted, InvoiceStatus.Completed], InvoiceStatus.CancellationPending, cancellationToken);
    }

    public async Task<ServiceResult<Guid>> EnqueueReconcileAsync(Guid tenantId, Guid id, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (invoice is null) return NotFound<Guid>();
        if (invoice.Status is not (InvoiceStatus.UnknownResult or InvoiceStatus.Submitted or InvoiceStatus.MarketplacePending or InvoiceStatus.MarketplaceFailed or InvoiceStatus.CancellationPending)) return ServiceResult<Guid>.Fail("INVOICE_STATE_INVALID", "Bu durumda provider reconciliation çalıştırılamaz.", 409);
        if (!await ReadGate(tenantId, invoice.ProviderConnectionId, F4Capabilities.InvoiceStatusRead, cancellationToken)) return CapabilityUnknown<Guid>(F4Capabilities.InvoiceStatusRead);
        return await AddJob(invoice, F4JobTypes.InvoiceReconcile, idempotencyKey, correlationId, cancellationToken);
    }

    public async Task<ServiceResult<Guid>> EnqueueDeliveryAsync(Guid tenantId, Guid id, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (invoice is null) return NotFound<Guid>();
        if (invoice.Status is not (InvoiceStatus.Accepted or InvoiceStatus.MarketplaceFailed)) return ServiceResult<Guid>.Fail("INVOICE_STATE_INVALID", "Fatura pazaryerine iletime hazır değil.", 409);
        if (invoice.PackageId is null) return Invalid<Guid>("packageId", "Pazaryeri fatura iletimi için paket zorunludur.");
        if (!await db.InvoiceDocuments.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id && x.PermanentUrl != null, cancellationToken)) return ServiceResult<Guid>.Fail("INVOICE_PERMANENT_LINK_REQUIRED", "Trendyol iletimi için kalıcı HTTPS fatura bağlantısı henüz hazır değil.", 409);
        var marketplaceConnectionId = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == invoice.PackageId).Select(x => (Guid?)x.ConnectionId).SingleOrDefaultAsync(cancellationToken);
        if (marketplaceConnectionId is null) return Invalid<Guid>("packageId", "Faturaya bağlı pazaryeri paketi bulunamadı.");
        if (!await WriteGates(tenantId, marketplaceConnectionId.Value, F4Capabilities.InvoiceDeliver, cancellationToken)) return CapabilityUnknown<Guid>(F4Capabilities.InvoiceDeliver);
        invoice.Status = InvoiceStatus.MarketplacePending; invoice.UpdatedAt = timeProvider.GetUtcNow(); invoice.Version++;
        return await AddJob(invoice, F4JobTypes.MarketplaceDelivery, idempotencyKey, correlationId, cancellationToken, marketplaceConnectionId.Value);
    }

    public async Task<ServiceResult<(Stream Content, string MimeType, string FileName)>> OpenDocumentAsync(Guid tenantId, Guid invoiceId, Guid documentId, CancellationToken cancellationToken)
    {
        var row = await (from document in db.InvoiceDocuments.AsNoTracking() join asset in db.FileAssets.AsNoTracking() on new { document.TenantId, Id = document.FileAssetId } equals new { asset.TenantId, asset.Id } where document.TenantId == tenantId && document.InvoiceId == invoiceId && document.Id == documentId select new { asset.RelativePath, asset.MimeType, asset.OriginalNameSafe }).SingleOrDefaultAsync(cancellationToken);
        return row is null ? NotFound<(Stream, string, string)>() : ServiceResult<(Stream, string, string)>.Ok((await files.OpenReadAsync(tenantId, row.RelativePath, cancellationToken), row.MimeType, row.OriginalNameSafe ?? $"invoice-{documentId:N}"));
    }

    private async Task<ServiceResult<Guid>> EnqueueWrite(Guid tenantId, Guid id, long expectedVersion, string idempotencyKey, string correlationId, string jobType, string capability, InvoiceStatus[] states, InvoiceStatus next, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (invoice is null) return NotFound<Guid>(); if (invoice.Version != expectedVersion) return Precondition<Guid>(invoice.Version);
        if (!states.Contains(invoice.Status) || !InvoiceStateMachine.CanTransition(invoice.Status, next)) return ServiceResult<Guid>.Fail("INVOICE_STATE_INVALID", "Fatura mevcut durumdan bu işleme geçemez.", 409);
        if (!await WriteGates(tenantId, invoice.ProviderConnectionId, capability, cancellationToken)) return CapabilityUnknown<Guid>(capability);
        invoice.Status = next; invoice.UpdatedAt = timeProvider.GetUtcNow(); if (jobType == F4JobTypes.InvoiceSubmit) invoice.IssuedAt ??= invoice.UpdatedAt; invoice.Version++; return await AddJob(invoice, jobType, idempotencyKey, correlationId, cancellationToken);
    }

    private async Task<ServiceResult<Guid>> AddJob(Invoice invoice, string jobType, string idempotencyKey, string correlationId, CancellationToken cancellationToken, Guid? connectionId = null)
    {
        var dedup = $"{jobType}:{invoice.Id}:{idempotencyKey}"; var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == invoice.TenantId && x.JobType == jobType && x.JobDedupKey == dedup, cancellationToken); if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var payload = JsonSerializer.Serialize(new { invoiceId = invoice.Id }); var job = new IntegrationJob { Id = Guid.CreateVersion7(), TenantId = invoice.TenantId, ConnectionId = connectionId ?? invoice.ProviderConnectionId, JobType = jobType, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = $"{jobType}:{invoice.Id}:{idempotencyKey}", AvailableAt = timeProvider.GetUtcNow(), CorrelationId = correlationId, Version = 1 };
        db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(job.Id);
    }

    private async Task<IReadOnlyList<string>> AllowedActions(Invoice invoice, PlatformConnection? connection, CancellationToken cancellationToken)
    {
        var actions = new List<string>(); if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.ValidationFailed) actions.Add("VALIDATE");
        if (invoice.Status == InvoiceStatus.Ready && await WriteGates(invoice.TenantId, invoice.ProviderConnectionId, F4Capabilities.InvoiceSubmit, cancellationToken)) actions.Add("SUBMIT");
        if (AllowsStageCapabilityProbe(invoice.Status, invoice.LastErrorCode, invoice.ExternalReference, invoice.InvoiceType, connection)) actions.Add("STAGE_CAPABILITY_PROBE");
        if (invoice.Status is (InvoiceStatus.UnknownResult or InvoiceStatus.Submitted) && await ReadGate(invoice.TenantId, invoice.ProviderConnectionId, F4Capabilities.InvoiceStatusRead, cancellationToken)) actions.Add("RECONCILE");
        if (invoice.Status is (InvoiceStatus.Accepted or InvoiceStatus.MarketplaceFailed) && invoice.PackageId is not null)
        {
            var marketplaceConnectionId = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == invoice.TenantId && x.Id == invoice.PackageId).Select(x => (Guid?)x.ConnectionId).SingleOrDefaultAsync(cancellationToken);
            var permanentLinkReady = await db.InvoiceDocuments.AsNoTracking().AnyAsync(x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && x.PermanentUrl != null, cancellationToken);
            if (permanentLinkReady && marketplaceConnectionId is not null && await WriteGates(invoice.TenantId, marketplaceConnectionId.Value, F4Capabilities.InvoiceDeliver, cancellationToken)) actions.Add("DELIVER");
        }
        if (invoice.InvoiceType == "EARSIVFATURA" && invoice.Status is (InvoiceStatus.Accepted or InvoiceStatus.Completed) && await WriteGates(invoice.TenantId, invoice.ProviderConnectionId, F4Capabilities.InvoiceCancel, cancellationToken)) actions.Add("CANCEL");
        if (invoice.Status == InvoiceStatus.CancellationPending && await ReadGate(invoice.TenantId, invoice.ProviderConnectionId, F4Capabilities.InvoiceStatusRead, cancellationToken)) actions.Add("RECONCILE");
        return actions;
    }

    internal static bool AllowsStageCapabilityProbe(InvoiceStatus status, string? lastErrorCode, string? externalReference, string invoiceType, PlatformConnection? connection)
    {
        var safeScopeReplay = status == InvoiceStatus.ManualReview
            && string.Equals(lastErrorCode, "EFATURAM_TOKEN_SCOPE_MISSING", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(externalReference);
        return (status == InvoiceStatus.Ready || safeScopeReplay)
            && string.Equals(invoiceType, "EARSIVFATURA", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(externalReference)
            && connection is not null
            && string.Equals(connection.PlatformCode, "TRENDYOL_EFATURAM", StringComparison.Ordinal)
            && string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase)
            && string.Equals(connection.ExternalStoreId, "Ravencia - Ravencia", StringComparison.Ordinal);
    }

    private async Task<bool> WriteGates(Guid tenantId, Guid connectionId, string capability, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        if (connection is null) return false;
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
