using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class CatalogService(AppDbContext db, CursorCodec cursors, TimeProvider timeProvider) : ICatalogService
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
        if (await db.Categories.AnyAsync(x => x.TenantId == tenantId && x.ParentId == command.ParentId && x.NormalizedName == normalized, cancellationToken))
            return Conflict<CategoryView>("CATEGORY_DUPLICATE", "Aynı üst kategoride bu ad zaten var.");
        var now = timeProvider.GetUtcNow(); var category = new Category { Id = Guid.CreateVersion7(), TenantId = tenantId, ParentId = parent?.Id, Name = name, NormalizedName = normalized, Path = parent is null ? name : $"{parent.Path} / {name}", Depth = parent is null ? 0 : parent.Depth + 1, IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now };
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
        var afterId = Decode(after); var query = db.AttributeDefinitions.AsNoTracking().Where(x => x.TenantId == tenantId); if (afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0);
        var rows = await query.OrderBy(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken); var ids = rows.Take(limit).Select(x => x.Id).ToArray(); var values = await db.AttributeValues.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.AttributeId)).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        return Page(rows, limit, attribute => MapAttribute(attribute, values.Where(x => x.AttributeId == attribute.Id)));
    }

    public async Task<ServiceResult<AttributeView>> CreateAttributeAsync(Guid tenantId, CreateAttributeCommand command, CancellationToken cancellationToken)
    {
        if (!TryAttributeType(command.DataType, out var dataType)) return Invalid<AttributeView>("dataType", "İzinli tipler TEXT, NUMBER, SINGLE_SELECT, MULTI_SELECT ve BOOLEAN'dır.");
        var code = Normalize(command.Code); if (code.Length is < 1 or > 96) return Invalid<AttributeView>("code", "Özellik kodu geçersizdir.");
        if (await db.AttributeDefinitions.AnyAsync(x => x.TenantId == tenantId && x.Code == code, cancellationToken)) return Conflict<AttributeView>("ATTRIBUTE_DUPLICATE", "Bu özellik kodu zaten var.");
        var now = timeProvider.GetUtcNow(); var attribute = new AttributeDefinition { Id = Guid.CreateVersion7(), TenantId = tenantId, Code = code, Name = command.Name.Trim(), DataType = dataType, SelectionMode = command.SelectionMode, Unit = command.Unit, CreatedAt = now, UpdatedAt = now };
        var values = command.Values.Select(value => new AttributeValue { Id = Guid.CreateVersion7(), TenantId = tenantId, AttributeId = attribute.Id, Value = value.Value.Trim(), NormalizedValue = Normalize(value.Value), SortOrder = value.SortOrder }).ToList();
        if (values.GroupBy(x => x.NormalizedValue).Any(group => group.Count() > 1)) return Invalid<AttributeView>("values", "Aynı özellik değeri tekrarlanamaz.");
        if (dataType is AttributeDataType.SingleSelect or AttributeDataType.MultiSelect && values.Count == 0) return Invalid<AttributeView>("values", "Seçimli özellik en az bir değer ister.");
        db.AttributeDefinitions.Add(attribute); db.AttributeValues.AddRange(values); await db.SaveChangesAsync(cancellationToken); return ServiceResult<AttributeView>.Ok(MapAttribute(attribute, values));
    }

    public async Task<ServiceResult<IReadOnlyList<AttributeRequirementCommand>>> ReplaceRequirementsAsync(Guid tenantId, Guid categoryId, long expectedVersion, IReadOnlyList<AttributeRequirementCommand> requirements, CancellationToken cancellationToken)
    {
        var category = await db.Categories.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == categoryId, cancellationToken); if (category is null) return NotFound<IReadOnlyList<AttributeRequirementCommand>>(); if (category.Version != expectedVersion) return Precondition<IReadOnlyList<AttributeRequirementCommand>>(category.Version);
        if (requirements.Select(x => x.AttributeId).Distinct().Count() != requirements.Count) return Invalid<IReadOnlyList<AttributeRequirementCommand>>("requirements", "Özellik gereksinimi tekrarlanamaz.");
        var ids = requirements.Select(x => x.AttributeId).ToArray(); if (await db.AttributeDefinitions.CountAsync(x => x.TenantId == tenantId && ids.Contains(x.Id) && x.IsActive, cancellationToken) != ids.Length) return Invalid<IReadOnlyList<AttributeRequirementCommand>>("requirements", "Etkin olmayan veya bulunmayan özellik vardır.");
        var current = await db.CategoryAttributeRequirements.Where(x => x.TenantId == tenantId && x.CategoryId == categoryId).ToListAsync(cancellationToken); db.CategoryAttributeRequirements.RemoveRange(current); db.CategoryAttributeRequirements.AddRange(requirements.Select(x => new CategoryAttributeRequirement { Id = Guid.CreateVersion7(), TenantId = tenantId, CategoryId = categoryId, AttributeId = x.AttributeId, IsRequired = x.IsRequired, AllowsCustomValue = x.AllowsCustomValue, DisplayOrder = x.DisplayOrder })); category.Version++; category.UpdatedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return ServiceResult<IReadOnlyList<AttributeRequirementCommand>>.Ok(requirements);
    }

    public async Task<PageResult<ProductView>> ListProductsAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken)
    {
        var afterId = Decode(after); var query = db.Products.AsNoTracking().Where(x => x.TenantId == tenantId); if (afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0); if (!string.IsNullOrWhiteSpace(status) && TryProductStatus(status, out var parsed)) query = query.Where(x => x.Status == parsed);
        var products = await query.OrderBy(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken); var ids = products.Take(limit).Select(x => x.Id).ToArray(); var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.ProductId)).ToListAsync(cancellationToken); return Page(products, limit, product => MapProduct(product, variants.Where(x => x.ProductId == product.Id)));
    }

    public async Task<ServiceResult<ProductView>> CreateProductAsync(Guid tenantId, CreateProductCommand command, CancellationToken cancellationToken)
    {
        var validation = await ValidateProductReferencesAsync(tenantId, command.Title, command.CategoryId, command.BrandId, cancellationToken); if (validation is not null) return ServiceResult<ProductView>.Fail(validation.Code, validation.Message, validation.Status, validation.FieldErrors);
        var attributeValidation = await ValidateAttributesAsync(tenantId, command.CategoryId, command.Attributes ?? [], cancellationToken); if (attributeValidation is not null) return ServiceResult<ProductView>.Fail(attributeValidation.Code, attributeValidation.Message, attributeValidation.Status, attributeValidation.FieldErrors);
        if (command.Variants.Count == 0) return Invalid<ProductView>("variants", "Ürün en az bir satış varyantı ister.");
        var normalizedSkus = command.Variants.Select(x => Normalize(x.Sku)).ToArray(); if (normalizedSkus.Any(string.IsNullOrWhiteSpace) || normalizedSkus.Distinct().Count() != normalizedSkus.Length) return Invalid<ProductView>("variants", "SKU boş veya tekrarlı olamaz.");
        if (await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId && normalizedSkus.Contains(x.SkuNormalized), cancellationToken)) return Conflict<ProductView>("SKU_CONFLICT_REVIEW_REQUIRED", "SKU başka bir varyantla çakışıyor; otomatik birleştirme yapılmadı.");
        var normalizedBarcodes = command.Variants.Where(x => !string.IsNullOrWhiteSpace(x.Barcode)).Select(x => Normalize(x.Barcode!)).ToArray(); if (normalizedBarcodes.Distinct().Count() != normalizedBarcodes.Length || await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId && x.BarcodeNormalized != null && normalizedBarcodes.Contains(x.BarcodeNormalized), cancellationToken)) return Conflict<ProductView>("BARCODE_CONFLICT_REVIEW_REQUIRED", "Barkod başka bir varyantla çakışıyor; otomatik birleştirme yapılmadı.");
        var now = timeProvider.GetUtcNow(); var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenantId, Title = command.Title.Trim(), Description = command.Description.Trim(), BrandId = command.BrandId, CategoryId = command.CategoryId, CreatedAt = now, UpdatedAt = now };
        var variants = command.Variants.Select(variant => new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, Sku = variant.Sku.Trim(), SkuNormalized = Normalize(variant.Sku), Barcode = NullTrim(variant.Barcode), BarcodeNormalized = string.IsNullOrWhiteSpace(variant.Barcode) ? null : Normalize(variant.Barcode), ModelCode = NullTrim(variant.ModelCode), OptionSignature = Signature(variant.Options), CreatedAt = now, UpdatedAt = now }).ToList();
        db.Products.Add(product); db.ProductVariants.AddRange(variants); db.ProductAttributeAssignments.AddRange((command.Attributes ?? []).Select(x => Assignment(tenantId, product.Id, x))); await EnsureMainInventoryAsync(tenantId, variants, cancellationToken); await db.SaveChangesAsync(cancellationToken); return ServiceResult<ProductView>.Ok(MapProduct(product, variants));
    }

    public async Task<ServiceResult<ProductView>> GetProductAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (product is null) return NotFound<ProductView>(); var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == id).ToListAsync(cancellationToken); return ServiceResult<ProductView>.Ok(MapProduct(product, variants));
    }

    public async Task<ServiceResult<ProductView>> UpdateProductAsync(Guid tenantId, Guid id, long expectedVersion, UpdateProductCommand command, CancellationToken cancellationToken)
    {
        var product = await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (product is null) return NotFound<ProductView>(); if (product.Version != expectedVersion) return Precondition<ProductView>(product.Version);
        var validation = await ValidateProductReferencesAsync(tenantId, command.Title, command.CategoryId, command.BrandId, cancellationToken); if (validation is not null) return ServiceResult<ProductView>.Fail(validation.Code, validation.Message, validation.Status, validation.FieldErrors);
        var attributeValidation = await ValidateAttributesAsync(tenantId, command.CategoryId, command.Attributes ?? [], cancellationToken); if (attributeValidation is not null) return ServiceResult<ProductView>.Fail(attributeValidation.Code, attributeValidation.Message, attributeValidation.Status, attributeValidation.FieldErrors);
        product.Title = command.Title.Trim(); product.Description = command.Description.Trim(); product.CategoryId = command.CategoryId; product.BrandId = command.BrandId; product.Version++; product.UpdatedAt = timeProvider.GetUtcNow(); var currentAssignments = await db.ProductAttributeAssignments.Where(x => x.TenantId == tenantId && x.ProductId == id && x.VariantId == null).ToListAsync(cancellationToken); db.ProductAttributeAssignments.RemoveRange(currentAssignments); db.ProductAttributeAssignments.AddRange((command.Attributes ?? []).Select(x => Assignment(tenantId, id, x))); await db.SaveChangesAsync(cancellationToken); var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == id).ToListAsync(cancellationToken); return ServiceResult<ProductView>.Ok(MapProduct(product, variants));
    }

    public async Task<ServiceResult<ProductView>> ArchiveProductAsync(Guid tenantId, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var product = await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); if (product is null) return NotFound<ProductView>(); if (product.Version != expectedVersion) return Precondition<ProductView>(product.Version); product.Status = ProductStatus.Archived; product.ArchivedAt = timeProvider.GetUtcNow(); product.UpdatedAt = product.ArchivedAt.Value; product.Version++; var variants = await db.ProductVariants.Where(x => x.TenantId == tenantId && x.ProductId == id).ToListAsync(cancellationToken); foreach (var variant in variants) { variant.Status = ProductStatus.Archived; variant.UpdatedAt = product.UpdatedAt; variant.Version++; }
        var media = await db.ProductMedia.Where(x => x.TenantId == tenantId && x.ProductId == id && x.Status != "ARCHIVED").ToListAsync(cancellationToken); foreach (var item in media) item.Status = "ARCHIVED"; var assetIds = media.Select(x => x.FileAssetId).Distinct().ToArray(); var sharedAssetIds = await db.ProductMedia.Where(x => x.TenantId == tenantId && x.ProductId != id && x.Status != "ARCHIVED" && assetIds.Contains(x.FileAssetId)).Select(x => x.FileAssetId).Distinct().ToListAsync(cancellationToken); var assets = await db.FileAssets.Where(x => x.TenantId == tenantId && assetIds.Contains(x.Id) && !sharedAssetIds.Contains(x.Id)).ToListAsync(cancellationToken); foreach (var asset in assets) { asset.Status = "ARCHIVED"; asset.ArchivedAt = product.ArchivedAt; }
        await db.SaveChangesAsync(cancellationToken); return ServiceResult<ProductView>.Ok(MapProduct(product, variants));
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

    public async Task<ServiceResult<Guid>> ValidatePublicationAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken); if (product is null) return NotFound<Guid>();
        if (product.CategoryId is null || !await db.CategoryMappings.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == product.CategoryId && x.Status == "VERIFIED", cancellationToken)) return ServiceResult<Guid>.Fail("CATALOG_MAPPING_REQUIRED", "Yayın öncesi kategori eşlemesi gereklidir.", 422, new Dictionary<string, string[]> { ["categoryId"] = ["Seçilen bağlantı için doğrulanmış kategori eşlemesi yok."] });
        return ServiceResult<Guid>.Fail("CAPABILITY_UNKNOWN", "Gerçek platform capability kanıtı F3'te doğrulanmadan yayın işi oluşturulamaz.", 422);
    }

    private async Task<ServiceError?> ValidateProductReferencesAsync(Guid tenantId, string title, Guid? categoryId, Guid? brandId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 320) return new("PRODUCT_VALIDATION_FAILED", "Ürün başlığı geçersizdir.", 422, new Dictionary<string, string[]> { ["title"] = ["Başlık zorunludur ve en fazla 320 karakterdir."] });
        if (categoryId is Guid category && !await db.Categories.AnyAsync(x => x.TenantId == tenantId && x.Id == category && x.IsActive && x.IsLeaf, cancellationToken)) return new("CATEGORY_LEAF_REQUIRED", "Yalnız etkin yaprak kategori seçilebilir.", 422, new Dictionary<string, string[]> { ["categoryId"] = ["Kategori etkin bir yaprak olmalıdır."] });
        if (brandId is Guid brand && !await db.Brands.AnyAsync(x => x.TenantId == tenantId && x.Id == brand && x.IsActive, cancellationToken)) return new("BRAND_ACTIVE_REQUIRED", "Seçilen marka etkin değildir.", 422, new Dictionary<string, string[]> { ["brandId"] = ["Etkin marka seçin."] }); return null;
    }

    private async Task<ServiceError?> ValidateAttributesAsync(Guid tenantId, Guid? categoryId, IReadOnlyList<ProductAttributeCommand> assignments, CancellationToken cancellationToken)
    {
        var ids = assignments.Select(x => x.AttributeId).Distinct().ToArray();
        var definitions = await db.AttributeDefinitions.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.Id) && x.IsActive).ToDictionaryAsync(x => x.Id, cancellationToken);
        if (definitions.Count != ids.Length) return new("ATTRIBUTE_INVALID", "Etkin olmayan veya bulunmayan ürün özelliği var.", 422, new Dictionary<string, string[]> { ["attributes"] = ["Tüm özellikler etkin ve aynı tenant içinde olmalıdır."] });
        foreach (var assignment in assignments)
        {
            var textValue = string.IsNullOrWhiteSpace(assignment.TextValue) ? null : assignment.TextValue;
            var count = new object?[] { assignment.ValueId, textValue, assignment.NumberValue, assignment.BooleanValue }.Count(x => x is not null);
            if (count != 1) return new("ATTRIBUTE_TYPED_VALUE_REQUIRED", "Her özellik ataması exactly-one typed value ister.", 422, new Dictionary<string, string[]> { ["attributes"] = ["valueId, textValue, numberValue, booleanValue alanlarından tam biri dolu olmalıdır."] });
            var definition = definitions[assignment.AttributeId];
            var typeMatches = definition.DataType switch { AttributeDataType.Text => textValue is not null, AttributeDataType.Number => assignment.NumberValue is not null, AttributeDataType.Boolean => assignment.BooleanValue is not null, AttributeDataType.SingleSelect or AttributeDataType.MultiSelect => assignment.ValueId is not null, _ => false };
            if (!typeMatches) return new("ATTRIBUTE_TYPE_MISMATCH", "Özellik değeri tanımlı veri tipiyle eşleşmiyor.", 422);
            if (assignment.ValueId is Guid valueId && !await db.AttributeValues.AnyAsync(x => x.TenantId == tenantId && x.AttributeId == assignment.AttributeId && x.Id == valueId && x.IsActive, cancellationToken)) return new("ATTRIBUTE_VALUE_INVALID", "Seçim değeri özelliğe ait veya etkin değil.", 422);
        }
        if (categoryId is Guid category)
        {
            var required = await db.CategoryAttributeRequirements.AsNoTracking().Where(x => x.TenantId == tenantId && x.CategoryId == category && x.IsRequired).Select(x => x.AttributeId).ToListAsync(cancellationToken);
            var supplied = assignments.Select(x => x.AttributeId).ToHashSet();
            if (required.Any(id => !supplied.Contains(id))) return new("REQUIRED_ATTRIBUTE_MISSING", "Kategori için zorunlu özellikler eksik.", 422, new Dictionary<string, string[]> { ["attributes"] = ["Tüm zorunlu kategori özelliklerini girin."] });
        }
        return null;
    }

    private static ProductAttributeAssignment Assignment(Guid tenantId, Guid productId, ProductAttributeCommand value) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ProductId = productId,
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

    private Guid Decode(string? cursor) => cursors.TryDecode(cursor, out var id) ? id : throw new ArgumentException("Cursor geçersiz veya süresi dolmuş.", nameof(cursor));
    private PageResult<TView> Page<TEntity, TView>(List<TEntity> rows, int limit, Func<TEntity, TView> map) where TEntity : class { var hasMore = rows.Count > limit; var items = rows.Take(limit).Select(map).ToList(); var next = hasMore ? cursors.Encode((Guid)typeof(TEntity).GetProperty("Id")!.GetValue(rows[limit - 1])!) : null; return new(items, next, hasMore); }
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static string? NullTrim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Signature(IReadOnlyDictionary<string, string>? options) => options is null || options.Count == 0 ? "-" : string.Join('|', options.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{Normalize(x.Key)}={Normalize(x.Value)}"));
    private static bool TryAttributeType(string value, out AttributeDataType result) { result = value.Trim().ToUpperInvariant() switch { "TEXT" => AttributeDataType.Text, "NUMBER" => AttributeDataType.Number, "SINGLE_SELECT" => AttributeDataType.SingleSelect, "MULTI_SELECT" => AttributeDataType.MultiSelect, "BOOLEAN" => AttributeDataType.Boolean, _ => (AttributeDataType)(-1) }; return Enum.IsDefined(result); }
    private static bool TryProductStatus(string value, out ProductStatus result) => Enum.TryParse(value, true, out result);
    private static CategoryView MapCategory(Category value) => new(value.Id, value.ParentId, value.Name, value.Path, value.Depth, value.IsLeaf, value.IsActive, value.Version);
    private static BrandView MapBrand(Brand value) => new(value.Id, value.Name, value.IsActive, value.Version);
    private static AttributeView MapAttribute(AttributeDefinition value, IEnumerable<AttributeValue> values) => new(value.Id, value.Code, value.Name, value.DataType switch { AttributeDataType.SingleSelect => "SINGLE_SELECT", AttributeDataType.MultiSelect => "MULTI_SELECT", _ => value.DataType.ToString().ToUpperInvariant() }, value.SelectionMode, value.Unit, value.IsActive, value.Version, values.Select(x => new AttributeValueView(x.Id, x.Value, x.SortOrder, x.IsActive)).ToList());
    private static ProductVariantView MapVariant(ProductVariant value) => new(value.Id, value.Sku, value.Barcode, value.ModelCode, value.OptionSignature, value.Status.ToString().ToUpperInvariant(), value.Version);
    private static ProductView MapProduct(Product value, IEnumerable<ProductVariant> variants) => new(value.Id, value.Title, value.Description, value.BrandId, value.CategoryId, value.Status.ToString().ToUpperInvariant(), value.UpdatedAt, value.Version, variants.Select(MapVariant).ToList());
    private static ListingProfileView MapProfile(ChannelListingProfile value) => new(value.Id, value.ProductId, value.ConnectionId, value.TitleOverride, value.DescriptionOverride, value.ExternalCategoryId, value.ExternalBrandId, value.DeliveryTimeDays, value.Enabled, value.DesiredStatus, value.ActualStatus, value.Version);
    private static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Conflict<T>(string code, string message) => ServiceResult<T>.Fail(code, message, 409);
    private static ServiceResult<T> Precondition<T>(long version) => ServiceResult<T>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{version}.", 412);
}
