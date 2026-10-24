using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;

namespace MarketplaceHub.Api.Catalog;

// The products page's export and import: the export and the summary are reads (every signed-in role); the upload
// preview and the apply are catalog work (the "products" rule). The preview writes nothing; only apply does.
internal static class ProductTransferEndpoints
{
    private const long MultipartOverhead = 128 * 1024;

    public static RouteGroupBuilder MapProductTransferEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/products/transfer/summary", SummaryAsync);
        api.MapGet("/products/transfer/export.xlsx", ExportAsync);
        api.MapPost("/products/transfer/import", PreviewAsync).DisableAntiforgery();
        api.MapPost("/products/transfer/apply", ApplyAsync);
        return api;
    }

    private static async Task<IResult> SummaryAsync(HttpContext http, IProductTransferService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        return Result(await service.SummaryAsync(tenant.TenantId, http.RequestAborted));
    }

    // missing=cost or missing=desi lists only the variants still without that value.
    private static async Task<IResult> ExportAsync(string? missing, Guid? brandId, Guid? categoryId, HttpContext http, IProductTransferService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        ProductTransferExportFilter? filter = missing?.Trim().ToLowerInvariant() switch
        {
            null or "" => ProductTransferExportFilter.All,
            "cost" => ProductTransferExportFilter.MissingCost,
            "desi" => ProductTransferExportFilter.MissingDesi,
            _ => null
        };
        if (filter is null) return Problem(http, new("PRODUCT_TRANSFER_FILTER_INVALID", "missing yalnız cost ya da desi olabilir.", 400));
        var result = await service.ExportAsync(tenant.TenantId, filter.Value, brandId, categoryId, http.RequestAborted);
        if (!result.Succeeded) return ApiProblems.Of(http, result.Error!);
        http.Response.Headers.CacheControl = "no-store";
        var name = filter switch
        {
            ProductTransferExportFilter.MissingCost => "urunler-maliyet-eksik",
            ProductTransferExportFilter.MissingDesi => "urunler-desi-eksik",
            _ => "urunler"
        };
        return Results.File(result.Value!, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{name}-{DateTime.UtcNow:yyyyMMdd}.xlsx");
    }

    private static async Task<IResult> PreviewAsync(HttpContext http, IProductTransferService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var sizeFeature = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = ProductTransferService.MaximumFileBytes + MultipartOverhead;
        if (http.Request.ContentLength is > ProductTransferService.MaximumFileBytes + MultipartOverhead)
            return Problem(http, new("PRODUCT_TRANSFER_FILE_SIZE", "Dosya en fazla 10 MiB olabilir.", 413));
        if (!http.Request.HasFormContentType) return Problem(http, new("PRODUCT_TRANSFER_FORM_REQUIRED", "multipart/form-data biçiminde bir .xlsx ya da .csv dosyası gönderilmelidir.", 400));
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length <= 0) return Problem(http, new("PRODUCT_TRANSFER_FILE_REQUIRED", "Ürün dosyasını seçin.", 400));
        if (file.Length > ProductTransferService.MaximumFileBytes) return Problem(http, new("PRODUCT_TRANSFER_FILE_SIZE", "Dosya en fazla 10 MiB olabilir.", 413));
        var extension = Path.GetExtension(Path.GetFileName(file.FileName));
        if (!string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
            return Problem(http, new("PRODUCT_TRANSFER_EXTENSION_INVALID", "Yalnız .xlsx ya da .csv dosyası kabul edilir.", 415));
        // fields: the fields to write, comma separated (COST, DESI, STOCK, PRICES); cost and desi when not given.
        string[] fields = string.IsNullOrWhiteSpace(form["fields"])
            ? [ProductTransferFields.Cost, ProductTransferFields.Desi]
            : form["fields"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(field => field.ToUpperInvariant()).Distinct().ToArray();
        await using var input = file.OpenReadStream();
        return Result(await service.PreviewAsync(tenant.TenantId, file.FileName, input, fields, http.RequestAborted));
    }

    private static async Task<IResult> ApplyAsync(ApplyProductTransferCommand command, HttpContext http, IProductTransferService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (string.IsNullOrWhiteSpace(http.Request.Headers["Idempotency-Key"])) return Problem(http, new("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key başlığı zorunludur.", 400));
        return Result(await service.ApplyAsync(tenant.TenantId, tenant.UserId, command, http.TraceIdentifier, http.RequestAborted));
    }

    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
    private static IResult Result<T>(ServiceResult<T> result) => result.Succeeded ? Results.Ok(result.Value) : ApiProblems.Of(null, result.Error!);
    private static IResult Unauthorized(HttpContext http) => Problem(http, new("AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401));
    private static IResult Problem(HttpContext http, ServiceError error) => ApiProblems.Of(http, error);
}
