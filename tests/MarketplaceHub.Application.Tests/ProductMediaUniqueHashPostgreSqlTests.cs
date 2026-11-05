using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// K11 (audit 2026-10-20 / 2026-10-24): a picture uploaded twice at the same moment stored two active PRODUCT_MEDIA assets
// with one content hash. The partial unique index refuses the second one, so the upload endpoint's catch reuses the
// first; an archived copy, a URL image with the same hash and another tenant's upload stay allowed.
[Collection("postgres-integration")]
public sealed class ProductMediaUniqueHashPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task ActiveProductMedia_WithTheSameHashTwice_IsRefusedButArchivedUrlAndOtherTenantCopiesAreNot()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = NewTenant("k11-media", now);
        var other = NewTenant("k11-media-other", now);
        var hash = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.AddRange(tenant, other);
            seed.FileAssets.AddRange(
                Asset(tenant, "PRODUCT_MEDIA", hash, now),
                Asset(tenant, "PRODUCT_MEDIA", hash, now, archived: true),
                Asset(tenant, "PRODUCT_MEDIA_URL", hash, now),
                Asset(other, "PRODUCT_MEDIA", hash, now));
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        db.FileAssets.Add(Asset(tenant, "PRODUCT_MEDIA", hash, now));
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(refused.InnerException).SqlState);
    }

    private static Tenant NewTenant(string code, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Code = $"{code}-{Guid.NewGuid():N}",
        DisplayName = code,
        Status = RecordStatus.Active,
        Timezone = "Europe/Istanbul",
        CreatedAt = now,
        UpdatedAt = now,
        Version = 1
    };

    private static FileAsset Asset(Tenant tenant, string classification, string hash, DateTimeOffset now, bool archived = false)
    {
        var id = Guid.CreateVersion7();
        return new FileAsset
        {
            Id = id,
            TenantId = tenant.Id,
            Classification = classification,
            RelativePath = $"k11/{id:N}.png",
            MimeType = "image/png",
            SizeBytes = 1,
            Sha256 = hash,
            Status = archived ? "DELETED" : "ACTIVE",
            CreatedAt = now,
            ArchivedAt = archived ? now : null
        };
    }
}
