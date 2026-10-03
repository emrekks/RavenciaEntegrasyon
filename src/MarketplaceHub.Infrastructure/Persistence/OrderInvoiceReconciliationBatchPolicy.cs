using System.Text.Json;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed record OrderInvoiceReconciliationCandidate(Guid OrderId, string ExternalOrderId);

internal static class OrderInvoiceReconciliationBatchPolicy
{
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
