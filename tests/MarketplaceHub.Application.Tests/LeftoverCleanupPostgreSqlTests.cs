using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Files;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner, 12 Nov 2026 ("kökten çözüm"): replaced keys, deleted connections' settings and files whose owner rows are gone
// no longer pile up.
[Collection("postgres-integration")]
public sealed class LeftoverCleanupPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task RotatingAKey_LeavesOnlyTheNewKey()
    {
        var tenant = NewTenant("lo-key");
        var connection = NewConnection(tenant.Id, "TRENDYOL", "ACTIVE");
        await SeedAsync(tenant, db =>
        {
            db.PlatformConnections.Add(connection);
            db.PlatformCredentials.Add(NewCredential(tenant.Id, connection.Id, revokedAt: fixture.Now.AddDays(-3)));
            db.PlatformCredentials.Add(NewCredential(tenant.Id, connection.Id, revokedAt: null));
        });
        try
        {
            await using var db = fixture.CreateContext();
            var dataProtection = new EphemeralDataProtectionProvider();
            var service = new MarketplaceConnectionService(db, new CursorCodec(dataProtection, fixture.TimeProvider), dataProtection, fixture.TokenHasher, fixture.TimeProvider, new ConfigurationBuilder().Build());

            var first = await service.RotateCredentialAsync(tenant.Id, connection.Id, connection.Version, new CredentialCommand("api-key-1", "api-secret-1"), CancellationToken.None);
            Assert.True(first.Succeeded, first.Error?.Message);
            var second = await service.RotateCredentialAsync(tenant.Id, connection.Id, first.Value!.Version, new CredentialCommand("api-key-2", "api-secret-2"), CancellationToken.None);
            Assert.True(second.Succeeded, second.Error?.Message);

            var keys = await db.PlatformCredentials.AsNoTracking().Where(x => x.ConnectionId == connection.Id).ToListAsync();
            var key = Assert.Single(keys);
            Assert.Null(key.RevokedAt);
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task TheSweep_RemovesRevokedKeys()
    {
        var tenant = NewTenant("lo-revoked");
        var connection = NewConnection(tenant.Id, "TRENDYOL", "ACTIVE");
        var revoked = NewCredential(tenant.Id, connection.Id, revokedAt: fixture.Now.AddDays(-1));
        var current = NewCredential(tenant.Id, connection.Id, revokedAt: null);
        await SeedAsync(tenant, db => { db.PlatformConnections.Add(connection); db.PlatformCredentials.AddRange(revoked, current); });
        try
        {
            await using var db = fixture.CreateContext();
            Assert.True(await ConnectionLeftovers.RemoveRevokedCredentialsAsync(db, CancellationToken.None) >= 1);
            Assert.Equal([current.Id], await db.PlatformCredentials.AsNoTracking().Where(x => x.ConnectionId == connection.Id).Select(x => x.Id).ToListAsync());
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    // A deleted connection with nothing historical left loses every setting and its row; one an order still names stays
    // as a tombstone without keys or settings.
    [PostgreSqlFact]
    public async Task ADeletedConnection_LosesItsSettings_AndItsRowWhenNoRecordNamesIt()
    {
        var tenant = NewTenant("lo-conn");
        var bare = NewConnection(tenant.Id, "TRENDYOL", ConnectionLeftovers.DeletedStatus);
        var withOrder = NewConnection(tenant.Id, "HEPSIBURADA", ConnectionLeftovers.DeletedStatus);
        var live = NewConnection(tenant.Id, "N11", "ACTIVE");
        var order = NewOrder(tenant.Id, withOrder.Id);
        await SeedAsync(tenant, db =>
        {
            db.PlatformConnections.AddRange(bare, withOrder, live);
            foreach (var connection in new[] { bare, withOrder, live })
            {
                db.PlatformCredentials.Add(NewCredential(tenant.Id, connection.Id, revokedAt: null));
                db.SyncCursors.Add(new SyncCursor { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = "orders" });
            }
            db.Orders.Add(order);
        });
        try
        {
            await using (var db = fixture.CreateContext())
                Assert.Equal(1, await ConnectionLeftovers.SweepDeletedConnectionsAsync(db, fixture.Now, CancellationToken.None, tenant.Id));

            await using var verify = fixture.CreateContext();
            var rows = await verify.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.Id).ToListAsync();
            Assert.DoesNotContain(bare.Id, rows);
            Assert.Contains(withOrder.Id, rows);
            Assert.Contains(live.Id, rows);
            var keyed = await verify.PlatformCredentials.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.ConnectionId).ToListAsync();
            Assert.Equal([live.Id], keyed);
            var cursors = await verify.SyncCursors.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.ConnectionId).ToListAsync();
            Assert.Equal([live.Id], cursors);
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task DeletingAProduct_HandsItsPicturesToThePurge_ButNotOnesAnotherCardShows()
    {
        var tenant = NewTenant("lo-product");
        var deleted = NewProduct(tenant.Id);
        var kept = NewProduct(tenant.Id);
        var own = NewAsset(tenant.Id, "PRODUCT_MEDIA", fixture.Now);
        var shared = NewAsset(tenant.Id, "PRODUCT_MEDIA_URL", fixture.Now);
        await SeedAsync(tenant, db =>
        {
            db.Products.AddRange(deleted, kept);
            db.FileAssets.AddRange(own, shared);
            db.ProductMedia.Add(NewMedia(tenant.Id, deleted.Id, own.Id, 0));
            db.ProductMedia.Add(NewMedia(tenant.Id, deleted.Id, shared.Id, 1));
            db.ProductMedia.Add(NewMedia(tenant.Id, kept.Id, shared.Id, 0));
        });
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var result = await new CatalogService(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()))
                    .DeleteProductAsync(tenant.Id, deleted.Id, deleted.Version, CancellationToken.None);
                Assert.True(result.Succeeded, result.Error?.Message);
            }

            await using var verify = fixture.CreateContext();
            Assert.Equal(OperationalDataMaintenanceService.PurgePendingStatus, (await verify.FileAssets.AsNoTracking().SingleAsync(x => x.Id == own.Id)).Status);
            Assert.Equal("ACTIVE", (await verify.FileAssets.AsNoTracking().SingleAsync(x => x.Id == shared.Id)).Status);
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    // The safety net: an old file nothing points at is marked; a referenced one, a young one and one an unfinished
    // job names in its payload (JSON) are left alone.
    [PostgreSqlFact]
    public async Task TheOrphanSweep_MarksOnlyOldUnreferencedFiles()
    {
        var tenant = NewTenant("lo-orphan");
        var product = NewProduct(tenant.Id);
        var old = fixture.Now.AddDays(-3);
        var orphan = NewAsset(tenant.Id, "PRODUCT_MEDIA_URL", old);
        var referenced = NewAsset(tenant.Id, "PRODUCT_MEDIA", old);
        var archivedOnly = NewAsset(tenant.Id, "PRODUCT_MEDIA", old);
        var young = NewAsset(tenant.Id, "RETURN_EVIDENCE", fixture.Now.AddHours(-1));
        var inJob = NewAsset(tenant.Id, "RETURN_EVIDENCE", old);
        var unknownKind = NewAsset(tenant.Id, "SOMETHING_ELSE", old);
        await SeedAsync(tenant, db =>
        {
            db.Products.Add(product);
            db.FileAssets.AddRange(orphan, referenced, archivedOnly, young, inJob, unknownKind);
            db.ProductMedia.Add(NewMedia(tenant.Id, product.Id, referenced.Id, 0));
            var archived = NewMedia(tenant.Id, product.Id, archivedOnly.Id, 1);
            archived.Status = "ARCHIVED";
            db.ProductMedia.Add(archived);
            db.IntegrationJobs.Add(new IntegrationJob
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                JobType = "RETURN_DECISION_TEST",
                PayloadJson = $$"""{"evidenceAssetIds":["{{inJob.Id:D}}"]}""",
                PayloadHash = "hash",
                JobDedupKey = $"lo-{Guid.NewGuid():N}",
                EffectIdempotencyKey = $"lo-{Guid.NewGuid():N}",
                CorrelationId = "lo",
                Status = JobStatus.Pending,
                AvailableAt = fixture.Now,
                CreatedAt = fixture.Now
            });
        });
        try
        {
            await using (var db = fixture.CreateContext())
                Assert.Equal(2, await FileAssetReferences.MarkOrphansAsync(db, fixture.Now, CancellationToken.None, tenant.Id));

            await using var verify = fixture.CreateContext();
            var status = await verify.FileAssets.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.Id, x => x.Status);
            Assert.Equal(OperationalDataMaintenanceService.PurgePendingStatus, status[orphan.Id]);
            Assert.Equal(OperationalDataMaintenanceService.PurgePendingStatus, status[archivedOnly.Id]);
            Assert.Equal("ACTIVE", status[referenced.Id]);
            Assert.Equal("ACTIVE", status[young.Id]);
            Assert.Equal("ACTIVE", status[inJob.Id]);
            Assert.Equal("ACTIVE", status[unknownKind.Id]);
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    // The purge deletes the file and then the row; a file an owner took back after it was marked goes back to ACTIVE
    // and keeps its file; a row an archived picture row still points at stays as DELETED.
    [PostgreSqlFact]
    public async Task ThePurge_DeletesFileAndRow_GivesBackAReusedFile_AndKeepsARowAnArchivedPictureNames()
    {
        var tenant = NewTenant("lo-purge");
        var product = NewProduct(tenant.Id);
        var marked = fixture.Now.AddHours(-2);
        var gone = NewAsset(tenant.Id, "PRODUCT_MEDIA", fixture.Now.AddDays(-2), OperationalDataMaintenanceService.PurgePendingStatus, marked);
        var reused = NewAsset(tenant.Id, "PRODUCT_MEDIA", fixture.Now.AddDays(-2), OperationalDataMaintenanceService.PurgePendingStatus, marked);
        var archivedRow = NewAsset(tenant.Id, "PRODUCT_MEDIA", fixture.Now.AddDays(-2), OperationalDataMaintenanceService.PurgePendingStatus, marked);
        await SeedAsync(tenant, db =>
        {
            db.Products.Add(product);
            db.FileAssets.AddRange(gone, reused, archivedRow);
            db.ProductMedia.Add(NewMedia(tenant.Id, product.Id, reused.Id, 0));
            var archived = NewMedia(tenant.Id, product.Id, archivedRow.Id, 1);
            archived.Status = "ARCHIVED";
            db.ProductMedia.Add(archived);
        });
        try
        {
            var storage = new RecordingStorage();
            await using (var db = fixture.CreateContext())
                await PurgedFileRetention.SweepAsync(db, storage, fixture.Now, CancellationToken.None);

            Assert.Contains((tenant.Id, gone.RelativePath), storage.Deleted);
            Assert.Contains((tenant.Id, archivedRow.RelativePath), storage.Deleted);
            Assert.DoesNotContain((tenant.Id, reused.RelativePath), storage.Deleted);
            await using var verify = fixture.CreateContext();
            var rows = await verify.FileAssets.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.Id);
            Assert.False(rows.ContainsKey(gone.Id));
            Assert.Equal("ACTIVE", rows[reused.Id].Status);
            Assert.Null(rows[reused.Id].ArchivedAt);
            Assert.Equal(PurgedFileRetention.DeletedStatus, rows[archivedRow.Id].Status);
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    // Every foreign key on ops.file_assets is one FileAssetReferences checks.
    [PostgreSqlFact]
    public async Task EveryForeignKeyOnFileAssets_IsAKnownReference()
    {
        await using var db = fixture.CreateContext();
        var referencing = await db.Database.SqlQueryRaw<string>("""
            SELECT conrelid::regclass::text || '.' || a.attname AS "Value"
            FROM pg_constraint c JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = ANY(c.conkey)
            WHERE c.contype = 'f' AND c.confrelid = 'ops.file_assets'::regclass AND a.attname <> 'TenantId'
            """).ToListAsync();
        Assert.NotEmpty(referencing);
        foreach (var reference in referencing)
        {
            var dot = reference.LastIndexOf('.');
            Assert.Contains($"{reference[..dot]} ", FileAssetReferences.NotReferenced.Replace("\n", " ", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Contains($"\"{reference[(dot + 1)..]}\"=f.\"Id\"", FileAssetReferences.NotReferenced, StringComparison.Ordinal);
        }
    }

    private Tenant NewTenant(string code) => new() { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };

    private static PlatformConnection NewConnection(Guid tenantId, string platformCode, string status) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = $"{platformCode} artık testi",
        ExternalStoreId = $"lo-{Guid.NewGuid():N}"[..20],
        Status = status,
        ApiVersion = "V1",
        Version = 1
    };

    private PlatformCredential NewCredential(Guid tenantId, Guid connectionId, DateTimeOffset? revokedAt) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        CredentialType = "BASIC",
        ProtectedPayload = "protected",
        MaskedHint = "***",
        CreatedAt = fixture.Now.AddDays(-5),
        RevokedAt = revokedAt
    };

    private Order NewOrder(Guid tenantId, Guid connectionId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        ExternalOrderId = $"lo-{Guid.NewGuid():N}",
        OrderNumber = $"lo-{Guid.NewGuid():N}"[..20],
        Currency = "TRY",
        GrossAmount = 120m,
        NetAmount = 120m,
        OrderedAt = fixture.Now.AddDays(-3),
        LastRemoteModifiedAt = fixture.Now.AddDays(-3),
        CustomerSnapshotJson = "{}",
        ShipmentAddressSnapshotJson = "{}",
        InvoiceAddressSnapshotJson = "{}",
        DerivedStatus = "DELIVERED",
        CreatedAt = fixture.Now,
        UpdatedAt = fixture.Now,
        Version = 1
    };

    private Product NewProduct(Guid tenantId) => new() { Id = Guid.CreateVersion7(), TenantId = tenantId, Title = $"Artık ürün {Guid.NewGuid():N}", Description = "Açıklama", Status = ProductStatus.Active, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };

    private static ProductMedia NewMedia(Guid tenantId, Guid productId, Guid assetId, int sortOrder) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ProductId = productId,
        FileAssetId = assetId,
        MediaRole = sortOrder == 0 ? "PRIMARY" : "GALLERY",
        SortOrder = sortOrder,
        Status = "ACTIVE"
    };

    private static FileAsset NewAsset(Guid tenantId, string classification, DateTimeOffset createdAt, string status = "ACTIVE", DateTimeOffset? archivedAt = null) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Classification = classification,
        RelativePath = classification == "PRODUCT_MEDIA_URL" ? $"https://cdn.example.com/{Guid.NewGuid():N}.jpg" : $"{tenantId:N}/{Guid.NewGuid():N}.jpg",
        MimeType = classification == "PRODUCT_MEDIA_URL" ? "image/remote" : "image/jpeg",
        SizeBytes = 10,
        Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        Status = status,
        CreatedAt = createdAt,
        ArchivedAt = archivedAt
    };

    private async Task SeedAsync(Tenant tenant, Action<AppDbContext> extra)
    {
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        extra(db);
        await db.SaveChangesAsync();
    }

    private async Task DeleteTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ProductMedia.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ProductVariants.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Products.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.FileAssets.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.SyncCursors.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformCapabilities.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformCredentials.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AuditLogs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private sealed class RecordingStorage : IPrivateFileStorage
    {
        public List<(Guid TenantId, string Path)> Deleted { get; } = [];
        public Task<string> SaveAsync(Guid tenantId, string relativeName, string mimeType, Stream content, long maximumBytes, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(Guid tenantId, string storedPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid tenantId, string storedPath, CancellationToken cancellationToken)
        {
            Deleted.Add((tenantId, storedPath));
            return Task.FromResult(true);
        }
    }
}
