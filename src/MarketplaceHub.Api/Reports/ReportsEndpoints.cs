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
        return endpoints;
    }

    private static List<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(50).ToList();

    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
}
