using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceInvoiceLinkRevocationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("TRENDYOL", MarketplaceInvoiceLinkRevocationPolicy.Automatic)]
    [InlineData("trendyol", MarketplaceInvoiceLinkRevocationPolicy.Automatic)]
    [InlineData("HEPSIBURADA", MarketplaceInvoiceLinkRevocationPolicy.VerifyOnly)]
    [InlineData("N11", MarketplaceInvoiceLinkRevocationPolicy.Manual)]
    [InlineData("PAZARAMA", MarketplaceInvoiceLinkRevocationPolicy.Manual)]
    [InlineData("SHOPIFY", MarketplaceInvoiceLinkRevocationPolicy.Manual)]
    [InlineData(null, MarketplaceInvoiceLinkRevocationPolicy.Manual)]
    public void OnlyTrendyolRemovesTheLinkAndOnlyHepsiburadaCanBeCheckedAfterAManualRemoval(string? platform, string mode) =>
        Assert.Equal(mode, MarketplaceInvoiceLinkRevocationPolicy.ModeFor(platform));

    [Fact]
    public void ARunningRemovalWaitsUntilItIsStaleWhileRequiredAndUnknownStatesCanStartAtOnce()
    {
        Assert.True(MarketplaceInvoiceLinkRevocationPolicy.CanStart(MarketplaceInvoiceLinkRevocationPolicy.Required, Now, Now));
        Assert.True(MarketplaceInvoiceLinkRevocationPolicy.CanStart(MarketplaceInvoiceLinkRevocationPolicy.Unknown, Now, Now));
        Assert.False(MarketplaceInvoiceLinkRevocationPolicy.CanStart(MarketplaceInvoiceLinkRevocationPolicy.Started, Now.AddMinutes(-1), Now));
        Assert.True(MarketplaceInvoiceLinkRevocationPolicy.CanStart(MarketplaceInvoiceLinkRevocationPolicy.Started, Now - MarketplaceInvoiceLinkRevocationPolicy.StaleStart, Now));
        Assert.False(MarketplaceInvoiceLinkRevocationPolicy.CanStart(MarketplaceInvoiceLinkRevocationPolicy.Removed, Now.AddDays(-1), Now));
        Assert.False(MarketplaceInvoiceLinkRevocationPolicy.CanStart("CONFIRMED", Now.AddDays(-1), Now));
        Assert.False(MarketplaceInvoiceLinkRevocationPolicy.IsOpen(MarketplaceInvoiceLinkRevocationPolicy.Removed));
    }

    [Fact]
    public void OnlyTransientAnswersAndAPendingReadbackLeaveTheOutcomeUnknown()
    {
        static AdapterError Error(AdapterErrorClass errorClass, string code = "X") => new(errorClass, code, "m", null, null, null);

        Assert.True(MarketplaceInvoiceLinkRevocationPolicy.LeavesOutcomeUnknown(Error(AdapterErrorClass.TransientNetwork)));
        Assert.True(MarketplaceInvoiceLinkRevocationPolicy.LeavesOutcomeUnknown(Error(AdapterErrorClass.Remote5xx)));
        Assert.True(MarketplaceInvoiceLinkRevocationPolicy.LeavesOutcomeUnknown(Error(AdapterErrorClass.RateLimit)));
        Assert.True(MarketplaceInvoiceLinkRevocationPolicy.LeavesOutcomeUnknown(Error(AdapterErrorClass.BusinessConflict, MarketplaceInvoiceLinkRevocationPolicy.ReadbackPendingCode)));
        Assert.False(MarketplaceInvoiceLinkRevocationPolicy.LeavesOutcomeUnknown(Error(AdapterErrorClass.BusinessConflict, MarketplaceInvoiceLinkRevocationPolicy.NotOursCode)));
        Assert.False(MarketplaceInvoiceLinkRevocationPolicy.LeavesOutcomeUnknown(Error(AdapterErrorClass.Validation)));
        Assert.False(MarketplaceInvoiceLinkRevocationPolicy.LeavesOutcomeUnknown(Error(AdapterErrorClass.NotSupported)));
    }

    [Fact]
    public void EveryMarketplaceGetsPlainInstructionsNamingThePackage()
    {
        Assert.Contains("4052072376", MarketplaceInvoiceLinkRevocationPolicy.Instructions("TRENDYOL", "4052072376"), StringComparison.Ordinal);
        Assert.Contains("“Kaldırıldığını Kontrol Et”", MarketplaceInvoiceLinkRevocationPolicy.Instructions("HEPSIBURADA", "HB-PKG-1"), StringComparison.Ordinal);
        Assert.Contains("HB-PKG-1", MarketplaceInvoiceLinkRevocationPolicy.Instructions("HEPSIBURADA", "HB-PKG-1"), StringComparison.Ordinal);
        Assert.Contains("yerine geçer", MarketplaceInvoiceLinkRevocationPolicy.Instructions("N11", null), StringComparison.Ordinal);
        Assert.Contains("Pazarama", MarketplaceInvoiceLinkRevocationPolicy.Instructions("PAZARAMA", null), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(MarketplaceInvoiceLinkRevocationPolicy.Instructions("SHOPIFY", null)));
    }

    [Fact]
    public async Task AMarketplaceWithoutADeleteEndpointAnswersUnsupportedWithoutAnyRequest()
    {
        IInvoiceMarketplacePort port = new LinkOnlyMarketplace();

        var result = await port.RevokeLinkAsync(new AdapterContext(Guid.NewGuid(), Guid.NewGuid(), "c", "k", Now), new(new("PKG-1"), [], null), CancellationToken.None);

        Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.UnsupportedCode, result.Error?.Code);
        Assert.Equal(AdapterErrorClass.NotSupported, result.Error?.Class);
    }

    private sealed class LinkOnlyMarketplace : IInvoiceMarketplacePort
    {
        public Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
