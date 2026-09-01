using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Catalog;

public static class CatalogEndpoints
{
    private const long MaxUploadBytes = 6 * 1024 * 1024;

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ArgumentException exception) { return Problem(context.HttpContext, new("INVALID_CURSOR", exception.Message, 400)); }
        });

        api.MapGet("/catalog/categories", async (HttpContext http, ICatalogService service, int? limit, string? after) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.ListCategoriesAsync(tenant.TenantId, PageSize(limit), after, http.RequestAborted)) : Unauthorized(http));
        api.MapGet("/catalog/categories/{id:guid}", async (Guid id, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? WithEtag(http, await service.GetCategoryAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapPost("/catalog/categories", async (CreateCategoryCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Created(await service.CreateCategoryAsync(tenant.TenantId, command, http.RequestAborted), "/api/v1/catalog/categories") : MissingContext(http));
        api.MapPatch("/catalog/categories/{id:guid}", async (Guid id, UpdateCategoryCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.UpdateCategoryAsync(tenant.TenantId, id, version, command, http.RequestAborted), x => x.Version) : failure!) : Unauthorized(http));

        api.MapGet("/catalog/brands", async (HttpContext http, ICatalogService service, int? limit, string? after) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.ListBrandsAsync(tenant.TenantId, PageSize(limit), after, http.RequestAborted)) : Unauthorized(http));
        api.MapGet("/catalog/brands/{id:guid}", async (Guid id, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? WithEtag(http, await service.GetBrandAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapPost("/catalog/brands", async (CreateBrandCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Created(await service.CreateBrandAsync(tenant.TenantId, command, http.RequestAborted), "/api/v1/catalog/brands") : MissingContext(http));
        api.MapPatch("/catalog/brands/{id:guid}", async (Guid id, UpdateBrandCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.UpdateBrandAsync(tenant.TenantId, id, version, command, http.RequestAborted), x => x.Version) : failure!) : Unauthorized(http));

        api.MapGet("/catalog/attributes", async (HttpContext http, ICatalogService service, int? limit, string? after) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.ListAttributesAsync(tenant.TenantId, PageSize(limit), after, http.RequestAborted)) : Unauthorized(http));
        api.MapPost("/catalog/attributes", async (CreateAttributeCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Created(await service.CreateAttributeAsync(tenant.TenantId, command, http.RequestAborted), "/api/v1/catalog/attributes") : MissingContext(http));
        api.MapDelete("/catalog/attributes/{id:guid}", async (Guid id, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? Result(await service.DeactivateAttributeAsync(tenant.TenantId, id, version, http.RequestAborted), Results.Ok) : failure!) : Unauthorized(http));
        api.MapPost("/catalog/attributes/{id:guid}/values", async (Guid id, IReadOnlyList<CreateAttributeValueCommand> command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.AddAttributeValuesAsync(tenant.TenantId, id, command, http.RequestAborted), Results.Ok) : MissingContext(http));
        api.MapDelete("/catalog/attributes/{id:guid}/values/{valueId:guid}", async (Guid id, Guid valueId, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? Result(await service.DeactivateAttributeValueAsync(tenant.TenantId, id, valueId, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapGet("/catalog/categories/{id:guid}/attribute-requirements", async (Guid id, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? Result(await service.GetRequirementsAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapPut("/catalog/categories/{id:guid}/attribute-requirements", async (Guid id, IReadOnlyList<AttributeRequirementCommand> command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? Result(await service.ReplaceRequirementsAsync(tenant.TenantId, id, version, command, http.RequestAborted), Results.Ok) : failure!) : Unauthorized(http));

        api.MapGet("/products", async (HttpContext http, ICatalogService service, int? limit, string? after, string? status, string? search, string? platform, string? stock) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.ListProductsAsync(tenant.TenantId, PageSize(limit), after, status, search, platform, stock, http.RequestAborted)) : Unauthorized(http));
        api.MapGet("/products/summary", async (HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.ProductSummaryAsync(tenant.TenantId, http.RequestAborted)) : Unauthorized(http));
        api.MapPost("/products/bulk-status", async (BulkProductStatusCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.BulkSetStatusAsync(tenant.TenantId, command, http.RequestAborted), Results.Ok) : MissingContext(http));
        api.MapPost("/products/bulk-delete", async (BulkProductDeleteCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.BulkDeleteProductsAsync(tenant.TenantId, command, http.RequestAborted), count => Results.Ok(new { deletedCount = count })) : MissingContext(http));
        api.MapGet("/products/{id:guid}", async (Guid id, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? WithEtag(http, await service.GetProductAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapPost("/products", async (CreateProductCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Created(await service.CreateProductAsync(tenant.TenantId, command, http.RequestAborted), "/api/v1/products") : MissingContext(http));
        api.MapPatch("/products/{id:guid}", async (Guid id, UpdateProductCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.UpdateProductAsync(tenant.TenantId, id, version, command, http.RequestAborted), x => x.Version) : failure!) : Unauthorized(http));
        api.MapDelete("/products/{id:guid}", async (Guid id, HttpContext http, ICatalogService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
            return TryIfMatch(http, out var version, out var failure)
                ? Result(await service.DeleteProductAsync(tenant.TenantId, id, version, http.RequestAborted), count => Results.Ok(new { deletedCount = count }))
                : failure!;
        });
        api.MapPost("/products/{id:guid}/archive", async (Guid id, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? (RequireIdempotency(http) is { } keyFailure ? keyFailure : TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.ArchiveProductAsync(tenant.TenantId, id, version, http.RequestAborted), x => x.Version) : failure!) : Unauthorized(http));
        api.MapGet("/products/{id:guid}/listing-profiles/{connectionId:guid}", async (Guid id, Guid connectionId, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? WithEtag(http, await service.GetListingProfileAsync(tenant.TenantId, id, connectionId, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapPut("/products/{id:guid}/listing-profiles/{connectionId:guid}", async (Guid id, Guid connectionId, UpsertListingProfileCommand command, HttpContext http, ICatalogService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var expected = OptionalIfMatch(http, out var malformed); if (malformed is not null) return malformed;
            return WithEtag(http, await service.UpsertListingProfileAsync(tenant.TenantId, id, connectionId, expected, command, http.RequestAborted), x => x.Version);
        });
        api.MapPost("/products/{id:guid}/publication-jobs", async (Guid id, PublicationRequest command, HttpContext http, ICatalogService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
            return Accepted(await service.EnqueuePublicationAsync(tenant.TenantId, id, command.ConnectionId, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted));
        });
        api.MapPost("/products/{id:guid}/update-jobs", async (Guid id, PublicationRequest command, HttpContext http, ICatalogService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
            return Accepted(await service.EnqueueProductUpdateAsync(tenant.TenantId, id, command.ConnectionId, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted));
        });
        api.MapPost("/products/{id:guid}/archive-jobs", async (Guid id, ProductArchiveRequest command, HttpContext http, ICatalogService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
            return Accepted(await service.EnqueueProductArchiveAsync(tenant.TenantId, id, command.ConnectionId, command.Archived, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted));
        });
        api.MapGet("/products/{id:guid}/publication-status/{connectionId:guid}", async (Guid id, Guid connectionId, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? Result(await service.GetPublicationStatusAsync(tenant.TenantId, id, connectionId, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapGet("/files/product-media/{assetId:guid}/content", async (Guid assetId, HttpContext http, AppDbContext db, IPrivateFileStorage storage) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var asset = await db.FileAssets.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Id == assetId && x.Classification == "PRODUCT_MEDIA" && x.Status == "ACTIVE" && x.ArchivedAt == null, http.RequestAborted);
            if (asset is null) return Results.NotFound();
            try
            {
                var content = await storage.OpenReadAsync(tenant.TenantId, asset.RelativePath, http.RequestAborted);
                http.Response.Headers.CacheControl = "private, max-age=300";
                return Results.File(content, asset.MimeType, asset.OriginalNameSafe, enableRangeProcessing: false);
            }
            catch (FileNotFoundException) { return Results.NotFound(); }
        });
        api.MapPost("/files/product-media", UploadProductMediaAsync).DisableAntiforgery();
        api.MapPost("/files/product-media-url", RegisterProductMediaUrlAsync);
        api.MapDelete("/files/product-media", ClearProductMediaAsync);
        api.MapDelete("/files/product-media-variant", ClearProductVariantMediaAsync);

        api.MapGet("/imports", async (HttpContext http, IImportService service, int? limit, string? after) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.ListAsync(tenant.TenantId, PageSize(limit), after, http.RequestAborted)) : Unauthorized(http));
        api.MapPost("/imports", async (CreateImportCommand command, HttpContext http, IImportService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Created(await service.CreateAsync(tenant.TenantId, command, http.RequestAborted), "/api/v1/imports") : MissingContext(http));
        api.MapPost("/imports/{id:guid}/source-file", UploadImportSourceAsync).DisableAntiforgery();
        api.MapPut("/imports/{id:guid}/column-mapping", async (Guid id, UpdateColumnMappingCommand command, HttpContext http, IImportService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.ConfigureColumnsAsync(tenant.TenantId, id, version, command, http.RequestAborted), x => x.Version) : failure!) : Unauthorized(http));
        api.MapPost("/imports/{id:guid}/preview-jobs", async (Guid id, HttpContext http, IImportService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueuePreviewAsync(tenant.TenantId, id, http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));
        api.MapGet("/imports/{id:guid}", async (Guid id, HttpContext http, IImportService service) =>
            Tenant(http) is { } tenant ? WithEtag(http, await service.GetAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapGet("/imports/{id:guid}/candidates", async (Guid id, HttpContext http, IImportService service, int? limit, string? after) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.CandidatesAsync(tenant.TenantId, id, PageSize(limit), after, http.RequestAborted)) : Unauthorized(http));
        api.MapPut("/imports/{id:guid}/decisions/{candidateId:guid}", async (Guid id, Guid candidateId, ImportDecisionCommand command, HttpContext http, IImportService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.DecideAsync(tenant.TenantId, tenant.UserId, id, candidateId, version, command, http.RequestAborted), x => x.Version) : failure!) : Unauthorized(http));
        api.MapPost("/imports/{id:guid}/apply-jobs", async (Guid id, HttpContext http, IImportService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueApplyAsync(tenant.TenantId, id, http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));
        api.MapGet("/imports/{id:guid}/errors.csv", async (Guid id, HttpContext http, IImportService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var result = await service.BuildErrorsCsvAsync(tenant.TenantId, id, http.RequestAborted);
            return result.Succeeded ? Results.Text(result.Value!, "text/csv; charset=utf-8", Encoding.UTF8) : Problem(http, result.Error!);
        });

        api.MapGet("/inventory", async (HttpContext http, IInventoryService service, int? limit, string? after) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.ListAsync(tenant.TenantId, PageSize(limit), after, http.RequestAborted)) : Unauthorized(http));
        api.MapGet("/inventory/{variantId:guid}/ledger", async (Guid variantId, HttpContext http, IInventoryService service, int? limit, string? after) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.LedgerAsync(tenant.TenantId, variantId, PageSize(limit), after, http.RequestAborted)) : Unauthorized(http));
        api.MapPost("/inventory/{variantId:guid}/adjustments", async (Guid variantId, StockAdjustmentCommand command, HttpContext http, IInventoryService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http); var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
            return WithEtag(http, await service.AdjustAsync(tenant.TenantId, tenant.UserId, variantId, command, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted), x => x.Version);
        });
        api.MapPost("/inventory/stock-sync-jobs", async (HttpContext http, IInventoryService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.ValidateExternalSyncAsync(tenant.TenantId, "STOCK_SYNC", http.RequestAborted)) : MissingContext(http));
        api.MapPost("/channel-offers", async (UpsertChannelOfferCommand command, HttpContext http, IInventoryService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.UpsertOfferAsync(tenant.TenantId, tenant.UserId, command, http.RequestAborted), value => Results.Created($"/api/v1/channel-offers/{value.Id:D}", value)) : MissingContext(http));
        api.MapGet("/channel-offers/{id:guid}", async (Guid id, HttpContext http, IInventoryService service) =>
            Tenant(http) is { } tenant ? WithEtag(http, await service.GetOfferAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapPatch("/channel-offers/{id:guid}", async (Guid id, UpdateChannelOfferCommand command, HttpContext http, IInventoryService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.UpdateOfferAsync(tenant.TenantId, tenant.UserId, id, version, command, http.RequestAborted), x => x.Version) : failure!) : Unauthorized(http));
        api.MapPost("/channel-offers/price-sync-jobs", async (HttpContext http, IInventoryService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.ValidateExternalSyncAsync(tenant.TenantId, "PRICE_SYNC", http.RequestAborted)) : MissingContext(http));
        api.MapPost("/connections/{connectionId:guid}/price-inventory-sync-jobs", async (Guid connectionId, HttpContext http, IInventoryService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueuePriceInventorySyncAsync(tenant.TenantId, connectionId, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));

        MapReferenceEndpoints(api);
        return endpoints;
    }

    private static void MapReferenceEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/reference-data/categories", (Guid connectionId, HttpContext http, IReferenceDataService service) => Reference(http, service, connectionId, "CATEGORIES", null));
        api.MapGet("/reference-data/categories/{externalId}/attributes", (string externalId, Guid connectionId, HttpContext http, IReferenceDataService service) => Reference(http, service, connectionId, "CATEGORY_ATTRIBUTES", externalId));
        api.MapGet("/reference-data/categories/{categoryId}/attributes/{attributeId}/values", (string categoryId, string attributeId, Guid connectionId, HttpContext http, IReferenceDataService service) => Reference(http, service, connectionId, "ATTRIBUTE_VALUES", $"{categoryId}/{attributeId}"));
        api.MapGet("/reference-data/brands", (Guid connectionId, HttpContext http, IReferenceDataService service) => Reference(http, service, connectionId, "BRANDS", null));
        foreach (var type in new[] { "categories", "brands", "attributes", "attribute-values" })
        {
            var routeType = type;
            api.MapGet($"/mappings/{routeType}", async (Guid connectionId, string? scopeExternalId, HttpContext http, IReferenceDataService service) =>
                Tenant(http) is { } tenant ? Result(await service.ListMappingsAsync(tenant.TenantId, routeType, connectionId, scopeExternalId, http.RequestAborted), Results.Ok) : Unauthorized(http));
            api.MapGet($"/mappings/{routeType}/{{localId:guid}}", async (Guid localId, Guid connectionId, string? scopeExternalId, HttpContext http, IReferenceDataService service) =>
                Tenant(http) is { } tenant ? Result(await service.GetMappingAsync(tenant.TenantId, routeType, localId, connectionId, scopeExternalId, http.RequestAborted), Results.Ok) : Unauthorized(http));
            api.MapPut($"/mappings/{routeType}/{{localId:guid}}", async (Guid localId, UpsertCatalogMappingCommand command, HttpContext http, IReferenceDataService service) =>
            {
                if (Tenant(http) is not { } tenant) return Unauthorized(http); var expected = OptionalIfMatch(http, out var malformed); if (malformed is not null) return malformed;
                return WithEtag(http, await service.UpsertMappingAsync(tenant.TenantId, routeType, localId, expected, command, http.RequestAborted), x => x.Version);
            });
            api.MapDelete($"/mappings/{routeType}/{{localId:guid}}", async (Guid localId, Guid connectionId, string? scopeExternalId, HttpContext http, IReferenceDataService service) =>
                Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? Result(await service.DeleteMappingAsync(tenant.TenantId, routeType, localId, connectionId, scopeExternalId, version, http.RequestAborted), Results.Ok) : failure!) : Unauthorized(http));
        }
    }

    private static async Task<IResult> Reference(HttpContext http, IReferenceDataService service, Guid connectionId, string resourceType, string? parent)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        return Result(await service.ListAsync(tenant.TenantId, connectionId, resourceType, parent, http.RequestAborted), Results.Ok);
    }

    private static async Task<IResult> UploadImportSourceAsync(Guid id, HttpContext http, IImportService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http); var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
        if (!http.Request.HasFormContentType) return Problem(http, new("VALIDATION_FAILED", "Multipart form-data gereklidir.", 422));
        var form = await http.Request.ReadFormAsync(http.RequestAborted); var file = form.Files.GetFile("file"); if (file is null) return Problem(http, new("VALIDATION_FAILED", "file zorunludur.", 422));
        await using var source = file.OpenReadStream(); var result = await service.AttachSourceAsync(tenant.TenantId, id, new(file.FileName, file.ContentType, source, file.Length), http.RequestAborted);
        return WithEtag(http, result, x => x.Version);
    }

    private static async Task<IResult> UploadProductMediaAsync(HttpContext http, AppDbContext db, IPrivateFileStorage storage, TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http); var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
        if (!http.Request.HasFormContentType) return Problem(http, new("VALIDATION_FAILED", "Multipart form-data gereklidir.", 422));
        var form = await http.Request.ReadFormAsync(http.RequestAborted); var file = form.Files.GetFile("file");
        if (file is null || !Guid.TryParse(form["productId"], out var productId)) return Problem(http, new("VALIDATION_FAILED", "file ve productId zorunludur.", 422));
        var variantId = Guid.TryParse(form["variantId"], out var parsedVariant) ? parsedVariant : (Guid?)null;
        if (!await db.Products.AnyAsync(x => x.TenantId == tenant.TenantId && x.Id == productId, http.RequestAborted) || variantId is Guid variant && !await db.ProductVariants.AnyAsync(x => x.TenantId == tenant.TenantId && x.ProductId == productId && x.Id == variant, http.RequestAborted)) return Problem(http, new("RESOURCE_NOT_FOUND", "Ürün veya varyant bulunamadı.", 404));
        if (file.Length is <= 0 or > MaxUploadBytes || file.ContentType is not ("image/jpeg" or "image/png")) return Problem(http, new("VALIDATION_FAILED", "Yalnız en fazla 6 MiB JPEG veya PNG kabul edilir.", 422));
        await using var input = file.OpenReadStream(); await using var buffer = new MemoryStream(); await input.CopyToAsync(buffer, http.RequestAborted); var bytes = buffer.ToArray();
        var validMagic = file.ContentType == "image/jpeg" ? bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff : bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        if (!validMagic) return Problem(http, new("VALIDATION_FAILED", "Dosya imzası MIME türüyle eşleşmiyor.", 422));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)); var asset = await db.FileAssets.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Classification == "PRODUCT_MEDIA" && x.Sha256 == hash && x.ArchivedAt == null, http.RequestAborted);
        if (asset is null) { buffer.Position = 0; var id = Guid.CreateVersion7(); var stored = await storage.SaveAsync(tenant.TenantId, $"{id:N}{(file.ContentType == "image/jpeg" ? ".jpg" : ".png")}", file.ContentType, buffer, MaxUploadBytes, http.RequestAborted); asset = new FileAsset { Id = id, TenantId = tenant.TenantId, Classification = "PRODUCT_MEDIA", RelativePath = stored, OriginalNameSafe = Path.GetFileName(file.FileName), MimeType = file.ContentType, SizeBytes = bytes.Length, Sha256 = hash, Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() }; db.FileAssets.Add(asset); }
        var media = new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, ProductId = productId, VariantId = variantId, FileAssetId = asset.Id, MediaRole = string.IsNullOrWhiteSpace(form["mediaRole"]) ? "GALLERY" : form["mediaRole"].ToString().Trim(), SortOrder = int.TryParse(form["sortOrder"], out var sort) ? sort : 0, AltText = string.IsNullOrWhiteSpace(form["altText"]) ? null : form["altText"].ToString().Trim(), Status = "ACTIVE" }; db.ProductMedia.Add(media); await db.SaveChangesAsync(http.RequestAborted);
        return Results.Created($"/api/v1/products/{productId:D}", new { media.Id, media.ProductId, media.VariantId, media.FileAssetId, media.MediaRole, media.SortOrder, media.AltText, media.Status });
    }

    private static async Task<IResult> RegisterProductMediaUrlAsync(RegisterProductMediaUrl command, HttpContext http, AppDbContext db, TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
        if (!Uri.TryCreate(command.Url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsoluteUri.Length > 512)
            return Problem(http, new("PRODUCT_MEDIA_URL_INVALID", "Kalıcı, kullanıcı bilgisi içermeyen ve en fazla 512 karakterlik HTTPS görsel URL'si gereklidir.", 422, new Dictionary<string, string[]> { ["url"] = ["Geçerli bir HTTPS görsel URL'si girin."] }));
        if (uri.IsLoopback || !IsPublicHost(uri.Host) || IPAddress.TryParse(uri.Host, out var address) && !IsPublicAddress(address))
            return Problem(http, new("PRODUCT_MEDIA_URL_INVALID", "Yerel, özel ağ veya loopback görsel adresi kullanılamaz.", 422, new Dictionary<string, string[]> { ["url"] = ["Trendyol tarafından internet üzerinden erişilebilen bir adres girin."] }));
        if (command.SortOrder is < 0 or > 999) return Problem(http, new("PRODUCT_MEDIA_SORT_INVALID", "sortOrder 0-999 arasında olmalıdır.", 422));
        if (!await db.Products.AnyAsync(x => x.TenantId == tenant.TenantId && x.Id == command.ProductId, http.RequestAborted) || command.VariantId is Guid variantId && !await db.ProductVariants.AnyAsync(x => x.TenantId == tenant.TenantId && x.ProductId == command.ProductId && x.Id == variantId, http.RequestAborted))
            return Problem(http, new("RESOURCE_NOT_FOUND", "Ürün veya varyant bulunamadı.", 404));

        var url = uri.AbsoluteUri;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var asset = await db.FileAssets.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.RelativePath == url, http.RequestAborted);
        if (asset is not null && asset.Classification != "PRODUCT_MEDIA_URL") return Problem(http, new("PRODUCT_MEDIA_URL_CONFLICT", "Bu URL farklı bir dosya sınıfında kayıtlı.", 409));
        if (asset is null)
        {
            var originalName = Path.GetFileName(uri.AbsolutePath);
            asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, Classification = "PRODUCT_MEDIA_URL", RelativePath = url, OriginalNameSafe = string.IsNullOrWhiteSpace(originalName) ? null : originalName[..Math.Min(originalName.Length, 256)], MimeType = "image/remote", SizeBytes = 0, Sha256 = hash, Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() };
            db.FileAssets.Add(asset);
        }
        else
        {
            asset.Status = "ACTIVE";
            asset.ArchivedAt = null;
        }

        var media = await db.ProductMedia.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.ProductId == command.ProductId && x.VariantId == command.VariantId && x.SortOrder == command.SortOrder, http.RequestAborted);
        if (media is null)
        {
            media = new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, ProductId = command.ProductId, VariantId = command.VariantId, FileAssetId = asset.Id, MediaRole = MediaRole(command.MediaRole), SortOrder = command.SortOrder, AltText = SafeAltText(command.AltText), Status = "ACTIVE" };
            db.ProductMedia.Add(media);
        }
        else
        {
            media.FileAssetId = asset.Id;
            media.MediaRole = MediaRole(command.MediaRole);
            media.AltText = SafeAltText(command.AltText);
            media.Status = "ACTIVE";
        }
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Created($"/api/v1/products/{command.ProductId:D}", new { media.Id, media.ProductId, media.VariantId, media.FileAssetId, url, media.MediaRole, media.SortOrder, media.AltText, media.Status });
    }

    private static async Task<IResult> ClearProductVariantMediaAsync(Guid productId, Guid variantId, HttpContext http, AppDbContext db)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
        if (!await db.Products.AnyAsync(x => x.TenantId == tenant.TenantId && x.Id == productId)
            || !await db.ProductVariants.AnyAsync(x => x.TenantId == tenant.TenantId && x.ProductId == productId && x.Id == variantId))
            return Problem(http, new("RESOURCE_NOT_FOUND", "Ürün veya varyant bulunamadı.", 404));

        var media = await db.ProductMedia
            .Where(x => x.TenantId == tenant.TenantId && x.ProductId == productId && x.VariantId == variantId && x.Status == "ACTIVE")
            .ToListAsync(http.RequestAborted);
        foreach (var item in media) item.Status = "ARCHIVED";
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> ClearProductMediaAsync(Guid productId, HttpContext http, AppDbContext db)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
        if (!await db.Products.AnyAsync(x => x.TenantId == tenant.TenantId && x.Id == productId))
            return Problem(http, new("RESOURCE_NOT_FOUND", "Ürün bulunamadı.", 404));

        var media = await db.ProductMedia
            .Where(x => x.TenantId == tenant.TenantId && x.ProductId == productId && x.VariantId == null && x.Status == "ACTIVE")
            .ToListAsync(http.RequestAborted);
        foreach (var item in media) item.Status = "ARCHIVED";
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast;
        var bytes = address.GetAddressBytes();
        return bytes[0] is not 0 and not 10 and not 127
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && bytes[1] == 168)
            && bytes[0] < 224;
    }

    private static bool IsPublicHost(string host)
    {
        var normalized = host.TrimEnd('.');
        return !string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase)
            && !normalized.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            && !normalized.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            && !normalized.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);
    }

    private static string MediaRole(string? value)
    {
        var role = string.IsNullOrWhiteSpace(value) ? "GALLERY" : value.Trim().ToUpperInvariant();
        return role[..Math.Min(role.Length, 32)];
    }

    private static string? SafeAltText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return text[..Math.Min(text.Length, 320)];
    }

    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
    private static int PageSize(int? limit) => limit is null ? 50 : limit is >= 1 and <= 200 ? limit.Value : throw new ArgumentException("limit 1-200 arasında olmalıdır.");
    private static IResult? RequireIdempotency(HttpContext http) => string.IsNullOrWhiteSpace(http.Request.Headers["Idempotency-Key"]) ? Problem(http, new("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key başlığı zorunludur.", 400)) : http.Request.Headers["Idempotency-Key"].ToString().Length > 256 ? Problem(http, new("IDEMPOTENCY_KEY_INVALID", "Idempotency-Key en fazla 256 karakterdir.", 400)) : null;
    private static bool TryIfMatch(HttpContext http, out long version, out IResult? failure) { var parsed = OptionalIfMatch(http, out failure); version = parsed ?? 0; if (failure is null && parsed is null) failure = Problem(http, new("PRECONDITION_REQUIRED", "If-Match gereklidir.", 428)); return failure is null; }
    private static long? OptionalIfMatch(HttpContext http, out IResult? failure) { failure = null; var value = http.Request.Headers.IfMatch.ToString(); if (string.IsNullOrWhiteSpace(value)) return null; if (value.Length >= 4 && value.StartsWith("\"v", StringComparison.Ordinal) && value.EndsWith('"') && long.TryParse(value[2..^1], out var version) && version > 0) return version; failure = Problem(http, new("INVALID_ETAG", "If-Match güçlü ETag biçiminde olmalıdır: \"v{version}\".", 400)); return null; }
    private static IResult WithEtag<T>(HttpContext http, ServiceResult<T> result, Func<T, long> version) { if (!result.Succeeded) return Problem(http, result.Error!); http.Response.Headers.ETag = $"\"v{version(result.Value!)}\""; return Results.Ok(result.Value); }
    private static IResult Created<T>(ServiceResult<T> result, string collection) { if (!result.Succeeded) return Results.Json(ToProblem(result.Error!, null), statusCode: result.Error!.Status); var id = typeof(T).GetProperty("Id")?.GetValue(result.Value); return Results.Created(id is Guid guid ? $"{collection}/{guid:D}" : collection, result.Value); }
    private static IResult Accepted(ServiceResult<Guid> result) => result.Succeeded ? Results.Accepted($"/api/v1/jobs/{result.Value:D}", new { jobId = result.Value }) : Results.Json(ToProblem(result.Error!, null), statusCode: result.Error!.Status);
    private static IResult Result<T>(ServiceResult<T> result, Func<T, IResult> success) => result.Succeeded ? success(result.Value!) : Results.Json(ToProblem(result.Error!, null), statusCode: result.Error!.Status);
    private static IResult Unauthorized(HttpContext http) => Problem(http, new("AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401));
    private static IResult MissingContext(HttpContext http) => Tenant(http) is null ? Unauthorized(http) : RequireIdempotency(http) ?? Problem(http, new("REQUEST_INVALID", "İstek tamamlanamadı.", 400));
    private static IResult Problem(HttpContext http, ServiceError error) => Results.Json(ToProblem(error, http.TraceIdentifier), statusCode: error.Status, contentType: "application/problem+json");
    private static object ToProblem(ServiceError error, string? correlationId) => new { type = $"https://marketplacehub.invalid/problems/{error.Code.ToLowerInvariant().Replace('_', '-')}", title = error.Message, status = error.Status, code = error.Code, correlationId, retryable = error.Status is 429 or >= 500, fieldErrors = error.FieldErrors };
    public sealed record PublicationRequest(Guid ConnectionId);
    public sealed record ProductArchiveRequest(Guid ConnectionId, bool Archived);
    public sealed record RegisterProductMediaUrl(Guid ProductId, Guid? VariantId, string Url, string? MediaRole, int SortOrder, string? AltText);
}
