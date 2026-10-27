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
        var keys = LookupKeys(name);
        var existing = await db.Categories.Where(x => x.TenantId == tenantId && x.ParentId == command.ParentId && keys.Contains(x.NormalizedName)).OrderBy(x => x.NormalizedName == normalized ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
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
        var normalized = Normalize(command.Name); var nameKeys = LookupKeys(command.Name); if (await db.Categories.AnyAsync(x => x.TenantId == tenantId && x.Id != id && x.ParentId == command.ParentId && nameKeys.Contains(x.NormalizedName), cancellationToken)) return Conflict<CategoryView>("CATEGORY_DUPLICATE", "Aynı üst kategoride bu ad zaten var.");
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
        var name = command.Name.Trim(); if (name.Length is < 1 or > 160) return Invalid<BrandView>("name", "Marka adı 1-160 karakter olmalıdır."); var normalized = Normalize(name); var keys = LookupKeys(name);
        if (await db.Brands.AnyAsync(x => x.TenantId == tenantId && keys.Contains(x.NormalizedName), cancellationToken)) return Conflict<BrandView>("BRAND_DUPLICATE", "Bu marka zaten var.");
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
        var normalized = Normalize(command.Name); var nameKeys = LookupKeys(command.Name); if (await db.Brands.AnyAsync(x => x.TenantId == tenantId && x.Id != id && nameKeys.Contains(x.NormalizedName), cancellationToken)) return Conflict<BrandView>("BRAND_DUPLICATE", "Bu marka zaten var.");
        brand.Name = command.Name.Trim(); brand.NormalizedName = normalized; brand.IsActive = command.IsActive; brand.UpdatedAt = timeProvider.GetUtcNow(); brand.Version++; await db.SaveChangesAsync(cancellationToken); return ServiceResult<BrandView>.Ok(MapBrand(brand));
    }

    public async Task<PageResult<AttributeView>> ListAttributesAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken)
    {
        var afterId = Decode(after); var query = db.AttributeDefinitions.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive); if (afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0);
        var rows = await query.OrderBy(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken); var ids = rows.Take(limit).Select(x => x.Id).ToArray(); var values = await db.AttributeValues.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.AttributeId)).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        var roleRows = await db.CategoryAttributeRequirements.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.AttributeId) && (x.IsPanelScoped || x.Role == "OPTION")).Select(x => new { x.AttributeId, x.Role }).ToListAsync(cancellationToken);
        var roles = roleRows.GroupBy(x => x.AttributeId).ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(x => NormalizeRequirementRole(x.Role)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList());
        return Page(rows, limit, attribute => MapAttribute(attribute, values.Where(x => x.AttributeId == attribute.Id), roles.GetValueOrDefault(attribute.Id)));
    }

    public async Task<ServiceResult<AttributeView>> CreateAttributeAsync(Guid tenantId, CreateAttributeCommand command, CancellationToken cancellationToken)
    {
        if (!TryAttributeType(command.DataType, out var dataType)) return Invalid<AttributeView>("dataType", "İzinli tipler TEXT, NUMBER, SINGLE_SELECT, MULTI_SELECT ve BOOLEAN'dır.");
        var code = NormalizeCode(command.Code); if (code.Length is < 1 or > 96) return Invalid<AttributeView>("code", "Özellik kodu geçersizdir.");
        var requestedValues = command.Values
            .Select(value => new { Value = value.Value.Trim(), NormalizedValue = NormalizeValue(value.Value), value.SortOrder })
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
                var previousValue = existingValues.FirstOrDefault(value => CatalogKeyNormalizer.SameKey(value.NormalizedValue, requestedValue.NormalizedValue, 320));
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

    public async Task<ServiceResult<AttributeView>> RenameAttributeAsync(Guid tenantId, Guid attributeId, long expectedVersion, RenameAttributeCommand command, CancellationToken cancellationToken)
    {
        var name = command.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 160) return Invalid<AttributeView>("name", "Özellik adı 1-160 karakter olmalıdır.");
        var attribute = await db.AttributeDefinitions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == attributeId && x.IsActive, cancellationToken);
        if (attribute is null) return NotFound<AttributeView>();
        if (attribute.Version != expectedVersion) return Precondition<AttributeView>(attribute.Version);
        var key = Normalize(name);
        var others = await db.AttributeDefinitions.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive && x.Id != attributeId).Select(x => x.Name).ToListAsync(cancellationToken);
        if (others.Any(other => Normalize(other) == key)) return Conflict<AttributeView>("ATTRIBUTE_NAME_DUPLICATE", "Bu adda başka bir özellik var.");
        if (attribute.Name != name)
        {
            attribute.Name = name;
            attribute.Version++;
            attribute.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
        var values = await db.AttributeValues.AsNoTracking().Where(x => x.TenantId == tenantId && x.AttributeId == attributeId && x.IsActive).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        return ServiceResult<AttributeView>.Ok(MapAttribute(attribute, values));
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
        var normalized = values.Select(x => NormalizeValue(x.Value)).ToArray();
        if (normalized.Length == 0 || normalized.Any(string.IsNullOrWhiteSpace) || normalized.Distinct().Count() != normalized.Length) return Invalid<AttributeView>("values", "En az bir benzersiz ve boş olmayan seçenek değeri girin.");
        var existing = await db.AttributeValues.Where(x => x.TenantId == tenantId && x.AttributeId == attributeId).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        if (existing.Any(x => x.IsActive && normalized.Any(key => CatalogKeyNormalizer.SameKey(x.NormalizedValue, key, 320)))) return Conflict<AttributeView>("ATTRIBUTE_VALUE_DUPLICATE", "Seçenek değerlerinden biri bu özellikte zaten var.");
        var nextSort = existing.Count == 0 ? 0 : existing.Max(x => x.SortOrder) + 1;
        var now = timeProvider.GetUtcNow();
        var additions = new List<AttributeValue>();
        foreach (var (value, index) in values.Select((value, index) => (value, index)))
        {
            var normalizedValue = normalized[index];
            var inactiveValue = existing.FirstOrDefault(item => !item.IsActive && CatalogKeyNormalizer.SameKey(item.NormalizedValue, normalizedValue, 320));
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

    // Values of a panel attribute that products actually use: as a product/variant attribute, or as a
    // variant option of the same name (e.g. option "Renk" with value "Siyah" for attribute Renk).
    // The value mapping screen lists these first, so a 120-value attribute shows the 14 in use.
    public async Task<ServiceResult<IReadOnlyList<Guid>>> UsedAttributeValueIdsAsync(Guid tenantId, Guid attributeId, CancellationToken cancellationToken)
    {
        var attribute = await db.AttributeDefinitions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == attributeId && x.IsActive, cancellationToken);
        if (attribute is null) return ServiceResult<IReadOnlyList<Guid>>.Fail("ATTRIBUTE_NOT_FOUND", "Özellik bulunamadı.", 404);
        var assigned = await db.ProductAttributeAssignments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.AttributeId == attributeId && x.ValueId != null)
            .Select(x => x.ValueId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        var optionNameKeys = LookupKeys(attribute.Name);
        var optionValueKeys = await (from option in db.ProductOptions.AsNoTracking()
                                     join optionValue in db.ProductOptionValues.AsNoTracking() on new { option.TenantId, OptionId = option.Id } equals new { optionValue.TenantId, optionValue.OptionId }
                                     where option.TenantId == tenantId && optionNameKeys.Contains(option.NormalizedKey)
                                     select optionValue.NormalizedKey)
            .Distinct()
            .ToListAsync(cancellationToken);
        var assignedIds = assigned.ToHashSet();
        // Option values and attribute values may be stored in the shared or the legacy form; compare both
        // as shared keys.
        var optionKeys = optionValueKeys.Select(value => CatalogKeyNormalizer.Key(value)).ToHashSet(StringComparer.Ordinal);
        var used = (await db.AttributeValues.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.AttributeId == attributeId && x.IsActive)
                .Select(x => new { x.Id, x.NormalizedValue })
                .ToListAsync(cancellationToken))
            .Where(value => assignedIds.Contains(value.Id) || optionKeys.Contains(CatalogKeyNormalizer.Key(value.NormalizedValue)))
            .Select(value => value.Id)
            .ToList();
        return ServiceResult<IReadOnlyList<Guid>>.Ok(used);
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

    public async Task<ServiceResult<AttributeView>> UpdateAttributeValueAsync(Guid tenantId, Guid attributeId, Guid valueId, long expectedVersion, UpdateAttributeValueCommand command, CancellationToken cancellationToken)
    {
        var attribute = await db.AttributeDefinitions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == attributeId && x.IsActive, cancellationToken);
        if (attribute is null) return ServiceResult<AttributeView>.Fail("ATTRIBUTE_NOT_FOUND", "Özellik bulunamadı.", 404);
        if (attribute.Version != expectedVersion) return Precondition<AttributeView>(attribute.Version);
        var normalized = NormalizeValue(command.Value);
        if (string.IsNullOrWhiteSpace(normalized)) return Invalid<AttributeView>("value", "Değer boş olamaz.");
        var value = await db.AttributeValues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.AttributeId == attributeId && x.Id == valueId && x.IsActive, cancellationToken);
        if (value is null) return ServiceResult<AttributeView>.Fail("ATTRIBUTE_VALUE_NOT_FOUND", "Seçenek değeri bulunamadı.", 404);
        var valueKeys = LookupKeys(command.Value, 320);
        if (await db.AttributeValues.AnyAsync(x => x.TenantId == tenantId && x.AttributeId == attributeId && x.IsActive && x.Id != valueId && valueKeys.Contains(x.NormalizedValue), cancellationToken))
            return Conflict<AttributeView>("ATTRIBUTE_VALUE_DUPLICATE", "Bu özellikte aynı değer zaten var.");
        value.Value = command.Value.Trim();
        value.NormalizedValue = normalized;
        value.SortOrder = command.SortOrder;
        attribute.Version++;
        attribute.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        var values = await db.AttributeValues.AsNoTracking().Where(x => x.TenantId == tenantId && x.AttributeId == attributeId).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return ServiceResult<AttributeView>.Ok(MapAttribute(attribute, values));
    }

    public async Task<ServiceResult<AttributeView>> ReorderAttributeValuesAsync(Guid tenantId, Guid attributeId, long expectedVersion, ReorderAttributeValuesCommand command, CancellationToken cancellationToken)
    {
        var attribute = await db.AttributeDefinitions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == attributeId && x.IsActive, cancellationToken);
        if (attribute is null) return ServiceResult<AttributeView>.Fail("ATTRIBUTE_NOT_FOUND", "Özellik bulunamadı.", 404);
        if (attribute.Version != expectedVersion) return Precondition<AttributeView>(attribute.Version);

        var values = await db.AttributeValues
            .Where(x => x.TenantId == tenantId && x.AttributeId == attributeId && x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var requestedIds = command.ValueIds?.ToArray() ?? [];
        if (requestedIds.Length != values.Count || requestedIds.Distinct().Count() != requestedIds.Length || values.Any(value => !requestedIds.Contains(value.Id)))
            return Invalid<AttributeView>("valueIds", "Değer sıralaması bu özelliğin etkin değerlerinin tamamını içermelidir.");

        var valuesById = values.ToDictionary(value => value.Id);
        for (var index = 0; index < requestedIds.Length; index++) valuesById[requestedIds[index]].SortOrder = index;
        attribute.Version++;
        attribute.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        var result = await db.AttributeValues.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.AttributeId == attributeId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return ServiceResult<AttributeView>.Ok(MapAttribute(attribute, result));
    }

    public async Task<ServiceResult<IReadOnlyList<CategoryAttributeRequirementView>>> GetRequirementsAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken)
    {
        if (!await db.Categories.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == categoryId, cancellationToken)) return NotFound<IReadOnlyList<CategoryAttributeRequirementView>>();
        var requirements = await db.CategoryAttributeRequirements.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CategoryId == categoryId && (x.IsPanelScoped || x.Role == "OPTION"))
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
        var webColorAttributeIds = await FindWebColorAttributeIdsAsync(tenantId, categoryId, attributeIds, cancellationToken);
        var roles = requirements
            .GroupBy(x => x.AttributeId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(x => NormalizeRequirementRole(x.Role)).Distinct(StringComparer.Ordinal).ToList());
        var attributeLookup = attributes.ToDictionary(x => x.Id, x => MapAttribute(x, values.Where(value => value.AttributeId == x.Id), roles.GetValueOrDefault(x.Id)));
        var result = requirements.Where(x => attributeLookup.ContainsKey(x.AttributeId)).Select(x => new CategoryAttributeRequirementView(x.AttributeId, x.IsRequired, x.AllowsCustomValue, x.DisplayOrder, attributeLookup[x.AttributeId], NormalizeRequirementRole(x.Role), webColorAttributeIds.Contains(x.AttributeId))).ToList();
        return ServiceResult<IReadOnlyList<CategoryAttributeRequirementView>>.Ok(result);
    }

    private async Task<HashSet<Guid>> FindWebColorAttributeIdsAsync(Guid tenantId, Guid categoryId, IReadOnlyCollection<Guid> attributeIds, CancellationToken cancellationToken)
    {
        if (attributeIds.Count == 0) return [];

        var categoryScopes = await db.CategoryMappings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.LocalId == categoryId && x.Status == "VERIFIED")
            .Select(x => x.ExternalId)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        if (categoryScopes.Length == 0) return [];

        var mappedNames = await (from mapping in db.AttributeMappings.AsNoTracking()
                                 join item in db.ReferenceItems.AsNoTracking()
                                     on new { mapping.TenantId, mapping.SnapshotId, mapping.ExternalId }
                                     equals new { item.TenantId, item.SnapshotId, item.ExternalId }
                                 where mapping.TenantId == tenantId
                                     && attributeIds.Contains(mapping.LocalId)
                                     && categoryScopes.Contains(mapping.ScopeExternalId)
                                     && mapping.Status == "VERIFIED"
                                     && item.ResourceType == "CATEGORY_ATTRIBUTES"
                                     && item.IsActive
                                 select new { mapping.LocalId, item.Name })
            .ToListAsync(cancellationToken);

        return mappedNames
            .Where(item => IsWebColorAttributeName(item.Name))
            .Select(item => item.LocalId)
            .ToHashSet();
    }

    public async Task<ServiceResult<IReadOnlyList<AttributeRequirementCommand>>> ReplaceRequirementsAsync(Guid tenantId, Guid categoryId, long expectedVersion, IReadOnlyList<AttributeRequirementCommand> requirements, CancellationToken cancellationToken)
    {
        var category = await db.Categories.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == categoryId, cancellationToken); if (category is null) return NotFound<IReadOnlyList<AttributeRequirementCommand>>(); if (category.Version != expectedVersion) return Precondition<IReadOnlyList<AttributeRequirementCommand>>(category.Version);
        if (requirements.Select(x => x.AttributeId).Distinct().Count() != requirements.Count) return Invalid<IReadOnlyList<AttributeRequirementCommand>>("requirements", "Özellik gereksinimi tekrarlanamaz.");
        var optionCount = requirements.Count(x => string.Equals(NormalizeRequirementRole(x.Role), "OPTION", StringComparison.Ordinal));
        if (optionCount > 2) return Invalid<IReadOnlyList<AttributeRequirementCommand>>("requirements", "Bir kategoride en fazla 2 seçenek grubu tanımlanabilir.");
        var ids = requirements.Select(x => x.AttributeId).ToArray(); if (await db.AttributeDefinitions.CountAsync(x => x.TenantId == tenantId && ids.Contains(x.Id) && x.IsActive, cancellationToken) != ids.Length) return Invalid<IReadOnlyList<AttributeRequirementCommand>>("requirements", "Etkin olmayan veya bulunmayan özellik vardır.");
        var current = await db.CategoryAttributeRequirements.Where(x => x.TenantId == tenantId && x.CategoryId == categoryId).ToListAsync(cancellationToken); db.CategoryAttributeRequirements.RemoveRange(current); db.CategoryAttributeRequirements.AddRange(requirements.Select(x => new CategoryAttributeRequirement { Id = Guid.CreateVersion7(), TenantId = tenantId, CategoryId = categoryId, AttributeId = x.AttributeId, IsRequired = x.IsRequired, AllowsCustomValue = x.AllowsCustomValue, IsPanelScoped = true, Role = NormalizeRequirementRole(x.Role), DisplayOrder = x.DisplayOrder })); category.Version++; category.UpdatedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return ServiceResult<IReadOnlyList<AttributeRequirementCommand>>.Ok(requirements);
    }

    public async Task<PageResult<ProductView>> ListProductsAsync(Guid tenantId, int limit, string? after, string? status, string? search, string? platform, string? stock, string? sort, CancellationToken cancellationToken)
    {
        var hasPlatformStatusFilter = TryProductPlatformFilter(platform?.Trim() ?? string.Empty, out var platformFilterCode, out var platformFilterState);
        var query = VisibleProducts(tenantId);
        ApplyProductFilters(ref query, tenantId, status, search, platform);
        var countKey = $"catalog:product-family-count:v3:{tenantId:N}:{status?.Trim()}:{search?.Trim()}:{platform?.Trim()}:{stock?.Trim()}";
        var cachedCount = hasPlatformStatusFilter ? null : countCache.Get(countKey);
        var catalogSort = ProductCatalogSort.Parse(sort);
        var afterCreatedAt = default(DateTimeOffset);
        var afterId = Guid.Empty;
        ProductCatalogSortKey? afterSortKey = null;
        if (!string.IsNullOrWhiteSpace(after))
        {
            var validCursor = catalogSort is null
                ? cursors.TryDecodeProduct(after, out afterCreatedAt, out afterId)
                : cursors.TryDecodeProductSort(after, catalogSort.Value, out var sortKey) && (afterSortKey = sortKey) is not null;
            if (!validCursor) throw new ArgumentException("Cursor geçersiz veya süresi dolmuş.", nameof(after));
        }
        // A platform refresh updates the card content, but must not reshuffle the
        // catalog. Keep the list in its original insertion order instead of the
        // mutable UpdatedAt order.
        // K7 (partial): the family grouping, the stock and platform filters and the paging still run over the
        // whole filtered catalog, but on narrow rows (no descriptions, no variant details) joined in SQL instead
        // of id lists; full rows are read only for the page. A stored family key with SQL paging is the full fix.
        var allProducts = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Select(ProductShape).ToListAsync(cancellationToken);
        var allProductIds = allProducts.Select(x => x.Id).ToArray();
        var allVariants = await VariantShapes(tenantId).Where(x => query.Any(product => product.Id == x.ProductId)).ToListAsync(cancellationToken);
        var inventoryByVariant = await db.InventoryItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && query.Any(product => db.ProductVariants.Any(variant => variant.TenantId == tenantId && variant.Id == x.VariantId && variant.ProductId == product.Id)))
            .GroupBy(x => x.VariantId)
            .Select(group => new { VariantId = group.Key, TotalStock = group.Sum(x => x.OnHand) })
            .ToDictionaryAsync(x => x.VariantId, x => x.TotalStock, cancellationToken);
        if (!string.IsNullOrWhiteSpace(stock))
        {
            var variantsByProductForFilter = allVariants
                .GroupBy(x => x.ProductId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<ProductVariant>)group.ToList());
            var stockByProduct = variantsByProductForFilter.ToDictionary(x => x.Key, x => ProductStockPolicy.TotalStock(x.Value, inventoryByVariant));
            allProducts = allProducts.Where(product => stock.Trim().ToUpperInvariant() switch
            {
                "OUT" => stockByProduct.GetValueOrDefault(product.Id) <= 0,
                "LOW" => stockByProduct.GetValueOrDefault(product.Id) > 0
                    && ProductStockPolicy.IsLowStock(variantsByProductForFilter.GetValueOrDefault(product.Id) ?? [], inventoryByVariant),
                "OK" => stockByProduct.GetValueOrDefault(product.Id) > 0
                    && !ProductStockPolicy.IsLowStock(variantsByProductForFilter.GetValueOrDefault(product.Id) ?? [], inventoryByVariant),
                _ => true
            }).ToList();
            allProductIds = allProducts.Select(x => x.Id).ToArray();
            allVariants = allVariants.Where(x => allProductIds.Contains(x.ProductId)).ToList();
        }
        if (hasPlatformStatusFilter)
        {
            var variantsByProductForPlatform = allVariants
                .GroupBy(variant => variant.ProductId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<ProductVariant>)group.ToList());
            var platformVariantIds = allVariants.Select(variant => variant.Id).ToArray();
            var matchedVariantIds = platformVariantIds.Length == 0
                ? new HashSet<Guid>()
                : await PlatformMatchedVariantIds(tenantId, platformFilterCode)
                    .Where(variantId => platformVariantIds.Contains(variantId))
                    .ToHashSetAsync(cancellationToken);
            var matchingFamilyKeys = allProducts
                .GroupBy(product => ProductFamilyKey(product, variantsByProductForPlatform.GetValueOrDefault(product.Id) ?? []), StringComparer.Ordinal)
                .Where(group => PlatformFilterMatchesFamily(
                    group.SelectMany(product => variantsByProductForPlatform.GetValueOrDefault(product.Id) ?? []).Select(variant => variant.Id),
                    matchedVariantIds,
                    platformFilterState))
                .Select(group => group.Key)
                .ToHashSet(StringComparer.Ordinal);
            allProducts = allProducts
                .Where(product => matchingFamilyKeys.Contains(ProductFamilyKey(product, variantsByProductForPlatform.GetValueOrDefault(product.Id) ?? [])))
                .ToList();
            allProductIds = allProducts.Select(x => x.Id).ToArray();
            allVariants = allVariants.Where(x => allProductIds.Contains(x.ProductId)).ToList();
        }
        var variantsByProduct = allVariants.GroupBy(x => x.ProductId).ToDictionary(x => x.Key, x => x.ToList());
        var families = allProducts
            .GroupBy(product => ProductFamilyKey(product, variantsByProduct.GetValueOrDefault(product.Id) ?? []), StringComparer.Ordinal)
            .Select(group => new ProductFamily(group.Key, group.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).First(), group.ToList()))
            .OrderByDescending(x => x.Primary.CreatedAt).ThenByDescending(x => x.Primary.Id)
            .ToList();
        var totalCount = cachedCount is int count ? count : families.Count;
        if (cachedCount is not int)
            countCache.Set(countKey, totalCount, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(15), Size = 1 });
        if (catalogSort is not null)
            return await ListSortedProductFamiliesAsync(tenantId, query, families, variantsByProduct, inventoryByVariant, catalogSort, afterSortKey, limit, totalCount, cancellationToken);
        var pageFamilies = families
            .Where(x => afterId == Guid.Empty || x.Primary.CreatedAt < afterCreatedAt || x.Primary.CreatedAt == afterCreatedAt && x.Primary.Id.CompareTo(afterId) < 0)
            .Take(limit)
            .ToList();
        var pageProductIds = pageFamilies.SelectMany(x => x.Products).Select(x => x.Id).ToArray();
        var fullProducts = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && pageProductIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var products = pageProductIds.Where(fullProducts.ContainsKey).Select(productId => fullProducts[productId]).ToList();
        var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && pageProductIds.Contains(x.ProductId)).ToListAsync(cancellationToken);
        var views = await BuildProductViewsAsync(tenantId, products, variants, cancellationToken);
        var lastFamily = pageFamilies.LastOrDefault();
        var hasMore = lastFamily is not null && families.Any(x => x.Primary.CreatedAt < lastFamily.Primary.CreatedAt || x.Primary.CreatedAt == lastFamily.Primary.CreatedAt && x.Primary.Id.CompareTo(lastFamily.Primary.Id) < 0);
        return new(views, hasMore ? cursors.EncodeProduct(lastFamily!.Primary.CreatedAt, lastFamily.Primary.Id) : null, hasMore, totalCount);
    }

    // Price and stock sorting run over every card of the filtered catalog, by the values the card shows: the lowest
    // effective sale price of its variants (BuildProductViewsAsync: channel offer, else variant, else product default)
    // and the total MAIN stock. The cursor carries the last card's value, so page 2 continues where page 1 ended.
    private async Task<PageResult<ProductView>> ListSortedProductFamiliesAsync(Guid tenantId, IQueryable<Product> query, IReadOnlyList<ProductFamily> families, IReadOnlyDictionary<Guid, List<ProductVariant>> variantsByProduct, IReadOnlyDictionary<Guid, decimal> inventoryByVariant, ProductCatalogSort catalogSort, ProductCatalogSortKey? afterSortKey, int limit, int totalCount, CancellationToken cancellationToken)
    {
        var offerPriceByVariant = new Dictionary<Guid, decimal>();
        if (catalogSort.ByPrice)
        {
            // The card takes each variant's first offer, active ones first (BuildProductViewsAsync's order).
            var offers = await db.ChannelOffers.AsNoTracking()
                .Where(x => x.TenantId == tenantId && query.Any(product => db.ProductVariants.Any(variant => variant.TenantId == tenantId && variant.Id == x.VariantId && variant.ProductId == product.Id)))
                .OrderByDescending(x => x.Status == "ACTIVE").ThenBy(x => x.Id)
                .Select(x => new { x.VariantId, x.SalePrice })
                .ToListAsync(cancellationToken);
            foreach (var offer in offers) offerPriceByVariant.TryAdd(offer.VariantId, offer.SalePrice);
        }
        decimal? FamilyMetric(ProductFamily family)
        {
            var familyVariants = family.Products.SelectMany(product => (variantsByProduct.GetValueOrDefault(product.Id) ?? []).Select(variant => (product, variant))).ToList();
            if (catalogSort.ByPrice)
                return CatalogPricePolicy.MinimumPositivePrice(familyVariants.Select(item => CatalogPricePolicy.EffectiveSalePrice(offerPriceByVariant.TryGetValue(item.variant.Id, out var salePrice) ? salePrice : null, item.variant.DefaultSalePrice, item.product.DefaultSalePrice)));
            var stocked = familyVariants.Where(item => inventoryByVariant.ContainsKey(item.variant.Id)).ToList();
            return stocked.Count == 0 ? null : stocked.Sum(item => inventoryByVariant[item.variant.Id]);
        }
        var ordered = families
            .Select(family => (Family: family, Key: new ProductCatalogSortKey(FamilyMetric(family), family.Primary.CreatedAt, family.Primary.Id)))
            .OrderBy(item => item.Key, catalogSort)
            .ToList();
        var pageItems = ordered
            .Where(item => afterSortKey is not { } afterKey || catalogSort.Compare(item.Key, afterKey) > 0)
            .Take(limit)
            .ToList();
        var pageProductIds = pageItems.SelectMany(item => item.Family.Products).Select(x => x.Id).ToArray();
        var fullProducts = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && pageProductIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var products = pageProductIds.Where(fullProducts.ContainsKey).Select(productId => fullProducts[productId]).ToList();
        var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && pageProductIds.Contains(x.ProductId)).ToListAsync(cancellationToken);
        var views = await BuildProductViewsAsync(tenantId, products, variants, cancellationToken);
        var hasMore = pageItems.Count > 0 && catalogSort.Compare(ordered[^1].Key, pageItems[^1].Key) > 0;
        return new(views, hasMore ? cursors.EncodeProductSort(catalogSort.Value, pageItems[^1].Key) : null, hasMore, totalCount);
    }

    private sealed record ProductFamily(string Key, Product Primary, IReadOnlyList<Product> Products);

    // Narrow rows for catalog-wide grouping and counting; never saved or shown as they are.
    private static readonly System.Linq.Expressions.Expression<Func<Product, Product>> ProductShape = x => new Product { Id = x.Id, TenantId = x.TenantId, Title = "", Description = "", BrandId = x.BrandId, Status = x.Status, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt, Version = x.Version, DefaultSalePrice = x.DefaultSalePrice };
    private IQueryable<ProductVariant> VariantShapes(Guid tenantId) => db.ProductVariants.AsNoTracking()
        .Where(x => x.TenantId == tenantId)
        .Select(x => new ProductVariant { Id = x.Id, TenantId = x.TenantId, ProductId = x.ProductId, Sku = "", SkuNormalized = "", ModelCode = x.ModelCode, OptionSignature = x.OptionSignature, Status = x.Status, DefaultSalePrice = x.DefaultSalePrice });

    private static string ProductFamilyKey(Product product, IReadOnlyList<ProductVariant> variants)
    {
        var modelCode = variants.Select(x => x.ModelCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        return ProductFamilyKeyPolicy.FamilyKey(product.Id, product.BrandId, modelCode);
    }

    public async Task<ProductSummaryView> ProductSummaryAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var products = await VisibleProducts(tenantId)
            .Select(x => new { x.Id, x.Status })
            .ToListAsync(cancellationToken);
        // K7 (partial): only the columns the stock counts need.
        var variants = await VariantShapes(tenantId).ToListAsync(cancellationToken);
        var inventoryByVariant = await db.InventoryItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN")
            .GroupBy(x => x.VariantId)
            .Select(group => new { VariantId = group.Key, TotalStock = group.Sum(x => x.OnHand) })
            .ToDictionaryAsync(x => x.VariantId, x => x.TotalStock, cancellationToken);
        var stockByProduct = variants
            .GroupBy(x => x.ProductId)
            .ToDictionary(group => group.Key, group => ProductStockPolicy.TotalStock(group, inventoryByVariant));
        var variantsByProduct = variants
            .GroupBy(x => x.ProductId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ProductVariant>)group.ToList());
        var platforms = await (from profile in db.ChannelListingProfiles.AsNoTracking()
                               join connection in db.PlatformConnections.AsNoTracking()
                                   on new { profile.TenantId, profile.ConnectionId } equals new { connection.TenantId, ConnectionId = connection.Id }
                               where profile.TenantId == tenantId && profile.Enabled && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")
                               select connection.DisplayName)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);

        return new(
            products.Count,
            products.Count(x => x.Status == ProductStatus.Active),
            products.Count(x => stockByProduct.GetValueOrDefault(x.Id) <= 0),
            products.Count(x => stockByProduct.GetValueOrDefault(x.Id) > 0
                && ProductStockPolicy.IsLowStock(variantsByProduct.GetValueOrDefault(x.Id) ?? [], inventoryByVariant)),
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
        // D4 (K8): the bulk change moves the same rows as the editor's status change: the product, its sale
        // rows and its marketplace links (a local edit). Archive and activate are both status flips only, so an
        // archived product comes back with its variants and images on activation.
        var changedProductIds = products.Where(product => product.Status != status).Select(product => product.Id).ToArray();
        var variants = changedProductIds.Length == 0
            ? []
            : await db.ProductVariants.Where(x => x.TenantId == tenantId && changedProductIds.Contains(x.ProductId) && x.Status != status).ToListAsync(cancellationToken);
        foreach (var product in products)
        {
            product.Status = status;
            product.ArchivedAt = status == ProductStatus.Archived ? product.ArchivedAt ?? now : null;
            product.UpdatedAt = now;
            product.Version++;
        }
        foreach (var variant in variants) { variant.Status = status; variant.UpdatedAt = now; variant.Version++; }
        await CatalogLocalEdits.MarkLinksDirtyAsync(db, tenantId, changedProductIds, CatalogLocalEdits.ProductField, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<int>.Ok(products.Count);
    }

    // Ledger sources written by order and return flows; such a movement is stock history that a delete must keep.
    private static readonly string[] OrderMovementSourceTypes = ["ORDER_LINE", "RETURN_CLAIM"];

    private static readonly string[] PendingCatalogWriteJobTypes =
    [
        MarketplaceJobTypes.ProductCreate, MarketplaceJobTypes.ProductUpdate, MarketplaceJobTypes.ProductArchive, MarketplaceJobTypes.ProductApprovalReconcile,
        MarketplaceJobTypes.PriceInventorySync, MarketplaceJobTypes.StockProjectionDispatch
    ];

    // Only a job that will still run counts. Blocked is terminal (a refused publication or bulk price/stock
    // sync stays there for good), so counting it refused the delete forever; such jobs are left as they are.
    private static readonly JobStatus[] PendingJobStatuses = [JobStatus.Pending, JobStatus.Leased, JobStatus.RetryScheduled];

    // D3 (K19): a delete purges the variants' reservations and stock ledger. It is refused while a variant has
    // an active order reservation, has any order or return stock movement (the ledger is history, never purged),
    // or a publication or price/stock job for the product or variant is still pending; the caller archives
    // instead. Returns the Turkish reason ("... bağlı") or null when the delete may proceed.
    private async Task<string?> StockHistoryDeleteBlockAsync(Guid tenantId, Guid[] productIds, Guid[] variantIds, Guid[] inventoryIds, CancellationToken cancellationToken)
    {
        if (inventoryIds.Length > 0)
        {
            if (await db.StockReservations.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && inventoryIds.Contains(x.InventoryItemId) && x.Status == ReservationStatus.Active, cancellationToken))
                return "açık sipariş rezervasyonuna";
            if (await db.StockLedgerEntries.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && inventoryIds.Contains(x.InventoryItemId) && OrderMovementSourceTypes.Contains(x.SourceType), cancellationToken))
                return "sipariş stok hareketine";
        }
        if (productIds.Length == 0 && variantIds.Length == 0) return null;
        var pendingJobs = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && PendingCatalogWriteJobTypes.Contains(x.JobType) && PendingJobStatuses.Contains(x.Status))
            .Select(x => new { x.JobDedupKey, x.PayloadJson })
            .ToListAsync(cancellationToken);
        if (pendingJobs.Count == 0) return null;
        // Publication dedup keys carry the product id in the N form; payloads serialize ids in the D form.
        var markers = productIds.Select(productId => productId.ToString("N")).Concat(productIds.Concat(variantIds).Select(id => id.ToString("D"))).ToArray();
        return pendingJobs.Any(job => markers.Any(marker => job.JobDedupKey.Contains(marker, StringComparison.OrdinalIgnoreCase) || job.PayloadJson.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            ? "bekleyen yayın veya fiyat-stok işine"
            : null;
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
        // A product is deleted with its whole stock history (the owner's decision, 26 Oct 2026): its order and return
        // stock movements, reservations and return stock dispositions go with it, while the orders and returns
        // themselves stay and only lose their link to the variant. Only a publication or price/stock job still to
        // run refuses it, as that job would write to the product being deleted.
        var productDeleteBlock = await StockHistoryDeleteBlockAsync(tenantId, targetIds, variantIds, [], cancellationToken);
        if (productDeleteBlock is not null)
            return Conflict<int>("PRODUCT_DELETE_BLOCKED", $"Ürün {productDeleteBlock} bağlı olduğu için şu an silinemiyor; iş bittikten sonra tekrar deneyin.");

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
            DELETE FROM inventory.channel_inventory_observations o
            USING catalog.product_variants v, purge_product_ids p
            WHERE o."TenantId"={{tenantId}} AND o."VariantId"=v."Id" AND v."ProductId"=p."Id";
            DELETE FROM inventory.stock_reservations r
            USING inventory.inventory_items i, catalog.product_variants v, purge_product_ids p
            WHERE r."TenantId"={{tenantId}} AND r."InventoryItemId"=i."Id" AND i."VariantId"=v."Id" AND v."ProductId"=p."Id";
            DELETE FROM inventory.stock_ledger_entries l
            USING inventory.inventory_items i, catalog.product_variants v, purge_product_ids p
            WHERE l."TenantId"={{tenantId}} AND l."InventoryItemId"=i."Id" AND i."VariantId"=v."Id" AND v."ProductId"=p."Id";
            DELETE FROM sales.return_stock_dispositions d
            USING inventory.inventory_items i, catalog.product_variants v, purge_product_ids p
            WHERE d."TenantId"={{tenantId}} AND d."InventoryItemId"=i."Id" AND i."VariantId"=v."Id" AND v."ProductId"=p."Id";
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
        if (command.DefaultListPrice is < 0 || command.DefaultSalePrice is < 0) return Invalid<ProductView>("prices", "Fiyat negatif olamaz.");
        if (command.WarrantyMonths is < 0 or > 240) return Invalid<ProductView>("warrantyMonths", "Garanti süresi 0-240 ay arasında olmalıdır.");
        if (command.TrendyolWebColorValueId is Guid webColorValueId && webColorValueId != Guid.Empty && !await db.AttributeValues.AnyAsync(x => x.TenantId == tenantId && x.Id == webColorValueId && x.IsActive, cancellationToken)) return Invalid<ProductView>("trendyolWebColorValueId", "Trendyol Web Color değeri bulunamadı.");
        if (command.DefaultListPrice is decimal listPrice && command.DefaultSalePrice is decimal salePrice && listPrice < salePrice) return Invalid<ProductView>("prices", "Liste fiyatı satış fiyatından küçük olamaz.");
        if (command.Variants.Any(variant => variant.DefaultListPrice is < 0 || variant.DefaultSalePrice is < 0)) return Invalid<ProductView>("variants", "Varyant fiyatı negatif olamaz.");
        if (command.Variants.Any(variant => variant.DefaultListPrice is decimal variantList && variant.DefaultSalePrice is decimal variantSale && variantList < variantSale)) return Invalid<ProductView>("variants", "Varyant liste fiyatı satış fiyatından küçük olamaz.");
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
                .Where(x => x.TenantId == tenantId && x.CategoryId == categoryId && (x.IsPanelScoped || x.Role == "OPTION") && x.IsRequired)
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
        var skuKeys = CatalogKeyNormalizer.LookupKeys(command.Variants.Select(x => x.Sku));
        if (await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId && skuKeys.Contains(x.SkuNormalized), cancellationToken)) return Conflict<ProductView>("SKU_CONFLICT_REVIEW_REQUIRED", "SKU başka bir varyantla çakışıyor; otomatik birleştirme yapılmadı.");
        var normalizedBarcodes = command.Variants.Select(x => NormalizeBarcode(x.Barcode)).OfType<string>().ToArray();
        var barcodeKeys = CatalogKeyNormalizer.LookupKeys(command.Variants.Select(x => x.Barcode));
        if (normalizedBarcodes.Distinct().Count() != normalizedBarcodes.Length || await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId && x.BarcodeNormalized != null && barcodeKeys.Contains(x.BarcodeNormalized), cancellationToken)) return Conflict<ProductView>("BARCODE_CONFLICT_REVIEW_REQUIRED", "Barkod başka bir varyantla çakışıyor; otomatik birleştirme yapılmadı.");

        var now = timeProvider.GetUtcNow();
        var productStatus = command.Status == "ACTIVE" ? ProductStatus.Active : (command.Status == "ARCHIVED" ? ProductStatus.Archived : ProductStatus.Draft); var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenantId, Title = command.Title.Trim(), Description = command.Description.Trim(), BrandId = command.BrandId, CategoryId = command.CategoryId, DefaultListPrice = command.DefaultListPrice, DefaultSalePrice = command.DefaultSalePrice, WarrantyMonths = command.WarrantyMonths is > 0 ? command.WarrantyMonths : null, TrendyolWebColorValueId = command.TrendyolWebColorValueId is Guid createWebColor && createWebColor != Guid.Empty ? createWebColor : null, Status = productStatus, CreatedAt = now, UpdatedAt = now };
        if (command.Variants.Any(variant => variant.CostPrice is < 0)) return Invalid<ProductView>("variants", "Maliyet negatif olamaz.");
        var variants = command.Variants.Select((variant, index) => new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, SortOrder = index, Sku = variant.Sku.Trim(), SkuNormalized = Normalize(variant.Sku), Barcode = NullTrim(variant.Barcode), BarcodeNormalized = NormalizeBarcode(variant.Barcode), ModelCode = NullTrim(variant.ModelCode), OptionSignature = Signature(variant.Options), Status = productStatus, Weight = PositiveOrNull(variant.Weight), Width = PositiveOrNull(variant.Width), Height = PositiveOrNull(variant.Height), Length = PositiveOrNull(variant.Length), Desi = PositiveOrNull(variant.Desi), CostPrice = NonNegativeOrNull(variant.CostPrice), DefaultListPrice = variant.DefaultListPrice, DefaultSalePrice = variant.DefaultSalePrice, CreatedAt = now, UpdatedAt = now }).ToList();
        db.Products.Add(product);
        db.ProductVariants.AddRange(variants);
        db.ProductAttributeAssignments.AddRange(globalAssignments.Select(x => Assignment(tenantId, product.Id, null, x)));
        for (var index = 0; index < variants.Count; index++) db.ProductAttributeAssignments.AddRange((command.Variants[index].Attributes ?? []).Select(x => Assignment(tenantId, product.Id, variants[index].Id, x)));
        await PersistVariantOptionsAsync(tenantId, product.Id, variants, command.Variants, cancellationToken);
        await EnsureMainInventoryAsync(tenantId, variants, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await GetProductAsync(tenantId, product.Id, cancellationToken);
    }

    public async Task<ServiceResult<ProductView>> DuplicateProductAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var source = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken);
        if (source is null) return NotFound<ProductView>();

        var familyProductIds = await ProductFamilyProductIdsAsync(tenantId, source, cancellationToken);
        var sourceProducts = await db.Products.AsNoTracking()
            .Where(x => x.TenantId == tenantId && familyProductIds.Contains(x.Id))
            .OrderBy(x => x.Id == productId ? 0 : 1).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var sourceProductIds = sourceProducts.Select(x => x.Id).ToArray();
        var sourceVariants = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && sourceProductIds.Contains(x.ProductId))
            .OrderBy(x => x.ProductId).ThenBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        if (sourceVariants.Count == 0) return Invalid<ProductView>("variants", "Varyantı olmayan ürün kopyalanamaz.");
        if (sourceVariants.Count > 1000) return Invalid<ProductView>("variants", "Tek ürün kaydında en fazla 1000 varyant oluşturulabilir.");

        var sourceAssignments = await db.ProductAttributeAssignments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && sourceProductIds.Contains(x.ProductId))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var sourceOptions = await db.ProductOptions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && sourceProductIds.Contains(x.ProductId))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var sourceOptionIds = sourceOptions.Select(x => x.Id).ToArray();
        var sourceOptionValues = sourceOptionIds.Length == 0
            ? []
            : await db.ProductOptionValues.AsNoTracking()
                .Where(x => x.TenantId == tenantId && sourceOptionIds.Contains(x.OptionId))
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);
        var sourceVariantIds = sourceVariants.Select(x => x.Id).ToArray();
        var sourceVariantOptions = sourceVariantIds.Length == 0
            ? []
            : await db.VariantOptionValues.AsNoTracking()
                .Where(x => x.TenantId == tenantId && sourceVariantIds.Contains(x.VariantId))
                .ToListAsync(cancellationToken);
        var sourceMedia = await db.ProductMedia.AsNoTracking()
            .Where(x => x.TenantId == tenantId && sourceProductIds.Contains(x.ProductId) && x.Status == "ACTIVE")
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        var copyProductIds = new Dictionary<Guid, Guid>(sourceProducts.Count);
        var copies = new List<Product>(sourceProducts.Count);
        foreach (var sourceProduct in sourceProducts)
        {
            var copyId = Guid.CreateVersion7();
            copyProductIds.Add(sourceProduct.Id, copyId);
            copies.Add(new Product
            {
                Id = copyId,
                TenantId = tenantId,
                Title = ProductCopyCodePolicy.WithCopySuffix(sourceProduct.Title, 320),
                Description = sourceProduct.Description,
                BrandId = sourceProduct.BrandId,
                CategoryId = sourceProduct.CategoryId,
                DefaultListPrice = sourceProduct.DefaultListPrice,
                DefaultSalePrice = sourceProduct.DefaultSalePrice,
                Status = ProductStatus.Draft,
                SourcePolicyVersion = sourceProduct.SourcePolicyVersion,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        var token = copyProductIds[productId].ToString("N")[..8];
        // Every copy code carries the token, so only stored codes containing it can collide; load those
        // instead of every code of the tenant.
        var tokenKey = token.ToUpperInvariant();
        var usedSkus = (await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.SkuNormalized.Contains(tokenKey)).Select(x => x.SkuNormalized).ToListAsync(cancellationToken)).Select(value => Normalize(value)).ToHashSet(StringComparer.Ordinal);
        var usedBarcodes = (await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.BarcodeNormalized != null && x.BarcodeNormalized.Contains(tokenKey)).Select(x => x.BarcodeNormalized!).ToListAsync(cancellationToken)).Select(value => Normalize(value)).ToHashSet(StringComparer.Ordinal);
        var usedModelCodes = (await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.ModelCode != null && x.ModelCode.ToUpper().Contains(tokenKey)).Select(x => x.ModelCode!).ToListAsync(cancellationToken)).Select(value => Normalize(value)).ToHashSet(StringComparer.Ordinal);
        var modelCodeCopies = new Dictionary<string, string>(StringComparer.Ordinal);
        var newVariants = new List<ProductVariant>(sourceVariants.Count);
        var variantIdMap = new Dictionary<Guid, Guid>(sourceVariants.Count);
        for (var variantIndex = 0; variantIndex < sourceVariants.Count; variantIndex++)
        {
            var sourceVariant = sourceVariants[variantIndex];
            var newVariantId = Guid.CreateVersion7();
            variantIdMap.Add(sourceVariant.Id, newVariantId);
            var variantToken = $"{token}{variantIndex + 1}";
            string? modelCode = null;
            if (!string.IsNullOrWhiteSpace(sourceVariant.ModelCode))
            {
                var normalizedModelCode = Normalize(sourceVariant.ModelCode);
                if (!modelCodeCopies.TryGetValue(normalizedModelCode, out modelCode))
                {
                    modelCode = ProductCopyCodePolicy.WithUniqueCopySuffix(sourceVariant.ModelCode, token, usedModelCodes, 160);
                    modelCodeCopies.Add(normalizedModelCode, modelCode);
                }
            }

            var sku = ProductCopyCodePolicy.WithUniqueCopySuffix(sourceVariant.Sku, variantToken, usedSkus);
            var barcode = string.IsNullOrWhiteSpace(sourceVariant.Barcode)
                ? null
                : ProductCopyCodePolicy.WithUniqueCopySuffix(sourceVariant.Barcode, variantToken, usedBarcodes);
            newVariants.Add(new ProductVariant
            {
                Id = newVariantId,
                TenantId = tenantId,
                ProductId = copyProductIds[sourceVariant.ProductId],
                SortOrder = sourceVariant.SortOrder,
                Sku = sku,
                SkuNormalized = Normalize(sku),
                Barcode = barcode,
                BarcodeNormalized = NormalizeBarcode(barcode),
                ModelCode = modelCode,
                OptionSignature = sourceVariant.OptionSignature,
                Status = ProductStatus.Draft,
                Weight = sourceVariant.Weight,
                Width = sourceVariant.Width,
                Height = sourceVariant.Height,
                Length = sourceVariant.Length,
                Desi = sourceVariant.Desi,
                CostPrice = sourceVariant.CostPrice,
                DefaultListPrice = sourceVariant.DefaultListPrice,
                DefaultSalePrice = sourceVariant.DefaultSalePrice,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        var optionIdMap = new Dictionary<Guid, Guid>(sourceOptions.Count);
        foreach (var option in sourceOptions)
        {
            var newOptionId = Guid.CreateVersion7();
            optionIdMap.Add(option.Id, newOptionId);
            db.ProductOptions.Add(new ProductOption { Id = newOptionId, TenantId = tenantId, ProductId = copyProductIds[option.ProductId], Label = option.Label, NormalizedKey = option.NormalizedKey, SortOrder = option.SortOrder });
        }

        var optionValueIdMap = new Dictionary<Guid, Guid>(sourceOptionValues.Count);
        foreach (var optionValue in sourceOptionValues)
        {
            var newOptionValueId = Guid.CreateVersion7();
            optionValueIdMap.Add(optionValue.Id, newOptionValueId);
            db.ProductOptionValues.Add(new ProductOptionValue { Id = newOptionValueId, TenantId = tenantId, OptionId = optionIdMap[optionValue.OptionId], Label = optionValue.Label, NormalizedKey = optionValue.NormalizedKey, SortOrder = optionValue.SortOrder });
        }

        db.Products.AddRange(copies);
        db.ProductVariants.AddRange(newVariants);
        db.ProductAttributeAssignments.AddRange(sourceAssignments.Select(assignment => new ProductAttributeAssignment
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ProductId = copyProductIds[assignment.ProductId],
            VariantId = assignment.VariantId is Guid oldVariantId ? variantIdMap[oldVariantId] : null,
            AttributeId = assignment.AttributeId,
            ValueId = assignment.ValueId,
            TextValue = assignment.TextValue,
            NumberValue = assignment.NumberValue,
            BooleanValue = assignment.BooleanValue,
            SortOrder = assignment.SortOrder
        }));
        db.VariantOptionValues.AddRange(sourceVariantOptions.Select(assignment => new VariantOptionValue
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            VariantId = variantIdMap[assignment.VariantId],
            OptionId = optionIdMap[assignment.OptionId],
            OptionValueId = optionValueIdMap[assignment.OptionValueId]
        }));
        db.ProductMedia.AddRange(sourceMedia.Select(media => new ProductMedia
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ProductId = copyProductIds[media.ProductId],
            VariantId = media.VariantId is Guid oldVariantId ? variantIdMap[oldVariantId] : null,
            FileAssetId = media.FileAssetId,
            MediaRole = media.MediaRole,
            SortOrder = media.SortOrder,
            AltText = media.AltText,
            Status = media.Status
        }));

        await EnsureMainInventoryAsync(tenantId, newVariants, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetProductAsync(tenantId, copyProductIds[productId], cancellationToken);
    }

    // Hepsiburada and Shopify address a variant by their own code (hbSku, variant id); the product editor shows
    // them for every variant of the colour family it edits.
    private static readonly string[] MarketplaceCodePlatforms = ["HEPSIBURADA", "SHOPIFY"];

    public async Task<ServiceResult<ProductMarketplaceCodesView>> GetProductMarketplaceCodesAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var product = await VisibleProducts(tenantId).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (product is null) return NotFound<ProductMarketplaceCodesView>();
        var modelCode = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == id && x.ModelCode != null && x.ModelCode != "")
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => x.ModelCode).FirstOrDefaultAsync(cancellationToken);
        var familyProductIds = await VisibleFamilyProductIdsAsync(tenantId, product.BrandId, modelCode?.Trim(), cancellationToken);
        if (!familyProductIds.Contains(id)) familyProductIds.Add(id);
        var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && familyProductIds.Contains(x.ProductId))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new { x.Id, x.Sku, x.Barcode }).ToListAsync(cancellationToken);
        var connections = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && MarketplaceCodePlatforms.Contains(x.PlatformCode) && (x.Status == "ACTIVE" || x.Status == "VERIFIED"))
            .OrderBy(x => x.PlatformCode).ThenBy(x => x.DisplayName)
            .Select(x => new MarketplaceCodeConnectionView(x.Id, x.PlatformCode, x.DisplayName)).ToListAsync(cancellationToken);
        var variantIds = variants.Select(x => x.Id).ToArray();
        var connectionIds = connections.Select(x => x.ConnectionId).ToArray();
        var links = await db.MarketplaceVariantLinks.AsNoTracking()
            .Where(x => x.TenantId == tenantId && variantIds.Contains(x.VariantId) && connectionIds.Contains(x.ConnectionId))
            .Select(x => new { x.VariantId, Code = new VariantMarketplaceCodeView(x.ConnectionId, x.ExternalId, x.ExternalBarcode, x.ExternalSku, x.LastSeenAt, x.HealthStatus, x.HealthReason) })
            .ToListAsync(cancellationToken);
        var codesByVariant = links.GroupBy(x => x.VariantId).ToDictionary(group => group.Key, group => (IReadOnlyList<VariantMarketplaceCodeView>)group.Select(x => x.Code).ToList());
        return ServiceResult<ProductMarketplaceCodesView>.Ok(new(connections,
            variants.Select(x => new VariantMarketplaceCodesView(x.Id, x.Sku, x.Barcode, codesByVariant.GetValueOrDefault(x.Id) ?? [])).ToList()));
    }

    private const int MaxMarketplaceCodeChanges = 1000;
    // Shopify's variant id as a number, from the id itself, its gid or an admin link ending in /variants/{id}.
    private static readonly System.Text.RegularExpressions.Regex ShopifyVariantId = new(@"^(?:gid://shopify/ProductVariant/|.*/variants/)?(\d{1,20})(?:[/?#].*)?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    // Codes typed in the product editor. A code links the variant on that Hepsiburada (hbSku) or Shopify (variant id)
    // connection, an empty one removes the link. The marketplace's barcode and stock code described the previous code,
    // so a changed link starts without them and the next product import reads them; until then price and stock skip it.
    public async Task<ServiceResult<ProductMarketplaceCodesView>> UpdateProductMarketplaceCodesAsync(Guid tenantId, Guid id, UpdateMarketplaceCodesCommand command, CancellationToken cancellationToken)
    {
        var current = await GetProductMarketplaceCodesAsync(tenantId, id, cancellationToken);
        if (!current.Succeeded) return current;
        var changes = command.Codes ?? [];
        if (changes.Count == 0) return Invalid<ProductMarketplaceCodesView>("codes", "Kaydedilecek kod yok.");
        if (changes.Count > MaxMarketplaceCodeChanges) return Invalid<ProductMarketplaceCodesView>("codes", $"Tek seferde en fazla {MaxMarketplaceCodeChanges} kod kaydedilebilir.");
        var variants = current.Value!.Variants.ToDictionary(x => x.VariantId);
        var connections = current.Value.Connections.ToDictionary(x => x.ConnectionId);
        var wanted = new Dictionary<(Guid VariantId, Guid ConnectionId), string?>();
        foreach (var change in changes)
        {
            var field = MarketplaceCodeField(change.VariantId, change.ConnectionId);
            if (!variants.ContainsKey(change.VariantId) || !connections.TryGetValue(change.ConnectionId, out var connection))
                return Invalid<ProductMarketplaceCodesView>(field, "Varyant veya bağlantı bu ürüne ait değil; pencereyi yenileyin.");
            var code = MarketplaceCode(connection.PlatformCode, change.Code, out var error);
            if (error is not null) return Invalid<ProductMarketplaceCodesView>(field, error);
            if (!wanted.TryAdd((change.VariantId, change.ConnectionId), code)) return Invalid<ProductMarketplaceCodesView>(field, "Aynı varyantın kodu iki kez gönderildi.");
        }
        // One code links one variant on a connection.
        if (wanted.Where(x => x.Value is not null).GroupBy(x => (x.Key.ConnectionId, x.Value)).FirstOrDefault(group => group.Count() > 1) is { } repeated)
            return Invalid<ProductMarketplaceCodesView>(MarketplaceCodeField(repeated.Last().Key.VariantId, repeated.Key.ConnectionId), $"'{repeated.Key.Value}' kodu birden fazla varyanta yazıldı.");

        var variantIds = wanted.Keys.Select(x => x.VariantId).Distinct().ToArray();
        var connectionIds = wanted.Keys.Select(x => x.ConnectionId).Distinct().ToArray();
        var codes = wanted.Values.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var links = await db.MarketplaceVariantLinks
            .Where(x => x.TenantId == tenantId && connectionIds.Contains(x.ConnectionId) && (variantIds.Contains(x.VariantId) || codes.Contains(x.ExternalId)))
            .ToListAsync(cancellationToken);
        // A code another variant holds and keeps is refused; one that variant gives up in this same save may move.
        foreach (var ((variantId, connectionId), code) in wanted)
        {
            if (code is null || links.FirstOrDefault(x => x.ConnectionId == connectionId && x.VariantId != variantId && string.Equals(x.ExternalId, code, StringComparison.Ordinal)) is not { } holder) continue;
            if (wanted.TryGetValue((holder.VariantId, connectionId), out var holderCode) && !string.Equals(holderCode, code, StringComparison.Ordinal)) continue;
            var holderSku = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == holder.VariantId).Select(x => x.Sku).FirstOrDefaultAsync(cancellationToken);
            var message = $"'{code}' kodu {(holderSku is null ? "başka bir varyanta" : $"{holderSku} stok kodlu varyanta")} bağlı.";
            return ServiceResult<ProductMarketplaceCodesView>.Fail("MARKETPLACE_CODE_IN_USE", message, 409, new Dictionary<string, string[]> { [MarketplaceCodeField(variantId, connectionId)] = [message] });
        }
        // Changed and removed links go first, so two variants swapping codes never meet the unique index half done.
        var added = new List<MarketplaceVariantLink>();
        foreach (var ((variantId, connectionId), code) in wanted)
        {
            var existing = links.FirstOrDefault(x => x.ConnectionId == connectionId && x.VariantId == variantId);
            if (existing is not null && string.Equals(existing.ExternalId, code, StringComparison.Ordinal)) continue;
            if (existing is not null) db.MarketplaceVariantLinks.Remove(existing);
            // A typed code is unverified until an import reads it; the change time keeps a scan or push that began
            // before it from judging the new code.
            if (code is not null) added.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, VariantId = variantId, ExternalId = code, HealthStatus = MarketplaceVariantLinkHealth.Unverified, HealthChangedAt = timeProvider.GetUtcNow(), Version = 1 });
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            db.MarketplaceVariantLinks.AddRange(added);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return Conflict<ProductMarketplaceCodesView>("MARKETPLACE_CODES_CHANGED", "Kodlar bu sırada başka bir işlemde (örneğin ürün aktarımı) değişti; pencereyi yenileyip yeniden kaydedin.");
        }
        return await GetProductMarketplaceCodesAsync(tenantId, id, cancellationToken);
    }

    internal static string MarketplaceCodeField(Guid variantId, Guid connectionId) => $"{variantId:D}:{connectionId:D}";

    // Hepsiburada's hbSku as typed (no spaces); Shopify's variant id as a number. Empty means no link.
    internal static string? MarketplaceCode(string platformCode, string? value, out string? error)
    {
        error = null;
        var code = value?.Trim();
        if (string.IsNullOrEmpty(code)) return null;
        if (platformCode == "SHOPIFY")
        {
            if (ShopifyVariantId.Match(code) is { Success: true } match) return match.Groups[1].Value;
            error = "Shopify Variant ID yalnız rakamlardan oluşur (örneğin 44026663600212).";
            return null;
        }
        if (code.Length > 160 || code.Any(char.IsWhiteSpace))
        {
            error = "Kod boşluk içeremez ve en fazla 160 karakter olabilir.";
            return null;
        }
        return code;
    }

    public async Task<ServiceResult<ProductView>> GetProductAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var product = await VisibleProducts(tenantId).SingleOrDefaultAsync(x => x.Id == id, cancellationToken); if (product is null) return NotFound<ProductView>();
        var ownVariants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == id)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        var modelCode = ownVariants.Select(x => x.ModelCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();
        var familyProductIds = await VisibleFamilyProductIdsAsync(tenantId, product.BrandId, modelCode, cancellationToken);
        if (!familyProductIds.Contains(id)) familyProductIds.Add(id);
        var familyProducts = await VisibleProducts(tenantId)
            .Where(x => familyProductIds.Contains(x.Id))
            .OrderBy(x => x.Id == id ? 0 : 1)
            .ThenBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        var familyVariants = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && familyProductIds.Contains(x.ProductId))
            .ToListAsync(cancellationToken);
        var familyViews = await BuildProductViewsAsync(tenantId, familyProducts, familyVariants, cancellationToken, id);
        var primaryView = familyViews.First(x => x.Id == id);
        var allVariants = familyViews.SelectMany(x => x.Variants).ToList();
        var allOptions = familyViews.SelectMany(x => x.Options ?? [])
            .GroupBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProductOptionView(
                group.First().Id,
                group.First().Label,
                group.SelectMany(x => x.Values)
                    .GroupBy(value => value.Label, StringComparer.OrdinalIgnoreCase)
                    .Select(value => value.First())
                    .ToList()))
            .ToList();
        var familyMediaRows = await (from item in db.ProductMedia.AsNoTracking()
                                     join asset in db.FileAssets.AsNoTracking() on new { item.TenantId, item.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                                     where item.TenantId == tenantId && familyProductIds.Contains(item.ProductId) && item.Status == "ACTIVE" && asset.Status == "ACTIVE" && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                                     orderby item.SortOrder, item.ProductId, item.Id
                                     select new { MediaId = item.Id, item.ProductId, item.VariantId, item.SortOrder, AssetId = asset.Id, asset.Classification, Url = asset.RelativePath }).ToListAsync(cancellationToken);
        var familyMediaSelection = familyMediaRows
            .GroupBy(item => item.ProductId)
            .SelectMany(group =>
            {
                var productMedia = group.Where(item => item.VariantId is null).OrderBy(item => item.SortOrder).ToList();
                return productMedia.Count > 0 ? productMedia : group.OrderBy(item => item.SortOrder).Take(1).ToList();
            })
            .OrderBy(item => item.SortOrder)
            .Select(item => new { Media = item, Url = CatalogMediaDisplay.Url(item.AssetId, item.Classification, item.Url) })
            .Where(item => !string.IsNullOrWhiteSpace(item.Url))
            .ToList();
        var familyProductTitles = familyProducts.ToDictionary(item => item.Id, item => item.Title);
        var familyMediaItems = familyMediaSelection
            .GroupBy(item => CatalogImageIdentity.Key(item.Url), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProductFamilyMediaView(
                group.First().Url,
                group.Select(item => item.Media.MediaId).Distinct().ToList(),
                group.Select(item => familyProductTitles.GetValueOrDefault(item.Media.ProductId, "Ürün")).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
        var familyMediaUrls = familyMediaItems.Select(item => item.Url).ToList();
        var familyOrderedMediaUrls = CatalogImageIdentity.DistinctDisplayUrls(familyMediaRows
            .Select(item => CatalogMediaDisplay.Url(item.AssetId, item.Classification, item.Url)));
        return ServiceResult<ProductView>.Ok(primaryView with
        {
            Variants = allVariants,
            TotalStock = allVariants.Sum(x => x.OnHand),
            StartingPrice = CatalogPricePolicy.MinimumPositivePrice(familyViews.SelectMany(view => view.Variants.Select(variant => CatalogPricePolicy.EffectiveSalePrice(variant.SalePrice, variant.DefaultSalePrice, view.DefaultSalePrice)))),
            Options = allOptions,
            FamilyMediaUrls = familyMediaUrls,
            FamilyMediaItems = familyMediaItems,
            FamilyOrderedMediaUrls = familyViews.Any(view => view.HasCustomMediaOrder) ? familyOrderedMediaUrls : null,
            FamilyProductIds = familyViews.Select(view => view.Id).ToList()
        });
    }

    public async Task<ServiceResult<ProductView>> UpdateProductAsync(Guid tenantId, Guid id, long expectedVersion, UpdateProductCommand command, CancellationToken cancellationToken)
    {
        var product = await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (product is null) return NotFound<ProductView>(); if (product.Version != expectedVersion) return Precondition<ProductView>(product.Version);
        var effectiveListPrice = command.DefaultListPrice ?? product.DefaultListPrice;
        var effectiveSalePrice = command.DefaultSalePrice ?? product.DefaultSalePrice;
        if (command.DefaultListPrice is < 0 || command.DefaultSalePrice is < 0) return Invalid<ProductView>("prices", "Fiyat negatif olamaz.");
        if (command.WarrantyMonths is < 0 or > 240) return Invalid<ProductView>("warrantyMonths", "Garanti süresi 0-240 ay arasında olmalıdır.");
        if (command.TrendyolWebColorValueId is Guid webColorValueId && webColorValueId != Guid.Empty && !await db.AttributeValues.AnyAsync(x => x.TenantId == tenantId && x.Id == webColorValueId && x.IsActive, cancellationToken)) return Invalid<ProductView>("trendyolWebColorValueId", "Trendyol Web Color değeri bulunamadı.");
        if (effectiveListPrice is decimal listPrice && effectiveSalePrice is decimal salePrice && listPrice < salePrice) return Invalid<ProductView>("prices", "Liste fiyatı satış fiyatından küçük olamaz.");
        var validation = await ValidateProductReferencesAsync(tenantId, command.Title, command.CategoryId, command.BrandId, cancellationToken); if (validation is not null) return ServiceResult<ProductView>.Fail(validation.Code, validation.Message, validation.Status, validation.FieldErrors);
        if (command.Attributes is not null)
        {
            var attributeValidation = await ValidateAttributesAsync(tenantId, command.CategoryId, command.Attributes, cancellationToken); if (attributeValidation is not null) return ServiceResult<ProductView>.Fail(attributeValidation.Code, attributeValidation.Message, attributeValidation.Status, attributeValidation.FieldErrors);
        }
        var variantsToCreate = command.VariantsToCreate ?? [];
        var variantUpdates = command.VariantUpdates ?? [];
        if (variantUpdates.Any(variant => variant.CostPrice is < 0) || variantsToCreate.Any(variant => variant.CostPrice is < 0)) return Invalid<ProductView>("variants", "Maliyet negatif olamaz.");
        if (variantUpdates.Any(variant => variant.Desi is <= 0)) return Invalid<ProductView>("variants", "Desi sıfırdan büyük olmalıdır.");
        if (variantUpdates.Any(variant => variant.DefaultListPrice is < 0 || variant.DefaultSalePrice is < 0) || variantsToCreate.Any(variant => variant.DefaultListPrice is < 0 || variant.DefaultSalePrice is < 0)) return Invalid<ProductView>("variants", "Varyant fiyatı negatif olamaz.");
        if (variantsToCreate.Any(variant => variant.DefaultListPrice is decimal variantList && variant.DefaultSalePrice is decimal variantSale && variantList < variantSale)) return Invalid<ProductView>("variantsToCreate", "Varyant liste fiyatı satış fiyatından küçük olamaz.");
        // Status updates apply to every existing sale row as well. Load the rows
        // whenever status is present; otherwise a save that only changes the
        // product status leaves its variants on their previous status.
        var familyVariantProductIds = await ProductFamilyProductIdsAsync(tenantId, product, cancellationToken);
        var variantIdsToDelete = (command.VariantIdsToDelete ?? []).Distinct().ToList();
        var existingVariants = variantsToCreate.Count > 0 || variantUpdates.Count > 0 || variantIdsToDelete.Count > 0 || command.Status is not null
            ? await db.ProductVariants.Where(x => x.TenantId == tenantId && familyVariantProductIds.Contains(x.ProductId)).ToListAsync(cancellationToken)
            : [];
        if (variantIdsToDelete.Count > 0)
        {
            if (variantIdsToDelete.Any(deleteId => existingVariants.All(existing => existing.Id != deleteId))) return Invalid<ProductView>("variantIdsToDelete", "Silinecek varyant bu ürün ailesinde bulunamadı.");
            if (variantUpdates.Any(update => variantIdsToDelete.Contains(update.Id))) return Invalid<ProductView>("variantIdsToDelete", "Aynı varyant hem güncellenip hem silinemez.");
            if (existingVariants.Count(existing => !variantIdsToDelete.Contains(existing.Id)) + variantsToCreate.Count < 1) return Invalid<ProductView>("variantIdsToDelete", "Ürünün en az bir varyantı kalmalıdır; silmeden önce yeni varyant oluşturun.");
            var deletingInventoryIds = await db.InventoryItems.AsNoTracking().Where(x => x.TenantId == tenantId && variantIdsToDelete.Contains(x.VariantId)).Select(x => x.Id).ToArrayAsync(cancellationToken);
            if (deletingInventoryIds.Length > 0 && await db.ReturnStockDispositions.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && deletingInventoryIds.Contains(x.InventoryItemId), cancellationToken))
                return Conflict<ProductView>("VARIANT_DELETE_BLOCKED", "Varyant iade stok hareketine bağlı olduğu için silinemiyor.");
            var deletingProductIds = existingVariants.Where(existing => variantIdsToDelete.Contains(existing.Id)).Select(existing => existing.ProductId).Distinct().ToArray();
            var variantDeleteBlock = await StockHistoryDeleteBlockAsync(tenantId, deletingProductIds, [.. variantIdsToDelete], deletingInventoryIds, cancellationToken);
            if (variantDeleteBlock is not null)
                return Conflict<ProductView>("VARIANT_DELETE_BLOCKED", $"Varyant {variantDeleteBlock} bağlı olduğu için silinemiyor. Stok geçmişini korumak için ürünü arşivleyin.");
        }
        var changedVariantIds = new HashSet<Guid>();
        foreach (var update in variantUpdates)
        {
            var existing = existingVariants.SingleOrDefault(variant => variant.Id == update.Id);
            if (existing is null) continue;
            // K2: the products import bumps only the variant's version, so the product version above does not see
            // it. A sent cost or desi is written only over the value the editor was loaded with; one set meanwhile
            // is refused instead of silently overwritten. A value already equal to the sent one is no conflict.
            if (update.CostPrice is decimal sentCost && existing.CostPrice != NonNegativeOrNull(sentCost) && existing.CostPrice != update.LoadedCostPrice)
                return Conflict<ProductView>("VARIANT_COST_CHANGED", $"{existing.Sku} varyantının maliyeti bu form açıkken başka bir işlemle (ör. ürün içe aktarma) değişti; güncel maliyet {(existing.CostPrice is decimal storedCost ? storedCost.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("tr-TR")) + " TL" : "boş")}. Sayfayı yenileyip maliyeti tekrar girin.");
            if (update.Desi is decimal sentDesi && existing.Desi != sentDesi && existing.Desi != update.LoadedDesi)
                return Conflict<ProductView>("VARIANT_DESI_CHANGED", $"{existing.Sku} varyantının desisi bu form açıkken başka bir işlemle (ör. ürün içe aktarma) değişti; güncel desi {(existing.Desi is decimal storedDesi ? storedDesi.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("tr-TR")) : "boş")}. Sayfayı yenileyip desiyi tekrar girin.");
            var effectiveVariantListPrice = update.DefaultListPrice ?? existing.DefaultListPrice ?? product.DefaultListPrice;
            var effectiveVariantSalePrice = update.DefaultSalePrice ?? existing.DefaultSalePrice ?? product.DefaultSalePrice;
            if (effectiveVariantListPrice is decimal variantList && effectiveVariantSalePrice is decimal variantSale && variantList < variantSale)
                return Invalid<ProductView>("variantUpdates", "Varyant liste fiyatı satış fiyatından küçük olamaz.");
        }
        if (variantUpdates.Count > 0)
        {
            if (variantUpdates.Select(x => x.Id).Distinct().Count() != variantUpdates.Count || variantUpdates.Any(x => !existingVariants.Any(existing => existing.Id == x.Id))) return Invalid<ProductView>("variantUpdates", "Güncellenecek varyant ürün kaydına ait değil.");
            var optionUpdates = variantUpdates.Where(x => x.Options is not null).ToList();
            foreach (var update in optionUpdates)
            {
                var attributeValidation = await ValidateAttributeValuesAsync(tenantId, update.Attributes ?? [], cancellationToken);
                if (attributeValidation is not null) return ServiceResult<ProductView>.Fail(attributeValidation.Code, attributeValidation.Message, attributeValidation.Status, attributeValidation.FieldErrors);
            }
            if (optionUpdates.Count > 0)
            {
                var optionValidation = await ValidateVariantOptionsAsync(tenantId, command.CategoryId, optionUpdates.Select(update => new CreateVariantCommand(update.Sku, update.Barcode, update.ModelCode, update.Options, Attributes: update.Attributes)).ToList(), cancellationToken);
                if (optionValidation is not null) return ServiceResult<ProductView>.Fail(optionValidation.Code, optionValidation.Message, optionValidation.Status, optionValidation.FieldErrors);
            }
            var updatesById = variantUpdates.ToDictionary(x => x.Id);
            var proposedSkus = existingVariants.Select(variant => Normalize(updatesById.TryGetValue(variant.Id, out var update) ? update.Sku : variant.Sku)).ToArray();
            if (proposedSkus.Any(string.IsNullOrWhiteSpace) || proposedSkus.Distinct().Count() != proposedSkus.Length) return Invalid<ProductView>("variantUpdates", "Stok kodları boş veya tekrarlı olamaz.");
            var proposedBarcodes = existingVariants.Select(variant => updatesById.TryGetValue(variant.Id, out var update) ? NormalizeBarcode(update.Barcode) : NormalizeBarcode(variant.Barcode)).OfType<string>().ToArray();
            if (proposedBarcodes.Distinct().Count() != proposedBarcodes.Length) return Conflict<ProductView>("BARCODE_CONFLICT_REVIEW_REQUIRED", "Barkodlar ürün içinde benzersiz olmalıdır.");
            var proposedSkuKeys = CatalogKeyNormalizer.LookupKeys(existingVariants.Select(variant => updatesById.TryGetValue(variant.Id, out var update) ? update.Sku : variant.Sku));
            var proposedBarcodeKeys = CatalogKeyNormalizer.LookupKeys(existingVariants.Select(variant => updatesById.TryGetValue(variant.Id, out var update) ? update.Barcode : variant.Barcode));
            if (await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId && !familyVariantProductIds.Contains(x.ProductId) && (proposedSkuKeys.Contains(x.SkuNormalized) || x.BarcodeNormalized != null && proposedBarcodeKeys.Contains(x.BarcodeNormalized)), cancellationToken)) return Conflict<ProductView>("VARIANT_CODE_CONFLICT_REVIEW_REQUIRED", "Stok kodu veya barkod başka bir varyantla çakışıyor.");
            var updatedAt = timeProvider.GetUtcNow();
            // O15: the editor sends every row of the model-code family on each save. Only a row whose
            // data really changes gets a new version, and only such a row makes its product a local edit.
            var attributeUpdateIds = variantUpdates.Where(update => update.Attributes is not null).Select(update => update.Id).ToArray();
            var storedVariantAttributes = attributeUpdateIds.Length == 0
                ? []
                : await db.ProductAttributeAssignments.AsNoTracking().Where(x => x.TenantId == tenantId && x.VariantId != null && attributeUpdateIds.Contains(x.VariantId.Value)).ToListAsync(cancellationToken);
            foreach (var update in variantUpdates)
            {
                var variant = existingVariants.Single(x => x.Id == update.Id);
                var dataChanged = VariantDataChanged(variant, update, storedVariantAttributes.Where(x => x.VariantId == variant.Id));
                if (dataChanged) changedVariantIds.Add(variant.Id);
                // D2: a stored key in a legacy form ("ABC_01", "KıRMıZı") that still names the same code is
                // rewritten to the shared key on every save, so exact-key lookups (order lines, imports) find
                // it. This is safe under the unique (tenant, key) indexes: the shared keys of every family row
                // were checked above to be distinct inside the family and absent outside it. Assigning an
                // unchanged value does not mark the row modified, and a key rewrite is not a data change.
                if (CatalogKeyNormalizer.SameKey(variant.SkuNormalized, update.Sku)) variant.SkuNormalized = Normalize(update.Sku);
                if (variant.BarcodeNormalized is not null && CatalogKeyNormalizer.SameKey(variant.BarcodeNormalized, update.Barcode)) variant.BarcodeNormalized = NormalizeBarcode(update.Barcode);
                if (!dataChanged && variant.SortOrder == Math.Max(0, update.SortOrder)) continue;
                variant.Sku = update.Sku.Trim(); variant.SkuNormalized = Normalize(update.Sku); variant.Barcode = NullTrim(update.Barcode); variant.BarcodeNormalized = NormalizeBarcode(update.Barcode); variant.ModelCode = NullTrim(update.ModelCode); if (update.CostPrice is decimal variantCost) variant.CostPrice = NonNegativeOrNull(variantCost); if (update.Desi is decimal variantDesi) variant.Desi = variantDesi; if (update.DefaultListPrice is decimal variantDefaultListPrice) variant.DefaultListPrice = variantDefaultListPrice; if (update.DefaultSalePrice is decimal variantDefaultSalePrice) variant.DefaultSalePrice = variantDefaultSalePrice; variant.SortOrder = Math.Max(0, update.SortOrder);
                if (update.Options is not null) variant.OptionSignature = Signature(update.Options);
                variant.UpdatedAt = updatedAt; variant.Version++;
            }
            var optionUpdatesByProduct = variantUpdates.Where(update => update.Options is not null && changedVariantIds.Contains(update.Id))
                .GroupBy(update => existingVariants.Single(variant => variant.Id == update.Id).ProductId);
            foreach (var productOptionUpdates in optionUpdatesByProduct)
            {
                var updatedRows = productOptionUpdates.Select(update => existingVariants.Single(variant => variant.Id == update.Id)).ToList();
                var updatedIds = updatedRows.Select(variant => variant.Id).ToArray();
                var currentOptions = await db.VariantOptionValues.Where(x => x.TenantId == tenantId && updatedIds.Contains(x.VariantId)).ToListAsync(cancellationToken);
                db.VariantOptionValues.RemoveRange(currentOptions);
                var optionCommands = productOptionUpdates.Select(update => new CreateVariantCommand(update.Sku, update.Barcode, update.ModelCode, update.Options, Attributes: update.Attributes)).ToList();
                await PersistVariantOptionsAsync(tenantId, productOptionUpdates.Key, updatedRows, optionCommands, cancellationToken);
            }
            var attributeUpdates = variantUpdates.Where(update => update.Attributes is not null && changedVariantIds.Contains(update.Id)).ToList();
            if (attributeUpdates.Count > 0)
            {
                var updatedIds = attributeUpdates.Select(update => update.Id).ToArray();
                var currentAssignments = await db.ProductAttributeAssignments.Where(x => x.TenantId == tenantId && updatedIds.Contains(x.VariantId ?? Guid.Empty)).ToListAsync(cancellationToken);
                db.ProductAttributeAssignments.RemoveRange(currentAssignments);
                foreach (var update in attributeUpdates) db.ProductAttributeAssignments.AddRange((update.Attributes ?? []).Select(attribute => Assignment(tenantId, existingVariants.Single(variant => variant.Id == update.Id).ProductId, update.Id, attribute)));
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
            var createSkuKeys = CatalogKeyNormalizer.LookupKeys(variantsToCreate.Select(x => x.Sku));
            if (await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId && createSkuKeys.Contains(x.SkuNormalized) && !variantIdsToDelete.Contains(x.Id), cancellationToken)) return Conflict<ProductView>("SKU_CONFLICT_REVIEW_REQUIRED", "SKU başka bir varyantla çakışıyor; otomatik birleştirme yapılmadı.");
            var normalizedBarcodes = variantsToCreate.Select(x => NormalizeBarcode(x.Barcode)).OfType<string>().ToArray();
            var createBarcodeKeys = CatalogKeyNormalizer.LookupKeys(variantsToCreate.Select(x => x.Barcode));
            if (normalizedBarcodes.Distinct().Count() != normalizedBarcodes.Length || await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId && x.BarcodeNormalized != null && createBarcodeKeys.Contains(x.BarcodeNormalized) && !variantIdsToDelete.Contains(x.Id), cancellationToken)) return Conflict<ProductView>("BARCODE_CONFLICT_REVIEW_REQUIRED", "Barkod başka bir varyantla çakışıyor; otomatik birleştirme yapılmadı.");

            var globalAssignments = command.Attributes ?? await db.ProductAttributeAssignments.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == id && x.VariantId == null).Select(x => new ProductAttributeCommand(x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue, x.SortOrder)).ToListAsync(cancellationToken);
            if (command.CategoryId is Guid categoryId)
            {
                var requiredAttributeIds = await db.CategoryAttributeRequirements.AsNoTracking().Where(x => x.TenantId == tenantId && x.CategoryId == categoryId && (x.IsPanelScoped || x.Role == "OPTION") && x.IsRequired).Select(x => x.AttributeId).ToListAsync(cancellationToken);
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
            var newVariants = variantsToCreate.Select((variant, index) => new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = id, SortOrder = Math.Max(0, variant.SortOrder), Sku = variant.Sku.Trim(), SkuNormalized = Normalize(variant.Sku), Barcode = NullTrim(variant.Barcode), BarcodeNormalized = NormalizeBarcode(variant.Barcode), ModelCode = NullTrim(variant.ModelCode), OptionSignature = Signature(variant.Options), Status = newVariantStatus, Weight = PositiveOrNull(variant.Weight), Width = PositiveOrNull(variant.Width), Height = PositiveOrNull(variant.Height), Length = PositiveOrNull(variant.Length), Desi = PositiveOrNull(variant.Desi), CostPrice = NonNegativeOrNull(variant.CostPrice), DefaultListPrice = variant.DefaultListPrice, DefaultSalePrice = variant.DefaultSalePrice, CreatedAt = now, UpdatedAt = now }).ToList();
            db.ProductVariants.AddRange(newVariants);
            for (var index = 0; index < newVariants.Count; index++) db.ProductAttributeAssignments.AddRange((variantsToCreate[index].Attributes ?? []).Select(x => Assignment(tenantId, id, newVariants[index].Id, x)));
            await PersistVariantOptionsAsync(tenantId, id, newVariants, variantsToCreate, cancellationToken);
            await EnsureMainInventoryAsync(tenantId, newVariants, cancellationToken);
        }
        // O15: the editor always sends the status it shows, which is the edited product's. A status the
        // user did not change touches nothing; a changed one moves the edited product, its rows and the
        // family products that were on the same status, never a sibling that differs (an archived colour
        // stays archived when the family is activated).
        var previousStatus = product.Status;
        var productStatus = command.Status == "ACTIVE" ? ProductStatus.Active : (command.Status == "ARCHIVED" ? ProductStatus.Archived : ProductStatus.Draft);
        var statusChanged = command.Status is not null && productStatus != previousStatus;
        var familyStatusProductIds = statusChanged
            ? (await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && familyVariantProductIds.Contains(x.Id) && x.Id != id && x.Status == previousStatus).Select(x => x.Id).ToListAsync(cancellationToken)).ToHashSet()
            : [];
        // K5: other products of the model-code family whose rows this save changes are local edits too.
        var familyProductIdsToProtect = existingVariants
            .Where(variant => variant.ProductId != id && (familyStatusProductIds.Contains(variant.ProductId) || variantIdsToDelete.Contains(variant.Id) || changedVariantIds.Contains(variant.Id)))
            .Select(variant => variant.ProductId)
            .ToHashSet();
        // A sibling whose every row is deleted keeps no empty ACTIVE product card behind: it is archived.
        var emptiedFamilyProductIds = existingVariants
            .GroupBy(variant => variant.ProductId)
            .Where(group => group.Key != id && group.All(variant => variantIdsToDelete.Contains(variant.Id)))
            .Select(group => group.Key)
            .ToHashSet();
        await using var variantPurgeTransaction = variantIdsToDelete.Count > 0 && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        if (variantIdsToDelete.Count > 0)
        {
            var purgeIds = variantIdsToDelete.ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                CREATE TEMP TABLE IF NOT EXISTS purge_variant_ids ("Id" uuid PRIMARY KEY) ON COMMIT DROP;
                TRUNCATE purge_variant_ids;
                INSERT INTO purge_variant_ids ("Id") SELECT unnest({{purgeIds}});

                -- Sipariş geçmişi korunur; yalnızca silinen varyantla canlı bağlantı kaldırılır.
                UPDATE sales.order_lines SET "VariantId"=NULL
                WHERE "TenantId"={{tenantId}} AND "VariantId" IN (SELECT "Id" FROM purge_variant_ids);

                DELETE FROM catalog.import_decisions
                WHERE "TenantId"={{tenantId}}
                  AND "CandidateId" IN (SELECT c."Id" FROM catalog.import_match_candidates c WHERE c."TenantId"={{tenantId}} AND c."VariantId" IN (SELECT "Id" FROM purge_variant_ids));
                DELETE FROM catalog.import_match_candidates c
                WHERE c."TenantId"={{tenantId}} AND c."VariantId" IN (SELECT "Id" FROM purge_variant_ids);
                DELETE FROM catalog.field_provenance f
                WHERE f."TenantId"={{tenantId}} AND f."VariantId" IN (SELECT "Id" FROM purge_variant_ids);
                DELETE FROM catalog.external_identifier_aliases a
                WHERE a."TenantId"={{tenantId}} AND a."EntityType"='VARIANT' AND a."LocalId" IN (SELECT "Id" FROM purge_variant_ids);

                DELETE FROM inventory.channel_price_history h
                USING inventory.channel_offers o, purge_variant_ids v
                WHERE h."TenantId"={{tenantId}} AND h."OfferId"=o."Id" AND o."VariantId"=v."Id";
                DELETE FROM inventory.channel_offers o
                USING purge_variant_ids v
                WHERE o."TenantId"={{tenantId}} AND o."VariantId"=v."Id";
                DELETE FROM inventory.channel_inventory_observations o
                USING purge_variant_ids v
                WHERE o."TenantId"={{tenantId}} AND o."VariantId"=v."Id";
                DELETE FROM inventory.stock_reservations r
                USING inventory.inventory_items i, purge_variant_ids v
                WHERE r."TenantId"={{tenantId}} AND r."InventoryItemId"=i."Id" AND i."VariantId"=v."Id";
                DELETE FROM inventory.stock_ledger_entries l
                USING inventory.inventory_items i, purge_variant_ids v
                WHERE l."TenantId"={{tenantId}} AND l."InventoryItemId"=i."Id" AND i."VariantId"=v."Id";
                DELETE FROM inventory.inventory_items i
                USING purge_variant_ids v
                WHERE i."TenantId"={{tenantId}} AND i."VariantId"=v."Id";

                DELETE FROM catalog.marketplace_listing_states s
                USING purge_variant_ids v
                WHERE s."TenantId"={{tenantId}} AND s."VariantId"=v."Id";
                DELETE FROM catalog.marketplace_variant_links l
                USING purge_variant_ids v
                WHERE l."TenantId"={{tenantId}} AND l."VariantId"=v."Id";
                DELETE FROM catalog.channel_media_order o
                USING catalog.product_media m, purge_variant_ids v
                WHERE o."TenantId"={{tenantId}} AND o."MediaId"=m."Id" AND m."VariantId"=v."Id";
                DELETE FROM catalog.product_media m
                USING purge_variant_ids v
                WHERE m."TenantId"={{tenantId}} AND m."VariantId"=v."Id";
                DELETE FROM catalog.product_attribute_assignments a
                USING purge_variant_ids v
                WHERE a."TenantId"={{tenantId}} AND a."VariantId"=v."Id";
                DELETE FROM catalog.variant_option_values x
                USING purge_variant_ids v
                WHERE x."TenantId"={{tenantId}} AND x."VariantId"=v."Id";
                DELETE FROM catalog.product_variants pv
                USING purge_variant_ids v
                WHERE pv."TenantId"={{tenantId}} AND pv."Id"=v."Id";
                """, cancellationToken);
            foreach (var removed in existingVariants.Where(variant => variantIdsToDelete.Contains(variant.Id)).ToList())
            {
                db.Entry(removed).State = EntityState.Detached;
                existingVariants.Remove(removed);
            }
        }
        product.Title = command.Title.Trim(); product.Description = command.Description.Trim(); product.CategoryId = command.CategoryId; product.BrandId = command.BrandId;
        if (command.DefaultListPrice is decimal defaultListPrice) product.DefaultListPrice = defaultListPrice;
        if (command.DefaultSalePrice is decimal defaultSalePrice) product.DefaultSalePrice = defaultSalePrice;
        // Sent only by the editor: 0 clears the warranty; a caller that leaves it out keeps it.
        if (command.WarrantyMonths is int warrantyMonths) product.WarrantyMonths = warrantyMonths > 0 ? warrantyMonths : null;
        // Sent only by the editor: Guid.Empty = automatic (each variant's own Renk), a value = that colour on Trendyol.
        if (command.TrendyolWebColorValueId is Guid trendyolWebColor) product.TrendyolWebColorValueId = trendyolWebColor == Guid.Empty ? null : trendyolWebColor;
        product.Version++; product.UpdatedAt = timeProvider.GetUtcNow();
        if (command.Status != null)
        {
            product.Status = productStatus;
            foreach (var variant in existingVariants.Where(variant => statusChanged && variant.Status != productStatus && (variant.ProductId == id || familyStatusProductIds.Contains(variant.ProductId))))
            { variant.Status = productStatus; variant.UpdatedAt = product.UpdatedAt; variant.Version++; }
        }
        if (command.Attributes is not null)
        {
            var currentAssignments = await db.ProductAttributeAssignments.Where(x => x.TenantId == tenantId && x.ProductId == id && x.VariantId == null).ToListAsync(cancellationToken);
            db.ProductAttributeAssignments.RemoveRange(currentAssignments);
            db.ProductAttributeAssignments.AddRange(command.Attributes.Select(x => Assignment(tenantId, id, null, x)));
        }
        await MarkMarketplaceLinksDirtyAsync(tenantId, id, cancellationToken);
        if (familyProductIdsToProtect.Count > 0)
        {
            var siblings = await db.Products.Where(x => x.TenantId == tenantId && familyProductIdsToProtect.Contains(x.Id)).ToListAsync(cancellationToken);
            foreach (var sibling in siblings)
            {
                if (emptiedFamilyProductIds.Contains(sibling.Id)) { sibling.Status = ProductStatus.Archived; sibling.ArchivedAt ??= product.UpdatedAt; }
                else if (familyStatusProductIds.Contains(sibling.Id)) sibling.Status = productStatus;
                sibling.UpdatedAt = product.UpdatedAt;
                sibling.Version++;
            }
            await CatalogLocalEdits.MarkLinksDirtyAsync(db, tenantId, familyProductIdsToProtect, CatalogLocalEdits.ProductField, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        if (variantPurgeTransaction is not null) await variantPurgeTransaction.CommitAsync(cancellationToken);
        return await GetProductAsync(tenantId, id, cancellationToken);
    }

    public async Task<ServiceResult<ProductView>> ArchiveProductAsync(Guid tenantId, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var product = await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (product is null) return NotFound<ProductView>(); if (product.Version != expectedVersion) return Precondition<ProductView>(product.Version); product.Status = ProductStatus.Archived; product.ArchivedAt = timeProvider.GetUtcNow(); product.UpdatedAt = product.ArchivedAt.Value; product.Version++; var variants = await db.ProductVariants.Where(x => x.TenantId == tenantId && x.ProductId == id).ToListAsync(cancellationToken); foreach (var variant in variants) { variant.Status = ProductStatus.Archived; variant.UpdatedAt = product.UpdatedAt; variant.Version++; }
        // D4 (K8): archiving is a status flip only. Images stay as they are: an archived image is a removed one
        // and cannot be told apart from it later, so an archive that removed them could not be undone.
        await MarkMarketplaceLinksDirtyAsync(tenantId, id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken); return await GetProductAsync(tenantId, id, cancellationToken);
    }

    private Task MarkMarketplaceLinksDirtyAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken) =>
        CatalogLocalEdits.MarkLinksDirtyAsync(db, tenantId, [productId], CatalogLocalEdits.ProductField, cancellationToken);

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

    public async Task<ServiceResult<BulkPublicationResult>> EnqueueBulkPublicationAsync(Guid tenantId, Guid userId, BulkPublicationCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var productIds = (command.ProductIds ?? []).Distinct().ToList();
        var connectionIds = (command.ConnectionIds ?? []).Distinct().ToList();
        if (productIds.Count == 0 || connectionIds.Count == 0) return Invalid<BulkPublicationResult>("productIds", "En az bir ürün ve bir platform seçin.");
        if (productIds.Count > 1000) return Invalid<BulkPublicationResult>("productIds", "Tek seferde en fazla 1000 ürün kartı yayınlanabilir.");
        var products = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && productIds.Contains(x.Id)).ToListAsync(cancellationToken);
        var connectionCount = await db.PlatformConnections.AsNoTracking().CountAsync(x => x.TenantId == tenantId && connectionIds.Contains(x.Id), cancellationToken);
        if (products.Count != productIds.Count || connectionCount != connectionIds.Count) return Invalid<BulkPublicationResult>("productIds", "Seçilen ürün veya platform bulunamadı.");
        var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && productIds.Contains(x.ProductId)).ToListAsync(cancellationToken);
        var variantIds = variants.Select(x => x.Id).ToList();
        var offers = await db.ChannelOffers.AsNoTracking().Where(x => x.TenantId == tenantId && variantIds.Contains(x.VariantId)).ToListAsync(cancellationToken);
        var links = await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == tenantId && variantIds.Contains(x.VariantId)).Select(x => new { x.ConnectionId, x.VariantId }).ToListAsync(cancellationToken);
        var profiles = await db.ChannelListingProfiles.AsNoTracking().Where(x => x.TenantId == tenantId && productIds.Contains(x.ProductId) && connectionIds.Contains(x.ConnectionId)).ToListAsync(cancellationToken);
        var queued = 0; var skipped = 0; var failed = new List<BulkPublicationFailure>();
        foreach (var product in products.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
        {
            var productVariants = variants.Where(x => x.ProductId == product.Id).ToList();
            foreach (var connectionId in connectionIds)
            {
                // A card already on this marketplace (a variant matched there, live, or still being published) is left as it is.
                var profile = profiles.FirstOrDefault(x => x.ProductId == product.Id && x.ConnectionId == connectionId);
                if (productVariants.Any(variant => links.Any(link => link.VariantId == variant.Id && link.ConnectionId == connectionId))
                    || profile?.ActualStatus?.Trim().ToUpperInvariant() is "LIVE" or "ACTIVE" or "ON_SALE" || IsPlatformUpdateInProgress(profile?.ActualStatus))
                {
                    skipped++;
                    continue;
                }
                // The marketplace price follows the general price, as in the editor; VAT and its policy come from the
                // variant's offer on another marketplace, since the product holds none of its own.
                string? priceError = null;
                var newOffers = new List<ChannelOffer>();
                foreach (var variant in productVariants.Where(variant => !offers.Any(offer => offer.VariantId == variant.Id && offer.ConnectionId == connectionId)))
                {
                    var template = offers.FirstOrDefault(offer => offer.VariantId == variant.Id) ?? offers.FirstOrDefault(offer => productVariants.Any(item => item.Id == offer.VariantId));
                    var salePrice = variant.DefaultSalePrice ?? product.DefaultSalePrice ?? template?.SalePrice;
                    var listPrice = variant.DefaultListPrice ?? product.DefaultListPrice ?? template?.ListPrice ?? salePrice;
                    if (template is null || salePrice is not > 0 || listPrice is null || listPrice < salePrice) { priceError = $"'{variant.Sku}' için genel fiyat veya KDV bilgisi yok; ürünü editörde açıp fiyatını kaydedin."; break; }
                    newOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, VariantId = variant.Id, ListPrice = listPrice.Value, SalePrice = salePrice.Value, Currency = template.Currency, VatRate = template.VatRate, VatInclusion = template.VatInclusion, RoundingMode = template.RoundingMode, SafetyStock = template.SafetyStock, Status = "ACTIVE", PriceVersion = 1, Version = 1 });
                }
                if (priceError is not null)
                {
                    failed.Add(new(product.Id, product.Title, connectionId, "CHANNEL_OFFER_REQUIRED", priceError));
                    continue;
                }
                foreach (var offer in newOffers)
                {
                    db.ChannelOffers.Add(offer);
                    db.ChannelPriceHistory.Add(new ChannelPriceHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OfferId = offer.Id, PriceVersion = 1, ListPrice = offer.ListPrice, SalePrice = offer.SalePrice, Currency = offer.Currency, Reason = "Toplu yayın fiyatı", ActorSource = $"USER:{userId:D}", EffectiveAt = timeProvider.GetUtcNow() });
                }
                offers.AddRange(newOffers);
                var trackedProfile = await db.ChannelListingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == product.Id && x.ConnectionId == connectionId, cancellationToken);
                if (trackedProfile is null) db.ChannelListingProfiles.Add(new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, ConnectionId = connectionId, DesiredStatus = "DRAFT", ActualStatus = "UNKNOWN", Enabled = true });
                else if (!trackedProfile.Enabled) { trackedProfile.Enabled = true; trackedProfile.Version++; }
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
                var enqueued = await EnqueuePublicationAsync(tenantId, product.Id, connectionId, $"{idempotencyKey}:{product.Id:N}:{connectionId:N}", correlationId, cancellationToken);
                db.ChangeTracker.Clear();
                if (enqueued.Succeeded) queued++;
                else failed.Add(new(product.Id, product.Title, connectionId, enqueued.Error!.Code, enqueued.Error.Message));
            }
        }
        return ServiceResult<BulkPublicationResult>.Ok(new(queued, skipped, failed));
    }

    public async Task<ServiceResult<Guid>> EnqueuePublicationAsync(Guid tenantId, Guid productId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        if (connection is null || connection.PlatformCode is not ("TRENDYOL" or "HEPSIBURADA" or "SHOPIFY" or "N11" or "PAZARAMA" or "PTTAVM") || !IntegrationRuntimePolicy.IsManualProductWriteReady(connection)) return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Yayın için ACTIVE veya doğrulanmış STAGE marketplace bağlantısı gerekir.", 422);
        if (!IntegrationRuntimePolicy.IsSupportedEnvironment(connection)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Yayın yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (IntegrationRuntimePolicy.IsProduction(connection) && !WritesEnabled(connection.SettingsJson)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        if (connection.PlatformCode == "SHOPIFY")
        {
            if (!WritesEnabled(connection.SettingsJson)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Shopify ürün yayınlamak için global ve bağlantı dış yazma anahtarları açık olmalıdır.", 422);
            if (!await HasShopifyPublicationEvidenceAsync(connection, cancellationToken)) return ServiceResult<Guid>.Fail("SHOPIFY_PUBLICATION_SCOPES_REQUIRED", "Shopify ürün yayını için PRODUCT_WRITE, INVENTORY_WRITE, PUBLICATION_READ ve PUBLICATION_WRITE izinleri aynı bağlantı/ortamda doğrulanmalıdır.", 422);
        }
        if (connection.PlatformCode == "HEPSIBURADA")
        {
            if (!string.Equals(configuration["Hepsiburada:AuthenticationMode"], "BASIC", StringComparison.OrdinalIgnoreCase)) return ServiceResult<Guid>.Fail("HEPSIBURADA_AUTHENTICATION_UNVERIFIED", "Hepsiburada auth biçimi SIT hesabında doğrulanana kadar ürün yayını kuyruğa alınamaz.", 422);
            if (!await HasHepsiburadaPublicationEvidenceAsync(connection, cancellationToken)) return ServiceResult<Guid>.Fail("HEPSIBURADA_WRITE_CAPABILITY_EVIDENCE_REQUIRED", "Hepsiburada bağlantısında bu mağaza ve ortam için doğrulanmış fiyat ve stok yazma kanıtı yok; Entegrasyonlar'dan bağlantıyı test edin.", 422);
        }
        // n11 has no sandbox, so write evidence can only follow a first live write; the
        // global and connection write switches are the gate for product create.
        if (connection.PlatformCode == "N11" && !WritesEnabled(connection.SettingsJson)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "N11'de ürün açmak için global ve bağlantı dış yazma anahtarları açık olmalıdır.", 422);
        if (connection.PlatformCode == "PAZARAMA" && !WritesEnabled(connection.SettingsJson)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Pazarama'da ürün açmak için global ve bağlantı dış yazma anahtarları açık olmalıdır.", 422);
        if (connection.PlatformCode == "PTTAVM" && !WritesEnabled(connection.SettingsJson)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "PTT AVM'ye ürün göndermek için global ve bağlantı dış yazma anahtarları açık olmalıdır.", 422);
        var draftResult = connection.PlatformCode == "SHOPIFY"
            ? await new ShopifyProductPublicationComposer(db, configuration).BuildAsync(tenantId, productId, connectionId, cancellationToken)
            : await new ProductPublicationComposer(db, configuration).BuildAsync(tenantId, productId, connectionId, cancellationToken);
        if (!draftResult.Succeeded) return ServiceResult<Guid>.Fail(draftResult.Error!.Code, draftResult.Error.Message, draftResult.Error.Status, draftResult.Error.FieldErrors);
        var draft = draftResult.Value!;
        var dedup = $"product-create:{connectionId:N}:{productId:N}:{draft.PayloadHash}";
        var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.ProductCreate && x.JobDedupKey == dedup, cancellationToken);
        if (existing is not null && await ReuseOrRetirePublicationJobAsync(existing, dedup, reuseSucceeded: true, cancellationToken))
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
            listing.ExternalBarcode = string.IsNullOrWhiteSpace(variant.Barcode) ? null : variant.Barcode;
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
        var now = timeProvider.GetUtcNow();
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
            Priority = JobPriorityPolicy.For(MarketplaceJobTypes.ProductCreate, userInitiated: true),
            Status = JobStatus.Pending,
            AvailableAt = now,
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
        if (connection is null || connection.PlatformCode is not ("TRENDYOL" or "HEPSIBURADA") || !IntegrationRuntimePolicy.IsManualProductWriteReady(connection)) return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Güncelleme için ACTIVE veya doğrulanmış STAGE marketplace bağlantısı gerekir.", 422);
        if (!IntegrationRuntimePolicy.IsSupportedEnvironment(connection)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Güncelleme yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (IntegrationRuntimePolicy.IsProduction(connection) && !WritesEnabled(connection.SettingsJson)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        var build = await new ProductUpdateComposer(db, configuration).BuildAsync(tenantId, productId, connectionId, cancellationToken);
        if (!build.Succeeded) return ServiceResult<Guid>.Fail(build.Error!.Code, build.Error.Message, build.Error.Status, build.Error.FieldErrors);
        var draft = build.Value!;
        var dedup = $"product-update:{connectionId:N}:{productId:N}:{draft.Publication.PayloadHash}";
        var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.ProductUpdate && x.JobDedupKey == dedup, cancellationToken);
        if (existing is not null && await ReuseOrRetirePublicationJobAsync(existing, dedup, reuseSucceeded: false, cancellationToken)) { await transaction.CommitAsync(cancellationToken); return ServiceResult<Guid>.Ok(existing.Id); }

        var profile = await db.ChannelListingProfiles.SingleAsync(x => x.TenantId == tenantId && x.Id == draft.ProfileId, cancellationToken);
        profile.DesiredStatus = "LIVE"; profile.ActualStatus = "UPDATE_QUEUED"; profile.LastRejectionCode = null; profile.Version++;
        var states = await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && draft.Variants.Select(v => v.VariantId).Contains(x.VariantId)).ToListAsync(cancellationToken);
        foreach (var state in states) { state.DesiredStatus = "LIVE"; state.PayloadHash = draft.Publication.PayloadHash; state.Version++; }

        var jobId = Guid.CreateVersion7();
        var now = timeProvider.GetUtcNow();
        var phase = draft.Publication.Mode == "APPROVED" ? "SUBMIT_CONTENT" : "SUBMIT_UNAPPROVED";
        var payload = JsonSerializer.Serialize(new ProductUpdateJobPayload(jobId, productId, profile.Id, phase, draft.Publication.Mode, draft.Publication.PayloadHash, draft.Publication.UnapprovedPayloadJson, draft.Publication.ApprovedContentPayloadJson, draft.Publication.ApprovedVariantPayloadJson, draft.Publication.ApprovedDeliveryPayloadJson, null, null));
        db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = tenantId, ConnectionId = connectionId, JobType = MarketplaceJobTypes.ProductUpdate, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = $"{dedup}:{NormalizeKey(idempotencyKey)}", Priority = JobPriorityPolicy.For(MarketplaceJobTypes.ProductUpdate, userInitiated: true), Status = JobStatus.Pending, AvailableAt = now, MaxAttempts = 12, CorrelationId = correlationId, CreatedAt = now, Version = 1 });
        return await SavePublicationJobAsync(transaction, tenantId, MarketplaceJobTypes.ProductUpdate, dedup, jobId, cancellationToken);
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
        if (existing is not null && await ReuseOrRetirePublicationJobAsync(existing, dedup, reuseSucceeded: false, cancellationToken)) { await transaction.CommitAsync(cancellationToken); return ServiceResult<Guid>.Ok(existing.Id); }
        var profile = await db.ChannelListingProfiles.SingleAsync(x => x.TenantId == tenantId && x.Id == draft.ProfileId, cancellationToken);
        profile.DesiredStatus = archived ? "ARCHIVED" : "LIVE"; profile.ActualStatus = archived ? "ARCHIVE_QUEUED" : "UNARCHIVE_QUEUED"; profile.LastRejectionCode = null; profile.Version++;
        var listingVariants = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).ToListAsync(cancellationToken);
        foreach (var listing in listingVariants) { listing.DesiredStatus = archived ? "ARCHIVED" : "LIVE"; listing.ActualStatus = profile.ActualStatus; listing.RejectionCode = null; }
        var states = await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && draft.Variants.Select(v => v.VariantId).Contains(x.VariantId)).ToListAsync(cancellationToken);
        foreach (var state in states) { state.DesiredStatus = archived ? "ARCHIVED" : "LIVE"; state.ActualStatus = profile.ActualStatus; state.LastRejectionCode = null; state.PayloadHash = draft.PayloadHash; state.Version++; }
        var jobId = Guid.CreateVersion7(); var now = timeProvider.GetUtcNow();
        var payload = JsonSerializer.Serialize(new ProductArchiveJobPayload(jobId, productId, profile.Id, archived, "SUBMIT", draft.PayloadHash, draft.PayloadJson, null, now, now.AddHours(24)));
        db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = tenantId, ConnectionId = connectionId, JobType = MarketplaceJobTypes.ProductArchive, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = $"{dedup}:{NormalizeKey(idempotencyKey)}", Priority = JobPriorityPolicy.For(MarketplaceJobTypes.ProductArchive, userInitiated: true), Status = JobStatus.Pending, AvailableAt = now, MaxAttempts = 20, CorrelationId = correlationId, CreatedAt = now, Version = 1 });
        return await SavePublicationJobAsync(transaction, tenantId, MarketplaceJobTypes.ProductArchive, dedup, jobId, cancellationToken);
    }

    // Pazarama: close (or reopen) mapped products for sale by barcode. There is no listing profile behind a
    // mapped product, so the job carries the barcodes itself; the connection write switch is the gate.
    public async Task<ServiceResult<Guid>> EnqueuePazaramaSaleStatusAsync(Guid tenantId, Guid connectionId, IReadOnlyList<string> barcodes, bool close, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "PAZARAMA", cancellationToken);
        if (connection is null || !IntegrationRuntimePolicy.IsManualProductWriteReady(connection)) return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Satışa açma/kapatma için aktif veya doğrulanmış bir Pazarama bağlantısı gerekir.", 422);
        if (!WritesEnabled(connection.SettingsJson)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        var normalized = barcodes.Select(x => x?.Trim() ?? "").Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (normalized.Count is < 1 or > 100) return ServiceResult<Guid>.Fail("VALIDATION_FAILED", "Bir istekte 1-100 barkod gönderilebilir.", 422);
        // K28: the stored barcode may differ in case from the one sent; compare upper-cased on both sides.
        var upperBarcodes = normalized.Select(code => code.ToUpperInvariant()).ToArray();
        var linked = await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && upperBarcodes.Contains(x.ExternalId.Trim().ToUpper())).Select(x => x.ExternalId).ToListAsync(cancellationToken);
        var unknown = normalized.Where(code => !linked.Any(external => string.Equals(external.Trim(), code, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0) return ServiceResult<Guid>.Fail("PAZARAMA_BARCODE_NOT_LINKED", $"Şu barkodlar panelde bir varyanta bağlı değil: {string.Join(", ", unknown.Take(5))}{(unknown.Count > 5 ? " …" : "")}. Önce Pazarama ürün eşleştirmesini çalıştırın.", 404);

        var dedup = $"pazarama-sale:{connectionId:N}:{close}:{Hash(string.Join('|', normalized))}";
        var existing = await db.IntegrationJobs.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.ProductArchive && x.JobDedupKey == dedup && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled), cancellationToken);
        if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var jobId = Guid.CreateVersion7(); var now = timeProvider.GetUtcNow();
        var payload = JsonSerializer.Serialize(new PazaramaSaleStatusJobPayload(jobId, normalized, close, now));
        db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = tenantId, ConnectionId = connectionId, JobType = MarketplaceJobTypes.ProductArchive, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup + ":" + jobId.ToString("N"), EffectIdempotencyKey = $"{dedup}:{NormalizeKey(idempotencyKey)}", Priority = JobPriorityPolicy.For(MarketplaceJobTypes.ProductArchive, userInitiated: true), Status = JobStatus.Pending, AvailableAt = now, MaxAttempts = 3, CorrelationId = correlationId, CreatedAt = now, Version = 1 });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(jobId);
    }

    public async Task<ServiceResult<Guid>> RequestPublicationStatusRefreshAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken)
    {
        var result = await RequestCardPublicationStatusRefreshAsync(tenantId, productId, connectionId, cancellationToken);
        if (!result.Succeeded) return result;
        // Shopify and n11 hold a model family's colour cards in one marketplace product, so one refresh reads all the
        // cards: each sibling with a publication on this connection gets its own read-only check (one that cannot is skipped).
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleAsync(cancellationToken);
        if (platformCode is not ("SHOPIFY" or "N11")) return result;
        var product = await db.Products.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken);
        var familyIds = await ProductFamilyProductIdsAsync(tenantId, product, cancellationToken);
        var siblings = await db.ChannelListingProfiles.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && familyIds.Contains(x.ProductId) && x.ProductId != productId)
            .Select(x => x.ProductId)
            .ToListAsync(cancellationToken);
        foreach (var siblingId in siblings)
        {
            db.ChangeTracker.Clear();
            await RequestCardPublicationStatusRefreshAsync(tenantId, siblingId, connectionId, cancellationToken);
        }
        return result;
    }

    private async Task<ServiceResult<Guid>> RequestCardPublicationStatusRefreshAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        // Trendyol, Hepsiburada, Shopify and n11 read the publication state back; Hepsiburada by the tracking id of its
        // last import, Shopify and n11 by each variant's stock code.
        if (connection is null || connection.PlatformCode is not ("TRENDYOL" or "HEPSIBURADA" or "SHOPIFY" or "N11") || !IntegrationRuntimePolicy.AllowsManualRead(connection))
            return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Yayın durumunu sorgulamak için etkin Trendyol, Hepsiburada, Shopify veya N11 bağlantısı gerekir.", 422);
        var platformName = connection.PlatformCode switch { "HEPSIBURADA" => "Hepsiburada", "SHOPIFY" => "Shopify", "N11" => "N11", _ => "Trendyol" };

        var profile = await db.ChannelListingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.ConnectionId == connectionId, cancellationToken);
        if (profile is null || !await db.Products.AnyAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken))
            return ServiceResult<Guid>.Fail("PUBLICATION_PROFILE_REQUIRED", "Bu ürün ve mağaza için önce bir yayın işi başlatılmalıdır.", 404);

        var listingVariants = await db.ChannelListingVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id)
            .Select(x => x.VariantId)
            .ToListAsync(cancellationToken);
        if (listingVariants.Count == 0)
            return ServiceResult<Guid>.Fail("PUBLICATION_STATE_REQUIRED", $"Güncellenecek {platformName} yayın kaydı bulunamadı.", 409);

        var states = await db.MarketplaceListingStates
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && listingVariants.Contains(x.VariantId))
            .ToListAsync(cancellationToken);
        var payloadHashes = states.Select(x => x.PayloadHash).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        if (states.Count != listingVariants.Count || payloadHashes.Length != 1)
            return ServiceResult<Guid>.Fail("PUBLICATION_STATE_REQUIRED", $"Yayın durumu eşitlenemedi; önce ürünü {platformName} pazaryerine gönderin.", 409);

        var productJobs = await db.IntegrationJobs.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId
            && (x.JobType == MarketplaceJobTypes.ProductCreate || x.JobType == MarketplaceJobTypes.ProductUpdate || x.JobType == MarketplaceJobTypes.ProductArchive)
            && (x.JobDedupKey.StartsWith($"product-create:{connectionId:N}:{productId:N}:")
                || x.JobDedupKey.StartsWith($"product-update:{connectionId:N}:{productId:N}:")
                || x.JobDedupKey.StartsWith($"product-archive:{connectionId:N}:{productId:N}:")))
            .ToListAsync(cancellationToken);
        if (productJobs.Any(x => x.Status is JobStatus.Pending or JobStatus.Leased or JobStatus.RetryScheduled))
            return ServiceResult<Guid>.Fail("PUBLICATION_WRITE_IN_PROGRESS", "Ürün için devam eden bir yayın veya güncelleme var. İşlem tamamlandıktan sonra durumu yenileyin.", 409);

        // Hepsiburada answers the state of a product import by its tracking id, kept on the last create or update job.
        string? trackingId = null;
        if (connection.PlatformCode == "HEPSIBURADA")
        {
            trackingId = productJobs.OrderByDescending(x => x.CreatedAt).Select(x => ExternalOperationIdOf(x.PayloadJson)).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
            if (trackingId is null)
                return ServiceResult<Guid>.Fail("PUBLICATION_STATE_REQUIRED", "Hepsiburada takip kodu bulunamadı; ürünü Hepsiburada'ya yeniden gönderin.", 409);
        }

        var payloadHash = payloadHashes[0]!;
        var approvalDedup = $"product-approval:{connectionId:N}:{profile.Id:N}:{payloadHash}";
        var manualPrefix = $"{approvalDedup}:manual-v";
        var approvalJobs = await db.IntegrationJobs.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId
            && x.JobType == MarketplaceJobTypes.ProductApprovalReconcile
            && (x.JobDedupKey == approvalDedup || x.JobDedupKey.StartsWith(manualPrefix)))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        var leased = approvalJobs.FirstOrDefault(x => x.Status == JobStatus.Leased);
        if (leased is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<Guid>.Ok(leased.Id);
        }
        var existing = approvalJobs.FirstOrDefault();
        if (existing?.Status is JobStatus.Pending or JobStatus.RetryScheduled)
        {
            existing.Status = JobStatus.Pending;
            existing.AvailableAt = timeProvider.GetUtcNow();
            existing.LastErrorCode = null;
            existing.LastErrorSummary = null;
            existing.CompletedAt = null;
            existing.Version++;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return ServiceResult<Guid>.Ok(existing.Id);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                return ServiceResult<Guid>.Fail("PUBLICATION_REFRESH_CONFLICT", "Yayın durumu aynı anda güncellendi. Biraz sonra yeniden deneyin.", 409);
            }
        }

        var now = timeProvider.GetUtcNow();
        profile.Version++;
        var jobId = Guid.CreateVersion7();
        var dedup = existing is null ? approvalDedup : $"{manualPrefix}{profile.Version}";
        var payload = JsonSerializer.Serialize(new ProductApprovalReconciliationJobPayload(jobId, productId, profile.Id, payloadHash, now, now.AddDays(7), trackingId));
        db.IntegrationJobs.Add(new IntegrationJob
        {
            Id = jobId,
            TenantId = tenantId,
            ConnectionId = connectionId,
            JobType = MarketplaceJobTypes.ProductApprovalReconcile,
            PayloadJson = payload,
            PayloadVersion = 1,
            PayloadHash = Hash(payload),
            JobDedupKey = dedup,
            EffectIdempotencyKey = dedup,
            Priority = JobPriorityPolicy.For(MarketplaceJobTypes.ProductApprovalReconcile, userInitiated: true),
            Status = JobStatus.Pending,
            AvailableAt = now,
            MaxAttempts = (7 * 24 * 12) + 1,
            CorrelationId = $"publication-status:{productId:N}",
            CreatedAt = now,
            Version = 1
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<Guid>.Ok(jobId);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return ServiceResult<Guid>.Fail("PUBLICATION_REFRESH_CONFLICT", "Yayın durumu aynı anda güncellendi. Biraz sonra yeniden deneyin.", 409);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return ServiceResult<Guid>.Fail("PUBLICATION_REFRESH_CONFLICT", "Yayın durumu aynı anda yenilendi. Sayfayı yenileyip tekrar deneyin.", 409);
        }
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

    private static bool IsPlatformUpdateInProgress(string? status) => status?.Trim().ToUpperInvariant() is
        "QUEUED" or "UPDATE_QUEUED" or "BATCH_SUBMITTED" or "BATCH_IN_PROGRESS" or "IMPORT_SUBMITTED" or "IMPORT_IN_PROGRESS" or "UPDATE_SUBMITTED" or "UPDATE_IN_PROGRESS" or
        "APPROVAL_PENDING" or "APPROVAL_PARTIAL_PENDING" or "ARCHIVE_QUEUED" or "ARCHIVE_BATCH_SUBMITTED" or "ARCHIVE_RECONCILING" or
        "UNARCHIVE_QUEUED";

    private bool WritesEnabled(string settingsJson)
    {
        if (!configuration.GetValue<bool>("FeatureFlags:ExternalWrites")) return false;
        try { using var document = JsonDocument.Parse(settingsJson); return document.RootElement.TryGetProperty("ExternalWritesEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True; }
        catch (JsonException) { return false; }
    }

    private static string? ExternalOperationIdOf(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("ExternalOperationId", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<bool> HasHepsiburadaPublicationEvidenceAsync(PlatformConnection connection, CancellationToken cancellationToken)
    {
        // A STAGE (SIT) connection does not require PRODUCT_WRITE: that evidence is produced by the SIT
        // product import itself. PRICE_WRITE/INVENTORY_WRITE stay required; PRODUCTION requires all three.
        var required = HepsiburadaStageCertificationPolicy.PublicationEvidenceCapabilities(connection);
        var capabilities = await db.PlatformCapabilities.AsNoTracking()
            .Where(x => x.TenantId == connection.TenantId && x.ConnectionId == connection.Id && required.Contains(x.Code))
            .ToListAsync(cancellationToken);
        return required.All(code => CapabilityEvidencePolicy.IsVerifiedWriteCapability(capabilities.SingleOrDefault(x => x.Code == code), connection, code));
    }

    private async Task<bool> HasShopifyPublicationEvidenceAsync(PlatformConnection connection, CancellationToken cancellationToken)
    {
        var required = new[]
        {
            MarketplaceCapabilities.ProductWrite,
            MarketplaceCapabilities.InventoryWrite,
            MarketplaceCapabilities.PublicationRead,
            MarketplaceCapabilities.PublicationWrite
        };
        var capabilities = await db.PlatformCapabilities.AsNoTracking()
            .Where(x => x.TenantId == connection.TenantId && x.ConnectionId == connection.Id && required.Contains(x.Code))
            .ToListAsync(cancellationToken);
        return required.All(code => CapabilityEvidencePolicy.IsVerifiedWriteCapability(capabilities.SingleOrDefault(x => x.Code == code), connection, code));
    }
    private static string NormalizeKey(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    // K6: a queued or running job with the same payload is reused. A finished one (blocked, cancelled, dead,
    // manual review, or a succeeded update/archive) gives up its dedup key ("...:retired:{id}") so the same
    // payload can be queued again; before, a Blocked or Cancelled create was returned forever and an update
    // or archive hit the unique (TenantId, JobType, JobDedupKey) index with a 500. A succeeded create is still
    // reused, so the same product payload is never submitted twice.
    private async Task<bool> ReuseOrRetirePublicationJobAsync(IntegrationJob job, string dedup, bool reuseSucceeded, CancellationToken cancellationToken)
    {
        if (job.Status is JobStatus.Pending or JobStatus.Leased or JobStatus.RetryScheduled || reuseSucceeded && job.Status == JobStatus.Succeeded) return true;
        var retiredKey = $"{dedup}:retired:{job.Id:N}";
        await db.IntegrationJobs
            .Where(x => x.TenantId == job.TenantId && x.Id == job.Id && x.JobDedupKey == dedup)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.JobDedupKey, retiredKey), cancellationToken);
        return false;
    }

    // A concurrent request that queued the same payload first wins; its job is returned instead of a 500.
    private async Task<ServiceResult<Guid>> SavePublicationJobAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, Guid tenantId, string jobType, string dedup, Guid jobId, CancellationToken cancellationToken)
    {
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
            var concurrent = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == jobType && x.JobDedupKey == dedup, cancellationToken);
            if (concurrent is not null) return ServiceResult<Guid>.Ok(concurrent.Id);
            throw;
        }
    }
    private static string JobWire(JobStatus value) => value switch { JobStatus.RetryScheduled => "RETRY_SCHEDULED", JobStatus.ManualReview => "MANUAL_REVIEW", _ => value.ToString().ToUpperInvariant() };

    private async Task<List<Guid>> ProductFamilyProductIdsAsync(Guid tenantId, Product product, CancellationToken cancellationToken)
    {
        var modelCode = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProductId == product.Id && x.ModelCode != null)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => x.ModelCode)
            .FirstOrDefaultAsync(cancellationToken);
        var ids = await VisibleFamilyProductIdsAsync(tenantId, product.BrandId, modelCode, cancellationToken);
        if (!ids.Contains(product.Id)) ids.Add(product.Id);
        return ids;
    }

    // D9: the visible products of the same brand whose model code has the same key.
    private Task<List<Guid>> VisibleFamilyProductIdsAsync(Guid tenantId, Guid? brandId, string? modelCode, CancellationToken cancellationToken) =>
        ProductFamilyScope.ProductIdsAsync(db, tenantId, brandId, modelCode,
            from siblingVariant in db.ProductVariants.AsNoTracking()
            join siblingProduct in VisibleProducts(tenantId) on siblingVariant.ProductId equals siblingProduct.Id
            select siblingVariant,
            cancellationToken);

    private async Task<IReadOnlyList<ProductView>> BuildProductViewsAsync(Guid tenantId, IReadOnlyList<Product> products, IReadOnlyList<ProductVariant> variants, CancellationToken cancellationToken, Guid? variantAttributeProductId = null)
    {
        if (products.Count == 0) return [];
        var productIds = products.Select(x => x.Id).ToArray();
        var variantIds = variants.Select(x => x.Id).ToArray();
        var inventories = await db.InventoryItems.AsNoTracking().Where(x => x.TenantId == tenantId && variantIds.Contains(x.VariantId) && x.LocationCode == "MAIN").ToListAsync(cancellationToken);
        var offers = await db.ChannelOffers.AsNoTracking().Where(x => x.TenantId == tenantId && variantIds.Contains(x.VariantId)).OrderByDescending(x => x.Status == "ACTIVE").ThenBy(x => x.Id).ToListAsync(cancellationToken);
        var profiles = await db.ChannelListingProfiles.AsNoTracking().Where(x => x.TenantId == tenantId && productIds.Contains(x.ProductId) && x.Enabled
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))).ToListAsync(cancellationToken);
        var variantLinks = await db.MarketplaceVariantLinks.AsNoTracking()
            .Where(x => x.TenantId == tenantId && variantIds.Contains(x.VariantId))
            .Select(x => new { x.ConnectionId, x.VariantId })
            .ToListAsync(cancellationToken);
        var profileIds = profiles.Select(x => x.Id).ToArray();
        var listingVariants = await db.ChannelListingVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && profileIds.Contains(x.ProfileId) && variantIds.Contains(x.VariantId))
            .Select(x => new { x.ProfileId, x.VariantId })
            .ToListAsync(cancellationToken);
        var productAttributes = await db.ProductAttributeAssignments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && productIds.Contains(x.ProductId) && x.VariantId == null)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);
        var variantAttributeRows = variantAttributeProductId is Guid attributeProductId
            ? await db.ProductAttributeAssignments.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ProductId == attributeProductId && x.VariantId != null && variantIds.Contains(x.VariantId.Value))
                .OrderBy(x => x.SortOrder)
                .Select(x => new { VariantId = x.VariantId!.Value, Assignment = new ProductAttributeAssignmentView(x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue, x.SortOrder) })
                .ToListAsync(cancellationToken)
            : [];
        var variantAttributesByVariant = variantAttributeRows
            .GroupBy(x => x.VariantId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ProductAttributeAssignmentView>)group.Select(x => x.Assignment).ToList());
        var productOptions = await db.ProductOptions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && productIds.Contains(x.ProductId))
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);
        var optionIds = productOptions.Select(x => x.Id).ToArray();
        var optionValues = await db.ProductOptionValues.AsNoTracking()
            .Where(x => x.TenantId == tenantId && optionIds.Contains(x.OptionId))
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);
        var variantOptionRows = await (from assignment in db.VariantOptionValues.AsNoTracking()
                                       join option in db.ProductOptions.AsNoTracking() on new { assignment.TenantId, assignment.OptionId } equals new { option.TenantId, OptionId = option.Id }
                                       join value in db.ProductOptionValues.AsNoTracking() on new { assignment.TenantId, assignment.OptionValueId } equals new { value.TenantId, OptionValueId = value.Id }
                                       where assignment.TenantId == tenantId && variantIds.Contains(assignment.VariantId) && productIds.Contains(option.ProductId)
                                       select new { assignment.VariantId, OptionLabel = option.Label, ValueLabel = value.Label }).ToListAsync(cancellationToken);
        var variantOptionsByVariant = variantOptionRows
            .GroupBy(item => item.VariantId)
            .ToDictionary(group => group.Key, group => (IReadOnlyDictionary<string, string>)group
                .GroupBy(item => item.OptionLabel, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(option => option.Key, option => option.Last().ValueLabel, StringComparer.OrdinalIgnoreCase));
        var connections = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (x.Status == "ACTIVE" || x.Status == "VERIFIED") && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "SHOPIFY" || x.PlatformCode == "HEPSIBURADA" || x.PlatformCode == "N11" || x.PlatformCode == "PAZARAMA" || x.PlatformCode == "PTTAVM"))
            .ToDictionaryAsync(x => x.Id, x => new { x.DisplayName, x.PlatformCode }, cancellationToken);
        var media = await (from item in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on new { item.TenantId, item.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                           where item.TenantId == tenantId && productIds.Contains(item.ProductId) && item.Status == "ACTIVE" && asset.Status == "ACTIVE" && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                           orderby item.SortOrder, item.VariantId, item.Id
                           select new { item.ProductId, item.VariantId, item.MediaRole, item.SortOrder, MediaId = item.Id, asset.Id, asset.Classification, Url = asset.RelativePath }).ToListAsync(cancellationToken);
        var mediaUrlsByVariant = media.Where(x => x.VariantId is not null)
            .GroupBy(x => x.VariantId!.Value)
            .ToDictionary(group => group.Key, group => CatalogImageIdentity.DistinctDisplayUrls(group.Select(item => CatalogMediaDisplay.Url(item.Id, item.Classification, item.Url))));
        var globalMediaUrlsByProduct = media.Where(x => x.VariantId is null)
            .GroupBy(x => x.ProductId)
            .ToDictionary(group => group.Key, group => CatalogImageIdentity.DistinctDisplayUrls(group.Select(item => CatalogMediaDisplay.Url(item.Id, item.Classification, item.Url))));
        var customMediaOrderProductIds = media
            .Where(item => item.MediaRole.StartsWith("ORDERED_", StringComparison.Ordinal))
            .Select(item => item.ProductId)
            .ToHashSet();
        var customMediaUrlsByProduct = media
            .Where(item => customMediaOrderProductIds.Contains(item.ProductId))
            .GroupBy(item => item.ProductId)
            .ToDictionary(group => group.Key, group => CatalogImageIdentity.DistinctDisplayUrls(
                group.OrderBy(item => item.SortOrder).ThenBy(item => item.VariantId).ThenBy(item => item.MediaId)
                    .Select(item => CatalogMediaDisplay.Url(item.Id, item.Classification, item.Url))));
        var categoryIds = products.Select(x => x.CategoryId).OfType<Guid>().Distinct().ToArray();
        var categoryPathById = categoryIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await db.Categories.AsNoTracking()
                .Where(x => x.TenantId == tenantId && categoryIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Path, cancellationToken);
        var inventoryByVariant = inventories.GroupBy(x => x.VariantId).ToDictionary(x => x.Key, x => x.First());
        var offerByVariant = offers.GroupBy(x => x.VariantId).ToDictionary(x => x.Key, x => x.First());
        var offerByVariantConnection = offers.ToDictionary(x => (x.VariantId, x.ConnectionId));
        return products.Select(product =>
        {
            // Product variants are inserted in the marketplace response order.
            // Sorting by SKU made the gallery jump between colours (for example
            // black, green, black) even though Trendyol sent each colour as a
            // contiguous block.
            var productVariants = variants.Where(x => x.ProductId == product.Id)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .ToList();
            var productVariantIds = productVariants.Select(x => x.Id).ToHashSet();
            var productProfiles = profiles.Where(x => x.ProductId == product.Id).ToList();
            var productLinks = variantLinks.Where(x => productVariantIds.Contains(x.VariantId)).ToList();
            var variantViews = productVariants.Select(variant =>
            {
                inventoryByVariant.TryGetValue(variant.Id, out var inventory); offerByVariant.TryGetValue(variant.Id, out var offer);
                var linkedConnectionIds = productLinks.Where(x => x.VariantId == variant.Id).Select(x => x.ConnectionId).ToHashSet();
                var listingConnectionIds = listingVariants
                    .Where(x => x.VariantId == variant.Id)
                    .Select(x => productProfiles.FirstOrDefault(profile => profile.Id == x.ProfileId)?.ConnectionId)
                    .OfType<Guid>()
                    .ToHashSet();
                var variantPlatformStatuses = connections
                    .OrderBy(connection => connection.Value.PlatformCode)
                    .ThenBy(connection => connection.Value.DisplayName)
                    .Select(connection =>
                    {
                        var connectionId = connection.Key;
                        var profile = productProfiles.FirstOrDefault(x => x.ConnectionId == connectionId);
                        var isLinked = linkedConnectionIds.Contains(connectionId) || listingConnectionIds.Contains(connectionId);
                        offerByVariantConnection.TryGetValue((variant.Id, connectionId), out var channelOffer);
                        return new ProductVariantPlatformStatusView(
                            connection.Value.DisplayName,
                            connection.Value.PlatformCode,
                            isLinked,
                            profile?.ActualStatus ?? (isLinked ? "LINKED" : "UNLINKED"),
                            connectionId,
                            channelOffer?.Id,
                            channelOffer?.ListPrice,
                            channelOffer?.SalePrice,
                            channelOffer?.Currency,
                            channelOffer?.VatRate,
                            channelOffer?.VatInclusion,
                            channelOffer?.RoundingMode,
                            channelOffer?.SafetyStock,
                            channelOffer?.Version,
                            linkedConnectionIds.Contains(connectionId));
                    })
                    .ToList();
                return new ProductVariantView(variant.Id, variant.Sku, variant.Barcode, variant.ModelCode, variant.OptionSignature, variant.Status.ToString().ToUpperInvariant(), variant.Version, variant.Weight, variant.Width, variant.Height, variant.Length, variant.Desi, variant.CostPrice, inventory?.OnHand ?? 0, inventory?.Available ?? 0, inventory?.Version, offer?.Id, offer?.ListPrice, offer?.SalePrice, offer?.Currency, offer?.Status, offer?.PriceVersion, offer?.Version, offer?.VatRate, offer?.VatInclusion, offer?.RoundingMode, offer?.SafetyStock, mediaUrlsByVariant.GetValueOrDefault(variant.Id), variantOptionsByVariant.GetValueOrDefault(variant.Id), variantPlatformStatuses, variant.DefaultListPrice, variant.DefaultSalePrice, variantAttributesByVariant.GetValueOrDefault(variant.Id), variant.ProductId);
            }).ToList();
            var productConnectionIds = connections.Keys.ToList();
            var platformStatuses = productConnectionIds
                .Select(connectionId =>
                {
                    var profile = productProfiles.FirstOrDefault(x => x.ConnectionId == connectionId);
                    var linkedVariantIds = productLinks.Where(x => x.ConnectionId == connectionId).Select(x => x.VariantId).ToHashSet();
                    if (profile is not null)
                    {
                        foreach (var listing in listingVariants.Where(x => x.ProfileId == profile.Id)) linkedVariantIds.Add(listing.VariantId);
                    }

                    var matchedVariantCount = linkedVariantIds.Count;
                    var status = profile?.ActualStatus ?? (matchedVariantCount == productVariantIds.Count && productVariantIds.Count > 0 ? "LINKED" : matchedVariantCount > 0 ? "PARTIAL_LINKED" : "UNLINKED");
                    var connection = connections[connectionId];
                    return new ProductPlatformStatusView(connection.DisplayName, status, matchedVariantCount, productVariantIds.Count, IsPlatformUpdateInProgress(profile?.ActualStatus), connection.PlatformCode);
                })
                .OrderBy(x => x.Platform, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var activePlatforms = platformStatuses.Select(x => x.Platform).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            var image = media.Where(x => x.ProductId == product.Id && x.VariantId == null).Select(item => CatalogMediaDisplay.Url(item.Id, item.Classification, item.Url)).FirstOrDefault() ?? media.Where(x => x.ProductId == product.Id).Select(item => CatalogMediaDisplay.Url(item.Id, item.Classification, item.Url)).FirstOrDefault();
            var prices = variantViews.Select(variant => CatalogPricePolicy.EffectiveSalePrice(variant.SalePrice, variant.DefaultSalePrice, product.DefaultSalePrice));
            var currency = variantViews.Select(x => x.Currency).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "TRY";
            var modelCode = variantViews.Select(x => x.ModelCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            var attributes = productAttributes.Where(x => x.ProductId == product.Id && x.VariantId is null)
                .Select(x => new ProductAttributeAssignmentView(x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue, x.SortOrder))
                .ToList();
            var options = productOptions.Where(x => x.ProductId == product.Id)
                .Select(option => new ProductOptionView(option.Id, option.Label, optionValues.Where(value => value.OptionId == option.Id).Select(value => new ProductOptionValueView(value.Id, value.Label)).ToList()))
                .ToList();
            var hasCustomMediaOrder = customMediaOrderProductIds.Contains(product.Id);
            return new ProductView(product.Id, product.Title, product.Description, product.BrandId, product.CategoryId, product.Status.ToString().ToUpperInvariant(), product.UpdatedAt, product.Version, variantViews, image, variantViews.Sum(x => x.OnHand), CatalogPricePolicy.MinimumPositivePrice(prices), currency, modelCode, activePlatforms, attributes, options, ProductMediaForView(variantViews, globalMediaUrlsByProduct.GetValueOrDefault(product.Id), hasCustomMediaOrder, customMediaUrlsByProduct.GetValueOrDefault(product.Id)), null, platformStatuses, product.CategoryId is Guid categoryId ? categoryPathById.GetValueOrDefault(categoryId) : null, null, hasCustomMediaOrder, product.DefaultListPrice, product.DefaultSalePrice, product.WarrantyMonths, product.TrendyolWebColorValueId);
        }).ToList();

    }

    private static IReadOnlyList<string> ProductMediaForView(IReadOnlyList<ProductVariantView> variants, IReadOnlyList<string>? globalMedia, bool hasCustomMediaOrder, IReadOnlyList<string>? customOrderedMedia)
    {
        var fallback = globalMedia ?? [];
        if (hasCustomMediaOrder) return customOrderedMedia ?? fallback;
        var hasColor = variants.Any(variant => ColorOptionValue(variant.OptionSignature) is not null);
        if (!hasColor) return fallback;

        var selected = new List<string>();
        var seenColors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variant in variants)
        {
            var color = ColorOptionValue(variant.OptionSignature);
            if (color is null || !seenColors.Add(color)) continue;
            foreach (var image in variant.MediaUrls ?? [])
                if (!string.IsNullOrWhiteSpace(image) && seenImages.Add(CatalogImageIdentity.Key(image))) selected.Add(image);
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
    private static decimal? NonNegativeOrNull(decimal? value) => value is >= 0 ? value : null;

    // O15: whether an editor row carries a change to the stored variant's sale data (codes, model
    // code, prices, options, variant attributes). The row order alone is not a marketplace change.
    private static bool VariantDataChanged(ProductVariant variant, UpdateVariantCommand update, IEnumerable<ProductAttributeAssignment> storedAttributes)
    {
        if (!string.Equals(variant.Sku, update.Sku.Trim(), StringComparison.Ordinal)) return true;
        if (!string.Equals(variant.Barcode, NullTrim(update.Barcode), StringComparison.Ordinal)) return true;
        if (!string.Equals(variant.ModelCode, NullTrim(update.ModelCode), StringComparison.Ordinal)) return true;
        // An unsent cost (null) leaves the stored one as it is; the editor sends only a cost the user changed.
        if (update.CostPrice is decimal cost && variant.CostPrice != NonNegativeOrNull(cost)) return true;
        if (update.Desi is decimal desi && variant.Desi != desi) return true;
        if (update.DefaultListPrice is decimal listPrice && variant.DefaultListPrice != listPrice) return true;
        if (update.DefaultSalePrice is decimal salePrice && variant.DefaultSalePrice != salePrice) return true;
        if (update.Options is not null)
        {
            var stored = VariantOptionSignature.ParseKeyed(variant.OptionSignature);
            var proposed = VariantOptionSignature.ParseKeyed(Signature(update.Options));
            if (stored.Count != proposed.Count || stored.Any(pair => !proposed.TryGetValue(pair.Key, out var value) || !string.Equals(value, pair.Value, StringComparison.Ordinal))) return true;
        }
        if (update.Attributes is not null)
        {
            static string AttributeKey(Guid attributeId, Guid? valueId, string? text, decimal? number, bool? boolean) =>
                $"{attributeId:N}|{valueId:N}|{(string.IsNullOrWhiteSpace(text) ? "" : text.Trim())}|{number?.ToString("G29", System.Globalization.CultureInfo.InvariantCulture)}|{boolean}";
            var stored = storedAttributes.Select(x => AttributeKey(x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue)).Order(StringComparer.Ordinal).ToList();
            var proposed = update.Attributes.Select(x => AttributeKey(x.AttributeId, x.ValueId, x.TextValue, x.NumberValue, x.BooleanValue)).Order(StringComparer.Ordinal).ToList();
            if (!stored.SequenceEqual(proposed, StringComparer.Ordinal)) return true;
        }
        return false;
    }

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
            var required = await db.CategoryAttributeRequirements.AsNoTracking().Where(x => x.TenantId == tenantId && x.CategoryId == category && x.IsPanelScoped && x.Role != "OPTION" && x.IsRequired).Select(x => x.AttributeId).ToListAsync(cancellationToken);
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
            var optionKeys = LookupKeys(first.Key);
            var option = db.ProductOptions.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == productId && CatalogKeyNormalizer.SameKey(x.NormalizedKey, optionGroup.Key))
                ?? await db.ProductOptions.Where(x => x.TenantId == tenantId && x.ProductId == productId && optionKeys.Contains(x.NormalizedKey)).OrderBy(x => x.NormalizedKey == optionGroup.Key ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
            if (option is null)
            {
                option = new ProductOption { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = productId, Label = first.Key.Trim(), NormalizedKey = optionGroup.Key, SortOrder = optionRows.IndexOf(optionGroup) };
                db.ProductOptions.Add(option);
            }
            foreach (var row in optionGroup)
            {
                var valueKey = Normalize(row.Value);
                var valueKeys = LookupKeys(row.Value);
                var optionValue = db.ProductOptionValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.OptionId == option.Id && CatalogKeyNormalizer.SameKey(x.NormalizedKey, valueKey))
                    ?? await db.ProductOptionValues.Where(x => x.TenantId == tenantId && x.OptionId == option.Id && valueKeys.Contains(x.NormalizedKey)).OrderBy(x => x.NormalizedKey == valueKey ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
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
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == link.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))));

    private void ApplyProductFilters(ref IQueryable<Product> query, Guid tenantId, string? status, string? search, string? platform)
    {
        if (!string.IsNullOrWhiteSpace(status) && TryProductStatus(status, out var parsed))
            query = query.Where(x => x.Status == parsed);

        if (!string.IsNullOrWhiteSpace(search))
        {
            // K23: "%" and "_" typed by the user are literal characters, not wildcards.
            var pattern = $"%{EscapeLike(search.Trim())}%";
            query = query.Where(product =>
                EF.Functions.ILike(product.Title, pattern, LikeEscape) ||
                db.ProductVariants.Any(variant => variant.TenantId == tenantId && variant.ProductId == product.Id &&
                    (EF.Functions.ILike(variant.Sku, pattern, LikeEscape) ||
                     (variant.Barcode != null && EF.Functions.ILike(variant.Barcode, pattern, LikeEscape)) ||
                     (variant.ModelCode != null && EF.Functions.ILike(variant.ModelCode, pattern, LikeEscape)))));
        }

        if (!string.IsNullOrWhiteSpace(platform))
        {
            var platformFilter = platform.Trim();
            if (!TryProductPlatformFilter(platformFilter, out _, out _))
            {
                var platformName = platformFilter;
                query = query.Where(product => db.ChannelListingProfiles.Any(profile =>
                    profile.TenantId == tenantId && profile.ProductId == product.Id && profile.Enabled &&
                    db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == profile.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED") && connection.DisplayName == platformName)));
            }
        }

    }

    private IQueryable<Guid> PlatformMatchedVariantIds(Guid tenantId, string platformCode) =>
        db.MarketplaceVariantLinks
            .Where(link => link.TenantId == tenantId
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == link.ConnectionId
                    && connection.PlatformCode == platformCode
                    && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))
            .Select(link => link.VariantId)
            .Concat(db.ChannelListingVariants
                .Where(listing => listing.TenantId == tenantId
                    && db.ChannelListingProfiles.Any(profile => profile.TenantId == tenantId && profile.Id == listing.ProfileId && profile.Enabled
                        && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == profile.ConnectionId
                            && connection.PlatformCode == platformCode
                            && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))))
                .Select(listing => listing.VariantId))
            .Distinct();

    private static bool PlatformFilterMatchesFamily(IEnumerable<Guid> familyVariantIds, IReadOnlySet<Guid> matchedVariantIds, string platformState)
    {
        var variantIds = familyVariantIds.Distinct().ToArray();
        var matchedCount = variantIds.Count(matchedVariantIds.Contains);
        return platformState switch
        {
            "ACTIVE" => variantIds.Length > 0 && matchedCount == variantIds.Length,
            "PARTIAL" => matchedCount > 0 && matchedCount < variantIds.Length,
            "PASSIVE" => matchedCount == 0,
            _ => true
        };
    }

    internal static bool TryProductPlatformFilter(string value, out string platformCode, out string platformState)
    {
        var parts = value.Split(':', 2, StringSplitOptions.TrimEntries);
        platformCode = parts.Length == 2 ? parts[0].ToUpperInvariant() : string.Empty;
        platformState = parts.Length == 2 ? parts[1].ToUpperInvariant() : string.Empty;
        return platformCode is "TRENDYOL" or "SHOPIFY" or "HEPSIBURADA" or "N11" or "PAZARAMA" or "PTTAVM"
            && platformState is "ACTIVE" or "PARTIAL" or "PASSIVE";
    }

    private Guid Decode(string? cursor) => cursors.TryDecode(cursor, out var id) ? id : throw new ArgumentException("Cursor geçersiz veya süresi dolmuş.", nameof(cursor));
    private PageResult<TView> Page<TEntity, TView>(List<TEntity> rows, int limit, Func<TEntity, TView> map) where TEntity : class { var hasMore = rows.Count > limit; var items = rows.Take(limit).Select(map).ToList(); var next = hasMore ? cursors.Encode((Guid)typeof(TEntity).GetProperty("Id")!.GetValue(rows[limit - 1])!) : null; return new(items, next, hasMore); }
    // Shared catalog key (CatalogKeyNormalizer) for codes, names and option keys; attribute values allow 320.
    private static string Normalize(string value) => CatalogKeyNormalizer.Key(value);
    private static string NormalizeValue(string value) => CatalogKeyNormalizer.Key(value, 320);
    private static string? NormalizeBarcode(string? value) => CatalogKeyNormalizer.Key(value) is { Length: > 0 } key ? key : null;
    private static string[] LookupKeys(string? value, int maximum = CatalogKeyNormalizer.DefaultMaximum) => CatalogKeyNormalizer.LookupKeys(value, maximum);
    // Attribute codes are identifiers chosen by the user, not matching keys; they keep their old form.
    private static string NormalizeCode(string value) => value.Trim().ToUpperInvariant();
    private static string NormalizeRequirementRole(string? value) => string.Equals(value?.Trim(), "OPTION", StringComparison.OrdinalIgnoreCase) ? "OPTION" : "ATTRIBUTE";
    internal static bool IsWebColorAttributeName(string? value)
    {
        var normalized = value?.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToUpperInvariant();
        return normalized is "WEBCOLOR" or "WEBCOLOUR" or "WEBRENK";
    }
    private const string LikeEscape = "\\";
    internal static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    private static string? NullTrim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Signature(IReadOnlyDictionary<string, string>? options) => VariantOptionSignature.Build(options, empty: "-");
    private static bool TryAttributeType(string value, out AttributeDataType result) { result = value.Trim().ToUpperInvariant() switch { "TEXT" => AttributeDataType.Text, "NUMBER" => AttributeDataType.Number, "SINGLE_SELECT" => AttributeDataType.SingleSelect, "MULTI_SELECT" => AttributeDataType.MultiSelect, "BOOLEAN" => AttributeDataType.Boolean, _ => (AttributeDataType)(-1) }; return Enum.IsDefined(result); }
    private static bool TryProductStatus(string value, out ProductStatus result) => Enum.TryParse(value, true, out result);
    private static CategoryView MapCategory(Category value) => new(value.Id, value.ParentId, value.Name, value.Path, value.Depth, value.IsLeaf, value.IsActive, value.Version);
    private static BrandView MapBrand(Brand value) => new(value.Id, value.Name, value.IsActive, value.Version);
    private static AttributeView MapAttribute(AttributeDefinition value, IEnumerable<AttributeValue> values, IReadOnlyList<string>? roles = null) => new(value.Id, value.Code, value.Name, value.DataType switch { AttributeDataType.SingleSelect => "SINGLE_SELECT", AttributeDataType.MultiSelect => "MULTI_SELECT", _ => value.DataType.ToString().ToUpperInvariant() }, value.SelectionMode, value.Unit, value.IsActive, value.Version, values.Select(x => new AttributeValueView(x.Id, x.Value, x.SortOrder, x.IsActive)).ToList(), roles);
    private static ProductVariantView MapVariant(ProductVariant value) => new(value.Id, value.Sku, value.Barcode, value.ModelCode, value.OptionSignature, value.Status.ToString().ToUpperInvariant(), value.Version, CostPrice: value.CostPrice);
    private static ProductView MapProduct(Product value, IEnumerable<ProductVariant> variants) => new(value.Id, value.Title, value.Description, value.BrandId, value.CategoryId, value.Status.ToString().ToUpperInvariant(), value.UpdatedAt, value.Version, variants.Select(MapVariant).ToList());
    private static ListingProfileView MapProfile(ChannelListingProfile value) => new(value.Id, value.ProductId, value.ConnectionId, value.TitleOverride, value.DescriptionOverride, value.ExternalCategoryId, value.ExternalBrandId, value.DeliveryTimeDays, value.Enabled, value.DesiredStatus, value.ActualStatus, value.Version);
    private static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Conflict<T>(string code, string message) => ServiceResult<T>.Fail(code, message, 409);
    private static ServiceResult<T> Precondition<T>(long version) => ServiceResult<T>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{version}.", 412);
}
