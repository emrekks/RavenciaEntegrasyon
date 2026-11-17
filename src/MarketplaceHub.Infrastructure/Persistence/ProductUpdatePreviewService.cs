using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// The "Platformlarda Güncelle" summary (ProductUpdatePreviewContracts). Read only: the update composers build the payload
// the send would build (they read and never write), nothing is queued or saved and no marketplace is called.
// Compared per colour / size: the title, description and desi in the payload of Trendyol (unapproved full update, or the
// approved content and variant requests) and Hepsiburada (its product package). A field counts as sent only when the
// payload carries it, so a field the marketplace takes only together with a picked one (Trendyol's unapproved full
// update, Hepsiburada's whole package) is compared too. Every other field the update sends, and every field on n11,
// Pazarama, PTT AVM and Shopify, is listed as unknown: the panel keeps no marketplace value to compare it with.
public sealed class ProductUpdatePreviewService(AppDbContext db, IConfiguration configuration, IMarketplaceKnownListingValues knownValues) : IProductUpdatePreviewService
{
    private static readonly string[] ComparedFields = [ProductUpdateFields.Title, ProductUpdateFields.Description, ProductUpdateFields.Desi];

    public async Task<ServiceResult<ProductUpdatePreviewView>> PreviewAsync(Guid tenantId, ProductUpdatePreviewRequest request, CancellationToken cancellationToken)
    {
        // Per store the cards Gönder sends there, in the window's order; a store named twice keeps its first list.
        var targets = (request.Targets ?? []).Where(target => target is not null && target.ConnectionId != Guid.Empty)
            .GroupBy(target => target.ConnectionId).Select(group => (ConnectionId: group.Key, ProductIds: (group.First().ProductIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList()))
            .Where(target => target.ProductIds.Count > 0).ToList();
        var productIds = targets.SelectMany(target => target.ProductIds).Distinct().ToList();
        var connectionIds = targets.Select(target => target.ConnectionId).ToList();
        if (productIds.Count == 0 || connectionIds.Count == 0)
            return Fail("VALIDATION_FAILED", "En az bir ürün ve bir mağaza seçin.");
        if (productIds.Count > ProductUpdatePreviewPolicy.MaxProducts)
            return Fail("PRODUCT_UPDATE_PREVIEW_TOO_MANY", $"Değişiklik özeti en çok {ProductUpdatePreviewPolicy.MaxProducts} ürün için hazırlanır; {productIds.Count} ürün seçildi. Gönderim bundan etkilenmez.");
        if (connectionIds.Count > ProductUpdatePreviewPolicy.MaxConnections)
            return Fail("VALIDATION_FAILED", $"Değişiklik özeti en çok {ProductUpdatePreviewPolicy.MaxConnections} mağaza için hazırlanır.");
        var sections = ProductUpdateFields.Sections(request.Fields);
        if (!sections.Succeeded) return Fail(sections.Error!.Code, sections.Error.Message);
        var picked = request.Fields is null
            ? ProductUpdateFields.Known.ToHashSet(StringComparer.Ordinal)
            : request.Fields.Select(field => field?.Trim().ToUpperInvariant() ?? "").ToHashSet(StringComparer.Ordinal);

        var connections = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && connectionIds.Contains(x.Id))
            .Select(x => new { x.Id, x.PlatformCode, x.DisplayName })
            .ToListAsync(cancellationToken);
        var cards = await (from product in EffectiveCatalog.Products(db)
                           join model in db.ProductModels.AsNoTracking() on new { product.TenantId, Id = product.FamilyId } equals new { model.TenantId, Id = (Guid?)model.Id } into models
                           from model in models.DefaultIfEmpty()
                           where product.TenantId == tenantId && productIds.Contains(product.Id)
                           select new Card(product.Id, product.FamilyId, product.Title, model == null ? null : model.ModelCode))
            .ToListAsync(cancellationToken);
        var variants = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && productIds.Contains(x.ProductId))
            .Select(x => new VariantRow(x.Id, x.ProductId, x.Sku, x.ModelCode, x.OptionSignature, x.SortOrder))
            .ToListAsync(cancellationToken);
        var variantsById = variants.ToDictionary(x => x.Id);
        // In the order the products were asked for, so the summary follows the window's list.
        var ordered = productIds.Select(id => cards.FirstOrDefault(card => card.Id == id)).OfType<Card>().ToList();

        var result = new List<ProductUpdatePreviewConnection>();
        foreach (var connection in connections.OrderBy(x => connectionIds.IndexOf(x.Id)))
        {
            var platform = connection.PlatformCode.Trim().ToUpperInvariant();
            var supported = ProductUpdateFields.SupportedBy(platform);
            // Shopify takes the whole card whatever was picked (its productSet); elsewhere the picked fields it takes.
            var sentPicks = platform == "SHOPIFY" ? supported.ToHashSet(StringComparer.Ordinal) : supported.Where(picked.Contains).ToHashSet(StringComparer.Ordinal);
            if (platform is not ("TRENDYOL" or "HEPSIBURADA"))
            {
                result.Add(new(connection.Id, platform, connection.DisplayName, [], OrderedFields(sentPicks), [], null));
                continue;
            }

            var reached = targets.Single(target => target.ConnectionId == connection.Id).ProductIds;
            var cardsHere = reached.Select(id => ordered.FirstOrDefault(card => card.Id == id)).OfType<Card>().ToList();
            var sent = new Dictionary<Guid, SentValues>();
            var problems = new List<ProductUpdatePreviewProblem>();
            var composer = new ProductUpdateComposer(db, configuration);
            foreach (var card in cardsHere)
            {
                var draft = await composer.BuildAsync(tenantId, card.Id, connection.Id, cancellationToken, sections.Value);
                if (!draft.Succeeded)
                {
                    problems.Add(new(card.Id, CardLabel(card, variants), draft.Error!.Code, draft.Error.Message));
                    continue;
                }
                foreach (var (variantId, values) in SentValuesOf(draft.Value!))
                    sent[variantId] = values;
            }
            var known = sent.Count == 0 ? new Dictionary<Guid, KnownListingValues>() : await knownValues.ReadAsync(tenantId, connection.Id, [.. sent.Keys], cancellationToken);
            var compared = ComparedFields.Where(field => sent.Values.Any(values => values.Carries(field))).ToList();
            var used = new List<(string Source, DateTimeOffset At)>();
            var models = cardsHere
                .GroupBy(card => card.FamilyId ?? card.Id)
                .Select(group => Model(group.ToList(), variants, sent, known, compared, used))
                .Where(model => model.Fields.Count > 0)
                .ToList();
            DateTimeOffset? Oldest(string source) => used.Where(x => x.Source == source).Select(x => (DateTimeOffset?)x.At).Min();
            result.Add(new(connection.Id, platform, connection.DisplayName, models, OrderedFields(sentPicks.Where(field => !compared.Contains(field))), problems,
                Oldest(MarketplaceKnownListingValueStore.ImportSource), Oldest(MarketplaceKnownListingValueStore.SentSource)));
        }
        return ServiceResult<ProductUpdatePreviewView>.Ok(new(ProductUpdatePreviewPolicy.MaxProducts, result));
    }

    internal sealed record Card(Guid Id, Guid? FamilyId, string Title, string? ModelCode);
    internal sealed record VariantRow(Guid Id, Guid ProductId, string Sku, string? ModelCode, string OptionSignature, int SortOrder);

    // What the payload carries for one variant: a field the item names is sent (Sent), with its value. A field sent with
    // no usable value (Hepsiburada's package writes a missing one as null) is sent all the same, and what the marketplace
    // makes of it is not known: it is compared as unknown, never left out and never "the same".
    internal sealed record SentValues(bool TitleSent, string? Title, bool DescriptionSent, string? Description, bool DesiSent, decimal? Desi)
    {
        public bool Carries(string field) => field switch
        {
            ProductUpdateFields.Title => TitleSent,
            ProductUpdateFields.Description => DescriptionSent,
            ProductUpdateFields.Desi => DesiSent,
            _ => false
        };
    }

    // The title, description and desi each variant's request carries, read from the draft's payloads: Trendyol's items
    // by barcode (title, description, dimensionalWeight), Hepsiburada's by stock code (productName, productDescription,
    // desi). A request that does not go is "{}" or carries no items.
    internal static Dictionary<Guid, SentValues> SentValuesOf(ProductUpdateDraft draft)
    {
        var byBarcode = draft.Variants.Where(x => !string.IsNullOrWhiteSpace(x.Barcode)).GroupBy(x => x.Barcode.Trim(), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().VariantId, StringComparer.Ordinal);
        var bySku = draft.Variants.Where(x => !string.IsNullOrWhiteSpace(x.Sku)).GroupBy(x => x.Sku.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First().VariantId, StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<Guid, SentValues>();
        var publication = draft.Publication;
        var payloads = publication.Mode == "UNAPPROVED" ? new[] { publication.UnapprovedPayloadJson } : [publication.ApprovedContentPayloadJson, publication.ApprovedVariantPayloadJson];
        foreach (var payload in payloads)
        {
            if (string.IsNullOrWhiteSpace(payload)) continue;
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
            {
                Guid variantId;
                if (Text(item, "merchantSku") is { } sku && bySku.TryGetValue(sku.Trim(), out var skuVariant)) variantId = skuVariant;
                else if (Text(item, "barcode") is { } barcode && byBarcode.TryGetValue(barcode.Trim(), out var barcodeVariant)) variantId = barcodeVariant;
                else continue;
                var current = values.GetValueOrDefault(variantId) ?? new SentValues(false, null, false, null, false, null);
                var read = MarketplaceKnownListingValueStore.ReadItem(item);
                values[variantId] = new SentValues(
                    current.TitleSent || read.TitlePresent, read.TitlePresent ? read.Title : current.Title,
                    current.DescriptionSent || read.DescriptionPresent, read.DescriptionPresent ? read.Description : current.Description,
                    current.DesiSent || read.DesiPresent, read.DesiPresent ? read.Desi : current.Desi);
            }
        }
        return values;
    }

    internal static ProductUpdatePreviewModel Model(IReadOnlyList<Card> cards, IReadOnlyList<VariantRow> variants, IReadOnlyDictionary<Guid, SentValues> sent, IReadOnlyDictionary<Guid, KnownListingValues> known, IReadOnlyList<string> compared, List<(string Source, DateTimeOffset At)> used)
    {
        var first = cards[0];
        var label = !string.IsNullOrWhiteSpace(first.ModelCode) ? first.ModelCode.Trim()
            : variants.Where(x => x.ProductId == first.Id && !string.IsNullOrWhiteSpace(x.ModelCode)).Select(x => x.ModelCode!.Trim()).FirstOrDefault() ?? first.Title.Trim();
        var fields = new List<ProductUpdatePreviewField>();
        foreach (var field in compared)
        {
            var changes = new List<ProductUpdatePreviewChange>();
            int changed = 0, same = 0, unknown = 0, sentSame = 0;
            if (field == ProductUpdateFields.Desi)
            {
                foreach (var variant in variants.Where(x => cards.Any(card => card.Id == x.ProductId)).OrderBy(x => cards.ToList().FindIndex(card => card.Id == x.ProductId)).ThenBy(x => x.SortOrder))
                {
                    if (!sent.TryGetValue(variant.Id, out var values) || !values.DesiSent) continue;
                    if (values.Desi is null || !known.TryGetValue(variant.Id, out var marketplace) || marketplace.Desi is not { } desi) { unknown++; continue; }
                    used.Add((desi.Source, desi.ReadAt));
                    if (MarketplaceTextComparison.SameDesi(desi.Value, values.Desi)) { if (desi.Source == MarketplaceKnownListingValueStore.SentSource) sentSame++; else same++; continue; }
                    changed++;
                    changes.Add(new(variant.Sku.Trim(), MarketplaceTextComparison.DesiText(desi.Value), MarketplaceTextComparison.DesiText(values.Desi)));
                }
                if (changed + same + unknown + sentSame > 0) fields.Add(Field(field, ProductUpdatePreviewStates.VariantUnit, changed, same, unknown, sentSame, changes));
                continue;
            }
            // A title or description is the colour card's: the card changes when one of its variants' request carries a
            // text that differs from that variant's known marketplace text; it is unknown when none differs and one is
            // not known.
            foreach (var card in cards)
            {
                var cardVariants = variants.Where(x => x.ProductId == card.Id && sent.TryGetValue(x.Id, out var values) && values.Carries(field)).OrderBy(x => x.SortOrder).ToList();
                if (cardVariants.Count == 0) continue;
                (string Before, string After)? difference = null;
                var anyUnknown = false;
                var anySent = false;
                foreach (var variant in cardVariants)
                {
                    var panel = field == ProductUpdateFields.Title ? sent[variant.Id].Title : sent[variant.Id].Description;
                    var marketplace = known.TryGetValue(variant.Id, out var read) ? field == ProductUpdateFields.Title ? read.Title : read.Description : null;
                    if (panel is null || marketplace is null) { anyUnknown = true; continue; }
                    used.Add((marketplace.Source, marketplace.ReadAt));
                    if (!MarketplaceTextComparison.SameText(marketplace.Value, panel)) { difference = (marketplace.Value, panel); break; }
                    anySent |= marketplace.Source == MarketplaceKnownListingValueStore.SentSource;
                }
                var colour = ColourLabel(card, cardVariants);
                if (difference is { } pair)
                {
                    changed++;
                    if (field == ProductUpdateFields.Title)
                        changes.Add(new(colour, MarketplaceTextComparison.Normalize(pair.Before), MarketplaceTextComparison.Normalize(pair.After)));
                    else
                    {
                        var (before, after) = MarketplaceTextComparison.Excerpts(pair.Before, pair.After);
                        changes.Add(new(colour, before, after, MarketplaceTextComparison.Normalize(pair.Before).Length, MarketplaceTextComparison.Normalize(pair.After).Length));
                    }
                }
                else if (anyUnknown) unknown++;
                else if (anySent) sentSame++;
                else same++;
            }
            if (changed + same + unknown + sentSame > 0) fields.Add(Field(field, ProductUpdatePreviewStates.ColorUnit, changed, same, unknown, sentSame, changes));
        }
        return new(label, fields);
    }

    private static ProductUpdatePreviewField Field(string field, string unit, int changed, int same, int unknown, int sentSame, IReadOnlyList<ProductUpdatePreviewChange> changes) =>
        new(field, unit, changed > 0 ? ProductUpdatePreviewStates.Changes : unknown > 0 ? ProductUpdatePreviewStates.Unknown : sentSame > 0 ? ProductUpdatePreviewStates.SentSame : ProductUpdatePreviewStates.Same, changed, same, unknown, changes, sentSame);

    // The colour word of a card ("Renk: Siyah | Beden: M" → Siyah); its title when the variants name no colour.
    private static string ColourLabel(Card card, IReadOnlyList<VariantRow> variants)
    {
        foreach (var variant in variants)
            foreach (var part in variant.OptionSignature.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split(':', 2, StringSplitOptions.TrimEntries);
                if (pair.Length == 2 && pair[1].Length > 0 && (pair[0].Equals("Renk", StringComparison.OrdinalIgnoreCase) || pair[0].Equals("Color", StringComparison.OrdinalIgnoreCase) || pair[0].Equals("Colour", StringComparison.OrdinalIgnoreCase)))
                    return pair[1];
            }
        return card.Title.Trim();
    }

    private static string CardLabel(Card card, IReadOnlyList<VariantRow> variants)
    {
        var own = variants.Where(x => x.ProductId == card.Id).OrderBy(x => x.SortOrder).ToList();
        var code = !string.IsNullOrWhiteSpace(card.ModelCode) ? card.ModelCode.Trim() : own.Select(x => x.ModelCode?.Trim()).FirstOrDefault(x => !string.IsNullOrEmpty(x));
        var colour = ColourLabel(card, own);
        return code is null ? card.Title.Trim() : colour == card.Title.Trim() ? code : $"{code} · {colour}";
    }

    // The fields in the window's order (ProductUpdateFields), only those given.
    private static IReadOnlyList<string> OrderedFields(IEnumerable<string> fields)
    {
        var given = fields.ToHashSet(StringComparer.Ordinal);
        return [.. FieldOrder.Where(given.Contains)];
    }

    private static readonly string[] FieldOrder = [ProductUpdateFields.Title, ProductUpdateFields.Description, ProductUpdateFields.Images, ProductUpdateFields.Attributes, ProductUpdateFields.Desi, ProductUpdateFields.VatRate, ProductUpdateFields.Delivery, ProductUpdateFields.Warranty];

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static ServiceResult<ProductUpdatePreviewView> Fail(string code, string message) => ServiceResult<ProductUpdatePreviewView>.Fail(code, message, 422);
}
