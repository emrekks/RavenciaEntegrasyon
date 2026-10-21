using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    // Reads by number the Hepsiburada orders that package rows or the missing-invoice list name but
    // the panel does not have, and adds a package the list names to an order that lacks it (so the
    // invoice can be made before the package ships). A failed read is noted and retried next run.
    private async Task ReadMissingHepsiburadaOrdersAsync(Guid tenantId, Guid connectionId, string correlationId, IEnumerable<(string OrderNumber, string? PackageNumber)> references, CancellationToken cancellationToken)
    {
        var named = references
            .Where(reference => !string.IsNullOrWhiteSpace(reference.OrderNumber))
            .Select(reference => (OrderNumber: reference.OrderNumber.Trim(), PackageNumber: string.IsNullOrWhiteSpace(reference.PackageNumber) ? null : reference.PackageNumber.Trim()))
            .Distinct()
            .ToArray();
        if (named.Length == 0) return;
        var orderNumbers = named.Select(reference => reference.OrderNumber).Distinct(StringComparer.Ordinal).ToArray();
        var known = await (from order in db.Orders.AsNoTracking()
                           where order.TenantId == tenantId && order.ConnectionId == connectionId && orderNumbers.Contains(order.ExternalOrderId)
                           select new
                           {
                               order.ExternalOrderId,
                               Packages = db.ShipmentPackages.Where(package => package.TenantId == tenantId && package.OrderId == order.Id).Select(package => package.ExternalPackageId).ToList()
                           })
            .ToListAsync(cancellationToken);
        var knownPackages = known.ToDictionary(order => order.ExternalOrderId, order => order.Packages.ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        var toRead = named
            .Where(reference => !knownPackages.TryGetValue(reference.OrderNumber, out var packages)
                || reference.PackageNumber is { } package && !packages.Contains(package))
            .GroupBy(reference => reference.OrderNumber, StringComparer.Ordinal)
            .Select(group => group.FirstOrDefault(reference => reference.PackageNumber is not null) is { PackageNumber: not null } withPackage ? withPackage : group.First())
            .Take(HepsiburadaPackagePolicy.MaxOrderReadsPerRun)
            .ToArray();
        foreach (var (orderNumber, packageNumber) in toRead)
        {
            var issueKey = $"hepsiburada-missing-order:{connectionId}:{orderNumber}";
            try
            {
                var payload = JsonSerializer.Serialize(new { connectionId, externalOrderId = orderNumber, packageNumber });
                await SyncHepsiburadaOrders(tenantId, connectionId, payload, correlationId, "ORDERS", false, cancellationToken);
                await ResolveIssue(tenantId, issueKey, cancellationToken);
            }
            // A quota rejection (or refused credentials) is about the connection, not this order: the next reads
            // would hit the same wall, so it ends the job and lets the connection back off instead of being
            // noted as an unread order while the loop keeps calling the marketplace.
            catch (JobProcessingException exception) when (!exception.Result.BackOffConnection)
            {
                await RecordIssue(tenantId, issueKey, exception.Result.ErrorCode ?? "HEPSIBURADA_MISSING_ORDER_UNREAD",
                    $"Hepsiburada'da görünen {orderNumber} numaralı sipariş{(packageNumber is null ? "" : $" ({packageNumber} paketiyle)")} okunamadı; sonraki taramada yeniden denenecek. {exception.Result.ErrorSummary}", cancellationToken);
            }
        }
    }
}
