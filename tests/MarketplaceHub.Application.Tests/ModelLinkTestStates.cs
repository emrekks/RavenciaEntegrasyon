using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Application.Tests;

// KATALOG-14 D5 step B1: products.FamilyId is NOT NULL, so a card "whose link is still to come" (a failed or late link, a
// previous release's write during a deploy) is no longer a NULL link but a card left on a model of its own ("#<card id>",
// its brand and values, as ProductModelLinks.EnsureOwnModelsAsync opens one) although it may carry a code.
internal static class ModelLinkTestStates
{
    // KATALOG-14 D5 step B2b: the own model takes the values of the model the card is on (its copies may not be kept); a
    // card left on no model (a state only raw SQL makes) gives its own.
    public static async Task PutOnOwnModelAsync(AppDbContext db, Guid tenantId, params Guid[] productIds)
    {
        foreach (var productId in productIds)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO catalog.product_models ("Id", "TenantId", "BrandId", "ModelCode", "ModelCodeKey", "Title", "TitleColor", "Description", "CategoryId", "VatRate", "VatInclusion", "WarrantyMonths", "DeliveryDays", "FieldsSavedAt", "CreatedAt", "UpdatedAt", "Version")
                SELECT {Guid.CreateVersion7()}, p."TenantId", p."BrandId", '', {ProductModelLinks.OwnModelKeyPrefix + productId.ToString("N")}, left(p."Title", 320), NULL,
                       CASE WHEN m."Id" IS NULL THEN p."Description" ELSE m."Description" END, CASE WHEN m."Id" IS NULL THEN p."CategoryId" ELSE m."CategoryId" END,
                       CASE WHEN m."Id" IS NULL THEN p."VatRate" ELSE m."VatRate" END, CASE WHEN m."Id" IS NULL THEN p."VatInclusion" ELSE m."VatInclusion" END,
                       CASE WHEN m."Id" IS NULL THEN p."WarrantyMonths" ELSE m."WarrantyMonths" END, CASE WHEN m."Id" IS NULL THEN p."DeliveryDays" ELSE m."DeliveryDays" END, NULL, now(), now(), 1
                FROM catalog.products p LEFT JOIN catalog.product_models m ON m."TenantId" = p."TenantId" AND m."Id" = p."FamilyId"
                WHERE p."TenantId" = {tenantId} AND p."Id" = {productId}
                ON CONFLICT DO NOTHING
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE catalog.products p SET "FamilyId" = m."Id" FROM catalog.product_models m
                WHERE p."TenantId" = {tenantId} AND p."Id" = {productId} AND m."TenantId" = p."TenantId" AND m."ModelCodeKey" = {ProductModelLinks.OwnModelKeyPrefix + productId.ToString("N")}
                """);
        }
    }
}
