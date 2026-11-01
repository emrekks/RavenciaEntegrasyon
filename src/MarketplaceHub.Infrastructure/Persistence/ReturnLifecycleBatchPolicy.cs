namespace MarketplaceHub.Infrastructure.Persistence;

internal static class ReturnLifecycleBatchPolicy
{
    // Trendyol's getClaims takes one or several claim ids in claimIds ("Tekil veya çoğul"), comma separated; the other
    // parameters are ignored then. A scan asks for at most this many claims in one request, below the default page size.
    public const int ClaimIdsPerRequest = 20;

    public static (int IncompleteCargo, int CompleteCargo) SplitBatch(int batchSize)
    {
        var boundedSize = Math.Clamp(batchSize, 2, 100);
        var incompleteCargo = boundedSize / 2;
        return (incompleteCargo, boundedSize - incompleteCargo);
    }

    public static IReadOnlyList<string[]> ClaimIdRequests(IEnumerable<string> externalClaimIds) =>
        externalClaimIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Chunk(ClaimIdsPerRequest).ToArray();
}
