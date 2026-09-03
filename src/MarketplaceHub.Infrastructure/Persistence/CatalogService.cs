using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class CatalogService(AppDbContext db, CursorCodec cursors, IConfiguration configuration, TimeProvider timeProvider, IMemoryCache countCache) : ICatalogService
{
    public async Task<PageResult<CategoryView>> ListCategoriesAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken)
    {
        var afterId = Decode(after); var query = db.Categories.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0);
        var rows = await query.OrderBy(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken);
        return Page(rows, limit, MapCategory);
    }

    public async Task<ServiceResult<CategoryView>> CreateCategoryAsync(Guid tenantId, CreateCategoryCommand command, CancellationToken cancellationToken)
    {
        var name = command.Name.Trim(); if (name.Length is < 1 or > 160) return Invalid<CategoryView>("name", "Kategori adı 1-160 karakter olmalıdır.");
        Category? parent = null;
        if (command.ParentId is Guid parentId)
        {
            parent = await db.Categories.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == parentId, cancellationToken);
            if (parent is null) return NotFound<CategoryView>();
        }
        var normalized = Normalize(name);
        var existing = await db.Categories.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ParentId == command.ParentId && x.NormalizedName == normalized, cancellationToken);
        if (existing?.IsActive == true) return Conflict<CategoryView>("CATEGORY_DUPLICATE", "Aynı üst kategoride bu ad zaten var.");
        var now = timeProvider.GetUtcNow();
        if (existing is not null)
        {
            existing.Name = name; existing.Path = parent is null ? name : $"{parent.Path} / {name}"; existing.Depth = parent is null ? 0 : parent.Depth + 1; existing.IsActive = true; existing.IsLeaf = !await db.Categories.AnyAsync(x => x.TenantId == tenantId && x.ParentId == existing.Id && x.IsActive, cancellationToken); existing.Version++; existing.UpdatedAt = now;
            if (parent is not null) { parent.IsLeaf = false; parent.Version++; parent.UpdatedAt = now; }
            await db.SaveChangesAsync(cancellationToken); return ServiceResult<CategoryView>.Ok(MapCategory(existing));
        }
        var category = new Category { Id = Guid.CreateVersion7(), TenantId = tenantId, ParentId = parent?.Id, Name = name, NormalizedName = normalized, Path = parent is null ? name : $"{parent.Path} / {name}", Depth = parent is null ? 0 : parent.Depth + 1, IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now };
        db.Categories.Add(category); if (parent is not null) { parent.IsLeaf = false; parent.Version++; parent.UpdatedAt = now; }
        await db.SaveChangesAsync(cancellationToken); return ServiceResult<CategoryView>.Ok(MapCategory(category));
    }

    public async Task<ServiceResult<CategoryView>> GetCategoryAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var category = await db.Categories.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        return category is null ? NotFound<CategoryView>() : ServiceResult<CategoryView>.Ok(MapCategory(category));
    }

    public async Task<ServiceResult<CategoryView>> UpdateCategoryAsync(Guid tenantId, Guid id, long expectedVersion, UpdateCategoryCommand command, CancellationToken cancellationToken)
    {
        var category = await db.Categories.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (category is null) return NotFound<CategoryView>();
        if (category.Version != expectedVersion) return Precondition<CategoryView>(category.Version);
        if (command.ParentId == id) return Conflict<CategoryView>("CATEGORY_PARENT_CYCLE", "Kategori kendisinin üst kategorisi olamaz.");
        Category? parent = null;
        if (command.ParentId is Guid parentId)
        {
            parent = await db.Categories.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == parentId, cancellationToken); if (parent is null) return NotFound<CategoryView>();
            var descendants = await DescendantIdsAsync(tenantId, id, cancellationToken); if (descendants.Contains(parentId)) return Conflict<CategoryView>("CATEGORY_PARENT_CYCLE", "Kategori kendi altına taşınamaz.");
        }
        var normalized = Normalize(command.Name); if (await db.Categories.AnyAsync(x => x.TenantId == tenantId && x.Id != id && x.ParentId == command.ParentId && x.NormalizedName == normalized, cancellationToken)) return Conflict<CategoryView>("CATEGORY_DUPLICATE", "Aynı üst kategoride bu ad zaten var.");
        var previousParent = category.ParentId; category.Name = command.Name.Trim(); category.NormalizedName = normalized; category.ParentId = command.ParentId; category.Path = parent is null ? category.Name : $"{parent.Path} / {category.Name}"; category.Depth = parent is null ? 0 : parent.Depth + 1; category.IsActive = command.IsActive; category.Version++; category.UpdatedAt = timeProvider.GetUtcNow();
        await RebuildDescendantPathsAsync(category, cancellationToken); await RefreshLeafAsync(tenantId, previousParent, cancellationToken); await RefreshLeafAsync(tenantId, command.ParentId, cancellationToken); await db.SaveChangesAsync(cancellationToken); return ServiceResult<CategoryView>.Ok(MapCategory(category));
    }

    public async Task<PageResult<BrandView>> ListBrandsAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken)
    {
        var afterId = Decode(after); var query = db.Brands.AsNoTracking().Where(x => x.TenantId == tenantId); if (afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0);
        var rows = await query.OrderBy(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken); return Page(rows, limit, MapBrand);
    }

    public async Task<ServiceResult<BrandView>> CreateBrandAsync(Guid tenantId, CreateBrandCommand command, CancellationToken cancellationToken)
    {
        var name = command.Name.Trim(); if (name.Length is < 1 or > 160) return Invalid<BrandView>("name", "Marka adı 1-160 karakter olmalıdır."); var normalized = Normalize(name);
        if (await db.Brands.AnyAsync(x => x.TenantId == tenantId && x.NormalizedName == normalized, cancellationToken)) return Conflict<BrandView>("BRAND_DUPLICATE", "Bu marka zaten var.");
        var now = timeProvider.GetUtcNow(); var brand = new Brand { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = name, NormalizedName = normalized, CreatedAt = now, UpdatedAt = now };
        db.Brands.Add(brand); await db.SaveChangesAsync(cancellationToken); return ServiceResult<BrandView>.Ok(MapBrand(brand));
    }

    public async Task<ServiceResult<BrandView>> GetBrandAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var brand = await db.Brands.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        return brand is null ? NotFound<BrandView>() : ServiceResult<BrandView>.Ok(MapBrand(brand));
    }

    public async Task<ServiceResult<BrandView>> UpdateBrandAsync(Guid tenantId, Guid id, long expectedVersion, UpdateBrandCommand command, CancellationToken cancellationToken)
    {
        var brand = await db.Brands.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (brand is null) return NotFound<BrandView>(); if (brand.Version != expectedVersion) return Precondition<BrandView>(brand.Version);
        var normalized = Normalize(command.Name); if (await db.Brands.AnyAsync(x => x.TenantId == tenantId && x.Id != id && x.NormalizedName == normalized, cancellationToken)) return Conflict<BrandView>("BRAND_DUPLICATE", "Bu marka zaten var.");
        brand.Name = command.Name.Trim(); brand.NormalizedName = normalized; brand.IsActive = command.IsActive; brand.UpdatedAt = timeProvider.GetUtcNow(); brand.Version++; await db.SaveChangesAsync(cancellationToken); return ServiceResult<BrandView>.Ok(MapBrand(brand));
    }

    public async Task<PageResult<AttributeView>> ListAttributesAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken)
    {
        var afterId = Decode(after); var query = db.AttributeDefinitions.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive); if (afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0);
        var rows = await query.OrderBy(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken); var ids = rows.Take(limit).Select(x => x.Id).ToArray(); var values = await db.AttributeValues.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.AttributeId)).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        var roleRows = await db.CategoryAttributeRequirements.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.AttributeId)).Select(x => new { x.AttributeId, x.Role }).ToListAsync(cancellationToken);
        var roles = roleRows.GroupBy(x => x.AttributeId).ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(x => NormalizeRequirementRole(x.Role)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList());
        return Page(rows, limit, attribute => MapAttribute(attribute, values.Where(x => x.AttributeId == attribute.Id), roles.GetValueOrDefault(attribute.Id)));
    }

    public async Task<ServiceResult<AttributeView>> CreateAttributeAsync(Guid tenantId, CreateAttributeCommand command, CancellationToken cancellationToken)
    {
        if (!TryAttributeType(command.DataType, out var dataType)) return Invalid<AttributeView>("dataType", "İzinli tipler TEXT, NUMBER, SINGLE_SELECT, MULTI_SELECT ve BOOLEAN'dır.");
        var code = Normalize(command.Code); if (code.Length is < 1 or > 96) return Invalid<AttributeView>("code", "Özellik kodu geçersizdir.");
        var requestedValues = command.Values
            .Select(value => new { Value = value.Value.Trim(), NormalizedValue = Normalize(value.Value), value.SortOrder })
            .ToList();
        if (requestedValues.GroupBy(value => value.NormalizedValue).Any(group => group.Count() > 1)) return Invalid<AttributeView>("values", "Aynı özellik değeri tekrarlanamaz.");
        if (dataType is AttributeDataType.SingleSelect or AttributeDataType.MultiSelect && requestedValues.Count == 0) return Invalid<AttributeView>("values", "Seçimli özellik en az bir değer ister.");

        var now = timeProvider.GetUtcNow();
        var existingAttribute = await db.AttributeDefinitions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Code == code, cancellationToken);
        if (existingAttribute is not null)
        {
            if (existingAttribute.IsActive) return Conflict<AttributeView>("ATTRIBUTE_DUPLICATE", "Bu özellik kodu zaten var.");

            existingAttribute.IsActive = true;
            existingAttribute.Name = command.Name.Trim();
            existingAttribute.DataType = dataType;
            existingAttribute.SelectionMode = command.SelectionMode;
            existingAttribute.Unit = command.Unit;
            existingAttribute.Version++;
            existingAttribute.UpdatedAt = now;

            var existingValues = await db.AttributeValues
                .Where(x => x.TenantId == tenantId && x.AttributeId == existingAttribute.Id)
                .OrderBy(x => x.SortOrder)
                .ToListAsync(cancellationToken);
            foreach (var requestedValue in requestedValues)
            {
                var previousValue = existingValues.FirstOrDefault(value => value.NormalizedValue == requestedValue.NormalizedValue);
                if (previousValue is not null)
                {
                    previousValue.IsActive = true;
                    previousValue.Value = requestedValue.Value;
                    previousValue.SortOrder = requestedValue.SortOrder;
                }
                else
                {
                    var restoredValue = new AttributeValue
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = tenantId,
                        AttributeId = existingAttribute.Id,
                        Value = requestedValue.Value,
                        NormalizedValue = requestedValue.NormalizedValue,
                        SortOrder = requestedValue.SortOrder,
                        IsActive = true
                    };
                    existingValues.Add(restoredValue);
                    db.AttributeValues.Add(restoredValue);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<AttributeView>.Ok(MapAttribute(existingAttribute, existingValues));
        }

        var attribute = new AttributeDefinition { Id = Guid.CreateVersion7(), TenantId = tenantId, Code = code, Name = command.Name.Trim(), DataType = dataType, SelectionMode = command.SelectionMode, Unit = command.Unit, CreatedAt = now, UpdatedAt = now };
        var values = requestedValues.Select(value => new AttributeValue { Id = Guid.CreateVersion7(), TenantId = tenantId, AttributeId = attribute.Id, Value = value.Value, NormalizedValue = value.NormalizedValue, SortOrder = value.SortOrder }).ToList();
        db.AttributeDefinitions.Add(attribute); db.AttributeValues.AddRange(values); await db.SaveChangesAsync(cancellationToken); return ServiceResult<AttributeView>.Ok(MapAttribute(attribute, values));
    }

    public async Task<ServiceResult<AttributeView>> DeactivateAttributeAsync(Guid tenantId, Guid attributeId, long expectedVersion, CancellationToken cancellationToken)
    {
        var attribute = await db.AttributeDefinitions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == attributeId && x.IsActive, cancellationToken);
        if (attribute is null) return NotFound<AttributeView>();
        if (attribute.Version != expectedVersion) return Precondition<AttributeView>(attribute.Version);
        attribute.IsActive = false;
        attribute.Version++;
        attribute.UpdatedAt = timeProvider.GetUtcNow();
        var values = await db.AttributeValues
            .Where(x => x.TenantId == tenantId && x.AttributeId == attributeId && x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);
        foreach (var value in values) value.IsActive = false;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<AttributeView>.Ok(MapAttribute(attribute, values));
    }

    public async Task<ServiceResult<AttributeView>> AddAttributeValuesAsync(Guid tenantId, Guid attributeId, IReadOnlyList<CreateAttributeValueCommand> values, CancellationToken cancellationToken)
    {
        var attribute = await db.AttributeDefinitions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == attributeId && x.IsActive, cancellationToken);
        if (attribute is null) return NotFound<AttributeView>();
        if (attribute.DataType == AttributeDataType.Text)
        {
            attribute.DataType = AttributeDataType.SingleSelect;
            attribute.SelectionMode = "SINGLE";
        }
        else if (attribute.DataType is not (AttributeDataType.SingleSelect or AttributeDataType.MultiSelect))
        {
            return Invalid<AttributeView>("values", "Yalnız metin veya seçim tipindeki özelliklere seçenek değeri eklenebilir.");
        }
        var normalized = values.Select(x => Normalize(x.Value)).ToArray();
        if (normalized.Length == 0 || normalized.Any(string.IsNullOrWhiteSpace) || normalized.Distinct().Count() != normalized.Length) return Invalid<AttributeView>("values", "En az bir benzersiz ve boş olmayan seçenek değeri girin.");
        var existing = await db.AttributeValues.Where(x => x.TenantId == tenantId && x.AttributeId == attributeId).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        if (existing.Any(x => x.IsActive && normalized.Contains(x.NormalizedValue))) return Conflict<AttributeView>("ATTRIBUTE_VALUE_DUPLICATE", "Seçenek değerlerinden biri bu özellikte zaten var.");
        var nextSort = existing.Count == 0 ? 0 : existing.Max(x => x.SortOrder) + 1;
        var now = timeProvider.GetUtcNow();
        var additions = new List<AttributeValue>();
        foreach (var (value, index) in values.Select((value, index) => (value, index)))
        {
            var normalizedValue = normalized[index];
            var inactiveValue = existing.FirstOrDefault(item => !item.IsActive && item.NormalizedValue == normalizedValue);
            if (inactiveValue is not null)
            {
                inactiveValue.IsActive = true;
                inactiveValue.Value = value.Value.Trim();
                inactiveValue.SortOrder = nextSort + index;
                continue;
            }

            additions.Add(new AttributeValue { Id = Guid.CreateVersion7(), TenantId = tenantId, AttributeId = attributeId, Value = value.Value.Trim(), NormalizedValue = normalizedValue, SortOrder = nextSort + index, IsActive = true });
        }
        db.AttributeValues.AddRange(additions); attribute.Version++; attribute.UpdatedAt = now; await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<AttributeView>.Ok(MapAttribute(attribute, existing.Concat(additions)));
    }

    public async Task<ServiceResult<AttributeView>> DeactivateAttributeValueAsync(Guid tenantId, Guid attributeId, Guid valueId, CancellationToken cancellationToken)
    {
        var attribute = await db.AttributeDefinitions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == attributeId && x.IsActive, cancellationToken);
        if (attribute is null) return ServiceResult<AttributeView>.Fail("ATTRIBUTE_NOT_FOUND", "Özellik bulunamadı.", 404);
        var value = await db.AttributeValues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == valueId && x.AttributeId == attributeId, cancellationToken);
        if (value is null) return ServiceResult<AttributeView>.Fail("ATTRIBUTE_VALUE_NOT_FOUND", "Seçenek değeri bulunamadı.", 404);
        value.IsActive = false;
        attribute.Version++;
        attribute.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        var values = await db.AttributeValues.AsNoTracking().Where(x => x.TenantId == tenantId && x.AttributeId == attributeId).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        return ServiceResult<AttributeView>.Ok(MapAttribute(attribute, values));
    }

    public async Task<ServiceResult<IReadOnlyList<CategoryAttributeRequirementView>>> GetRequirementsAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken)
    {
        if (!await db.Categories.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == categoryId, cancellationToken)) return NotFound<IReadOnlyList<CategoryAttributeRequirementView>>();
        var requirements = await db.CategoryAttributeRequirements.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CategoryId == categoryId)
            .OrderBy(x => x.DisplayOrder)
            .ToListAsync(cancellationToken);
        var attributeIds = requirements.Select(x => x.AttributeId).Distinct().ToArray();
        var attributes = await db.AttributeDefinitions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && attributeIds.Contains(x.Id) && x.IsActive)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var values = await db.AttributeValues.AsNoTracking()
            .Where(x => x.TenantId == tenantId && attributeIds.Contains(x.AttributeId) && x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Value)
            .ToListAsync(cancellationToken);
        var attributeLookup = attributes.ToDictionary(x => x.Id, x => MapAttribute(x, values.Where(value => value.AttributeId == x.Id)));
        var result = requirements.Where(x => attributeLookup.ContainsKey(x.AttributeId)).Select(x => new CategoryAttributeRequirementView(x.AttributeId, x.IsRequired, x.AllowsCustomValue, x.DisplayOrder, attributeLookup[x.AttributeId], NormalizeRequirementRole(x.Role))).ToList();
        return ServiceResult<IReadOnlyList<CategoryAttributeRequirementView>>.Ok(result);
    }

    public async Task<ServiceResult<IReadOnlyList<AttributeRequirementCommand>>> ReplaceRequirementsAsync(Guid tenantId, Guid categoryId, long expectedVersion, IReadOnlyList<AttributeRequirementCommand> requirements, CancellationToken cancellationToken)
    {
        var category = await db.Categories.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == categoryId, cancellationToken); if (category is null) return NotFound<IReadOnlyList<AttributeRequirementCommand>>(); if (category.Version != expectedVersion) return Precondition<IReadOnlyList<AttributeRequirementCommand>>(category.Version);
        if (requirements.Select(x => x.AttributeId).Distinct().Count() != requirements.Count) return Invalid<IReadOnlyList<AttributeRequirementCommand>>("requirements", "Özellik gereksinimi tekrarlanamaz.");
        var optionCount = requirements.Count(x => string.Equals(NormalizeRequirementRole(x.Role), "OPTION", StringComparison.Ordinal));
        if (optionCount > 2) return Invalid<IReadOnlyList<AttributeRequirementCommand>>("requirements", "Bir kategoride en fazla 2 seçenek grubu tanımlanabilir.");
        var ids = requirements.Select(x => x.AttributeId).ToArray(); if (await db.AttributeDefinitions.CountAsync(x => x.TenantId == tenantId && ids.Contains(x.Id) && x.IsActive, cancellationToken) != ids.Length) return Invalid<IReadOnlyList<AttributeRequirementCommand>>("requirements", "Etkin olmayan veya bulunmayan özellik vardır.");
        var current = await db.CategoryAttributeRequirements.Where(x => x.TenantId == tenantId && x.CategoryId == categoryId).ToListAsync(cancellationToken); db.CategoryAttributeRequirements.RemoveRange(current); db.CategoryAttributeRequirements.AddRange(requirements.Select(x => new CategoryAttributeRequirement { Id = Guid.CreateVersion7(), TenantId = tenantId, CategoryId = categoryId, AttributeId = x.AttributeId, IsRequired = x.IsRequired, AllowsCustomValue = x.AllowsCustomValue, Role = NormalizeRequirementRole(x.Role), DisplayOrder = x.DisplayOrder })); category.Version++; category.UpdatedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return ServiceResult<IReadOnlyList<AttributeRequirementCommand>>.Ok(requirements);
    }

    public async Task<PageResult<ProductView>> ListProductsAsync(Guid tenantId, int limit, string? after, string? status, string? search, string? platform, string? stock, CancellationToken cancellationToken)
    {
        var query = VisibleProducts(tenantId);
        ApplyProductFilters(ref query, tenantId, status, search, platform, stock);
        var countKey = $"catalog:product-family-count:v2:{tenantId:N}:{status?.Trim()}:{search?.Trim()}:{platform?.Trim()}:{stock?.Trim()}";
        var cachedCount = countCache.Get(countKey);
        if (!cursors.TryDecodeProduct(after, out var afterUpdatedAt, out var afterId))
            throw new ArgumentException("Cursor geçersiz veya süresi dolmuş.", nameof(after));
        var allProducts = await query.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id).ToListAsync(cancellationToken);
        var allProductIds = allProducts.Select(x => x.Id).ToArray();
        var allVariants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && allProductIds.Contains(x.ProductId)).ToListAsync(cancellationToken);
        var variantsByProduct = allVariants.GroupBy(x => x.ProductId).ToDictionary(x => x.Key, x => x.ToList());
        var families = allProducts
            .GroupBy(product => ProductFamilyKey(product, variantsByProduct.GetValueOrDefault(product.Id) ?? []), StringComparer.Ordinal)
            .Select(group => new ProductFamily(group.Key, group.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id).First(), group.ToList()))
            .OrderByDescending(x => x.Primary.UpdatedAt).ThenByDescending(x => x.Primary.Id)
            .ToList();
        var totalCount = cachedCount is int count ? count : families.Count;
        if (cachedCount is not int)
            countCache.Set(countKey, totalCount, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(15), Size = 1 });
        var pageFamilies = families
            .Where(x => afterId == Guid.Empty || x.Primary.UpdatedAt < afterUpdatedAt || x.Primary.UpdatedAt == afterUpdatedAt && x.Primary.Id.CompareTo(afterId) < 0)
            .Take(limit)
            .ToList();
        var products = pageFamilies.SelectMany(x => x.Products).ToList();
        var ids = products.Select(x => x.Id).ToHashSet();
        var variants = allVariants.Where(x => ids.Contains(x.ProductId)).ToList();
        var views = await BuildProductViewsAsync(tenantId, products, variants, cancellationToken);
        var lastFamily = pageFamilies.LastOrDefault();
        var hasMore = lastFamily is not null && families.Any(x => x.Primary.UpdatedAt < lastFamily.Primary.UpdatedAt || x.Primary.UpdatedAt == lastFamily.Primary.UpdatedAt && x.Primary.Id.CompareTo(lastFamily.Primary.Id) < 0);
        return new(views, hasMore ? cursors.EncodeProduct(lastFamily!.Primary.UpdatedAt, lastFamily.Primary.Id) : null, hasMore, totalCount);
    }

    private sealed record ProductFamily(string Key, Product Primary, IReadOnlyList<Product> Products);

    private static string ProductFamilyKey(Product product, IReadOnlyList<ProductVariant> variants)
    {
        var modelCode = variants.Select(x => x.ModelCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        return string.IsNullOrWhiteSpace(modelCode) ? $"product:{product.Id:D}" : $"model:{modelCode.Trim().ToUpperInvariant()}";
    }

    public async Task<ProductSummaryView> ProductSummaryAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var products = await VisibleProducts(tenantId)
            .Select(x => new { x.Id, x.Status })
            .ToListAsync(cancellationToken);
        var variants = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => new { x.Id, x.ProductId })
            .ToListAsync(cancellationToken);
        var inventoryByVariant = await db.InventoryItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN")
            .GroupBy(x => x.VariantId)
            .Select(group => new { VariantId = group.Key, TotalStock = group.Sum(x => x.OnHand) })
            .ToDictionaryAsync(x => x.VariantId, x => x.TotalStock, cancellationToken);
        var stockByProduct = variants
            .GroupBy(x => x.ProductId)
            .ToDictionary(group => group.Key, group => group.Sum(variant => inventoryByVariant.GetValueOrDefault(variant.Id)));
        var platforms = await (from profile in db.ChannelListingProfiles.AsNoTracking()
                               join connection in db.PlatformConnections.AsNoTracking()
                                   on new { profile.TenantId, profile.ConnectionId } equals new { connection.TenantId, ConnectionId = connection.Id }
                               where profile.TenantId == tenantId && profile.Enabled && connection.Status != "HIDDEN"
                               select connection.DisplayName)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
        return new(
            products.Count,
            products.Count(x => x.Status == ProductStatus.Active),
            products.Count(x => stockByProduct.GetValueOrDefault(x.Id) <= 0),
            products.Count(x => stockByProduct.GetValueOrDefault(x.Id) > 0 && stockByProduct.GetValueOrDefault(x.Id) <= 5),
            platforms);
    }

    public async Task<ServiceResult<int>> BulkSetStatusAsync(Guid tenantId, BulkProductStatusCommand command, CancellationToken cancellationToken)
    {
        var ids = command.ProductIds.Distinct().ToArray();
        if (ids.Length is 0 or > 500) return ServiceResult<int>.Fail("BULK_PRODUCT_LIMIT", "Toplu işlemde 1-500 ürün seçilebilir.", 422);
        if (!Enum.TryParse<ProductStatus>(command.Status, true, out var status) || status == ProductStatus.Draft)
            return ServiceResult<int>.Fail("BULK_PRODUCT_STATUS_INVALID", "Toplu işlem yalnız ACTIVE veya ARCHIVED durumuna izin verir.", 422);

        var products = await db.Products.Where(x => x.TenantId == tenantId && ids.Contains(x.Id)).ToListAsync(cancellationToken);
        if (products.Count != ids.Length) return ServiceResult<int>.Fail("BULK_PRODUCT_NOT_FOUND", "Seçilen ürünlerden biri bulunamadı veya bu çalışma alanına ait değil.", 404);
        var now = timeProvider.GetUtcNow();
        foreach (var product in products)
        {
            product.Status = status;
            product.ArchivedAt = status == ProductStatus.Archived ? now : null;
            product.UpdatedAt = now;
            product.Version++;
        }
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<int>.Ok(products.Count);
    }

    public Task<ServiceResult<int>> DeleteProductAsync(Guid tenantId, Guid id, long expectedVersion, CancellationToken cancellationToken) =>
        DeleteProductsAsync(tenantId, [id], expectedVersion, cancellationToken);

    public async Task<ServiceResult<int>> BulkDeleteProductsAsync(Guid tenantId, BulkProductDeleteCommand command, CancellationToken cancellationToken)
    {
        var ids = command.ProductIds.Distinct().ToArray();
        if (ids.Length is 0 or > 500) return ServiceResult<int>.Fail("BULK_PRODUCT_LIMIT", "Toplu silme işleminde 1-500 ürün seçilebilir.", 422);
        return await DeleteProductsAsync(tenantId, ids, null, cancellationToken);
    }

    private async Task<ServiceResult<int>> DeleteProductsAsync(Guid tenantId, IReadOnlyList<Guid> ids, long? expectedVersion, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var products = await db.Products
            .Where(x => x.TenantId == tenantId && ids.Contains(x.Id))
            .ToListAsync(cancellationToken);
        if (expectedVersion is not null)
        {
            var product = products.SingleOrDefault(x => x.Id == ids[0]);
            if (product is null) return NotFound<int>();
            if (product.Version != expectedVersion.Value) return Precondition<int>(product.Version);
        }
        else if (products.Count != ids.Count)
        {
            return ServiceResult<int>.Fail("BULK_PRODUCT_NOT_FOUND", "Seçilen ürünlerden biri bulunamadı veya bu çalışma alanına ait değil.", 404);
        }

        var targetIds = products.Select(x => x.Id).ToArray();
        var variantIds = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && targetIds.Contains(x.ProductId))
            .Select(x => x.Id)
            .ToArrayAsync(cancellationToken);
        var inventoryIds = await db.InventoryItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && variantIds.Contains(x.VariantId))
            .Select(x => x.Id)
            .ToArrayAsync(cancellationToken);
        if (inventoryIds.Length > 0 && await db.ReturnStockDispositions.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && inventoryIds.Contains(x.InventoryItemId), cancellationToken))
            return Conflict<int>("PRODUCT_DELETE_BLOCKED", "Ürün iade stok hareketine bağlı olduğu için silinemiyor. Sipariş ve iade geçmişini korumak için ürünü arşivleyin.");

        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            CREATE TEMP TABLE IF NOT EXISTS purge_product_ids ("Id" uuid PRIMARY KEY) ON COMMIT DROP;
            TRUNCATE purge_product_ids;
            INSERT INTO purge_product_ids ("Id") SELECT unnest({{targetIds}});

            -- Sipariş geçmişi korunur; yalnızca silinen varyantla canlı bağlantı kaldırılır.
            UPDATE sales.order_lines
            SET "VariantId"=NULL
            WHERE "TenantId"={{tenantId}}
              AND "VariantId" IN (SELECT v."Id" FROM catalog.product_variants v JOIN purge_product_ids p ON p."Id"=v."ProductId" WHERE v."TenantId"={{tenantId}});

            DELETE FROM catalog.import_decisions
            WHERE "TenantId"={{tenantId}}
              AND "CandidateId" IN (
                  SELECT c."Id" FROM catalog.import_match_candidates c
                  WHERE c."TenantId"={{tenantId}}
                    AND (c."ProductId" IN (SELECT "Id" FROM purge_product_ids)
                      OR c."VariantId" IN (SELECT v."Id" FROM catalog.product_variants v JOIN purge_product_ids p ON p."Id"=v."ProductId")));
            DELETE FROM catalog.import_match_candidates c
            WHERE c."TenantId"={{tenantId}}
              AND (c."ProductId" IN (SELECT "Id" FROM purge_product_ids)
                OR c."VariantId" IN (SELECT v."Id" FROM catalog.product_variants v JOIN purge_product_ids p ON p."Id"=v."ProductId"));
            DELETE FROM catalog.field_provenance f
            WHERE f."TenantId"={{tenantId}}
              AND (f."ProductId" IN (SELECT "Id" FROM purge_product_ids)
                OR f."VariantId" IN (SELECT v."Id" FROM catalog.product_variants v JOIN purge_product_ids p ON p."Id"=v."ProductId"));
            DELETE FROM catalog.external_identifier_aliases a
            WHERE a."TenantId"={{tenantId}}
              AND ((a."EntityType"='PRODUCT' AND a."LocalId" IN (SELECT "Id" FROM purge_product_ids))
                OR (a."EntityType"='VARIANT' AND a."LocalId" IN (SELECT v."Id" FROM catalog.product_variants v JOIN purge_product_ids p ON p."Id"=v."ProductId")));

            DELETE FROM inventory.channel_price_history h
            USING inventory.channel_offers o, catalog.product_variants v, purge_product_ids p
            WHERE h."TenantId"={{tenantId}} AND h."OfferId"=o."Id" AND o."VariantId"=v."Id" AND v."ProductId"=p."Id";
            DELETE FROM inventory.channel_offers o
            USING catalog.product_variants v, purge_product_ids p
            WHERE o."TenantId"={{tenantId}} AND o."VariantId"=v."Id" AND v."ProductId"=p."Id";
            DELETE FROM inventory.stock_reservations r
            USING inventory.inventory_items i, catalog.product_variants v, purge_product_ids p
            WHERE r."TenantId"={{tenantId}} AND r."InventoryItemId"=i."Id" AND i."VariantId"=v."Id" AND v."ProductId"=p."Id";
            DELETE FROM inventory.stock_ledger_entries l
            USING inventory.inventory_items i, catalog.product_variants v, purge_product_ids p
            WHERE l."TenantId"={{tenantId}} AND l."InventoryItemId"=i."Id" AND i."VariantId"=v."Id" AND v."ProductId"=p."Id";
            DELETE FROM inventory.inventory_items i
            USING catalog.product_variants v, purge_product_ids p
            WHERE i."TenantId"={{tenantId}} AND i."VariantId"=v."Id" AND v."ProductId"=p."Id";

            DELETE FROM catalog.marketplace_listing_states s
            USING catalog.product_variants v, purge_product_ids p
            WHERE s."TenantId"={{tenantId}} AND s."VariantId"=v."Id" AND v."ProductId"=p."Id";
            DELETE FROM catalog.marketplace_variant_links l
            USING catalog.product_variants v, purge_product_ids p
            WHERE l."TenantId"={{tenantId}} AND l."VariantId"=v."Id" AND v."ProductId"=p."Id";
            DELETE FROM catalog.channel_media_order o
            USING catalog.product_media m, purge_product_ids p
            WHERE o."TenantId"={{tenantId}} AND o."MediaId"=m."Id" AND m."ProductId"=p."Id";
            DELETE FROM catalog.channel_listing_profiles p
            USING purge_product_ids targets
            WHERE p."TenantId"={{tenantId}} AND p."ProductId"=targets."Id";
            DELETE FROM catalog.marketplace_product_links l
            USING purge_product_ids p
            WHERE l."TenantId"={{tenantId}} AND l."ProductId"=p."Id";
            DELETE FROM catalog.product_media m
            USING purge_product_ids p
            WHERE m."TenantId"={{tenantId}} AND m."ProductId"=p."Id";
            DELETE FROM catalog.product_attribute_assignments a
            USING purge_product_ids p
            WHERE a."TenantId"={{tenantId}} AND a."ProductId"=p."Id";
            DELETE FROM catalog.variant_option_values x
            USING catalog.product_variants v, purge_product_ids p
            WHERE x."TenantId"={{tenantId}} AND x."VariantId"=v."Id" AND v."ProductId"=p."Id";
            DELETE FROM catalog.product_option_values x
            USING catalog.product_options o, purge_product_ids p
            WHERE x."TenantId"={{tenantId}} AND x."OptionId"=o."Id" AND o."ProductId"=p."Id";
            DELETE FROM catalog.product_options o
            USING purge_product_ids p
            WHERE o."TenantId"={{tenantId}} AND o."ProductId"=p."Id";
            DELETE FROM catalog.product_variants v
            USING purge_product_ids p
            WHERE v."TenantId"={{tenantId}} AND v."ProductId"=p."Id";
            DELETE FROM dashboard.low_stock d
            USING purge_product_ids p
            WHERE d."TenantId"={{tenantId}} AND d."ProductId"=p."Id";
            DELETE FROM catalog.products x
            USING purge_product_ids p
            WHERE x."TenantId"={{tenantId}} AND x."Id"=p."Id";
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<int>.Ok(products.Count);
    }

    public async Task<ServiceResult<ProductView>> CreateProductAsync(Guid tenantId, CreateProductCommand command, CancellationToken cancellationToken)
    {
        var validation = await ValidateProductReferencesAsync(tenantId, command.Title, command.CategoryId, command.BrandId, cancellationToken);
        if (validation is not null) return ServiceResult<ProductView>.Fail(validation.Code, validation.Message, validation.Status, validation.FieldErrors);
        if (command.Variants.Count == 0) return Invalid<ProductView>("variants", "Ürün en az bir satış varyantı ister.");
        if (command.Variants.Count > 1000) return Invalid<ProductView>("variants", "Tek ürün kaydında en fazla 1000 varyant oluşturulabilir.");

        var globalAssignments = command.Attributes ?? [];
        var globalAttributeValidation = await ValidateAttributeValuesAsync(tenantId, globalAssignments, cancellationToken);
        if (globalAttributeValidation is not null) return ServiceResult<ProductView>.Fail(globalAttributeValidation.Code, globalAttributeValidation.Message, globalAttributeValidation.Status, globalAttributeValidation.FieldErrors);
        foreach (var variant in command.Variants)
        {
            var variantAttributeValidation = await ValidateAttributeValuesAsync(tenantId, variant.Attributes ?? [], cancellationToken);
            if (variantAttributeValidation is not null) return ServiceResult<ProductView>.Fail(variantAttributeValidation.Code, variantAttributeValidation.Message, variantAttributeValidation.Status, variantAttributeValidation.FieldErrors);
        }
        var optionValidation = await ValidateVariantOptionsAsync(tenantId, command.CategoryId, command.Variants, cancellationToken);
        if (optionValidation is not null) return ServiceResult<ProductView>.Fail(optionValidation.Code, optionValidation.Message, optionValidation.Status, optionValidation.FieldErrors);
        if (command.CategoryId is Guid categoryId)
        {
            var requiredAttributeIds = await db.CategoryAttributeRequirements.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.CategoryId == categoryId && x.IsRequired)
                .Select(x => x.AttributeId)
                .ToListAsync(cancellationToken);
            var globalIds = globalAssignments.Select(x => x.AttributeId).ToHashSet();
            for (var index = 0; index < command.Variants.Count; index++)
            {
                var supplied = globalIds.Concat((command.Variants[index].Attributes ?? []).Select(x => x.AttributeId)).ToHashSet();
                if (requiredAttributeIds.Any(id => !supplied.Contains(id)))
                    return ServiceResult<ProductView>.Fail("REQUIRED_ATTRIBUTE_MISSING", $"{index + 1}. varyant için kategori zorunlu özellikleri eksik.", 422, new Dictionary<string, string[]> { ["variants"] = [$"{index + 1}. varyant için tüm zorunlu özellikleri seçin."] });
            }
        }

        var normalizedSkus = command.Variants.Select(x => Normalize(x.Sku)).ToArray();
        if (normalizedSkus.Any(string.IsNullOrWhiteSpace) || normalizedSkus.Distinct().Count() != normalizedSkus.Length) return Invalid<ProductView>("variants", "SKU boş veya tekrarlı olamaz.");
        if (await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId && normalizedSkus.Contains(x.SkuNormalized), cancellationToken)) return Conflict<ProductView>("SKU_CONFLICT_REVIEW_REQUIRED", "SKU başka bir varyantla çakışıyor; otomatik birleştirme yapılmadı.");
        var normalizedBarcodes = command.Variants.Where(x => !string.IsNullOrWhiteSpace(x.Barcode)).Select(x => Normalize(x.Barcode!)).ToArray();
        if (normalizedBarcodes.Distinct().Count() != normalizedBarcodes.Length || await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId && x.BarcodeNormalized != null && normalizedBarcodes.Contains(x.BarcodeNormalized), cancellationToken)) return Conflict<ProductView>("BARCODE_CONFLICT_REVIEW_REQUIRED", "Barkod başka bir varyantla çakışıyor; otomatik birleştirme yapılmadı.");

        var now = timeProvider.GetUtcNow();
        var productStatus = command.Status == "ACTIVE" ? ProductStatus.Active : (command.Status == "ARCHIVED" ? ProductStatus.Archived : ProductStatus.Draft); var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenantId, Title = command.Title.Trim(), Description = command.Description.Trim(), BrandId = command.BrandId, CategoryId = command.CategoryId, Status = productStatus, CreatedAt = now, UpdatedAt = now };
        var variants = command.Variants.Select(variant => new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, Sku = variant.Sku.Trim(), SkuNormalized = Normalize(variant.Sku), Barcode = NullTrim(variant.Barcode), BarcodeNormalized = string.IsNullOrWhiteSpace(variant.Barcode) ? null : Normalize(variant.Barcode), ModelCode = NullTrim(variant.ModelCode), OptionSignature = Signature(variant.Options), Status = productStatus, Weight = PositiveOrNull(variant.Weight), Width = PositiveOrNull(variant.Width), Height = PositiveOrNull(variant.Height), Length = PositiveOrNull(variant.Length), Desi = PositiveOrNull(variant.Desi), CreatedAt = now, UpdatedAt = now }).ToList();
        db.Products.Add(product);
        db.ProductVariants.AddRange(variants);
        db.ProductAttributeAssignments.AddRange(globalAssignments.Select(x => Assignment(tenantId, product.Id, null, x)));
        for (var index = 0; index < variants.Count; index++) db.ProductAttributeAssignments.AddRange((command.Variants[index].Attributes ?? []).Select(x => Assignment(tenantId, product.Id, variants[index].Id, x)));
        await PersistVariantOptionsAsync(tenantId, product.Id, variants, command.Variants, cancellationToken);
        await EnsureMainInventoryAsync(tenantId, variants, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await GetProductAsync(tenantId, product.Id, cancellationToken);
    }

    public async Task<ServiceResult<ProductView>> GetProductAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var product = await VisibleProducts(tenantId).SingleOrDefaultAsync(x => x.Id == id, cancellationToken); if (product is null) return NotFound<ProductView>();
        var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == id).ToListAsync(cancellationToken);
        var views = await BuildProductViewsAsync(tenantId, [product], variants, cancellationToken);
        var modelCode = variants.Select(x => x.ModelCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();
        var familyProductIds = string.IsNullOrWhiteSpace(modelCode)
            ? [id]
            : await (from siblingVariant in db.ProductVariants.AsNoTracking()
                     join siblingProduct in VisibleProducts(tenantId) on siblingVariant.ProductId equals siblingProduct.Id
                     where siblingVariant.ModelCode != null && siblingVariant.ModelCode == modelCode
                     select siblingProduct.Id).Distinct().ToListAsync(cancellationToken);
        var familyMediaRows = await (from item in db.ProductMedia.AsNoTracking()
                                     join asset in db.FileAssets.AsNoTracking() on new { item.TenantId, item.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                                     where item.TenantId == tenantId && familyProductIds.Contains(item.ProductId) && item.Status == "ACTIVE" && asset.Status == "ACTIVE" && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                                     orderby item.SortOrder
                                     select new { item.ProductId, item.VariantId, item.SortOrder, asset.Id, asset.Classification, Url = asset.RelativePath }).ToListAsync(cancellationToken);
        var familyMediaUrls = familyMediaRows
            .GroupBy(item => item.ProductId)
            .SelectMany(group =>
            {
                var productMedia = group.Where(item => item.VariantId is null).OrderBy(item => item.SortOrder).ToList();
                return productMedia.Count > 0 ? productMedia : group.OrderBy(item => item.SortOrder).Take(1).ToList();
            })
            .OrderBy(item => item.SortOrder)
            .Select(item => item.Classification == "PRODUCT_MEDIA_URL" ? item.Url : $"/api/v1/files/product-media/{item.Id:D}/content")
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ServiceResult<ProductView>.Ok(views[0] with { FamilyMediaUrls = familyMediaUrls });
    }

    public async Task<ServiceResult<ProductView>> UpdateProductAsync(Guid tenantId, Guid id, long expectedVersion, UpdateProductCommand command, CancellationToken cancellationToken)
    {
        var product = await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (product is null) return NotFound<ProductView>(); if (product.Version != expectedVersion) return Precondition<ProductView>(product.Version);
        var validation = await ValidateProductReferencesAsync(tenantId, command.Title, command.CategoryId, command.BrandId, cancellationToken); if (validation is not null) return ServiceResult<ProductView>.Fail(validation.Code, validation.Message, validation.Status, validation.FieldErrors);
        if (command.Attributes is not null)
        {
            var attributeValidation = await ValidateAttributesAsync(tenantId, command.CategoryId, command.Attributes, cancellationToken); if (attributeValidation is not null) return ServiceResult<ProductView>.Fail(attributeValidation.Code, attributeValidation.Message, attributeValidation.Status, attributeValidation.FieldErrors);
        }
        var variantsToCreate = command.VariantsToCreate ?? [];
        var variantUpdates = command.VariantUpdates ?? [];
        // Status updates apply to every existing sale row as well. Load the rows
        // whenever status is present; otherwise a save that only changes the
        // product status leaves its variants on their previous status.
        var existingVariants = variantsToCreate.Count > 0 || variantUpdates.Count > 0 || command.Status is not null
            ? await db.ProductVariants.Where(x => x.TenantId == tenantId && x.ProductId == id).ToListAsync(cancellationToken)
            : [];
        if (variantUpdates.Count > 0)
        {
            if (variantUpdates.Select(x => x.Id).Distinct().Count() != variantUpdates.Count || variantUpdates.Any(x => !existingVariants.Any(existing => existing.Id == x.Id))) return Invalid<ProductView>("variantUpdates", "Güncellenecek varyant ürün kaydına ait değil.");
            var updatesById = variantUpdates.ToDictionary(x => x.Id);
            var proposedSkus = existingVariants.Select(variant => Normalize(updatesById.TryGetValue(variant.Id, out var update) ? update.Sku : variant.Sku)).ToArray();
            if (proposedSkus.Any(string.IsNullOrWhiteSpace) || proposedSkus.Distinct().Count() != proposedSkus.Length) return Invalid<ProductView>("variantUpdates", "Stok kodları boş veya tekrarlı olamaz.");
            var proposedBarcodes = existingVariants.Select(variant => updatesById.TryGetValue(variant.Id, out var update) ? Normalize(update.Barcode ?? string.Empty) : variant.BarcodeNormalized).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray();
            if (proposedBarcodes.Distinct().Count() != proposedBarcodes.Length) return Conflict<ProductView>("BARCODE_CONFLICT_REVIEW_REQUIRED", "Barkodlar ürün içinde benzersiz olmalıdır.");
            if (await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId && x.ProductId != id && (proposedSkus.Contains(x.SkuNormalized) || x.BarcodeNormalized != null && proposedBarcodes.Contains(x.BarcodeNormalized)), cancellationToken)) return Conflict<ProductView>("VARIANT_CODE_CONFLICT_REVIEW_REQUIRED", "Stok kodu veya barkod başka bir varyantla çakışıyor.");
            var updatedAt = timeProvider.GetUtcNow();
            foreach (var update in variantUpdates)
            {
                var variant = existingVariants.Single(x => x.Id == update.Id);
                variant.Sku = update.Sku.Trim(); variant.SkuNormalized = Normalize(update.Sku); variant.Barcode = NullTrim(update.Barcode); variant.BarcodeNormalized = string.IsNullOrWhiteSpace(update.Barcode) ? null : Normalize(update.Barcode); variant.ModelCode = NullTrim(update.ModelCode); variant.UpdatedAt = updatedAt; variant.Version++;
            }
        }
        if (variantsToCreate.Count > 0)
        {
            if (existingVariants.Count + variantsToCreate.Count > 1000) return Invalid<ProductView>("variantsToCreate", "Tek ürün kaydında en fazla 1000 varyant oluşturulabilir.");
            foreach (var variant in variantsToCreate)
            {
                var variantAttributeValidation = await ValidateAttributeValuesAsync(tenantId, variant.Attributes ?? [], cancellationToken);
                if (variantAttributeValidation is not null) return ServiceResult<ProductView>.Fail(variantAttributeValidation.Code, variantAttributeValidation.Message, variantAttributeValidation.Status, variantAttributeValidation.FieldErrors);
            }
            var optionValidation = await ValidateVariantOptionsAsync(tenantId, command.CategoryId, variantsToCreate, cancellationToken);
            if (optionValidation is not null) return ServiceResult<ProductView>.Fail(optionValidation.Code, optionValidation.Message, optionValidation.Status, optionValidation.FieldErrors);
            var normalizedSkus = variantsToCreate.Select(x => Normalize(x.Sku)).ToArray();
            if (normalizedSkus.Any(string.IsNullOrWhiteSpace) || normalizedSkus.Distinct().Count() != normalizedSkus.Length) return Invalid<ProductView>("variantsToCreate", "SKU boş veya tekrarlı olamaz.");
            if (await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId && normalizedSkus.Contains(x.SkuNormalized), cancellationToken)) return Conflict<ProductView>("SKU_CONFLICT_REVIEW_REQUIRED", "SKU başka bir varyantla çakışıyor; otomatik birleştirme yapılmadı.");
            var normalizedBarcodes = variantsToCreate.Where(x => !string.IsNullOrWhiteSpace(x.Barcode)).Select(x => Normalize(x.Barcode!)).ToArray();
            if (normalizedBarcodes.Distinct().Count() != normalizedBarcodes.Length || await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId && x.BarcodeNormalized != null && normalizedBarcodes.Contains(x.BarcodeNormalized), cancellationToken)) return Conflict<ProductView>("BARCODE_CONFLICT_REVIEW_REQUIRED", "Barkod başka bir varyantla çakışıyor; otomatik birleştirme yapılmadı.");

            var globalAssignments = command.Attributes ?? await db.ProductAttributeAssignments.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == id && x.VariantId == null).Select(x => new ProductAttributeCommand(x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue, x.SortOrder)).ToListAsync(cancellationToken);
            if (command.CategoryId is Guid categoryId)
            {
                var requiredAttributeIds = await db.CategoryAttributeRequirements.AsNoTracking().Where(x => x.TenantId == tenantId && x.CategoryId == categoryId && x.IsRequired).Select(x => x.AttributeId).ToListAsync(cancellationToken);
                var globalIds = globalAssignments.Select(x => x.AttributeId).ToHashSet();
                for (var index = 0; index < variantsToCreate.Count; index++)
                {
                    var supplied = globalIds.Concat((variantsToCreate[index].Attributes ?? []).Select(x => x.AttributeId)).ToHashSet();
                    if (requiredAttributeIds.Any(requiredId => !supplied.Contains(requiredId))) return ServiceResult<ProductView>.Fail("REQUIRED_ATTRIBUTE_MISSING", $"{index + 1}. varyant için kategori zorunlu özellikleri eksik.", 422, new Dictionary<string, string[]> { ["variantsToCreate"] = [$"{index + 1}. varyant için tüm zorunlu özellikleri seçin."] });
                }
            }
            var now = timeProvider.GetUtcNow();
            var newVariantStatus = command.Status switch
            {
                "ACTIVE" => ProductStatus.Active,
                "ARCHIVED" => ProductStatus.Archived,
                "DRAFT" => ProductStatus.Draft,
                _ => product.Status
            };
            var newVariants = variantsToCreate.Select(variant => new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = id, Sku = variant.Sku.Trim(), SkuNormalized = Normalize(variant.Sku), Barcode = NullTrim(variant.Barcode), BarcodeNormalized = string.IsNullOrWhiteSpace(variant.Barcode) ? null : Normalize(variant.Barcode), ModelCode = NullTrim(variant.ModelCode), OptionSignature = Signature(variant.Options), Status = newVariantStatus, Weight = PositiveOrNull(variant.Weight), Width = PositiveOrNull(variant.Width), Height = PositiveOrNull(variant.Height), Length = PositiveOrNull(variant.Length), Desi = PositiveOrNull(variant.Desi), CreatedAt = now, UpdatedAt = now }).ToList();
            db.ProductVariants.AddRange(newVariants);
            for (var index = 0; index < newVariants.Count; index++) db.ProductAttributeAssignments.AddRange((variantsToCreate[index].Attributes ?? []).Select(x => Assignment(tenantId, id, newVariants[index].Id, x)));
            await PersistVariantOptionsAsync(tenantId, id, newVariants, variantsToCreate, cancellationToken);
            await EnsureMainInventoryAsync(tenantId, newVariants, cancellationToken);
        }
        var productStatus = command.Status == "ACTIVE" ? ProductStatus.Active : (command.Status == "ARCHIVED" ? ProductStatus.Archived : ProductStatus.Draft);
        product.Title = command.Title.Trim(); product.Description = command.Description.Trim(); product.CategoryId = command.CategoryId; product.BrandId = command.BrandId; product.Version++; product.UpdatedAt = timeProvider.GetUtcNow();
        if (command.Status != null)
        {
            product.Status = productStatus;
            foreach (var variant in existingVariants) { variant.Status = productStatus; variant.UpdatedAt = product.UpdatedAt; variant.Version++; }
        }
        if (command.Attributes is not null)
        {
            var currentAssignments = await db.ProductAttributeAssignments.Where(x => x.TenantId == tenantId && x.ProductId == id && x.VariantId == null).ToListAsync(cancellationToken);
            db.ProductAttributeAssignments.RemoveRange(currentAssignments);
            db.ProductAttributeAssignments.AddRange(command.Attributes.Select(x => Assignment(tenantId, id, null, x)));
        }
        await MarkMarketplaceLinksDirtyAsync(tenantId, id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken); return await GetProductAsync(tenantId, id, cancellationToken);
    }

    public async Task<ServiceResult<ProductView>> ArchiveProductAsync(Guid tenantId, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var product = await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (product is null) return NotFound<ProductView>(); if (product.Version != expectedVersion) return Precondition<ProductView>(product.Version); product.Status = ProductStatus.Archived; product.ArchivedAt = timeProvider.GetUtcNow(); product.UpdatedAt = product.ArchivedAt.Value; product.Version++; var variants = await db.ProductVariants.Where(x => x.TenantId == tenantId && x.ProductId == id).ToListAsync(cancellationToken); foreach (var variant in variants) { variant.Status = ProductStatus.Archived; variant.UpdatedAt = product.UpdatedAt; variant.Version++; }
        var media = await db.ProductMedia.Where(x => x.TenantId == tenantId && x.ProductId == id && x.Status != "ARCHIVED").ToListAsync(cancellationToken); foreach (var item in media) item.Status = "ARCHIVED"; var assetIds = media.Select(x => x.FileAssetId).Distinct().ToArray(); var sharedAssetIds = await db.ProductMedia.Where(x => x.TenantId == tenantId && x.ProductId != id && x.Status != "ARCHIVED" && assetIds.Contains(x.FileAssetId)).Select(x => x.FileAssetId).Distinct().ToListAsync(cancellationToken); var assets = await db.FileAssets.Where(x => x.TenantId == tenantId && assetIds.Contains(x.Id) && !sharedAssetIds.Contains(x.Id)).ToListAsync(cancellationToken); foreach (var asset in assets) { asset.Status = "ARCHIVED"; asset.ArchivedAt = product.ArchivedAt; }
        await MarkMarketplaceLinksDirtyAsync(tenantId, id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken); return await GetProductAsync(tenantId, id, cancellationToken);
    }

    private async Task MarkMarketplaceLinksDirtyAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken)
    {
        var links = await db.MarketplaceProductLinks.Where(x => x.TenantId == tenantId && x.ProductId == productId).ToListAsync(cancellationToken);
        foreach (var link in links)
        {
            link.SyncStatus = "LOCAL_CHANGES_PENDING";
            link.DirtyFieldsJson = "[\"product\"]";
            link.Version++;
        }
    }

    public async Task<ServiceResult<ListingProfileView>> GetListingProfileAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken)
    {
        var profile = await db.ChannelListingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.ConnectionId == connectionId, cancellationToken); return profile is null ? NotFound<ListingProfileView>() : ServiceResult<ListingProfileView>.Ok(MapProfile(profile));
    }

    public async Task<ServiceResult<ListingProfileView>> UpsertListingProfileAsync(Guid tenantId, Guid productId, Guid connectionId, long? expectedVersion, UpsertListingProfileCommand command, CancellationToken cancellationToken)
    {
        if (!await db.Products.AnyAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken) || !await db.PlatformConnections.AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken)) return NotFound<ListingProfileView>();
        var profile = await db.ChannelListingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.ConnectionId == connectionId, cancellationToken);
        if (profile is null)
        {
            if (expectedVersion is not null) return NotFound<ListingProfileView>(); profile = new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = productId, ConnectionId = connectionId, DesiredStatus = "DRAFT", ActualStatus = "UNKNOWN" }; db.ChannelListingProfiles.Add(profile);
        }
        else if (expectedVersion is null || profile.Version != expectedVersion) return expectedVersion is null ? ServiceResult<ListingProfileView>.Fail("PRECONDITION_REQUIRED", "If-Match gereklidir.", 428) : Precondition<ListingProfileView>(profile.Version);
        profile.TitleOverride = NullTrim(command.TitleOverride); profile.DescriptionOverride = NullTrim(command.DescriptionOverride); profile.ExternalCategoryId = NullTrim(command.ExternalCategoryId); profile.ExternalBrandId = NullTrim(command.ExternalBrandId); profile.DeliveryTimeDays = command.DeliveryTimeDays; profile.Enabled = command.Enabled; if (expectedVersion is not null) profile.Version++; await db.SaveChangesAsync(cancellationToken); return ServiceResult<ListingProfileView>.Ok(MapProfile(profile));
    }

    public async Task<ServiceResult<Guid>> EnqueuePublicationAsync(Guid tenantId, Guid productId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        if (connection is null || connection.PlatformCode != "TRENDYOL" || !IntegrationRuntimePolicy.IsManualProductWriteReady(connection)) return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Yayın için ACTIVE veya doğrulanmış STAGE Trendyol bağlantısı gerekir.", 422);
        if (!IntegrationRuntimePolicy.IsSupportedEnvironment(connection)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Yayın yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (IntegrationRuntimePolicy.IsProduction(connection) && !WritesEnabled(connection.SettingsJson)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);

        var draftResult = await new ProductPublicationComposer(db).BuildAsync(tenantId, productId, connectionId, cancellationToken);
        if (!draftResult.Succeeded) return ServiceResult<Guid>.Fail(draftResult.Error!.Code, draftResult.Error.Message, draftResult.Error.Status, draftResult.Error.FieldErrors);
        var draft = draftResult.Value!;
        var dedup = $"product-create:{connectionId:N}:{productId:N}:{draft.PayloadHash}";
        var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.ProductCreate && x.JobDedupKey == dedup, cancellationToken);
        if (existing is not null && ReusePublicationJob(existing))
        {
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<Guid>.Ok(existing.Id);
        }

        var profile = await db.ChannelListingProfiles.SingleAsync(x => x.TenantId == tenantId && x.Id == draft.ProfileId && x.ConnectionId == connectionId, cancellationToken);
        profile.ExternalCategoryId = draft.ExternalCategoryId;
        profile.ExternalBrandId = draft.ExternalBrandId;
        profile.DesiredStatus = "LIVE";
        profile.ActualStatus = "QUEUED";
        profile.LastRejectionCode = null;
        profile.Version++;

        foreach (var variant in draft.Variants)
        {
            var listing = await db.ChannelListingVariants.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProfileId == profile.Id && x.VariantId == variant.VariantId, cancellationToken);
            if (listing is null)
            {
                listing = new ChannelListingVariant { Id = Guid.CreateVersion7(), TenantId = tenantId, ProfileId = profile.Id, VariantId = variant.VariantId, DesiredStatus = "LIVE", ActualStatus = "QUEUED" };
                db.ChannelListingVariants.Add(listing);
            }
            listing.ExternalSku = variant.Sku;
            listing.ExternalBarcode = variant.Barcode;
            listing.DesiredStatus = "LIVE";
            listing.ActualStatus = "QUEUED";
            listing.RejectionCode = null;

            var state = await db.MarketplaceListingStates.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.VariantId, cancellationToken);
            if (state is null)
            {
                state = new MarketplaceListingState { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, VariantId = variant.VariantId, DesiredStatus = "LIVE", ActualStatus = "QUEUED", Version = 1 };
                db.MarketplaceListingStates.Add(state);
            }
            else state.Version++;
            state.DesiredStatus = "LIVE";
            state.ActualStatus = "QUEUED";
            state.LastRejectionCode = null;
            state.PayloadHash = draft.PayloadHash;
        }

        var jobId = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(new ProductPublicationJobPayload(jobId, productId, profile.Id, "SUBMIT", draft.PayloadHash, draft.PayloadJson, null, null));
        db.IntegrationJobs.Add(new IntegrationJob
        {
            Id = jobId,
            TenantId = tenantId,
            ConnectionId = connectionId,
            JobType = MarketplaceJobTypes.ProductCreate,
            PayloadJson = payload,
            PayloadVersion = 1,
            PayloadHash = Hash(payload),
            JobDedupKey = dedup,
            EffectIdempotencyKey = $"{dedup}:{NormalizeKey(idempotencyKey)}",
            Priority = 4,
            Status = JobStatus.Pending,
            AvailableAt = timeProvider.GetUtcNow(),
            MaxAttempts = 10,
            CorrelationId = correlationId,
            CreatedAt = timeProvider.GetUtcNow(),
            Version = 1
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<Guid>.Ok(jobId);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var concurrent = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.ProductCreate && x.JobDedupKey == dedup, cancellationToken);
            if (concurrent is not null) return ServiceResult<Guid>.Ok(concurrent.Id);
            throw;
        }
    }

    public async Task<ServiceResult<Guid>> EnqueueProductUpdateAsync(Guid tenantId, Guid productId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        if (connection is null || connection.PlatformCode != "TRENDYOL" || !IntegrationRuntimePolicy.IsManualProductWriteReady(connection)) return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Güncelleme için ACTIVE veya doğrulanmış STAGE Trendyol bağlantısı gerekir.", 422);
        if (!IntegrationRuntimePolicy.IsSupportedEnvironment(connection)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Güncelleme yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (IntegrationRuntimePolicy.IsProduction(connection) && !WritesEnabled(connection.SettingsJson)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);

        var build = await new ProductUpdateComposer(db).BuildAsync(tenantId, productId, connectionId, cancellationToken);
        if (!build.Succeeded) return ServiceResult<Guid>.Fail(build.Error!.Code, build.Error.Message, build.Error.Status, build.Error.FieldErrors);
        var draft = build.Value!;
        var dedup = $"product-update:{connectionId:N}:{productId:N}:{draft.Publication.PayloadHash}";
        var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.ProductUpdate && x.JobDedupKey == dedup, cancellationToken);
        if (existing is not null && ReusePublicationJob(existing)) { await transaction.CommitAsync(cancellationToken); return ServiceResult<Guid>.Ok(existing.Id); }

        var profile = await db.ChannelListingProfiles.SingleAsync(x => x.TenantId == tenantId && x.Id == draft.ProfileId, cancellationToken);
        profile.DesiredStatus = "LIVE"; profile.ActualStatus = "UPDATE_QUEUED"; profile.LastRejectionCode = null; profile.Version++;
        var states = await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && draft.Variants.Select(v => v.VariantId).Contains(x.VariantId)).ToListAsync(cancellationToken);
        foreach (var state in states) { state.DesiredStatus = "LIVE"; state.PayloadHash = draft.Publication.PayloadHash; state.Version++; }

        var jobId = Guid.CreateVersion7();
        var phase = draft.Publication.Mode == "APPROVED" ? "SUBMIT_CONTENT" : "SUBMIT_UNAPPROVED";
        var payload = JsonSerializer.Serialize(new ProductUpdateJobPayload(jobId, productId, profile.Id, phase, draft.Publication.Mode, draft.Publication.PayloadHash, draft.Publication.UnapprovedPayloadJson, draft.Publication.ApprovedContentPayloadJson, draft.Publication.ApprovedVariantPayloadJson, draft.Publication.ApprovedDeliveryPayloadJson, null, null));
        db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = tenantId, ConnectionId = connectionId, JobType = MarketplaceJobTypes.ProductUpdate, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = $"{dedup}:{NormalizeKey(idempotencyKey)}", Priority = 4, Status = JobStatus.Pending, AvailableAt = timeProvider.GetUtcNow(), MaxAttempts = 12, CorrelationId = correlationId, CreatedAt = timeProvider.GetUtcNow(), Version = 1 });
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return ServiceResult<Guid>.Ok(jobId);
    }

    public async Task<ServiceResult<Guid>> EnqueueProductArchiveAsync(Guid tenantId, Guid productId, Guid connectionId, bool archived, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        if (connection is null || connection.PlatformCode != "TRENDYOL" || !IntegrationRuntimePolicy.IsManualProductWriteReady(connection)) return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Arşiv işlemi için ACTIVE veya doğrulanmış STAGE Trendyol bağlantısı gerekir.", 422);
        if (!IntegrationRuntimePolicy.IsSupportedEnvironment(connection)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Arşiv işlemi yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (IntegrationRuntimePolicy.IsProduction(connection) && !WritesEnabled(connection.SettingsJson)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        var build = await new ProductArchiveComposer(db).BuildAsync(tenantId, productId, connectionId, archived, cancellationToken);
        if (!build.Succeeded) return ServiceResult<Guid>.Fail(build.Error!.Code, build.Error.Message, build.Error.Status, build.Error.FieldErrors);
        var draft = build.Value!; var dedup = $"product-archive:{connectionId:N}:{productId:N}:{archived}:{draft.PayloadHash}";
        var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.ProductArchive && x.JobDedupKey == dedup, cancellationToken);
        if (existing is not null && ReusePublicationJob(existing)) { await transaction.CommitAsync(cancellationToken); return ServiceResult<Guid>.Ok(existing.Id); }
        var profile = await db.ChannelListingProfiles.SingleAsync(x => x.TenantId == tenantId && x.Id == draft.ProfileId, cancellationToken);
        profile.DesiredStatus = archived ? "ARCHIVED" : "LIVE"; profile.ActualStatus = archived ? "ARCHIVE_QUEUED" : "UNARCHIVE_QUEUED"; profile.LastRejectionCode = null; profile.Version++;
        var listingVariants = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).ToListAsync(cancellationToken);
        foreach (var listing in listingVariants) { listing.DesiredStatus = archived ? "ARCHIVED" : "LIVE"; listing.ActualStatus = profile.ActualStatus; listing.RejectionCode = null; }
        var states = await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && draft.Variants.Select(v => v.VariantId).Contains(x.VariantId)).ToListAsync(cancellationToken);
        foreach (var state in states) { state.DesiredStatus = archived ? "ARCHIVED" : "LIVE"; state.ActualStatus = profile.ActualStatus; state.LastRejectionCode = null; state.PayloadHash = draft.PayloadHash; state.Version++; }
        var jobId = Guid.CreateVersion7(); var now = timeProvider.GetUtcNow();
        var payload = JsonSerializer.Serialize(new ProductArchiveJobPayload(jobId, productId, profile.Id, archived, "SUBMIT", draft.PayloadHash, draft.PayloadJson, null, now, now.AddHours(24)));
        db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = tenantId, ConnectionId = connectionId, JobType = MarketplaceJobTypes.ProductArchive, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = $"{dedup}:{NormalizeKey(idempotencyKey)}", Priority = 4, Status = JobStatus.Pending, AvailableAt = now, MaxAttempts = 20, CorrelationId = correlationId, CreatedAt = now, Version = 1 });
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return ServiceResult<Guid>.Ok(jobId);
    }

    public async Task<ServiceResult<PublicationStatusView>> GetPublicationStatusAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken)
    {
        if (!await db.Products.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken) || !await db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken)) return NotFound<PublicationStatusView>();
        var profile = await db.ChannelListingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.ConnectionId == connectionId, cancellationToken);
        IReadOnlyList<PublicationLineView> lines = profile is null ? Array.Empty<PublicationLineView>() : await (from listing in db.ChannelListingVariants.AsNoTracking()
                                                                                                                 join variant in db.ProductVariants.AsNoTracking() on new { listing.TenantId, listing.VariantId } equals new { variant.TenantId, VariantId = variant.Id }
                                                                                                                 where listing.TenantId == tenantId && listing.ProfileId == profile.Id
                                                                                                                 orderby variant.Sku
                                                                                                                 select new PublicationLineView(variant.Id, variant.Sku, variant.Barcode, listing.DesiredStatus, listing.ActualStatus, listing.RejectionCode)).ToListAsync(cancellationToken);
        var createPrefix = $"product-create:{connectionId:N}:{productId:N}:";
        var hasProfile = profile is not null;
        var approvalPrefix = hasProfile ? $"product-approval:{connectionId:N}:{profile!.Id:N}:" : "";
        var job = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ((x.JobType == MarketplaceJobTypes.ProductCreate && x.JobDedupKey.StartsWith(createPrefix)) || (x.JobType == MarketplaceJobTypes.ProductUpdate && x.JobDedupKey.StartsWith($"product-update:{connectionId:N}:{productId:N}:")) || (x.JobType == MarketplaceJobTypes.ProductArchive && x.JobDedupKey.StartsWith($"product-archive:{connectionId:N}:{productId:N}:")) || (hasProfile && x.JobType == MarketplaceJobTypes.ProductApprovalReconcile && x.JobDedupKey.StartsWith(approvalPrefix))))
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        return ServiceResult<PublicationStatusView>.Ok(new(productId, connectionId, profile?.Id, profile?.DesiredStatus, profile?.ActualStatus, profile?.LastRejectionCode, job?.Id, job is null ? null : JobWire(job.Status), lines));
    }

    private bool WritesEnabled(string settingsJson)
    {
        if (!configuration.GetValue<bool>("FeatureFlags:ExternalWrites")) return false;
        try { using var document = JsonDocument.Parse(settingsJson); return document.RootElement.TryGetProperty("ExternalWritesEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True; }
        catch (JsonException) { return false; }
    }

    private static string NormalizeKey(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool ReusePublicationJob(IntegrationJob job) => job.Status is not JobStatus.Blocked and not JobStatus.Cancelled;
    private static string JobWire(JobStatus value) => value switch { JobStatus.RetryScheduled => "RETRY_SCHEDULED", JobStatus.ManualReview => "MANUAL_REVIEW", _ => value.ToString().ToUpperInvariant() };

    private async Task<IReadOnlyList<ProductView>> BuildProductViewsAsync(Guid tenantId, IReadOnlyList<Product> products, IReadOnlyList<ProductVariant> variants, CancellationToken cancellationToken)
    {
        if (products.Count == 0) return [];
        var productIds = products.Select(x => x.Id).ToArray();
        var variantIds = variants.Select(x => x.Id).ToArray();
        var inventories = await db.InventoryItems.AsNoTracking().Where(x => x.TenantId == tenantId && variantIds.Contains(x.VariantId) && x.LocationCode == "MAIN").ToListAsync(cancellationToken);
        var offers = await db.ChannelOffers.AsNoTracking().Where(x => x.TenantId == tenantId && variantIds.Contains(x.VariantId)).OrderByDescending(x => x.Status == "ACTIVE").ThenBy(x => x.Id).ToListAsync(cancellationToken);
        var profiles = await db.ChannelListingProfiles.AsNoTracking().Where(x => x.TenantId == tenantId && productIds.Contains(x.ProductId) && x.Enabled
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && connection.Status != "HIDDEN")).ToListAsync(cancellationToken);
        var productAttributes = await db.ProductAttributeAssignments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && productIds.Contains(x.ProductId) && x.VariantId == null)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);
        var productOptions = await db.ProductOptions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && productIds.Contains(x.ProductId))
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);
        var optionIds = productOptions.Select(x => x.Id).ToArray();
        var optionValues = await db.ProductOptionValues.AsNoTracking()
            .Where(x => x.TenantId == tenantId && optionIds.Contains(x.OptionId))
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);
        var connectionIds = profiles.Select(x => x.ConnectionId).Distinct().ToArray();
        var connections = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && connectionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, cancellationToken);
        var media = await (from item in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on new { item.TenantId, item.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                           where item.TenantId == tenantId && productIds.Contains(item.ProductId) && item.Status == "ACTIVE" && asset.Status == "ACTIVE" && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                           orderby item.SortOrder
                           select new { item.ProductId, item.VariantId, asset.Id, asset.Classification, Url = asset.RelativePath }).ToListAsync(cancellationToken);
        var mediaUrlsByVariant = media.Where(x => x.VariantId is not null)
            .GroupBy(x => x.VariantId!.Value)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(MediaUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        var globalMediaUrlsByProduct = media.Where(x => x.VariantId is null)
            .GroupBy(x => x.ProductId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(MediaUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        var inventoryByVariant = inventories.GroupBy(x => x.VariantId).ToDictionary(x => x.Key, x => x.First());
        var offerByVariant = offers.GroupBy(x => x.VariantId).ToDictionary(x => x.Key, x => x.First());
        return products.Select(product =>
        {
            // Product variants are inserted in the marketplace response order.
            // Sorting by SKU made the gallery jump between colours (for example
            // black, green, black) even though Trendyol sent each colour as a
            // contiguous block.
            var productVariants = variants.Where(x => x.ProductId == product.Id)
                .OrderBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .ToList();
            var variantViews = productVariants.Select(variant =>
            {
                inventoryByVariant.TryGetValue(variant.Id, out var inventory); offerByVariant.TryGetValue(variant.Id, out var offer);
                return new ProductVariantView(variant.Id, variant.Sku, variant.Barcode, variant.ModelCode, variant.OptionSignature, variant.Status.ToString().ToUpperInvariant(), variant.Version, variant.Weight, variant.Width, variant.Height, variant.Length, variant.Desi, inventory?.OnHand ?? 0, inventory?.Available ?? 0, inventory?.Version, offer?.Id, offer?.ListPrice, offer?.SalePrice, offer?.Currency, offer?.Status, offer?.PriceVersion, offer?.Version, offer?.VatRate, offer?.VatInclusion, offer?.RoundingMode, offer?.SafetyStock, mediaUrlsByVariant.GetValueOrDefault(variant.Id));
            }).ToList();
            var activePlatforms = profiles.Where(x => x.ProductId == product.Id).Select(x => connections.GetValueOrDefault(x.ConnectionId, "Platform")).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            var image = media.Where(x => x.ProductId == product.Id && x.VariantId == null).Select(MediaUrl).FirstOrDefault() ?? media.Where(x => x.ProductId == product.Id).Select(MediaUrl).FirstOrDefault();
            var prices = variantViews.Where(x => x.SalePrice is not null).Select(x => x.SalePrice!.Value).ToList();
            var currency = variantViews.Select(x => x.Currency).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "TRY";
            var modelCode = variantViews.Select(x => x.ModelCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            var attributes = productAttributes.Where(x => x.ProductId == product.Id)
                .Select(x => new ProductAttributeAssignmentView(x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue, x.SortOrder))
                .ToList();
            var options = productOptions.Where(x => x.ProductId == product.Id)
                .Select(option => new ProductOptionView(option.Id, option.Label, optionValues.Where(value => value.OptionId == option.Id).Select(value => new ProductOptionValueView(value.Id, value.Label)).ToList()))
                .ToList();
            return new ProductView(product.Id, product.Title, product.Description, product.BrandId, product.CategoryId, product.Status.ToString().ToUpperInvariant(), product.UpdatedAt, product.Version, variantViews, image, variantViews.Sum(x => x.OnHand), prices.Count > 0 ? prices.Min() : null, currency, modelCode, activePlatforms, attributes, options, ProductMediaForView(variantViews, globalMediaUrlsByProduct.GetValueOrDefault(product.Id)));
        }).ToList();

        static string MediaUrl(dynamic item) => item.Classification == "PRODUCT_MEDIA_URL" ? item.Url : $"/api/v1/files/product-media/{item.Id:D}/content";
    }

    private static IReadOnlyList<string> ProductMediaForView(IReadOnlyList<ProductVariantView> variants, IReadOnlyList<string>? globalMedia)
    {
        var fallback = globalMedia ?? [];
        var hasColor = variants.Any(variant => ColorOptionValue(variant.OptionSignature) is not null);
        if (!hasColor) return fallback;

        var selected = new List<string>();
        var seenColors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variant in variants)
        {
            var color = ColorOptionValue(variant.OptionSignature);
            if (color is null || !seenColors.Add(color)) continue;
            foreach (var image in variant.MediaUrls ?? [])
                if (!string.IsNullOrWhiteSpace(image) && !selected.Contains(image, StringComparer.OrdinalIgnoreCase)) selected.Add(image);
        }

        return selected.Count > 0 ? selected : fallback.Take(1).ToList();
    }

    private static string? ColorOptionValue(string optionSignature)
    {
        foreach (var part in optionSignature.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf(':');
            if (separator < 0) separator = part.IndexOf('=');
            if (separator < 0) continue;
            var key = part[..separator].Replace(" ", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
            if (key is "RENK" or "WEBCOLOR" or "COLOR" or "COLOUR")
            {
                var value = part[(separator + 1)..].Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value.ToUpperInvariant();
            }
        }

        return null;
    }

    private static decimal? PositiveOrNull(decimal? value) => value is > 0 ? value : null;

    private async Task<ServiceError?> ValidateProductReferencesAsync(Guid tenantId, string title, Guid? categoryId, Guid? brandId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 320) return new("PRODUCT_VALIDATION_FAILED", "Ürün başlığı geçersizdir.", 422, new Dictionary<string, string[]> { ["title"] = ["Başlık zorunludur ve en fazla 320 karakterdir."] });
        if (categoryId is Guid category && !await db.Categories.AnyAsync(x => x.TenantId == tenantId && x.Id == category && x.IsActive && x.IsLeaf, cancellationToken)) return new("CATEGORY_LEAF_REQUIRED", "Yalnız etkin yaprak kategori seçilebilir.", 422, new Dictionary<string, string[]> { ["categoryId"] = ["Kategori etkin bir yaprak olmalıdır."] });
        if (brandId is Guid brand && !await db.Brands.AnyAsync(x => x.TenantId == tenantId && x.Id == brand && x.IsActive, cancellationToken)) return new("BRAND_ACTIVE_REQUIRED", "Seçilen marka etkin değildir.", 422, new Dictionary<string, string[]> { ["brandId"] = ["Etkin marka seçin."] }); return null;
    }

    private async Task<ServiceError?> ValidateAttributesAsync(Guid tenantId, Guid? categoryId, IReadOnlyList<ProductAttributeCommand> assignments, CancellationToken cancellationToken)
    {
        var valueValidation = await ValidateAttributeValuesAsync(tenantId, assignments, cancellationToken);
        if (valueValidation is not null) return valueValidation;
        if (categoryId is Guid category)
        {
            var required = await db.CategoryAttributeRequirements.AsNoTracking().Where(x => x.TenantId == tenantId && x.CategoryId == category && x.IsRequired).Select(x => x.AttributeId).ToListAsync(cancellationToken);
            var supplied = assignments.Select(x => x.AttributeId).ToHashSet();
            if (required.Any(id => !supplied.Contains(id))) return new("REQUIRED_ATTRIBUTE_MISSING", "Kategori için zorunlu özellikler eksik.", 422, new Dictionary<string, string[]> { ["attributes"] = ["Tüm zorunlu kategori özelliklerini girin."] });
        }
        return null;
    }

    private async Task<ServiceError?> ValidateAttributeValuesAsync(Guid tenantId, IReadOnlyList<ProductAttributeCommand> assignments, CancellationToken cancellationToken)
    {
        var ids = assignments.Select(x => x.AttributeId).Distinct().ToArray();
        var definitions = await db.AttributeDefinitions.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.Id) && x.IsActive).ToDictionaryAsync(x => x.Id, cancellationToken);
        if (definitions.Count != ids.Length) return new("ATTRIBUTE_INVALID", "Etkin olmayan veya bulunmayan ürün özelliği var.", 422, new Dictionary<string, string[]> { ["attributes"] = ["Tüm özellikler etkin ve aynı tenant içinde olmalıdır."] });
        foreach (var group in assignments.GroupBy(x => x.AttributeId))
        {
            var definition = definitions[group.Key];
            if (definition.DataType != AttributeDataType.MultiSelect && group.Count() > 1) return new("ATTRIBUTE_ASSIGNMENT_AMBIGUOUS", $"'{definition.Name}' yalnız bir değer kabul eder.", 422);
            var selectedValues = group.Where(x => x.ValueId is not null).Select(x => x.ValueId!.Value).ToList();
            if (selectedValues.Distinct().Count() != selectedValues.Count) return new("ATTRIBUTE_VALUE_DUPLICATE", $"'{definition.Name}' içinde aynı değer tekrarlanamaz.", 422);
        }
        foreach (var assignment in assignments)
        {
            var textValue = string.IsNullOrWhiteSpace(assignment.TextValue) ? null : assignment.TextValue;
            var count = new object?[] { assignment.ValueId, textValue, assignment.NumberValue, assignment.BooleanValue }.Count(x => x is not null);
            if (count != 1) return new("ATTRIBUTE_TYPED_VALUE_REQUIRED", "Her özellik ataması tam bir tipli değer ister.", 422, new Dictionary<string, string[]> { ["attributes"] = ["valueId, textValue, numberValue, booleanValue alanlarından tam biri dolu olmalıdır."] });
            var definition = definitions[assignment.AttributeId];
            var typeMatches = definition.DataType switch { AttributeDataType.Text => textValue is not null, AttributeDataType.Number => assignment.NumberValue is not null, AttributeDataType.Boolean => assignment.BooleanValue is not null, AttributeDataType.SingleSelect or AttributeDataType.MultiSelect => assignment.ValueId is not null, _ => false };
            if (!typeMatches) return new("ATTRIBUTE_TYPE_MISMATCH", "Özellik değeri tanımlı veri tipiyle eşleşmiyor.", 422);
            if (assignment.ValueId is Guid valueId && !await db.AttributeValues.AnyAsync(x => x.TenantId == tenantId && x.AttributeId == assignment.AttributeId && x.Id == valueId && x.IsActive, cancellationToken)) return new("ATTRIBUTE_VALUE_INVALID", "Seçim değeri özelliğe ait veya etkin değil.", 422);
        }
        return null;
    }

    private async Task<ServiceError?> ValidateVariantOptionsAsync(Guid tenantId, Guid? categoryId, IReadOnlyList<CreateVariantCommand> variants, CancellationToken cancellationToken)
    {
        var keys = variants.SelectMany(x => x.Options?.Keys ?? Array.Empty<string>())
            .Select(Normalize)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (keys.Length > 2) return new("OPTION_LIMIT_EXCEEDED", "Bir ürün en fazla 2 seçenek grubu kullanabilir.", 422, new Dictionary<string, string[]> { ["variants"] = ["Seçenek gruplarını en fazla 2 başlıkla sınırlayın."] });
        if (categoryId is not Guid category) return null;
        var configuredOptionNames = await (from requirement in db.CategoryAttributeRequirements.AsNoTracking()
                                           join attribute in db.AttributeDefinitions.AsNoTracking() on new { requirement.TenantId, requirement.AttributeId } equals new { attribute.TenantId, AttributeId = attribute.Id }
                                           where requirement.TenantId == tenantId && requirement.CategoryId == category && requirement.Role == "OPTION"
                                           select attribute.Name).ToListAsync(cancellationToken);
        if (configuredOptionNames.Count > 0)
        {
            var allowed = configuredOptionNames.Select(Normalize).ToHashSet(StringComparer.Ordinal);
            if (keys.Any(key => !allowed.Contains(key))) return new("OPTION_MAPPING_REQUIRED", "Ürün seçenekleri kategori için Seçenek Eşitleme bölümünde tanımlanan başlıklardan seçilmelidir.", 422, new Dictionary<string, string[]> { ["variants"] = ["Seçenek başlıklarını önce kategori eşlemesinde OPTION olarak işaretleyin."] });
        }
        return null;
    }

    private async Task PersistVariantOptionsAsync(Guid tenantId, Guid productId, IReadOnlyList<ProductVariant> variants, IReadOnlyList<CreateVariantCommand> commands, CancellationToken cancellationToken)
    {
        var optionRows = commands.SelectMany((command, index) => (command.Options ?? new Dictionary<string, string>()).Select(pair => (command, index, pair.Key, pair.Value)))
            .Where(row => !string.IsNullOrWhiteSpace(row.Key) && !string.IsNullOrWhiteSpace(row.Value))
            .GroupBy(row => Normalize(row.Key), StringComparer.Ordinal)
            .Take(2)
            .ToList();
        foreach (var optionGroup in optionRows)
        {
            var first = optionGroup.First();
            var option = db.ProductOptions.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == productId && x.NormalizedKey == optionGroup.Key)
                ?? await db.ProductOptions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.NormalizedKey == optionGroup.Key, cancellationToken);
            if (option is null)
            {
                option = new ProductOption { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = productId, Label = first.Key.Trim(), NormalizedKey = optionGroup.Key, SortOrder = optionRows.IndexOf(optionGroup) };
                db.ProductOptions.Add(option);
            }
            foreach (var row in optionGroup)
            {
                var valueKey = Normalize(row.Value);
                var optionValue = db.ProductOptionValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.OptionId == option.Id && x.NormalizedKey == valueKey)
                    ?? await db.ProductOptionValues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.OptionId == option.Id && x.NormalizedKey == valueKey, cancellationToken);
                if (optionValue is null)
                {
                    optionValue = new ProductOptionValue { Id = Guid.CreateVersion7(), TenantId = tenantId, OptionId = option.Id, Label = row.Value.Trim(), NormalizedKey = valueKey, SortOrder = optionGroup.Select(x => Normalize(x.Value)).Distinct(StringComparer.Ordinal).ToList().IndexOf(valueKey) };
                    db.ProductOptionValues.Add(optionValue);
                }
                var variant = variants[row.index];
                var variantOption = db.VariantOptionValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.VariantId == variant.Id && x.OptionId == option.Id)
                    ?? await db.VariantOptionValues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variant.Id && x.OptionId == option.Id, cancellationToken);
                if (variantOption is null)
                    db.VariantOptionValues.Add(new VariantOptionValue { Id = Guid.CreateVersion7(), TenantId = tenantId, VariantId = variant.Id, OptionId = option.Id, OptionValueId = optionValue.Id });
                else variantOption.OptionValueId = optionValue.Id;
            }
        }
    }

    private static ProductAttributeAssignment Assignment(Guid tenantId, Guid productId, Guid? variantId, ProductAttributeCommand value) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ProductId = productId,
        VariantId = variantId,
        AttributeId = value.AttributeId,
        ValueId = value.ValueId,
        TextValue = string.IsNullOrWhiteSpace(value.TextValue) ? null : value.TextValue.Trim(),
        NumberValue = value.NumberValue,
        BooleanValue = value.BooleanValue,
        SortOrder = value.SortOrder
    };

    private async Task EnsureMainInventoryAsync(Guid tenantId, IEnumerable<ProductVariant> variants, CancellationToken cancellationToken)
    {
        var location = await db.InventoryLocations.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Code == "MAIN", cancellationToken);
        if (location is null) { location = new InventoryLocation { Id = Guid.CreateVersion7(), TenantId = tenantId, Code = "MAIN", Name = "Ana Depo", Status = "ACTIVE", Priority = 1 }; db.InventoryLocations.Add(location); }
        foreach (var variant in variants) db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenantId, VariantId = variant.Id, LocationCode = "MAIN", Available = 0 });
    }

    private async Task<HashSet<Guid>> DescendantIdsAsync(Guid tenantId, Guid parentId, CancellationToken cancellationToken)
    {
        var all = await db.Categories.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => new { x.Id, x.ParentId }).ToListAsync(cancellationToken); var result = new HashSet<Guid>(); var pending = new Queue<Guid>(); pending.Enqueue(parentId); while (pending.TryDequeue(out var id)) foreach (var child in all.Where(x => x.ParentId == id)) if (result.Add(child.Id)) pending.Enqueue(child.Id); return result;
    }

    private async Task RebuildDescendantPathsAsync(Category root, CancellationToken cancellationToken)
    {
        var all = await db.Categories.Where(x => x.TenantId == root.TenantId).ToListAsync(cancellationToken); var queue = new Queue<Category>(); queue.Enqueue(root); while (queue.TryDequeue(out var parent)) foreach (var child in all.Where(x => x.ParentId == parent.Id)) { child.Path = $"{parent.Path} / {child.Name}"; child.Depth = parent.Depth + 1; child.Version++; child.UpdatedAt = timeProvider.GetUtcNow(); queue.Enqueue(child); }
    }

    private async Task RefreshLeafAsync(Guid tenantId, Guid? id, CancellationToken cancellationToken)
    {
        if (id is not Guid categoryId) return; var parent = await db.Categories.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == categoryId, cancellationToken); if (parent is not null) parent.IsLeaf = !await db.Categories.AnyAsync(x => x.TenantId == tenantId && x.ParentId == categoryId && x.IsActive, cancellationToken);
    }

    private IQueryable<Product> VisibleProducts(Guid tenantId) => db.Products.AsNoTracking().Where(product =>
        product.TenantId == tenantId
        && (!db.MarketplaceProductLinks.Any(link => link.TenantId == tenantId && link.ProductId == product.Id)
            || db.MarketplaceProductLinks.Any(link => link.TenantId == tenantId && link.ProductId == product.Id
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == link.ConnectionId && connection.Status != "HIDDEN"))));

    private void ApplyProductFilters(ref IQueryable<Product> query, Guid tenantId, string? status, string? search, string? platform, string? stock)
    {
        if (!string.IsNullOrWhiteSpace(status) && TryProductStatus(status, out var parsed))
            query = query.Where(x => x.Status == parsed);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(product =>
                EF.Functions.ILike(product.Title, pattern) ||
                db.ProductVariants.Any(variant => variant.TenantId == tenantId && variant.ProductId == product.Id &&
                    (EF.Functions.ILike(variant.Sku, pattern) ||
                     (variant.Barcode != null && EF.Functions.ILike(variant.Barcode, pattern)) ||
                     (variant.ModelCode != null && EF.Functions.ILike(variant.ModelCode, pattern)))));
        }

        if (!string.IsNullOrWhiteSpace(platform))
        {
            var platformName = platform.Trim();
            query = query.Where(product => db.ChannelListingProfiles.Any(profile =>
                profile.TenantId == tenantId && profile.ProductId == product.Id && profile.Enabled &&
                db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == profile.ConnectionId && connection.Status != "HIDDEN" && connection.DisplayName == platformName)));
        }

        var stockTotals = db.ProductVariants.AsNoTracking()
            .Where(variant => variant.TenantId == tenantId)
            .Select(variant => new
            {
                variant.ProductId,
                TotalStock = db.InventoryItems.AsNoTracking()
                    .Where(inventory => inventory.TenantId == tenantId && inventory.VariantId == variant.Id && inventory.LocationCode == "MAIN")
                    .Select(inventory => (decimal?)inventory.OnHand)
                    .FirstOrDefault() ?? 0
            })
            .GroupBy(value => value.ProductId)
            .Select(group => new { ProductId = group.Key, TotalStock = group.Sum(value => value.TotalStock) });

        switch (stock?.Trim().ToUpperInvariant())
        {
            case "OUT":
                query = query.Where(product => !stockTotals.Any(total => total.ProductId == product.Id && total.TotalStock > 0));
                break;
            case "LOW":
                query = query.Where(product => stockTotals.Any(total => total.ProductId == product.Id && total.TotalStock > 0 && total.TotalStock <= 5));
                break;
            case "OK":
                query = query.Where(product => stockTotals.Any(total => total.ProductId == product.Id && total.TotalStock > 5));
                break;
        }
    }

    private Guid Decode(string? cursor) => cursors.TryDecode(cursor, out var id) ? id : throw new ArgumentException("Cursor geçersiz veya süresi dolmuş.", nameof(cursor));
    private PageResult<TView> Page<TEntity, TView>(List<TEntity> rows, int limit, Func<TEntity, TView> map) where TEntity : class { var hasMore = rows.Count > limit; var items = rows.Take(limit).Select(map).ToList(); var next = hasMore ? cursors.Encode((Guid)typeof(TEntity).GetProperty("Id")!.GetValue(rows[limit - 1])!) : null; return new(items, next, hasMore); }
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static string NormalizeRequirementRole(string? value) => string.Equals(value?.Trim(), "OPTION", StringComparison.OrdinalIgnoreCase) ? "OPTION" : "ATTRIBUTE";
    private static string? NullTrim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Signature(IReadOnlyDictionary<string, string>? options) => options is null || options.Count == 0 ? "-" : string.Join('|', options.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{Normalize(x.Key)}={Normalize(x.Value)}"));
    private static bool TryAttributeType(string value, out AttributeDataType result) { result = value.Trim().ToUpperInvariant() switch { "TEXT" => AttributeDataType.Text, "NUMBER" => AttributeDataType.Number, "SINGLE_SELECT" => AttributeDataType.SingleSelect, "MULTI_SELECT" => AttributeDataType.MultiSelect, "BOOLEAN" => AttributeDataType.Boolean, _ => (AttributeDataType)(-1) }; return Enum.IsDefined(result); }
    private static bool TryProductStatus(string value, out ProductStatus result) => Enum.TryParse(value, true, out result);
    private static CategoryView MapCategory(Category value) => new(value.Id, value.ParentId, value.Name, value.Path, value.Depth, value.IsLeaf, value.IsActive, value.Version);
    private static BrandView MapBrand(Brand value) => new(value.Id, value.Name, value.IsActive, value.Version);
    private static AttributeView MapAttribute(AttributeDefinition value, IEnumerable<AttributeValue> values, IReadOnlyList<string>? roles = null) => new(value.Id, value.Code, value.Name, value.DataType switch { AttributeDataType.SingleSelect => "SINGLE_SELECT", AttributeDataType.MultiSelect => "MULTI_SELECT", _ => value.DataType.ToString().ToUpperInvariant() }, value.SelectionMode, value.Unit, value.IsActive, value.Version, values.Select(x => new AttributeValueView(x.Id, x.Value, x.SortOrder, x.IsActive)).ToList(), roles);
    private static ProductVariantView MapVariant(ProductVariant value) => new(value.Id, value.Sku, value.Barcode, value.ModelCode, value.OptionSignature, value.Status.ToString().ToUpperInvariant(), value.Version);
    private static ProductView MapProduct(Product value, IEnumerable<ProductVariant> variants) => new(value.Id, value.Title, value.Description, value.BrandId, value.CategoryId, value.Status.ToString().ToUpperInvariant(), value.UpdatedAt, value.Version, variants.Select(MapVariant).ToList());
    private static ListingProfileView MapProfile(ChannelListingProfile value) => new(value.Id, value.ProductId, value.ConnectionId, value.TitleOverride, value.DescriptionOverride, value.ExternalCategoryId, value.ExternalBrandId, value.DeliveryTimeDays, value.Enabled, value.DesiredStatus, value.ActualStatus, value.Version);
    private static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Conflict<T>(string code, string message) => ServiceResult<T>.Fail(code, message, 409);
    private static ServiceResult<T> Precondition<T>(long version) => ServiceResult<T>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{version}.", 412);
}
