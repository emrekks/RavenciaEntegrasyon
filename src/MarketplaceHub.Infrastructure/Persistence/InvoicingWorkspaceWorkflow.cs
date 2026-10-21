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
        if (request.Items.Select(item => item.OrderId).Distinct().Count() != request.Items.Count)
            return ServiceResult<IReadOnlyList<InvoiceWorkspacePreviewItem>>.Fail("INVOICE_PREVIEW_DUPLICATE_ORDER", "Sipariş başına tek mali fatura oluşturulabilir; aynı sipariş önizlemede bir kez yer almalıdır.", 422);

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
        if (request.Items.Select(item => item.OrderId).Distinct().Count() != request.Items.Count)
            return ServiceResult<InvoiceWorkspaceConfirmResult>.Fail("INVOICE_CONFIRM_DUPLICATE_ORDER", "Sipariş başına tek mali fatura oluşturulabilir; aynı sipariş onay isteğinde bir kez yer almalıdır.", 422);

        var results = new List<InvoiceWorkspaceConfirmItemResult>(request.Items.Count);
        foreach (var confirmation in request.Items)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            if (!await InvoiceOrderMutationLock.AcquireAsync(db, tenantId, confirmation.OrderId, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                results.Add(new(confirmation.PackageId, null, null, "BLOCKED", "NONE", "Sipariş bulunamadı."));
                continue;
            }
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

                var itemKey = $"{idempotencyKey}:order:{confirmation.OrderId:N}";
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

                // Checked before the in-progress rule: a submission the provider refused before creating
                // anything sits in Submitting with an error code and is retryable, not in progress.
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

                if (invoice is not null && invoice.Status is InvoiceStatus.Submitting or InvoiceStatus.UnknownResult or InvoiceStatus.Submitted or InvoiceStatus.MarketplacePending)
                {
                    await transaction.CommitAsync(cancellationToken);
                    results.Add(new(confirmation.PackageId, invoice.Id, null, "IN_PROGRESS", "WAIT", "Fatura işlemi zaten sürüyor veya sonucu uzlaştırılıyor; yeni mali gönderim yapılmadı."));
                    continue;
                }

                if (invoice is null)
                {
                    // An invoice that was replaced earlier still carries this order's draft key; free it so a new draft can be created.
                    var orderDraftKey = $"invoice-workspace-order:{confirmation.OrderId:N}";
                    var replacedWithKey = await db.Invoices.Where(item => item.TenantId == tenantId && item.IdempotencyKey == orderDraftKey && item.SequencePurpose == "REPLACED").ToListAsync(cancellationToken);
                    foreach (var replaced in replacedWithKey) replaced.IdempotencyKey = $"replaced:{replaced.Id:N}";
                    if (replacedWithKey.Count > 0) await db.SaveChangesAsync(cancellationToken);
                    var created = await CreateDraftAsync(tenantId,
                        new(confirmation.OrderId, confirmation.PackageId, confirmation.ProviderConnectionId, null, request.IncludeInternetSalesInfo),
                        $"invoice-workspace-order:{confirmation.OrderId:N}", cancellationToken);
                    if (!created.Succeeded)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        results.Add(new(confirmation.PackageId, null, null, "BLOCKED", "CREATE", created.Error!.Message));
                        continue;
                    }
                    invoice = await db.Invoices.SingleAsync(item => item.TenantId == tenantId && item.Id == created.Value!.Id, cancellationToken);
                    // The fiscal document must carry exactly the amounts the user just reviewed.
                    if (invoice.TaxExclusiveTotal != preview.TaxExclusiveTotal || invoice.DiscountTotal != preview.DiscountTotal || invoice.TaxTotal != preview.TaxTotal || invoice.PayableTotal != preview.PayableTotal)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        results.Add(new(confirmation.PackageId, null, null, "BLOCKED", "REFRESH_PREVIEW", "Oluşturulan fatura tutarları önizlemeyle eşleşmedi; fatura kesilmedi. Önizlemeyi yenileyip tekrar deneyin."));
                        continue;
                    }
                }

                if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.ValidationFailed)
                {
                    var validated = await ValidateAsync(tenantId, invoice.Id, invoice.Version, cancellationToken);
                    if (!validated.Succeeded || validated.Value!.Status != InvoiceStatus.Ready.ToString().ToUpperInvariant())
                    {
                        await transaction.CommitAsync(cancellationToken);
                        results.Add(new(confirmation.PackageId, invoice.Id, null, "VALIDATION_FAILED", "REVIEW", validated.Succeeded ? InvoiceIssuePolicy.DescribeValidationCode(validated.Value!.LastErrorCode) : validated.Error!.Message));
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
                // The failed item's entities are still tracked as Added/Modified; without this the next
                // item's SaveChanges would write them again (and fail, so every remaining item became CONFLICT).
                db.ChangeTracker.Clear();
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
        var activePackages = await db.ShipmentPackages.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OrderId == order.Id && x.Status != ShipmentPackageStatus.Cancelled)
            .OrderBy(x => x.Id).ToListAsync(cancellationToken);
        if (activePackages.Count == 0)
            return ServiceResult<InvoiceWorkspacePreviewItem>.Fail("INVOICE_PACKAGE_NOT_ELIGIBLE", "Siparişte faturalanabilir etkin paket bulunamadı.", 422);
        if (activePackages.Any(item => item.ConnectionId != order.ConnectionId))
            return ServiceResult<InvoiceWorkspacePreviewItem>.Fail("INVOICE_PACKAGE_CONNECTION_MISMATCH", "Sipariş paketleri farklı pazaryeri bağlantılarına bağlı. Tek belge güvenli hedeflere ayrıştırılamadığı için fatura kesilmedi.", 422);
        var activePackageIds = activePackages.Select(item => item.Id).ToList();
        var allocations = await db.PackageLineAllocations.AsNoTracking()
            .Where(x => x.TenantId == tenantId && activePackageIds.Contains(x.PackageId)).ToListAsync(cancellationToken);
        if (!TryResolveWholeOrderInvoiceLines(sourceLines, allocations, activePackageIds, out var activeLines, out var allocationError))
            return ServiceResult<InvoiceWorkspacePreviewItem>.Fail("INVOICE_PACKAGE_LINE_ALLOCATION_INVALID", allocationError!, 422);
        var remainingByLine = sourceLines.ToDictionary(line => line.Id, line => line.OrderedQuantity - line.CancelledQuantity);

        var orderInvoices = await db.Invoices.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OrderId == order.Id && x.OriginalInvoiceId == null && x.SequencePurpose == "SALE")
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        var existing = orderInvoices.Count == 1 ? orderInvoices[0] : null;
        if (existing is not null && existing.ProviderConnectionId != provider.Id)
            return ServiceResult<InvoiceWorkspacePreviewItem>.Fail("INVOICE_PROVIDER_MISMATCH", "Mevcut faturanın sağlayıcısı kullanılmalıdır. Başka sağlayıcıyla yeniden mali fatura oluşturulamaz.", 409);
        var orderTotals = InvoiceAmounts.ResolveOrderTotals(
            order.NetAmount,
            order.GrossAmount,
            order.DiscountAmount,
            activePackages.Select(item => new InvoicePackageTotalSource(item.NetAmount, item.GrossAmount, item.DiscountAmount)).ToArray());
        var packageTotal = orderTotals.PayableAmount;
        var orderGross = orderTotals.GrossAmount;
        var orderDiscount = orderTotals.DiscountAmount;
        if (!InvoiceAmounts.TryCalculatePackage(
            activeLines.Select(line => new InvoicePackageLineSource(line.Id, line.TitleSnapshot, line.Sku,
                remainingByLine[line.Id], line.UnitPrice, line.VatRate)).ToArray(),
            orderGross, orderDiscount, packageTotal, out var calculated, out var calculationError))
            return ServiceResult<InvoiceWorkspacePreviewItem>.Fail("INVOICE_TOTAL_MISMATCH", calculationError ?? "Sipariş ürün toplamı ile sipariş/paket toplamı eşleşmiyor.", 422);

        var lines = calculated!.Lines.Select(line => new InvoiceWorkspacePreviewLine(line.Description, line.Sku, line.Quantity, "ADET", line.VatRate,
            line.UnitPrice, line.DiscountAmount, line.VatAmount, line.PayableAmount)).ToArray();

        var invoiceType = InvoiceAmounts.InvoiceDocumentType(marketplace.PlatformCode, order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson);
        var taxIdentity = ReadTaxIdentity(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson);
        var corporate = TrendyolEFaturamCanonicalPayload.IsCorporateRecipient(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, taxIdentity);
        var customerType = corporate ? "Kurumsal" : "Bireysel";
        if (!corporate) taxIdentity = "11111111111";
        else if (!TurkishTaxIdentity.IsValid(taxIdentity))
            return ServiceResult<InvoiceWorkspacePreviewItem>.Fail("EFATURAM_CORPORATE_TAX_ID_REQUIRED", "Kurumsal müşteri için geçerli 10 haneli vergi numarası veya 11 haneli T.C. kimlik numarası eksik ya da kontrol hanesi hatalı.", 422);
        var customerName = InvoiceWorkspaceCustomerName(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson);
        string? deliveryCarrierName = null;
        DateTimeOffset? deliverySentAt = null;
        string? deliveryBlockedReason = null;
        if (existing is null && invoiceType == "EARSIVFATURA")
        {
            if (!InvoicingBillingService.IsValidFiscalDeliveryPackage(new(
                    package.ExternalPackageId,
                    package.CargoProviderExternalId,
                    package.StatusOccurredAt)))
                deliveryBlockedReason = "E-Arşiv için gönderim tarihi ve E-Faturam kataloğunda doğrulanmış kargo firması gerekli. Sipariş paketini yeniden eşitleyin.";
            else
            {
                deliverySentAt = package.StatusOccurredAt;
                TrendyolCarrierCatalog.TryResolve(package.CargoProviderExternalId ?? string.Empty, out var carrier);
                deliveryCarrierName = carrier.Name;
            }
        }
        var taxTotal = calculated.TaxTotal;
        var taxExclusive = calculated.TaxExclusiveTotal;
        var remoteInvoicePresent = activePackages.Any(item => item.MarketplaceInvoiceStatus is MarketplaceInvoiceStatus.Invoiced or MarketplaceInvoiceStatus.Received);
        var legacyInvoiceConflict = orderInvoices.Count > 1;
        var manualDocument = await db.ManualOrderInvoiceDocuments.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.OrderId == order.Id && item.Status == "ACTIVE")
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new { item.Id, item.DocumentType, item.DocumentUrl, item.InvoiceNumber, item.IssuedAt })
            .FirstOrDefaultAsync(cancellationToken);
        var canCreate = !remoteInvoicePresent && !legacyInvoiceConflict && manualDocument is null && deliveryBlockedReason is null
            && (existing is not null || MarketplaceInvoiceCreationPolicy.IsEnabled(marketplace.PlatformCode, marketplace.SettingsJson))
            && InvoiceDeliveryEnvironmentPolicy.IsCompatible(provider.Environment, marketplace.Environment);
        string? blockedReason = canCreate ? null
            : remoteInvoicePresent ? "Pazaryeri siparişinde fatura bulundu veya işleniyor. Yeni mali fatura kesimi kapatıldı; önce platformdaki belgeyi eşitleyin."
            : legacyInvoiceConflict ? "Siparişte birden fazla tarihî mali fatura kaydı bulundu. Yeni kesim durduruldu; kayıtlar korunarak incelenmeli."
            : manualDocument is not null ? "Siparişe manuel fatura belgesi eklendi. İkinci mali fatura kesilmedi; aynı belgeyi platforma iletin veya yeni sürümle değiştirin."
            : deliveryBlockedReason is not null ? deliveryBlockedReason
            : existing is null && !MarketplaceInvoiceCreationPolicy.IsEnabled(marketplace.PlatformCode, marketplace.SettingsJson)
                ? string.Equals(marketplace.PlatformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase)
                    ? MarketplaceInvoiceCreationPolicy.UnsupportedFiscalProviderMessage
                    : MarketplaceInvoiceCreationPolicy.DisabledMessage
                : InvoiceDeliveryEnvironmentPolicy.DescribeMismatch(provider.Environment, marketplace.Environment);
        if (existing is null && InvoiceIssuePolicy.Refusal(invoiceType, order.Currency, order.CustomerSnapshotJson, marketplace.PlatformCode, order.InvoiceAddressSnapshotJson) is { } issueRefusal)
        {
            canCreate = false;
            blockedReason = issueRefusal.Message;
        }
        else if (existing is null && activeLines.Any(line => InvoiceIssuePolicy.IsLineVatRateUnknown(marketplace.PlatformCode, line.SourceSnapshotJson)))
        {
            canCreate = false;
            blockedReason = InvoiceIssuePolicy.LineVatRateUnknown.Message;
        }
        var nextAction = manualDocument is not null ? "MANUAL_DOCUMENT_PRESENT" : existing is null ? "CREATE_AND_SUBMIT"
            : ExistingInvoiceNextAction(existing.Status, existing.LastErrorCode, existing.ExternalReference);
        if (remoteInvoicePresent || legacyInvoiceConflict) nextAction = "REVIEW";
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
                blockedReason = await DeliveryBlockedReasonAsync(tenantId, marketplace.Id, cancellationToken);
            }
        }

        var digestSource = JsonSerializer.Serialize(new
        {
            target.OrderId,
            target.PackageId,
            target.ProviderConnectionId,
            order.OrderNumber,
            order.OrderedAt,
            order.Currency,
            OrderNetAmount = order.NetAmount,
            order.CustomerSnapshotJson,
            order.InvoiceAddressSnapshotJson,
            order.ShipmentAddressSnapshotJson,
            Packages = activePackages.Select(item => new { item.Id, item.ExternalPackageId, item.NetAmount, item.GrossAmount, item.DiscountAmount, item.Status, item.StatusOccurredAt, item.CargoProviderExternalId, item.MarketplaceInvoiceStatus, item.MarketplaceInvoiceNumber, item.MarketplaceInvoiceUrl, item.MarketplaceInvoiceSourceUpdatedAt, item.MarketplaceInvoiceObservedAt }),
            Marketplace = new { marketplace.PlatformCode, marketplace.Environment, marketplace.SettingsJson },
            Provider = new { provider.PlatformCode, provider.Environment },
            Allocations = allocations.OrderBy(item => item.PackageId).ThenBy(item => item.OrderLineId).ThenBy(item => item.SourceEventId).Select(item => new { item.PackageId, item.OrderLineId, item.AllocatedQuantity, item.SourceEventId }),
            SourceLines = sourceLines.Select(line => new { line.Id, line.TitleSnapshot, line.Sku, line.OrderedQuantity, line.CancelledQuantity, line.UnitPrice, line.VatRate }),
            Existing = orderInvoices.Select(item => new { item.Id, item.PackageId, item.Status, item.Version, item.InvoiceNumber, item.ExternalReference, item.LastErrorCode, item.OrderFiscalGuarded }),
            ManualDocument = manualDocument,
            includeInternetSalesInfo,
            Totals = new { taxExclusive, Discount = calculated.DiscountTotal, taxTotal, Payable = packageTotal, invoiceType }
        });
        var digest = Hash(digestSource);
        var canConfirm = canCreate && (nextAction is "DELIVERY_ONLY" or "SUBMIT_RETRY" or "CREATE_AND_SUBMIT");
        if (existing is not null && existing.PackageId is not null && existing.OrderFiscalGuarded)
        {
            canCreate = false;
            canConfirm = false;
            nextAction = "REVIEW";
            blockedReason = "Sipariş düzeyinde korunan eski bir mali fatura kaydı paketle ilişkili. Kayıt eşlemesi incelenmeden yeni işlem başlatılmadı.";
        }
        var view = new InvoiceWorkspacePreviewItem(order.Id, package.Id, provider.Id, order.OrderNumber, marketplace.PlatformCode,
            marketplace.DisplayName, marketplace.Environment, customerType, customerName, taxIdentity,
            string.IsNullOrWhiteSpace(order.InvoiceAddressSnapshotJson) ? "{}" : order.InvoiceAddressSnapshotJson,
            invoiceType, order.Currency, taxExclusive, calculated.DiscountTotal, taxTotal, packageTotal, lines,
            canConfirm,
            blockedReason, digest, existing?.Id, existing?.Status.ToString().ToUpperInvariant(), nextAction, includeInternetSalesInfo,
            activePackages.Select(item => item.ExternalPackageId).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray(),
            deliveryCarrierName, deliverySentAt, deliveryCarrierName is null ? null : package.ExternalPackageId);
        return ServiceResult<InvoiceWorkspacePreviewItem>.Ok(view);
    }

    // A pre-provider refusal is checked before the in-progress states: it sits in Submitting with an error code.
    internal static string ExistingInvoiceNextAction(InvoiceStatus status, string? lastErrorCode, string? externalReference) => status switch
    {
        _ when InvoiceMarketplaceRetryPolicy.CanRetryDelivery(status, lastErrorCode) => "DELIVERY_ONLY",
        InvoiceStatus.Completed => "NONE",
        _ when CanRetryPreProviderFailure(status, lastErrorCode, externalReference) => "SUBMIT_RETRY",
        InvoiceStatus.Submitting or InvoiceStatus.UnknownResult or InvoiceStatus.Submitted or InvoiceStatus.MarketplacePending => "WAIT",
        _ => "REVIEW"
    };

    // The operator saw this invoice on the platform's own seller panel; the platform cannot report which invoice a package holds, so this is the proof.
    public async Task<ServiceResult<bool>> ConfirmMarketplaceDeliveredAsync(Guid tenantId, Guid orderId, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.Where(item => item.TenantId == tenantId && item.OrderId == orderId && item.SequencePurpose == "SALE" && item.OriginalInvoiceId == null)
            .OrderByDescending(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (invoice is null) return NotFound<bool>();
        if (invoice.Status is not (InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending or InvoiceStatus.MarketplaceFailed or InvoiceStatus.ManualReview) || string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
            return ServiceResult<bool>.Fail("INVOICE_STATE_INVALID", "Bu fatura için platform teslimi doğrulanamaz; fatura sağlayıcıda kabul edilmiş ve numaralı olmalıdır.", 409);
        var needle = invoice.Id.ToString();
        if (await db.IntegrationJobs.AsNoTracking().AnyAsync(job => job.TenantId == tenantId && job.PayloadJson.Contains(needle)
            && (job.Status == JobStatus.Pending || job.Status == JobStatus.Leased || job.Status == JobStatus.RetryScheduled), cancellationToken))
            return ServiceResult<bool>.Fail("INVOICE_JOB_STILL_RUNNING", "Bu fatura için işlem hâlâ sürüyor. Birkaç dakika bekleyip tekrar deneyin.", 409);
        var packages = await db.ShipmentPackages.Where(item => item.TenantId == tenantId && (invoice.PackageId == null
                ? item.OrderId == invoice.OrderId && item.Status != ShipmentPackageStatus.Cancelled
                : item.Id == invoice.PackageId.Value)).ToListAsync(cancellationToken);
        if (packages.Count == 0) return ServiceResult<bool>.Fail("INVOICE_PACKAGE_NOT_ELIGIBLE", "Faturaya bağlı etkin paket bulunamadı.", 409);
        var now = timeProvider.GetUtcNow();
        foreach (var package in packages)
        {
            var state = await db.MarketplaceDeliveryStates.SingleOrDefaultAsync(item => item.TenantId == tenantId && item.InvoiceId == invoice.Id && item.PackageId == package.Id, cancellationToken);
            if (state is null)
            {
                state = new MarketplaceDeliveryState
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    InvoiceId = invoice.Id,
                    ConnectionId = package.ConnectionId,
                    PackageId = package.Id,
                    ExternalIdempotencyKey = $"operator-confirmed:{invoice.Id:N}:{package.Id:N}",
                    RequestHash = Hash($"operator-confirmed:{invoice.Id:N}:{package.Id:N}:{invoice.InvoiceNumber}"),
                    DeliveryType = "LINK",
                    Status = "UNKNOWN",
                    ExternalReference = package.ExternalPackageId,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Version = 1
                };
                db.MarketplaceDeliveryStates.Add(state);
            }
            var sequence = state.AttemptNumber + 1;
            db.MarketplaceDeliveries.Add(new MarketplaceDelivery
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                InvoiceId = invoice.Id,
                ConnectionId = state.ConnectionId,
                PackageId = package.Id,
                AttemptNumber = sequence,
                IdempotencyKey = $"delivery-operator-confirmed:{state.Id:N}:{sequence}",
                ExternalIdempotencyKey = state.ExternalIdempotencyKey,
                RequestHash = state.RequestHash,
                DeliveryType = state.DeliveryType,
                Status = "CONFIRMED",
                ExternalReference = package.ExternalPackageId,
                CreatedAt = now,
                CompletedAt = now
            });
            state.AttemptNumber = sequence;
            state.Status = "CONFIRMED";
            state.ErrorCode = null;
            state.ExternalReference = package.ExternalPackageId;
            state.UpdatedAt = now;
            state.CompletedAt = now;
            state.Version++;
        }
        invoice.Status = InvoiceStatus.Completed;
        invoice.LastErrorCode = null;
        invoice.UpdatedAt = now;
        invoice.Version++;
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, Action = "INVOICE_MARKETPLACE_DELIVERY_OPERATOR_CONFIRMED", TargetType = "Invoice", TargetId = invoice.Id.ToString("D"), Reason = $"operator-saw-invoice-on-platform:{invoice.InvoiceNumber}", CorrelationId = correlationId, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    // The operator cancelled the issued invoice in the provider portal. The record is kept for audit but leaves the sale slot, so a new invoice can be issued.
    public async Task<ServiceResult<bool>> ReplaceCancelledInvoiceAsync(Guid tenantId, Guid orderId, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.Where(item => item.TenantId == tenantId && item.OrderId == orderId && item.SequencePurpose == "SALE" && item.OriginalInvoiceId == null)
            .OrderByDescending(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (invoice is null) return NotFound<bool>();
        if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Validating or InvoiceStatus.Ready or InvoiceStatus.Submitting or InvoiceStatus.CancellationPending)
            return ServiceResult<bool>.Fail("INVOICE_STATE_INVALID", "Bu faturanın gönderimi tamamlanmadı; önce sonucunu netleştirin.", 409);
        if (string.IsNullOrWhiteSpace(invoice.ExternalReference) && string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
            return ServiceResult<bool>.Fail("INVOICE_NOT_ISSUED", "Bu fatura sağlayıcıda oluşturulmamış; yeniden gönderim işlemini kullanın.", 409);
        var needle = invoice.Id.ToString();
        var now = timeProvider.GetUtcNow();
        var activeJob = await db.IntegrationJobs.AsNoTracking().AnyAsync(job => job.TenantId == tenantId && job.PayloadJson.Contains(needle)
            && (job.Status == JobStatus.Pending || job.Status == JobStatus.Leased || job.Status == JobStatus.RetryScheduled), cancellationToken);
        if (activeJob) return ServiceResult<bool>.Fail("INVOICE_JOB_STILL_RUNNING", "Bu fatura için işlem hâlâ sürüyor. Birkaç dakika bekleyip tekrar deneyin.", 409);
        // Freeing the order's fiscal slot on the operator's word alone could issue a second invoice for goods
        // whose first invoice is still valid: when the provider can be asked, it must report the cancellation.
        if (await VerifyProviderCancellationAsync(invoice, correlationId, now, cancellationToken) is { } refusal) return refusal;
        invoice.SequencePurpose = "REPLACED";
        invoice.IdempotencyKey = $"replaced:{invoice.Id:N}";
        invoice.OrderFiscalGuarded = false;
        invoice.Status = InvoiceStatus.Cancelled;
        invoice.LastErrorCode = "OPERATOR_CONFIRMED_CANCELLED_AT_PROVIDER";
        invoice.UpdatedAt = now;
        invoice.Version++;
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, Action = "INVOICE_REPLACED_AFTER_PROVIDER_CANCELLATION", TargetType = "Invoice", TargetId = invoice.Id.ToString("D"), Reason = $"operator-cancelled-in-provider-portal:{invoice.InvoiceNumber}", CorrelationId = correlationId, CreatedAt = now });
        await InvoiceAdjustmentTracking.MarkLinkRevocationRequiredAsync(db, invoice, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    private async Task<ServiceResult<bool>?> VerifyProviderCancellationAsync(Invoice invoice, string correlationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (invoice.Status == InvoiceStatus.Cancelled) return null;
        var remoteId = !string.IsNullOrWhiteSpace(invoice.ExternalReference) ? invoice.ExternalReference : invoice.EttnUuid;
        // Without any provider identifier the existing operator confirmation is all there is.
        if (string.IsNullOrWhiteSpace(remoteId)) return null;
        if (!await ReadGate(invoice.TenantId, invoice.ProviderConnectionId, InvoicingCapabilities.InvoiceStatusRead, cancellationToken))
            return ServiceResult<bool>.Fail("INVOICE_PROVIDER_STATUS_UNAVAILABLE", "Fatura sağlayıcısı bağlantısı durum sorgusuna uygun değil; iptal doğrulanamadığı için kayıt değiştirilmedi.", 409);
        var remote = await invoiceProvider.QueryStatusAsync(
            new AdapterContext(invoice.TenantId, invoice.ProviderConnectionId, correlationId, $"replace-cancelled-check:{invoice.Id:N}", now.AddMinutes(1)),
            new ExternalInvoiceReference(remoteId, invoice.EttnUuid, invoice.InvoiceType), cancellationToken);
        if (!remote.IsSuccess)
            return ServiceResult<bool>.Fail("INVOICE_PROVIDER_STATUS_UNAVAILABLE", $"Faturanın sağlayıcıdaki durumu okunamadı ({remote.Error!.Code}); iptal doğrulanamadığı için kayıt değiştirilmedi.", 409);
        return string.Equals(remote.Value!.CanonicalStatus, "CANCELLED", StringComparison.Ordinal)
            ? null
            : ServiceResult<bool>.Fail("INVOICE_NOT_CANCELLED_AT_PROVIDER", "E-Faturam bu faturayı iptal edilmiş göstermiyor. Portalda iptal tamamlanmadan yeni fatura kesilemez.", 409);
    }

    // The operator checked the provider portal and saw no invoice for a submission whose result was lost; only then may it be sent again.
    public async Task<ServiceResult<bool>> ConfirmNoRemoteInvoiceAsync(Guid tenantId, Guid orderId, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.Where(item => item.TenantId == tenantId && item.OrderId == orderId && item.SequencePurpose == "SALE")
            .OrderByDescending(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (invoice is null) return NotFound<bool>();
        if (invoice.Status is not (InvoiceStatus.Submitting or InvoiceStatus.UnknownResult or InvoiceStatus.ManualReview))
            return ServiceResult<bool>.Fail("INVOICE_STATE_INVALID", "Bu faturanın gönderim sonucu belirsiz değil; yeniden gönderim açılamaz.", 409);
        if (!string.IsNullOrWhiteSpace(invoice.ExternalReference) || !string.IsNullOrWhiteSpace(invoice.EttnUuid) || !string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
            return ServiceResult<bool>.Fail("INVOICE_REMOTE_REFERENCE_EXISTS", "Faturanın sağlayıcıda kaydı var; yeniden gönderilemez. Durumu uzlaştırın.", 409);
        var attempts = await db.InvoiceSubmissionAttempts.Where(item => item.TenantId == tenantId && item.InvoiceId == invoice.Id).ToListAsync(cancellationToken);
        if (attempts.Any(item => item.Outcome == "SUCCEEDED"))
            return ServiceResult<bool>.Fail("INVOICE_REMOTE_REFERENCE_EXISTS", "Bu fatura için başarılı bir gönderim kaydı var; yeniden gönderilemez.", 409);
        var now = timeProvider.GetUtcNow();
        var needle = invoice.Id.ToString();
        var activeJob = await db.IntegrationJobs.AsNoTracking().AnyAsync(job => job.TenantId == tenantId && job.PayloadJson.Contains(needle)
            && (job.Status == JobStatus.Pending || job.Status == JobStatus.Leased || job.Status == JobStatus.RetryScheduled), cancellationToken);
        if (activeJob || invoice.UpdatedAt > now.AddMinutes(-2))
            return ServiceResult<bool>.Fail("INVOICE_SUBMISSION_STILL_RUNNING", "Bu fatura için işlem hâlâ sürüyor olabilir. Birkaç dakika bekleyip tekrar deneyin.", 409);
        foreach (var attempt in attempts.Where(item => item.Outcome == "STARTED"))
        {
            attempt.Outcome = "FAILED";
            attempt.ErrorCode = OperatorConfirmedNoRemoteInvoiceCode;
            attempt.CompletedAt = now;
        }
        invoice.Status = InvoiceStatus.ManualReview;
        invoice.LastErrorCode = OperatorConfirmedNoRemoteInvoiceCode;
        invoice.UpdatedAt = now;
        invoice.Version++;
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, Action = "EFATURAM_NO_REMOTE_INVOICE_CONFIRMED", TargetType = "Invoice", TargetId = invoice.Id.ToString("D"), Reason = "operator-checked-provider-portal", CorrelationId = correlationId, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
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
