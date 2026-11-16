using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Api.Common;
using MarketplaceHub.Api.Operations;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Api.Common.EndpointHelpers;

namespace MarketplaceHub.Api.Invoicing;

public static partial class InvoicingEndpoints
{
    public static IEndpointRouteBuilder MapInvoicingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").WithArgumentErrorsAnswered().WithGates(ApiGate.SignedIn, ApiGate.Working);

        // The same document-bound link with a .pdf file name, for marketplaces (n11) that only accept
        // invoice links ending in a document extension. The stored link stays the /content form.
        api.MapGet("/public/invoice-documents/{token}/" + InvoiceDocumentLink.PdfFileName, (string token, HttpContext http, IDataProtectionProvider protection, AppDbContext db, IInvoicingBillingService service) =>
            OpenPublicInvoiceDocumentAsync(token, http, protection, db, service)).RequireGate(ApiGate.Open).ProducesFile("application/pdf", "image/jpeg", "image/png");
        api.MapGet("/public/invoice-documents/{token}/content", (string token, HttpContext http, IDataProtectionProvider protection, AppDbContext db, IInvoicingBillingService service) =>
            OpenPublicInvoiceDocumentAsync(token, http, protection, db, service)).RequireGate(ApiGate.Open).ProducesFile("application/pdf", "image/jpeg", "image/png");
        static async Task<IResult> OpenPublicInvoiceDocumentAsync(string token, HttpContext http, IDataProtectionProvider protection, AppDbContext db, IInvoicingBillingService service)
        {
            if (!InvoiceDocumentLink.TryRead(protection, token, out var tenantId, out var invoiceId, out var documentId)) return Results.NotFound();
            var expectedPath = $"/api/v1/public/invoice-documents/{token}/content";
            var permanentUrl = await db.InvoiceDocuments.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.InvoiceId == invoiceId && x.Id == documentId)
                .Select(x => x.PermanentUrl).SingleOrDefaultAsync(http.RequestAborted);
            if (!Uri.TryCreate(permanentUrl, UriKind.Absolute, out var uri) || uri.AbsolutePath != expectedPath) return Results.NotFound();
            if (await InvoiceDocumentLink.IsWithdrawnAsync(db, tenantId, invoiceId, http.RequestAborted)) return Results.NotFound();
            LogPublicInvoiceDocumentOpened(http, "invoice", tenantId, documentId);
            try { return Stream(await service.OpenDocumentAsync(tenantId, invoiceId, documentId, http.RequestAborted), http); }
            catch (FileNotFoundException) { return Results.NotFound(); }
        }
        api.MapGet("/public/manual-invoice-documents/{token}/content", OpenPublicManualOrderInvoiceDocumentAsync).RequireGate(ApiGate.Open).ProducesFile("application/pdf");
        // The same manual document under a .pdf file name, for n11, Pazarama and PTT AVM, which take only links that end
        // in a document extension (ManualOrderInvoiceDocumentLink.PdfAlias). The stored link stays the /content form.
        api.MapGet("/public/manual-invoice-documents/{token}/" + InvoiceDocumentLink.PdfFileName, OpenPublicManualOrderInvoiceDocumentAsync).RequireGate(ApiGate.Open).ProducesFile("application/pdf");


        api.MapGet("/invoices", async (HttpContext http, IInvoicingBillingService service, int? limit, string? after, string? status) => Tenant(http) is { } tenant ? Results.Ok(await service.ListAsync(tenant.TenantId, PageSize(limit), after, status, http.RequestAborted)) : Unauthorized(http)).Produces<PageResult<InvoiceListView>>();
        api.MapGet("/invoice-workspace/summary", async (HttpContext http, IInvoicingBillingService service) => Tenant(http) is { } tenant ? Results.Ok(await service.WorkspaceSummaryAsync(tenant.TenantId, http.RequestAborted)) : Unauthorized(http)).Produces<InvoiceWorkspaceSummaryView>();
        // Unified paging (PagingContract): limit wins over pageSize (read the same way: 20, 50, 100 or 200) and after, the
        // nextCursor of the previous page, over pageNumber. Every sort ends on the package id, so the order is total.
        api.MapGet("/invoice-workspace/page", async (HttpContext http, IInvoicingBillingService service, int? pageNumber, int? pageSize, string? tab, string? search, string? platformCodes, string? shipmentStatus, string? cargoProviderName, string? invoiceStatus, string? invoiceAction, DateTimeOffset? from, DateTimeOffset? to, bool? providerHasCredential, string? sort, int? limit, string? after) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var size = limit ?? pageSize ?? 20;
            var cursorPage = OffsetCursor.PageIndex(after, size is 20 or 50 or 100 or 200 ? size : 20);
            var filter = new InvoiceWorkspacePageQuery(
                cursorPage is { } index ? index + 1 : pageNumber ?? 1,
                size,
                tab ?? "UNINVOICED",
                search,
                platformCodes?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                shipmentStatus,
                cargoProviderName,
                invoiceStatus,
                invoiceAction,
                from,
                to,
                providerHasCredential ?? false,
                sort);
            var page = MarketplaceHub.Api.Security.InvoiceLinkVisibility.ForRole(await service.WorkspacePageAsync(tenant.TenantId, filter, http.RequestAborted), tenant.Role);
            return Results.Ok(page with { HasMore = page.PageNumber < page.TotalPages, NextCursor = page.PageNumber < page.TotalPages ? OffsetCursor.Encode(page.PageNumber * page.PageSize) : null });
        }).Produces<InvoiceWorkspacePageView>();
        api.MapPut("/invoice-workspace/hidden", UpdateInvoiceWorkspaceHiddenAsync).RequireIdempotencyKey().Produces<InvoiceWorkspaceHiddenView>();
        api.MapPut("/invoice-workspace/manual-status", UpdateInvoiceWorkspaceManualStatusAsync).RequireIdempotencyKey().Produces<InvoiceWorkspaceManualStatusView>();
        api.MapPost("/invoice-workspace/orders/{orderId:guid}/reconcile-jobs", async (Guid orderId, HttpContext http, IInvoicingBillingService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            if (RequireIdempotency(http) is { } failure) return failure;
            var result = await service.EnqueueOrderInvoiceReconciliationAsync(tenant.TenantId, orderId,
                http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted);
            return result.Succeeded ? Results.Accepted(value: new { jobId = result.Value }) : Problem(http, result.Error!);
        }).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);
        api.MapPost("/invoice-workspace/manual-documents", CreateManualOrderInvoiceDocumentAsync).DisableAntiforgery().RequireIdempotencyKey().Produces<ManualInvoiceDocumentView>();
        // 202 with the queued deliveries; 200 when every package already confirmed the document and nothing was queued.
        api.MapPost("/invoice-workspace/manual-documents/{id:guid}/delivery-jobs", QueueManualOrderInvoiceDeliveryAsync).RequireGate(ApiGate.Elevated).RequireIdempotencyKey()
            .Produces<ManualInvoiceDeliveryConfirmedView>().Produces<ManualInvoiceDeliveryQueuedView>(StatusCodes.Status202Accepted);
        api.MapGet("/invoice-workspace/manual-documents/{id:guid}/content", OpenManualOrderInvoiceDocumentAsync).RequireGate(ApiGate.Working).ProducesFile("application/pdf");
        api.MapPost("/invoice-workspace/orders/{orderId:guid}/confirm-no-remote-invoice", async (Guid orderId, HttpContext http, IInvoicingBillingService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            if (RequireIdempotency(http) is { } missingKey) return missingKey;
            // The body is optional: {"searchedByOrderNumber": true} states the portal was searched by the order number (D-2).
            var searchedByOrderNumber = false;
            if (http.Request.ContentLength is > 0)
            {
                try
                {
                    using var body = await System.Text.Json.JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                    searchedByOrderNumber = body.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                        && body.RootElement.TryGetProperty("searchedByOrderNumber", out var flag) && flag.ValueKind == System.Text.Json.JsonValueKind.True;
                }
                catch (System.Text.Json.JsonException) { return Results.BadRequest(); }
            }
            var result = await service.ConfirmNoRemoteInvoiceAsync(tenant.TenantId, orderId, http.TraceIdentifier, http.RequestAborted, searchedByOrderNumber);
            return result.Succeeded ? Results.Ok(new { confirmed = true }) : Problem(http, result.Error!);
        }).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<InvoiceConfirmedView>();
        api.MapPost("/invoice-workspace/orders/{orderId:guid}/confirm-marketplace-delivered", async (Guid orderId, HttpContext http, IInvoicingBillingService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            if (RequireIdempotency(http) is { } missingKey) return missingKey;
            var result = await service.ConfirmMarketplaceDeliveredAsync(tenant.TenantId, orderId, http.TraceIdentifier, http.RequestAborted);
            return result.Succeeded ? Results.Ok(new { confirmed = true }) : Problem(http, result.Error!);
        }).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<InvoiceConfirmedView>();
        api.MapPost("/invoice-workspace/orders/{orderId:guid}/replace-cancelled-invoice", async (Guid orderId, HttpContext http, IInvoicingBillingService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            if (RequireIdempotency(http) is { } missingKey) return missingKey;
            var result = await service.ReplaceCancelledInvoiceAsync(tenant.TenantId, orderId, http.TraceIdentifier, http.RequestAborted);
            return result.Succeeded ? Results.Ok(new { replaced = true }) : Problem(http, result.Error!);
        }).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<InvoiceReplacedView>();
        api.MapPost("/invoice-workspace/preview", async (InvoiceWorkspacePreviewRequest request, HttpContext http, IInvoicingBillingService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var result = await service.PreviewWorkspaceInvoicesAsync(tenant.TenantId, request, http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : Problem(http, result.Error!);
        }).Produces<IReadOnlyList<InvoiceWorkspacePreviewItem>>().Idempotent(WriteReasons.ReadOnly);
        api.MapPost("/invoice-workspace/confirm", ConfirmWorkspaceAsync).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<InvoiceWorkspaceConfirmResult>();
        api.MapPost("/invoices", async (CreateInvoiceCommand command, HttpContext http, IInvoicingBillingService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Created(await service.CreateDraftAsync(tenant.TenantId, command, http.Request.Headers["Idempotency-Key"].ToString(), http.RequestAborted), "/api/v1/invoices") : MissingContext(http)).RequireIdempotencyKey().Produces<InvoiceDetailView>(StatusCodes.Status201Created);
        api.MapGet("/invoices/{id:guid}", async (Guid id, HttpContext http, IInvoicingBillingService service) => Tenant(http) is { } tenant ? WithEtag(http, await service.GetAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http)).Produces<InvoiceDetailView>();
        api.MapPost("/invoices/{id:guid}/validate", async (Guid id, HttpContext http, IInvoicingBillingService service) => Tenant(http) is { } tenant ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.ValidateAsync(tenant.TenantId, id, version, http.RequestAborted), x => x.Version) : failure! : Unauthorized(http)).Produces<InvoiceDetailView>().IfMatch().Idempotent(WriteReasons.VersionGuarded);
        api.MapPost("/invoices/{id:guid}/submit-jobs", async (Guid id, ConfirmedAction command, HttpContext http, IInvoicingBillingService service, AppDbContext db) => await EnqueueProtected(id, command, http, db, (tenant, version, key) => service.EnqueueSubmitAsync(tenant, id, version, key, http.TraceIdentifier, http.RequestAborted), requireSensitiveConfirmation: false)).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);
        api.MapPost("/invoices/{id:guid}/stage-capability-probe-jobs", async (Guid id, HttpContext http, IInvoicingBillingService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Accepted(await service.EnqueueStageCapabilityProbeAsync(tenant.TenantId, id, version, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted)) : failure! : MissingContext(http)).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted).IfMatch();
        api.MapPost("/invoices/{id:guid}/reconcile-jobs", async (Guid id, HttpContext http, IInvoicingBillingService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueReconcileAsync(tenant.TenantId, id, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted)) : MissingContext(http)).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);
        api.MapPost("/invoices/{id:guid}/marketplace-delivery-jobs", async (Guid id, ConfirmedAction command, HttpContext http, IInvoicingBillingService service, AppDbContext db) => await EnqueueProtected(id, command, http, db, (tenant, _, key) => service.EnqueueDeliveryAsync(tenant, id, key, http.TraceIdentifier, http.RequestAborted), false)).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);
        // "Tekrar Dene": the document fetch (when missing) and the delivery of the invoice already issued; no new invoice.
        api.MapPost("/invoices/{id:guid}/delivery-retry-jobs", async (Guid id, ConfirmedAction command, HttpContext http, IInvoicingBillingService service, AppDbContext db) => await EnqueueProtected(id, command, http, db, (tenant, _, key) => service.RetryDeliveryAsync(tenant, id, key, http.TraceIdentifier, http.RequestAborted), false)).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);
        // Operator-only: removes (Trendyol) or confirms the removal of (Hepsiburada) the link a cancelled invoice left on one package.
        // It takes an explicit confirmation (no password: a signed-in session with the role is enough) and runs under the
        // server's budget, so a closed tab does not leave the state "being removed".
        api.MapPost("/invoices/{id:guid}/marketplace-link-revocations/{stateId:guid}", async (Guid id, Guid stateId, LinkRevocationRequest request, HttpContext http, IInvoicingBillingService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            if (RequireIdempotency(http) is { } missingKey) return missingKey;
            if (request is not { Confirmed: true }) return Problem(http, new("EXPLICIT_CONFIRMATION_REQUIRED", "Fatura linkini kaldırmak için açık onay gerekir.", 422));
            var result = await service.RevokeMarketplaceLinkAsync(tenant.TenantId, id, stateId, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : Problem(http, result.Error!);
        }).WithServerBudget().RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<MarketplaceLinkRevocationView>();
        api.MapPost("/invoices/{id:guid}/cancellation-jobs", async (Guid id, ConfirmedAction command, HttpContext http, IInvoicingBillingService service, AppDbContext db) => await EnqueueProtected(id, command, http, db, (tenant, version, key) => service.EnqueueCancellationAsync(tenant, id, version, key, http.TraceIdentifier, http.RequestAborted))).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);
        api.MapPost("/invoices/{id:guid}/documents/manual", UploadManualInvoiceDocumentAsync).DisableAntiforgery().RequireIdempotencyKey()
            .Produces<InvoiceDocumentUploadView>(StatusCodes.Status201Created).Produces<InvoiceDocumentUploadView>();
        api.MapPut("/invoices/{id:guid}/shopify-status", UpdateShopifyInvoiceStatusAsync).RequireIdempotencyKey().Produces<ShopifyInvoiceStatusView>();
        api.MapGet("/invoices/{invoiceId:guid}/documents/latest/content", async (Guid invoiceId, HttpContext http, AppDbContext db, IInvoicingBillingService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            // Provider documents are typed as PDF, whereas manually uploaded
            // PDF files are intentionally stored as MANUAL_UPLOAD. Both are
            // valid invoice documents and the latest one must be downloadable
            // from the order card.
            var documentId = await db.InvoiceDocuments.AsNoTracking()
                .Where(x => x.TenantId == tenant.TenantId && x.InvoiceId == invoiceId)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => (Guid?)x.Id)
                .FirstOrDefaultAsync(http.RequestAborted);
            return documentId is { } id ? Stream(await service.OpenDocumentAsync(tenant.TenantId, invoiceId, id, http.RequestAborted), http) : Problem(http, new("INVOICE_PDF_NOT_READY", "Faturanın PDF belgesi henüz arşivlenmedi.", 404));
        }).RequireGate(ApiGate.Working).ProducesFile("application/pdf", "image/jpeg", "image/png");
        api.MapGet("/invoices/{invoiceId:guid}/documents/{documentId:guid}/content", async (Guid invoiceId, Guid documentId, HttpContext http, IInvoicingBillingService service) => Tenant(http) is { } tenant ? Stream(await service.OpenDocumentAsync(tenant.TenantId, invoiceId, documentId, http.RequestAborted), http) : Unauthorized(http)).RequireGate(ApiGate.Working).ProducesFile("application/pdf", "image/jpeg", "image/png");
        return endpoints;
    }

    private sealed record ShopifyInvoiceStatusCommand(string Status);
    private sealed record LinkRevocationRequest(bool Confirmed);

    // Confirms the workspace selection. Queuing the batch is bounded by the server's lifetime only: not by the
    // browser (a closed tab must not stop it midway) and not by the inline budget (a large batch may take longer
    // than it). The budget starts afterwards and bounds only the inline runs (see InlineExecutionBudget).
    internal static async Task<IResult> ConfirmWorkspaceAsync(InvoiceWorkspacePreviewConfirmRequest request, HttpContext http, IInvoicingBillingService service, IInvoiceInlineRunner inline)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (RequireIdempotency(http) is { } failure) return failure;
        var lifetime = http.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        ServiceResult<InvoiceWorkspaceConfirmResult> result;
        try
        {
            result = await service.ConfirmWorkspaceInvoicesAsync(tenant.TenantId, request,
                http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, lifetime.ApplicationStopping);
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return Problem(http, new("INLINE_BUDGET_EXHAUSTED", "Sunucu yeniden başlatılırken işlem yarıda kaldı; sonucu iş listesinden kontrol edin.", 503));
        }
        if (!result.Succeeded) return Problem(http, result.Error!);
        using var budget = new InlineExecutionBudget(lifetime, http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System);
        // These are immediate writes: the first queued jobs run now and are answered with their real outcome;
        // the rest stay QUEUED with their job ids for the worker.
        var items = await budget.RunAsync(result.Value!.Items,
            item => item.Status == "QUEUED" && item.JobId is not null && item.InvoiceId is not null,
            async (item, cancellationToken) =>
            {
                var outcome = await inline.RunAsync(tenant.TenantId, item.InvoiceId!.Value, item.JobId!.Value, budget.Deadline, cancellationToken);
                return item with { Status = outcome.Status == "IN_PROGRESS" ? "QUEUED" : outcome.Status, Message = outcome.Message };
            });
        return Results.Ok(new InvoiceWorkspaceConfirmResult(items));
    }

    internal sealed record InvoiceWorkspaceManualStatusCommand(IReadOnlyList<Guid> PackageIds, string Status);
    private sealed record InvoiceWorkspaceHiddenCommand(IReadOnlyList<Guid> PackageIds, bool Hidden);

    private static string? DetectInvoiceMimeType(byte[] bytes) => bytes.Length >= 5 && bytes[..5].SequenceEqual("%PDF-"u8.ToArray()) ? "application/pdf"
        : bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF }) ? "image/jpeg"
        : bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "image/png"
        : null;

    private static async Task<IResult> EnqueueProtected(Guid invoiceId, ConfirmedAction command, HttpContext http, AppDbContext db, Func<Guid, long, string, Task<ServiceResult<Guid>>> enqueue, bool requireVersion = true, bool requireSensitiveConfirmation = true)
    {
        var tenant = Tenant(http); if (tenant is null) return Unauthorized(http);
        var missingKey = RequireIdempotency(http); if (missingKey is not null) return missingKey;
        var connection = await (from invoice in db.Invoices.AsNoTracking()
                                join provider in db.PlatformConnections.AsNoTracking() on new { invoice.TenantId, Id = invoice.ProviderConnectionId } equals new { provider.TenantId, provider.Id }
                                where invoice.TenantId == tenant.TenantId && invoice.Id == invoiceId
                                select provider).SingleOrDefaultAsync(http.RequestAborted);
        if (connection is null) return Problem(http, new("RESOURCE_NOT_FOUND", "Invoice not found.", 404));
        if (!requireSensitiveConfirmation || !IntegrationRuntimePolicy.RequiresSensitiveConfirmation(connection))
        {
            long stageVersion = 0;
            if (requireVersion && !TryIfMatch(http, out stageVersion, out var stageFailure)) return stageFailure!;
            return Accepted(await enqueue(tenant.TenantId, stageVersion, http.Request.Headers["Idempotency-Key"].ToString()));
        }
        // A live provider connection takes an explicit confirmation; no password: the signed-in session with the role is enough.
        if (!command.Confirmed) return Problem(http, new("EXPLICIT_CONFIRMATION_REQUIRED", "Dış mali işlem için açık onay zorunludur.", 422));
        long version = 0;
        if (requireVersion && !TryIfMatch(http, out version, out var failure)) return failure!;
        return Accepted(await enqueue(tenant.TenantId, version, http.Request.Headers["Idempotency-Key"].ToString()));
    }

    private static IResult Stream(ServiceResult<(Stream Content, string MimeType, string FileName)> result, HttpContext http) { if (!result.Succeeded) return Problem(http, result.Error!); http.Response.Headers.CacheControl = "private, no-store"; return Results.File(result.Value.Content, result.Value.MimeType, result.Value.FileName, enableRangeProcessing: false); }
    public sealed record ConfirmedAction(bool Confirmed);
}
