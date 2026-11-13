using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Backups;

public sealed record OffsiteBackupView(
    bool Enabled,
    string? AgeRecipient,
    string FolderMode,
    string FolderValue,
    int KeepDays,
    int KeepMonths,
    string? ClientId,
    bool ClientSecretSaved,
    bool Connected,
    string? Account,
    DateTimeOffset? ConnectedAt,
    bool AuthorizationInvalid,
    string? AuthorizationError,
    string? FolderId,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    string? LastSet,
    long? LastSize,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    int ConsecutiveFailures,
    DateTimeOffset? RequestedAt,
    DateTimeOffset? RunningSince,
    DateTimeOffset? NextRunAt,
    int? RemoteCopies,
    DateTimeOffset? UpdatedAt,
    long Version);

public sealed record OffsiteBackupSaveInput(
    bool Enabled,
    string? AgeRecipient,
    string? FolderMode,
    string? FolderValue,
    int? KeepDays,
    int? KeepMonths,
    string? ClientId,
    string? ClientSecret);

public sealed record OffsiteBackupResult(OffsiteBackupView? View, ServiceError? Error)
{
    public static OffsiteBackupResult Fail(string code, string message, int status = 400) => new(null, new ServiceError(code, message, status));
}

// What the worker needs for one run; secrets decrypted.
public sealed record OffsiteBackupRunContext(Guid TenantId, string AgeRecipient, string FolderMode, string FolderValue, int KeepDays, int KeepMonths, GoogleOAuthClient Client, string RefreshToken, Guid ConnectionId, string? FolderId);

// Sistem Ayarları › Google Drive Yedeği, per tenant in tenant_settings (kept by every reset, like the other settings):
//   "offsite-backup"        what the owner chose (age public key, folder, retention, OAuth client); its Version is the
//                           page's If-Match.
//   "offsite-backup-state"  what the app keeps: the Google refresh token, the pending "Google ile Bağlan", the last runs.
//                           The API and the worker both write it, each change over the version it read.
// The client secret and the refresh token are encrypted with Data Protection, bound to the tenant (BoundSecret); they are
// never returned to the browser. The server holds only the age PUBLIC key. The worker copies with the settings of the
// oldest active tenant that has them, i.e. the owner's: the server serves one company (like the alert e-mail).
public sealed class OffsiteBackupSettingsService(AppDbContext db, IDataProtectionProvider dataProtection, IGoogleDriveApi google, TimeProvider timeProvider)
{
    public const string SettingKey = "offsite-backup";
    public const string StateKey = "offsite-backup-state";
    private const string ProtectionPurpose = "MarketplaceHub.OffsiteBackup.v1";
    private readonly IDataProtector protector = dataProtection.CreateProtector(ProtectionPurpose);

    public sealed class Settings
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("ageRecipient")] public string? AgeRecipient { get; set; }
        [JsonPropertyName("folderMode")] public string? FolderMode { get; set; }
        [JsonPropertyName("folderValue")] public string? FolderValue { get; set; }
        [JsonPropertyName("keepDays")] public int? KeepDays { get; set; }
        [JsonPropertyName("keepMonths")] public int? KeepMonths { get; set; }
        [JsonPropertyName("clientId")] public string? ClientId { get; set; }
        [JsonPropertyName("clientSecretProtected")] public string? ClientSecretProtected { get; set; }
    }

    public sealed class State
    {
        [JsonPropertyName("refreshTokenProtected")] public string? RefreshTokenProtected { get; set; }
        [JsonPropertyName("connectionId")] public Guid? ConnectionId { get; set; }
        [JsonPropertyName("clientId")] public string? ClientId { get; set; }
        [JsonPropertyName("account")] public string? Account { get; set; }
        [JsonPropertyName("connectedAt")] public DateTimeOffset? ConnectedAt { get; set; }
        [JsonPropertyName("authInvalidAt")] public DateTimeOffset? AuthInvalidAt { get; set; }
        [JsonPropertyName("authError")] public string? AuthError { get; set; }
        [JsonPropertyName("folderId")] public string? FolderId { get; set; }
        [JsonPropertyName("folderKey")] public string? FolderKey { get; set; }
        [JsonPropertyName("pendingStateHash")] public string? PendingStateHash { get; set; }
        [JsonPropertyName("pendingVerifierProtected")] public string? PendingVerifierProtected { get; set; }
        [JsonPropertyName("pendingUserId")] public Guid? PendingUserId { get; set; }
        [JsonPropertyName("pendingExpiresAt")] public DateTimeOffset? PendingExpiresAt { get; set; }
        [JsonPropertyName("pendingRedirectUri")] public string? PendingRedirectUri { get; set; }
        [JsonPropertyName("lastAttemptAt")] public DateTimeOffset? LastAttemptAt { get; set; }
        [JsonPropertyName("lastSuccessAt")] public DateTimeOffset? LastSuccessAt { get; set; }
        [JsonPropertyName("lastSet")] public string? LastSet { get; set; }
        [JsonPropertyName("lastSize")] public long? LastSize { get; set; }
        // The Drive MD5 of the last copy sent (its encrypted bytes): a copy of the same name counts as present only with it.
        [JsonPropertyName("lastMd5")] public string? LastMd5 { get; set; }
        [JsonPropertyName("lastError")] public string? LastError { get; set; }
        [JsonPropertyName("lastErrorAt")] public DateTimeOffset? LastErrorAt { get; set; }
        [JsonPropertyName("consecutiveFailures")] public int ConsecutiveFailures { get; set; }
        [JsonPropertyName("failingSince")] public DateTimeOffset? FailingSince { get; set; }
        [JsonPropertyName("requestedAt")] public DateTimeOffset? RequestedAt { get; set; }
        [JsonPropertyName("runningSince")] public DateTimeOffset? RunningSince { get; set; }
        [JsonPropertyName("alertSentAt")] public DateTimeOffset? AlertSentAt { get; set; }
        [JsonPropertyName("remoteCopies")] public int? RemoteCopies { get; set; }
    }

    // ── reads ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<OffsiteBackupView> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await db.TenantSettings.AsNoTracking().Where(x => x.TenantId == tenantId && (x.Key == SettingKey || x.Key == StateKey)).ToListAsync(cancellationToken);
        var settingRow = rows.FirstOrDefault(x => x.Key == SettingKey);
        return View(settingRow, Read<Settings>(settingRow), Read<State>(rows.FirstOrDefault(x => x.Key == StateKey)));
    }

    public static State? ReadState(TenantSetting? row) => Read<State>(row);
    public static Settings? ReadSettings(TenantSetting? row) => Read<Settings>(row);

    // The server was bootstrapped with this tenant (iam.bootstrap_state, BootstrapService).
    public const string HostBootstrapKey = "initial-owner";

    // The tenant whose settings the server's copy uses: the one the server was bootstrapped with, while it is active.
    // A backup set is the whole database, every tenant's data, so the destination is never picked among tenants that
    // saved settings (before 13 Nov 2026: the oldest that saved them, which a second tenant could become); without the
    // bootstrap marker, or with its tenant inactive, nothing is sent.
    public async Task<Guid?> HostTenantAsync(CancellationToken cancellationToken) =>
        await (from marker in db.BootstrapStates.AsNoTracking()
               join tenant in db.Tenants.AsNoTracking() on marker.TenantId equals tenant.Id
               where marker.Key == HostBootstrapKey && tenant.Status == RecordStatus.Active
               select (Guid?)marker.TenantId).SingleOrDefaultAsync(cancellationToken);

    public async Task<(Settings? Settings, State State)> LoadAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await db.TenantSettings.AsNoTracking().Where(x => x.TenantId == tenantId && (x.Key == SettingKey || x.Key == StateKey)).ToListAsync(cancellationToken);
        return (Read<Settings>(rows.FirstOrDefault(x => x.Key == SettingKey)), Read<State>(rows.FirstOrDefault(x => x.Key == StateKey)) ?? new State());
    }

    // Null when something is missing (the reason in Problem): the age key, the OAuth client or the Google authorization.
    public (OffsiteBackupRunContext? Context, string? Problem) RunContext(Guid tenantId, Settings? settings, State state)
    {
        if (settings is null) return (null, "Google Drive yedeği ayarlanmamış.");
        if (settings.AgeRecipient is not { Length: > 0 } recipient || !OffsiteBackupRules.IsAgeRecipient(recipient)) return (null, "age açık anahtarı kayıtlı değil.");
        if (settings.ClientId is not { Length: > 0 } clientId || string.IsNullOrEmpty(settings.ClientSecretProtected)) return (null, "Google OAuth istemcisi kayıtlı değil.");
        if (string.IsNullOrEmpty(state.RefreshTokenProtected) || state.ConnectionId is not { } connectionId || !string.Equals(state.ClientId, clientId, StringComparison.Ordinal))
            return (null, "Google Drive bağlı değil: Google ile Bağlan.");
        var clientSecret = Unprotect(tenantId, settings.ClientSecretProtected);
        var refreshToken = Unprotect(tenantId, state.RefreshTokenProtected);
        if (clientSecret is null || refreshToken is null) return (null, "Kayıtlı Google anahtarları çözülemedi; istemci gizli anahtarını yeniden girip Google ile yeniden bağlanın.");
        var folderKey = FolderKey(settings, connectionId);
        return (new OffsiteBackupRunContext(tenantId, recipient, settings.FolderMode ?? OffsiteBackupRules.FolderModeName, settings.FolderValue ?? OffsiteBackupRules.DefaultFolderName,
            OffsiteBackupRules.ClampKeepDays(settings.KeepDays), OffsiteBackupRules.ClampKeepMonths(settings.KeepMonths), new GoogleOAuthClient(clientId, clientSecret), refreshToken, connectionId,
            state.FolderKey == folderKey ? state.FolderId : null), null);
    }

    // The folder id resolved for a folder choice is kept only for that choice and that Google connection.
    public static string FolderKey(Settings settings, Guid connectionId) => $"{connectionId:N}|{settings.FolderMode}|{settings.FolderValue}";

    // ── save ──────────────────────────────────────────────────────────────────────────────────────────────────────

    // clientSecret: blank keeps the saved one. Changing the client id ends the Google connection (its refresh token belongs
    // to the old client), which is revoked at Google on the way.
    public async Task<OffsiteBackupResult> SaveAsync(Guid tenantId, Guid userId, string correlationId, OffsiteBackupSaveInput input, long? expectedVersion, CancellationToken cancellationToken)
    {
        var current = await db.TenantSettings.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Key == SettingKey, cancellationToken);
        if (expectedVersion is { } version && (current?.Version ?? 0) != version)
            throw new DbUpdateConcurrencyException("The off-site backup settings changed meanwhile.");
        var previous = Read<Settings>(current) ?? new Settings();

        string? recipient = null;
        if (!string.IsNullOrWhiteSpace(input.AgeRecipient) || input.Enabled)
        {
            if (OffsiteBackupRules.AgeRecipientError(input.AgeRecipient, out var normalizedRecipient) is { } recipientError)
                return OffsiteBackupResult.Fail("OFFSITE_BACKUP_AGE_KEY_INVALID", recipientError);
            recipient = normalizedRecipient;
        }
        if (OffsiteBackupRules.FolderError(input.FolderMode, input.FolderValue, out var folderMode, out var folderValue) is { } folderError)
            return OffsiteBackupResult.Fail("OFFSITE_BACKUP_FOLDER_INVALID", folderError);
        var clientId = (input.ClientId ?? string.Empty).Trim();
        if (clientId.Length > 0 && !OffsiteBackupRules.IsGoogleClientId(clientId))
            return OffsiteBackupResult.Fail("OFFSITE_BACKUP_CLIENT_INVALID", "İstemci kimliği …apps.googleusercontent.com ile biter; Google Cloud Console'daki OAuth istemcisinden kopyalayın.");
        var secret = (input.ClientSecret ?? string.Empty).Trim();
        if (secret.Length > 0 && !OffsiteBackupRules.IsGoogleClientSecret(secret))
            return OffsiteBackupResult.Fail("OFFSITE_BACKUP_SECRET_INVALID", "İstemci gizli anahtarı geçersiz görünüyor (genellikle GOCSPX- ile başlar).");
        var clientChanged = !string.Equals(previous.ClientId ?? string.Empty, clientId, StringComparison.Ordinal);
        var protectedSecret = secret.Length > 0 ? Protect(tenantId, secret) : clientChanged ? null : previous.ClientSecretProtected;
        if (clientId.Length > 0 && protectedSecret is null)
            return OffsiteBackupResult.Fail("OFFSITE_BACKUP_SECRET_REQUIRED", "İstemci gizli anahtarını girin.");
        if (input.Enabled && clientId.Length == 0)
            return OffsiteBackupResult.Fail("OFFSITE_BACKUP_CLIENT_REQUIRED", "Yedeği açmak için önce Google OAuth istemcisini kaydedin.");

        var next = new Settings
        {
            Enabled = input.Enabled,
            AgeRecipient = recipient,
            FolderMode = folderMode,
            FolderValue = folderValue,
            KeepDays = OffsiteBackupRules.ClampKeepDays(input.KeepDays),
            KeepMonths = OffsiteBackupRules.ClampKeepMonths(input.KeepMonths),
            ClientId = clientId.Length > 0 ? clientId : null,
            ClientSecretProtected = clientId.Length > 0 ? protectedSecret : null
        };
        var changes = new List<string>();
        if (previous.Enabled != next.Enabled) changes.Add(next.Enabled ? "açıldı" : "kapatıldı");
        if (!string.Equals(previous.AgeRecipient, next.AgeRecipient, StringComparison.Ordinal)) changes.Add($"age açık anahtarı {Short(next.AgeRecipient)}");
        if (previous.FolderMode != next.FolderMode || previous.FolderValue != next.FolderValue) changes.Add($"klasör {(next.FolderMode == OffsiteBackupRules.FolderModeId ? "ID " : string.Empty)}{next.FolderValue}");
        if (previous.KeepDays != next.KeepDays || previous.KeepMonths != next.KeepMonths) changes.Add($"saklama {next.KeepDays} gün + {next.KeepMonths} ay");
        if (clientChanged) changes.Add(next.ClientId is null ? "OAuth istemcisi kaldırıldı" : $"OAuth istemcisi {next.ClientId}");
        else if (secret.Length > 0) changes.Add("istemci gizli anahtarı değişti");

        var now = timeProvider.GetUtcNow();
        var json = JsonSerializer.Serialize(next);
        if (current is null)
        {
            current = new TenantSetting { TenantId = tenantId, Key = SettingKey, ValueJson = json, UpdatedAt = now, Version = 1 };
            db.TenantSettings.Add(current);
        }
        else
        {
            current.ValueJson = json;
            current.UpdatedAt = now;
            current.Version++;
        }
        Audit(tenantId, userId, correlationId, "OFFSITE_BACKUP_SETTINGS_CHANGED", changes.Count == 0 ? "Google Drive yedeği ayarları değişmeden kaydedildi." : $"Google Drive yedeği: {string.Join("; ", changes)}.");
        await db.SaveChangesAsync(cancellationToken);

        if (clientChanged)
        {
            // The old client's authorization is useless now; it is revoked and forgotten.
            var (_, state) = await LoadAsync(tenantId, cancellationToken);
            if (state.RefreshTokenProtected is { } stale && Unprotect(tenantId, stale) is { } staleToken)
            {
                try { await google.RevokeAsync(staleToken, cancellationToken); }
                catch (Exception exception) when (exception is GoogleDriveException or HttpRequestException or TaskCanceledException) { }
            }
            await MutateStateAsync(tenantId, value => { ClearConnection(value); return true; }, cancellationToken);
        }
        return new(await GetAsync(tenantId, cancellationToken), null);
    }

    // ── Google ile Bağlan (authorization code + PKCE) ─────────────────────────────────────────────────────────────

    // Starts the connection: a one-time state (only its hash is stored) and a PKCE verifier (stored encrypted), both bound to
    // this tenant and user, valid AuthorizationLifetime. Returns Google's consent address.
    public async Task<(string? Url, ServiceError? Error)> BeginAuthorizationAsync(Guid tenantId, Guid userId, string redirectUri, string correlationId, CancellationToken cancellationToken)
    {
        var (settings, _) = await LoadAsync(tenantId, cancellationToken);
        if (settings?.ClientId is not { Length: > 0 } clientId || string.IsNullOrEmpty(settings.ClientSecretProtected))
            return (null, new("OFFSITE_BACKUP_CLIENT_REQUIRED", "Önce Google OAuth istemci kimliğini ve gizli anahtarını kaydedin.", 409));
        var stateValue = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var expires = timeProvider.GetUtcNow() + OffsiteBackupRules.AuthorizationLifetime;
        await MutateStateAsync(tenantId, state =>
        {
            state.PendingStateHash = Hash(stateValue);
            state.PendingVerifierProtected = Protect(tenantId, verifier);
            state.PendingUserId = userId;
            state.PendingExpiresAt = expires;
            state.PendingRedirectUri = redirectUri;
            return true;
        }, cancellationToken);
        Audit(tenantId, userId, correlationId, "OFFSITE_BACKUP_GOOGLE_AUTHORIZATION_STARTED", "Google Drive bağlantısı başlatıldı.");
        await db.SaveChangesAsync(cancellationToken);
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = OffsiteBackupRules.AuthorizationScopes,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "false",
            ["state"] = stateValue,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        return ($"{GoogleDriveApi.AuthorizeEndpoint}?{string.Join("&", query.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"))}", null);
    }

    // Google sent the person back with a code (or an error). The state must be the pending one, unexpired and started by the
    // same user of the same tenant; it is used up whatever happens next.
    public async Task<OffsiteBackupResult> CompleteAuthorizationAsync(Guid tenantId, Guid userId, string? code, string? stateValue, string? error, string correlationId, CancellationToken cancellationToken)
    {
        var (settings, state) = await LoadAsync(tenantId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var matches = !string.IsNullOrEmpty(stateValue) && state.PendingStateHash is { } pendingHash
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pendingHash), Encoding.ASCII.GetBytes(Hash(stateValue)));
        if (!matches)
            return OffsiteBackupResult.Fail("OFFSITE_BACKUP_STATE_INVALID", "Bu Google dönüşü bu panelden başlatılan bir bağlantıya ait değil ya da zaten kullanıldı. Google ile Bağlan'a yeniden basın.");
        var verifierProtected = state.PendingVerifierProtected;
        var redirectUri = state.PendingRedirectUri;
        var pendingUser = state.PendingUserId;
        var expiresAt = state.PendingExpiresAt;
        // One use: the pending authorization goes before anything is checked further.
        await MutateStateAsync(tenantId, value =>
        {
            if (value.PendingStateHash != state.PendingStateHash) return false;
            value.PendingStateHash = null; value.PendingVerifierProtected = null; value.PendingUserId = null; value.PendingExpiresAt = null; value.PendingRedirectUri = null;
            return true;
        }, cancellationToken);
        if (pendingUser != userId)
            return OffsiteBackupResult.Fail("OFFSITE_BACKUP_STATE_INVALID", "Bağlantıyı başka bir kullanıcı başlattı; Google ile Bağlan'a kendi oturumunuzdan yeniden basın.", 403);
        if (expiresAt is not { } expires || expires < now)
            return OffsiteBackupResult.Fail("OFFSITE_BACKUP_STATE_EXPIRED", "Google bağlantısının süresi doldu (10 dakika). Google ile Bağlan'a yeniden basın.");
        if (!string.IsNullOrEmpty(error))
            return OffsiteBackupResult.Fail("OFFSITE_BACKUP_GOOGLE_DENIED", error == "access_denied" ? "Google izni verilmedi; bağlantı kurulmadı." : $"Google bağlantıyı tamamlamadı ({Clip(error, 80)}).");
        if (string.IsNullOrWhiteSpace(code) || code.Length > 2048)
            return OffsiteBackupResult.Fail("OFFSITE_BACKUP_GOOGLE_CODE_MISSING", "Google yetki kodu gelmedi. Google ile Bağlan'a yeniden basın.");
        if (settings?.ClientId is not { Length: > 0 } clientId || settings.ClientSecretProtected is null || Unprotect(tenantId, settings.ClientSecretProtected) is not { } clientSecret)
            return OffsiteBackupResult.Fail("OFFSITE_BACKUP_CLIENT_REQUIRED", "Google OAuth istemcisi kayıtlı değil.", 409);
        if (verifierProtected is null || Unprotect(tenantId, verifierProtected) is not { } verifier || redirectUri is null)
            return OffsiteBackupResult.Fail("OFFSITE_BACKUP_STATE_INVALID", "Bağlantı bilgisi okunamadı. Google ile Bağlan'a yeniden basın.");

        GoogleTokenGrant grant;
        try { grant = await google.ExchangeCodeAsync(new GoogleOAuthClient(clientId, clientSecret), code, verifier, redirectUri, cancellationToken); }
        catch (Exception exception) when (exception is GoogleDriveException or HttpRequestException or TaskCanceledException)
        {
            return OffsiteBackupResult.Fail("OFFSITE_BACKUP_GOOGLE_EXCHANGE_FAILED", exception is GoogleDriveException drive ? drive.Message : "Google'a ulaşılamadı; biraz sonra yeniden deneyin.", 502);
        }
        if (string.IsNullOrEmpty(grant.RefreshToken))
            return OffsiteBackupResult.Fail("OFFSITE_BACKUP_NO_REFRESH_TOKEN", "Google kalıcı yetki vermedi. myaccount.google.com › Güvenlik › Üçüncü taraf erişimi'nden uygulamayı kaldırıp yeniden bağlanın.", 502);
        if (!(grant.Scope ?? string.Empty).Split(' ').Contains(OffsiteBackupRules.DriveFileScope, StringComparer.Ordinal))
        {
            try { await google.RevokeAsync(grant.RefreshToken, cancellationToken); } catch (Exception exception) when (exception is GoogleDriveException or HttpRequestException or TaskCanceledException) { }
            return OffsiteBackupResult.Fail("OFFSITE_BACKUP_SCOPE_MISSING", "Google Drive izni verilmedi: bağlanırken \"Google Drive'da bu uygulamayla kullandığınız dosyaları görme, düzenleme, oluşturma ve silme\" kutusunu işaretleyin.");
        }

        string? previousToken = null;
        await MutateStateAsync(tenantId, value =>
        {
            previousToken = value.RefreshTokenProtected;
            value.RefreshTokenProtected = Protect(tenantId, grant.RefreshToken);
            value.ConnectionId = Guid.CreateVersion7();
            value.ClientId = clientId;
            value.Account = grant.Email is { Length: > 0 } email ? Clip(email, 254) : null;
            value.ConnectedAt = now;
            value.AuthInvalidAt = null;
            value.AuthError = null;
            value.ConsecutiveFailures = 0;
            value.FolderId = null;
            value.FolderKey = null;
            return true;
        }, cancellationToken);
        // The authorization it replaces is not kept anywhere; it is revoked so it cannot be used.
        if (previousToken is not null && Unprotect(tenantId, previousToken) is { } replaced && replaced != grant.RefreshToken)
        {
            try { await google.RevokeAsync(replaced, cancellationToken); } catch (Exception exception) when (exception is GoogleDriveException or HttpRequestException or TaskCanceledException) { }
        }
        Audit(tenantId, userId, correlationId, "OFFSITE_BACKUP_GOOGLE_CONNECTED", $"Google Drive bağlandı{(grant.Email is { Length: > 0 } account ? ": " + account : string.Empty)}.");
        await db.SaveChangesAsync(cancellationToken);
        return new(await GetAsync(tenantId, cancellationToken), null);
    }

    // Bağlantıyı Kes: the refresh token is revoked at Google (best effort: an unreachable Google does not keep it here)
    // and deleted.
    public async Task<OffsiteBackupResult> DisconnectAsync(Guid tenantId, Guid userId, string correlationId, CancellationToken cancellationToken)
    {
        var (_, state) = await LoadAsync(tenantId, cancellationToken);
        var revoked = "Google'da iptal edildi";
        if (state.RefreshTokenProtected is { } stored && Unprotect(tenantId, stored) is { } token)
        {
            try { await google.RevokeAsync(token, cancellationToken); }
            catch (Exception exception) when (exception is GoogleDriveException or HttpRequestException or TaskCanceledException) { revoked = "Google'a ulaşılamadı, anahtar yalnız panelden silindi"; }
        }
        await MutateStateAsync(tenantId, value => { ClearConnection(value); return true; }, cancellationToken);
        Audit(tenantId, userId, correlationId, "OFFSITE_BACKUP_GOOGLE_DISCONNECTED", $"Google Drive bağlantısı kesildi ({revoked}).");
        await db.SaveChangesAsync(cancellationToken);
        return new(await GetAsync(tenantId, cancellationToken), null);
    }

    // Bağlantıyı Test Et: a fresh access token and the folder (created when it is missing). Records an invalid
    // authorization like a run does.
    public async Task<(string? Folder, ServiceError? Error)> TestAsync(Guid tenantId, Guid userId, string correlationId, CancellationToken cancellationToken)
    {
        var (settings, state) = await LoadAsync(tenantId, cancellationToken);
        var (context, problem) = RunContext(tenantId, settings, state);
        if (context is null) return (null, new("OFFSITE_BACKUP_NOT_READY", problem!, 409));
        try
        {
            var accessToken = await google.RefreshAccessTokenAsync(context.Client, context.RefreshToken, cancellationToken);
            var folderId = await ResolveFolderAsync(context, accessToken, cancellationToken);
            Audit(tenantId, userId, correlationId, "OFFSITE_BACKUP_TESTED", "Google Drive bağlantısı denendi: çalışıyor.");
            await db.SaveChangesAsync(cancellationToken);
            return (folderId, null);
        }
        catch (GoogleDriveException exception)
        {
            if (exception.Authorization) await MarkAuthorizationInvalidAsync(tenantId, context.ConnectionId, exception.Message, cancellationToken);
            Audit(tenantId, userId, correlationId, "OFFSITE_BACKUP_TESTED", Clip($"Google Drive bağlantısı denendi: {exception.Message}", 500));
            await db.SaveChangesAsync(cancellationToken);
            return (null, new(exception.Code, exception.Message, exception.Authorization ? 409 : 502));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return (null, new("OFFSITE_BACKUP_GOOGLE_UNREACHABLE", "Google'a ulaşılamadı; biraz sonra yeniden deneyin.", 502));
        }
    }

    // Şimdi Gönder: the worker picks the request up within a minute.
    public async Task<ServiceError?> RequestRunAsync(Guid tenantId, Guid userId, string correlationId, CancellationToken cancellationToken)
    {
        var (settings, state) = await LoadAsync(tenantId, cancellationToken);
        var (context, problem) = RunContext(tenantId, settings, state);
        if (context is null) return new("OFFSITE_BACKUP_NOT_READY", problem!, 409);
        if (state.AuthInvalidAt is not null) return new("GOOGLE_AUTH_INVALID", "Google yetkisi geçersiz; önce Google ile yeniden bağlanın.", 409);
        if (await HostTenantAsync(cancellationToken) != tenantId) return new("OFFSITE_BACKUP_NOT_HOST", "Bu sunucunun yedeğini başka bir hesap gönderiyor.", 409);
        var now = timeProvider.GetUtcNow();
        await MutateStateAsync(tenantId, value => { value.RequestedAt = now; return true; }, cancellationToken);
        Audit(tenantId, userId, correlationId, "OFFSITE_BACKUP_RUN_REQUESTED", "Google Drive yedeği için hemen gönderim istendi.");
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    // ── shared with the worker ────────────────────────────────────────────────────────────────────────────────────

    // NAME: the app's own folder of that name, found or created (and its id kept); ID: the given folder, which must exist
    // and be reachable with drive.file.
    public async Task<string> ResolveFolderAsync(OffsiteBackupRunContext context, string accessToken, CancellationToken cancellationToken)
    {
        if (context.FolderId is { Length: > 0 } known) return known;
        string folderId;
        if (context.FolderMode == OffsiteBackupRules.FolderModeId)
        {
            if (!await google.FolderExistsAsync(accessToken, context.FolderValue, cancellationToken))
                throw new GoogleDriveException("DRIVE_FOLDER_UNREACHABLE", "Girilen klasör ID'sine erişilemiyor: drive.file yalnız uygulamanın kendi oluşturduğu klasörleri görür. Klasör adı seçeneğini kullanın.");
            folderId = context.FolderValue;
        }
        else
        {
            folderId = await google.FindFolderAsync(accessToken, context.FolderValue, cancellationToken) ?? await google.CreateFolderAsync(accessToken, context.FolderValue, cancellationToken);
        }
        var key = $"{context.ConnectionId:N}|{context.FolderMode}|{context.FolderValue}";
        await MutateStateAsync(context.TenantId, value =>
        {
            if (value.ConnectionId != context.ConnectionId) return false;
            value.FolderId = folderId;
            value.FolderKey = key;
            return true;
        }, cancellationToken);
        return folderId;
    }

    // Stops every run until the owner reconnects; returns whether this was the change (the first time).
    public async Task<bool> MarkAuthorizationInvalidAsync(Guid tenantId, Guid connectionId, string message, CancellationToken cancellationToken)
    {
        var first = false;
        var now = timeProvider.GetUtcNow();
        await MutateStateAsync(tenantId, value =>
        {
            if (value.ConnectionId != connectionId || value.AuthInvalidAt is not null) return false;
            value.AuthInvalidAt = now;
            value.AuthError = Clip(message, 500);
            first = true;
            return true;
        }, cancellationToken);
        return first;
    }

    // Read-modify-write of the state row over the version read; retried when another writer came first. change returns
    // false to leave the row as it is.
    public async Task<State> MutateStateAsync(Guid tenantId, Func<State, bool> change, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var row = await db.TenantSettings.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Key == StateKey, cancellationToken);
            var state = Read<State>(row) ?? new State();
            if (!change(state)) { if (row is not null) db.Entry(row).State = EntityState.Detached; return state; }
            var now = timeProvider.GetUtcNow();
            var json = JsonSerializer.Serialize(state);
            if (row is null)
            {
                row = new TenantSetting { TenantId = tenantId, Key = StateKey, ValueJson = json, UpdatedAt = now, Version = 1 };
                db.TenantSettings.Add(row);
            }
            else
            {
                row.ValueJson = json;
                row.UpdatedAt = now;
                row.Version++;
            }
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                db.Entry(row).State = EntityState.Detached;
                return state;
            }
            catch (DbUpdateException exception) when (attempt < 5 && (exception is DbUpdateConcurrencyException || exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }))
            {
                db.Entry(row).State = EntityState.Detached;
            }
        }
    }

    public string? Unprotect(Guid tenantId, string stored)
    {
        try { return BoundSecret.Unprotect(protector, Scope(tenantId), stored); }
        catch (CryptographicException) { return null; }
    }

    private string Protect(Guid tenantId, string plaintext) => BoundSecret.Protect(protector, Scope(tenantId), plaintext);

    private static string Scope(Guid tenantId) => $"tenant:{tenantId:N}/offsite-backup";

    private static void ClearConnection(State state)
    {
        state.RefreshTokenProtected = null;
        state.ConnectionId = null;
        state.ClientId = null;
        state.Account = null;
        state.ConnectedAt = null;
        state.AuthInvalidAt = null;
        state.AuthError = null;
        state.FolderId = null;
        state.FolderKey = null;
        state.PendingStateHash = null;
        state.PendingVerifierProtected = null;
        state.PendingUserId = null;
        state.PendingExpiresAt = null;
        state.PendingRedirectUri = null;
        state.RequestedAt = null;
    }

    private void Audit(Guid tenantId, Guid? userId, string correlationId, string action, string reason) =>
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = userId,
            Action = action,
            TargetType = "TenantSetting",
            TargetId = SettingKey,
            Reason = Clip(reason, 512),
            CorrelationId = correlationId,
            CreatedAt = timeProvider.GetUtcNow()
        });

    private OffsiteBackupView View(TenantSetting? settingRow, Settings? settings, State? state)
    {
        state ??= new State();
        var connected = !string.IsNullOrEmpty(state.RefreshTokenProtected) && state.ConnectionId is not null && settings?.ClientId is { Length: > 0 } && string.Equals(state.ClientId, settings.ClientId, StringComparison.Ordinal);
        var ready = connected && settings?.AgeRecipient is { Length: > 0 } && !string.IsNullOrEmpty(settings.ClientSecretProtected);
        var now = timeProvider.GetUtcNow();
        var next = OffsiteBackupRules.NextRunAt(new OffsiteBackupScheduleState(settings?.Enabled == true, ready, state.AuthInvalidAt is not null, state.LastAttemptAt, state.ConsecutiveFailures, state.RequestedAt), now);
        return new OffsiteBackupView(
            settings?.Enabled == true,
            settings?.AgeRecipient,
            settings?.FolderMode ?? OffsiteBackupRules.FolderModeName,
            settings?.FolderValue ?? OffsiteBackupRules.DefaultFolderName,
            OffsiteBackupRules.ClampKeepDays(settings?.KeepDays),
            OffsiteBackupRules.ClampKeepMonths(settings?.KeepMonths),
            settings?.ClientId,
            !string.IsNullOrEmpty(settings?.ClientSecretProtected),
            connected,
            connected ? state.Account : null,
            connected ? state.ConnectedAt : null,
            connected && state.AuthInvalidAt is not null,
            connected ? state.AuthError : null,
            connected ? state.FolderId : null,
            state.LastAttemptAt,
            state.LastSuccessAt,
            state.LastSet,
            state.LastSize,
            state.LastError,
            state.LastErrorAt,
            state.ConsecutiveFailures,
            state.RequestedAt,
            state.RunningSince is { } running && now - running < TimeSpan.FromHours(2) ? running : null,
            next,
            state.RemoteCopies,
            settingRow?.UpdatedAt,
            settingRow?.Version ?? 0);
    }

    private static T? Read<T>(TenantSetting? row) where T : class
    {
        if (string.IsNullOrWhiteSpace(row?.ValueJson)) return null;
        try { return JsonSerializer.Deserialize<T>(row.ValueJson); }
        catch (JsonException) { return null; }
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Short(string? value) => value is { Length: > 16 } ? $"{value[..10]}…{value[^6..]}" : value ?? "yok";
    private static string Clip(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
