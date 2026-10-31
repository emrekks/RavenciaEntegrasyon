using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// One SKU of an n11 product update; a field that was not picked stays null and is left out of the body.
internal sealed record N11SkuUpdate(string StockCode, string? Description, int? VatRate, int? PreparingDay, string? ShipmentTemplate, IReadOnlyList<N11SkuAttribute>? Attributes);

// The "Platformlarda Güncelle" picks n11 takes (ProductUpdateFields.SupportedBy("N11")).
internal sealed record N11UpdatePicks(bool Description, bool Attributes, bool VatRate, bool Delivery)
{
    public bool Any => Description || Attributes || VatRate || Delivery;
}

// n11 product update (developer.n11.com "Ürün Bilgisi Güncelleme": POST ms/product/tasks/product-update, at most 1000
// SKUs, answered with a task id read through the task details). A SKU is found by its stock code and n11 changes only
// the fields it carries: description, vatRate (0, 1, 10, 20), preparingDay (> 0), shipmentTemplate and attributes (the
// API also takes status, productMainId and maxPurchaseQuantity, which the panel does not offer here). Name, images,
// brand, category, colour/size, barcode and desi cannot change through the API: the n11 seller panel shows them as
// "Güncelleme Talep Et". Only the picked fields go:
// - DESCRIPTION: the listing's description override, else the product description.
// - ATTRIBUTES: the attribute list a create would send (N11ProductPublicationComposer), because n11 asks for every
//   attribute its category marks mandatory whenever attributes are sent. Brand and colour/size ride along unchanged.
// - VAT_RATE: the n11 offer's VAT rate.
// - DELIVERY: the product card's Teslimat Süresi (else the connection's preparing day) and the connection's shipment
//   template, the same pair a create sends.
// Price and stock do not go here: they go through the n11 price-stock task.
internal sealed class N11ProductUpdateComposer(AppDbContext db, IConfiguration configuration)
{
    internal const string EmptyItems = """{"items":[]}""";

    public async Task<ServiceResult<ProductUpdateDraft>> BuildAsync(Guid tenantId, Guid productId, Guid connectionId, IReadOnlyCollection<string>? fields, CancellationToken cancellationToken)
    {
        var picks = Picks(fields);
        if (!picks.Any) return Fail("PRODUCT_UPDATE_FIELDS_UNSUPPORTED", "Seçilen ürün bilgileri N11'e gönderilemiyor: N11 API ile yalnız açıklama, kategori özellikleri, KDV oranı ve teslimat süresi güncellenir.");
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken);
        if (product is null) return Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", status: 404);
        if (product.Status == ProductStatus.Archived) return Fail("PRODUCT_ARCHIVED", "Arşivlenmiş ürün güncellenemez.");
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "N11", cancellationToken);
        if (connection is null) return Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", status: 404);
        var profile = await db.ChannelListingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.ConnectionId == connectionId, cancellationToken);
        if (profile is null) return Fail("LISTING_PROFILE_REQUIRED", "N11 ürün güncellemesi için ürünün N11 yayın kaydı gerekir; ürünü önce N11'e yayınlayın.");
        if (!profile.Enabled) return Fail("LISTING_PROFILE_DISABLED", "Listing profile etkinleştirilmeden güncelleme işi oluşturulamaz.");

        // Every listing of the card takes the job's payload hash (the job checks it); only the SKUs n11 holds are sent.
        var listings = await (from listing in db.ChannelListingVariants.AsNoTracking()
                              join variant in db.ProductVariants.AsNoTracking() on new { listing.TenantId, listing.VariantId } equals new { variant.TenantId, VariantId = variant.Id }
                              where listing.TenantId == tenantId && listing.ProfileId == profile.Id
                              orderby variant.Id
                              select new { Listing = listing, Variant = variant }).ToListAsync(cancellationToken);
        if (listings.Count == 0) return Fail("PUBLICATION_STATE_REQUIRED", "Güncellenecek N11 yayın kaydı bulunamadı.", status: 409);
        var listingVariantIds = listings.Select(x => x.Variant.Id).ToArray();
        var links = (await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && listingVariantIds.Contains(x.VariantId))
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.VariantId).ToDictionary(group => group.Key, group => group.First());
        // n11 finds a SKU by the stock code its link holds. A link read without a stock code ("n11:{id}"), one n11 no
        // longer knows (NOT_FOUND) or refused (REJECTED), and an archived variant are not sent.
        var sendable = listings
            .Where(x => x.Variant.Status != ProductStatus.Archived && links.TryGetValue(x.Variant.Id, out var link) && Addressable(link))
            .Select(x => (x.Variant, StockCode: links[x.Variant.Id].ExternalId.Trim()))
            .ToList();
        if (sendable.Count == 0) return Fail("N11_PRODUCT_NOT_LISTED", "Ürünün N11'de stok koduyla eşleşen yayında bir varyantı yok; önce yayın durumunu yenileyin.", status: 409);
        if (sendable.Count > N11PriceInventoryRules.MaxSkusPerRequest) return Fail("PRODUCT_BATCH_LIMIT_EXCEEDED", $"Tek N11 ürün güncelleme isteğinde en fazla {N11PriceInventoryRules.MaxSkusPerRequest} varyant gönderilebilir.");

        string? description = null;
        if (picks.Description)
        {
            description = (profile.DescriptionOverride ?? product.Description).Trim();
            if (description.Length == 0) return Fail("PRODUCT_DESCRIPTION_INVALID", "N11 ürün açıklaması boş olamaz.");
        }

        int? preparingDay = null;
        string? shipmentTemplate = null;
        if (picks.Delivery)
        {
            var (template, connectionPreparingDay) = N11ProductPublicationComposer.PublishSettings(connection.SettingsJson);
            preparingDay = N11ProductPublicationComposer.PreparingDay(product.DeliveryDays, connectionPreparingDay);
            if (preparingDay is < 1 or > N11ProductPublicationComposer.MaxPreparingDay)
                return Fail("N11_PREPARING_DAY_INVALID", $"Ürün kartındaki teslimat süresi (yoksa N11 bağlantısındaki hazırlık süresi) 1-{N11ProductPublicationComposer.MaxPreparingDay} gün arasında olmalıdır.");
            // A connection without a chosen template keeps the template n11 already holds.
            shipmentTemplate = template;
        }

        var variantIds = sendable.Select(x => x.Variant.Id).ToArray();
        var offers = new Dictionary<Guid, ChannelOffer>();
        if (picks.VatRate)
        {
            offers = (await db.ChannelOffers.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).ToListAsync(cancellationToken))
                .GroupBy(x => x.VariantId)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.Status == "ACTIVE").First());
        }

        N11ProductPublicationComposer.AttributeContext? attributeContext = null;
        IReadOnlyList<N11ProductPublicationComposer.OptionRow> optionRows = [];
        var create = new N11ProductPublicationComposer(db, configuration);
        if (picks.Attributes)
        {
            var loaded = await create.LoadAttributeContextAsync(tenantId, product, connectionId, profile, cancellationToken);
            if (!loaded.Succeeded) return N11ProductPublicationComposer.Relay<N11ProductPublicationComposer.AttributeContext, ProductUpdateDraft>(loaded);
            attributeContext = loaded.Value!;
            optionRows = await create.LoadOptionRowsAsync(tenantId, productId, variantIds, cancellationToken);
        }
        var title = (profile.TitleOverride ?? product.Title).Trim();

        var skus = new List<N11SkuUpdate>(sendable.Count);
        foreach (var (variant, stockCode) in sendable)
        {
            int? vatRate = null;
            if (picks.VatRate)
            {
                if (!offers.TryGetValue(variant.Id, out var offer)) return Fail("CHANNEL_OFFER_REQUIRED", $"'{variant.Sku}' için N11 teklifi yok; KDV oranı gönderilemedi.");
                if (N11ProductPublicationComposer.VatRate(offer.VatRate) is not int rate)
                    return Fail("VAT_RATE_INVALID", $"'{variant.Sku}' için KDV oranı {offer.VatRate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}; N11 yalnız 0, 1, 10 veya 20 kabul eder.");
                vatRate = rate;
            }
            IReadOnlyList<N11SkuAttribute>? attributes = null;
            if (attributeContext is not null)
            {
                var composed = await create.VariantAttributesAsync(attributeContext, optionRows, variant, title, cancellationToken);
                if (!composed.Succeeded) return N11ProductPublicationComposer.Relay<IReadOnlyList<N11SkuAttribute>, ProductUpdateDraft>(composed);
                attributes = composed.Value!;
            }
            skus.Add(new(stockCode, description, vatRate, preparingDay, shipmentTemplate, attributes));
        }

        var payloadJson = Payload(N11PriceInventoryRules.Integrator, skus);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        // The job runs it as the content phase of an approved product: one task, the variant and delivery phases empty.
        var publication = new ProductUpdatePublication(productId, "APPROVED", hash, "{}", payloadJson, EmptyItems, EmptyItems);
        var draftVariants = listings.Select(x => new PublicationVariantDraft(x.Variant.Id, x.Listing.ExternalSku ?? x.Variant.Sku, x.Listing.ExternalBarcode ?? "")).ToList();
        return ServiceResult<ProductUpdateDraft>.Ok(new(profile.Id, publication, draftVariants));
    }

    // No list (an older client) is every field n11 takes; otherwise the picked ones n11 takes.
    internal static N11UpdatePicks Picks(IReadOnlyCollection<string>? fields)
    {
        var supported = ProductUpdateFields.SupportedBy("N11");
        var codes = fields is null ? supported : fields.Select(field => field?.Trim().ToUpperInvariant() ?? "").Where(supported.Contains).ToHashSet(StringComparer.Ordinal);
        return new(codes.Contains(ProductUpdateFields.Description), codes.Contains(ProductUpdateFields.Attributes), codes.Contains(ProductUpdateFields.VatRate), codes.Contains(ProductUpdateFields.Delivery));
    }

    internal static bool Addressable(MarketplaceVariantLink link) =>
        !string.IsNullOrWhiteSpace(link.ExternalId) && !link.ExternalId.StartsWith("n11:", StringComparison.Ordinal)
        && link.HealthStatus != MarketplaceVariantLinkHealth.NotFound && link.HealthStatus != MarketplaceVariantLinkHealth.Rejected;

    // The n11 body: {"payload":{"integrator":...,"skus":[{"stockCode":...,<picked fields>}]}}. An attribute carries id,
    // valueId and customValue (n11 asks for all three, the one that does not apply as null), as on create.
    internal static string Payload(string integrator, IReadOnlyList<N11SkuUpdate> skus)
    {
        var items = new JsonArray();
        foreach (var sku in skus)
        {
            var item = new JsonObject { ["stockCode"] = sku.StockCode };
            if (sku.Description is not null) item["description"] = sku.Description;
            if (sku.VatRate is int vatRate) item["vatRate"] = vatRate;
            if (sku.PreparingDay is int preparingDay) item["preparingDay"] = preparingDay;
            if (sku.ShipmentTemplate is not null) item["shipmentTemplate"] = sku.ShipmentTemplate;
            if (sku.Attributes is not null)
            {
                var attributes = new JsonArray();
                foreach (var attribute in sku.Attributes) attributes.Add(new JsonObject { ["id"] = attribute.Id, ["valueId"] = attribute.ValueId, ["customValue"] = attribute.CustomValue });
                item["attributes"] = attributes;
            }
            items.Add(item);
        }
        return new JsonObject { ["payload"] = new JsonObject { ["integrator"] = integrator, ["skus"] = items } }.ToJsonString();
    }

    private static ServiceResult<ProductUpdateDraft> Fail(string code, string message, int status = 422) => ServiceResult<ProductUpdateDraft>.Fail(code, message, status);
}
