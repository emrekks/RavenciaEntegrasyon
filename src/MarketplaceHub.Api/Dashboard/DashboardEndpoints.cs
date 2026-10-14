using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;

namespace MarketplaceHub.Api.Dashboard;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1");
        api.MapGet("/dashboard/bootstrap", async (HttpContext http, IDashboardReadService service) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.BootstrapAsync(tenant.TenantId, http.RequestAborted)) : Results.Unauthorized());
        api.MapGet("/dashboard/revenue-series", async (HttpContext http, IDashboardReadService service, DateTimeOffset? from, DateTimeOffset? to, string? platform) =>
        {
            if (Tenant(http) is not { } tenant) return Results.Unauthorized();
            var end = to ?? DateTimeOffset.UtcNow;
            var start = from ?? end.Date;
            var span = end - start;
            if (span > TimeSpan.FromDays(366)) return Results.BadRequest(new { code = "REVENUE_RANGE_TOO_LARGE", title = "Ciro aralığı en fazla 366 gün olabilir." });
            return Results.Ok(await service.RevenueSeriesAsync(tenant.TenantId, start, end, platform, http.RequestAborted));
        });
        return endpoints;
    }

    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
}
