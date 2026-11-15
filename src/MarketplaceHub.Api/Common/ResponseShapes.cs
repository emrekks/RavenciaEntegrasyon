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

// Round 5 (API-GUVENLIK-8): the remaining anonymous success answers the document now names. Each has the handler's
// members in its order; ApiResponseSchemaTests re-serializes a real answer through it, or, for an answer that is a
// fixed literal the test host cannot reach, ApiResponseShapeLiteralTests compares it with the handler's literal.

/// <summary>POST /auth/mfa/setup: the authenticator to scan (one-time secret: never logged or stored in clear).</summary>
public sealed record MfaSetupView(string OtpauthUri, string Secret, string QrSvg, DateTimeOffset ExpiresAt);

/// <summary>POST /auth/mfa/confirm and /auth/recovery-codes/regenerate: the new one-time recovery codes.</summary>
public sealed record RecoveryCodesView(IReadOnlyList<string> RecoveryCodes);

/// <summary>POST /catalog/variant-codes/taken: the asked codes the catalog already holds.</summary>
public sealed record TakenVariantCodesView(IReadOnlyList<MarketplaceHub.Infrastructure.Persistence.VariantCodeAvailability.TakenVariantCode> Items);

/// <summary>201 of POST /files/product-media: the stored picture's media row.</summary>
public sealed record ProductMediaView(Guid Id, Guid ProductId, Guid? VariantId, Guid FileAssetId, string MediaRole, int SortOrder, string? AltText, string Status);

/// <summary>201 of POST /files/product-media-url: the linked picture's media row with its address.</summary>
public sealed record ProductMediaUrlView(Guid Id, Guid ProductId, Guid? VariantId, Guid FileAssetId, string Url, string MediaRole, int SortOrder, string? AltText, string Status);

/// <summary>201 of POST /files/return-evidence: the stored evidence file.</summary>
public sealed record ReturnEvidenceView(Guid Id, string MimeType, long SizeBytes, string Sha256);

/// <summary>PUT /files/product-media-reconcile.</summary>
public sealed record ProductMediaReconciledView(Guid ProductId, Guid? VariantId, int MediaCount);

/// <summary>PUT /files/product-media-family-reorder.</summary>
public sealed record ProductFamilyMediaReorderedView(int ChangedProductCount, int ChangedGalleryCount);

/// <summary>DELETE /files/product-media-items.</summary>
public sealed record ProductMediaDeletedView(int DeletedCount);

/// <summary>DELETE /files/product-media-item: the product's new version and every family card's.</summary>
public sealed record ProductMediaItemDeletedView(int DeletedCount, long Version, IReadOnlyDictionary<string, long> ProductVersions);

/// <summary>202 of POST /questions/sync.</summary>
public sealed record QuestionSyncQueuedView(IReadOnlyList<Guid> Jobs, int QueuedConnections, int SkippedConnections);

/// <summary>POST /notifications: { saved }.</summary>
public sealed record NotificationsSavedView(int Saved);

/// <summary>GET /settings/ai/usage.</summary>
public sealed record AiUsageView(IReadOnlyList<MarketplaceHub.Infrastructure.Ai.AiUsageLogView> Items);

/// <summary>PUT /products/{id}/size-chart: the chart picture's address, null when cleared.</summary>
public sealed record SizeChartMarkedView(string? SizeChartUrl);

/// <summary>POST /settings/users/{id}/password-reset (one-time secret).</summary>
public sealed record TemporaryPasswordView(string TemporaryPassword);

/// <summary>POST /settings/users/{id}/sessions/revoke.</summary>
public sealed record SessionsRevokedView(int Revoked);

/// <summary>POST /settings/offsite-backup/google/authorize: Google's consent address (carries a one-time state).</summary>
public sealed record OffsiteBackupAuthorizationView(string Url);

/// <summary>202 of POST /settings/offsite-backup/run: { requested: true }.</summary>
public sealed record OffsiteBackupRunRequestedView(bool Requested);

/// <summary>PUT /invoice-workspace/hidden.</summary>
public sealed record InvoiceWorkspaceHiddenView(bool Hidden, int UpdatedCount, int HiddenCount);

/// <summary>PUT /invoice-workspace/manual-status.</summary>
public sealed record InvoiceWorkspaceManualStatusView(string Status, int UpdatedCount, int ReopenedCount);

/// <summary>POST /invoice-workspace/manual-documents: the order's current manual document (duplicate: the same request again).</summary>
public sealed record ManualInvoiceDocumentView(Guid Id, string DocumentType, string DocumentUrl, string? InvoiceNumber, string Status, bool Duplicate);

/// <summary>202 of POST /invoice-workspace/manual-documents/{id}/delivery-jobs: the queued (or still running) deliveries.</summary>
public sealed record ManualInvoiceDeliveryQueuedView(Guid DocumentId, IReadOnlyList<Guid> JobIds, int QueuedPackageCount, int InspectionOnlyPackageCount);

/// <summary>200 of POST /invoice-workspace/manual-documents/{id}/delivery-jobs: every package already confirmed, nothing queued.</summary>
public sealed record ManualInvoiceDeliveryConfirmedView(Guid DocumentId, IReadOnlyList<Guid> JobIds, string Message);

/// <summary>POST /invoices/{id}/documents/manual: 201 with the stored document, 200 (duplicate) when the same file was stored before.</summary>
public sealed record InvoiceDocumentUploadView(Guid Id, string DocumentType, string Sha256, DateTimeOffset CreatedAt, bool Duplicate);

/// <summary>PUT /invoices/{id}/shopify-status: the package's manual invoice state as the panel shows it.</summary>
public sealed record ShopifyInvoiceStatusView(Guid Id, string Status, long Version);

/// <summary>POST /invoice-workspace/orders/{orderId}/confirm-no-remote-invoice and confirm-marketplace-delivered: { confirmed: true }.</summary>
public sealed record InvoiceConfirmedView(bool Confirmed);

/// <summary>POST /invoice-workspace/orders/{orderId}/replace-cancelled-invoice: { replaced: true }.</summary>
public sealed record InvoiceReplacedView(bool Replaced);

/// <summary>A webhook delivered to the connection's base address: { accepted: true }.</summary>
public sealed record WebhookAcceptedView(bool Accepted);

// Round 6 (API-GUVENLIK-8): the six answers round 5 left undeclared; the handlers keep their anonymous objects and
// ApiResponseShapeLiteralTests compares each with its record.

/// <summary>GET /orders/{id}/finance (Pazarama): the marketplace's finance rows for the order, read now.</summary>
public sealed record OrderFinanceView(DateTimeOffset ReadAt, IReadOnlyList<MarketplaceHub.Application.RemoteFinanceTransaction> Transactions, IReadOnlyList<MarketplaceHub.Application.RemoteCommissionInvoice> CommissionInvoices);

/// <summary>202 of POST /products/{id}/publication-jobs: the job and what the send left out of a card already on the marketplace.</summary>
public sealed record ProductPublicationQueuedView(Guid JobId, IReadOnlyList<MarketplaceHub.Application.PublicationSkippedVariant> SkippedVariants);

/// <summary>202 of POST /products/{id}/update-jobs: the job and the notices the panel records in its notification center.</summary>
public sealed record ProductUpdateQueuedView(Guid JobId, IReadOnlyList<string> Notices);

/// <summary>POST /settings/ai/test: the model that answered.</summary>
public sealed record AiConnectionTestView(bool Ok, string Model, string Message);

/// <summary>POST /settings/alert-email/test: { sent: true, recipients }.</summary>
public sealed record AlertEmailTestView(bool Sent, IReadOnlyList<string> Recipients);

/// <summary>POST /settings/offsite-backup/test: { ok: true, folderId }.</summary>
public sealed record OffsiteBackupTestView(bool Ok, string? FolderId);
