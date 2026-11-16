using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 1 (16 Nov 2026): a connection's sync cursor by its resource, on the caller's AppDbContext. Get makes
// the row when it is missing (added, saved with the caller's next save), as the processor's Cursor did; Find only reads.
internal sealed class SyncCursorStore(AppDbContext db)
{
    public async Task<SyncCursor> GetAsync(Guid tenantId, Guid connectionId, string resource, CancellationToken cancellationToken)
    {
        var cursor = await FindAsync(tenantId, connectionId, resource, cancellationToken);
        if (cursor is not null) return cursor;
        cursor = new SyncCursor { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ResourceType = resource, Version = 1 };
        db.SyncCursors.Add(cursor);
        return cursor;
    }

    public Task<SyncCursor?> FindAsync(Guid tenantId, Guid connectionId, string resource, CancellationToken cancellationToken) =>
        db.SyncCursors.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resource, cancellationToken);
}
