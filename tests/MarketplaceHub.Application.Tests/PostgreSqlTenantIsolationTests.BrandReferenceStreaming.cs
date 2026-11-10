using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Speed plan item 9 (10 Nov 2026): a brand read that brings the current list again writes no row; a changed list is
// stored as before (the agreeing rows before the first difference copied, the rest written), and so is a shorter one.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlFact]
    public async Task BrandReferenceSync_AnUnchangedListWritesNoRowAndAChangedOneIsStoredWhole()
    {
        var tenant = NewTenant("brand-stream");
        var connection = NewQuestionConnection(tenant);
        static RemoteReferenceItem Brand(string id, string name) => new("BRANDS", id, null, name, name, 0, false, true, $$"""{"id":{{id}},"name":"{{name}}"}""");
        RemoteReferenceItem[] list = [Brand("101", "Ravencia"), Brand("102", "Çiçek"), Brand("103", "İpek"), Brand("104", "Şal")];
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            await seed.SaveChangesAsync();
        }

        async Task SyncAsync(IReadOnlyList<RemoteReferenceItem> items)
        {
            var port = new ScriptedReferenceDataTestPort();
            port.Enqueue(new("BRANDS", null), items);
            await using var db = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(db, null!, port, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ReferenceSync, """{"resourceType":"BRANDS"}""", "brand-stream", CancellationToken.None);
            Assert.True(result.Succeeded, result.ErrorSummary);
        }

        async Task<(ReferenceSnapshot Current, int Snapshots, List<ReferenceItem> Items, string Stamps)> ReadAsync()
        {
            await using var db = fixture.CreateContext();
            var current = await db.ReferenceSnapshots.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == "BRANDS" && x.IsCurrent);
            var snapshots = await db.ReferenceSnapshots.CountAsync(x => x.TenantId == tenant.Id && x.ResourceType == "BRANDS");
            var items = await db.ReferenceItems.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.SnapshotId == current.Id).OrderBy(x => x.SortOrder).ToListAsync();
            var stamps = await db.Database.SqlQuery<string>($"""SELECT coalesce(string_agg(xmin::text, ',' ORDER BY "Id"), '') AS "Value" FROM integration.reference_items WHERE "TenantId" = {tenant.Id}""").SingleAsync();
            return (current, snapshots, items, stamps);
        }

        try
        {
            await SyncAsync(list);
            var first = await ReadAsync();
            Assert.Equal(4, first.Items.Count);

            // The same list again: the current snapshot stays, no row is written or removed, no staging snapshot is left.
            await SyncAsync(list);
            var same = await ReadAsync();
            Assert.Equal(first.Current.Id, same.Current.Id);
            Assert.Equal(first.Current.ContentHash, same.Current.ContentHash);
            Assert.Equal(1, same.Snapshots);
            Assert.Equal(first.Stamps, same.Stamps);

            // A brand renamed in the middle: a new snapshot holding the whole new list, hashed as a full write hashes it.
            RemoteReferenceItem[] renamed = [list[0], list[1], Brand("103", "İpek Yolu"), list[3]];
            await SyncAsync(renamed);
            var changed = await ReadAsync();
            Assert.NotEqual(first.Current.Id, changed.Current.Id);
            Assert.Equal(["Ravencia", "Çiçek", "İpek Yolu", "Şal"], changed.Items.Select(x => x.Name));
            Assert.Equal([0, 1, 2, 3], changed.Items.Select(x => x.SortOrder ?? -1));
            Assert.Equal(first.Items[0].PayloadHash, changed.Items[0].PayloadHash);
            Assert.Equal("İPEK YOLU", changed.Items[2].NormalizedName);
            Assert.Equal(4, changed.Current.ItemCount);
            var expectedHash = await FreshBrandHashAsync(renamed);
            Assert.Equal(expectedHash, changed.Current.ContentHash);

            // A brand dropped at the end (every read item agrees, the list is shorter): stored as a new list too.
            RemoteReferenceItem[] shorter = [.. renamed.Take(3)];
            await SyncAsync(shorter);
            var shortened = await ReadAsync();
            Assert.NotEqual(changed.Current.Id, shortened.Current.Id);
            Assert.Equal(3, shortened.Items.Count);
            Assert.Equal(3, shortened.Current.ItemCount);
            Assert.Equal(await FreshBrandHashAsync(shorter), shortened.Current.ContentHash);
        }
        finally
        {
            await DeleteBrandStreamTenantAsync(tenant.Id);
        }
    }

    // The hash a first read of this list (nothing stored yet) gives, in a tenant of its own.
    private async Task<string> FreshBrandHashAsync(IReadOnlyList<RemoteReferenceItem> items)
    {
        var tenant = NewTenant("brand-stream-fresh");
        var connection = NewQuestionConnection(tenant);
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        await db.SaveChangesAsync();
        try
        {
            var port = new ScriptedReferenceDataTestPort();
            port.Enqueue(new("BRANDS", null), items);
            var processor = new MarketplaceJobProcessor(db, null!, port, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            Assert.True((await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ReferenceSync, """{"resourceType":"BRANDS"}""", "brand-stream-fresh", CancellationToken.None)).Succeeded);
            return await db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.IsCurrent).Select(x => x.ContentHash).SingleAsync();
        }
        finally
        {
            await DeleteBrandStreamTenantAsync(tenant.Id);
        }
    }

    private async Task DeleteBrandStreamTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.SyncCursors.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReferenceItems.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReferenceSnapshots.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }
}
