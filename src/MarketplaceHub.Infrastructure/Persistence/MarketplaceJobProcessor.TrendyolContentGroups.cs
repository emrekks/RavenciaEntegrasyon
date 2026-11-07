using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;

namespace MarketplaceHub.Infrastructure.Persistence;

// The Trendyol content request of a product update, turned from per barcode into per content right before the send:
// each barcode's content is read from the approved list (read only), then each content goes once with its own variants'
// data (TrendyolContentGroups). A barcode whose content was already read with another barcode costs no request.
public sealed partial class MarketplaceJobProcessor
{
    private async Task<(string? ContentPayloadJson, IReadOnlyList<string> NotFound, JobExecutionResult? Stop)> ResolveTrendyolContentGroupsAsync(Guid tenantId, Guid connectionId, IntegrationJob job, ChannelListingProfile profile, string contentPayloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var contentByBarcode = new Dictionary<string, TrendyolContentObservation>(StringComparer.OrdinalIgnoreCase);
        var answered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var barcode in TrendyolContentGroups.Barcodes(contentPayloadJson))
        {
            if (!answered.Add(barcode)) continue;
            TrackRequest();
            var result = await products.ListCatalogAsync(
                Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:content-lookup:{barcode}"),
                new(null, 100),
                new ProductReadFilter(null, Barcode: barcode),
                cancellationToken);
            if (!result.IsSuccess)
            {
                TrackResultFailure(result.Error);
                var failure = JobExecutionResult.FromAdapterError(result.Error!);
                return (null, [], failure.Kind == JobCompletionKind.Retry ? failure : await MarkPublicationResult(tenantId, connectionId, profile, "UPDATE_BLOCKED", result.Error!.Code, failure, cancellationToken));
            }
            var observed = TrendyolContentGroups.Observe(barcode, result.Value!.Items);
            if (!observed.Succeeded)
                return (null, [], await MarkPublicationResult(tenantId, connectionId, profile, "UPDATE_BLOCKED", observed.Error!.Code, JobExecutionResult.Blocked(observed.Error.Code, observed.Error.Message), cancellationToken));
            if (observed.Value is not { } content) continue;
            contentByBarcode[barcode] = content;
            // The content's other variants are in the same answer: they need no request of their own.
            foreach (var sibling in content.Barcodes)
            {
                if (contentByBarcode.ContainsKey(sibling)) continue;
                contentByBarcode[sibling] = content;
                answered.Add(sibling);
            }
        }
        var roles = TrendyolContentGroups.CarriesAttributes(contentPayloadJson)
            ? await ReadTrendyolAttributeRolesAsync(tenantId, connectionId, profile.ExternalCategoryId, job, correlationId, cancellationToken)
            : null;
        var resolved = TrendyolContentGroups.Resolve(contentPayloadJson, contentByBarcode, roles);
        if (!resolved.Succeeded)
            return (null, [], await MarkPublicationResult(tenantId, connectionId, profile, "UPDATE_BLOCKED", resolved.Error!.Code, JobExecutionResult.Blocked(resolved.Error.Code, resolved.Error.Message), cancellationToken));
        return (resolved.Value, TrendyolContentGroups.Unresolved(contentPayloadJson, contentByBarcode), null);
    }

    // The category's varianter and slicer attributes, read from Trendyol's category attribute list (a read). Null when the
    // card has no category or the list could not be read: the content is then built without leaving anything out, and
    // any attribute that differs between its variants stops it.
    private async Task<TrendyolAttributeRoles?> ReadTrendyolAttributeRolesAsync(Guid tenantId, Guid connectionId, string? externalCategoryId, IntegrationJob job, string correlationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalCategoryId) || references is null) return null;
        var rows = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 5; page++)
        {
            TrackRequest();
            var result = await references.ReadAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:category-attributes:{externalCategoryId}:{cursor}"), new("CATEGORY_ATTRIBUTES", externalCategoryId.Trim()), new(cursor, 1000), cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); return null; }
            rows.AddRange(result.Value!.Items.Select(item => item.RawJson));
            cursor = result.Value.NextCursor;
            if (string.IsNullOrWhiteSpace(cursor)) return TrendyolAttributeRoles.FromCategoryAttributes(rows);
        }
        return null;
    }
}
