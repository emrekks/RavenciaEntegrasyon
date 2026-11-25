using System.Text.Json;

namespace MarketplaceHub.Application;

// İşlem Takibi's grouped list: the product jobs one send queued for the colour cards of one model show as one row.
// A group is the same tenant, connection, job type, model (ProductFamilyKeyPolicy.FamilyKey: brand + model code) and
// send. The send is the job's correlation id: the server gives every request a fresh one, the panel sends one id
// (X-Correlation-ID) on every request of one Yayınla / Platformlarda Güncelle / bulk publish, and a bulk request queues
// all its cards under its own. A job without a connection, a product in its payload, or a card the catalog still
// holds stays on its own row; jobs older than the shared id carry one id each, so they stay one row each.
public static class JobModelGroupPolicy
{
    // The product jobs about one card: create, update, archive and the approval check that reads the send's answer.
    public static readonly IReadOnlySet<string> GroupedJobTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        MarketplaceJobTypes.ProductCreate, MarketplaceJobTypes.ProductUpdate, MarketplaceJobTypes.ProductArchive, MarketplaceJobTypes.ProductApprovalReconcile
    };

    // One send queues its cards within seconds (a 1,000-card bulk publish within minutes). The jobs of one key are cut
    // into groups oldest first: a group takes every job created less than this after its first one, the next job starts
    // the next group (Clusters). A key reused hours later (an approval check that carries its send's id) is so never
    // folded into the earlier group, and every job falls in exactly one group.
    public static readonly TimeSpan GroupSpan = TimeSpan.FromHours(1);

    // The approval check "Durumu Yenile" queues carries a fixed id per card (publication-status:<product>), one for every
    // click: it names no send, so such a job is never grouped.
    public const string StatusRefreshCorrelationPrefix = "publication-status:";

    public static bool IsGrouped(string jobType) => GroupedJobTypes.Contains(jobType);

    public static bool NamesASend(string correlationId) => !correlationId.StartsWith(StatusRefreshCorrelationPrefix, StringComparison.Ordinal);

    // The groups of one key's jobs (all of them, whatever their age), each oldest first: the same jobs always give the
    // same groups, whichever page or job they are read from.
    public static IReadOnlyList<IReadOnlyList<T>> Clusters<T>(IEnumerable<T> jobs, Func<T, DateTimeOffset> createdAt, Func<T, Guid> id)
    {
        var groups = new List<IReadOnlyList<T>>();
        List<T>? current = null;
        var start = DateTimeOffset.MinValue;
        foreach (var job in jobs.OrderBy(createdAt).ThenBy(job => id(job).ToString("N"), StringComparer.Ordinal))
        {
            if (current is null || createdAt(job) - start >= GroupSpan)
            {
                current = [];
                groups.Add(current);
                start = createdAt(job);
            }
            current.Add(job);
        }
        return groups;
    }

    // The card the job is about (ProductId of ProductPublicationJobPayload, ProductUpdateJobPayload, ProductArchiveJobPayload
    // and ProductApprovalReconciliationJobPayload); null when the payload names none.
    public static Guid? ProductId(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
                if (string.Equals(property.Name, "ProductId", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String && property.Value.TryGetGuid(out var id) && id != Guid.Empty)
                    return id;
            return null;
        }
        catch (JsonException) { return null; }
    }

    public static string Key(Guid connectionId, string jobType, string correlationId, string familyKey) =>
        $"{connectionId:N}|{jobType}|{correlationId}|{familyKey}";

    // The card's variants the job's payload carried: the distinct item codes (barcode, merchant SKU, stock code) of its
    // requests that name one of the card's variants by barcode or SKU. A PTT AVM or Shopify card's request carries the
    // whole family, so only the card's own variants count. Null when the payload holds no items (an approval check)
    // or its items name none of them: unknown, not zero.
    public static int? CardVariantCount(string jobType, string payloadJson, IReadOnlyCollection<(string Sku, string? Barcode)> cardVariants)
    {
        var write = JobWritePayloadReader.Read(jobType, payloadJson);
        if (write is null || write.Parts.Count == 0) return null;
        var codes = write.Parts.SelectMany(part => part.Barcodes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var count = cardVariants.Count(variant => codes.Contains(variant.Sku.Trim()) || variant.Barcode is { } barcode && codes.Contains(barcode.Trim()));
        return count > 0 ? count : null;
    }

    // The group's variants: the sum over its cards when every card's count is known, else null.
    public static int? GroupVariantCount(IEnumerable<int?> cardCounts)
    {
        var total = 0;
        foreach (var count in cardCounts)
        {
            if (count is not { } known) return null;
            total += known;
        }
        return total;
    }
}
