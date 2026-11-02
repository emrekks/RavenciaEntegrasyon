using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// An operational issue that has not been seen for a month no longer describes the live system (the
// product mapping it named was fixed, the order it named was delivered), yet it stayed open and buried
// the live ones. Such issues are resolved; the next occurrence reopens the same record.
// A resolved issue is deleted once it was resolved, and last seen, longer ago than the retention: many
// issue keys carry an event id or a timestamp, so they would otherwise pile up forever. An issue the stale
// pass resolves today is therefore kept for the retention period instead of being deleted in the same pass.
public sealed class OperationalIssueRetentionService(AppDbContext db, TimeProvider timeProvider)
{
    public async Task<int> ResolveStaleAsync(TimeSpan staleAfter, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cutoff = now - staleAfter;
        return await db.OperationalIssues
            .Where(issue => issue.Status == IssueStatus.Open && issue.LastSeenAt < cutoff)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(issue => issue.Status, IssueStatus.Resolved)
                .SetProperty(issue => issue.ResolvedAt, now), cancellationToken);
    }

    public const string PackageQuantityRejectedCode = "PACKAGE_QUANTITY_INVARIANT_REJECTED";

    // The key MarketplaceJobProcessor gives a package's quantity refusal: package-quantity:{connection}:{package}:{event ms}.
    public static string PackageQuantityIssuePrefix(Guid connectionId, string externalPackageId) => $"package-quantity:{connectionId}:{externalPackageId}:";

    // The connection and package a quantity refusal names; null for another key shape (an order-level key).
    public static (Guid ConnectionId, string ExternalPackageId)? ParsePackageQuantityIssueKey(string dedupeKey)
    {
        var parts = dedupeKey.Split(':');
        return parts.Length == 4 && parts[0] == "package-quantity" && Guid.TryParse(parts[1], out var connectionId)
            && parts[2].Length > 0 && long.TryParse(parts[3], out _)
            ? (connectionId, parts[2])
            : null;
    }

    // A Trendyol quantity refusal of a package that no longer needs one: the package is superseded (a stored package
    // names it as its origin: a split parent whose children were read), or Trendyol no longer lists it (it was not
    // reported again for absentAfter and the panel never stored it; live 2 Nov 2026: 4040543346, a removed split
    // parent, is not in the Trendyol panel).
    public async Task<int> ResolveSupersededPackageQuantityIssuesAsync(TimeSpan absentAfter, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var open = await db.OperationalIssues
            .Where(issue => issue.Status != IssueStatus.Resolved && issue.Code == PackageQuantityRejectedCode && issue.TenantId != null)
            .ToListAsync(cancellationToken);
        var resolved = 0;
        foreach (var issue in open)
        {
            if (ParsePackageQuantityIssueKey(issue.DedupeKey) is not { } key) continue;
            var tenantId = issue.TenantId!.Value;
            var isTrendyol = await db.PlatformConnections.AnyAsync(connection => connection.TenantId == tenantId && connection.Id == key.ConnectionId && connection.PlatformCode == "TRENDYOL", cancellationToken);
            if (!isTrendyol) continue;
            var superseded = await db.ShipmentPackages.AnyAsync(package => package.TenantId == tenantId && package.ConnectionId == key.ConnectionId
                && package.OriginExternalPackageId == key.ExternalPackageId && package.ExternalPackageId != key.ExternalPackageId, cancellationToken);
            var absent = !superseded && issue.LastSeenAt < now - absentAfter
                && !await db.ShipmentPackages.AnyAsync(package => package.TenantId == tenantId && package.ConnectionId == key.ConnectionId && package.ExternalPackageId == key.ExternalPackageId, cancellationToken);
            if (!superseded && !absent) continue;
            issue.Status = IssueStatus.Resolved;
            issue.ResolvedAt = now;
            resolved++;
        }
        if (resolved > 0) await db.SaveChangesAsync(cancellationToken);
        return resolved;
    }

    public async Task<int> DeleteResolvedAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow() - retention;
        // Issues resolved before ResolvedAt existed (or by a path that does not set it) fall back to LastSeenAt.
        return await db.OperationalIssues
            .Where(issue => issue.Status == IssueStatus.Resolved && issue.LastSeenAt < cutoff
                && (issue.ResolvedAt ?? issue.LastSeenAt) < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
