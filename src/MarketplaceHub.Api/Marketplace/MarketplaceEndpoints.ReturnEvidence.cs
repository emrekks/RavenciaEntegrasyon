using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Api.Common;
using MarketplaceHub.Api.Operations;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Api.Common.EndpointHelpers;

namespace MarketplaceHub.Api.Marketplace;

public static partial class MarketplaceEndpoints
{
    private static async Task<IResult> UploadReturnEvidenceAsync(HttpContext http, AppDbContext db, IPrivateFileStorage storage, TimeProvider timeProvider)
    {
        const long maximumBytes = 10 * 1024 * 1024;
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (RequireIdempotency(http) is { } missingKey) return missingKey;
        if (!http.Request.HasFormContentType) return Problem(http, new("EVIDENCE_FORM_REQUIRED", "multipart/form-data body gereklidir.", 400));
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length <= 0) return Problem(http, new("EVIDENCE_FILE_REQUIRED", "file alanında kanıt dosyası gereklidir.", 400));
        if (file.Length > maximumBytes) return Problem(http, new("EVIDENCE_TOO_LARGE", "İade kanıtı en fazla 10 MiB olabilir.", 413));

        await using var input = file.OpenReadStream();
        await using var buffer = new MemoryStream((int)file.Length);
        await input.CopyToAsync(buffer, http.RequestAborted);
        if (buffer.Length != file.Length || buffer.Length > maximumBytes) return Problem(http, new("EVIDENCE_SIZE_MISMATCH", "Dosya boyutu doğrulanamadı.", 422));
        var bytes = buffer.ToArray();
        var mimeType = DetectEvidenceMimeType(bytes);
        if (mimeType is null) return Problem(http, new("EVIDENCE_TYPE_UNSUPPORTED", "Yalnız gerçek PDF, JPEG veya PNG iade kanıtı kabul edilir.", 415));
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var asset = await db.FileAssets.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Classification == "RETURN_EVIDENCE" && x.Sha256 == hash && x.ArchivedAt == null && x.Status == "ACTIVE", http.RequestAborted);
        if (asset is null)
        {
            var id = Guid.CreateVersion7();
            var extension = mimeType switch { "application/pdf" => ".pdf", "image/jpeg" => ".jpg", _ => ".png" };
            buffer.Position = 0;
            // The file gets its final name only after its row is saved (disposing it uncommitted removes it).
            await using var staged = await storage.StageAsync(tenant.TenantId, $"{id:N}{extension}", mimeType, buffer, maximumBytes, http.RequestAborted);
            asset = new FileAsset { Id = id, TenantId = tenant.TenantId, Classification = "RETURN_EVIDENCE", RelativePath = staged.RelativePath, OriginalNameSafe = Path.GetFileName(file.FileName), MimeType = mimeType, SizeBytes = bytes.LongLength, Sha256 = hash, Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() };
            db.FileAssets.Add(asset);
            await db.SaveChangesAsync(http.RequestAborted);
            staged.Commit();
        }
        return Results.Created($"/api/v1/files/return-evidence/{asset.Id:D}", new { asset.Id, asset.MimeType, asset.SizeBytes, asset.Sha256 });
    }

    private static string? DetectEvidenceMimeType(byte[] bytes) => bytes.Length >= 5 && bytes[..5].SequenceEqual("%PDF-"u8.ToArray()) ? "application/pdf"
        : bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF }) ? "image/jpeg"
        : bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "image/png"
        : null;
}
