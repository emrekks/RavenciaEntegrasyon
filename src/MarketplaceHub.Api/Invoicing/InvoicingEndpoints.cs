using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Api.Operations;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Invoicing;

public static class InvoicingEndpoints
{
    public static IEndpointRouteBuilder MapInvoicingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").AddEndpointFilter(async (context, next) => { try { return await next(context); } catch (ArgumentException exception) { var (code, message) = MarketplaceHub.Api.Security.RequestArgumentError.From(exception); return Problem(context.HttpContext, new(code, message, 400)); } });

        // The same document-bound link with a .pdf file name, for marketplaces (n11) that only accept
        // invoice links ending in a document extension. The stored link stays the /content form.
        api.MapGet("/public/invoice-documents/{token}/" + InvoiceDocumentLink.PdfFileName, (string token, HttpContext http, IDataProtectionProvider protection, AppDbContext db, IInvoicingBillingService service) =>
            OpenPublicInvoiceDocumentAsync(token, http, protection, db, service));
        api.MapGet("/public/invoice-documents/{token}/content", (string token, HttpContext http, IDataProtectionProvider protection, AppDbContext db, IInvoicingBillingService service) =>
            OpenPublicInvoiceDocumentAsync(token, http, protection, db, service));
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
        api.MapGet("/public/manual-invoice-documents/{token}/content", OpenPublicManualOrderInvoiceDocumentAsync);

        api.MapGet("/billing/invoice-policies/{connectionId:guid}", async (Guid connectionId, HttpContext http, IInvoicingBillingService service) => Tenant(http) is { } tenant ? WithEtag(http, await service.GetPolicyAsync(tenant.TenantId, connectionId, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapPut("/billing/invoice-policies/{connectionId:guid}", UpsertPolicy);

        api.MapGet("/invoices", async (HttpContext http, IInvoicingBillingService service, int? limit, string? after, string? status) => Tenant(http) is { } tenant ? Results.Ok(await service.ListAsync(tenant.TenantId, PageSize(limit), after, status, http.RequestAborted)) : Unauthorized(http));
        api.MapGet("/invoice-workspace/summary", async (HttpContext http, IInvoicingBillingService service) => Tenant(http) is { } tenant ? Results.Ok(await service.WorkspaceSummaryAsync(tenant.TenantId, http.RequestAborted)) : Unauthorized(http));
        api.MapGet("/invoice-workspace/page", async (HttpContext http, IInvoicingBillingService service, int? pageNumber, int? pageSize, string? tab, string? search, string? platformCodes, string? shipmentStatus, string? cargoProviderName, string? invoiceStatus, string? invoiceAction, DateTimeOffset? from, DateTimeOffset? to, bool? providerHasCredential, string? sort) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var filter = new InvoiceWorkspacePageQuery(
                pageNumber ?? 1,
                pageSize ?? 20,
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
            return Results.Ok(await service.WorkspacePageAsync(tenant.TenantId, filter, http.RequestAborted));
        });
        api.MapPut("/invoice-workspace/hidden", UpdateInvoiceWorkspaceHiddenAsync);
        api.MapPut("/invoice-workspace/manual-status", UpdateInvoiceWorkspaceManualStatusAsync);
        api.MapPost("/invoice-workspace/orders/{orderId:guid}/reconcile-jobs", async (Guid orderId, HttpContext http, IInvoicingBillingService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            if (RequireIdempotency(http) is { } failure) return failure;
            var result = await service.EnqueueOrderInvoiceReconciliationAsync(tenant.TenantId, orderId,
                http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted);
            return result.Succeeded ? Results.Accepted(value: new { jobId = result.Value }) : Problem(http, result.Error!);
        });
        api.MapPost("/invoice-workspace/manual-documents", CreateManualOrderInvoiceDocumentAsync).DisableAntiforgery();
        api.MapPost("/invoice-workspace/manual-documents/{id:guid}/delivery-jobs", QueueManualOrderInvoiceDeliveryAsync);
        api.MapGet("/invoice-workspace/manual-documents/{id:guid}/content", OpenManualOrderInvoiceDocumentAsync);
        api.MapPost("/invoice-workspace/orders/{orderId:guid}/confirm-no-remote-invoice", async (Guid orderId, HttpContext http, IInvoicingBillingService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            if (RequireIdempotency(http) is { } missingKey) return missingKey;
            var result = await service.ConfirmNoRemoteInvoiceAsync(tenant.TenantId, orderId, http.TraceIdentifier, http.RequestAborted);
            return result.Succeeded ? Results.Ok(new { confirmed = true }) : Problem(http, result.Error!);
        });
        api.MapPost("/invoice-workspace/orders/{orderId:guid}/confirm-marketplace-delivered", async (Guid orderId, HttpContext http, IInvoicingBillingService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            if (RequireIdempotency(http) is { } missingKey) return missingKey;
            var result = await service.ConfirmMarketplaceDeliveredAsync(tenant.TenantId, orderId, http.TraceIdentifier, http.RequestAborted);
            return result.Succeeded ? Results.Ok(new { confirmed = true }) : Problem(http, result.Error!);
        });
        api.MapPost("/invoice-workspace/orders/{orderId:guid}/replace-cancelled-invoice", async (Guid orderId, HttpContext http, IInvoicingBillingService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            if (RequireIdempotency(http) is { } missingKey) return missingKey;
            var result = await service.ReplaceCancelledInvoiceAsync(tenant.TenantId, orderId, http.TraceIdentifier, http.RequestAborted);
            return result.Succeeded ? Results.Ok(new { replaced = true }) : Problem(http, result.Error!);
        });
        api.MapPost("/invoice-workspace/preview", async (InvoiceWorkspacePreviewRequest request, HttpContext http, IInvoicingBillingService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var result = await service.PreviewWorkspaceInvoicesAsync(tenant.TenantId, request, http.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : Problem(http, result.Error!);
        });
        api.MapPost("/invoice-workspace/confirm", ConfirmWorkspaceAsync);
        api.MapPost("/invoices", async (CreateInvoiceCommand command, HttpContext http, IInvoicingBillingService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Created(await service.CreateDraftAsync(tenant.TenantId, command, http.Request.Headers["Idempotency-Key"].ToString(), http.RequestAborted), "/api/v1/invoices") : MissingContext(http));
        api.MapGet("/invoices/{id:guid}", async (Guid id, HttpContext http, IInvoicingBillingService service) => Tenant(http) is { } tenant ? WithEtag(http, await service.GetAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapPost("/invoices/{id:guid}/validate", async (Guid id, HttpContext http, IInvoicingBillingService service) => Tenant(http) is { } tenant ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.ValidateAsync(tenant.TenantId, id, version, http.RequestAborted), x => x.Version) : failure! : Unauthorized(http));
        api.MapPost("/invoices/{id:guid}/submit-jobs", async (Guid id, ConfirmedAction command, HttpContext http, IInvoicingBillingService service, AppDbContext db) => await EnqueueProtected(id, command, http, db, (tenant, version, key) => service.EnqueueSubmitAsync(tenant, id, version, key, http.TraceIdentifier, http.RequestAborted), requireSensitiveConfirmation: false));
        api.MapPost("/invoices/{id:guid}/stage-capability-probe-jobs", async (Guid id, HttpContext http, IInvoicingBillingService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Accepted(await service.EnqueueStageCapabilityProbeAsync(tenant.TenantId, id, version, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted)) : failure! : MissingContext(http));
        api.MapPost("/invoices/{id:guid}/reconcile-jobs", async (Guid id, HttpContext http, IInvoicingBillingService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueReconcileAsync(tenant.TenantId, id, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));
        api.MapPost("/invoices/{id:guid}/marketplace-delivery-jobs", async (Guid id, ConfirmedAction command, HttpContext http, IInvoicingBillingService service, AppDbContext db) => await EnqueueProtected(id, command, http, db, (tenant, _, key) => service.EnqueueDeliveryAsync(tenant, id, key, http.TraceIdentifier, http.RequestAborted), false));
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
        }).WithServerBudget();
        api.MapPost("/invoices/{id:guid}/cancellation-jobs", async (Guid id, ConfirmedAction command, HttpContext http, IInvoicingBillingService service, AppDbContext db) => await EnqueueProtected(id, command, http, db, (tenant, version, key) => service.EnqueueCancellationAsync(tenant, id, version, key, http.TraceIdentifier, http.RequestAborted)));
        api.MapPost("/invoices/{id:guid}/documents/manual", UploadManualInvoiceDocumentAsync).DisableAntiforgery();
        api.MapPut("/invoices/{id:guid}/shopify-status", UpdateShopifyInvoiceStatusAsync);
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
        });
        api.MapGet("/invoices/{invoiceId:guid}/documents/{documentId:guid}/content", async (Guid invoiceId, Guid documentId, HttpContext http, IInvoicingBillingService service) => Tenant(http) is { } tenant ? Stream(await service.OpenDocumentAsync(tenant.TenantId, invoiceId, documentId, http.RequestAborted), http) : Unauthorized(http));
        return endpoints;
    }

    private static async Task<IResult> UpsertPolicy(Guid connectionId, UpsertInvoicePolicyCommand command, HttpContext http, IInvoicingBillingService service)
    {
        var tenant = Tenant(http); if (tenant is null) return Unauthorized(http); var version = OptionalIfMatch(http, out var failure); if (failure is not null) return failure;
        return WithEtag(http, await service.UpsertPolicyAsync(tenant.TenantId, connectionId, version, command, http.RequestAborted), x => x.Version);
    }

    private static async Task<IResult> CreateManualOrderInvoiceDocumentAsync(HttpContext http, AppDbContext db, IPrivateFileStorage storage, TimeProvider timeProvider, IDataProtectionProvider protection, IConfiguration configuration)
    {
        const long maximumBytes = 10 * 1024 * 1024;
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (RequireIdempotency(http) is { } idempotencyFailure) return idempotencyFailure;
        if (!http.Request.HasFormContentType) return Problem(http, new("INVOICE_DOCUMENT_FORM_REQUIRED", "multipart/form-data body gereklidir.", 400));
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (!Guid.TryParse(form["orderId"], out var orderId)) return Problem(http, new("INVOICE_ORDER_REQUIRED", "Sipariş kimliği gereklidir.", 400));
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Id == orderId, http.RequestAborted);
        if (order is null) return Problem(http, new("RESOURCE_NOT_FOUND", "Sipariş bulunamadı.", 404));
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Id == order.ConnectionId, http.RequestAborted);
        if (connection is null || connection.PlatformCode is not ("TRENDYOL" or "HEPSIBURADA" or "SHOPIFY"))
            return Problem(http, new("INVOICE_MARKETPLACE_UNSUPPORTED", "Manuel fatura belgesi yalnız desteklenen Trendyol, Hepsiburada veya Shopify siparişlerine eklenebilir.", 422));
        if (!await db.ShipmentPackages.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId && x.OrderId == orderId && x.Status != ShipmentPackageStatus.Cancelled, http.RequestAborted))
            return Problem(http, new("INVOICE_PACKAGE_REQUIRED", "Siparişte etkin bir paket bulunamadı.", 422));

        var file = form.Files.GetFile("file");
        var externalUrl = form["invoiceUrl"].ToString().Trim();
        if ((file is null) == string.IsNullOrWhiteSpace(externalUrl))
            return Problem(http, new("INVOICE_DOCUMENT_SOURCE_REQUIRED", "Bir PDF dosyası veya herkese açık HTTPS fatura bağlantısı girin.", 422));
        if (!string.IsNullOrWhiteSpace(externalUrl)
            && (externalUrl.Length > 4096 || !IsPublicHttpsInvoiceUrl(externalUrl)))
            return Problem(http, new("INVOICE_DOCUMENT_URL_INVALID", "Fatura bağlantısı geçerli, herkese açık HTTPS adresi olmalıdır.", 422));
        var invoiceNumber = form["invoiceNumber"].ToString().Trim();
        if (invoiceNumber.Length > 64) return Problem(http, new("INVOICE_NUMBER_INVALID", "Fatura numarası en fazla 64 karakter olabilir.", 422));
        // Trendyol takes the link of a micro-export invoice only with its GİB number; a domestic package's link is
        // sent without a number Trendyol would refuse (the number is optional there).
        if (connection.PlatformCode == "TRENDYOL" && InvoiceIssuePolicy.IsMicroExport(order.CustomerSnapshotJson)
            && !InvoiceIssuePolicy.IsGibInvoiceNumber(invoiceNumber))
            return Problem(http, new("INVOICE_NUMBER_INVALID", "Mikro ihracat faturası için GİB biçiminde fatura numarası gerekli: 3 harf/rakam ve 13 rakam (örnek: ABC2026000000045).", 422));
        DateTimeOffset? issuedAt = null;
        if (!string.IsNullOrWhiteSpace(form["issuedAt"]))
        {
            if (!DateTimeOffset.TryParse(form["issuedAt"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                return Problem(http, new("INVOICE_DATE_INVALID", "Fatura tarihi geçerli bir tarih olmalıdır.", 422));
            issuedAt = parsed.ToUniversalTime();
        }

        byte[]? bytes = null;
        string? sha256 = null;
        if (file is not null)
        {
            if (file.Length <= 0) return Problem(http, new("INVOICE_DOCUMENT_FILE_REQUIRED", "PDF dosyası boş olamaz.", 400));
            if (file.Length > maximumBytes) return Problem(http, new("INVOICE_DOCUMENT_TOO_LARGE", "Fatura PDF dosyası en fazla 10 MiB olabilir.", 413));
            await using var input = file.OpenReadStream();
            await using var buffer = new MemoryStream((int)file.Length);
            await input.CopyToAsync(buffer, http.RequestAborted);
            bytes = buffer.ToArray();
            if (bytes.LongLength != file.Length || bytes.LongLength > maximumBytes) return Problem(http, new("INVOICE_DOCUMENT_SIZE_MISMATCH", "Dosya boyutu doğrulanamadı.", 422));
            if (DetectInvoiceMimeType(bytes) != "application/pdf") return Problem(http, new("INVOICE_DOCUMENT_TYPE_UNSUPPORTED", "Manuel platform iletimi için yalnız PDF dosyası kabul edilir.", 415));
            sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        }

        var idempotencyKey = http.Request.Headers["Idempotency-Key"].ToString();
        var prior = await db.ManualOrderInvoiceDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.IdempotencyKey == idempotencyKey, http.RequestAborted);
        if (prior is not null)
        {
            var sameRequest = prior.OrderId == orderId
                && string.Equals(prior.Sha256, sha256, StringComparison.Ordinal)
                && string.Equals(prior.DocumentType == "URL" ? prior.DocumentUrl : null, string.IsNullOrWhiteSpace(externalUrl) ? null : externalUrl, StringComparison.Ordinal)
                && string.Equals(prior.InvoiceNumber, string.IsNullOrWhiteSpace(invoiceNumber) ? null : invoiceNumber, StringComparison.Ordinal)
                && prior.IssuedAt == issuedAt;
            return sameRequest
                ? Results.Ok(new { prior.Id, prior.DocumentType, prior.DocumentUrl, prior.InvoiceNumber, prior.Status, duplicate = true })
                : Problem(http, new("IDEMPOTENCY_KEY_REUSED", "Aynı idempotency anahtarı farklı belge bilgileriyle kullanılamaz.", 409));
        }

        await using var mutationTransaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, http.RequestAborted);
        if (!await InvoiceOrderMutationLock.AcquireAsync(db, tenant.TenantId, orderId, http.RequestAborted))
            return Problem(http, new("RESOURCE_NOT_FOUND", "Sipariş bulunamadı.", 404));
        prior = await db.ManualOrderInvoiceDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.IdempotencyKey == idempotencyKey, http.RequestAborted);
        if (prior is not null)
        {
            var sameRequest = prior.OrderId == orderId
                && string.Equals(prior.Sha256, sha256, StringComparison.Ordinal)
                && string.Equals(prior.DocumentType == "URL" ? prior.DocumentUrl : null, string.IsNullOrWhiteSpace(externalUrl) ? null : externalUrl, StringComparison.Ordinal)
                && string.Equals(prior.InvoiceNumber, string.IsNullOrWhiteSpace(invoiceNumber) ? null : invoiceNumber, StringComparison.Ordinal)
                && prior.IssuedAt == issuedAt;
            if (!sameRequest) return Problem(http, new("IDEMPOTENCY_KEY_REUSED", "Aynı idempotency anahtarı farklı belge bilgileriyle kullanılamaz.", 409));
            await mutationTransaction.CommitAsync(http.RequestAborted);
            return Results.Ok(new { prior.Id, prior.DocumentType, prior.DocumentUrl, prior.InvoiceNumber, prior.Status, duplicate = true });
        }
        if (await db.Invoices.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId && x.OrderId == orderId
                && x.OriginalInvoiceId == null && x.SequencePurpose == "SALE", http.RequestAborted))
            return Problem(http, new("INVOICE_ALREADY_EXISTS", "Bu sipariş için mali fatura kaydı zaten var. Mevcut faturaya yeni belge ekleyin; siparişe ikinci belge kaydedilmedi.", 409));

        var now = timeProvider.GetUtcNow();
        var documentId = Guid.CreateVersion7();
        Guid? assetId = null;
        string documentUrl;
        if (file is null) documentUrl = externalUrl;
        else
        {
            var publicBaseUrl = configuration["Marketplace:PublicBaseUrl"] ?? string.Empty;
            try { documentUrl = ManualOrderInvoiceDocumentLink.Create(protection, publicBaseUrl, tenant.TenantId, documentId); }
            catch (ArgumentException) { return Problem(http, new("INVOICE_PUBLIC_DOCUMENT_URL_UNAVAILABLE", "Manuel PDF iletimi için herkese açık HTTPS belge sunucusu yapılandırılmalıdır.", 409)); }
            assetId = Guid.CreateVersion7();
            await using var fileBuffer = new MemoryStream(bytes!, writable: false);
            var stored = await storage.SaveAsync(tenant.TenantId, $"{assetId.Value:N}.pdf", "application/pdf", fileBuffer, maximumBytes, http.RequestAborted);
            db.FileAssets.Add(new FileAsset { Id = assetId.Value, TenantId = tenant.TenantId, Classification = "INVOICE_DOCUMENT_MANUAL", RelativePath = stored, OriginalNameSafe = Path.GetFileName(file!.FileName), MimeType = "application/pdf", SizeBytes = bytes!.LongLength, Sha256 = sha256!, Status = "ACTIVE", CreatedAt = now });
        }

        var activeDocuments = await db.ManualOrderInvoiceDocuments
            .Where(x => x.TenantId == tenant.TenantId && x.OrderId == orderId && x.Status == "ACTIVE")
            .ToListAsync(http.RequestAborted);
        foreach (var old in activeDocuments)
        {
            old.Status = "REPLACED";
            old.UpdatedAt = now;
            old.Version++;
        }
        var document = new ManualOrderInvoiceDocument
        {
            Id = documentId,
            TenantId = tenant.TenantId,
            OrderId = orderId,
            PackageId = null,
            ConnectionId = order.ConnectionId,
            InvoiceNumber = string.IsNullOrWhiteSpace(invoiceNumber) ? null : invoiceNumber,
            IssuedAt = issuedAt,
            IdempotencyKey = idempotencyKey,
            DocumentType = file is null ? "URL" : "PDF",
            DocumentUrl = documentUrl,
            FileAssetId = assetId,
            Sha256 = sha256,
            Status = "ACTIVE",
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
        db.ManualOrderInvoiceDocuments.Add(document);
        db.AuditLogs.Add(new AuditLog { TenantId = tenant.TenantId, ActorUserId = tenant.UserId, Action = "MANUAL_ORDER_INVOICE_DOCUMENT_ADDED", TargetType = "Order", TargetId = orderId.ToString("D"), Reason = file is null ? "URL" : $"PDF:{bytes!.LongLength}", CorrelationId = http.TraceIdentifier, CreatedAt = now });
        try { await db.SaveChangesAsync(http.RequestAborted); }
        catch (DbUpdateException) { return Problem(http, new("MANUAL_INVOICE_DOCUMENT_CONFLICT", "Siparişin manuel fatura belgesi eşzamanlı olarak değişti. Sayfayı yenileyip tekrar deneyin.", 409)); }
        await mutationTransaction.CommitAsync(http.RequestAborted);
        return Results.Ok(new { document.Id, document.DocumentType, document.DocumentUrl, document.InvoiceNumber, document.Status, duplicate = false });
    }

    internal static async Task<IResult> QueueManualOrderInvoiceDeliveryAsync(Guid id, HttpContext http, AppDbContext db, TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (RequireIdempotency(http) is { } idempotencyFailure) return idempotencyFailure;
        var document = await db.ManualOrderInvoiceDocuments.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Id == id, http.RequestAborted);
        if (document is null) return Problem(http, new("RESOURCE_NOT_FOUND", "Manuel fatura belgesi bulunamadı.", 404));
        if (document.Status != "ACTIVE") return Problem(http, new("MANUAL_INVOICE_DOCUMENT_NOT_CURRENT", "Yalnız siparişin güncel manuel fatura belgesi iletilebilir.", 409));
        var packages = await db.ShipmentPackages.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.OrderId == document.OrderId && x.Status != ShipmentPackageStatus.Cancelled)
            .OrderBy(x => x.Id).ToListAsync(http.RequestAborted);
        if (packages.Count == 0) return Problem(http, new("INVOICE_PACKAGE_REQUIRED", "Siparişte etkin bir paket bulunamadı.", 422));
        if (document.IssuedAt is null) return Problem(http, new("INVOICE_DATE_REQUIRED", "Platforma iletim için faturanın tarihi gereklidir.", 422));
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.Id == document.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(http.RequestAborted);
        if (platformCode == "SHOPIFY" && string.IsNullOrWhiteSpace(document.InvoiceNumber))
            return Problem(http, new("INVOICE_NUMBER_REQUIRED", "Shopify siparişine iletim için fatura numarası gereklidir.", 422));
        if (packages.Any(package => package.ConnectionId != document.ConnectionId))
            return Problem(http, new("INVOICE_PACKAGE_CONNECTION_MISMATCH", "Sipariş paketleri farklı pazaryeri bağlantılarına bağlı. Belgenin hangi hedeflere gönderileceği güvenle belirlenemedi.", 409));
        var jobs = new List<Guid>();
        var now = timeProvider.GetUtcNow();
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{document.DocumentUrl}\n{document.InvoiceNumber}\n{document.IssuedAt:O}")));
        var inspectionOnlyCount = 0;
        foreach (var package in packages)
        {
            var state = await db.ManualOrderInvoiceDeliveryStates.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.DocumentId == document.Id && x.PackageId == package.Id, http.RequestAborted);
            if (state?.Status == "CONFIRMED") continue;
            var inspectionOnly = ManualInvoiceDeliveryPolicy.RequiresReadOnlyInspection(state?.Status);
            if (inspectionOnly) inspectionOnlyCount++;
            var idempotencyDigest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(http.Request.Headers["Idempotency-Key"].ToString())));
            var packageKey = $"manual-invoice-delivery:{document.Id:N}:{package.Id:N}";
            var dedup = $"{InvoicingJobTypes.ManualOrderInvoiceDelivery}:{id:N}:{package.Id:N}:{idempotencyDigest}";
            // The same key reaches its own job only while that job is still waiting or running. A finished (failed) job
            // is never answered as "queued": the retry gets a new job, its dedup key told apart by the state version.
            var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.JobType == InvoicingJobTypes.ManualOrderInvoiceDelivery && x.JobDedupKey == dedup, http.RequestAborted);
            if (existing is { Status: JobStatus.Pending or JobStatus.Leased or JobStatus.RetryScheduled }) { jobs.Add(existing.Id); continue; }
            if (state is not null)
            {
                var activeJob = await db.IntegrationJobs.AsNoTracking()
                    .Where(x => x.TenantId == tenant.TenantId && x.JobType == InvoicingJobTypes.ManualOrderInvoiceDelivery
                        && x.EffectIdempotencyKey == state.IdempotencyKey
                        && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled))
                    .OrderByDescending(x => x.CreatedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(http.RequestAborted);
                if (activeJob is { } activeId) { jobs.Add(activeId); continue; }
                if (state.Status == "QUEUED") return Problem(http, new("MANUAL_INVOICE_DELIVERY_ALREADY_RUNNING", "İletim işi başladı ancak durumu kesinleşmedi; yeni bir iletim oluşturulmadı.", 409));
            }
            if (state is null)
            {
                state = new ManualOrderInvoiceDeliveryState { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, DocumentId = document.Id, ConnectionId = package.ConnectionId, PackageId = package.Id, IdempotencyKey = packageKey, RequestHash = hash, Status = "QUEUED", CreatedAt = now, UpdatedAt = now, Version = 1 };
                db.ManualOrderInvoiceDeliveryStates.Add(state);
            }
            else
            {
                if (!string.Equals(state.RequestHash, hash, StringComparison.Ordinal)) return Problem(http, new("MANUAL_INVOICE_DELIVERY_DOCUMENT_CHANGED", "Belirsiz önceki teslimat başka bir belge sürümüne aittir; otomatik tekrar durduruldu.", 409));
                state.ConnectionId = package.ConnectionId;
                state.IdempotencyKey = packageKey;
                if (!inspectionOnly)
                {
                    state.Status = "QUEUED";
                    state.ErrorCode = null;
                }
                state.UpdatedAt = now;
                state.Version++;
            }
            var payload = JsonSerializer.Serialize(new { manualOrderInvoiceDocumentId = document.Id, packageId = package.Id, inspectionOnly });
            var job = new IntegrationJob { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, ConnectionId = package.ConnectionId, JobType = InvoicingJobTypes.ManualOrderInvoiceDelivery, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))), JobDedupKey = existing is null ? dedup : $"{dedup}:r{state.Version}", EffectIdempotencyKey = packageKey, Priority = JobPriorityPolicy.ForUser(InvoicingJobTypes.ManualOrderInvoiceDelivery), Status = JobStatus.Pending, AvailableAt = now, MaxAttempts = 4, CorrelationId = http.TraceIdentifier, CreatedAt = now, Version = 1 };
            db.IntegrationJobs.Add(job);
            jobs.Add(job.Id);
        }
        if (jobs.Count == 0) return Results.Ok(new { documentId = document.Id, jobIds = Array.Empty<Guid>(), message = "Belge seçili paketlerin tamamında zaten doğrulanmış." });
        db.AuditLogs.Add(new AuditLog { TenantId = tenant.TenantId, ActorUserId = tenant.UserId, Action = "MANUAL_ORDER_INVOICE_DELIVERY_QUEUED", TargetType = "Order", TargetId = document.OrderId.ToString("D"), Reason = $"{jobs.Count} paket", CorrelationId = http.TraceIdentifier, CreatedAt = now });
        try { await db.SaveChangesAsync(http.RequestAborted); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var operationKeys = packages.Select(package => $"manual-invoice-delivery:{document.Id:N}:{package.Id:N}").ToArray();
            var concurrentJobs = await db.IntegrationJobs.AsNoTracking()
                .Where(job => job.TenantId == tenant.TenantId && job.JobType == InvoicingJobTypes.ManualOrderInvoiceDelivery
                    && operationKeys.Contains(job.EffectIdempotencyKey)
                    && (job.Status == JobStatus.Pending || job.Status == JobStatus.Leased || job.Status == JobStatus.RetryScheduled))
                .OrderBy(job => job.CreatedAt).Select(job => job.Id).ToArrayAsync(http.RequestAborted);
            if (concurrentJobs.Length == 0)
                return Problem(http, new("MANUAL_INVOICE_DELIVERY_CONFLICT", "İletim durumu eşzamanlı değişti. Sayfayı yenileyip pazaryeri durumunu yeniden kontrol edin.", 409));
            return Results.Accepted($"/api/v1/jobs/{concurrentJobs[0]:D}", new { documentId = document.Id, jobIds = concurrentJobs, queuedPackageCount = concurrentJobs.Length, inspectionOnlyPackageCount = inspectionOnlyCount });
        }
        return Results.Accepted($"/api/v1/jobs/{jobs[0]:D}", new { documentId = document.Id, jobIds = jobs, queuedPackageCount = jobs.Count, inspectionOnlyPackageCount = inspectionOnlyCount });
    }

    private static async Task<IResult> OpenManualOrderInvoiceDocumentAsync(Guid id, HttpContext http, AppDbContext db, IPrivateFileStorage storage)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var row = await (from document in db.ManualOrderInvoiceDocuments.AsNoTracking()
                         join asset in db.FileAssets.AsNoTracking() on new { document.TenantId, Id = document.FileAssetId } equals new { asset.TenantId, Id = (Guid?)asset.Id }
                         where document.TenantId == tenant.TenantId && document.Id == id && asset.Status == "ACTIVE"
                         select new { asset.RelativePath, asset.MimeType, asset.OriginalNameSafe }).SingleOrDefaultAsync(http.RequestAborted);
        if (row is null) return Results.NotFound();
        return Results.File(await storage.OpenReadAsync(tenant.TenantId, row.RelativePath, http.RequestAborted), row.MimeType, row.OriginalNameSafe ?? $"invoice-{id:N}.pdf", enableRangeProcessing: false);
    }

    private static async Task<IResult> OpenPublicManualOrderInvoiceDocumentAsync(string token, HttpContext http, IDataProtectionProvider protection, AppDbContext db, IPrivateFileStorage storage)
    {
        if (!ManualOrderInvoiceDocumentLink.TryRead(protection, token, out var tenantId, out var documentId)) return Results.NotFound();
        var expectedPath = $"/api/v1/public/manual-invoice-documents/{token}/content";
        var row = await (from document in db.ManualOrderInvoiceDocuments.AsNoTracking()
                         join asset in db.FileAssets.AsNoTracking() on new { document.TenantId, Id = document.FileAssetId } equals new { asset.TenantId, Id = (Guid?)asset.Id }
                         where document.TenantId == tenantId && document.Id == documentId && document.DocumentType == "PDF"
                             && asset.Status == "ACTIVE"
                         select new { asset.RelativePath, asset.MimeType, asset.OriginalNameSafe, document.DocumentUrl }).SingleOrDefaultAsync(http.RequestAborted);
        if (row is null || !Uri.TryCreate(row.DocumentUrl, UriKind.Absolute, out var storedUri)
            || !string.Equals(storedUri.AbsolutePath, expectedPath, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(storedUri.Query)
            || !string.IsNullOrEmpty(storedUri.Fragment)) return Results.NotFound();
        LogPublicInvoiceDocumentOpened(http, "manual", tenantId, documentId);
        // Invoices carry personal data: not kept by shared caches or the browser (A17).
        http.Response.Headers.CacheControl = "private, no-store";
        return Results.File(await storage.OpenReadAsync(tenantId, row.RelativePath, http.RequestAborted), row.MimeType, row.OriginalNameSafe ?? $"invoice-{documentId:N}.pdf", enableRangeProcessing: false);
    }

    // G10: the public invoice links have no expiry on purpose. The marketplaces keep the link they were given (Trendyol's
    // invoiceLink, the n11 .pdf form) and show it to the buyer for as long as the order is listed; an expiring link would
    // leave the buyer without the invoice. A link is withdrawn by removing it (link revocation clears the stored URL),
    // and every opening is logged here so a leaked link's use can be traced. The token (a bearer secret) is never logged:
    // not here, and not in the request path every event of the request carries (SecretPathEnricher masks it).
    private static void LogPublicInvoiceDocumentOpened(HttpContext http, string kind, Guid tenantId, Guid documentId) =>
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("MarketplaceHub.InvoiceLinks")
            .LogInformation("Public {Kind} invoice document {DocumentId} of tenant {TenantId} opened from {ClientNetwork} (pdf alias: {PdfAlias}).",
                kind, documentId, tenantId, MarketplaceHub.Api.Security.ClientNetwork.PartitionKey(http),
                http.Request.Path.Value?.EndsWith("/" + InvoiceDocumentLink.PdfFileName, StringComparison.Ordinal) == true);

    private static bool IsPublicHttpsInvoiceUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.IsLoopback) return false;

        var host = uri.Host.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IPAddress.TryParse(host, out var address)) return true;
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast
                && (bytes[0] & 0xfe) != 0xfc;
        }
        var octets = address.GetAddressBytes();
        return octets[0] is not 0 and not 10 and not 127
            && !(octets[0] == 169 && octets[1] == 254)
            && !(octets[0] == 172 && octets[1] is >= 16 and <= 31)
            && !(octets[0] == 192 && octets[1] == 168)
            && octets[0] < 224;
    }

    private static async Task<IResult> UploadManualInvoiceDocumentAsync(Guid id, HttpContext http, AppDbContext db, IPrivateFileStorage storage, TimeProvider timeProvider)
    {
        const long maximumBytes = 10 * 1024 * 1024;
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (RequireIdempotency(http) is { } idempotencyFailure) return idempotencyFailure;
        if (!http.Request.HasFormContentType) return Problem(http, new("INVOICE_DOCUMENT_FORM_REQUIRED", "multipart/form-data body gereklidir.", 400));
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Id == id, http.RequestAborted);
        if (invoice is null) return Problem(http, new("RESOURCE_NOT_FOUND", "Fatura kaydı bulunamadı.", 404));
        var providerPlatform = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.Id == invoice.ProviderConnectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(http.RequestAborted);
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length <= 0) return Problem(http, new("INVOICE_DOCUMENT_FILE_REQUIRED", "file alanında fatura belgesi gereklidir.", 400));
        if (file.Length > maximumBytes) return Problem(http, new("INVOICE_DOCUMENT_TOO_LARGE", "Fatura belgesi en fazla 10 MiB olabilir.", 413));

        await using var input = file.OpenReadStream();
        await using var buffer = new MemoryStream((int)file.Length);
        await input.CopyToAsync(buffer, http.RequestAborted);
        var bytes = buffer.ToArray();
        if (bytes.LongLength != file.Length || bytes.LongLength > maximumBytes) return Problem(http, new("INVOICE_DOCUMENT_SIZE_MISMATCH", "Dosya boyutu doğrulanamadı.", 422));
        var mimeType = DetectInvoiceMimeType(bytes);
        if (mimeType is null) return Problem(http, new("INVOICE_DOCUMENT_TYPE_UNSUPPORTED", "Yalnız PDF, JPEG veya PNG fatura belgesi kabul edilir.", 415));
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var existing = await db.InvoiceDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.InvoiceId == id && x.DocumentType == "MANUAL_UPLOAD" && x.Sha256 == hash, http.RequestAborted);
        if (existing is not null)
        {
            if (providerPlatform == "SHOPIFY" && invoice.Status != InvoiceStatus.Completed)
            {
                var duplicateNow = timeProvider.GetUtcNow();
                invoice.Status = InvoiceStatus.Completed;
                invoice.IssuedAt ??= duplicateNow;
                invoice.UpdatedAt = duplicateNow;
                invoice.Version++;
                await db.SaveChangesAsync(http.RequestAborted);
            }
            return Results.Ok(new { existing.Id, existing.DocumentType, existing.Sha256, existing.CreatedAt, duplicate = true });
        }

        var assetId = Guid.CreateVersion7();
        var extension = mimeType switch { "application/pdf" => ".pdf", "image/jpeg" => ".jpg", _ => ".png" };
        buffer.Position = 0;
        var stored = await storage.SaveAsync(tenant.TenantId, $"{assetId:N}{extension}", mimeType, buffer, maximumBytes, http.RequestAborted);
        var now = timeProvider.GetUtcNow();
        var document = new InvoiceDocument { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, InvoiceId = id, DocumentType = "MANUAL_UPLOAD", FileAssetId = assetId, Sha256 = hash, CreatedAt = now };
        db.FileAssets.Add(new FileAsset { Id = assetId, TenantId = tenant.TenantId, Classification = "INVOICE_DOCUMENT_MANUAL", RelativePath = stored, OriginalNameSafe = Path.GetFileName(file.FileName), MimeType = mimeType, SizeBytes = bytes.LongLength, Sha256 = hash, Status = "ACTIVE", CreatedAt = now });
        db.InvoiceDocuments.Add(document);
        if (providerPlatform == "SHOPIFY")
        {
            invoice.Status = InvoiceStatus.Completed;
            invoice.IssuedAt ??= now;
            invoice.UpdatedAt = now;
            invoice.Version++;
        }
        db.AuditLogs.Add(new AuditLog { TenantId = tenant.TenantId, ActorUserId = tenant.UserId, Action = "INVOICE_DOCUMENT_MANUAL_UPLOAD", TargetType = "Invoice", TargetId = id.ToString("D"), Reason = $"{mimeType}:{bytes.LongLength}", CorrelationId = http.TraceIdentifier, CreatedAt = now });
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Created($"/api/v1/invoices/{id:D}/documents/{document.Id:D}/content", new { document.Id, document.DocumentType, document.Sha256, document.CreatedAt, duplicate = false });
    }

    private static async Task<IResult> UpdateShopifyInvoiceStatusAsync(Guid id, ShopifyInvoiceStatusCommand command, HttpContext http, AppDbContext db, TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (RequireIdempotency(http) is { } idempotencyFailure) return idempotencyFailure;
        var status = command.Status?.Trim().ToUpperInvariant();
        if (status is not ("PENDING" or "UPLOADED")) return Problem(http, new("SHOPIFY_INVOICE_STATUS_INVALID", "Shopify fatura durumu PENDING veya UPLOADED olmalıdır.", 422));
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Id == id, http.RequestAborted);
        if (invoice is null) return Problem(http, new("RESOURCE_NOT_FOUND", "Fatura kaydı bulunamadı.", 404));
        var providerPlatform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.Id == invoice.ProviderConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(http.RequestAborted);
        if (providerPlatform != "SHOPIFY") return Problem(http, new("SHOPIFY_INVOICE_ONLY", "Bu manuel durum akışı yalnız Shopify faturaları için kullanılabilir.", 422));
        var orderUsesShopifyConnection = await db.Orders.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId && x.Id == invoice.OrderId && x.ConnectionId == invoice.ProviderConnectionId, http.RequestAborted);
        if (!orderUsesShopifyConnection) return Problem(http, new("SHOPIFY_INVOICE_ONLY", "Fatura kaydı aynı Shopify bağlantısına ait bir siparişle eşleşmiyor.", 422));
        if (invoice.PackageId is not { } packageId) return Problem(http, new("SHOPIFY_INVOICE_PACKAGE_REQUIRED", "Manuel Shopify fatura durumu bir sipariş paketine bağlı olmalıdır.", 422));
        var package = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Id == packageId && x.OrderId == invoice.OrderId, http.RequestAborted);
        if (package is null) return Problem(http, new("SHOPIFY_INVOICE_PACKAGE_REQUIRED", "Shopify sipariş paketi bulunamadı.", 422));
        var now = timeProvider.GetUtcNow();
        package.ManualInvoiceStatus = status;
        package.UpdatedAt = now;
        package.Version++;
        db.AuditLogs.Add(new AuditLog { TenantId = tenant.TenantId, ActorUserId = tenant.UserId, Action = "SHOPIFY_INVOICE_STATUS_MANUAL", TargetType = "ShipmentPackage", TargetId = package.Id.ToString("D"), Reason = status, CorrelationId = http.TraceIdentifier, CreatedAt = now });
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { id = package.Id, status = status == "UPLOADED" ? "FATURA_KONTROLDE" : "FATURA_BEKLIYOR", version = package.Version });
    }

    internal static readonly HashSet<string> ManualInvoiceStatusPlatforms = new(["SHOPIFY", "N11", "PTTAVM", "PAZARAMA"], StringComparer.OrdinalIgnoreCase);

    internal static async Task<IResult> UpdateInvoiceWorkspaceManualStatusAsync(InvoiceWorkspaceManualStatusCommand command, HttpContext http, AppDbContext db, TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (RequireIdempotency(http) is { } idempotencyFailure) return idempotencyFailure;
        var status = command.Status?.Trim().ToUpperInvariant();
        if (status is not ("PENDING" or "UPLOADED")) return Problem(http, new("INVOICE_WORKSPACE_STATUS_INVALID", "Manuel fatura durumu PENDING veya UPLOADED olmalıdır.", 422));
        if (command.PackageIds is null || command.PackageIds.Count is < 1 or > 200) return Problem(http, new("INVOICE_WORKSPACE_PACKAGES_INVALID", "1 ile 200 arasında sipariş paketi seçilmelidir.", 422));
        var packageIds = command.PackageIds.Distinct().ToArray();
        if (packageIds.Length != command.PackageIds.Count) return Problem(http, new("INVOICE_WORKSPACE_PACKAGES_DUPLICATED", "Sipariş paket kimlikleri benzersiz olmalıdır.", 422));
        var packagePlatforms = await (from package in db.ShipmentPackages.AsNoTracking()
                                      join connection in db.PlatformConnections.AsNoTracking() on new { package.TenantId, package.ConnectionId } equals new { connection.TenantId, ConnectionId = connection.Id }
                                      where package.TenantId == tenant.TenantId && packageIds.Contains(package.Id)
                                      select new { package.Id, connection.PlatformCode }).ToListAsync(http.RequestAborted);
        if (packagePlatforms.Count != packageIds.Length) return Problem(http, new("RESOURCE_NOT_FOUND", "Seçilen fatura siparişlerinin bir kısmı bulunamadı.", 404));
        // Only where the marketplace cannot tell the panel about an invoice uploaded in its own seller panel: Shopify has
        // no invoice of its own, n11, PTT AVM and Pazarama report only invoices sent through the API.
        if (packagePlatforms.Any(x => !ManualInvoiceStatusPlatforms.Contains(x.PlatformCode))) return Problem(http, new("MANUAL_INVOICE_STATUS_PLATFORM_UNSUPPORTED", "Elle fatura durumu yalnızca Shopify, n11, PTT AVM ve Pazarama siparişlerinde kullanılabilir; Trendyol ve Hepsiburada faturayı kendileri bildirir. Hiçbir sipariş güncellenmedi.", 422));
        var packages = await db.ShipmentPackages.Where(x => x.TenantId == tenant.TenantId && packageIds.Contains(x.Id)).ToListAsync(http.RequestAborted);
        var now = timeProvider.GetUtcNow();
        foreach (var package in packages)
        {
            package.ManualInvoiceStatus = status;
            package.UpdatedAt = now;
            package.Version++;
            db.AuditLogs.Add(new AuditLog { TenantId = tenant.TenantId, ActorUserId = tenant.UserId, Action = "INVOICE_WORKSPACE_STATUS_MANUAL", TargetType = "ShipmentPackage", TargetId = package.Id.ToString("D"), Reason = status, CorrelationId = http.TraceIdentifier, CreatedAt = now });
        }
        // "Fatura Bekliyor" on an n11, Pazarama or PTT AVM package whose invoice was cancelled reopens it for a new
        // invoice: these marketplaces cannot report that the cancelled invoice's link no longer counts.
        var reopenedCount = status == "PENDING"
            ? await MarketplaceInvoiceReopen.ReopenAsync(db, tenant.TenantId,
                packages.Select(package => (package, packagePlatforms.Single(item => item.Id == package.Id).PlatformCode)).ToList(),
                tenant.UserId, http.TraceIdentifier, now, http.RequestAborted)
            : 0;
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { status, updatedCount = packages.Count, reopenedCount });
    }

    private static async Task<IResult> UpdateInvoiceWorkspaceHiddenAsync(InvoiceWorkspaceHiddenCommand command, HttpContext http, AppDbContext db, TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (RequireIdempotency(http) is { } idempotencyFailure) return idempotencyFailure;
        if (command.PackageIds is null || command.PackageIds.Count is < 1 or > 200) return Problem(http, new("INVOICE_WORKSPACE_PACKAGES_INVALID", "1 ile 200 arasında sipariş paketi seçilmelidir.", 422));
        var packageIds = command.PackageIds.Distinct().ToArray();
        if (packageIds.Length != command.PackageIds.Count) return Problem(http, new("INVOICE_WORKSPACE_PACKAGES_DUPLICATED", "Sipariş paket kimlikleri benzersiz olmalıdır.", 422));
        var ownedIds = await db.ShipmentPackages.AsNoTracking().Where(package => package.TenantId == tenant.TenantId && packageIds.Contains(package.Id)).Select(package => package.Id).ToListAsync(http.RequestAborted);
        if (ownedIds.Count != packageIds.Length) return Problem(http, new("RESOURCE_NOT_FOUND", "Seçilen fatura siparişlerinin bir kısmı bulunamadı.", 404));

        var setting = await db.TenantSettings.SingleOrDefaultAsync(item => item.TenantId == tenant.TenantId && item.Key == "invoice-workspace-hidden-packages", http.RequestAborted);
        var hiddenIds = setting is null ? new HashSet<Guid>() : JsonSerializer.Deserialize<HashSet<Guid>>(setting.ValueJson) ?? [];
        foreach (var packageId in packageIds)
        {
            if (command.Hidden) hiddenIds.Add(packageId);
            else hiddenIds.Remove(packageId);
            db.AuditLogs.Add(new AuditLog { TenantId = tenant.TenantId, ActorUserId = tenant.UserId, Action = command.Hidden ? "INVOICE_WORKSPACE_PACKAGE_HIDDEN" : "INVOICE_WORKSPACE_PACKAGE_UNHIDDEN", TargetType = "ShipmentPackage", TargetId = packageId.ToString("D"), Reason = command.Hidden ? "HIDDEN" : "VISIBLE", CorrelationId = http.TraceIdentifier, CreatedAt = timeProvider.GetUtcNow() });
        }
        var now = timeProvider.GetUtcNow();
        var json = JsonSerializer.Serialize(hiddenIds);
        if (setting is null) db.TenantSettings.Add(new TenantSetting { TenantId = tenant.TenantId, Key = "invoice-workspace-hidden-packages", ValueJson = json, UpdatedAt = now, Version = 1 });
        else { setting.ValueJson = json; setting.UpdatedAt = now; setting.Version++; }
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { hidden = command.Hidden, updatedCount = packageIds.Length, hiddenCount = hiddenIds.Count });
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

    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
    private static int PageSize(int? limit) => limit is null ? 50 : limit is >= 1 and <= 200 ? limit.Value : throw new ArgumentException("limit 1-200 arasında olmalıdır.");
    private static IResult? RequireIdempotency(HttpContext http) => string.IsNullOrWhiteSpace(http.Request.Headers["Idempotency-Key"]) ? Problem(http, new("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key başlığı zorunludur.", 400)) : http.Request.Headers["Idempotency-Key"].ToString().Length > 256 ? Problem(http, new("IDEMPOTENCY_KEY_INVALID", "Idempotency-Key en fazla 256 karakterdir.", 400)) : null;
    private static bool TryIfMatch(HttpContext http, out long version, out IResult? failure) { var parsed = OptionalIfMatch(http, out failure); version = parsed ?? 0; if (failure is null && parsed is null) failure = Problem(http, new("PRECONDITION_REQUIRED", "If-Match gereklidir.", 428)); return failure is null; }
    private static long? OptionalIfMatch(HttpContext http, out IResult? failure) { failure = null; var value = http.Request.Headers.IfMatch.ToString(); if (string.IsNullOrWhiteSpace(value)) return null; if (value.Length >= 4 && value.StartsWith("\"v", StringComparison.Ordinal) && value.EndsWith('"') && long.TryParse(value[2..^1], out var version) && version > 0) return version; failure = Problem(http, new("INVALID_ETAG", "If-Match güçlü ETag biçiminde olmalıdır: \"v{version}\".", 400)); return null; }
    private static IResult WithEtag<T>(HttpContext http, ServiceResult<T> result, Func<T, long> version) { if (!result.Succeeded) return Problem(http, result.Error!); http.Response.Headers.ETag = $"\"v{version(result.Value!)}\""; return Results.Ok(result.Value); }
    private static IResult Created<T>(ServiceResult<T> result, string collection) { if (!result.Succeeded) return MarketplaceHub.Api.Security.ApiProblems.Of(null, result.Error!); var id = typeof(T).GetProperty("Id")?.GetValue(result.Value); return Results.Created(id is Guid guid ? $"{collection}/{guid:D}" : collection, result.Value); }
    private static IResult Accepted(ServiceResult<Guid> result) => result.Succeeded ? Results.Accepted($"/api/v1/jobs/{result.Value:D}", new { jobId = result.Value }) : MarketplaceHub.Api.Security.ApiProblems.Of(null, result.Error!);
    private static IResult Result<T>(ServiceResult<T> result, Func<T, IResult> success) => result.Succeeded ? success(result.Value!) : MarketplaceHub.Api.Security.ApiProblems.Of(null, result.Error!);
    private static IResult Stream(ServiceResult<(Stream Content, string MimeType, string FileName)> result, HttpContext http) { if (!result.Succeeded) return Problem(http, result.Error!); http.Response.Headers.CacheControl = "private, no-store"; return Results.File(result.Value.Content, result.Value.MimeType, result.Value.FileName, enableRangeProcessing: false); }
    private static IResult Unauthorized(HttpContext http) => Problem(http, new("AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401));
    private static IResult MissingContext(HttpContext http) => Tenant(http) is null ? Unauthorized(http) : RequireIdempotency(http) ?? Problem(http, new("REQUEST_INVALID", "İstek tamamlanamadı.", 400));
    private static IResult Problem(HttpContext http, ServiceError error) => MarketplaceHub.Api.Security.ApiProblems.Of(http, error);
    public sealed record ConfirmedAction(bool Confirmed);
}
