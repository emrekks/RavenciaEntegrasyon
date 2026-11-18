using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

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
        cursor.LastRequestCount = run.RequestCount;
        cursor.LastReceivedCount = counts.Received;
        cursor.LastChangedCount = counts.Changed;
        cursor.LastInsertedCount = run.InsertedCount;
        cursor.LastUpdatedCount = run.UpdatedCount;
        cursor.LastSkippedCount = counts.Skipped;
        cursor.LastRetryCount = run.RetryCount;
        cursor.LastRateLimitCount = run.RateLimitCount;
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
        cursor.LastRequestCount = run.RequestCount;
        cursor.LastReceivedCount = counts.Received;
        cursor.LastChangedCount = counts.Changed;
        cursor.LastInsertedCount = run.InsertedCount;
        cursor.LastUpdatedCount = run.UpdatedCount;
        cursor.LastSkippedCount = counts.Skipped;
        cursor.LastRetryCount = run.RetryCount + (succeeded ? 0 : 1);
        cursor.LastRateLimitCount = run.RateLimitCount;
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    internal readonly record struct CursorCounts(int Received, int Changed, int Skipped, int Failed);

    // The counts a run leaves on its cursor. A product import counts what the import did over the whole run (the job's
    // own Alınan / İşlenen / Atlanan / Hatalı); any other read counts its records as it read them.
    internal static CursorCounts CursorImportCounts(bool productImport, CursorCounts run, CursorCounts import) =>
        productImport ? import : run;

    private CursorCounts CurrentCursorCounts() => CursorImportCounts(
        run.ProductImportTelemetry,
        new(run.ReceivedCount, run.ChangedCount, run.SkippedCount, run.FailedCount),
        new(run.ImportReceivedCount, run.ImportProcessedCount, run.ImportSkippedCount, run.ImportFailedCount));

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

    // An open-order status scan that could not read an order or package (a passing or connection error on that read,
    // OpenOrderReadTally) says so, without a count: a request can carry fifty packages and a "not found" is an answer,
    // so no count of requests or failures is the number of orders not read. The orders were not read, so they are
    // neither in Okunan nor in the records' Hatalı. Other reads keep their label.
    internal static string OpenOrderScanNote(string jobType, bool readFailed) =>
        readFailed && OrderReadRunHandler.StatusTypes.Contains(jobType) ? " · bazı siparişler okunamadı" : "";

    private async Task RecordReadProgressAsync(Guid tenantId, Guid? jobId, string jobType, CancellationToken cancellationToken)
    {
        if (jobId is not { } id || !RecordsReadProgress(jobType)) return;
        var received = run.ReceivedCount;
        var processed = run.InsertedCount + run.UpdatedCount;
        var skipped = run.SkippedCount;
        var failed = (int?)null;
        var label = ReadProgressLabel(received, run.InsertedCount, run.UpdatedCount);
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
        label += OpenOrderScanNote(jobType, run.OpenOrderReadFailed);
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
}
