using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The panel product of a question whose marketplace sent no photo or no product codes (n11 sends neither, only its
// productId and the listing title): its photo and its codes (model code, and the stock code and barcode when one
// variant is named) fill what the question lacks. The product is found only by an exact identity on the same store,
// and only when exactly one panel product answers; otherwise the question keeps its placeholder and its own codes:
//  1. the question's own stock code or barcode, matched as order lines are (OrderLineCatalog);
//  2. the marketplace product id through the product link (n11 links are keyed "n11:{productId}");
//  3. the store's own order lines with the very same listing title, whose stock codes name the variant.
internal static class QuestionProductImages
{
    private sealed record Match(string? ImageUrl, string? ModelCode, string? Sku, string? Barcode);

    public static async Task<IReadOnlyList<MarketplaceQuestionView>> FillAsync(AppDbContext db, Guid tenantId, IReadOnlyList<MarketplaceQuestionView> views, CancellationToken cancellationToken)
    {
        var missing = views.Where(view => string.IsNullOrWhiteSpace(view.ProductImageUrl)
            || string.IsNullOrWhiteSpace(view.ProductSku) && string.IsNullOrWhiteSpace(view.ProductBarcode) && string.IsNullOrWhiteSpace(view.ProductModelCode)).ToList();
        if (missing.Count == 0) return views;
        var missingIds = missing.Select(view => view.Id).ToArray();
        var externalIds = await db.MarketplaceQuestions.AsNoTracking()
            .Where(question => question.TenantId == tenantId && missingIds.Contains(question.Id) && question.ProductExternalId != null)
            .ToDictionaryAsync(question => question.Id, question => question.ProductExternalId!, cancellationToken);
        var found = new Dictionary<Guid, Match>();
        foreach (var view in missing)
        {
            var match = await CodesMatchAsync(db, tenantId, view, cancellationToken)
                ?? await ProductIdMatchAsync(db, tenantId, view.ConnectionId, externalIds.GetValueOrDefault(view.Id), cancellationToken)
                ?? await OrderedTitleMatchAsync(db, tenantId, view.ConnectionId, view.ProductName, cancellationToken);
            if (match is not null) found[view.Id] = match;
        }
        return found.Count == 0 ? views : views.Select(view => found.TryGetValue(view.Id, out var match) ? Filled(view, match) : view).ToList();
    }

    // The marketplace's own values stay; the panel's fill only what is empty.
    private static MarketplaceQuestionView Filled(MarketplaceQuestionView view, Match match) => view with
    {
        ProductImageUrl = string.IsNullOrWhiteSpace(view.ProductImageUrl) ? match.ImageUrl : view.ProductImageUrl,
        ProductModelCode = string.IsNullOrWhiteSpace(view.ProductModelCode) ? match.ModelCode : view.ProductModelCode,
        ProductSku = string.IsNullOrWhiteSpace(view.ProductSku) ? match.Sku : view.ProductSku,
        ProductBarcode = string.IsNullOrWhiteSpace(view.ProductBarcode) ? match.Barcode : view.ProductBarcode
    };

    private static Match VariantMatch(OrderLineCatalog catalog, ProductVariant variant) =>
        new(catalog.CatalogImageUrl(variant), Blank(variant.ModelCode), Blank(variant.Sku), Blank(variant.Barcode));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<Match?> CodesMatchAsync(AppDbContext db, Guid tenantId, MarketplaceQuestionView view, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(view.ProductSku) && string.IsNullOrWhiteSpace(view.ProductBarcode)) return null;
        var line = new OrderLine { Id = Guid.Empty, TenantId = tenantId, OrderId = Guid.Empty, ExternalLineId = "", Sku = view.ProductSku?.Trim() ?? "", Barcode = view.ProductBarcode?.Trim(), TitleSnapshot = view.ProductName ?? "", RawStatus = "" };
        var catalog = await OrderLineCatalog.LoadAsync(db, tenantId, [line], _ => view.ConnectionId, cancellationToken);
        return catalog.Variant(line) is { } variant ? VariantMatch(catalog, variant) : null;
    }

    // A product link names the product, not one of its variants: its photo and model code, no stock code.
    private static async Task<Match?> ProductIdMatchAsync(AppDbContext db, Guid tenantId, Guid connectionId, string? productId, CancellationToken cancellationToken)
    {
        var id = productId?.Trim();
        if (string.IsNullOrEmpty(id)) return null;
        string[] keys = [id, $"n11:{id}"];
        var productIds = await db.MarketplaceProductLinks.AsNoTracking()
            .Where(link => link.TenantId == tenantId && link.ConnectionId == connectionId && keys.Contains(link.ExternalId))
            .Select(link => link.ProductId).Distinct().Take(2).ToListAsync(cancellationToken);
        if (productIds.Count != 1) return null;
        var variants = await db.ProductVariants.AsNoTracking().Where(variant => variant.TenantId == tenantId && variant.ProductId == productIds[0])
            .OrderBy(variant => variant.SortOrder).ThenBy(variant => variant.Id).Select(variant => new { variant.Id, variant.ModelCode }).Take(100).ToListAsync(cancellationToken);
        var images = await OrderLineCatalog.MediaUrlsAsync(db, tenantId, variants.Select(variant => variant.Id), cancellationToken);
        var modelCodes = variants.Select(variant => Blank(variant.ModelCode)).Where(code => code is not null).Distinct().ToList();
        return new(variants.Select(variant => images.GetValueOrDefault(variant.Id)).FirstOrDefault(url => url is not null), modelCodes.Count == 1 ? modelCodes[0] : null, null, null);
    }

    private static async Task<Match?> OrderedTitleMatchAsync(AppDbContext db, Guid tenantId, Guid connectionId, string? productName, CancellationToken cancellationToken)
    {
        var title = productName?.Trim();
        if (string.IsNullOrEmpty(title)) return null;
        var lines = await (from line in db.OrderLines.AsNoTracking()
                           join order in db.Orders.AsNoTracking() on new { line.TenantId, line.OrderId } equals new { order.TenantId, OrderId = order.Id }
                           where line.TenantId == tenantId && order.ConnectionId == connectionId && line.TitleSnapshot == title
                           orderby line.Id descending
                           select line).Take(20).ToListAsync(cancellationToken);
        if (lines.Count == 0) return null;
        var catalog = await OrderLineCatalog.LoadAsync(db, tenantId, lines, _ => connectionId, cancellationToken);
        var variants = lines.Select(catalog.Variant).Where(variant => variant is not null).Select(variant => variant!).DistinctBy(variant => variant.Id).ToList();
        if (variants.Count == 0 || variants.Select(variant => variant.ProductId).Distinct().Count() != 1) return null;
        // One listing title is one variant on n11; should several answer, only what they share is shown.
        var shown = VariantMatch(catalog, variants[0]);
        return variants.Count == 1 ? shown : shown with { Sku = null, Barcode = null, ImageUrl = variants.Select(catalog.CatalogImageUrl).FirstOrDefault(url => url is not null) };
    }
}
