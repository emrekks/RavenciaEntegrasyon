namespace MarketplaceHub.Infrastructure.Persistence;

internal static class CatalogMediaDisplay
{
    public static string Url(Guid assetId, string classification, string relativePath) =>
        classification == "PRODUCT_MEDIA_URL"
            ? relativePath
            : $"/api/v1/files/product-media/{assetId:D}/content";
}
