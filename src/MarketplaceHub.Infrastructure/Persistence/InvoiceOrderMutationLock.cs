using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public static class InvoiceOrderMutationLock
{
    public static async Task<bool> AcquireAsync(
        AppDbContext db,
        Guid tenantId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Sipariş fatura kilidi açık bir veritabanı işlemi içinde alınmalıdır.");

        var rows = await db.Database.SqlQuery<int>($"""
                SELECT 1 AS "Value"
                FROM sales.orders
                WHERE "TenantId" = {tenantId} AND "Id" = {orderId}
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
        return rows.Count > 0;
    }
}
