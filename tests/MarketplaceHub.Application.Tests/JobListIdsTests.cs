using MarketplaceHub.Api.Operations;
using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The panel follows the jobs one press started (Toplu Yayın) with /jobs?ids=…: only ids are read, at most 200, and an
// ids list with nothing readable never turns into the whole list.
public sealed class JobListIdsTests
{
    [Fact]
    public void Ids_are_read_from_a_comma_list_and_capped()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.Equal([first, second], JobEndpoints.Ids($"{first}, nonsense ,{second}"));
        Assert.Null(JobEndpoints.Ids(null));
        Assert.Empty(JobEndpoints.Ids("nonsense")!);
        var many = string.Join(',', Enumerable.Range(0, 300).Select(_ => Guid.NewGuid()));
        Assert.Equal(JobListQuery.MaximumIds, JobEndpoints.Ids(many)!.Count);
    }
}
