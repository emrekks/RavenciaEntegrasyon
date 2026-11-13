using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The owner's invoice and return rules of 5 Nov 2026 that need no database.
public sealed class OwnerRulesNov5Tests
{
    private static readonly Guid LineA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid LineB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    // Rule 1 (T2-Y3): only a return of every invoiced unit adjusts the invoice; a partial one is a note.
    [Fact]
    public void APartialReturnIsANoteAndOnlyAFullReturnAdjustsTheInvoice()
    {
        var invoiced = new Dictionary<Guid, decimal> { [LineA] = 1, [LineB] = 2 };
        Assert.Equal(InvoiceReturnScope.None, InvoiceReturnAdjustmentPolicy.Classify(invoiced, new Dictionary<Guid, decimal>()));
        Assert.Equal(InvoiceReturnScope.Partial, InvoiceReturnAdjustmentPolicy.Classify(invoiced, new Dictionary<Guid, decimal> { [LineA] = 1 }));
        Assert.Equal(InvoiceReturnScope.Partial, InvoiceReturnAdjustmentPolicy.Classify(invoiced, new Dictionary<Guid, decimal> { [LineA] = 1, [LineB] = 1 }));
        Assert.Equal(InvoiceReturnScope.Full, InvoiceReturnAdjustmentPolicy.Classify(invoiced, new Dictionary<Guid, decimal> { [LineA] = 1, [LineB] = 2 }));
        // An invoice line that names no order line can never be proven returned: partial at most.
        Assert.Equal(InvoiceReturnScope.Partial, InvoiceReturnAdjustmentPolicy.Classify(invoiced, new Dictionary<Guid, decimal> { [LineA] = 1, [LineB] = 2 }, hasUnmappedLines: true));
        // A returned line that is not on the invoice does not count.
        Assert.Equal(InvoiceReturnScope.None, InvoiceReturnAdjustmentPolicy.Classify(invoiced, new Dictionary<Guid, decimal> { [Guid.NewGuid()] = 5 }));
    }

    // Scan D-1 (13 Nov 2026): units cancelled after issuing count with the returned ones, never past the invoice.
    [Fact]
    public void AnInvoiceLineLosesItsUnitsCancelledAfterIssuingAndItsReturnedOnes()
    {
        // 3 invoiced, 1 cancelled since: 1 unit off the invoice.
        Assert.Equal(1m, InvoiceReturnAdjustmentPolicy.RemovedUnits(3, 3, 1, 0));
        Assert.Equal(1m, InvoiceReturnAdjustmentPolicy.CancelledAfterIssue(3, 3, 1));
        // Invoiced after the cancellation (2 of 3): nothing cancelled after issuing.
        Assert.Equal(0m, InvoiceReturnAdjustmentPolicy.RemovedUnits(2, 3, 1, 0));
        // 1 cancelled and 1 of the kept 2 returned: 2 off; never more than the invoice holds.
        Assert.Equal(2m, InvoiceReturnAdjustmentPolicy.RemovedUnits(3, 3, 1, 1));
        Assert.Equal(3m, InvoiceReturnAdjustmentPolicy.RemovedUnits(3, 3, 3, 3));
        Assert.Equal(0m, InvoiceReturnAdjustmentPolicy.RemovedUnits(0, 3, 0, 3));
    }

    // Scan D-2: a provider reference other than the UUID sent is a mismatch; the same UUID in another case, or no
    // reference at all, is not.
    [Fact]
    public void AProviderReferenceOtherThanTheSentUuidIsAMismatch()
    {
        var sent = InvoiceProviderIdentityPolicy.UuidFor(LineA).ToString("D");
        Assert.True(InvoiceProviderIdentityPolicy.Matches(sent, sent.ToUpperInvariant()));
        Assert.True(InvoiceProviderIdentityPolicy.Matches(sent, null));
        Assert.False(InvoiceProviderIdentityPolicy.Matches(sent, Guid.NewGuid().ToString("D")));
        Assert.False(InvoiceProviderIdentityPolicy.Matches(sent, "GIB2026000000001"));
    }

    // Rule 2 (T2-D27): A approved, B rejected ends "Kısmen Onaylandı", never Rejected.
    [Fact]
    public void AClaimDecidedBothWaysIsApprovedInPart()
    {
        Assert.Equal(ReturnClaimStatus.Approved, ReturnClaimDecisionPolicy.StatusAfterLineDecision(ReturnClaimStatus.ActionRequired, "REJECT", 1, 1, ["Accepted"]));
        Assert.Equal(ReturnClaimStatus.Rejected, ReturnClaimDecisionPolicy.StatusAfterLineDecision(ReturnClaimStatus.ActionRequired, "REJECT", 1, 1, ["Rejected"]));
        Assert.Equal(ReturnClaimStatus.Rejected, ReturnClaimDecisionPolicy.StatusAfterLineDecision(ReturnClaimStatus.ActionRequired, "REJECT", 2, 2));
        Assert.Equal(ReturnClaimStatus.Approved, ReturnClaimDecisionPolicy.StatusAfterLineDecision(ReturnClaimStatus.ActionRequired, "APPROVE", 1, 1, ["Rejected"]));
        Assert.Equal(ReturnClaimStatus.ActionRequired, ReturnClaimDecisionPolicy.StatusAfterLineDecision(ReturnClaimStatus.ActionRequired, "REJECT", 1, 2, ["Accepted"]));

        // The scheduled read: Trendyol reports the claim as its first item (Rejected here).
        Assert.Equal(ReturnClaimStatus.Approved, ReturnLineStatusPolicy.Resolve(ReturnClaimStatus.Rejected, ["Rejected", "Accepted"]));
        Assert.Equal(ReturnClaimStatus.Completed, ReturnLineStatusPolicy.Resolve(ReturnClaimStatus.Completed, ["Rejected", "Accepted"]));
        Assert.Equal(ReturnClaimStatus.ActionRequired, ReturnLineStatusPolicy.Resolve(ReturnClaimStatus.Rejected, ["WaitingInAction", "Accepted"]));
        Assert.Equal(ReturnClaimStatus.Rejected, ReturnLineStatusPolicy.Resolve(ReturnClaimStatus.Rejected, ["Rejected", "Rejected"]));

        Assert.True(ReturnClaimPartialApprovalPolicy.IsPartiallyApproved(ReturnClaimStatus.Approved, ["Accepted", "Rejected"]));
        Assert.False(ReturnClaimPartialApprovalPolicy.IsPartiallyApproved(ReturnClaimStatus.Approved, ["Accepted", "Accepted"]));
        Assert.False(ReturnClaimPartialApprovalPolicy.IsPartiallyApproved(ReturnClaimStatus.Rejected, ["Accepted", "Rejected"]));
        Assert.Equal("PARTIALLY_APPROVED", MarketplaceSalesService.ReturnStatusWire(ReturnClaimStatus.Approved, ["Accepted", "Rejected"]));
        Assert.Equal("APPROVED", MarketplaceSalesService.ReturnStatusWire(ReturnClaimStatus.Approved, ["Accepted", null]));
        Assert.Equal("APPROVED", MarketplaceSalesService.ReturnTabGroup("PARTIALLY_APPROVED"));
        // Its approved line counts as returned for the invoice; the rejected one does not.
        Assert.True(ReturnDecisionLinePolicy.StockDispositionAllowed(ReturnClaimStatus.Approved, "Accepted"));
        Assert.False(ReturnDecisionLinePolicy.StockDispositionAllowed(ReturnClaimStatus.Approved, "Rejected"));
    }

    // Rule 3 (O3): the last check before issuing names what changed since the approval.
    [Fact]
    public void TheLastCheckBeforeIssuingFindsAChangedAmountOrLine()
    {
        var invoice = new Invoice { InvoiceType = "EARSIVFATURA", SequencePurpose = "SALE", Currency = "TRY", Note = "", IdempotencyKey = "k", TaxExclusiveTotal = 100m, TaxTotal = 20m, PayableTotal = 120m };
        var lines = new List<InvoiceLine> { new() { OrderLineId = LineA, DescriptionSnapshot = "Gömlek", UnitSnapshot = "ADET", Quantity = 2, UnitPrice = 60m, VatRate = 20m, VatAmount = 20m, LineTotal = 120m } };
        var same = new InvoicePackageCalculation([new(LineA, "Gömlek", null, 2, 60m, 0m, 20m, 20m, 120m)], 100m, 0m, 20m, 120m);
        Assert.Null(InvoicePreIssueCheck.Describe(invoice, lines, same));

        var oneCancelled = new InvoicePackageCalculation([new(LineA, "Gömlek", null, 1, 60m, 0m, 20m, 10m, 60m)], 50m, 0m, 10m, 60m);
        Assert.Contains("60,00", InvoicePreIssueCheck.Describe(invoice, lines, oneCancelled), StringComparison.Ordinal);

        var otherVat = new InvoicePackageCalculation([new(LineA, "Gömlek", null, 2, 60m, 0m, 10m, 10.91m, 120m)], 100m, 0m, 20m, 120m);
        Assert.Contains("KDV", InvoicePreIssueCheck.Describe(invoice, lines, otherVat), StringComparison.Ordinal);

        Assert.True(InvoicingBillingService.CanRetryPreProviderFailure(InvoiceStatus.ManualReview, InvoiceIssuePolicy.PreIssueChangedCode, null));
        Assert.Contains("yeniden onaylayın", InvoiceIssuePolicy.DescribeValidationCode(InvoiceIssuePolicy.PreIssueChangedCode), StringComparison.Ordinal);
    }

    // Rule 6 (T2-O1): the Hepsiburada customer's own request preselects Para İadesi / Değişim.
    [Theory]
    [InlineData("Return", false, ReturnCustomerRequestPolicy.Refund)]
    [InlineData("return", false, ReturnCustomerRequestPolicy.Refund)]
    [InlineData("Change", false, ReturnCustomerRequestPolicy.Change)]
    [InlineData("RenewProduct", false, ReturnCustomerRequestPolicy.Change)]
    [InlineData("Return", true, ReturnCustomerRequestPolicy.Change)]
    [InlineData("MissingPart", false, null)]
    [InlineData(null, false, null)]
    public void TheCustomersRequestIsReadFromTheClaim(string? claimType, bool hasRequestedProduct, string? expected)
    {
        Assert.Equal(expected, ReturnCustomerRequestPolicy.FromHepsiburada(claimType, hasRequestedProduct));
        Assert.Equal(expected switch { ReturnCustomerRequestPolicy.Refund => "Refund", ReturnCustomerRequestPolicy.Change => "Change", _ => null },
            ReturnCustomerRequestPolicy.FinalizedWith(expected));
    }

    // Decided by how the claim closed, not by what was requested (owner, 6 Nov 2026).
    [Theory]
    [InlineData(ReturnCustomerRequestPolicy.Change, "Refunded", true)]
    [InlineData(ReturnCustomerRequestPolicy.Change, "refunded", true)]
    [InlineData(ReturnCustomerRequestPolicy.Change, "Accepted", false)]
    [InlineData(ReturnCustomerRequestPolicy.Change, "AwaitingAction", false)]
    [InlineData(ReturnCustomerRequestPolicy.Change, null, false)]
    [InlineData(ReturnCustomerRequestPolicy.Refund, "Accepted", true)]
    [InlineData(null, "Accepted", true)]
    public void AnExchangeClosedWithARefundCountsAsAReturn(string? request, string? rawStatus, bool counts) =>
        Assert.Equal(counts, ReturnCustomerRequestPolicy.CountsAsReturn(request, rawStatus));

    [Fact]
    public void AHepsiburadaExchangeClaimCarriesTheCustomersRequest()
    {
        const string claim = """
        {
          "claimNumber": "356189794", "type": "Return", "quantity": 1, "status": "AwaitingAction",
          "orderNumber": "2152988946", "claimDate": "2021-10-14T11:03:17.676Z", "orderDate": "2021-10-14T11:00:20Z",
          "line": { "lineItemId": "61680dc6", "productName": "Gömlek", "merchantSku": "KIVIRCIK20", "price": { "currency": "TRY", "amount": 56.0 } },
          "requestedProduct": { "sku": "GYGEO11009AM", "changeOption": "WithDifferentVariant" }
        }
        """;
        using var json = JsonDocument.Parse(claim);
        Assert.Equal(ReturnCustomerRequestPolicy.Change, HepsiburadaJsonMapper.ReturnClaim(json.RootElement).CustomerRequest);
        using var refund = JsonDocument.Parse(claim.Replace("\"requestedProduct\": { \"sku\": \"GYGEO11009AM\", \"changeOption\": \"WithDifferentVariant\" }", "\"requestedProduct\": null", StringComparison.Ordinal));
        Assert.Equal(ReturnCustomerRequestPolicy.Refund, HepsiburadaJsonMapper.ReturnClaim(refund.RootElement).CustomerRequest);
    }

    // Rule 8 (DO3): a line without invoice information makes the order's invoice state unknown, never NOT_INVOICED.
    [Fact]
    public void AHepsiburadaLineWithoutInvoiceInformationLeavesTheStateUnknown()
    {
        using var gap = JsonDocument.Parse("""{"lineItems":[{"id":"l-1","hasInvoice":false},{"id":"l-2"}]}""");
        Assert.Null(HepsiburadaJsonMapper.InvoiceStatus(gap.RootElement, out var missing));
        Assert.True(missing);

        using var known = JsonDocument.Parse("""{"lineItems":[{"id":"l-1","hasInvoice":false},{"id":"l-2","hasInvoice":false}]}""");
        Assert.Equal("NOT_INVOICED", HepsiburadaJsonMapper.InvoiceStatus(known.RootElement, out missing));
        Assert.False(missing);

        // Lines that carry no invoice field at all say nothing; the package list decides.
        using var packages = JsonDocument.Parse("""{"lineItems":[{"id":"l-1"}],"packages":[{"packageNumber":"p-1","hasInvoice":false}]}""");
        Assert.Equal("NOT_INVOICED", HepsiburadaJsonMapper.InvoiceStatus(packages.RootElement, out missing));
        Assert.False(missing);

        // One package's items, read on their own.
        using var items = JsonDocument.Parse("""{"items":[{"id":"l-1","packageNumber":"p-1","hasInvoice":false},{"id":"l-2","packageNumber":"p-1"},{"id":"l-3","packageNumber":"p-2","hasInvoice":true}]}""");
        Assert.Null(HepsiburadaJsonMapper.InvoiceObservationForPackage(items.RootElement, "p-1", out missing));
        Assert.True(missing);
        Assert.Equal("INVOICED", HepsiburadaJsonMapper.InvoiceObservationForPackage(items.RootElement, "p-2", out missing)!.RawStatus);
        Assert.False(missing);
    }

    [Fact]
    public void AnIssuedInvoiceStoppedForMissingHepsiburadaInformationIsSentAgainByTheOwner()
    {
        Assert.True(InvoiceMarketplaceRetryPolicy.IsDeliveryReview(InvoiceStatus.ManualReview, HepsiburadaInvoiceInfoPolicy.LineInvoiceInfoMissingCode));
        Assert.True(InvoiceMarketplaceRetryPolicy.CanRetryDelivery(InvoiceStatus.ManualReview, HepsiburadaInvoiceInfoPolicy.LineInvoiceInfoMissingCode));
        Assert.False(InvoiceMarketplaceRetryPolicy.IsOwnerApprovedRetry(HepsiburadaInvoiceInfoPolicy.LineInvoiceInfoMissingCode));
    }
}
