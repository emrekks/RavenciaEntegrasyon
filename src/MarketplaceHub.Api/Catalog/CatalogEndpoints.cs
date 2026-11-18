using System.Net;
using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Api.Common;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Files;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Api.Common.EndpointHelpers;

namespace MarketplaceHub.Api.Catalog;

public static partial class CatalogEndpoints
{
    private const long MaxUploadBytes = 6 * 1024 * 1024;

    // The products list leaves out its empty (null) fields: a 20-family page carried ~29,500 of them. The list screens
    // read every optional field with ?? / ?. / != null, so a missing one reads as null there; GET /products/{id} keeps
    // them (the editor tells null from a value).
    public static readonly System.Text.Json.JsonSerializerOptions ProductListJson = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").WithArgumentErrorsAnswered().WithGates(ApiGate.SignedIn, ApiGate.Operations);

        api.MapCommissionTariffEndpoints();
        api.MapProductTransferEndpoints();

        api.MapGet("/catalog/categories", async (HttpContext http, ICatalogService service, int? limit, string? after) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.ListCategoriesAsync(tenant.TenantId, PageSize(limit), after, http.RequestAborted)) : Unauthorized(http)).Produces<PageResult<CategoryView>>();
        api.MapGet("/catalog/categories/{id:guid}", async (Guid id, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? WithEtag(http, await service.GetCategoryAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http)).Produces<CategoryView>();
        api.MapPost("/catalog/categories", async (CreateCategoryCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Created(await service.CreateCategoryAsync(tenant.TenantId, command, http.RequestAborted), "/api/v1/catalog/categories") : MissingContext(http)).RequireIdempotencyKey().Produces<CategoryView>(StatusCodes.Status201Created);
        api.MapPatch("/catalog/categories/{id:guid}", async (Guid id, UpdateCategoryCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.UpdateCategoryAsync(tenant.TenantId, id, version, command, http.RequestAborted), x => x.Version) : failure!) : Unauthorized(http)).Produces<CategoryView>().IfMatch().Idempotent(WriteReasons.VersionGuarded);

        api.MapGet("/catalog/brands", async (HttpContext http, ICatalogService service, int? limit, string? after) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.ListBrandsAsync(tenant.TenantId, PageSize(limit), after, http.RequestAborted)) : Unauthorized(http)).Produces<PageResult<BrandView>>();
        api.MapGet("/catalog/brands/{id:guid}", async (Guid id, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? WithEtag(http, await service.GetBrandAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http)).Produces<BrandView>();
        api.MapPost("/catalog/brands", async (CreateBrandCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Created(await service.CreateBrandAsync(tenant.TenantId, command, http.RequestAborted), "/api/v1/catalog/brands") : MissingContext(http)).RequireIdempotencyKey().Produces<BrandView>(StatusCodes.Status201Created);
        api.MapPatch("/catalog/brands/{id:guid}", async (Guid id, UpdateBrandCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.UpdateBrandAsync(tenant.TenantId, id, version, command, http.RequestAborted), x => x.Version) : failure!) : Unauthorized(http)).Produces<BrandView>().IfMatch().Idempotent(WriteReasons.VersionGuarded);

        api.MapGet("/catalog/attributes", async (HttpContext http, ICatalogService service, int? limit, string? after) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.ListAttributesAsync(tenant.TenantId, PageSize(limit), after, http.RequestAborted)) : Unauthorized(http)).Produces<PageResult<AttributeView>>();
        api.MapPost("/catalog/attributes", async (CreateAttributeCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Created(await service.CreateAttributeAsync(tenant.TenantId, command, http.RequestAborted), "/api/v1/catalog/attributes") : MissingContext(http)).RequireIdempotencyKey().Produces<AttributeView>(StatusCodes.Status201Created);
        api.MapPatch("/catalog/attributes/{id:guid}", async (Guid id, RenameAttributeCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.RenameAttributeAsync(tenant.TenantId, id, version, command, http.RequestAborted), x => x.Version) : failure!) : Unauthorized(http)).Produces<AttributeView>().IfMatch().Idempotent(WriteReasons.VersionGuarded);
        api.MapDelete("/catalog/attributes/{id:guid}", async (Guid id, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? Result(await service.DeactivateAttributeAsync(tenant.TenantId, id, version, http.RequestAborted), Results.Ok) : failure!) : Unauthorized(http)).Produces<AttributeView>().IfMatch().Idempotent(WriteReasons.VersionGuarded);
        api.MapPost("/catalog/attributes/{id:guid}/values", async (Guid id, IReadOnlyList<CreateAttributeValueCommand> command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.AddAttributeValuesAsync(tenant.TenantId, id, command, http.RequestAborted), Results.Ok) : MissingContext(http)).RequireIdempotencyKey().Produces<AttributeView>();
        api.MapPut("/catalog/attributes/{id:guid}/values/{valueId:guid}", async (Guid id, Guid valueId, UpdateAttributeValueCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.UpdateAttributeValueAsync(tenant.TenantId, id, valueId, version, command, http.RequestAborted), x => x.Version) : failure!) : Unauthorized(http)).Produces<AttributeView>().IfMatch().Idempotent(WriteReasons.VersionGuarded);
        api.MapPut("/catalog/attributes/{id:guid}/values/order", async (Guid id, ReorderAttributeValuesCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.ReorderAttributeValuesAsync(tenant.TenantId, id, version, command, http.RequestAborted), x => x.Version) : failure!) : Unauthorized(http)).Produces<AttributeView>().IfMatch().Idempotent(WriteReasons.VersionGuarded);
        api.MapGet("/catalog/attributes/{id:guid}/used-values", async (Guid id, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? Result(await service.UsedAttributeValueIdsAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<IReadOnlyList<Guid>>();
        api.MapDelete("/catalog/attributes/{id:guid}/values/{valueId:guid}", async (Guid id, Guid valueId, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? Result(await service.DeactivateAttributeValueAsync(tenant.TenantId, id, valueId, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<AttributeView>().Idempotent("deactivates the value; a repeat finds it inactive");
        api.MapGet("/catalog/categories/{id:guid}/attribute-requirements", async (Guid id, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? Result(await service.GetRequirementsAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<IReadOnlyList<CategoryAttributeRequirementView>>();
        api.MapPut("/catalog/categories/{id:guid}/attribute-requirements", async (Guid id, IReadOnlyList<AttributeRequirementCommand> command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? Result(await service.ReplaceRequirementsAsync(tenant.TenantId, id, version, command, http.RequestAborted), Results.Ok) : failure!) : Unauthorized(http)).Produces<IReadOnlyList<AttributeRequirementCommand>>().IfMatch().Idempotent(WriteReasons.VersionGuarded);

        api.MapGet("/products", async (HttpContext http, ICatalogService service, int? limit, string? after, string? status, string? search, string? platform, string? stock, string? sort, string? platforms) =>
            Tenant(http) is { } tenant ? Results.Json(await service.ListProductsAsync(tenant.TenantId, PageSize(limit), after, status, search, SinglePlatform(platforms, platform), stock, sort, http.RequestAborted), ProductListJson) : Unauthorized(http)).Produces<PageResult<ProductView>>();
        api.MapGet("/products/summary", async (HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.ProductSummaryAsync(tenant.TenantId, http.RequestAborted)) : Unauthorized(http)).Produces<ProductSummaryView>();
        api.MapPost("/products/bulk-status", async (BulkProductStatusCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.BulkSetStatusAsync(tenant.TenantId, command, http.RequestAborted, tenant.UserId, http.TraceIdentifier), Results.Ok) : MissingContext(http)).RequireIdempotencyKey().Produces<BulkProductStatusResult>();
        // Toplu Değer Ata: one field on every colour card and variant of the selected models (owner and administrators).
        api.MapPost("/products/bulk-field", async (BulkProductFieldCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.BulkSetFieldAsync(tenant.TenantId, command, http.RequestAborted, tenant.UserId, http.TraceIdentifier), Results.Ok) : MissingContext(http)).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<BulkProductFieldResult>();
        api.MapPost("/products/bulk-delete", async (BulkProductDeleteCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.BulkDeleteProductsAsync(tenant.TenantId, command, http.RequestAborted), deleted => Results.Ok(new { deletedCount = deleted.DeletedCount, cancelledJobCount = deleted.CancelledJobCount })) : MissingContext(http)).RequireIdempotencyKey().Produces<ProductDeleteResult>();
        // The jobs a delete would cancel, for its confirmation; it changes nothing.
        api.MapPost("/products/delete-preview", async (BulkProductDeleteCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? Result(await service.PreviewProductDeleteAsync(tenant.TenantId, command, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<ProductDeletePreview>().Idempotent(WriteReasons.ReadOnly);
        api.MapGet("/products/{id:guid}", async (Guid id, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? WithEtag(http, await service.GetProductAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http)).Produces<ProductView>();
        // Read only: which of the given codes the tenant's catalog already holds as a stock code or a barcode, with the
        // holding card. The editor asks once for a range of candidates before it generates codes from a model code.
        api.MapPost("/catalog/variant-codes/taken", async (TakenVariantCodesRequest request, HttpContext http, AppDbContext db) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var codes = VariantCodeAvailability.RequestedCodes(request.Codes);
            var excluded = VariantCodeAvailability.ExcludedVariantIds(request.ExcludeVariantIds);
            var taken = await VariantCodeAvailability.FindAsync(db, db.ProductVariants.Where(variant => variant.TenantId == tenant.TenantId && !excluded.Contains(variant.Id)), codes, codes, http.RequestAborted);
            return Results.Ok(new { items = taken });
        }).Produces<TakenVariantCodesView>().Idempotent(WriteReasons.ReadOnly);
        // Read only: the card that already holds a model code, for the "Yeni Ürün" editor (a new card may not take it).
        // A model is its brand and its code (owner, 17 Nov 2026): only a card of the same brand (brandId; none: a blank brand) holds it.
        api.MapGet("/catalog/model-codes/holder", async (string? code, Guid? brandId, HttpContext http, AppDbContext db) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var modelCode = code?.Trim() ?? "";
            if (modelCode.Length > ModelCodeUniquenessPolicy.MaxCodeLength) return Problem(http, new("VALIDATION_FAILED", $"Model kodu en fazla {ModelCodeUniquenessPolicy.MaxCodeLength} karakter olabilir.", 422));
            var holder = await ModelCodeOwnership.FindAsync(db, tenant.TenantId, brandId, [modelCode], http.RequestAborted);
            return Results.Ok(new { taken = holder is not null, productId = holder?.ProductId, productTitle = holder?.Title, message = holder is null ? null : ModelCodeUniquenessPolicy.Message(holder.Title) });
        }).Produces<ModelCodeHolderView>();
        api.MapGet("/products/{id:guid}/marketplace-codes", async (Guid id, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? Result(await service.GetProductMarketplaceCodesAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<ProductMarketplaceCodesView>();
        api.MapPut("/products/{id:guid}/marketplace-codes", async (Guid id, UpdateMarketplaceCodesCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.UpdateProductMarketplaceCodesAsync(tenant.TenantId, id, command, http.RequestAborted), Results.Ok) : MissingContext(http)).RequireIdempotencyKey().Produces<ProductMarketplaceCodesView>();
        api.MapPost("/products", async (CreateProductCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Created(await service.CreateProductAsync(tenant.TenantId, command, http.RequestAborted, tenant.UserId, http.TraceIdentifier), "/api/v1/products") : MissingContext(http)).RequireIdempotencyKey().Produces<ProductView>(StatusCodes.Status201Created);
        api.MapPatch("/products/{id:guid}", async (Guid id, UpdateProductCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.UpdateProductAsync(tenant.TenantId, id, version, command, http.RequestAborted, tenant.UserId, http.TraceIdentifier), x => x.Version) : failure!) : Unauthorized(http)).Produces<ProductView>().IfMatch().Idempotent(WriteReasons.VersionGuarded);
        // The panel's own price of a variant; If-Match carries the variant version. Nothing is sent to a marketplace.
        api.MapPatch("/variants/{id:guid}/default-prices", async (Guid id, UpdateVariantDefaultPricesCommand command, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.UpdateVariantDefaultPricesAsync(tenant.TenantId, tenant.UserId, id, version, command, http.TraceIdentifier, http.RequestAborted), x => x.Version) : failure!) : Unauthorized(http)).Produces<VariantDefaultPricesView>().IfMatch().Idempotent(WriteReasons.VersionGuarded);
        api.MapDelete("/products/{id:guid}", async (Guid id, HttpContext http, ICatalogService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
            return TryIfMatch(http, out var version, out var failure)
                ? Result(await service.DeleteProductAsync(tenant.TenantId, id, version, http.RequestAborted), deleted => Results.Ok(new { deletedCount = deleted.DeletedCount, cancelledJobCount = deleted.CancelledJobCount }))
                : failure!;
        }).RequireIdempotencyKey().IfMatch().Produces<ProductDeleteResult>();
        api.MapGet("/products/{id:guid}/listing-profiles/{connectionId:guid}", async (Guid id, Guid connectionId, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? WithEtag(http, await service.GetListingProfileAsync(tenant.TenantId, id, connectionId, http.RequestAborted), x => x.Version) : Unauthorized(http)).Produces<ListingProfileView>();
        api.MapPut("/products/{id:guid}/listing-profiles/{connectionId:guid}", async (Guid id, Guid connectionId, UpsertListingProfileCommand command, HttpContext http, ICatalogService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var expected = OptionalIfMatch(http, out var malformed); if (malformed is not null) return malformed;
            return WithEtag(http, await service.UpsertListingProfileAsync(tenant.TenantId, id, connectionId, expected, command, http.RequestAborted), x => x.Version);
        }).Produces<ListingProfileView>().IfMatch(required: false).Idempotent(WriteReasons.Replaces);
        api.MapPost("/products/{id:guid}/publication-jobs", async (Guid id, PublicationRequest command, HttpContext http, ICatalogService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
            // skippedVariants: what the send left out of a card already on the marketplace, each with its reason.
            var queued = await service.EnqueuePublicationWithSkippedAsync(tenant.TenantId, id, command.ConnectionId, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted);
            return queued.Succeeded ? Results.Accepted($"/api/v1/jobs/{queued.Value!.JobId:D}", new { jobId = queued.Value.JobId, skippedVariants = queued.Value.SkippedVariants }) : MarketplaceHub.Api.Security.ApiProblems.Of(null, queued.Error!);
        }).RequireIdempotencyKey().Produces<ProductPublicationQueuedView>(StatusCodes.Status202Accepted);
        api.MapPost("/products/bulk-publication-jobs", async (BulkPublicationCommand command, HttpContext http, ICatalogService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
            return Result(await service.EnqueueBulkPublicationAsync(tenant.TenantId, tenant.UserId, command, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted), Results.Ok);
        }).RequireIdempotencyKey().Produces<BulkPublicationResult>();
        api.MapPost("/products/{id:guid}/update-jobs", async (Guid id, PublicationRequest command, HttpContext http, ICatalogService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
            // notices: what the panel records in its notification center (a card value sent where the listing profile has none).
            var queued = await service.EnqueueProductUpdateWithNoticesAsync(tenant.TenantId, id, command.ConnectionId, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted, command.Fields, command.SendGroupId);
            return queued.Succeeded ? Results.Accepted($"/api/v1/jobs/{queued.Value!.JobId:D}", new { jobId = queued.Value.JobId, notices = queued.Value.Notices }) : MarketplaceHub.Api.Security.ApiProblems.Of(null, queued.Error!);
        }).RequireIdempotencyKey().Produces<ProductUpdateQueuedView>(StatusCodes.Status202Accepted);
        // The "Platformlarda Güncelle" summary: which fields the update would change on each store, per model, before
        // Gönder. Read only (no job, no write, no marketplace call); at most ProductUpdatePreviewPolicy.MaxProducts cards.
        api.MapPost("/products/update-preview", async (ProductUpdatePreviewRequest command, HttpContext http, IProductUpdatePreviewService service) =>
            Tenant(http) is { } tenant ? Result(await service.PreviewAsync(tenant.TenantId, command, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<ProductUpdatePreviewView>().Idempotent(WriteReasons.ReadOnly);
        api.MapPost("/connections/{connectionId:guid}/sale-status-jobs", async (Guid connectionId, PazaramaSaleStatusRequest command, HttpContext http, ICatalogService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
            return Accepted(await service.EnqueuePazaramaSaleStatusAsync(tenant.TenantId, connectionId, command.Barcodes ?? [], command.Close, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted));
        }).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);
        api.MapPost("/products/{id:guid}/publication-status/{connectionId:guid}/refresh", async (Guid id, Guid connectionId, HttpContext http, ICatalogService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
            return Accepted(await service.RequestPublicationStatusRefreshAsync(tenant.TenantId, id, connectionId, http.RequestAborted));
        }).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);
        api.MapGet("/products/{id:guid}/publication-status/{connectionId:guid}", async (Guid id, Guid connectionId, HttpContext http, ICatalogService service) =>
            Tenant(http) is { } tenant ? Result(await service.GetPublicationStatusAsync(tenant.TenantId, id, connectionId, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<PublicationStatusView>();
        api.MapGet("/public/product-media/{assetId:guid}/content", async (Guid assetId, HttpContext http, AppDbContext db, IPrivateFileStorage storage) =>
        {
            var asset = await (from media in db.ProductMedia.AsNoTracking()
                               join product in EffectiveCatalog.Products(db) on new { media.TenantId, media.ProductId } equals new { product.TenantId, ProductId = product.Id }
                               join file in db.FileAssets.AsNoTracking() on new { media.TenantId, FileAssetId = media.FileAssetId } equals new { file.TenantId, FileAssetId = file.Id }
                               where media.FileAssetId == assetId && media.Status == "ACTIVE" && product.Status != ProductStatus.Archived
                                   && file.Classification == "PRODUCT_MEDIA" && file.Status == "ACTIVE" && file.ArchivedAt == null
                                   // G11: only images a marketplace was given. This address goes out only in publication
                                   // payloads (ProductPublicationComposer and the platform composers), and the submit and
                                   // update requests set the product's listing profile LIVE in the same transaction as their
                                   // job, before the marketplace fetches anything; products linked to a marketplace listing
                                   // (published or imported) stay readable. A draft never submitted is not served.
                                   && (db.ChannelListingProfiles.Any(profile => profile.TenantId == media.TenantId && profile.ProductId == media.ProductId && profile.DesiredStatus == "LIVE")
                                       || db.MarketplaceProductLinks.Any(link => link.TenantId == media.TenantId && link.ProductId == media.ProductId))
                               select new { file.TenantId, file.RelativePath, file.MimeType, file.Sha256 }).FirstOrDefaultAsync(http.RequestAborted);
            if (asset is null || asset.MimeType is not ("image/jpeg" or "image/png")) return Results.NotFound();
            // Always the original: marketplaces read their listing photos here (no thumbnail width).
            var entityTag = ProductMediaCaching.EntityTag(asset.Sha256);
            if (ProductMediaCaching.IsNotModified(http.Request, entityTag)) return ProductMediaCaching.NotModified(http.Response, ProductMediaCaching.PublicImmutable, entityTag);
            try
            {
                var content = await storage.OpenReadAsync(asset.TenantId, asset.RelativePath, http.RequestAborted);
                ProductMediaCaching.Apply(http.Response, ProductMediaCaching.PublicImmutable, entityTag);
                return Results.File(content, asset.MimeType, enableRangeProcessing: false);
            }
            catch (FileNotFoundException) { return Results.NotFound(); }
        }).RequireGate(ApiGate.Open).ProducesFile("image/jpeg", "image/png");
        api.MapGet("/files/product-media/{assetId:guid}/content", async (Guid assetId, HttpContext http, AppDbContext db, IPrivateFileStorage storage, ProductMediaRenditions renditions, IHttpClientFactory clients) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var asset = await db.FileAssets.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Id == assetId && (x.Classification == "PRODUCT_MEDIA" || x.Classification == "PRODUCT_MEDIA_URL") && x.Status == "ACTIVE" && x.ArchivedAt == null, http.RequestAborted);
            if (asset is null) return Results.NotFound();
            if (asset.Classification == "PRODUCT_MEDIA_URL")
            {
                if (!Uri.TryCreate(asset.RelativePath, UriKind.Absolute, out var remoteUri) || remoteUri.Scheme != Uri.UriSchemeHttps || remoteUri.IsLoopback || string.Equals(remoteUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
                // The row's hash is the address's: the tag names the URL, not its bytes, so it is kept for a week only.
                var remoteTag = ProductMediaCaching.EntityTag(asset.Sha256);
                if (ProductMediaCaching.IsNotModified(http.Request, remoteTag)) return ProductMediaCaching.NotModified(http.Response, ProductMediaCaching.PrivateRemote, remoteTag);
                try
                {
                    // The proxy client connects only to public addresses, follows redirects itself and
                    // stops reading past the upload limit.
                    var image = await PublicNetwork.GetImageAsync(clients.CreateClient(PublicNetwork.ClientName), remoteUri, MaxUploadBytes, http.RequestAborted);
                    if (image is not { } found) return Results.NotFound();
                    ProductMediaCaching.Apply(http.Response, ProductMediaCaching.PrivateRemote, remoteTag);
                    http.Response.Headers.ContentSecurityPolicy = ProductMediaCaching.InertImagePolicy;
                    return Results.Bytes(found.Bytes, found.ContentType);
                }
                catch (HttpRequestException) { return Results.NotFound(); }
                catch (TaskCanceledException) { return Results.NotFound(); }
            }
            // ?w=168|480: the small WebP copy (made now when it is still missing); any other width is the original.
            var width = ProductMediaRenditions.RequestedWidth(http.Request.Query["w"].ToString());
            var entityTag = ProductMediaCaching.EntityTag(asset.Sha256, width);
            if (ProductMediaCaching.IsNotModified(http.Request, entityTag)) return ProductMediaCaching.NotModified(http.Response, ProductMediaCaching.PrivateImmutable, entityTag);
            if (width is int thumbnailWidth && asset.MimeType is "image/jpeg" or "image/png")
            {
                var copy = renditions.TryOpen(tenant.TenantId, asset.Id, thumbnailWidth);
                if (copy is null && await renditions.TryEnsureAsync(tenant.TenantId, asset.Id, token => ReadStoredBytesAsync(storage, tenant.TenantId, asset.RelativePath, token), http.RequestAborted))
                    copy = renditions.TryOpen(tenant.TenantId, asset.Id, thumbnailWidth);
                if (copy is not null)
                {
                    ProductMediaCaching.Apply(http.Response, ProductMediaCaching.PrivateImmutable, entityTag);
                    http.Response.Headers.ContentSecurityPolicy = ProductMediaCaching.InertImagePolicy;
                    return Results.File(copy, "image/webp", enableRangeProcessing: false);
                }
            }
            try
            {
                var content = await storage.OpenReadAsync(tenant.TenantId, asset.RelativePath, http.RequestAborted);
                if (width is null) ProductMediaCaching.Apply(http.Response, ProductMediaCaching.PrivateImmutable, entityTag);
                // The original standing in for a thumbnail that could not be made carries no tag and a short life.
                else http.Response.Headers.CacheControl = ProductMediaCaching.PrivateShort;
                http.Response.Headers.ContentSecurityPolicy = ProductMediaCaching.InertImagePolicy;
                return Results.File(content, asset.MimeType, asset.OriginalNameSafe, enableRangeProcessing: false);
            }
            catch (FileNotFoundException) { return Results.NotFound(); }
        }).ProducesFile("image/jpeg", "image/png", "image/webp", "image/gif");
        api.MapPost("/files/product-media", UploadProductMediaAsync).DisableAntiforgery().RequireIdempotencyKey().Produces<ProductMediaView>(StatusCodes.Status201Created);
        api.MapPost("/files/product-media-url", RegisterProductMediaUrlAsync).RequireIdempotencyKey().Produces<ProductMediaUrlView>(StatusCodes.Status201Created);
        api.MapPut("/files/product-media-reconcile", ReconcileProductMediaAsync).RequireIdempotencyKey().Produces<ProductMediaReconciledView>();
        api.MapPut("/files/product-media-family-reorder", ReorderProductFamilyMediaAsync).RequireIdempotencyKey().Produces<ProductFamilyMediaReorderedView>();
        api.MapDelete("/files/product-media-item", DeleteProductMediaItemAsync).RequireIdempotencyKey().IfMatch().Produces<ProductMediaItemDeletedView>();
        api.MapDelete("/files/product-media-items", DeleteProductMediaItemsAsync).RequireIdempotencyKey().Produces<ProductMediaDeletedView>();
        api.MapDelete("/files/product-media", ClearProductMediaAsync).RequireIdempotencyKey().Produces(StatusCodes.Status204NoContent);

        api.MapGet("/imports", async (HttpContext http, IImportService service, int? limit, string? after) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.ListAsync(tenant.TenantId, PageSize(limit), after, http.RequestAborted)) : Unauthorized(http)).Produces<PageResult<ImportSessionView>>();
        api.MapPost("/imports", async (CreateImportCommand command, HttpContext http, IImportService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Created(await service.CreateAsync(tenant.TenantId, command, http.RequestAborted), "/api/v1/imports") : MissingContext(http)).RequireIdempotencyKey().Produces<ImportSessionView>(StatusCodes.Status201Created);
        api.MapPost("/imports/{id:guid}/source-file", UploadImportSourceAsync).DisableAntiforgery().RequireIdempotencyKey().Produces<ImportSessionView>();
        api.MapPut("/imports/{id:guid}/column-mapping", async (Guid id, UpdateColumnMappingCommand command, HttpContext http, IImportService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.ConfigureColumnsAsync(tenant.TenantId, id, version, command, http.RequestAborted), x => x.Version) : failure!) : Unauthorized(http)).Produces<ImportSessionView>().IfMatch().Idempotent(WriteReasons.VersionGuarded);
        api.MapPost("/imports/{id:guid}/preview-jobs", async (Guid id, HttpContext http, IImportService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueuePreviewAsync(tenant.TenantId, id, http.TraceIdentifier, http.RequestAborted)) : MissingContext(http)).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);
        api.MapGet("/imports/{id:guid}", async (Guid id, HttpContext http, IImportService service) =>
            Tenant(http) is { } tenant ? WithEtag(http, await service.GetAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http)).Produces<ImportSessionView>();
        api.MapGet("/imports/{id:guid}/candidates", async (Guid id, HttpContext http, IImportService service, int? limit, string? after) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.CandidatesAsync(tenant.TenantId, id, PageSize(limit), after, http.RequestAborted)) : Unauthorized(http)).Produces<PageResult<ImportCandidateView>>();
        api.MapPut("/imports/{id:guid}/decisions/{candidateId:guid}", async (Guid id, Guid candidateId, ImportDecisionCommand command, HttpContext http, IImportService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.DecideAsync(tenant.TenantId, tenant.UserId, id, candidateId, version, command, http.RequestAborted), x => x.Version) : failure!) : Unauthorized(http)).Produces<ImportCandidateView>().IfMatch().Idempotent(WriteReasons.VersionGuarded);
        api.MapPost("/imports/{id:guid}/apply-jobs", async (Guid id, HttpContext http, IImportService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueApplyAsync(tenant.TenantId, id, http.TraceIdentifier, http.RequestAborted)) : MissingContext(http)).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);
        api.MapGet("/imports/{id:guid}/errors.csv", async (Guid id, HttpContext http, IImportService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var result = await service.BuildErrorsCsvAsync(tenant.TenantId, id, http.RequestAborted);
            return result.Succeeded ? Results.Text(result.Value!, "text/csv; charset=utf-8", Encoding.UTF8) : Problem(http, result.Error!);
        }).RequireGate(ApiGate.Operations).Produces<string>(StatusCodes.Status200OK, "text/csv");

        api.MapGet("/inventory", async (HttpContext http, IInventoryService service, int? limit, string? after) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.ListAsync(tenant.TenantId, PageSize(limit), after, http.RequestAborted)) : Unauthorized(http)).Produces<PageResult<InventoryItemView>>();
        api.MapPut("/inventory/{variantId:guid}/on-hand", async (Guid variantId, StockSetCommand command, HttpContext http, IInventoryService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http); var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
            return WithEtag(http, await service.SetOnHandAsync(tenant.TenantId, tenant.UserId, variantId, command, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted), x => x.Version);
        }).RequireIdempotencyKey().Produces<InventoryItemView>();
        api.MapPost("/channel-offers", async (UpsertChannelOfferCommand command, HttpContext http, IInventoryService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.UpsertOfferAsync(tenant.TenantId, tenant.UserId, command, http.RequestAborted), value => Results.Created($"/api/v1/channel-offers/{value.Id:D}", value)) : MissingContext(http)).RequireIdempotencyKey().Produces<ChannelOfferView>(StatusCodes.Status201Created);
        api.MapGet("/channel-offers/{id:guid}", async (Guid id, HttpContext http, IInventoryService service) =>
            Tenant(http) is { } tenant ? WithEtag(http, await service.GetOfferAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http)).Produces<ChannelOfferView>();
        api.MapPatch("/channel-offers/{id:guid}", async (Guid id, UpdateChannelOfferCommand command, HttpContext http, IInventoryService service) =>
            Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.UpdateOfferAsync(tenant.TenantId, tenant.UserId, id, version, command, http.RequestAborted), x => x.Version) : failure!) : Unauthorized(http)).Produces<ChannelOfferView>().IfMatch().Idempotent(WriteReasons.VersionGuarded);
        // The "Özel Fiyat" choice of the variants' offers on one connection, stored without a price change.
        // Owner rule of 18 Nov 2026: what "Panel Fiyatlarını İlk Pazaryeri Fiyatından Doldur" would write (read only).
        api.MapGet("/channel-offers/panel-price-fill-preview", async (HttpContext http, IInventoryService service) =>
            Tenant(http) is { } tenant ? Results.Ok(await service.PanelPriceFillPreviewAsync(tenant.TenantId, http.RequestAborted)) : Unauthorized(http)).Produces<PanelPriceFillPreview>();
        api.MapPost("/channel-offers/panel-price-fill", async (PanelPriceFillCommand command, HttpContext http, IInventoryService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.ApplyPanelPriceFillAsync(tenant.TenantId, tenant.UserId, command, http.TraceIdentifier, http.RequestAborted), Results.Ok) : MissingContext(http))
            .RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<PanelPriceFillApplied>();
        api.MapPut("/channel-offers/panel-price-follow", async (SetChannelOfferPanelFollowCommand command, HttpContext http, IInventoryService service) =>
            Tenant(http) is { } tenant ? Result(await service.SetOfferPanelFollowAsync(tenant.TenantId, command, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<ChannelOfferPanelFollowView>().Idempotent(WriteReasons.Replaces);
        api.MapPost("/connections/{connectionId:guid}/price-inventory-sync-jobs", async (Guid connectionId, string? stockCode, HttpContext http, IInventoryService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueuePriceInventorySyncAsync(tenant.TenantId, connectionId, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted, stockCode)) : MissingContext(http)).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);
        // The body may name the parts to send ({"price": true, "stock": false}); an empty body sends both, as before.
        api.MapPost("/products/{productId:guid}/connections/{connectionId:guid}/price-inventory-sync-jobs", async (Guid productId, Guid connectionId, PriceInventoryFieldsRequest? command, HttpContext http, IInventoryService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueProductPriceInventorySyncAsync(tenant.TenantId, productId, connectionId, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted, command?.Price ?? true, command?.Stock ?? true)) : MissingContext(http)).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);

        MapReferenceEndpoints(api);
        return endpoints;
    }

    private static async Task<IResult> UploadImportSourceAsync(Guid id, HttpContext http, IImportService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http); var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
        if (!http.Request.HasFormContentType) return Problem(http, new("VALIDATION_FAILED", "Multipart form-data gereklidir.", 422));
        var form = await http.Request.ReadFormAsync(http.RequestAborted); var file = form.Files.GetFile("file"); if (file is null) return Problem(http, new("VALIDATION_FAILED", "file zorunludur.", 422));
        await using var source = file.OpenReadStream(); var result = await service.AttachSourceAsync(tenant.TenantId, id, new(file.FileName, file.ContentType, source, file.Length), http.RequestAborted);
        return WithEtag(http, result, x => x.Version);
    }

    // No mapping yet is a JSON null, not an empty body: Results.Ok(null) writes nothing, and the panel read that as a
    // failed request ("Hepsiburada kategori bilgisi alınamadı") instead of "not mapped".
    public static IResult MappingOrNull(CatalogMappingView? mapping) => mapping is null ? Results.Content("null", "application/json") : Results.Ok(mapping);
    // Fields: "Platformlarda Güncelle" picks (ProductUpdateFields codes); omitted on a publication and by older clients.
    // SendGroupId: set by the panel on every card of one family update (TrendyolUpdateGroups).
    public sealed record PublicationRequest(Guid ConnectionId, IReadOnlyList<string>? Fields = null, Guid? SendGroupId = null);
    public sealed record TakenVariantCodesRequest(IReadOnlyList<string?>? Codes, IReadOnlyList<Guid>? ExcludeVariantIds = null);
    public sealed record PriceInventoryFieldsRequest(bool? Price = null, bool? Stock = null);
    public sealed record PazaramaSaleStatusRequest(IReadOnlyList<string>? Barcodes, bool Close);
    public sealed record RegisterProductMediaUrl(Guid ProductId, Guid? VariantId, string Url, string? MediaRole, int SortOrder, string? AltText);
    public sealed record ReconcileProductMediaCommand(Guid ProductId, Guid? VariantId, IReadOnlyList<ReconcileProductMediaItem>? Items, string? AltText);
    public sealed record ReconcileProductMediaItem(string Url, int SortOrder);
    public sealed record ReorderProductFamilyMediaCommand(Guid ProductId, IReadOnlyList<string>? UrlsInOrder = null, int? SourceIndex = null, int? TargetIndex = null);
    private sealed record ProductFamilyMediaRow(ProductMedia Media, string Url, string ImageKey);
    private sealed record ProductFamilyImageGroup(string Key, string Url, ProductFamilyMediaRow[] Rows, int SortOrder, Guid FirstProductId, Guid FirstMediaId);
    public sealed record DeleteProductMediaItemsCommand(IReadOnlyList<Guid>? MediaIds);
    public sealed record DeleteProductMediaItemCommand(Guid ProductId, string? Url);
}
