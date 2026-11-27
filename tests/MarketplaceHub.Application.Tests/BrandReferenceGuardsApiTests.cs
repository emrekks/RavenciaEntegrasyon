using System.Net;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Kuyruk Faz 1B review: the brand list is updated in place, so the snapshot id no longer stops a save made from a page
// read before a rename (O1). A brand mapping is bound only while the current row carries the name the page showed;
// anything else answers 409 REFERENCE_ITEM_CHANGED. The shrink a brand read refused is approved once by the owner or an
// administrator (D2), audited.
[Collection("postgres-integration")]
public sealed class BrandReferenceGuardsApiTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    private async Task<(Guid ConnectionId, Guid SnapshotId, Guid LocalId)> SeedAsync()
    {
        await using var scope = fixture.Host!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = fixture.TenantId, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "Marka koruma", ExternalStoreId = $"seller-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1" };
        var snapshot = new ReferenceSnapshot { Id = Guid.CreateVersion7(), TenantId = fixture.TenantId, ConnectionId = connection.Id, ResourceType = "BRANDS", SourceVersion = "v1", ContentHash = $"seed-{Guid.NewGuid():N}", FetchedAt = now, IsCurrent = true, ItemCount = 3 };
        var localId = Guid.CreateVersion7();
        db.PlatformConnections.Add(connection);
        db.ReferenceSnapshots.Add(snapshot);
        foreach (var (id, name) in new[] { ("101", "Eski Ad"), ("102", "Kalan"), ("103", "Silinecek") })
            db.ReferenceItems.Add(new ReferenceItem { Id = Guid.CreateVersion7(), TenantId = fixture.TenantId, ConnectionId = connection.Id, SnapshotId = snapshot.Id, ResourceType = "BRANDS", ExternalId = id, Name = name, NormalizedName = name.ToUpperInvariant(), Path = name, IsActive = true, PayloadHash = "h" });
        db.Brands.Add(new Brand { Id = localId, TenantId = fixture.TenantId, Name = $"Panel {Guid.NewGuid():N}", NormalizedName = $"PANEL {Guid.NewGuid():N}", IsActive = true, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return (connection.Id, snapshot.Id, localId);
    }

    private static HttpRequestMessage Save(string csrf, Guid localId, Guid connectionId, Guid snapshotId, string externalId, string? externalName, string? etag = null)
    {
        var request = ApiTestHost.Json(HttpMethod.Put, $"/api/v1/mappings/brands/{localId}", csrf, externalName is null
            ? new { connectionId, snapshotId, externalId, status = "VERIFIED" }
            : (object)new { connectionId, snapshotId, externalId, externalName, status = "VERIFIED" });
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return request;
    }

    [PostgreSqlFact]
    public async Task ABrandMappingIsSavedOnlyWhileTheBrandStillCarriesTheNameThePageShowed()
    {
        var (connectionId, snapshotId, localId) = await SeedAsync();
        var (client, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Operations);
        using (client)
        using (signIn)
        {
            // A client that names no brand (an old page) is refused, not trusted.
            using (var blind = await client.SendAsync(Save(csrf, localId, connectionId, snapshotId, "101", null)))
                await AssertChangedAsync(blind);

            // Unchanged: accepted.
            using var saved = await client.SendAsync(Save(csrf, localId, connectionId, snapshotId, "101", "Eski Ad"));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

            // The daily read renames 101 in place (same snapshot id) while a page still shows "Eski Ad": refused.
            await using (var scope = fixture.Host!.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.ReferenceItems.Where(x => x.TenantId == fixture.TenantId && x.SnapshotId == snapshotId && x.ExternalId == "101")
                    .ExecuteUpdateAsync(x => x.SetProperty(i => i.Name, "Tamamen Başka Marka").SetProperty(i => i.NormalizedName, "TAMAMEN BAŞKA MARKA"));
                await db.ReferenceItems.Where(x => x.TenantId == fixture.TenantId && x.SnapshotId == snapshotId && x.ExternalId == "103").ExecuteDeleteAsync();
            }
            using (var renamed = await client.SendAsync(Save(csrf, localId, connectionId, snapshotId, "101", "Eski Ad", saved.Headers.ETag!.Tag)))
                await AssertChangedAsync(renamed);

            // A brand the read removed: refused the same way.
            using (var vanished = await client.SendAsync(Save(csrf, localId, connectionId, snapshotId, "103", "Silinecek", saved.Headers.ETag!.Tag)))
                await AssertChangedAsync(vanished);

            // The mapping still points at 101 under the name the owner saw and confirmed at the time.
            using var read = await client.GetAsync($"/api/v1/mappings/brands/{localId}?connectionId={connectionId}");
            var mapping = JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("101", mapping.GetProperty("externalId").GetString());
            Assert.Equal(1, mapping.GetProperty("version").GetInt64());

            // Chosen again from the refreshed list: accepted.
            using var again = await client.SendAsync(Save(csrf, localId, connectionId, snapshotId, "102", "Kalan", saved.Headers.ETag!.Tag));
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }
    }

    private static async Task AssertChangedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ApiTestHost.ProblemAsync(response);
        Assert.Equal("REFERENCE_ITEM_CHANGED", problem.GetProperty("code").GetString());
        Assert.StartsWith("Marka Listesi Değişti, Yeniden Seçin", problem.GetProperty("title").GetString());
    }

    [PostgreSqlFact]
    public async Task AShrinkTheReadRefusedIsShownAndOnlyOwnersAndAdministratorsApproveItOnceAudited()
    {
        var (connectionId, _, _) = await SeedAsync();
        var (operations, operationsCsrf, operationsSignIn) = await fixture.SignInAsync(MembershipRole.Operations);
        var (admin, csrf, adminSignIn) = await fixture.SignInAsync(MembershipRole.Administrator);
        using (operations)
        using (operationsSignIn)
        using (admin)
        using (adminSignIn)
        {
            using (var none = await admin.GetAsync($"/api/v1/reference-data/brands/shrink?connectionId={connectionId}"))
                Assert.Equal("null", (await none.Content.ReadAsStringAsync()).Trim());
            using (var nothingToApprove = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Post, $"/api/v1/reference-data/brands/shrink/approval?connectionId={connectionId}", csrf, new { })))
                Assert.Equal(HttpStatusCode.NotFound, nothingToApprove.StatusCode);

            var seenAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await using (var scope = fixture.Host!.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = fixture.TenantId, DedupeKey = ReferenceSyncService.BrandListShrankIssueKey(connectionId), Code = ReferenceSyncService.BrandListShrankCode, Summary = ReferenceSyncService.BrandListShrinkSummary(3, 1), Status = IssueStatus.Open, FirstSeenAt = seenAt, LastSeenAt = seenAt, OccurrenceCount = 1 });
                await db.SaveChangesAsync();
            }

            using (var open = await operations.GetAsync($"/api/v1/reference-data/brands/shrink?connectionId={connectionId}"))
            {
                var view = JsonDocument.Parse(await open.Content.ReadAsStringAsync()).RootElement;
                Assert.Equal(3, view.GetProperty("currentCount").GetInt32());
                Assert.Contains("hiçbir marka silinmedi", view.GetProperty("summary").GetString());
                Assert.Equal(JsonValueKind.Null, view.GetProperty("approvedAt").ValueKind);
            }
            using (var forbidden = await operations.SendAsync(ApiTestHost.Json(HttpMethod.Post, $"/api/v1/reference-data/brands/shrink/approval?connectionId={connectionId}", operationsCsrf, new { })))
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

            using var approved = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Post, $"/api/v1/reference-data/brands/shrink/approval?connectionId={connectionId}", csrf, new { }));
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            var approval = JsonDocument.Parse(await approved.Content.ReadAsStringAsync()).RootElement;
            var approvedAt = approval.GetProperty("approvedAt").GetDateTimeOffset();
            Assert.Equal(approvedAt + TimeSpan.FromHours(48), approval.GetProperty("approvalExpiresAt").GetDateTimeOffset());

            await using (var scope = fixture.Host!.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var issue = await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == fixture.TenantId && x.DedupeKey == ReferenceSyncService.BrandListShrankIssueKey(connectionId));
                Assert.Equal(IssueStatus.Acknowledged, issue.Status);
                var audit = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.TenantId == fixture.TenantId && x.Action == "REFERENCE_LIST_SHRINK_APPROVED" && x.TargetId == connectionId.ToString());
                Assert.NotNull(audit.ActorUserId);
            }
        }
    }
}
