using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MarketplaceHub.Infrastructure.Persistence;

// Kuyruk Faz 1B (27 Nov 2026): a brand list read again is written as the difference to the current snapshot, in place.
// Before, any difference (one new brand, one shifted position) built a whole new snapshot (Trendyol ~820k, Pazarama ~100k
// rows) and the retention pass deleted the old one a day later: ~1M inserts and ~1M deletes a day through five indexes,
// GIN included, and a table of 1 GB. Now:
//   1. The current snapshot's rows are loaded as (ExternalId -> row id, digest of every stored column but Id, SnapshotId
//      and SortOrder; no brand reader orders by SortOrder). Nothing is written while the list is read.
//   2. The whole list is read with the same guards as before (ReadBrandPagesAsync). A failed or partial read throws and
//      changes nothing. An empty list, or one more than 20 % (and 50 rows) shorter than the current one, is unreliable: nothing
//      is written or deleted, an operational issue is raised and the job is blocked with its cause.
//   3. One transaction locks the current snapshot row (FOR UPDATE: a second run of the same list waits, a run that finds
//      the snapshot changed retries), deletes the vanished rows, updates the changed ones (only where a column IS
//      DISTINCT FROM the read), inserts the new ones, checks the row count and stores the new content hash. Readers keep
//      reading the snapshot they read before (they are never blocked: only RowExclusive and row locks are taken) and see
//      the whole new list once the transaction commits, never half of it.
//   4. A mapping on the current snapshot whose brand vanished or changed so that it would not have been carried to a new
//      snapshot before (ReferenceMappingRevalidationPolicy) keeps exactly what it had: its brand's previous row is copied
//      into a small not-current "RETIRED-" snapshot and the mapping points at it, as it pointed at the old full snapshot
//      before. The retention pass keeps that snapshot while a mapping points at it; the revalidation carries the mapping
//      back to the current snapshot when the same brand returns unchanged.
// The switch References:BrandDiffWrites (default on) goes back to the full-copy path (SyncBrandReferences) without a
// release; both paths read and leave the same table shape, so switching either way is safe at any time.
internal static class ReferenceWriteSwitches
{
    public const string BrandDiffWritesKey = "References:BrandDiffWrites";

    public static bool BrandDiffWrites(Microsoft.Extensions.Configuration.IConfiguration configuration) =>
        Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue(configuration, BrandDiffWritesKey, true);
}

internal sealed partial class ReferenceSyncService
{
    internal const string BrandListShrankCode = "REFERENCE_LIST_SHRANK";
    internal const string BrandListMovedCode = "REFERENCE_LIST_MOVED";
    internal const string RetiredSnapshotPrefix = "RETIRED-";
    // A read with more than this share fewer brands than the current list is not trusted to delete anything, once it
    // lacks at least BrandListShrinkMinimumRows (a short list may lose a few brands for real).
    internal const double BrandListShrinkLimit = 0.20;
    internal const int BrandListShrinkMinimumRows = 50;
    private const int BrandDiffChunk = 5_000;

    internal static string BrandListShrankIssueKey(Guid connectionId) => $"reference-list-shrank:{connectionId:N}:BRANDS";

    internal sealed record LockedSnapshotRow(bool IsCurrent, string ContentHash, DateTimeOffset FetchedAt);

    internal sealed record CurrentBrandRows(Guid SnapshotId, string ContentHash, DateTimeOffset FetchedAt, Dictionary<string, int> Index, Guid[] Ids, string[] ExternalIds, UInt128[] Digests)
    {
        public int Count => Ids.Length;
    }

    internal readonly record struct BrandRow(string ExternalId, string Name, string NormalizedName, string Path, int Depth, bool IsLeaf, bool IsActive, bool? IsRequired, bool? AllowsCustomValue, bool? AllowsMultipleValues, string PayloadHash, int SortOrder);

    internal static BrandRow ToBrandRow(RemoteReferenceItem item, int position) =>
        new(item.ExternalId, item.Name, item.Name.Trim().ToUpperInvariant(), item.Path, item.Depth, item.IsLeaf, item.IsActive, item.IsRequired, item.AllowsCustomValue, item.AllowsMultipleValues, Hash(item.RawJson), position);

    internal static UInt128 BrandRowDigest(BrandRow row) =>
        BrandRowDigest(row.ExternalId, null, row.Name, row.NormalizedName, row.Path, row.Depth, row.IsLeaf, row.IsActive, row.IsRequired, row.AllowsCustomValue, row.AllowsMultipleValues, row.PayloadHash);

    // The difference between a complete read and the current list, by ExternalId.
    internal sealed record BrandDiff(List<BrandRow> Added, List<(Guid Id, BrandRow Row)> Changed, List<(Guid Id, string ExternalId)> Vanished, int ReadCount)
    {
        public bool IsEmpty => Added.Count == 0 && Changed.Count == 0 && Vanished.Count == 0;
    }

    // Whether a complete read of readCount brands may replace a list of currentCount: not when it is empty or more than
    // BrandListShrinkLimit shorter (a marketplace answering a part of its list must never remove the rest).
    internal static bool IsTrustedBrandListSize(int readCount, int currentCount) =>
        readCount > 0 && (readCount >= currentCount * (1 - BrandListShrinkLimit) || currentCount - readCount < BrandListShrinkMinimumRows);

    // The current brand snapshot's rows, or null when the connection has none (or an empty one): the first list is then
    // stored whole by the full-copy path.
    private async Task<CurrentBrandRows?> LoadCurrentBrandRowsAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var snapshot = await db.ReferenceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "BRANDS" && x.ScopeExternalId == "" && x.IsCurrent)
            .OrderByDescending(x => x.FetchedAt)
            .Select(x => new { x.Id, x.ContentHash, x.FetchedAt, x.ItemCount })
            .FirstOrDefaultAsync(cancellationToken);
        if (snapshot is null) return null;
        var capacity = Math.Max(snapshot.ItemCount, 0);
        var ids = new List<Guid>(capacity);
        var externalIds = new List<string>(capacity);
        var digests = new List<UInt128>(capacity);
        var index = new Dictionary<string, int>(capacity, StringComparer.Ordinal);
        await foreach (var row in db.ReferenceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SnapshotId == snapshot.Id && x.ResourceType == "BRANDS")
            .Select(x => new { x.Id, x.ExternalId, x.ParentExternalId, x.Name, x.NormalizedName, x.Path, x.Depth, x.IsLeaf, x.IsActive, x.IsRequired, x.AllowsCustomValue, x.AllowsMultipleValues, x.PayloadHash })
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken))
        {
            // The unique (TenantId, SnapshotId, ResourceType, ExternalId) index keeps one row per id.
            if (!index.TryAdd(row.ExternalId, ids.Count)) continue;
            ids.Add(row.Id);
            externalIds.Add(row.ExternalId);
            digests.Add(BrandRowDigest(row.ExternalId, row.ParentExternalId, row.Name, row.NormalizedName, row.Path, row.Depth, row.IsLeaf, row.IsActive, row.IsRequired, row.AllowsCustomValue, row.AllowsMultipleValues, row.PayloadHash));
        }
        return ids.Count == 0 ? null : new(snapshot.Id, snapshot.ContentHash, snapshot.FetchedAt, index, [.. ids], [.. externalIds], [.. digests]);
    }

    // The read compared with the current rows as it streams in; only the differences are kept in memory.
    internal static async Task<BrandDiff> DiffBrandListAsync(CurrentBrandRows current, IAsyncEnumerable<List<RemoteReferenceItem>> pages, CancellationToken cancellationToken)
    {
        var seen = new bool[current.Count];
        var added = new List<BrandRow>();
        var changed = new List<(Guid Id, BrandRow Row)>();
        var position = 0;
        await foreach (var page in pages.WithCancellation(cancellationToken))
            foreach (var item in page)
            {
                var row = ToBrandRow(item, position++);
                if (current.Index.TryGetValue(row.ExternalId, out var at))
                {
                    seen[at] = true;
                    if (current.Digests[at] != BrandRowDigest(row)) changed.Add((current.Ids[at], row));
                }
                else added.Add(row);
            }
        var vanished = new List<(Guid Id, string ExternalId)>();
        for (var at = 0; at < seen.Length; at++)
            if (!seen[at]) vanished.Add((current.Ids[at], current.ExternalIds[at]));
        return new(added, changed, vanished, position);
    }

    // True when the list was written as a difference; false when the connection has no brand list yet (the caller stores
    // the first one whole). Every other outcome throws the job's result and leaves the stored list as it was.
    private async Task<bool> SyncBrandReferencesByDiffAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var current = await LoadCurrentBrandRowsAsync(tenantId, connectionId, cancellationToken);
        if (current is null) return false;
        var diff = await DiffBrandListAsync(current, ReadBrandPagesAsync(tenantId, connectionId, correlationId, cancellationToken), cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (diff.ReadCount == 0) throw new JobProcessingException(JobExecutionResult.Blocked("REFERENCE_EMPTY_RESPONSE", "Trendyol BRANDS salt-okunur çağrısı boş koleksiyon döndürdü; mevcut snapshot korunuyor."));
        if (!IsTrustedBrandListSize(diff.ReadCount, current.Count))
        {
            var summary = $"Marka listesi {current.Count} kayıttan {diff.ReadCount} kayda düştü (%{Math.Round(100.0 * (current.Count - diff.ReadCount) / current.Count)} eksik); okuma güvenilir sayılmadı, hiçbir marka silinmedi, paneldeki liste korunuyor.";
            if (issues is not null)
            {
                await issues.RecordAsync(tenantId, BrandListShrankIssueKey(connectionId), BrandListShrankCode, summary, now, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            throw new JobProcessingException(JobExecutionResult.Blocked(BrandListShrankCode, summary));
        }
        var sourceVersion = await db.PlatformCapabilities.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == MarketplaceCapabilities.ReferenceRead).Select(x => x.SourceVersion).SingleOrDefaultAsync(cancellationToken)
            ?? await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.ApiVersion).SingleAsync(cancellationToken);

        // Reference refresh can be invoked while the caller already owns a transaction: participate in it then.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        // The row as the database holds it (a tracked entity of this context may carry finer clock ticks than stored).
        var locked = await db.Database.SqlQuery<LockedSnapshotRow>($"""
            SELECT "IsCurrent", "ContentHash", "FetchedAt" FROM integration.reference_snapshots
            WHERE "TenantId" = {tenantId} AND "Id" = {current.SnapshotId} FOR UPDATE
            """).ToListAsync(cancellationToken);
        var row = locked.SingleOrDefault();
        // Another run replaced or changed the list since it was loaded: this read is compared again on the next attempt.
        if (row is null || !row.IsCurrent || row.ContentHash != current.ContentHash || row.FetchedAt != current.FetchedAt)
            throw new JobProcessingException(JobExecutionResult.Retry(BrandListMovedCode, "Marka listesi bu okuma sürerken başka bir çalışmada değişti; liste bir sonraki denemede yeniden karşılaştırılacak.", TimeSpan.FromMinutes(2)));
        var snapshot = await db.ReferenceSnapshots.SingleAsync(x => x.TenantId == tenantId && x.Id == current.SnapshotId, cancellationToken);

        if (!diff.IsEmpty)
        {
            await RetireChangedMappedBrandsAsync(tenantId, connectionId, snapshot, diff, cancellationToken);
            await DeleteBrandRowsAsync(tenantId, snapshot.Id, diff.Vanished.Select(x => x.Id).ToArray(), cancellationToken);
            await UpdateBrandRowsAsync(tenantId, snapshot.Id, diff.Changed, cancellationToken);
            await InsertBrandRowsAsync(tenantId, connectionId, snapshot.Id, diff.Added, cancellationToken);
            var stored = await db.ReferenceItems.CountAsync(x => x.TenantId == tenantId && x.SnapshotId == snapshot.Id && x.ResourceType == "BRANDS", cancellationToken);
            if (stored != diff.ReadCount)
                throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_DIFF_MISMATCH", $"Marka listesi farkı uygulandıktan sonra {stored} kayıt kaldı, okunan {diff.ReadCount}; değişiklik geri alındı, paneldeki liste korunuyor."));
            var contentHash = await HashReferenceItemsAsync(tenantId, snapshot.Id, cancellationToken);
            // The content hash is unique per connection list: an older snapshot of the same content gives its hash up.
            var clashes = await db.ReferenceSnapshots
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "BRANDS" && x.ScopeExternalId == "" && x.Id != snapshot.Id && x.ContentHash == contentHash)
                .ToListAsync(cancellationToken);
            foreach (var clash in clashes) clash.ContentHash = $"SUPERSEDED-{Guid.CreateVersion7():N}";
            if (clashes.Count > 0) await db.SaveChangesAsync(cancellationToken);
            snapshot.ContentHash = contentHash;
            snapshot.ItemCount = diff.ReadCount;
        }
        snapshot.SourceVersion = sourceVersion;
        snapshot.FetchedAt = now;
        if (issues is not null) await issues.ResolveAsync(tenantId, BrandListShrankIssueKey(connectionId), now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await RevalidateMappingsForCurrentSnapshotAsync(tenantId, connectionId, "BRANDS", "", snapshot, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        run.InsertedCount += diff.Added.Count;
        run.UpdatedCount += diff.Changed.Count;
        run.ChangedCount += diff.Added.Count + diff.Changed.Count + diff.Vanished.Count;
        return true;
    }

    // The mappings on the current snapshot whose brand vanishes, or changes so that the full-copy path would not have
    // carried them to its new snapshot, keep their brand's previous row in a not-current snapshot of their own.
    private async Task RetireChangedMappedBrandsAsync(Guid tenantId, Guid connectionId, ReferenceSnapshot snapshot, BrandDiff diff, CancellationToken cancellationToken)
    {
        var mappings = await db.BrandMappings
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == "" && x.Status == "VERIFIED" && x.SnapshotId == snapshot.Id)
            .ToListAsync(cancellationToken);
        if (mappings.Count == 0) return;
        var vanished = diff.Vanished.Select(x => x.ExternalId).ToHashSet(StringComparer.Ordinal);
        var changed = diff.Changed.ToDictionary(x => x.Row.ExternalId, x => x.Row, StringComparer.Ordinal);
        var touched = mappings.Where(x => vanished.Contains(x.ExternalId) || changed.ContainsKey(x.ExternalId)).ToList();
        if (touched.Count == 0) return;
        var touchedIds = touched.Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).ToArray();
        var previous = await db.ReferenceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SnapshotId == snapshot.Id && x.ResourceType == "BRANDS" && touchedIds.Contains(x.ExternalId))
            .ToDictionaryAsync(x => x.ExternalId, StringComparer.Ordinal, cancellationToken);
        var retired = touched.Where(mapping => previous.TryGetValue(mapping.ExternalId, out var before)
                && (vanished.Contains(mapping.ExternalId)
                    || !ReferenceMappingRevalidationPolicy.CanRevalidate("BRANDS", before, ToReferenceItem(changed[mapping.ExternalId], tenantId, connectionId, snapshot.Id))))
            .ToList();
        if (retired.Count == 0) return;
        var retiredIds = retired.Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).ToArray();
        var archive = new ReferenceSnapshot
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            ResourceType = "BRANDS",
            ScopeExternalId = "",
            SourceVersion = snapshot.SourceVersion,
            ContentHash = $"{RetiredSnapshotPrefix}{Guid.CreateVersion7():N}",
            // The list these rows came from was fetched then; the retention pass keeps the snapshot while a mapping points at it.
            FetchedAt = snapshot.FetchedAt,
            IsCurrent = false,
            ItemCount = retiredIds.Length
        };
        db.ReferenceSnapshots.Add(archive);
        await db.SaveChangesAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO integration.reference_items ("Id", "TenantId", "ConnectionId", "SnapshotId", "ResourceType", "ExternalId", "ParentExternalId", "Name", "NormalizedName", "Path", "Depth", "IsLeaf", "IsActive", "IsRequired", "AllowsCustomValue", "AllowsMultipleValues", "PayloadHash", "SortOrder")
            SELECT gen_random_uuid(), "TenantId", "ConnectionId", {archive.Id}, "ResourceType", "ExternalId", "ParentExternalId", "Name", "NormalizedName", "Path", "Depth", "IsLeaf", "IsActive", "IsRequired", "AllowsCustomValue", "AllowsMultipleValues", "PayloadHash", "SortOrder"
            FROM integration.reference_items
            WHERE "TenantId" = {tenantId} AND "SnapshotId" = {snapshot.Id} AND "ResourceType" = 'BRANDS' AND "ExternalId" = ANY({retiredIds})
            """, cancellationToken);
        foreach (var mapping in retired)
        {
            mapping.SnapshotId = archive.Id;
            mapping.Version++;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ReferenceItem ToReferenceItem(BrandRow row, Guid tenantId, Guid connectionId, Guid snapshotId) => new()
    {
        Id = Guid.Empty,
        TenantId = tenantId,
        ConnectionId = connectionId,
        SnapshotId = snapshotId,
        ResourceType = "BRANDS",
        ExternalId = row.ExternalId,
        ParentExternalId = null,
        Name = row.Name,
        NormalizedName = row.NormalizedName,
        Path = row.Path,
        Depth = row.Depth,
        IsLeaf = row.IsLeaf,
        IsActive = row.IsActive,
        IsRequired = row.IsRequired,
        AllowsCustomValue = row.AllowsCustomValue,
        AllowsMultipleValues = row.AllowsMultipleValues,
        PayloadHash = row.PayloadHash,
        SortOrder = row.SortOrder
    };

    private async Task DeleteBrandRowsAsync(Guid tenantId, Guid snapshotId, Guid[] ids, CancellationToken cancellationToken)
    {
        foreach (var chunk in ids.Chunk(BrandDiffChunk))
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM integration.reference_items
                WHERE "TenantId" = {tenantId} AND "SnapshotId" = {snapshotId} AND "ResourceType" = 'BRANDS' AND "Id" = ANY({chunk})
                """, cancellationToken);
    }

    private async Task UpdateBrandRowsAsync(Guid tenantId, Guid snapshotId, List<(Guid Id, BrandRow Row)> rows, CancellationToken cancellationToken)
    {
        foreach (var chunk in rows.Chunk(BrandDiffChunk))
        {
            // Only a row with a column that really differs is written; SortOrder follows the read but alone moves nothing.
            const string sql = """
                UPDATE integration.reference_items AS t
                SET "Name" = u.name, "NormalizedName" = u.normalized, "Path" = u.path, "Depth" = u.depth, "IsLeaf" = u.leaf,
                    "IsActive" = u.active, "IsRequired" = u.required, "AllowsCustomValue" = u.custom, "AllowsMultipleValues" = u.multiple,
                    "PayloadHash" = u.payload, "SortOrder" = u.sort
                FROM unnest(@ids, @names, @normalized, @paths, @depths, @leaves, @actives, @required, @custom, @multiple, @payloads, @sorts)
                    AS u(id, name, normalized, path, depth, leaf, active, required, custom, multiple, payload, sort)
                WHERE t."TenantId" = @tenant AND t."SnapshotId" = @snapshot AND t."ResourceType" = 'BRANDS' AND t."Id" = u.id
                  AND (t."Name", t."NormalizedName", t."Path", t."Depth", t."IsLeaf", t."IsActive", t."IsRequired", t."AllowsCustomValue", t."AllowsMultipleValues", t."PayloadHash")
                      IS DISTINCT FROM (u.name, u.normalized, u.path, u.depth, u.leaf, u.active, u.required, u.custom, u.multiple, u.payload)
                """;
            await db.Database.ExecuteSqlRawAsync(sql, [
                new NpgsqlParameter("tenant", tenantId),
                new NpgsqlParameter("snapshot", snapshotId),
                ArrayParameter("ids", NpgsqlDbType.Uuid, chunk.Select(x => x.Id).ToArray()),
                .. RowArrays(chunk.Select(x => x.Row).ToArray())
            ], cancellationToken);
        }
    }

    private async Task InsertBrandRowsAsync(Guid tenantId, Guid connectionId, Guid snapshotId, List<BrandRow> rows, CancellationToken cancellationToken)
    {
        foreach (var chunk in rows.Chunk(BrandDiffChunk))
        {
            const string sql = """
                INSERT INTO integration.reference_items ("Id", "TenantId", "ConnectionId", "SnapshotId", "ResourceType", "ExternalId", "ParentExternalId", "Name", "NormalizedName", "Path", "Depth", "IsLeaf", "IsActive", "IsRequired", "AllowsCustomValue", "AllowsMultipleValues", "PayloadHash", "SortOrder")
                SELECT u.id, @tenant, @connection, @snapshot, 'BRANDS', u.external, NULL, u.name, u.normalized, u.path, u.depth, u.leaf, u.active, u.required, u.custom, u.multiple, u.payload, u.sort
                FROM unnest(@ids, @externals, @names, @normalized, @paths, @depths, @leaves, @actives, @required, @custom, @multiple, @payloads, @sorts)
                    AS u(id, external, name, normalized, path, depth, leaf, active, required, custom, multiple, payload, sort)
                """;
            await db.Database.ExecuteSqlRawAsync(sql, [
                new NpgsqlParameter("tenant", tenantId),
                new NpgsqlParameter("connection", connectionId),
                new NpgsqlParameter("snapshot", snapshotId),
                ArrayParameter("ids", NpgsqlDbType.Uuid, chunk.Select(_ => Guid.CreateVersion7()).ToArray()),
                ArrayParameter("externals", NpgsqlDbType.Text, chunk.Select(x => x.ExternalId).ToArray()),
                .. RowArrays(chunk)
            ], cancellationToken);
        }
    }

    private static NpgsqlParameter ArrayParameter<T>(string name, NpgsqlDbType type, T[] values) =>
        new(name, NpgsqlDbType.Array | type) { Value = values };

    private static IEnumerable<NpgsqlParameter> RowArrays(BrandRow[] rows) =>
    [
        ArrayParameter("names", NpgsqlDbType.Text, rows.Select(x => x.Name).ToArray()),
        ArrayParameter("normalized", NpgsqlDbType.Text, rows.Select(x => x.NormalizedName).ToArray()),
        ArrayParameter("paths", NpgsqlDbType.Text, rows.Select(x => x.Path).ToArray()),
        ArrayParameter("depths", NpgsqlDbType.Integer, rows.Select(x => x.Depth).ToArray()),
        ArrayParameter("leaves", NpgsqlDbType.Boolean, rows.Select(x => x.IsLeaf).ToArray()),
        ArrayParameter("actives", NpgsqlDbType.Boolean, rows.Select(x => x.IsActive).ToArray()),
        ArrayParameter("required", NpgsqlDbType.Boolean, rows.Select(x => x.IsRequired).ToArray()),
        ArrayParameter("custom", NpgsqlDbType.Boolean, rows.Select(x => x.AllowsCustomValue).ToArray()),
        ArrayParameter("multiple", NpgsqlDbType.Boolean, rows.Select(x => x.AllowsMultipleValues).ToArray()),
        ArrayParameter("payloads", NpgsqlDbType.Text, rows.Select(x => x.PayloadHash).ToArray()),
        ArrayParameter("sorts", NpgsqlDbType.Integer, rows.Select(x => x.SortOrder).ToArray()),
    ];
}
