using System.Globalization;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// KATALOG-14 phase B (expand, write-through): the model record (catalog.product_models, product_model_attributes,
// product_model_size_charts) holds the shared values of its colour cards: name, description, category, VAT, warranty,
// delivery days, the category attributes (less the colour ones), the desi and the size-chart measurements. The cards and
// variants keep their own copies and stay what every screen, job, report and marketplace payload reads.
// One rule for every path, the editor's own (ProductFamilySharedFields): each value is the one most of the model's cards
// hold, on a tie the oldest card's (cards by CreatedAt, Id); the desi is the one most of its variants hold, on a tie the
// smaller (the editor's editorOpenedDesi); the chart is the measurement set most charted cards hold, on a tie the oldest.
// A model is written only where a value differs (the name compared by what it gives each colour, so an editor-saved name
// spelled with another colour is kept); FieldsSavedAt / AttributesSavedAt are never touched here.
// Callers: AppDbContext's save hook (ProductModelLinks.AfterSaveAsync) for every save that changes a card's shared field,
// a variant's desi, a card-level attribute or a card's chart, so every EF write path (editor, create, Toplu Değer Ata,
// Excel aktarımı, marketplace product imports, model-code correction, family save) writes through without code of its
// own; the deploy-time backfill (DatabaseMigrator) and the Worker's nightly model scan for every model.
public static class ProductModelValues
{
    public const string DifferCode = "PRODUCT_MODEL_VALUES_DIFFER";
    public const string DifferDedupeKey = "product-model-values-differ";
    private const int ListedModels = 25;

    // The shared fields a model's cards can disagree on, in the warning's words.
    public const string Title = "ad";
    public const string Description = "açıklama";
    public const string Brand = "marka";
    public const string Category = "kategori";
    public const string Vat = "KDV";
    public const string Warranty = "garanti";
    public const string DeliveryDays = "teslimat süresi";
    public const string Attributes = "özellikler";
    public const string Desi = "desi";
    public const string SizeChart = "beden tablosu";

    public sealed record Card(Guid Id, DateTimeOffset CreatedAt, string? Color, string Title, string Description, Guid? BrandId, Guid? CategoryId, decimal? VatRate, string? VatInclusion, int? WarrantyMonths, int? DeliveryDays);

    // The model's desi: the one most variants hold (above 0), on a tie the smaller; null when none holds one.
    public static decimal? ModelDesi(IEnumerable<decimal?> desis) => desis.OfType<decimal>().Where(desi => desi > 0)
        .GroupBy(desi => desi).OrderByDescending(group => group.Count()).ThenBy(group => group.Key).Select(group => (decimal?)group.Key).FirstOrDefault();

    // The index (cards oldest first) of the card whose chart most charted cards hold, on a tie the oldest; null: none.
    public static int? ModelChartCard(IReadOnlyList<string?> measurements) => measurements.Select((json, index) => (json, index)).Where(item => item.json is not null)
        .GroupBy(item => item.json, StringComparer.Ordinal).OrderByDescending(group => group.Count()).ThenBy(group => group.Min(item => item.index))
        .Select(group => (int?)group.Min(item => item.index)).FirstOrDefault();

    // A card's name without its colour word (its own or another colour of the model): ProductFamilySharedFields.ModelValues'
    // comparison, so "Kadın Siyah Etek" and "Kadın Mavi Etek" are one name.
    internal static string TitleKey(Card card, IReadOnlyCollection<string> colors)
    {
        var title = card.Title.Trim();
        var word = ProductFamilyNewColorCard.HasColorWord(title, card.Color) ? card.Color!.Trim() : colors.FirstOrDefault(color => ProductFamilyNewColorCard.HasColorWord(title, color));
        return word is null ? title : ProductFamilyNewColorCard.Title(title, word, "\u0001");
    }

    private static List<string> Colors(IReadOnlyList<Card> cards) =>
        [.. cards.Select(card => card.Color?.Trim()).OfType<string>().Where(color => color.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    // The shared fields the cards do not all hold alike (ModelValues' keys), in a fixed order; empty when they agree.
    public static IReadOnlyList<string> DifferingFields(IReadOnlyList<Card> cards)
    {
        if (cards.Count < 2) return [];
        var colors = Colors(cards);
        var differing = new List<string>();
        void Check(string field, Func<Card, string> key)
        {
            if (cards.Select(key).Distinct(StringComparer.Ordinal).Skip(1).Any()) differing.Add(field);
        }
        Check(Title, card => TitleKey(card, colors));
        Check(Description, card => card.Description.Trim());
        Check(Brand, card => $"{card.BrandId}");
        Check(Category, card => $"{card.CategoryId}");
        Check(Vat, card => $"{card.VatRate?.ToString("0.####", CultureInfo.InvariantCulture)}|{card.VatInclusion?.ToUpperInvariant()}");
        Check(Warranty, card => $"{card.WarrantyMonths}");
        Check(DeliveryDays, card => $"{card.DeliveryDays}");
        return differing;
    }

    private sealed record Variant(Guid ProductId, int SortOrder, Guid Id, string OptionSignature, decimal? Desi);
    private sealed record AttributeRow(Guid ProductId, Guid AttributeId, Guid? ValueId, string? TextValue, decimal? NumberValue, bool? BooleanValue, int SortOrder);

    // What the cards of the given models hold, read in a few set-based queries.
    private sealed class Snapshot
    {
        public required List<ProductModel> Models { get; init; }
        public required ILookup<Guid, Card> CardsByModel { get; init; }
        public required ILookup<Guid, Variant> VariantsByCard { get; init; }
        public required ILookup<Guid, AttributeRow> AttributesByCard { get; init; }
        public required HashSet<Guid> ColorAttributeIds { get; init; }
        public required Dictionary<Guid, string> ChartByCard { get; init; }
        public required Dictionary<Guid, ProductModelAttribute[]> ModelAttributes { get; init; }
        public required Dictionary<Guid, string?> ModelCharts { get; init; }
    }

    private static async Task<Snapshot> ReadAsync(AppDbContext db, Guid tenantId, Guid[]? modelIds, CancellationToken cancellationToken)
    {
        var models = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && (modelIds == null || modelIds.Contains(x.Id))).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var ids = models.Select(x => x.Id).ToArray();
        var cards = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && x.FamilyId != null && ids.Contains(x.FamilyId.Value))
            .Select(x => new { x.Id, FamilyId = x.FamilyId!.Value, x.CreatedAt, x.Title, x.Description, x.BrandId, x.CategoryId, x.VatRate, x.VatInclusion, x.WarrantyMonths, x.DeliveryDays })
            .ToListAsync(cancellationToken);
        var cardIds = cards.Select(x => x.Id).ToArray();
        var variants = (await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && cardIds.Contains(x.ProductId))
                .Select(x => new Variant(x.ProductId, x.SortOrder, x.Id, x.OptionSignature, x.Desi)).ToListAsync(cancellationToken))
            .ToLookup(x => x.ProductId);
        var colorOf = cardIds.ToDictionary(id => id, id => variants[id].OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => ProductFamilyColorOrder.ColorOf(x.OptionSignature)).FirstOrDefault(color => !string.IsNullOrWhiteSpace(color)));
        var attributes = (await db.ProductAttributeAssignments.AsNoTracking().Where(x => x.TenantId == tenantId && cardIds.Contains(x.ProductId) && x.VariantId == null)
                .Select(x => new AttributeRow(x.ProductId, x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue, x.SortOrder)).ToListAsync(cancellationToken));
        var attributeIds = attributes.Select(x => x.AttributeId).Distinct().ToArray();
        var colorAttributeIds = attributeIds.Length == 0 ? [] : (await db.AttributeDefinitions.AsNoTracking().Where(x => x.TenantId == tenantId && attributeIds.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(cancellationToken))
            .Where(x => VariantOptionSignature.IsColorName(x.Name) || VariantOptionSignature.IsWebColorName(x.Name)).Select(x => x.Id).ToHashSet();
        var charts = await db.ProductSizeCharts.AsNoTracking().Where(x => x.TenantId == tenantId && cardIds.Contains(x.ProductId) && x.MeasurementsJson != null)
            .Select(x => new { x.ProductId, x.MeasurementsJson }).ToListAsync(cancellationToken);
        var modelAttributes = (await db.ProductModelAttributes.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.ModelId)).ToListAsync(cancellationToken))
            .GroupBy(x => x.ModelId).ToDictionary(group => group.Key, group => group.ToArray());
        var modelCharts = await db.ProductModelSizeCharts.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.ModelId))
            .ToDictionaryAsync(x => x.ModelId, x => x.MeasurementsJson, cancellationToken);
        return new Snapshot
        {
            Models = models,
            CardsByModel = cards.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                .ToLookup(x => x.FamilyId, x => new Card(x.Id, x.CreatedAt, colorOf.GetValueOrDefault(x.Id), x.Title, x.Description, x.BrandId, x.CategoryId, x.VatRate, x.VatInclusion, x.WarrantyMonths, x.DeliveryDays)),
            VariantsByCard = variants,
            AttributesByCard = attributes.ToLookup(x => x.ProductId),
            ColorAttributeIds = colorAttributeIds,
            ChartByCard = charts.ToDictionary(x => x.ProductId, x => x.MeasurementsJson!),
            ModelAttributes = modelAttributes,
            ModelCharts = modelCharts,
        };
    }

    private static string AttributeSetKey(IEnumerable<AttributeRow> rows) =>
        string.Join(";", rows.Select(x => ProductModelLinks.AttributeKey(x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    private static string AttributeSetKey(IEnumerable<ProductModelAttribute> rows) =>
        string.Join(";", rows.Select(x => ProductModelLinks.AttributeKey(x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    // What one model should hold, and where its cards disagree.
    private sealed record Target(
        ProductModel Model, IReadOnlyList<Card> Cards, ProductFamilySharedFields.ModelFields Fields, string? TitleColor, bool KeepTitle,
        decimal? Desi, IReadOnlyList<ProductModelLinks.ModelAttributeRow> Attributes, bool AttributesDiffer, string? Chart, IReadOnlyList<string> Differing);

    private static Target? Compute(Snapshot snapshot, ProductModel model)
    {
        var cards = snapshot.CardsByModel[model.Id].ToList();
        if (cards.Count == 0) return null;
        var fields = ProductFamilySharedFields.ModelValues([.. cards.Select(x => new ProductFamilySharedFields.CardFields(x.Id, x.Color, x.Title, x.Description, x.BrandId, x.CategoryId, x.VatRate, x.VatInclusion, x.WarrantyMonths, x.DeliveryDays))], null);
        var titleCard = cards.FirstOrDefault(x => x.Title.Trim() == fields.Title);
        var title = Short(fields.Title, 320);
        var titleColor = titleCard?.Color is { } color && ProductFamilyNewColorCard.HasColorWord(fields.Title, color) ? Short(color.Trim(), 120) : null;
        // The stored name stays when it gives every colour of the model the same name the cards' name does.
        var keepTitle = cards.Select(x => x.Color).Append(null).All(color =>
            ProductFamilySharedFields.TitleFor(model.Title, model.TitleColor, color) == ProductFamilySharedFields.TitleFor(title, titleColor, color));

        var desi = ModelDesi(cards.SelectMany(card => snapshot.VariantsByCard[card.Id]).Select(x => x.Desi));

        var sets = cards.Select(card => snapshot.AttributesByCard[card.Id].Where(row => !snapshot.ColorAttributeIds.Contains(row.AttributeId)).ToList()).ToList();
        var setKeys = sets.Select(AttributeSetKey).ToList();
        var chosen = sets[ProductFamilySharedFields.ModelAttributeCard(setKeys)];
        var attributes = chosen.Select(x => new ProductModelLinks.ModelAttributeRow(x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue, x.SortOrder)).ToList();

        var charts = cards.Select(card => snapshot.ChartByCard.GetValueOrDefault(card.Id)).ToList();
        var chart = ModelChartCard(charts) is int chartCard ? charts[chartCard] : null;

        var differing = DifferingFields(cards).ToList();
        if (setKeys.Distinct(StringComparer.Ordinal).Skip(1).Any()) differing.Add(Attributes);
        // Variants of the model holding different desis (a variant with none only waits for one: the editor fills it).
        if (cards.SelectMany(card => snapshot.VariantsByCard[card.Id]).Select(x => x.Desi).OfType<decimal>().Where(x => x > 0).Distinct().Skip(1).Any()) differing.Add(Desi);
        if (charts.OfType<string>().Distinct(StringComparer.Ordinal).Skip(1).Any()) differing.Add(SizeChart);
        var attributesDiffer = AttributeSetKey(snapshot.ModelAttributes.GetValueOrDefault(model.Id) ?? []) != setKeys[ProductFamilySharedFields.ModelAttributeCard(setKeys)];
        return new Target(model, cards, fields, titleColor, keepTitle, desi, attributes, attributesDiffer, chart, differing);
    }

    public sealed record Outcome(int Models, int Written, IReadOnlyList<(string ModelCode, IReadOnlyList<string> Fields)> Differing);

    // Writes the given models (null: every model of the tenant) where they differ from their cards. Inside a caller's
    // transaction the models are read and written in it (a save's write-through); outside one each model is written in a
    // short transaction of its own that first locks the model row and only then reads its cards, so a save of the model
    // made meanwhile is either seen or waits (VERITABANI-DAGITIM-12's rule for the attribute recompute).
    public static async Task<Outcome> RefreshAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid>? modelIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ids = modelIds?.Distinct().Order().ToArray();
        if (ids is { Length: 0 }) return new(0, 0, []);
        var written = 0;
        var differing = new List<(string, IReadOnlyList<string>)>();
        if (db.Database.CurrentTransaction is not null)
        {
            var snapshot = await ReadAsync(db, tenantId, ids, cancellationToken);
            foreach (var model in snapshot.Models)
                if (Compute(snapshot, model) is { } target)
                {
                    if (await WriteAsync(db, target, now, cancellationToken)) written++;
                    if (target.Differing.Count > 0) differing.Add((model.ModelCode, target.Differing));
                }
            return new(snapshot.Models.Count, written, differing);
        }
        ids ??= [.. await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(cancellationToken)];
        var count = 0;
        foreach (var modelId in ids)
        {
            // One model at a time: a model that fails (a lock timeout during a deploy) is left for the next run.
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
                var locked = await db.Database.SqlQuery<int>($"""
                    SELECT 1 AS "Value" FROM catalog.product_models WHERE "TenantId" = {tenantId} AND "Id" = {modelId} FOR UPDATE
                    """).ToListAsync(cancellationToken);
                if (locked.Count == 1)
                {
                    var snapshot = await ReadAsync(db, tenantId, [modelId], cancellationToken);
                    if (snapshot.Models.Count == 1 && Compute(snapshot, snapshot.Models[0]) is { } target)
                    {
                        count++;
                        if (await WriteAsync(db, target, now, cancellationToken)) written++;
                        if (target.Differing.Count > 0) differing.Add((target.Model.ModelCode, target.Differing));
                    }
                }
                await transaction.CommitAsync(cancellationToken);
            }
            catch (System.Data.Common.DbException) when (!cancellationToken.IsCancellationRequested)
            {
                db.ChangeTracker.Clear();
            }
        }
        return new(count, written, differing);
    }

    private static async Task<bool> WriteAsync(AppDbContext db, Target target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var model = target.Model;
        var fields = target.Fields;
        var title = target.KeepTitle ? model.Title : Short(fields.Title, 320);
        var titleColor = target.KeepTitle ? model.TitleColor : target.TitleColor;
        var wrote = false;
        if ((model.Title, model.TitleColor, model.Description, model.CategoryId, model.VatRate, model.VatInclusion, model.WarrantyMonths, model.DeliveryDays, model.Desi)
            != (title, titleColor, fields.Description, fields.CategoryId, fields.VatRate, fields.VatInclusion, fields.WarrantyMonths, fields.DeliveryDays, target.Desi))
        {
            await db.ProductModels.Where(x => x.TenantId == model.TenantId && x.Id == model.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Title, title).SetProperty(x => x.TitleColor, titleColor).SetProperty(x => x.Description, fields.Description)
                    .SetProperty(x => x.CategoryId, fields.CategoryId).SetProperty(x => x.VatRate, fields.VatRate).SetProperty(x => x.VatInclusion, fields.VatInclusion)
                    .SetProperty(x => x.WarrantyMonths, fields.WarrantyMonths).SetProperty(x => x.DeliveryDays, fields.DeliveryDays).SetProperty(x => x.Desi, target.Desi)
                    .SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
            wrote = true;
        }
        if (target.AttributesDiffer)
            wrote |= await ProductModelLinks.ReplaceModelAttributesAsync(db, model.TenantId, model.Id, target.Attributes, cancellationToken);
        wrote |= await WriteChartAsync(db, model.TenantId, model.Id, target.Chart, now, cancellationToken);
        return wrote;
    }

    // The model's chart row follows its cards' measurements; a model none of whose cards has measurements holds no row.
    private static async Task<bool> WriteChartAsync(AppDbContext db, Guid tenantId, Guid modelId, string? measurements, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (measurements is null)
            return await db.ProductModelSizeCharts.Where(x => x.TenantId == tenantId && x.ModelId == modelId).ExecuteDeleteAsync(cancellationToken) > 0;
        var at = now.ToUniversalTime();
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO catalog.product_model_size_charts AS c ("Id", "TenantId", "ModelId", "MeasurementsJson", "UpdatedAt")
            VALUES ({Guid.CreateVersion7()}, {tenantId}, {modelId}, CAST({measurements} AS jsonb), {at})
            ON CONFLICT ("TenantId", "ModelId") DO UPDATE SET "MeasurementsJson" = EXCLUDED."MeasurementsJson", "UpdatedAt" = EXCLUDED."UpdatedAt"
            WHERE c."MeasurementsJson" IS DISTINCT FROM EXCLUDED."MeasurementsJson"
            """, cancellationToken) > 0;
    }

    // The backfill and the nightly scan: every model of every tenant, then one PRODUCT_MODEL_VALUES_DIFFER warning per
    // tenant naming the models whose cards disagree (resolved when none does). Nothing is merged or chosen for the owner:
    // the cards keep their values and the screens keep showing what they showed. Idempotent; a model a save holds
    // locked waits for it, a model that fails is left for the next run. Returns (models, written, differing).
    public static async Task<(int Models, int Written, int Differing)> RefreshAllAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenants = await db.ProductModels.AsNoTracking().Select(x => x.TenantId).Distinct().ToListAsync(cancellationToken);
        tenants.AddRange((await db.OperationalIssues.AsNoTracking().Where(x => x.Code == DifferCode && x.Status != IssueStatus.Resolved && x.TenantId != null).Select(x => x.TenantId!.Value).ToListAsync(cancellationToken)).Except(tenants));
        int models = 0, written = 0, differing = 0;
        foreach (var tenantId in tenants)
        {
            var outcome = await RefreshTenantAsync(db, tenantId, now, cancellationToken);
            models += outcome.Models;
            written += outcome.Written;
            differing += outcome.Differing.Count;
        }
        return (models, written, differing);
    }

    // One tenant's models and its PRODUCT_MODEL_VALUES_DIFFER warning.
    public static async Task<Outcome> RefreshTenantAsync(AppDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var outcome = await RefreshAsync(db, tenantId, null, now, cancellationToken);
        await RecordDifferingAsync(db, tenantId, outcome.Differing, now, cancellationToken);
        return outcome;
    }

    private static Task RecordDifferingAsync(AppDbContext db, Guid tenantId, IReadOnlyList<(string ModelCode, IReadOnlyList<string> Fields)> differing, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var at = now.ToUniversalTime();
        if (differing.Count == 0)
            return db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ops.operational_issues SET "Status" = 'Resolved', "ResolvedAt" = {at}, "LastSeenAt" = {at}
                WHERE "TenantId" = {tenantId} AND "DedupeKey" = {DifferDedupeKey} AND "Status" <> 'Resolved'
                """, cancellationToken);
        var byField = differing.SelectMany(x => x.Fields).GroupBy(x => x).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{x.Key} {x.Count().ToString(CultureInfo.InvariantCulture)}");
        var codes = differing.Select(x => x.ModelCode).Order(StringComparer.Ordinal).ToList();
        var listed = string.Join(", ", codes.Take(ListedModels)) + (codes.Count > ListedModels ? $" ve {(codes.Count - ListedModels).ToString(CultureInfo.InvariantCulture)} model daha" : "");
        var summary = Short($"{differing.Count.ToString(CultureInfo.InvariantCulture)} modelin renk kartlarında ortak değerler birbirinden farklı ({string.Join(", ", byField)}): {listed}. Hiçbir şey birleştirilmedi; ekranlar bugünkü değeri göstermeye devam eder. Modeli düzenleme ekranında açıp kaydedince bütün renkler aynı değeri alır.", 1000);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ops.operational_issues AS i ("Id", "TenantId", "DedupeKey", "Code", "Summary", "Status", "FirstSeenAt", "LastSeenAt", "OccurrenceCount", "ResolvedAt")
            VALUES ({Guid.CreateVersion7()}, {tenantId}, {DifferDedupeKey}, {DifferCode}, {summary}, 'Open', {at}, {at}, 1, NULL)
            ON CONFLICT ("TenantId", "DedupeKey") DO UPDATE SET
                "Summary" = EXCLUDED."Summary",
                "LastSeenAt" = EXCLUDED."LastSeenAt",
                "OccurrenceCount" = i."OccurrenceCount" + CASE WHEN i."Status" = 'Resolved' THEN 1 ELSE 0 END,
                "Status" = CASE WHEN i."Status" = 'Resolved' THEN 'Open' ELSE i."Status" END,
                "ResolvedAt" = NULL
            """, cancellationToken);
    }

    // KATALOG-14 phase C (editor read): the model record's fields when they are the cards' own: every card of the family
    // is linked to the model and holds one value for every shared field, and the record holds that value. Null otherwise,
    // and the editor goes by today's rule (ProductModelLinks.SavedFieldsAsync, else the cards' majority), so the screen
    // shows the same as before for every model; a model whose cards disagree is reported (PRODUCT_MODEL_VALUES_DIFFER).
    // today: what the editor showed before phase C (ProductModelLinks.SavedFieldsAsync, else the cards' majority). The
    // record is used only where it is that value, so no model shows anything else; phase D drops the fallback once the
    // nightly scan reports no model whose cards or record disagree.
    public static async Task<ProductFamilySharedFields.ModelFields?> RecordFieldsAsync(AppDbContext db, Guid tenantId, Guid? familyId, IReadOnlyCollection<Product> cards, IReadOnlyList<ProductFamilySharedFields.CardFields> cardFields, string? openedColor, ProductFamilySharedFields.ModelFields today, CancellationToken cancellationToken)
    {
        if (familyId is not { } modelId || cards.Count == 0 || cards.Any(x => x.FamilyId != modelId)) return null;
        var asCards = cardFields.Select(x => new Card(x.ProductId, default, x.Color, x.Title, x.Description, x.BrandId, x.CategoryId, x.VatRate, x.VatInclusion, x.WarrantyMonths, x.DeliveryDays)).ToList();
        if (DifferingFields(asCards).Count > 0) return null;
        var model = await db.ProductModels.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == modelId, cancellationToken);
        if (model is null) return null;
        var fromRecord = new ProductFamilySharedFields.ModelFields(
            ProductFamilySharedFields.TitleFor(model.Title, model.TitleColor, openedColor), model.Description, model.BrandId, model.CategoryId,
            model.VatRate, model.VatInclusion, model.WarrantyMonths, model.DeliveryDays);
        return SameAs(fromRecord, today) ? fromRecord : null;
    }

    // Equal as the editor shows them (a VAT of 10 and 10.0000 is one value; the record keeps the cards' own spelling).
    private static bool SameAs(ProductFamilySharedFields.ModelFields left, ProductFamilySharedFields.ModelFields right) =>
        left.Title == right.Title && left.Description == right.Description && left.BrandId == right.BrandId && left.CategoryId == right.CategoryId
        && left.VatRate == right.VatRate && left.VatRate?.Scale == right.VatRate?.Scale && left.VatInclusion == right.VatInclusion
        && left.WarrantyMonths == right.WarrantyMonths && left.DeliveryDays == right.DeliveryDays;

    private static string Short(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
