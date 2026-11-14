using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// MODEL-KAYDI-PLANI Faz 2: a model's own marketplace listing (catalog.marketplace_model_links). The panel keeps one card
// per colour while a marketplace keeps a model as one listing (Shopify product, PTT AVM product, Pazarama group, N11
// productMainId) or groups its colour contents under one model id (Trendyol productMainId), which no colour card's
// product link can hold. After an import or match run saved its variant links, each marketplace model read is recorded
// against the model its variants belong to, when that is unambiguous:
// - every panel card holding one of the listing's variants is linked to one and the same model (a card without a model,
//   KENE01 style, records nothing);
// - no card of that model holds a variant of another listing on the same connection (a model listed as one product per
//   colour is not one listing).
// A model already linked to another listing keeps that link (mapping only links, as for product links).
// Nothing is sent to the marketplace; the card and variant links are not touched.
//
// The model-level id per marketplace (ModelListings): Shopify the product id; Trendyol the productMainId; N11
// "main:<productMainId>" (the product id its import gives a model); Pazarama "group:<groupCode>" with the groupCode as
// ExternalCode; PTT AVM the product id (UrunId) with its product barcode as ExternalCode (for a model with variants
// usually the model code; a one-variant product may carry the variant barcode). Hepsiburada has no model: nothing is written, unknown stays unknown. A product without its
// marketplace's model id (N11 "n11:", Pazarama "code:", Trendyol without productMainId) records nothing either.
public static class MarketplaceModelLinks
{
    public sealed record Listing(string ExternalId, string? ExternalCode, IReadOnlyCollection<string> ExternalVariantIds);

    // A model link a reader may use: its model still has a card holding a variant link on the connection, and no card of
    // the model holds a product link there to another listing (MarketplaceModelLinks.Verified).
    public sealed record Verified(Guid ModelId, string ExternalId, string? ExternalCode);

    public static IReadOnlyList<Listing> ModelListings(string? platformCode, IReadOnlyCollection<RemoteCatalogProduct> snapshots)
    {
        switch (platformCode?.ToUpperInvariant())
        {
            case "SHOPIFY":
                return [.. snapshots.Where(x => !string.IsNullOrWhiteSpace(x.ExternalProductId)).Select(x => new Listing(Cut(x.ExternalProductId), null, Variants([x])))];
            case "TRENDYOL":
                return [.. snapshots.Where(x => !string.IsNullOrWhiteSpace(x.ProductMainId))
                    .GroupBy(x => Cut(x.ProductMainId!), StringComparer.Ordinal)
                    .Select(group => new Listing(group.Key, null, Variants(group)))];
            case "N11":
                return [.. snapshots.Where(x => x.ExternalProductId.Trim().StartsWith("main:", StringComparison.Ordinal) && x.ExternalProductId.Trim().Length > 5)
                    .GroupBy(x => Cut(x.ExternalProductId), StringComparer.Ordinal)
                    .Select(group => new Listing(group.Key, null, Variants(group)))];
            case "PAZARAMA":
                return [.. snapshots.Where(x => x.ExternalProductId.Trim().StartsWith("group:", StringComparison.Ordinal) && x.ExternalProductId.Trim().Length > 6)
                    .GroupBy(x => Cut(x.ExternalProductId), StringComparer.Ordinal)
                    .Select(group => new Listing(group.Key, group.Key[6..], Variants(group)))];
            case "PTTAVM":
                return [.. snapshots.Where(x => !string.IsNullOrWhiteSpace(x.ExternalProductId))
                    .GroupBy(x => Cut(x.ExternalProductId), StringComparer.Ordinal)
                    .Select(group => new Listing(group.Key, group.Select(x => x.ProductMainId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) is { } code ? Cut(code) : null, Variants(group)))];
            default:
                return [];
        }
    }

    private static string Cut(string value) => value.Trim().Length <= 256 ? value.Trim() : value.Trim()[..256];

    private static string[] Variants(IEnumerable<RemoteCatalogProduct> group) =>
        [.. group.SelectMany(x => x.Variants).Select(x => x.ExternalVariantId).Where(x => !string.IsNullOrWhiteSpace(x)).Select(Cut).Distinct(StringComparer.Ordinal)];

    public static Task RecordAsync(AppDbContext db, Guid tenantId, Guid connectionId, IEnumerable<(string ExternalProductId, IReadOnlyCollection<string> ExternalVariantIds)> products, DateTimeOffset now, CancellationToken cancellationToken) =>
        RecordAsync(db, tenantId, connectionId, products.Select(x => new Listing(x.ExternalProductId, null, x.ExternalVariantIds)), now, cancellationToken);

    public static async Task RecordAsync(AppDbContext db, Guid tenantId, Guid connectionId, IEnumerable<Listing> listings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var (externalProductId, externalCode, externalVariantIds) in listings)
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
                INSERT INTO catalog.marketplace_model_links AS m ("Id", "TenantId", "ConnectionId", "ModelId", "ExternalId", "ExternalCode", "CreatedAt", "LastSeenAt", "Version")
                SELECT @id, @tenant, @connection, one."ModelId", @external, @code, @now, @now, 1
                FROM one
                WHERE NOT EXISTS (
                    SELECT 1 FROM catalog.products p
                    JOIN catalog.product_variants v ON v."TenantId" = p."TenantId" AND v."ProductId" = p."Id"
                    JOIN catalog.marketplace_variant_links l ON l."TenantId" = v."TenantId" AND l."VariantId" = v."Id" AND l."ConnectionId" = @connection
                    WHERE p."TenantId" = @tenant AND p."FamilyId" = one."ModelId" AND NOT (l."ExternalId" = ANY(@variants)))
                ON CONFLICT ("TenantId", "ConnectionId", "ModelId") DO UPDATE SET "LastSeenAt" = EXCLUDED."LastSeenAt", "ExternalCode" = COALESCE(EXCLUDED."ExternalCode", m."ExternalCode"), "Version" = m."Version" + 1
                WHERE m."ExternalId" = EXCLUDED."ExternalId"
                """,
                [
                    new NpgsqlParameter("id", Guid.CreateVersion7()),
                    new NpgsqlParameter("tenant", tenantId),
                    new NpgsqlParameter("connection", connectionId),
                    new NpgsqlParameter("variants", externalVariantIds.Distinct(StringComparer.Ordinal).ToArray()),
                    new NpgsqlParameter("external", externalProductId),
                    new NpgsqlParameter("code", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = (object?)externalCode ?? DBNull.Value },
                    new NpgsqlParameter("now", now),
                ],
                cancellationToken);
        }
    }

    // A model link outlives what it was read from when the cards or variants holding it are deleted or move to another
    // model. A link whose model no longer has any card holding a variant link on that connection says nothing any more and
    // is removed (the next import writes it again when the listing is still there). Runs after each model reconcile, for its
    // models (modelIds null: the whole tenant).
    public static Task<int> RemoveOrphansAsync(AppDbContext db, Guid tenantId, Guid[]? modelIds, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync("""
            DELETE FROM catalog.marketplace_model_links m
            WHERE m."TenantId" = @tenant AND (@all OR m."ModelId" = ANY(@models)) AND NOT EXISTS (
                SELECT 1 FROM catalog.products p
                JOIN catalog.product_variants v ON v."TenantId" = p."TenantId" AND v."ProductId" = p."Id"
                JOIN catalog.marketplace_variant_links l ON l."TenantId" = v."TenantId" AND l."VariantId" = v."Id" AND l."ConnectionId" = m."ConnectionId"
                WHERE p."TenantId" = m."TenantId" AND p."FamilyId" = m."ModelId")
            """, [new NpgsqlParameter("tenant", tenantId), new NpgsqlParameter("all", modelIds is null), new NpgsqlParameter("models", modelIds ?? [])], cancellationToken);

    // The model link of a card's model on a connection, only when it still holds (Verified): the model has a card with a
    // variant link on the connection and none of its cards holds a product link there to another listing. Null otherwise:
    // the reader then goes by the card link as before.
    public static async Task<Verified?> ForCardAsync(AppDbContext db, Guid tenantId, Guid connectionId, Guid? familyId, CancellationToken cancellationToken, bool cardLinksAreModelLevel = true)
    {
        if (familyId is not { } modelId) return null;
        var link = await db.MarketplaceModelLinks.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ModelId == modelId)
            .Select(x => new { x.ExternalId, x.ExternalCode })
            .SingleOrDefaultAsync(cancellationToken);
        if (link is null) return null;
        var cards = db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && x.FamilyId == modelId).Select(x => x.Id);
        var heldByVariant = await (from variantLink in db.MarketplaceVariantLinks.AsNoTracking()
                                   join variant in db.ProductVariants.AsNoTracking() on new { variantLink.TenantId, Id = variantLink.VariantId } equals new { variant.TenantId, variant.Id }
                                   where variantLink.TenantId == tenantId && variantLink.ConnectionId == connectionId && cards.Contains(variant.ProductId)
                                   select variantLink.Id).AnyAsync(cancellationToken);
        if (!heldByVariant) return null;
        // Trendyol card links are colour contents, a level below the model: they never name another model listing.
        if (!cardLinksAreModelLevel) return new Verified(modelId, link.ExternalId, link.ExternalCode);
        var otherListing = await db.MarketplaceProductLinks.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && cards.Contains(x.ProductId) && x.ExternalId != link.ExternalId, cancellationToken);
        return otherListing ? null : new Verified(modelId, link.ExternalId, link.ExternalCode);
    }
}
