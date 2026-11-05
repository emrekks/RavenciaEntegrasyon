using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The VAT rate each order line is invoiced at (InvoiceIssuePolicy.ResolveLineVatRate): the marketplace's own rate when
// it reported one, else the product card's KDV Oranı (the line's variant found as on the order screens,
// OrderLineCatalog); null when neither is known, and the invoice is stopped ("KDV Oranı Bilinmiyor"). Never 0 by default.
internal static class InvoiceLineVatRates
{
    public static async Task<IReadOnlyDictionary<Guid, decimal?>> ResolveAsync(AppDbContext db, Guid tenantId, string? platformCode, Guid connectionId, IReadOnlyCollection<OrderLine> lines, CancellationToken cancellationToken)
    {
        var rates = new Dictionary<Guid, decimal?>();
        var unknown = lines.Where(line => InvoiceIssuePolicy.IsLineVatRateUnknown(platformCode, line.SourceSnapshotJson)).ToList();
        foreach (var line in lines.Except(unknown)) rates[line.Id] = line.VatRate;
        if (unknown.Count == 0) return rates;
        var catalog = await OrderLineCatalog.LoadAsync(db, tenantId, unknown, _ => connectionId, cancellationToken);
        var variants = unknown.ToDictionary(line => line.Id, catalog.Variant);
        var productIds = variants.Values.Where(variant => variant is not null).Select(variant => variant!.ProductId).Distinct().ToArray();
        var cardRates = productIds.Length == 0
            ? new Dictionary<Guid, decimal?>()
            : await db.Products.AsNoTracking().Where(product => product.TenantId == tenantId && productIds.Contains(product.Id))
                .ToDictionaryAsync(product => product.Id, product => product.VatRate, cancellationToken);
        foreach (var line in unknown)
        {
            var cardRate = variants[line.Id] is { } variant ? cardRates.GetValueOrDefault(variant.ProductId) : null;
            rates[line.Id] = InvoiceIssuePolicy.ResolveLineVatRate(platformCode, line.SourceSnapshotJson, line.VatRate, cardRate);
        }
        return rates;
    }

    // The lines with the rate they are invoiced at; false when one is unknown.
    public static bool TryApply(IReadOnlyList<OrderLine> lines, IReadOnlyDictionary<Guid, decimal?> rates, out IReadOnlyDictionary<Guid, decimal> resolved)
    {
        var result = new Dictionary<Guid, decimal>();
        resolved = result;
        foreach (var line in lines)
        {
            if (rates.GetValueOrDefault(line.Id) is not { } rate) return false;
            result[line.Id] = rate;
        }
        return true;
    }
}
