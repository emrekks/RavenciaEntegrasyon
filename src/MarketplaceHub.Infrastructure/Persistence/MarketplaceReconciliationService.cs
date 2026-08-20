using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class MarketplaceReconciliationService(AppDbContext db, TimeProvider timeProvider) : IMarketplaceReconciliationService
{
    private static readonly HashSet<string> Scopes = new(StringComparer.Ordinal) { "PRODUCT_LISTING", "ORDER_PACKAGE_RETURN", "ALL_LOCAL" };

    public async Task<ServiceResult<ReconciliationRunView>> RunLocalDryAsync(Guid tenantId, Guid connectionId, string scope, CancellationToken cancellationToken)
    {
        var normalized = scope.Trim().ToUpperInvariant(); if (!Scopes.Contains(normalized)) return ServiceResult<ReconciliationRunView>.Fail("VALIDATION_FAILED", "Reconciliation scope PRODUCT_LISTING, ORDER_PACKAGE_RETURN veya ALL_LOCAL olmalıdır.", 422);
        var connection = await db.PlatformConnections
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => new { x.PlatformCode })
            .SingleOrDefaultAsync(cancellationToken);
        if (connection is null || !LocalReconciliationPolicy.Supports(connection.PlatformCode)) return NotFound();
        var now = timeProvider.GetUtcNow(); var run = new ReconciliationRun { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, Scope = normalized, Status = "RUNNING_LOCAL_DRY", StartedAt = now }; db.ReconciliationRuns.Add(run); var differences = new List<ReconciliationDifference>(); var compared = 0;
        if (normalized is "PRODUCT_LISTING" or "ALL_LOCAL")
        {
            var listings = await db.MarketplaceListingStates.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId).ToListAsync(cancellationToken); compared += listings.Count;
            differences.AddRange(listings.Where(x => x.DesiredStatus != x.ActualStatus).Select(x => Difference(tenantId, run.Id, "LISTING", x.VariantId.ToString("D"), "STATUS", x.DesiredStatus, x.ActualStatus, "REQUIRES_REMOTE_READ")));
        }
        if (normalized is "ORDER_PACKAGE_RETURN" or "ALL_LOCAL")
        {
            var orders = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId).ToListAsync(cancellationToken); compared += orders.Count;
            foreach (var order in orders)
            {
                var packageStatuses = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).Select(x => x.Status).ToListAsync(cancellationToken); var derived = packageStatuses.Count == 0 ? "NEW" : Wire(packageStatuses.OrderByDescending(StatusRank).First());
                if (derived != order.DerivedStatus) differences.Add(Difference(tenantId, run.Id, "ORDER", order.ExternalOrderId, "DERIVED_STATUS", order.DerivedStatus, derived, "LOCAL_RECOMPUTE_REQUIRED"));
            }
        }
        db.ReconciliationDifferences.AddRange(differences); run.ComparedCount = compared; run.DifferenceCount = differences.Count; run.Status = "COMPLETED_LOCAL_DRY"; run.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return ServiceResult<ReconciliationRunView>.Ok(Map(run, differences));
    }

    public async Task<ServiceResult<ReconciliationRunView>> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var run = await db.ReconciliationRuns.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (run is null) return NotFound(); var differences = await db.ReconciliationDifferences.AsNoTracking().Where(x => x.TenantId == tenantId && x.RunId == id).OrderBy(x => x.Id).ToListAsync(cancellationToken); return ServiceResult<ReconciliationRunView>.Ok(Map(run, differences));
    }
    private static ReconciliationDifference Difference(Guid tenantId, Guid runId, string entityType, string entityKey, string field, string local, string remote, string resolution) => new() { Id = Guid.CreateVersion7(), TenantId = tenantId, RunId = runId, EntityType = entityType, EntityKey = entityKey, FieldName = field, LocalValueHash = Hash(local), RemoteValueHash = Hash(remote), Resolution = resolution };
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ReconciliationRunView Map(ReconciliationRun run, IReadOnlyList<ReconciliationDifference> differences) => new(run.Id, run.ConnectionId, run.Scope, run.Status, run.ComparedCount, run.DifferenceCount, run.StartedAt, run.CompletedAt, differences.Select(x => new ReconciliationDifferenceView(x.EntityType, x.EntityKey, x.FieldName, x.LocalValueHash, x.RemoteValueHash, x.Resolution)).ToList());
    private static int StatusRank(ShipmentPackageStatus status) => status switch { ShipmentPackageStatus.New => 1, ShipmentPackageStatus.Processing => 2, ShipmentPackageStatus.OnHold => 3, ShipmentPackageStatus.ReadyToShip => 4, ShipmentPackageStatus.Shipped => 5, ShipmentPackageStatus.Undelivered => 6, ShipmentPackageStatus.Delivered => 7, ShipmentPackageStatus.ReturnInTransit => 8, ShipmentPackageStatus.Returned => 9, ShipmentPackageStatus.PartiallyCancelled => 2, ShipmentPackageStatus.Cancelled => 9, _ => 10 };
    private static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();
    private static ServiceResult<ReconciliationRunView> NotFound() => ServiceResult<ReconciliationRunView>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
}
