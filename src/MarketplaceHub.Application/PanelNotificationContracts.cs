namespace MarketplaceHub.Application;

// The notification center (İşlem Takibi › Bildirimler): every notification the bell showed, kept on the server for a
// week whether or not it was removed from the bell (PanelNotification).
public static class PanelNotificationPolicy
{
    // A row is listed for seven days after it was raised and removed by the hourly retention after that.
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    public const int MaximumBatch = 50;
    public const int MaximumClientKeyLength = 120;
    public const int MaximumTitleLength = 200;
    public const int MaximumMessageLength = 2_000;
    public const int MaximumPayloadLength = 16_000;
    public const int MaximumSearchLength = 100;

    // The background notices (AttentionNoticePolicy) and the server's own records: written by the server only.
    public const string AttentionKeyPrefix = "attention:";
    public static readonly IReadOnlyList<string> ServerKeyPrefixes = [AttentionKeyPrefix, "offsite-backup:"];

    public static bool IsServerKey(string? clientKey) =>
        clientKey is not null && ServerKeyPrefixes.Any(prefix => clientKey.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    // A notification links only inside the panel: a path that starts with one '/', not "//host" or "/\host" (which a
    // browser reads as another site), and nothing with a scheme or control characters. Anything else is dropped.
    public static string? InternalHref(string? href)
    {
        var value = href?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 500 || value[0] != '/') return null;
        if (value.Length > 1 && value[1] is '/' or '\\') return null;
        if (value.Any(c => char.IsControl(c) || c == '\\')) return null;
        return value;
    }
}

public static class PanelNotificationSeverities
{
    public const string Success = "SUCCESS";
    public const string Error = "ERROR";
    public const string Warning = "WARNING";
    public const string Info = "INFO";
    public static readonly IReadOnlyList<string> All = [Success, Error, Warning, Info];
}

// What a notification is about; the browser derives it from the notification's subject (an order, a product...) or its
// source (ATTENTION: a background notice such as a read failing run after run). Anything else is OTHER.
public static class PanelNotificationCategories
{
    public const string Other = "OTHER";
    public const string Attention = "ATTENTION";
    public static readonly IReadOnlyList<string> All =
        ["ORDER", "PACKAGE", "INVOICE", "RETURN", "PRODUCT", "CONNECTION", "JOB", "MAPPING", "QUESTION", "IMPORT", "ACCOUNT", Attention, Other];
}

// One notification from the bell. Shared: a notice for the whole tenant (every user sees it), else it is the caller's.
// Read: the bell already shows it read. CreatedAt: when the bell raised it (a first-load move of the browser's history
// sends older ones); it is clamped to now and a notification older than the retention is not kept.
public sealed record PanelNotificationInput(
    string ClientKey,
    string? Severity,
    string? Category,
    string? Title,
    string Message,
    string? Href = null,
    string? ActionLabel = null,
    string? PlatformCode = null,
    string? StoreName = null,
    int? Count = null,
    string? Payload = null,
    DateTimeOffset? CreatedAt = null,
    bool Shared = false,
    bool Read = false);

public sealed record PanelNotificationQuery(
    int? Limit = null,
    string? After = null,
    IReadOnlyList<string>? Categories = null,
    IReadOnlyList<string>? Severities = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Search = null,
    // UNREAD, READ or null for both.
    string? State = null)
{
    public int EffectiveLimit => Math.Clamp(Limit ?? 50, 1, 100);
}

public sealed record PanelNotificationView(
    Guid Id,
    string ClientKey,
    string Severity,
    string Category,
    string Title,
    string Message,
    string? Href,
    string? ActionLabel,
    string? PlatformCode,
    string? StoreName,
    int Count,
    string? Payload,
    bool Shared,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ReadAt,
    DateTimeOffset? DismissedAt);

public sealed record PanelNotificationSummary(int Total, int Unread, int Errors);

// Ids (center rows) or ClientKeys (bell entries) name the rows; All takes every row the caller sees.
public sealed record PanelNotificationSelection(IReadOnlyList<Guid>? Ids = null, IReadOnlyList<string>? ClientKeys = null, bool All = false);

public interface IPanelNotificationService
{
    Task<ServiceResult<int>> SaveAsync(Guid tenantId, Guid userId, IReadOnlyList<PanelNotificationInput> items, CancellationToken cancellationToken);
    Task<ServiceResult<PageResult<PanelNotificationView>>> PageAsync(Guid tenantId, Guid userId, PanelNotificationQuery query, CancellationToken cancellationToken);
    Task<PanelNotificationSummary> SummaryAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);
    Task<int> MarkReadAsync(Guid tenantId, Guid userId, PanelNotificationSelection selection, CancellationToken cancellationToken);
    // Removes them from the bell only: the center keeps listing them until the retention removes them.
    Task<int> DismissAsync(Guid tenantId, Guid userId, PanelNotificationSelection selection, CancellationToken cancellationToken);
}
