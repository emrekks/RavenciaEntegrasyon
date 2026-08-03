using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Application.Tests;

public sealed class F3ConnectionScopeTests
{
    [Fact]
    public async Task CreateAsync_rejects_deferred_shopify_before_database_access()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=scope_guard;Username=unused;Password=unused")
            .Options);
        var protection = new EphemeralDataProtectionProvider();
        var service = new F3ConnectionService(
            db,
            new CursorCodec(protection, TimeProvider.System),
            protection,
            new TokenHasher(Enumerable.Repeat((byte)7, 32).ToArray()),
            TimeProvider.System);

        var result = await service.CreateAsync(
            Guid.NewGuid(),
            new CreateConnectionCommand("Deferred Shopify", "STAGE", "example.myshopify.com", "2026-07", null, "SHOPIFY"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("VALIDATION_FAILED", result.Error?.Code);
        Assert.Equal(422, result.Error?.Status);
        Assert.True(result.Error?.FieldErrors?.ContainsKey("platformCode"));
    }
}
