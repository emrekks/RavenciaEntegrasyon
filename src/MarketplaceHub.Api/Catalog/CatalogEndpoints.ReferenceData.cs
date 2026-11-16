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
    private static void MapReferenceEndpoints(RouteGroupBuilder api)
    {
        // ?externalId= reads one category of the list (with the list's snapshot id) instead of the whole tree.
        api.MapGet("/reference-data/categories", async (Guid connectionId, string? externalId, HttpContext http, IReferenceDataService service) =>
            externalId is null ? await Reference(http, service, connectionId, "CATEGORIES", null)
                : Tenant(http) is { } tenant ? Result(await service.FindCategoryAsync(tenant.TenantId, connectionId, externalId, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<ReferenceDataView>();
        // Several fields' value lists of one category in one read (attributeIds=a,b,c), by field id.
        api.MapGet("/reference-data/categories/{categoryId}/attribute-values", async (string categoryId, Guid connectionId, string? attributeIds, HttpContext http, IReferenceDataService service) =>
            Tenant(http) is { } tenant ? Result(await service.ListAttributeValueReferencesAsync(tenant.TenantId, connectionId, categoryId, SplitIds(attributeIds), http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<IReadOnlyDictionary<string, ReferenceDataView>>();
        api.MapGet("/reference-data/categories/{externalId}/attributes", (string externalId, Guid connectionId, HttpContext http, IReferenceDataService service) => Reference(http, service, connectionId, "CATEGORY_ATTRIBUTES", externalId)).Produces<ReferenceDataView>();
        api.MapGet("/reference-data/categories/{categoryId}/attributes/{attributeId}/values", (string categoryId, string attributeId, Guid connectionId, HttpContext http, IReferenceDataService service) => Reference(http, service, connectionId, "ATTRIBUTE_VALUES", $"{categoryId}/{attributeId}")).Produces<ReferenceDataView>();
        api.MapGet("/reference-data/brands", (Guid connectionId, HttpContext http, IReferenceDataService service) => Reference(http, service, connectionId, "BRANDS", null)).Produces<ReferenceDataView>();
        // A brand list can hold 800k rows: the panel reads its size and age, searches it and resolves saved ids instead.
        api.MapGet("/reference-data/brands/summary", async (Guid connectionId, HttpContext http, IReferenceDataService service) =>
            Tenant(http) is { } tenant ? Result(await service.BrandSummaryAsync(tenant.TenantId, connectionId, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<ReferenceSliceView>();
        api.MapGet("/reference-data/brands/search", async (Guid connectionId, string? q, int? limit, HttpContext http, IReferenceDataService service) =>
            Tenant(http) is { } tenant ? Result(await service.SearchBrandsAsync(tenant.TenantId, connectionId, q, limit, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<ReferenceSliceView>();
        // Marketplace brands a catalog import met that the user has not mapped yet; the import never maps by name.
        api.MapGet("/reference-data/brands/unmapped", async (Guid connectionId, HttpContext http, IReferenceDataService service) =>
            Tenant(http) is { } tenant ? Result(await service.UnmappedBrandsAsync(tenant.TenantId, connectionId, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<IReadOnlyList<UnmappedMarketplaceBrandView>>();
        api.MapGet("/reference-data/brands/lookup", async (Guid connectionId, string? ids, HttpContext http, IReferenceDataService service) =>
            Tenant(http) is { } tenant ? Result(await service.LookupBrandsAsync(tenant.TenantId, connectionId, (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<ReferenceSliceView>();
        api.MapGet("/connections/{connectionId:guid}/attribute-defaults", async (Guid connectionId, HttpContext http, IReferenceDataService service) =>
            Tenant(http) is { } tenant ? Result(await service.ListAttributeDefaultsAsync(tenant.TenantId, connectionId, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<IReadOnlyList<ConnectionAttributeDefaultView>>();
        api.MapPut("/connections/{connectionId:guid}/attribute-defaults/{externalAttributeId}", async (Guid connectionId, string externalAttributeId, UpsertConnectionAttributeDefaultCommand command, HttpContext http, IReferenceDataService service) =>
            Tenant(http) is { } tenant ? Result(await service.UpsertAttributeDefaultAsync(tenant.TenantId, connectionId, externalAttributeId, command, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<ConnectionAttributeDefaultView>().Idempotent(WriteReasons.Replaces);
        api.MapDelete("/connections/{connectionId:guid}/attribute-defaults/{externalAttributeId}", async (Guid connectionId, string externalAttributeId, HttpContext http, IReferenceDataService service) =>
            Tenant(http) is { } tenant ? Result(await service.DeleteAttributeDefaultAsync(tenant.TenantId, connectionId, externalAttributeId, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<bool>().Idempotent(WriteReasons.Removes);
        api.MapGet("/mappings/name-hints", async (Guid attributeId, Guid connectionId, HttpContext http, IReferenceDataService service) =>
            Tenant(http) is { } tenant ? Result(await service.MarketplaceNameHintsAsync(tenant.TenantId, attributeId, connectionId, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<MarketplaceNameHintsView>();
        api.MapGet("/mappings/attribute-name-hints", async (Guid connectionId, HttpContext http, IReferenceDataService service) =>
            Tenant(http) is { } tenant ? Result(await service.AttributeNameHintsAsync(tenant.TenantId, connectionId, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<IReadOnlyDictionary<Guid, IReadOnlyList<string>>>();
        api.MapGet("/mappings/conflicts", async (Guid connectionId, HttpContext http, IReferenceDataService service) =>
            Tenant(http) is { } tenant ? Result(await service.ListMappingConflictsAsync(tenant.TenantId, connectionId, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<MappingConflictsView>();
        api.MapGet("/mappings/attribute-value-progress", async (Guid connectionId, string? categoryExternalId, HttpContext http, IReferenceDataService service) =>
            Tenant(http) is { } tenant ? Result(await service.AttributeValueMappingProgressAsync(tenant.TenantId, connectionId, categoryExternalId ?? "", http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<IReadOnlyDictionary<string, IReadOnlyList<Guid>>>();
        // Several fields' value mappings of one category in one read (attributeIds=a,b,c), by field id.
        api.MapGet("/mappings/attribute-values/batch", async (Guid connectionId, string? categoryExternalId, string? attributeIds, HttpContext http, IReferenceDataService service) =>
            Tenant(http) is { } tenant ? Result(await service.ListAttributeValueMappingsAsync(tenant.TenantId, connectionId, categoryExternalId ?? "", SplitIds(attributeIds), http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<IReadOnlyDictionary<string, IReadOnlyList<CatalogMappingView>>>();
        // "Birebir Eşle" (Shopify): apply=false counts for the confirmation, apply=true maps; it never changes a mapping.
        api.MapPost("/mappings/categories/exact-match", async (Guid connectionId, bool? apply, HttpContext http, IReferenceDataService service) =>
            Tenant(http) is { } tenant ? Result(await service.ExactMatchShopifyCategoriesAsync(tenant.TenantId, connectionId, apply == true, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<ShopifyExactCategoryMatchView>().Idempotent("maps only categories still unmapped to a same-named type; a repeat finds none left (apply=false only counts)");
        foreach (var type in new[] { "categories", "brands", "attributes", "attribute-values" })
        {
            var routeType = type;
            api.MapGet($"/mappings/{routeType}", async (Guid connectionId, string? scopeExternalId, HttpContext http, IReferenceDataService service) =>
                Tenant(http) is { } tenant ? Result(await service.ListMappingsAsync(tenant.TenantId, routeType, connectionId, scopeExternalId, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<IReadOnlyList<CatalogMappingView>>();
            // The local record's mapping, or the JSON null when it has none (MappingOrNull).
            api.MapGet($"/mappings/{routeType}/{{localId:guid}}", async (Guid localId, Guid connectionId, string? scopeExternalId, string? externalId, HttpContext http, IReferenceDataService service) =>
                Tenant(http) is { } tenant ? Result(await service.GetMappingAsync(tenant.TenantId, routeType, localId, connectionId, scopeExternalId, externalId, http.RequestAborted), MappingOrNull) : Unauthorized(http)).ProducesOneOf(StatusCodes.Status200OK, typeof(CatalogMappingView), null);
            api.MapPut($"/mappings/{routeType}/{{localId:guid}}", async (Guid localId, UpsertCatalogMappingCommand command, HttpContext http, IReferenceDataService service) =>
            {
                if (Tenant(http) is not { } tenant) return Unauthorized(http); var expected = OptionalIfMatch(http, out var malformed); if (malformed is not null) return malformed;
                return WithEtag(http, await service.UpsertMappingAsync(tenant.TenantId, routeType, localId, expected, command, http.RequestAborted), x => x.Version);
            }).Produces<CatalogMappingView>().Idempotent(WriteReasons.Replaces);
            api.MapDelete($"/mappings/{routeType}/{{localId:guid}}", async (Guid localId, Guid connectionId, string? scopeExternalId, string? externalId, HttpContext http, IReferenceDataService service) =>
                Tenant(http) is { } tenant ? (TryIfMatch(http, out var version, out var failure) ? Result(await service.DeleteMappingAsync(tenant.TenantId, routeType, localId, connectionId, scopeExternalId, externalId, version, http.RequestAborted), Results.Ok) : failure!) : Unauthorized(http)).Produces<bool>().Idempotent(WriteReasons.Removes);
        }
    }

    private static string[] SplitIds(string? ids) => (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<IResult> Reference(HttpContext http, IReferenceDataService service, Guid connectionId, string resourceType, string? parent)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        return Result(await service.ListAsync(tenant.TenantId, connectionId, resourceType, parent, http.RequestAborted), Results.Ok);
    }
}
