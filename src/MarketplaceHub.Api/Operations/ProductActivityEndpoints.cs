using MarketplaceHub.Api.Common;
using MarketplaceHub.Application;
using static MarketplaceHub.Api.Common.EndpointHelpers;

namespace MarketplaceHub.Api.Operations;

public static class ProductActivityEndpoints
{
    // Ürün Hareketleri (İşlem Takibi › Loglar): { items, nextCursor, hasMore }, newest first, at most 100 a page. kinds
    // (STOCK, PRICE, STATUS, RETURN) and platforms are comma lists; from/to bound the time; search names a product or a
    // code. Read-only.
    public static IEndpointRouteBuilder MapProductActivityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/product-activity", async (HttpContext http, IProductActivityService service, int? limit, string? after, string? kinds, string? platform, string? platforms,
            DateTimeOffset? from, DateTimeOffset? to, string? search, string? movements, bool? includeInitialPrices) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            if (tenant is null) return Unauthorized(http);
            var result = await service.PageAsync(tenant.TenantId,
                new ProductActivityQuery(limit, after, List(kinds), JobEndpoints.Platforms(platform, platforms), from, to, search, List(movements), includeInitialPrices == true), http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        }).RequireGate(ApiGate.SignedIn).Produces<PageResult<ProductActivityView>>();
        // The summary above the list: [{ kind, count }] under the same filters as the list, without its kind filter.
        endpoints.MapGet("/api/v1/product-activity/counts", async (HttpContext http, IProductActivityService service, string? platform, string? platforms,
            DateTimeOffset? from, DateTimeOffset? to, string? search, string? movements, bool? includeInitialPrices) =>
        {
            var tenant = http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
            if (tenant is null) return Unauthorized(http);
            var result = await service.CountsAsync(tenant.TenantId,
                new ProductActivityQuery(Platforms: JobEndpoints.Platforms(platform, platforms), From: from, To: to, Search: search, Movements: List(movements), IncludeInitialPrices: includeInitialPrices == true), http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        }).RequireGate(ApiGate.SignedIn).Produces<IReadOnlyList<ProductActivityCount>>();
        return endpoints;
    }

}
