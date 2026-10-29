using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// The approval state of many products from one walk of Pazarama's own lists: the approved list (approved, or
// archived when closed for sale) and then the unapproved one (waiting, or turned down). Asked one barcode at a time,
// the same answer costs up to three requests per barcode (see LookupPublicationAsync); a seller with a few hundred
// listed products is walked in a few pages of 100. Each item is read by the same mapper as the single lookup, and a
// barcode on the approved list wins over the same barcode on the unapproved one, as the lookup asks the approved list
// first. A barcode neither list showed is left out: the caller looks it up on its own.
public sealed partial class PazaramaHttpClient : IProductApprovalSnapshotPort
{
    public Task<AdapterResult<ProductApprovalSnapshot>> ReadApprovalSnapshotAsync(AdapterContext context, int maxRequests, CancellationToken cancellationToken) =>
        WalkApprovalSnapshotAsync(page => ListCatalogAsync(context, page, new ProductReadFilter(null, IncludePendingApproval: true), cancellationToken), maxRequests, cancellationToken);

    internal static async Task<AdapterResult<ProductApprovalSnapshot>> WalkApprovalSnapshotAsync(
        Func<AdapterPageRequest, Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>>> list,
        int maxRequests,
        CancellationToken cancellationToken)
    {
        var statuses = new Dictionary<string, RemotePublicationStatus>(StringComparer.OrdinalIgnoreCase);
        string? cursor = null;
        var requests = 0;
        RateLimitMetadata? rate = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The first unapproved page may take two requests (page 1, then page 0 on a server counting from zero).
            var cost = cursor is not null && cursor.StartsWith("U:1:", StringComparison.Ordinal) ? 2 : 1;
            if (requests + cost > maxRequests) return AdapterResult<ProductApprovalSnapshot>.Success(new(statuses, false, requests), rate);
            requests += cost;
            var page = await list(new AdapterPageRequest(cursor, ProductPageSize));
            rate = page.RateLimit ?? rate;
            if (!page.IsSuccess) return AdapterResult<ProductApprovalSnapshot>.Failure(page.Error!, page.RateLimit);
            foreach (var product in page.Value!.Items)
                foreach (var variant in product.Variants)
                {
                    var code = variant.Barcode?.Trim();
                    if (string.IsNullOrEmpty(code) || statuses.ContainsKey(code)) continue;
                    try
                    {
                        using var item = JsonDocument.Parse(variant.RawJson);
                        statuses[code] = PazaramaProductWriteMapper.Publication(item.RootElement, code, product.IsPendingApproval);
                    }
                    catch (JsonException) { }
                }
            if (!page.Value.HasMore || string.IsNullOrWhiteSpace(page.Value.NextCursor)) return AdapterResult<ProductApprovalSnapshot>.Success(new(statuses, true, requests), rate);
            if (page.Value.NextCursor == cursor) return AdapterResult<ProductApprovalSnapshot>.Success(new(statuses, false, requests), rate);
            cursor = page.Value.NextCursor;
        }
    }
}
