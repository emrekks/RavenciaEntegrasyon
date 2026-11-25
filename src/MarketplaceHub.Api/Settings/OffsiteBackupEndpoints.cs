using MarketplaceHub.Api.Common;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Backups;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Api.Common.EndpointHelpers;

namespace MarketplaceHub.Api.Settings;

// Sistem Ayarları › Google Drive Yedeği (RoleAuthorizationMiddleware: reads, test and run for the owner and administrators;
// every change of where the copy goes, for the owner alone; a changed age key or Google account alerts the owner):
//   GET  /api/v1/settings/offsite-backup                    settings and copy status; never the client secret or a token
//   PUT  /api/v1/settings/offsite-backup                    save (If-Match + password, and the authenticator code when on)
//   POST /api/v1/settings/offsite-backup/google/authorize   "Google ile Bağlan" (password): Google's consent address; with
//                                                           `settings` (and If-Match) it saves them first, in the same
//                                                           verified request (the one-button setup: one code, not two)
//   POST /api/v1/settings/offsite-backup/google/complete    the panel's /settings/google-drive-callback page hands over
//                                                           Google's code and state (same user, unexpired, once)
//   POST /api/v1/settings/offsite-backup/google/disconnect  "Bağlantıyı Kes" (password): revoked at Google and deleted
//   POST /api/v1/settings/offsite-backup/test               "Bağlantıyı Test Et": a fresh token and the Drive folder
//   POST /api/v1/settings/offsite-backup/run                "Şimdi Gönder": the worker copies within a minute
// Google sends the person back to a panel page, not to the API: the page posts the code here with the session and CSRF
// token like any other change, so the callback is never an unauthenticated entry point.
public static class OffsiteBackupEndpoints
{
    public const string CallbackPath = "/settings/google-drive-callback";

    public sealed record SaveRequest(bool Enabled, string? AgeRecipient, string? FolderMode, string? FolderValue, int? KeepDays, int? KeepMonths, string? ClientId, string? ClientSecret,
        string? Password, string? Code = null, string? RecoveryCode = null);
    public sealed record CompleteRequest(string? Code, string? State, string? Error);
    // The settings the one-button setup saves before it asks Google (scan 25 Nov 2026, D-6): a save and an authorize sent
    // one after the other each checked the authenticator code, which works once, so an owner with MFA always failed the
    // first try. One request is verified once and does both; nothing about the check itself is relaxed.
    public sealed record SetupSettings(bool Enabled, string? AgeRecipient, string? FolderMode, string? FolderValue, int? KeepDays, int? KeepMonths, string? ClientId, string? ClientSecret);
    public sealed record AuthorizeRequest(string? Password, string? Code = null, string? RecoveryCode = null, SetupSettings? Settings = null);

    public static IEndpointRouteBuilder MapOffsiteBackupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/settings/offsite-backup").WithGates(ApiGate.Elevated, ApiGate.Owner);
        group.MapGet("", GetAsync).Produces<OffsiteBackupSettingsView>();
        group.MapPut("", SaveAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit).Idempotent("replaces the stored backup settings; If-Match refuses a stale repeat").IfMatch(required: false).Produces<OffsiteBackupSettingsView>();
        group.MapPost("/google/authorize", AuthorizeAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit).NotIdempotent("each call issues a new one-time consent state").IfMatch(required: false).Produces<OffsiteBackupAuthorizationView>();
        group.MapPost("/google/complete", CompleteAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit).NotIdempotent("redeems Google's one-time authorization code, which works once").Produces<OffsiteBackupSettingsView>();
        group.MapPost("/google/disconnect", DisconnectAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit).Idempotent("forgets the Google connection; a repeat finds none").Produces<OffsiteBackupSettingsView>();
        group.MapPost("/test", TestAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit).RequireGate(ApiGate.Elevated).Idempotent("reads a fresh token and the Drive folder; a repeat finds the same folder").Produces<OffsiteBackupTestView>();
        group.MapPost("/run", RunAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit).RequireGate(ApiGate.Elevated).Idempotent("sets the run-requested mark the host timer reads; a repeat sets it again").Produces<OffsiteBackupRunRequestedView>(StatusCodes.Status202Accepted);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(HttpContext http, OffsiteBackupSettingsService service, IConfiguration configuration)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        return Versioned(http, await service.GetAsync(tenant.TenantId, http.RequestAborted), configuration);
    }

    private static async Task<IResult> SaveAsync(SaveRequest request, HttpContext http, OffsiteBackupSettingsService service, IConfiguration configuration,
        UserManager<ApplicationUser> users, AppDbContext db, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time, OffsiteBackupChangeAlert alert)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (IfMatchVersion(http, out var expected) is { } invalidEtag) return invalidEtag;
        var before = await service.GetAsync(tenant.TenantId, http.RequestAborted);
        if (expected is null && before.Version > 0) return SettingsEndpoints.PreconditionRequired(http);
        if (await Reauth(request.Password, request.Code, request.RecoveryCode, http, users, db, protection, totp, hasher, time) is { } refusal) return refusal;
        var (view, problem) = await SaveSettingsAsync(tenant.TenantId, tenant.UserId, new SetupSettings(request.Enabled, request.AgeRecipient, request.FolderMode, request.FolderValue, request.KeepDays, request.KeepMonths, request.ClientId, request.ClientSecret), expected, before, http, service, alert);
        return view is not null ? Versioned(http, view, configuration) : problem!;
    }

    // Saves the settings after the caller has checked If-Match and re-authenticated; the owner is alerted of a change.
    private static async Task<(OffsiteBackupView? View, IResult? Problem)> SaveSettingsAsync(Guid tenantId, Guid userId, SetupSettings settings, long? expected, OffsiteBackupView before, HttpContext http, OffsiteBackupSettingsService service, OffsiteBackupChangeAlert alert)
    {
        try
        {
            var result = await service.SaveAsync(tenantId, userId, http.TraceIdentifier,
                new OffsiteBackupSaveInput(settings.Enabled, settings.AgeRecipient, settings.FolderMode, settings.FolderValue, settings.KeepDays, settings.KeepMonths, settings.ClientId, settings.ClientSecret),
                expected, http.RequestAborted);
            if (result.View is { } saved && OffsiteBackupChangeAlert.Describe(before, saved, Actor(http)) is { } change) await alert.SendAsync(tenantId, change, CancellationToken.None);
            return result.View is { } view ? (view, null) : (null, Problem(http, result.Error!));
        }
        catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException || SettingsEndpoints.IsUniqueViolation(exception))
        {
            return (null, Problem(http, new("PRECONDITION_FAILED", "Ayarlar başka bir oturumda değişti; sayfayı yenileyip yeniden deneyin.", 412)));
        }
    }

    private static async Task<IResult> AuthorizeAsync(AuthorizeRequest request, HttpContext http, OffsiteBackupSettingsService service, IConfiguration configuration,
        UserManager<ApplicationUser> users, AppDbContext db, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time, OffsiteBackupChangeAlert alert)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        // With settings it is the save's request too: the same If-Match rules as PUT, checked before the re-authentication.
        long? expected = null;
        OffsiteBackupView? before = null;
        if (request.Settings is not null)
        {
            if (IfMatchVersion(http, out expected) is { } invalidEtag) return invalidEtag;
            before = await service.GetAsync(tenant.TenantId, http.RequestAborted);
            if (expected is null && before.Version > 0) return SettingsEndpoints.PreconditionRequired(http);
        }
        if (await Reauth(request.Password, request.Code, request.RecoveryCode, http, users, db, protection, totp, hasher, time) is { } refusal) return refusal;
        if (request.Settings is { } settings && (await SaveSettingsAsync(tenant.TenantId, tenant.UserId, settings, expected, before!, http, service, alert)).Problem is { } saveProblem) return saveProblem;
        var (url, error) = await service.BeginAuthorizationAsync(tenant.TenantId, tenant.UserId, RedirectUri(http, configuration), http.TraceIdentifier, http.RequestAborted);
        return error is not null ? Problem(http, error) : Results.Ok(new { url });
    }

    private static async Task<IResult> CompleteAsync(CompleteRequest request, HttpContext http, OffsiteBackupSettingsService service, IConfiguration configuration, OffsiteBackupChangeAlert alert)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var before = await service.GetAsync(tenant.TenantId, http.RequestAborted);
        var result = await service.CompleteAuthorizationAsync(tenant.TenantId, tenant.UserId, request.Code, request.State, request.Error, http.TraceIdentifier, http.RequestAborted);
        if (result.View is { } connected && OffsiteBackupChangeAlert.Describe(before, connected, Actor(http)) is { } change) await alert.SendAsync(tenant.TenantId, change, CancellationToken.None);
        return result.View is { } view ? Versioned(http, view, configuration) : Problem(http, result.Error!);
    }

    // Who made the change, for the owner's alert: the signed-in account's name (its e-mail).
    private static string Actor(HttpContext http) => http.User.Identity?.Name is { Length: > 0 } name ? name : "bilinmeyen kullanıcı";

    private static async Task<IResult> DisconnectAsync(UserManagementEndpoints.ReauthRequest request, HttpContext http, OffsiteBackupSettingsService service, IConfiguration configuration,
        UserManager<ApplicationUser> users, AppDbContext db, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (await Reauth(request.Password, request.Code, request.RecoveryCode, http, users, db, protection, totp, hasher, time) is { } refusal) return refusal;
        var result = await service.DisconnectAsync(tenant.TenantId, tenant.UserId, http.TraceIdentifier, http.RequestAborted);
        return Versioned(http, result.View!, configuration);
    }

    private static async Task<IResult> TestAsync(HttpContext http, OffsiteBackupSettingsService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var (folderId, error) = await service.TestAsync(tenant.TenantId, tenant.UserId, http.TraceIdentifier, http.RequestAborted);
        return error is not null ? Problem(http, error) : Results.Ok(new { ok = true, folderId });
    }

    private static async Task<IResult> RunAsync(HttpContext http, OffsiteBackupSettingsService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var error = await service.RequestRunAsync(tenant.TenantId, tenant.UserId, http.TraceIdentifier, http.RequestAborted);
        return error is not null ? Problem(http, error) : Results.Accepted(value: new { requested = true });
    }

    // Where Google sends the person back; the owner registers exactly this address in the OAuth client.
    internal static string RedirectUri(HttpContext http, IConfiguration configuration)
    {
        var configured = configuration["Marketplace:PublicBaseUrl"]?.Trim().TrimEnd('/');
        var origin = !string.IsNullOrWhiteSpace(configured) && Uri.TryCreate(configured, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback)
            ? uri.GetLeftPart(UriPartial.Authority)
            : $"{http.Request.Scheme}://{http.Request.Host}";
        return origin + CallbackPath;
    }

    private static Task<IResult?> Reauth(string? password, string? code, string? recoveryCode, HttpContext http, UserManager<ApplicationUser> users, AppDbContext db, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time) =>
        AuthEndpoints.ReauthenticateAsync(new AuthEndpoints.ReauthenticateRequest(password ?? string.Empty, code, recoveryCode), http, users, db, protection, totp, hasher, time);

    private static IResult Versioned(HttpContext http, OffsiteBackupView view, IConfiguration configuration)
    {
        if (view.Version > 0) http.Response.Headers.ETag = $"\"v{view.Version}\"";
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new
        {
            enabled = view.Enabled,
            ageRecipient = view.AgeRecipient,
            folderMode = view.FolderMode,
            folderValue = view.FolderValue,
            keepDays = view.KeepDays,
            keepMonths = view.KeepMonths,
            clientId = view.ClientId,
            clientSecretSaved = view.ClientSecretSaved,
            redirectUri = RedirectUri(http, configuration),
            scopes = OffsiteBackupRules.AuthorizationScopes,
            connected = view.Connected,
            account = view.Account,
            connectedAt = view.ConnectedAt,
            authorizationInvalid = view.AuthorizationInvalid,
            authorizationError = view.AuthorizationError,
            folderId = view.FolderId,
            lastAttemptAt = view.LastAttemptAt,
            lastSuccessAt = view.LastSuccessAt,
            lastSet = view.LastSet,
            lastSize = view.LastSize,
            lastError = view.LastError,
            lastErrorAt = view.LastErrorAt,
            consecutiveFailures = view.ConsecutiveFailures,
            requestedAt = view.RequestedAt,
            runningSince = view.RunningSince,
            nextRunAt = view.NextRunAt,
            remoteCopies = view.RemoteCopies,
            updatedAt = view.UpdatedAt,
            version = view.Version
        });
    }

}
