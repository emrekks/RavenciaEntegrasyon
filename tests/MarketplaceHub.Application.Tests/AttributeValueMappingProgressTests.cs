using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The category window reads every field's value progress in one request instead of one value list per field.
public sealed class AttributeValueMappingProgressTests
{
    [Fact]
    public void GroupsTheCategorysValueScopesByFieldAndCountsAValueOnce()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var progress = ReferenceDataService.GroupValueMappingProgress("1182", [
            ("1182/179", a), ("1182/179", b), ("1182/179", a),
            ("1182/338", c),
            ("11820/179", c),
            ("1182", a),
            ("1182/", b),
            ("2000/179", a)
        ]);

        Assert.Equal(["179", "338"], progress.Keys.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal([a, b], progress["179"]);
        Assert.Equal([c], progress["338"]);
    }
}
