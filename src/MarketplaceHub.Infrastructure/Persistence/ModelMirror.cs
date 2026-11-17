using System.Globalization;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// KATALOG-14 phase D5b (owner, 17 Nov 2026: "Model kartına yazılan ad, açıklama, KDV, teslimat süresi, garanti süresi,
// marka, kategori, desi model kartına yazılır; varyantlar buradan alır, yani ortaktır"). No colour or variant holds its
// own value of these. The brand is the model's key (ProductModelLinks re-links a card whose brand changes); the other
// seven are written here:
// - A save that gives a card (or a variant, the desi) one of these values (the editor, Toplu Değer Ata, an Excel row of
//   one card, the API) gives it to the model and mirrors it to every colour and every variant of the model, in the save's
//   transaction (ApplyAsync). Only the fields the save changed are mirrored: a field nobody touched keeps what the cards
//   hold today (no flattening of phase A's differences without the owner). Cards of one model given different values
//   in one save leave the model as it is and the difference is reported (the Excel file refuses such rows first).
// - A marketplace product import (AppDbContext.MarketplaceCatalogImport) never moves a model: a colour that comes in
//   with another value keeps the model's (its copy is written back) and PRODUCT_MODEL_VALUES_DIFFER names the model, the
//   field and both values. A new colour card never moves its model either; a model the save opens took its colours'
//   values when it was opened (most colours, on a tie the oldest card: the editor's rule).
// - The startup pass, the deploy refresh and the nightly scan write none of these fields and mark no link (review,
//   17 Nov 2026): a colour whose copy differs from its model's (the previous release wrote the card alone during a
//   deploy) is only reported (ReportGapsAsync); the owner's own save closes it.
// The card and variant copies stay a mirror of the model, so a rollback to phase A reads the same values.
public static class ModelMirror
{
    // What a save gave one card. Added: a new colour card, which never moves its model.
    public sealed record FieldChange(
        bool Vat, decimal? VatRate, string? VatInclusion, bool Category, Guid? CategoryId, bool Warranty, int? WarrantyMonths, bool Delivery, int? DeliveryDays,
        bool Added = false, bool Title = false, string? TitleText = null, bool Description = false, string? DescriptionText = null, IReadOnlyList<DesiGiven>? Desis = null,
        bool Brand = false, Guid? BrandId = null)
    {
        public bool Any => Vat || Category || Warranty || Delivery || Title || Description || Brand || Desis is { Count: > 0 };

        // A variant's desi the save changed (Added: a new size, which only checks its model's).
        public FieldChange WithDesi(Guid variantId, decimal? desi, bool added = false) => this with { Desis = [.. Desis ?? [], new DesiGiven(variantId, desi, added)] };

        public static FieldChange None { get; } = new(false, null, null, false, null, false, null, false, null);
    }

    public sealed record DesiGiven(Guid VariantId, decimal? Desi, bool Added);

    // What a save wrote: the model, the number of colours it has, the fields given to it, and whether a difference was
    // kept apart (an import, colours given different values in one save).
    public sealed record Applied(Guid ModelId, string ModelCode, int Colours, IReadOnlyList<string> Fields, bool Refused);

    public const string DifferDedupePrefix = "product-model-values-differ:";

    // The colour cards of the models whose fields a request's saves moved (each model once, its colour count as last seen):
    // the "Model İçindeki N Renge Uygulandı" of Toplu Değer Ata and the product file.
    internal static int ColoursReached(IEnumerable<Applied> report) =>
        report.Where(x => x.Fields.Count > 0).GroupBy(x => x.ModelId).Sum(x => x.Last().Colours);

    private sealed record Values(string Title, string? TitleColor, string Description, decimal? VatRate, string? VatInclusion, Guid? CategoryId, int? WarrantyMonths, int? DeliveryDays, decimal? Desi, Guid? BrandId);

    private sealed record CardRow(Guid Id, string Title, string Description, string? Color);

    // The save's changes (ProductModelLinks.Pending.FieldChanges of one tenant), in the save's transaction.
    public static async Task ApplyAsync(AppDbContext db, Guid tenantId, IReadOnlyDictionary<Guid, FieldChange> changes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (changes.Count == 0) return;
        var ids = changes.Keys.ToArray();
        var cards = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.Id) && x.FamilyId != null)
            .Select(x => new { x.Id, ModelId = x.FamilyId!.Value }).ToListAsync(cancellationToken);
        foreach (var group in cards.GroupBy(x => x.ModelId).OrderBy(x => x.Key))
        {
            var model = await db.ProductModels.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == group.Key, cancellationToken);
            if (model is null) continue;
            var members = await CardsAsync(db, tenantId, model.Id, cancellationToken);
            var colors = members.Select(x => x.Color?.Trim()).OfType<string>().Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // Oldest colour first (members are by CreatedAt, Id): a new model's desi goes by the oldest card on a tie.
            var given = group.Select(x => (Card: members.First(member => member.Id == x.Id), Change: changes[x.Id])).OrderBy(x => members.IndexOf(x.Card)).ToList();
            var target = new Values(model.Title, model.TitleColor, model.Description, model.VatRate, model.VatInclusion, model.CategoryId, model.WarrantyMonths, model.DeliveryDays, model.Desi, model.BrandId);
            var fields = new List<string>();
            // Per touched field, the cards that brought a value: when the model does not move they alone take its value
            // back (an import, a new colour), so a colour nobody touched keeps what it holds today.
            var touched = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
            var conflicts = new List<string>();

            // One field: the values the save gave (new colours only check the model's), the model's value and how to set it.
            void Take<T>(string field, Func<FieldChange, bool> isSet, Func<(CardRow Card, FieldChange Change), T> valueOf, Func<T, string> key, T current, Func<Values, T, Values> with, Func<T, string>? show = null, Func<(CardRow Card, FieldChange Change), bool>? unknown = null)
            {
                string Shown(T value) => show is null ? Show(value) : show(value);
                var set = given.Where(x => isSet(x.Change)).ToList();
                if (set.Count == 0) return;
                var currentKey = key(current);
                // Only a card whose value differs from the model's takes it back: one that brought the model's value in
                // another spelling (a colour word written in lower case) keeps its text.
                touched[field] = [.. set.Where(x => key(valueOf(x)) != currentKey).Select(x => x.Card.Id)];
                // KATALOG-14 phase D5b review 4: an unknown or empty value a new colour or a marketplace import brings is no
                // difference: the colour takes the model's value (above) and nobody is warned.
                var counted = unknown is null ? set : set.Where(x => !((x.Change.Added || db.MarketplaceCatalogImport) && unknown(x))).ToList();
                var added = counted.Where(x => x.Change.Added).Select(valueOf).Where(value => key(value) != currentKey).ToList();
                var changed = counted.Where(x => !x.Change.Added).Select(valueOf).GroupBy(key, StringComparer.Ordinal).Select(g => g.First()).ToList();
                if (added.Count > 0) conflicts.Add($"{field} ({string.Join(" / ", added.Select(Shown).Distinct())}; modelin: {Shown(current)})");
                if (changed.Count == 0) return;
                // An import fills a value the model does not hold yet (unknown stays unknown until a source knows it); it
                // never replaces one.
                var modelEmpty = currentKey.Trim('|').Length == 0;
                if (changed.Count > 1 || db.MarketplaceCatalogImport && !modelEmpty)
                {
                    if (changed.Any(value => key(value) != currentKey)) conflicts.Add($"{field} ({string.Join(" / ", changed.Select(Shown))}; modelin: {Shown(current)})");
                    return;
                }
                if (key(changed[0]) == currentKey) return;
                target = with(target, changed[0]);
                fields.Add(field);
            }

            Take(ProductModelValues.Title, x => x.Title, x => (x.Change.TitleText ?? x.Card.Title, x.Card.Color),
                value => value.Item2 is null ? value.Item1.Trim() : TitleKey(value.Item1, value.Item2, colors), (target.Title, target.TitleColor),
                (v, x) => v with { Title = Short(x.Item1.Trim(), 320), TitleColor = x.Item2 is { } color && ProductFamilyNewColorCard.HasColorWord(x.Item1, color) ? Short(color.Trim(), 120) : null },
                unknown: x => string.IsNullOrWhiteSpace(x.Change.TitleText ?? x.Card.Title));
            Take(ProductModelValues.Description, x => x.Description, x => x.Change.DescriptionText ?? x.Card.Description, value => value.Trim(), target.Description, (v, x) => v with { Description = x }, unknown: x => string.IsNullOrWhiteSpace(x.Change.DescriptionText ?? x.Card.Description));
            Take(ProductModelValues.Vat, x => x.Vat, x => (x.Change.VatRate, x.Change.VatInclusion), value => $"{value.VatRate?.ToString("0.####", CultureInfo.InvariantCulture)}|{value.VatInclusion?.ToUpperInvariant()}", (target.VatRate, target.VatInclusion), (v, x) => v with { VatRate = x.VatRate, VatInclusion = x.VatInclusion }, unknown: x => x.Change.VatRate is null);
            Take(ProductModelValues.Category, x => x.Category, x => x.Change.CategoryId, value => $"{value}", target.CategoryId, (v, x) => v with { CategoryId = x }, unknown: x => x.Change.CategoryId is null);
            // An emptied warranty (a draft saved with it emptied, 15-16 Nov 2026) is "not filled", not a value: it neither moves the
            // model nor reaches another colour. A new colour a marketplace import opens carries no warranty or delivery days of
            // the listing (0 and the card default 1 are placeholders): unknown, so no difference.
            Take(ProductModelValues.Warranty, x => x.Warranty && x.WarrantyMonths is not null, x => x.Change.WarrantyMonths, value => $"{value}", target.WarrantyMonths, (v, x) => v with { WarrantyMonths = x },
                unknown: x => x.Change.Added && db.MarketplaceCatalogImport);
            Take(ProductModelValues.DeliveryDays, x => x.Delivery, x => x.Change.DeliveryDays, value => $"{value}", target.DeliveryDays, (v, x) => v with { DeliveryDays = x }, unknown: x => x.Change.DeliveryDays is null || x.Change.Added && db.MarketplaceCatalogImport);
            // The brand (KATALOG-14 phase D5b, model key change): an empty brand is not a value; a new colour without one takes
            // the model's.
            var brandIds = given.Select(x => x.Change.BrandId).Append(target.BrandId).OfType<Guid>().Distinct().ToArray();
            var brandNames = brandIds.Length == 0 || !given.Any(x => x.Change.Brand) ? [] : await db.Brands.AsNoTracking().Where(x => x.TenantId == tenantId && brandIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
            Take(ProductModelValues.Brand, x => x.Brand && x.BrandId is not null, x => x.Change.BrandId, value => $"{value}", target.BrandId, (v, x) => v with { BrandId = x },
                value => value is Guid id && brandNames.TryGetValue(id, out var name) ? $"\"{Clip(name)}\"" : "boş");
            var emptyBrand = given.Where(x => x.Change.Added && x.Change.BrandId is null).Select(x => x.Card.Id).ToList();
            if (emptyBrand.Count > 0 && target.BrandId is not null)
                touched[ProductModelValues.Brand] = [.. touched.GetValueOrDefault(ProductModelValues.Brand) ?? [], .. emptyBrand];
            // The desi: every variant value the save gave; an empty one (not filled) never moves the model.
            var desiGiven = given.SelectMany(x => (x.Change.Desis ?? []).Select(desi => desi with { Added = desi.Added || x.Change.Added })).ToList();
            var desiVariants = new HashSet<Guid>(desiGiven.Select(x => x.VariantId));
            if (desiGiven.Count > 0)
            {
                touched[ProductModelValues.Desi] = [];
                var addedValues = desiGiven.Where(x => x.Added).Select(x => x.Desi).OfType<decimal>().ToList();
                var changedDesis = desiGiven.Where(x => !x.Added).Select(x => x.Desi).OfType<decimal>().Distinct().ToList();
                if (changedDesis.Count > 1 || (changedDesis.Count == 1 && db.MarketplaceCatalogImport && target.Desi is not null))
                {
                    if (changedDesis.Any(value => value != target.Desi)) conflicts.Add($"{ProductModelValues.Desi} ({string.Join(" / ", changedDesis.Select(value => Show((decimal?)value)))}; modelin: {Show(target.Desi)})");
                }
                else if (changedDesis.Count == 1 && changedDesis[0] != target.Desi)
                {
                    target = target with { Desi = changedDesis[0] };
                    fields.Add(ProductModelValues.Desi);
                }
                // A model without a desi yet takes the one most of its new sizes bring (on a tie the oldest card's), as the
                // refresh does.
                else if (target.Desi is null && changedDesis.Count == 0 && ProductModelValues.ModelDesi(addedValues.Select(value => (decimal?)value)) is { } opened)
                {
                    target = target with { Desi = opened };
                    fields.Add(ProductModelValues.Desi);
                }
                // New sizes that came with another desi take the model's, and the owner is told.
                var addedDesis = addedValues.Where(value => value != target.Desi).Distinct().ToList();
                if (addedDesis.Count > 0 && target.Desi is not null) conflicts.Add($"{ProductModelValues.Desi} ({string.Join(" / ", addedDesis.Select(value => Show((decimal?)value)))}; modelin: {Show(target.Desi)})");
            }

            if (fields.Count > 0)
            {
                await db.ProductModels.Where(x => x.TenantId == tenantId && x.Id == model.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Title, target.Title).SetProperty(x => x.TitleColor, target.TitleColor).SetProperty(x => x.Description, target.Description)
                        .SetProperty(x => x.VatRate, target.VatRate).SetProperty(x => x.VatInclusion, target.VatInclusion)
                        .SetProperty(x => x.CategoryId, target.CategoryId).SetProperty(x => x.WarrantyMonths, target.WarrantyMonths).SetProperty(x => x.DeliveryDays, target.DeliveryDays)
                        .SetProperty(x => x.Desi, target.Desi).SetProperty(x => x.BrandId, target.BrandId).SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
                if (!db.MarketplaceCatalogImport) await ResolveAsync(db, tenantId, model.Id, now, cancellationToken);
            }
            if (conflicts.Count > 0) await RecordAsync(db, tenantId, model, conflicts, db.MarketplaceCatalogImport, now, cancellationToken);
            // A field the model took goes to every colour (and every variant); otherwise only the cards and variants that
            // brought a value take the model's back.
            var scope = touched.ToDictionary(x => x.Key, x => fields.Contains(x.Key) ? null : (IReadOnlySet<Guid>)x.Value, StringComparer.Ordinal);
            await MirrorAsync(db, tenantId, model.Id, target, scope, members, fields.Contains(ProductModelValues.Desi) ? null : desiVariants, !db.MarketplaceCatalogImport, cancellationToken);
            db.ModelMirrorReport.Add(new Applied(model.Id, model.ModelCode, members.Count, fields, conflicts.Count > 0));
        }
    }

    // The name a colour takes from its model's (TitleFor), compared without the colour word.
    private static string TitleKey(string title, string? color, IReadOnlyCollection<string> colors) =>
        ProductModelValues.TitleKey(new ProductModelValues.Card(Guid.Empty, default, color, title, "", null, null, null, null, null, null), colors);

    private static string Show<T>(T value) => value switch
    {
        null => "boş",
        ValueTuple<decimal?, string?> vat => vat.Item1 is null ? "boş" : $"%{vat.Item1.Value.ToString("0.##", CultureInfo.InvariantCulture)}{(vat.Item2 is { } inclusion ? $" {(inclusion == "INCLUDED" ? "dahil" : inclusion == "EXCLUDED" ? "hariç" : inclusion)}" : "")}",
        ValueTuple<string, string?> title => $"\"{Clip(title.Item1)}\"",
        string text => $"\"{Clip(text)}\"",
        decimal number => number.ToString("0.##", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "boş",
    };

    private static string Clip(string text) => text.Length <= 60 ? text : text[..60] + "…";

    private static string Short(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static async Task<List<CardRow>> CardsAsync(AppDbContext db, Guid tenantId, Guid modelId, CancellationToken cancellationToken)
    {
        var cards = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && x.FamilyId == modelId)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => new { x.Id, x.Title, x.Description }).ToListAsync(cancellationToken);
        var ids = cards.Select(x => x.Id).ToArray();
        var signatures = (await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.ProductId))
                .Select(x => new { x.ProductId, x.SortOrder, x.Id, x.OptionSignature }).ToListAsync(cancellationToken))
            .ToLookup(x => x.ProductId);
        return [.. cards.Select(x => new CardRow(x.Id, x.Title, x.Description,
            signatures[x.Id].OrderBy(v => v.SortOrder).ThenBy(v => v.Id).Select(v => ProductFamilyColorOrder.ColorOf(v.OptionSignature)).FirstOrDefault(color => !string.IsNullOrWhiteSpace(color))))];
    }

    // Writes the model's value of each given field, where the copy differs, to the field's cards (null: every colour of
    // the model; the name with each colour's word) and the desi to the given variants (null: every variant of the model).
    // Only those columns are written (Version and UpdatedAt stay). The colours whose copies changed get their marketplace
    // links marked edited, as a card edit marks them (a link already marked keeps its version); not in a marketplace
    // import, which only gives a colour its model's value back (the listing's other value is reported, and marking the
    // link would make the next import keep the card whole as if the owner had edited it).
    private static async Task MirrorAsync(AppDbContext db, Guid tenantId, Guid modelId, Values values, IReadOnlyDictionary<string, IReadOnlySet<Guid>?> fields, IReadOnlyList<CardRow> members, IReadOnlySet<Guid>? desiVariants, bool markLinks, CancellationToken cancellationToken)
    {
        if (fields.Count == 0) return;
        var changedCards = new HashSet<Guid>();
        async Task Cards(string field, System.Linq.Expressions.Expression<Func<Product, bool>> differs, Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<Product>> set)
        {
            if (!fields.TryGetValue(field, out var scope)) return;
            var only = scope?.ToArray();
            var ids = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && x.FamilyId == modelId && (only == null || only.Contains(x.Id))).Where(differs).Select(x => x.Id).ToListAsync(cancellationToken);
            if (ids.Count == 0) return;
            await db.Products.Where(x => x.TenantId == tenantId && ids.Contains(x.Id)).ExecuteUpdateAsync(set, cancellationToken);
            changedCards.UnionWith(ids);
        }
        if (fields.TryGetValue(ProductModelValues.Title, out var titleScope))
            foreach (var card in members.Where(x => titleScope is null || titleScope.Contains(x.Id)))
            {
                var title = Short(ProductFamilySharedFields.TitleFor(values.Title, values.TitleColor, card.Color), 320);
                if (card.Title == title) continue;
                await db.Products.Where(x => x.TenantId == tenantId && x.Id == card.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, title), cancellationToken);
                changedCards.Add(card.Id);
            }
        await Cards(ProductModelValues.Description, x => x.Description != values.Description, s => s.SetProperty(x => x.Description, values.Description));
        await Cards(ProductModelValues.Vat, x => x.VatRate != values.VatRate || x.VatInclusion != values.VatInclusion, s => s.SetProperty(x => x.VatRate, values.VatRate).SetProperty(x => x.VatInclusion, values.VatInclusion));
        await Cards(ProductModelValues.Category, x => x.CategoryId != values.CategoryId, s => s.SetProperty(x => x.CategoryId, values.CategoryId));
        await Cards(ProductModelValues.Warranty, x => x.WarrantyMonths != values.WarrantyMonths, s => s.SetProperty(x => x.WarrantyMonths, values.WarrantyMonths));
        await Cards(ProductModelValues.DeliveryDays, x => x.DeliveryDays != values.DeliveryDays, s => s.SetProperty(x => x.DeliveryDays, values.DeliveryDays));
        await Cards(ProductModelValues.Brand, x => x.BrandId != values.BrandId, s => s.SetProperty(x => x.BrandId, values.BrandId));
        if (fields.ContainsKey(ProductModelValues.Desi) && values.Desi is { } desi)
        {
            var only = desiVariants?.ToArray();
            var variants = await db.ProductVariants.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Desi != desi && (only == null || only.Contains(x.Id)) && db.Products.Any(p => p.TenantId == tenantId && p.Id == x.ProductId && p.FamilyId == modelId))
                .Select(x => new { x.Id, x.ProductId }).ToListAsync(cancellationToken);
            if (variants.Count > 0)
            {
                var variantIds = variants.Select(x => x.Id).ToArray();
                await db.ProductVariants.Where(x => x.TenantId == tenantId && variantIds.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Desi, desi), cancellationToken);
                changedCards.UnionWith(variants.Select(x => x.ProductId));
            }
        }
        if (changedCards.Count == 0 || !markLinks) return;
        await MarkLinksEditedAsync(db, tenantId, changedCards, cancellationToken);
    }

    // The marketplace links of the given cards marked edited, as a card edit marks them (a link already marked keeps its
    // version).
    internal static Task MarkLinksEditedAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync("""
            UPDATE catalog.marketplace_product_links l
            SET "SyncStatus" = 'LOCAL_CHANGES_PENDING',
                "DirtyFieldsJson" = coalesce(l."DirtyFieldsJson", '[]'::jsonb) || '["product"]'::jsonb,
                "Version" = l."Version" + 1
            WHERE l."TenantId" = @tenant AND l."ProductId" = ANY(@products)
              AND NOT (l."SyncStatus" = 'LOCAL_CHANGES_PENDING' AND coalesce(l."DirtyFieldsJson", '[]'::jsonb) @> '["product"]'::jsonb)
            """, [new NpgsqlParameter("tenant", tenantId), new NpgsqlParameter("products", productIds.ToArray())], cancellationToken);

    // KATALOG-14 phase D5b (review, 17 Nov 2026; "yeni kart kazanır" withdrawn): the automatic paths (the deploy refresh,
    // the Worker's startup pass, the nightly scan) write nothing in the eight model fields and mark no marketplace link.
    // A colour whose VAT, category, warranty, delivery days or brand copy differs from its model's (the previous release
    // wrote the card alone during a deploy, a raw edit) is only reported: one PRODUCT_MODEL_VALUES_DIFFER warning per model
    // naming the colour, the field and both values. The readers read the model, so the invoice takes the model's VAT (and
    // stops when it is unknown). Only the owner's own save (the editor, Excel, Toplu Değer Ata) closes the difference. A
    // colour whose copy is empty (unknown) is no difference: unknown stays unknown and never counts as another value.
    // Returns the models with a difference (code and fields), for the caller's log; resolves the warning of a model that
    // no longer has one.
    public const string GapDedupePrefix = "product-model-gap:";

    public sealed record Gap(Guid ModelId, string ModelCode, IReadOnlyList<string> Fields);

    public static async Task<IReadOnlyList<Gap>> ReportGapsAsync(AppDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await (from card in db.Products.AsNoTracking()
                          join model in db.ProductModels.AsNoTracking() on new { card.TenantId, Id = card.FamilyId } equals new { model.TenantId, Id = (Guid?)model.Id }
                          where card.TenantId == tenantId
                                && ((card.VatRate != null && (card.VatRate != model.VatRate || card.VatInclusion != model.VatInclusion))
                                    || (card.CategoryId != null && card.CategoryId != model.CategoryId)
                                    || (card.WarrantyMonths != null && card.WarrantyMonths != model.WarrantyMonths)
                                    || (card.DeliveryDays != null && card.DeliveryDays != model.DeliveryDays)
                                    || (card.BrandId != null && card.BrandId != model.BrandId))
                          orderby model.ModelCode, card.CreatedAt, card.Id
                          select new
                          {
                              ModelId = model.Id,
                              model.ModelCode,
                              model.VatRate,
                              model.VatInclusion,
                              model.CategoryId,
                              model.WarrantyMonths,
                              model.DeliveryDays,
                              model.BrandId,
                              CardId = card.Id,
                              CardTitle = card.Title,
                              CardVatRate = card.VatRate,
                              CardVatInclusion = card.VatInclusion,
                              CardCategoryId = card.CategoryId,
                              CardWarranty = card.WarrantyMonths,
                              CardDelivery = card.DeliveryDays,
                              CardBrandId = card.BrandId
                          })
            .ToListAsync(cancellationToken);
        var cardIds = rows.Select(x => x.CardId).Distinct().ToArray();
        var colours = cardIds.Length == 0 ? [] : (await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && cardIds.Contains(x.ProductId))
                .Select(x => new { x.ProductId, x.SortOrder, x.Id, x.OptionSignature }).ToListAsync(cancellationToken))
            .GroupBy(x => x.ProductId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => ProductFamilyColorOrder.ColorOf(x.OptionSignature)).FirstOrDefault(color => !string.IsNullOrWhiteSpace(color)));
        var categoryIds = rows.SelectMany(x => new[] { x.CategoryId, x.CardCategoryId }).OfType<Guid>().Distinct().ToArray();
        var categories = categoryIds.Length == 0 ? [] : await db.Categories.AsNoTracking().Where(x => x.TenantId == tenantId && categoryIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var brandIds = rows.SelectMany(x => new[] { x.BrandId, x.CardBrandId }).OfType<Guid>().Distinct().ToArray();
        var brands = brandIds.Length == 0 ? [] : await db.Brands.AsNoTracking().Where(x => x.TenantId == tenantId && brandIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        string Named(Guid? id, Dictionary<Guid, string> names) => id is Guid value && names.TryGetValue(value, out var name) ? $"\"{Clip(name)}\"" : "boş";

        var gaps = new List<Gap>();
        var at = now.ToUniversalTime();
        foreach (var model in rows.GroupBy(x => x.ModelId))
        {
            var first = model.First();
            var fields = new List<string>();
            var lines = new List<string>();
            foreach (var card in model)
            {
                var parts = new List<string>();
                void Differs(string field, string cardValue, string modelValue)
                {
                    parts.Add($"{field} {cardValue} (modelin: {modelValue})");
                    if (!fields.Contains(field)) fields.Add(field);
                }
                if (card.CardVatRate is not null && (card.CardVatRate != card.VatRate || card.CardVatInclusion != card.VatInclusion))
                    Differs(ProductModelValues.Vat, Show((card.CardVatRate, card.CardVatInclusion)), Show((card.VatRate, card.VatInclusion)));
                if (card.CardCategoryId is not null && card.CardCategoryId != card.CategoryId) Differs(ProductModelValues.Category, Named(card.CardCategoryId, categories), Named(card.CategoryId, categories));
                if (card.CardWarranty is not null && card.CardWarranty != card.WarrantyMonths) Differs(ProductModelValues.Warranty, Show(card.CardWarranty), Show(card.WarrantyMonths));
                if (card.CardDelivery is not null && card.CardDelivery != card.DeliveryDays) Differs(ProductModelValues.DeliveryDays, Show(card.CardDelivery), Show(card.DeliveryDays));
                if (card.CardBrandId is not null && card.CardBrandId != card.BrandId) Differs(ProductModelValues.Brand, Named(card.CardBrandId, brands), Named(card.BrandId, brands));
                if (parts.Count == 0) continue;
                var colour = colours.GetValueOrDefault(card.CardId) is { } word ? $"{word.Trim()} rengi" : $"\"{Clip(card.CardTitle)}\" kartı";
                lines.Add($"{colour}: {string.Join(", ", parts)}");
            }
            if (lines.Count == 0) continue;
            var code = string.IsNullOrWhiteSpace(first.ModelCode) ? $"\"{Clip(model.First().CardTitle)}\"" : first.ModelCode;
            gaps.Add(new Gap(model.Key, code, fields));
            var summary = $"{code} modelinin renk kartında modelinkinden farklı değer kayıtlı: {string.Join("; ", lines)}. Otomatik olarak hiçbir şey değiştirilmedi: ekranlar, pazaryeri gönderimleri ve faturalar modelin değerini kullanır. Doğru değer renkteki ise modeli düzenleme ekranında açıp o değeri girip kaydedin; bütün renklere uygulanır.";
            if (summary.Length > 1000) summary = summary[..1000];
            var key = $"{GapDedupePrefix}{model.Key:N}";
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ops.operational_issues AS i ("Id", "TenantId", "DedupeKey", "Code", "Summary", "Status", "FirstSeenAt", "LastSeenAt", "OccurrenceCount", "ResolvedAt")
                VALUES ({Guid.CreateVersion7()}, {tenantId}, {key}, {ProductModelValues.DifferCode}, {summary}, 'Open', {at}, {at}, 1, NULL)
                ON CONFLICT ("TenantId", "DedupeKey") DO UPDATE SET
                    "Summary" = EXCLUDED."Summary", "LastSeenAt" = EXCLUDED."LastSeenAt",
                    "OccurrenceCount" = i."OccurrenceCount" + CASE WHEN i."Status" = 'Resolved' THEN 1 ELSE 0 END,
                    "Status" = CASE WHEN i."Status" = 'Resolved' THEN 'Open' ELSE i."Status" END, "ResolvedAt" = NULL
                """, cancellationToken);
        }
        // The warnings of models whose colours agree with them again (or that are gone).
        var open = gaps.Select(x => $"{GapDedupePrefix}{x.ModelId:N}").ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.operational_issues SET "Status" = 'Resolved', "ResolvedAt" = {at}, "LastSeenAt" = {at}
            WHERE "TenantId" = {tenantId} AND "Code" = {ProductModelValues.DifferCode} AND "DedupeKey" LIKE {GapDedupePrefix + "%"} AND "Status" <> 'Resolved' AND NOT ("DedupeKey" = ANY({open}))
            """, cancellationToken);
        return gaps;
    }

    // One warning per model (its own dedupe key; the tenant-wide values warning stays as it is), in Turkish, where the
    // owner reads the operational issues.
    private static Task RecordAsync(AppDbContext db, Guid tenantId, ProductModel model, IReadOnlyList<string> conflicts, bool import, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var at = now.ToUniversalTime();
        var summary = $"{model.ModelCode} modelinin {(import ? "pazaryerinden gelen" : "aynı kayıtta değişen")} bir rengi modelinkinden farklı değerle geldi: {string.Join("; ", conflicts)}. Modelin değeri korundu ve bütün renklerde aynı kaldı. Doğru değer farklıysa modeli düzenleme ekranında açıp kaydedin.";
        if (summary.Length > 1000) summary = summary[..1000];
        var key = $"{DifferDedupePrefix}{model.Id:N}";
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ops.operational_issues AS i ("Id", "TenantId", "DedupeKey", "Code", "Summary", "Status", "FirstSeenAt", "LastSeenAt", "OccurrenceCount", "ResolvedAt")
            VALUES ({Guid.CreateVersion7()}, {tenantId}, {key}, {ProductModelValues.DifferCode}, {summary}, 'Open', {at}, {at}, 1, NULL)
            ON CONFLICT ("TenantId", "DedupeKey") DO UPDATE SET
                "Summary" = EXCLUDED."Summary", "LastSeenAt" = EXCLUDED."LastSeenAt",
                "OccurrenceCount" = i."OccurrenceCount" + 1,
                "Status" = CASE WHEN i."Status" = 'Resolved' THEN 'Open' ELSE i."Status" END, "ResolvedAt" = NULL
            """, cancellationToken);
    }

    private static Task ResolveAsync(AppDbContext db, Guid tenantId, Guid modelId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var at = now.ToUniversalTime();
        var key = $"{DifferDedupePrefix}{modelId:N}";
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.operational_issues SET "Status" = 'Resolved', "ResolvedAt" = {at}, "LastSeenAt" = {at}
            WHERE "TenantId" = {tenantId} AND "DedupeKey" = {key} AND "Status" <> 'Resolved'
            """, cancellationToken);
    }
}
