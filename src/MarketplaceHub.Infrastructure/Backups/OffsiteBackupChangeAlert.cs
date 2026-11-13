using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Alerts;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Backups;

// Where the off-site copy goes decides who can read a copy of the whole database: when the age key or the Google account
// changes, the owner hears of it at once, by the Alarm Bildirimleri e-mail and in the notification center (shared), so a
// change nobody expected does not go unnoticed. Best effort: a failure here never undoes or refuses the change.
public sealed class OffsiteBackupChangeAlert(IPanelNotificationService notifications, AlertEmailSettingsService alertSettings, IAlertEmailSender alertSender, TimeProvider timeProvider, ILogger<OffsiteBackupChangeAlert> logger)
{
    public const string Title = "Google Drive Yedeğinin Hedefi Değişti";

    // The sentence to send, or null when nothing that decides the destination changed.
    public static string? Describe(OffsiteBackupView? before, OffsiteBackupView after, string actor)
    {
        var changes = new List<string>();
        if (!string.Equals(before?.AgeRecipient, after.AgeRecipient, StringComparison.Ordinal))
            changes.Add(after.AgeRecipient is null ? "age açık anahtarı kaldırıldı" : $"age açık anahtarı {Short(after.AgeRecipient)} oldu");
        var accountChanged = after.Connected && (!string.Equals(before?.Account, after.Account, StringComparison.OrdinalIgnoreCase) || before?.ConnectedAt != after.ConnectedAt);
        if (accountChanged) changes.Add($"Google hesabı {(after.Account is { Length: > 0 } account ? account : "(adı bilinmiyor)")} olarak bağlandı");
        return changes.Count == 0 ? null : $"Google Drive yedeği: {string.Join("; ", changes)} ({actor}). Bu değişikliği siz yapmadıysanız Sistem Ayarları › Google Drive Yedeği'ni hemen denetleyin.";
    }

    public async Task SendAsync(Guid tenantId, string message, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        try
        {
            await notifications.SaveAsync(tenantId, Guid.Empty, [new PanelNotificationInput($"offsite-backup:destination:{now:yyyyMMddHHmmssfff}", PanelNotificationSeverities.Info, "ACCOUNT", Title, message,
                Href: AttentionNoticePolicy.OffsiteBackupHref, ActionLabel: "Ayarlara Git", Shared: true)], cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Off-site backup destination change notice could not be saved");
        }
        try
        {
            if (await alertSettings.LoadCredentialsAsync(tenantId, cancellationToken) is not { } credentials)
            {
                logger.LogWarning("Off-site backup destination change not e-mailed: Alarm Bildirimleri has no sender saved");
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await alertSender.SendAsync(credentials, $"Ravencia: {message}", timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Off-site backup destination change e-mail could not be sent");
        }
    }

    private static string Short(string value) => value.Length > 16 ? $"{value[..10]}…{value[^6..]}" : value;
}
