using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    // Reads by number the Hepsiburada orders that package rows or the missing-invoice list name
    // but the panel does not have. A failed read is noted and retried on the next run.
    private async Task ReadMissingHepsiburadaOrdersAsync(Guid tenantId, Guid connectionId, string correlationId, IEnumerable<string> orderNumbers, CancellationToken cancellationToken)
    {
        var named = orderNumbers.Where(number => !string.IsNullOrWhiteSpace(number)).Select(number => number.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (named.Length == 0) return;
        var known = await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && named.Contains(x.ExternalOrderId))
            .Select(x => x.ExternalOrderId)
            .ToListAsync(cancellationToken);
        var received = new List<RemoteOrder>();
        foreach (var orderNumber in named.Except(known, StringComparer.Ordinal).Take(HepsiburadaPackagePolicy.MaxOrderReadsPerRun))
        {
            TrackRequest();
            var order = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"hepsiburada-missing-order:{orderNumber}"), orderNumber, cancellationToken);
            if (!order.IsSuccess)
            {
                TrackResultFailure(order.Error);
                await RecordIssue(tenantId, $"hepsiburada-missing-order:{connectionId}:{orderNumber}", order.Error!.Code, $"Hepsiburada'da görünen {orderNumber} numaralı sipariş okunamadı; sonraki taramada yeniden denenecek. {order.Error.SafeMessage}", cancellationToken);
                continue;
            }
            TrackReceived();
            received.Add(order.Value!);
            await ResolveIssue(tenantId, $"hepsiburada-missing-order:{connectionId}:{orderNumber}", cancellationToken);
        }
        if (received.Count > 0) await UpsertOrders(tenantId, connectionId, received, cancellationToken);
    }
}
