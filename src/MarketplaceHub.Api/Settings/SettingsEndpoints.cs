using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Settings;

public static class SettingsEndpoints
{
    private const string ShippingLabelKey = "shipping-label";
    private const int MaximumJsonCharacters = 262_144;

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1/settings");
        api.MapGet("/shipping-label", GetShippingLabelSettingsAsync);
        api.MapPut("/shipping-label", SaveShippingLabelSettingsAsync);
        return endpoints;
    }

    private static async Task<IResult> GetShippingLabelSettingsAsync(HttpContext http, AppDbContext db)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var setting = await db.TenantSettings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Key == ShippingLabelKey, http.RequestAborted);
        var settings = ParseObject(setting?.ValueJson);
        return Results.Ok(new { settings, version = setting?.Version ?? 0 });
    }

    private static async Task<IResult> SaveShippingLabelSettingsAsync(JsonElement settings, HttpContext http, AppDbContext db, TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (settings.ValueKind != JsonValueKind.Object) return Problem(http, new("SHIPPING_LABEL_SETTINGS_INVALID", "Kargo etiketi ayarları JSON nesnesi olmalıdır.", 400));
        var valueJson = settings.GetRawText();
        if (valueJson.Length > MaximumJsonCharacters) return Problem(http, new("SHIPPING_LABEL_SETTINGS_TOO_LARGE", "Kargo etiketi ayarları çok büyük.", 413));

        var setting = await db.TenantSettings.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Key == ShippingLabelKey, http.RequestAborted);
        if (setting is null)
        {
            setting = new TenantSetting { TenantId = tenant.TenantId, Key = ShippingLabelKey, ValueJson = valueJson, UpdatedAt = timeProvider.GetUtcNow(), Version = 1 };
            db.TenantSettings.Add(setting);
        }
        else
        {
            setting.ValueJson = valueJson;
            setting.UpdatedAt = timeProvider.GetUtcNow();
            setting.Version++;
        }

        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { settings = ParseObject(valueJson), version = setting.Version });
    }

    private static JsonElement? ParseObject(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(valueJson);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
    private static IResult Unauthorized(HttpContext http) => Problem(http, new("AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401));
    private static IResult Problem(HttpContext http, ServiceError error) => Results.Json(new
    {
        type = $"https://marketplacehub.invalid/problems/{error.Code.ToLowerInvariant().Replace('_', '-')}",
        title = error.Message,
        status = error.Status,
        code = error.Code,
        correlationId = http.TraceIdentifier,
        retryable = error.Status is 429 or >= 500,
        fieldErrors = error.FieldErrors
    }, statusCode: error.Status, contentType: "application/problem+json");
}
