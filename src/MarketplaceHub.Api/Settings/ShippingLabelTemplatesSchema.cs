using System.Text.Json;
using System.Text.Json.Nodes;

namespace MarketplaceHub.Api.Settings;

// The shipping label templates of a tenant (Ayarlar › Kargo Etiketi › Şablonlar), one list for every device of the
// account (owner, 26 Nov 2026; until then each browser kept its own). Stored as { templates: [{ id, name, savedAt,
// settings }] }; each template's settings go through ShippingLabelSettingsSchema like the label settings themselves.
// A list that is not well formed is refused whole (never stored in part): every template needs an id and a name, and
// ids and names are unique, so a merge of several devices cannot leave two templates under one name.
public static class ShippingLabelTemplatesSchema
{
    public const int MaximumTemplates = 50;
    public const int MaximumNameLength = 80;
    public const int MaximumIdLength = 100;
    public const int MaximumJsonCharacters = 1_048_576;

    public static bool TryNormalize(JsonElement value, out JsonObject normalized, out string error)
    {
        normalized = new JsonObject();
        error = string.Empty;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("templates", out var templates) || templates.ValueKind != JsonValueKind.Array)
        {
            error = "Etiket şablonları { templates: [...] } biçiminde olmalıdır.";
            return false;
        }
        if (templates.GetArrayLength() > MaximumTemplates)
        {
            error = $"En çok {MaximumTemplates} etiket şablonu kaydedilebilir.";
            return false;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var list = new JsonArray();
        foreach (var template in templates.EnumerateArray())
        {
            if (template.ValueKind != JsonValueKind.Object) { error = "Her etiket şablonu bir nesne olmalıdır."; return false; }
            var id = Text(template, "id")?.Trim();
            var name = Text(template, "name")?.Trim();
            if (string.IsNullOrEmpty(id) || id.Length > MaximumIdLength) { error = "Her etiket şablonunun geçerli bir kimliği olmalıdır."; return false; }
            if (string.IsNullOrEmpty(name) || name.Length > MaximumNameLength) { error = $"Şablon adı 1 ile {MaximumNameLength} karakter arasında olmalıdır."; return false; }
            if (!template.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object) { error = $"“{name}” şablonunun ayarları eksik."; return false; }
            if (!ids.Add(id)) { error = "Aynı kimlikle iki etiket şablonu kaydedilemez."; return false; }
            if (!names.Add(name)) { error = $"“{name}” adında iki etiket şablonu kaydedilemez."; return false; }
            var savedAt = Text(template, "savedAt") is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                ? at.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                : null;
            list.Add(new JsonObject
            {
                ["id"] = id,
                ["name"] = name,
                ["savedAt"] = savedAt,
                ["settings"] = ShippingLabelSettingsSchema.Normalize(settings)
            });
        }
        normalized["templates"] = list;
        return true;
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
