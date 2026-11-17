using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MarketplaceHub.Infrastructure.Persistence;

// The marketplace values the panel last knew per listed variant (catalog.marketplace_known_listing_values), behind the
// "Platformlarda Güncelle" summary (ProductUpdatePreviewService). Two writers, each only with what the marketplace itself
// gave, never a value taken from the panel card or the model (unknown stays unknown):
//  - a product import (RecordImportAsync): the title, description and desi the read carried for each variant the run
//    links on the connection, dated with the read (Source IMPORT). Trendyol and Hepsiburada only, the two marketplaces
//    the summary compares; a field the read did not carry (Trendyol answering 0 for a desi never entered, a Hepsiburada
//    listing without a product name) leaves what was known;
//  - an update the marketplace accepted (RecordAcceptedAsync): per variant the fields of a request whose batch line for
//    that variant (Trendyol by barcode or content, Hepsiburada by stock code) came back accepted, dated with the answer
//    (Source SENT). A refused, missing or unclear line writes nothing; a field sent empty (null) makes it unknown, since
//    what the marketplace does with it is not known.
// A value is replaced only by one read or accepted at the same time or later, so an import that read the marketplace
// before an accepted send does not put the older value back. Old code (deploy overlap) never touches the table; a
// variant without a row is unknown.
public static class MarketplaceKnownListingValueStore
{
    public const string ImportSource = "IMPORT";
    public const string SentSource = "SENT";

    // What one source says of one variant. A null value with its Clear flag off says nothing of the field; Clear makes the
    // field unknown.
    public sealed record Write(Guid VariantId, string? Title = null, string? Description = null, decimal? Desi = null, bool ClearTitle = false, bool ClearDescription = false, bool ClearDesi = false)
    {
        public bool SaysAnything => Title is not null || Description is not null || Desi is not null || ClearTitle || ClearDescription || ClearDesi;
    }

    // The values of one request item (Trendyol: title, description, dimensionalWeight; Hepsiburada: productName,
    // productDescription, desi). A field the item names is sent (Present), whatever its value; a value that is not text
    // (or, for the desi, not a real measurement: ImportedDesi) is null.
    public sealed record ItemValues(bool TitlePresent, string? Title, bool DescriptionPresent, string? Description, bool DesiPresent, decimal? Desi);

    public static ItemValues ReadItem(JsonElement item)
    {
        var (titlePresent, title) = TextField(item, "title", "productName");
        var (descriptionPresent, description) = TextField(item, "description", "productDescription");
        var desiPresent = Field(item, out var desi, "dimensionalWeight", "desi");
        return new(titlePresent, title, descriptionPresent, description, desiPresent, desiPresent ? ImportedDesi.FromJson(desi) : null);
    }

    // Import ------------------------------------------------------------------------------------------------------------

    // The values a product read carried, per marketplace variant id (the id its variant link holds). Trendyol: the
    // content's title and description (its raw answer, not the mapper's fallbacks) and each variant's desi. Hepsiburada:
    // each listing's own product name and description, when the listing answer carries them, and its desi.
    public static IReadOnlyList<(string ExternalVariantId, string? Title, string? Description, decimal? Desi)> ImportValues(string? platformCode, RemoteCatalogProduct snapshot)
    {
        var platform = platformCode?.Trim().ToUpperInvariant();
        if (platform is not ("TRENDYOL" or "HEPSIBURADA")) return [];
        var values = new List<(string, string?, string?, decimal?)>();
        string? contentTitle = null, contentDescription = null;
        if (platform == "TRENDYOL") (contentTitle, contentDescription) = RawTexts(snapshot.RawJson, ["title", "name"], ["description"]);
        foreach (var variant in snapshot.Variants)
        {
            var externalId = Cut(variant.ExternalVariantId);
            if (externalId.Length == 0) continue;
            var (title, description) = platform == "TRENDYOL" ? (contentTitle, contentDescription) : RawTexts(variant.RawJson, ["productName", "ProductName", "name", "Name", "title", "Title"], ["description", "Description"]);
            var desi = ImportedDesi.From(variant.Desi);
            if (title is null && description is null && desi is null) continue;
            values.Add((externalId, title, description, desi));
        }
        return values;
    }

    // After an import run saved its variant links: the values its reads carried, for the variants they link on the
    // connection. readAt: when the marketplace was read.
    public static Task RecordImportAsync(AppDbContext db, Guid tenantId, Guid connectionId, string? platformCode, IEnumerable<RemoteCatalogProduct> snapshots, DateTimeOffset readAt, CancellationToken cancellationToken) =>
        RecordImportAsync(db, tenantId, connectionId, platformCode, snapshots, () => readAt, cancellationToken);

    // readAt is asked for only when there is something to write.
    public static async Task RecordImportAsync(AppDbContext db, Guid tenantId, Guid connectionId, string? platformCode, IEnumerable<RemoteCatalogProduct> snapshots, Func<DateTimeOffset> readAt, CancellationToken cancellationToken)
    {
        var byExternalId = new Dictionary<string, (string? Title, string? Description, decimal? Desi)>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
            foreach (var (externalId, title, description, desi) in ImportValues(platformCode, snapshot))
                byExternalId[externalId] = (title, description, desi);
        if (byExternalId.Count == 0) return;
        var externalIds = byExternalId.Keys.ToArray();
        var links = await db.MarketplaceVariantLinks.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && externalIds.Contains(x.ExternalId))
            .Select(x => new { x.ExternalId, x.VariantId })
            .ToListAsync(cancellationToken);
        var writes = links
            .GroupBy(link => link.VariantId)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .Select(link => byExternalId[link.ExternalId] is var value ? new Write(link.VariantId, value.Title, value.Description, value.Desi) : null)
            .OfType<Write>()
            .ToList();
        if (writes.Count == 0) return;
        await UpsertAsync(db, tenantId, connectionId, ImportSource, readAt(), writes, cancellationToken);
    }

    // Accepted send --------------------------------------------------------------------------------------------------------

    public sealed record Listing(Guid VariantId, string? Barcode, string? Sku);

    // The fields one request carried for each variant whose batch line came back accepted. part: the request (UNAPPROVED,
    // CONTENT, VARIANTS, DELIVERY). Trendyol's lines name a barcode, or a contentId for a content request turned per
    // content (TrendyolContentGroups): a content request's values are taken only when every content item of the card came
    // back accepted, no line of the answer is refused, and its items agree on the title and description (one card's data),
    // and then for every variant of the card the request reached (contentNotFound: barcodes left out of it). Hepsiburada's
    // lines name the stock code (merchantSku). Other marketplaces: nothing (the summary does not compare them).
    public static IReadOnlyList<Write> Accepted(string? platformCode, string part, string? requestJson, IEnumerable<RemoteOperationLine> lines, IReadOnlyList<Listing> listings, IReadOnlyCollection<string>? contentNotFound = null)
    {
        var platform = platformCode?.Trim().ToUpperInvariant();
        if (platform is not ("TRENDYOL" or "HEPSIBURADA") || string.IsNullOrWhiteSpace(requestJson)) return [];
        var answer = lines.ToList();
        var accepted = answer.Where(line => !string.IsNullOrWhiteSpace(line.ExternalKey))
            .GroupBy(line => line.ExternalKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.All(line => line.Succeeded), StringComparer.OrdinalIgnoreCase);
        bool Accepted(string? key) => !string.IsNullOrWhiteSpace(key) && accepted.TryGetValue(key.Trim(), out var ok) && ok;

        List<JsonElement> items;
        using var document = ParseItems(requestJson, out items);
        if (items.Count == 0) return [];
        var writes = new Dictionary<Guid, Write>();
        void Add(Guid variantId, ItemValues values)
        {
            var write = new Write(variantId,
                values.TitlePresent ? values.Title : null, values.DescriptionPresent ? values.Description : null, values.DesiPresent ? values.Desi : null,
                values.TitlePresent && values.Title is null, values.DescriptionPresent && values.Description is null, values.DesiPresent && values.Desi is null);
            if (write.SaysAnything) writes[variantId] = write;
        }

        if (platform == "HEPSIBURADA")
        {
            foreach (var item in items)
            {
                var sku = Text(item, "merchantSku");
                if (!Accepted(sku)) continue;
                var listing = listings.Where(x => string.Equals(x.Sku?.Trim(), sku!.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                if (listing.Count == 1) Add(listing[0].VariantId, ReadItem(item));
            }
            return [.. writes.Values];
        }

        var contentItems = new List<JsonElement>();
        foreach (var item in items)
        {
            var barcode = Text(item, "barcode");
            if (barcode is null)
            {
                if (item.TryGetProperty("contentId", out _)) contentItems.Add(item);
                continue;
            }
            if (!Accepted(barcode)) continue;
            var listing = listings.Where(x => string.Equals(x.Barcode?.Trim(), barcode.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (listing.Count == 1) Add(listing[0].VariantId, ReadItem(item));
        }
        if (contentItems.Count > 0 && answer.All(line => line.Succeeded) && contentItems.All(item => Accepted(ContentId(item))))
        {
            var values = contentItems.Select(ReadItem).Select(value => value with { DesiPresent = false, Desi = null }).Distinct().ToList();
            if (values.Count == 1)
            {
                var left = (contentNotFound ?? []).Select(x => x.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var listing in listings.Where(x => !string.IsNullOrWhiteSpace(x.Barcode) && !left.Contains(x.Barcode!.Trim())))
                    if (!writes.ContainsKey(listing.VariantId)) Add(listing.VariantId, values[0]);
            }
        }
        return [.. writes.Values];
    }

    public static Task RecordAcceptedAsync(AppDbContext db, Guid tenantId, Guid connectionId, IReadOnlyCollection<Write> writes, DateTimeOffset acceptedAt, CancellationToken cancellationToken) =>
        UpsertAsync(db, tenantId, connectionId, SentSource, acceptedAt, writes, cancellationToken);

    // Writing ----------------------------------------------------------------------------------------------------------------

    // One upsert for the values and one update per cleared field; a variant gone since (deleted) is skipped.
    internal static async Task UpsertAsync(AppDbContext db, Guid tenantId, Guid connectionId, string source, DateTimeOffset at, IReadOnlyCollection<Write> writes, CancellationToken cancellationToken)
    {
        var rows = writes.Where(x => x.SaysAnything).GroupBy(x => x.VariantId).Select(group => group.Last()).ToList();
        if (rows.Count == 0) return;
        at = at.ToUniversalTime();
        var valued = rows.Where(x => x.Title is not null || x.Description is not null || x.Desi is not null).ToList();
        if (valued.Count > 0)
            await db.Database.ExecuteSqlRawAsync(UpsertSql,
            [
                new NpgsqlParameter("tenant", tenantId),
                new NpgsqlParameter("connection", connectionId),
                new NpgsqlParameter("source", source),
                new NpgsqlParameter("at", at),
                new NpgsqlParameter("variants", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = valued.Select(x => x.VariantId).ToArray() },
                new NpgsqlParameter("titles", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = valued.Select(x => x.Title).ToArray() },
                new NpgsqlParameter("descriptions", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = valued.Select(x => x.Description).ToArray() },
                new NpgsqlParameter("desis", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = valued.Select(x => x.Desi).ToArray() },
            ], cancellationToken);
        foreach (var (sql, cleared) in new[] { (ClearTitleSql, rows.Where(x => x.ClearTitle)), (ClearDescriptionSql, rows.Where(x => x.ClearDescription)), (ClearDesiSql, rows.Where(x => x.ClearDesi)) })
        {
            var ids = cleared.Select(x => x.VariantId).ToArray();
            if (ids.Length == 0) continue;
            await db.Database.ExecuteSqlRawAsync(sql,
                [new NpgsqlParameter("tenant", tenantId), new NpgsqlParameter("connection", connectionId), new NpgsqlParameter("variants", ids), new NpgsqlParameter("at", at)],
                cancellationToken);
        }
    }

    private const string ClearTitleSql = """
        UPDATE catalog.marketplace_known_listing_values SET "Title" = NULL, "TitleSource" = NULL, "TitleReadAt" = NULL, "Version" = "Version" + 1
        WHERE "TenantId" = @tenant AND "ConnectionId" = @connection AND "VariantId" = ANY(@variants) AND ("TitleReadAt" IS NULL OR "TitleReadAt" <= @at)
        """;
    private const string ClearDescriptionSql = """
        UPDATE catalog.marketplace_known_listing_values SET "Description" = NULL, "DescriptionSource" = NULL, "DescriptionReadAt" = NULL, "Version" = "Version" + 1
        WHERE "TenantId" = @tenant AND "ConnectionId" = @connection AND "VariantId" = ANY(@variants) AND ("DescriptionReadAt" IS NULL OR "DescriptionReadAt" <= @at)
        """;
    private const string ClearDesiSql = """
        UPDATE catalog.marketplace_known_listing_values SET "Desi" = NULL, "DesiSource" = NULL, "DesiReadAt" = NULL, "Version" = "Version" + 1
        WHERE "TenantId" = @tenant AND "ConnectionId" = @connection AND "VariantId" = ANY(@variants) AND ("DesiReadAt" IS NULL OR "DesiReadAt" <= @at)
        """;

    // A field is replaced only when the new value is there and not older than the kept one.
    private const string UpsertSql = """
        INSERT INTO catalog.marketplace_known_listing_values AS k
            ("Id", "TenantId", "ConnectionId", "VariantId", "Title", "TitleSource", "TitleReadAt", "Description", "DescriptionSource", "DescriptionReadAt", "Desi", "DesiSource", "DesiReadAt", "Version")
        SELECT gen_random_uuid(), @tenant, @connection, r.variant,
            r.title, CASE WHEN r.title IS NULL THEN NULL ELSE @source END, CASE WHEN r.title IS NULL THEN NULL ELSE @at END,
            r.description, CASE WHEN r.description IS NULL THEN NULL ELSE @source END, CASE WHEN r.description IS NULL THEN NULL ELSE @at END,
            r.desi, CASE WHEN r.desi IS NULL THEN NULL ELSE @source END, CASE WHEN r.desi IS NULL THEN NULL ELSE @at END,
            1
        FROM unnest(@variants, @titles, @descriptions, @desis) AS r(variant, title, description, desi)
        WHERE EXISTS (SELECT 1 FROM catalog.product_variants v WHERE v."TenantId" = @tenant AND v."Id" = r.variant)
        ON CONFLICT ("TenantId", "ConnectionId", "VariantId") DO UPDATE SET
            "Title" = CASE WHEN EXCLUDED."Title" IS NULL OR EXCLUDED."TitleReadAt" < k."TitleReadAt" THEN k."Title" ELSE EXCLUDED."Title" END,
            "TitleSource" = CASE WHEN EXCLUDED."Title" IS NULL OR EXCLUDED."TitleReadAt" < k."TitleReadAt" THEN k."TitleSource" ELSE EXCLUDED."TitleSource" END,
            "TitleReadAt" = CASE WHEN EXCLUDED."Title" IS NULL OR EXCLUDED."TitleReadAt" < k."TitleReadAt" THEN k."TitleReadAt" ELSE EXCLUDED."TitleReadAt" END,
            "Description" = CASE WHEN EXCLUDED."Description" IS NULL OR EXCLUDED."DescriptionReadAt" < k."DescriptionReadAt" THEN k."Description" ELSE EXCLUDED."Description" END,
            "DescriptionSource" = CASE WHEN EXCLUDED."Description" IS NULL OR EXCLUDED."DescriptionReadAt" < k."DescriptionReadAt" THEN k."DescriptionSource" ELSE EXCLUDED."DescriptionSource" END,
            "DescriptionReadAt" = CASE WHEN EXCLUDED."Description" IS NULL OR EXCLUDED."DescriptionReadAt" < k."DescriptionReadAt" THEN k."DescriptionReadAt" ELSE EXCLUDED."DescriptionReadAt" END,
            "Desi" = CASE WHEN EXCLUDED."Desi" IS NULL OR EXCLUDED."DesiReadAt" < k."DesiReadAt" THEN k."Desi" ELSE EXCLUDED."Desi" END,
            "DesiSource" = CASE WHEN EXCLUDED."Desi" IS NULL OR EXCLUDED."DesiReadAt" < k."DesiReadAt" THEN k."DesiSource" ELSE EXCLUDED."DesiSource" END,
            "DesiReadAt" = CASE WHEN EXCLUDED."Desi" IS NULL OR EXCLUDED."DesiReadAt" < k."DesiReadAt" THEN k."DesiReadAt" ELSE EXCLUDED."DesiReadAt" END,
            "Version" = k."Version" + 1
        """;

    // Parsing ----------------------------------------------------------------------------------------------------------------

    private static JsonDocument? ParseItems(string json, out List<JsonElement> items)
    {
        items = [];
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
            items = [.. array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)];
        return document;
    }

    private static (string? Title, string? Description) RawTexts(string? rawJson, string[] titleNames, string[] descriptionNames)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            var title = titleNames.Select(name => Text(document.RootElement, name)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var description = descriptionNames.Select(name => Text(document.RootElement, name)).FirstOrDefault(value => value is not null);
            return (title, description);
        }
        catch (JsonException) { return (null, null); }
    }

    private static (bool Present, string? Value) TextField(JsonElement item, params string[] names)
    {
        if (!Field(item, out var value, names)) return (false, null);
        return (true, value.ValueKind == JsonValueKind.String ? value.GetString() : null);
    }

    private static bool Field(JsonElement item, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (item.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Undefined) return true;
        value = default;
        return false;
    }

    private static string? ContentId(JsonElement item) =>
        item.TryGetProperty("contentId", out var value) ? value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null } : null;

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Cut(string? value)
    {
        var trimmed = value?.Trim() ?? "";
        return trimmed.Length <= 256 ? trimmed : trimmed[..256];
    }
}

// The production source of the summary's marketplace values: the table above. A variant without a row, or a field
// without a value, is not known.
public sealed class MarketplaceKnownListingValuesReader(AppDbContext db) : IMarketplaceKnownListingValues
{
    public async Task<IReadOnlyDictionary<Guid, KnownListingValues>> ReadAsync(Guid tenantId, Guid connectionId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0) return new Dictionary<Guid, KnownListingValues>();
        var ids = variantIds.Distinct().ToArray();
        var rows = await db.MarketplaceKnownListingValues.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && ids.Contains(x.VariantId))
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.VariantId, row => new KnownListingValues(
            Known(row.Title, row.TitleSource, row.TitleReadAt),
            Known(row.Description, row.DescriptionSource, row.DescriptionReadAt),
            row.Desi is { } desi && row.DesiReadAt is { } desiAt && !string.IsNullOrWhiteSpace(row.DesiSource) ? new KnownListingValue<decimal>(desi, row.DesiSource, desiAt) : null));
    }

    private static KnownListingValue<string>? Known(string? value, string? source, DateTimeOffset? at) =>
        value is not null && at is { } readAt && !string.IsNullOrWhiteSpace(source) ? new(value, source, readAt) : null;
}
