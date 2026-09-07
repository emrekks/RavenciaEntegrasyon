using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Settings;

public static class SettingsEndpoints
{
    private const string ShippingLabelKey = "shipping-label";
    private const string AppearanceKey = "appearance";
    private const int MaximumJsonCharacters = 262_144;
    private static readonly HashSet<string> AppearanceFontFamilies = new(StringComparer.OrdinalIgnoreCase) { "inter", "system", "segoe", "arial" };
    private static readonly HashSet<string> AppearanceFontSizes = new(StringComparer.OrdinalIgnoreCase) { "small", "normal", "large", "extra-large" };
    private static readonly HashSet<string> AppearanceThemeModes = new(StringComparer.OrdinalIgnoreCase) { "system", "light", "dark" };
    private static readonly IReadOnlyDictionary<string, string> DefaultLightAppearanceColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["bg"] = "#f3f6fa", ["surface"] = "#ffffff", ["surfaceRaised"] = "#f8fbfd", ["surfaceSoft"] = "#eaf1f7", ["border"] = "#cbd9e5", ["borderStrong"] = "#9eb4c6", ["ink"] = "#10243a", ["muted"] = "#5b7186", ["subtle"] = "#7d91a3", ["primary"] = "#1677c8", ["primaryHover"] = "#0f62aa", ["primarySoft"] = "#e4f1ff", ["accent"] = "#0a9b8c", ["accentSoft"] = "#def7f1", ["warning"] = "#b7791f", ["warningSoft"] = "#fff4d8", ["danger"] = "#c53d52", ["dangerSoft"] = "#ffe8ed", ["info"] = "#326fbd"
    };
    private static readonly IReadOnlyDictionary<string, string> DefaultDarkAppearanceColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["bg"] = "#0a0f1a", ["surface"] = "#111827", ["surfaceRaised"] = "#1a2235", ["surfaceSoft"] = "#1d2638", ["border"] = "#1e2d45", ["borderStrong"] = "#243352", ["ink"] = "#f1f5f9", ["muted"] = "#94a3b8", ["subtle"] = "#64748b", ["primary"] = "#6366f1", ["primaryHover"] = "#4f46e5", ["primarySoft"] = "#292d67", ["accent"] = "#10b981", ["accentSoft"] = "#173c3a", ["warning"] = "#f59e0b", ["warningSoft"] = "#4a3514", ["danger"] = "#ef4444", ["dangerSoft"] = "#4a282c", ["info"] = "#3b82f6"
    };
    private static readonly string[] AppearanceColorTokens = ["bg", "surface", "surfaceRaised", "surfaceSoft", "border", "borderStrong", "ink", "muted", "subtle", "primary", "primaryHover", "primarySoft", "accent", "accentSoft", "warning", "warningSoft", "danger", "dangerSoft", "info"];

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1/settings");
        api.MapGet("/shipping-label", GetShippingLabelSettingsAsync);
        api.MapPut("/shipping-label", SaveShippingLabelSettingsAsync);
        api.MapGet("/appearance", GetAppearanceSettingsAsync);
        api.MapPut("/appearance", SaveAppearanceSettingsAsync);
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

    private static async Task<IResult> GetAppearanceSettingsAsync(HttpContext http, AppDbContext db)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var setting = await db.TenantSettings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Key == AppearanceKey, http.RequestAborted);
        var settings = NormalizeAppearance(ParseObject(setting?.ValueJson));
        return Results.Ok(new { settings, version = setting?.Version ?? 0 });
    }

    private static async Task<IResult> SaveAppearanceSettingsAsync(JsonElement settings, HttpContext http, AppDbContext db, TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (settings.ValueKind != JsonValueKind.Object) return Problem(http, new("APPEARANCE_SETTINGS_INVALID", "Görünüm ayarları JSON nesnesi olmalıdır.", 400));
        if (!TryNormalizeAppearance(settings, out var normalized)) return Problem(http, new("APPEARANCE_SETTINGS_INVALID", "Geçerli bir font tipi, yazı boyutu ve tema seçin.", 400));

        var valueJson = JsonSerializer.Serialize(normalized);
        var setting = await db.TenantSettings.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Key == AppearanceKey, http.RequestAborted);
        if (setting is null)
        {
            setting = new TenantSetting { TenantId = tenant.TenantId, Key = AppearanceKey, ValueJson = valueJson, UpdatedAt = timeProvider.GetUtcNow(), Version = 1 };
            db.TenantSettings.Add(setting);
        }
        else
        {
            setting.ValueJson = valueJson;
            setting.UpdatedAt = timeProvider.GetUtcNow();
            setting.Version++;
        }

        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { settings = normalized, version = setting.Version });
    }

    private static object NormalizeAppearance(JsonElement? value)
    {
        var fontFamily = value is { ValueKind: JsonValueKind.Object } ? ReadString(value.Value, "fontFamily") : null;
        var fontSize = value is { ValueKind: JsonValueKind.Object } ? ReadString(value.Value, "fontSize") : null;
        var themeMode = value is { ValueKind: JsonValueKind.Object } ? ReadString(value.Value, "themeMode") : null;
        var colors = NormalizeAppearanceColors(value);
        return new
        {
            fontFamily = AppearanceFontFamilies.Contains(fontFamily ?? string.Empty) ? fontFamily!.ToLowerInvariant() : "inter",
            fontSize = AppearanceFontSizes.Contains(fontSize ?? string.Empty) ? fontSize!.ToLowerInvariant() : "normal",
            themeMode = AppearanceThemeModes.Contains(themeMode ?? string.Empty) ? themeMode!.ToLowerInvariant() : "dark",
            colors
        };
    }

    private static bool TryNormalizeAppearance(JsonElement value, out object normalized)
    {
        var fontFamily = ReadString(value, "fontFamily");
        var fontSize = ReadString(value, "fontSize");
        var themeMode = ReadString(value, "themeMode") ?? "dark";
        if (fontFamily is null || fontSize is null || !AppearanceFontFamilies.Contains(fontFamily) || !AppearanceFontSizes.Contains(fontSize) || !AppearanceThemeModes.Contains(themeMode))
        {
            normalized = new { };
            return false;
        }

        if (!TryNormalizeAppearanceColors(value, out var colors))
        {
            normalized = new { };
            return false;
        }

        normalized = new { fontFamily = fontFamily.ToLowerInvariant(), fontSize = fontSize.ToLowerInvariant(), themeMode = themeMode.ToLowerInvariant(), colors };
        return true;
    }

    private static object NormalizeAppearanceColors(JsonElement? value)
    {
        var colors = value is { ValueKind: JsonValueKind.Object } && value.Value.TryGetProperty("colors", out var property) && property.ValueKind == JsonValueKind.Object ? property : (JsonElement?)null;
        return new
        {
            light = NormalizeAppearancePalette(colors, "light", DefaultLightAppearanceColors),
            dark = NormalizeAppearancePalette(colors, "dark", DefaultDarkAppearanceColors)
        };
    }

    private static Dictionary<string, string> NormalizeAppearancePalette(JsonElement? colors, string theme, IReadOnlyDictionary<string, string> defaults)
    {
        var palette = colors is { ValueKind: JsonValueKind.Object } && colors.Value.TryGetProperty(theme, out var property) && property.ValueKind == JsonValueKind.Object ? property : (JsonElement?)null;
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in AppearanceColorTokens)
        {
            var candidate = palette is { ValueKind: JsonValueKind.Object } ? ReadString(palette.Value, token) : null;
            normalized[token] = candidate is not null && IsHexColor(candidate) ? candidate.ToLowerInvariant() : defaults[token];
        }
        return normalized;
    }

    private static bool TryNormalizeAppearanceColors(JsonElement value, out object colors)
    {
        if (!value.TryGetProperty("colors", out var colorsElement))
        {
            colors = NormalizeAppearanceColors(null);
            return true;
        }

        if (colorsElement.ValueKind != JsonValueKind.Object || !TryNormalizeAppearancePalette(colorsElement, "light", DefaultLightAppearanceColors, out var light) || !TryNormalizeAppearancePalette(colorsElement, "dark", DefaultDarkAppearanceColors, out var dark))
        {
            colors = new { };
            return false;
        }

        colors = new { light, dark };
        return true;
    }

    private static bool TryNormalizeAppearancePalette(JsonElement colors, string theme, IReadOnlyDictionary<string, string> defaults, out Dictionary<string, string> normalized)
    {
        normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        var palette = colors.TryGetProperty(theme, out var property) && property.ValueKind == JsonValueKind.Object ? property : (JsonElement?)null;
        foreach (var token in AppearanceColorTokens)
        {
            var candidate = palette is { ValueKind: JsonValueKind.Object } ? ReadString(palette.Value, token) : null;
            if (candidate is not null && !IsHexColor(candidate)) return false;
            normalized[token] = candidate?.ToLowerInvariant() ?? defaults[token];
        }
        return true;
    }

    private static bool IsHexColor(string value)
    {
        return value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
    }

    private static string? ReadString(JsonElement value, string propertyName)
    {
        return value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;
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
