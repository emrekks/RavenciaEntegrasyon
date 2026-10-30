using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Full reset of one connection (Entegrasyonlar › Verileri Sıfırla › Tam Sıfırlama, 30 Oct 2026 owner decision): every
// operational record of the connection goes, issued invoices included (they stay in E-Faturam); nothing is written to
// the marketplace or the invoice provider. The connection's settings stay (ConnectionDataInventory.Kept), and so do the
// linked products another connection uses or the panel created; only their link to this connection goes. The units the
// removed order lines took from stock stay taken under their carry key (P-2), so the next sync does not take them again.
// It runs as a background job (CONNECTION_FULL_RESET) that first holds every sync lock of the connection: the orders,
// invoices, products and links go in one transaction, then the large lists (reference values, price history, finance
// lines) in batches, each well within the command time limit.
public sealed partial class OperationalDataMaintenanceService
{
    internal const int ImportedProductLinkSeconds = 600;
    internal static readonly TimeSpan FullResetLockWait = TimeSpan.FromMinutes(2);
    internal const int FullResetBatchSize = 20_000;
    private static readonly TimeSpan FullResetCommandTimeout = TimeSpan.FromMinutes(10);
    private const int FullResetSteps = 6;
    private static readonly JobStatus[] WaitingJobStatuses = [JobStatus.Pending, JobStatus.RetryScheduled];
    private static readonly JobStatus[] OpenJobStatuses = [JobStatus.Pending, JobStatus.Leased, JobStatus.RetryScheduled];
    // The lanes every job of a connection runs in: holding them all, the reset runs alone.
    internal static readonly string[] SyncLockGroups = [.. MarketplaceJobTypes.ProcessorJobTypes.Select(MarketplaceSyncExecutionLock.GroupFor).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public async Task<ServiceResult<ConnectionFullResetView>> FullResetAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        if (!await db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.Status != "DELETED", cancellationToken))
            return ServiceResult<ConnectionFullResetView>.Fail("RESOURCE_NOT_FOUND", "Bağlantı bulunamadı.", 404);
        return ServiceResult<ConnectionFullResetView>.Ok(await FullResetViewAsync(tenantId, connectionId, cancellationToken));
    }

    public async Task<ServiceResult<ConnectionFullResetView>> RequestFullResetAsync(Guid tenantId, Guid actorUserId, Guid connectionId, long expectedVersion, FullResetConnectionCommand command, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.Status != "DELETED", cancellationToken);
        if (connection is null) return ServiceResult<ConnectionFullResetView>.Fail("RESOURCE_NOT_FOUND", "Bağlantı bulunamadı.", 404);
        if (connection.Version != expectedVersion) return ServiceResult<ConnectionFullResetView>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{connection.Version}.", 412);
        if (!SupportsFullReset(connection.PlatformCode))
            return ServiceResult<ConnectionFullResetView>.Fail("FULL_RESET_UNSUPPORTED", "Tam sıfırlama yalnız pazaryeri bağlantılarında kullanılabilir.", 422);
        if (!string.Equals(command.Confirmation?.Trim(), connection.DisplayName, StringComparison.Ordinal))
            return ServiceResult<ConnectionFullResetView>.Fail("VALIDATION_FAILED", $"Onay alanına bağlantı adını tam olarak yazın: {connection.DisplayName}", 422, new Dictionary<string, string[]> { ["confirmation"] = [$"Onay alanına bağlantı adını tam olarak yazın: {connection.DisplayName}"] });

        // A second request while one is queued or running answers with that one.
        if (!await db.IntegrationJobs.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ConnectionFullReset && OpenJobStatuses.Contains(x.Status), cancellationToken))
        {
            var now = timeProvider.GetUtcNow();
            await CancelWaitingJobsAsync(tenantId, connectionId, null, now, cancellationToken);
            var job = ConnectionReferencePlan.NewJob(tenantId, connectionId, MarketplaceJobTypes.ConnectionFullReset, $"connection-full-reset:{connectionId:D}:{Guid.CreateVersion7():N}", "{}", correlationId, JobPriorityPolicy.ForUser(MarketplaceJobTypes.ConnectionFullReset), "user", now);
            job.ProgressLabel = "Sıfırlama sırada bekliyor";
            db.IntegrationJobs.Add(job);
            db.AuditLogs.Add(Audit(tenantId, actorUserId, "CONNECTION_FULL_RESET_REQUESTED", "PlatformConnection", connectionId.ToString("D"), $"{connection.PlatformCode}:{connection.ExternalStoreId}", correlationId));
            await db.SaveChangesAsync(cancellationToken);
        }
        return ServiceResult<ConnectionFullResetView>.Ok(await FullResetViewAsync(tenantId, connectionId, cancellationToken));
    }

    // Marketplaces only: an e-invoice provider's records are its invoices, which its own reset and delete cover.
    internal static bool SupportsFullReset(string platformCode) =>
        ActiveIntegrationScope.Contains(platformCode) && ConnectionDataResetPolicy.ScopesFor(platformCode).Contains("ORDERS");

    public async Task<JobOutcome> RunFullResetAsync(Guid tenantId, Guid connectionId, Guid? jobId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId && x.Status != "DELETED").Select(x => new { x.PlatformCode }).SingleOrDefaultAsync(cancellationToken);
        if (connection is null) return new(JobExecutionResult.Blocked("RESOURCE_NOT_FOUND", "Bağlantı bulunamadı."), new());
        if (!SupportsFullReset(connection.PlatformCode)) return new(JobExecutionResult.Blocked("FULL_RESET_UNSUPPORTED", "Tam sıfırlama yalnız pazaryeri bağlantılarında kullanılabilir."), new());

        // Nothing queued for the connection starts while the reset waits for the running ones to finish.
        await CancelWaitingJobsAsync(tenantId, connectionId, jobId, timeProvider.GetUtcNow(), cancellationToken);
        await ReportFullResetProgressAsync(tenantId, jobId, 0, "Bağlantıda süren eşitlemelerin bitmesi bekleniyor", cancellationToken);
        var locks = new List<IAsyncDisposable>();
        try
        {
            var deadline = timeProvider.GetUtcNow() + FullResetLockWait;
            foreach (var group in SyncLockGroups)
            {
                var remaining = deadline - timeProvider.GetUtcNow();
                var acquired = await MarketplaceSyncExecutionLock.AcquireGroupAsync(db, connectionId, group, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, timeProvider, cancellationToken);
                if (acquired is null)
                    return new(JobExecutionResult.Deferred("CONNECTION_FULL_RESET_BUSY", "Bağlantıda süren bir eşitleme bitince tam sıfırlama yeniden denenecek.", TimeSpan.FromSeconds(30)), new());
                locks.Add(acquired);
            }

            var counts = await FullResetViewAsync(tenantId, connectionId, cancellationToken);
            var commandTimeout = db.Database.GetCommandTimeout();
            db.Database.SetCommandTimeout(FullResetCommandTimeout);
            try
            {
                await ReportFullResetProgressAsync(tenantId, jobId, 1, "Siparişler, iadeler, faturalar ve ürün bağları siliniyor", cancellationToken);
                await DeleteFullResetRecordsAsync(tenantId, connectionId, jobId, cancellationToken);
                await ReportFullResetProgressAsync(tenantId, jobId, 2, "Fiyat geçmişi ve kanal kayıtları siliniyor", cancellationToken);
                await DeleteInBatchesAsync($"""DELETE FROM inventory.channel_price_history WHERE "Id" IN (SELECT h."Id" FROM inventory.channel_price_history h JOIN inventory.channel_offers o ON o."TenantId"=h."TenantId" AND o."Id"=h."OfferId" WHERE o."TenantId"={tenantId} AND o."ConnectionId"={connectionId} LIMIT {FullResetBatchSize})""", cancellationToken);
                await db.Database.ExecuteSqlInterpolatedAsync($$"""
                    DELETE FROM inventory.channel_offers WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
                    DELETE FROM inventory.channel_inventory_observations WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
                    """, cancellationToken);
                await ReportFullResetProgressAsync(tenantId, jobId, 3, "Finans hareketleri ve kargo faturası kalemleri siliniyor", cancellationToken);
                await DeleteInBatchesAsync($"""DELETE FROM sales.marketplace_finance_transactions WHERE "Id" IN (SELECT "Id" FROM sales.marketplace_finance_transactions WHERE "TenantId"={tenantId} AND "ConnectionId"={connectionId} LIMIT {FullResetBatchSize})""", cancellationToken);
                await DeleteInBatchesAsync($"""DELETE FROM sales.marketplace_cargo_invoice_items WHERE "Id" IN (SELECT "Id" FROM sales.marketplace_cargo_invoice_items WHERE "TenantId"={tenantId} AND "ConnectionId"={connectionId} LIMIT {FullResetBatchSize})""", cancellationToken);
                await ReportFullResetProgressAsync(tenantId, jobId, 4, "Ürün aktarım kayıtları siliniyor", cancellationToken);
                await DeleteInBatchesAsync($"""DELETE FROM catalog.product_import_staging_records WHERE "Id" IN (SELECT "Id" FROM catalog.product_import_staging_records WHERE "TenantId"={tenantId} AND "ConnectionId"={connectionId} LIMIT {FullResetBatchSize})""", cancellationToken);
                await ReportFullResetProgressAsync(tenantId, jobId, 5, "Kategori, özellik ve marka listeleri siliniyor", cancellationToken);
                // A list's values go in batches before the list itself: deleting a list cascades to all of its values at once.
                foreach (var snapshotId in await db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId).Select(x => x.Id).ToListAsync(cancellationToken))
                {
                    await DeleteInBatchesAsync($"""DELETE FROM integration.reference_items WHERE "Id" IN (SELECT "Id" FROM integration.reference_items WHERE "TenantId"={tenantId} AND "SnapshotId"={snapshotId} LIMIT {FullResetBatchSize})""", cancellationToken);
                    await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM integration.reference_snapshots WHERE "TenantId"={tenantId} AND "Id"={snapshotId}""", cancellationToken);
                }
                await ReportFullResetProgressAsync(tenantId, jobId, FullResetSteps, "Tam sıfırlama tamamlandı", cancellationToken);
            }
            finally
            {
                db.Database.SetCommandTimeout(commandTimeout);
            }
            await RefreshDashboardBestEffortAsync(tenantId);
            var removed = counts.Items.Where(x => x.Scope != "PRODUCT_LINKS").Sum(x => x.Count);
            return new(JobExecutionResult.SuccessWithNote("CONNECTION_FULL_RESET_DONE", $"Bağlantının {removed} kaydı silindi; {counts.KeptProducts} ortak veya panelde oluşturulmuş ürün korundu."), new(Changed: removed));
        }
        finally
        {
            // The first lock opened the database connection the others share; it closes it, so it goes last.
            for (var index = locks.Count - 1; index >= 0; index--) await locks[index].DisposeAsync();
        }
    }

    private async Task DeleteInBatchesAsync(FormattableString statement, CancellationToken cancellationToken)
    {
        while (await db.Database.ExecuteSqlInterpolatedAsync(statement, cancellationToken) >= FullResetBatchSize) { }
    }

    // One transaction: the order of the statements follows the restricting keys (deliveries and documents before their
    // invoices and packages, returns before orders, links before products).
    private async Task DeleteFullResetRecordsAsync(Guid tenantId, Guid connectionId, Guid? jobId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await BeginFilePurgeAsync(cancellationToken);
        // The reset's own job and a job a worker holds right now stay; the others go with their attempts.
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            DELETE FROM integration.job_attempts a USING integration.jobs j WHERE a."TenantId"={{tenantId}} AND a."JobId"=j."Id" AND j."TenantId"={{tenantId}} AND j."ConnectionId"={{connectionId}} AND j."Status"<>'LEASED' AND j."Id" IS DISTINCT FROM CAST({{jobId}} AS uuid);
            DELETE FROM integration.jobs j WHERE j."TenantId"={{tenantId}} AND j."ConnectionId"={{connectionId}} AND j."Status"<>'LEASED' AND j."Id" IS DISTINCT FROM CAST({{jobId}} AS uuid);
            INSERT INTO purge_files SELECT d."FileAssetId" FROM billing.manual_order_invoice_documents d WHERE d."TenantId"={{tenantId}} AND d."ConnectionId"={{connectionId}} AND d."FileAssetId" IS NOT NULL ON CONFLICT DO NOTHING;
            DELETE FROM billing.manual_order_invoice_delivery_states WHERE "TenantId"={{tenantId}} AND ("ConnectionId"={{connectionId}} OR "DocumentId" IN (SELECT "Id" FROM billing.manual_order_invoice_documents WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}}));
            DELETE FROM billing.manual_order_invoice_documents WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM billing.marketplace_delivery_states WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM billing.marketplace_deliveries WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM sales.marketplace_invoice_reference_history WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            """, cancellationToken);
        await DeleteReturnsAsync(tenantId, connectionId, cancellationToken);
        await DeleteInvoicesAsync(tenantId, connectionId, cancellationToken);
        await DeleteOrdersAsync(tenantId, connectionId, cancellationToken, carryStock: true);
        await DeleteProductsAsync(tenantId, connectionId, cancellationToken, importedOnly: true);
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            DELETE FROM sales.marketplace_questions WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM integration.marketplace_question_sync_states WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM integration.marketplace_finance_sync_states WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.marketplace_listing_states WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.marketplace_variant_links WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.channel_listing_profiles WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.marketplace_product_links WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.external_identifier_aliases WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.category_mappings WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.brand_mappings WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.attribute_value_mappings WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.attribute_mappings WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.commission_tariff_rows r USING catalog.commission_tariffs t WHERE r."TenantId"={{tenantId}} AND r."TariffId"=t."Id" AND t."TenantId"={{tenantId}} AND t."ConnectionId"={{connectionId}};
            DELETE FROM catalog.commission_tariffs WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.promotion_offer_files WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            CREATE TEMP TABLE IF NOT EXISTS purge_import_sessions ("Id" uuid PRIMARY KEY) ON COMMIT DROP;
            TRUNCATE purge_import_sessions;
            INSERT INTO purge_import_sessions SELECT "Id" FROM catalog.import_sessions WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            INSERT INTO purge_files SELECT s."SourceAssetId" FROM catalog.import_sessions s JOIN purge_import_sessions p ON p."Id"=s."Id" WHERE s."SourceAssetId" IS NOT NULL ON CONFLICT DO NOTHING;
            DELETE FROM catalog.import_decisions d USING catalog.import_match_candidates m, purge_import_sessions p WHERE d."TenantId"={{tenantId}} AND d."CandidateId"=m."Id" AND m."SessionId"=p."Id";
            DELETE FROM catalog.import_match_candidates m USING purge_import_sessions p WHERE m."TenantId"={{tenantId}} AND m."SessionId"=p."Id";
            DELETE FROM catalog.field_provenance f USING purge_import_sessions p WHERE f."TenantId"={{tenantId}} AND f."SessionId"=p."Id";
            DELETE FROM catalog.import_staging_records r USING purge_import_sessions p WHERE r."TenantId"={{tenantId}} AND r."SessionId"=p."Id";
            DELETE FROM catalog.import_sessions s USING purge_import_sessions p WHERE s."Id"=p."Id";
            DELETE FROM catalog.product_import_sessions WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM catalog.catalog_structure_runs WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM integration.reconciliation_differences d USING integration.reconciliation_runs r WHERE d."TenantId"={{tenantId}} AND d."RunId"=r."Id" AND r."TenantId"={{tenantId}} AND r."ConnectionId"={{connectionId}};
            DELETE FROM integration.reconciliation_runs WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM integration.external_effect_records WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM integration.price_stock_send_log WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            DELETE FROM integration.connection_backoffs WHERE "ConnectionId"={{connectionId}};
            DELETE FROM integration.sync_cursors WHERE "TenantId"={{tenantId}} AND "ConnectionId"={{connectionId}};
            -- The open problems about the connection, its orders or its invoices have nothing left to point at.
            UPDATE ops.operational_issues i SET "Status"='Resolved', "ResolvedAt"={{timeProvider.GetUtcNow()}}
            WHERE i."TenantId"={{tenantId}} AND i."Status"<>'Resolved'
              AND (strpos(i."DedupeKey", CAST({{connectionId}} AS text)) > 0
                OR EXISTS (SELECT 1 FROM purge_orders o WHERE strpos(i."DedupeKey", o."Id"::text) > 0)
                OR EXISTS (SELECT 1 FROM purge_invoices p WHERE strpos(i."DedupeKey", p."Id"::text) > 0)
                -- An order shortfall issue is keyed by its order line (N format); with the line gone it holds no reservation.
                OR i."Code"='STOCK_ORDER_CONSUMPTION_BLOCKED' AND i."DedupeKey" LIKE 'stock-order-consumption:%'
                   AND NOT EXISTS (SELECT 1 FROM inventory.stock_reservations r WHERE r."TenantId"=i."TenantId" AND r."SourceType"='ORDER_LINE' AND r."Status"='Active' AND replace(r."SourceId", '-', '') = substring(i."DedupeKey" FROM 25)));
            """, cancellationToken);
        await ArchivePurgedFilesAsync(tenantId, cancellationToken);
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, Action = "CONNECTION_FULL_RESET", TargetType = "PlatformConnection", TargetId = connectionId.ToString("D"), Reason = jobId is { } id ? $"job:{id:D}" : "full-reset", CorrelationId = jobId?.ToString("D") ?? Guid.CreateVersion7().ToString("D"), CreatedAt = timeProvider.GetUtcNow() });
        AnnounceReset(tenantId, "connections", "PlatformConnection", connectionId);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private Task<int> CancelWaitingJobsAsync(Guid tenantId, Guid connectionId, Guid? keep, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.IntegrationJobs
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && WaitingJobStatuses.Contains(x.Status) && x.JobType != MarketplaceJobTypes.ConnectionFullReset && x.Id != keep)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, JobStatus.Cancelled)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.LastErrorCode, "CONNECTION_FULL_RESET")
                .SetProperty(x => x.LastErrorSummary, "Bağlantının verileri tam sıfırlandığı için iptal edildi.")
                .SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);

    private Task ReportFullResetProgressAsync(Guid tenantId, Guid? jobId, int step, string label, CancellationToken cancellationToken) => jobId is not { } id
        ? Task.CompletedTask
        : db.IntegrationJobs.Where(x => x.TenantId == tenantId && x.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.ProgressCurrent, step)
            .SetProperty(x => x.ProgressTotal, FullResetSteps)
            .SetProperty(x => x.ProgressPercent, step * 100 / FullResetSteps)
            .SetProperty(x => x.ProgressLabel, label), cancellationToken);

    private async Task<ConnectionFullResetView> FullResetViewAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var orders = await db.Orders.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        var returns = await db.ReturnClaims.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        var invoices = await db.Invoices.CountAsync(x => x.TenantId == tenantId && (x.ProviderConnectionId == connectionId || db.Orders.Any(order => order.TenantId == tenantId && order.Id == x.OrderId && order.ConnectionId == connectionId)), cancellationToken);
        var questions = await db.MarketplaceQuestions.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        var linkedProducts = await db.MarketplaceProductLinks.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId).Select(x => x.ProductId).Distinct().CountAsync(cancellationToken);
        var removedProducts = await ImportedProductCountAsync(tenantId, connectionId, cancellationToken);
        var finance = await db.MarketplaceFinanceTransactions.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken)
            + await db.MarketplaceCargoInvoiceItems.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        // The current lists' item counts: counting the rows themselves scans millions (CurrentListSizeAsync).
        var references = await db.ReferenceSnapshots.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.IsCurrent).SumAsync(x => x.ItemCount, cancellationToken);
        var mappings = await db.CategoryMappings.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken)
            + await db.BrandMappings.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken)
            + await db.AttributeMappings.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken)
            + await db.AttributeValueMappings.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        var commissionTariffs = await db.CommissionTariffs.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        var promotionOffers = await db.PromotionOfferFiles.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        var jobs = await db.IntegrationJobs.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.JobType != MarketplaceJobTypes.ConnectionFullReset, cancellationToken);
        var job = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ConnectionFullReset)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Select(x => new { x.Id, x.Status, x.ProgressLabel, x.ProgressPercent, x.LastErrorSummary, x.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        return new(
        [
            new("ORDERS", orders), new("RETURNS", returns), new("INVOICES", invoices), new("QUESTIONS", questions),
            new("PRODUCTS", removedProducts), new("PRODUCT_LINKS", linkedProducts), new("FINANCE", finance), new("REFERENCES", references),
            new("MAPPINGS", mappings), new("COMMISSION_TARIFFS", commissionTariffs), new("PROMOTION_OFFERS", promotionOffers), new("JOBS", jobs),
        ],
            linkedProducts - removedProducts,
            job is null ? null : new(job.Id, FullResetJobStatus(job.Status), job.Status is JobStatus.Dead or JobStatus.Blocked or JobStatus.ManualReview ? job.LastErrorSummary ?? job.ProgressLabel : job.ProgressLabel, job.ProgressPercent, job.CreatedAt));
    }

    private static string FullResetJobStatus(JobStatus status) => status switch
    {
        JobStatus.RetryScheduled => "RETRY_SCHEDULED",
        JobStatus.ManualReview => "MANUAL_REVIEW",
        _ => status.ToString().ToUpperInvariant()
    };

    // The products the full reset removes (DeleteProductsAsync, importedOnly): the same selection, counted.
    private Task<int> ImportedProductCountAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => db.Database.SqlQuery<int>($$"""
        SELECT count(*)::int AS "Value" FROM catalog.products p WHERE p."TenantId"={{tenantId}}
          AND EXISTS (SELECT 1 FROM catalog.marketplace_product_links l WHERE l."TenantId"={{tenantId}} AND l."ProductId"=p."Id" AND l."ConnectionId"={{connectionId}}
                AND abs(extract(epoch FROM uuid_extract_timestamp(p."Id") - uuid_extract_timestamp(l."Id"))) <= {{ImportedProductLinkSeconds}})
          AND NOT EXISTS (SELECT 1 FROM catalog.marketplace_product_links other_link WHERE other_link."TenantId"={{tenantId}} AND other_link."ProductId"=p."Id" AND other_link."ConnectionId"<> {{connectionId}})
          AND NOT EXISTS (SELECT 1 FROM catalog.marketplace_variant_links other_variant JOIN catalog.product_variants v ON v."TenantId"=other_variant."TenantId" AND v."Id"=other_variant."VariantId" WHERE other_variant."TenantId"={{tenantId}} AND v."ProductId"=p."Id" AND other_variant."ConnectionId"<> {{connectionId}})
        """).SingleAsync(cancellationToken);
}
