using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    internal static bool IsConnectionAuthenticationFailure(string jobType, JobExecutionResult result) =>
        !IsConnectionTestJob(jobType)
        && result.Kind == JobCompletionKind.Blocked
        && result.BackOffConnection
        && !string.IsNullOrWhiteSpace(result.ErrorCode);

    // Refused credentials during a sync show on the connection, as a failed
    // connection test does; the next successful test clears the code.
    // The code is written in place without moving the connection's version: a background refusal is not an
    // edit, and bumping the version made a user saving the connection's settings at that moment get a 412.
    private async Task RecordConnectionAuthenticationFailureAsync(Guid tenantId, Guid connectionId, string jobType, JobExecutionResult result, CancellationToken cancellationToken)
    {
        if (!IsConnectionAuthenticationFailure(jobType, result)) return;
        var errorCode = result.ErrorCode;
        await db.PlatformConnections
            .Where(x => x.TenantId == tenantId && x.Id == connectionId && (x.LastErrorCode == null || x.LastErrorCode != errorCode))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastErrorCode, errorCode).SetProperty(x => x.LastErrorClass, nameof(AdapterErrorClass.Authentication)), cancellationToken);
    }

    private async Task RecordSyncAttempt(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, resourceType, cancellationToken);
        cursor.LastAttemptAt = timeProvider.GetUtcNow();
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    // On the failure path the telemetry write must not replace the job's own failure: when the database
    // connection itself broke, writing the cursor fails too and that second error would hide the first.
    private async Task TryRecordFailedSyncCompletion(Guid tenantId, Guid connectionId, string resourceType, TimeSpan duration, string? error, CancellationToken cancellationToken)
    {
        try
        {
            await RecordSyncCompletion(tenantId, connectionId, resourceType, duration, false, error, cancellationToken);
        }
        catch (Exception)
        {
            // Telemetry only; the original failure is what the job reports.
            db.ChangeTracker.Clear();
        }
    }

    // A finished slice of a long run: its duration and counts are shown, but the last success, the last error
    // and the failure count wait for the run's end, so a run that keeps slicing is not shown as healthy.
    private async Task RecordSyncSlice(Guid tenantId, Guid connectionId, string resourceType, TimeSpan duration, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, resourceType, cancellationToken);
        ApplySliceTelemetry(cursor, timeProvider.GetUtcNow(), duration);
        var counts = CurrentCursorCounts();
        cursor.LastRequestCount = telemetryRequestCount;
        cursor.LastReceivedCount = counts.Received;
        cursor.LastChangedCount = counts.Changed;
        cursor.LastInsertedCount = telemetryInsertedCount;
        cursor.LastUpdatedCount = telemetryUpdatedCount;
        cursor.LastSkippedCount = counts.Skipped;
        cursor.LastRetryCount = telemetryRetryCount;
        cursor.LastRateLimitCount = telemetryRateLimitCount;
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static void ApplySliceTelemetry(SyncCursor cursor, DateTimeOffset now, TimeSpan duration)
    {
        cursor.LastSliceAt = now;
        cursor.LastDurationMs = Math.Max(0, (long)duration.TotalMilliseconds);
    }

    private async Task RecordSyncCompletion(Guid tenantId, Guid connectionId, string resourceType, TimeSpan duration, bool succeeded, string? error, CancellationToken cancellationToken)
    {
        if (!succeeded) db.ChangeTracker.Clear();
        var cursor = await Cursor(tenantId, connectionId, resourceType, cancellationToken);
        var counts = CurrentCursorCounts();
        cursor.LastDurationMs = Math.Max(0, (long)duration.TotalMilliseconds);
        if (succeeded)
        {
            cursor.LastSuccessAt = timeProvider.GetUtcNow();
            cursor.LastError = null;
            cursor.LastErrorAt = null;
            cursor.ConsecutiveFailureCount = 0;
            cursor.LastFailedCount = counts.Failed;
        }
        else
        {
            cursor.LastError = Short(error, 1024);
            cursor.LastErrorAt = timeProvider.GetUtcNow();
            cursor.ConsecutiveFailureCount++;
            cursor.LastFailedCount = Math.Max(1, counts.Failed);
        }
        cursor.LastRequestCount = telemetryRequestCount;
        cursor.LastReceivedCount = counts.Received;
        cursor.LastChangedCount = counts.Changed;
        cursor.LastInsertedCount = telemetryInsertedCount;
        cursor.LastUpdatedCount = telemetryUpdatedCount;
        cursor.LastSkippedCount = counts.Skipped;
        cursor.LastRetryCount = telemetryRetryCount + (succeeded ? 0 : 1);
        cursor.LastRateLimitCount = telemetryRateLimitCount;
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    private void ResetTelemetry()
    {
        readRecorder.Reset();
        telemetryRequestCount = 0;
        telemetryReceivedCount = 0;
        telemetryChangedCount = 0;
        telemetryInsertedCount = 0;
        telemetryUpdatedCount = 0;
        telemetrySkippedCount = 0;
        telemetryFailedCount = 0;
        telemetryRetryCount = 0;
        telemetryRateLimitCount = 0;
        telemetryImportProcessedCount = 0;
        telemetryImportSkippedCount = 0;
        telemetryImportFailedCount = 0;
        productImportTelemetry = false;
        telemetryImportReceivedCount = 0;
        telemetryUnaddedSizeCount = 0;
        productUnaddedSizeCount = 0;
        importSummaryMode = ImportSummaryMode.Standard;
        priceChangedVariantIds.Clear();
        stockChangedVariantIds.Clear();
        telemetryUnchangedVariantCount = 0;
        telemetryNewProductCount = 0;
        telemetryNewSizeCount = 0;
        telemetryAlreadyInPanelCount = 0;
        productImportedNewVariantCount = 0;
        telemetryMappedCount = 0;
        telemetryAlreadyMappedCount = 0;
        telemetryNoCounterpartCount = 0;
        telemetryMappingConflictCount = 0;
    }

    internal readonly record struct CursorCounts(int Received, int Changed, int Skipped, int Failed);

    // The counts a run leaves on its cursor. A product import counts what the import did over the whole run (the job's
    // own Alınan / İşlenen / Atlanan / Hatalı); any other read counts its records as it read them.
    internal static CursorCounts CursorImportCounts(bool productImport, CursorCounts run, CursorCounts import) =>
        productImport ? import : run;

    private CursorCounts CurrentCursorCounts() => CursorImportCounts(
        productImportTelemetry,
        new(telemetryReceivedCount, telemetryChangedCount, telemetrySkippedCount, telemetryFailedCount),
        new(telemetryImportReceivedCount, telemetryImportProcessedCount, telemetryImportSkippedCount, telemetryImportFailedCount));

    // Order and return reads write what they brought on the job (received, new, updated), so the list tells a read that
    // came back empty from one that brought records. The counts are the run's own telemetry.
    internal static bool RecordsReadProgress(string jobType) => jobType is
        MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.PazaramaOrderSync or MarketplaceJobTypes.PttAvmOrderSync
        or MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.N11OrderRecoverySync or MarketplaceJobTypes.PazaramaOrderRecoverySync or MarketplaceJobTypes.PttAvmOrderRecoverySync
        or MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.HepsiburadaOrderStatusSync or MarketplaceJobTypes.PazaramaOrderStatusSync or MarketplaceJobTypes.PttAvmOrderStatusSync
        or MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.HepsiburadaReturnSync or MarketplaceJobTypes.ReturnStatusSync or MarketplaceJobTypes.ReturnReconciliation;

    internal static string ReadProgressLabel(int received, int inserted, int updated) =>
        received == 0 && inserted == 0 && updated == 0
            ? "Pazaryerinden yeni kayıt gelmedi"
            : $"{received} kayıt alındı · {inserted} yeni · {updated} güncellendi";

    internal static string ReadRecordLabel(JobReadCounts counts) =>
        counts.Received == 0
            ? "Pazaryerinden yeni kayıt gelmedi"
            : $"{counts.Received} kayıt okundu · {counts.Inserted} yeni · {counts.Updated} güncellendi · {counts.Unchanged} değişmedi" + (counts.Skipped > 0 ? $" · {counts.Skipped} atlandı" : "") + (counts.Failed > 0 ? $" · {counts.Failed} hatalı" : "");

    private async Task RecordReadProgressAsync(Guid tenantId, Guid? jobId, string jobType, CancellationToken cancellationToken)
    {
        if (jobId is not { } id || !RecordsReadProgress(jobType)) return;
        var received = telemetryReceivedCount;
        var processed = telemetryInsertedCount + telemetryUpdatedCount;
        var skipped = telemetrySkippedCount;
        var failed = (int?)null;
        var label = ReadProgressLabel(received, telemetryInsertedCount, telemetryUpdatedCount);
        // What the run recorded record by record wins: each record counts once, so Okunan = Yeni + Güncellenen +
        // Değişmeyen + Atlanan + Hatalı (an unchanged record counts with the skipped ones instead of vanishing).
        if (await FlushReadRecordsAsync(tenantId, id, cancellationToken, final: true) is { } counts)
        {
            received = counts.Received;
            processed = counts.Inserted + counts.Updated;
            skipped = counts.Unchanged + counts.Skipped;
            failed = counts.Failed;
            label = ReadRecordLabel(counts);
        }
        try
        {
            await db.IntegrationJobs
                .Where(job => job.TenantId == tenantId && job.Id == id && job.JobType == jobType)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.ProgressReceived, received)
                    .SetProperty(job => job.ProgressCurrent, received)
                    .SetProperty(job => job.ProgressProcessed, processed)
                    .SetProperty(job => job.ProgressSkipped, skipped)
                    .SetProperty(job => job.ProgressFailed, job => failed ?? job.ProgressFailed)
                    .SetProperty(job => job.ProgressLabel, label), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Display only: a failed progress write never fails the read it describes.
        }
    }

    private void TrackRequest() => telemetryRequestCount++;
    private void TrackReceived(bool changed = true)
    {
        telemetryReceivedCount++;
        if (changed) telemetryChangedCount++;
        else telemetrySkippedCount++;
    }
    private void TrackSkipped() => telemetrySkippedCount++;
    private void TrackResultFailure(AdapterError? error)
    {
        telemetryFailedCount++;
        if (error?.Class == AdapterErrorClass.RateLimit) telemetryRateLimitCount++;
        if (error?.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.RateLimit or AdapterErrorClass.Remote5xx) telemetryRetryCount++;
    }
}
