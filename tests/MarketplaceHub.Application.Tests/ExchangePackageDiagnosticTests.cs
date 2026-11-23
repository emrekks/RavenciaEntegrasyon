using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner, 18 Nov 2026: the one-time read of an exchange package says only whether each answer holds the package by its id.
public sealed class ExchangePackageDiagnosticTests
{
    [Theory]
    [InlineData("""{ "content": [ { "shipmentPackageId": 2000000001, "lines": [] } ] }""", true)]
    [InlineData("""{ "content": [ { "id": "2000000001" } ] }""", true)]
    [InlineData("""{ "content": [ { "replacementOutboundpackageinfo": { "packageid": 2000000001 } } ] }""", true)]
    [InlineData("""{ "content": [ { "shipmentPackageId": 2000000000, "orderNumber": "2000000001" } ] }""", false)]
    [InlineData("""{ "content": [] }""", false)]
    [InlineData("not json", false)]
    [InlineData(null, false)]
    public void An_answer_holds_the_package_only_by_its_id(string? body, bool holds) =>
        Assert.Equal(holds, ExchangePackageDiagnostic.Holds(body, "2000000001"));
}
