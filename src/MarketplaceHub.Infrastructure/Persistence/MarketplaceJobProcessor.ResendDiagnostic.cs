using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// One-time, read-only diagnostic (owner, 11 Nov 2026): Trendyol order 11805932340 has a resend ("Yeniden Gönderim")
// package 4337242198 that no order read brought. After a Trendyol recovery read, the order's number queries and the
// stream window around 13:56Z are read once and their answers (buyer fields removed) are kept as a resolved
// operational issue. No order, package, line, invoice or report row is touched; a cursor keeps it to one run.
public sealed partial class MarketplaceJobProcessor
{
    internal const string ResendDiagnosticCursor = "TRENDYOL_RESEND_DIAGNOSTIC";
    internal const string ResendDiagnosticIssueCode = "TRENDYOL_RESEND_DIAGNOSTIC";
    internal const string ResendDiagnosticOrderNumber = "11805932340";
    internal static readonly string[] ResendDiagnosticPackageIds = ["4337242198", "4326252839"];
    internal static readonly DateTimeOffset ResendDiagnosticStreamStart = new(2026, 11, 11, 13, 50, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset ResendDiagnosticStreamEnd = new(2026, 11, 11, 14, 10, 0, TimeSpan.Zero);

    private async Task RunResendDiagnosticOnce(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        if (orders is not IOrderRawReadPort raw || cancellationToken.IsCancellationRequested) return;
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (platform != "TRENDYOL") return;
        var orderedAt = await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalOrderId == ResendDiagnosticOrderNumber)
            .Select(x => (DateTimeOffset?)x.OrderedAt).FirstOrDefaultAsync(cancellationToken);
        if (orderedAt is null) return;
        if (await db.SyncCursors.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == ResendDiagnosticCursor && x.LastSuccessAt != null, cancellationToken)) return;

        TrackRequest();
        var read = await raw.ReadOrderRawAsync(Context(tenantId, connectionId, correlationId, $"resend-diagnostic:{ResendDiagnosticOrderNumber}"),
            ResendDiagnosticOrderNumber, ResendDiagnosticPackageIds, orderedAt.Value, ResendDiagnosticStreamStart, ResendDiagnosticStreamEnd, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var cursor = await Cursor(tenantId, connectionId, ResendDiagnosticCursor, cancellationToken);
        cursor.LastAttemptAt = now;
        cursor.Version++;
        if (!read.IsSuccess)
        {
            cursor.LastError = read.Error?.Code;
            cursor.LastErrorAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        var key = $"trendyol-resend-diagnostic:{connectionId}:{ResendDiagnosticOrderNumber}";
        var summary = JsonSerializer.Serialize(new { orderNumber = ResendDiagnosticOrderNumber, readAt = now, reads = read.Value });
        var issue = await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.DedupeKey == key, cancellationToken);
        if (issue is null)
            db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = ResendDiagnosticIssueCode, Summary = summary, Status = IssueStatus.Resolved, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1, ResolvedAt = now });
        else
        {
            issue.Summary = summary;
            issue.LastSeenAt = now;
            issue.OccurrenceCount++;
        }
        cursor.LastSuccessAt = now;
        cursor.LastError = null;
        await db.SaveChangesAsync(cancellationToken);
    }
}
