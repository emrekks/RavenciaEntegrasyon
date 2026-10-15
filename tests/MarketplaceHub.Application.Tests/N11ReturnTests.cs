using System.Xml.Linq;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class N11ReturnTests
{
    private static XElement ListResponse(string claims, int pageCount = 1) => XElement.Parse($$"""
        <ns3:ClaimReturnListResponse xmlns:ns3="http://www.n11.com/ws/schemas">
          <result><status>success</status></result>
          <pagingData><currentPage>0</currentPage><pageSize>20</pageSize><totalCount>2</totalCount><pageCount>{{pageCount}}</pageCount></pagingData>
          <claimReturnList>{{claims}}</claimReturnList>
        </ns3:ClaimReturnListResponse>
        """);

    private const string WaitingClaim = """
        <claimReturn><claimReturnId>98765</claimReturnId><status>APPROVAL_WAITING</status><returnReasonType>DEFECTIVE</returnReasonType>
          <returnReasonDescription>Ürün kusurlu</returnReasonDescription><orderNumber>205412345678</orderNumber><requestDate>2026-10-10</requestDate>
          <productId>4455667788</productId><quantity>2</quantity><shipmentCompany>Yurtiçi Kargo</shipmentCompany><trackingNumber>YK123</trackingNumber></claimReturn>
        """;

    [Fact]
    public void ClaimsMapToTheSharedReturnModel()
    {
        var page = N11ReturnMapper.Page(ListResponse(WaitingClaim), 0);

        var claim = Assert.Single(page.Items);
        Assert.Equal("98765", claim.ExternalClaimId);
        Assert.Equal("205412345678", claim.ExternalOrderId);
        Assert.Equal(ReturnClaimStatus.ActionRequired, MarketplaceReturnStatus.Canonicalize(claim.RawStatus));
        Assert.Equal("Ürün kusurlu", claim.ReasonText);
        Assert.Equal("YK123", claim.CargoTrackingNumber);
        Assert.Equal(2m, Assert.Single(claim.Lines).Quantity);
        Assert.Contains("4455667788", claim.RawJson);
        Assert.False(page.HasMore);
    }

    [Theory]
    [InlineData("REQUESTED", ReturnClaimStatus.Requested)]
    [InlineData("PENDING", ReturnClaimStatus.ActionRequired)]
    [InlineData("APPROVED", ReturnClaimStatus.Approved)]
    [InlineData("DENIED", ReturnClaimStatus.Rejected)]
    [InlineData("CANCELLED", ReturnClaimStatus.Cancelled)]
    [InlineData("MANUAL_REFUND", ReturnClaimStatus.Completed)]
    public void N11StatesReachTheSharedReturnStates(string n11Status, ReturnClaimStatus expected)
    {
        Assert.Equal(expected, MarketplaceReturnStatus.Canonicalize(N11ReturnMapper.CanonicalStatus(n11Status)));
    }

    [Fact]
    public void FurtherPagesAreFollowedUntilThePageCount()
    {
        var page = N11ReturnMapper.Page(ListResponse(WaitingClaim, pageCount: 3), 1);

        Assert.True(page.HasMore);
        Assert.Equal("2", page.NextCursor);
    }

    [Fact]
    public void ListRequestUsesTurkeyDatesAndAllStates()
    {
        var body = N11HttpClient.ClaimReturnListBody(new DateTimeOffset(2026, 9, 1, 22, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero), 0);

        Assert.Contains("<status>ALL</status>", body);
        Assert.Contains("<startDate>02/09/2026</startDate>", body);
        Assert.Contains("<endDate>15/10/2026</endDate>", body);
        Assert.Contains("<currentPage>0</currentPage>", body);
    }

    [Theory]
    [InlineData("""{"orderLineId":1,"productId":"4455667788"}""", "4455667788")]
    [InlineData("""{"orderLineId":1,"productId":4455667788}""", "4455667788")]
    [InlineData("""{"orderLineId":1}""", null)]
    public void OrderLineSnapshotsExposeTheN11Product(string snapshot, string? productId)
    {
        Assert.Equal(productId, MarketplaceJobProcessor.N11LineProductId(snapshot));
    }
}
