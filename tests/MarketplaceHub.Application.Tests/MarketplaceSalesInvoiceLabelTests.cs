using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceSalesInvoiceLabelTests
{
    [Theory]
    [InlineData("INVOICED")]
    [InlineData("Invoiced")]
    public void InvoicedPackageStatusIsPresentedAsIssuedInvoice(string rawStatus)
    {
        var label = MarketplaceSalesService.InvoiceLabel(null, "{}", [rawStatus]);

        Assert.Equal("FATURA_KESILDI", label);
    }

    [Fact]
    public void MissingRemoteInvoiceStatusIsPresentedAsUnknownInsteadOfWaiting()
    {
        var label = MarketplaceSalesService.InvoiceLabel(null, "{}", ["Delivered"]);

        Assert.Equal("FATURA_BILINMIYOR", label);
    }

    [Fact]
    public void ReturnWithoutInvoiceEvidenceIsPresentedAsWaiting()
    {
        var label = MarketplaceSalesService.ReturnInvoiceLabel(null, MarketplaceInvoiceStatus.Unknown, "{}", ["Delivered"]);

        Assert.Equal("FATURA_BEKLIYOR", label);
    }

    [Fact]
    public void ReturnWithInvoicedPackageEvidenceRemainsIssued()
    {
        var label = MarketplaceSalesService.ReturnInvoiceLabel(null, MarketplaceInvoiceStatus.Unknown, "{}", ["Invoiced"]);

        Assert.Equal("FATURA_KESILDI", label);
    }

    [Fact]
    public void LocalProviderRejectionWinsOverInvoicedMarketplaceSnapshot()
    {
        var label = MarketplaceSalesService.InvoiceLabel(new Invoice { Status = InvoiceStatus.Rejected, InvoiceType = "EARSIV", SequencePurpose = "MANUAL", Currency = "TRY", Note = string.Empty, IdempotencyKey = "test" }, MarketplaceInvoiceStatus.Invoiced, "{}", []);

        Assert.Equal("FATURA_REDDEDILDI", label);
    }

    [Fact]
    public void ShopifyDraftInvoiceIsPresentedAsWaiting()
    {
        var invoice = new Invoice { Status = InvoiceStatus.Draft, InvoiceType = "EARSIV", SequencePurpose = "MANUAL", Currency = "TRY", Note = string.Empty, IdempotencyKey = "test" };

        var label = MarketplaceSalesService.InvoiceLabelForPlatform(invoice, MarketplaceInvoiceStatus.Invoiced, "{}", [], "SHOPIFY");

        Assert.Equal("FATURA_BEKLIYOR", label);
    }

    [Fact]
    public void ShopifyCompletedManualInvoiceIsPresentedAsUploaded()
    {
        var invoice = new Invoice { Status = InvoiceStatus.Completed, InvoiceType = "EARSIV", SequencePurpose = "MANUAL", Currency = "TRY", Note = string.Empty, IdempotencyKey = "test" };

        var label = MarketplaceSalesService.InvoiceLabelForPlatform(invoice, MarketplaceInvoiceStatus.Unknown, "{}", [], "SHOPIFY");

        Assert.Equal("FATURA_YUKLENDI", label);
    }

    [Fact]
    public void ShopifyDisplayPackageSkipsSyntheticRemainderAndPrefersTrackingNumber()
    {
        var synthetic = new ShipmentPackage { ExternalPackageId = "order:6297409945684:remainder", RawStatus = "Cancelled", Status = ShipmentPackageStatus.Cancelled, StatusOccurredAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        var untracked = new ShipmentPackage { ExternalPackageId = "fulfillment-1", RawStatus = "Created", Status = ShipmentPackageStatus.New, StatusOccurredAt = DateTimeOffset.UtcNow.AddMinutes(1) };
        var tracked = new ShipmentPackage { ExternalPackageId = "fulfillment-2", RawStatus = "Shipped", Status = ShipmentPackageStatus.Shipped, CargoTrackingNumber = "TRK-2", StatusOccurredAt = DateTimeOffset.UtcNow };

        var selected = MarketplaceSalesService.SelectDisplayPackage([synthetic, untracked, tracked], "SHOPIFY");

        Assert.Same(tracked, selected);
    }
}
