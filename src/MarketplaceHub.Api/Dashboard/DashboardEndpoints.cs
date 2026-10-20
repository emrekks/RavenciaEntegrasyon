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
        // The performance panel in one request: the chosen calendar days (tenant time zone, yyyy-MM-dd) and the
        // previous period of equal elapsed time, per day × store × currency.
        api.MapGet("/dashboard/performance", async (HttpContext http, IDashboardReadService service, string? from, string? to) =>
        {
            if (Tenant(http) is not { } tenant) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            if (!TryDay(from, out var start) || !TryDay(to, out var end))
                return MarketplaceHub.Api.Security.ApiProblems.Of(http, "REVENUE_RANGE_INVALID", "Tarihler yyyy-AA-gg biçiminde olmalıdır.", 400);
            var result = await service.PerformanceAsync(tenant.TenantId, start, end, http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        });
        api.MapGet("/dashboard/integration-matrix", async (HttpContext http, AppDbContext db, TimeProvider timeProvider) =>
            Tenant(http) is { } tenant ? Results.Ok(await DashboardIntegrationMatrix.QueryAsync(db, tenant.TenantId, timeProvider.GetUtcNow(), http.RequestAborted)) : MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http));
        return endpoints;
    }

    internal static bool TryDay(string? value, out DateOnly? day)
    {
        day = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)) return false;
        day = parsed;
        return true;
    }

    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
}
