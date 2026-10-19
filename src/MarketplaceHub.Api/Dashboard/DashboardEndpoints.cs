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
            Tenant(http) is { } tenant ? Results.Ok(await service.BootstrapAsync(tenant.TenantId, http.RequestAborted)) : MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http));
        api.MapGet("/dashboard/revenue-series", async (HttpContext http, IDashboardReadService service, DateTimeOffset? from, DateTimeOffset? to, string? platform) =>
        {
            if (Tenant(http) is not { } tenant) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            var end = to ?? DateTimeOffset.UtcNow;
            var start = from ?? end.Date;
            var span = end - start;
            if (span > TimeSpan.FromDays(366)) return MarketplaceHub.Api.Security.ApiProblems.Of(http, "REVENUE_RANGE_TOO_LARGE", "Ciro aralığı en fazla 366 gün olabilir.", 400);
            return Results.Ok(await service.RevenueSeriesAsync(tenant.TenantId, start, end, platform, http.RequestAborted));
        });
        api.MapGet("/dashboard/integration-matrix", async (HttpContext http, AppDbContext db, TimeProvider timeProvider) =>
            Tenant(http) is { } tenant ? Results.Ok(await DashboardIntegrationMatrix.QueryAsync(db, tenant.TenantId, timeProvider.GetUtcNow(), http.RequestAborted)) : MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http));
        return endpoints;
    }

    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
}
