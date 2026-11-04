using MarketplaceHub.Application;

namespace MarketplaceHub.Api.Operations;

public static class PriceStockSendLogEndpoints
{
    // The price/stock send log (İşlem Takibi › Loglar): { items, nextCursor, hasMore }, newest first (order=asc: oldest
    // first), at most 100 a page. platforms, results and triggers are comma lists; from/to bound the send time; search
    // names a product or a code.
    public static IEndpointRouteBuilder MapPriceStockSendLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/price-stock-send-log", async (HttpContext http, IPriceStockSendLogService service, int? limit, string? after, string? platform, string? platforms,
            Guid? connectionId, DateTimeOffset? from, DateTimeOffset? to, string? search, string? results, string? triggers, string? order) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            if (tenant is null) return MarketplaceHub.Api.Security.ApiProblems.Of(http, new("AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401));
            var result = await service.PageAsync(tenant.TenantId,
                new PriceStockSendLogQuery(limit, after, JobEndpoints.Platforms(platform, platforms), connectionId, from, to, search, List(results), List(triggers),
                    string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)),
                http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        });
        // The summary above the log: [{ result, count }] under the same filters as the list, without its result filter.
        endpoints.MapGet("/api/v1/price-stock-send-log/counts", async (HttpContext http, IPriceStockSendLogService service, string? platform, string? platforms,
            Guid? connectionId, DateTimeOffset? from, DateTimeOffset? to, string? search, string? triggers) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            if (tenant is null) return MarketplaceHub.Api.Security.ApiProblems.Of(http, new("AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401));
            var result = await service.CountsAsync(tenant.TenantId,
                new PriceStockSendLogQuery(Platforms: JobEndpoints.Platforms(platform, platforms), ConnectionId: connectionId, From: from, To: to, Search: search, Triggers: List(triggers)),
                http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        });
        return endpoints;
    }

    private static string[]? List(string? value) => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
