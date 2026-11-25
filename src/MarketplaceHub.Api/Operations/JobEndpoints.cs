using MarketplaceHub.Api.Common;
using MarketplaceHub.Application;
using static MarketplaceHub.Api.Common.EndpointHelpers;

namespace MarketplaceHub.Api.Operations;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1/jobs").WithGates(ApiGate.SignedIn, ApiGate.Elevated);
        api.MapGet("", async (HttpContext http, IJobOperationsService service, string? status, DateTimeOffset? since, DateTimeOffset? dateFrom, string? jobTypes, int? limit, string? platform, string? platforms, string? ids) =>
            Tenant(http) is { } tenant
                ? Results.Ok(await service.ListAsync(
                    tenant.TenantId,
                    new JobListQuery(status, EndpointHelpers.Unified(dateFrom, since), List(jobTypes), limit, Platforms: Platforms(platform, platforms), Ids: Ids(ids)),
                    http.RequestAborted))
                : Unauthorized(http)).Produces<IReadOnlyList<JobSummaryView>>();
        // Cursor-paged list for the operations page: { items, nextCursor, hasMore }; after continues a previous page.
        // platform (or platforms, a comma list) keeps the jobs of those connection platforms; search keeps the jobs one
        // identifier names (job id, correlation id, marketplace id), so a job is found by its number at any age.
        // groupBy=model: a model's product jobs of one send come as one row (JobModelGroupPolicy); limit then counts rows.
        api.MapGet("/page", async (HttpContext http, IJobOperationsService service, string? status, DateTimeOffset? since, DateTimeOffset? dateFrom, string? jobTypes, int? limit, string? after, string? platform, string? platforms, string? search, string? groupBy) =>
            Tenant(http) is { } tenant
                ? Result(http, await service.PageAsync(
                    tenant.TenantId,
                    new JobListQuery(status, EndpointHelpers.Unified(dateFrom, since), List(jobTypes), limit, after, Platforms(platform, platforms), Search: search,
                        GroupByModel: string.Equals(groupBy, "model", StringComparison.OrdinalIgnoreCase)),
                    http.RequestAborted))
                : Unauthorized(http)).Produces<PageResult<JobSummaryView>>();
        // The notification center's background notices: reads failing run after run, approvals that ran out of time.
        // Each listed notice is also kept in the notification center for the whole tenant (the server records them; a
        // browser may not), once per notice and revision.
        api.MapGet("/attention", async (HttpContext http, IAttentionNoticeService service, IPanelNotificationService notifications, ILoggerFactory loggers) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var notices = await service.ListAsync(tenant.TenantId, http.RequestAborted);
            if (notices.Count > 0)
            {
                try { await notifications.SaveAsync(tenant.TenantId, tenant.UserId, [.. notices.Select(AttentionNoticePolicy.CenterInput)], http.RequestAborted); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    loggers.CreateLogger("MarketplaceHub.Api.AttentionNotices").LogWarning(exception, "Attention notices could not be recorded in the notification center");
                }
            }
            return Results.Ok(notices);
        }).Produces<IReadOnlyList<AttentionNoticeView>>();
        api.MapGet("/counts", async (HttpContext http, IJobOperationsService service, DateTimeOffset? since, DateTimeOffset? dateFrom, string? platform, string? platforms) =>
            Tenant(http) is { } tenant
                ? Results.Ok(await service.CountAsync(tenant.TenantId, EndpointHelpers.Unified(dateFrom, since), Platforms(platform, platforms), http.RequestAborted))
                : Unauthorized(http)).Produces<IReadOnlyList<JobCountView>>();
        api.MapGet("/metrics", async (HttpContext http, IJobOperationsService service, int? hours, string? platform, string? platforms) =>
            Tenant(http) is { } tenant
                ? Results.Ok(await service.MetricsAsync(tenant.TenantId, TimeSpan.FromHours(Math.Clamp(hours ?? 24, 1, 168)), Platforms(platform, platforms), http.RequestAborted))
                : Unauthorized(http)).Produces<IReadOnlyList<JobTypeMetricView>>();
        api.MapGet("/metrics/lanes", async (HttpContext http, IQueueHealthService service, int? hours) =>
            Tenant(http) is { } tenant
                ? Results.Ok(await service.LanesAsync(tenant.TenantId, TimeSpan.FromHours(Math.Clamp(hours ?? 24, 1, 168)), http.RequestAborted))
                : Unauthorized(http)).Produces<IReadOnlyList<LaneMetricView>>();
        api.MapGet("/metrics/quota", async (HttpContext http, IQueueHealthService service) =>
            Tenant(http) is { } tenant
                ? Results.Ok(await service.QuotaAsync(tenant.TenantId, http.RequestAborted))
                : Unauthorized(http)).Produces<IReadOnlyList<QuotaUsageView>>();
        api.MapGet("/{id:guid}", async (Guid id, HttpContext http, IJobOperationsService service) =>
            Tenant(http) is { } tenant
                ? Result(http, await service.GetAsync(tenant.TenantId, id, http.RequestAborted))
                : Unauthorized(http)).Produces<JobDetailView>();
        // Gidenler ve Değişenler: what a price/stock job sent line by line (its send log, or the send jobs a stock scan or
        // dispatch queued) and what a product import changed. Read-only. The lines: { items, nextCursor, hasMore,
        // totalCount, page, pageSize }, newest first (then by id). Cursor paging: limit 1-50 (default 50) and after; page
        // numbers: page (1-based) and pageSize 1-200 (default 25), when either is given. search names a barcode, stock
        // code, model code or title; groups (a comma list of the summary's counts: applied, unchanged, pending, rejected,
        // uncertain, superseded, priceChanged, stockChanged) keeps their lines. The summary: the list's mode, why it is
        // empty when the job recorded why, its counts and the send jobs it queued.
        api.MapGet("/{id:guid}/changes", async (Guid id, HttpContext http, IJobChangesService service, int? limit, string? after, string? search, int? page, int? pageSize, string? groups) =>
            Tenant(http) is { } tenant
                ? Result(http, await service.PageAsync(tenant.TenantId, id, ChangesQuery(limit, after, search, page, pageSize, groups), http.RequestAborted))
                : Unauthorized(http)).Produces<JobChangesPageView>();
        // The same lines as an Excel workbook (one row per value a line carried), search and groups as for the list.
        api.MapGet("/{id:guid}/changes/export.xlsx", async (Guid id, HttpContext http, IJobChangesService service, string? search, string? groups) =>
            Tenant(http) is { } tenant
                ? File(http, await service.ExportAsync(tenant.TenantId, id, ChangesQuery(null, null, search, null, null, groups), http.RequestAborted))
                : Unauthorized(http)).WithExportLimit().ProducesFile(Catalog.ProductTransferEndpoints.XlsxMediaType);
        // Çekilen Kayıtlar: the products a product read pulled, as it stored them (problems first). { kind, items, page,
        // pageSize, totalCount, storedCount, receivedCount, omittedCount, counts }; storedCount below receivedCount: the
        // read kept only part of its list. onlyProblems keeps the skipped and failed; search names a model code or barcode.
        api.MapGet("/{id:guid}/read-records", async (Guid id, HttpContext http, IJobReadRecordsService service, bool? onlyProblems, string? search, int? page, int? pageSize) =>
            Tenant(http) is { } tenant
                ? Result(http, await service.PageAsync(tenant.TenantId, id, ReadRecordsQuery(onlyProblems, search, page, pageSize), http.RequestAborted))
                : Unauthorized(http)).Produces<JobReadRecordsPageView>();
        api.MapGet("/{id:guid}/read-records/export.xlsx", async (Guid id, HttpContext http, IJobReadRecordsService service, bool? onlyProblems, string? search) =>
            Tenant(http) is { } tenant
                ? File(http, await service.ExportAsync(tenant.TenantId, id, ReadRecordsQuery(onlyProblems, search, null, null), http.RequestAborted))
                : Unauthorized(http)).WithExportLimit().ProducesFile(Catalog.ProductTransferEndpoints.XlsxMediaType);
        api.MapGet("/{id:guid}/changes/summary", async (Guid id, HttpContext http, IJobChangesService service) =>
            Tenant(http) is { } tenant
                ? Result(http, await service.SummaryAsync(tenant.TenantId, id, http.RequestAborted))
                : Unauthorized(http)).Produces<JobChangesSummaryView>();
        api.MapPost("/{id:guid}/retry", async (Guid id, HttpContext http, IJobOperationsService service) =>
            Tenant(http) is { } tenant
                ? RequireIdempotency(http) ?? Result(http, await service.RetryAsync(tenant.TenantId, id, http.RequestAborted))
                : Unauthorized(http)).RequireIdempotencyKey().Produces<JobDetailView>();
        api.MapPost("/{id:guid}/invoice-delivery-once", async (Guid id, OneTimeInvoiceDeliveryAction command, HttpContext http, IJobOperationsService service) =>
        {
            var tenant = Tenant(http);
            if (tenant is null) return Unauthorized(http);
            if (RequireIdempotency(http) is { } idempotencyFailure) return idempotencyFailure;
            if (!command.Confirmed) return Problem(http, new("EXPLICIT_CONFIRMATION_REQUIRED", "Bu tek seferlik dış fatura iletimi için açık onay zorunludur.", 422));
            if (!OneTimeInvoiceDeliveryPolicy.IsAuthorizedTarget(command.OrderNumber)) return Problem(http, new("ONE_TIME_INVOICE_ORDER_NOT_AUTHORIZED", "Tek seferlik fatura iletimi yalnızca yetkilendirilmiş test siparişleri için yetkilendirildi.", 403));
            var result = await service.EnqueueOneTimeInvoiceDeliveryAsync(
                tenant.TenantId,
                id,
                command.OrderNumber,
                http.Request.Headers["Idempotency-Key"].ToString(),
                http.TraceIdentifier,
                http.RequestAborted);
            return Result(http, result);
        }).RequireIdempotencyKey().Produces<JobDetailView>();
        api.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext http, IJobOperationsService service) =>
            Tenant(http) is { } tenant
                ? RequireIdempotency(http) ?? Result(http, await service.CancelAsync(tenant.TenantId, id, http.RequestAborted))
                : Unauthorized(http)).RequireIdempotencyKey().Produces<JobDetailView>();
        return endpoints;
    }

    // page 1-100000 and pageSize 1-200 are checked (400 outside); pageSize defaults to 25 in the service.
    private static void CheckPage(int? page, int? pageSize)
    {
        if (page is not null) EndpointHelpers.PageNumber(page);
        if (pageSize is not null) EndpointHelpers.PageSize(pageSize);
    }

    private static JobChangesQuery ChangesQuery(int? limit, string? after, string? search, int? page, int? pageSize, string? groups)
    {
        CheckPage(page, pageSize);
        return new(limit, after, search, page, pageSize, List(groups));
    }

    private static JobReadRecordsQuery ReadRecordsQuery(bool? onlyProblems, string? search, int? page, int? pageSize)
    {
        CheckPage(page, pageSize);
        return new(onlyProblems == true, search, page, pageSize);
    }

    // A workbook the service wrote to a temporary file: streamed to the browser, the file deleted when it is sent.
    private static IResult File(HttpContext http, ServiceResult<JobExportFile> result)
    {
        if (!result.Succeeded) return Security.ApiProblems.Of(http, result.Error!);
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(result.Value!.Content, Catalog.ProductTransferEndpoints.XlsxMediaType, result.Value.FileName);
    }

    // ids: a comma list of job ids; anything that is not an id is left out.
    public static IReadOnlyList<Guid>? Ids(string? value) => List(value)?.Select(part => Guid.TryParse(part, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Take(JobListQuery.MaximumIds).ToArray();
    public static IReadOnlyList<string>? Platforms(string? platform, string? platforms)
    {
        var codes = (List(platform) ?? []).Concat(List(platforms) ?? []).Select(code => code.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Take(20).ToArray();
        return codes.Length == 0 ? null : codes;
    }

    public sealed record OneTimeInvoiceDeliveryAction(bool Confirmed, string OrderNumber);
}
