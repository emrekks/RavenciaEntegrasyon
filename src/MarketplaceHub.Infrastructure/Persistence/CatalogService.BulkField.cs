using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Ürünler › Toplu İşlemler › Toplu Değer Ata (owner, 12.11.2026): one field (BulkProductFields) set to one value on the
// selected models. A model is one product whichever colour row was selected (ProductFamilySharedFields): a model-row
// field goes to every colour card of the model, desi to every variant, güvenlik stoğu to every marketplace offer of
// every variant (where the editor keeps it). Only that field is written and nothing else of the card is checked, so a
// card missing its prices still takes a desi. Each model is saved on its own: a model changed meanwhile by another save
// (the version check) is named and left, the rest go through. Nothing is sent to a marketplace; the changed cards' links
// are marked as holding local changes, as the editor's save does.
public sealed partial class CatalogService
{
    public const string BulkFieldAuditAction = "PRODUCT_BULK_FIELD_SET";
    private const string BulkFieldChangedMeanwhile = "İşlem sırasında başka bir kayıtla değişti; yazılmadı. Sayfayı yenileyip tekrar deneyin.";
    private const string BulkFieldDraftLeft = "Taslak ürün kartı; satış durumu yalnız ürün düzenleme ekranından değişir.";

    public async Task<ServiceResult<BulkProductFieldResult>> BulkSetFieldAsync(Guid tenantId, BulkProductFieldCommand command, CancellationToken cancellationToken, Guid? actorUserId = null, string? correlationId = null)
    {
        var ids = (command.ProductIds ?? []).Distinct().ToArray();
        if (ids.Length is 0 or > 500) return ServiceResult<BulkProductFieldResult>.Fail("BULK_PRODUCT_LIMIT", "Toplu işlemde 1-500 ürün seçilebilir.", 422);
        var parsed = BulkProductFields.Parse(command.Field, command.Value);
        if (parsed.Value is not { } value)
            return ServiceResult<BulkProductFieldResult>.Fail("VALIDATION_FAILED", parsed.Error!, 400, new Dictionary<string, string[]> { [parsed.ErrorField!] = [parsed.Error!] });

        var display = value.Display;
        if (value.Field == BulkProductFields.Brand)
        {
            var brandName = await db.Brands.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == value.Id && x.IsActive).Select(x => x.Name).FirstOrDefaultAsync(cancellationToken);
            if (brandName is null) return ServiceResult<BulkProductFieldResult>.Fail("BRAND_ACTIVE_REQUIRED", "Seçilen marka etkin değildir.", 422, new Dictionary<string, string[]> { ["value"] = ["Etkin marka seçin."] });
            display = brandName;
        }
        if (value.Field == BulkProductFields.Category)
        {
            var categoryPath = await db.Categories.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == value.Id && x.IsActive && x.IsLeaf).Select(x => x.Path).FirstOrDefaultAsync(cancellationToken);
            if (categoryPath is null) return ServiceResult<BulkProductFieldResult>.Fail("CATEGORY_LEAF_REQUIRED", "Yalnız etkin yaprak kategori seçilebilir.", 422, new Dictionary<string, string[]> { ["value"] = ["Kategori etkin bir yaprak olmalıdır."] });
            display = categoryPath;
        }

        var selected = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.Id))
            .Select(x => new BulkFieldCard(x.Id, x.BrandId, x.Title)).ToListAsync(cancellationToken);
        if (selected.Count != ids.Length) return ServiceResult<BulkProductFieldResult>.Fail("BULK_PRODUCT_NOT_FOUND", "Seçilen ürünlerden biri bulunamadı veya bu çalışma alanına ait değil.", 404);
        // The brand is the model code's (ApplyFamilySharedFieldsAsync): a card of the same model code under another brand
        // takes it too.
        var families = await BulkFieldFamiliesAsync(tenantId, selected, acrossBrands: value.Field == BulkProductFields.Brand, cancellationToken);

        var correlation = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId;
        var reason = $"Toplu Değer Ata: {BulkProductFields.Label(value.Field)} = {display}";
        int updated = 0, unchanged = 0, variantCount = 0;
        var failures = new List<BulkProductFieldFailure>();
        foreach (var family in families)
        {
            try
            {
                var outcome = await ApplyBulkFieldAsync(tenantId, family.ProductIds, value, reason, actorUserId, correlation, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                updated += outcome.Updated;
                unchanged += outcome.Unchanged;
                variantCount += outcome.Variants;
                if (outcome.Note is { } note) failures.Add(new(family.Model, note));
            }
            catch (DbUpdateConcurrencyException)
            {
                failures.Add(new(family.Model, BulkFieldChangedMeanwhile));
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
        return ServiceResult<BulkProductFieldResult>.Ok(new(updated, unchanged, variantCount, failures));
    }

    private sealed record BulkFieldCard(Guid Id, Guid? BrandId, string Title);
    private sealed record BulkFieldFamily(string Model, IReadOnlyList<Guid> ProductIds);
    private sealed record BulkFieldOutcome(int Updated, int Unchanged, int Variants, string? Note);

    // The selected cards grouped by model (same brand and model code; any brand for the brand itself), each with every
    // visible colour card of the model. A card without a model code is a model of its own, named by its title.
    private async Task<List<BulkFieldFamily>> BulkFieldFamiliesAsync(Guid tenantId, IReadOnlyList<BulkFieldCard> selected, bool acrossBrands, CancellationToken cancellationToken)
    {
        var selectedIds = selected.Select(x => x.Id).ToArray();
        var codes = (await db.ProductVariants.AsNoTracking()
                .Where(x => x.TenantId == tenantId && selectedIds.Contains(x.ProductId) && x.ModelCode != null && x.ModelCode != "")
                .Select(x => new { x.ProductId, x.ModelCode, x.SortOrder, x.Id }).ToListAsync(cancellationToken))
            .GroupBy(x => x.ProductId)
            .ToDictionary(group => group.Key, group => group.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).First().ModelCode!.Trim());
        var candidatesByKey = new Dictionary<string, List<(Guid Id, Guid? BrandId, ProductStatus Status)>>(StringComparer.Ordinal);
        if (codes.Count > 0)
        {
            var rows = await (from variant in db.ProductVariants.AsNoTracking()
                              join product in VisibleProducts(tenantId) on variant.ProductId equals product.Id
                              where variant.TenantId == tenantId && variant.ModelCode != null && variant.ModelCode != ""
                              select new { product.Id, product.BrandId, product.Status, variant.ModelCode }).Distinct().ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                if (ProductFamilyKeyPolicy.ModelCodeKey(row.ModelCode) is not { } key) continue;
                if (!candidatesByKey.TryGetValue(key, out var list)) candidatesByKey[key] = list = [];
                list.Add((row.Id, row.BrandId, row.Status));
            }
        }

        var families = new List<BulkFieldFamily>();
        var placed = new HashSet<Guid>();
        foreach (var card in selected)
        {
            if (placed.Contains(card.Id)) continue;
            var code = codes.GetValueOrDefault(card.Id);
            if (ProductFamilyKeyPolicy.ModelCodeKey(code) is not { } key)
            {
                placed.Add(card.Id);
                families.Add(new(card.Title.Trim(), [card.Id]));
                continue;
            }
            // The selected card always belongs to its model, even when another marketplace's link hides it from the list.
            var members = new List<Guid> { card.Id };
            members.AddRange((candidatesByKey.GetValueOrDefault(key) ?? [])
                .Where(candidate => candidate.BrandId == card.BrandId || acrossBrands && candidate.Status != ProductStatus.Archived)
                .Select(candidate => candidate.Id));
            members.AddRange(selected.Where(other => ProductFamilyKeyPolicy.ModelCodeKey(codes.GetValueOrDefault(other.Id)) == key && (other.BrandId == card.BrandId || acrossBrands)).Select(other => other.Id));
            var ids = members.Where(id => !placed.Contains(id)).Distinct().ToList();
            placed.UnionWith(ids);
            families.Add(new(code!, ids));
        }
        return families;
    }

    private async Task<BulkFieldOutcome> ApplyBulkFieldAsync(Guid tenantId, IReadOnlyList<Guid> productIds, BulkProductFields.Value value, string reason, Guid? actorUserId, string correlation, CancellationToken cancellationToken)
    {
        var ids = productIds.ToArray();
        var products = await db.Products.Where(x => x.TenantId == tenantId && ids.Contains(x.Id)).ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var changed = new HashSet<Guid>();
        var left = 0;
        string? note = null;
        void Set<T>(Product product, T current, T next, Action<T> write)
        {
            if (EqualityComparer<T>.Default.Equals(current, next)) return;
            write(next);
            changed.Add(product.Id);
        }

        var variants = value.Field is BulkProductFields.Desi or BulkProductFields.SafetyStock
            ? await db.ProductVariants.Where(x => x.TenantId == tenantId && ids.Contains(x.ProductId)).ToListAsync(cancellationToken)
            : null;
        var variantCount = variants?.Count ?? await db.ProductVariants.AsNoTracking().CountAsync(x => x.TenantId == tenantId && ids.Contains(x.ProductId), cancellationToken);

        switch (value.Field)
        {
            case BulkProductFields.VatRate:
                foreach (var product in products) Set(product, product.VatRate, value.Number, next => product.VatRate = next);
                break;
            case BulkProductFields.VatInclusion:
                foreach (var product in products) Set(product, product.VatInclusion, value.Text, next => product.VatInclusion = next);
                break;
            case BulkProductFields.WarrantyMonths:
                // The editor keeps "0 = garanti yok" as an empty warranty.
                foreach (var product in products) Set(product, product.WarrantyMonths, value.Integer > 0 ? value.Integer : null, next => product.WarrantyMonths = next);
                break;
            case BulkProductFields.DeliveryDays:
                foreach (var product in products) Set(product, product.DeliveryDays, value.Integer, next => product.DeliveryDays = next);
                break;
            case BulkProductFields.Description:
                foreach (var product in products) Set(product, product.Description, value.Text!, next => product.Description = next);
                break;
            case BulkProductFields.Brand:
                foreach (var product in products) Set(product, product.BrandId, value.Id, next => product.BrandId = next);
                break;
            case BulkProductFields.Category:
                foreach (var product in products) Set(product, product.CategoryId, value.Id, next => product.CategoryId = next);
                break;
            case BulkProductFields.Desi:
                foreach (var variant in variants!.Where(variant => variant.Desi != value.Number))
                {
                    variant.Desi = value.Number;
                    variant.UpdatedAt = now;
                    variant.Version++;
                    changed.Add(variant.ProductId);
                }
                break;
            case BulkProductFields.SafetyStock:
                {
                    var variantIds = variants!.Select(variant => variant.Id).ToArray();
                    var offers = await db.ChannelOffers.Where(x => x.TenantId == tenantId && variantIds.Contains(x.VariantId)).ToListAsync(cancellationToken);
                    var productOfVariant = variants!.ToDictionary(variant => variant.Id, variant => variant.ProductId);
                    foreach (var offer in offers.Where(offer => offer.SafetyStock != value.Integer!.Value))
                    {
                        offer.SafetyStock = value.Integer!.Value;
                        offer.Version++;
                        changed.Add(productOfVariant[offer.VariantId]);
                    }
                    var withoutOffer = variants!.Count(variant => offers.All(offer => offer.VariantId != variant.Id));
                    if (withoutOffer > 0) note = $"{withoutOffer.ToString(System.Globalization.CultureInfo.InvariantCulture)} varyantın pazar yeri kaydı yok; güvenlik stoğu pazar yeri kaydında tutulduğu için bu varyantlara yazılmadı.";
                    break;
                }
            case BulkProductFields.Status:
                {
                    var status = value.Text == "ACTIVE" ? ProductStatus.Active : ProductStatus.Archived;
                    // Toplu Satışa Aç / Kapat's rule: a draft goes on sale only from its editor, with every rule met.
                    left = products.Count(product => ProductDraftPolicy.IsDraft(product.Status));
                    if (left > 0) note = BulkFieldDraftLeft;
                    var moving = products.Where(product => !ProductDraftPolicy.IsDraft(product.Status) && product.Status != status).ToList();
                    await ApplyBulkStatusAsync(tenantId, moving, status, actorUserId, correlation, now, cancellationToken);
                    changed.UnionWith(moving.Select(product => product.Id));
                    break;
                }
        }

        var changedProducts = products.Where(product => changed.Contains(product.Id)).ToList();
        foreach (var product in changedProducts)
        {
            // The status change bumps its cards itself; güvenlik stoğu is the offers' (each offer's version moved).
            if (value.Field is not (BulkProductFields.Status or BulkProductFields.SafetyStock))
            {
                product.UpdatedAt = now;
                product.Version++;
            }
            db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = actorUserId, Action = BulkFieldAuditAction, TargetType = "Product", TargetId = product.Id.ToString("D"), Reason = Truncate(reason, 512), CorrelationId = correlation, CreatedAt = now });
        }
        if (value.Field is not (BulkProductFields.Status or BulkProductFields.SafetyStock) && changedProducts.Count > 0)
            await CatalogLocalEdits.MarkLinksDirtyAsync(db, tenantId, [.. changedProducts.Select(product => product.Id)], CatalogLocalEdits.ProductField, cancellationToken);
        return new(changedProducts.Count, products.Count - changedProducts.Count - left, variantCount, note);
    }
}
