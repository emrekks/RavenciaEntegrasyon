using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    // ISLEYICI-9 stage 1 (16 Nov 2026): cursors and issues are kept by their own classes on this job's AppDbContext; the
    // processor keeps the cursor for its run telemetry and hands both to the run's services (JobRunServices).
    private readonly SyncCursorStore cursors = new(db);
    private readonly IssueRecorder issues = new(db);

    private Task<SyncCursor> Cursor(Guid tenantId, Guid connectionId, string resource, CancellationToken cancellationToken) =>
        cursors.GetAsync(tenantId, connectionId, resource, cancellationToken);

    internal static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();
}
