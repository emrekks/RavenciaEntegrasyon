using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed record OrderInvoiceReconciliationCandidate(Guid OrderId, string ExternalOrderId);

internal static class OrderInvoiceReconciliationBatchPolicy
{
    public static IReadOnlyList<OrderInvoiceReconciliationCandidate> SelectReturnClaimHydration(
        IReadOnlyCollection<(OrderInvoiceReconciliationCandidate Candidate, string? CustomerSnapshotJson)> candidates,
        IReadOnlySet<Guid> permanentlyUnreachableOrderIds,
        int batchSize)
    {
        if (batchSize <= 0) return [];
        return candidates
            .Where(candidate => !permanentlyUnreachableOrderIds.Contains(candidate.Candidate.OrderId)
                && TrendyolJsonMapper.IsReturnClaimReadModelSnapshot(candidate.CustomerSnapshotJson))
            .OrderBy(candidate => candidate.Candidate.OrderId)
            .Take(batchSize)
            .Select(candidate => candidate.Candidate)
            .ToArray();
    }

    public static IReadOnlyList<OrderInvoiceReconciliationCandidate> Select(
        IReadOnlyCollection<OrderInvoiceReconciliationCandidate> afterCursor,
        IReadOnlyCollection<OrderInvoiceReconciliationCandidate> wrapped,
        int batchSize)
    {
        if (batchSize <= 0) return [];
        return afterCursor.Take(batchSize)
            .Concat(wrapped.Take(Math.Max(0, batchSize - afterCursor.Count)))
            .ToArray();
    }

    public static Guid? ReadCursor(string? opaqueCursor)
    {
        if (string.IsNullOrWhiteSpace(opaqueCursor)) return null;
        try { return JsonSerializer.Deserialize<Guid>(opaqueCursor); }
        catch (JsonException) { return null; }
    }

    public static string WriteCursor(Guid orderId) => JsonSerializer.Serialize(orderId);
}
