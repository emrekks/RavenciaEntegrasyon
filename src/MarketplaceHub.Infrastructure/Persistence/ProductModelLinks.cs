using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// MODEL-KAYDI-PLANI Faz 1: keeps catalog.product_models and products.FamilyId in step with the model-code rule. A card's
// model code is its first variant's (sort order, id) non-blank ModelCode. KATALOG-14 phase D5b (owner, 17 Nov 2026): a
// model is its code alone (one per tenant and key, a unique index) and the brand is one of its fields. For every key:
// - the key's model is found, re-keyed (KATALOG-2) or opened, and the cards are linked to it; an open
//   PRODUCT_MODEL_AMBIGUOUS issue of the key is resolved;
// - a card of another brand joining the code is refused: a save fails (ModelRuleException), a whole-tenant run
//   leaves the card on a model of its own and opens PRODUCT_MODEL_AMBIGUOUS; no card is merged or given another brand;
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
    // so the second save waits for the first. In the save's transaction; not in a serializable caller's (its model writes
    // run after its commit, and a row lock there would only add serialization failures).
    public static async Task LockPendingModelsAsync(AppDbContext db, Pending pending, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not { } current
            || Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(current).IsolationLevel == System.Data.IsolationLevel.Serializable) return;
        foreach (var tenantId in pending.FieldChanges.Keys.Concat(pending.CodeChanges.Keys).Distinct().Order())
        {
            var productIds = (pending.FieldChanges.GetValueOrDefault(tenantId)?.Keys ?? Enumerable.Empty<Guid>())
                .Concat(pending.CodeChanges.GetValueOrDefault(tenantId)?.Keys ?? Enumerable.Empty<Guid>())
                .Concat(pending.ProductIds.GetValueOrDefault(tenantId) ?? []).Distinct().ToArray();
            await LockModelsOfCardsAsync(db, tenantId, productIds, [.. pending.Keys.GetValueOrDefault(tenantId) ?? []], cancellationToken);
        }
    }

    // The rows of the models the given cards are linked to and of the given keys, locked in one statement in id order (the
    // one order every model write takes them in), in the caller's transaction.
    internal static Task<List<int>> LockModelsOfCardsAsync(AppDbContext db, Guid tenantId, Guid[] productIds, string[] keys, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<int>($"""
            SELECT 1 AS "Value" FROM catalog.product_models
            WHERE "TenantId" = {tenantId}
              AND ("Id" IN (SELECT p."FamilyId" FROM catalog.products p WHERE p."TenantId" = {tenantId} AND p."Id" = ANY({productIds}) AND p."FamilyId" IS NOT NULL)
                   OR "ModelCodeKey" = ANY({keys}))
            ORDER BY "Id" FOR UPDATE
            """).ToListAsync(cancellationToken);

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
            // KATALOG-14 phase D5b: a code changed on a colour is the model's new code (not in a marketplace import, which never
            // moves a model).
            if (!db.MarketplaceCatalogImport && pending.CodeChanges.TryGetValue(tenantId, out var codeChanges))
                keys.UnionWith(await PropagateCodeChangesAsync(db, tenantId, codeChanges, cancellationToken));
            if (keys.Count > 0)
            {
                // The save's cards (a card of another brand among them joining a code fails the save) and the brands the save
                // gave cards already linked (the model's brand as the save leaves it, ModelMirror writing it right after).
                var brandChanges = (pending.FieldChanges.GetValueOrDefault(tenantId) ?? []).Where(x => x.Value.Brand && !x.Value.Added).ToDictionary(x => x.Key, x => x.Value.BrandId);
                await ReconcileAsync(db, tenantId, keys, now, cancellationToken, new SaveScope(new HashSet<Guid>([.. productIds ?? [], .. brandChanges.Keys]), brandChanges));
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
            // KATALOG-14 phase D5a/b: the model's fields (ModelMirror) and values are written in the save's own transaction
            // (the caller's or the one SaveChanges opened); a write that fails fails the save. A serializable caller (the
            // editor's save, the delete) is the exception (review, 17 Nov 2026): a model row written in it would make the
            // commit fail whenever another session wrote the model meanwhile, so its models are written right after its
            // commit (AppDbContext.DeferModelValues).
            var serializable = db.Database.CurrentTransaction is { } current && Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(current).IsolationLevel == System.Data.IsolationLevel.Serializable;
            if (pending.FieldChanges.TryGetValue(tenantId, out var fieldChanges) && fieldChanges.Count > 0)
            {
                if (serializable) db.DeferFieldChanges(tenantId, fieldChanges);
                else await ModelMirror.ApplyAsync(db, tenantId, fieldChanges, now, cancellationToken);
            }
            if (valueModels.Count == 0) continue;
            if (serializable) db.DeferModelValues(tenantId, valueModels);
            else await ProductModelValues.RefreshAsync(db, tenantId, valueModels, now, cancellationToken);
        }
    }

    // KATALOG-14 phase D5b (owner, 17 Nov 2026): a model code changed on one colour (the editor, an Excel row, the API) is
    // the model's: every variant of the model still holding the old code takes the new one in the save's transaction, so
    // the reconcile keeps the model under its new code (KATALOG-2's re-key). A code another model holds is refused (the
    // editor and the Excel file refuse it first; this is the last guard). Returns the keys to reconcile.
    private static async Task<HashSet<string>> PropagateCodeChangesAsync(AppDbContext db, Guid tenantId, IReadOnlyDictionary<Guid, (string? Before, string? After)> changes, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var cardIds = changes.Keys.ToArray();
        var cards = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && cardIds.Contains(x.Id) && x.FamilyId != null).Select(x => new { x.Id, ModelId = x.FamilyId!.Value }).ToListAsync(cancellationToken);
        foreach (var card in cards)
        {
            var (before, after) = changes[card.Id];
            if (ProductFamilyKeyPolicy.ModelCodeKey(before) is not { } oldKey || ProductFamilyKeyPolicy.ModelCodeKey(after) is not { } newKey || oldKey == newKey) continue;
            var model = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == card.ModelId && x.ModelCodeKey == oldKey).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
            if (model is not { } modelId) continue;
            var holder = await (from other in db.ProductModels.AsNoTracking()
                                join product in db.Products.AsNoTracking() on other.Id equals product.FamilyId
                                where other.TenantId == tenantId && other.ModelCodeKey == newKey && other.Id != modelId && product.TenantId == tenantId
                                orderby product.CreatedAt, product.Id
                                select product.Title).FirstOrDefaultAsync(cancellationToken);
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
    public sealed record SaveScope(IReadOnlySet<Guid> Products, IReadOnlyDictionary<Guid, Guid?> BrandChanges);

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
        var products = (await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && cardIds.Contains(x.Id))
                .Select(x => new { x.Id, x.BrandId, x.FamilyId, x.CreatedAt, x.Title, x.Description, x.CategoryId, x.VatRate, x.VatInclusion, x.WarrantyMonths, x.DeliveryDays })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Id);
        var targetKeyArray = targetKeys.ToArray();
        var models = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && targetKeyArray.Contains(x.ModelCodeKey))
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
            var cards = FirstBroughtIn(db, cardKeys.Where(x => x.Key == key && products.ContainsKey(x.ProductId)).Select(x => (Card: products[x.ProductId], x.Color, x.Code)),
                x => x.Card.Id, x => x.Card.CreatedAt, x => x.Color, externalIds);
            // KATALOG-14 phase D5b (owner, 17 Nov 2026): a model is its code alone, one per key; the brand is one of its
            // fields (ModelMirror gives a brand change of one colour to the model and every colour).
            var keyModels = models.Where(x => x.ModelCodeKey == key && !rekeyed.Contains(x.Id)).ToList();
            if (cards.Count == 0)
            {
                if (openIssueKeys.Contains(AmbiguousDedupeKey(key))) await ResolveAmbiguityAsync(db, tenantId, key, now, cancellationToken);
                continue;
            }
            var current = keyModels.OrderByDescending(model => cards.Count(x => x.Card.FamilyId == model.Id)).ThenBy(model => model.Id).FirstOrDefault();
            var target = current?.Id;
            var modelBrand = current?.BrandId;
            // KATALOG-14 phase D5b review: a model left without a colour (its cards deleted or re-coded) is deleted, never
            // re-branded or reused: the cards that come to its code open a new model from their own values (a stale model's
            // old values would otherwise reach them, and no run touches a model's brand).
            if (current is not null && !linkedElsewhere.Any(x => x.FamilyId == current.Id)
                && await db.ProductModels.Where(x => x.TenantId == tenantId && x.Id == current.Id && !db.Products.Any(product => product.TenantId == tenantId && product.FamilyId == x.Id)).ExecuteDeleteAsync(cancellationToken) == 1)
            {
                rekeyed.Add(current.Id);
                current = null;
                target = null;
                modelBrand = null;
            }
            // KATALOG-2: the cards' code was corrected together. A model is kept by its id, not re-opened under the new
            // code: when every card linked to one model of another key now holds this key (none stays behind on the old
            // code, and no card of another key was given it in this run), that model takes the new code, so its
            // marketplace model links and saved attributes stay. Cards of another model or of none may join it.
            if (target is null
                && models.Where(model => model.ModelCodeKey != key && !rekeyed.Contains(model.Id) && cards.Any(x => x.Card.FamilyId == model.Id))
                    .OrderByDescending(model => cards.Count(x => x.Card.FamilyId == model.Id)).ThenBy(model => model.Id).FirstOrDefault() is { } recoding
                && linkedElsewhere.Where(x => x.FamilyId == recoding.Id).All(x => cards.Any(card => card.Card.Id == x.Id))
                && !desired.Any(pair => pair.Value == recoding.Id)
                && await db.ProductModels.Where(x => x.TenantId == tenantId && x.Id == recoding.Id && !db.ProductModels.Any(other => other.TenantId == tenantId && other.ModelCodeKey == key))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ModelCode, Short(cards[0].Code, 160)).SetProperty(x => x.ModelCodeKey, key)
                        .SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken) == 1)
            {
                rekeyed.Add(recoding.Id);
                target = recoding.Id;
                modelBrand = recoding.BrandId;
            }
            if (target is null)
            {
                // KATALOG-14 phase D5b review 5: unknown values do not vote; most colours that know a value, on a tie the oldest.
                var fields = ProductFamilySharedFields.NewModelValues(
                    [.. cards.Select(x => new ProductFamilySharedFields.CardFields(x.Card.Id, x.Color, x.Card.Title, x.Card.Description, x.Card.BrandId, x.Card.CategoryId, x.Card.VatRate, x.Card.VatInclusion, x.Card.WarrantyMonths, x.Card.DeliveryDays))]);
                var titleCard = cards.FirstOrDefault(x => x.Card.Title.Trim() == fields.Title);
                // KATALOG-14 phase D5b: the colour word the name holds, whichever colour card it came from (a copy can hold another colour's).
                var titleColor = (titleCard.Color is { } color && ProductFamilyNewColorCard.HasColorWord(fields.Title, color) ? color : cards.Select(x => x.Color).FirstOrDefault(other => other is not null && ProductFamilyNewColorCard.HasColorWord(fields.Title, other))) is { } word ? Short(word.Trim(), 120) : null;
                // The brand of the first card that has one (an empty brand only when no colour has one), as every field.
                var brandId = fields.BrandId;
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO catalog.product_models ("Id", "TenantId", "BrandId", "ModelCode", "ModelCodeKey", "Title", "TitleColor", "Description", "CategoryId", "VatRate", "VatInclusion", "WarrantyMonths", "DeliveryDays", "FieldsSavedAt", "CreatedAt", "UpdatedAt", "Version")
                    VALUES ({Guid.CreateVersion7()}, {tenantId}, {brandId}, {Short(cards[0].Code, 160)}, {key}, {Short(fields.Title, 320)}, {titleColor}, {fields.Description}, {fields.CategoryId}, {fields.VatRate}, {fields.VatInclusion}, {fields.WarrantyMonths}, {fields.DeliveryDays}, {(DateTimeOffset?)null}, {now}, {now}, {1L})
                    ON CONFLICT DO NOTHING
                    """, cancellationToken);
                var opened = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && x.ModelCodeKey == key).OrderBy(x => x.Id).Select(x => new { x.Id, x.BrandId }).FirstOrDefaultAsync(cancellationToken);
                target = opened?.Id;
                modelBrand = opened?.BrandId;
            }
            // A card of another brand joining the code (one not linked to the model yet) is refused: in a save the save
            // fails with "Bu Model Kodu <kod> Başka Bir Markada Kayıtlı (<marka>)"; in a whole-tenant run (a code two
            // brands share from before) the card stays on a model of its own and PRODUCT_MODEL_AMBIGUOUS names the code.
            // Nothing is merged or given another brand (owner, 14 and 17 Nov 2026). A colour already linked to the model
            // whose brand changed gives the model its brand (ModelMirror), so it is not a joining card: the model's brand is
            // read as the save leaves it. A card of another brand left from before (on a model of its own) that the save
            // did not touch only keeps the code reported.
            if (save is not null && cards.Where(x => x.Card.FamilyId == target && save.BrandChanges.ContainsKey(x.Card.Id)).Select(x => save.BrandChanges[x.Card.Id]).OfType<Guid>().Distinct().ToList() is [var given])
                modelBrand = given;
            var refused = cards.Where(x => x.Card.FamilyId != target && x.Card.BrandId is Guid brand && modelBrand is Guid held && brand != held).ToList();
            if (refused.Count > 0)
            {
                if (save is not null && refused.Any(x => save.Products.Contains(x.Card.Id)))
                {
                    var brandName = await db.Brands.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == modelBrand).Select(x => x.Name).SingleOrDefaultAsync(cancellationToken);
                    throw ModelRuleException.OtherBrand(cards[0].Code, brandName ?? "markasız");
                }
                foreach (var (card, _, _) in refused) desired[card.Id] = card.FamilyId is Guid family && ownModelIds.Contains(family) ? family : null;
                await OpenAmbiguityAsync(db, tenantId, key, cards[0].Code, [.. cards.Select(x => x.Card.BrandId).Distinct()], cards.Count, now, cancellationToken);
            }
            else if (openIssueKeys.Contains(AmbiguousDedupeKey(key))) await ResolveAmbiguityAsync(db, tenantId, key, now, cancellationToken);
            foreach (var (card, _, _) in cards.Except(refused)) desired[card.Id] = target;
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
        await db.ProductModels.Where(x => x.TenantId == tenantId && (targetKeyArray.Contains(x.ModelCodeKey) || left.Contains(x.Id)) && !db.Products.Any(product => product.FamilyId == x.Id))
            .ExecuteDeleteAsync(cancellationToken);
        if (keyArray is null)
            await db.ProductModels.Where(x => x.TenantId == tenantId && !db.Products.Any(product => product.FamilyId == x.Id)).ExecuteDeleteAsync(cancellationToken);
        // Faz 2: a model link whose model no longer has a card holding a variant link on its connection goes.
        await MarketplaceModelLinks.RemoveOrphansAsync(db, tenantId, keyArray is null ? null : modelIds, cancellationToken);
        // KATALOG-14 phase D5b review: the model warnings of models this run (or an earlier delete) removed are settled.
        await ResolveWarningsOfMissingModelsAsync(db, tenantId, now, cancellationToken);

        Guid? CurrentFamily(Guid productId) =>
            products.TryGetValue(productId, out var card) ? card.FamilyId : linkedElsewhere.FirstOrDefault(x => x.Id == productId)?.FamilyId;
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
    // warning when the tenant has no model left) and the PRODUCT_MODEL_AMBIGUOUS warnings of a code no model holds any
    // more are resolved: a deleted catalog leaves no open model warning behind.
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
                OR (i."Code" = {AmbiguousCode} AND i."DedupeKey" LIKE {AmbiguousKeyPrefix + "%"}
                    AND NOT EXISTS (SELECT 1 FROM catalog.product_models m WHERE m."TenantId" = {tenantId} AND m."ModelCodeKey" = substring(i."DedupeKey" from {AmbiguousKeyPrefix.Length + 1})))
                OR (i."Code" = {AmbiguousCode} AND i."DedupeKey" LIKE 'product-model-other-brand:%'
                    AND NOT EXISTS (SELECT 1 FROM catalog.product_models m WHERE m."TenantId" = {tenantId} AND m."ModelCodeKey" = split_part(i."DedupeKey", ':', 3))))
            """, cancellationToken);
    }

    // KATALOG-14 phase D5a: a card the model-code rule gives no model (it has no model code) is a model of its own, keyed
    // "#<card id>" (a model-code key never holds '#', CatalogKeyNormalizer), with the card's brand, code-less, and its
    // values. Nothing is merged: one card, one model. When the card gets a code its model takes that code (the reconcile's
    // re-key, KATALOG-2). productIds null: every card of the tenant without a model.
    public const string OwnModelKeyPrefix = "#";


    public static bool IsOwnModelKey(string key) => key.StartsWith(OwnModelKeyPrefix, StringComparison.Ordinal);

    public static async Task<int> EnsureOwnModelsAsync(AppDbContext db, Guid tenantId, Guid[]? productIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (productIds is { Length: 0 }) return 0;
        var cards = await db.Products.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.FamilyId == null && (productIds == null || productIds.Contains(x.Id)))
            .Select(x => new { x.Id, x.BrandId, x.Title, x.Description, x.CategoryId, x.VatRate, x.VatInclusion, x.WarrantyMonths, x.DeliveryDays })
            .ToListAsync(cancellationToken);
        foreach (var card in cards)
        {
            var key = $"{OwnModelKeyPrefix}{card.Id:N}";
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO catalog.product_models ("Id", "TenantId", "BrandId", "ModelCode", "ModelCodeKey", "Title", "TitleColor", "Description", "CategoryId", "VatRate", "VatInclusion", "WarrantyMonths", "DeliveryDays", "FieldsSavedAt", "CreatedAt", "UpdatedAt", "Version")
                VALUES ({Guid.CreateVersion7()}, {tenantId}, {card.BrandId}, {""}, {key}, {Short(card.Title, 320)}, {(string?)null}, {card.Description}, {card.CategoryId}, {card.VatRate}, {card.VatInclusion}, {card.WarrantyMonths}, {card.DeliveryDays}, {(DateTimeOffset?)null}, {now}, {now}, {1L})
                ON CONFLICT DO NOTHING
                """, cancellationToken);
            var modelId = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && x.ModelCodeKey == key).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
            if (modelId is Guid id)
                await db.Products.Where(x => x.TenantId == tenantId && x.Id == card.Id && x.FamilyId == null)
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
        if (model is null || !HoldsSavedFields(model, cards.Select(x => (x.UpdatedAt, x.CategoryId)))) return null;
        return new ProductFamilySharedFields.ModelFields(
            ProductFamilySharedFields.TitleFor(model.Title, model.TitleColor, openedColor), model.Description, model.BrandId, model.CategoryId,
            model.VatRate, model.VatInclusion, model.WarrantyMonths, model.DeliveryDays);
    }

    // The record holds the owner's saved fields (the editor shows them): the editor saved them (FieldsSavedAt) after the
    // last change of every card and a card still holds the record's category. ProductModelValues leaves such fields as
    // they are, so the editor and the nightly scan never take turns overwriting each other.
    internal static bool HoldsSavedFields(ProductModel model, IEnumerable<(DateTimeOffset UpdatedAt, Guid? CategoryId)> cards)
    {
        var list = cards.ToList();
        return model.FieldsSavedAt is { } savedAt && list.All(x => x.UpdatedAt <= savedAt) && list.Any(x => x.CategoryId == model.CategoryId);
    }

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
    public static async Task WriteFieldsAsync(AppDbContext db, Guid tenantId, Guid productId, string? titleColor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var card = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == productId && x.FamilyId != null)
            .Select(x => new { x.FamilyId, x.Title, x.Description, x.CategoryId, x.VatRate, x.VatInclusion, x.WarrantyMonths, x.DeliveryDays })
            .SingleOrDefaultAsync(cancellationToken);
        if (card is null) return;
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

    private static async Task OpenAmbiguityAsync(AppDbContext db, Guid tenantId, string key, string code, IReadOnlyCollection<Guid?> brandIds, int cardCount, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ids = brandIds.OfType<Guid>().ToArray();
        var names = await db.Brands.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.Id)).Select(x => x.Name).ToListAsync(cancellationToken);
        if (brandIds.Any(x => x is null)) names.Add("markasız");
        var held = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && x.ModelCodeKey == key).Select(x => x.BrandId).FirstOrDefaultAsync(cancellationToken);
        var heldName = held is null ? null : await db.Brands.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == held).Select(x => x.Name).FirstOrDefaultAsync(cancellationToken);
        var summary = Short($"{ModelCodeUniquenessPolicy.OtherBrandMessage(code, heldName ?? "markasız")}. Model kodu {code} {brandIds.Count} ayrı markada kullanılıyor ({string.Join(", ", names.Order(StringComparer.Ordinal))}, {cardCount} kart): kartlar bir modele bağlanmadı, hiçbir şey birleştirilmedi; hangi modele ait olduklarına siz karar verin.", 1000);
        var dedupeKey = AmbiguousDedupeKey(key);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ops.operational_issues AS i ("Id", "TenantId", "DedupeKey", "Code", "Summary", "Status", "FirstSeenAt", "LastSeenAt", "OccurrenceCount", "ResolvedAt")
            VALUES ({Guid.CreateVersion7()}, {tenantId}, {dedupeKey}, {AmbiguousCode}, {summary}, 'Open', {now}, {now}, 1, NULL)
            ON CONFLICT ("TenantId", "DedupeKey") DO UPDATE SET
                "Summary" = EXCLUDED."Summary",
                "LastSeenAt" = EXCLUDED."LastSeenAt",
                "OccurrenceCount" = i."OccurrenceCount" + CASE WHEN i."Status" = 'Resolved' THEN 1 ELSE 0 END,
                "Status" = CASE WHEN i."Status" = 'Resolved' THEN 'Open' ELSE i."Status" END,
                "ResolvedAt" = NULL
            """, cancellationToken);
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
