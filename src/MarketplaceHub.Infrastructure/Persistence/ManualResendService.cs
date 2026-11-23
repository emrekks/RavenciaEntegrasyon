using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Infrastructure.Persistence.SalesResults;

namespace MarketplaceHub.Infrastructure.Persistence;

// Owner, 23 Nov 2026 ("Platform seçtirsin ve sipariş no yazdırsın"): the Yeniden Gönderim gear keeps records instead of a
// number. A record names an order the panel already holds for a marketplace connection; the order is then listed under
// Siparişler › Yeniden Gönderimler marked "Elle Eklendi" (OrderQueryService RESENT, one rule for the tab, its count and
// the card) until the owner marks it handed to the carrier. The marketplace is never called: an unknown number is
// answered as unknown and nothing is stored. Panel only, no stock or invoice effect; every change leaves an audit row.
internal sealed class ManualResendService(AppDbContext db, TimeProvider timeProvider) : IManualResendService
{
    internal const string OrderNotFoundCode = "MANUAL_RESEND_ORDER_NOT_FOUND";
    internal const string DuplicateCode = "MANUAL_RESEND_DUPLICATE";
    internal const string ConnectionInvalidCode = "MANUAL_RESEND_CONNECTION_INVALID";
    internal const string AmbiguousCode = "MANUAL_RESEND_ORDER_AMBIGUOUS";
    internal const string ShipMarkNotAllowedCode = "MANUAL_RESEND_SHIP_MARK_NOT_ALLOWED";
    private const int MaximumOrderNumberLength = 160;

    public async Task<ManualResendListView> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        new(await Views(tenantId, null).ToListAsync(cancellationToken));

    public async Task<ServiceResult<ManualResendView>> AddAsync(Guid tenantId, Guid userId, ManualResendCreateCommand command, string correlationId, CancellationToken cancellationToken)
    {
        var number = NormalizeOrderNumber(command.OrderNumber);
        if (number is null) return Invalid<ManualResendView>("orderNumber", "Sipariş numarasını yazın.");
        if (number.Length > MaximumOrderNumberLength) return Invalid<ManualResendView>("orderNumber", "Sipariş numarası en fazla 160 karakter olabilir.");
        var connection = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == command.ConnectionId)
            .Select(x => new { x.Id, x.PlatformCode, x.DisplayName, x.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (connection is null || !ActiveIntegrationScope.IsMarketplace(connection.PlatformCode) || connection.Status is not ("ACTIVE" or "VERIFIED"))
            return ServiceResult<ManualResendView>.Fail(ConnectionInvalidCode, "Etkin bir pazaryeri bağlantısı seçin.", 422, new Dictionary<string, string[]> { ["connectionId"] = ["Etkin bir pazaryeri bağlantısı seçin."] });
        // The order as the panel holds it for that connection: its number as typed (a leading # dropped) or the
        // marketplace's own order id. Only what the panel already read; the marketplace is not asked.
        var hashed = "#" + number;
        var orders = await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connection.Id && (x.OrderNumber == number || x.OrderNumber == hashed || x.ExternalOrderId == number))
            .Select(x => new { x.Id, x.OrderNumber })
            .Take(2).ToListAsync(cancellationToken);
        if (orders.Count == 0)
            return ServiceResult<ManualResendView>.Fail(OrderNotFoundCode, $"{connection.DisplayName} bağlantısında {number} numaralı sipariş panelde yok. Numarayı kontrol edin ya da siparişler çekildikten sonra yeniden deneyin.", 404);
        if (orders.Count > 1)
            return ServiceResult<ManualResendView>.Fail(AmbiguousCode, $"{number} numarası bu bağlantıda birden fazla siparişe uyuyor; pazaryerinin sipariş numarasını tam yazın.", 409);
        var order = orders[0];
        if (await db.ManualResends.AnyAsync(x => x.TenantId == tenantId && x.OrderId == order.Id, cancellationToken))
            return ServiceResult<ManualResendView>.Fail(DuplicateCode, $"{order.OrderNumber} numaralı sipariş zaten Yeniden Gönderimler'e elle eklenmiş.", 409);
        var now = timeProvider.GetUtcNow();
        var record = new ManualResend { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, CreatedByUserId = userId, CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.ManualResends.Add(record);
        db.AuditLogs.Add(Audit(tenantId, userId, "MANUAL_RESEND_ADDED", record.Id, $"{connection.PlatformCode} {order.OrderNumber} Yeniden Gönderimler'e elle eklendi; pazaryerine bir şey gönderilmedi.", correlationId, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // The same order added at the same moment from another window.
            return ServiceResult<ManualResendView>.Fail(DuplicateCode, $"{order.OrderNumber} numaralı sipariş zaten Yeniden Gönderimler'e elle eklenmiş.", 409);
        }
        return ServiceResult<ManualResendView>.Ok(await Views(tenantId, record.Id).SingleAsync(cancellationToken));
    }

    public async Task<ServiceResult<bool>> RemoveAsync(Guid tenantId, Guid userId, Guid id, string correlationId, CancellationToken cancellationToken)
    {
        var record = await db.ManualResends.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (record is null) return NotFound<bool>();
        var orderNumber = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == record.OrderId).Select(x => x.OrderNumber).SingleOrDefaultAsync(cancellationToken);
        db.ManualResends.Remove(record);
        var now = timeProvider.GetUtcNow();
        db.AuditLogs.Add(Audit(tenantId, userId, "MANUAL_RESEND_REMOVED", record.Id, $"{orderNumber} için elle eklenen yeniden gönderim kaldırıldı; pazaryerine bir şey gönderilmedi.", correlationId, now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<ManualResendView>> MarkShippedAsync(Guid tenantId, Guid userId, Guid id, long expectedVersion, bool marked, string correlationId, CancellationToken cancellationToken)
    {
        var record = await db.ManualResends.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (record is null) return NotFound<ManualResendView>();
        if (record.Version != expectedVersion) return Precondition<ManualResendView>(record.Version);
        if (marked == (record.ShippedAt is not null))
            return ServiceResult<ManualResendView>.Fail(ShipMarkNotAllowedCode, marked ? "Bu yeniden gönderim zaten Kargoya Verildi olarak işaretli." : "Bu yeniden gönderimde Kargoya Verildi işareti yok.", 409);
        var now = timeProvider.GetUtcNow();
        record.ShippedAt = marked ? now : null;
        record.UpdatedAt = now;
        record.Version++;
        db.AuditLogs.Add(Audit(tenantId, userId, marked ? "MANUAL_RESEND_MARKED_SHIPPED" : "MANUAL_RESEND_SHIP_MARK_REMOVED", record.Id,
            marked ? "Elle eklenen yeniden gönderim Kargoya Verildi olarak işaretlendi; pazaryerine bir şey gönderilmedi."
                : "Elle eklenen yeniden gönderimin Kargoya Verildi işareti kaldırıldı; pazaryerine bir şey gönderilmedi.", correlationId, now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<ManualResendView>.Ok(await Views(tenantId, record.Id).SingleAsync(cancellationToken));
    }

    // The number as typed: trimmed, a leading # (as the panel shows it) dropped; null when nothing is left.
    internal static string? NormalizeOrderNumber(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim().TrimStart('#').Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private IQueryable<ManualResendView> Views(Guid tenantId, Guid? id) =>
        from record in db.ManualResends.AsNoTracking()
        join order in db.Orders.AsNoTracking() on new { record.TenantId, Id = record.OrderId } equals new { order.TenantId, order.Id }
        join connection in db.PlatformConnections.AsNoTracking() on new { order.TenantId, Id = order.ConnectionId } equals new { connection.TenantId, connection.Id }
        where record.TenantId == tenantId && (id == null || record.Id == id)
        orderby record.ShippedAt != null, record.CreatedAt descending, record.Id
        select new ManualResendView(record.Id, order.Id, order.OrderNumber, connection.Id, connection.PlatformCode, connection.DisplayName, record.CreatedAt, record.ShippedAt, record.Version);

    private static AuditLog Audit(Guid tenantId, Guid userId, string action, Guid targetId, string reason, string correlationId, DateTimeOffset now) => new()
    {
        TenantId = tenantId,
        ActorUserId = userId,
        Action = action,
        TargetType = "ManualResend",
        TargetId = targetId.ToString("D"),
        Reason = reason,
        CorrelationId = correlationId,
        CreatedAt = now
    };
}
