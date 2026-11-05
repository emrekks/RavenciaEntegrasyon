using System.Xml.Linq;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.N11;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Report items 19 (catalog import brand/status) and 8 (return decision scope, order-level partial approval, n11 deny).
public sealed class ImportBrandAndReturnScopeTests
{
    [Fact]
    public void ImportedBrand_comes_only_from_one_user_mapping()
    {
        var mapped = Guid.NewGuid();
        Assert.Null(ImportedBrandRule.Resolve([]));
        Assert.Equal(mapped, ImportedBrandRule.Resolve([mapped, mapped]));
        // Two panel brands mapped to one marketplace brand: the user's to settle, the import picks none.
        Assert.Null(ImportedBrandRule.Resolve([mapped, Guid.NewGuid()]));
    }

    [Fact]
    public void ImportedBrand_without_a_mapping_leaves_a_new_product_without_brand_and_keeps_an_existing_one()
    {
        var current = Guid.NewGuid();
        var mapped = Guid.NewGuid();
        Assert.Null(ImportedBrandRule.NextProductBrand(isNewProduct: true, currentBrandId: null, mappedBrandId: null));
        Assert.Equal(current, ImportedBrandRule.NextProductBrand(isNewProduct: false, currentBrandId: current, mappedBrandId: null));
        Assert.Equal(mapped, ImportedBrandRule.NextProductBrand(isNewProduct: false, currentBrandId: current, mappedBrandId: mapped));
        Assert.Equal(mapped, ImportedBrandRule.NextProductBrand(isNewProduct: true, currentBrandId: null, mappedBrandId: mapped));
    }

    [Fact]
    public void Unmapped_brand_issue_carries_the_id_in_its_key_and_the_name_in_its_summary()
    {
        var connectionId = Guid.NewGuid();
        var key = ImportedBrandRule.IssueKey(connectionId, " 1234 ");
        Assert.StartsWith(ImportedBrandRule.IssueKeyPrefix(connectionId), key);
        Assert.Equal("1234", key[ImportedBrandRule.IssueKeyPrefix(connectionId).Length..]);
        Assert.Equal("Ravencia Moda", ImportedBrandRule.NameFromSummary(ImportedBrandRule.IssueSummary("Trendyol", "1234", " Ravencia Moda ")));
        Assert.Null(ImportedBrandRule.NameFromSummary(ImportedBrandRule.IssueSummary("Trendyol", "1234", null)));
    }

    [Theory]
    [InlineData("TRENDYOL", false)]
    [InlineData("HEPSIBURADA", true)]
    [InlineData("N11", false)]
    [InlineData("PAZARAMA", false)]
    [InlineData("PTTAVM", false)]
    [InlineData("SHOPIFY", true)]
    [InlineData("shopify", true)]
    [InlineData(null, false)]
    public void Decision_scope_is_the_whole_claim_on_hepsiburada_and_shopify(string? platformCode, bool wholeClaim) =>
        Assert.Equal(wholeClaim, ReturnDecisionScope.IsWholeClaim(platformCode));

    private static (ReturnClaimStatus, IReadOnlyCollection<string?>) Claim(ReturnClaimStatus status, params string?[] lines) => (status, lines);

    // n11: one product per claim; an order with one claim approved and another rejected is approved in part.
    [Fact]
    public void Order_level_partial_approval_n11() =>
        Assert.True(OrderReturnPartialApprovalPolicy.IsPartiallyApproved([Claim(ReturnClaimStatus.Approved, "Accepted"), Claim(ReturnClaimStatus.Rejected, "Rejected")]));

    // Pazarama: the same rule, with a completed (refunded) claim beside a rejected one.
    [Fact]
    public void Order_level_partial_approval_pazarama() =>
        Assert.True(OrderReturnPartialApprovalPolicy.IsPartiallyApproved([Claim(ReturnClaimStatus.Completed), Claim(ReturnClaimStatus.Rejected)]));

    // Trendyol: a single claim whose lines were decided both ways already counts for its order.
    [Fact]
    public void Order_level_partial_approval_trendyol() =>
        Assert.True(OrderReturnPartialApprovalPolicy.IsPartiallyApproved([Claim(ReturnClaimStatus.Approved, "Accepted", "Rejected")]));

    [Fact]
    public void Order_is_not_partially_approved_while_a_claim_waits_or_all_agree()
    {
        Assert.False(OrderReturnPartialApprovalPolicy.IsPartiallyApproved([Claim(ReturnClaimStatus.Approved), Claim(ReturnClaimStatus.ActionRequired)]));
        Assert.False(OrderReturnPartialApprovalPolicy.IsPartiallyApproved([Claim(ReturnClaimStatus.Approved), Claim(ReturnClaimStatus.Completed)]));
        Assert.False(OrderReturnPartialApprovalPolicy.IsPartiallyApproved([Claim(ReturnClaimStatus.Rejected), Claim(ReturnClaimStatus.Cancelled)]));
        Assert.False(OrderReturnPartialApprovalPolicy.IsPartiallyApproved([]));
    }

    [Fact]
    public void N11_deny_needs_reason_note_and_for_other_reasons_the_custom_shipment()
    {
        Assert.NotNull(N11ReturnDenyRule.Problem(null, "not", "1", "T1"));
        Assert.NotNull(N11ReturnDenyRule.Problem("21", " ", "1", "T1"));
        Assert.NotNull(N11ReturnDenyRule.Problem("21", "not", null, "T1"));
        Assert.NotNull(N11ReturnDenyRule.Problem("21", "not", "1", ""));
        Assert.Null(N11ReturnDenyRule.Problem("21", "not", "1", "T1"));
        // Reason 41: n11 uses the buyer's tracking number; nothing of the seller's is sent.
        Assert.Null(N11ReturnDenyRule.Problem("41", "not", null, null));
        Assert.NotNull(N11ReturnDenyRule.Problem("41", "not", "1", "T1"));
    }

    [Fact]
    public void N11_deny_body_sends_custom_shipment_and_never_campaign()
    {
        var body = N11HttpClient.ClaimReturnDenyBody(new ReturnActionCommand("123456789", [], "REJECT", "21", "Ürün kullanılmış", [], ReturnShipment: new("7", "TRK-1")));
        var xml = XElement.Parse($"<r>{body}</r>");
        Assert.Equal("123456789", xml.Element("claimReturnId")!.Value);
        Assert.Equal("21", xml.Element("denyReasonId")!.Value);
        Assert.Equal("Ürün kullanılmış", xml.Element("denyReasonNote")!.Value);
        Assert.Equal("CUSTOM", xml.Element("returnShipmentType")!.Value);
        Assert.Equal("7", xml.Element("shipmentCompanyId")!.Value);
        Assert.Equal("TRK-1", xml.Element("trackingNumber")!.Value);
        Assert.DoesNotContain("CAMPAIGN", body, StringComparison.Ordinal);

        var notReceived = XElement.Parse($"<r>{N11HttpClient.ClaimReturnDenyBody(new ReturnActionCommand("1", [], "REJECT", "41", "Ulaşmadı", [], ReturnShipment: new("7", "TRK-1")))}</r>");
        Assert.Equal("", notReceived.Element("returnShipmentType")!.Value);
        Assert.Equal("", notReceived.Element("shipmentCompanyId")!.Value);
        Assert.Equal("", notReceived.Element("trackingNumber")!.Value);
    }

    [Fact]
    public void N11_shipment_companies_are_read_from_id_and_name_elements()
    {
        var response = XElement.Parse("""<GetShipmentCompaniesResponse><result><status>success</status></result><shipmentCompanies><shipmentCompany><id>2</id><name>Yurtiçi Kargo</name><shortName>YK</shortName></shipmentCompany><shipmentCompany><id>1</id><name>Aras Kargo</name></shipmentCompany><shipmentCompany><id>3</id><shortName>MNG</shortName></shipmentCompany></shipmentCompanies></GetShipmentCompaniesResponse>""");
        var companies = N11HttpClient.ShipmentCompanies(response);
        Assert.Equal([("1", "Aras Kargo"), ("3", "MNG"), ("2", "Yurtiçi Kargo")], companies.Select(x => (x.Id, x.Name)).ToArray());
    }
}
