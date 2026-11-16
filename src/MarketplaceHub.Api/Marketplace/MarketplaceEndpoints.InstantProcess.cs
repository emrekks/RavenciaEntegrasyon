using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Api.Common;
using MarketplaceHub.Api.Operations;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Api.Common.EndpointHelpers;

namespace MarketplaceHub.Api.Marketplace;

public static partial class MarketplaceEndpoints
{
    private static async Task<IResult> InstantProcessOrderAsync(Guid id, TenantContext tenant, HttpContext http, AppDbContext db, IMarketplaceSalesService service, IMarketplaceJobProcessor marketplaceProcessor, CancellationToken cancellationToken)
    {
        var detail = await service.OrderAsync(tenant.TenantId, id, cancellationToken);
        if (!detail.Succeeded) return Problem(http, detail.Error!);
        if (detail.Value!.Packages.Any(package => package.Status == "PAYMENT_PENDING"))
            return Problem(http, new("SHIPMENT_PAYMENT_PENDING", "Trendyol ödeme onayı vermeden sipariş işleme alınamaz.", 409));
        var package = PickingTarget(detail.Value!.Packages);
        if (package is null)
        {
            if (detail.Value.ConnectionId is not { } connectionId)
                return Problem(http, new("ORDER_CONNECTION_REQUIRED", "Siparişin aktif platform bağlantısı bulunamadı.", 422));

            // Refresh an order without creating a worker job, then continue the
            // picking action in the same request.
            var platform = await db.PlatformConnections.AsNoTracking()
                .Where(x => x.TenantId == tenant.TenantId && x.Id == connectionId)
                .Select(x => x.PlatformCode)
                .SingleOrDefaultAsync(cancellationToken);
            var sync = await marketplaceProcessor.ProcessAsync(
                tenant.TenantId,
                connectionId,
                MarketplaceJobTypes.ForPlatform(platform, MarketplaceJobTypes.OrderSync),
                JsonSerializer.Serialize(new { connectionId, externalOrderId = detail.Value.OrderNumber, full = false }),
                OperationCorrelation(http),
                cancellationToken);
            if (!sync.Succeeded)
            {
                var status = sync.Kind == JobCompletionKind.Retry ? 503 : 422;
                return Problem(http, new(sync.ErrorCode ?? "ORDER_REFRESH_FAILED", sync.ErrorSummary ?? (platform == "SHOPIFY" ? "Sipariş Shopify’dan anlık olarak yenilenemedi." : "Sipariş Trendyol’dan anlık olarak yenilenemedi."), status));
            }

            detail = await service.OrderAsync(tenant.TenantId, id, cancellationToken);
            if (!detail.Succeeded) return Problem(http, detail.Error!);
            if (detail.Value!.Packages.Any(package => package.Status == "PAYMENT_PENDING"))
                return Problem(http, new("SHIPMENT_PAYMENT_PENDING", "Trendyol ödeme onayı vermeden sipariş işleme alınamaz.", 409));
            package = PickingTarget(detail.Value!.Packages);
        }

        if (package is null)
            return Problem(http, new("PICKING_PACKAGE_NOT_FOUND", "Sipariş yenilendi ancak işleme alınabilir kargo paketi bulunamadı.", 422));

        return Result(await service.ProcessShipmentInstantAsync(
            tenant.TenantId,
            package.Id,
            package.Version,
            http.Request.Headers["Idempotency-Key"].ToString(),
            OperationCorrelation(http),
            cancellationToken), value =>
        {
            http.Response.Headers.ETag = $"\"v{value.Version}\"";
            return Results.Ok(value);
        });
    }

    // The order-level "İşleme Al" works on the package that is still new (a split order may hold
    // shipped or cancelled packages first); without one, the first package answers why not.
    internal static ShipmentView? PickingTarget(IReadOnlyList<ShipmentView> packages) =>
        packages.FirstOrDefault(package => package.Status == "NEW") ?? packages.FirstOrDefault();
}
