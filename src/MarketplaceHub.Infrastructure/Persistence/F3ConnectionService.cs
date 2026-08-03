using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class F3ConnectionService(AppDbContext db, CursorCodec cursors, IDataProtectionProvider dataProtection, TokenHasher tokenHasher, TimeProvider timeProvider) : IF3ConnectionService
{
    private static readonly string[] TrendyolCapabilityCodes =
    [
        F3Capabilities.ConnectionTest, F3Capabilities.ReferenceRead, F3Capabilities.ProductRead, F3Capabilities.ProductWrite,
        F3Capabilities.InventoryWrite, F3Capabilities.PriceWrite, F3Capabilities.OrderRead, F3Capabilities.OrderWebhook,
        F3Capabilities.ShipmentWrite, F3Capabilities.LabelRead, F3Capabilities.ReturnRead, F3Capabilities.ReturnWrite,
        F4Capabilities.InvoiceDeliver
    ];
    private static readonly string[] EfaturamCapabilityCodes =
    [
        F4Capabilities.ConnectionTest, F4Capabilities.TaxpayerQuery, F4Capabilities.InvoiceSubmit,
        F4Capabilities.InvoiceStatusRead, F4Capabilities.InvoiceDocumentRead, F4Capabilities.InvoiceCancel
    ];
    private static readonly string[] ShopifyCapabilityCodes =
    [
        F3Capabilities.ConnectionTest, F3Capabilities.ProductRead, F3Capabilities.ProductWrite,
        F3Capabilities.InventoryWrite, F3Capabilities.PriceWrite, F3Capabilities.OrderRead,
        F3Capabilities.OrderWebhook, F3Capabilities.ShipmentWrite
    ];
    private static readonly string[] HepsiburadaCapabilityCodes =
    [
        F3Capabilities.ConnectionTest, F3Capabilities.ReferenceRead, F3Capabilities.ProductRead,
        F3Capabilities.ProductWrite, F3Capabilities.InventoryWrite, F3Capabilities.PriceWrite,
        F3Capabilities.OrderRead, F3Capabilities.OrderWebhook, F3Capabilities.ShipmentWrite,
        F3Capabilities.LabelRead, F3Capabilities.ReturnRead, F3Capabilities.ReturnWrite
    ];
    private static readonly HashSet<string> ResourceTypes = new(StringComparer.Ordinal) { "ORDERS", "RETURNS", "REFERENCE_DATA", "PRODUCTS" };
    private readonly IDataProtector _credentialProtector = dataProtection.CreateProtector("MarketplaceHub.PlatformCredential.v1");
    private readonly IDataProtector _webhookProtector = dataProtection.CreateProtector("MarketplaceHub.WebhookVerifier.v1");

    public async Task<PageResult<ConnectionView>> ListAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken)
    {
        var afterId = Decode(after); var query = db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "TRENDYOL_EFATURAM" || x.PlatformCode == ShopifyContract.PlatformCode || x.PlatformCode == HepsiburadaContract.PlatformCode));
        if (afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0);
        var rows = await query.OrderBy(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var credentialIds = await ActiveCredentialConnectionIds(tenantId, rows.Select(x => x.Id), cancellationToken);
        return Page(rows, limit, x => Map(x, credentialIds.Contains(x.Id)));
    }

    public async Task<ServiceResult<ConnectionView>> CreateAsync(Guid tenantId, CreateConnectionCommand command, CancellationToken cancellationToken)
    {
        var platform = string.IsNullOrWhiteSpace(command.PlatformCode) ? "TRENDYOL" : command.PlatformCode.Trim().ToUpperInvariant();
        if (platform is not ("TRENDYOL" or "TRENDYOL_EFATURAM" or ShopifyContract.PlatformCode or HepsiburadaContract.PlatformCode)) return Invalid<ConnectionView>("platformCode", "Yalnız aktif faz platformları TRENDYOL, TRENDYOL_EFATURAM, SHOPIFY veya HEPSIBURADA olabilir.");
        var environment = command.Environment.Trim().ToUpperInvariant();
        if (environment is not ("STAGE" or "PRODUCTION")) return Invalid<ConnectionView>("environment", "Environment yalnız STAGE veya PRODUCTION olabilir.");
        var apiVersion = command.ApiVersion.Trim();
        if (platform == "TRENDYOL" && !string.Equals(apiVersion, "V2", StringComparison.OrdinalIgnoreCase)) return Invalid<ConnectionView>("apiVersion", "Trendyol marketplace bağlantısı yalnız Product Integration V2 kullanır.");
        if (platform == "TRENDYOL_EFATURAM" && !string.Equals(apiVersion, "1.0.0", StringComparison.OrdinalIgnoreCase)) return Invalid<ConnectionView>("apiVersion", "E-Faturam bağlantısı doğrulanmış doküman sürümü 1.0.0 ile pinlenmelidir.");
        if (platform == ShopifyContract.PlatformCode && apiVersion != ShopifyContract.ApiVersion) return Invalid<ConnectionView>("apiVersion", "Shopify Admin GraphQL sürümü 2026-07 olarak pinlenmelidir.");
        if (platform == HepsiburadaContract.PlatformCode && !string.Equals(apiVersion, HepsiburadaContract.DocumentedApiVersion, StringComparison.OrdinalIgnoreCase)) return Invalid<ConnectionView>("apiVersion", "Hepsiburada draft bağlantısı doğrulanan guide sürümü v1.0 ile kaydedilmelidir.");
        if (platform == ShopifyContract.PlatformCode && !ShopifyContract.TryNormalizeShopDomain(command.ExternalStoreId, out _)) return Invalid<ConnectionView>("externalStoreId", "Shopify mağaza alanı yalnız canonical *.myshopify.com biçiminde olabilir.");
        if (string.IsNullOrWhiteSpace(command.DisplayName) || string.IsNullOrWhiteSpace(command.ExternalStoreId) || (platform is "TRENDYOL" or HepsiburadaContract.PlatformCode) && string.IsNullOrWhiteSpace(command.UserAgentIdentity)) return Invalid<ConnectionView>("connection", "Ad ve dış mağaza/firma kapsamı; Trendyol ve Hepsiburada için ayrıca User-Agent kimliği zorunludur.");
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
            ApiVersion = platform == "TRENDYOL" ? "V2" : platform == "TRENDYOL_EFATURAM" ? "1.0.0" : platform == ShopifyContract.PlatformCode ? ShopifyContract.ApiVersion : HepsiburadaContract.DocumentedApiVersion,
            Status = "DRAFT",
            SettingsJson = platform is "TRENDYOL" or HepsiburadaContract.PlatformCode ? JsonSerializer.Serialize(new ConnectionSettings(command.UserAgentIdentity!.Trim(), false)) : platform == "TRENDYOL_EFATURAM" ? JsonSerializer.Serialize(new TrendyolEFaturamConnectionSettings("UNVERIFIED", false)) : JsonSerializer.Serialize(new ShopifyConnectionSettings(false)),
            Version = 1
        };
        db.PlatformConnections.Add(connection);
        var capabilities = platform == "TRENDYOL" ? TrendyolCapabilityCodes : platform == "TRENDYOL_EFATURAM" ? EfaturamCapabilityCodes : platform == ShopifyContract.PlatformCode ? ShopifyCapabilityCodes : HepsiburadaCapabilityCodes;
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
        var connection = await db.PlatformConnections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "TRENDYOL_EFATURAM" || x.PlatformCode == ShopifyContract.PlatformCode || x.PlatformCode == HepsiburadaContract.PlatformCode), cancellationToken); if (connection is null) return NotFound<ConnectionView>(); if (connection.Version != expectedVersion) return Precondition<ConnectionView>(connection.Version);
        if (string.IsNullOrWhiteSpace(command.DisplayName) || (connection.PlatformCode is "TRENDYOL" or HepsiburadaContract.PlatformCode) && string.IsNullOrWhiteSpace(command.UserAgentIdentity)) return Invalid<ConnectionView>("connection", "Ad; Trendyol ve Hepsiburada için ayrıca User-Agent kimliği zorunludur.");
        connection.DisplayName = command.DisplayName.Trim();
        if (connection.PlatformCode is "TRENDYOL" or HepsiburadaContract.PlatformCode) connection.SettingsJson = JsonSerializer.Serialize(new ConnectionSettings(command.UserAgentIdentity!.Trim(), ReadSettings(connection).ExternalWritesEnabled));
        if (connection.PlatformCode == "TRENDYOL_EFATURAM" && command.EfaturamIntegrationModel is not null)
        {
            var integrationModel = command.EfaturamIntegrationModel.Trim().ToUpperInvariant();
            if (integrationModel is not ("API_USER" or "MARKETPLACE")) return Invalid<ConnectionView>("efaturamIntegrationModel", "E-Faturam entegrasyon modeli API_USER veya MARKETPLACE olmalıdır.");
            if (command.EfaturamCompanyId is null or <= 0 || command.EfaturamUserId is null or <= 0) return Invalid<ConnectionView>("efaturamFiscalAccount", "E-Faturam companyId ve userId pozitif olmalıdır.");
            var prefix = command.EfaturamPrefix?.Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(prefix) && (prefix.Length != 3 || prefix.Any(x => !char.IsAsciiLetterOrDigit(x)))) return Invalid<ConnectionView>("efaturamPrefix", "Fatura serisi tam 3 harf/rakam olmalıdır.");
            var carriers = (command.EfaturamCarriers ?? []).Select(x => new EfaturamCarrierIdentity(x.ProviderName.Trim(), x.TaxId.Trim(), x.LegalName.Trim())).ToList();
            if (carriers.Any(x => string.IsNullOrWhiteSpace(x.ProviderName) || string.IsNullOrWhiteSpace(x.LegalName) || x.TaxId.Length is not (10 or 11) || !x.TaxId.All(char.IsAsciiDigit))) return Invalid<ConnectionView>("efaturamCarriers", "Kargo bilgisi girildiğinde sağlayıcı adı, yasal unvan ve 10/11 haneli VKN/TCKN birlikte girilmelidir.");
            if (carriers.Select(x => x.ProviderName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != carriers.Count) return Invalid<ConnectionView>("efaturamCarriers", "Aynı kargo sağlayıcısı yalnız bir kez tanımlanabilir.");
            var current = ReadEfaturamSettings(connection);
            connection.SettingsJson = JsonSerializer.Serialize(new TrendyolEFaturamConnectionSettings(integrationModel, current.ExternalWritesEnabled, command.EfaturamCompanyId, command.EfaturamUserId, prefix, carriers));
        }
        connection.Version++;
        await db.SaveChangesAsync(cancellationToken); return ServiceResult<ConnectionView>.Ok(Map(connection, await HasCredential(tenantId, id, cancellationToken)));
    }

    public async Task<ServiceResult<ConnectionView>> RotateCredentialAsync(Guid tenantId, Guid id, long expectedVersion, CredentialCommand command, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "TRENDYOL_EFATURAM" || x.PlatformCode == ShopifyContract.PlatformCode || x.PlatformCode == HepsiburadaContract.PlatformCode), cancellationToken); if (connection is null) return NotFound<ConnectionView>(); if (connection.Version != expectedVersion) return Precondition<ConnectionView>(connection.Version);
        if (connection.PlatformCode == HepsiburadaContract.PlatformCode && connection.Environment != "STAGE") return ServiceResult<ConnectionView>.Fail("HEPSIBURADA_PRODUCTION_AUTH_UNVERIFIED", "Hepsiburada credential yalnız doğrulanmış STAGE/SIT bağlantısında kaydedilebilir.", 422);
        if (connection.PlatformCode == HepsiburadaContract.PlatformCode && (string.IsNullOrWhiteSpace(command.Username) || string.IsNullOrWhiteSpace(command.Password))) return Invalid<ConnectionView>("credential", "Hepsiburada STAGE için Merchant ID ve Secret Key zorunludur.");
        if (connection.PlatformCode == HepsiburadaContract.PlatformCode && !string.Equals(command.Username!.Trim(), connection.ExternalStoreId, StringComparison.OrdinalIgnoreCase)) return Invalid<ConnectionView>("username", "Merchant ID, bağlantının merchant kapsam kimliğiyle aynı olmalıdır.");
        if (connection.PlatformCode == "TRENDYOL" && (string.IsNullOrWhiteSpace(command.ApiKey) || string.IsNullOrWhiteSpace(command.ApiSecret))) return Invalid<ConnectionView>("credential", "Trendyol için API key ve secret zorunludur.");
        if (connection.PlatformCode == "TRENDYOL_EFATURAM" && (string.IsNullOrWhiteSpace(command.Email) || string.IsNullOrWhiteSpace(command.Password))) return Invalid<ConnectionView>("credential", "E-Faturam için e-posta ve parola zorunludur.");
        if (connection.PlatformCode == ShopifyContract.PlatformCode && (string.IsNullOrWhiteSpace(command.AccessToken) || string.IsNullOrWhiteSpace(command.ClientSecret))) return Invalid<ConnectionView>("credential", "Shopify için access token ve webhook HMAC doğrulamasında kullanılan app client secret zorunludur.");
        var now = timeProvider.GetUtcNow(); var current = await db.PlatformCredentials.Where(x => x.TenantId == tenantId && x.ConnectionId == id && x.RevokedAt == null).ToListAsync(cancellationToken); foreach (var item in current) { item.RevokedAt = now; item.Version++; }
        var payload = connection.PlatformCode switch
        {
            "TRENDYOL" => JsonSerializer.Serialize(new CredentialPayload(command.ApiKey!, command.ApiSecret!)),
            "TRENDYOL_EFATURAM" => JsonSerializer.Serialize(new EfaturamCredentialPayload(command.Email!, command.Password!)),
            ShopifyContract.PlatformCode => JsonSerializer.Serialize(new ShopifyCredentialPayload(command.AccessToken!, command.ClientSecret!)),
            _ => JsonSerializer.Serialize(new HepsiburadaCredentialPayload(command.Username!.Trim(), command.Password!))
        };
        var hint = connection.PlatformCode switch { "TRENDYOL" => Mask(command.ApiKey!), "TRENDYOL_EFATURAM" => MaskEmail(command.Email!), ShopifyContract.PlatformCode => Mask(command.AccessToken!), _ => Mask(command.Username!) };
        var credentialType = connection.PlatformCode switch { "TRENDYOL" => "BASIC", "TRENDYOL_EFATURAM" => "EMAIL_PASSWORD", ShopifyContract.PlatformCode => "ACCESS_TOKEN", _ => "BASIC" };
        db.PlatformCredentials.Add(new PlatformCredential { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, CredentialType = credentialType, ProtectedPayload = _credentialProtector.Protect(payload), MaskedHint = hint, CreatedAt = now, Version = 1 });
        connection.LastTestedAt = null; connection.LastSuccessAt = null; connection.LastErrorCode = null; connection.Status = "DRAFT"; connection.Version++;
        foreach (var capability in await db.PlatformCapabilities.Where(x => x.TenantId == tenantId && x.ConnectionId == id).ToListAsync(cancellationToken)) { capability.SupportLevel = CapabilitySupportLevel.Unknown; capability.VerifiedAt = null; capability.EvidenceNote = "Credential rotasyonu sonrası yeniden doğrulama gerekiyor."; capability.Version++; }
        await db.SaveChangesAsync(cancellationToken); return ServiceResult<ConnectionView>.Ok(Map(connection, true));
    }

    public async Task<ServiceResult<Guid>> EnqueueTestAsync(Guid tenantId, Guid id, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await Find(tenantId, id, cancellationToken); if (connection is null) return NotFound<Guid>(); if (!await HasCredential(tenantId, id, cancellationToken)) return ServiceResult<Guid>.Fail("CREDENTIAL_REQUIRED", "Bağlantı testi için şifreli credential gerekir.", 422);
        var jobType = connection.PlatformCode == "TRENDYOL" ? F3JobTypes.ConnectionTest : connection.PlatformCode == "TRENDYOL_EFATURAM" ? F4JobTypes.ConnectionTest : connection.PlatformCode == ShopifyContract.PlatformCode ? ShopifyContract.ConnectionTestJob : HepsiburadaContract.ConnectionTestJob;
        var dedup = $"connection-test:{connection.Id}:{idempotencyKey}"; var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == jobType && x.JobDedupKey == dedup, cancellationToken); if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var payload = JsonSerializer.Serialize(new { connectionId = id }); var job = NewJob(tenantId, id, jobType, dedup, payload, correlationId); db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(job.Id);
    }

    public async Task<ServiceResult<ConnectionView>> SetActiveAsync(Guid tenantId, Guid id, long expectedVersion, bool active, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "TRENDYOL_EFATURAM" || x.PlatformCode == ShopifyContract.PlatformCode || x.PlatformCode == HepsiburadaContract.PlatformCode), cancellationToken); if (connection is null) return NotFound<ConnectionView>(); if (connection.Version != expectedVersion) return Precondition<ConnectionView>(connection.Version);
        if (active)
        {
            var connectionTest = await db.PlatformCapabilities.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == id && x.Code == F3Capabilities.ConnectionTest, cancellationToken);
            if (connection.LastSuccessAt is null || connectionTest?.SupportLevel != CapabilitySupportLevel.Supported) return ServiceResult<ConnectionView>.Fail("CONNECTION_TEST_REQUIRED", "Bağlantı etkinleştirilmeden önce başarılı Stage/Production testi gerekir.", 422);
            connection.Status = "ACTIVE";
        }
        else connection.Status = "DISABLED";
        connection.Version++; await db.SaveChangesAsync(cancellationToken); return ServiceResult<ConnectionView>.Ok(Map(connection, await HasCredential(tenantId, id, cancellationToken)));
    }

    public async Task<ServiceResult<IReadOnlyList<CapabilityView>>> CapabilitiesAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        if (!await db.PlatformConnections.AnyAsync(x => x.TenantId == tenantId && x.Id == id && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "TRENDYOL_EFATURAM" || x.PlatformCode == ShopifyContract.PlatformCode || x.PlatformCode == HepsiburadaContract.PlatformCode), cancellationToken)) return NotFound<IReadOnlyList<CapabilityView>>();
        var rows = await db.PlatformCapabilities.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == id).OrderBy(x => x.Code).Select(x => new CapabilityView(x.Code, x.SupportLevel.ToString().ToUpperInvariant(), x.ApiVersion, x.Environment, x.StoreScope, x.SourceUrl, x.VerifiedAt, x.ConstraintsJson)).ToListAsync(cancellationToken);
        return ServiceResult<IReadOnlyList<CapabilityView>>.Ok(rows);
    }

    public async Task<ServiceResult<IReadOnlyList<SyncPolicyView>>> SyncPoliciesAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        if (!await db.PlatformConnections.AnyAsync(x => x.TenantId == tenantId && x.Id == id && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == ShopifyContract.PlatformCode), cancellationToken)) return NotFound<IReadOnlyList<SyncPolicyView>>();
        var rows = await db.ConnectionSyncPolicies.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == id).OrderBy(x => x.ResourceType).Select(x => new SyncPolicyView(x.Id, x.ResourceType, x.IntervalSeconds, x.OverlapSeconds, x.JitterSeconds, x.Enabled, x.Version)).ToListAsync(cancellationToken);
        return ServiceResult<IReadOnlyList<SyncPolicyView>>.Ok(rows);
    }

    public async Task<ServiceResult<SyncPolicyView>> UpsertSyncPolicyAsync(Guid tenantId, Guid id, string resourceType, long? expectedVersion, UpdateSyncPolicyCommand command, CancellationToken cancellationToken)
    {
        var normalized = resourceType.Trim().ToUpperInvariant(); if (!ResourceTypes.Contains(normalized)) return Invalid<SyncPolicyView>("resourceType", "F3 için desteklenen sync resource türü değil.");
        if (command.IntervalSeconds <= 0 || command.OverlapSeconds < 0 || command.JitterSeconds < 0) return Invalid<SyncPolicyView>("interval", "Sync interval pozitif; overlap ve jitter sıfır veya pozitif olmalıdır.");
        if (!await db.PlatformConnections.AnyAsync(x => x.TenantId == tenantId && x.Id == id && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == ShopifyContract.PlatformCode), cancellationToken)) return NotFound<SyncPolicyView>();
        var policy = await db.ConnectionSyncPolicies.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == id && x.ResourceType == normalized, cancellationToken);
        if (policy is null) { if (expectedVersion is not null) return NotFound<SyncPolicyView>(); policy = new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, ResourceType = normalized, Version = 1 }; db.ConnectionSyncPolicies.Add(policy); }
        else { if (expectedVersion is null) return ServiceResult<SyncPolicyView>.Fail("PRECONDITION_REQUIRED", "Mevcut sync policy için If-Match gereklidir.", 428); if (policy.Version != expectedVersion) return Precondition<SyncPolicyView>(policy.Version); policy.Version++; }
        policy.IntervalSeconds = command.IntervalSeconds; policy.OverlapSeconds = command.OverlapSeconds; policy.JitterSeconds = command.JitterSeconds; policy.Enabled = command.Enabled; await db.SaveChangesAsync(cancellationToken); return ServiceResult<SyncPolicyView>.Ok(Map(policy));
    }

    public async Task<ServiceResult<IReadOnlyList<WebhookSubscriptionView>>> WebhooksAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        if (!await db.PlatformConnections.AnyAsync(x => x.TenantId == tenantId && x.Id == id && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == ShopifyContract.PlatformCode), cancellationToken)) return NotFound<IReadOnlyList<WebhookSubscriptionView>>();
        var rows = await db.WebhookSubscriptions.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == id).OrderBy(x => x.Id).Select(x => new WebhookSubscriptionView(x.Id, x.AuthenticationType, x.Status, x.ExternalSubscriptionId, x.VerifiedAt, x.LastReceivedAt, x.Version)).ToListAsync(cancellationToken);
        return ServiceResult<IReadOnlyList<WebhookSubscriptionView>>.Ok(rows);
    }

    public async Task<ServiceResult<CreatedWebhookSubscription>> CreateWebhookAsync(Guid tenantId, Guid id, CreateWebhookSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == ShopifyContract.PlatformCode), cancellationToken); if (connection is null) return NotFound<CreatedWebhookSubscription>(); var type = command.AuthenticationType.Trim().ToUpperInvariant();
        WebhookVerifierPayload payload;
        if (connection.PlatformCode == ShopifyContract.PlatformCode)
        {
            var credential = await db.PlatformCredentials.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == id && x.RevokedAt == null).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            if (credential is null) return ServiceResult<CreatedWebhookSubscription>.Fail("CREDENTIAL_REQUIRED", "Shopify HMAC route için şifreli credential gerekir.", 422);
            try { var stored = JsonSerializer.Deserialize<ShopifyCredentialPayload>(_credentialProtector.Unprotect(credential.ProtectedPayload)); payload = new(null, null, null, stored?.ClientSecret); }
            catch (Exception exception) when (exception is CryptographicException or JsonException) { return ServiceResult<CreatedWebhookSubscription>.Fail("CREDENTIAL_INVALID", "Shopify credential çözülemedi.", 422); }
            if (string.IsNullOrWhiteSpace(payload.ClientSecret)) return ServiceResult<CreatedWebhookSubscription>.Fail("CREDENTIAL_INVALID", "Shopify app client secret eksik.", 422);
            type = "SHOPIFY_HMAC";
        }
        else if (type == "API_KEY" && !string.IsNullOrWhiteSpace(command.ApiKey)) payload = new(null, null, command.ApiKey, null);
        else if (type == "BASIC_AUTHENTICATION" && !string.IsNullOrWhiteSpace(command.Username) && !string.IsNullOrWhiteSpace(command.Password)) payload = new(command.Username, command.Password, null, null);
        else return Invalid<CreatedWebhookSubscription>("authenticationType", "API_KEY için apiKey; BASIC_AUTHENTICATION için username ve password zorunludur.");
        var routeToken = TokenHasher.NewToken(); var subscription = new WebhookSubscription { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = id, RouteTokenHash = tokenHasher.Hash(routeToken), AuthenticationType = type, ProtectedVerifierSecret = _webhookProtector.Protect(JsonSerializer.Serialize(payload)), Status = "ACTIVE", Version = 1 };
        db.WebhookSubscriptions.Add(subscription); await db.SaveChangesAsync(cancellationToken); return ServiceResult<CreatedWebhookSubscription>.Ok(new(Map(subscription), connection.PublicId, routeToken));
    }

    private Task<PlatformConnection?> Find(Guid tenantId, Guid id, CancellationToken cancellationToken) => db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "TRENDYOL_EFATURAM" || x.PlatformCode == ShopifyContract.PlatformCode || x.PlatformCode == HepsiburadaContract.PlatformCode), cancellationToken);
    private Task<bool> HasCredential(Guid tenantId, Guid id, CancellationToken cancellationToken) => db.PlatformCredentials.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == id && x.RevokedAt == null, cancellationToken);
    private async Task<HashSet<Guid>> ActiveCredentialConnectionIds(Guid tenantId, IEnumerable<Guid> connectionIds, CancellationToken cancellationToken) => (await db.PlatformCredentials.AsNoTracking().Where(x => x.TenantId == tenantId && connectionIds.Contains(x.ConnectionId) && x.RevokedAt == null).Select(x => x.ConnectionId).ToListAsync(cancellationToken)).ToHashSet();
    private Guid Decode(string? cursor) => cursors.TryDecode(cursor, out var id) ? id : throw new ArgumentException("Cursor geçersiz veya süresi dolmuş.", nameof(cursor));
    private PageResult<TView> Page<TEntity, TView>(List<TEntity> rows, int limit, Func<TEntity, TView> map) where TEntity : class { var hasMore = rows.Count > limit; var items = rows.Take(limit).Select(map).ToList(); var next = hasMore ? cursors.Encode((Guid)typeof(TEntity).GetProperty("Id")!.GetValue(rows[limit - 1])!) : null; return new(items, next, hasMore); }
    private static ConnectionView Map(PlatformConnection x, bool hasCredential) => new(x.Id, x.PublicId, x.PlatformCode, x.Environment, x.DisplayName, x.ExternalStoreId, x.Status, x.ApiVersion, x.LastTestedAt, x.LastSuccessAt, x.LastErrorCode, hasCredential, x.Version);
    private static SyncPolicyView Map(ConnectionSyncPolicy x) => new(x.Id, x.ResourceType, x.IntervalSeconds, x.OverlapSeconds, x.JitterSeconds, x.Enabled, x.Version);
    private static WebhookSubscriptionView Map(WebhookSubscription x) => new(x.Id, x.AuthenticationType, x.Status, x.ExternalSubscriptionId, x.VerifiedAt, x.LastReceivedAt, x.Version);
    private static ConnectionSettings ReadSettings(PlatformConnection value) { try { return JsonSerializer.Deserialize<ConnectionSettings>(value.SettingsJson) ?? new("", false); } catch (JsonException) { return new("", false); } }
    private static TrendyolEFaturamConnectionSettings ReadEfaturamSettings(PlatformConnection value) { try { return JsonSerializer.Deserialize<TrendyolEFaturamConnectionSettings>(value.SettingsJson) ?? new("UNVERIFIED", false); } catch (JsonException) { return new("UNVERIFIED", false); } }
    private static string Mask(string value) => value.Length <= 4 ? "****" : $"****{value[^4..]}";
    private static string MaskEmail(string value) { var separator = value.IndexOf('@'); return separator <= 1 ? "***" : value[..1] + "***" + value[separator..]; }
    private IntegrationJob NewJob(Guid tenantId, Guid connectionId, string type, string dedup, string payload, string correlationId) => new() { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, JobType = type, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), JobDedupKey = dedup, EffectIdempotencyKey = dedup, AvailableAt = timeProvider.GetUtcNow(), CorrelationId = correlationId, Version = 1 };
    private static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Precondition<T>(long version) => ServiceResult<T>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{version}.", 412);
    private sealed record CredentialPayload(string ApiKey, string ApiSecret);
    private sealed record EfaturamCredentialPayload(string Email, string Password);
    private sealed record ShopifyCredentialPayload(string AccessToken, string ClientSecret);
    private sealed record HepsiburadaCredentialPayload(string Username, string Password);
    private sealed record ConnectionSettings(string UserAgentIdentity, bool ExternalWritesEnabled);
    private sealed record ShopifyConnectionSettings(bool ExternalWritesEnabled);
    private sealed record WebhookVerifierPayload(string? Username, string? Password, string? ApiKey, string? ClientSecret);
}
