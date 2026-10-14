namespace MarketplaceHub.Infrastructure.Persistence;

internal static class ReturnLifecycleBatchPolicy
{
    public static (int IncompleteCargo, int CompleteCargo) SplitBatch(int batchSize)
    {
        var boundedSize = Math.Clamp(batchSize, 2, 100);
        var incompleteCargo = boundedSize / 2;
        return (incompleteCargo, boundedSize - incompleteCargo);
    }
}
