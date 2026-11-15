using System.Text.Json.Nodes;

namespace MarketplaceHub.Api.Common;

// API-GUVENLIK-8: names for answers a handler writes as an anonymous object, so the OpenAPI document can describe them
// (`.Produces<T>()`). The handlers keep writing their anonymous objects; each record here has the same members in the
// same order, with the same types and null handling, and ApiResponseSchemaTests checks the real JSON against the
// schema the document derives from it. Change a handler's answer and its record together.

/// <summary>202 of a queued job (EndpointHelpers.Accepted): { jobId }.</summary>
public sealed record JobAccepted(Guid JobId);

/// <summary>GET /auth/csrf: { token }.</summary>
public sealed record CsrfTokenView(string Token);

/// <summary>POST /auth/login and /auth/change-password: { state } (ACTIVE, MFA_CHALLENGE, PASSWORD_CHANGE_REQUIRED, REVOKED).</summary>
public sealed record SessionStateView(string State);

/// <summary>GET /auth/me.</summary>
public sealed record CurrentUserView(Guid Id, string? Email, string DisplayName, string? Role, string State, Guid? TenantId);

/// <summary>GET /auth/security-status.</summary>
public sealed record SecurityStatusView(string TotpState, int RecoveryCodesRemaining);

/// <summary>One row of GET /auth/sessions.</summary>
public sealed record SignInSessionView(Guid Id, string State, DateTimeOffset IssuedAt, DateTimeOffset? LastSeenAt, DateTimeOffset ExpiresAt, bool Current);

/// <summary>GET /catalog/model-codes/holder.</summary>
public sealed record ModelCodeHolderView(bool Taken, Guid? ProductId, string? ProductTitle, string? Message);

/// <summary>POST /notifications/read and /notifications/dismiss: { updated }.</summary>
public sealed record NotificationsUpdatedView(int Updated);

/// <summary>GET and PUT of /settings/shipping-label, /settings/appearance and /settings/platform-update: the stored JSON object (null when none is saved) and its version.</summary>
public sealed record VersionedSettingsView(JsonObject? Settings, long Version);

/// <summary>GET and PUT /settings/resend-reminder.</summary>
public sealed record ResendReminderView(int Count, long Version);

/// <summary>GET /settings/offsite-backup (OffsiteBackupEndpoints.Versioned).</summary>
public sealed record OffsiteBackupSettingsView(
    bool Enabled,
    string? AgeRecipient,
    string FolderMode,
    string FolderValue,
    int KeepDays,
    int KeepMonths,
    string? ClientId,
    bool ClientSecretSaved,
    string RedirectUri,
    string Scopes,
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

/// <summary>GET /settings/users.</summary>
public sealed record TenantUsersView(Guid CurrentUserId, string CurrentRole, IReadOnlyList<TenantUserView> Items);

/// <summary>One user of GET /settings/users.</summary>
public sealed record TenantUserView(
    Guid Id,
    string DisplayName,
    string? Email,
    string Role,
    bool Active,
    bool LockedOut,
    bool MfaEnabled,
    bool PasswordChangePending,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt,
    int OpenSessions,
    bool Self);
