using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;

namespace MarketplaceHub.Infrastructure.Jobs;

// The pure parts of the Trendyol accounting read: which dates a run reads, how they are cut into the service's
// 15-day windows, and how a window's records are merged into the stored ones (the same record read again updates
// the stored one; nothing is stored twice).
internal static class TrendyolFinanceSyncPlan
{
    // The range a run reads. The first run goes back BackfillDays; a new run starts OverlapDays before what the last
    // finished run covered, so records Trendyol writes late (or fills in later) are read again. A run still in
    // progress (CoveredThrough past the last finished run) carries on where its last window ended.
    public static (DateTimeOffset Start, DateTimeOffset End)? Range(DateTimeOffset? coveredFrom, DateTimeOffset? coveredThrough, DateTimeOffset? lastSyncedAt,
        DateTimeOffset now, int backfillDays, int overlapDays)
    {
        DateTimeOffset start;
        if (coveredThrough is not { } through) start = now.AddDays(-Math.Max(1, backfillDays));
        else
        {
            var finished = lastSyncedAt is { } synced && through <= synced;
            start = finished ? through.AddDays(-Math.Max(0, overlapDays)) : through;
            if (coveredFrom is { } from && start < from) start = from;
        }
        return start < now ? (start, now) : null;
    }

    // [start, end) cut into consecutive windows of at most `maximum`, oldest first.
    public static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Windows(DateTimeOffset start, DateTimeOffset end, TimeSpan maximum)
    {
        if (maximum <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximum));
        var windows = new List<(DateTimeOffset, DateTimeOffset)>();
        for (var cursor = start; cursor < end; cursor += maximum)
            windows.Add((cursor, cursor + maximum < end ? cursor + maximum : end));
        return windows;
    }

    // The window's records by their key. A later read of the same record replaces an earlier one, except that the
    // service-fee mark (found through the PlatformServiceFee filter) is kept.
    public static Dictionary<(string Source, string ExternalId), RemoteStatementRecord> Collect(IEnumerable<RemoteStatementRecord> records)
    {
        var collected = new Dictionary<(string, string), RemoteStatementRecord>();
        foreach (var record in records)
        {
            var key = (record.Source, record.ExternalId);
            if (collected.TryGetValue(key, out var earlier) && record.TransactionSubType is null && earlier.TransactionSubType is not null)
                collected[key] = record with { TransactionSubType = earlier.TransactionSubType };
            else
                collected[key] = record;
        }
        return collected;
    }

    // Applies the window's records to the stored ones: a stored record is updated in place, a new one is returned to be
    // added. Returns how many were added and how many stored ones changed.
    public static (List<MarketplaceFinanceTransaction> Added, int Updated) Merge(
        IReadOnlyDictionary<(string Source, string ExternalId), MarketplaceFinanceTransaction> stored,
        IEnumerable<RemoteStatementRecord> incoming, Guid tenantId, Guid connectionId, DateTimeOffset now)
    {
        var added = new List<MarketplaceFinanceTransaction>();
        var updated = 0;
        foreach (var record in incoming)
        {
            if (stored.TryGetValue((record.Source, record.ExternalId), out var row))
            {
                // A record read again without the service-fee filter keeps the mark it got through it.
                var subType = record.TransactionSubType ?? (row.RequestedType == record.RequestedType ? row.TransactionSubType : null);
                if (Apply(row, record with { TransactionSubType = subType }, now)) updated++;
                continue;
            }
            var created = new MarketplaceFinanceTransaction
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                PlatformCode = "TRENDYOL",
                Source = record.Source,
                ExternalId = record.ExternalId,
                RequestedType = record.RequestedType,
                Category = MarketplaceFinanceCategories.Other,
                RawJson = record.RawJson,
                FirstSeenAt = now
            };
            Apply(created, record, now);
            added.Add(created);
        }
        return (added, updated);
    }

    private static bool Apply(MarketplaceFinanceTransaction row, RemoteStatementRecord record, DateTimeOffset now)
    {
        // Stored as the columns hold it (UTC times, texts cut to the column length), so an unchanged record compares equal.
        var value = record with
        {
            TransactionSubType = Short(record.TransactionSubType, 64),
            TransactionType = Short(record.TransactionType, 200),
            TransactionDate = record.TransactionDate.ToUniversalTime(),
            Description = Short(record.Description, 1000),
            OrderNumber = Short(record.OrderNumber, 128),
            ShipmentPackageId = Short(record.ShipmentPackageId, 128),
            Barcode = Short(record.Barcode, 160),
            PaymentOrderId = Short(record.PaymentOrderId, 64),
            PaymentDate = record.PaymentDate?.ToUniversalTime()
        };
        var category = TrendyolFinanceCategorizer.Charged(value.Source, value.RequestedType,
            TrendyolFinanceCategorizer.Categorize(value.Source, value.RequestedType, value.TransactionSubType, value.TransactionType, value.Description), value.Debt, value.Credit);
        var changed = row.RequestedType != value.RequestedType || row.TransactionSubType != value.TransactionSubType || row.TransactionType != value.TransactionType
            || row.Category != category || row.TransactionDate != value.TransactionDate || row.Description != value.Description || row.OrderNumber != value.OrderNumber
            || row.ShipmentPackageId != value.ShipmentPackageId || row.Barcode != value.Barcode || row.Debt != value.Debt || row.Credit != value.Credit
            || row.CommissionRate != value.CommissionRate || row.CommissionAmount != value.CommissionAmount || row.SellerRevenue != value.SellerRevenue
            || row.PaymentOrderId != value.PaymentOrderId || row.PaymentDate != value.PaymentDate || row.RawJson != value.RawJson;
        if (!changed) return false;
        row.RequestedType = value.RequestedType;
        row.TransactionSubType = value.TransactionSubType;
        row.TransactionType = value.TransactionType;
        row.Category = category;
        row.TransactionDate = value.TransactionDate;
        row.Description = value.Description;
        row.OrderNumber = value.OrderNumber;
        row.ShipmentPackageId = value.ShipmentPackageId;
        row.Barcode = value.Barcode;
        row.Debt = value.Debt;
        row.Credit = value.Credit;
        row.CommissionRate = value.CommissionRate;
        row.CommissionAmount = value.CommissionAmount;
        row.SellerRevenue = value.SellerRevenue;
        row.PaymentOrderId = value.PaymentOrderId;
        row.PaymentDate = value.PaymentDate;
        row.RawJson = value.RawJson;
        row.UpdatedAt = now;
        return true;
    }

    private static string? Short(string? value, int length) => value is null || value.Length <= length ? value : value[..length];
}
