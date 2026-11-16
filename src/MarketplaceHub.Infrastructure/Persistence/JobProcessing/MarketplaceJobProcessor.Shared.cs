using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    // ISLEYICI-9 stage 1 (16 Nov 2026): cursors and issues are kept by their own classes on this job's AppDbContext.
    private readonly SyncCursorStore cursors = new(db);
    private readonly IssueRecorder issues = new(db);

    // The cursors and issues live in SyncCursorStore and IssueRecorder; these names stay so the call sites read as before.
    // ISLEYICI-9 stage 5: the payload readers and the issue resolution lost their last callers here with the moved jobs.
    private Task<SyncCursor> Cursor(Guid tenantId, Guid connectionId, string resource, CancellationToken cancellationToken) =>
        cursors.GetAsync(tenantId, connectionId, resource, cancellationToken);

    private Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken) =>
        issues.RecordAsync(tenantId, key, code, summary, timeProvider.GetUtcNow(), cancellationToken);

    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotency) => new(tenantId, connectionId, correlationId, idempotency, timeProvider.GetUtcNow().AddMinutes(2));
    private static string Hash(string value) => TextValues.Sha256Hex(value);
    internal static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();
}
