using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class MarketplaceInvoiceReferenceHistoryRecorder
{
    public static void RecordPreviousReferences(
        AppDbContext db,
        ShipmentPackage package,
        string? nextInvoiceNumber,
        string? nextInvoiceUrl,
        DateTimeOffset changedAt,
        string source)
    {
        var previousNumber = Normalize(package.MarketplaceInvoiceNumber);
        var previousUrl = Normalize(package.MarketplaceInvoiceUrl);
        var nextNumber = Normalize(nextInvoiceNumber);
        var nextUrl = Normalize(nextInvoiceUrl);

        if (previousNumber is null && previousUrl is null) return;
        if (string.Equals(previousNumber, nextNumber, StringComparison.Ordinal)
            && string.Equals(previousUrl, nextUrl, StringComparison.Ordinal)) return;

        var reason = nextNumber is null && nextUrl is null
            ? "REMOVED"
            : nextUrl is null && previousUrl is not null
                ? "LINK_UNAVAILABLE"
                : "REPLACED";

        db.MarketplaceInvoiceReferenceHistories.Add(new MarketplaceInvoiceReferenceHistory
        {
            Id = Guid.CreateVersion7(),
            TenantId = package.TenantId,
            OrderId = package.OrderId,
            PackageId = package.Id,
            ConnectionId = package.ConnectionId,
            InvoiceNumber = previousNumber,
            InvoiceUrl = previousUrl,
            ChangeReason = $"{reason}:{source}",
            ChangedAt = changedAt
        });
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
