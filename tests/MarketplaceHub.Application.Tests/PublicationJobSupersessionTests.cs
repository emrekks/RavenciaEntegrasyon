using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// O4 of the 2 Nov 2026 catalog review: an old failed publish or approval check of a product is closed once a later one of
// the same profile on the same connection succeeded; nothing else is touched.
public sealed class PublicationJobSupersessionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-02T12:00:00Z");

    [Fact]
    public void ALaterPublishOrApprovalSuccessClosesTheOldFailure()
    {
        var connection = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var failed = (connection, MarketplaceJobTypes.ProductCreate, profile, JobStatus.ManualReview, Now.AddDays(-3));
        var later = (connection, MarketplaceJobTypes.ProductCreate, profile, JobStatus.Succeeded, Now.AddDays(-1));

        Assert.True(PublicationJobSupersession.Supersedes(failed, later));
        Assert.True(PublicationJobSupersession.Supersedes(failed with { Item4 = JobStatus.Blocked }, later));
        Assert.True(PublicationJobSupersession.Supersedes(failed with { Item2 = MarketplaceJobTypes.ProductApprovalReconcile }, later));
        Assert.True(PublicationJobSupersession.Supersedes(failed, later with { Item2 = MarketplaceJobTypes.ProductApprovalReconcile }));
    }

    [Fact]
    public void OnlyFailuresOfTheSameProfileBeforeASuccessAreClosed()
    {
        var connection = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var failed = (connection, MarketplaceJobTypes.ProductCreate, profile, JobStatus.ManualReview, Now.AddDays(-3));
        var later = (connection, MarketplaceJobTypes.ProductApprovalReconcile, profile, JobStatus.Succeeded, Now.AddDays(-1));

        // An earlier success, another connection or another product's profile do not count.
        Assert.False(PublicationJobSupersession.Supersedes(failed, later with { Item5 = Now.AddDays(-4) }));
        Assert.False(PublicationJobSupersession.Supersedes(failed, later with { Item5 = failed.Item5 }));
        Assert.False(PublicationJobSupersession.Supersedes(failed, later with { Item1 = Guid.NewGuid() }));
        Assert.False(PublicationJobSupersession.Supersedes(failed, later with { Item3 = Guid.NewGuid() }));
        Assert.False(PublicationJobSupersession.Supersedes(failed with { Item3 = Guid.Empty }, later with { Item3 = Guid.Empty }));
        // Only a success closes, and only blocked or manual-review failures; running, retrying or finished jobs stay.
        foreach (var status in new[] { JobStatus.Blocked, JobStatus.ManualReview, JobStatus.RetryScheduled, JobStatus.Dead, JobStatus.Cancelled })
            Assert.False(PublicationJobSupersession.Supersedes(failed, later with { Item4 = status }));
        foreach (var status in new[] { JobStatus.Pending, JobStatus.Leased, JobStatus.RetryScheduled, JobStatus.Dead, JobStatus.Cancelled, JobStatus.Succeeded })
            Assert.False(PublicationJobSupersession.Supersedes(failed with { Item4 = status }, later));
        // Other job types (updates, archives, price and stock) are never touched.
        foreach (var other in new[] { MarketplaceJobTypes.ProductUpdate, MarketplaceJobTypes.ProductArchive, MarketplaceJobTypes.PriceInventorySync })
        {
            Assert.False(PublicationJobSupersession.Supersedes(failed with { Item2 = other }, later));
            Assert.False(PublicationJobSupersession.Supersedes(failed, later with { Item2 = other }));
        }
    }

    [Fact]
    public void TheProfileIsReadFromThePayload()
    {
        var profile = Guid.Parse("01a196f3-d59e-7c4d-85b9-0c82d40d0641");
        Assert.Equal(profile, PublicationJobSupersession.ProfileIdOf("{\"JobId\":\"01a19dc1-0228-723e-abbc-4de60454dbbe\",\"ProductId\":\"01a19282-c632-78dc-8c58-c9cbc41e462e\",\"ProfileId\":\"01a196f3-d59e-7c4d-85b9-0c82d40d0641\"}"));
        Assert.Equal(Guid.Empty, PublicationJobSupersession.ProfileIdOf("{\"ProductId\":\"01a19282-c632-78dc-8c58-c9cbc41e462e\"}"));
        Assert.Equal(Guid.Empty, PublicationJobSupersession.ProfileIdOf("not json"));
        Assert.Equal(Guid.Empty, PublicationJobSupersession.ProfileIdOf("[]"));
    }
}
