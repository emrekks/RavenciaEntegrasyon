using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Ai;
using MarketplaceHub.Infrastructure.SizeCharts;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Settings;

// Ayarlar › Yapay Zekâ (owner and administrator only; RoleAuthorizationMiddleware) and its first consumer, the
// product editor's size chart.
//   GET  /api/v1/settings/ai                       key saved or not (never the key), default model, skills
//   PUT  /api/v1/settings/ai                       save; a blank apiKey keeps the saved one; audit-logged
//   POST /api/v1/settings/ai/test                  a tiny request with the saved key and model
//   POST /api/v1/settings/ai/skills/{key}/upload   uploads the repository's skill with the Skills API
//   GET  /api/v1/settings/ai/usage                 the latest Claude calls (who, what, tokens, cost)
//   GET  /api/v1/products/{id}/size-chart          sizes, last measurements, the chart picture, AI readiness
//   POST /api/v1/products/{id}/size-chart/generate draws the chart (Claude + beden-tablosu skill); 30-180 s
//   PUT  /api/v1/products/{id}/size-chart          which picture of the card is its size chart (editor's Kaydet)
public static class AiEndpoints
{
    public sealed record SaveRequest(string? ApiKey, string? DefaultModel);
    public sealed record SizeChartMarkRequest(string? Url);

    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.MapGroup("/api/v1/settings/ai");
        settings.MapGet("", GetAsync);
        settings.MapPut("", SaveAsync);
        settings.MapPost("/test", TestAsync);
        settings.MapPost("/skills/{key}/upload", UploadSkillAsync);
        settings.MapGet("/usage", UsageAsync);

        var products = endpoints.MapGroup("/api/v1/products/{id:guid}/size-chart");
        products.MapGet("", GetSizeChartAsync);
        products.MapPost("/generate", GenerateSizeChartAsync);
        products.MapPut("", MarkSizeChartAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(HttpContext http, AiSettingsService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        return Versioned(http, await service.GetAsync(tenant.TenantId, http.RequestAborted));
    }

    private static async Task<IResult> SaveAsync(SaveRequest request, HttpContext http, AiSettingsService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (IfMatchVersion(http, out var expected) is { } invalidEtag) return invalidEtag;
        // D9: a stored setting is changed only over the version the page read (If-Match), never blind.
        if (expected is null && (await service.GetAsync(tenant.TenantId, http.RequestAborted)).Version > 0) return SettingsEndpoints.PreconditionRequired(http);
        try
        {
            var result = await service.SaveAsync(tenant.TenantId, tenant.UserId, http.TraceIdentifier, request.ApiKey, request.DefaultModel, expected, http.RequestAborted);
            return result.Settings is { } saved
                ? Versioned(http, saved)
                : Problem(http, new("AI_SETTINGS_INVALID", result.Error ?? "Yapay zekâ ayarları geçersiz.", 400));
        }
        catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException || SettingsEndpoints.IsUniqueViolation(exception))
        {
            return Problem(http, new("PRECONDITION_FAILED", "Ayarlar başka bir oturumda değişti; sayfayı yenileyip yeniden deneyin.", 412));
        }
    }

    private static async Task<IResult> TestAsync(HttpContext http, ClaudeService claude)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        try
        {
            var model = await claude.TestAsync(new AiCallContext(tenant.TenantId, tenant.UserId, "CONNECTION_TEST"), http.RequestAborted);
            return Results.Ok(new { ok = true, model, message = $"Bağlantı çalışıyor: {model} yanıt verdi." });
        }
        catch (ClaudeException exception) { return Problem(http, new(exception.Code, exception.Message, exception.Status)); }
    }

    private static async Task<IResult> UploadSkillAsync(string key, HttpContext http, ClaudeService claude)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (AiSkillCatalog.Find(key) is not { } skill) return Problem(http, new("AI_SKILL_UNKNOWN", "Bu adla bir beceri yok.", 404));
        try
        {
            var view = await claude.UploadSkillAsync(new AiCallContext(tenant.TenantId, tenant.UserId, "SKILL_UPLOAD", "Skill", null, skill.Title), http.TraceIdentifier, skill, http.RequestAborted);
            return Versioned(http, view);
        }
        catch (ClaudeException exception) { return Problem(http, new(exception.Code, exception.Message, exception.Status)); }
    }

    private static async Task<IResult> UsageAsync(HttpContext http, ClaudeService claude, int? limit)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var items = await claude.RecentAsync(tenant.TenantId, limit ?? 50, http.RequestAborted);
        return Results.Ok(new { items });
    }

    private static async Task<IResult> GetSizeChartAsync(Guid id, HttpContext http, SizeChartService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var view = await service.GetAsync(tenant.TenantId, id, http.RequestAborted);
        return view is null ? Problem(http, new("RESOURCE_NOT_FOUND", "Ürün bulunamadı.", 404)) : Results.Ok(view);
    }

    private static async Task<IResult> GenerateSizeChartAsync(Guid id, SizeChartInput request, HttpContext http, SizeChartService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        try { return Results.Ok(await service.GenerateAsync(tenant.TenantId, tenant.UserId, id, request, http.RequestAborted)); }
        catch (SizeChartException exception) { return Problem(http, new(exception.Code, exception.Message, exception.Status)); }
    }

    private static async Task<IResult> MarkSizeChartAsync(Guid id, SizeChartMarkRequest request, HttpContext http, SizeChartService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        try { return Results.Ok(new { sizeChartUrl = await service.SetAsync(tenant.TenantId, tenant.UserId, id, request.Url, http.RequestAborted) }); }
        catch (SizeChartException exception) { return Problem(http, new(exception.Code, exception.Message, exception.Status)); }
    }

    private static IResult? IfMatchVersion(HttpContext http, out long? expected)
    {
        expected = null;
        var value = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length < 4 || !value.StartsWith("\"v", StringComparison.Ordinal) || !value.EndsWith('"') || !long.TryParse(value[2..^1], out var version) || version <= 0)
            return Problem(http, new("INVALID_ETAG", "If-Match güçlü ETag biçiminde olmalıdır: \"v{version}\".", 400));
        expected = version;
        return null;
    }

    private static IResult Versioned(HttpContext http, AiSettingsView view)
    {
        if (view.Version > 0) http.Response.Headers.ETag = $"\"v{view.Version}\"";
        return Results.Ok(view);
    }

    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
    private static IResult Unauthorized(HttpContext http) => Problem(http, new("AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401));
    private static IResult Problem(HttpContext http, ServiceError error) => ApiProblems.Of(http, error);
}
