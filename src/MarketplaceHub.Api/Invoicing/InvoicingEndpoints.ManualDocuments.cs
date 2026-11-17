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
        if (connection is null || !ManualInvoiceDeliveryPolicy.SupportsManualDocument(connection.PlatformCode))
            return Problem(http, new("INVOICE_MARKETPLACE_UNSUPPORTED", "Manuel fatura belgesi yalnız Trendyol, Hepsiburada, Shopify, N11, Pazarama veya PttAVM siparişlerine eklenebilir.", 422));
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
        // n11 takes an invoice link only with its 16-character number (3 letters or digits and 13 digits).
        if (connection.PlatformCode == "N11" && !string.IsNullOrWhiteSpace(invoiceNumber) && !InvoiceIssuePolicy.IsGibInvoiceNumber(invoiceNumber))
            return Problem(http, new("INVOICE_NUMBER_INVALID", "N11 fatura numarasını GİB biçiminde bekler: 3 harf/rakam ve 13 rakam (örnek: ABC2026000000045).", 422));
        // n11, Pazarama and PTT AVM take only links that end in a document extension (pdf, png, jpeg); an uploaded PDF
        // is sent under its .pdf link.
        if (!string.IsNullOrWhiteSpace(externalUrl) && ManualInvoiceDeliveryPolicy.RequiresDocumentExtension(connection.PlatformCode)
            && !ManualInvoiceDeliveryPolicy.HasDocumentExtension(externalUrl))
            return Problem(http, new("INVOICE_DOCUMENT_URL_INVALID", "Bu pazaryeri fatura bağlantısının pdf, png veya jpeg dosyasına işaret etmesini ister.", 422));
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
        // A replaced PDF is no longer served (YENİ-01): its stored file is archived, kept on disk as a record, and its
        // public link answers 404 like any other unknown link.
        var replacedAssetIds = activeDocuments.Where(x => x.FileAssetId is not null).Select(x => x.FileAssetId!.Value).ToList();
        if (replacedAssetIds.Count > 0)
        {
            var replacedAssets = await db.FileAssets
                .Where(x => x.TenantId == tenant.TenantId && replacedAssetIds.Contains(x.Id) && x.Status == "ACTIVE")
                .ToListAsync(http.RequestAborted);
            foreach (var asset in replacedAssets)
            {
                asset.Status = "ARCHIVED";
                asset.ArchivedAt = now;
            }
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
}
