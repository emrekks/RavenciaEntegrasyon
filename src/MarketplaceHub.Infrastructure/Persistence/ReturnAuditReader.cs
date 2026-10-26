using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// A Trendyol return's status history, line by line, read live from getClaimItemAudits when the return detail asks for
// it. Nothing is stored and nothing is written to Trendyol; a line whose read fails says so and the others still show.
public sealed class ReturnAuditReader(AppDbContext db, IReturnAuditPort audits, TimeProvider timeProvider) : IReturnAuditReader
{
    // One request per line; a claim has a handful, this only bounds a malformed one.
    private const int MaximumLines = 20;

    public async Task<ServiceResult<IReadOnlyList<ReturnLineAuditView>>> ReturnAuditsAsync(Guid tenantId, Guid returnId, string correlationId, CancellationToken cancellationToken)
    {
        var claim = await db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == returnId).Select(x => new { x.Id, x.ConnectionId }).SingleOrDefaultAsync(cancellationToken);
        if (claim is null) return ServiceResult<IReadOnlyList<ReturnLineAuditView>>.Fail("RESOURCE_NOT_FOUND", "İade bulunamadı.", 404);
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == claim.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (!string.Equals(platform, "TRENDYOL", StringComparison.OrdinalIgnoreCase))
            return ServiceResult<IReadOnlyList<ReturnLineAuditView>>.Fail("RETURN_AUDIT_UNSUPPORTED", "Durum geçmişi yalnızca Trendyol iadelerinde okunabilir.", 422);

        var lines = await (from line in db.ReturnLines.AsNoTracking()
                           join orderLine in db.OrderLines.AsNoTracking() on new { line.TenantId, Id = line.OrderLineId } equals new { orderLine.TenantId, orderLine.Id }
                           where line.TenantId == tenantId && line.ClaimId == claim.Id
                           orderby line.Id
                           select new { line.Id, line.ExternalLineId, orderLine.TitleSnapshot })
            .Take(MaximumLines)
            .ToListAsync(cancellationToken);
        var views = new List<ReturnLineAuditView>();
        foreach (var line in lines)
        {
            var context = new AdapterContext(tenantId, claim.ConnectionId, correlationId, $"return-audit:{line.ExternalLineId}", timeProvider.GetUtcNow().AddSeconds(20));
            var result = await audits.ReturnLineAuditsAsync(context, line.ExternalLineId, cancellationToken);
            views.Add(result.IsSuccess
                ? new(line.Id, line.TitleSnapshot, result.Value!.Select(x => new ReturnAuditEntryView(x.PreviousStatus, x.NewStatus, x.ExecutorApp, x.ExecutorUser, x.At)).ToList(), null)
                : new(line.Id, line.TitleSnapshot, [], result.Error?.SafeMessage ?? "Durum geçmişi okunamadı."));
        }
        return ServiceResult<IReadOnlyList<ReturnLineAuditView>>.Ok(views);
    }
}
