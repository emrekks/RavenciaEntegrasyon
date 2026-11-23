using MarketplaceHub.Api.Common;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using static MarketplaceHub.Api.Common.EndpointHelpers;

namespace MarketplaceHub.Api.Marketplace;

public static partial class MarketplaceEndpoints
{
    // Owner, 23 Nov 2026 ("Platform seçtirsin ve sipariş no yazdırsın"): the Yeniden Gönderim gear's records. Reading the
    // list is open like the order list; adding, removing and the "Kargoya Verildi" mark are the owner's and administrators'
    // (the gate of the exchange package's mark). Panel only: no marketplace is called, no stock or invoice changes.
    private static void MapManualResendEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/manual-resends", async (HttpContext http, IManualResendService service) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.ListAsync(tenant.TenantId, http.RequestAborted)) : Unauthorized(http)).Produces<ManualResendListView>();
        api.MapPost("/manual-resends", async (ManualResendCreateCommand command, HttpContext http, IManualResendService service) =>
        {
            if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
            if (!RoleAuthorizationMiddleware.IsElevated(tenant.Role))
                return Problem(http, new("PERMISSION_DENIED", "Yeniden gönderim kaydı yalnız sahip ve yöneticiler içindir.", 403));
            return Created(await service.AddAsync(tenant.TenantId, tenant.UserId, command, OperationCorrelation(http), http.RequestAborted), "/api/v1/manual-resends");
        }).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<ManualResendView>(StatusCodes.Status201Created);
        api.MapDelete("/manual-resends/{id:guid}", async (Guid id, HttpContext http, IManualResendService service) =>
        {
            if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
            if (!RoleAuthorizationMiddleware.IsElevated(tenant.Role))
                return Problem(http, new("PERMISSION_DENIED", "Yeniden gönderim kaydı yalnız sahip ve yöneticiler içindir.", 403));
            var result = await service.RemoveAsync(tenant.TenantId, tenant.UserId, id, OperationCorrelation(http), http.RequestAborted);
            return result.Succeeded ? Results.NoContent() : Problem(http, result.Error!);
        }).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces(StatusCodes.Status204NoContent);
        api.MapPost("/manual-resends/{id:guid}/shipped", async (Guid id, ReturnExchangeMarkCommand command, HttpContext http, IManualResendService service) =>
        {
            if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
            if (!RoleAuthorizationMiddleware.IsElevated(tenant.Role))
                return Problem(http, new("PERMISSION_DENIED", "Yeniden gönderimin kargoya verildi işareti yalnız sahip ve yöneticiler içindir.", 403));
            return TryIfMatch(http, out var version, out var failure)
                ? WithEtag(http, await service.MarkShippedAsync(tenant.TenantId, tenant.UserId, id, version, command.Marked, OperationCorrelation(http), http.RequestAborted), x => x.Version)
                : failure!;
        }).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<ManualResendView>().IfMatch();
    }
}
