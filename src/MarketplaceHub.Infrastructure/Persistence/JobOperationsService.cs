using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.ErrorMapping;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class JobOperationsService(AppDbContext db, TimeProvider timeProvider) : IJobOperationsService
{
    public async Task<IReadOnlyList<JobSummaryView>> ListAsync(Guid tenantId, string? status, CancellationToken cancellationToken)
    {
        var query = db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!TryParseStatus(status, out var parsed)) return [];
            query = query.Where(x => x.Status == parsed);
        }
        var jobs = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).ToListAsync(cancellationToken);
        var failures = await FailureTimes(jobs, cancellationToken);
        var jobIds = jobs.Select(job => job.Id).ToArray();
        var lastAttemptStarts = jobIds.Length == 0
            ? new Dictionary<Guid, DateTimeOffset>()
            : await db.JobAttempts.AsNoTracking()
                .Where(attempt => attempt.TenantId == tenantId && jobIds.Contains(attempt.JobId))
                .GroupBy(attempt => attempt.JobId)
                .Select(group => new { JobId = group.Key, StartedAt = group.Max(attempt => attempt.StartedAt) })
                .ToDictionaryAsync(attempt => attempt.JobId, attempt => attempt.StartedAt, cancellationToken);
        var batchCounts = jobs.GroupBy(job => job.CorrelationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return jobs.Select(job =>
        {
            lastAttemptStarts.TryGetValue(job.Id, out var lastAttemptStartedAt);
            return Summary(
                job,
                failures.GetValueOrDefault(job.Id),
                batchCounts.GetValueOrDefault(job.CorrelationId, 1),
                lastAttemptStarts.ContainsKey(job.Id) ? lastAttemptStartedAt : null);
        }).ToList();
    }

    public async Task<ServiceResult<JobDetailView>> GetAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == jobId, cancellationToken);
        return job is null
            ? ServiceResult<JobDetailView>.Fail("JOB_NOT_FOUND", "Job bulunamadı.", 404)
            : ServiceResult<JobDetailView>.Ok(await DetailAsync(job, cancellationToken));
    }

    public async Task<ServiceResult<JobDetailView>> RetryAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == jobId, cancellationToken);
        if (job is null) return ServiceResult<JobDetailView>.Fail("JOB_NOT_FOUND", "Job bulunamadı.", 404);
        if (job.Status is JobStatus.Leased or JobStatus.Pending or JobStatus.RetryScheduled or JobStatus.Succeeded or JobStatus.Cancelled)
            return ServiceResult<JobDetailView>.Fail("JOB_NOT_RETRYABLE", "Bu job mevcut durumunda manuel yeniden deneme kabul etmiyor.", 409);

        var now = timeProvider.GetUtcNow();
        if (job.JobType == InvoicingJobTypes.InvoiceSubmit)
        {
            var invoiceId = PayloadGuid(job.PayloadJson, "invoiceId");
            if (invoiceId is null)
                return ServiceResult<JobDetailView>.Fail("INVOICE_JOB_PAYLOAD_INVALID", "Fatura gönderim job'unda geçerli bir fatura kimliği bulunamadı.", 409);
            var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invoiceId.Value, cancellationToken);
            if (invoice is null)
                return ServiceResult<JobDetailView>.Fail("INVOICE_NOT_FOUND", "Yeniden denenecek fatura kaydı bulunamadı.", 404);
            if (!PrepareInvoiceSubmitRetry(invoice, now))
                return ServiceResult<JobDetailView>.Fail("INVOICE_STATE_NOT_RETRYABLE", $"Fatura mevcut durumu ({invoice.Status}) ile güvenli biçimde yeniden gönderilemez. Önce fatura kaydındaki işlemi tamamlayın.", 409);
            job.Priority = Math.Min(job.Priority, InvoicingBillingService.InvoiceJobPriority(job.JobType));
        }

        job.Status = JobStatus.RetryScheduled;
        job.AvailableAt = now;
        job.CompletedAt = null;
        job.LeaseTokenHash = null;
        job.LeaseExpiresAt = null;
        job.HeartbeatAt = null;
        job.LastErrorCode = null;
        job.LastErrorSummary = null;
        job.MaxAttempts = Math.Max(job.MaxAttempts, job.AttemptCount + 1);
        job.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<JobDetailView>.Ok(await DetailAsync(job, cancellationToken));
    }

    public async Task<ServiceResult<JobDetailView>> EnqueueOneTimeInvoiceDeliveryAsync(
        Guid tenantId,
        Guid jobId,
        string orderNumber,
        string idempotencyKey,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!OneTimeInvoiceDeliveryPolicy.IsAuthorizedTarget(orderNumber))
            return ServiceResult<JobDetailView>.Fail("ONE_TIME_INVOICE_ORDER_NOT_AUTHORIZED", "Tek seferlik fatura iletimi yalnızca yetkilendirilmiş test siparişleri için yetkilendirildi.", 403);

        var sourceJob = await db.IntegrationJobs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == jobId, cancellationToken);
        if (sourceJob is null) return ServiceResult<JobDetailView>.Fail("JOB_NOT_FOUND", "Job bulunamadı.", 404);
        var hasPriorNoWriteAttempt = sourceJob.LastErrorCode == OneTimeInvoiceDeliveryPolicy.PriorNoWriteFailureCode
            || await db.JobAttempts.AsNoTracking().AnyAsync(x =>
                x.TenantId == tenantId
                && x.JobId == sourceJob.Id
                && !x.Succeeded
                && x.ErrorCode == OneTimeInvoiceDeliveryPolicy.PriorNoWriteFailureCode,
                cancellationToken);
        if (sourceJob.JobType != InvoicingJobTypes.MarketplaceDelivery
            || sourceJob.Status is not (JobStatus.Blocked or JobStatus.ManualReview or JobStatus.Dead)
            || !OneTimeInvoiceDeliveryPolicy.IsEligibleSourceFailure(sourceJob.LastErrorCode, hasPriorNoWriteAttempt))
            return ServiceResult<JobDetailView>.Fail("ONE_TIME_INVOICE_SOURCE_NOT_SAFE", "Bu sipariş için dış çağrı yapılmadan oluştuğu doğrulanmış bir fatura iletim engeli bulunamadı.", 409);

        var invoiceId = PayloadGuid(sourceJob.PayloadJson, "invoiceId");
        if (invoiceId is null) return ServiceResult<JobDetailView>.Fail("INVOICE_JOB_PAYLOAD_INVALID", "Fatura iletim job'unda geçerli bir fatura kimliği bulunamadı.", 409);
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invoiceId.Value, cancellationToken);
        if (invoice is null) return ServiceResult<JobDetailView>.Fail("INVOICE_NOT_FOUND", "İletilecek fatura kaydı bulunamadı.", 404);
        if (!InvoiceMarketplaceRetryPolicy.CanRetryDelivery(invoice.Status, invoice.LastErrorCode)
            || string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            || invoice.PackageId is null)
            return ServiceResult<JobDetailView>.Fail("ONE_TIME_INVOICE_NOT_READY", "Siparişin kesilmiş, numarası bulunan ve pazaryerine iletime hazır faturası yok.", 409);

        var package = await db.ShipmentPackages.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invoice.PackageId && x.OrderId == invoice.OrderId, cancellationToken);
        if (package is null
            || sourceJob.ConnectionId != package.ConnectionId
            || string.IsNullOrWhiteSpace(package.ExternalPackageId))
            return ServiceResult<JobDetailView>.Fail("ONE_TIME_INVOICE_PACKAGE_MISMATCH", "Fatura ve başarısız Hepsiburada teslim job'ı aynı sipariş paketini göstermiyor.", 409);

        var order = await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == invoice.OrderId)
            .Select(x => new { x.OrderNumber })
            .SingleOrDefaultAsync(cancellationToken);
        if (order is null || !string.Equals(order.OrderNumber, orderNumber, StringComparison.Ordinal)
            || !OneTimeInvoiceDeliveryPolicy.IsAuthorizedTarget(order.OrderNumber))
            return ServiceResult<JobDetailView>.Fail("ONE_TIME_INVOICE_ORDER_MISMATCH", "Fatura onaylanan siparişe ait değil.", 409);

        var connection = await db.PlatformConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == package.ConnectionId, cancellationToken);
        if (connection is null
            || !string.Equals(connection.PlatformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
            || !IntegrationRuntimePolicy.IsProduction(connection)
            || !IntegrationRuntimePolicy.IsActive(connection))
            return ServiceResult<JobDetailView>.Fail("ONE_TIME_INVOICE_DESTINATION_INVALID", "Tek seferlik işlem yalnızca etkin Hepsiburada Production siparişine gönderilebilir.", 409);

        var documentReady = await db.InvoiceDocuments.AsNoTracking().AnyAsync(x =>
            x.TenantId == tenantId && x.InvoiceId == invoice.Id && x.PermanentUrl != null,
            cancellationToken);
        if (!documentReady)
            return ServiceResult<JobDetailView>.Fail("INVOICE_PERMANENT_LINK_REQUIRED", "Pazaryerine gönderilebilecek kalıcı HTTPS fatura belgesi hazır değil.", 409);

        var state = await db.MarketplaceDeliveryStates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id, cancellationToken);
        var latestHistory = state is null
            ? await db.MarketplaceDeliveries.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id)
                .OrderByDescending(x => x.AttemptNumber)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        if (state is not null && !OneTimeInvoiceDeliveryPolicy.IsSafePriorFailure(state.Status, state.ErrorCode, state.ExternalReference)
            || latestHistory is not null && !OneTimeInvoiceDeliveryPolicy.IsSafePriorFailure(latestHistory.Status, latestHistory.ErrorCode, latestHistory.ExternalReference))
            return ServiceResult<JobDetailView>.Fail("ONE_TIME_INVOICE_PRIOR_ATTEMPT_UNSAFE", "Önceki pazaryeri denemesinin dış etkisi kesin olarak dışlanamadığı için yeniden gönderim engellendi.", 409);

        var dedupKey = $"one-time-invoice-delivery:{invoice.Id:N}:{orderNumber}:stage-test-v2";
        var existing = await db.IntegrationJobs.SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.JobType == InvoicingJobTypes.MarketplaceDelivery && x.JobDedupKey == dedupKey,
            cancellationToken);
        if (existing is not null) return ServiceResult<JobDetailView>.Ok(await DetailAsync(existing, cancellationToken));

        var now = timeProvider.GetUtcNow();
        var payload = JsonSerializer.Serialize(new
        {
            invoiceId = invoice.Id,
            oneTimeInvoiceDelivery = new
            {
                orderNumber,
                sourceJobId = sourceJob.Id
            }
        });
        var job = new IntegrationJob
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = package.ConnectionId,
            JobType = InvoicingJobTypes.MarketplaceDelivery,
            PayloadJson = payload,
            PayloadVersion = 1,
            PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            JobDedupKey = dedupKey,
            EffectIdempotencyKey = $"{InvoicingJobTypes.MarketplaceDelivery}:one-time:{invoice.Id:N}:{idempotencyKey}",
            Priority = -1,
            MaxAttempts = 1,
            Status = JobStatus.Pending,
            AvailableAt = now,
            TriggerType = "manual",
            ResourceType = "invoices",
            OperationType = "marketplace-delivery-once",
            CorrelationId = correlationId,
            CreatedAt = now,
            Version = 1
        };
        db.IntegrationJobs.Add(job);
        AddOutboxEvent(job, now);
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<JobDetailView>.Ok(await DetailAsync(job, cancellationToken));
    }

    internal static bool PrepareInvoiceSubmitRetry(Invoice invoice, DateTimeOffset now)
    {
        if (invoice.LastErrorCode == MarketplaceInvoiceCreationPolicy.DisabledErrorCode) return false;
        if (invoice.Status == InvoiceStatus.Submitting && invoice.LastErrorCode is null) return true;
        if (!InvoicingBillingService.CanRetryPreProviderFailure(invoice.Status, invoice.LastErrorCode, invoice.ExternalReference)) return false;

        invoice.Status = InvoiceStatus.Submitting;
        invoice.LastErrorCode = null;
        invoice.IssuedAt ??= now;
        invoice.UpdatedAt = now;
        invoice.Version++;
        return true;
    }

    public async Task<ServiceResult<JobDetailView>> CancelAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == jobId, cancellationToken);
        if (job is null) return ServiceResult<JobDetailView>.Fail("JOB_NOT_FOUND", "Job bulunamadı.", 404);
        if (job.Status is JobStatus.Succeeded or JobStatus.Dead or JobStatus.Cancelled)
            return ServiceResult<JobDetailView>.Fail("JOB_TERMINAL", "Terminal durumdaki job iptal edilemez.", 409);

        var now = timeProvider.GetUtcNow();
        var wasLeased = job.Status == JobStatus.Leased;
        if (wasLeased)
        {
            var attempt = await db.JobAttempts.SingleOrDefaultAsync(x =>
                x.JobId == job.Id && x.AttemptNumber == job.AttemptCount && x.CompletedAt == null,
                cancellationToken);
            if (attempt is not null)
            {
                attempt.CompletedAt = now;
                attempt.Succeeded = false;
                attempt.ErrorCode = "CANCELLED_BY_OPERATOR";
                attempt.ErrorSummary = "Çalışan job kullanıcı tarafından durduruldu.";
            }
        }

        job.Status = JobStatus.Cancelled;
        job.CompletedAt = now;
        job.LastErrorCode = "CANCELLED_BY_OPERATOR";
        job.LastErrorSummary = wasLeased
            ? "Çalışan job kullanıcı tarafından durduruldu."
            : "Job kullanıcı tarafından iptal edildi.";
        job.LeaseTokenHash = null;
        job.LeaseExpiresAt = null;
        job.HeartbeatAt = null;
        job.Version++;
        AddOutboxEvent(job, now);
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<JobDetailView>.Ok(await DetailAsync(job, cancellationToken));
    }

    private async Task<JobDetailView> DetailAsync(IntegrationJob job, CancellationToken cancellationToken)
    {
        var attempts = await db.JobAttempts.AsNoTracking()
            .Where(x => x.TenantId == job.TenantId && x.JobId == job.Id)
            .OrderByDescending(x => x.AttemptNumber)
            .Select(x => new JobAttemptDetailView(x.AttemptNumber, x.StartedAt, x.CompletedAt, x.Succeeded, x.ErrorCode, x.ErrorSummary))
            .ToListAsync(cancellationToken);
        var failure = attempts.Where(x => !x.Succeeded).ToList();
        var failureTimes = failure.Count == 0
            ? null
            : new FailureTime(failure.Min(x => x.StartedAt), failure.Max(x => x.CompletedAt ?? x.StartedAt));
        var currentOrder = await OrderContext(job, cancellationToken);
        var invoice = await InvoiceContext(job, cancellationToken);
        var relatedJobs = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == job.TenantId && x.CorrelationId == job.CorrelationId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);
        var relatedOrders = new List<JobOrderContextView>();
        foreach (var relatedJob in relatedJobs)
        {
            var relatedOrder = await OrderContext(relatedJob, cancellationToken);
            if (relatedOrder is not null && relatedOrders.All(order => order.OrderId != relatedOrder.OrderId)) relatedOrders.Add(relatedOrder);
        }
        var scan = await ScanAsync(job, cancellationToken);
        var failureReasons = await ProductImportFailureReasonsAsync(job, cancellationToken);
        var summary = Summary(job, failureTimes, relatedJobs.Count, attempts.FirstOrDefault()?.StartedAt);
        if (job.JobType == InvoicingJobTypes.InvoiceSubmit && job.LastErrorCode == "EFATURAM_REQUEST_REJECTED")
        {
            var invoiceId = PayloadGuid(job.PayloadJson, "invoiceId");
            if (invoiceId is not null)
            {
                var providerReference = await db.InvoiceSubmissionAttempts.AsNoTracking()
                    .Where(attempt => attempt.TenantId == job.TenantId && attempt.InvoiceId == invoiceId.Value)
                    .OrderByDescending(attempt => attempt.AttemptNumber)
                    .Select(attempt => attempt.RemoteRequestId)
                    .FirstOrDefaultAsync(cancellationToken);
                var providerDetail = TrendyolEFaturamProblemDetails.ToOperatorSummary(providerReference);
                if (providerDetail is not null)
                {
                    var previousSummary = summary.LastErrorSummary;
                    summary = summary with
                    {
                        LastErrorSummary = string.IsNullOrWhiteSpace(previousSummary)
                            ? providerDetail
                            : $"{previousSummary} {providerDetail}"
                    };
                }
            }
        }
        return new JobDetailView(summary, attempts, currentOrder, Change(job), relatedOrders, scan, failureReasons, invoice);
    }

    private async Task<JobInvoiceContextView?> InvoiceContext(IntegrationJob job, CancellationToken cancellationToken)
    {
        var invoiceId = PayloadGuid(job.PayloadJson, "invoiceId");
        if (invoiceId is null) return null;
        var invoice = await db.Invoices.AsNoTracking()
            .Where(x => x.TenantId == job.TenantId && x.Id == invoiceId.Value)
            .Select(x => new { x.Id, x.OrderId, x.PackageId, x.Status, x.InvoiceType, x.Currency, x.PayableTotal, x.InvoiceNumber })
            .SingleOrDefaultAsync(cancellationToken);
        if (invoice is null) return null;
        var orderNumber = await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == job.TenantId && x.Id == invoice.OrderId)
            .Select(x => x.OrderNumber)
            .SingleOrDefaultAsync(cancellationToken);
        if (orderNumber is null) return null;
        var externalPackageId = invoice.PackageId is { } packageId
            ? await db.ShipmentPackages.AsNoTracking()
                .Where(x => x.TenantId == job.TenantId && x.Id == packageId)
                .Select(x => x.ExternalPackageId)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        return new JobInvoiceContextView(invoice.Id, orderNumber, invoice.Status.ToString().ToUpperInvariant(), invoice.InvoiceType,
            invoice.Currency, invoice.PayableTotal, invoice.InvoiceNumber, externalPackageId);
    }

    private async Task<IReadOnlyList<JobFailureReasonView>> ProductImportFailureReasonsAsync(IntegrationJob job, CancellationToken cancellationToken)
    {
        var type = job.JobType.ToUpperInvariant();
        if (job.ConnectionId is not { } connectionId || !type.Contains("PRODUCT_SYNC", StringComparison.Ordinal)) return [];

        var prefix = $"product-sync-import:{connectionId}:";
        var attemptStartedAt = job.StartedAt ?? job.CreatedAt;
        var issues = await db.OperationalIssues.AsNoTracking()
            .Where(issue => issue.TenantId == job.TenantId
                && issue.Code == "PRODUCT_IMPORT_FAILED"
                && issue.DedupeKey.StartsWith(prefix)
                && issue.LastSeenAt >= attemptStartedAt)
            .OrderByDescending(issue => issue.LastSeenAt)
            .ToListAsync(cancellationToken);

        return ProductImportFailureReasonPolicy.Build(issues);
    }

    private async Task<JobScanView> ScanAsync(IntegrationJob job, CancellationToken cancellationToken)
    {
        var type = job.JobType.ToUpperInvariant();
        var mode = "OPERATION";
        var label = "Standart işlem";
        var detail = "Bu işlem belirli bir tarama kapsamı yerine tek bir operasyon yürütür.";
        string? window = null;
        string? externalOrderId = null;
        var full = false;
        int? lookbackDays = null;
        int? lookbackHours = null;

        try
        {
            using var document = JsonDocument.Parse(job.PayloadJson);
            var root = document.RootElement;
            externalOrderId = StringValue(root, "externalOrderId");
            full = BoolValue(root, "full");
            lookbackDays = IntValue(root, "lookbackDays");
            lookbackHours = IntValue(root, "lookbackHours");
        }
        catch (JsonException) { }

        if (type.Contains("ORDER_SYNC", StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(externalOrderId))
            {
                mode = "SINGLE";
                label = "Tekil sipariş taraması";
                detail = $"Yalnızca {externalOrderId} numaralı sipariş yenilendi.";
            }
            else if (full)
            {
                mode = "FULL";
                label = "Tam sipariş taraması";
                detail = "Erişilebilen sipariş geçmişi baştan taranır ve yerel kayıtlarla uzlaştırılır.";
                window = "Trendyol Stream sınırı nedeniyle 14 günlük güvenli pencereler";
            }
            else
            {
                mode = "QUICK";
                label = "Hızlı sipariş taraması";
                detail = "Son başarılı watermark sonrasındaki değişiklikler alınır; güvenlik örtüşmesiyle kaçan kayıtlar tekrar kontrol edilir.";
                window = "Son watermark + güvenlik örtüşmesi";
            }
        }
        else if (type.Contains("ORDER_RECOVERY_SYNC", StringComparison.Ordinal))
        {
            mode = "FULL";
            label = "Tam sipariş taraması";
            detail = "Sipariş geçmişindeki erişilebilir pencereler sırayla taranarak eksik yerel kayıtlar tamamlanır.";
            window = "14 günlük güvenli pencereler";
        }
        else if (type.Contains("ORDER_INVOICE_RECONCILIATION", StringComparison.Ordinal))
        {
            mode = "INVOICE";
            label = "Paket fatura taraması";
            detail = "Teslim edilmiş ve açık paketlerin pazaryeri fatura durumu ayrı kontrol edilir; sipariş gövdesinin eski olması bu kontrolü engellemez.";
            window = "Faturası kesinleşmemiş paketler";
        }
        else if (type.Contains("ORDER_RECONCILIATION", StringComparison.Ordinal))
        {
            mode = "COMPREHENSIVE";
            label = "Kapsamlı sipariş taraması";
            detail = "Yerel siparişler ile pazaryeri kayıtları karşılaştırılır; durum ve paket farklılıkları düzeltilir.";
            window = lookbackDays is > 0 ? $"Son {lookbackDays} gün" : "Uzlaştırma kapsamı";
        }
        else if (type.Contains("ORDER_STATUS_SYNC", StringComparison.Ordinal))
        {
            mode = "STATUS";
            label = "Sipariş durum taraması";
            detail = "Açık siparişlerin paket ve taşıma durumları kontrol edilerek yerel durum güncellenir.";
            window = "Açık siparişler";
        }
        else if (type.Contains("RETURN_RECONCILIATION", StringComparison.Ordinal))
        {
            mode = "COMPREHENSIVE";
            label = "Kapsamlı iade taraması";
            detail = "Yerel iade kayıtları ile pazaryeri kayıtları karşılaştırılır ve farklar giderilir.";
            window = lookbackDays is > 0 ? $"Son {lookbackDays} gün" : "Uzlaştırma kapsamı";
        }
        else if (type.Contains("RETURN_SYNC", StringComparison.Ordinal))
        {
            mode = "QUICK";
            label = "Hızlı iade taraması";
            detail = "Son değişen iade talepleri güvenlik örtüşmesiyle alınır.";
            window = "Son watermark + güvenlik örtüşmesi";
        }
        else if (type.Contains("RETURN_STATUS_SYNC", StringComparison.Ordinal))
        {
            mode = "STATUS";
            label = "İade durum taraması";
            detail = "Açık iadelerin güncel durumları kontrol edilir.";
            window = "Açık iadeler";
        }
        else if (type.Contains("STOCK_RECONCILIATION", StringComparison.Ordinal))
        {
            mode = "COMPREHENSIVE";
            label = "Kapsamlı stok taraması";
            detail = "Yerel stok projeksiyonları ile pazaryeri stokları karşılaştırılır.";
            window = lookbackHours is > 0 ? $"Son {lookbackHours} saat" : "Uzlaştırma kapsamı";
        }
        else if (type.Contains("PRODUCT_SYNC", StringComparison.Ordinal))
        {
            mode = "FULL";
            label = "Tam ürün taraması";
            detail = "Pazaryerindeki erişilebilir katalog ürünleri ve varyantları baştan okunur.";
            window = "Erişilebilir tüm katalog";
        }
        else if (type.Contains("REFERENCE_SYNC", StringComparison.Ordinal))
        {
            mode = "FULL";
            label = "Tam referans taraması";
            detail = "Seçilen kategori, marka veya özellik referansları sayfa sayfa yenilenir.";
            window = "Erişilebilir tüm referans kayıtları";
        }

        var resourceType = ScheduledResourceType(job.JobDedupKey);
        var scheduledPrefix = ScheduledPrefix(job.JobDedupKey);
        var policy = resourceType is null || job.ConnectionId is null
            ? null
            : await db.ConnectionSyncPolicies.AsNoTracking()
                .Where(x => x.TenantId == job.TenantId && x.ConnectionId == job.ConnectionId.Value && x.ResourceType == resourceType)
                .Select(x => new { x.IntervalSeconds })
                .FirstOrDefaultAsync(cancellationToken);
        var previous = scheduledPrefix is null
            ? null
            : await db.IntegrationJobs.AsNoTracking()
                .Where(x => x.TenantId == job.TenantId && x.Id != job.Id && x.CreatedAt < job.CreatedAt && x.JobDedupKey.StartsWith(scheduledPrefix))
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => (DateTimeOffset?)x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

        var actualInterval = previous is { } previousAt
            ? job.CreatedAt - previousAt
            : (TimeSpan?)null;
        return new JobScanView(
            mode,
            label,
            detail,
            window,
            policy?.IntervalSeconds,
            policy is null ? null : DurationLabel(TimeSpan.FromSeconds(policy.IntervalSeconds)),
            previous?.ToString("O"),
            actualInterval is { } gap && gap > TimeSpan.Zero ? DurationLabel(gap) : null);
    }

    private async Task<JobOrderContextView?> OrderContext(IntegrationJob job, CancellationToken cancellationToken)
    {
        Guid? orderId = null;
        Guid? packageId = null;
        Guid? invoiceId = null;
        Guid? claimId = null;
        string? externalOrderId = null;
        string? externalPackageId = null;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(job.PayloadJson);
            var root = document.RootElement;
            orderId = GuidValue(root, "orderId");
            packageId = GuidValue(root, "packageId");
            invoiceId = GuidValue(root, "invoiceId");
            claimId = GuidValue(root, "claimId");
            externalOrderId = StringValue(root, "externalOrderId");
            externalPackageId = StringValue(root, "externalPackageId");
        }
        catch (System.Text.Json.JsonException) { }

        if (invoiceId is { } invoiceGuid && (orderId is null || packageId is null))
        {
            var invoice = await db.Invoices.AsNoTracking()
                .Where(x => x.TenantId == job.TenantId && x.Id == invoiceGuid)
                .Select(x => new { x.OrderId, x.PackageId })
                .SingleOrDefaultAsync(cancellationToken);
            if (invoice is not null)
            {
                orderId ??= invoice.OrderId;
                packageId ??= invoice.PackageId;
            }
        }

        string? cargoProvider = null;
        string? cargoTrackingNumber = null;
        if (packageId is { } packageGuid)
        {
            var package = await db.ShipmentPackages.AsNoTracking()
                .Where(x => x.TenantId == job.TenantId && x.Id == packageGuid)
                .Select(x => new { x.OrderId, x.ExternalPackageId, x.CargoProviderExternalId, x.CargoTrackingNumber })
                .SingleOrDefaultAsync(cancellationToken);
            if (package is not null)
            {
                orderId ??= package.OrderId;
                externalPackageId ??= package.ExternalPackageId;
                cargoProvider = package.CargoProviderExternalId;
                cargoTrackingNumber = package.CargoTrackingNumber;
            }
        }

        if (claimId is { } claimGuid)
        {
            var claimOrderId = await db.ReturnClaims.AsNoTracking()
                .Where(x => x.TenantId == job.TenantId && x.Id == claimGuid)
                .Select(x => x.OrderId)
                .SingleOrDefaultAsync(cancellationToken);
            if (claimOrderId != Guid.Empty) orderId ??= claimOrderId;
        }

        if (orderId is null && !string.IsNullOrWhiteSpace(externalPackageId))
        {
            var package = await db.ShipmentPackages.AsNoTracking()
                .Where(x => x.TenantId == job.TenantId && x.ExternalPackageId == externalPackageId)
                .Select(x => new { x.OrderId, x.CargoProviderExternalId, x.CargoTrackingNumber })
                .SingleOrDefaultAsync(cancellationToken);
            if (package is not null)
            {
                orderId = package.OrderId;
                cargoProvider ??= package.CargoProviderExternalId;
                cargoTrackingNumber ??= package.CargoTrackingNumber;
            }
        }

        var order = orderId is { } localOrderId
            ? await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == job.TenantId && x.Id == localOrderId, cancellationToken)
            : !string.IsNullOrWhiteSpace(externalOrderId)
                ? await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == job.TenantId && (job.ConnectionId == null || x.ConnectionId == job.ConnectionId) && x.ExternalOrderId == externalOrderId, cancellationToken)
                : null;
        if (order is null) return null;

        var lineCount = await db.OrderLines.AsNoTracking().CountAsync(x => x.TenantId == job.TenantId && x.OrderId == order.Id, cancellationToken);
        return new JobOrderContextView(order.Id, order.OrderNumber, order.ExternalOrderId, order.DerivedStatus, order.Currency, order.NetAmount, order.OrderedAt, externalPackageId, cargoProvider, cargoTrackingNumber, CustomerName(order.CustomerSnapshotJson), lineCount);
    }

    private static Guid? PayloadGuid(string payloadJson, string field)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
            return GuidValue(document.RootElement, field);
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private void AddOutboxEvent(IntegrationJob job, DateTimeOffset now)
    {
        var metadata = IntegrationJobMetadataPolicy.Apply(job);
        db.IntegrationOutboxEvents.Add(new IntegrationOutboxEvent
        {
            Id = Guid.CreateVersion7(),
            TenantId = job.TenantId,
            ResourceType = metadata.ResourceType,
            OperationType = metadata.OperationType,
            AggregateType = "IntegrationJob",
            AggregateId = job.Id,
            AggregateVersion = job.Version,
            PayloadJson = JsonSerializer.Serialize(new { jobId = job.Id, jobType = job.JobType, status = JobStatusText(job.Status), version = job.Version }),
            CreatedAt = now,
            NextAttemptAt = now
        });
    }

    private static string JobStatusText(JobStatus status) => status == JobStatus.RetryScheduled ? "RETRY_SCHEDULED" : status == JobStatus.ManualReview ? "MANUAL_REVIEW" : status.ToString().ToUpperInvariant();

    private static Guid? GuidValue(System.Text.Json.JsonElement root, string name) =>
        Property(root, name) is { } value && value.ValueKind == System.Text.Json.JsonValueKind.String && Guid.TryParse(value.GetString(), out var result)
            ? result
            : null;

    private static string? StringValue(System.Text.Json.JsonElement root, string name) =>
        Property(root, name) is { } value && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool BoolValue(System.Text.Json.JsonElement root, string name) =>
        Property(root, name) is { } value && value.ValueKind == System.Text.Json.JsonValueKind.True;

    private static int? IntValue(System.Text.Json.JsonElement root, string name) =>
        Property(root, name) is { } value && value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : null;

    private static string? ScheduledResourceType(string dedupKey)
    {
        if (!dedupKey.StartsWith("scheduled:", StringComparison.Ordinal)) return null;
        if (dedupKey.StartsWith("scheduled:orders:", StringComparison.Ordinal)) return "ORDERS";
        if (dedupKey.StartsWith("scheduled:order-recovery:", StringComparison.Ordinal)) return "ORDER_RECOVERY";
        if (dedupKey.StartsWith("scheduled:order-lifecycle:", StringComparison.Ordinal)) return "ORDER_LIFECYCLE";
        if (dedupKey.StartsWith("scheduled:order-reconcile-short:", StringComparison.Ordinal)) return "ORDER_RECONCILE_SHORT";
        if (dedupKey.StartsWith("scheduled:order-reconcile-medium:", StringComparison.Ordinal)) return "ORDER_RECONCILE_MEDIUM";
        if (dedupKey.StartsWith("scheduled:order-reconcile-daily:", StringComparison.Ordinal)) return "ORDER_RECONCILE_DAILY";
        if (dedupKey.StartsWith("scheduled:returns:", StringComparison.Ordinal)) return "RETURNS";
        if (dedupKey.StartsWith("scheduled:return-lifecycle:", StringComparison.Ordinal)) return "RETURN_LIFECYCLE";
        if (dedupKey.StartsWith("scheduled:return-reconcile-short:", StringComparison.Ordinal)) return "RETURN_RECONCILE_SHORT";
        if (dedupKey.StartsWith("scheduled:return-reconcile-medium:", StringComparison.Ordinal)) return "RETURN_RECONCILE_MEDIUM";
        if (dedupKey.StartsWith("scheduled:return-reconcile-daily:", StringComparison.Ordinal)) return "RETURN_RECONCILE_DAILY";
        if (dedupKey.StartsWith("scheduled:stock-reconcile-short:", StringComparison.Ordinal)) return "STOCK_RECONCILE_SHORT";
        if (dedupKey.StartsWith("scheduled:stock-reconcile-medium:", StringComparison.Ordinal)) return "STOCK_RECONCILE_MEDIUM";
        if (dedupKey.StartsWith("scheduled:stock-reconcile-daily:", StringComparison.Ordinal)) return "STOCK_RECONCILE_DAILY";
        if (dedupKey.StartsWith("scheduled:reference:", StringComparison.Ordinal)) return "REFERENCE_DATA";
        return null;
    }

    private static string? ScheduledPrefix(string dedupKey)
    {
        if (!dedupKey.StartsWith("scheduled:", StringComparison.Ordinal)) return null;
        var separator = dedupKey.LastIndexOf(':');
        return separator > 0 ? dedupKey[..separator] : dedupKey;
    }

    private static string DurationLabel(TimeSpan duration)
    {
        var seconds = Math.Max(0, (int)Math.Round(duration.TotalSeconds));
        if (seconds < 60) return $"{seconds} sn";
        var minutes = seconds / 60;
        return seconds % 60 == 0 ? $"{minutes} dk" : $"{minutes} dk {seconds % 60} sn";
    }

    private static System.Text.Json.JsonElement? Property(System.Text.Json.JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static string? CustomerName(string snapshot)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(snapshot);
            var first = TextValue(document.RootElement, "customerFirstName", "firstName");
            var last = TextValue(document.RootElement, "customerLastName", "lastName");
            if (!string.IsNullOrWhiteSpace(first) || !string.IsNullOrWhiteSpace(last)) return string.Join(' ', new[] { first, last }.Where(value => !string.IsNullOrWhiteSpace(value)));
            foreach (var name in new[] { "name", "fullName", "customerName", "firstName" })
                if (Property(document.RootElement, name) is { } value && value.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString();
        }
        catch (System.Text.Json.JsonException) { }
        return null;
    }

    private static string? TextValue(System.Text.Json.JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (Property(root, name) is { } value && value.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString();
        return null;
    }

    private async Task<Dictionary<Guid, FailureTime>> FailureTimes(IReadOnlyCollection<IntegrationJob> jobs, CancellationToken cancellationToken)
    {
        if (jobs.Count == 0) return [];
        var jobIds = jobs.Select(x => x.Id).ToArray();
        return (await db.JobAttempts.AsNoTracking()
            .Where(x => x.TenantId == jobs.First().TenantId && jobIds.Contains(x.JobId) && !x.Succeeded)
            .GroupBy(x => x.JobId)
            .Select(group => new
            {
                JobId = group.Key,
                FirstFailedAt = group.Min(x => x.StartedAt),
                LastFailedAt = group.Max(x => x.CompletedAt ?? x.StartedAt)
            })
            .ToListAsync(cancellationToken))
            .ToDictionary(x => x.JobId, x => new FailureTime(x.FirstFailedAt, x.LastFailedAt));
    }

    private static JobSummaryView Summary(IntegrationJob x, FailureTime? failure = null, int batchCount = 1, DateTimeOffset? lastAttemptStartedAt = null) => new(
        x.Id, x.ConnectionId, x.JobType, Wire(x.Status), x.AttemptCount, x.MaxAttempts,
        x.AvailableAt, x.LastErrorCode, x.LastErrorSummary, x.CorrelationId,
        x.CreatedAt, x.StartedAt, x.CompletedAt, Marketplace(x.JobType), ExternalId(x.PayloadJson),
        failure?.FirstFailedAt, failure?.LastFailedAt,
        x.Status is JobStatus.Pending or JobStatus.RetryScheduled ? x.AvailableAt : null,
        Math.Max(1, batchCount), x.ProgressCurrent, x.ProgressTotal, x.ProgressPercent, x.ProgressLabel,
        x.ProgressReceived, x.ProgressProcessed, x.ProgressSkipped, x.ProgressFailed,
        lastAttemptStartedAt, x.HeartbeatAt);

    private static JobChangeView? Change(IntegrationJob job)
    {
        var type = job.JobType.ToUpperInvariant();
        if (type is MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.HepsiburadaOrderStatusSync) return new("Tarama türü", "Sipariş durum taraması", "Açık siparişlerin paket ve taşıma durumları kontrol edilerek yerel durum güncellendi.");
        if (type == MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation) return new("Tarama türü", "Trendyol kargo bilgi taraması", "Teslim edilen Trendyol paketlerinin kargo bilgileri salt okunur olarak yenilendi.");
        if (type == InvoicingJobTypes.InvoiceSubmit
            && (job.LastErrorCode == "EFATURAM_FISCAL_PAYLOAD_INVALID"
                || job.LastErrorCode == "EFATURAM_REQUEST_REJECTED"))
        {
            var detail = job.LastErrorSummary switch
            {
                "EFATURAM_RECIPIENT_TAX_ID_REQUIRED" => "Alıcı için gerekli vergi kimlik numarası bulunamadı. Bireysel e-Arşiv alıcılarında 11111111111 kullanılır; kurumsal alıcı için şirket VKN'si, e-Fatura alıcısı için gerçek TCKN gerekir. Eksik şirket veya alıcı bilgisi pazaryeri fatura adresine girilip sipariş eşitlendikten sonra yeniden deneyin. Bu fatura kesilmiş sayılmaz.",
                "EFATURAM_RECIPIENT_TAX_ID_PLACEHOLDER_NOT_ALLOWED" => "Bu eski denemede 11111111111 nedeniyle fatura gönderilmedi. Bireysel e-Arşiv alıcılarında bu değer kullanılır; kurumsal ve e-Fatura alıcılarında gerçek VKN/TCKN gerekir. Sipariş bilgilerini güncelleyip fatura denemesini yeniden kuyruğa alın. Fatura kesilmiş sayılmaz.",
                _ => "E-Faturam isteği kabul etmedi. Bu kayıt fatura olarak kesilmiş sayılmaz; hata ayrıntısındaki alıcı/fatura bilgileri düzeltilip güvenli yeniden deneme yapılmalıdır."
            };
            return new("İstek sonucu", "Fatura gönderilmedi", detail);
        }
        if (type is MarketplaceJobTypes.OrderReconciliation or MarketplaceJobTypes.ShopifyOrderReconciliation) return new("Tarama türü", "Kapsamlı sipariş taraması", "Yerel siparişler ile pazaryeri kayıtları karşılaştırıldı; durum ve paket farklılıkları düzeltildi.");
        if (type is MarketplaceJobTypes.OrderInvoiceReconciliation or MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation or MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation) return new("Tarama türü", "Paket fatura taraması", "Teslim edilmiş ve açık paketlerin pazaryeri fatura durumu kontrol edildi.");
        if (type is MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync) return new("Tarama türü", "Tam sipariş taraması", "Erişilebilen sipariş pencereleri taranarak eksik yerel kayıtlar tamamlandı.");
        if (type is MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync) return new("Yapılan değişiklik", "Sipariş senkronizasyonu", "Sipariş bilgileri pazaryerinden eşitlendi.");
        if (type is MarketplaceJobTypes.ReferenceSync or MarketplaceJobTypes.HepsiburadaReferenceSync)
            return new("Yapılan işlem", type == MarketplaceJobTypes.HepsiburadaReferenceSync ? "Hepsiburada kategori ve eşleştirme verisi" : "Trendyol kategori ve eşleştirme verisi", "Kategori, özellik ve eşleştirme referansları salt okunur yenilendi; dış platforma veri gönderilmedi.");
        if (type.Contains("PRODUCT", StringComparison.Ordinal) || type.Contains("CATALOG", StringComparison.Ordinal)) return new("Yapılan değişiklik", "Ürün senkronizasyonu", "Ürün bilgileri pazaryerinden okundu; dış yazma yapılmadı.");
        if (type.Contains("INVOICE", StringComparison.Ordinal)) return new("Yapılan değişiklik", "Fatura işlemi", "Fatura isteği pazaryerine gönderildi.");

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(job.PayloadJson);
            var root = document.RootElement;
            if (type.Contains("SHIPMENT_ACTION", StringComparison.Ordinal))
            {
                var action = StringValue(root, "action")?.ToUpperInvariant();
                var payload = StringValue(root, "payloadJson");
                using var nested = string.IsNullOrWhiteSpace(payload) ? null : System.Text.Json.JsonDocument.Parse(payload);
                var provider = nested is null ? null : StringValue(nested.RootElement, "cargoProvider");
                if (action == "CHANGE_CARGO_PROVIDER") return new("Yapılan değişiklik", "Kargo firması değişikliği", provider is null ? "Yeni firma bilgisi gönderilmedi." : $"Yeni firma: {provider}");
                if (action == "PICKING") return new("Yapılan değişiklik", "Sipariş işleme alındı", "Paket Trendyol’a işleme alma isteğiyle gönderildi.");
                if (!string.IsNullOrWhiteSpace(action)) return new("Yapılan değişiklik", action.Replace('_', ' '), "Paket işlemi Trendyol’a gönderildi.");
            }
        }
        catch (System.Text.Json.JsonException) { }
        return null;
    }

    private static string Marketplace(string jobType) => jobType.Contains("EFATURAM", StringComparison.OrdinalIgnoreCase)
        ? "Trendyol e-Faturam"
        : jobType.Contains("HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
            ? "Hepsiburada"
        : jobType.Contains("SHOPIFY", StringComparison.OrdinalIgnoreCase)
            ? "Shopify"
        : jobType.Contains("TRENDYOL", StringComparison.OrdinalIgnoreCase)
            ? "Trendyol"
            : "Ravencia";

    private static string? ExternalId(string payloadJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
            foreach (var name in new[] { "externalOrderId", "externalClaimId", "externalPackageId", "externalProductId", "externalVariantId" })
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                    return value.GetString();
        }
        catch (System.Text.Json.JsonException) { }
        return null;
    }

    private sealed record FailureTime(DateTimeOffset FirstFailedAt, DateTimeOffset LastFailedAt);

    private static bool TryParseStatus(string value, out JobStatus status) => Enum.TryParse(value.Replace("_", string.Empty, StringComparison.Ordinal), true, out status);
    private static string Wire(JobStatus status) => status switch
    {
        JobStatus.RetryScheduled => "RETRY_SCHEDULED",
        JobStatus.ManualReview => "MANUAL_REVIEW",
        _ => status.ToString().ToUpperInvariant()
    };
}
