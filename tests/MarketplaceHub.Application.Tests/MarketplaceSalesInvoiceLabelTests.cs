using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceSalesInvoiceLabelTests
{
    [Theory]
    [InlineData("INVOICED")]
    [InlineData("Invoiced")]
    public void InvoicedPackageStatusWithoutMatchedLocalInvoiceIsPresentedForReview(string rawStatus)
    {
        var label = MarketplaceSalesService.InvoiceLabel(null, "{}", [rawStatus]);

        Assert.Equal("FATURA_KONTROLDE", label);
    }

    [Fact]
    public void MissingRemoteInvoiceStatusIsPresentedAsUnknownInsteadOfWaiting()
    {
        var label = MarketplaceSalesService.InvoiceLabel(null, "{}", ["Delivered"]);

        Assert.Equal("FATURA_BILINMIYOR", label);
    }

    [Theory]
    [InlineData("INVOICED", "FATURA_KONTROLDE")]
    [InlineData("NOT_INVOICED", "FATURA_BEKLIYOR")]
    public void HepsiburadaOrderDetailInvoiceEvidenceIsPresented(string marketplaceStatus, string expected)
    {
        var label = MarketplaceSalesService.InvoiceLabelForPlatform(null, MarketplaceInvoiceStatus.Unknown,
            $$"""{"marketplaceInvoiceStatus":"{{marketplaceStatus}}"}""", [], "HEPSIBURADA");

        Assert.Equal(expected, label);
    }

    // A seller-panel upload is invisible to the n11, PTT AVM and Pazarama APIs, so the panel's own record decides:
    // without a panel invoice the package waits for one (owner, 24 Oct and 31 Oct 2026).
    [Theory]
    [InlineData("N11")]
    [InlineData("PTTAVM")]
    [InlineData("PAZARAMA")]
    public void UnreportedPlatformOrdersWithoutAPanelInvoiceWaitForAnInvoice(string platformCode)
    {
        var label = MarketplaceSalesService.InvoiceLabelForPlatform(null, MarketplaceInvoiceStatus.Unknown, "{}", ["Delivered"], platformCode);

        Assert.Equal("FATURA_BEKLIYOR", label);
    }

    // Platforms that report the state keep an absent observation unknown.
    [Theory]
    [InlineData("TRENDYOL")]
    [InlineData("HEPSIBURADA")]
    public void ReportingPlatformOrderWithoutInvoiceEvidenceStaysUnknown(string platformCode) =>
        Assert.Equal("FATURA_BILINMIYOR", MarketplaceSalesService.InvoiceLabelForPlatform(null, MarketplaceInvoiceStatus.Unknown, "{}", ["Delivered"], platformCode));

    [Theory]
    [InlineData("PTTAVM", MarketplaceInvoiceStatus.Invoiced)]
    [InlineData("PTTAVM", MarketplaceInvoiceStatus.Received)]
    [InlineData("PAZARAMA", MarketplaceInvoiceStatus.Invoiced)]
    public void UnreportedPlatformOrderWithAnObservedInvoiceIsIssued(string platformCode, MarketplaceInvoiceStatus status) =>
        Assert.Equal("FATURA_KONTROLDE", MarketplaceSalesService.InvoiceLabelForPlatform(null, status, "{}", ["Delivered"], platformCode));

    // A completed panel invoice reads as issued; one still on its way or refused keeps its own state.
    [Theory]
    [InlineData(InvoiceStatus.Completed, "FATURA_KONTROLDE")]
    [InlineData(InvoiceStatus.MarketplacePending, "FATURA_ISLENIYOR")]
    [InlineData(InvoiceStatus.Rejected, "FATURA_REDDEDILDI")]
    public void PazaramaPanelInvoiceDecidesTheLabel(InvoiceStatus status, string expected) =>
        Assert.Equal(expected, MarketplaceSalesService.InvoiceLabelForPlatform(status, "GIB2026000000001", MarketplaceInvoiceStatus.Unknown, "{}", ["Delivered"], "PAZARAMA"));

    [Theory]
    [InlineData("FATURA_BEKLIYOR", true)]
    [InlineData("FATURA_KONTROLDE", false)]
    public void WorkspaceCountsAnUnreportedWaitingPackageAsUninvoiced(string label, bool canCreate) =>
        Assert.Equal(canCreate, InvoicingBillingService.IsWorkspaceInvoiceUninvoiced(label, canCreate, marketplaceInvoicePresent: label == "FATURA_KONTROLDE"));

    [Fact]
    public void N11OrderWithAnInvoiceLinkIsShownForReview()
    {
        var label = MarketplaceSalesService.InvoiceLabelForPlatform(null, MarketplaceInvoiceStatus.Unknown, """{"invoiceLink":"https://example.com/a.pdf","invoiceDate":"2026-10-08"}""", ["Delivered"], "N11");

        Assert.Equal("FATURA_KONTROLDE", label);
    }

    [Fact]
    public void N11OrderWithAnEmptyInvoiceLinkCannotBeVerified() =>
        Assert.Equal("FATURA_BEKLIYOR", MarketplaceSalesService.InvoiceLabelForPlatform(null, MarketplaceInvoiceStatus.Unknown, """{"invoiceLink":"","invoiceDate":""}""", ["Delivered"], "N11"));

    [Fact]
    public void ReturnWithoutInvoiceEvidenceIsPresentedAsUnknown()
    {
        var label = MarketplaceSalesService.ReturnInvoiceLabel(null, MarketplaceInvoiceStatus.Unknown, "{}", ["Delivered"]);

        Assert.Equal("FATURA_BILINMIYOR", label);
    }

    [Fact]
    public void ReturnWithRemoteInvoicedEvidenceWithoutMatchedLocalInvoiceRemainsForReview()
    {
        var label = MarketplaceSalesService.ReturnInvoiceLabel(null, MarketplaceInvoiceStatus.Unknown, "{}", ["Invoiced"]);

        Assert.Equal("FATURA_KONTROLDE", label);
    }

    [Theory]
    [InlineData(InvoiceStatus.Rejected)]
    [InlineData(InvoiceStatus.ValidationFailed)]
    [InlineData(InvoiceStatus.ManualReview)]
    public void LocalInvoiceFailureWinsOverInvoicedMarketplaceSnapshot(InvoiceStatus failureStatus)
    {
        var label = MarketplaceSalesService.InvoiceLabel(new Invoice { Status = failureStatus, InvoiceType = "EARSIV", SequencePurpose = "MANUAL", Currency = "TRY", Note = string.Empty, IdempotencyKey = "test" }, MarketplaceInvoiceStatus.Invoiced, "{}", []);

        Assert.Equal("FATURA_REDDEDILDI", label);
    }

    [Fact]
    public void CompletedFiscalInvoiceWithoutMarketplaceDeliveryProofStaysUnderReview()
    {
        var label = MarketplaceSalesService.InvoiceLabel(
            InvoiceStatus.Completed,
            "INV-1",
            MarketplaceInvoiceStatus.Invoiced,
            "{}",
            [],
            marketplaceDeliveryConfirmed: false);

        Assert.Equal("FATURA_KONTROLDE", label);
    }

    [Fact]
    public void CompletedFiscalInvoiceWithConfirmedMarketplaceDeliveryIsIssued()
    {
        var label = MarketplaceSalesService.InvoiceLabel(
            InvoiceStatus.Completed,
            "INV-1",
            MarketplaceInvoiceStatus.Unknown,
            "{}",
            [],
            marketplaceDeliveryConfirmed: true);

        Assert.Equal("FATURA_KONTROLDE", label);
    }

    [Fact]
    public void MarketplaceDeliveryFailureRemainsRetryableInsteadOfLookingLikeFiscalRejection()
    {
        var label = MarketplaceSalesService.InvoiceLabel(
            new Invoice { Status = InvoiceStatus.MarketplaceFailed, InvoiceType = "EARSIV", SequencePurpose = "SALE", Currency = "TRY", Note = string.Empty, IdempotencyKey = "test" },
            MarketplaceInvoiceStatus.Invoiced,
            "{}",
            []);

        Assert.Equal("FATURA_PLATFORMA_AKTARILMADI", label);
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

        Assert.Equal("FATURA_KONTROLDE", label);
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
