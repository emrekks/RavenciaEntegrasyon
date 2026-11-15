using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// MODEL-KAYDI-PLANI Faz 1: keeps catalog.product_models and products.FamilyId in step with the model-code rule
// (ProductFamilyKeyPolicy: one brand + one model-code key). A card's model code is its first variant's (sort order, id)
// non-blank ModelCode. For every model-code key it touches:
// - every card of the key under one brand: the brand's model is found, moved (the cards changed brand) or opened, and
//   the cards are linked to it; an open PRODUCT_MODEL_AMBIGUOUS issue of the key is resolved;
// - cards of the key under several brands (KENE01): nothing new is linked or opened, links already made to a model of
//   the card's own brand stay, every other link is cleared and the PRODUCT_MODEL_AMBIGUOUS issue is opened; no card
//   is merged, deleted or given another brand (owner, 14 Nov 2026);
// - a card linked to a model whose key or brand it no longer has is unlinked; a model left without cards is deleted.
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
        public bool IsEmpty => Keys.Count == 0 && ProductIds.Count == 0 && ModelIds.Count == 0 && MediaProductIds.Count == 0;
        public IEnumerable<Guid> Tenants => Keys.Keys.Concat(ProductIds.Keys).Concat(ModelIds.Keys).Concat(MediaProductIds.Keys).Distinct();

        internal void AddMedia(Guid tenantId, Guid productId) => Set(MediaProductIds, tenantId).Add(productId);

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
                    break;
                case EntityState.Deleted:
                    pending.AddKey(variant.TenantId, entry.Property(x => x.ModelCode).OriginalValue);
                    pending.AddProduct(variant.TenantId, entry.Property(x => x.ProductId).OriginalValue);
                    break;
                case EntityState.Modified:
                    var code = entry.Property(x => x.ModelCode);
                    var product = entry.Property(x => x.ProductId);
                    var sort = entry.Property(x => x.SortOrder);
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
            }
        }
        foreach (var entry in tracker.Entries<ProductMedia>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified && (Changed(entry.Property(x => x.ProductId)) || Changed(entry.Property(x => x.FamilyId))))
                pending.AddMedia(entry.Entity.TenantId, entry.Entity.ProductId);
        }
        return pending.IsEmpty ? null : pending;
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
            if (keys.Count > 0) await ReconcileAsync(db, tenantId, keys, now, cancellationToken);
            if (pending.MediaProductIds.TryGetValue(tenantId, out var mediaProductIds) && mediaProductIds.Count > 0)
                await SyncMediaFamiliesAsync(db, tenantId, [.. mediaProductIds], cancellationToken);
        }
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
    public static async Task ReconcileAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<string>? keys, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var keyArray = keys?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (keyArray is { Length: 0 }) return;
        if (db.Database.CurrentTransaction is not null)
        {
            await ReconcileCoreAsync(db, tenantId, keyArray, now, cancellationToken);
            return;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
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
        await ReconcileCoreAsync(db, tenantId, keyArray, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ReconcileCoreAsync(AppDbContext db, Guid tenantId, string[]? keyArray, DateTimeOffset now, CancellationToken cancellationToken)
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
        // Models given another key in this run (KATALOG-2): no longer a model of the key they were read under.
        var rekeyed = new HashSet<Guid>();
        foreach (var key in targetKeys.Order(StringComparer.Ordinal))
        {
            var cards = cardKeys.Where(x => x.Key == key && products.ContainsKey(x.ProductId))
                .Select(x => (Card: products[x.ProductId], x.Color, x.Code))
                .OrderBy(x => x.Card.CreatedAt).ThenBy(x => x.Card.Id).ToList();
            var keyModels = models.Where(x => x.ModelCodeKey == key && !rekeyed.Contains(x.Id)).ToList();
            var brands = cards.Select(x => x.Card.BrandId).Distinct().ToList();
            if (brands.Count == 1)
            {
                var brandId = brands[0];
                var target = keyModels.FirstOrDefault(x => x.BrandId == brandId)?.Id;
                // The cards changed brand together: their model goes with them.
                if (target is null && keyModels.Where(model => cards.Any(x => x.Card.FamilyId == model.Id)).OrderByDescending(model => cards.Count(x => x.Card.FamilyId == model.Id)).FirstOrDefault() is { } moving
                    && await db.ProductModels.Where(x => x.TenantId == tenantId && x.Id == moving.Id && !db.ProductModels.Any(other => other.TenantId == tenantId && other.BrandId == brandId && other.ModelCodeKey == key))
                        .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.BrandId, brandId).SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken) == 1)
                    target = moving.Id;
                // KATALOG-2: the cards' code was corrected together. A model is kept by its id, not re-opened under the new
                // code: when every card linked to one model of another key now holds this key (none stays behind on the old
                // code, and no card of another key was given it in this run), that model takes the new code, so its
                // marketplace model links and saved attributes stay. Cards of another model or of none may join it.
                if (target is null
                    && models.Where(model => model.ModelCodeKey != key && !rekeyed.Contains(model.Id) && cards.Any(x => x.Card.FamilyId == model.Id))
                        .OrderByDescending(model => cards.Count(x => x.Card.FamilyId == model.Id)).ThenBy(model => model.Id).FirstOrDefault() is { } recoding
                    && linkedElsewhere.Where(x => x.FamilyId == recoding.Id).All(x => cards.Any(card => card.Card.Id == x.Id))
                    && !desired.Any(pair => pair.Value == recoding.Id)
                    && await db.ProductModels.Where(x => x.TenantId == tenantId && x.Id == recoding.Id && !db.ProductModels.Any(other => other.TenantId == tenantId && other.BrandId == brandId && other.ModelCodeKey == key))
                        .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.BrandId, brandId).SetProperty(x => x.ModelCode, Short(cards[0].Code, 160)).SetProperty(x => x.ModelCodeKey, key)
                            .SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken) == 1)
                {
                    rekeyed.Add(recoding.Id);
                    target = recoding.Id;
                }
                if (target is null)
                {
                    var fields = ProductFamilySharedFields.ModelValues(
                        [.. cards.Select(x => new ProductFamilySharedFields.CardFields(x.Card.Id, x.Color, x.Card.Title, x.Card.Description, x.Card.BrandId, x.Card.CategoryId, x.Card.VatRate, x.Card.VatInclusion, x.Card.WarrantyMonths, x.Card.DeliveryDays))],
                        null);
                    var titleCard = cards.FirstOrDefault(x => x.Card.Title.Trim() == fields.Title);
                    var titleColor = titleCard.Color is { } color && ProductFamilyNewColorCard.HasColorWord(fields.Title, color) ? Short(color.Trim(), 120) : null;
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO catalog.product_models ("Id", "TenantId", "BrandId", "ModelCode", "ModelCodeKey", "Title", "TitleColor", "Description", "CategoryId", "VatRate", "VatInclusion", "WarrantyMonths", "DeliveryDays", "FieldsSavedAt", "CreatedAt", "UpdatedAt", "Version")
                        VALUES ({Guid.CreateVersion7()}, {tenantId}, {brandId}, {Short(cards[0].Code, 160)}, {key}, {Short(fields.Title, 320)}, {titleColor}, {fields.Description}, {fields.CategoryId}, {fields.VatRate}, {fields.VatInclusion}, {fields.WarrantyMonths}, {fields.DeliveryDays}, {(DateTimeOffset?)null}, {now}, {now}, {1L})
                        ON CONFLICT DO NOTHING
                        """, cancellationToken);
                    target = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && x.BrandId == brandId && x.ModelCodeKey == key).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
                }
                foreach (var (card, _, _) in cards) desired[card.Id] = target;
                if (openIssueKeys.Contains(AmbiguousDedupeKey(key))) await ResolveAmbiguityAsync(db, tenantId, key, now, cancellationToken);
            }
            else
            {
                foreach (var (card, _, _) in cards)
                    desired[card.Id] = keyModels.Any(model => model.Id == card.FamilyId && model.BrandId == card.BrandId) ? card.FamilyId : null;
                if (brands.Count > 1) await OpenAmbiguityAsync(db, tenantId, key, cards[0].Code, brands, cards.Count, now, cancellationToken);
                else if (openIssueKeys.Contains(AmbiguousDedupeKey(key))) await ResolveAmbiguityAsync(db, tenantId, key, now, cancellationToken);
            }
        }
        // A card linked to one of these models that no longer carries its key (its code changed): unlinked, unless its own
        // key is reconciled above.
        foreach (var stray in linkedElsewhere.Where(x => !desired.ContainsKey(x.Id))) desired[stray.Id] = null;

        foreach (var group in desired.Where(pair => pair.Value != CurrentFamily(pair.Key)).GroupBy(pair => pair.Value))
        {
            var ids = group.Select(pair => pair.Key).ToArray();
            var familyId = group.Key;
            await db.Products.Where(x => x.TenantId == tenantId && ids.Contains(x.Id) && x.FamilyId != familyId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.FamilyId, familyId), cancellationToken);
        }
        // Faz 2: the pictures follow their card's model.
        await SyncMediaFamiliesAsync(db, tenantId, keyArray is null ? null : [.. desired.Keys], cancellationToken);
        // Models left without a card go.
        await db.ProductModels.Where(x => x.TenantId == tenantId && targetKeyArray.Contains(x.ModelCodeKey) && !db.Products.Any(product => product.FamilyId == x.Id))
            .ExecuteDeleteAsync(cancellationToken);
        if (keyArray is null)
            await db.ProductModels.Where(x => x.TenantId == tenantId && !db.Products.Any(product => product.FamilyId == x.Id)).ExecuteDeleteAsync(cancellationToken);
        // Faz 2: a model link whose model no longer has a card holding a variant link on its connection goes.
        await MarketplaceModelLinks.RemoveOrphansAsync(db, tenantId, keyArray is null ? null : modelIds, cancellationToken);

        Guid? CurrentFamily(Guid productId) =>
            products.TryGetValue(productId, out var card) ? card.FamilyId : linkedElsewhere.FirstOrDefault(x => x.Id == productId)?.FamilyId;
    }

    // Faz 2: a picture belongs to its card's model (product_media.FamilyId = products.FamilyId); productIds null: the tenant.
    // Only rows that differ are written.
    public static Task<int> SyncMediaFamiliesAsync(AppDbContext db, Guid tenantId, Guid[]? productIds, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync("""
            UPDATE catalog.product_media m SET "FamilyId" = p."FamilyId"
            FROM catalog.products p
            WHERE m."TenantId" = @tenant AND p."TenantId" = m."TenantId" AND p."Id" = m."ProductId"
              AND (@all OR m."ProductId" = ANY(@products)) AND m."FamilyId" IS DISTINCT FROM p."FamilyId"
            """, [new NpgsqlParameter("tenant", tenantId), new NpgsqlParameter("all", productIds is null), new NpgsqlParameter("products", productIds ?? [])], cancellationToken);

    // Faz 2: the fields of every model the editor has not saved yet (FieldsSavedAt null) are the editor's ModelValues of
    // its linked cards (oldest first), the same rule a model opened by the application gets. The backfill's SQL could only
    // approximate it (whole-title majority, a colour found as a substring). Runs after the migrations (DatabaseMigrator);
    // a model whose fields already agree is not written. Returns the number of models written.
    public static async Task<int> RecomputeUnsavedFieldsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var models = await db.ProductModels.AsNoTracking().Where(x => x.FieldsSavedAt == null).ToListAsync(cancellationToken);
        if (models.Count == 0) return 0;
        var modelIds = models.Select(x => x.Id).ToArray();
        var cards = await db.Products.AsNoTracking().Where(x => x.FamilyId != null && modelIds.Contains(x.FamilyId.Value))
            .Select(x => new { x.Id, x.FamilyId, x.CreatedAt, x.Title, x.Description, x.BrandId, x.CategoryId, x.VatRate, x.VatInclusion, x.WarrantyMonths, x.DeliveryDays })
            .ToListAsync(cancellationToken);
        var cardIds = cards.Select(x => x.Id).ToArray();
        var signatures = (await db.ProductVariants.AsNoTracking().Where(x => cardIds.Contains(x.ProductId))
                .Select(x => new { x.ProductId, x.SortOrder, x.Id, x.OptionSignature }).ToListAsync(cancellationToken))
            .GroupBy(x => x.ProductId)
            .ToDictionary(group => group.Key, group => group.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => ProductFamilyColorOrder.ColorOf(x.OptionSignature)).FirstOrDefault(color => !string.IsNullOrWhiteSpace(color)));
        var byModel = cards.ToLookup(x => x.FamilyId);
        var written = 0;
        foreach (var model in models)
        {
            var linked = byModel[model.Id].OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToList();
            if (linked.Count == 0) continue;
            var fields = ProductFamilySharedFields.ModelValues(
                [.. linked.Select(x => new ProductFamilySharedFields.CardFields(x.Id, signatures.GetValueOrDefault(x.Id), x.Title, x.Description, x.BrandId, x.CategoryId, x.VatRate, x.VatInclusion, x.WarrantyMonths, x.DeliveryDays))],
                null);
            var titleCard = linked.FirstOrDefault(x => x.Title.Trim() == fields.Title);
            var titleColor = titleCard is not null && signatures.GetValueOrDefault(titleCard.Id) is { } color && ProductFamilyNewColorCard.HasColorWord(fields.Title, color) ? Short(color.Trim(), 120) : null;
            var title = Short(fields.Title, 320);
            if ((model.Title, model.TitleColor, model.Description, model.CategoryId, model.VatRate, model.VatInclusion, model.WarrantyMonths, model.DeliveryDays)
                == (title, titleColor, fields.Description, fields.CategoryId, fields.VatRate, fields.VatInclusion, fields.WarrantyMonths, fields.DeliveryDays)) continue;
            // One model at a time: a model a save holds locked is left for the next deploy, the rest go on.
            try
            {
                written += await db.ProductModels.Where(x => x.Id == model.Id && x.Version == model.Version && x.FieldsSavedAt == null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Title, title).SetProperty(x => x.TitleColor, titleColor).SetProperty(x => x.Description, fields.Description)
                        .SetProperty(x => x.CategoryId, fields.CategoryId).SetProperty(x => x.VatRate, fields.VatRate).SetProperty(x => x.VatInclusion, fields.VatInclusion)
                        .SetProperty(x => x.WarrantyMonths, fields.WarrantyMonths).SetProperty(x => x.DeliveryDays, fields.DeliveryDays)
                        .SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
            }
            catch (System.Data.Common.DbException) when (db.Database.CurrentTransaction is null) { }
        }
        return written;
    }

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

    // Replaces the model's attribute rows with the given set when it differs; returns whether it wrote.
    private static async Task<bool> ReplaceModelAttributesAsync(AppDbContext db, Guid tenantId, Guid modelId, IReadOnlyList<AttributeRow> rows, DateTimeOffset? savedAt, CancellationToken cancellationToken)
    {
        rows = [.. rows.DistinctBy(x => (x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue))];
        var current = await db.ProductModelAttributes.AsNoTracking().Where(x => x.TenantId == tenantId && x.ModelId == modelId).ToListAsync(cancellationToken);
        var same = current.Select(x => AttributeKey(x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue)).Order(StringComparer.Ordinal)
            .SequenceEqual(rows.Select(x => AttributeKey(x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue)).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        if (!same)
        {
            // Delete and insert in one transaction: a failed insert leaves the model's set as it was.
            await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
            await db.ProductModelAttributes.Where(x => x.TenantId == tenantId && x.ModelId == modelId).ExecuteDeleteAsync(cancellationToken);
            foreach (var row in rows)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO catalog.product_model_attributes ("Id", "TenantId", "ModelId", "AttributeId", "ValueId", "TextValue", "NumberValue", "BooleanValue", "SortOrder")
                    VALUES ({Guid.CreateVersion7()}, {tenantId}, {modelId}, {row.AttributeId}, {row.ValueId}, {row.TextValue}, {row.NumberValue}, {row.BooleanValue}, {row.SortOrder})
                    ON CONFLICT DO NOTHING
                    """, cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        if (savedAt is { } at)
            await db.ProductModels.Where(x => x.TenantId == tenantId && x.Id == modelId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.AttributesSavedAt, at).SetProperty(x => x.UpdatedAt, at).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
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
        if (model?.FieldsSavedAt is not { } savedAt || cards.Any(x => x.UpdatedAt > savedAt) || cards.All(x => x.CategoryId != model.CategoryId)) return null;
        return new ProductFamilySharedFields.ModelFields(
            ProductFamilySharedFields.TitleFor(model.Title, model.TitleColor, openedColor), model.Description, model.BrandId, model.CategoryId,
            model.VatRate, model.VatInclusion, model.WarrantyMonths, model.DeliveryDays);
    }

    // Faz 2 (editor read): the model record's category attributes (without colour), when the editor saved them after the
    // last change of every card and the opened card still holds exactly that set; null otherwise (the cards' majority).
    public static async Task<IReadOnlyList<ProductModelAttribute>?> SavedAttributesAsync(AppDbContext db, Guid tenantId, Guid? familyId, IReadOnlyCollection<Product> cards, IReadOnlyCollection<(Guid AttributeId, Guid? ValueId, string? TextValue, decimal? NumberValue, bool? BooleanValue)> openedCardAttributes, CancellationToken cancellationToken)
    {
        if (familyId is not { } modelId || cards.Count == 0 || cards.Any(x => x.FamilyId != modelId)) return null;
        var savedAt = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == modelId).Select(x => x.AttributesSavedAt).SingleOrDefaultAsync(cancellationToken);
        if (savedAt is not { } at || cards.Any(x => x.UpdatedAt > at)) return null;
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
        await db.ProductModels.Where(x => x.TenantId == tenantId && x.Id == card.FamilyId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Title, title).SetProperty(x => x.TitleColor, color).SetProperty(x => x.Description, card.Description)
                .SetProperty(x => x.CategoryId, card.CategoryId).SetProperty(x => x.VatRate, card.VatRate).SetProperty(x => x.VatInclusion, card.VatInclusion)
                .SetProperty(x => x.WarrantyMonths, card.WarrantyMonths).SetProperty(x => x.DeliveryDays, card.DeliveryDays)
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

    // Whether the cards are one model: one linked model, or one brand and one model-code key.
    public static async Task<bool> OneModelAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        var ids = productIds.Distinct().ToArray();
        if (ids.Length <= 1) return true;
        var cards = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.Id)).Select(x => new { x.Id, x.BrandId, x.FamilyId }).ToListAsync(cancellationToken);
        if (cards.Count != ids.Length) return false;
        if (cards.All(x => x.FamilyId is not null) && cards.Select(x => x.FamilyId).Distinct().Count() == 1) return true;
        if (cards.Select(x => x.BrandId).Distinct().Count() != 1) return false;
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
        var summary = Short($"Model kodu {code} {brandIds.Count} ayrı markada kullanılıyor ({string.Join(", ", names.Order(StringComparer.Ordinal))}, {cardCount} kart): kartlar bir modele bağlanmadı, hiçbir şey birleştirilmedi; hangi modele ait olduklarına siz karar verin.", 1000);
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
        catch (Exception recordFailure) when (recordFailure is System.Data.Common.DbException or InvalidOperationException) { }
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
