using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class OpenOrderReadTallyTests
{
    private static AdapterError Error(AdapterErrorClass @class, string code = "X") => new(@class, code, code, null, null, null);

    [Theory]
    [InlineData(AdapterErrorClass.TransientNetwork)]
    [InlineData(AdapterErrorClass.Remote5xx)]
    [InlineData(AdapterErrorClass.RateLimit)]
    [InlineData(AdapterErrorClass.Authentication)]
    public void NoAnswerAndOnlyPassingFailuresFailWithTheFirst(AdapterErrorClass @class)
    {
        var tally = new OpenOrderReadTally();
        tally.Failed(Error(@class, "FIRST"));
        tally.Failed(Error(AdapterErrorClass.Remote5xx, "SECOND"));

        Assert.Equal("FIRST", tally.NothingRead()!.Code);
        Assert.Equal(2, tally.Failures);
    }

    [Fact]
    public void OneAnswerMakesItAPartialSuccess()
    {
        var tally = new OpenOrderReadTally();
        tally.Failed(Error(AdapterErrorClass.Remote5xx));
        tally.Answered();

        Assert.Null(tally.NothingRead());
        Assert.Equal(1, tally.Failures);
    }

    [Theory]
    [InlineData(AdapterErrorClass.Validation)]
    [InlineData(AdapterErrorClass.ContractViolation)]
    public void AFailureThatIsTheScansOwnFindingKeepsItASuccess(AdapterErrorClass @class)
    {
        var tally = new OpenOrderReadTally();
        tally.Failed(Error(AdapterErrorClass.Remote5xx));
        tally.Failed(Error(@class));

        Assert.Null(tally.NothingRead());
    }

    [Fact]
    public void AScanThatAskedNothingIsNotAFailure() => Assert.Null(new OpenOrderReadTally().NothingRead());
}
