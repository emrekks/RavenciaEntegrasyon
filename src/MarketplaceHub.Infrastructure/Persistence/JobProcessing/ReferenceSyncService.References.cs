using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class ReferenceSyncService
{
    // ISLEYICI-11 (16 Nov 2026): reference pages in one read, guards against a list that never ends with empty pages
    // (the item limits never fill then). 1,000 items a page reach MaxReferenceItems in 500 pages, 2,000 brands a page
    // MaxBrandReferenceItems in 1,000.
    internal const int ReferencePageCap = 2_000;
    internal const int BrandReferencePageCap = 5_000;
    private static readonly PageGuard BrandReferencePageGuard = new(MaxPages: BrandReferencePageCap, StopOnRepeatedCursor: true);

    internal async Task<bool> SyncReferences(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var (resourceType, parentExternalId) = ReferenceResource(payloadJson);
        if (!await IsValidReferenceScope(tenantId, connectionId, resourceType, parentExternalId, cancellationToken)) return false;
        if (resourceType == "BRANDS") return await SyncBrandReferences(tenantId, connectionId, correlationId, cancellationToken);
        var items = new List<RemoteReferenceItem>();
        var visitedCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            TrackRequest();
            var pageSize = resourceType == "BRANDS" ? 2000 : 1000;
            var result = await references.ReadAsync(Context(tenantId, connectionId, correlationId, $"reference-sync:{resourceType}:{parentExternalId}:{cursor}"), new(resourceType, parentExternalId), new(cursor, pageSize), cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
            foreach (var _ in result.Value!.Items) TrackReceived();
            items.AddRange(result.Value!.Items);
            if (items.Count > MaxReferenceItems) throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_RESULT_LIMIT_EXCEEDED", "Referans yanıtı güvenli işleme sınırını aştı."));
            cursor = result.Value.NextCursor;
            if (!result.Value.HasMore) break;
            if (string.IsNullOrWhiteSpace(cursor) || !visitedCursors.Add(cursor)) throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_CURSOR_INVALID", "Referans sayfalama imleci eksik veya yinelendi."));
            // ISLEYICI-11: empty pages under ever new cursors fill neither the item limit nor the visited set's repeat check.
            if (visitedCursors.Count >= ReferencePageCap) throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_PAGE_CAP", $"Referans listesi {ReferencePageCap} sayfayı aştı; okuma durduruldu, paneldeki mevcut liste korunuyor."));
        } while (!cancellationToken.IsCancellationRequested);

        cancellationToken.ThrowIfCancellationRequested();
        // Shopify's categories are the store's product types, read from its products: a store with none answers an empty
        // list, and a type a panel category is mapped to that no product carries yet (typed on Eşleştirmeler, created by
        // the first publication) stays in the list so its mapping holds.
        var shopifyProductTypes = resourceType == "CATEGORIES"
            && await db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "SHOPIFY", cancellationToken);
        if (shopifyProductTypes)
        {
            var listed = items.Select(x => x.ExternalId).ToHashSet(StringComparer.Ordinal);
            var mappedTypes = await db.CategoryMappings.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == "" && x.Status == "VERIFIED")
                .Select(x => x.ExternalId).Distinct().ToListAsync(cancellationToken);
            foreach (var mappedType in mappedTypes.Where(x => !listed.Contains(x) && ShopifyProductTypeNames.Clean(x) is not null))
                items.Add(new RemoteReferenceItem("CATEGORIES", mappedType, null, mappedType.Trim(), mappedType.Trim(), 0, true, true, JsonSerializer.Serialize(mappedType)));
        }
        if (items.Count == 0 && resourceType is "CATEGORIES" or "BRANDS" && !shopifyProductTypes) throw new JobProcessingException(JobExecutionResult.Blocked("REFERENCE_EMPTY_RESPONSE", $"Marketplace {resourceType} salt-okunur çağrısı boş koleksiyon döndürdü; mevcut snapshot korunuyor."));
        if (items.Any(x => !string.Equals(x.ResourceType, resourceType, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(x.ExternalId) || string.IsNullOrWhiteSpace(x.Name))) throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_CONTRACT_INVALID", "Referans yanıtı zorunlu kimlik, ad veya kapsam sözleşmesini sağlamıyor."));
        var ordered = items.OrderBy(x => x.ExternalId, StringComparer.Ordinal).ToList();
        if (resourceType == "ATTRIBUTE_VALUES")
        {
            if (!TryDeduplicateRepeatedEnumValues(ordered, out var uniqueValues))
                throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_IDENTIFIERS_DUPLICATE", "Hepsiburada değer yanıtı aynı uzak kimliği farklı seçenek bilgileriyle döndürdü; snapshot korunuyor."));
            ordered = uniqueValues.ToList();
        }
        if (ordered.Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).Count() != ordered.Count) throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_IDENTIFIERS_DUPLICATE", "Referans yanıtı yinelenen uzak kimlik içeriyor."));
        var identifiers = ordered.Select(x => x.ExternalId).ToHashSet(StringComparer.Ordinal);
        var scopeIsValid = resourceType == "CATEGORIES"
            ? ordered.All(x => x.ParentExternalId is null || (!string.Equals(x.ExternalId, x.ParentExternalId, StringComparison.Ordinal) && identifiers.Contains(x.ParentExternalId)))
            : ordered.All(x => string.Equals(x.ParentExternalId ?? "", parentExternalId ?? "", StringComparison.Ordinal));
        if (!scopeIsValid) throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_CONTRACT_INVALID", "Referans yanıtı geçerli kapsam veya kategori hiyerarşisi sağlamıyor."));
        var canonical = JsonSerializer.Serialize(ordered.Select(x => new { x.ExternalId, x.ParentExternalId, x.Name, x.Path, x.Depth, x.IsLeaf, x.IsActive, x.IsRequired, x.AllowsCustomValue, x.AllowsMultipleValues }));
        var contentHash = Hash(canonical);
        var now = timeProvider.GetUtcNow();
        var sourceVersion = await db.PlatformCapabilities.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == MarketplaceCapabilities.ReferenceRead).Select(x => x.SourceVersion).SingleOrDefaultAsync(cancellationToken)
            ?? await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.ApiVersion).SingleAsync(cancellationToken);

        // Reference refresh can be invoked while the caller already owns a transaction.
        // In that case, participate in it instead of trying to begin a nested transaction.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var scope = parentExternalId ?? "";
        var snapshots = await db.ReferenceSnapshots.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.ScopeExternalId == scope).ToListAsync(cancellationToken);
        var snapshot = snapshots.SingleOrDefault(x => x.ContentHash == contentHash);
        foreach (var current in snapshots.Where(x => x.IsCurrent && x.Id != snapshot?.Id)) current.IsCurrent = false;
        if (snapshot is null)
        {
            snapshot = new ReferenceSnapshot { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ResourceType = resourceType, ScopeExternalId = scope, SourceVersion = sourceVersion, ContentHash = contentHash, FetchedAt = now, IsCurrent = true, ItemCount = ordered.Count };
            db.ReferenceSnapshots.Add(snapshot);
            for (var index = 0; index < ordered.Count; index++)
            {
                var item = ordered[index];
                db.ReferenceItems.Add(new ReferenceItem { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = snapshot.Id, ResourceType = resourceType, ExternalId = item.ExternalId, ParentExternalId = item.ParentExternalId, Name = item.Name, NormalizedName = item.Name.Trim().ToUpperInvariant(), Path = item.Path, Depth = item.Depth, IsLeaf = item.IsLeaf, IsActive = item.IsActive, IsRequired = item.IsRequired, AllowsCustomValue = item.AllowsCustomValue, AllowsMultipleValues = item.AllowsMultipleValues, PayloadHash = Hash(item.RawJson), SortOrder = index });
            }
        }
        else
        {
            snapshot.IsCurrent = true;
            snapshot.FetchedAt = now;
            snapshot.SourceVersion = sourceVersion;
        }
        await db.SaveChangesAsync(cancellationToken);
        await RevalidateMappingsForCurrentSnapshotAsync(tenantId, connectionId, resourceType, scope, snapshot, now, cancellationToken);
        // A category's attribute or value list just arrived: fill it from the connection-wide templates.
        if (resourceType == "CATEGORY_ATTRIBUTES")
            await MappingTemplateFiller.FillCategoryAsync(db, tenantId, connectionId, scope, now, cancellationToken);
        else if (resourceType == "ATTRIBUTE_VALUES")
            await MappingTemplateFiller.FillValuesAsync(db, tenantId, connectionId, scope, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task RevalidateMappingsForCurrentSnapshotAsync(
        Guid tenantId,
        Guid connectionId,
        string resourceType,
        string scope,
        ReferenceSnapshot currentSnapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        switch (resourceType)
        {
            case "CATEGORIES":
                await RevalidateSnapshotMappingsAsync(
                    db.CategoryMappings,
                    tenantId, connectionId, resourceType, scope, currentSnapshot, now,
                    (localIds, token) => db.Categories.AsNoTracking()
                        .Where(x => x.TenantId == tenantId && localIds.Contains(x.Id) && x.IsActive && x.IsLeaf)
                        .Select(x => x.Id).ToHashSetAsync(token),
                    cancellationToken);
                break;
            case "BRANDS":
                await RevalidateSnapshotMappingsAsync(
                    db.BrandMappings,
                    tenantId, connectionId, resourceType, scope, currentSnapshot, now,
                    (localIds, token) => db.Brands.AsNoTracking()
                        .Where(x => x.TenantId == tenantId && localIds.Contains(x.Id) && x.IsActive)
                        .Select(x => x.Id).ToHashSetAsync(token),
                    cancellationToken);
                break;
            case "CATEGORY_ATTRIBUTES":
                await RevalidateSnapshotMappingsAsync(
                    db.AttributeMappings,
                    tenantId, connectionId, resourceType, scope, currentSnapshot, now,
                    (localIds, token) => db.AttributeDefinitions.AsNoTracking()
                        .Where(x => x.TenantId == tenantId && localIds.Contains(x.Id) && x.IsActive)
                        .Select(x => x.Id).ToHashSetAsync(token),
                    cancellationToken);
                break;
            case "ATTRIBUTE_VALUES":
                await RevalidateSnapshotMappingsAsync(
                    db.AttributeValueMappings,
                    tenantId, connectionId, resourceType, scope, currentSnapshot, now,
                    (localIds, token) => db.AttributeValues.AsNoTracking()
                        .Where(x => x.TenantId == tenantId && localIds.Contains(x.Id) && x.IsActive)
                        .Select(x => x.Id).ToHashSetAsync(token),
                    cancellationToken);
                break;
        }
    }

    private async Task RevalidateSnapshotMappingsAsync<TMapping>(
        IQueryable<TMapping> mappingQuery,
        Guid tenantId,
        Guid connectionId,
        string resourceType,
        string scope,
        ReferenceSnapshot currentSnapshot,
        DateTimeOffset now,
        Func<Guid[], CancellationToken, Task<HashSet<Guid>>> loadActiveLocalIds,
        CancellationToken cancellationToken)
        where TMapping : CatalogMapping
    {
        var mappings = await mappingQuery
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == scope
                && x.Status == "VERIFIED" && x.SnapshotId != currentSnapshot.Id)
            .ToListAsync(cancellationToken);
        if (mappings.Count == 0) return;

        var oldSnapshotIds = mappings.Select(x => x.SnapshotId).Distinct().ToArray();
        var externalIds = mappings.Select(x => x.ExternalId).Distinct().ToArray();
        var validOldSnapshotIds = await db.ReferenceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType
                && x.ScopeExternalId == scope && oldSnapshotIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToHashSetAsync(cancellationToken);
        var activeLocalIds = await loadActiveLocalIds(mappings.Select(x => x.LocalId).Distinct().ToArray(), cancellationToken);
        var previousItems = await db.ReferenceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ResourceType == resourceType
                && validOldSnapshotIds.Contains(x.SnapshotId) && externalIds.Contains(x.ExternalId))
            .ToListAsync(cancellationToken);
        var previousByMapping = previousItems.ToDictionary(x => (x.SnapshotId, x.ExternalId));
        var currentItems = await db.ReferenceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SnapshotId == currentSnapshot.Id
                && x.ResourceType == resourceType && externalIds.Contains(x.ExternalId))
            .ToDictionaryAsync(x => x.ExternalId, cancellationToken);

        foreach (var mapping in mappings)
        {
            if (!activeLocalIds.Contains(mapping.LocalId)
                || !validOldSnapshotIds.Contains(mapping.SnapshotId)
                || !previousByMapping.TryGetValue((mapping.SnapshotId, mapping.ExternalId), out var previous)
                || !currentItems.TryGetValue(mapping.ExternalId, out var current)
                || !ReferenceMappingRevalidationPolicy.CanRevalidate(resourceType, previous, current))
                continue;

            mapping.SnapshotId = currentSnapshot.Id;
            mapping.VerifiedAt = now;
            mapping.Version++;
        }
    }

    internal static bool TryDeduplicateRepeatedEnumValues(IReadOnlyCollection<RemoteReferenceItem> items, out IReadOnlyList<RemoteReferenceItem> uniqueItems)
    {
        var byId = new Dictionary<string, RemoteReferenceItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!byId.TryGetValue(item.ExternalId, out var existing))
            {
                byId.Add(item.ExternalId, item);
                continue;
            }
            if (!string.Equals(existing.ParentExternalId, item.ParentExternalId, StringComparison.Ordinal)
                || !string.Equals(existing.Name, item.Name, StringComparison.Ordinal)
                || !string.Equals(existing.Path, item.Path, StringComparison.Ordinal)
                || existing.Depth != item.Depth || existing.IsLeaf != item.IsLeaf || existing.IsActive != item.IsActive
                || existing.IsRequired != item.IsRequired || existing.AllowsCustomValue != item.AllowsCustomValue
                || existing.AllowsMultipleValues != item.AllowsMultipleValues)
            {
                uniqueItems = [];
                return false;
            }
        }
        uniqueItems = byId.Values.OrderBy(x => x.ExternalId, StringComparer.Ordinal).ToArray();
        return true;
    }

    // The whole brand list, page by page, each page's items once (an id met again on a later page is skipped, the first
    // one kept). Every guard of the read throws, so a list read only in part never reaches its caller's last step: a
    // failed page, a broken item, the item limit, a repeated or missing cursor, the page cap and a cancellation.
    private async IAsyncEnumerable<List<RemoteReferenceItem>> ReadBrandPagesAsync(Guid tenantId, Guid connectionId, string correlationId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        var itemCount = 0;
        var brandWalk = new PageWalk(BrandReferencePageGuard);
        do
        {
            TrackRequest();
            const int pageSize = 2000;
            var result = await references.ReadAsync(Context(tenantId, connectionId, correlationId, $"reference-sync:BRANDS::{cursor}"), new("BRANDS", null), new(cursor, pageSize), cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
            var pageItems = result.Value!.Items;
            if (itemCount > MaxBrandReferenceItems - pageItems.Count) throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_RESULT_LIMIT_EXCEEDED", "Trendyol marka referansı güvenli işleme sınırını aştı."));
            var uniquePageItems = new List<RemoteReferenceItem>(pageItems.Count);
            foreach (var item in pageItems)
            {
                TrackReceived();
                if (!string.Equals(item.ResourceType, "BRANDS", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(item.ExternalId) || string.IsNullOrWhiteSpace(item.Name) || item.ParentExternalId is not null)
                    throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_CONTRACT_INVALID", "Marka referans yanıtı zorunlu kimlik, ad veya kapsam sözleşmesini sağlamıyor."));
                if (seen.Add(item.ExternalId)) uniquePageItems.Add(item);
            }
            itemCount += uniquePageItems.Count;
            yield return uniquePageItems;
            // ISLEYICI-11: a next cursor equal to the one just read (its items are already seen, so the item limit never
            // fills) or a list that never ends stops the read; the current brand list is kept.
            switch (brandWalk.After(cursor, result.Value.HasMore ? result.Value.NextCursor : null))
            {
                case PagedReadStop.RepeatedCursor:
                    throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_CURSOR_INVALID", "Marka referans sayfalama imleci yinelendi."));
                case PagedReadStop.PageCap:
                    throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_PAGE_CAP", $"Marka listesi {BrandReferencePageCap} sayfayı aştı; okuma durduruldu, paneldeki mevcut liste korunuyor."));
            }
            cursor = result.Value.NextCursor;
            if (!result.Value.HasMore) break;
            if (string.IsNullOrWhiteSpace(cursor)) throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_CURSOR_INVALID", "Marka referans sayfalama imleci eksik döndü."));
        } while (!cancellationToken.IsCancellationRequested);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<bool> SyncBrandReferences(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        // Kuyruk Faz 1B (27 Nov 2026): with the switch on (the default) a list read again changes the current snapshot in
        // place, row by row (ReferenceSyncService.BrandDiff); the first list of a connection is still stored whole below.
        if (brandDiffWrites && await SyncBrandReferencesByDiffAsync(tenantId, connectionId, correlationId, cancellationToken)) return true;
        var sourceVersion = await db.PlatformCapabilities.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == MarketplaceCapabilities.ReferenceRead).Select(x => x.SourceVersion).SingleOrDefaultAsync(cancellationToken)
            ?? await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.ApiVersion).SingleAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        // While the read agrees with the current snapshot nothing is written (MarketplaceJobProcessor.BrandReferences).
        var stored = await LoadStoredBrandListAsync(tenantId, connectionId, cancellationToken);
        ReferenceSnapshot? staging = null;
        async Task OpenStagingAsync(int agreeingRows)
        {
            staging = new ReferenceSnapshot
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                ResourceType = "BRANDS",
                SourceVersion = sourceVersion,
                ContentHash = $"PENDING-{Guid.CreateVersion7():N}",
                FetchedAt = now,
                IsCurrent = false,
                ItemCount = 0
            };
            db.ReferenceSnapshots.Add(staging);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            if (stored is not null) await CopyStoredBrandRowsAsync(tenantId, stored.SnapshotId, staging.Id, agreeingRows, cancellationToken);
        }
        if (stored is null) await OpenStagingAsync(0);

        var itemCount = 0;
        try
        {
            await foreach (var uniquePageItems in ReadBrandPagesAsync(tenantId, connectionId, correlationId, cancellationToken))
            {
                if (staging is null && stored is not null)
                    for (var index = 0; index < uniquePageItems.Count; index++)
                        if (!SameBrandRow(stored, itemCount + index, uniquePageItems[index]))
                        {
                            await OpenStagingAsync(itemCount);
                            break;
                        }
                for (var index = 0; staging is not null && index < uniquePageItems.Count; index++)
                {
                    var item = uniquePageItems[index];
                    db.ReferenceItems.Add(new ReferenceItem
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = tenantId,
                        ConnectionId = connectionId,
                        SnapshotId = staging.Id,
                        ResourceType = "BRANDS",
                        ExternalId = item.ExternalId,
                        ParentExternalId = null,
                        Name = item.Name,
                        NormalizedName = item.Name.Trim().ToUpperInvariant(),
                        Path = item.Path,
                        Depth = item.Depth,
                        IsLeaf = item.IsLeaf,
                        IsActive = item.IsActive,
                        IsRequired = item.IsRequired,
                        AllowsCustomValue = item.AllowsCustomValue,
                        AllowsMultipleValues = item.AllowsMultipleValues,
                        PayloadHash = Hash(item.RawJson),
                        SortOrder = itemCount + index
                    });
                }
                itemCount += uniquePageItems.Count;
                if (staging is not null)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    db.ChangeTracker.Clear();
                }
            }

            if (itemCount == 0) throw new JobProcessingException(JobExecutionResult.Blocked("REFERENCE_EMPTY_RESPONSE", "Trendyol BRANDS salt-okunur çağrısı boş koleksiyon döndürdü; mevcut snapshot korunuyor."));
            // Fewer items than the current snapshot holds: the list changed after all.
            if (staging is null && itemCount != stored!.Digests.Length) await OpenStagingAsync(itemCount);
            // Every item agreed with the current snapshot: its hash is the read's hash.
            var contentHash = staging is null ? stored!.ContentHash : await HashReferenceItemsAsync(tenantId, staging.Id, cancellationToken);
            var stagingId = staging?.Id;
            // Reference refresh can be invoked while the caller already owns a transaction.
            // In that case, participate in it instead of trying to begin a nested transaction.
            await using var transaction = db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;
            var snapshots = await db.ReferenceSnapshots.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "BRANDS" && x.ScopeExternalId == "").ToListAsync(cancellationToken);
            var existing = snapshots.SingleOrDefault(x => x.Id != stagingId && x.ContentHash == contentHash);
            var current = existing ?? snapshots.Single(x => x.Id == stagingId);
            foreach (var snapshot in snapshots.Where(x => x.IsCurrent && x.Id != current.Id)) snapshot.IsCurrent = false;
            current.ContentHash = contentHash;
            current.SourceVersion = sourceVersion;
            current.FetchedAt = now;
            current.ItemCount = itemCount;
            current.IsCurrent = true;
            if (existing is not null && stagingId is not null) db.ReferenceSnapshots.Remove(snapshots.Single(x => x.Id == stagingId));
            await db.SaveChangesAsync(cancellationToken);
            await RevalidateMappingsForCurrentSnapshotAsync(tenantId, connectionId, "BRANDS", "", current, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            db.ChangeTracker.Clear();
            if (staging is null) throw;
            try
            {
                var stagingId = staging.Id;
                var orphan = await db.ReferenceSnapshots.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == stagingId && !x.IsCurrent, CancellationToken.None);
                if (orphan is not null)
                {
                    db.ReferenceSnapshots.Remove(orphan);
                    await db.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch { db.ChangeTracker.Clear(); }
            throw;
        }
    }

    private async Task<string> HashReferenceItemsAsync(Guid tenantId, Guid snapshotId, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("["));
        var first = true;
        await foreach (var item in db.ReferenceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SnapshotId == snapshotId && x.ResourceType == "BRANDS")
            .OrderBy(x => x.ExternalId)
            .Select(x => new { x.ExternalId, x.ParentExternalId, x.Name, x.Path, x.Depth, x.IsLeaf, x.IsActive, x.IsRequired, x.AllowsCustomValue, x.AllowsMultipleValues })
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken))
        {
            if (!first) hash.AppendData(Encoding.UTF8.GetBytes(","));
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(item));
            first = false;
        }
        hash.AppendData(Encoding.UTF8.GetBytes("]"));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static (string ResourceType, string? ParentExternalId) ReferenceResource(string payloadJson)
    {
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            var resourceType = payload.RootElement.TryGetProperty("resourceType", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim().ToUpperInvariant() : "CATEGORIES";
            var parent = payload.RootElement.TryGetProperty("parentExternalId", out var parentValue) && parentValue.ValueKind == JsonValueKind.String ? parentValue.GetString()?.Trim() : null;
            return (resourceType, string.IsNullOrWhiteSpace(parent) ? null : parent);
        }
        catch (JsonException) { return ("", null); }
    }

    private async Task<bool> IsValidReferenceScope(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, CancellationToken cancellationToken)
    {
        if (resourceType is "CATEGORIES" or "BRANDS") return parentExternalId is null;
        if (resourceType == "CATEGORY_ATTRIBUTES" && parentExternalId is not null)
            return await db.ReferenceItems.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORIES" && x.ExternalId == parentExternalId && x.IsLeaf && x.IsActive
                && db.ReferenceSnapshots.Any(snapshot => snapshot.TenantId == tenantId && snapshot.Id == x.SnapshotId && snapshot.IsCurrent && snapshot.ScopeExternalId == ""), cancellationToken);
        if (resourceType != "ATTRIBUTE_VALUES" || parentExternalId is null) return false;
        var parts = parentExternalId.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        return await db.ReferenceItems.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.ExternalId == parts[1] && x.ParentExternalId == parts[0]
            && db.ReferenceSnapshots.Any(snapshot => snapshot.TenantId == tenantId && snapshot.Id == x.SnapshotId && snapshot.IsCurrent && snapshot.ScopeExternalId == parts[0]), cancellationToken);
    }
}
