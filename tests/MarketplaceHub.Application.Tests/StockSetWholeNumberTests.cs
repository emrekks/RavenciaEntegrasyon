using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The on-hand stock endpoint takes whole pieces only: "1.250" read as 1.25 by a screen, or any other fraction, is a 422
// before the database is touched, never a stored 1.25.
public sealed class StockSetWholeNumberTests
{
    [Theory]
    [InlineData("1.25")]
    [InlineData("2.5")]
    [InlineData("0.0001")]
    public async Task SetOnHand_RefusesAFractionWith422(string onHand)
    {
        // No database is reached: the check runs before the first query.
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);
        var service = new InventoryService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), TimeProvider.System, new ConfigurationBuilder().Build());

        var result = await service.SetOnHandAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new StockSetCommand(decimal.Parse(onHand, System.Globalization.CultureInfo.InvariantCulture), 0m, "test", "event-1"), "key-1", "corr-1", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(422, result.Error!.Status);
        Assert.Equal("VALIDATION_FAILED", result.Error.Code);
        Assert.Contains("tam sayı", result.Error.Message, StringComparison.Ordinal);
        Assert.True(result.Error.FieldErrors!.ContainsKey("onHand"));
    }
}
