using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Identity;

namespace MarketplaceHub.Api.Reports;

public static class ReportsEndpoints
{
    public static IEndpointRouteBuilder MapReportsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1");
        // The sales report: calendar days in the tenant's time zone (yyyy-MM-dd), optional platform codes and store ids
        // (comma separated), day / week / month buckets (chosen by the period length when left out), and whether test
        // stores count.
        api.MapGet("/reports/sales", async (HttpContext http, IReportsService service, string? from, string? to, string? platformCodes, string? connectionIds, string? granularity, bool? includeTest) =>
        {
            if (Tenant(http) is not { } tenant) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            if (!MarketplaceHub.Api.Dashboard.DashboardEndpoints.TryDay(from, out var start) || !MarketplaceHub.Api.Dashboard.DashboardEndpoints.TryDay(to, out var end))
                return MarketplaceHub.Api.Security.ApiProblems.Of(http, "REPORT_RANGE_INVALID", "Tarihler yyyy-AA-gg biçiminde olmalıdır.", 400);
            var ids = new List<Guid>();
            foreach (var value in Split(connectionIds))
            {
                if (!Guid.TryParse(value, out var id)) return MarketplaceHub.Api.Security.ApiProblems.Of(http, "REPORT_CONNECTION_INVALID", "Mağaza kimliği geçersiz.", 400);
                ids.Add(id);
            }
            var result = await service.SalesAsync(tenant.TenantId, new ReportSalesQuery(start, end, Split(platformCodes), ids, granularity, includeTest ?? false), http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        });
        // Sipariş Kârlılık Analizi: the period's orders with each one's profit, a page at a time.
        api.MapGet("/reports/order-profitability", async (HttpContext http, IReportsService service, string? from, string? to, string? platformCodes, string? search, string? sort, string? dir, int? page, int? limit) =>
        {
            if (Tenant(http) is not { } tenant) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            if (!MarketplaceHub.Api.Dashboard.DashboardEndpoints.TryDay(from, out var start) || !MarketplaceHub.Api.Dashboard.DashboardEndpoints.TryDay(to, out var end))
                return MarketplaceHub.Api.Security.ApiProblems.Of(http, "REPORT_RANGE_INVALID", "Tarihler yyyy-AA-gg biçiminde olmalıdır.", 400);
            if (page is < 0 or > 100_000 || limit is not null and (< 1 or > 200) || sort is not (null or "" or "date" or "amount" or "profit" or "profitOnCost" or "margin") || dir is not (null or "" or "asc" or "desc"))
                return MarketplaceHub.Api.Security.ApiProblems.Of(http, "REPORT_QUERY_INVALID", "Sayfa, sıralama ya da yön geçersiz.", 400);
            var result = await service.OrderProfitabilityAsync(tenant.TenantId, new ReportOrderProfitQuery(start, end, Split(platformCodes), search?.Length > 120 ? search[..120] : search,
                sort, dir != "asc", page ?? 0, limit ?? 50), http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        });
        // Stok raporu: the stock now with its cost and value; the period only sets the sales the cover days are read from.
        api.MapGet("/reports/stock", async (HttpContext http, IReportsService service, string? from, string? to, string? platformCodes, bool? includeTest) =>
        {
            if (Tenant(http) is not { } tenant) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            if (!MarketplaceHub.Api.Dashboard.DashboardEndpoints.TryDay(from, out var start) || !MarketplaceHub.Api.Dashboard.DashboardEndpoints.TryDay(to, out var end))
                return MarketplaceHub.Api.Security.ApiProblems.Of(http, "REPORT_RANGE_INVALID", "Tarihler yyyy-AA-gg biçiminde olmalıdır.", 400);
            var result = await service.StockAsync(tenant.TenantId, new ReportStockQuery(start, end, Split(platformCodes), includeTest ?? false), http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        });
        return endpoints;
    }

    private static List<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(50).ToList();

    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
}
