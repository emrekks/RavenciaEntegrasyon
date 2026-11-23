using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner, 18 Nov 2026: the one-time read of an exchange package says only whether each answer holds the package by its id.
public sealed class ExchangePackageDiagnosticTests
{
    [Theory]
    [InlineData("""{ "content": [ { "shipmentPackageId": 4360616611, "lines": [] } ] }""", true)]
    [InlineData("""{ "content": [ { "id": "4360616611" } ] }""", true)]
    [InlineData("""{ "content": [ { "replacementOutboundpackageinfo": { "packageid": 4360616611 } } ] }""", true)]
    [InlineData("""{ "content": [ { "shipmentPackageId": 4326271446, "orderNumber": "4360616611" } ] }""", false)]
    [InlineData("""{ "content": [] }""", false)]
    [InlineData("not json", false)]
    [InlineData(null, false)]
    public void An_answer_holds_the_package_only_by_its_id(string? body, bool holds) =>
        Assert.Equal(holds, ExchangePackageDiagnostic.Holds(body, "4360616611"));

    // Scan, 23 Nov 2026: a failed diagnostic read was asked again after every recovery read; it waits six hours now.
    [Fact]
    public void A_failed_read_waits_six_hours_and_a_read_that_succeeded_is_never_repeated()
    {
        var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        Assert.True(ExchangePackageDiagnostic.ShouldRead(null, null, now));
        Assert.False(ExchangePackageDiagnostic.ShouldRead(null, now.AddMinutes(-15), now));
        Assert.False(ExchangePackageDiagnostic.ShouldRead(null, now.AddHours(-5), now));
        Assert.True(ExchangePackageDiagnostic.ShouldRead(null, now.AddHours(-6), now));
        Assert.False(ExchangePackageDiagnostic.ShouldRead(now.AddDays(-1), null, now));
    }
}
