using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner's rule of 6 Nov 2026 for sale invoices not issued yet: returned units never go on the invoice, an order
// returned in full gets none, an open claim keeps its units on it until it is approved.
public sealed class OwnerRulesNov6SaleReturnTests
{
    [Theory]
    [InlineData(3, false, new double[] { 1 }, 1)]
    // The pieces of evidence count the same units: the largest counts, never their sum.
    [InlineData(3, false, new double[] { 1, 1, 0, 1 }, 1)]
    [InlineData(3, false, new double[] { 1, 2 }, 2)]
    // Never more than the units still ordered.
    [InlineData(2, false, new double[] { 5 }, 2)]
    // No evidence is no return: nothing is inferred from a missing field.
    [InlineData(3, false, new double[] { }, 0)]
    [InlineData(3, false, new double[] { 0, 0 }, 0)]
    // A returned order took every unit back.
    [InlineData(3, true, new double[] { }, 3)]
    // A fully cancelled line has nothing to return.
    [InlineData(0, true, new double[] { 1 }, 0)]
    public void ReturnedUnitsTakeTheLargestEvidenceUpToTheUnitsOrdered(double active, bool orderReturned, double[] evidence, double expected) =>
        Assert.Equal((decimal)expected, OrderLineReturnPolicy.ReturnedUnits((decimal)active, orderReturned, evidence.Select(value => (decimal)value).ToArray()));

    [Fact]
    public void AnOrderIsReturnedInFullOnlyWhenEveryUnitStillOrderedCameBack()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Equal(InvoiceSaleReturnScope.None, InvoiceSaleReturnPolicy.Classify([new(a, 100m, 3m, 0m), new(b, 50m, 1m, 0m)]));
        Assert.Equal(InvoiceSaleReturnScope.Partial, InvoiceSaleReturnPolicy.Classify([new(a, 100m, 3m, 1m), new(b, 50m, 1m, 0m)]));
        Assert.Equal(InvoiceSaleReturnScope.Partial, InvoiceSaleReturnPolicy.Classify([new(a, 100m, 3m, 3m), new(b, 50m, 1m, 0m)]));
        Assert.Equal(InvoiceSaleReturnScope.Full, InvoiceSaleReturnPolicy.Classify([new(a, 100m, 3m, 3m), new(b, 50m, 1m, 1m)]));
        // A cancelled line (nothing still ordered) neither makes nor breaks a full return.
        Assert.Equal(InvoiceSaleReturnScope.Full, InvoiceSaleReturnPolicy.Classify([new(a, 100m, 3m, 3m), new(b, 50m, 0m, 0m)]));
        Assert.Equal(InvoiceSaleReturnScope.None, InvoiceSaleReturnPolicy.Classify([new(a, 100m, 0m, 0m)]));
    }

    [Fact]
    public void APartlyReturnedOrderIsInvoicedForTheKeptUnitsAtTheirShareOfTheDiscount()
    {
        // Three items, one returned: a line of 3 at 100 with 1 back and a line of 1 at 50, order 350 less 35 discount.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        IReadOnlyCollection<InvoiceLineUnits> units = [new(a, 100m, 3m, 1m), new(b, 50m, 1m, 0m)];
        var kept = InvoiceSaleReturnPolicy.KeptTotals(units, new InvoiceOrderTotals(350m, 35m, 315m));

        Assert.Equal(new InvoiceOrderTotals(250m, 25m, 225m), kept);
        Assert.True(InvoiceAmounts.TryCalculatePackage(
            [new(a, "A", "A-SKU", 2m, 100m, 20m), new(b, "B", "B-SKU", 1m, 50m, 20m)],
            kept.GrossAmount, kept.DiscountAmount, kept.PayableAmount, out var calculation, out var error), error);
        Assert.Equal(225m, calculation!.PayableTotal);
        Assert.Equal([2m, 1m], calculation.Lines.Select(line => line.Quantity));
        // The kept lines carry exactly the discount the full invoice would have given them.
        Assert.True(InvoiceAmounts.TryCalculatePackage(
            [new(a, "A", "A-SKU", 3m, 100m, 20m), new(b, "B", "B-SKU", 1m, 50m, 20m)], 350m, 35m, 315m, out var full, out _));
        Assert.Equal(full!.Lines[1].PayableAmount, calculation.Lines[1].PayableAmount);
    }

    [Fact]
    public void TotalsAreKeptAsTheyAreWhenNothingCameBackOrTheyDoNotMatchTheLines()
    {
        var a = Guid.NewGuid();
        var totals = new InvoiceOrderTotals(300m, 0m, 300m);
        Assert.Same(totals, InvoiceSaleReturnPolicy.KeptTotals([new(a, 100m, 3m, 0m)], totals));
        // The marketplace already took the returned unit off its total (it matches the kept units): used as it is.
        var alreadyKept = new InvoiceOrderTotals(200m, 0m, 200m);
        Assert.Same(alreadyKept, InvoiceSaleReturnPolicy.KeptTotals([new(a, 100m, 3m, 1m)], alreadyKept));
        // Totals that match no lines stay unchanged, so the calculation refuses them as before.
        var mismatch = new InvoiceOrderTotals(999m, 0m, 999m);
        Assert.Same(mismatch, InvoiceSaleReturnPolicy.KeptTotals([new(a, 100m, 3m, 1m)], mismatch));
    }

    [Fact]
    public void TotalsWithoutAGrossAmountTakeTheLinesAsTheirGross()
    {
        var a = Guid.NewGuid();
        var kept = InvoiceSaleReturnPolicy.KeptTotals([new(a, 100m, 4m, 1m)], new InvoiceOrderTotals(0m, 0m, 360m));
        Assert.Equal(new InvoiceOrderTotals(300m, 30m, 270m), kept);
    }

    [Fact]
    public void TheFullyReturnedRefusalSaysWhyInTurkish()
    {
        Assert.Equal("INVOICE_ORDER_FULLY_RETURNED", InvoiceIssuePolicy.OrderFullyReturned.Code);
        Assert.Equal("Tüm ürünler iade edildi, fatura kesilmez.", InvoiceIssuePolicy.OrderFullyReturned.Message);
        Assert.Equal(InvoiceIssuePolicy.OrderFullyReturned.Message, InvoiceIssuePolicy.DescribeValidationCode(InvoiceSaleReturnPolicy.FullyReturnedCode));
    }

    // An open claim is no return: only an approved line or claim takes units off (ReturnDecisionLinePolicy).
    [Theory]
    [InlineData(ReturnClaimStatus.Requested, "Created", false)]
    [InlineData(ReturnClaimStatus.InTransit, null, false)]
    [InlineData(ReturnClaimStatus.ActionRequired, "WaitingInAction", false)]
    [InlineData(ReturnClaimStatus.Rejected, "Rejected", false)]
    [InlineData(ReturnClaimStatus.Approved, "Rejected", false)]
    [InlineData(ReturnClaimStatus.Approved, null, true)]
    [InlineData(ReturnClaimStatus.Completed, null, true)]
    [InlineData(ReturnClaimStatus.ActionRequired, "Accepted", true)]
    public void OnlyApprovedClaimLinesCountAsReturned(ReturnClaimStatus claim, string? line, bool returned) =>
        Assert.Equal(returned, ReturnDecisionLinePolicy.StockDispositionAllowed(claim, line));
}
