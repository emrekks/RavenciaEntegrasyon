using System.Xml.Linq;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Jobs.Handlers;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class N11SettlementTests
{
    // Shaped as https://api.n11.com/ws/SettlementService.wsdl types the answers.
    private const string List = """
        <GetSettlementListResponse xmlns="http://www.n11.com/ws/schemas"><result><status>success</status></result>
          <settlementListData><settlementList><paymentAmount>1200.50</paymentAmount><deductionAmount>200.10</deductionAmount><settlementAmount>1000.40</settlementAmount>
            <settlementDate>05/11/2026</settlementDate><remittanceDate>06/11/2026</remittanceDate><status>ODEME_BEKLENIYOR</status></settlementList>
            <settlementList><paymentAmount>10</paymentAmount><deductionAmount>0</deductionAmount><settlementAmount>10</settlementAmount><settlementDate>2026-10-29</settlementDate></settlementList>
            <metadata><currentPage>0</currentPage><pageSize>100</pageSize><totalCount>2</totalCount><pageCount>1</pageCount></metadata></settlementListData>
        </GetSettlementListResponse>
        """;

    private const string Detail = """
        <GetSettlementDetailResponse xmlns="http://www.n11.com/ws/schemas"><result><status>success</status></result>
          <settlementDetail><settlement><salesAmount>1200.50</salesAmount><status>ODEME_BEKLENIYOR</status></settlement>
            <orderItemSettlementDataList><orderItemSettlements><orderItemSettlement>
              <adFee>5</adFee><adFeeVat>1</adFeeVat><buyerEmail>alici@example.com</buyerEmail><buyerName>Ad Soyad</buyerName><commission>150.25</commission>
              <completedDate>01/11/2026 14:20</completedDate><deductionAmount>200.10</deductionAmount><orderItemId>998877</orderItemId><orderNumber>205512345678</orderNumber>
              <paymentAmount>1200.50</paymentAmount><paymentDate>06/11/2026</paymentDate><remittanceDate>06/11/2026</remittanceDate><sellerCouponDiscount>0</sellerCouponDiscount>
              <sellerDiscount>10</sellerDiscount><settlementAmount>1000.40</settlementAmount><settlementDate>05/11/2026</settlementDate><serviceFee>20</serviceFee><serviceFeeVat>4</serviceFeeVat>
              <cargoPrice>9.85</cargoPrice></orderItemSettlement></orderItemSettlements>
              <metadata><currentPage>0</currentPage><pageSize>100</pageSize><totalCount>1</totalCount><pageCount>1</pageCount></metadata></orderItemSettlementDataList>
          </settlementDetail>
        </GetSettlementDetailResponse>
        """;

    [Fact]
    public void A_settlement_list_gives_each_days_totals_and_dates()
    {
        var page = N11SettlementMapper.Days(XElement.Parse(List), 0);
        Assert.Equal(1, page.PageCount);
        Assert.Equal(2, page.Items.Count);
        var day = page.Items[0];
        Assert.Equal(new DateOnly(2026, 11, 5), day.SettlementDate);
        Assert.Equal(new DateOnly(2026, 11, 6), day.RemittanceDate);
        Assert.Equal(1000.40m, day.SettlementAmount);
        Assert.Equal("ODEME_BEKLENIYOR", day.Status);
        Assert.Equal(new DateOnly(2026, 10, 29), page.Items[1].SettlementDate);
        Assert.Null(page.Items[1].RemittanceDate);
    }

    [Fact]
    public void A_days_lines_keep_the_money_and_dates_and_never_the_buyer()
    {
        var page = N11SettlementMapper.Items(XElement.Parse(Detail), 0);
        Assert.Equal("ODEME_BEKLENIYOR", page.Status);
        var line = Assert.Single(page.Items);
        Assert.Equal("998877", line.OrderItemId);
        Assert.Equal("205512345678", line.OrderNumber);
        Assert.Equal(150.25m, line.Commission);
        Assert.Equal(24m, line.ServiceFee);
        Assert.Equal(6m, line.AdFee);
        Assert.Equal(10m, line.Discount);
        Assert.Equal(1000.40m, line.SettlementAmount);
        Assert.Equal(new DateOnly(2026, 11, 6), line.RemittanceDate);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 11, 20, 0, TimeSpan.Zero), line.CompletedAt);
        Assert.DoesNotContain("alici@example.com", line.RawJson);
        Assert.DoesNotContain("Ad Soyad", line.RawJson);
        Assert.Contains("\"orderItemId\":\"998877\"", line.RawJson);
        Assert.Equal("2026-11-05", N11HttpClient.SettlementDate(new DateOnly(2026, 11, 5)));
        Assert.Equal("998877:20261105", N11SettlementSyncJobHandler.ExternalId("998877", new DateOnly(2026, 11, 5)));
    }

    [Theory]
    // A word that says paid or waiting decides; any other goes by the remittance day (today 4 Nov 2026).
    [InlineData("ODENDI", "2026-11-06", PayoutBuilder.Paid)]
    [InlineData("ODEME_BEKLENIYOR", "2026-11-01", PayoutBuilder.Expected)]
    [InlineData("TAMAM", "2026-11-01", PayoutBuilder.Paid)]
    [InlineData("TAMAM", "2026-11-06", PayoutBuilder.Expected)]
    public void An_n11_line_is_paid_by_its_word_or_its_remittance_day(string status, string remitted, string expected)
    {
        var row = new PayoutRow
        {
            ConnectionId = Guid.CreateVersion7(),
            PlatformCode = "N11",
            Source = N11SettlementSyncJobHandler.Source,
            ExternalId = "1:20261105",
            RequestedType = N11SettlementSyncJobHandler.RequestedType,
            TransactionType = status,
            Category = MarketplaceFinanceCategories.Sale,
            Credit = 1200.50m,
            Debt = 200.10m,
            SellerRevenue = 1000.40m,
            PaymentDate = PayoutBuilder.DayStart(DateOnly.Parse(remitted, System.Globalization.CultureInfo.InvariantCulture))
        };
        Assert.Equal(expected, PayoutBuilder.Status(row, new DateOnly(2026, 11, 4)));
        Assert.Equal(1000.40m, PayoutBuilder.Net(row));
    }
}
