using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Application.Tests;

// KATALOG-14 D5 step B1: products.FamilyId is NOT NULL, so a card "whose link is still to come" (a failed or late link, a
// previous release's write during a deploy) is no longer a NULL link but a card left on a model of its own ("#<card id>",
// its brand and values, as ProductModelLinks.EnsureOwnModelsAsync opens one) although it may carry a code.
internal static class ModelLinkTestStates
{
    public static async Task PutOnOwnModelAsync(AppDbContext db, Guid tenantId, params Guid[] productIds)
    {
        foreach (var productId in productIds)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO catalog.product_models ("Id", "TenantId", "BrandId", "ModelCode", "ModelCodeKey", "Title", "TitleColor", "Description", "CategoryId", "VatRate", "VatInclusion", "WarrantyMonths", "DeliveryDays", "FieldsSavedAt", "CreatedAt", "UpdatedAt", "Version")
                SELECT {Guid.CreateVersion7()}, p."TenantId", p."BrandId", '', {ProductModelLinks.OwnModelKeyPrefix + productId.ToString("N")}, left(p."Title", 320), NULL, p."Description", p."CategoryId", p."VatRate", p."VatInclusion", p."WarrantyMonths", p."DeliveryDays", NULL, now(), now(), 1
                FROM catalog.products p WHERE p."TenantId" = {tenantId} AND p."Id" = {productId}
                ON CONFLICT DO NOTHING
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE catalog.products p SET "FamilyId" = m."Id" FROM catalog.product_models m
                WHERE p."TenantId" = {tenantId} AND p."Id" = {productId} AND m."TenantId" = p."TenantId" AND m."ModelCodeKey" = {ProductModelLinks.OwnModelKeyPrefix + productId.ToString("N")}
                """);
        }
    }
}
