using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PackageQuantityIssueKeyTests
{
    private static readonly Guid Connection = Guid.Parse("01a05a69-86cd-7a57-af4f-d9d7276a5437");

    [Fact]
    public void APackageRefusalKeyNamesItsConnectionAndPackage()
    {
        Assert.Equal((Connection, "4084999185"), OperationalIssueRetentionService.ParsePackageQuantityIssueKey($"package-quantity:{Connection}:4084999185:1787028283965"));
        Assert.StartsWith(OperationalIssueRetentionService.PackageQuantityIssuePrefix(Connection, "4084999185"), $"package-quantity:{Connection}:4084999185:1787028283965", StringComparison.Ordinal);
    }

    [Theory]
    // An order-level refusal (PTT AVM) and other shapes name no package.
    [InlineData("package-quantity:01a15fda-b767-714d-ab0c-d40636b79aec:order:PTT-1C2OSLF2R-120926:1789229541000")]
    [InlineData("package-quantity:not-a-guid:4084999185:1787028283965")]
    [InlineData("package-quantity:01a05a69-86cd-7a57-af4f-d9d7276a5437:4084999185")]
    [InlineData("order-lines:01a05a69-86cd-7a57-af4f-d9d7276a5437:4084999185:1787028283965")]
    public void OtherKeysNameNoPackage(string key) => Assert.Null(OperationalIssueRetentionService.ParsePackageQuantityIssueKey(key));
}
