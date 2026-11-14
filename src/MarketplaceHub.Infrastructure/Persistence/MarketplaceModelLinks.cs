using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// MODEL-KAYDI-PLANI Faz 2, Shopify first: a model's own marketplace listing (catalog.marketplace_model_links). Shopify
// keeps a model with all its colours as one product while the panel keeps one card per colour, so the multi-colour
// product has no card-level product link (by design, ReconcileShopifyPublication). After an import or match run saved its
// variant links, each marketplace product read is recorded against the model its variants belong to, when that is
// unambiguous:
// - every panel card holding one of the product's variants is linked to one and the same model (a card without a model,
//   KENE01 style, records nothing);
// - no card of that model holds a variant of another marketplace product on the same connection (a model listed as one
//   product per colour is not one listing).
// A model already linked to another marketplace product keeps that link (mapping only links, as for product links).
// Nothing is sent to the marketplace; the card and variant links are not touched.
public static class MarketplaceModelLinks
{
    public static async Task RecordAsync(AppDbContext db, Guid tenantId, Guid connectionId, IEnumerable<(string ExternalProductId, IReadOnlyCollection<string> ExternalVariantIds)> products, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var (externalProductId, externalVariantIds) in products)
        {
            if (string.IsNullOrWhiteSpace(externalProductId) || externalVariantIds.Count == 0) continue;
            await db.Database.ExecuteSqlRawAsync("""
                WITH cards AS (
                    SELECT DISTINCT p."Id", p."FamilyId"
                    FROM catalog.marketplace_variant_links l
                    JOIN catalog.product_variants v ON v."TenantId" = l."TenantId" AND v."Id" = l."VariantId"
                    JOIN catalog.products p ON p."TenantId" = v."TenantId" AND p."Id" = v."ProductId"
                    WHERE l."TenantId" = @tenant AND l."ConnectionId" = @connection AND l."ExternalId" = ANY(@variants)
                ), one AS (
                    SELECT min("FamilyId"::text)::uuid AS "ModelId" FROM cards
                    HAVING count(*) > 0 AND count("FamilyId") = count(*) AND count(DISTINCT "FamilyId") = 1
                )
                INSERT INTO catalog.marketplace_model_links AS m ("Id", "TenantId", "ConnectionId", "ModelId", "ExternalId", "CreatedAt", "LastSeenAt", "Version")
                SELECT @id, @tenant, @connection, one."ModelId", @external, @now, @now, 1
                FROM one
                WHERE NOT EXISTS (
                    SELECT 1 FROM catalog.products p
                    JOIN catalog.product_variants v ON v."TenantId" = p."TenantId" AND v."ProductId" = p."Id"
                    JOIN catalog.marketplace_variant_links l ON l."TenantId" = v."TenantId" AND l."VariantId" = v."Id" AND l."ConnectionId" = @connection
                    WHERE p."TenantId" = @tenant AND p."FamilyId" = one."ModelId" AND NOT (l."ExternalId" = ANY(@variants)))
                ON CONFLICT ("TenantId", "ConnectionId", "ModelId") DO UPDATE SET "LastSeenAt" = EXCLUDED."LastSeenAt", "Version" = m."Version" + 1
                WHERE m."ExternalId" = EXCLUDED."ExternalId"
                """,
                [
                    new NpgsqlParameter("id", Guid.CreateVersion7()),
                    new NpgsqlParameter("tenant", tenantId),
                    new NpgsqlParameter("connection", connectionId),
                    new NpgsqlParameter("variants", externalVariantIds.Distinct(StringComparer.Ordinal).ToArray()),
                    new NpgsqlParameter("external", externalProductId),
                    new NpgsqlParameter("now", now),
                ],
                cancellationToken);
        }
    }
}
