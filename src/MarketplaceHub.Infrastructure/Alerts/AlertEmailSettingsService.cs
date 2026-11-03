using System.Text.Json;
using System.Text.Json.Serialization;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Alerts;

public sealed record AlertEmailSettingsView(string? SenderAddress, IReadOnlyList<string> Recipients, bool AppPasswordSaved, DateTimeOffset? UpdatedAt, long Version);

// A save's outcome: the new view, or a message for the person (400) when the values are not usable.
public sealed record AlertEmailSaveResult(AlertEmailSettingsView? Settings, string? Error);

// Stored per tenant in tenant_settings under "alert-email" (the panel's other settings live there too). The host
// reads the settings of the oldest active tenant that has them, i.e. the owner's: the server serves one company.
// The app password is encrypted with the Data Protection purpose below and never leaves the server except to the
// host's root-only sync (AlertHostSync in the API), which needs it to send alerts while the API is down.
public sealed class AlertEmailSettingsService(AppDbContext db, IDataProtectionProvider dataProtection, TimeProvider timeProvider)
{
    public const string SettingKey = "alert-email";
    private const string ProtectionPurpose = "MarketplaceHub.AlertEmail.v1";
    private readonly IDataProtector protector = dataProtection.CreateProtector(ProtectionPurpose);

    private sealed class Stored
    {
        [JsonPropertyName("senderAddress")] public string? SenderAddress { get; set; }
        [JsonPropertyName("recipients")] public List<string>? Recipients { get; set; }
        [JsonPropertyName("appPasswordProtected")] public string? AppPasswordProtected { get; set; }
    }

    public async Task<AlertEmailSettingsView> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var setting = await db.TenantSettings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Key == SettingKey, cancellationToken);
        return View(setting);
    }

    // appPassword: null or blank keeps the saved one ("Kayıtlı"); a new value replaces it.
    public async Task<AlertEmailSaveResult> SaveAsync(Guid tenantId, Guid userId, string correlationId, string? senderAddress, IEnumerable<string?>? recipients, string? appPassword, long? expectedVersion, CancellationToken cancellationToken)
    {
        var sender = AlertEmailRules.NormalizeAddress(senderAddress);
        var recipientList = AlertEmailRules.NormalizeRecipients(recipients);
        if (AlertEmailRules.Validate(sender, recipientList) is { } invalid) return new(null, invalid);

        var setting = await db.TenantSettings.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Key == SettingKey, cancellationToken);
        if (expectedVersion is { } version && (setting?.Version ?? 0) != version)
            throw new DbUpdateConcurrencyException("The alert e-mail settings changed meanwhile.");
        var previous = Read(setting);
        var password = AlertEmailRules.NormalizeAppPassword(appPassword);
        string? protectedPassword;
        if (password.Length == 0)
        {
            protectedPassword = previous?.AppPasswordProtected;
            if (string.IsNullOrEmpty(protectedPassword)) return new(null, "Gmail uygulama şifresini girin.");
        }
        else
        {
            if (!AlertEmailRules.IsAppPassword(password)) return new(null, "Uygulama şifresi 16 harften oluşur (Google dört grup halinde gösterir; boşluklar önemli değil).");
            protectedPassword = protector.Protect(password);
        }

        var changes = new List<string>();
        if (!string.Equals(previous?.SenderAddress, sender, StringComparison.Ordinal)) changes.Add($"gönderen {sender}");
        if (!(previous?.Recipients ?? []).SequenceEqual(recipientList, StringComparer.Ordinal)) changes.Add($"alıcılar {string.Join(", ", recipientList)}");
        if (password.Length > 0) changes.Add(previous?.AppPasswordProtected is null ? "uygulama şifresi kaydedildi" : "uygulama şifresi değişti");
        var valueJson = JsonSerializer.Serialize(new Stored { SenderAddress = sender, Recipients = recipientList, AppPasswordProtected = protectedPassword });
        var now = timeProvider.GetUtcNow();
        if (setting is null)
        {
            setting = new TenantSetting { TenantId = tenantId, Key = SettingKey, ValueJson = valueJson, UpdatedAt = now, Version = 1 };
            db.TenantSettings.Add(setting);
        }
        else
        {
            setting.ValueJson = valueJson;
            setting.UpdatedAt = now;
            setting.Version++;
        }
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = userId,
            Action = "ALERT_EMAIL_SETTINGS_CHANGED",
            TargetType = "TenantSetting",
            TargetId = SettingKey,
            Reason = Truncate(changes.Count == 0 ? "Alarm e-postası ayarları değişmeden kaydedildi." : $"Alarm e-postası: {string.Join("; ", changes)}."),
            CorrelationId = correlationId,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return new(View(setting), null);
    }

    // The settings with the password decrypted, for sending; null when none are saved.
    public async Task<AlertEmailCredentials?> LoadCredentialsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var setting = await db.TenantSettings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Key == SettingKey, cancellationToken);
        return Credentials(Read(setting));
    }

    // The host's view: the owner's (oldest active tenant's) settings.
    public async Task<AlertEmailCredentials?> LoadForHostAsync(CancellationToken cancellationToken)
    {
        var setting = await HostSettingQuery().FirstOrDefaultAsync(cancellationToken);
        return Credentials(Read(setting));
    }

    // First deploy after this feature: the host file the owner wrote becomes the panel's settings, when the panel has
    // none yet. Returns false when settings already exist (the panel stays the source of truth) or there is no tenant.
    public async Task<bool> ImportFromHostAsync(AlertEmailCredentials credentials, string correlationId, CancellationToken cancellationToken)
    {
        if (await HostSettingQuery().AnyAsync(cancellationToken)) return false;
        var tenantId = await db.Tenants.AsNoTracking().Where(x => x.Status == RecordStatus.Active).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
        if (tenantId is null) return false;
        if (!AlertEmailRules.IsAppPassword(credentials.AppPassword) || AlertEmailRules.Validate(credentials.SenderAddress, credentials.Recipients) is not null) return false;
        var now = timeProvider.GetUtcNow();
        db.TenantSettings.Add(new TenantSetting
        {
            TenantId = tenantId.Value,
            Key = SettingKey,
            ValueJson = JsonSerializer.Serialize(new Stored { SenderAddress = credentials.SenderAddress, Recipients = credentials.Recipients.ToList(), AppPasswordProtected = protector.Protect(credentials.AppPassword) }),
            UpdatedAt = now,
            Version = 1
        });
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId.Value,
            Action = "ALERT_EMAIL_SETTINGS_IMPORTED",
            TargetType = "TenantSetting",
            TargetId = SettingKey,
            Reason = Truncate($"Alarm e-postası sunucudaki alert_smtp.txt dosyasından alındı: gönderen {credentials.SenderAddress}, alıcılar {string.Join(", ", credentials.Recipients)}."),
            CorrelationId = correlationId,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task RecordTestAsync(Guid tenantId, Guid userId, string correlationId, string? failure, CancellationToken cancellationToken)
    {
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = userId,
            Action = failure is null ? "ALERT_EMAIL_TEST_SENT" : "ALERT_EMAIL_TEST_FAILED",
            TargetType = "TenantSetting",
            TargetId = SettingKey,
            Reason = Truncate(failure is null ? "Alarm test e-postası gönderildi." : $"Alarm test e-postası gönderilemedi: {failure}"),
            CorrelationId = correlationId,
            CreatedAt = timeProvider.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private IQueryable<TenantSetting> HostSettingQuery() =>
        from setting in db.TenantSettings.AsNoTracking()
        join tenant in db.Tenants.AsNoTracking() on setting.TenantId equals tenant.Id
        where setting.Key == SettingKey && tenant.Status == RecordStatus.Active
        orderby tenant.CreatedAt, tenant.Id
        select setting;

    private AlertEmailCredentials? Credentials(Stored? stored)
    {
        if (stored?.SenderAddress is not { Length: > 0 } sender || stored.Recipients is not { Count: > 0 } recipients || string.IsNullOrEmpty(stored.AppPasswordProtected)) return null;
        return new AlertEmailCredentials(sender, recipients, protector.Unprotect(stored.AppPasswordProtected));
    }

    private static AlertEmailSettingsView View(TenantSetting? setting)
    {
        var stored = Read(setting);
        return new AlertEmailSettingsView(stored?.SenderAddress, stored?.Recipients ?? [], !string.IsNullOrEmpty(stored?.AppPasswordProtected), setting?.UpdatedAt, setting?.Version ?? 0);
    }

    private static Stored? Read(TenantSetting? setting)
    {
        if (string.IsNullOrWhiteSpace(setting?.ValueJson)) return null;
        try { return JsonSerializer.Deserialize<Stored>(setting.ValueJson); }
        catch (JsonException) { return null; }
    }

    private static string Truncate(string value) => value.Length <= 512 ? value : value[..512];
}
