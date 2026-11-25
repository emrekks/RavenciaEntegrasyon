using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceLifecycleAuditTests
{
    [Theory]
    [InlineData(InvoiceStatus.Accepted)]
    [InlineData(InvoiceStatus.MarketplacePending)]
    [InlineData(InvoiceStatus.Completed)]
    [InlineData(InvoiceStatus.MarketplaceFailed)]
    public void Issued_sale_invoice_can_require_an_adjustment(InvoiceStatus status) =>
        Assert.True(InvoiceStateMachine.CanTransition(status, InvoiceStatus.AdjustmentRequired));

    // Scan A-2: the owner may cancel an issued invoice in review; no background job may (BackgroundMayMove), and the
    // review offers it only for an issued e-Archive with a cancelled order or an open adjustment notice.
    [Fact]
    public void Invoice_in_review_is_cancelled_only_by_the_owner_and_only_when_issued_and_its_order_is_gone()
    {
        Assert.True(InvoiceStateMachine.CanTransition(InvoiceStatus.ManualReview, InvoiceStatus.CancellationPending));
        Assert.False(InvoiceDeliveryGuardPolicy.BackgroundMayMove(InvoiceStatus.ManualReview, InvoiceStatus.CancellationPending));
        Assert.True(InvoiceCancellationOutcomePolicy.CanRequestFromReview(InvoiceStatus.ManualReview, "EARSIVFATURA", "remote-1", orderCancelled: true, adjustmentNoticeOpen: false));
        Assert.True(InvoiceCancellationOutcomePolicy.CanRequestFromReview(InvoiceStatus.ManualReview, "EARSIVFATURA", "remote-1", orderCancelled: false, adjustmentNoticeOpen: true));
        Assert.False(InvoiceCancellationOutcomePolicy.CanRequestFromReview(InvoiceStatus.ManualReview, "EARSIVFATURA", "remote-1", orderCancelled: false, adjustmentNoticeOpen: false));
        Assert.False(InvoiceCancellationOutcomePolicy.CanRequestFromReview(InvoiceStatus.ManualReview, "EARSIVFATURA", null, orderCancelled: true, adjustmentNoticeOpen: true));
        Assert.False(InvoiceCancellationOutcomePolicy.CanRequestFromReview(InvoiceStatus.ManualReview, "TEMELFATURA", "remote-1", orderCancelled: true, adjustmentNoticeOpen: true));
        Assert.False(InvoiceCancellationOutcomePolicy.CanRequestFromReview(InvoiceStatus.MarketplaceFailed, "EARSIVFATURA", "remote-1", orderCancelled: true, adjustmentNoticeOpen: true));
    }

    [Fact]
    public void Invoice_requiring_an_adjustment_can_be_cancelled_or_reviewed_but_not_re_completed()
    {
        Assert.True(InvoiceStateMachine.CanTransition(InvoiceStatus.AdjustmentRequired, InvoiceStatus.CancellationPending));
        Assert.True(InvoiceStateMachine.CanTransition(InvoiceStatus.AdjustmentRequired, InvoiceStatus.ManualReview));
        Assert.False(InvoiceStateMachine.CanTransition(InvoiceStatus.AdjustmentRequired, InvoiceStatus.Completed));
        Assert.False(InvoiceStateMachine.CanTransition(InvoiceStatus.Draft, InvoiceStatus.AdjustmentRequired));
    }

    [Fact]
    public void Issuing_deadline_follows_the_first_delivered_package()
    {
        var first = DateTimeOffset.Parse("2026-10-10T08:00:00Z");
        var due = InvoicingBillingService.InvoiceIssueDueAt([
            (ShipmentPackageStatus.Delivered, first.AddDays(2)),
            (ShipmentPackageStatus.Delivered, first),
            (ShipmentPackageStatus.Shipped, first.AddDays(-5))
        ]);

        Assert.Equal(first.AddDays(DashboardMetricPolicy.InvoiceDueDays), due);
        Assert.Null(InvoicingBillingService.InvoiceIssueDueAt([(ShipmentPackageStatus.Shipped, first)]));
    }

    [Fact]
    public void Invoice_detail_shows_its_own_fiscal_number_before_the_marketplace_one()
    {
        Assert.Equal("GIB2026000000001", InvoicingBillingService.DetailInvoiceNumber("GIB2026000000001", "TY-OTHER"));
        Assert.Equal("TY-OTHER", InvoicingBillingService.DetailInvoiceNumber(" ", "TY-OTHER"));
        Assert.Null(InvoicingBillingService.DetailInvoiceNumber(null, null));
    }

    [Fact]
    public void Discounted_invoice_reconciles_without_a_false_total_equation_difference()
    {
        // TaxExclusiveTotal is already net of the discount (InvoiceAmounts.TryCalculatePackage).
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            InvoiceType = "EARSIVFATURA",
            SequencePurpose = "SALE",
            Currency = "TRY",
            Note = string.Empty,
            IdempotencyKey = "reconcile",
            TaxExclusiveTotal = 180m,
            DiscountTotal = 20m,
            TaxTotal = 27m,
            PayableTotal = 207m
        };
        var lines = new[]
        {
            new InvoiceLine { DescriptionSnapshot = "A", UnitSnapshot = "ADET", LineTotal = 99m },
            new InvoiceLine { DescriptionSnapshot = "B", UnitSnapshot = "ADET", LineTotal = 108m }
        };

        Assert.Empty(InvoiceTotalsReconciliationPolicy.LocalDifferences(invoice, lines));

        invoice.TaxTotal = 30m;
        Assert.Equal("TOTAL_EQUATION", Assert.Single(InvoiceTotalsReconciliationPolicy.LocalDifferences(invoice, lines)).FieldName);
    }
}
