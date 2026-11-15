using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Speed plan item 9 (10 Nov 2026): a brand read wrote the whole list (Trendyol 813k, Pazarama 104k rows) into a staging
// snapshot, hashed it and deleted it again when it equalled the current one (8.76M inserts, 7.46M deletes in 6 days).
// Each read item is now compared, as it streams in, with the row the current snapshot holds at the same position (every
// stored column but the row id). While they agree nothing is written; the first difference opens the staging snapshot,
// copies the agreeing rows already passed into it (in the database) and the read goes on exactly as before. Two equal
// lists are equal row by row, so their ContentHash (HashReferenceItemsAsync) is equal too and the current snapshot stays.
public sealed partial class MarketplaceJobProcessor
{
    internal sealed record StoredBrandList(Guid SnapshotId, string ContentHash, UInt128[] Digests);

    // The current brand snapshot's rows by position, or null when there is none or its rows cannot be compared
    // (an older row without a position): the read then writes as before.
    private async Task<StoredBrandList?> LoadStoredBrandListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var snapshot = await db.ReferenceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "BRANDS" && x.ScopeExternalId == "" && x.IsCurrent)
            .Select(x => new { x.Id, x.ContentHash, x.ItemCount })
            .SingleOrDefaultAsync(cancellationToken);
        if (snapshot is null || snapshot.ItemCount <= 0) return null;
        var digests = new UInt128[snapshot.ItemCount];
        var filled = new bool[snapshot.ItemCount];
        var count = 0;
        await foreach (var row in db.ReferenceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SnapshotId == snapshot.Id && x.ResourceType == "BRANDS")
            .Select(x => new { x.ExternalId, x.ParentExternalId, x.Name, x.NormalizedName, x.Path, x.Depth, x.IsLeaf, x.IsActive, x.IsRequired, x.AllowsCustomValue, x.AllowsMultipleValues, x.PayloadHash, x.SortOrder })
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken))
        {
            if (row.SortOrder is not { } position || position < 0 || position >= digests.Length || filled[position]) return null;
            digests[position] = BrandRowDigest(row.ExternalId, row.ParentExternalId, row.Name, row.NormalizedName, row.Path, row.Depth, row.IsLeaf, row.IsActive, row.IsRequired, row.AllowsCustomValue, row.AllowsMultipleValues, row.PayloadHash);
            filled[position] = true;
            count++;
        }
        return count == digests.Length ? new(snapshot.Id, snapshot.ContentHash, digests) : null;
    }

    // Whether the read item at this position is the stored row there, column for column (as SyncBrandReferences writes it).
    internal static bool SameBrandRow(StoredBrandList stored, int position, RemoteReferenceItem item) =>
        position < stored.Digests.Length && stored.Digests[position] == RemoteBrandDigest(item);

    internal static UInt128 RemoteBrandDigest(RemoteReferenceItem item) =>
        BrandRowDigest(item.ExternalId, null, item.Name, item.Name.Trim().ToUpperInvariant(), item.Path, item.Depth, item.IsLeaf, item.IsActive, item.IsRequired, item.AllowsCustomValue, item.AllowsMultipleValues, Hash(item.RawJson));

    private static UInt128 BrandRowDigest(string externalId, string? parentExternalId, string name, string normalizedName, string path, int depth, bool isLeaf, bool isActive, bool? isRequired, bool? allowsCustomValue, bool? allowsMultipleValues, string payloadHash)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new object?[] { externalId, parentExternalId, name, normalizedName, path, depth, isLeaf, isActive, isRequired, allowsCustomValue, allowsMultipleValues, payloadHash });
        return BinaryPrimitives.ReadUInt128LittleEndian(SHA256.HashData(bytes));
    }

    // The rows before the first difference agreed with the current snapshot: they are copied into the staging one in the
    // database instead of being sent again.
    private Task<int> CopyStoredBrandRowsAsync(Guid tenantId, Guid storedSnapshotId, Guid stagingSnapshotId, int count, CancellationToken cancellationToken) =>
        count <= 0
            ? Task.FromResult(0)
            : db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO integration.reference_items ("Id", "TenantId", "ConnectionId", "SnapshotId", "ResourceType", "ExternalId", "ParentExternalId", "Name", "NormalizedName", "Path", "Depth", "IsLeaf", "IsActive", "IsRequired", "AllowsCustomValue", "AllowsMultipleValues", "PayloadHash", "SortOrder")
                SELECT gen_random_uuid(), "TenantId", "ConnectionId", {stagingSnapshotId}, "ResourceType", "ExternalId", "ParentExternalId", "Name", "NormalizedName", "Path", "Depth", "IsLeaf", "IsActive", "IsRequired", "AllowsCustomValue", "AllowsMultipleValues", "PayloadHash", "SortOrder"
                FROM integration.reference_items
                WHERE "TenantId" = {tenantId} AND "SnapshotId" = {storedSnapshotId} AND "ResourceType" = 'BRANDS' AND "SortOrder" < {count}
                """, cancellationToken);
}
