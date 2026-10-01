using System.Text.Json;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed record HepsiburadaInvoiceOrderCandidate(Guid OrderId, string ExternalOrderId, DateTimeOffset UpdatedAt, DateTimeOffset OrderedAt);

internal static class HepsiburadaInvoiceReconciliationBatchPolicy
{
    public static IReadOnlyList<HepsiburadaInvoiceOrderCandidate> Select(
        IReadOnlyCollection<HepsiburadaInvoiceOrderCandidate> afterCursor,
        IReadOnlyCollection<HepsiburadaInvoiceOrderCandidate> wrapped,
        int batchSize)
    {
        if (batchSize <= 0) return [];
        return afterCursor.Take(batchSize)
            .Concat(wrapped.Take(Math.Max(0, batchSize - afterCursor.Count)))
            .ToArray();
    }

    public static HepsiburadaInvoiceOrderCandidate? ReadCursor(string? opaqueCursor)
    {
        if (string.IsNullOrWhiteSpace(opaqueCursor)) return null;
        try { return JsonSerializer.Deserialize<HepsiburadaInvoiceOrderCandidate>(opaqueCursor); }
        catch (JsonException) { return null; }
    }

    public static string WriteCursor(HepsiburadaInvoiceOrderCandidate candidate) => JsonSerializer.Serialize(candidate);
}
