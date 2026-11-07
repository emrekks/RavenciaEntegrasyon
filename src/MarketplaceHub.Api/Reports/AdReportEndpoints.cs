using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;

namespace MarketplaceHub.Api.Reports;

// Trendyol's product ads report the seller uploads (Raporlar › Maliyet ve Reklam). Reading is open to every signed-in
// role; uploading is operational work (the "connections" rule), as the promotion files are.
internal static class AdReportEndpoints
{
    public static RouteGroupBuilder MapAdReportEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/reports/ad-report", LatestAsync);
        api.MapPost("/connections/{connectionId:guid}/ad-reports", UploadAsync).DisableAntiforgery();
        return api;
    }

    private static async Task<IResult> LatestAsync(HttpContext http, IAdReportService service)
    {
        if (Tenant(http) is not { } tenant) return ApiProblems.Unauthenticated(http);
        http.Response.Headers.CacheControl = "no-store";
        var result = await service.LatestAsync(tenant.TenantId, http.RequestAborted);
        return result.Succeeded ? Results.Ok(result.Value) : ApiProblems.Of(http, result.Error!);
    }

    private static async Task<IResult> UploadAsync(Guid connectionId, HttpContext http, IAdReportService service)
    {
        if (Tenant(http) is not { } tenant) return ApiProblems.Unauthenticated(http);
        if (string.IsNullOrWhiteSpace(http.Request.Headers["Idempotency-Key"])) return ApiProblems.Of(http, new("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key başlığı zorunludur.", 400));
        var sizeFeature = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = AdReportService.MaximumFileBytes + 128 * 1024;
        if (http.Request.ContentLength is > AdReportService.MaximumFileBytes + 128 * 1024)
            return ApiProblems.Of(http, new("AD_REPORT_FILE_SIZE", "Dosya en fazla 10 MiB olabilir.", 413));
        if (!http.Request.HasFormContentType) return ApiProblems.Of(http, new("AD_REPORT_FORM_REQUIRED", "multipart/form-data biçiminde bir .xlsx dosyası gönderilmelidir.", 400));
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length <= 0) return ApiProblems.Of(http, new("AD_REPORT_FILE_REQUIRED", "Reklam raporu dosyasını seçin.", 400));
        if (file.Length > AdReportService.MaximumFileBytes) return ApiProblems.Of(http, new("AD_REPORT_FILE_SIZE", "Dosya en fazla 10 MiB olabilir.", 413));
        if (!string.Equals(Path.GetExtension(Path.GetFileName(file.FileName)), ".xlsx", StringComparison.OrdinalIgnoreCase))
            return ApiProblems.Of(http, new("AD_REPORT_EXTENSION_INVALID", "Yalnız Trendyol'dan indirilen .xlsx dosyası kabul edilir.", 415));
        await using var input = file.OpenReadStream();
        var result = await service.UploadAsync(tenant.TenantId, tenant.UserId, connectionId, file.FileName, input, http.TraceIdentifier, http.RequestAborted);
        return result.Succeeded ? Results.Ok(result.Value) : ApiProblems.Of(http, result.Error!);
    }

    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
}
