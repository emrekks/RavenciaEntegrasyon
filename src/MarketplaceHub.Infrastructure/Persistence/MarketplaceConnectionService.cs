using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class MarketplaceConnectionService(AppDbContext db, CursorCodec cursors, IDataProtectionProvider dataProtection, TokenHasher tokenHasher, TimeProvider timeProvider, IConfiguration configuration) : IMarketplaceConnectionService
{
    private static readonly string[] TrendyolCapabilityCodes =
    [
        MarketplaceCapabilities.ConnectionTest, MarketplaceCapabilities.ReferenceRead, MarketplaceCapabilities.ProductRead, MarketplaceCapabilities.ProductWrite,
        MarketplaceCapabilities.InventoryWrite, MarketplaceCapabilities.PriceWrite, MarketplaceCapabilities.OrderRead, MarketplaceCapabilities.OrderWebhook,
        MarketplaceCapabilities.ShipmentWrite, MarketplaceCapabilities.LabelRead, MarketplaceCapabilities.LabelWrite, MarketplaceCapabilities.ReturnRead, MarketplaceCapabilities.ReturnWrite,
        InvoicingCapabilities.InvoiceDeliver
    ];
    private static readonly string[] EfaturamCapabilityCodes =
    [
        InvoicingCapabilities.ConnectionTest, InvoicingCapabilities.InvoiceSubmit,
        InvoicingCapabilities.InvoiceStatusRead, InvoicingCapabilities.InvoiceDocumentRead, InvoicingCapabilities.InvoiceCancel
    ];
    private static readonly HashSet<string> ResourceTypes = new(StringComparer.Ordinal) { "ORDERS", "ORDER_RECOVERY", "ORDER_LIFECYCLE", "ORDER_RECONCILE_SHORT", "ORDER_RECONCILE_MEDIUM", "ORDER_RECONCILE_DAILY", "RETURNS", "RETURN_LIFECYCLE", "RETURN_RECONCILE_DAILY", "STOCK_RECONCILE_SHORT", "STOCK_RECONCILE_MEDIUM", "STOCK_RECONCILE_DAILY", "REFERENCE_DATA", "PRODUCTS" };
    private readonly IDataProtector _credentialProtector = dataProtection.CreateProtector("MarketplaceHub.PlatformCredential.v1");
    private readonly IDataProtector _webhookProtector = dataProtection.CreateProtector("MarketplaceHub.WebhookVerifier.v1");

    public async Task<PageResult<ConnectionView>> ListAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken)
    {
        var afterId = Decode(after); var query = db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Status != "DELETED" && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "TRENDYOL_EFATURAM"));
        if (afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0);
        var rows = await query.OrderBy(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var credentialIds = await ActiveCredentialConnectionIds(tenantId, rows.Select(x => x.Id), cancellationToken);
        return Page(rows, limit, x => Map(x, credentialIds.Contains(x.Id)));
    }

    public async Task<ServiceResult<ConnectionView>> CreateAsync(Guid tenantId, CreateConnectionCommand command, CancellationToken cancellationToken)
    {
        var platform = string.IsNullOrWhiteSpace(command.PlatformCode) ? "TRENDYOL" : command.PlatformCode.Trim().ToUpperInvariant();
        if (!ActiveIntegrationScope.Contains(platform)) return Invalid<ConnectionView>("platformCode", "ADR-016 kapsamında yalnız TRENDYOL veya TRENDYOL_EFATURAM bağlantısı oluşturulabilir.");
        var environment = command.Environment.Trim().ToUpperInvariant();
        if (environment is not ("STAGE" or "PRODUCTION")) return Invalid<ConnectionView>("environment", "Environment yalnız STAGE veya PRODUCTION olabilir.");
        var apiVersion = command.ApiVersion.Trim();
        if (platform == "TRENDYOL" && !string.Equals(apiVersion, "V2", StringComparison.OrdinalIgnoreCase)) return Invalid<ConnectionView>("apiVersion", "Trendyol marketplace bağlantısı yalnız Product Integration V2 kullanır.");
        if (platform == "TRENDYOL_EFATURAM" && !string.Equals(apiVersion, "1.0.0", StringComparison.OrdinalIgnoreCase)) return Invalid<ConnectionView>("apiVersion", "E-Faturam bağlantısı doğrulanmış doküman sürümü 1.0.0 ile pinlenmelidir.");
        if (string.IsNullOrWhiteSpace(command.DisplayName) || string.IsNullOrWhiteSpace(command.ExternalStoreId) || platform == "TRENDYOL" && string.IsNullOrWhiteSpace(command.UserAgentIdentity)) return Invalid<ConnectionView>("connection", "Ad ve dış mağaza/firma kapsamı; Trendyol için ayrıca User-Agent kimliği zorunludur.");
        if (await db.PlatformConnections.AnyAsync(x => x.TenantId == tenantId && x.PlatformCode == platform && x.Environment == environment && x.ExternalStoreId == command.ExternalStoreId.Trim(), cancellationToken)) return ServiceResult<ConnectionView>.Fail("CONNECTION_ALREADY_EXISTS", "Bu platform kapsamı ve environment için bağlantı zaten var.", 409);

        var now = timeProvider.GetUtcNow(); var connection = new PlatformConnection
        {
            Id = Guid.CreateVersion7(),
            PublicId = Guid.NewGuid(),
            TenantId = tenantId,
            PlatformCode = platform,
            Environment = environment,
            DisplayName = command.DisplayName.Trim(),
            ExternalStoreId = command.ExternalStoreId.Trim(),
            ApiVersion = platform == "TRENDYOL" ? "V2" : "1.0.0",
            Status = "DRAFT",
            SettingsJson = platform == "TRENDYOL" ? JsonSerializer.Serialize(new ConnectionSettings(command.UserAgentIdentity!.Trim(), false)) : JsonSerializer.Serialize(new TrendyolEFaturamConnectionSettings(false)),
            Version = 1
        };
        db.PlatformConnections.Add(connection);
        var capabilities = platform == "TRENDYOL" ? TrendyolCapabilityCodes : EfaturamCapabilityCodes;
        db.PlatformCapabilities.AddRange(capabilities.Select(code => new PlatformCapability
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connection.Id,
            Code = code,
            SupportLevel = CapabilitySupportLevel.Unknown,
            ApiVersion = connection.ApiVersion,
            Environment = environment,
            StoreScope = connection.ExternalStoreId,
            EvidenceNote = "Stage/SIT kanıtı bekleniyor.",
            Version = 1
        }));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<ConnectionView>.Ok(Map(connection, false));
    }

    public async Task<ServiceResult<ConnectionView>> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var connection = await Find(tenantId, id, cancellationToken); return connection is null ? NotFound<ConnectionView>() : ServiceResult<ConnectionView>.Ok(Map(connection, await HasCredential(tenantId, id, cancellationToken)));
    }

    public async Task<ServiceResult<ConnectionView>> UpdateAsync(Guid tenantId, Guid id, long expectedVersion, UpdateConnectionCommand command, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "TRENDYOL_EFATURAM"), cancellationToken); if (connection is null) return NotFound<ConnectionView>(); if (!ActiveIntegrationScope.Contains(connection.PlatformCode)) return Deferred<ConnectionView>(); if (connection.Version != expectedVersion) return Precondition<ConnectionView>(connection.Version);
        if (string.IsNullOrWhiteSpace(command.DisplayName)) return Invalid<ConnectionView>("connection", "Bağlantı adı zorunludur.");
        if (command.Environment is not null && command.Environment is not ("STAGE" or "PRODUCTION")) return Invalid<ConnectionView>("environment", "Ortam STAGE veya PRODUCTION olmalıdır.");
        connection.DisplayName = command.DisplayName.Trim();
        if (command.Environment is not null) connection.Environment = command.Environment;
        if (command.ExternalStoreId is not null) connection.ExternalStoreId = command.ExternalStoreId.Trim();
        if (connection.PlatformCode == "TRENDYOL")
        {
            var current = ReadSettings(connection);
            connection.SettingsJson = JsonSerializer.Serialize(new ConnectionSettings(string.IsNullOrWhiteSpace(command.UserAgentIdentity) ? current.UserAgentIdentity : command.UserAgentIdentity.Trim(), current.ExternalWritesEnabled));
        }
        else
            connection.SettingsJson = JsonSerializer.Serialize(new TrendyolEFaturamConnectionSettings(ReadEfaturamSettings(connection).ExternalWritesEnabled));
        connection.Version++;
        await db.SaveChangesAsync(cancellationToken); return ServiceResult<ConnectionView>.Ok(Map(connection, await HasCredential(tenantId, id, cancellationToken)));
    }

    public async Task<ServiceResult<ConnectionView>> RotateCredentialAsync(Guid tenantId, Guid id, long expectedVersion, CredentialCommand command, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "TRENDYOL_EFATURAM"), cancellationToken); if (connection is null) return NotFound<ConnectionView>(); if (!ActiveIntegrationScope.Contains(connection.PlatformCode)) return Deferred<ConnectionView>(); if (connection.Version != expectedVersion) return Precondition<ConnectionView>(connection.Version);
        if (connection.PlatformCode == "TRENDYOL" && (string.IsNullOrWhiteSpace(command.ApiKey) || string.IsNullOrWhiteSpace(command.ApiSecret))) return Invalid<ConnectionView>("credential", "Trendyol için API key ve secret zorunludur.");
        TrendyolEFaturamCredentialPayload? efaturamCredential = null;
        if (connection.PlatformCode == "TRENDYOL_EFATURAM")
        {
            if (string.IsNullOrWhiteSpace(command.Email) || string.IsNullOrWhiteSpace(command.Password))
                return Invalid<ConnectionView>("credential", "E-Faturam hesabı için e-posta ve parola zorunludur.");
            efaturamCredential = new(command.Email.Trim(), command.Password);
        }
        var now = timeProvider.GetUtcNow(); var current = await db.PlatformCredentials.Where(x => x.TenantId == tenantId && x.ConnectionId == id && x.RevokedAt == null).ToListAsync(cancellationToken); foreach (var item in current) { item.RevokedAt = now; item.Version++; }
        var payload = connection.PlatformCode == "TRENDYOL"
            ? JsonSerializer.Serialize(new CredentialPayload(command.ApiKey!, command.ApiSecret!))
            : JsonSerializer.Serialize(efaturamCredential!);
        var hint = connection.PlatformCode == "TRENDYOL" ? Mask(command.ApiKey!) : MaskEmail(efaturamCredential!.Email!);
        var credentialType = connection.PlatformCode == "TRENDYOL" ? "BASIC" : "EMAIL_PASSWORD";
        db.PlatformCredentials.Add(new PlatformCredential { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, CredentialType = credentialType, ProtectedPayload = _credentialProtector.Protect(payload), MaskedHint = hint, CreatedAt = now, Version = 1 });
        if (connection.PlatformCode == "TRENDYOL_EFATURAM")
            connection.SettingsJson = JsonSerializer.Serialize(new TrendyolEFaturamConnectionSettings(ReadEfaturamSettings(connection).ExternalWritesEnabled));
        connection.LastTestedAt = null; connection.LastSuccessAt = null; connection.LastErrorCode = null; connection.Status = "DRAFT"; connection.Version++;
        foreach (var capability in await db.PlatformCapabilities.Where(x => x.TenantId == tenantId && x.ConnectionId == id).ToListAsync(cancellationToken)) { capability.SupportLevel = CapabilitySupportLevel.Unknown; capability.VerifiedAt = null; capability.EvidenceNote = "Credential rotasyonu sonrası yeniden doğrulama gerekiyor."; capability.Version++; }
        await db.SaveChangesAsync(cancellationToken); return ServiceResult<ConnectionView>.Ok(Map(connection, true));
    }

    public async Task<ServiceResult<Guid>> EnqueueTestAsync(Guid tenantId, Guid id, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await Find(tenantId, id, cancellationToken); if (connection is null) return NotFound<Guid>(); if (!ActiveIntegrationScope.Contains(connection.PlatformCode)) return Deferred<Guid>(); if (!await HasCredential(tenantId, id, cancellationToken)) return ServiceResult<Guid>.Fail("CREDENTIAL_REQUIRED", "Bağlantı testi için şifreli credential gerekir.", 422);
        var jobType = connection.PlatformCode == "TRENDYOL" ? MarketplaceJobTypes.ConnectionTest : InvoicingJobTypes.ConnectionTest;
        var dedup = $"connection-test:{connection.Id}:{idempotencyKey}"; var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == jobType && x.JobDedupKey == dedup, cancellationToken); if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var payload = JsonSerializer.Serialize(new { connectionId = id }); var job = NewJob(tenantId, id, jobType, dedup, payload, correlationId); db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(job.Id);
    }

    public async Task<ServiceResult<ConnectionView>> SetActiveAsync(Guid tenantId, Guid id, long expectedVersion, bool active, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "TRENDYOL_EFATURAM"), cancellationToken); if (connection is null) return NotFound<ConnectionView>(); if (!ActiveIntegrationScope.Contains(connection.PlatformCode) && active) return Deferred<ConnectionView>(); if (connection.Version != expectedVersion) return Precondition<ConnectionView>(connection.Version);
        if (active)
        {
            var connectionTest = await db.PlatformCapabilities.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == id && x.Code == MarketplaceCapabilities.ConnectionTest, cancellationToken);
            if (connection.LastSuccessAt is null || connectionTest?.SupportLevel != CapabilitySupportLevel.Supported) return ServiceResult<ConnectionView>.Fail("CONNECTION_TEST_REQUIRED", "Bağlantı etkinleştirilmeden önce başarılı Stage/Production testi gerekir.", 422);
            connection.Status = "ACTIVE";
        }
        else connection.Status = "DISABLED";
        if (connection.PlatformCode == "TRENDYOL_EFATURAM")
            connection.SettingsJson = JsonSerializer.Serialize(new TrendyolEFaturamConnectionSettings(ReadEfaturamSettings(connection).ExternalWritesEnabled));
        connection.Version++; await db.SaveChangesAsync(cancellationToken); return ServiceResult<ConnectionView>.Ok(Map(connection, await HasCredential(tenantId, id, cancellationToken)));
    }

    public async Task<ServiceResult<IReadOnlyList<CapabilityView>>> CapabilitiesAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "TRENDYOL_EFATURAM"), cancellationToken);
        if (connection is null) return NotFound<IReadOnlyList<CapabilityView>>();
        await EnsureCapabilityRowsAsync(connection, cancellationToken);
        var rows = await db.PlatformCapabilities.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == id).OrderBy(x => x.Code).Select(x => new CapabilityView(x.Code, x.SupportLevel.ToString().ToUpperInvariant(), x.ApiVersion, x.Environment, x.StoreScope, x.SourceUrl, x.VerifiedAt, x.ConstraintsJson, x.EvidenceNote, x.Version)).ToListAsync(cancellationToken);
        return ServiceResult<IReadOnlyList<CapabilityView>>.Ok(rows);
    }

    public async Task<ServiceResult<CapabilityView>> RecordCapabilityEvidenceAsync(Guid tenantId, Guid actorUserId, Guid id, string code, long expectedVersion, RecordCapabilityEvidenceCommand command, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "TRENDYOL_EFATURAM"), cancellationToken);
        if (connection is null) return NotFound<CapabilityView>();
        await EnsureCapabilityRowsAsync(connection, cancellationToken);
        var normalizedCode = code.Trim().ToUpperInvariant();
        var expectedCodes = connection.PlatformCode == "TRENDYOL" ? TrendyolCapabilityCodes : EfaturamCapabilityCodes;
        if (!expectedCodes.Contains(normalizedCode, StringComparer.Ordinal)) return Invalid<CapabilityView>("code", "Bu bağlantı türü için tanımlı capability değildir.");
        var capability = await db.PlatformCapabilities.SingleAsync(x => x.TenantId == tenantId && x.ConnectionId == id && x.Code == normalizedCode, cancellationToken);
        if (capability.Version != expectedVersion) return Precondition<CapabilityView>(capability.Version);

        var support = command.SupportLevel.Trim().ToUpperInvariant();
        if (support is not ("SUPPORTED" or "UNKNOWN" or "NOT_SUPPORTED")) return Invalid<CapabilityView>("supportLevel", "Support level SUPPORTED, UNKNOWN veya NOT_SUPPORTED olmalıdır.");
        if (!string.Equals(command.Environment.Trim(), connection.Environment, StringComparison.OrdinalIgnoreCase) || !string.Equals(command.StoreScope.Trim(), connection.ExternalStoreId, StringComparison.Ordinal))
            return Invalid<CapabilityView>("scope", "Evidence environment ve store scope bağlantıyla bire bir eşleşmelidir.");
        var officialEvidenceHost = CapabilityEvidencePolicy.OfficialDocumentationHost(connection.PlatformCode);
        if (!Uri.TryCreate(command.SourceUrl.Trim(), UriKind.Absolute, out var sourceUri) || sourceUri.Scheme != Uri.UriSchemeHttps || !sourceUri.Host.Equals(officialEvidenceHost, StringComparison.OrdinalIgnoreCase))
            return Invalid<CapabilityView>("sourceUrl", $"Capability evidence yalnız resmî HTTPS {officialEvidenceHost} kaynağına dayanabilir.");
        if (string.IsNullOrWhiteSpace(command.SourceVersion) || string.IsNullOrWhiteSpace(command.EvidenceNote) || command.EvidenceNote.Trim().Length > 1000)
            return Invalid<CapabilityView>("evidenceNote", "Kaynak sürümü ve en fazla 1000 karakterlik evidence note zorunludur.");
        var now = timeProvider.GetUtcNow();
        if (command.VerifiedAt > now.AddMinutes(5) || command.VerifiedAt < now.AddYears(-2)) return Invalid<CapabilityView>("verifiedAt", "Doğrulama zamanı gelecekte veya iki yıldan eski olamaz.");
        if (command.ConstraintsJson is not null)
        {
            try { using var constraints = JsonDocument.Parse(command.ConstraintsJson); if (constraints.RootElement.ValueKind != JsonValueKind.Object) return Invalid<CapabilityView>("constraintsJson", "Capability constraints JSON nesnesi olmalıdır."); }
            catch (JsonException) { return Invalid<CapabilityView>("constraintsJson", "Capability constraints geçerli JSON olmalıdır."); }
        }
        var writeCapability = CapabilityEvidencePolicy.RequiresStageFixtureChecksum(normalizedCode);
        var checksum = command.FixtureChecksum?.Trim().ToUpperInvariant();
        if (support == "SUPPORTED" && writeCapability && (checksum is null || checksum.Length != 64 || checksum.Any(x => !Uri.IsHexDigit(x))))
            return Invalid<CapabilityView>("fixtureChecksum", "Write capability SUPPORTED yapılırken 64 haneli SHA-256 Stage/SIT fixture checksum zorunludur.");

        capability.SupportLevel = support switch { "SUPPORTED" => CapabilitySupportLevel.Supported, "NOT_SUPPORTED" => CapabilitySupportLevel.NotSupported, _ => CapabilitySupportLevel.Unknown };
        capability.SourceUrl = sourceUri.ToString(); capability.SourceVersion = command.SourceVersion.Trim(); capability.Environment = connection.Environment; capability.StoreScope = connection.ExternalStoreId;
        capability.EvidenceNote = command.EvidenceNote.Trim(); capability.FixtureChecksum = checksum; capability.ConstraintsJson = command.ConstraintsJson; capability.VerifiedAt = command.VerifiedAt; capability.Version++;
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = actorUserId, Action = "CAPABILITY_EVIDENCE_RECORDED", TargetType = "PlatformCapability", TargetId = capability.Id.ToString("D"), Reason = $"{normalizedCode}:{support}", CorrelationId = correlationId, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<CapabilityView>.Ok(new(capability.Code, capability.SupportLevel.ToString().ToUpperInvariant(), capability.ApiVersion, capability.Environment, capability.StoreScope, capability.SourceUrl, capability.VerifiedAt, capability.ConstraintsJson, capability.EvidenceNote, capability.Version));
    }

    public async Task<ServiceResult<IReadOnlyList<SyncPolicyView>>> SyncPoliciesAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        if (!await db.PlatformConnections.AnyAsync(x => x.TenantId == tenantId && x.Id == id && x.PlatformCode == "TRENDYOL", cancellationToken)) return NotFound<IReadOnlyList<SyncPolicyView>>();
        var policies = await db.ConnectionSyncPolicies.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == id).OrderBy(x => x.ResourceType).ToListAsync(cancellationToken);
        var cursors = await db.SyncCursors.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == id).ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var delayedAfter = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:Health:DelayedAfterSeconds", 120), 30, 86_400));
        var degradedAfter = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:Health:DegradedAfterSeconds", 600), (int)delayedAfter.TotalSeconds + 1, 172_800));
        var offlineAfter = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:Health:OfflineAfterSeconds", 1800), (int)degradedAfter.TotalSeconds + 1, 604_800));
        var rows = policies.Select(x =>
        {
            var cursor = cursors.FirstOrDefault(candidate => candidate.ResourceType == x.ResourceType)
                ?? (x.ResourceType == "ORDERS" ? cursors.FirstOrDefault(candidate => candidate.ResourceType == "ORDERS_HOT") : null);
            var health = MarketplaceSyncHealthPolicy.Classify(cursor?.LastSuccessAt, now, delayedAfter, degradedAfter, offlineAfter).ToString().ToUpperInvariant();
            return new SyncPolicyView(x.Id, x.ResourceType, x.IntervalSeconds, x.OverlapSeconds, x.JitterSeconds, x.Enabled, x.Version, cursor?.LastSuccessAt, cursor?.LastModifiedWatermark, health, cursor?.LastAttemptAt, cursor?.ConsecutiveFailureCount ?? 0, cursor?.LastRequestCount ?? 0, cursor?.LastReceivedCount ?? 0, cursor?.LastChangedCount ?? 0, cursor?.LastInsertedCount ?? 0, cursor?.LastUpdatedCount ?? 0, cursor?.LastSkippedCount ?? 0, cursor?.LastFailedCount ?? 0, cursor?.LastRetryCount ?? 0, cursor?.LastRateLimitCount ?? 0);
        }).ToList();
        return ServiceResult<IReadOnlyList<SyncPolicyView>>.Ok(rows);
    }

    public async Task<ServiceResult<SyncPolicyView>> UpsertSyncPolicyAsync(Guid tenantId, Guid id, string resourceType, long? expectedVersion, UpdateSyncPolicyCommand command, CancellationToken cancellationToken)
    {
        var normalized = resourceType.Trim().ToUpperInvariant(); if (!ResourceTypes.Contains(normalized)) return Invalid<SyncPolicyView>("resourceType", "Trendyol için desteklenen sync resource türü değil.");
        if (command.IntervalSeconds is < 30 or > 86_400 || command.OverlapSeconds is < 0 or > 1_209_599 || command.JitterSeconds is < 0 or > 3_600) return Invalid<SyncPolicyView>("interval", "Sync aralığı 30 saniye-24 saat, overlap 0-14 gün ve jitter 0-1 saat arasında olmalıdır.");
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && x.PlatformCode == "TRENDYOL", cancellationToken); if (connection is null) return NotFound<SyncPolicyView>(); if (!ActiveIntegrationScope.Contains(connection.PlatformCode)) return Deferred<SyncPolicyView>();
        var policy = await db.ConnectionSyncPolicies.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == id && x.ResourceType == normalized, cancellationToken);
        if (policy is null) { if (expectedVersion is not null) return NotFound<SyncPolicyView>(); policy = new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, ResourceType = normalized, Version = 1 }; db.ConnectionSyncPolicies.Add(policy); }
        else { if (expectedVersion is null) return ServiceResult<SyncPolicyView>.Fail("PRECONDITION_REQUIRED", "Mevcut sync policy için If-Match gereklidir.", 428); if (policy.Version != expectedVersion) return Precondition<SyncPolicyView>(policy.Version); policy.Version++; }
        policy.IntervalSeconds = command.IntervalSeconds; policy.OverlapSeconds = command.OverlapSeconds; policy.JitterSeconds = command.JitterSeconds; policy.Enabled = command.Enabled; await db.SaveChangesAsync(cancellationToken); return ServiceResult<SyncPolicyView>.Ok(Map(policy));
    }

    public async Task<ServiceResult<IReadOnlyList<WebhookSubscriptionView>>> WebhooksAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        if (!await db.PlatformConnections.AnyAsync(x => x.TenantId == tenantId && x.Id == id && x.PlatformCode == "TRENDYOL", cancellationToken)) return NotFound<IReadOnlyList<WebhookSubscriptionView>>();
        var rows = await db.WebhookSubscriptions.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == id).OrderBy(x => x.Id).Select(x => new WebhookSubscriptionView(x.Id, x.AuthenticationType, x.Status, x.ExternalSubscriptionId, x.VerifiedAt, x.LastReceivedAt, x.Version)).ToListAsync(cancellationToken);
        return ServiceResult<IReadOnlyList<WebhookSubscriptionView>>.Ok(rows);
    }

    public async Task<ServiceResult<CreatedWebhookSubscription>> CreateWebhookAsync(Guid tenantId, Guid id, CreateWebhookSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && x.PlatformCode == "TRENDYOL", cancellationToken); if (connection is null) return NotFound<CreatedWebhookSubscription>(); if (!ActiveIntegrationScope.Contains(connection.PlatformCode)) return Deferred<CreatedWebhookSubscription>(); var type = command.AuthenticationType.Trim().ToUpperInvariant();
        WebhookVerifierPayload payload;
        if (type == "API_KEY" && !string.IsNullOrWhiteSpace(command.ApiKey)) payload = new(null, null, command.ApiKey, null);
        else if (type == "BASIC_AUTHENTICATION" && !string.IsNullOrWhiteSpace(command.Username) && !string.IsNullOrWhiteSpace(command.Password)) payload = new(command.Username, command.Password, null, null);
        else return Invalid<CreatedWebhookSubscription>("authenticationType", "API_KEY için apiKey; BASIC_AUTHENTICATION için username ve password zorunludur.");
        var routeToken = TokenHasher.NewToken(); var subscription = new WebhookSubscription { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, RouteTokenHash = tokenHasher.Hash(routeToken), AuthenticationType = type, ProtectedVerifierSecret = _webhookProtector.Protect(JsonSerializer.Serialize(payload)), Status = "ACTIVE", Version = 1 };
        db.WebhookSubscriptions.Add(subscription); await db.SaveChangesAsync(cancellationToken); return ServiceResult<CreatedWebhookSubscription>.Ok(new(Map(subscription), connection.PublicId, routeToken));
    }

    private Task<PlatformConnection?> Find(Guid tenantId, Guid id, CancellationToken cancellationToken) => db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "TRENDYOL_EFATURAM"), cancellationToken);
    private async Task EnsureCapabilityRowsAsync(PlatformConnection connection, CancellationToken cancellationToken)
    {
        var expected = connection.PlatformCode == "TRENDYOL" ? TrendyolCapabilityCodes : EfaturamCapabilityCodes;
        var existing = await db.PlatformCapabilities.Where(x => x.TenantId == connection.TenantId && x.ConnectionId == connection.Id).Select(x => x.Code).ToListAsync(cancellationToken);
        var missing = expected.Except(existing, StringComparer.Ordinal).ToArray();
        if (missing.Length == 0) return;
        db.PlatformCapabilities.AddRange(missing.Select(code => new PlatformCapability
        {
            Id = Guid.CreateVersion7(),
            TenantId = connection.TenantId,
            ConnectionId = connection.Id,
            Code = code,
            SupportLevel = CapabilitySupportLevel.Unknown,
            ApiVersion = connection.ApiVersion,
            Environment = connection.Environment,
            StoreScope = connection.ExternalStoreId,
            EvidenceNote = "Bu capability sonradan eklendi; Stage/SIT kanıtı bekleniyor.",
            Version = 1
        }));
        await db.SaveChangesAsync(cancellationToken);
    }
    private Task<bool> HasCredential(Guid tenantId, Guid id, CancellationToken cancellationToken) => db.PlatformCredentials.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == id && x.RevokedAt == null, cancellationToken);
    private async Task<HashSet<Guid>> ActiveCredentialConnectionIds(Guid tenantId, IEnumerable<Guid> connectionIds, CancellationToken cancellationToken) => (await db.PlatformCredentials.AsNoTracking().Where(x => x.TenantId == tenantId && connectionIds.Contains(x.ConnectionId) && x.RevokedAt == null).Select(x => x.ConnectionId).ToListAsync(cancellationToken)).ToHashSet();
    private Guid Decode(string? cursor) => cursors.TryDecode(cursor, out var id) ? id : throw new ArgumentException("Cursor geçersiz veya süresi dolmuş.", nameof(cursor));
    private PageResult<TView> Page<TEntity, TView>(List<TEntity> rows, int limit, Func<TEntity, TView> map) where TEntity : class { var hasMore = rows.Count > limit; var items = rows.Take(limit).Select(map).ToList(); var next = hasMore ? cursors.Encode((Guid)typeof(TEntity).GetProperty("Id")!.GetValue(rows[limit - 1])!) : null; return new(items, next, hasMore); }
    private static ConnectionView Map(PlatformConnection x, bool hasCredential) => new(x.Id, x.PublicId, x.PlatformCode, x.Environment, x.DisplayName, x.ExternalStoreId, x.Status, x.ApiVersion, x.LastTestedAt, x.LastSuccessAt, x.LastErrorCode, hasCredential, x.Version);
    private static SyncPolicyView Map(ConnectionSyncPolicy x) => new(x.Id, x.ResourceType, x.IntervalSeconds, x.OverlapSeconds, x.JitterSeconds, x.Enabled, x.Version);
    private static WebhookSubscriptionView Map(WebhookSubscription x) => new(x.Id, x.AuthenticationType, x.Status, x.ExternalSubscriptionId, x.VerifiedAt, x.LastReceivedAt, x.Version);
    private static ConnectionSettings ReadSettings(PlatformConnection value) { try { return JsonSerializer.Deserialize<ConnectionSettings>(value.SettingsJson) ?? new("", false); } catch (JsonException) { return new("", false); } }
    private static TrendyolEFaturamConnectionSettings ReadEfaturamSettings(PlatformConnection value) { try { return JsonSerializer.Deserialize<TrendyolEFaturamConnectionSettings>(value.SettingsJson) ?? new(false); } catch (JsonException) { return new(false); } }
    private static string Mask(string value) => value.Length <= 4 ? "****" : $"****{value[^4..]}";
    private static string MaskEmail(string value) { var separator = value.IndexOf('@'); return separator <= 1 ? "***" : value[..1] + "***" + value[separator..]; }
    private IntegrationJob NewJob(Guid tenantId, Guid connectionId, string type, string dedup, string payload, string correlationId) => new() { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, JobType = type, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), JobDedupKey = dedup, EffectIdempotencyKey = dedup, AvailableAt = timeProvider.GetUtcNow(), CorrelationId = correlationId, Version = 1 };
    private static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    private static ServiceResult<T> Deferred<T>() => ServiceResult<T>.Fail("PLATFORM_OUT_OF_SCOPE", "ADR-016 kapsamında yalnız Trendyol ve Trendyol E-Faturam işlemleri açıktır.", 409);
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Precondition<T>(long version) => ServiceResult<T>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{version}.", 412);
    private sealed record CredentialPayload(string ApiKey, string ApiSecret);
    private sealed record ConnectionSettings(string UserAgentIdentity, bool ExternalWritesEnabled);
    private sealed record WebhookVerifierPayload(string? Username, string? Password, string? ApiKey, string? ClientSecret);
}
