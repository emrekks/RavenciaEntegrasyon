using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class InvoicingBillingService
{
    public async Task<ServiceResult<IReadOnlyList<InvoiceWorkspacePreviewItem>>> PreviewWorkspaceInvoicesAsync(
        Guid tenantId,
        InvoiceWorkspacePreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Items is null || request.Items.Count is < 1 or > 200)
            return ServiceResult<IReadOnlyList<InvoiceWorkspacePreviewItem>>.Fail("INVOICE_PREVIEW_ITEMS_INVALID", "Önizleme için 1 ile 200 paket seçilmelidir.", 422);
        if (request.Items.Select(item => item.PackageId).Distinct().Count() != request.Items.Count)
            return ServiceResult<IReadOnlyList<InvoiceWorkspacePreviewItem>>.Fail("INVOICE_PREVIEW_DUPLICATE_PACKAGE", "Aynı paket önizlemede birden fazla kez seçilemez.", 422);

        var items = new List<InvoiceWorkspacePreviewItem>(request.Items.Count);
        foreach (var target in request.Items)
        {
            var result = await BuildWorkspacePreviewAsync(tenantId, target, request.IncludeInternetSalesInfo, cancellationToken);
            if (!result.Succeeded) return ServiceResult<IReadOnlyList<InvoiceWorkspacePreviewItem>>.Fail(result.Error!.Code, result.Error.Message, result.Error.Status, result.Error.FieldErrors);
            items.Add(result.Value!);
        }
        return ServiceResult<IReadOnlyList<InvoiceWorkspacePreviewItem>>.Ok(items);
    }

    public async Task<ServiceResult<InvoiceWorkspaceConfirmResult>> ConfirmWorkspaceInvoicesAsync(
        Guid tenantId,
        InvoiceWorkspacePreviewConfirmRequest request,
        string idempotencyKey,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (request.Items is null || request.Items.Count is < 1 or > 200)
            return ServiceResult<InvoiceWorkspaceConfirmResult>.Fail("INVOICE_CONFIRM_ITEMS_INVALID", "Onay için 1 ile 200 paket seçilmelidir.", 422);
        if (request.Items.Select(item => item.PackageId).Distinct().Count() != request.Items.Count)
            return ServiceResult<InvoiceWorkspaceConfirmResult>.Fail("INVOICE_CONFIRM_DUPLICATE_PACKAGE", "Aynı paket onay isteğinde birden fazla kez bulunamaz.", 422);

        var results = new List<InvoiceWorkspaceConfirmItemResult>(request.Items.Count);
        foreach (var confirmation in request.Items)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var fresh = await BuildWorkspacePreviewAsync(tenantId,
                new(confirmation.OrderId, confirmation.PackageId, confirmation.ProviderConnectionId), request.IncludeInternetSalesInfo, cancellationToken);
            if (!fresh.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                results.Add(new(confirmation.PackageId, null, null, "BLOCKED", "NONE", fresh.Error!.Message));
                continue;
            }
            var preview = fresh.Value!;
            if (!FixedDigestEquals(preview.PreviewDigest, confirmation.PreviewDigest))
            {
                await transaction.RollbackAsync(cancellationToken);
                results.Add(new(confirmation.PackageId, preview.ExistingInvoiceId, null, "PREVIEW_STALE", "REFRESH_PREVIEW", "Sipariş veya fatura verileri önizlemeden sonra değişti. Güncel bilgileri yeniden açıp kontrol edin."));
                continue;
            }
            if (!preview.CanConfirm)
            {
                await transaction.RollbackAsync(cancellationToken);
                results.Add(new(confirmation.PackageId, preview.ExistingInvoiceId, null, "BLOCKED", "NONE", preview.BlockedReason ?? "Bu paket için fatura işlemi uygun değil."));
                continue;
            }

            try
            {
                var invoice = preview.ExistingInvoiceId is { } existingId
                    ? await db.Invoices.SingleAsync(item => item.TenantId == tenantId && item.Id == existingId, cancellationToken)
                    : null;
                if (invoice?.Status == InvoiceStatus.Completed)
                {
                    await transaction.CommitAsync(cancellationToken);
                    results.Add(new(confirmation.PackageId, invoice.Id, null, "COMPLETED", "NONE", "Fatura ve pazaryeri iletimi zaten doğrulanmış."));
                    continue;
                }

                var itemKey = $"{idempotencyKey}:package:{confirmation.PackageId:N}";
                if (invoice is not null && InvoiceMarketplaceRetryPolicy.CanRetryDelivery(invoice.Status, invoice.LastErrorCode))
                {
                    var queuedDelivery = await EnqueueDeliveryAsync(tenantId, invoice.Id, $"{itemKey}:delivery", correlationId, cancellationToken);
                    if (!queuedDelivery.Succeeded)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        results.Add(new(confirmation.PackageId, invoice.Id, null, "BLOCKED", "DELIVERY", queuedDelivery.Error!.Message));
                        continue;
                    }
                    await transaction.CommitAsync(cancellationToken);
                    results.Add(new(confirmation.PackageId, invoice.Id, queuedDelivery.Value, "QUEUED", "DELIVERY_ONLY", "Mali fatura mevcut. Yalnızca pazaryeri iletimi kuyruğa alındı."));
                    continue;
                }

                if (invoice is not null && invoice.Status is InvoiceStatus.Submitting or InvoiceStatus.UnknownResult or InvoiceStatus.Submitted or InvoiceStatus.MarketplacePending)
                {
                    await transaction.CommitAsync(cancellationToken);
                    results.Add(new(confirmation.PackageId, invoice.Id, null, "IN_PROGRESS", "WAIT", "Fatura işlemi zaten sürüyor veya sonucu uzlaştırılıyor; yeni mali gönderim yapılmadı."));
                    continue;
                }

                if (invoice is not null && InvoicingBillingService.CanRetryPreProviderFailure(invoice.Status, invoice.LastErrorCode, invoice.ExternalReference))
                {
                    invoice.IncludeInternetSalesInfo = request.IncludeInternetSalesInfo;
                    invoice.UpdatedAt = timeProvider.GetUtcNow();
                    invoice.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                    var queuedRetry = await EnqueueSubmitAsync(tenantId, invoice.Id, invoice.Version, $"{itemKey}:submit-retry", correlationId, cancellationToken);
                    if (!queuedRetry.Succeeded)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        results.Add(new(confirmation.PackageId, invoice.Id, null, "BLOCKED", "SUBMIT", queuedRetry.Error!.Message));
                        continue;
                    }
                    await transaction.CommitAsync(cancellationToken);
                    results.Add(new(confirmation.PackageId, invoice.Id, queuedRetry.Value, "QUEUED", "SUBMIT_RETRY", "Sağlayıcıdan kesin ret alınmış mevcut fatura güvenli biçimde tekrar sıraya alındı."));
                    continue;
                }

                if (invoice is null)
                {
                    var created = await CreateDraftAsync(tenantId,
                        new(confirmation.OrderId, confirmation.PackageId, confirmation.ProviderConnectionId, null, request.IncludeInternetSalesInfo),
                        $"invoice-workspace:{confirmation.PackageId:N}", cancellationToken);
                    if (!created.Succeeded)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        results.Add(new(confirmation.PackageId, null, null, "BLOCKED", "CREATE", created.Error!.Message));
                        continue;
                    }
                    invoice = await db.Invoices.SingleAsync(item => item.TenantId == tenantId && item.Id == created.Value!.Id, cancellationToken);
                }

                if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.ValidationFailed)
                {
                    var validated = await ValidateAsync(tenantId, invoice.Id, invoice.Version, cancellationToken);
                    if (!validated.Succeeded || validated.Value!.Status != InvoiceStatus.Ready.ToString().ToUpperInvariant())
                    {
                        await transaction.CommitAsync(cancellationToken);
                        results.Add(new(confirmation.PackageId, invoice.Id, null, "VALIDATION_FAILED", "REVIEW", validated.Succeeded ? validated.Value!.LastErrorCode ?? "Fatura doğrulanamadı." : validated.Error!.Message));
                        continue;
                    }
                    invoice = await db.Invoices.SingleAsync(item => item.TenantId == tenantId && item.Id == invoice.Id, cancellationToken);
                }

                var queued = await EnqueueSubmitAsync(tenantId, invoice.Id, invoice.Version, $"{itemKey}:submit", correlationId, cancellationToken);
                if (!queued.Succeeded)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    results.Add(new(confirmation.PackageId, invoice.Id, null, "BLOCKED", "SUBMIT", queued.Error!.Message));
                    continue;
                }
                await transaction.CommitAsync(cancellationToken);
                results.Add(new(confirmation.PackageId, invoice.Id, queued.Value, "QUEUED", "CREATE_AND_SUBMIT", "Fatura sağlayıcıya güvenli kuyruğa alındı. Kabul ve PDF hazır olduğunda platform iletimi başlayacak."));
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(cancellationToken);
                results.Add(new(confirmation.PackageId, null, null, "CONFLICT", "REFRESH_PREVIEW", "Bu paket için eşzamanlı bir fatura işlemi oluştu. Güncel durumu yenileyin; tekrar mali fatura oluşturulmadı."));
            }
        }
        return ServiceResult<InvoiceWorkspaceConfirmResult>.Ok(new(results));
    }

    private async Task<ServiceResult<InvoiceWorkspacePreviewItem>> BuildWorkspacePreviewAsync(
        Guid tenantId,
        InvoiceWorkspacePreviewTarget target,
        bool includeInternetSalesInfo,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == target.OrderId, cancellationToken);
        if (order is null) return ServiceResult<InvoiceWorkspacePreviewItem>.Fail("ORDER_NOT_FOUND", "Sipariş bulunamadı.", 404);
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == target.PackageId && x.OrderId == order.Id, cancellationToken);
        if (package is null) return ServiceResult<InvoiceWorkspacePreviewItem>.Fail("PACKAGE_NOT_FOUND", "Seçilen paket bu siparişe ait değil.", 404);
        if (DashboardMetricPolicy.InvoiceExcludedOrderStatuses.Contains(order.DerivedStatus)
            || !DashboardMetricPolicy.IsInvoiceEligiblePackage(package.Status))
            return ServiceResult<InvoiceWorkspacePreviewItem>.Fail("INVOICE_PACKAGE_NOT_ELIGIBLE", "İptal edilmiş sipariş veya paket için satış faturası oluşturulamaz.", 422);
        var marketplace = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == package.ConnectionId, cancellationToken);
        var provider = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == target.ProviderConnectionId, cancellationToken);
        if (marketplace is null || provider is null || provider.PlatformCode != "TRENDYOL_EFATURAM" || provider.Status is not ("ACTIVE" or "VERIFIED"))
            return ServiceResult<InvoiceWorkspacePreviewItem>.Fail("INVOICE_CONNECTION_INVALID", "Aktif E-Faturam sağlayıcısı ve pazaryeri sipariş bağlantısı gerekli.", 422);

        var sourceLines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        if (sourceLines.Count == 0) return ServiceResult<InvoiceWorkspacePreviewItem>.Fail("INVOICE_LINES_MISSING", "Fatura önizlemesi için sipariş ürün satırları bulunamadı.", 422);
        var activePackageIds = await db.ShipmentPackages.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OrderId == order.Id && x.Status != ShipmentPackageStatus.Cancelled)
            .Select(x => x.Id).ToListAsync(cancellationToken);
        var allocations = await db.PackageLineAllocations.AsNoTracking()
            .Where(x => x.TenantId == tenantId && activePackageIds.Contains(x.PackageId)).ToListAsync(cancellationToken);
        if (!TryResolvePackageInvoiceLines(sourceLines, allocations, package.Id, activePackageIds.Count,
                out var activeLines, out var quantities, out var allocationError))
            return ServiceResult<InvoiceWorkspacePreviewItem>.Fail("INVOICE_PACKAGE_LINE_ALLOCATION_INVALID", allocationError!, 422);

        var packageExisting = await db.Invoices.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PackageId == package.Id && x.OriginalInvoiceId == null && x.SequencePurpose == "SALE")
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var orderWideExisting = await db.Invoices.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OrderId == order.Id && x.PackageId == null && x.OriginalInvoiceId == null && x.SequencePurpose == "SALE")
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var existing = packageExisting ?? orderWideExisting;
        if (existing is not null && existing.ProviderConnectionId != provider.Id)
            return ServiceResult<InvoiceWorkspacePreviewItem>.Fail("INVOICE_PROVIDER_MISMATCH", "Mevcut faturanın sağlayıcısı kullanılmalıdır. Başka sağlayıcıyla yeniden mali fatura oluşturulamaz.", 409);
        var packageTotal = package.NetAmount > 0 ? package.NetAmount : order.NetAmount;
        if (!InvoiceAmounts.TryCalculatePackage(
            activeLines.Select(line => new InvoicePackageLineSource(line.Id, line.TitleSnapshot, line.Sku,
                quantities?.GetValueOrDefault(line.Id) ?? line.OrderedQuantity - line.CancelledQuantity, line.UnitPrice, line.VatRate)).ToArray(),
            package.GrossAmount, package.DiscountAmount, packageTotal, out var calculated, out var calculationError))
            return ServiceResult<InvoiceWorkspacePreviewItem>.Fail("INVOICE_TOTAL_MISMATCH", calculationError ?? "Paket ürün toplamı ile sipariş/paket toplamı eşleşmiyor.", 422);

        var lines = calculated!.Lines.Select(line => new InvoiceWorkspacePreviewLine(line.Description, line.Sku, line.Quantity, "ADET", line.VatRate,
            line.UnitPrice, line.DiscountAmount, line.VatAmount, line.PayableAmount)).ToArray();

        var invoiceType = InvoiceAmounts.TrendyolInvoiceType(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson);
        var taxIdentity = ReadTaxIdentity(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson);
        var corporate = TrendyolEFaturamCanonicalPayload.IsCorporateRecipient(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, taxIdentity);
        var customerType = corporate ? "Kurumsal" : "Bireysel";
        if (!corporate) taxIdentity = "11111111111";
        else if (taxIdentity.Length is not (10 or 11) || taxIdentity.Any(character => !char.IsAsciiDigit(character)))
            return ServiceResult<InvoiceWorkspacePreviewItem>.Fail("EFATURAM_CORPORATE_TAX_ID_REQUIRED", "Kurumsal müşteri için geçerli 10 haneli vergi numarası veya 11 haneli T.C. kimlik numarası eksik.", 422);
        var customerName = InvoiceWorkspaceCustomerName(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson);
        var taxTotal = calculated.TaxTotal;
        var taxExclusive = calculated.TaxExclusiveTotal;
        var canCreate = (existing is not null || MarketplaceInvoiceCreationPolicy.IsEnabled(marketplace.PlatformCode, marketplace.SettingsJson))
            && InvoiceDeliveryEnvironmentPolicy.IsCompatible(provider.Environment, marketplace.Environment);
        string? blockedReason = canCreate ? null
            : existing is null && !MarketplaceInvoiceCreationPolicy.IsEnabled(marketplace.PlatformCode, marketplace.SettingsJson)
                ? string.Equals(marketplace.PlatformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase)
                    ? MarketplaceInvoiceCreationPolicy.UnsupportedFiscalProviderMessage
                    : MarketplaceInvoiceCreationPolicy.DisabledMessage
                : InvoiceDeliveryEnvironmentPolicy.DescribeMismatch(provider.Environment, marketplace.Environment);
        var nextAction = existing is null ? "CREATE_AND_SUBMIT" : existing.Status switch
        {
            _ when InvoiceMarketplaceRetryPolicy.CanRetryDelivery(existing.Status, existing.LastErrorCode) => "DELIVERY_ONLY",
            InvoiceStatus.Completed => "NONE",
            InvoiceStatus.Submitting or InvoiceStatus.UnknownResult or InvoiceStatus.Submitted or InvoiceStatus.MarketplacePending => "WAIT",
            _ when CanRetryPreProviderFailure(existing.Status, existing.LastErrorCode, existing.ExternalReference) => "SUBMIT_RETRY",
            _ => "REVIEW"
        };
        if (existing is not null && nextAction == "REVIEW")
        {
            canCreate = false;
            blockedReason = "Bu paket için mevcut mali fatura kaydı var. Durumu incelenmeden yeni mali gönderim başlatılamaz.";
        }

        if (canCreate)
        {
            var providerHasCredential = await db.PlatformCredentials.AsNoTracking().AnyAsync(credential =>
                credential.TenantId == tenantId && credential.ConnectionId == provider.Id && credential.RevokedAt == null, cancellationToken);
            if (!providerHasCredential)
            {
                canCreate = false;
                blockedReason = "Seçilen E-Faturam bağlantısında etkin kimlik bilgisi yok.";
            }
            else if ((nextAction is "CREATE_AND_SUBMIT" or "SUBMIT_RETRY")
                     && !await WriteGates(tenantId, provider.Id, InvoicingCapabilities.InvoiceSubmit, cancellationToken))
            {
                canCreate = false;
                blockedReason = "E-Faturam fatura oluşturma izni kapalı veya doğrulanmamış.";
            }
            else if ((nextAction is "CREATE_AND_SUBMIT" or "DELIVERY_ONLY")
                     && !await WriteGates(tenantId, marketplace.Id, InvoicingCapabilities.InvoiceDeliver, cancellationToken))
            {
                canCreate = false;
                blockedReason = "Bu pazaryeri için ayrı fatura iletme izni kapalı veya doğrulanmamış.";
            }
        }

        var digestSource = JsonSerializer.Serialize(new
        {
            target.OrderId, target.PackageId, target.ProviderConnectionId,
            order.OrderNumber, order.OrderedAt, order.Currency, OrderNetAmount = order.NetAmount,
            order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson,
            package.ExternalPackageId, PackageNetAmount = package.NetAmount, package.Status, package.StatusOccurredAt, package.CargoProviderExternalId,
            Marketplace = new { marketplace.PlatformCode, marketplace.Environment, marketplace.SettingsJson },
            Provider = new { provider.PlatformCode, provider.Environment },
            Allocations = allocations.OrderBy(item => item.PackageId).ThenBy(item => item.OrderLineId).ThenBy(item => item.SourceEventId).Select(item => new { item.PackageId, item.OrderLineId, item.AllocatedQuantity, item.SourceEventId }),
            SourceLines = sourceLines.Select(line => new { line.Id, line.TitleSnapshot, line.Sku, line.OrderedQuantity, line.CancelledQuantity, line.UnitPrice, line.VatRate }),
            Existing = existing is null ? null : new { existing.Id, existing.PackageId, existing.Status, existing.Version, existing.InvoiceNumber, existing.ExternalReference, existing.LastErrorCode },
            includeInternetSalesInfo,
            Totals = new { taxExclusive, Discount = calculated.DiscountTotal, taxTotal, Payable = packageTotal, invoiceType }
        });
        var digest = Hash(digestSource);
        var canConfirm = canCreate && (nextAction is "DELIVERY_ONLY" or "SUBMIT_RETRY" or "CREATE_AND_SUBMIT");
        if (orderWideExisting is not null)
        {
            canCreate = false;
            canConfirm = false;
            nextAction = "REVIEW";
            blockedReason = "Bu siparişte pakete bağlanmamış eski bir mali fatura kaydı var. Mükerrer kesimi önlemek için otomatik işlem durduruldu; fatura ve paket eşleşmesi incelenmeli.";
        }
        var view = new InvoiceWorkspacePreviewItem(order.Id, package.Id, provider.Id, order.OrderNumber, marketplace.PlatformCode,
            marketplace.DisplayName, marketplace.Environment, customerType, customerName, taxIdentity,
            string.IsNullOrWhiteSpace(order.InvoiceAddressSnapshotJson) ? "{}" : order.InvoiceAddressSnapshotJson,
            invoiceType, order.Currency, taxExclusive, calculated.DiscountTotal, taxTotal, packageTotal, lines,
            canConfirm,
            blockedReason, digest, existing?.Id, existing?.Status.ToString().ToUpperInvariant(), nextAction, includeInternetSalesInfo);
        return ServiceResult<InvoiceWorkspacePreviewItem>.Ok(view);
    }

    private static string ReadTaxIdentity(string customerJson, string addressJson)
    {
        foreach (var json in new[] { addressJson, customerJson })
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var value = Find(document.RootElement, 0);
                if (!string.IsNullOrWhiteSpace(value)) return new string(value.Where(char.IsAsciiDigit).ToArray());
            }
            catch (JsonException) { }
        }
        return "—";

        static string? Find(JsonElement node, int depth)
        {
            if (depth > 6) return null;
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in node.EnumerateObject())
                {
                    if (property.Name.Equals("taxNumber", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("taxId", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("taxIdentityNumber", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("identityNumber", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("nationalId", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("vkn", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("tckn", StringComparison.OrdinalIgnoreCase))
                    {
                        var candidate = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
                        if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
                    }
                    var nested = Find(property.Value, depth + 1);
                    if (nested is not null) return nested;
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray())
                {
                    var nested = Find(item, depth + 1);
                    if (nested is not null) return nested;
                }
            }
            return null;
        }
    }

    private static bool FixedDigestEquals(string first, string second)
    {
        try
        {
            var firstBytes = Convert.FromHexString(first);
            var secondBytes = Convert.FromHexString(second);
            return firstBytes.Length == secondBytes.Length && CryptographicOperations.FixedTimeEquals(firstBytes, secondBytes);
        }
        catch (FormatException) { return false; }
    }
}
