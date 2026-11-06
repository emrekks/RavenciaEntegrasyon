using System.Text.Json;
using System.Text.Json.Serialization;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Ai;

public sealed record AiUploadedSkillView(string SkillId, string Version, DateTimeOffset UploadedAt, string BundleHash);
public sealed record AiSkillView(string Key, string Title, string Description, string BundleHash, AiUploadedSkillView? Uploaded, bool UpToDate);
// The API key never leaves the server: the view says only whether one is saved.
public sealed record AiSettingsView(bool ApiKeySaved, string DefaultModel, DateTimeOffset? UpdatedAt, long Version, IReadOnlyList<AiSkillView> Skills);
public sealed record AiSettingsSaveResult(AiSettingsView? Settings, string? Error);

// What a Claude call needs: the decrypted key, the model, and the uploaded skills by key.
public sealed record AiCredentials(string ApiKey, string DefaultModel, IReadOnlyDictionary<string, AiUploadedSkillView> Skills);

// Ayarlar › Yapay Zekâ, stored per tenant in tenant_settings under "ai": the Claude API key (encrypted with Data
// Protection, bound to the tenant, like the alert e-mail password), the default model, and the skills uploaded with
// "Beceriyi Yükle". Shared by every AI feature; only the owner and administrators reach it (RoleAuthorizationMiddleware).
public sealed class AiSettingsService(AppDbContext db, IDataProtectionProvider dataProtection, TimeProvider timeProvider)
{
    public const string SettingKey = "ai";
    public const string DefaultModel = "claude-sonnet-5-5";
    private const string ProtectionPurpose = "MarketplaceHub.AiSettings.v1";
    private readonly IDataProtector protector = dataProtection.CreateProtector(ProtectionPurpose);

    private sealed class Stored
    {
        [JsonPropertyName("apiKeyProtected")] public string? ApiKeyProtected { get; set; }
        [JsonPropertyName("defaultModel")] public string? DefaultModel { get; set; }
        [JsonPropertyName("skills")] public Dictionary<string, StoredSkill>? Skills { get; set; }
    }

    private sealed class StoredSkill
    {
        [JsonPropertyName("skillId")] public string SkillId { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("uploadedAt")] public DateTimeOffset UploadedAt { get; set; }
        [JsonPropertyName("bundleHash")] public string BundleHash { get; set; } = "";
    }

    public async Task<AiSettingsView> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        View(await db.TenantSettings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Key == SettingKey, cancellationToken));

    // apiKey: null or blank keeps the saved key; a new value replaces it.
    public async Task<AiSettingsSaveResult> SaveAsync(Guid tenantId, Guid userId, string correlationId, string? apiKey, string? defaultModel, long? expectedVersion, CancellationToken cancellationToken)
    {
        var model = NormalizeModel(defaultModel);
        if (model is null) return new(null, "Model adı harf, rakam, nokta ve tireden oluşmalı (örnek: claude-sonnet-5-5).");
        var key = apiKey?.Trim() ?? "";
        if (key.Length > 0 && !IsApiKeyShaped(key)) return new(null, "Claude API anahtarı \"sk-ant-\" ile başlar ve boşluk içermez.");

        var setting = await db.TenantSettings.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Key == SettingKey, cancellationToken);
        if (expectedVersion is { } version && (setting?.Version ?? 0) != version)
            throw new DbUpdateConcurrencyException("The AI settings changed meanwhile.");
        var stored = Read(setting) ?? new Stored();
        var changes = new List<string>();
        if (key.Length > 0)
        {
            var previousKey = SavedKey(tenantId, stored);
            changes.Add(stored.ApiKeyProtected is null ? "API anahtarı kaydedildi" : previousKey == key ? "API anahtarı aynı" : "API anahtarı değişti");
            // O9: uploaded skills live in the old key's workspace; another key cannot use them, so they are forgotten
            // and show "Yüklenmedi" until "Beceriyi Yükle" uploads them with the new key.
            if (previousKey != key && stored.Skills is { Count: > 0 })
            {
                changes.Add($"yüklü beceriler ({string.Join(", ", stored.Skills.Keys.Order(StringComparer.Ordinal))}) yeni anahtar için yeniden yüklenmeli");
                stored.Skills = null;
            }
            stored.ApiKeyProtected = BoundSecret.Protect(protector, TenantScope(tenantId), key);
        }
        if (!string.Equals(stored.DefaultModel ?? DefaultModel, model, StringComparison.Ordinal)) changes.Add($"varsayılan model {model}");
        stored.DefaultModel = model;
        Write(tenantId, ref setting, stored);
        Audit(tenantId, userId, correlationId, "AI_SETTINGS_CHANGED", changes.Count == 0 ? "Yapay zekâ ayarları değişmeden kaydedildi." : $"Yapay zekâ: {string.Join("; ", changes)}.");
        await db.SaveChangesAsync(cancellationToken);
        return new(View(setting), null);
    }

    public async Task<AiCredentials?> LoadCredentialsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var stored = Read(await db.TenantSettings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Key == SettingKey, cancellationToken));
        if (string.IsNullOrEmpty(stored?.ApiKeyProtected)) return null;
        string apiKey;
        try { apiKey = BoundSecret.Unprotect(protector, TenantScope(tenantId), stored.ApiKeyProtected); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
        return new(apiKey, stored.DefaultModel ?? DefaultModel, (stored.Skills ?? []).ToDictionary(pair => pair.Key, pair => UploadedView(pair.Value), StringComparer.Ordinal));
    }

    public async Task<AiSettingsView> RecordSkillAsync(Guid tenantId, Guid userId, string correlationId, AiSkillDefinition skill, ClaudeSkillUpload upload, CancellationToken cancellationToken)
    {
        var setting = await db.TenantSettings.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Key == SettingKey, cancellationToken);
        var stored = Read(setting) ?? new Stored();
        stored.Skills ??= [];
        stored.Skills[skill.Key] = new StoredSkill { SkillId = upload.SkillId, Version = upload.Version, UploadedAt = timeProvider.GetUtcNow(), BundleHash = skill.BundleHash };
        Write(tenantId, ref setting, stored);
        Audit(tenantId, userId, correlationId, "AI_SKILL_UPLOADED", $"Yapay zekâ becerisi yüklendi: {skill.Key} ({upload.SkillId}, sürüm {upload.Version}).");
        await db.SaveChangesAsync(cancellationToken);
        return View(setting);
    }

    internal static bool IsApiKeyShaped(string key) => key.StartsWith("sk-ant-", StringComparison.Ordinal) && key.Length is >= 20 and <= 400 && key.All(c => c > ' ' && c < 127);

    internal static string? NormalizeModel(string? value)
    {
        var model = string.IsNullOrWhiteSpace(value) ? DefaultModel : value.Trim();
        return model.Length is >= 3 and <= 80 && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_') ? model : null;
    }

    private static string TenantScope(Guid tenantId) => $"tenant:{tenantId:N}/ai";

    // The saved key, null when none is saved or it cannot be read (then any new key counts as another one).
    private string? SavedKey(Guid tenantId, Stored stored)
    {
        if (string.IsNullOrEmpty(stored.ApiKeyProtected)) return null;
        try { return BoundSecret.Unprotect(protector, TenantScope(tenantId), stored.ApiKeyProtected); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    private void Write(Guid tenantId, ref TenantSetting? setting, Stored stored)
    {
        var json = JsonSerializer.Serialize(stored);
        var now = timeProvider.GetUtcNow();
        if (setting is null)
        {
            setting = new TenantSetting { TenantId = tenantId, Key = SettingKey, ValueJson = json, UpdatedAt = now, Version = 1 };
            db.TenantSettings.Add(setting);
        }
        else
        {
            setting.ValueJson = json;
            setting.UpdatedAt = now;
            setting.Version++;
        }
    }

    private void Audit(Guid tenantId, Guid userId, string correlationId, string action, string reason) => db.AuditLogs.Add(new AuditLog
    {
        TenantId = tenantId,
        ActorUserId = userId,
        Action = action,
        TargetType = "TenantSetting",
        TargetId = SettingKey,
        Reason = reason.Length <= 512 ? reason : reason[..512],
        CorrelationId = correlationId,
        CreatedAt = timeProvider.GetUtcNow()
    });

    private static AiUploadedSkillView UploadedView(StoredSkill skill) => new(skill.SkillId, skill.Version, skill.UploadedAt, skill.BundleHash);

    private static AiSettingsView View(TenantSetting? setting)
    {
        var stored = Read(setting);
        var skills = AiSkillCatalog.All.Select(skill =>
        {
            var uploaded = stored?.Skills?.GetValueOrDefault(skill.Key) is { } saved ? UploadedView(saved) : null;
            return new AiSkillView(skill.Key, skill.Title, skill.Description, skill.BundleHash, uploaded, uploaded is not null && uploaded.BundleHash == skill.BundleHash);
        }).ToList();
        return new AiSettingsView(!string.IsNullOrEmpty(stored?.ApiKeyProtected), stored?.DefaultModel ?? DefaultModel, setting?.UpdatedAt, setting?.Version ?? 0, skills);
    }

    private static Stored? Read(TenantSetting? setting)
    {
        if (string.IsNullOrWhiteSpace(setting?.ValueJson)) return null;
        try { return JsonSerializer.Deserialize<Stored>(setting.ValueJson); }
        catch (JsonException) { return null; }
    }
}
