using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;

namespace MarketplaceHub.Api.Catalog;

// Commission tariffs the seller uploads for a connection (Trendyol's weekly "komisyon tarifesi" export). Reading them
// is open to every signed-in role; uploading and deleting is operational work (the "connections" rule).
internal static class CommissionTariffEndpoints
{
    public static RouteGroupBuilder MapCommissionTariffEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/connections/{connectionId:guid}/commission-tariffs", ListAsync);
        api.MapPost("/connections/{connectionId:guid}/commission-tariffs", UploadAsync).DisableAntiforgery();
        api.MapGet("/connections/{connectionId:guid}/commission-tariffs/{tariffId:guid}/rows", RowsAsync);
        api.MapDelete("/connections/{connectionId:guid}/commission-tariffs/{tariffId:guid}", DeleteAsync);
        api.MapGet("/connections/{connectionId:guid}/commission-tariffs/{tariffId:guid}/facets", FacetsAsync);
        // The seller's band picks as an Excel file. It writes nothing; as a POST under /connections it takes the operational
        // roles, as the upload does.
        api.MapPost("/connections/{connectionId:guid}/commission-tariffs/{tariffId:guid}/export", ExportAsync);
        // The seller's saved picks: read by every role, replaced (PUT, the operational roles) after each change on the page.
        api.MapGet("/connections/{connectionId:guid}/commission-tariffs/{tariffId:guid}/selection", SelectionAsync);
        api.MapPut("/connections/{connectionId:guid}/commission-tariffs/{tariffId:guid}/selection", SaveSelectionAsync);
        // Promotion offers (Plus, advantage label, flash): the latest file of each kind, the kind told by its columns.
        api.MapGet("/connections/{connectionId:guid}/promotion-offers", ListOffersAsync);
        api.MapPost("/connections/{connectionId:guid}/promotion-offers", UploadOfferAsync).DisableAntiforgery();
        api.MapGet("/connections/{connectionId:guid}/promotion-offers/{kind}", OfferAsync);
        api.MapDelete("/connections/{connectionId:guid}/promotion-offers/{kind}", DeleteOfferAsync);
        return api;
    }

    private static async Task<IResult> ListOffersAsync(Guid connectionId, HttpContext http, IPromotionOfferService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        return Result(await service.ListAsync(tenant.TenantId, connectionId, http.RequestAborted));
    }

    private static async Task<IResult> OfferAsync(Guid connectionId, string kind, HttpContext http, IPromotionOfferService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (OfferKind(kind) is not { } known) return Problem(http, new("PROMOTION_KIND_INVALID", "Promosyon türü PLUS, ADVANTAGE ya da FLASH olmalıdır.", 400));
        return Result(await service.DetailAsync(tenant.TenantId, connectionId, known, http.RequestAborted));
    }

    private static async Task<IResult> DeleteOfferAsync(Guid connectionId, string kind, HttpContext http, IPromotionOfferService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (OfferKind(kind) is not { } known) return Problem(http, new("PROMOTION_KIND_INVALID", "Promosyon türü PLUS, ADVANTAGE ya da FLASH olmalıdır.", 400));
        return Result(await service.DeleteAsync(tenant.TenantId, tenant.UserId, connectionId, known, http.TraceIdentifier, http.RequestAborted));
    }

    private static async Task<IResult> UploadOfferAsync(Guid connectionId, HttpContext http, IPromotionOfferService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (string.IsNullOrWhiteSpace(http.Request.Headers["Idempotency-Key"])) return Problem(http, new("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key başlığı zorunludur.", 400));
        var sizeFeature = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = PromotionOfferService.MaximumFileBytes + 128 * 1024;
        if (http.Request.ContentLength is > PromotionOfferService.MaximumFileBytes + 128 * 1024)
            return Problem(http, new("PROMOTION_OFFER_FILE_SIZE", "Dosya en fazla 10 MiB olabilir.", 413));
        if (!http.Request.HasFormContentType) return Problem(http, new("PROMOTION_OFFER_FORM_REQUIRED", "multipart/form-data biçiminde bir .xlsx dosyası gönderilmelidir.", 400));
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length <= 0) return Problem(http, new("PROMOTION_OFFER_FILE_REQUIRED", "Promosyon dosyasını seçin.", 400));
        if (file.Length > PromotionOfferService.MaximumFileBytes) return Problem(http, new("PROMOTION_OFFER_FILE_SIZE", "Dosya en fazla 10 MiB olabilir.", 413));
        if (!string.Equals(Path.GetExtension(Path.GetFileName(file.FileName)), ".xlsx", StringComparison.OrdinalIgnoreCase))
            return Problem(http, new("PROMOTION_OFFER_EXTENSION_INVALID", "Yalnız Trendyol'dan indirilen .xlsx dosyası kabul edilir.", 415));
        await using var input = file.OpenReadStream();
        return Result(await service.UploadAsync(tenant.TenantId, tenant.UserId, connectionId, file.FileName, input, http.TraceIdentifier, http.RequestAborted));
    }

    private static string? OfferKind(string kind) => Domain.PromotionOfferKinds.All.FirstOrDefault(x => x.Equals(kind, StringComparison.OrdinalIgnoreCase));

    private static async Task<IResult> ExportAsync(Guid connectionId, Guid tariffId, CommissionTariffExportRequest request, HttpContext http, ICommissionTariffService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var result = await service.ExportAsync(tenant.TenantId, connectionId, tariffId, request, http.RequestAborted);
        if (!result.Succeeded) return ApiProblems.Of(http, result.Error!);
        http.Response.Headers.CacheControl = "no-store";
        // How many rows carry a pick, and whether the file is the uploaded one or written again from the stored rows.
        http.Response.Headers["X-Picked-Rows"] = result.Value!.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers["X-Tariff-File"] = result.Value.FromSourceFile ? "original" : "rebuilt";
        return Results.File(result.Value.Content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", result.Value.FileName);
    }

    private static async Task<IResult> SelectionAsync(Guid connectionId, Guid tariffId, HttpContext http, ICommissionTariffService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        http.Response.Headers.CacheControl = "no-store";
        return Result(await service.SelectionAsync(tenant.TenantId, connectionId, tariffId, http.RequestAborted));
    }

    private static async Task<IResult> SaveSelectionAsync(Guid connectionId, Guid tariffId, CommissionTariffExportRequest request, HttpContext http, ICommissionTariffService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        return Result(await service.SaveSelectionAsync(tenant.TenantId, tenant.UserId, connectionId, tariffId, request, http.RequestAborted));
    }

    private static async Task<IResult> ListAsync(Guid connectionId, HttpContext http, ICommissionTariffService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        return Result(await service.ListAsync(tenant.TenantId, connectionId, http.RequestAborted));
    }

    private static async Task<IResult> UploadAsync(Guid connectionId, HttpContext http, ICommissionTariffService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (string.IsNullOrWhiteSpace(http.Request.Headers["Idempotency-Key"])) return Problem(http, new("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key başlığı zorunludur.", 400));
        var sizeFeature = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = CommissionTariffService.MaximumFileBytes + 128 * 1024;
        if (http.Request.ContentLength is > CommissionTariffService.MaximumFileBytes + 128 * 1024)
            return Problem(http, new("COMMISSION_TARIFF_FILE_SIZE", "Tarife dosyası en fazla 10 MiB olabilir.", 413));
        if (!http.Request.HasFormContentType) return Problem(http, new("COMMISSION_TARIFF_FORM_REQUIRED", "multipart/form-data biçiminde bir .xlsx dosyası gönderilmelidir.", 400));
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length <= 0) return Problem(http, new("COMMISSION_TARIFF_FILE_REQUIRED", "Komisyon tarifesi dosyasını seçin.", 400));
        if (file.Length > CommissionTariffService.MaximumFileBytes) return Problem(http, new("COMMISSION_TARIFF_FILE_SIZE", "Tarife dosyası en fazla 10 MiB olabilir.", 413));
        if (!string.Equals(Path.GetExtension(Path.GetFileName(file.FileName)), ".xlsx", StringComparison.OrdinalIgnoreCase))
            return Problem(http, new("COMMISSION_TARIFF_EXTENSION_INVALID", "Yalnız pazaryerinden indirilen .xlsx tarife dosyası kabul edilir.", 415));
        await using var input = file.OpenReadStream();
        return Result(await service.UploadAsync(tenant.TenantId, tenant.UserId, connectionId, file.FileName, input, http.TraceIdentifier, http.RequestAborted));
    }

    private static async Task<IResult> FacetsAsync(Guid connectionId, Guid tariffId, HttpContext http, ICommissionTariffService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        return Result(await service.FacetsAsync(tenant.TenantId, connectionId, tariffId, http.RequestAborted));
    }

    private static async Task<IResult> RowsAsync(Guid connectionId, Guid tariffId, string? search, int? page, int? limit, int? tier, string? cost, string? name, string? barcode, string? model,
        decimal? minStock, decimal? maxStock, string? category, string? brand, string? sort, string? dir, int? profitTier, decimal? minProfit, decimal? maxProfit, decimal? minShare, decimal? maxShare, HttpContext http, ICommissionTariffService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (page is < 0 or > 100_000) return Problem(http, new("PAGE_INVALID", "page 0 ile 100000 arasında olmalıdır.", 400));
        if (limit is not null and (< 1 or > 200)) return Problem(http, new("LIMIT_INVALID", "limit 1 ile 200 arasında olmalıdır.", 400));
        if (tier is not null and (< 1 or > 4)) return Problem(http, new("TIER_INVALID", "tier 1 ile 4 arasında olmalıdır.", 400));
        if (cost is not (null or "" or "with" or "without")) return Problem(http, new("COST_FILTER_INVALID", "cost with ya da without olmalıdır.", 400));
        if (minStock is < 0 or > 10_000_000 || maxStock is < 0 or > 10_000_000) return Problem(http, new("STOCK_FILTER_INVALID", "Stok 0 ile 10000000 arasında olmalıdır.", 400));
        if (profitTier is not null and (< 1 or > 4) || minShare is < -1000 or > 100 || maxShare is < -1000 or > 100 || minProfit is < -10_000_000 or > 10_000_000 || maxProfit is < -10_000_000 or > 10_000_000)
            return Problem(http, new("PROFIT_FILTER_INVALID", "Fiyat aralığı 1-4, kâr oranı en fazla %100 olmalıdır.", 400));
        if (sort is not (null or "" or "name" or "price" or "tier1" or "tier2" or "tier3" or "tier4") || dir is not (null or "" or "asc" or "desc"))
            return Problem(http, new("SORT_INVALID", "sort name, price ya da tier1-tier4; dir asc ya da desc olmalıdır.", 400));
        var filter = new CommissionTariffRowFilter(tier, cost switch { "with" => true, "without" => false, _ => null }, name, barcode, model, minStock, maxStock, category, brand,
            string.IsNullOrEmpty(sort) ? null : sort, dir == "desc", profitTier, minProfit, maxProfit, minShare, maxShare);
        return Result(await service.RowsAsync(tenant.TenantId, connectionId, tariffId, search, page ?? 0, limit ?? 50, filter, http.RequestAborted));
    }

    private static async Task<IResult> DeleteAsync(Guid connectionId, Guid tariffId, HttpContext http, ICommissionTariffService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        return Result(await service.DeleteAsync(tenant.TenantId, tenant.UserId, connectionId, tariffId, http.TraceIdentifier, http.RequestAborted));
    }

    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
    private static IResult Result<T>(ServiceResult<T> result) => result.Succeeded ? Results.Ok(result.Value) : ApiProblems.Of(null, result.Error!);
    private static IResult Unauthorized(HttpContext http) => Problem(http, new("AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401));
    private static IResult Problem(HttpContext http, ServiceError error) => ApiProblems.Of(http, error);
}
