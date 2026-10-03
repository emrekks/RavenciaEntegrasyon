using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceJobProcessorReturnClaimTests
{
    [Fact]
    public void NewReturnClaim_PersistsDueDateAndMarketplaceDetails()
    {
        var lastModifiedAt = DateTimeOffset.Parse("2026-10-03T10:00:00Z");
        var dueAt = lastModifiedAt.AddHours(48);
        var now = DateTimeOffset.Parse("2026-10-03T11:00:00Z");
        var remote = new RemoteReturnClaim(
            "claim-1",
            "order-1",
            "WaitingInAction",
            "SIZE_TOO_LARGE",
            "Bedeni büyük geldi",
            dueAt,
            lastModifiedAt,
            [],
            "{}",
            "PTT Kargo",
            " return-tracking-1 ");

        var claim = MarketplaceJobProcessor.NewReturnClaim(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            remote,
            ReturnClaimStatus.ActionRequired,
            now);

        Assert.Equal(dueAt, claim.ActionDueAt);
        Assert.Equal("SIZE_TOO_LARGE", claim.ReasonCode);
        Assert.Equal("Bedeni büyük geldi", claim.ReasonText);
        Assert.Equal("PTT Kargo", claim.CargoProviderName);
        Assert.Equal("return-tracking-1", claim.CargoTrackingNumber);
        Assert.Equal(lastModifiedAt, claim.LastRemoteModifiedAt);
    }
}
