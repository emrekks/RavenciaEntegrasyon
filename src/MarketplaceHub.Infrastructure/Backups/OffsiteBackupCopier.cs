using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Alerts;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Backups;

public enum OffsiteBackupRunResult { NotConfigured, NotDue, Uploaded, AlreadyPresent, Failed, AuthorizationInvalid, Interrupted }

// One off-site copy: the newest verified backup set, age-encrypted to the owner's public key while it streams, uploaded to
// the owner's Drive folder, then Drive retention. Never uploads a set whose checksums do not match; deletes only the app's
// own "<set>.tar.age" copies (drive.file cannot reach anything else). Outcomes go to the state row the panel shows; an
// invalid Google authorization stops the copy until the owner reconnects. The owner gets one alert e-mail when the copy
// stops (at once for an invalid authorization, after AlertAfterFailures failed runs otherwise) and one when it works again.
public sealed class OffsiteBackupCopier(
    OffsiteBackupSettingsService settingsService,
    IGoogleDriveApi google,
    IBackupEncryptor encryptor,
    IBackupSetSource sets,
    IPanelNotificationService notifications,
    AlertEmailSettingsService alertSettings,
    IAlertEmailSender alertSender,
    TimeProvider timeProvider,
    ILogger<OffsiteBackupCopier> logger)
{
    public static readonly TimeSpan RunTimeout = TimeSpan.FromHours(2);

    public async Task<OffsiteBackupRunResult> RunIfDueAsync(CancellationToken cancellationToken)
    {
        if (await settingsService.HostTenantAsync(cancellationToken) is not { } tenantId) return OffsiteBackupRunResult.NotConfigured;
        var (settings, state) = await settingsService.LoadAsync(tenantId, cancellationToken);
        var (context, _) = settingsService.RunContext(tenantId, settings, state);
        var now = timeProvider.GetUtcNow();
        var next = OffsiteBackupRules.NextRunAt(new OffsiteBackupScheduleState(settings?.Enabled == true, context is not null, state.AuthInvalidAt is not null, state.LastAttemptAt, state.ConsecutiveFailures, state.RequestedAt), now);
        if (context is null || next is null) return OffsiteBackupRunResult.NotConfigured;
        if (next > now) return OffsiteBackupRunResult.NotDue;
        return await RunAsync(context, cancellationToken);
    }

    public async Task<OffsiteBackupRunResult> RunAsync(OffsiteBackupRunContext context, CancellationToken cancellationToken)
    {
        var tenantId = context.TenantId;
        var started = timeProvider.GetUtcNow();
        DateTimeOffset? previousAttempt = null;
        await settingsService.MutateStateAsync(tenantId, state =>
        {
            previousAttempt = state.LastAttemptAt;
            state.LastAttemptAt = started;
            state.RunningSince = started;
            return true;
        }, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RunTimeout);
        try
        {
            var accessToken = await google.RefreshAccessTokenAsync(context.Client, context.RefreshToken, timeout.Token);
            var folderId = await settingsService.ResolveFolderAsync(context, accessToken, timeout.Token);
            var remote = (await google.ListAsync(accessToken, folderId, timeout.Token)).ToList();
            var (set, notes) = await sets.OpenNewestVerifiedAsync(timeout.Token);
            if (set is null)
                throw new GoogleDriveException("BACKUP_SET_MISSING", $"Sunucuda doğrulanmış yedek seti bulunamadı{(notes.Count > 0 ? ": " + string.Join(" ", notes.Take(3)) : ".")}");
            await using (set)
            {
                foreach (var note in notes) logger.LogWarning("Off-site backup skipped a newer set: {Note}", note);
                var objectName = OffsiteBackupRules.ObjectName(set.Name);
                var result = OffsiteBackupRunResult.AlreadyPresent;
                var size = remote.Where(item => item.Name == objectName && item.Size > 0).Select(item => item.Size).FirstOrDefault();
                if (size == 0)
                {
                    var uploaded = await google.UploadAsync(accessToken, folderId, objectName,
                        (output, token) => encryptor.EncryptAsync(context.AgeRecipient, (plain, inner) => set.WriteTarAsync(plain, inner), output, token), timeout.Token);
                    if (uploaded.Size <= 0) throw new GoogleDriveException("DRIVE_UPLOAD_EMPTY", "Drive'a yüklenen kopya boş görünüyor.");
                    size = uploaded.Size;
                    remote.Add(uploaded);
                    result = OffsiteBackupRunResult.Uploaded;
                    logger.LogInformation("Off-site backup {Set} uploaded to Google Drive ({Bytes} bytes, age-encrypted, from {PlainBytes} plain bytes)", set.Name, size, set.PlainBytes);
                }

                // Retention only after this run's copy is safely there.
                var deleted = 0;
                foreach (var name in OffsiteBackupRules.SelectForDeletion(remote.Select(item => item.Name), timeProvider.GetUtcNow(), context.KeepDays, context.KeepMonths))
                {
                    foreach (var item in remote.Where(item => item.Name == name).ToList())
                    {
                        await google.DeleteAsync(accessToken, item.Id, timeout.Token);
                        remote.Remove(item);
                        deleted++;
                    }
                }
                if (deleted > 0) logger.LogInformation("Off-site backup retention removed {Count} old copies from Google Drive", deleted);

                var copies = remote.Count(item => OffsiteBackupRules.TryParseObjectName(item.Name, out _, out _));
                await SucceededAsync(context, set.Name, size, copies);
                return result;
            }
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is OperationCanceledException)
        {
            // The worker is stopping (a deploy): the run did not happen, so it is due again at the next start.
            await settingsService.MutateStateAsync(tenantId, state =>
            {
                state.LastAttemptAt = previousAttempt;
                state.RunningSince = null;
                return true;
            }, CancellationToken.None);
            return OffsiteBackupRunResult.Interrupted;
        }
        catch (GoogleDriveException exception) when (exception.Authorization)
        {
            var first = await settingsService.MarkAuthorizationInvalidAsync(tenantId, context.ConnectionId, exception.Message, CancellationToken.None);
            await FailedAsync(context, exception.Message, alertNow: first);
            return OffsiteBackupRunResult.AuthorizationInvalid;
        }
        catch (Exception exception) when (exception is GoogleDriveException or HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException)
        {
            var message = exception switch
            {
                GoogleDriveException drive => drive.Message,
                OperationCanceledException => "Gönderim iki saatte bitmedi ve durduruldu.",
                HttpRequestException => "Google'a ulaşılamadı.",
                _ => $"Gönderim tamamlanamadı: {exception.Message}"
            };
            logger.LogWarning(exception, "Off-site backup copy failed: {Message}", message);
            await FailedAsync(context, message, alertNow: false);
            return OffsiteBackupRunResult.Failed;
        }
    }

    private async Task SucceededAsync(OffsiteBackupRunContext context, string setName, long size, int copies)
    {
        var now = timeProvider.GetUtcNow();
        DateTimeOffset? alertSent = null;
        await settingsService.MutateStateAsync(context.TenantId, state =>
        {
            alertSent = state.AlertSentAt;
            state.LastSuccessAt = now;
            state.LastSet = setName;
            state.LastSize = size;
            state.LastError = null;
            state.LastErrorAt = null;
            state.ConsecutiveFailures = 0;
            state.FailingSince = null;
            state.RunningSince = null;
            state.AlertSentAt = null;
            state.RemoteCopies = copies;
            return true;
        }, CancellationToken.None);
        if (alertSent is null) return;
        const string message = "Google Drive yedeği yeniden çalışıyor: en yeni yedek seti Drive'a gönderildi.";
        await NotifyAsync(context.TenantId, $"offsite-backup:recovered:{now:yyyyMMddHHmmss}", PanelNotificationSeverities.Success, "Google Drive Yedeği Düzeldi", $"{message} Set {setName}.");
        await EmailAsync(context.TenantId, $"Ravencia: {message} (set {setName})");
    }

    private async Task FailedAsync(OffsiteBackupRunContext context, string message, bool alertNow)
    {
        var now = timeProvider.GetUtcNow();
        var sendAlert = false;
        DateTimeOffset failingSince = now;
        await settingsService.MutateStateAsync(context.TenantId, state =>
        {
            state.LastError = message.Length <= 500 ? message : message[..500];
            state.LastErrorAt = now;
            state.ConsecutiveFailures++;
            state.FailingSince ??= now;
            state.RunningSince = null;
            failingSince = state.FailingSince.Value;
            if (state.AlertSentAt is null && (alertNow || state.ConsecutiveFailures >= OffsiteBackupRules.AlertAfterFailures))
            {
                state.AlertSentAt = now;
                sendAlert = true;
            }
            return true;
        }, CancellationToken.None);
        if (!sendAlert) return;
        // The same key the bell gives the background notice (AttentionNoticeService), so the center keeps one row.
        await NotifyAsync(context.TenantId, AttentionNoticePolicy.OffsiteBackupClientKey(failingSince), PanelNotificationSeverities.Error, AttentionNoticePolicy.OffsiteBackupTitle, $"Google Drive yedeği gönderilemiyor: {message}");
        await EmailAsync(context.TenantId, $"Ravencia: Google Drive yedeği durdu — {message} Sistem Ayarları › Google Drive Yedeği.");
    }

    private async Task NotifyAsync(Guid tenantId, string key, string severity, string title, string message)
    {
        try
        {
            await notifications.SaveAsync(tenantId, Guid.Empty, [new PanelNotificationInput(key, severity, PanelNotificationCategories.Attention, title, message,
                Href: AttentionNoticePolicy.OffsiteBackupHref, ActionLabel: "Ayarlara Git", Shared: true)], CancellationToken.None);
        }
        catch (Exception exception) when (exception is DbUpdateException or NpgsqlException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Off-site backup notification could not be saved");
        }
    }

    private async Task EmailAsync(Guid tenantId, string message)
    {
        try
        {
            if (await alertSettings.LoadCredentialsAsync(tenantId, CancellationToken.None) is not { } credentials)
            {
                logger.LogWarning("Off-site backup alert not e-mailed: Alarm Bildirimleri has no sender saved");
                return;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await alertSender.SendAsync(credentials, message, timeout.Token);
        }
        catch (Exception exception) when (exception is AlertEmailException or IOException or OperationCanceledException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException or System.Security.Cryptography.CryptographicException)
        {
            logger.LogWarning(exception, "Off-site backup alert e-mail could not be sent");
        }
    }
}

// The worker's loop: once a minute it asks whether a copy is due (the 09:30 / 21:30 UTC slots, a retry, or Şimdi Gönder)
// and runs it under a PostgreSQL advisory lock, so two workers (an overlapping deploy) never copy at once.
public sealed class OffsiteBackupWorkerService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<OffsiteBackupWorkerService> logger) : BackgroundService
{
    // pg_try_advisory_lock key: "OFFSITE1" as ASCII.
    private const long LockKey = 0x4F46465349544531;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("OffsiteBackup:Enabled", true)) return;
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("OffsiteBackup:StartDelaySeconds", 60), 0, 3600)), stoppingToken); }
        catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is DbUpdateException or NpgsqlException or InvalidOperationException or TimeoutException)
            {
                logger.LogWarning(exception, "Off-site backup check failed; it is tried again in a minute");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(stoppingToken);
        await using (var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection))
        {
            command.Parameters.AddWithValue("key", LockKey);
            if (await command.ExecuteScalarAsync(stoppingToken) is not true) return;
        }
        // Closing the connection releases the lock.
        var result = await scope.ServiceProvider.GetRequiredService<OffsiteBackupCopier>().RunIfDueAsync(stoppingToken);
        if (result is not (OffsiteBackupRunResult.NotDue or OffsiteBackupRunResult.NotConfigured))
            logger.LogInformation("Off-site backup run finished: {Result}", result);
    }
}
