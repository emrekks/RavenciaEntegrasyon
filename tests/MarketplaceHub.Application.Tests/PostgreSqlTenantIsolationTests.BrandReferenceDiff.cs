using System.Diagnostics;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Kuyruk Faz 1B (27 Nov 2026): a brand list read again is written as its difference to the current snapshot, in place
// (ReferenceSyncService.BrandDiff). These tests pin what that path writes, what it refuses to write, what the readers see
// and that the full-copy path (References:BrandDiffWrites=false) still works and leaves the table consistent.
public sealed partial class PostgreSqlTenantIsolationTests
{
    private static RemoteReferenceItem DiffBrand(string id, string name, string? extra = null) =>
        new("BRANDS", id, null, name, name, 0, false, true, $$"""{"id":"{{id}}","name":"{{name}}"{{extra}}}""");

    private static RemoteReferenceItem[] DiffBrandList(string prefix, int count) =>
        [.. Enumerable.Range(0, count).Select(index => DiffBrand($"{prefix}{index:D7}", $"{prefix} Marka {index} {(char)('A' + index % 26)}"))];

    private static IConfiguration BrandWriteConfiguration(bool diff) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [ReferenceWriteSwitches.BrandDiffWritesKey] = diff ? "true" : "false" }).Build();

    private async Task<JobExecutionResult> SyncBrandListAsync(Guid tenantId, Guid connectionId, IReferenceDataPort port, bool diff)
    {
        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, port, null!, null!, null!, null!, null!, null!, null!, null!, BrandWriteConfiguration(diff), fixture.TimeProvider);
        return await processor.ProcessAsync(tenantId, connectionId, MarketplaceJobTypes.ReferenceSync, """{"resourceType":"BRANDS"}""", "brand-diff", CancellationToken.None);
    }

    private async Task SyncBrandListOkAsync(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteReferenceItem> items, bool diff)
    {
        var result = await SyncBrandListAsync(tenantId, connectionId, new PagedBrandPort(items), diff);
        Assert.True(result.Succeeded, $"{result.ErrorCode}: {result.ErrorSummary}");
    }

    // (row id, ctid, xmin) of every brand row of the tenant: equal before and after means not a row was written or moved.
    private async Task<string> BrandRowStampsAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        return await db.Database.SqlQuery<string>($"""SELECT coalesce(string_agg("Id"::text || ':' || ctid::text || ':' || xmin::text, ',' ORDER BY "Id"), '') AS "Value" FROM integration.reference_items WHERE "TenantId" = {tenantId}""").SingleAsync();
    }

    private async Task<Dictionary<Guid, string>> BrandRowStampMapAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        var rows = await db.Database.SqlQuery<string>($"""SELECT "Id"::text || '|' || ctid::text || ':' || xmin::text AS "Value" FROM integration.reference_items WHERE "TenantId" = {tenantId}""").ToListAsync();
        return rows.Select(x => x.Split('|')).ToDictionary(x => Guid.Parse(x[0]), x => x[1]);
    }

    private async Task<(ReferenceSnapshot Current, List<ReferenceItem> Items)> CurrentBrandListAsync(Guid tenantId, Guid connectionId)
    {
        await using var db = fixture.CreateContext();
        var current = await db.ReferenceSnapshots.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "BRANDS" && x.IsCurrent);
        var items = await db.ReferenceItems.AsNoTracking().Where(x => x.TenantId == tenantId && x.SnapshotId == current.Id).OrderBy(x => x.ExternalId).ToListAsync();
        return (current, items);
    }

    // The current list is exactly this read: one row per item with every column as the full-copy path writes it, and
    // the content hash a first read of the same list gives.
    private async Task AssertBrandListIsAsync(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteReferenceItem> expected)
    {
        var (current, items) = await CurrentBrandListAsync(tenantId, connectionId);
        Assert.Equal(expected.Count, current.ItemCount);
        Assert.Equal(expected.Count, items.Count);
        var byId = items.ToDictionary(x => x.ExternalId, StringComparer.Ordinal);
        foreach (var item in expected)
        {
            var row = byId[item.ExternalId];
            Assert.Equal(item.Name, row.Name);
            Assert.Equal(item.Name.Trim().ToUpperInvariant(), row.NormalizedName);
            Assert.Equal(item.Path, row.Path);
            Assert.Equal(item.IsActive, row.IsActive);
            Assert.Equal(TextValues.Sha256Hex(item.RawJson), row.PayloadHash);
        }
        Assert.Equal(await FreshBrandHashAsync(expected), current.ContentHash);
    }

    private async Task<(Tenant Tenant, PlatformConnection Connection)> SeedBrandDiffTenantAsync(string code)
    {
        var tenant = NewTenant(code);
        var connection = NewQuestionConnection(tenant);
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        await db.SaveChangesAsync();
        return (tenant, connection);
    }

    private async Task DeleteBrandDiffTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.BrandMappings.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Brands.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await DeleteBrandStreamTenantAsync(tenantId);
    }

    [PostgreSqlFact]
    public async Task BrandDiff_AnUnchangedListWritesNoRowAndOneAddedRenamedRemovedTouchesExactlyThoseRows()
    {
        var (tenant, connection) = await SeedBrandDiffTenantAsync("brand-diff-basic");
        try
        {
            var list = DiffBrandList("B", 300);
            await SyncBrandListOkAsync(tenant.Id, connection.Id, list, diff: true);
            var first = await CurrentBrandListAsync(tenant.Id, connection.Id);
            await AssertBrandListIsAsync(tenant.Id, connection.Id, list);

            // Same list, another order: nothing written, the snapshot stays.
            var stamps = await BrandRowStampsAsync(tenant.Id);
            await SyncBrandListOkAsync(tenant.Id, connection.Id, [.. list.Reverse()], diff: true);
            Assert.Equal(stamps, await BrandRowStampsAsync(tenant.Id));
            var same = await CurrentBrandListAsync(tenant.Id, connection.Id);
            Assert.Equal(first.Current.Id, same.Current.Id);
            Assert.Equal(first.Current.ContentHash, same.Current.ContentHash);

            // One added, one renamed, one removed: exactly those three rows written, the snapshot keeps its id.
            var before = await BrandRowStampMapAsync(tenant.Id);
            var renamedId = list[10].ExternalId;
            var removedId = list[20].ExternalId;
            RemoteReferenceItem[] next = [.. list.Where(x => x.ExternalId != removedId).Select(x => x.ExternalId == renamedId ? DiffBrand(renamedId, "Yeni İsim") : x), DiffBrand("B9999999", "Eklenen Marka")];
            await SyncBrandListOkAsync(tenant.Id, connection.Id, next, diff: true);
            var after = await BrandRowStampMapAsync(tenant.Id);
            var changed = await CurrentBrandListAsync(tenant.Id, connection.Id);
            Assert.Equal(first.Current.Id, changed.Current.Id);
            var renamedRowId = first.Items.Single(x => x.ExternalId == renamedId).Id;
            var removedRowId = first.Items.Single(x => x.ExternalId == removedId).Id;
            var addedRowId = changed.Items.Single(x => x.ExternalId == "B9999999").Id;
            Assert.False(after.ContainsKey(removedRowId));
            Assert.NotEqual(before[renamedRowId], after[renamedRowId]);
            Assert.DoesNotContain(addedRowId, before.Keys);
            var untouched = before.Keys.Where(x => x != renamedRowId && x != removedRowId).ToList();
            Assert.Equal(298, untouched.Count);
            Assert.All(untouched, id => Assert.Equal(before[id], after[id]));
            Assert.Equal("Yeni İsim".ToUpperInvariant(), changed.Items.Single(x => x.ExternalId == renamedId).NormalizedName);
            await AssertBrandListIsAsync(tenant.Id, connection.Id, next);
            Assert.Equal(1, await CountBrandSnapshotsAsync(tenant.Id));
        }
        finally
        {
            await DeleteBrandDiffTenantAsync(tenant.Id);
        }
    }

    // The snapshot is still tracked by the job's context with finer clock ticks than PostgreSQL stores: the list is applied,
    // not taken for a list another run changed meanwhile.
    [PostgreSqlFact]
    public async Task BrandDiff_ASnapshotTrackedByTheSameContextIsAppliedInPlace()
    {
        var (tenant, connection) = await SeedBrandDiffTenantAsync("brand-diff-tracked");
        try
        {
            await using var db = fixture.CreateContext();
            var snapshot = new ReferenceSnapshot { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = "BRANDS", SourceVersion = "v1", ContentHash = "seeded", FetchedAt = DateTimeOffset.UtcNow.AddTicks(7), IsCurrent = true, ItemCount = 1 };
            db.ReferenceSnapshots.Add(snapshot);
            db.ReferenceItems.Add(new ReferenceItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = snapshot.Id, ResourceType = "BRANDS", ExternalId = "456", Name = "Test marka", NormalizedName = "TEST MARKA", Path = "Test marka", PayloadHash = "h", SortOrder = 0 });
            await db.SaveChangesAsync();
            var processor = new MarketplaceJobProcessor(db, null!, new PagedBrandPort([DiffBrand("456", "Test marka"), DiffBrand("457", "İkinci")]), null!, null!, null!, null!, null!, null!, null!, null!, BrandWriteConfiguration(diff: true), fixture.TimeProvider);
            var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ReferenceSync, """{"resourceType":"BRANDS"}""", "brand-diff-tracked", CancellationToken.None);
            Assert.True(result.Succeeded, result.ErrorSummary);
            var current = await CurrentBrandListAsync(tenant.Id, connection.Id);
            Assert.Equal(snapshot.Id, current.Current.Id);
            Assert.Equal(["456", "457"], current.Items.Select(x => x.ExternalId));
        }
        finally
        {
            await DeleteBrandDiffTenantAsync(tenant.Id);
        }
    }

    private async Task<int> CountBrandSnapshotsAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        return await db.ReferenceSnapshots.CountAsync(x => x.TenantId == tenantId && x.ResourceType == "BRANDS");
    }

    [PostgreSqlFact]
    public async Task BrandDiff_AnIncompleteReadOrASuspiciousShrinkChangesNothingAndTheShrinkRaisesAnIssue()
    {
        var (tenant, connection) = await SeedBrandDiffTenantAsync("brand-diff-guards");
        try
        {
            var list = DiffBrandList("G", 1_000);
            await SyncBrandListOkAsync(tenant.Id, connection.Id, list, diff: true);
            var stamps = await BrandRowStampsAsync(tenant.Id);
            var snapshot = (await CurrentBrandListAsync(tenant.Id, connection.Id)).Current;
            RemoteReferenceItem[] changedList = [.. list.Skip(1), DiffBrand("G9999999", "Yeni")];

            // A read failing on its third page (the first two hold changes): nothing written.
            var failing = await SyncBrandListAsync(tenant.Id, connection.Id, new PagedBrandPort(changedList, pageSize: 200, failAtPage: 2), diff: true);
            Assert.False(failing.Succeeded);
            Assert.Equal(stamps, await BrandRowStampsAsync(tenant.Id));

            // A repeated cursor (a list that never ends): nothing written.
            var looping = await SyncBrandListAsync(tenant.Id, connection.Id, new PagedBrandPort(changedList, pageSize: 200, repeatCursorAtPage: 1), diff: true);
            Assert.Equal("REFERENCE_CURSOR_INVALID", looping.ErrorCode);
            Assert.Equal(stamps, await BrandRowStampsAsync(tenant.Id));

            // An empty list: nothing written, as before.
            var empty = await SyncBrandListAsync(tenant.Id, connection.Id, new PagedBrandPort([]), diff: true);
            Assert.Equal("REFERENCE_EMPTY_RESPONSE", empty.ErrorCode);
            Assert.Equal(stamps, await BrandRowStampsAsync(tenant.Id));

            // 30 % fewer brands: nothing deleted (nor added), an issue is open, the job is blocked with its cause.
            RemoteReferenceItem[] shrunk = [.. list.Take(700), DiffBrand("G8888888", "Yeni")];
            var shrink = await SyncBrandListAsync(tenant.Id, connection.Id, new PagedBrandPort(shrunk), diff: true);
            Assert.Equal(JobCompletionKind.Blocked, shrink.Kind);
            Assert.Equal("REFERENCE_LIST_SHRANK", shrink.ErrorCode);
            Assert.Equal(stamps, await BrandRowStampsAsync(tenant.Id));
            await using (var db = fixture.CreateContext())
            {
                var issue = await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.DedupeKey == $"reference-list-shrank:{connection.Id:N}:BRANDS");
                Assert.Equal("REFERENCE_LIST_SHRANK", issue.Code);
                Assert.Equal(IssueStatus.Open, issue.Status);
                Assert.Contains("1.000 kayıttan 701 kayda", issue.Summary);
                Assert.Contains("hiçbir marka silinmedi", issue.Summary);
                Assert.Contains("Küçülmeyi Onayla", issue.Summary);
            }
            var unchanged = await CurrentBrandListAsync(tenant.Id, connection.Id);
            Assert.Equal(snapshot.Id, unchanged.Current.Id);
            Assert.Equal(snapshot.ContentHash, unchanged.Current.ContentHash);

            // The owner approves it ("Küçülmeyi Onayla"): the next read applies the shrink once and the approval is used up.
            await using (var db = fixture.CreateContext())
                Assert.True((await new ReferenceDataService(db, fixture.TimeProvider).ApproveBrandListShrinkAsync(tenant.Id, null, connection.Id, "brand-diff-approve", CancellationToken.None)).Succeeded);
            await SyncBrandListOkAsync(tenant.Id, connection.Id, shrunk, diff: true);
            await AssertBrandListIsAsync(tenant.Id, connection.Id, shrunk);
            await using (var db = fixture.CreateContext())
                Assert.Equal(IssueStatus.Resolved, (await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.Code == "REFERENCE_LIST_SHRANK")).Status);

            // The next large shrink is refused again: the approval held for one read only.
            RemoteReferenceItem[] halved = [.. shrunk.Take(350)];
            var again = await SyncBrandListAsync(tenant.Id, connection.Id, new PagedBrandPort(halved), diff: true);
            Assert.Equal("REFERENCE_LIST_SHRANK", again.ErrorCode);
            // An approval older than 48 hours no longer counts.
            await using (var db = fixture.CreateContext())
                await db.OperationalIssues.Where(x => x.TenantId == tenant.Id && x.Code == "REFERENCE_LIST_SHRANK")
                    .ExecuteUpdateAsync(x => x.SetProperty(i => i.Status, IssueStatus.Acknowledged).SetProperty(i => i.LastSeenAt, DateTimeOffset.UtcNow.AddHours(-49)));
            var expired = await SyncBrandListAsync(tenant.Id, connection.Id, new PagedBrandPort(halved), diff: true);
            Assert.Equal("REFERENCE_LIST_SHRANK", expired.ErrorCode);
            await AssertBrandListIsAsync(tenant.Id, connection.Id, shrunk);

            // A small loss (within the rule) is trusted: applied, and the shrink issue resolves.
            RemoteReferenceItem[] smaller = [.. shrunk.Take(600)];
            await SyncBrandListOkAsync(tenant.Id, connection.Id, smaller, diff: true);
            await AssertBrandListIsAsync(tenant.Id, connection.Id, smaller);
            await using (var db = fixture.CreateContext())
                Assert.Equal(IssueStatus.Resolved, (await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.Code == "REFERENCE_LIST_SHRANK")).Status);
        }
        finally
        {
            await DeleteBrandDiffTenantAsync(tenant.Id);
        }
    }

    // The shrink rule (ReferenceSyncService.AllowedBrandListDrop): max(50, up to 100k: min(20 %, 2,000), above: 2 %).
    [Theory]
    [InlineData(4, 50)]
    [InlineData(200, 50)]
    [InlineData(1_000, 200)]
    [InlineData(10_000, 2_000)]
    [InlineData(50_000, 2_000)]
    [InlineData(100_000, 2_000)]
    [InlineData(103_048, 2_060)]
    [InlineData(818_545, 16_370)]
    [InlineData(2_000_000, 40_000)]
    public void BrandDiff_TheShrinkRuleAllowsTwentyPercentOnSmallListsAndTwoPercentOnLargeOnes(int currentCount, int allowedDrop)
    {
        Assert.Equal(allowedDrop, ReferenceSyncService.AllowedBrandListDrop(currentCount));
        Assert.True(ReferenceSyncService.IsTrustedBrandListSize(Math.Max(1, currentCount - allowedDrop), currentCount));
        if (currentCount - allowedDrop - 1 > 0) Assert.False(ReferenceSyncService.IsTrustedBrandListSize(currentCount - allowedDrop - 1, currentCount));
        Assert.False(ReferenceSyncService.IsTrustedBrandListSize(0, currentCount));
    }

    [Fact]
    public void BrandDiff_AnApprovalCountsWhileAcknowledgedAndYoungerThanFortyEightHours()
    {
        var now = DateTimeOffset.UtcNow;
        OperationalIssue Issue(IssueStatus status, double hoursAgo) => new() { Id = Guid.NewGuid(), DedupeKey = "k", Code = "REFERENCE_LIST_SHRANK", Summary = "s", Status = status, LastSeenAt = now.AddHours(-hoursAgo) };
        Assert.True(ReferenceSyncService.IsLiveShrinkApproval(Issue(IssueStatus.Acknowledged, 47.9), now));
        Assert.False(ReferenceSyncService.IsLiveShrinkApproval(Issue(IssueStatus.Acknowledged, 48.1), now));
        Assert.False(ReferenceSyncService.IsLiveShrinkApproval(Issue(IssueStatus.Open, 1), now));
        Assert.False(ReferenceSyncService.IsLiveShrinkApproval(Issue(IssueStatus.Resolved, 1), now));
        Assert.False(ReferenceSyncService.IsLiveShrinkApproval(null, now));
    }

    // A brand the owner mapped: what the composers and the mapping pages resolve through its mapping (the mapping on the
    // current snapshot and its brand row there), the same under both paths.
    private async Task<(bool OnCurrent, string? Name)> MappedBrandAsync(Guid tenantId, Guid connectionId, Guid localId)
    {
        await using var db = fixture.CreateContext();
        var onCurrent = await db.BrandMappings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localId && x.ScopeExternalId == "" && x.Status == "VERIFIED")
            .Join(db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "BRANDS" && x.IsCurrent), mapping => mapping.SnapshotId, snapshot => snapshot.Id, (mapping, _) => mapping)
            .AnyAsync();
        var name = await (from mapping in db.BrandMappings.AsNoTracking()
                          join item in db.ReferenceItems.AsNoTracking() on new { mapping.TenantId, mapping.SnapshotId, mapping.ExternalId } equals new { item.TenantId, item.SnapshotId, item.ExternalId }
                          where mapping.TenantId == tenantId && mapping.ConnectionId == connectionId && mapping.LocalId == localId
                          select item.Name).SingleOrDefaultAsync();
        return (onCurrent, name);
    }

    [PostgreSqlTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BrandDiff_AMappedBrandThatVanishesOrIsRenamedLeavesItsMappingOnTheOldRowAndReturnsWhenTheBrandReturns(bool diff)
    {
        var (tenant, connection) = await SeedBrandDiffTenantAsync($"brand-diff-mapped-{diff}");
        var now = DateTimeOffset.UtcNow;
        var vanishingLocal = Guid.CreateVersion7();
        var renamedLocal = Guid.CreateVersion7();
        var payloadLocal = Guid.CreateVersion7();
        try
        {
            var list = DiffBrandList("M", 200);
            await SyncBrandListOkAsync(tenant.Id, connection.Id, list, diff: false);
            var snapshot = (await CurrentBrandListAsync(tenant.Id, connection.Id)).Current;
            await using (var db = fixture.CreateContext())
            {
                foreach (var (local, external) in new[] { (vanishingLocal, list[1].ExternalId), (renamedLocal, list[2].ExternalId), (payloadLocal, list[3].ExternalId) })
                {
                    db.Brands.Add(new Brand { Id = local, TenantId = tenant.Id, Name = $"Panel {external}", NormalizedName = $"PANEL {external}", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
                    db.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = snapshot.Id, LocalId = local, ScopeExternalId = "", ExternalId = external, Status = "VERIFIED", VerifiedAt = now, Version = 1 });
                }
                await db.SaveChangesAsync();
            }

            // The first vanishes, the second is renamed, the third only changes its raw payload.
            RemoteReferenceItem[] next = [list[0], DiffBrand(list[2].ExternalId, "Başka Ad"), DiffBrand(list[3].ExternalId, list[3].Name, ""","extra":1"""), .. list.Skip(4)];
            await SyncBrandListOkAsync(tenant.Id, connection.Id, next, diff);
            Assert.Equal((false, list[1].Name), await MappedBrandAsync(tenant.Id, connection.Id, vanishingLocal));
            Assert.Equal((false, list[2].Name), await MappedBrandAsync(tenant.Id, connection.Id, renamedLocal));
            Assert.Equal((true, list[3].Name), await MappedBrandAsync(tenant.Id, connection.Id, payloadLocal));

            // The retention pass leaves a snapshot a mapping points at, whatever its age.
            await using (var db = fixture.CreateContext())
                await new JobRetentionService(db, new FixedTimeProvider(now.AddDays(3))).PurgeStaleReferenceSnapshotsAsync(50, CancellationToken.None);
            Assert.Equal((false, list[1].Name), await MappedBrandAsync(tenant.Id, connection.Id, vanishingLocal));

            // The vanished brand returns unchanged: its mapping is carried to the current list again.
            RemoteReferenceItem[] back = [.. next.Take(1), list[1], .. next.Skip(1)];
            await SyncBrandListOkAsync(tenant.Id, connection.Id, back, diff);
            Assert.Equal((true, list[1].Name), await MappedBrandAsync(tenant.Id, connection.Id, vanishingLocal));
            Assert.Equal((false, list[2].Name), await MappedBrandAsync(tenant.Id, connection.Id, renamedLocal));
            await AssertBrandListIsAsync(tenant.Id, connection.Id, back);
            if (diff)
            {
                // The diff path keeps one small snapshot per retired brand set, not the whole old list.
                await using var db = fixture.CreateContext();
                var retired = await db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenant.Id && !x.IsCurrent).ToListAsync();
                Assert.All(retired, x => Assert.StartsWith("RETIRED-", x.ContentHash));
                Assert.True(await db.ReferenceItems.CountAsync(x => x.TenantId == tenant.Id && !db.ReferenceSnapshots.Any(s => s.Id == x.SnapshotId && s.IsCurrent)) <= 2);
            }
        }
        finally
        {
            await DeleteBrandDiffTenantAsync(tenant.Id);
        }
    }

    // What every brand reader returns for a connection, without the snapshot id (it differs between the paths).
    private async Task<string> BrandReadersAsync(Guid tenantId, Guid connectionId, IReadOnlyList<Guid> mappedLocals, IReadOnlyList<string> lookupIds)
    {
        await using var db = fixture.CreateContext();
        var service = new ReferenceDataService(db, fixture.TimeProvider);
        var lines = new List<string>();
        var summary = await service.BrandSummaryAsync(tenantId, connectionId, CancellationToken.None);
        lines.Add($"summary {summary.Value!.ItemCount}");
        foreach (var query in new[] { "", "a", "marka 29", "MARKA 28", "x00", "İsim", "Marka 77 ", "zzz-none", "1", "Yeni" })
        {
            var found = await service.SearchBrandsAsync(tenantId, connectionId, query, 100, CancellationToken.None);
            lines.Add($"search '{query}': " + string.Join(";", found.Value!.Items.Select(x => $"{x.ExternalId}={x.Name}/{x.IsActive}")));
        }
        var looked = await service.LookupBrandsAsync(tenantId, connectionId, lookupIds, CancellationToken.None);
        lines.Add("lookup: " + string.Join(";", looked.Value!.Items.Select(x => $"{x.ExternalId}={x.Name}")));
        foreach (var local in mappedLocals)
        {
            var (onCurrent, name) = await MappedBrandAsync(tenantId, connectionId, local);
            lines.Add($"mapping {onCurrent} {name}");
        }
        return string.Join("\n", lines);
    }

    [PostgreSqlFact]
    public async Task BrandDiff_ReadersReturnTheSameUnderBothPathsAcrossTwoTenantsAndTwoConnections()
    {
        var seeds = new List<(Tenant Tenant, PlatformConnection Old, PlatformConnection New)>();
        try
        {
            for (var t = 0; t < 2; t++)
            {
                var tenant = NewTenant($"brand-diff-golden-{t}");
                var oldConnection = NewQuestionConnection(tenant);
                var newConnection = NewQuestionConnection(tenant);
                newConnection.ExternalStoreId = "seller-18";
                await using var db = fixture.CreateContext();
                db.Tenants.Add(tenant);
                db.PlatformConnections.AddRange(oldConnection, newConnection);
                await db.SaveChangesAsync();
                seeds.Add((tenant, oldConnection, newConnection));
            }
            var now = DateTimeOffset.UtcNow;
            for (var t = 0; t < seeds.Count; t++)
            {
                var (tenant, oldConnection, newConnection) = seeds[t];
                var list = DiffBrandList($"T{t}x", 3_000);
                // Day 0: both store the first list whole.
                await SyncBrandListOkAsync(tenant.Id, oldConnection.Id, list, diff: false);
                await SyncBrandListOkAsync(tenant.Id, newConnection.Id, list, diff: true);
                var locals = new List<Guid>();
                await using (var db = fixture.CreateContext())
                {
                    foreach (var connection in new[] { oldConnection, newConnection })
                    {
                        var snapshotId = (await CurrentBrandListAsync(tenant.Id, connection.Id)).Current.Id;
                        foreach (var index in new[] { 5, 6, 7, 8 })
                        {
                            var local = Guid.CreateVersion7();
                            if (connection == oldConnection) locals.Add(local);
                            db.Brands.Add(new Brand { Id = local, TenantId = tenant.Id, Name = $"Panel {connection.Id:N}{index}", NormalizedName = $"PANEL {connection.Id:N}{index}", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
                            db.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = snapshotId, LocalId = local, ScopeExternalId = "", ExternalId = list[index].ExternalId, Status = "VERIFIED", VerifiedAt = now, Version = 1 });
                        }
                    }
                    await db.SaveChangesAsync();
                }
                var newLocals = await MappedLocalsAsync(tenant.Id, newConnection.Id);
                var lookup = new[] { list[5].ExternalId, list[6].ExternalId, list[7].ExternalId, list[100].ExternalId, "T-none" };

                // Days 1-3: renames (one mapped), removals (one mapped), additions, a deactivation, a payload-only change, a reorder.
                RemoteReferenceItem[][] days =
                [
                    [.. list.Where((_, i) => i != 6 && i != 50).Select((x, i) => i == 7 ? DiffBrand(x.ExternalId, "İsim Değişti") : x), DiffBrand($"T{t}xNEW1", "Yeni Marka 1")],
                    [.. list.Where((_, i) => i != 50).Select((x, i) => i == 8 ? x with { IsActive = false } : i == 9 ? DiffBrand(x.ExternalId, x.Name, ""","p":2""") : x).Reverse(), DiffBrand($"T{t}xNEW2", "Yeni Marka 2")],
                    [.. list.Take(2_990), DiffBrand($"T{t}xNEW3", "Marka 1 Yeni")],
                ];
                Assert.Equal(await BrandReadersAsync(tenant.Id, oldConnection.Id, locals, lookup), await BrandReadersAsync(tenant.Id, newConnection.Id, newLocals, lookup));
                foreach (var day in days)
                {
                    await SyncBrandListOkAsync(tenant.Id, oldConnection.Id, day, diff: false);
                    await SyncBrandListOkAsync(tenant.Id, newConnection.Id, day, diff: true);
                    var oldView = await BrandReadersAsync(tenant.Id, oldConnection.Id, locals, lookup);
                    var newView = await BrandReadersAsync(tenant.Id, newConnection.Id, newLocals, lookup);
                    Assert.Equal(oldView, newView);
                    await AssertBrandListIsAsync(tenant.Id, newConnection.Id, day);
                    await AssertBrandListIsAsync(tenant.Id, oldConnection.Id, day);
                }
            }
        }
        finally
        {
            foreach (var seed in seeds) await DeleteBrandDiffTenantAsync(seed.Tenant.Id);
        }
    }

    private async Task<List<Guid>> MappedLocalsAsync(Guid tenantId, Guid connectionId)
    {
        await using var db = fixture.CreateContext();
        return await db.BrandMappings.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId).OrderBy(x => x.ExternalId).Select(x => x.LocalId).ToListAsync();
    }

    [PostgreSqlFact]
    public async Task BrandDiff_SwitchingBetweenTheFullCopyAndTheDiffPathKeepsOneConsistentList()
    {
        var (tenant, connection) = await SeedBrandDiffTenantAsync("brand-diff-switch");
        try
        {
            var list = DiffBrandList("S", 500);
            var steps = new (bool Diff, RemoteReferenceItem[] Items)[]
            {
                (false, list),
                (true, [.. list.Skip(3), DiffBrand("S9000001", "Bir")]),
                (false, [.. list.Skip(5).Reverse(), DiffBrand("S9000001", "Bir"), DiffBrand("S9000002", "İki")]),
                (true, [.. list.Skip(5), DiffBrand("S9000002", "İki Yeni")]),
                (true, [.. list.Skip(5), DiffBrand("S9000002", "İki Yeni")]),
                (false, [.. list.Skip(5), DiffBrand("S9000002", "İki Yeni")]),
                (false, list),
                (true, list),
            };
            foreach (var (diff, items) in steps)
            {
                await SyncBrandListOkAsync(tenant.Id, connection.Id, items, diff);
                await AssertBrandListIsAsync(tenant.Id, connection.Id, items);
                await using var db = fixture.CreateContext();
                Assert.Equal(1, await db.ReferenceSnapshots.CountAsync(x => x.TenantId == tenant.Id && x.IsCurrent));
            }
        }
        finally
        {
            await DeleteBrandDiffTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task BrandDiff_ASearchDuringTheApplySeesTheWholeOldOrTheWholeNewListAndIsNotBlocked()
    {
        var (tenant, connection) = await SeedBrandDiffTenantAsync("brand-diff-concurrency");
        try
        {
            var list = DiffBrandList("C", 60_000);
            await SyncBrandListOkAsync(tenant.Id, connection.Id, list, diff: true);
            // 20,000 renamed, 5,000 removed, 5,000 added: a long enough apply to read during it.
            RemoteReferenceItem[] next = [.. list.Skip(5_000).Select((x, i) => i < 20_000 ? DiffBrand(x.ExternalId, x.Name + " Yeni") : x), .. DiffBrandList("D", 5_000)];
            var snapshotId = (await CurrentBrandListAsync(tenant.Id, connection.Id)).Current.Id;
            var observed = new HashSet<string>();
            var slowest = TimeSpan.Zero;
            using var stop = new CancellationTokenSource();
            var reader = Task.Run(async () =>
            {
                await using var db = fixture.CreateContext();
                while (!stop.IsCancellationRequested)
                {
                    var watch = Stopwatch.StartNew();
                    // One statement: one database snapshot.
                    var state = await db.Database.SqlQuery<string>($"""
                        SELECT count(*) FILTER (WHERE "ExternalId" = 'C0000000')::text || '/' || count(*) FILTER (WHERE "ExternalId" = 'D0000000')::text || '/'
                            || count(*) FILTER (WHERE "Name" LIKE '% Yeni')::text || '/' || count(*)::text AS "Value"
                        FROM integration.reference_items WHERE "TenantId" = {tenant.Id} AND "SnapshotId" = {snapshotId} AND "ResourceType" = 'BRANDS'
                        """).SingleAsync();
                    watch.Stop();
                    lock (observed) observed.Add(state);
                    if (watch.Elapsed > slowest) slowest = watch.Elapsed;
                }
            });
            var applyWatch = Stopwatch.StartNew();
            await SyncBrandListOkAsync(tenant.Id, connection.Id, next, diff: true);
            applyWatch.Stop();
            await Task.Delay(200);
            await stop.CancelAsync();
            await reader;
            output.WriteLine($"apply (read+diff+write) {applyWatch.ElapsedMilliseconds} ms, states {string.Join(", ", observed)}, slowest read {slowest.TotalMilliseconds:F0} ms");
            Assert.Subset(new HashSet<string> { "1/0/0/60000", "0/1/20000/60000" }, observed);
            Assert.Contains("0/1/20000/60000", observed);
            Assert.True(slowest < TimeSpan.FromSeconds(2), $"slowest read {slowest}");
            await AssertBrandListIsAsync(tenant.Id, connection.Id, next);
        }
        finally
        {
            await DeleteBrandDiffTenantAsync(tenant.Id);
        }
    }

    // Old vs new with 0.1 % changes: rows written, WAL bytes, duration (printed for the report). Sized for CI (28 Nov 2026): at
    // 1 M rows the test ran 4-6 min locally and longer on a runner, where it stayed silent past the 8-minute hang limit
    // (Validate 46082558502, attempt 1); the old path's two full rewrites of 1 M rows alone wrote ~1.45 GB of WAL. What it shows,
    // "the diff path writes in proportion to the changes, not to the list", holds at 200 000 rows, where the new path is
    // measured; the old path rewrites the whole list every time, so it is measured on a 20 000-row list only.
    [PostgreSqlFact(Lane = PostgreSqlLane.Load)]
    public async Task BrandDiff_LargeListWithATenthOfAPercentChanged_WritesOnlyTheChanges()
    {
        var (tenant, connection) = await SeedBrandDiffTenantAsync("brand-diff-large");
        var (oldTenant, oldConnection) = await SeedBrandDiffTenantAsync("brand-diff-large-old");
        try
        {
            const int size = 200_000;
            const int oldPathSize = 20_000;
            // 0.1 %: every 2 500th renamed, every 3 333rd removed, 300 added, spread over the list.
            RemoteReferenceItem[] Mutate(RemoteReferenceItem[] source, string tag) =>
            [
                .. source.Where((_, i) => i % 3_333 != 7).Select((x, i) => i % 2_500 == 11 ? DiffBrand(x.ExternalId, $"{x.Name} {tag}") : x),
                .. Enumerable.Range(0, 300).Select(i => DiffBrand($"Q{tag}{i:D6}", $"Yeni {tag} {i}"))
            ];

            async Task<long> MeasureAsync(string label, Guid tenantId, Guid connectionId, RemoteReferenceItem[] items, bool diff)
            {
                Npgsql.NpgsqlConnection.ClearAllPools();
                await Task.Delay(1_500);
                var before = await WriteCountersAsync();
                var watch = Stopwatch.StartNew();
                await SyncBrandListOkAsync(tenantId, connectionId, items, diff);
                watch.Stop();
                Npgsql.NpgsqlConnection.ClearAllPools();
                await Task.Delay(1_500);
                var after = await WriteCountersAsync();
                var line = $"{label}: {watch.Elapsed.TotalSeconds:F1} s, inserted {after.Ins - before.Ins}, updated {after.Upd - before.Upd}, deleted {after.Del - before.Del}, WAL {(after.Wal - before.Wal) / 1024.0 / 1024.0:F1} MiB";
                output.WriteLine(line);
                return (after.Ins - before.Ins) + (after.Upd - before.Upd) + (after.Del - before.Del);
            }

            // The old path (a full rewrite) on 20 000 rows.
            var oldList = DiffBrandList("O", oldPathSize);
            await SyncBrandListOkAsync(oldTenant.Id, oldConnection.Id, oldList, diff: false);
            var oldDay1 = Mutate(oldList, "a");
            await MeasureAsync($"old path ({oldPathSize:N0}), unchanged", oldTenant.Id, oldConnection.Id, oldList, diff: false);
            await MeasureAsync($"old path ({oldPathSize:N0}), 0.1 % changed", oldTenant.Id, oldConnection.Id, oldDay1, diff: false);
            await AssertBrandListIsAsync(oldTenant.Id, oldConnection.Id, oldDay1);

            // The new path on 200 000 rows.
            var list = DiffBrandList("P", size);
            var seedWatch = Stopwatch.StartNew();
            await SyncBrandListOkAsync(tenant.Id, connection.Id, list, diff: false);
            output.WriteLine($"seed (full copy, first list of {size:N0}) {seedWatch.Elapsed.TotalSeconds:F1} s");
            var day1 = Mutate(list, "a");
            var day2 = Mutate(day1, "b");
            // Rows written follow the changes, not the list: none for an unchanged list, about 440 (300 added, 80 renamed, 60
            // removed) for a 0.1 % change of 200 000 rows. Upper bounds only (the statistics counters may lag, never run ahead).
            Assert.True(await MeasureAsync($"new path ({size:N0}), unchanged", tenant.Id, connection.Id, list, diff: true) <= 10);
            Assert.True(await MeasureAsync($"new path ({size:N0}), 0.1 % changed", tenant.Id, connection.Id, day1, diff: true) <= 2_000);
            Assert.True(await MeasureAsync($"new path ({size:N0}), 0.1 % changed again", tenant.Id, connection.Id, day2, diff: true) <= 2_000);
            await AssertBrandListIsAsync(tenant.Id, connection.Id, day2);
        }
        finally
        {
            await using var db = fixture.CreateContext();
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
            await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM integration.reference_items WHERE "TenantId" = {tenant.Id} OR "TenantId" = {oldTenant.Id}""");
            await DeleteBrandDiffTenantAsync(tenant.Id);
            await DeleteBrandDiffTenantAsync(oldTenant.Id);
        }
    }

    private async Task<(long Ins, long Upd, long Del, long Wal)> WriteCountersAsync()
    {
        await using var db = fixture.CreateContext();
        var text = await db.Database.SqlQuery<string>($"""
            SELECT n_tup_ins::text || '/' || n_tup_upd::text || '/' || n_tup_del::text || '/' || (pg_current_wal_lsn() - '0/0'::pg_lsn)::bigint::text AS "Value"
            FROM pg_stat_user_tables WHERE schemaname = 'integration' AND relname = 'reference_items'
            """).SingleAsync();
        var parts = text.Split('/').Select(long.Parse).ToArray();
        return (parts[0], parts[1], parts[2], parts[3]);
    }

    // Review probe 2: an id reused for another brand, a case-only or whitespace-only rename and a deactivation each take
    // the VERIFIED mapping off the current list (the composer then asks for a mapping); a payload-only change keeps it.
    [PostgreSqlFact]
    public async Task BrandDiff_AnyNameOrActiveChangeTakesTheMappingOffTheCurrentList()
    {
        var (tenant, connection) = await SeedBrandDiffTenantAsync("brand-diff-reuse");
        var now = DateTimeOffset.UtcNow;
        var locals = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToArray();
        try
        {
            var list = DiffBrandList("R", 100);
            await SyncBrandListOkAsync(tenant.Id, connection.Id, list, diff: true);
            var snapshot = (await CurrentBrandListAsync(tenant.Id, connection.Id)).Current;
            await using (var db = fixture.CreateContext())
            {
                for (var i = 0; i < locals.Length; i++)
                {
                    db.Brands.Add(new Brand { Id = locals[i], TenantId = tenant.Id, Name = $"Panel {i}", NormalizedName = $"PANEL {i}", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
                    db.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = snapshot.Id, LocalId = locals[i], ScopeExternalId = "", ExternalId = list[i + 1].ExternalId, Status = "VERIFIED", VerifiedAt = now, Version = 1 });
                }
                await db.SaveChangesAsync();
            }
            RemoteReferenceItem[] next = [list[0],
                DiffBrand(list[1].ExternalId, "Bambaşka Firma"),
                DiffBrand(list[2].ExternalId, list[2].Name.ToLowerInvariant()),
                DiffBrand(list[3].ExternalId, list[3].Name + " "),
                list[4] with { IsActive = false },
                DiffBrand(list[5].ExternalId, list[5].Name, ""","logo":"x" """),
                .. list.Skip(6)];
            await SyncBrandListOkAsync(tenant.Id, connection.Id, next, diff: true);
            var results = new List<bool>();
            foreach (var local in locals) results.Add((await MappedBrandAsync(tenant.Id, connection.Id, local)).OnCurrent);
            Assert.Equal([false, false, false, false, true], results);
            await using (var db = fixture.CreateContext())
                Assert.All(await db.BrandMappings.AsNoTracking().Where(m => m.TenantId == tenant.Id).ToListAsync(), m => Assert.Contains(m.LocalId, locals));
        }
        finally
        {
            await DeleteBrandDiffTenantAsync(tenant.Id);
        }
    }

    // Review probe 3: a second run of the same connection while the first is still reading is refused by the connection
    // lock; the first applies its own list.
    [PostgreSqlFact]
    public async Task BrandDiff_TwoRunsOfOneConnectionDoNotInterleave()
    {
        var (tenant, connection) = await SeedBrandDiffTenantAsync("brand-diff-concurrent-runs");
        try
        {
            var list = DiffBrandList("C", 400);
            await SyncBrandListOkAsync(tenant.Id, connection.Id, list, diff: true);
            RemoteReferenceItem[] listA = [.. list.Skip(1), DiffBrand("C9999991", "A Ekledi")];
            RemoteReferenceItem[] listB = [.. list.Skip(2), DiffBrand("C9999992", "B Ekledi")];
            var gate = new TaskCompletionSource();
            var entered = new TaskCompletionSource();
            var runA = Task.Run(() => SyncBrandListAsync(tenant.Id, connection.Id, new GatedBrandPort(listA, entered, gate.Task), diff: true));
            await entered.Task;
            var b = await SyncBrandListAsync(tenant.Id, connection.Id, new PagedBrandPort(listB), diff: true);
            gate.SetResult();
            var a = await runA;
            Assert.Equal("CONNECTION_OPERATION_BUSY", b.ErrorCode);
            Assert.True(a.Succeeded, a.ErrorSummary);
            await AssertBrandListIsAsync(tenant.Id, connection.Id, listA);
        }
        finally
        {
            await DeleteBrandDiffTenantAsync(tenant.Id);
        }
    }

    private sealed class GatedBrandPort(IReadOnlyList<RemoteReferenceItem> items, TaskCompletionSource entered, Task gate) : IReferenceDataPort
    {
        public async Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken)
        {
            var index = string.IsNullOrEmpty(page.Cursor) ? 0 : int.Parse(page.Cursor);
            if (index == 1) { entered.TrySetResult(); await gate; }
            const int size = 200;
            var slice = items.Skip(index * size).Take(size).ToList();
            var hasMore = (index + 1) * size < items.Count;
            return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Success(new(slice, hasMore ? (index + 1).ToString() : null, hasMore));
        }
    }

    // A brand list served in pages; it can fail on a page or repeat a cursor.
    private sealed class PagedBrandPort(IReadOnlyList<RemoteReferenceItem> items, int pageSize = 2_000, int failAtPage = -1, int repeatCursorAtPage = -1) : IReferenceDataPort
    {
        public Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken)
        {
            if (resource.ResourceType != "BRANDS") throw new InvalidOperationException($"Unexpected reference read: {resource.ResourceType}.");
            var index = string.IsNullOrEmpty(page.Cursor) ? 0 : int.Parse(page.Cursor);
            if (index == failAtPage)
                return Task.FromResult(AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(new(AdapterErrorClass.Remote5xx, "REMOTE_5XX", "Sunucu hatası.", 503, null, null)));
            var slice = items.Skip(index * pageSize).Take(pageSize).ToList();
            var hasMore = (index + 1) * pageSize < items.Count;
            var next = index == repeatCursorAtPage ? index.ToString() : (index + 1).ToString();
            return Task.FromResult(AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Success(new(slice, hasMore ? next : null, hasMore, items.Count)));
        }
    }
}
