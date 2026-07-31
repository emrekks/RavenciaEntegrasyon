using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
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

    public async Task<ServiceResult<LegalEntityProfileView>> GetLegalEntityAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var profile = await db.LegalEntityProfiles.AsNoTracking().Where(x => x.TenantId == tenantId).OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync(cancellationToken);
        return profile is null ? NotFound<LegalEntityProfileView>() : ServiceResult<LegalEntityProfileView>.Ok(Map(profile));
    }

    public async Task<ServiceResult<LegalEntityProfileView>> UpsertLegalEntityAsync(Guid tenantId, long? expectedVersion, UpsertLegalEntityProfileCommand command, CancellationToken cancellationToken)
    {
        var taxId = command.TaxId.Trim();
        if (!TaxIdPattern().IsMatch(taxId)) return Invalid<LegalEntityProfileView>("taxId", "VKN/TCKN yalnız 10 veya 11 rakam olmalıdır; bu kontrol mükellefiyet sonucu değildir.");
        if (string.IsNullOrWhiteSpace(command.Title)) return Invalid<LegalEntityProfileView>("title", "Mali unvan zorunludur.");
        if (command.Status.Trim().ToUpperInvariant() is not ("ACTIVE" or "DISABLED")) return Invalid<LegalEntityProfileView>("status", "Status ACTIVE veya DISABLED olmalıdır.");
        if (!ValidJson(command.AddressSnapshotJson) || !ValidJson(command.ContactSnapshotJson)) return Invalid<LegalEntityProfileView>("snapshot", "Adres ve iletişim snapshot alanları geçerli JSON olmalıdır.");

        var now = timeProvider.GetUtcNow();
        var profile = await db.LegalEntityProfiles.Where(x => x.TenantId == tenantId).OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync(cancellationToken);
        if (profile is null)
        {
            if (expectedVersion is not null) return NotFound<LegalEntityProfileView>();
            profile = new LegalEntityProfile { Id = Guid.CreateVersion7(), TenantId = tenantId, Title = command.Title.Trim(), ProtectedTaxId = _taxProtector.Protect(taxId), MaskedTaxId = MaskTaxId(taxId), AddressSnapshotJson = command.AddressSnapshotJson, ContactSnapshotJson = command.ContactSnapshotJson, Status = command.Status.Trim().ToUpperInvariant(), CreatedAt = now, UpdatedAt = now, Version = 1 };
            db.LegalEntityProfiles.Add(profile);
        }
        else
        {
            if (expectedVersion is null) return PreconditionRequired<LegalEntityProfileView>();
            if (profile.Version != expectedVersion) return Precondition<LegalEntityProfileView>(profile.Version);
            profile.Title = command.Title.Trim(); profile.ProtectedTaxId = _taxProtector.Protect(taxId); profile.MaskedTaxId = MaskTaxId(taxId); profile.AddressSnapshotJson = command.AddressSnapshotJson; profile.ContactSnapshotJson = command.ContactSnapshotJson; profile.Status = command.Status.Trim().ToUpperInvariant(); profile.UpdatedAt = now; profile.Version++;
        }
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<LegalEntityProfileView>.Ok(Map(profile));
    }

    public async Task<ServiceResult<InvoicePolicyView>> GetPolicyAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var policy = await db.InvoicePolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProviderConnectionId == connectionId, cancellationToken);
        return policy is null ? NotFound<InvoicePolicyView>() : ServiceResult<InvoicePolicyView>.Ok(Map(policy));
    }

    public async Task<ServiceResult<InvoicePolicyView>> UpsertPolicyAsync(Guid tenantId, Guid connectionId, long? expectedVersion, UpsertInvoicePolicyCommand command, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "TRENDYOL_EFATURAM", cancellationToken);
        if (connection is null) return Invalid<InvoicePolicyView>("connectionId", "Trendyol E-Faturam provider bağlantısı bulunamadı.");
        if (new[] { command.TriggerState, command.PackageScope, command.DueRule, command.RoundingRule, command.AdjustmentRule }.Any(string.IsNullOrWhiteSpace)) return Invalid<InvoicePolicyView>("policy", "Policy alanları açık bir değer veya UNAPPROVED taşımalıdır.");
        if (new[] { command.TriggerState, command.PackageScope, command.DueRule, command.RoundingRule, command.AdjustmentRule }.Select(Normalize).Any(x => x != "UNAPPROVED")) return ServiceResult<InvoicePolicyView>.Fail("FISCAL_POLICY_DECISION_REQUIRED", "Mali karar kaydı tamamlanana kadar policy alanları yalnız UNAPPROVED olabilir.", 422);
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

    public async Task<ServiceResult<InvoiceDetailView>> CreateDraftAsync(Guid tenantId, CreateInvoiceCommand command, string idempotencyKey, CancellationToken cancellationToken)
    {
        var existing = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null) return await GetAsync(tenantId, existing.Id, cancellationToken);
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == command.OrderId, cancellationToken);
        if (order is null) return Invalid<InvoiceDetailView>("orderId", "Sipariş bulunamadı.");
        if (command.PackageId is { } packageId && !await db.ShipmentPackages.AnyAsync(x => x.TenantId == tenantId && x.Id == packageId && x.OrderId == command.OrderId, cancellationToken)) return Invalid<InvoiceDetailView>("packageId", "Paket siparişe ait değil.");
        var provider = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == command.ProviderConnectionId && x.PlatformCode == "TRENDYOL_EFATURAM", cancellationToken);
        var profile = await db.LegalEntityProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == command.LegalEntityProfileId && x.Status == "ACTIVE", cancellationToken);
        var policy = await db.InvoicePolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == command.InvoicePolicyId && x.ProviderConnectionId == command.ProviderConnectionId, cancellationToken);
        if (provider is null || profile is null || policy is null) return Invalid<InvoiceDetailView>("billing", "Aktif provider bağlantısı, legal entity ve bu bağlantıya ait policy zorunludur.");
        if (command.OriginalInvoiceId is { } originalId && !await db.Invoices.AnyAsync(x => x.TenantId == tenantId && x.Id == originalId, cancellationToken)) return Invalid<InvoiceDetailView>("originalInvoiceId", "Orijinal fatura bulunamadı.");

        var orderLines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        if (orderLines.Count == 0) return Invalid<InvoiceDetailView>("orderId", "Fatura taslağı için sipariş satırı gerekir.");
        var now = timeProvider.GetUtcNow(); var invoice = new Invoice
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OrderId = order.Id,
            PackageId = command.PackageId,
            ProviderConnectionId = provider.Id,
            LegalEntityProfileId = profile.Id,
            InvoicePolicyId = policy.Id,
            InvoiceType = "UNDETERMINED",
            SequencePurpose = command.OriginalInvoiceId is null ? "SALE" : "ADJUSTMENT",
            Currency = order.Currency,
            IdempotencyKey = idempotencyKey,
            OriginalInvoiceId = command.OriginalInvoiceId,
            Status = InvoiceStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
        var lines = orderLines.Select((line, index) => new InvoiceLine { Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = invoice.Id, OrderLineId = line.Id, LineSequence = index + 1, DescriptionSnapshot = line.TitleSnapshot, SkuSnapshot = line.Sku, UnitSnapshot = "UNSPECIFIED", Quantity = line.OrderedQuantity - line.CancelledQuantity, UnitPrice = line.UnitPrice, DiscountAmount = 0, VatRate = line.VatRate, VatAmount = 0, LineTotal = (line.OrderedQuantity - line.CancelledQuantity) * line.UnitPrice }).ToList();
        invoice.TaxExclusiveTotal = lines.Sum(x => x.LineTotal); invoice.DiscountTotal = 0; invoice.TaxTotal = 0; invoice.PayableTotal = lines.Sum(x => x.LineTotal);
        db.Invoices.Add(invoice); db.InvoiceLines.AddRange(lines);
        var sellerJson = JsonSerializer.Serialize(new { profile.Title, TaxId = _taxProtector.Unprotect(profile.ProtectedTaxId), profile.AddressSnapshotJson, profile.ContactSnapshotJson });
        var receiverJson = JsonSerializer.Serialize(new { order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson });
        db.InvoicePartySnapshots.AddRange(Snapshot(invoice, "SELLER", sellerJson, now), Snapshot(invoice, "RECEIVER", receiverJson, now));
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
        return ServiceResult<InvoiceDetailView>.Ok(new(invoice.Id, invoice.OrderId, orderNumber, invoice.PackageId, invoice.ProviderConnectionId, invoice.InvoiceType, invoice.SequencePurpose, Status(invoice.Status), invoice.Currency, invoice.TaxExclusiveTotal, invoice.DiscountTotal, invoice.TaxTotal, invoice.PayableTotal, invoice.InvoiceNumber, invoice.EttnUuid, invoice.DueAt, invoice.IssuedAt, invoice.LastErrorCode, lines, documents, attempts, deliveries, await AllowedActions(invoice, cancellationToken), invoice.Version));
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
        if (lines.Sum(x => x.LineTotal) != invoice.PayableTotal) failures.Add("INVOICE_TOTAL_MISMATCH");
        if (lines.Any(x => x.UnitSnapshot == "UNSPECIFIED" || x.VatRate != 0 && x.VatAmount == 0)) failures.Add("FISCAL_CALCULATION_AUTHORITY_REQUIRED");
        if (Unapproved(policy.RoundingRule) || Unapproved(policy.DueRule) || Unapproved(policy.AdjustmentRule)) failures.Add("FISCAL_POLICY_UNAPPROVED");
        if (invoice.InvoiceType == "UNDETERMINED") failures.Add("TAXPAYER_RESULT_REQUIRED");
        invoice.Status = failures.Count == 0 ? InvoiceStatus.Ready : InvoiceStatus.ValidationFailed; invoice.LastErrorCode = failures.FirstOrDefault(); invoice.Version++; invoice.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(tenantId, id, cancellationToken);
    }

    public Task<ServiceResult<Guid>> EnqueueSubmitAsync(Guid tenantId, Guid id, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken) => EnqueueWrite(tenantId, id, expectedVersion, idempotencyKey, correlationId, F4JobTypes.InvoiceSubmit, F4Capabilities.InvoiceSubmit, [InvoiceStatus.Ready], InvoiceStatus.Submitting, cancellationToken);
    public Task<ServiceResult<Guid>> EnqueueCancellationAsync(Guid tenantId, Guid id, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken) => EnqueueWrite(tenantId, id, expectedVersion, idempotencyKey, correlationId, F4JobTypes.InvoiceCancellation, F4Capabilities.InvoiceCancel, [InvoiceStatus.Accepted, InvoiceStatus.Completed], InvoiceStatus.CancellationPending, cancellationToken);

    public async Task<ServiceResult<Guid>> EnqueueReconcileAsync(Guid tenantId, Guid id, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (invoice is null) return NotFound<Guid>();
        if (invoice.Status is not (InvoiceStatus.UnknownResult or InvoiceStatus.Submitted or InvoiceStatus.MarketplacePending or InvoiceStatus.MarketplaceFailed)) return ServiceResult<Guid>.Fail("INVOICE_STATE_INVALID", "Bu durumda provider reconciliation çalıştırılamaz.", 409);
        if (!await CapabilitySupported(tenantId, invoice.ProviderConnectionId, F4Capabilities.InvoiceStatusRead, cancellationToken)) return CapabilityUnknown<Guid>(F4Capabilities.InvoiceStatusRead);
        return await AddJob(invoice, F4JobTypes.InvoiceReconcile, idempotencyKey, correlationId, cancellationToken);
    }

    public async Task<ServiceResult<Guid>> EnqueueDeliveryAsync(Guid tenantId, Guid id, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (invoice is null) return NotFound<Guid>();
        if (invoice.Status is not (InvoiceStatus.Accepted or InvoiceStatus.MarketplaceFailed)) return ServiceResult<Guid>.Fail("INVOICE_STATE_INVALID", "Fatura pazaryerine iletime hazır değil.", 409);
        if (invoice.PackageId is null) return Invalid<Guid>("packageId", "Pazaryeri fatura iletimi için paket zorunludur.");
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
        invoice.Status = next; invoice.UpdatedAt = timeProvider.GetUtcNow(); invoice.Version++; return await AddJob(invoice, jobType, idempotencyKey, correlationId, cancellationToken);
    }

    private async Task<ServiceResult<Guid>> AddJob(Invoice invoice, string jobType, string idempotencyKey, string correlationId, CancellationToken cancellationToken, Guid? connectionId = null)
    {
        var dedup = $"{jobType}:{invoice.Id}:{idempotencyKey}"; var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == invoice.TenantId && x.JobType == jobType && x.JobDedupKey == dedup, cancellationToken); if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var payload = JsonSerializer.Serialize(new { invoiceId = invoice.Id }); var job = new IntegrationJob { Id = Guid.CreateVersion7(), TenantId = invoice.TenantId, ConnectionId = connectionId ?? invoice.ProviderConnectionId, JobType = jobType, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = $"{jobType}:{invoice.Id}:{idempotencyKey}", AvailableAt = timeProvider.GetUtcNow(), CorrelationId = correlationId, Version = 1 };
        db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(job.Id);
    }

    private async Task<IReadOnlyList<string>> AllowedActions(Invoice invoice, CancellationToken cancellationToken)
    {
        var actions = new List<string>(); if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.ValidationFailed) actions.Add("VALIDATE");
        if (invoice.Status == InvoiceStatus.Ready && await WriteGates(invoice.TenantId, invoice.ProviderConnectionId, F4Capabilities.InvoiceSubmit, cancellationToken)) actions.Add("SUBMIT");
        if (invoice.Status is InvoiceStatus.UnknownResult or InvoiceStatus.Submitted && await CapabilitySupported(invoice.TenantId, invoice.ProviderConnectionId, F4Capabilities.InvoiceStatusRead, cancellationToken)) actions.Add("RECONCILE");
        if (invoice.Status is InvoiceStatus.Accepted or InvoiceStatus.MarketplaceFailed && invoice.PackageId is not null)
        {
            var marketplaceConnectionId = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == invoice.TenantId && x.Id == invoice.PackageId).Select(x => (Guid?)x.ConnectionId).SingleOrDefaultAsync(cancellationToken);
            if (marketplaceConnectionId is not null && await WriteGates(invoice.TenantId, marketplaceConnectionId.Value, F4Capabilities.InvoiceDeliver, cancellationToken)) actions.Add("DELIVER");
        }
        if (invoice.Status is InvoiceStatus.Accepted or InvoiceStatus.Completed && await WriteGates(invoice.TenantId, invoice.ProviderConnectionId, F4Capabilities.InvoiceCancel, cancellationToken)) actions.Add("CANCEL");
        return actions;
    }

    private async Task<bool> WriteGates(Guid tenantId, Guid connectionId, string capability, CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>("FeatureFlags:ExternalWrites") || !await AutoInvoiceEnabled(tenantId, cancellationToken) || !await CapabilitySupported(tenantId, connectionId, capability, cancellationToken)) return false;
        var settings = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.SettingsJson).SingleOrDefaultAsync(cancellationToken);
        if (settings is null) return false; try { return JsonDocument.Parse(settings).RootElement.TryGetProperty("ExternalWritesEnabled", out var value) && value.ValueKind == JsonValueKind.True; } catch (JsonException) { return false; }
    }
    private Task<bool> CapabilitySupported(Guid tenantId, Guid connectionId, string code, CancellationToken cancellationToken) => db.PlatformCapabilities.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == code && x.SupportLevel == CapabilitySupportLevel.Supported, cancellationToken);
    private async Task<bool> AutoInvoiceEnabled(Guid tenantId, CancellationToken cancellationToken) => await db.FeatureFlags.AsNoTracking().AnyAsync(x => x.Key == "AUTO_INVOICE_ENABLED" && x.Enabled, cancellationToken);
    private InvoicePartySnapshot Snapshot(Invoice invoice, string role, string content, DateTimeOffset now) => new() { Id = Guid.CreateVersion7(), TenantId = invoice.TenantId, InvoiceId = invoice.Id, Role = role, ProtectedContent = _partyProtector.Protect(content), ContentHash = Hash(content), CreatedAt = now };
    private Guid Decode(string? cursor) => cursors.TryDecode(cursor, out var id) ? id : throw new ArgumentException("Cursor geçersiz veya süresi dolmuş.", nameof(cursor));
    private static bool ValidJson(string value) { try { JsonDocument.Parse(value); return true; } catch (JsonException) { return false; } }
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static bool Unapproved(string value) => value is "UNKNOWN" or "UNAPPROVED";
    private static string Status(InvoiceStatus value) => value.ToString().ToUpperInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string MaskTaxId(string value) => value.Length <= 4 ? "****" : new string('*', value.Length - 4) + value[^4..];
    private static LegalEntityProfileView Map(LegalEntityProfile value) => new(value.Id, value.Title, value.MaskedTaxId, value.Status, value.UpdatedAt, value.Version);
    private static InvoicePolicyView Map(InvoicePolicy value) => new(value.Id, value.ProviderConnectionId, value.TriggerState, value.PackageScope, value.DueRule, value.RoundingRule, value.AdjustmentRule, value.AutoSubmit, value.Version);
    private static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Precondition<T>(long version) => ServiceResult<T>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{version}.", 412);
    private static ServiceResult<T> PreconditionRequired<T>() => ServiceResult<T>.Fail("PRECONDITION_REQUIRED", "Mevcut kayıt için If-Match gereklidir.", 428);
    private static ServiceResult<T> CapabilityUnknown<T>(string capability) => ServiceResult<T>.Fail("CAPABILITY_NOT_SUPPORTED", $"{capability} doğrulanmadığı için dış işlem kapalıdır.", 422);

    [GeneratedRegex("^[0-9]{10,11}$", RegexOptions.CultureInvariant)]
    private static partial Regex TaxIdPattern();
}
