using MarketplaceHub.Application;

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
        // Hakediş: what the marketplaces will pay and have paid, now (no period: due days and payouts of the last days).
        api.MapGet("/reports/payouts", async (HttpContext http, IPayoutsService service, string? platformCodes, string? connectionIds, bool? includeTest) =>
        {
            if (Tenant(http) is not { } tenant) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            if (!TryIds(connectionIds, out var ids)) return MarketplaceHub.Api.Security.ApiProblems.Of(http, "REPORT_CONNECTION_INVALID", "Mağaza kimliği geçersiz.", 400);
            var result = await service.SummaryAsync(tenant.TenantId, new PayoutQuery(Split(platformCodes), ids, includeTest ?? false), http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        });
        // The records of one payout, of a store's due day in a region, or of a search by order, invoice or record number.
        api.MapGet("/reports/payouts/lines", async (HttpContext http, IPayoutsService service, string? platformCodes, Guid? connectionId, string? payoutId, string? day, string? region, string? kind, string? q, int? page) =>
        {
            if (Tenant(http) is not { } tenant) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            DateOnly? due = null;
            if (!string.IsNullOrEmpty(day))
            {
                if (!MarketplaceHub.Api.Dashboard.DashboardEndpoints.TryDay(day, out var parsed) || parsed is null)
                    return MarketplaceHub.Api.Security.ApiProblems.Of(http, "REPORT_RANGE_INVALID", "Tarihler yyyy-AA-gg biçiminde olmalıdır.", 400);
                due = parsed;
            }
            if (page is < 0 or > 10_000 || q?.Length > 64 || payoutId?.Length > 64 || region?.Length > 16 || kind is not (null or "" or "PAYOUT_DAY" or "DUE_DAY" or "OVERDUE" or "PAID_DAY"))
                return MarketplaceHub.Api.Security.ApiProblems.Of(http, "PAYOUT_LINES_QUERY_INVALID", "Sorgu geçersiz.", 400);
            var result = await service.LinesAsync(tenant.TenantId, new PayoutQuery(Split(platformCodes), null, false),
                new PayoutLinesQuery(connectionId, payoutId, due, region, q, page ?? 0, string.IsNullOrEmpty(kind) ? null : kind), http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        });
        return endpoints;
    }

    private static List<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(50).ToList();

    private static bool TryIds(string? value, out List<Guid> ids)
    {
        ids = [];
        foreach (var item in Split(value))
        {
            if (!Guid.TryParse(item, out var id)) return false;
            ids.Add(id);
        }
        return true;
    }

    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
}
