using MarketplaceHub.Application;

namespace MarketplaceHub.Api.Operations;

public static class ProductActivityEndpoints
{
    // Ürün Hareketleri (İşlem Takibi › Loglar): { items, nextCursor, hasMore }, newest first, at most 100 a page. kinds
    // (STOCK, PRICE, STATUS, RETURN) and platforms are comma lists; from/to bound the time; search names a product or a
    // code. Read-only.
    public static IEndpointRouteBuilder MapProductActivityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/product-activity", async (HttpContext http, IProductActivityService service, int? limit, string? after, string? kinds, string? platform, string? platforms,
            DateTimeOffset? from, DateTimeOffset? to, string? search) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            if (tenant is null) return MarketplaceHub.Api.Security.ApiProblems.Of(http, new("AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401));
            var result = await service.PageAsync(tenant.TenantId,
                new ProductActivityQuery(limit, after, List(kinds), JobEndpoints.Platforms(platform, platforms), from, to, search), http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        });
        // The summary above the list: [{ kind, count }] under the same filters as the list, without its kind filter.
        endpoints.MapGet("/api/v1/product-activity/counts", async (HttpContext http, IProductActivityService service, string? platform, string? platforms,
            DateTimeOffset? from, DateTimeOffset? to, string? search) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            if (tenant is null) return MarketplaceHub.Api.Security.ApiProblems.Of(http, new("AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401));
            var result = await service.CountsAsync(tenant.TenantId,
                new ProductActivityQuery(Platforms: JobEndpoints.Platforms(platform, platforms), From: from, To: to, Search: search), http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        });
        return endpoints;
    }

    private static string[]? List(string? value) => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
