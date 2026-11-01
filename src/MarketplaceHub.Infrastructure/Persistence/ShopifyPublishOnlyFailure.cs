using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// A Shopify product create that wrote the product and stopped at its Online Store publication ("Yayına alınamadı").
// Before the publish was fixed (live 2026-10-27/28) such a create ended EXTERNAL_EFFECT_AMBIGUOUS with the adapter's
// SHOPIFY_PRODUCT_CREATED_BUT_PUBLISH_FAILED in its summary: the product was written for certain, only the publication
// was not. Those jobs are shown with the publish code (a read mapping, the rows are not changed), and a retry of a job
// whose attempt ended that way only publishes the product (ShopifyHttpClient.PublishOnlyKey), never writes or creates it.
// A summary without the adapter's code proves nothing and stays ambiguous.
internal static class ShopifyPublishOnlyFailure
{
    internal const string AmbiguousCode = "EXTERNAL_EFFECT_AMBIGUOUS";
    private const string LegacyMarker = ShopifyHttpClient.PublishFailedCode + ":";

    internal static bool IsLegacyAmbiguous(string jobType, string? errorCode, string? errorSummary) =>
        jobType == MarketplaceJobTypes.ProductCreate
        && errorCode == AmbiguousCode
        && errorSummary?.Contains(LegacyMarker, StringComparison.Ordinal) == true;

    // The code and summary a job screen shows.
    internal static (string? Code, string? Summary) Display(string jobType, string? errorCode, string? errorSummary) =>
        IsLegacyAmbiguous(jobType, errorCode, errorSummary)
            ? (ShopifyHttpClient.PublishFailedCode, $"{ShopifyHttpClient.PublishFailedLabel}: Shopify ürünü oluşturdu ancak Online Store yayını yapılamadı. İşi Yeniden Dene ile tekrarlayın; ürün yeniden oluşturulmaz ve yazılmaz, yalnızca yayın adımı tekrarlanır.")
            : (errorCode, errorSummary);

    // Whether an earlier attempt of this job wrote the product and stopped at the publication.
    internal static Task<bool> EarlierAttemptWroteProductAsync(AppDbContext db, Guid tenantId, Guid jobId, CancellationToken cancellationToken) =>
        db.JobAttempts.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.JobId == jobId && x.ErrorCode != null
            && (x.ErrorCode == ShopifyHttpClient.PublishFailedCode
                || x.ErrorCode == ShopifyHttpClient.PublishRetryCode
                || x.ErrorCode == AmbiguousCode && x.ErrorSummary != null && x.ErrorSummary.Contains(LegacyMarker)), cancellationToken);
}
