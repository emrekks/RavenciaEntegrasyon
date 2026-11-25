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

    internal static async Task<IResult> UpdateShopifyInvoiceStatusAsync(Guid id, ShopifyInvoiceStatusCommand command, HttpContext http, AppDbContext db, TimeProvider timeProvider)
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
        if (await OrderCancelledAsync(db, tenant.TenantId, invoice.OrderId, http.RequestAborted)) return Problem(http, OrderCancelledError);
        var now = timeProvider.GetUtcNow();
        package.ManualInvoiceStatus = status;
        package.UpdatedAt = now;
        package.Version++;
        db.AuditLogs.Add(new AuditLog { TenantId = tenant.TenantId, ActorUserId = tenant.UserId, Action = "SHOPIFY_INVOICE_STATUS_MANUAL", TargetType = "ShipmentPackage", TargetId = package.Id.ToString("D"), Reason = status, CorrelationId = http.TraceIdentifier, CreatedAt = now });
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { id = package.Id, status = status == "UPLOADED" ? "FATURA_KONTROLDE" : "FATURA_BEKLIYOR", version = package.Version });
    }

    // A fully cancelled order (its own status CANCELLED) takes no hand-uploaded invoice, no delivery of one and no
    // hand-set invoice status; a partly cancelled order is not cancelled and keeps all three. An invoice already issued
    // is cancelled through Fatura İptal Et, which these checks do not touch.
    internal static readonly ServiceError OrderCancelledError = new("ORDER_CANCELLED_INVOICE_LOCKED",
        "Sipariş iptal edildi; bu siparişe fatura yüklenmez, pazaryerine iletilmez ve fatura durumu elle değiştirilmez. Kesilmiş fatura Fatura İptal Et ile iptal edilir.", 409);

    internal static async Task<bool> OrderCancelledAsync(AppDbContext db, Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        !DashboardMetricPolicy.IsInvoiceEligibleOrder(await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == orderId).Select(x => x.DerivedStatus).SingleOrDefaultAsync(cancellationToken));

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
        if (packagePlatforms.Any(x => !ManualInvoiceStatusPlatforms.Contains(x.PlatformCode))) return Problem(http, new("MANUAL_INVOICE_STATUS_PLATFORM_UNSUPPORTED", "Elle fatura durumu yalnızca Shopify, N11, PttAVM ve Pazarama siparişlerinde kullanılabilir; Trendyol ve Hepsiburada faturayı kendileri bildirir. Hiçbir sipariş güncellenmedi.", 422));
        var packages = await db.ShipmentPackages.Where(x => x.TenantId == tenant.TenantId && packageIds.Contains(x.Id)).ToListAsync(http.RequestAborted);
        // A package of a fully cancelled order refuses the whole request, as a package of another marketplace does.
        var orderIds = packages.Select(package => package.OrderId).Distinct().ToList();
        var cancelled = await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && orderIds.Contains(x.Id) && DashboardMetricPolicy.InvoiceExcludedOrderStatuses.Contains(x.DerivedStatus))
            .CountAsync(http.RequestAborted);
        if (cancelled > 0) return Problem(http, OrderCancelledError with { Message = $"{cancelled} seçili sipariş iptal edildi; iptal edilen siparişin fatura durumu elle değiştirilmez. Hiçbir sipariş güncellenmedi." });
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
        // Ids of packages deleted since they were hidden (a reset or a resync) are dropped while the setting is written.
        var storedIds = hiddenIds.ToArray();
        var existingIds = await db.ShipmentPackages.AsNoTracking().Where(package => package.TenantId == tenant.TenantId && storedIds.Contains(package.Id)).Select(package => package.Id).ToListAsync(http.RequestAborted);
        hiddenIds.IntersectWith(existingIds);
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
}
