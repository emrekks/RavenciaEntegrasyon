using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;

namespace MarketplaceHub.Api.Catalog;

// Product costs entered in bulk from a spreadsheet: the template and the summary are reads (every signed-in role); the
// upload preview and the apply are catalog work (the "products" rule). The preview writes nothing; only apply does.
internal static class ProductCostEndpoints
{
    private const long MultipartOverhead = 128 * 1024;

    public static RouteGroupBuilder MapProductCostEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/products/costs/summary", SummaryAsync);
        api.MapGet("/products/costs/template.xlsx", TemplateAsync);
        api.MapPost("/products/costs/import", PreviewAsync).DisableAntiforgery();
        api.MapPost("/products/costs/apply", ApplyAsync);
        return api;
    }

    private static async Task<IResult> SummaryAsync(HttpContext http, IProductCostService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        return Result(await service.SummaryAsync(tenant.TenantId, http.RequestAborted));
    }

    private static async Task<IResult> TemplateAsync(bool? missingOnly, Guid? brandId, Guid? categoryId, HttpContext http, IProductCostService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var result = await service.TemplateAsync(tenant.TenantId, missingOnly ?? false, brandId, categoryId, http.RequestAborted);
        if (!result.Succeeded) return ApiProblems.Of(http, result.Error!);
        http.Response.Headers.CacheControl = "no-store";
        var name = missingOnly == true ? "maliyet-eksik-sablonu" : "maliyet-sablonu";
        return Results.File(result.Value!, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{name}-{DateTime.UtcNow:yyyyMMdd}.xlsx");
    }

    private static async Task<IResult> PreviewAsync(HttpContext http, IProductCostService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var sizeFeature = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = ProductCostService.MaximumFileBytes + MultipartOverhead;
        if (http.Request.ContentLength is > ProductCostService.MaximumFileBytes + MultipartOverhead)
            return Problem(http, new("PRODUCT_COST_FILE_SIZE", "Dosya en fazla 10 MiB olabilir.", 413));
        if (!http.Request.HasFormContentType) return Problem(http, new("PRODUCT_COST_FORM_REQUIRED", "multipart/form-data biçiminde bir .xlsx ya da .csv dosyası gönderilmelidir.", 400));
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length <= 0) return Problem(http, new("PRODUCT_COST_FILE_REQUIRED", "Maliyet dosyasını seçin.", 400));
        if (file.Length > ProductCostService.MaximumFileBytes) return Problem(http, new("PRODUCT_COST_FILE_SIZE", "Dosya en fazla 10 MiB olabilir.", 413));
        var extension = Path.GetExtension(Path.GetFileName(file.FileName));
        if (!string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
            return Problem(http, new("PRODUCT_COST_EXTENSION_INVALID", "Yalnız .xlsx ya da .csv dosyası kabul edilir.", 415));
        await using var input = file.OpenReadStream();
        return Result(await service.PreviewAsync(tenant.TenantId, file.FileName, input, http.RequestAborted));
    }

    private static async Task<IResult> ApplyAsync(ApplyProductCostsCommand command, HttpContext http, IProductCostService service)
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
