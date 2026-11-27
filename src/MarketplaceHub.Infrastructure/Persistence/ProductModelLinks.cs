using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// MODEL-KAYDI-PLANI Faz 1: keeps catalog.product_models and products.FamilyId in step with the model-code rule. A card's
// model code is its first variant's (sort order, id) non-blank ModelCode. A model is its brand and its code (owner, 17 Nov
// 2026: "farklı markalar aynı model kodunu kullanabiliyor"; one per tenant, brand and key, a unique index). For every key
// and brand:
// - the brand's model of the key is found, re-keyed (KATALOG-2) or opened, and the cards are linked to it; the same code
//   under another brand is that brand's own model (nothing is refused, skipped or warned about; an open
//   PRODUCT_MODEL_AMBIGUOUS issue from before is resolved); a colour whose brand a save changed stays with its model, whose
//   brand the save moves (ModelMirror, refused when that brand already holds the code);
// - a card linked to a model whose key it no longer has is unlinked; a model left without cards is deleted.
// An unlinked card simply follows today's model-code rule, so a link that is missing or late is never wrong, only late.
// Only FamilyId of the cards is written (raw updates): no card's Version, UpdatedAt or other column changes.
// AppDbContext runs it after every save that adds, deletes or re-codes a card or variant or changes a card's brand;
// the raw-SQL deletes (product delete, connection data purge, Tam Sıfırlama) call it themselves.
public static class ProductModelLinks
{
    public const string AmbiguousCode = "PRODUCT_MODEL_AMBIGUOUS";
    public const string AmbiguousKeyPrefix = "product-model-ambiguous:";
    public static string AmbiguousDedupeKey(string modelCodeKey) => AmbiguousKeyPrefix + modelCodeKey;

    private const string TurkishLetters = "çÇğĞıİöÖşŞüÜ";

    // CatalogKeyNormalizer.Key in SQL for a code of ASCII and Turkish letters (SqlKeySafe): the Turkish letters and a-z
    // are mapped by hand (no upper(), whose result depends on the database locale), every run of other characters is
    // one '-', the ends are trimmed of '-', 160 characters at most. The migration's backfill uses the same expression.
    public static string SqlKey(string column) =>
        $"left(btrim(regexp_replace(translate({column}, 'abcdefghijklmnopqrstuvwxyz{TurkishLetters}', 'ABCDEFGHIJKLMNOPQRSTUVWXYZCCGGIIOOSSUU'), '[^ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789]+', '-', 'g'), '-'), 160)";

    // True when SqlKey gives CatalogKeyNormalizer.Key for the code: nothing but ASCII and the Turkish letters.
    public static string SqlKeySafe(string column) =>
        $"(octet_length(translate({column}, '{TurkishLetters}', 'cCgGiIoOsSuU')) = char_length({column}))";

    public sealed class Pending
    {
        public Dictionary<Guid, HashSet<string>> Keys { get; } = [];
        public Dictionary<Guid, HashSet<Guid>> ProductIds { get; } = [];
        public Dictionary<Guid, HashSet<Guid>> ModelIds { get; } = [];
        // Cards whose pictures were added or moved by the save: their FamilyId is set from the card after it (Faz 2).
        public Dictionary<Guid, HashSet<Guid>> MediaProductIds { get; } = [];
        // KATALOG-14 phase B: cards whose shared values (a model-row field, a variant's desi, a card-level attribute, the
        // size chart) the save changed: their model takes the values after it (ProductModelValues).
        public Dictionary<Guid, HashSet<Guid>> ValueProductIds { get; } = [];
        // KATALOG-14 phase D5b: the model fields (VAT, category, warranty, delivery days) each changed card was given by the
        // save, per tenant and card (ModelMirror).
        public Dictionary<Guid, Dictionary<Guid, ModelMirror.FieldChange>> FieldChanges { get; } = [];
        // KATALOG-14 phase D5b: the model codes the save changed, per tenant and card (before, after): the model takes the
        // new code and every colour's copy follows (PropagateCodeChangesAsync).
        public Dictionary<Guid, Dictionary<Guid, (string? Before, string? After)>> CodeChanges { get; } = [];
        // KATALOG-14 D5 step B1 (expand): the own models ("#<card id>") given to the save's new cards before their insert
        // (GiveNewCardsModels): per tenant, read by the reconcile as no link at all (a fresh model is never re-keyed into a code),
        // and with their cards, to settle after the save or undo when it fails.
        public Dictionary<Guid, HashSet<Guid>> FreshModels { get; } = [];
        internal List<(ProductModel Model, Product Card)> FreshPairs { get; } = [];

        internal void AddCodeChange(Guid tenantId, Guid productId, string? before, string? after)
        {
            if (!CodeChanges.TryGetValue(tenantId, out var map)) CodeChanges[tenantId] = map = [];
            map.TryAdd(productId, (before, after));
        }
        public bool IsEmpty => Keys.Count == 0 && ProductIds.Count == 0 && ModelIds.Count == 0 && MediaProductIds.Count == 0 && ValueProductIds.Count == 0;
        public IEnumerable<Guid> Tenants => Keys.Keys.Concat(ProductIds.Keys).Concat(ModelIds.Keys).Concat(MediaProductIds.Keys).Concat(ValueProductIds.Keys).Distinct();

        internal void AddMedia(Guid tenantId, Guid productId) => Set(MediaProductIds, tenantId).Add(productId);

        internal void AddValues(Guid tenantId, Guid productId) => Set(ValueProductIds, tenantId).Add(productId);

        internal ModelMirror.FieldChange FieldChangeOf(Guid tenantId, Guid productId) =>
            FieldChanges.TryGetValue(tenantId, out var map) && map.TryGetValue(productId, out var change) ? change : ModelMirror.FieldChange.None;

        internal void AddFieldChange(Guid tenantId, Guid productId, ModelMirror.FieldChange change)
        {
            if (!FieldChanges.TryGetValue(tenantId, out var map)) FieldChanges[tenantId] = map = [];
            map[productId] = change;
            AddValues(tenantId, productId);
        }

        internal void AddKey(Guid tenantId, string? modelCode)
        {
            if (ProductFamilyKeyPolicy.ModelCodeKey(modelCode) is { } key) Set(Keys, tenantId).Add(key);
        }

        internal void AddProduct(Guid tenantId, Guid productId) => Set(ProductIds, tenantId).Add(productId);

        internal void AddModel(Guid tenantId, Guid? modelId)
        {
            if (modelId is { } id) Set(ModelIds, tenantId).Add(id);
        }

        internal static HashSet<T> SetOf<T>(Dictionary<Guid, HashSet<T>> map, Guid tenantId) => Set(map, tenantId);

        private static HashSet<T> Set<T>(Dictionary<Guid, HashSet<T>> map, Guid tenantId)
        {
            if (!map.TryGetValue(tenantId, out var set)) map[tenantId] = set = [];
            return set;
        }
    }

    // The cards and keys a save is about to change, read before the save (original values are gone after it).
    public static Pending? Capture(ChangeTracker tracker)
    {
        var pending = new Pending();
        foreach (var entry in tracker.Entries<ProductVariant>())
        {
            var variant = entry.Entity;
            switch (entry.State)
            {
                case EntityState.Added:
                    pending.AddKey(variant.TenantId, variant.ModelCode);
                    pending.AddProduct(variant.TenantId, variant.ProductId);
                    // KATALOG-14 phase D5b: a new size takes its model's desi (ModelMirror).
                    pending.AddFieldChange(variant.TenantId, variant.ProductId, pending.FieldChangeOf(variant.TenantId, variant.ProductId).WithDesi(variant.Id, variant.Desi, added: true));
                    break;
                case EntityState.Deleted:
                    pending.AddKey(variant.TenantId, entry.Property(x => x.ModelCode).OriginalValue);
                    pending.AddProduct(variant.TenantId, entry.Property(x => x.ProductId).OriginalValue);
                    break;
                case EntityState.Modified:
                    var code = entry.Property(x => x.ModelCode);
                    var product = entry.Property(x => x.ProductId);
                    var sort = entry.Property(x => x.SortOrder);
                    if (Changed(entry.Property(x => x.Desi)) || Changed(entry.Property(x => x.OptionSignature))) pending.AddValues(variant.TenantId, variant.ProductId);
                    // KATALOG-14 phase D5b: a desi given to one variant goes to the model and every variant (ModelMirror).
                    if (Changed(entry.Property(x => x.Desi))) pending.AddFieldChange(variant.TenantId, variant.ProductId, pending.FieldChangeOf(variant.TenantId, variant.ProductId).WithDesi(variant.Id, variant.Desi));
                    if (Changed(code)) pending.AddCodeChange(variant.TenantId, variant.ProductId, code.OriginalValue, code.CurrentValue);
                    if (!Changed(code) && !Changed(product) && !Changed(sort)) break;
                    pending.AddKey(variant.TenantId, code.OriginalValue);
                    pending.AddKey(variant.TenantId, code.CurrentValue);
                    pending.AddProduct(variant.TenantId, product.OriginalValue);
                    pending.AddProduct(variant.TenantId, product.CurrentValue);
                    break;
            }
        }
        foreach (var entry in tracker.Entries<Product>())
        {
            var product = entry.Entity;
            // KATALOG-14 phase D5b: a model field given to the card goes to its model and every colour (ModelMirror).
            if (entry.State is EntityState.Modified or EntityState.Added)
            {
                var added = entry.State == EntityState.Added;
                var change = pending.FieldChangeOf(product.TenantId, product.Id) with
                {
                    Vat = added || Changed(entry.Property(x => x.VatRate)) || Changed(entry.Property(x => x.VatInclusion)),
                    VatRate = product.VatRate,
                    VatInclusion = product.VatInclusion,
                    Category = added || Changed(entry.Property(x => x.CategoryId)),
                    CategoryId = product.CategoryId,
                    Warranty = added || Changed(entry.Property(x => x.WarrantyMonths)),
                    WarrantyMonths = product.WarrantyMonths,
                    Delivery = added || Changed(entry.Property(x => x.DeliveryDays)),
                    DeliveryDays = product.DeliveryDays,
                    Title = added || Changed(entry.Property(x => x.Title)),
                    TitleText = product.Title,
                    Description = added || Changed(entry.Property(x => x.Description)),
                    DescriptionText = product.Description,
                    Brand = added || Changed(entry.Property(x => x.BrandId)),
                    BrandId = product.BrandId,
                    Added = added,
                };
                if (change.Any) pending.AddFieldChange(product.TenantId, product.Id, change);
            }
            switch (entry.State)
            {
                case EntityState.Added:
                    pending.AddProduct(product.TenantId, product.Id);
                    break;
                case EntityState.Deleted:
                    pending.AddModel(product.TenantId, entry.Property(x => x.FamilyId).OriginalValue);
                    break;
                case EntityState.Modified when Changed(entry.Property(x => x.BrandId)):
                    pending.AddProduct(product.TenantId, product.Id);
                    pending.AddModel(product.TenantId, entry.Property(x => x.FamilyId).OriginalValue);
                    break;
                case EntityState.Modified when Changed(entry.Property(x => x.Title)) || Changed(entry.Property(x => x.Description)) || Changed(entry.Property(x => x.CategoryId))
                    || Changed(entry.Property(x => x.VatRate)) || Changed(entry.Property(x => x.VatInclusion)) || Changed(entry.Property(x => x.WarrantyMonths))
                    || Changed(entry.Property(x => x.DeliveryDays)):
                    pending.AddValues(product.TenantId, product.Id);
                    break;
            }
        }
        // A card-level attribute (VariantId null) or a card's size chart changed: the model's set follows.
        foreach (var entry in tracker.Entries<ProductAttributeAssignment>())
        {
            var cardLevel = entry.Entity.VariantId is null || entry.State == EntityState.Modified && entry.Property(x => x.VariantId).OriginalValue is null;
            if (cardLevel && entry.State is EntityState.Added or EntityState.Deleted or EntityState.Modified)
                pending.AddValues(entry.Entity.TenantId, entry.State == EntityState.Deleted ? entry.Property(x => x.ProductId).OriginalValue : entry.Entity.ProductId);
        }
        foreach (var entry in tracker.Entries<ProductSizeChart>())
        {
            if (entry.State is EntityState.Added or EntityState.Deleted || entry.State == EntityState.Modified && (Changed(entry.Property(x => x.MeasurementsJson)) || Changed(entry.Property(x => x.FileAssetId))))
                pending.AddValues(entry.Entity.TenantId, entry.State == EntityState.Deleted ? entry.Property(x => x.ProductId).OriginalValue : entry.Entity.ProductId);
        }
        foreach (var entry in tracker.Entries<ProductMedia>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified && (Changed(entry.Property(x => x.ProductId)) || Changed(entry.Property(x => x.FamilyId))))
                pending.AddMedia(entry.Entity.TenantId, entry.Entity.ProductId);
        }
        return pending.IsEmpty ? null : pending;
    }

    // KATALOG-14 phase D5b review 3: a save that gives a model field (or a model code) to a card writes the model and then
    // its sibling colours (ModelMirror, PropagateCodeChangesAsync). Two such saves of sibling colours at once took the
    // locks in opposite orders (own card row, model, siblings) and one died of a deadlock. Before the save writes any card,
    // the rows of the models its cards belong to (and of the codes it touches) are locked in one statement, in id order,
    // so the second save waits for the first. In the save's transaction, a serializable caller's too since it writes its
    // model fields in it (B2a review M1); a row another session changed after its snapshot then fails it at once (40001).
    public static async Task LockPendingModelsAsync(AppDbContext db, Pending pending, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null) return;
        if (Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(db.Database.CurrentTransaction).IsolationLevel == System.Data.IsolationLevel.Serializable
            && pending.FieldChanges.Count == 0 && pending.CodeChanges.Count == 0) return;
        foreach (var tenantId in pending.FieldChanges.Keys.Concat(pending.CodeChanges.Keys).Distinct().Order())
        {
            var productIds = (pending.FieldChanges.GetValueOrDefault(tenantId)?.Keys ?? Enumerable.Empty<Guid>())
                .Concat(pending.CodeChanges.GetValueOrDefault(tenantId)?.Keys ?? Enumerable.Empty<Guid>())
                .Concat(pending.ProductIds.GetValueOrDefault(tenantId) ?? []).Distinct().ToArray();
            await LockModelsOfCardsAsync(db, tenantId, productIds, [.. pending.Keys.GetValueOrDefault(tenantId) ?? []], cancellationToken);
        }
    }

    // KATALOG-14 D5 step B1 (expand-contract phase 1): a card is never written without its model, so products.FamilyId can
    // become NOT NULL. Every card the save adds without a model gets, before its insert and in the save itself, a model of
    // its own ("#<card id>", the card's brand and values: what EnsureOwnModelsAsync gave it after the save until now); EF
    // inserts the model before the card. The reconcile after the save reads such a fresh model as no link at all (SaveScope
    // .FreshModels; it is never re-keyed into a code), so a coded card joins or opens its brand's model of the code exactly as
    // an unlinked card did (NewModelValues, the first card brought in) and the fresh model, left without a card, goes; a card
    // without a code keeps it as its own model.
    internal static void GiveNewCardsModels(AppDbContext db, Pending pending, DateTimeOffset now)
    {
        foreach (var entry in db.ChangeTracker.Entries<Product>().Where(x => x.State == EntityState.Added && x.Entity.FamilyId is null).ToList())
        {
            var card = entry.Entity;
            var model = new ProductModel
            {
                Id = Guid.CreateVersion7(),
                TenantId = card.TenantId,
                BrandId = card.BrandId,
                ModelCode = "",
                ModelCodeKey = $"{OwnModelKeyPrefix}{card.Id:N}",
                Title = Short(card.Title, 320),
                TitleColor = null,
                Description = card.Description,
                CategoryId = card.CategoryId,
                VatRate = card.VatRate,
                VatInclusion = card.VatInclusion,
                WarrantyMonths = card.WarrantyMonths,
                DeliveryDays = card.DeliveryDays,
                FieldsSavedAt = null,
                CreatedAt = now,
                UpdatedAt = now,
                Version = 1,
            };
            db.ProductModels.Add(model);
            card.FamilyId = model.Id;
            Pending.SetOf(pending.FreshModels, card.TenantId).Add(model.Id);
            pending.FreshPairs.Add((model, card));
        }
    }

    // After the save and its links: the fresh models leave the tracker (the reconcile wrote or deleted them by SQL) and each
    // new card's tracked FamilyId is the one the reconcile gave it.
    internal static async Task SettleFreshModelsAsync(AppDbContext db, Pending pending, CancellationToken cancellationToken)
    {
        if (pending.FreshPairs.Count == 0) return;
        foreach (var (model, _) in pending.FreshPairs) db.Entry(model).State = EntityState.Detached;
        foreach (var tenant in pending.FreshPairs.GroupBy(x => x.Card.TenantId))
        {
            var ids = tenant.Select(x => x.Card.Id).ToArray();
            var links = await db.Products.AsNoTracking().Where(x => x.TenantId == tenant.Key && ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FamilyId, cancellationToken);
            foreach (var (_, card) in tenant)
            {
                var entry = db.Entry(card);
                if (entry.State == EntityState.Detached || !links.TryGetValue(card.Id, out var familyId)) continue;
                var property = entry.Property(x => x.FamilyId);
                property.CurrentValue = familyId;
                if (entry.State == EntityState.Added) continue;
                property.OriginalValue = familyId;
                property.IsModified = false;
            }
        }
        pending.FreshPairs.Clear();
    }

    // A save that failed: its new cards are as the caller made them (no model) and the fresh models leave the tracker, so a
    // retry of the same save gives them new ones.
    internal static void ForgetFreshModels(AppDbContext db, Pending pending)
    {
        foreach (var (model, card) in pending.FreshPairs)
        {
            if (db.Entry(model).State == EntityState.Added) db.Entry(model).State = EntityState.Detached;
            if (db.Entry(card).State == EntityState.Added && card.FamilyId == model.Id) card.FamilyId = null;
        }
        pending.FreshPairs.Clear();
    }

    // How long a save waits for the model rows another save holds (review, 17 Nov 2026): a lock left by a session that
    // never ends (a stuck request) fails the save with 55P03 instead of holding it, and its request, forever; the import
    // retries the model, Excel and Toplu Değer Ata answer with a conflict (SerializableWriteRetry.IsLockConflict).
    public const string ModelLockTimeout = "30s";

    // The rows of the models the given cards are linked to and of the given keys, locked in one statement in id order (the
    // one order every model write takes them in), in the caller's transaction, waiting at most ModelLockTimeout; the
    // caller's own lock_timeout is put back after it.
    internal static async Task LockModelsOfCardsAsync(AppDbContext db, Guid tenantId, Guid[] productIds, string[] keys, CancellationToken cancellationToken)
    {
        var previous = (await db.Database.SqlQuery<string>($"""SELECT current_setting('lock_timeout') AS "Value" """).ToListAsync(cancellationToken)).Single();
        await db.Database.SqlQuery<string>($"""SELECT set_config('lock_timeout', {db.ModelLockTimeout}, true) AS "Value" """).ToListAsync(cancellationToken);
        await db.Database.SqlQuery<int>($"""
            SELECT 1 AS "Value" FROM catalog.product_models
            WHERE "TenantId" = {tenantId}
              AND ("Id" IN (SELECT p."FamilyId" FROM catalog.products p WHERE p."TenantId" = {tenantId} AND p."Id" = ANY({productIds}) AND p."FamilyId" IS NOT NULL)
                   OR "ModelCodeKey" = ANY({keys}))
            ORDER BY "Id" FOR UPDATE
            """).ToListAsync(cancellationToken);
        await db.Database.SqlQuery<string>($"""SELECT set_config('lock_timeout', {previous}, true) AS "Value" """).ToListAsync(cancellationToken);
    }

    private static bool Changed<TEntity, T>(PropertyEntry<TEntity, T> property) where TEntity : class =>
        property.IsModified && !EqualityComparer<T>.Default.Equals(property.OriginalValue, property.CurrentValue);

    // After the save: the keys of the captured cards as they are now and as they were linked, then the reconcile.
    public static async Task AfterSaveAsync(AppDbContext db, Pending pending, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var tenantId in pending.Tenants.ToList())
        {
            var keys = pending.Keys.GetValueOrDefault(tenantId) ?? [];
            if (pending.ProductIds.TryGetValue(tenantId, out var productIds)) keys.UnionWith(await KeysOfProductsAsync(db, tenantId, productIds, cancellationToken));
            if (pending.ModelIds.TryGetValue(tenantId, out var modelIds) && modelIds.Count > 0)
            {
                var ids = modelIds.ToArray();
                keys.UnionWith(await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.Id)).Select(x => x.ModelCodeKey).ToListAsync(cancellationToken));
            }
            // The brands the save gave cards already linked (a brand change moves the whole model, ModelMirror writing it right
            // after; a marketplace import moves no model, its card goes to its brand's model).
            var brandChanges = (pending.FieldChanges.GetValueOrDefault(tenantId) ?? []).Where(x => x.Value.Brand && !x.Value.Added && !db.MarketplaceCatalogImport).ToDictionary(x => x.Key, x => x.Value.BrandId);
            // KATALOG-14 phase D5b: a code changed on a colour is the model's new code (not in a marketplace import, which never
            // moves a model).
            if (!db.MarketplaceCatalogImport && pending.CodeChanges.TryGetValue(tenantId, out var codeChanges))
                keys.UnionWith(await PropagateCodeChangesAsync(db, tenantId, codeChanges, brandChanges, cancellationToken));
            if (keys.Count > 0)
            {
                // The save's cards and the brands it gave.
                await ReconcileAsync(db, tenantId, keys, now, cancellationToken, new SaveScope(new HashSet<Guid>([.. productIds ?? [], .. brandChanges.Keys]), brandChanges, pending.FreshModels.GetValueOrDefault(tenantId)));
            }
            // KATALOG-14 phase D5a: a card the model-code rule links to no model (no code) gets a model of its own.
            if (productIds is { Count: > 0 })
            {
                await EnsureOwnModelsAsync(db, tenantId, [.. productIds], now, cancellationToken);
                await SyncOwnModelBrandsAsync(db, tenantId, [.. productIds], now, cancellationToken);
            }
            if (pending.MediaProductIds.TryGetValue(tenantId, out var mediaProductIds) && mediaProductIds.Count > 0)
                await SyncMediaFamiliesAsync(db, tenantId, [.. mediaProductIds], cancellationToken);
            // KATALOG-14 phase B: the models of the cards the save added, moved or changed a shared value of (read after
            // the reconcile, so a card that just joined a model counts for it), the models of the reconciled keys and the
            // models cards left.
            var valueCards = (pending.ValueProductIds.GetValueOrDefault(tenantId) ?? []).Concat(productIds ?? []).Distinct().ToArray();
            var valueModels = new HashSet<Guid>(pending.ModelIds.GetValueOrDefault(tenantId) ?? []);
            if (valueCards.Length > 0)
                valueModels.UnionWith(await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && valueCards.Contains(x.Id) && x.FamilyId != null).Select(x => x.FamilyId!.Value).ToListAsync(cancellationToken));
            if (keys.Count > 0)
            {
                var keyArray = keys.ToArray();
                valueModels.UnionWith(await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && keyArray.Contains(x.ModelCodeKey)).Select(x => x.Id).ToListAsync(cancellationToken));
            }
            // KATALOG-14 phase D5a/b: the model's fields (ModelMirror) are written in the save's own transaction (the caller's
            // or the one SaveChanges opened), a serializable caller's too (the editor's save that deletes a variant): a write
            // that fails fails the save, which the caller answers as "nothing saved, try again" (CatalogService.SaveConflict).
            // KATALOG-14 D5 B2a review (M1): they were once written after a serializable caller's commit; a failure there left
            // the owner's edit on the cards only while every reader reads the model, a silent lost edit. A concurrent write of
            // the same model row now makes the serializable save fail with a serialization or lock conflict instead (409).
            // Only the derived model values (ProductModelValues: category attributes, size chart, a desi the model does not hold
            // yet) still follow a serializable caller's commit (AppDbContext.DeferModelValues); no owner edit is deferred.
            var serializable = db.Database.CurrentTransaction is { } current && Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(current).IsolationLevel == System.Data.IsolationLevel.Serializable;
            if (pending.FieldChanges.TryGetValue(tenantId, out var fieldChanges) && fieldChanges.Count > 0)
                await ModelMirror.ApplyAsync(db, tenantId, fieldChanges, now, cancellationToken);
            if (valueModels.Count == 0) continue;
            if (serializable) db.DeferModelValues(tenantId, valueModels);
            else await ProductModelValues.RefreshAsync(db, tenantId, valueModels, now, cancellationToken);
        }
    }

    // KATALOG-14 phase D5b (owner, 17 Nov 2026): a model code changed on one colour (the editor, an Excel row, the API) is
    // the model's: every variant of the model still holding the old code takes the new one in the save's transaction, so
    // the reconcile keeps the model under its new code (KATALOG-2's re-key). A code another model holds is refused (the
    // editor and the Excel file refuse it first; this is the last guard). Returns the keys to reconcile.
    private static async Task<HashSet<string>> PropagateCodeChangesAsync(AppDbContext db, Guid tenantId, IReadOnlyDictionary<Guid, (string? Before, string? After)> changes, IReadOnlyDictionary<Guid, Guid?> brandChanges, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var cardIds = changes.Keys.ToArray();
        var cards = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && cardIds.Contains(x.Id) && x.FamilyId != null).Select(x => new { x.Id, ModelId = x.FamilyId!.Value }).ToListAsync(cancellationToken);
        foreach (var card in cards)
        {
            var (before, after) = changes[card.Id];
            if (ProductFamilyKeyPolicy.ModelCodeKey(before) is not { } oldKey || ProductFamilyKeyPolicy.ModelCodeKey(after) is not { } newKey || oldKey == newKey) continue;
            var model = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == card.ModelId && x.ModelCodeKey == oldKey).Select(x => new { x.Id, x.BrandId }).SingleOrDefaultAsync(cancellationToken);
            if (model is null) continue;
            var modelId = model.Id;
            // A model is its brand and code: only the model of the new code the cards would join refuses it, under the brand
            // the save gives the card when it gives one (an Excel row with Marka and Model Kodu, review 18 Nov 2026), by the
            // reconcile's own rule (LinkBrand: a model without a brand joins the code's one branded model).
            var codeModels = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && x.ModelCodeKey == newKey && x.Id != modelId).Select(x => new { x.Id, x.BrandId }).ToListAsync(cancellationToken);
            var joining = LinkBrand(brandChanges.TryGetValue(card.Id, out var given) ? given : model.BrandId, [.. codeModels.Where(x => x.BrandId is not null).Select(x => x.BrandId)]);
            var joinedIds = codeModels.Where(x => x.BrandId == joining).Select(x => x.Id).ToArray();
            var holder = joinedIds.Length == 0 ? null : await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && x.FamilyId != null && joinedIds.Contains(x.FamilyId.Value))
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => x.Title).FirstOrDefaultAsync(cancellationToken);
            if (holder is not null) throw ModelRuleException.CodeInUse(holder);
            var variants = (await db.ProductVariants.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ModelCode != null && db.Products.Any(product => product.TenantId == tenantId && product.Id == x.ProductId && product.FamilyId == modelId))
                    .Select(x => new { x.Id, x.ModelCode }).ToListAsync(cancellationToken))
                .Where(x => ProductFamilyKeyPolicy.ModelCodeKey(x.ModelCode) == oldKey).Select(x => x.Id).ToArray();
            var code = Short(after!.Trim(), 160);
            if (variants.Length > 0)
            {
                var products = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && variants.Contains(x.Id)).Select(x => x.ProductId).Distinct().ToListAsync(cancellationToken);
                await db.ProductVariants.Where(x => x.TenantId == tenantId && variants.Contains(x.Id)).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ModelCode, code), cancellationToken);
                await ModelMirror.MarkLinksEditedAsync(db, tenantId, products, cancellationToken);
            }
            keys.Add(oldKey);
            keys.Add(newKey);
        }
        return keys;
    }

    // The model-code keys of the given cards: their own (first coded variant) and the key of the model they are linked to.
    public static async Task<HashSet<string>> KeysOfProductsAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (productIds.Count == 0) return keys;
        var ids = productIds.ToArray();
        var codes = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ProductId) && x.ModelCode != null)
            .Select(x => new CodeRow(x.ProductId, x.ModelCode, x.SortOrder, x.Id, null))
            .ToListAsync(cancellationToken);
        foreach (var card in codes.GroupBy(x => x.ProductId))
            if (CardKey(card) is { } key) keys.Add(key);
        keys.UnionWith(await (from product in db.Products.AsNoTracking()
                              join model in db.ProductModels.AsNoTracking() on product.FamilyId equals model.Id
                              where product.TenantId == tenantId && ids.Contains(product.Id)
                              select model.ModelCodeKey).ToListAsync(cancellationToken));
        return keys;
    }

    private sealed record CodeRow(Guid ProductId, string? ModelCode, int SortOrder, Guid Id, string? OptionSignature);

    // A card's model-code key: its first variant's (sort order, id) non-blank code; null when it has none.
    private static string? CardKey(IEnumerable<CodeRow> variants) =>
        ProductFamilyKeyPolicy.ModelCodeKey(variants.Where(x => !string.IsNullOrWhiteSpace(x.ModelCode)).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => x.ModelCode).FirstOrDefault());

    // Every key of the tenant: its cards', its models' and its open ambiguity issues'. After a raw-SQL delete.
    public static Task ReconcileTenantAsync(AppDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken) =>
        ReconcileAsync(db, tenantId, null, now, cancellationToken);

    // keys null: the whole tenant.
    // KATALOG-6: the reconcile of a key reads the cards, opens or re-keys a model, links the cards and deletes the models
    // left without one in several statements; two reconciles of one key at once could delete the model the other just
    // opened. Outside a caller's transaction it runs in a short transaction of its own holding an advisory lock per key
    // (shared tenant lock, exclusive for the whole tenant), so such reconciles take turns and a failure leaves nothing
    // half done. Inside a caller's transaction it runs as before (no lock, so a long import holds nothing for others).
    // save: a save's reconcile (KATALOG-14 phase D5b): a card of the save joining a code under another brand fails the save.
    // FreshModels: the own models the save gave its new cards before their insert (GiveNewCardsModels), read as no link.
    public sealed record SaveScope(IReadOnlySet<Guid> Products, IReadOnlyDictionary<Guid, Guid?> BrandChanges, IReadOnlySet<Guid>? FreshModels = null);

    // The one rule of which brand's model of a code a card joins when it is not linked to one of the code yet: the reconcile
    // below and every "is this code in use" check (ModelCodeOwnership) ask it. A card of a brand joins its brand's model. A
    // card without a brand is no other brand (owner, 17 Nov 2026): it joins the code's one model of a brand when the code has
    // exactly one, else the model without a brand. brandedModelBrands: the brands of the code's models that have a brand
    // (one model per brand and code).
    public static Guid? LinkBrand(Guid? cardBrand, IReadOnlyCollection<Guid?> brandedModelBrands) =>
        cardBrand ?? (brandedModelBrands.Count == 1 ? brandedModelBrands.First() : null);

    public static async Task ReconcileAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<string>? keys, DateTimeOffset now, CancellationToken cancellationToken, SaveScope? save = null)
    {
        var keyArray = keys?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (keyArray is { Length: 0 }) return;
        if (db.Database.CurrentTransaction is not null && !db.OwnsModelLinkTransaction)
        {
            await ReconcileCoreAsync(db, tenantId, keyArray, save, now, cancellationToken);
            return;
        }
        // KATALOG-14 phase D5a: a save's own transaction (AppDbContext) takes the key locks too, held to its commit.
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken) : null;
        var tenantLock = $"product-models:{tenantId:N}";
        if (keyArray is null)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({tenantLock}, 0))", cancellationToken);
        else
        {
            // The keys are locked in one statement and one global order (their hash), so two reconciles never wait on each
            // other in a circle.
            var keyLocks = keyArray.Select(key => $"{tenantLock}:{key}").ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock_shared(hashtextextended({tenantLock}, 0))", cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(s.h) FROM (SELECT DISTINCT hashtextextended(k, 0) AS h FROM unnest({keyLocks}) AS k ORDER BY 1) s", cancellationToken);
        }
        await ReconcileCoreAsync(db, tenantId, keyArray, save, now, cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ReconcileCoreAsync(AppDbContext db, Guid tenantId, string[]? keyArray, SaveScope? save, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The variants of every card that may hold one of the keys: SQL narrows by the key where it can compute it and
        // keeps every code it cannot (other non-ASCII letters); the exact key is computed here.
        var codeColumn = "c.\"ModelCode\"";
        var sql = $"""
            SELECT v."ProductId", v."ModelCode", v."SortOrder", v."Id", v."OptionSignature"
            FROM catalog.product_variants v
            WHERE v."TenantId" = @tenant AND v."ModelCode" IS NOT NULL AND v."ModelCode" ~ '[^[:space:]]'
              AND v."ProductId" IN (
                SELECT c."ProductId" FROM catalog.product_variants c
                WHERE c."TenantId" = @tenant AND c."ModelCode" IS NOT NULL AND c."ModelCode" ~ '[^[:space:]]'
                  AND (@all OR NOT {SqlKeySafe(codeColumn)} OR {SqlKey(codeColumn)} = ANY(@keys)))
            """;
        var rows = await db.Database.SqlQueryRaw<CodeRow>(sql,
                new NpgsqlParameter("tenant", tenantId),
                new NpgsqlParameter("all", keyArray is null),
                new NpgsqlParameter("keys", keyArray ?? []))
            .ToListAsync(cancellationToken);
        var cardKeys = rows.GroupBy(x => x.ProductId)
            .Select(card => (ProductId: card.Key, Key: CardKey(card), Color: card.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => ProductFamilyColorOrder.ColorOf(x.OptionSignature)).FirstOrDefault(color => !string.IsNullOrWhiteSpace(color)), Code: card.Where(x => !string.IsNullOrWhiteSpace(x.ModelCode)).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).First().ModelCode!.Trim()))
            .Where(card => card.Key is not null)
            .ToList();
        var targetKeys = new HashSet<string>(keyArray ?? cardKeys.Select(x => x.Key!), StringComparer.Ordinal);
        if (keyArray is null)
        {
            targetKeys.UnionWith(await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => x.ModelCodeKey).ToListAsync(cancellationToken));
            targetKeys.UnionWith((await db.OperationalIssues.AsNoTracking().Where(x => x.TenantId == tenantId && x.Code == AmbiguousCode && x.Status != IssueStatus.Resolved).Select(x => x.DedupeKey).ToListAsync(cancellationToken))
                .Where(x => x.StartsWith(AmbiguousKeyPrefix, StringComparison.Ordinal)).Select(x => x[AmbiguousKeyPrefix.Length..]));
        }
        cardKeys = cardKeys.Where(card => targetKeys.Contains(card.Key!)).ToList();
        var cardIds = cardKeys.Select(x => x.ProductId).ToArray();
        // KATALOG-14 D5 step B1: a fresh own model the save gave a new card before its insert counts as no link (the card
        // joins or opens its code's model as an unlinked card did); the card's real link stays in actualFamilies, so the fresh
        // model is among the models cards leave and goes once cardless. It is never re-keyed (review, 18 Nov 2026).
        var fresh = save?.FreshModels ?? new HashSet<Guid>();
        var freshIds = fresh.ToArray();
        // KATALOG-14 D5 step B2a: a model this run opens takes the shared values of the model its first card is on (the card's
        // brand stays its link key, the name its own), never the card's copies; a card on no model gives its own.
        var productRows = await (from x in db.Products.AsNoTracking()
                                 join m in db.ProductModels.AsNoTracking() on new { x.TenantId, Id = x.FamilyId } equals new { m.TenantId, Id = (Guid?)m.Id } into ms
                                 from m in ms.DefaultIfEmpty()
                                 where x.TenantId == tenantId && cardIds.Contains(x.Id)
                                 select new
                                 {
                                     x.Id,
                                     x.BrandId,
                                     x.FamilyId,
                                     x.CreatedAt,
                                     x.Title,
                                     Description = m == null ? x.Description : m.Description,
                                     CategoryId = m == null ? x.CategoryId : m.CategoryId,
                                     VatRate = m == null ? x.VatRate : m.VatRate,
                                     VatInclusion = m == null ? x.VatInclusion : m.VatInclusion,
                                     WarrantyMonths = m == null ? x.WarrantyMonths : m.WarrantyMonths,
                                     DeliveryDays = m == null ? x.DeliveryDays : m.DeliveryDays,
                                     Desi = m == null ? null : m.Desi,
                                 })
                .ToListAsync(cancellationToken);
        var actualFamilies = productRows.ToDictionary(x => x.Id, x => x.FamilyId);
        var products = productRows.Select(x => new { x.Id, x.BrandId, FamilyId = x.FamilyId is Guid family && fresh.Contains(family) ? null : x.FamilyId, x.CreatedAt, x.Title, x.Description, x.CategoryId, x.VatRate, x.VatInclusion, x.WarrantyMonths, x.DeliveryDays, x.Desi })
            .ToDictionary(x => x.Id);
        var targetKeyArray = targetKeys.ToArray();
        var models = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && targetKeyArray.Contains(x.ModelCodeKey) && !freshIds.Contains(x.Id))
            .Select(x => new { x.Id, x.BrandId, x.ModelCodeKey }).ToListAsync(cancellationToken);
        var modelIds = models.Select(x => x.Id).ToArray();
        var linkedElsewhere = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && x.FamilyId != null && modelIds.Contains(x.FamilyId.Value))
            .Select(x => new { x.Id, x.FamilyId }).ToListAsync(cancellationToken);

        var openIssueKeys = (await db.OperationalIssues.AsNoTracking().Where(x => x.TenantId == tenantId && x.Code == AmbiguousCode && x.Status != IssueStatus.Resolved).Select(x => x.DedupeKey).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var desired = new Dictionary<Guid, Guid?>();
        var ownModelIds = models.Where(x => IsOwnModelKey(x.ModelCodeKey)).Select(x => x.Id).ToHashSet();
        // Models given another key in this run (KATALOG-2): no longer a model of the key they were read under.
        var rekeyed = new HashSet<Guid>();
        var externalIds = await ExternalIdsAsync(db, tenantId, cardIds, cancellationToken);
        foreach (var key in targetKeys.Order(StringComparer.Ordinal))
        {
            // In the order the cards were brought in (FirstBroughtIn): the first gives a new model its values.
            var keyCards = FirstBroughtIn(db, cardKeys.Where(x => x.Key == key && products.ContainsKey(x.ProductId)).Select(x => (Card: products[x.ProductId], x.Color, x.Code)),
                x => x.Card.Id, x => x.Card.CreatedAt, x => x.Color, externalIds);
            // Owner (17 Nov 2026, "farklı markalar aynı model kodunu kullanabiliyor"): a model is its brand and its code. The same
            // code under another brand is that brand's own model: nothing is refused, skipped or warned about.
            var keyModels = models.Where(x => x.ModelCodeKey == key && !rekeyed.Contains(x.Id)).ToList();
            if (openIssueKeys.Contains(AmbiguousDedupeKey(key))) await ResolveAmbiguityAsync(db, tenantId, key, now, cancellationToken);
            if (keyCards.Count == 0) continue;
            var brandedModels = keyModels.Where(model => model.BrandId is not null).Select(model => model.BrandId).ToList();
            // A brand a save gives a linked colour moves its model to that brand's key: refused here, in the save's transaction,
            // when that brand (a blank brand too, owner 17 Nov 2026) already has a model of the code (nothing is merged).
            // A card whose brand and code the save changed together (an Excel row with Marka and Model Kodu, an editor save) is
            // not linked to a model of this code yet: it is refused when the model it would join (LinkBrand of its new brand)
            // already holds colours, as a code change to a code its brand holds is (review, 18 Nov 2026).
            if (save is not null && keyCards.FirstOrDefault(x => save.BrandChanges.TryGetValue(x.Card.Id, out var given)
                    && (keyModels.FirstOrDefault(model => model.Id == x.Card.FamilyId) is { } linked
                        ? given != linked.BrandId && keyModels.Any(model => model.Id != linked.Id && model.BrandId == given)
                        : LinkBrand(given, brandedModels) is var joining && keyModels.Any(model => model.BrandId == joining && linkedElsewhere.Any(card => card.FamilyId == model.Id && card.Id != x.Card.Id))))
                    is { Card: not null } moving)
            {
                var given = save.BrandChanges[moving.Card.Id];
                var taken = keyModels.Any(model => model.Id == moving.Card.FamilyId) ? given : LinkBrand(given, brandedModels);
                var brandName = taken is null ? null : await db.Brands.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == taken).Select(x => x.Name).SingleOrDefaultAsync(cancellationToken);
                throw ModelRuleException.BrandCodeTaken(moving.Code, brandName ?? "markasız");
            }
            // A colour whose brand this save changed stays with its model (a brand change moves the whole model, ModelMirror);
            // a card without a brand stays with the model of the code it is linked to; every other card joins the model LinkBrand
            // names.
            Guid? GroupBrand(Guid cardId, Guid? familyId, Guid? cardBrand) =>
                (save is not null && save.BrandChanges.ContainsKey(cardId) || cardBrand is null) && keyModels.FirstOrDefault(model => model.Id == familyId) is { } linked ? linked.BrandId
                : LinkBrand(cardBrand, brandedModels);
            foreach (var brandGroup in keyCards.GroupBy(x => GroupBrand(x.Card.Id, x.Card.FamilyId, x.Card.BrandId)))
            {
                var brand = brandGroup.Key;
                var cards = brandGroup.ToList();
                var current = keyModels.Where(model => model.BrandId == brand).OrderByDescending(model => cards.Count(x => x.Card.FamilyId == model.Id)).ThenBy(model => model.Id).FirstOrDefault();
                var target = current?.Id;
                // KATALOG-14 phase D5b review: a model left without a colour (its cards deleted or re-coded) is deleted, never
                // reused: the cards that come to its brand and code open a new model from their own values.
                if (current is not null && !linkedElsewhere.Any(x => x.FamilyId == current.Id)
                    && await DeleteModelsWithoutCardsAsync(db, db.ProductModels.Where(x => x.TenantId == tenantId && x.Id == current.Id && !db.Products.Any(product => product.TenantId == tenantId && product.FamilyId == x.Id)), cancellationToken) == 1)
                {
                    rekeyed.Add(current.Id);
                    target = null;
                }
                // KATALOG-2: the cards' code was corrected together. A model of the same brand is kept by its id, not re-opened
                // under the new code: when every card linked to it now holds this key (none stays behind on the old code, and no
                // card of another key was given it in this run), it takes the new code, so its marketplace model links and
                // saved attributes stay. Cards of another model or of none may join it.
                // Owner (17 Nov 2026): every field from the first card brought in (else the oldest card that knows it).
                var fields = ProductFamilySharedFields.NewModelValues(
                    [.. cards.Select(x => new ProductFamilySharedFields.CardFields(x.Card.Id, x.Color, x.Card.Title, x.Card.Description, x.Card.BrandId, x.Card.CategoryId, x.Card.VatRate, x.Card.VatInclusion, x.Card.WarrantyMonths, x.Card.DeliveryDays))]);
                var titleCard = cards.FirstOrDefault(x => x.Card.Title.Trim() == fields.Title);
                // KATALOG-14 phase D5b: the colour word the name holds, whichever colour card it came from (a copy can hold another colour's).
                var titleColor = (titleCard.Color is { } color && ProductFamilyNewColorCard.HasColorWord(fields.Title, color) ? color : cards.Select(x => x.Color).FirstOrDefault(other => other is not null && ProductFamilyNewColorCard.HasColorWord(fields.Title, other))) is { } word ? Short(word.Trim(), 120) : null;
                // KATALOG-14 D5 step B1 (review, 18 Nov 2026): a model of its own ("#<card id>", a card that had no code) is re-keyed
                // too, so its saved fields, attributes, size chart and marketplace model links stay; it then takes the name and
                // colour word the code's model opens with (NewModelValues: the first card brought in), never the name of whichever
                // own model came first by id. Among own models the first card brought in gives its own. A fresh own model (made by
                // this save, SaveScope.FreshModels) is not among the models at all. A code that already has its model: it wins.
                if (target is null
                    && models.Where(model => model.ModelCodeKey != key && model.BrandId == brand && !rekeyed.Contains(model.Id) && cards.Any(x => x.Card.FamilyId == model.Id))
                        .OrderByDescending(model => cards.Count(x => x.Card.FamilyId == model.Id))
                        .ThenBy(model => IsOwnModelKey(model.ModelCodeKey) ? cards.FindIndex(x => x.Card.FamilyId == model.Id) : 0)
                        .ThenBy(model => model.Id).FirstOrDefault() is { } recoding
                    && linkedElsewhere.Where(x => x.FamilyId == recoding.Id).All(x => cards.Any(card => card.Card.Id == x.Id))
                    && !desired.Any(pair => pair.Value == recoding.Id)
                    && await RekeyAsync(recoding.Id, IsOwnModelKey(recoding.ModelCodeKey)) == 1)
                {
                    rekeyed.Add(recoding.Id);
                    target = recoding.Id;
                }

                Task<int> RekeyAsync(Guid modelId, bool own)
                {
                    var rows = db.ProductModels.Where(x => x.TenantId == tenantId && x.Id == modelId && !db.ProductModels.Any(other => other.TenantId == tenantId && other.ModelCodeKey == key && other.BrandId == brand));
                    var code = Short(cards[0].Code, 160);
                    var title = Short(fields.Title, 320);
                    return own
                        ? rows.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ModelCode, code).SetProperty(x => x.ModelCodeKey, key)
                            .SetProperty(x => x.Title, title).SetProperty(x => x.TitleColor, titleColor)
                            .SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken)
                        : rows.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ModelCode, code).SetProperty(x => x.ModelCodeKey, key)
                            .SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
                }
                if (target is null)
                {
                    // KATALOG-14 D5 (B2a review): the desi too, from the model of the first card brought in that holds one (the
                    // cards' values are their models').
                    var desi = cards.Select(x => x.Card.Desi).FirstOrDefault(value => value is not null);
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO catalog.product_models ("Id", "TenantId", "BrandId", "ModelCode", "ModelCodeKey", "Title", "TitleColor", "Description", "CategoryId", "VatRate", "VatInclusion", "WarrantyMonths", "DeliveryDays", "Desi", "FieldsSavedAt", "CreatedAt", "UpdatedAt", "Version")
                        VALUES ({Guid.CreateVersion7()}, {tenantId}, {brand}, {Short(cards[0].Code, 160)}, {key}, {Short(fields.Title, 320)}, {titleColor}, {fields.Description}, {fields.CategoryId}, {fields.VatRate}, {fields.VatInclusion}, {fields.WarrantyMonths}, {fields.DeliveryDays}, {desi}, {(DateTimeOffset?)null}, {now}, {now}, {1L})
                        ON CONFLICT DO NOTHING
                        """, cancellationToken);
                    target = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && x.ModelCodeKey == key && x.BrandId == brand).OrderBy(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
                }
                foreach (var (card, _, _) in cards) desired[card.Id] = target;
            }
        }
        // A card linked to one of these models that no longer carries its key (its code changed): unlinked, unless its own
        // key is reconciled above.
        // A card holding a model of its own (a card without a code, KATALOG-14 phase D5a) stays on it.
        foreach (var stray in linkedElsewhere.Where(x => !desired.ContainsKey(x.Id) && !(x.FamilyId is Guid family && ownModelIds.Contains(family)))) desired[stray.Id] = null;

        // The models cards leave in this run (an own model "#<id>" included, whose key is not reconciled here).
        var left = desired.Where(pair => pair.Value != CurrentFamily(pair.Key)).Select(pair => CurrentFamily(pair.Key)).OfType<Guid>().Distinct().ToArray();
        foreach (var group in desired.Where(pair => pair.Value != CurrentFamily(pair.Key)).GroupBy(pair => pair.Value))
        {
            var ids = group.Select(pair => pair.Key).ToArray();
            var familyId = group.Key;
            // KATALOG-14 D5 step B1: a card taken off its model goes straight to a model of its own (no NULL link).
            if (familyId is null)
            {
                await GiveOwnModelsAsync(db, tenantId, ids, now, cancellationToken);
                continue;
            }
            await db.Products.Where(x => x.TenantId == tenantId && ids.Contains(x.Id) && x.FamilyId != familyId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.FamilyId, familyId), cancellationToken);
        }
        // KATALOG-14 phase D5a: a card left without a model (its code gone) gets one of its own; the whole tenant's run
        // gives one to every card still without.
        await EnsureOwnModelsAsync(db, tenantId, keyArray is null ? null : [.. desired.Where(pair => pair.Value is null).Select(pair => pair.Key)], now, cancellationToken);
        // KATALOG-14 phase D5b review 2: a whole-tenant run (startup pass, nightly scan, a raw-SQL delete) never touches a
        // model's brand, an own model's included; only a save of the card moves its own model (AfterSaveAsync).
        // KATALOG-10: the links moved; the product list's cached rule check of the tenant is dropped.
        CatalogListGenerations.Bump(tenantId);
        // Faz 2: the pictures follow their card's model.
        await SyncMediaFamiliesAsync(db, tenantId, keyArray is null ? null : [.. desired.Keys], cancellationToken);
        // Models left without a card go.
        await DeleteModelsWithoutCardsAsync(db, db.ProductModels.Where(x => x.TenantId == tenantId && (targetKeyArray.Contains(x.ModelCodeKey) || left.Contains(x.Id)) && !db.Products.Any(product => product.FamilyId == x.Id)), cancellationToken);
        if (keyArray is null)
            await DeleteModelsWithoutCardsAsync(db, db.ProductModels.Where(x => x.TenantId == tenantId && !db.Products.Any(product => product.FamilyId == x.Id)), cancellationToken);
        // Faz 2: a model link whose model no longer has a card holding a variant link on its connection goes.
        await MarketplaceModelLinks.RemoveOrphansAsync(db, tenantId, keyArray is null ? null : modelIds, cancellationToken);
        // KATALOG-14 phase D5b review: the model warnings of models this run (or an earlier delete) removed are settled.
        await ResolveWarningsOfMissingModelsAsync(db, tenantId, now, cancellationToken);

        Guid? CurrentFamily(Guid productId) =>
            actualFamilies.TryGetValue(productId, out var family) ? family : linkedElsewhere.FirstOrDefault(x => x.Id == productId)?.FamilyId;
    }

    // KATALOG-14 phase D5b (owner, 17 Nov 2026: "ilk çekilen"): the cards of a model in the order they were brought in, the
    // first one giving a new model its values. Cards pulled before this save come first (the earlier pulled wins), by
    // CreatedAt; the cards a marketplace import brings in this save follow in the order the marketplace sent them (page and
    // row, AppDbContext.ModelOrderHint). Ties are broken by the colour's key (CatalogKeyNormalizer, ordinal), then the
    // card's external id (its marketplace listing, ordinal), then its id, so the same catalog pulled twice from scratch
    // gives the same model whatever the clock.
    internal static List<T> FirstBroughtIn<T>(AppDbContext db, IEnumerable<T> cards, Func<T, Guid> id, Func<T, DateTimeOffset> createdAt, Func<T, string?> colour, IReadOnlyDictionary<Guid, string> externalIds) =>
        [.. cards.OrderBy(card => db.ModelOrderHint.ContainsKey(id(card)) ? 1 : 0)
            .ThenBy(card => db.ModelOrderHint.TryGetValue(id(card), out var position) ? position : 0)
            .ThenBy(createdAt)
            .ThenBy(card => CatalogKeyNormalizer.Key(colour(card)), StringComparer.Ordinal)
            .ThenBy(card => externalIds.GetValueOrDefault(id(card)) ?? "", StringComparer.Ordinal)
            .ThenBy(id)];

    // The external id of each card (its marketplace listings' smallest, ordinal), for FirstBroughtIn's last tie-break.
    internal static async Task<Dictionary<Guid, string>> ExternalIdsAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        if (productIds.Count == 0) return [];
        var ids = productIds.ToArray();
        return (await db.MarketplaceProductLinks.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.ProductId)).Select(x => new { x.ProductId, x.ExternalId }).ToListAsync(cancellationToken))
            .GroupBy(x => x.ProductId).ToDictionary(group => group.Key, group => group.Select(x => x.ExternalId).Order(StringComparer.Ordinal).First());
    }

    // KATALOG-14 phase D5b (review, 17 Nov 2026): the models the given cards were linked to that no card holds any more go
    // (a product delete, in its own transaction, so a reconcile that fails afterwards leaves no stale model whose old values
    // a re-import would hand its new colours), then the model warnings of models that are gone are settled.
    public static async Task DeleteCardlessModelsAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> modelIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (modelIds.Count > 0)
        {
            var ids = modelIds.ToArray();
            await db.ProductModels.Where(x => x.TenantId == tenantId && ids.Contains(x.Id) && !db.Products.Any(product => product.TenantId == tenantId && product.FamilyId == x.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }
        await ResolveWarningsOfMissingModelsAsync(db, tenantId, now, cancellationToken);
    }

    // The PRODUCT_MODEL_VALUES_DIFFER warnings of a model that no longer exists (its own key names it; the tenant's
    // warning when the tenant has no model left) are resolved, and every PRODUCT_MODEL_AMBIGUOUS warning: the same code under
    // two brands is two models (owner, 17 Nov 2026), so nothing is ambiguous any more.
    public static Task ResolveWarningsOfMissingModelsAsync(AppDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var at = now.ToUniversalTime();
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.operational_issues i SET "Status" = 'Resolved', "ResolvedAt" = {at}, "LastSeenAt" = {at}
            WHERE i."TenantId" = {tenantId} AND i."Status" <> 'Resolved' AND (
                (i."Code" = {ProductModelValues.DifferCode} AND (i."DedupeKey" LIKE {ModelMirror.GapDedupePrefix + "%"} OR i."DedupeKey" LIKE {ModelMirror.DifferDedupePrefix + "%"})
                    AND NOT EXISTS (SELECT 1 FROM catalog.product_models m WHERE m."TenantId" = {tenantId} AND replace(m."Id"::text, '-', '') = split_part(i."DedupeKey", ':', 2)))
                OR (i."Code" = {ProductModelValues.DifferCode} AND i."DedupeKey" = {ProductModelValues.DifferDedupeKey}
                    AND NOT EXISTS (SELECT 1 FROM catalog.product_models m WHERE m."TenantId" = {tenantId}))
                OR i."Code" = {AmbiguousCode})
            """, cancellationToken);
    }

    // KATALOG-14 phase D5a: a card the model-code rule gives no model (it has no model code) is a model of its own, keyed
    // "#<card id>" (a model-code key never holds '#', CatalogKeyNormalizer), with the card's brand, code-less, and its
    // values. Nothing is merged: one card, one model. When the card gets a code it joins the code's model when there is one, else
    // its model takes the code (KATALOG-2's re-key) with the name and colour word the code's model opens with (KATALOG-14 D5
    // B1); a fresh own model of the same save is never re-keyed. productIds null: every card of the tenant without a model.
    public const string OwnModelKeyPrefix = "#";


    public static bool IsOwnModelKey(string key) => key.StartsWith(OwnModelKeyPrefix, StringComparison.Ordinal);

    public static async Task<int> EnsureOwnModelsAsync(AppDbContext db, Guid tenantId, Guid[]? productIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (productIds is { Length: 0 }) return 0;
        var cards = await db.Products.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.FamilyId == null && (productIds == null || productIds.Contains(x.Id)))
            .Select(x => new OwnModelCard(x.Id, x.BrandId, x.Title, x.Description, x.CategoryId, x.VatRate, x.VatInclusion, x.WarrantyMonths, x.DeliveryDays, null))
            .ToListAsync(cancellationToken);
        return await LinkOwnModelsAsync(db, tenantId, cards, now, cancellationToken);
    }

    // KATALOG-14 D5 step B1: cards the reconcile takes off their model (their code gone) move straight to a model of their own,
    // never through a NULL link (products.FamilyId NOT NULL).
    // KATALOG-14 D5 step B2a: the own model opens with the values of the model the card leaves (its brand and name are the
    // card's own), never the card's copies.
    private static async Task GiveOwnModelsAsync(AppDbContext db, Guid tenantId, Guid[] productIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cards = await (from x in db.Products.AsNoTracking()
                           join m in db.ProductModels.AsNoTracking() on new { x.TenantId, Id = x.FamilyId } equals new { m.TenantId, Id = (Guid?)m.Id } into ms
                           from m in ms.DefaultIfEmpty()
                           where x.TenantId == tenantId && productIds.Contains(x.Id)
                           select new OwnModelCard(x.Id, x.BrandId, x.Title,
                               m == null ? x.Description : m.Description, m == null ? x.CategoryId : m.CategoryId, m == null ? x.VatRate : m.VatRate,
                               m == null ? x.VatInclusion : m.VatInclusion, m == null ? x.WarrantyMonths : m.WarrantyMonths, m == null ? x.DeliveryDays : m.DeliveryDays, m == null ? null : m.Desi))
            .ToListAsync(cancellationToken);
        await LinkOwnModelsAsync(db, tenantId, cards, now, cancellationToken);
    }

    private sealed record OwnModelCard(Guid Id, Guid? BrandId, string Title, string Description, Guid? CategoryId, decimal? VatRate, string? VatInclusion, int? WarrantyMonths, int? DeliveryDays, decimal? Desi);

    private static async Task<int> LinkOwnModelsAsync(AppDbContext db, Guid tenantId, List<OwnModelCard> cards, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var card in cards)
        {
            var key = $"{OwnModelKeyPrefix}{card.Id:N}";
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO catalog.product_models ("Id", "TenantId", "BrandId", "ModelCode", "ModelCodeKey", "Title", "TitleColor", "Description", "CategoryId", "VatRate", "VatInclusion", "WarrantyMonths", "DeliveryDays", "Desi", "FieldsSavedAt", "CreatedAt", "UpdatedAt", "Version")
                VALUES ({Guid.CreateVersion7()}, {tenantId}, {card.BrandId}, {""}, {key}, {Short(card.Title, 320)}, {(string?)null}, {card.Description}, {card.CategoryId}, {card.VatRate}, {card.VatInclusion}, {card.WarrantyMonths}, {card.DeliveryDays}, {card.Desi}, {(DateTimeOffset?)null}, {now}, {now}, {1L})
                ON CONFLICT DO NOTHING
                """, cancellationToken);
            var modelId = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && x.ModelCodeKey == key).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
            if (modelId is Guid id)
                await db.Products.Where(x => x.TenantId == tenantId && x.Id == card.Id && x.FamilyId != id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.FamilyId, id), cancellationToken);
        }
        if (cards.Count > 0) await SyncMediaFamiliesAsync(db, tenantId, [.. cards.Select(x => x.Id)], cancellationToken);
        return cards.Count;
    }

    // KATALOG-14 phase D5a (review, 17 Nov 2026): a one-card model ("#<card id>") holds its card's brand: a card whose
    // brand changes takes its model with it (a coded model is one per brand and code, re-linked by the reconcile).
    // productIds null: every own model of the tenant. Returns the number of models moved.
    public static Task<int> SyncOwnModelBrandsAsync(AppDbContext db, Guid tenantId, Guid[]? productIds, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync("""
            UPDATE catalog.product_models m SET "BrandId" = p."BrandId", "UpdatedAt" = @now, "Version" = m."Version" + 1
            FROM catalog.products p
            WHERE m."TenantId" = @tenant AND p."TenantId" = m."TenantId" AND p."FamilyId" = m."Id" AND m."ModelCodeKey" LIKE '#%'
              AND (@all OR p."Id" = ANY(@products)) AND m."BrandId" IS DISTINCT FROM p."BrandId"
            """, [new NpgsqlParameter("tenant", tenantId), new NpgsqlParameter("all", productIds is null), new NpgsqlParameter("products", productIds ?? []), new NpgsqlParameter("now", now.ToUniversalTime())], cancellationToken);

    // Faz 2: a picture belongs to its card's model (product_media.FamilyId = products.FamilyId); productIds null: the tenant.
    // Only rows that differ are written.
    public static Task<int> SyncMediaFamiliesAsync(AppDbContext db, Guid tenantId, Guid[]? productIds, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync("""
            UPDATE catalog.product_media m SET "FamilyId" = p."FamilyId"
            FROM catalog.products p
            WHERE m."TenantId" = @tenant AND p."TenantId" = m."TenantId" AND p."Id" = m."ProductId"
              AND (@all OR m."ProductId" = ANY(@products)) AND m."FamilyId" IS DISTINCT FROM p."FamilyId"
            """, [new NpgsqlParameter("tenant", tenantId), new NpgsqlParameter("all", productIds is null), new NpgsqlParameter("products", productIds ?? [])], cancellationToken);

    // Faz 2 computed the fields of models the editor had not saved from their cards at every deploy. KATALOG-14 phase D5b:
    // the model is the source of its fields; nothing recomputes them from the cards any more (DatabaseMigrator still calls
    // this, so a rollback build is one method away). Returns 0.
    public static Task<int> RecomputeUnsavedFieldsAsync(AppDbContext db, CancellationToken cancellationToken) => Task.FromResult(0);

    // Faz 2: the model's category attributes. The colour attributes (Renk, Web Color) stay each card's.
    private static async Task<HashSet<Guid>> ColorAttributeIdsAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> attributeIds, CancellationToken cancellationToken)
    {
        if (attributeIds.Count == 0) return [];
        var ids = attributeIds.ToArray();
        return (await db.AttributeDefinitions.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(cancellationToken))
            .Where(x => VariantOptionSignature.IsColorName(x.Name) || VariantOptionSignature.IsWebColorName(x.Name)).Select(x => x.Id).ToHashSet();
    }

    // The key the editor compares attribute sets by (numbers as 0.####: the database gives numeric(19,4)).
    internal static string AttributeKey(Guid attributeId, Guid? valueId, string? text, decimal? number, bool? flag) =>
        $"{attributeId:N}|{valueId:N}|{text?.Trim()}|{number?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}|{flag}";

    private sealed record AttributeRow(Guid ProductId, Guid AttributeId, Guid? ValueId, string? TextValue, decimal? NumberValue, bool? BooleanValue, int SortOrder);

    // Replaces the model's attribute rows with the given set when it differs; returns whether it wrote. The editor's
    // write (savedAt given) stamps AttributesSavedAt; the deploy-time recompute (savedAt null) writes only a model the
    // editor has still not saved.
    // VERITABANI-DAGITIM-12: both lock the model row first, in one transaction with the rows they replace and the stamp,
    // so a recompute and an editor save of the same model take turns: a recompute that waited for a save finds the model
    // saved and leaves it, and a save that waited for a recompute replaces the recomputed rows. Before, the recompute
    // replaced the rows on the list it read at the start and could overwrite a set the editor saved meanwhile.
    // KATALOG-14 phase B (ProductModelValues): the cards' majority set, unless the record holds the owner's saved set
    // (HoldsSavedAttributes, checked by the caller); the stamp is left as it is.
    internal sealed record ModelAttributeRow(Guid AttributeId, Guid? ValueId, string? TextValue, decimal? NumberValue, bool? BooleanValue, int SortOrder);

    internal static Task<bool> ReplaceModelAttributesAsync(AppDbContext db, Guid tenantId, Guid modelId, IReadOnlyList<ModelAttributeRow> rows, CancellationToken cancellationToken) =>
        ReplaceModelAttributesAsync(db, tenantId, modelId, [.. rows.Select(x => new AttributeRow(Guid.Empty, x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue, x.SortOrder))], null, cancellationToken, onlyUnsaved: false);

    private static async Task<bool> ReplaceModelAttributesAsync(AppDbContext db, Guid tenantId, Guid modelId, IReadOnlyList<AttributeRow> rows, DateTimeOffset? savedAt, CancellationToken cancellationToken, bool onlyUnsaved = true)
    {
        rows = [.. rows.DistinctBy(x => (x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue))];
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var locked = savedAt is null && onlyUnsaved
            ? await db.Database.SqlQuery<int>($"""
                SELECT 1 AS "Value" FROM catalog.product_models
                WHERE "TenantId" = {tenantId} AND "Id" = {modelId} AND "AttributesSavedAt" IS NULL
                FOR UPDATE
                """).ToListAsync(cancellationToken)
            : await db.Database.SqlQuery<int>($"""
                SELECT 1 AS "Value" FROM catalog.product_models
                WHERE "TenantId" = {tenantId} AND "Id" = {modelId}
                FOR UPDATE
                """).ToListAsync(cancellationToken);
        if (savedAt is null && locked.Count == 0)
        {
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return false;
        }
        var current = await db.ProductModelAttributes.AsNoTracking().Where(x => x.TenantId == tenantId && x.ModelId == modelId).ToListAsync(cancellationToken);
        var same = current.Select(x => AttributeKey(x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue)).Order(StringComparer.Ordinal)
            .SequenceEqual(rows.Select(x => AttributeKey(x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue)).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        if (!same)
        {
            // Delete and insert in the one transaction: a failed insert leaves the model's set as it was.
            await db.ProductModelAttributes.Where(x => x.TenantId == tenantId && x.ModelId == modelId).ExecuteDeleteAsync(cancellationToken);
            foreach (var row in rows)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO catalog.product_model_attributes ("Id", "TenantId", "ModelId", "AttributeId", "ValueId", "TextValue", "NumberValue", "BooleanValue", "SortOrder")
                    VALUES ({Guid.CreateVersion7()}, {tenantId}, {modelId}, {row.AttributeId}, {row.ValueId}, {row.TextValue}, {row.NumberValue}, {row.BooleanValue}, {row.SortOrder})
                    ON CONFLICT DO NOTHING
                    """, cancellationToken);
        }
        if (savedAt is { } at)
            await db.ProductModels.Where(x => x.TenantId == tenantId && x.Id == modelId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.AttributesSavedAt, at).SetProperty(x => x.UpdatedAt, at).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return !same;
    }

    // The editor saved the model's attributes to every card: the model takes the opened card's card-level set, less its
    // colour attributes (write-through, AttributesSavedAt).
    public static async Task WriteAttributesAsync(AppDbContext db, Guid tenantId, Guid productId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var modelId = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == productId).Select(x => x.FamilyId).SingleOrDefaultAsync(cancellationToken);
        if (modelId is not { } model) return;
        var rows = await db.ProductAttributeAssignments.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == productId && x.VariantId == null)
            .Select(x => new AttributeRow(x.ProductId, x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue, x.SortOrder)).ToListAsync(cancellationToken);
        var colors = await ColorAttributeIdsAsync(db, tenantId, [.. rows.Select(x => x.AttributeId).Distinct()], cancellationToken);
        await ReplaceModelAttributesAsync(db, tenantId, model, [.. rows.Where(x => !colors.Contains(x.AttributeId))], now, cancellationToken);
    }

    // Faz 2: the attributes of every model the editor has not saved them for yet (AttributesSavedAt null) are the set most
    // of its cards hold, the oldest card's on a tie (the editor's ModelAttributeCard rule), less the colour attributes.
    // Runs after the migrations (DatabaseMigrator); a model whose rows already agree is not written. Returns the count.
    public static async Task<int> RecomputeUnsavedAttributesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var models = await db.ProductModels.AsNoTracking().Where(x => x.AttributesSavedAt == null).Select(x => new { x.Id, x.TenantId }).ToListAsync(cancellationToken);
        if (models.Count == 0) return 0;
        var written = 0;
        foreach (var tenant in models.GroupBy(x => x.TenantId))
        {
            var tenantId = tenant.Key;
            var modelIds = tenant.Select(x => x.Id).ToArray();
            var cards = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && x.FamilyId != null && modelIds.Contains(x.FamilyId.Value))
                .Select(x => new { x.Id, FamilyId = x.FamilyId!.Value, x.CreatedAt }).ToListAsync(cancellationToken);
            var cardIds = cards.Select(x => x.Id).ToArray();
            var rows = (await db.ProductAttributeAssignments.AsNoTracking().Where(x => x.TenantId == tenantId && cardIds.Contains(x.ProductId) && x.VariantId == null)
                    .Select(x => new AttributeRow(x.ProductId, x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue, x.SortOrder)).ToListAsync(cancellationToken))
                .ToLookup(x => x.ProductId);
            var colors = await ColorAttributeIdsAsync(db, tenantId, [.. rows.SelectMany(x => x).Select(x => x.AttributeId).Distinct()], cancellationToken);
            foreach (var model in cards.GroupBy(x => x.FamilyId))
            {
                var ordered = model.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => rows[x.Id].Where(row => !colors.Contains(row.AttributeId)).ToList()).ToList();
                var keys = ordered.Select(set => string.Join(";", set.Select(x => AttributeKey(x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue)).Order(StringComparer.Ordinal))).ToList();
                var chosen = ordered[ProductFamilySharedFields.ModelAttributeCard(keys)];
                // One model at a time: a model a save is writing is left for the next deploy, the rest go on.
                try
                {
                    if (await ReplaceModelAttributesAsync(db, tenantId, model.Key, chosen, null, cancellationToken)) written++;
                }
                catch (System.Data.Common.DbException) when (db.Database.CurrentTransaction is null) { }
            }
        }
        return written;
    }

    // Faz 2 (editor read): the model record's shared fields for the editor, when they are the cards' current values: the
    // editor saved them (FieldsSavedAt) after the last change of every card of the family, every card is linked to this
    // model and the model's category is one a card holds. Null otherwise: the editor goes by the cards' majority
    // (ProductFamilySharedFields.ModelValues) as before. The name is given with the opened card's colour.
    public static async Task<ProductFamilySharedFields.ModelFields?> SavedFieldsAsync(AppDbContext db, Guid tenantId, Guid? familyId, IReadOnlyCollection<Product> cards, string? openedColor, CancellationToken cancellationToken)
    {
        if (familyId is not { } modelId || cards.Count == 0 || cards.Any(x => x.FamilyId != modelId)) return null;
        var model = await db.ProductModels.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == modelId, cancellationToken);
        if (model is null || !HoldsSavedFields(model, cards.Select(x => x.UpdatedAt))) return null;
        return new ProductFamilySharedFields.ModelFields(
            ProductFamilySharedFields.TitleFor(model.Title, model.TitleColor, openedColor), model.Description, model.BrandId, model.CategoryId,
            model.VatRate, model.VatInclusion, model.WarrantyMonths, model.DeliveryDays);
    }

    // KATALOG-14 D5 (B1 review): deletes the given models, which the query keeps to those without a colour card. The check
    // reads READ COMMITTED, so a card another save adds to such a model and commits while the delete runs is not seen by it;
    // the key products.FamilyId -> product_models is ON DELETE RESTRICT (20261127170948_ProductFamilyRestrict), so the
    // database refuses that delete on this key (SQLSTATE 23001 restrict_violation, "violates RESTRICT setting of foreign key
    // constraint"; 23503 foreign_key_violation is taken too, in case the key is ever NO ACTION) instead of nulling the NOT NULL
    // link. The delete is then run again (its check now sees the committed card and keeps that model), inside a savepoint
    // when the caller holds a transaction so the rest of its work stands. After three refusals the save stops with a
    // Turkish message (ModelRuleException, 409).
    internal static async Task<int> DeleteModelsWithoutCardsAsync(AppDbContext db, IQueryable<ProductModel> models, CancellationToken cancellationToken)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            var transaction = db.Database.CurrentTransaction;
            var savepoint = $"model_delete_{attempt}";
            if (transaction is not null) await transaction.CreateSavepointAsync(savepoint, cancellationToken);
            try
            {
                var deleted = await models.ExecuteDeleteAsync(cancellationToken);
                if (transaction is not null) await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
                return deleted;
            }
            catch (Exception exception) when (IsModelKeyRefusal(exception))
            {
                if (transaction is not null) await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
                if (attempt == attempts) throw ModelRuleException.ModelGainedCard();
            }
        }
    }

    private static bool IsModelKeyRefusal(Exception exception) =>
        (exception as PostgresException ?? exception.InnerException as PostgresException) is { SqlState: PostgresErrorCodes.RestrictViolation or PostgresErrorCodes.ForeignKeyViolation } refusal
        && refusal.ConstraintName == ProductFamilyKeyName;

    // The key of products.FamilyId (EF's name for the composite key (TenantId, FamilyId) -> product_models (TenantId, Id)).
    public const string ProductFamilyKeyName = "FK_products_product_models_TenantId_FamilyId";

    // The record holds the owner's saved fields (the editor shows them): the editor saved them (FieldsSavedAt) after the
    // last change of every card. ProductModelValues leaves such fields as they are, so the editor and the nightly scan never
    // take turns overwriting each other. KATALOG-14 D5 step B2a: no card copy is consulted (the category every card reads
    // is the record's own, EffectiveCatalog), so a stale copy never makes a saved record look unsaved.
    internal static bool HoldsSavedFields(ProductModel model, IEnumerable<DateTimeOffset> cardsUpdatedAt) =>
        model.FieldsSavedAt is { } savedAt && cardsUpdatedAt.All(updatedAt => updatedAt <= savedAt);

    // The record holds the owner's saved category attributes: saved (AttributesSavedAt) after the last change of every card.
    internal static bool HoldsSavedAttributes(DateTimeOffset? attributesSavedAt, IEnumerable<DateTimeOffset> cardsUpdatedAt) =>
        attributesSavedAt is { } savedAt && cardsUpdatedAt.All(updatedAt => updatedAt <= savedAt);

    // Faz 2 (editor read): the model record's category attributes (without colour), when the editor saved them after the
    // last change of every card and the opened card still holds exactly that set; null otherwise (the cards' majority).
    public static async Task<IReadOnlyList<ProductModelAttribute>?> SavedAttributesAsync(AppDbContext db, Guid tenantId, Guid? familyId, IReadOnlyCollection<Product> cards, IReadOnlyCollection<(Guid AttributeId, Guid? ValueId, string? TextValue, decimal? NumberValue, bool? BooleanValue)> openedCardAttributes, CancellationToken cancellationToken)
    {
        if (familyId is not { } modelId || cards.Count == 0 || cards.Any(x => x.FamilyId != modelId)) return null;
        var savedAt = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == modelId).Select(x => x.AttributesSavedAt).SingleOrDefaultAsync(cancellationToken);
        if (!HoldsSavedAttributes(savedAt, cards.Select(x => x.UpdatedAt))) return null;
        var rows = await db.ProductModelAttributes.AsNoTracking().Where(x => x.TenantId == tenantId && x.ModelId == modelId).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        var colors = await ColorAttributeIdsAsync(db, tenantId, [.. openedCardAttributes.Select(x => x.AttributeId).Distinct()], cancellationToken);
        var opened = openedCardAttributes.Where(x => !colors.Contains(x.AttributeId)).Select(x => AttributeKey(x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var model = rows.Select(x => AttributeKey(x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return opened.SequenceEqual(model, StringComparer.Ordinal) ? rows : null;
    }

    // The editor saved the model's fields to every card: the model takes the same values (write-through).
    // KATALOG-14 D5 step B2b (review L6): the values are the ones the editor saved on the card (the tracked card after its
    // save), never the card's copy columns, which are not written while Catalog:MirrorModelFields is off. The model is read
    // from the database (the save's reconcile may have moved the card).
    public static async Task WriteFieldsAsync(AppDbContext db, Guid tenantId, Product saved, string? titleColor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var familyId = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == saved.Id).Select(x => x.FamilyId).SingleOrDefaultAsync(cancellationToken);
        if (familyId is null) return;
        var card = new { FamilyId = familyId, saved.Title, saved.Description, saved.CategoryId, saved.VatRate, saved.VatInclusion, saved.WarrantyMonths, saved.DeliveryDays };
        var color = string.IsNullOrWhiteSpace(titleColor) ? null : Short(titleColor.Trim(), 120);
        var title = Short(card.Title, 320);
        // An empty warranty (a draft saved with it emptied) is the model's only while all its cards are drafts: a model
        // with a card on sale or closed keeps its own (review, 16 Nov 2026).
        var keepWarranty = card.WarrantyMonths is null
            && await db.Products.AnyAsync(x => x.TenantId == tenantId && x.FamilyId == card.FamilyId && x.Status != ProductStatus.Draft, cancellationToken);
        await db.ProductModels.Where(x => x.TenantId == tenantId && x.Id == card.FamilyId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Title, title).SetProperty(x => x.TitleColor, color).SetProperty(x => x.Description, card.Description)
                .SetProperty(x => x.CategoryId, card.CategoryId).SetProperty(x => x.VatRate, card.VatRate).SetProperty(x => x.VatInclusion, card.VatInclusion)
                .SetProperty(x => x.WarrantyMonths, x => keepWarranty ? x.WarrantyMonths : card.WarrantyMonths).SetProperty(x => x.DeliveryDays, card.DeliveryDays)
                .SetProperty(x => x.FieldsSavedAt, now).SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
    }

    // The editor's family with the model link: the cards linked to the opened card's model, plus the model-code rule's
    // cards that are linked to no model (a colour that came in since), less a card linked to another model.
    public static async Task<List<Guid>> FamilyAwareAsync(AppDbContext db, Guid tenantId, Guid? familyId, List<Guid> ruleIds, IQueryable<Product> visible, CancellationToken cancellationToken)
    {
        if (familyId is not { } modelId) return ruleIds;
        var ids = ruleIds.ToArray();
        var linkedElsewhere = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.Id) && x.FamilyId != null && x.FamilyId != modelId).Select(x => x.Id).ToListAsync(cancellationToken);
        var linked = await visible.Where(x => x.TenantId == tenantId && x.FamilyId == modelId).Select(x => x.Id).ToListAsync(cancellationToken);
        return ruleIds.Except(linkedElsewhere).Union(linked).ToList();
    }

    // Whether the cards are one model: one linked model, or one model-code key (KATALOG-14 phase D5b: the brand is a field
    // of the model, not part of its key).
    public static async Task<bool> OneModelAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        var ids = productIds.Distinct().ToArray();
        if (ids.Length <= 1) return true;
        var cards = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.Id)).Select(x => new { x.Id, x.BrandId, x.FamilyId }).ToListAsync(cancellationToken);
        if (cards.Count != ids.Length) return false;
        if (cards.All(x => x.FamilyId is not null) && cards.Select(x => x.FamilyId).Distinct().Count() == 1) return true;
        var codes = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.ProductId) && x.ModelCode != null)
            .Select(x => new CodeRow(x.ProductId, x.ModelCode, x.SortOrder, x.Id, null)).ToListAsync(cancellationToken);
        var keys = ids.Select(id => CardKey(codes.Where(x => x.ProductId == id))).ToList();
        return keys.All(key => key is not null) && keys.Distinct(StringComparer.Ordinal).Count() == 1;
    }

    public const string LinkFailedCode = "PRODUCT_MODEL_LINK_FAILED";
    public const string LinkFailedDedupeKey = "product-model-link-failed";

    // KATALOG-6: a model link or model field write that failed after the save stood (the save is kept, the link waits for
    // the next save) is no longer silent: one open PRODUCT_MODEL_LINK_FAILED issue per tenant names the last failure. Best
    // effort: a failure to record it is not the caller's.
    public static async Task RecordLinkFailureAsync(AppDbContext db, Guid tenantId, Exception exception, DateTimeOffset now)
    {
        var summary = Short($"Ürün kartlarının model bağı kayıttan sonra güncellenemedi ({exception.GetType().Name}: {exception.Message}). Kayıt duruyor; bağ bir sonraki kayıtta yeniden denenir.", 1000);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ops.operational_issues AS i ("Id", "TenantId", "DedupeKey", "Code", "Summary", "Status", "FirstSeenAt", "LastSeenAt", "OccurrenceCount", "ResolvedAt")
                VALUES ({Guid.CreateVersion7()}, {tenantId}, {LinkFailedDedupeKey}, {LinkFailedCode}, {summary}, 'Open', {now.ToUniversalTime()}, {now.ToUniversalTime()}, 1, NULL)
                ON CONFLICT ("TenantId", "DedupeKey") DO UPDATE SET
                    "Summary" = EXCLUDED."Summary",
                    "LastSeenAt" = EXCLUDED."LastSeenAt",
                    "OccurrenceCount" = i."OccurrenceCount" + 1,
                    "Status" = CASE WHEN i."Status" = 'Resolved' THEN 'Open' ELSE i."Status" END,
                    "ResolvedAt" = NULL
                """, CancellationToken.None);
        }
        catch (Exception recordFailure) when (recordFailure is not OperationCanceledException) { }
    }

    private static Task ResolveAmbiguityAsync(AppDbContext db, Guid tenantId, string key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var dedupeKey = AmbiguousDedupeKey(key);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.operational_issues SET "Status" = 'Resolved', "ResolvedAt" = {now}, "LastSeenAt" = {now}
            WHERE "TenantId" = {tenantId} AND "DedupeKey" = {dedupeKey} AND "Status" <> 'Resolved'
            """, cancellationToken);
    }

    private static string Short(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
