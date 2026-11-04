using System.Text;
using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MarketplaceHub.Infrastructure.Persistence;

// İşlem Takibi › Loglar › Ürün Hareketleri: one list of what changed on the products, read from the records that keep
// it, never copied into a table of its own:
//   - the stock ledger's stock-changing moves (orders, manual edits, file and marketplace imports, return restock) and
//     an order's stock shortfall (shown as information: stock did not change),
//   - return dispositions kept out of stock (Karantina, Hasarlı, Gelmedi; a restocked line is its ledger row),
//   - marketplace offer prices a user set, and a price read from a marketplace only when it differs from the one before,
//   - product status and panel price changes (catalog.product_change_entries, from 4 Nov 2026 on).
// Each source is read newest first over its own (tenant, time, id) index, at most a page plus one row, and the page is
// cut from their union, so a page costs a few short index scans whatever the history holds. Read-only.
public sealed class ProductActivityService(AppDbContext db) : IProductActivityService
{
    private const string LikeEscape = "\\";
    internal const int MaximumSearchVariants = 1_000;
    private const string UuidPattern = "'^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'";

    public async Task<ServiceResult<PageResult<ProductActivityView>>> PageAsync(Guid tenantId, ProductActivityQuery request, CancellationToken cancellationToken)
    {
        var empty = ServiceResult<PageResult<ProductActivityView>>.Ok(new([], null, false));
        DateTimeOffset? afterAt = null;
        Guid? afterId = null;
        if (!string.IsNullOrWhiteSpace(request.After))
        {
            if (!JobListCursor.TryDecode(request.After, out var cursorAt, out var cursorId))
                return ServiceResult<PageResult<ProductActivityView>>.Fail("PRODUCT_ACTIVITY_CURSOR_INVALID", "Listenin devamı okunamadı. Listeyi yenileyip tekrar deneyin.", 400);
            afterAt = cursorAt.ToUniversalTime();
            afterId = cursorId;
        }
        var kinds = Codes(request.Kinds, ProductActivityKinds.All);
        if (kinds is { Length: 0 }) return empty;
        var filter = await FilterAsync(tenantId, request, cancellationToken);
        if (filter is null) return empty;
        var limit = request.EffectiveLimit;
        var parameters = filter.Parameters(tenantId);
        parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit + 1 });
        if (afterAt is not null)
        {
            parameters.Add(new NpgsqlParameter("after_at", NpgsqlDbType.TimestampTz) { Value = afterAt.Value });
            parameters.Add(new NpgsqlParameter("after_id", NpgsqlDbType.Uuid) { Value = afterId!.Value });
        }
        var branches = Branches(kinds ?? ProductActivityKinds.All, filter, afterAt is not null)
            .Select(branch => $"({branch.Select} {branch.From} WHERE {branch.Where} ORDER BY {branch.Time} DESC, {branch.Id} DESC LIMIT @limit)")
            .ToList();
        if (branches.Count == 0) return empty;
        var sql = $"SELECT * FROM ({string.Join("\nUNION ALL\n", branches)}) activity ORDER BY \"OccurredAt\" DESC, \"Id\" DESC LIMIT @limit";
        var rows = await db.Database.SqlQueryRaw<ProductActivityRow>(sql, parameters.ToArray()).ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        if (hasMore) rows = rows.Take(limit).ToList();
        var nextCursor = hasMore ? JobListCursor.Encode(rows[^1].OccurredAt, rows[^1].Id) : null;
        return ServiceResult<PageResult<ProductActivityView>>.Ok(new(await ViewsAsync(tenantId, rows, cancellationToken), nextCursor, hasMore));
    }

    // The summary above the list: rows per kind under the list's filters, the kind filter left out.
    public async Task<ServiceResult<IReadOnlyList<ProductActivityCount>>> CountsAsync(Guid tenantId, ProductActivityQuery request, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(tenantId, request, cancellationToken);
        if (filter is null) return ServiceResult<IReadOnlyList<ProductActivityCount>>.Ok([]);
        var parameters = filter.Parameters(tenantId);
        var branches = Branches(ProductActivityKinds.All, filter, withCursor: false)
            .Select(branch => $"SELECT {branch.KindSql} AS \"Kind\", count(*)::int AS \"Count\" {branch.From} WHERE {branch.Where} GROUP BY 1");
        var sql = string.Join("\nUNION ALL\n", branches);
        var counts = await db.Database.SqlQueryRaw<ProductActivityCountRow>(sql, parameters.ToArray()).ToListAsync(cancellationToken);
        return ServiceResult<IReadOnlyList<ProductActivityCount>>.Ok(counts
            .GroupBy(x => x.Kind, StringComparer.Ordinal)
            .Select(group => new ProductActivityCount(group.Key, group.Sum(x => x.Count)))
            .OrderBy(x => x.Kind, StringComparer.Ordinal)
            .ToList());
    }

    private sealed record Branch(string Select, string From, string Where, string Time, string Id, string KindSql);

    // The filters every branch shares: the time bounds, the connections a platform filter names, and the variants and
    // products a search names.
    private sealed record Filter(DateTimeOffset? From, DateTimeOffset? To, Guid[]? ConnectionIds, Guid[]? VariantIds, Guid[]? ProductIds)
    {
        public List<NpgsqlParameter> Parameters(Guid tenantId)
        {
            var list = new List<NpgsqlParameter> { new("tenant", NpgsqlDbType.Uuid) { Value = tenantId } };
            if (From is { } from) list.Add(new("from_at", NpgsqlDbType.TimestampTz) { Value = from });
            if (To is { } to) list.Add(new("to_at", NpgsqlDbType.TimestampTz) { Value = to });
            if (ConnectionIds is not null) list.Add(new("connections", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ConnectionIds });
            if (VariantIds is not null) list.Add(new("variants", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = VariantIds });
            if (ProductIds is not null) list.Add(new("products", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ProductIds });
            return list;
        }
    }

    // Null when nothing can match (a platform with no connection, a search that names no product).
    private async Task<Filter?> FilterAsync(Guid tenantId, ProductActivityQuery request, CancellationToken cancellationToken)
    {
        Guid[]? connectionIds = null;
        var platforms = Codes(request.Platforms, null);
        if (platforms is not null)
        {
            connectionIds = await db.PlatformConnections.AsNoTracking()
                .Where(x => x.TenantId == tenantId && platforms.Contains(x.PlatformCode))
                .Select(x => x.Id)
                .ToArrayAsync(cancellationToken);
            if (connectionIds.Length == 0) return null;
        }
        Guid[]? variantIds = null, productIds = null;
        var term = request.Search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            if (term.Length > ProductActivityQuery.MaximumSearchLength) term = term[..ProductActivityQuery.MaximumSearchLength];
            var pattern = $"%{CatalogService.EscapeLike(term)}%";
            var matches = await (from variant in db.ProductVariants.AsNoTracking()
                                 join product in db.Products.AsNoTracking() on new { variant.TenantId, variant.ProductId } equals new { product.TenantId, ProductId = product.Id }
                                 where variant.TenantId == tenantId
                                     && (EF.Functions.ILike(variant.Sku, pattern, LikeEscape)
                                         || (variant.Barcode != null && EF.Functions.ILike(variant.Barcode, pattern, LikeEscape))
                                         || (variant.ModelCode != null && EF.Functions.ILike(variant.ModelCode, pattern, LikeEscape))
                                         || EF.Functions.ILike(product.Title, pattern, LikeEscape))
                                 select new { variant.Id, variant.ProductId })
                .Take(MaximumSearchVariants)
                .ToListAsync(cancellationToken);
            if (matches.Count == 0) return null;
            variantIds = matches.Select(x => x.Id).Distinct().ToArray();
            productIds = matches.Select(x => x.ProductId).Distinct().ToArray();
        }
        return new(request.From?.ToUniversalTime(), request.To?.ToUniversalTime(), connectionIds, variantIds, productIds);
    }

    private static IEnumerable<Branch> Branches(IReadOnlyCollection<string> kinds, Filter filter, bool withCursor)
    {
        // Stock ledger: the stock kind's moves and the return kind's restock, one scan.
        var ledgerTypes = new List<string>();
        if (kinds.Contains(ProductActivityKinds.Stock)) ledgerTypes.AddRange(ProductActivityTypes.StockLedger);
        if (kinds.Contains(ProductActivityKinds.Return)) ledgerTypes.AddRange(ProductActivityTypes.ReturnLedger);
        if (ledgerTypes.Count > 0)
        {
            const string connection = $"""COALESCE(o."ConnectionId", c."ConnectionId", CASE WHEN e."SourceType" = 'MARKETPLACE_CATALOG' AND e."SourceId" ~ {UuidPattern} THEN e."SourceId"::uuid END)""";
            yield return new(
                $"""
                SELECT e."Id" AS "Id", e."OccurredAt" AS "OccurredAt", e."MovementType"::text AS "Type", i."VariantId" AS "VariantId", v."ProductId" AS "ProductId",
                    e."QuantityDelta" AS "QuantityDelta", NULL::numeric AS "PreviousSalePrice", NULL::numeric AS "NewSalePrice", NULL::numeric AS "PreviousListPrice", NULL::numeric AS "NewListPrice",
                    NULL::text AS "Currency", NULL::text AS "PreviousStatus", NULL::text AS "NewStatus", e."SourceType"::text AS "SourceType", e."SourceId"::text AS "Detail",
                    {connection} AS "ConnectionId", COALESCE(o."Id", c."OrderId") AS "OrderId", d."ClaimId" AS "ReturnId", e."ActorUserId" AS "ActorUserId"
                """,
                """
                FROM inventory.stock_ledger_entries e
                JOIN inventory.inventory_items i ON i."TenantId" = e."TenantId" AND i."Id" = e."InventoryItemId"
                LEFT JOIN catalog.product_variants v ON v."TenantId" = i."TenantId" AND v."Id" = i."VariantId"
                LEFT JOIN sales.orders o ON o."TenantId" = e."TenantId" AND o."Id" = (CASE WHEN e."CorrelationId" ~ '^order:[0-9a-f]{32}$' THEN substr(e."CorrelationId", 7)::uuid END)
                LEFT JOIN sales.return_stock_dispositions d ON e."MovementType" = 'RETURN_PASS' AND d."TenantId" = e."TenantId" AND d."IdempotencyKey" = e."IdempotencyKey"
                LEFT JOIN sales.return_claims c ON c."TenantId" = d."TenantId" AND c."Id" = d."ClaimId"
                """,
                Where($"""e."TenantId" = @tenant AND e."MovementType" IN ({Literals(ledgerTypes)})""", filter, withCursor, "e.\"OccurredAt\"", "e.\"Id\"", connection, "i.\"VariantId\"", null),
                "e.\"OccurredAt\"", "e.\"Id\"",
                $"""CASE WHEN e."MovementType" = '{ProductActivityTypes.ReturnPass}' THEN '{ProductActivityKinds.Return}' ELSE '{ProductActivityKinds.Stock}' END""");
        }
        // Returns kept out of stock: information, the stock did not change.
        if (kinds.Contains(ProductActivityKinds.Return))
        {
            yield return new(
                """
                SELECT d."Id" AS "Id", d."CreatedAt" AS "OccurredAt", 'RETURN_' || upper(d."Disposition") AS "Type", i."VariantId" AS "VariantId", v."ProductId" AS "ProductId",
                    d."Quantity" AS "QuantityDelta", NULL::numeric AS "PreviousSalePrice", NULL::numeric AS "NewSalePrice", NULL::numeric AS "PreviousListPrice", NULL::numeric AS "NewListPrice",
                    NULL::text AS "Currency", NULL::text AS "PreviousStatus", NULL::text AS "NewStatus", 'RETURN_DISPOSITION'::text AS "SourceType", d."Reason"::text AS "Detail",
                    c."ConnectionId" AS "ConnectionId", c."OrderId" AS "OrderId", d."ClaimId" AS "ReturnId", d."ActorUserId" AS "ActorUserId"
                """,
                """
                FROM sales.return_stock_dispositions d
                JOIN inventory.inventory_items i ON i."TenantId" = d."TenantId" AND i."Id" = d."InventoryItemId"
                LEFT JOIN catalog.product_variants v ON v."TenantId" = i."TenantId" AND v."Id" = i."VariantId"
                JOIN sales.return_claims c ON c."TenantId" = d."TenantId" AND c."Id" = d."ClaimId"
                """,
                Where("""d."TenantId" = @tenant AND d."Disposition" <> 'Pass'""", filter, withCursor, "d.\"CreatedAt\"", "d.\"Id\"", "c.\"ConnectionId\"", "i.\"VariantId\"", null),
                "d.\"CreatedAt\"", "d.\"Id\"", $"'{ProductActivityKinds.Return}'");
        }
        if (kinds.Contains(ProductActivityKinds.Price))
        {
            // A user's price always counts (the first one too: it was set then); a marketplace import's only when it
            // differs from the version before, a repeat of the same price is no change.
            yield return new(
                """
                SELECT h."Id" AS "Id", h."EffectiveAt" AS "OccurredAt", 'OFFER_PRICE'::text AS "Type", o."VariantId" AS "VariantId", v."ProductId" AS "ProductId",
                    NULL::numeric AS "QuantityDelta", p."SalePrice" AS "PreviousSalePrice", h."SalePrice" AS "NewSalePrice", p."ListPrice" AS "PreviousListPrice", h."ListPrice" AS "NewListPrice",
                    h."Currency"::text AS "Currency", NULL::text AS "PreviousStatus", NULL::text AS "NewStatus", h."ActorSource"::text AS "SourceType", h."Reason"::text AS "Detail",
                    o."ConnectionId" AS "ConnectionId", NULL::uuid AS "OrderId", NULL::uuid AS "ReturnId", NULL::uuid AS "ActorUserId"
                """,
                """
                FROM inventory.channel_price_history h
                JOIN inventory.channel_offers o ON o."TenantId" = h."TenantId" AND o."Id" = h."OfferId"
                LEFT JOIN catalog.product_variants v ON v."TenantId" = o."TenantId" AND v."Id" = o."VariantId"
                LEFT JOIN inventory.channel_price_history p ON p."TenantId" = h."TenantId" AND p."OfferId" = h."OfferId" AND p."PriceVersion" = h."PriceVersion" - 1
                """,
                Where("""
                    h."TenantId" = @tenant
                    AND (h."ActorSource" LIKE 'USER:%' OR (p."Id" IS NOT NULL AND (p."SalePrice" <> h."SalePrice" OR p."ListPrice" <> h."ListPrice")))
                    AND (p."Id" IS NULL OR p."SalePrice" <> h."SalePrice" OR p."ListPrice" <> h."ListPrice")
                    """, filter, withCursor, "h.\"EffectiveAt\"", "h.\"Id\"", "o.\"ConnectionId\"", "o.\"VariantId\"", null),
                "h.\"EffectiveAt\"", "h.\"Id\"", $"'{ProductActivityKinds.Price}'");
        }
        var changeKinds = new List<string>();
        if (kinds.Contains(ProductActivityKinds.Status)) changeKinds.Add(ProductChangeKinds.Status);
        if (kinds.Contains(ProductActivityKinds.Price)) changeKinds.Add(ProductChangeKinds.DefaultPrice);
        if (changeKinds.Count > 0)
        {
            yield return new(
                """
                SELECT x."Id" AS "Id", x."OccurredAt" AS "OccurredAt", x."Kind"::text AS "Type", x."VariantId" AS "VariantId", x."ProductId" AS "ProductId",
                    NULL::numeric AS "QuantityDelta", x."PreviousSalePrice" AS "PreviousSalePrice", x."NewSalePrice" AS "NewSalePrice", x."PreviousListPrice" AS "PreviousListPrice", x."NewListPrice" AS "NewListPrice",
                    NULL::text AS "Currency", x."PreviousStatus"::text AS "PreviousStatus", x."NewStatus"::text AS "NewStatus", x."Source"::text AS "SourceType", x."Note"::text AS "Detail",
                    x."ConnectionId" AS "ConnectionId", NULL::uuid AS "OrderId", NULL::uuid AS "ReturnId", x."ActorUserId" AS "ActorUserId"
                """,
                "FROM catalog.product_change_entries x",
                Where($"""x."TenantId" = @tenant AND x."Kind" IN ({Literals(changeKinds)})""", filter, withCursor, "x.\"OccurredAt\"", "x.\"Id\"", "x.\"ConnectionId\"", "x.\"VariantId\"", "x.\"ProductId\""),
                "x.\"OccurredAt\"", "x.\"Id\"",
                $"""CASE WHEN x."Kind" = '{ProductChangeKinds.Status}' THEN '{ProductActivityKinds.Status}' ELSE '{ProductActivityKinds.Price}' END""");
        }
    }

    // A branch's conditions: its own, then the shared filters on its time, connection and variant (a card-wide change,
    // without a variant, matches by its product).
    private static string Where(string own, Filter filter, bool withCursor, string time, string id, string connection, string variant, string? product)
    {
        var where = new StringBuilder(own);
        if (withCursor) where.Append($" AND ({time}, {id}) < (@after_at, @after_id)");
        if (filter.From is not null) where.Append($" AND {time} >= @from_at");
        if (filter.To is not null) where.Append($" AND {time} <= @to_at");
        if (filter.ConnectionIds is not null) where.Append($" AND {connection} = ANY(@connections)");
        if (filter.VariantIds is not null)
            where.Append(product is null
                ? $" AND {variant} = ANY(@variants)"
                : $" AND ({variant} = ANY(@variants) OR ({variant} IS NULL AND {product} = ANY(@products)))");
        return where.ToString();
    }

    // Constant codes of this class only, never user input.
    private static string Literals(IEnumerable<string> values) => string.Join(", ", values.Select(value => $"'{value}'"));

    private static string[]? Codes(IReadOnlyList<string>? values, IReadOnlyList<string>? known)
    {
        var codes = (values ?? []).Select(code => code.Trim().ToUpperInvariant()).Where(code => code.Length > 0).Distinct(StringComparer.Ordinal).Take(20).ToArray();
        if (codes.Length == 0) return null;
        return known is null ? codes : codes.Where(code => known.Contains(code, StringComparer.Ordinal)).ToArray();
    }

    private async Task<IReadOnlyList<ProductActivityView>> ViewsAsync(Guid tenantId, List<ProductActivityRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];
        var variantIds = rows.Where(x => x.VariantId is not null).Select(x => x.VariantId!.Value).Distinct().ToArray();
        var variants = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && variantIds.Contains(x.Id))
            .Select(x => new { x.Id, x.ProductId, x.Sku, x.Barcode, x.ModelCode, x.OptionSignature })
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var productIds = rows.Select(x => x.ProductId).Concat(variants.Values.Select(x => (Guid?)x.ProductId)).Where(x => x is not null).Select(x => x!.Value).Distinct().ToArray();
        var products = await db.Products.AsNoTracking()
            .Where(x => x.TenantId == tenantId && productIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Title })
            .ToDictionaryAsync(x => x.Id, x => x.Title, cancellationToken);
        var media = await (from item in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on new { item.TenantId, item.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                           where item.TenantId == tenantId && productIds.Contains(item.ProductId) && item.Status == "ACTIVE" && asset.Status == "ACTIVE"
                               && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                           orderby item.SortOrder, item.Id
                           select new { item.ProductId, item.VariantId, asset.Id, asset.Classification, Url = asset.RelativePath }).ToListAsync(cancellationToken);
        var connectionIds = rows.Where(x => x.ConnectionId is not null).Select(x => x.ConnectionId!.Value).Distinct().ToArray();
        var connections = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && connectionIds.Contains(x.Id))
            .Select(x => new { x.Id, x.PlatformCode, x.DisplayName })
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var orderIds = rows.Where(x => x.OrderId is not null).Select(x => x.OrderId!.Value).Distinct().ToArray();
        var orders = await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && orderIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.OrderNumber, cancellationToken);
        var returnIds = rows.Where(x => x.ReturnId is not null).Select(x => x.ReturnId!.Value).Distinct().ToArray();
        var returns = await db.ReturnClaims.AsNoTracking()
            .Where(x => x.TenantId == tenantId && returnIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.ExternalClaimId, cancellationToken);
        var actors = rows.Select(Actor).Where(x => x is not null).Select(x => x!.Value).Distinct().ToArray();
        var users = await db.Users.AsNoTracking()
            .Where(x => actors.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, cancellationToken);
        string? Image(Guid? variantId, Guid productId)
        {
            var picked = (variantId is null ? null : media.FirstOrDefault(x => x.VariantId == variantId))
                ?? media.FirstOrDefault(x => x.ProductId == productId && x.VariantId == null)
                ?? media.FirstOrDefault(x => x.ProductId == productId);
            return picked is null ? null : CatalogMediaDisplay.Url(picked.Id, picked.Classification, picked.Url);
        }
        return rows.Select(row =>
        {
            var variant = row.VariantId is { } id ? variants.GetValueOrDefault(id) : null;
            Guid? productId = variant?.ProductId ?? row.ProductId;
            var title = productId is { } pid ? products.GetValueOrDefault(pid) : null;
            var connection = row.ConnectionId is { } cid ? connections.GetValueOrDefault(cid) : null;
            var actor = Actor(row);
            var type = row.Type.ToUpperInvariant();
            var options = variant?.OptionSignature?.Trim();
            return new ProductActivityView(row.Id, row.OccurredAt, ProductActivityPolicy.KindOfType(type), type,
                title is null ? null : productId, title, productId is { } imageProduct && title is not null ? Image(row.VariantId, imageProduct) : null,
                row.VariantId, variant?.Sku, variant?.Barcode, variant?.ModelCode, string.IsNullOrEmpty(options) || options == "-" ? null : options,
                row.QuantityDelta, row.PreviousSalePrice, row.NewSalePrice, row.PreviousListPrice, row.NewListPrice, row.Currency?.Trim(),
                row.PreviousStatus, row.NewStatus, Source(row), Note(row),
                connection is null ? row.ConnectionId : connection.Id, connection?.PlatformCode, connection?.DisplayName,
                row.OrderId, row.OrderId is { } oid ? orders.GetValueOrDefault(oid) : null,
                row.ReturnId, row.ReturnId is { } rid ? returns.GetValueOrDefault(rid) : null,
                actor, actor is { } uid ? users.GetValueOrDefault(uid) : null);
        }).ToList();
    }

    private static Guid? Actor(ProductActivityRow row) =>
        row.Type == ProductActivityTypes.OfferPrice ? ProductActivityPolicy.PriceHistoryActor(row.SourceType) : row.ActorUserId;

    private static string Source(ProductActivityRow row) => row.Type switch
    {
        ProductActivityTypes.OfferPrice => ProductActivityPolicy.PriceHistorySource(row.SourceType, row.Detail),
        ProductActivityTypes.Status or ProductActivityTypes.DefaultPrice => row.SourceType,
        _ when row.SourceType == "RETURN_DISPOSITION" => ProductActivitySources.Return,
        _ => ProductActivityPolicy.LedgerSource(row.Type, row.SourceType, row.Detail)
    };

    // The free text worth showing: a manual stock change's or return disposition's reason, a change entry's note. An
    // id the ledger keeps as its source (an order line, a connection, a return number) is not text.
    private static string? Note(ProductActivityRow row) => row.Type switch
    {
        ProductActivityTypes.ManualAdjustment => row.Detail,
        ProductActivityTypes.Status or ProductActivityTypes.DefaultPrice => row.Detail,
        _ when row.SourceType == "RETURN_DISPOSITION" => row.Detail,
        _ => null
    };
}

// One row of the union, as the SQL names its columns.
internal sealed class ProductActivityRow
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Type { get; set; } = "";
    public Guid? VariantId { get; set; }
    public Guid? ProductId { get; set; }
    public decimal? QuantityDelta { get; set; }
    public decimal? PreviousSalePrice { get; set; }
    public decimal? NewSalePrice { get; set; }
    public decimal? PreviousListPrice { get; set; }
    public decimal? NewListPrice { get; set; }
    public string? Currency { get; set; }
    public string? PreviousStatus { get; set; }
    public string? NewStatus { get; set; }
    public string SourceType { get; set; } = "";
    public string? Detail { get; set; }
    public Guid? ConnectionId { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? ReturnId { get; set; }
    public Guid? ActorUserId { get; set; }
}

internal sealed class ProductActivityCountRow
{
    public string Kind { get; set; } = "";
    public int Count { get; set; }
}
