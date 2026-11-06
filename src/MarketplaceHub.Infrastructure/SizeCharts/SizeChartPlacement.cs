using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.SizeCharts;

// A card's size chart is always its last picture: in the strip, in every variant's list and in what goes to the
// marketplaces, whatever order the rows were saved in (an upload after it, a drag that put it first).
public static class SizeChartPlacement
{
    // The urls with the chart moved to the end (compared as the same picture, CatalogImageIdentity.Key).
    public static IReadOnlyList<string> Last(IReadOnlyList<string> urls, string? chartUrl)
    {
        if (string.IsNullOrWhiteSpace(chartUrl) || urls.Count == 0) return urls;
        var key = CatalogImageIdentity.Key(chartUrl);
        var chart = urls.Where(url => string.Equals(CatalogImageIdentity.Key(url), key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (chart.Count == 0) return urls;
        return urls.Where(url => !string.Equals(CatalogImageIdentity.Key(url), key, StringComparison.OrdinalIgnoreCase)).Concat(chart.Take(1)).ToList();
    }

    // A variant's publication pictures with the chart last, and the chart added when the variant's own pictures lack
    // it (it belongs to every variant of the card). The chart is taken from the card's rows (chartRows).
    public static List<T> Last<T>(List<T> pictures, IEnumerable<T> chartRows, Func<T, bool> isChart)
    {
        var chart = pictures.FirstOrDefault(isChart) ?? chartRows.FirstOrDefault(isChart);
        if (chart is null) return pictures;
        var result = pictures.Where(item => !isChart(item)).ToList();
        result.Add(chart);
        return result;
    }

    // A marketplace takes at most `limit` pictures: when the list is longer, the size chart (its last) still goes, as
    // the last one sent, in place of the picture before it.
    public static IReadOnlyList<string> TakeKeepingLast(IReadOnlyList<string> urls, int limit, bool lastIsSizeChart) =>
        urls.Count <= limit ? urls
        : lastIsSizeChart && limit > 0 ? urls.Take(limit - 1).Append(urls[^1]).ToArray()
        : urls.Take(limit).ToArray();

    // The asset of the card's size chart, null when it has none.
    public static Task<Guid?> AssetIdAsync(AppDbContext db, Guid tenantId, Guid productId, CancellationToken cancellationToken) =>
        db.ProductSizeCharts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProductId == productId && x.FileAssetId != null)
            .Select(x => x.FileAssetId)
            .FirstOrDefaultAsync(cancellationToken);
}
