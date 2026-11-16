using MarketplaceHub.Api.Common;
using MarketplaceHub.Application;
using static MarketplaceHub.Api.Common.EndpointHelpers;

namespace MarketplaceHub.Api.Reports;

public static class ReportsEndpoints
{
    public static IEndpointRouteBuilder MapReportsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").WithGates(ApiGate.SignedIn, ApiGate.Elevated);
        // The sales report: calendar days in the tenant's time zone (yyyy-MM-dd), optional platform codes and store ids
        // (comma separated), day / week / month buckets (chosen by the period length when left out), and whether test
        // stores count.
        api.MapGet("/reports/sales", async (HttpContext http, IReportsService service, string? from, string? to, string? platformCodes, string? dateFrom, string? dateTo, string? platforms, string? connectionIds, string? granularity, bool? includeTest) =>
        {
            if (Tenant(http) is not { } tenant) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            if (!MarketplaceHub.Api.Dashboard.DashboardEndpoints.TryDay(Unified(dateFrom, from), out var start) || !MarketplaceHub.Api.Dashboard.DashboardEndpoints.TryDay(Unified(dateTo, to), out var end))
                return MarketplaceHub.Api.Security.ApiProblems.Of(http, "REPORT_RANGE_INVALID", "Tarihler yyyy-AA-gg biçiminde olmalıdır.", 400);
            var ids = new List<Guid>();
            foreach (var value in Split(connectionIds))
            {
                if (!Guid.TryParse(value, out var id)) return MarketplaceHub.Api.Security.ApiProblems.Of(http, "REPORT_CONNECTION_INVALID", "Mağaza kimliği geçersiz.", 400);
                ids.Add(id);
            }
            var result = await service.SalesAsync(tenant.TenantId, new ReportSalesQuery(start, end, Split(Unified(platforms, platformCodes)), ids, granularity, includeTest ?? false), http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        }).Produces<ReportSalesView>();
        // Sipariş Kârlılık Analizi: the period's orders with each one's profit, a page at a time.
        api.MapGet("/reports/order-profitability", async (HttpContext http, IReportsService service, string? from, string? to, string? platformCodes, string? dateFrom, string? dateTo, string? platforms, string? search, string? sort, string? dir, int? page, int? limit, int? pageSize, string? after) =>
        {
            if (Tenant(http) is not { } tenant) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            if (!MarketplaceHub.Api.Dashboard.DashboardEndpoints.TryDay(Unified(dateFrom, from), out var start) || !MarketplaceHub.Api.Dashboard.DashboardEndpoints.TryDay(Unified(dateTo, to), out var end))
                return MarketplaceHub.Api.Security.ApiProblems.Of(http, "REPORT_RANGE_INVALID", "Tarihler yyyy-AA-gg biçiminde olmalıdır.", 400);
            if (page is < 0 or > 100_000 || limit is not null and (< 1 or > 200) || pageSize is not null and (< 1 or > 200) || sort is not (null or "" or "date" or "amount" or "profit" or "profitOnCost" or "margin") || dir is not (null or "" or "asc" or "desc"))
                return MarketplaceHub.Api.Security.ApiProblems.Of(http, "REPORT_QUERY_INVALID", "Sayfa, sıralama ya da yön geçersiz.", 400);
            // Unified paging (PagingContract): the order is total (ReportsService breaks ties by the order id), so the list
            // also takes the cursor; after, the nextCursor of the previous page, wins over page.
            var size = pageSize ?? limit ?? 50;
            int? cursorPage;
            try { cursorPage = OffsetCursor.PageIndex(after, size); }
            catch (ArgumentException exception) { return MarketplaceHub.Api.Security.ApiProblems.Of(http, "INVALID_REQUEST", exception.Message, 400); }
            var result = await service.OrderProfitabilityAsync(tenant.TenantId, new ReportOrderProfitQuery(start, end, Split(Unified(platforms, platformCodes)), search?.Length > 120 ? search[..120] : search,
                sort, dir != "asc", cursorPage ?? page ?? 0, size), http.RequestAborted);
            if (!result.Succeeded) return MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
            var value = result.Value!;
            var next = OffsetCursor.Next(value.Page, value.PageSize, value.TotalCount);
            return Results.Ok(value with { NextCursor = next, HasMore = next is not null });
        }).Produces<ReportOrderProfitPage>();
        // Fiyat Hesapla's hints: each store's commission, cargo and service fee on the clothing orders of the last 60 days.
        api.MapGet("/reports/price-assumptions", async (HttpContext http, IReportsService service) =>
        {
            if (Tenant(http) is not { } tenant) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            var result = await service.PriceAssumptionsAsync(tenant.TenantId, http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        }).Produces<PriceAssumptionsView>();
        // Stok raporu: the stock now with its cost and value; the period only sets the sales the cover days are read from.
        api.MapGet("/reports/stock", async (HttpContext http, IReportsService service, string? from, string? to, string? platformCodes, string? dateFrom, string? dateTo, string? platforms, bool? includeTest) =>
        {
            if (Tenant(http) is not { } tenant) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            if (!MarketplaceHub.Api.Dashboard.DashboardEndpoints.TryDay(Unified(dateFrom, from), out var start) || !MarketplaceHub.Api.Dashboard.DashboardEndpoints.TryDay(Unified(dateTo, to), out var end))
                return MarketplaceHub.Api.Security.ApiProblems.Of(http, "REPORT_RANGE_INVALID", "Tarihler yyyy-AA-gg biçiminde olmalıdır.", 400);
            var result = await service.StockAsync(tenant.TenantId, new ReportStockQuery(start, end, Split(Unified(platforms, platformCodes)), includeTest ?? false), http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        }).Produces<ReportInventoryView>();
        // Stok's Tahmini Kâr: the real profit on cost of each of the last whole months (the platforms as the tab's filter).
        api.MapGet("/reports/profit-rate", async (HttpContext http, IReportsService service, string? platformCodes, string? platforms) =>
        {
            if (Tenant(http) is not { } tenant) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            var result = await service.ProfitRateAsync(tenant.TenantId, Split(Unified(platforms, platformCodes)), http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        }).Produces<ReportProfitRateView>();
        // Hakediş: what the marketplaces will pay and have paid, now (no period: due days and payouts of the last days).
        api.MapGet("/reports/payouts", async (HttpContext http, IPayoutsService service, string? platformCodes, string? platforms, string? connectionIds, bool? includeTest) =>
        {
            if (Tenant(http) is not { } tenant) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            if (!TryIds(connectionIds, out var ids)) return MarketplaceHub.Api.Security.ApiProblems.Of(http, "REPORT_CONNECTION_INVALID", "Mağaza kimliği geçersiz.", 400);
            var result = await service.SummaryAsync(tenant.TenantId, new PayoutQuery(Split(Unified(platforms, platformCodes)), ids, includeTest ?? false), http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
        }).Produces<PayoutView>();
        // The records of one payout, of a store's due day in a region, or of a search by order, invoice or record number.
        api.MapGet("/reports/payouts/lines", async (HttpContext http, IPayoutsService service, string? platformCodes, string? platforms, Guid? connectionId, string? payoutId, string? day, string? region, string? kind, string? q, int? page, int? limit, string? after) =>
        {
            if (Tenant(http) is not { } tenant) return MarketplaceHub.Api.Security.ApiProblems.Unauthenticated(http);
            DateOnly? due = null;
            if (!string.IsNullOrEmpty(day))
            {
                if (!MarketplaceHub.Api.Dashboard.DashboardEndpoints.TryDay(day, out var parsed) || parsed is null)
                    return MarketplaceHub.Api.Security.ApiProblems.Of(http, "REPORT_RANGE_INVALID", "Tarihler yyyy-AA-gg biçiminde olmalıdır.", 400);
                due = parsed;
            }
            if (page is < 0 or > 10_000 || limit is not null and (< 1 or > 100) || q?.Length > 64 || payoutId?.Length > 64 || region?.Length > 16 || kind is not (null or "" or "PAYOUT_DAY" or "DUE_DAY" or "OVERDUE" or "PAID_DAY"))
                return MarketplaceHub.Api.Security.ApiProblems.Of(http, "PAYOUT_LINES_QUERY_INVALID", "Sorgu geçersiz.", 400);
            // Unified paging (PagingContract): the records are in a total order (date, then id; PayoutsService), so the list
            // also takes the cursor; after wins over page. Without limit a page is a hundred records, as before.
            var size = limit ?? 100;
            int? cursorPage;
            try { cursorPage = OffsetCursor.PageIndex(after, size); }
            catch (ArgumentException exception) { return MarketplaceHub.Api.Security.ApiProblems.Of(http, "INVALID_REQUEST", exception.Message, 400); }
            var result = await service.LinesAsync(tenant.TenantId, new PayoutQuery(Split(Unified(platforms, platformCodes)), null, false),
                new PayoutLinesQuery(connectionId, payoutId, due, region, q, cursorPage ?? page ?? 0, string.IsNullOrEmpty(kind) ? null : kind, size), http.RequestAborted);
            if (!result.Succeeded) return MarketplaceHub.Api.Security.ApiProblems.Of(http, result.Error!);
            var value = result.Value!;
            var next = OffsetCursor.Next(value.Page, value.PageSize, value.TotalCount);
            return Results.Ok(value with { NextCursor = next, HasMore = next is not null });
        }).Produces<PayoutLinesPage>();
        // Trendyol's product ads report: the newest upload of each store, and the upload.
        api.MapAdReportEndpoints();
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

}
