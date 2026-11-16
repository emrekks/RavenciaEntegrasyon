using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static MarketplaceHub.Infrastructure.Persistence.InvoiceLabelPolicy;
using static MarketplaceHub.Infrastructure.Persistence.OrderSnapshotReader;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceSalesService(AppDbContext db, CursorCodec cursors, IConfiguration configuration, IProductVisualLookupPort productVisuals, IOrderPort orders, IReturnPort returns, IPrivateFileStorage files, TimeProvider timeProvider) : IMarketplaceSalesService
{
    // A real Trendyol package supersedes the zero-ID payment placeholder, and a
    // package another package names as its origin (a split UnPacked parent, a
    // resent original) is superseded by it. Retain their history without
    // displaying, counting or invoicing them as live packages.
    internal IQueryable<ShipmentPackage> CurrentShipmentPackages() => CurrentShipmentPackages(db);

    // Also used by the dashboard and invoicing, so their package sets follow the same rule as the order screens.
    internal static IQueryable<ShipmentPackage> CurrentShipmentPackages(AppDbContext db) => CurrentShipmentPackages(db, db.ShipmentPackages.AsNoTracking());

    // The same rule over a caller's source, e.g. tracked packages that the invoicing precheck updates.
    internal static IQueryable<ShipmentPackage> CurrentShipmentPackages(AppDbContext db, IQueryable<ShipmentPackage> packages) => packages
        .Where(package => package.RawStatus != ShipmentPackageSupersession.LocallyUnpackedRawStatus)
        .Where(package => (package.ExternalPackageId != "0"
            || !db.PlatformConnections.Any(connection => connection.TenantId == package.TenantId
                && connection.Id == package.ConnectionId && connection.PlatformCode == "TRENDYOL")
            || !db.ShipmentPackages.Any(real => real.TenantId == package.TenantId
                && real.ConnectionId == package.ConnectionId && real.OrderId == package.OrderId
                && real.ExternalPackageId != "0" && real.ExternalPackageId != ""))
            && !db.ShipmentPackages.Any(child => child.TenantId == package.TenantId
                && child.ConnectionId == package.ConnectionId && child.OrderId == package.OrderId
                && child.OriginExternalPackageId == package.ExternalPackageId
                && child.ExternalPackageId != package.ExternalPackageId));

    // SIPARIS-STOK-10: the helpers the partials share live in SalesContext and SalesResults; these forward to them.
    private SalesContext? sales;
    internal SalesContext Sales => sales ??= new(db, cursors, configuration, productVisuals, orders, returns, files, timeProvider);

    private Task<bool> IsStageConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => Sales.IsStageConnection(tenantId, connectionId, cancellationToken);
    private Task<bool> IsProductionConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => Sales.IsProductionConnection(tenantId, connectionId, cancellationToken);
    private Task<bool> WritesEnabled(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => Sales.WritesEnabled(tenantId, connectionId, cancellationToken);
    private Task<bool> ExternalWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) => Sales.ExternalWritePolicyEnabledAsync(tenantId, connectionId, resourceType, cancellationToken);
    private Task<(bool Enabled, int IntervalSeconds)> ExternalWritePolicyAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) => Sales.ExternalWritePolicyAsync(tenantId, connectionId, resourceType, cancellationToken);
    private Task<IReadOnlyList<string>> CapabilityValues(Guid tenantId, Guid connectionId, string code, string property, CancellationToken cancellationToken) => Sales.CapabilityValues(tenantId, connectionId, code, property, cancellationToken);
    private static ShipmentView Map(ShipmentPackage x, string orderNumber) => SalesResults.Map(x, orderNumber);
    private static string Wire<T>(T value) where T : Enum => SalesResults.Wire(value);
    private static ServiceResult<T> Verifying<T>() => SalesResults.Verifying<T>();
    private Task<ServiceResult<T>> VerifyLaterAsync<T>(ExternalEffectRecord effect, CancellationToken cancellationToken) => Sales.VerifyLaterAsync<T>(effect, cancellationToken);
    private static Task<ServiceResult<T>> QueueInsteadAsync<T>(Task<ServiceResult<Guid>> enqueue) => SalesResults.QueueInsteadAsync<T>(enqueue);
    private Task<Dictionary<Guid, string>> MediaUrls(Guid tenantId, IEnumerable<Guid?> variantIds, CancellationToken cancellationToken) => Sales.MediaUrls(tenantId, variantIds, cancellationToken);
    private static bool IsAmbiguous(AdapterError error) => SalesResults.IsAmbiguous(error);
    private static ServiceResult<T> Invalid<T>(string field, string message) => SalesResults.Invalid<T>(field, message);
    private static ServiceResult<T> NotFound<T>() => SalesResults.NotFound<T>();
    private static ServiceResult<T> Precondition<T>(long version) => SalesResults.Precondition<T>(version);
}
