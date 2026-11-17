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
        if (platformCode is "SHOPIFY" or "N11" && string.IsNullOrWhiteSpace(document.InvoiceNumber))
            return Problem(http, new("INVOICE_NUMBER_REQUIRED", $"{(platformCode == "N11" ? "N11" : "Shopify")} siparişine iletim için fatura numarası gereklidir.", 422));
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
            var job = IntegrationJobFactory.New(new IntegrationJobRequest(tenant.TenantId, package.ConnectionId, InvoicingJobTypes.ManualOrderInvoiceDelivery, payload, existing is null ? dedup : $"{dedup}:r{state.Version}", http.TraceIdentifier, now) { EffectIdempotencyKey = packageKey, Priority = JobPriorityPolicy.ForUser(InvoicingJobTypes.ManualOrderInvoiceDelivery), MaxAttempts = 4, CreatedAt = now });
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
        // An invoice PDF carries the buyer's personal data: no browser or proxy keeps a copy (as Stream() and the public link).
        http.Response.Headers.CacheControl = "private, no-store";
        return Results.File(await storage.OpenReadAsync(tenant.TenantId, row.RelativePath, http.RequestAborted), row.MimeType, row.OriginalNameSafe ?? $"invoice-{id:N}.pdf", enableRangeProcessing: false);
    }

    private static async Task<IResult> OpenPublicManualOrderInvoiceDocumentAsync(string token, HttpContext http, IDataProtectionProvider protection, AppDbContext db, IPrivateFileStorage storage)
    {
        if (!ManualOrderInvoiceDocumentLink.TryRead(protection, token, out var tenantId, out var documentId)) return Results.NotFound();
        var expectedPath = $"/api/v1/public/manual-invoice-documents/{token}/content";
        var row = await (from document in db.ManualOrderInvoiceDocuments.AsNoTracking()
                         join asset in db.FileAssets.AsNoTracking() on new { document.TenantId, Id = document.FileAssetId } equals new { asset.TenantId, Id = (Guid?)asset.Id }
                         where document.TenantId == tenantId && document.Id == documentId && document.DocumentType == "PDF"
                             && document.Status == "ACTIVE" && asset.Status == "ACTIVE"
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
}
