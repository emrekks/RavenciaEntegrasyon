using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// PTT AVM product send (UpdateProductsV3, an upsert keyed by the product barcode). A product sent without variants
// loses every variant it has on PTT AVM. A product with a single variant (its model family has no other variant) is
// sent as a product without variants under that variant's barcode, and only when PTT AVM cannot be holding it as a
// product with variants: the product's links on this connection must be that one barcode and nothing else.
// A product with variants is PTT AVM's model product: the panel's whole model family (every colour card sharing the
// brand and model code) under the model code, every non-archived variant of every card in one request. PTT AVM
// deletes the variants a request leaves out, so a variant that cannot be sent stops the whole send (none is skipped)
// and the adapter compares the request with the variants PTT AVM holds right before the send.
// The category must be a leaf; PTT AVM has no brand or attribute service, so neither is needed.
internal sealed class PttAvmProductPublicationComposer(AppDbContext db, IConfiguration configuration)
{
    public async Task<ServiceResult<ProductPublicationDraft>> BuildAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken);
        if (product is null) return NotFound();
        if (product.Status == ProductStatus.Archived) return Fail("PRODUCT_ARCHIVED", "Arşivlenmiş ürün yayınlanamaz.");
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "PTTAVM", cancellationToken);
        if (connection is null) return NotFound();
        var profile = await db.ChannelListingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.ConnectionId == connectionId, cancellationToken);
        if (profile is null) return Fail("LISTING_PROFILE_REQUIRED", "Yayın öncesi PTT AVM bağlantısına ait listing profile oluşturulmalıdır.");
        if (!profile.Enabled) return Fail("LISTING_PROFILE_DISABLED", "Listing profile etkinleştirilmeden yayın işi oluşturulamaz.");

        var categoryMapping = product.CategoryId is Guid categoryId
            ? await db.CategoryMappings.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == categoryId && x.Status == "VERIFIED")
                .Join(db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORIES" && x.IsCurrent), mapping => mapping.SnapshotId, snapshot => snapshot.Id, (mapping, _) => mapping)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        if (categoryMapping is null) return Fail("CATEGORY_MAPPING_REQUIRED", "Yayın öncesi güncel PTT AVM kategori eşlemesi gerekir.", "categoryId");
        if (!long.TryParse(categoryMapping.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var remoteCategoryId) || remoteCategoryId <= 0)
            return Fail("CATEGORY_MAPPING_INVALID", "PTT AVM kategori kimliği sayı olmalıdır.", "categoryId");
        if (!string.IsNullOrWhiteSpace(profile.ExternalCategoryId) && profile.ExternalCategoryId != categoryMapping.ExternalId)
            return Fail("LISTING_MAPPING_CONFLICT", "Listing profile kategorisi güncel doğrulanmış PTT AVM eşlemesiyle çelişiyor.", status: 409);
        if (!await db.ReferenceItems.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.SnapshotId == categoryMapping.SnapshotId && x.ResourceType == "CATEGORIES" && x.ExternalId == categoryMapping.ExternalId && x.IsActive && x.IsLeaf, cancellationToken))
            return Fail("CATEGORY_LEAF_REQUIRED", "PTT AVM'de ürün yalnız en alt kategoriye açılabilir; kategori eşlemesini bir alt kategoriye güncelleyin.", "categoryId");

        var allVariants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == productId).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var variants = allVariants.Where(x => x.Status != ProductStatus.Archived).ToList();
        if (variants.Count == 0) return Fail("PRODUCT_VARIANT_REQUIRED", "Yayın için en az bir etkin varyant gerekir.");

        // The model family: the card's own model code (its first variant carrying one) names it.
        var modelCode = variants.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => x.ModelCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var familyIds = await ProductFamilyScope.ProductIdsAsync(db, tenantId, product.BrandId, modelCode, db.ProductVariants.AsNoTracking(), cancellationToken);
        if (!familyIds.Contains(productId)) familyIds.Add(productId);
        // Card order is creation order (the id breaks a tie), so the family keeps one order on every publication.
        var members = await db.Products.AsNoTracking()
            .Where(x => x.TenantId == tenantId && familyIds.Contains(x.Id) && x.Status != ProductStatus.Archived)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var memberIds = members.Select(x => x.Id).ToArray();
        var memberIndex = members.Select((member, index) => (member.Id, index)).ToDictionary(x => x.Id, x => x.index);
        var familyVariants = (await db.ProductVariants.AsNoTracking()
                .Where(x => x.TenantId == tenantId && memberIds.Contains(x.ProductId) && x.Status != ProductStatus.Archived)
                .ToListAsync(cancellationToken))
            .OrderBy(x => memberIndex[x.ProductId]).ThenBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToList();
        if (familyVariants.Count > 1)
            return await BuildFamilyAsync(tenantId, productId, connectionId, product, profile, categoryMapping.ExternalId, members, familyVariants, cancellationToken);

        var variant = variants[0];
        var barcode = variant.Barcode?.Trim();
        if (string.IsNullOrEmpty(barcode) || barcode.Length > PttAvmPriceInventoryRules.MaxBarcodeLength)
            return Fail("BARCODE_REQUIRED", $"PTT AVM ürünü barkodla tanır; varyantın barkodu (en fazla {PttAvmPriceInventoryRules.MaxBarcodeLength} karakter) olmalıdır.");
        if (string.IsNullOrWhiteSpace(variant.Sku)) return Fail("STOCK_CODE_INVALID", "PTT AVM ürün kodu olarak kullanılan SKU boş olamaz.");
        if (await ProductPublicationComposer.ConnectionDuplicateAsync(db, tenantId, connectionId, [variant.Id], [barcode], [barcode], cancellationToken) is { } takenBarcode)
            return Fail("BARCODE_DUPLICATE", ProductPublicationComposer.ConnectionDuplicateMessage(takenBarcode), status: 409);

        // The variant-deletion guard: every barcode this product is linked to on PTT AVM must be the one being sent.
        var variantIds = allVariants.Select(item => item.Id).ToArray();
        var linkedBarcodes = await db.MarketplaceVariantLinks.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId))
            .Select(x => x.ExternalId)
            .ToListAsync(cancellationToken);
        if (PttAvmProductPayloadRules.VariantDeletionRisk(barcode, linkedBarcodes) is { } risk)
            return Fail("PTTAVM_VARIANT_DELETION_RISK", risk, status: 409);

        var offer = await db.ChannelOffers.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id && x.Status == "ACTIVE", cancellationToken);
        var inventory = await db.InventoryItems.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variant.Id && x.LocationCode == "MAIN", cancellationToken);
        if (offer is null) return Fail("CHANNEL_OFFER_REQUIRED", "Varyant için etkin PTT AVM fiyat teklifi gerekir.");
        if (inventory is null) return Fail("INVENTORY_REQUIRED", "Varyant için MAIN stok kaydı gerekir.");
        var salePrice = PttAvmPriceInventoryRules.Price(PriceInventoryComposer.OutboundSalePrice(offer));
        if (!string.Equals(offer.Currency, "TRY", StringComparison.OrdinalIgnoreCase) || salePrice <= 1)
            return Fail("CHANNEL_OFFER_INVALID", $"'{variant.Sku}' için TRY para birimi ve 1'den büyük satış fiyatı gerekir.");
        if (PttAvmPriceInventoryRules.VatRate(offer.VatRate) is not { } vatRate)
            return Fail("VAT_RATE_INVALID", $"'{variant.Sku}' için KDV oranı PTT AVM'de 0, 1, 10 veya 20 olmalıdır.");
        if (variant.Desi is not > 0) return Fail("DESI_REQUIRED", $"'{variant.Sku}' için PTT AVM'nin istediği desi değeri girilmemiş.");

        var title = (profile.TitleOverride ?? product.Title).Trim();
        var description = (profile.DescriptionOverride ?? product.Description).Trim();
        if (title.Length is < 1 or > PttAvmProductPayloadRules.MaxNameLength) return Fail("PRODUCT_TITLE_INVALID", $"PTT AVM ürün adı 1-{PttAvmProductPayloadRules.MaxNameLength} karakter olmalıdır; listing profilinde kısa bir başlık girin.", "title");
        if (description.Length == 0) return Fail("PRODUCT_DESCRIPTION_INVALID", "PTT AVM ürün açıklaması boş olamaz.");

        var media = await (from productMedia in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on productMedia.FileAssetId equals asset.Id
                           where productMedia.TenantId == tenantId && productMedia.ProductId == productId && productMedia.Status == "ACTIVE" && asset.TenantId == tenantId && asset.Status == "ACTIVE" && asset.ArchivedAt == null
                           orderby productMedia.SortOrder, productMedia.Id
                           select new { productMedia.VariantId, asset.Id, asset.Classification, asset.RelativePath }).ToListAsync(cancellationToken);
        var assignedMedia = media.Where(x => x.VariantId == variant.Id).ToList();
        var relevantMedia = assignedMedia.Count > 0 ? assignedMedia : media.Where(x => x.VariantId is null).ToList();
        if (relevantMedia.Where(x => x.Classification == "PRODUCT_MEDIA_URL").Any(x => !ProductPublicationComposer.IsPublicHttpsUrl(x.RelativePath.Trim())))
            return Fail("PRODUCT_MEDIA_PUBLIC_URL_INVALID", $"'{variant.Sku}' için görsel adresleri geçerli herkese açık HTTPS adresi olmalıdır.");
        var imageUrls = ProductPublicationComposer.SelectPublicationImageUrls(relevantMedia
            .Select(item => item.Classification == "PRODUCT_MEDIA_URL" ? item.RelativePath.Trim() : ProductPublicationComposer.BuildPublicProductMediaUrl(configuration["Marketplace:PublicBaseUrl"], item.Id))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!));
        // PTT AVM needs at least one image for a new product.
        if (imageUrls.Count == 0) return Fail("PRODUCT_MEDIA_PUBLIC_URL_REQUIRED", $"'{variant.Sku}' için en az bir herkese açık HTTPS ürün görseli gerekir.");

        var draft = new PttAvmProductPayloadRules.ProductDraft(barcode, title, description, categoryMapping.ExternalId, variant.Sku.Trim(), salePrice, vatRate,
            PttAvmPriceInventoryRules.Quantity(inventory.Available - offer.SafetyStock), Math.Ceiling(variant.Desi.Value), imageUrls);
        return Draft(profile.Id, categoryMapping.ExternalId, draft, [new(variant.Id, variant.Sku.Trim(), barcode)]);
    }

    // The whole model family as one PTT AVM product. The lead card (the family's first) gives the name and the
    // description, as for Shopify; every card's first images come first, then the rest, up to PTT AVM's eight.
    private async Task<ServiceResult<ProductPublicationDraft>> BuildFamilyAsync(Guid tenantId, Guid productId, Guid connectionId, Product product, ChannelListingProfile profile, string externalCategoryId,
        IReadOnlyList<Product> members, IReadOnlyList<ProductVariant> familyVariants, CancellationToken cancellationToken)
    {
        if (members.FirstOrDefault(member => member.CategoryId != product.CategoryId) is { } otherCategory)
            return Fail("PTTAVM_FAMILY_CATEGORY_MISMATCH", $"Model ailesinin kartları PTT AVM'de tek ürün olarak gönderilir; '{otherCategory.Title}' kartının kategorisi bu kartınkinden farklı. Ailenin kartlarını aynı kategoriye alın.", "categoryId");
        var memberIds = members.Select(x => x.Id).ToArray();
        var lead = members[0];
        var leadProfile = lead.Id == productId ? profile : await db.ChannelListingProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == lead.Id, cancellationToken);
        var title = (leadProfile?.TitleOverride ?? lead.Title).Trim();
        var description = (leadProfile?.DescriptionOverride ?? lead.Description).Trim();
        if (title.Length is < 1 or > PttAvmProductPayloadRules.MaxNameLength) return Fail("PRODUCT_TITLE_INVALID", $"PTT AVM ürün adı 1-{PttAvmProductPayloadRules.MaxNameLength} karakter olmalıdır; '{lead.Title}' kartının listing profilinde kısa bir başlık girin.", "title");
        if (description.Length == 0) return Fail("PRODUCT_DESCRIPTION_INVALID", $"PTT AVM ürün açıklaması boş olamaz ('{lead.Title}' kartı).");

        var variantIds = familyVariants.Select(x => x.Id).ToArray();
        var offers = await db.ChannelOffers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId) && x.Status == "ACTIVE")
            .ToDictionaryAsync(x => x.VariantId, cancellationToken);
        var inventories = await db.InventoryItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && variantIds.Contains(x.VariantId) && x.LocationCode == "MAIN")
            .ToDictionaryAsync(x => x.VariantId, cancellationToken);
        var externalCodes = await db.MarketplaceProductLinks.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && memberIds.Contains(x.ProductId) && x.ExternalCode != null)
            .Select(x => x.ExternalCode!)
            .ToListAsync(cancellationToken);
        var titles = members.ToDictionary(x => x.Id, x => x.Title);
        var rows = familyVariants.Select(variant =>
        {
            var offer = offers.GetValueOrDefault(variant.Id);
            var inventory = inventories.GetValueOrDefault(variant.Id);
            var attributes = PriceInventoryComposer.PttAvmVariantAttributes(variant.OptionSignature);
            return new FamilyVariantRow(variant.Id, titles[variant.ProductId], variant.Sku, variant.Barcode, variant.ModelCode,
                attributes.FirstOrDefault(x => x.Definition == "Renk")?.Value, attributes.FirstOrDefault(x => x.Definition == "Beden")?.Value, variant.Desi,
                offer is null ? null : PttAvmPriceInventoryRules.Price(PriceInventoryComposer.OutboundSalePrice(offer)), offer?.Currency, offer?.VatRate,
                offer is null || inventory is null ? null : PttAvmPriceInventoryRules.Quantity(inventory.Available - offer.SafetyStock), inventory is not null);
        }).ToList();
        var planned = PlanFamily(rows, externalCodes);
        if (planned.Error is { } error) return Fail(error.Code, error.Message, status: error.Status);
        var plan = planned.Plan!;

        var barcodes = plan.Variants.Select(x => x.Barcode).ToArray();
        if (await ProductPublicationComposer.ConnectionDuplicateAsync(db, tenantId, connectionId, variantIds, barcodes, barcodes, cancellationToken) is { } takenBarcode)
            return Fail("BARCODE_DUPLICATE", ProductPublicationComposer.ConnectionDuplicateMessage(takenBarcode), status: 409);

        var media = await (from productMedia in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on productMedia.FileAssetId equals asset.Id
                           where productMedia.TenantId == tenantId && memberIds.Contains(productMedia.ProductId) && productMedia.Status == "ACTIVE" && asset.TenantId == tenantId && asset.Status == "ACTIVE" && asset.ArchivedAt == null
                           orderby productMedia.SortOrder, productMedia.Id
                           select new { productMedia.ProductId, asset.Id, asset.Classification, asset.RelativePath }).ToListAsync(cancellationToken);
        if (media.Where(x => x.Classification == "PRODUCT_MEDIA_URL").Any(x => !ProductPublicationComposer.IsPublicHttpsUrl(x.RelativePath.Trim())))
            return Fail("PRODUCT_MEDIA_PUBLIC_URL_INVALID", "Model ailesinin görsel adresleri geçerli herkese açık HTTPS adresi olmalıdır.");
        var imagesByMember = members.Select(member => ProductPublicationComposer.SelectPublicationImageUrls(media
            .Where(item => item.ProductId == member.Id)
            .Select(item => item.Classification == "PRODUCT_MEDIA_URL" ? item.RelativePath.Trim() : ProductPublicationComposer.BuildPublicProductMediaUrl(configuration["Marketplace:PublicBaseUrl"], item.Id))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!))).ToList();
        // PTT AVM's images are the product's: one per colour first, then each card's others.
        var imageUrls = imagesByMember.Select(urls => urls.FirstOrDefault()).OfType<string>()
            .Concat(imagesByMember.SelectMany(urls => urls.Skip(1)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(PttAvmProductPayloadRules.MaxImages)
            .ToList();
        if (imageUrls.Count == 0) return Fail("PRODUCT_MEDIA_PUBLIC_URL_REQUIRED", "PTT AVM'ye model ailesini göndermek için en az bir herkese açık HTTPS ürün görseli gerekir.");

        var draft = new PttAvmProductPayloadRules.ProductDraft(plan.ParentBarcode, title, description, externalCategoryId, plan.ParentBarcode, plan.Price, plan.VatRate,
            plan.Variants.Sum(x => x.Quantity), plan.Desi, imageUrls, plan.Variants);
        // This card's job records only this card's variants; the family's other cards have jobs of their own.
        var own = familyVariants.Where(x => x.ProductId == productId).Select(x => new PublicationVariantDraft(x.Id, x.Sku.Trim(), x.Barcode!.Trim())).ToList();
        return Draft(profile.Id, externalCategoryId, draft, own);
    }

    internal sealed record FamilyVariantRow(Guid VariantId, string CardTitle, string Sku, string? Barcode, string? ModelCode, string? Color, string? Size, decimal? Desi,
        decimal? SalePrice, string? Currency, decimal? VatRate, int? Quantity, bool HasInventory);

    internal sealed record FamilyPlan(string ParentBarcode, decimal Price, decimal VatRate, decimal Desi, IReadOnlyList<PttAvmProductPayloadRules.VariantDraft> Variants);

    internal sealed record FamilyError(string Code, string Message, int Status = 422);

    // The family's variants as one PTT AVM product, or the first reason it cannot be sent. Pure, so it can be tested.
    // Rows carry the outbound sale price (null without an active offer) and the stock to send (null without a MAIN
    // stock record or an offer). The parent barcode is the code PTT AVM keeps the family by (a product link's
    // ExternalCode on this connection) or, before any import recorded one, the model code; the two must agree.
    internal static (FamilyPlan? Plan, FamilyError? Error) PlanFamily(IReadOnlyList<FamilyVariantRow> rows, IReadOnlyCollection<string> externalCodes)
    {
        static (FamilyPlan?, FamilyError?) Refuse(string code, string message, int status = 422) => (null, new(code, message, status));
        string Name(FamilyVariantRow row) => $"'{row.Sku.Trim()}' ({row.CardTitle})";
        if (rows.Count > PttAvmPriceInventoryRules.MaxVariantsPerProduct)
            return Refuse("PTTAVM_VARIANT_LIMIT_EXCEEDED", $"PTT AVM bir üründe en fazla {PttAvmPriceInventoryRules.MaxVariantsPerProduct} varyant tutar; model ailesinin kartlarında {rows.Count} etkin varyant var.");
        if (rows.FirstOrDefault(row => ProductFamilyKeyPolicy.ModelCodeKey(row.ModelCode) is null) is { } uncoded)
            return Refuse("PTTAVM_MODEL_CODE_REQUIRED", $"PTT AVM varyantlı ürünü model koduyla tanır; {Name(uncoded)} varyantında model kodu yok. Ailenin tüm varyantlarına aynı model kodunu girin.");
        var modelCode = rows[0].ModelCode!.Trim();
        if (rows.FirstOrDefault(row => ProductFamilyKeyPolicy.ModelCodeKey(row.ModelCode) != ProductFamilyKeyPolicy.ModelCodeKey(modelCode)) is { } otherModel)
            return Refuse("PTTAVM_MODEL_CODE_MISMATCH", $"PTT AVM'ye bir ürün tek model koduyla gönderilir; {Name(otherModel)} varyantının model kodu ({otherModel.ModelCode!.Trim()}) '{modelCode}' değil.");
        var codes = externalCodes.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (codes.Count > 1 || codes.Count == 1 && !string.Equals(codes[0], modelCode, StringComparison.OrdinalIgnoreCase))
            return Refuse("PTTAVM_PARENT_BARCODE_MISMATCH", $"PTT AVM bu model ailesini '{string.Join("', '", codes)}' barkoduyla tutuyor, panelin model kodu '{modelCode}'; ürün yanlış barkoda gönderilmesin diye gönderilmedi. Model kodunu PTT AVM'deki ürün barkoduyla aynı yapın.", 409);
        var parent = codes.Count == 1 ? codes[0] : modelCode;
        if (parent.Length > PttAvmPriceInventoryRules.MaxBarcodeLength)
            return Refuse("BARCODE_REQUIRED", $"PTT AVM ürün barkodu en fazla {PttAvmPriceInventoryRules.MaxBarcodeLength} karakter olabilir; model kodu '{modelCode}' daha uzun.");

        // Every variant must be sendable: one left out would be deleted on PTT AVM.
        foreach (var row in rows)
        {
            var barcode = row.Barcode?.Trim();
            if (string.IsNullOrEmpty(barcode) || barcode.Length > PttAvmPriceInventoryRules.MaxBarcodeLength)
                return Refuse("BARCODE_REQUIRED", $"PTT AVM varyantı barkodla tanır; {Name(row)} varyantının barkodu yok (en fazla {PttAvmPriceInventoryRules.MaxBarcodeLength} karakter). Varyant gönderilmeden ailenin gönderimi PTT AVM'de onu silerdi.");
            if (string.Equals(barcode, parent, StringComparison.OrdinalIgnoreCase))
                return Refuse("PTTAVM_VARIANT_BARCODE_IS_PARENT", $"{Name(row)} varyantının barkodu ürün barkoduyla ({parent}) aynı; PTT AVM'de varyant barkodu ürün barkodundan farklı olmalıdır.");
            if (string.IsNullOrWhiteSpace(row.Color) || string.IsNullOrWhiteSpace(row.Size))
                return Refuse("PTTAVM_VARIANT_OPTIONS_REQUIRED", $"PTT AVM varyantı Renk ve Beden değeriyle tanır; {Name(row)} varyantında {(string.IsNullOrWhiteSpace(row.Color) ? "renk" : "beden")} seçeneği yok.");
            if (row.Color.Trim().Length > PttAvmProductPayloadRules.MaxVariantValueLength || row.Size.Trim().Length > PttAvmProductPayloadRules.MaxVariantValueLength)
                return Refuse("PTTAVM_VARIANT_OPTIONS_INVALID", $"{Name(row)} varyantının renk veya beden değeri {PttAvmProductPayloadRules.MaxVariantValueLength} karakterden uzun.");
            if (row.SalePrice is not { } price)
                return Refuse("CHANNEL_OFFER_REQUIRED", $"PTT AVM için her varyantta etkin fiyat teklifi gerekir; {Name(row)} için yok. Varyant gönderilmeden ailenin gönderimi PTT AVM'de onu silerdi.");
            if (!string.Equals(row.Currency, "TRY", StringComparison.OrdinalIgnoreCase) || price <= 1)
                return Refuse("CHANNEL_OFFER_INVALID", $"{Name(row)} için TRY para birimi ve 1'den büyük satış fiyatı gerekir.");
            if (PttAvmPriceInventoryRules.VatRate(row.VatRate) is null)
                return Refuse("VAT_RATE_INVALID", $"{Name(row)} için KDV oranı PTT AVM'de 0, 1, 10 veya 20 olmalıdır.");
            if (!row.HasInventory || row.Quantity is null)
                return Refuse("INVENTORY_REQUIRED", $"PTT AVM için her varyantta MAIN stok kaydı gerekir; {Name(row)} için yok. Varyant gönderilmeden ailenin gönderimi PTT AVM'de onu silerdi.");
            if (row.Desi is not > 0)
                return Refuse("DESI_REQUIRED", $"{Name(row)} için PTT AVM'nin istediği desi değeri girilmemiş.");
        }
        if (rows.GroupBy(row => row.Barcode!.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } sameBarcode)
            return Refuse("BARCODE_DUPLICATE", $"Model ailesinde '{sameBarcode.Key}' barkodu birden fazla varyantta var; PTT AVM'de her varyantın barkodu ayrı olmalıdır.", 409);
        if (rows.GroupBy(row => $"{row.Color!.Trim().ToUpperInvariant()}/{row.Size!.Trim().ToUpperInvariant()}", StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } sameOptions)
            return Refuse("PTTAVM_VARIANT_OPTIONS_DUPLICATE", $"{string.Join(" ve ", sameOptions.Select(Name))} aynı renk ve bedeni taşıyor ({sameOptions.First().Color!.Trim()} / {sameOptions.First().Size!.Trim()}); PTT AVM bunları ayrı varyant olarak tutamaz.");
        // PTT AVM's variants of a product share its price (the price difference is 0 on every variant it holds).
        if (rows.Select(row => row.SalePrice!.Value).Distinct().Count() > 1)
            return Refuse("PTTAVM_VARIANT_PRICE_DIFFERS", $"PTT AVM'de bir ürünün varyantları aynı fiyatla satılır; model ailesinde farklı satış fiyatları var ({string.Join(", ", rows.Select(row => row.SalePrice!.Value).Distinct().Order().Select(x => x.ToString("0.00", CultureInfo.InvariantCulture)))} TL). Fiyatları eşitleyin.");
        if (rows.Select(row => row.VatRate!.Value).Distinct().Count() > 1)
            return Refuse("PTTAVM_VARIANT_VAT_DIFFERS", "PTT AVM'de bir ürünün tek KDV oranı vardır; model ailesinin varyantlarında farklı KDV oranları var.");
        var total = rows.Sum(row => row.Quantity!.Value);
        if (total > PttAvmPriceInventoryRules.MaxQuantity)
            return Refuse("PTTAVM_STOCK_TOTAL_TOO_HIGH", $"PTT AVM bir ürünün stoğunu en fazla {PttAvmPriceInventoryRules.MaxQuantity} tutar; model ailesinin varyantlarında toplam {total} adet var.");

        var variants = rows.Select(row => new PttAvmProductPayloadRules.VariantDraft(row.Barcode!.Trim(), row.Color!.Trim(), row.Size!.Trim(), row.Quantity!.Value)).ToList();
        return (new FamilyPlan(parent, rows[0].SalePrice!.Value, rows[0].VatRate!.Value, Math.Ceiling(rows.Max(row => row.Desi!.Value)), variants), null);
    }

    private static ServiceResult<ProductPublicationDraft> Draft(Guid profileId, string externalCategoryId, PttAvmProductPayloadRules.ProductDraft draft, IReadOnlyList<PublicationVariantDraft> variants)
    {
        var payloadJson = PttAvmProductPayloadRules.Payload(draft);
        if (PttAvmProductPayloadRules.Read(payloadJson, out var invalid) is null) return Fail("PRODUCT_PAYLOAD_INVALID", invalid ?? "PTT AVM ürün içeriği geçersiz.");
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        return ServiceResult<ProductPublicationDraft>.Ok(new(profileId, externalCategoryId, string.Empty, payloadHash, payloadJson, variants));
    }

    private static ServiceResult<ProductPublicationDraft> NotFound() => ServiceResult<ProductPublicationDraft>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<ProductPublicationDraft> Fail(string code, string message, string? field = null, int status = 422) => ServiceResult<ProductPublicationDraft>.Fail(code, message, status, field is null ? null : new Dictionary<string, string[]> { [field] = [message] });
}
