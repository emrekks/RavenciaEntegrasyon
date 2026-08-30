using MarketplaceHub.Application;
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
        var batchCounts = jobs.GroupBy(job => job.CorrelationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return jobs.Select(job => Summary(job, failures.GetValueOrDefault(job.Id), batchCounts.GetValueOrDefault(job.CorrelationId, 1))).ToList();
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

        job.Status = JobStatus.RetryScheduled;
        job.AvailableAt = timeProvider.GetUtcNow();
        job.CompletedAt = null;
        job.LeaseTokenHash = null;
        job.LeaseExpiresAt = null;
        job.HeartbeatAt = null;
        job.MaxAttempts = Math.Max(job.MaxAttempts, job.AttemptCount + 1);
        job.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<JobDetailView>.Ok(await DetailAsync(job, cancellationToken));
    }

    public async Task<ServiceResult<JobDetailView>> CancelAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == jobId, cancellationToken);
        if (job is null) return ServiceResult<JobDetailView>.Fail("JOB_NOT_FOUND", "Job bulunamadı.", 404);
        if (job.Status == JobStatus.Leased) return ServiceResult<JobDetailView>.Fail("JOB_ALREADY_RUNNING", "Çalışan job lease süresi bitmeden iptal edilemez.", 409);
        if (job.Status is JobStatus.Succeeded or JobStatus.Dead or JobStatus.Cancelled)
            return ServiceResult<JobDetailView>.Fail("JOB_TERMINAL", "Terminal durumdaki job iptal edilemez.", 409);

        job.Status = JobStatus.Cancelled;
        job.CompletedAt = timeProvider.GetUtcNow();
        job.LastErrorCode = "CANCELLED_BY_OPERATOR";
        job.LastErrorSummary = "Job kullanıcı tarafından iptal edildi.";
        job.Version++;
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
        return new JobDetailView(Summary(job, failureTimes, relatedJobs.Count), attempts, currentOrder, Change(job), relatedOrders);
    }

    private async Task<JobOrderContextView?> OrderContext(IntegrationJob job, CancellationToken cancellationToken)
    {
        Guid? orderId = null;
        Guid? packageId = null;
        Guid? claimId = null;
        string? externalOrderId = null;
        string? externalPackageId = null;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(job.PayloadJson);
            var root = document.RootElement;
            orderId = GuidValue(root, "orderId");
            packageId = GuidValue(root, "packageId");
            claimId = GuidValue(root, "claimId");
            externalOrderId = StringValue(root, "externalOrderId");
            externalPackageId = StringValue(root, "externalPackageId");
        }
        catch (System.Text.Json.JsonException) { }

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

    private static Guid? GuidValue(System.Text.Json.JsonElement root, string name) =>
        Property(root, name) is { } value && value.ValueKind == System.Text.Json.JsonValueKind.String && Guid.TryParse(value.GetString(), out var result)
            ? result
            : null;

    private static string? StringValue(System.Text.Json.JsonElement root, string name) =>
        Property(root, name) is { } value && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

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

    private static JobSummaryView Summary(IntegrationJob x, FailureTime? failure = null, int batchCount = 1) => new(
        x.Id, x.ConnectionId, x.JobType, Wire(x.Status), x.AttemptCount, x.MaxAttempts,
        x.AvailableAt, x.LastErrorCode, x.LastErrorSummary, x.CorrelationId,
        x.CreatedAt, x.StartedAt, x.CompletedAt, Marketplace(x.JobType), ExternalId(x.PayloadJson),
        failure?.FirstFailedAt, failure?.LastFailedAt,
        x.Status is JobStatus.Pending or JobStatus.RetryScheduled ? x.AvailableAt : null,
        Math.Max(1, batchCount));

    private static JobChangeView? Change(IntegrationJob job)
    {
        var type = job.JobType.ToUpperInvariant();
        if (type.Contains("ORDER_SYNC", StringComparison.Ordinal)) return new("Yapılan değişiklik", "Sipariş senkronizasyonu", "Sipariş bilgileri pazaryerinden eşitlendi.");
        if (type.Contains("REFERENCE_SYNC", StringComparison.Ordinal)) return new("Yapılan değişiklik", "Referans verisi senkronizasyonu", "Kategori, marka veya özellik verileri güncellendi.");
        if (type.Contains("PRODUCT", StringComparison.Ordinal) || type.Contains("CATALOG", StringComparison.Ordinal)) return new("Yapılan değişiklik", "Ürün senkronizasyonu", "Ürün bilgileri pazaryerine gönderildi.");
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
