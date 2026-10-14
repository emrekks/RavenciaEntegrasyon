using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TenantBoundaryModelTests
{
    [Fact]
    public void OperationalIssueDedupeKey_IsUniquePerTenant()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=metadata-only;Username=metadata-only;Password=metadata-only")
            .Options;

        using var db = new AppDbContext(options);
        var entity = db.Model.FindEntityType(typeof(OperationalIssue));
        Assert.NotNull(entity);

        var index = Assert.Single(entity!.GetIndexes(), candidate => candidate.IsUnique && candidate.Properties.Any(property => property.Name == nameof(OperationalIssue.DedupeKey)));
        Assert.Equal([nameof(OperationalIssue.TenantId), nameof(OperationalIssue.DedupeKey)], index.Properties.Select(property => property.Name));
    }
}
