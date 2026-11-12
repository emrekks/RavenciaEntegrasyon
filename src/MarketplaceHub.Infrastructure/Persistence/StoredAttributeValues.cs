using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The import's category context maps every value a marketplace attribute lists (MarketplaceJobProcessor
// .EnsureMappedAttribute). It used to ask the database once per value for the panel value and up to twice for its value
// mapping; these read the rows once per attribute and answer each value's question in memory with what that query
// returned: the same filter, the same order (PostgreSQL compares text byte for byte and uuid in its written order), and
// the tracked instance when the context already tracks the row, else the row attached as read (a tracking query does both).
// They hold while nothing is saved between the read and the last question (the value loop saves nothing).
internal sealed class StoredAttributeValues
{
    private readonly AppDbContext db;
    private readonly List<AttributeValue> rows;

    private StoredAttributeValues(AppDbContext db, List<AttributeValue> rows)
    {
        this.db = db;
        this.rows = rows;
    }

    // The attribute's values stored under any of these lookup keys.
    public static async Task<StoredAttributeValues> ReadAsync(AppDbContext db, Guid tenantId, Guid attributeId, IEnumerable<string> lookupKeys, CancellationToken cancellationToken)
    {
        var keys = lookupKeys.Distinct(StringComparer.Ordinal).ToArray();
        var rows = keys.Length == 0
            ? []
            : await db.AttributeValues.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.AttributeId == attributeId && keys.Contains(x.NormalizedValue))
                .ToListAsync(cancellationToken);
        return new(db, rows);
    }

    // What .Where(valueKeys.Contains(NormalizedValue)).OrderBy(NormalizedValue == normalizedValue ? 0 : 1).ThenBy(Id)
    // .FirstOrDefault() returned.
    public AttributeValue? First(IReadOnlyCollection<string> valueKeys, string normalizedValue) => rows
        .Where(x => valueKeys.Contains(x.NormalizedValue, StringComparer.Ordinal))
        .OrderBy(x => string.Equals(x.NormalizedValue, normalizedValue, StringComparison.Ordinal) ? 0 : 1)
        .ThenBy(x => x.Id, StoredRowOrder.Uuid)
        .FirstOrDefault();

    public AttributeValue? Track(AttributeValue? row) => StoredRowOrder.Track(db.AttributeValues, row, row?.Id);
}

internal sealed class StoredAttributeValueMappings
{
    private readonly AppDbContext db;
    private readonly List<AttributeValueMapping> rows;

    private StoredAttributeValueMappings(AppDbContext db, List<AttributeValueMapping> rows)
    {
        this.db = db;
        this.rows = rows;
    }

    // The value mappings stored in one value scope (category/attribute) of the connection.
    public static async Task<StoredAttributeValueMappings> ReadAsync(AppDbContext db, Guid tenantId, Guid connectionId, string valueScope, CancellationToken cancellationToken) =>
        new(db, await db.AttributeValueMappings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == valueScope)
            .ToListAsync(cancellationToken));

    // What .SingleOrDefault(LocalId == localId && ExternalId == externalId) returned (one row at most: the scope, local id
    // and external id are unique together).
    public AttributeValueMapping? Exact(Guid localId, string? externalId) =>
        rows.SingleOrDefault(x => x.LocalId == localId && string.Equals(x.ExternalId, externalId, StringComparison.Ordinal));

    // What .OrderBy(LocalId).FirstOrDefault(ExternalId == externalId) returned.
    public AttributeValueMapping? FirstByLocalId(string? externalId) => rows
        .Where(x => string.Equals(x.ExternalId, externalId, StringComparison.Ordinal))
        .OrderBy(x => x.LocalId, StoredRowOrder.Uuid)
        .FirstOrDefault();

    public AttributeValueMapping? Track(AttributeValueMapping? row) => StoredRowOrder.Track(db.AttributeValueMappings, row, row?.Id);
}

internal static class StoredRowOrder
{
    // PostgreSQL orders uuid by its bytes as written (the text form), not by .NET's Guid.CompareTo.
    public static readonly IComparer<Guid> Uuid = Comparer<Guid>.Create((left, right) => string.CompareOrdinal(left.ToString("N"), right.ToString("N")));

    // A tracking query hands back the instance the context already tracks for that key, else tracks the row it read.
    public static T? Track<T>(DbSet<T> set, T? row, Guid? id) where T : class
    {
        if (row is null || id is not Guid key) return null;
        if (set.Local.FindEntry(key) is { } tracked) return tracked.Entity;
        set.Attach(row);
        return row;
    }
}
