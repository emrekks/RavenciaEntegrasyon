using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class N11OrderMapperTests
{
    // Shape and values follow the GetShipmentPackages example in the n11 documentation.
    private const string DocumentedPage = """
        {"pageCount":1,"totalPages":1,"page":0,"size":100,"content":[{
          "billingAddress":{"address":"Reşitpaşa Mah Arı 3 D:8-9","city":"İstanbul","district":"Sarıyer","neighborhood":"Reşitpaşa","fullName":"N11 müşteri","gsm":"5xxxxxxxxx","postalCode":"34000","invoiceType":1},
          "shippingAddress":{"address":"Reşitpaşa Mah Arı 3 D:8-9","city":"İstanbul","district":"Sarıyer","neighborhood":"Reşitpaşa","fullName":"N11 müşteri","gsm":"5xxxxxxxxx","postalCode":"34000"},
          "orderNumber":"203872347637","id":"112999455244259","customerEmail":"n11@n11.com","customerfullName":"N11 müşteri","customerId":12345678,
          "cargoSenderNumber":null,"cargoTrackingNumber":"112999455244259","shipmentCompanyId":342,"cargoProviderName":"MNG Kargo","shipmentMethod":1,
          "lines":[
            {"quantity":2,"productId":123456789,"productName":"Erkek Spor Ayakkabı Bordo 45","stockCode":"20242024","price":292.8,"sellerInvoiceAmount":579.8,"totalSellerDiscountPrice":5.8,"orderLineId":415490391,"orderItemLineItemStatusName":"Picking","vatRate":10,"barcode":"8690000000001"},
            {"quantity":3,"productId":123456799,"productName":" Erkek Klasik Ayakkabı Kahverengi 42","stockCode":"202420241","price":250,"sellerInvoiceAmount":750,"totalSellerDiscountPrice":0,"orderLineId":415490390,"orderItemLineItemStatusName":"Picking","vatRate":10}
          ],
          "lastModifiedDate":1724323386203,"agreedDeliveryDate":1725310828346,"totalAmount":1479.79,
          "packageHistories":[{"createdDate":1724274054460,"status":"Created"},{"createdDate":1724274255751,"status":"Picking"},{"createdDate":1724396400000,"status":" Delivered"}],
          "shipmentPackageStatus":" Delivered","sellerId":9876543}]}
        """;

    [Fact]
    public void DocumentedPackageMapsToAnOrderWithLinesAmountsAndDates()
    {
        var page = N11OrderMapper.Packages(DocumentedPage);

        var order = Assert.Single(page.Items);
        Assert.False(page.HasMore);
        Assert.Empty(page.Issues!);
        Assert.Equal("203872347637", order.ExternalOrderId);
        Assert.Equal("TRY", order.Currency);
        Assert.Equal(1335.6m, order.GrossAmount);
        Assert.Equal(1329.8m, order.NetAmount);
        Assert.Equal(5.8m, order.DiscountAmount);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1724274054460), order.OrderedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1724323386203), order.LastModifiedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1725310828346), order.ShipmentDueAt);

        var package = Assert.Single(order.Packages);
        Assert.Equal("112999455244259", package.ExternalPackageId);
        Assert.Equal("Delivered", package.RawStatus);
        Assert.Equal("MNG Kargo", package.CargoProviderExternalId);
        Assert.Equal("112999455244259", package.CargoTrackingNumber);
        Assert.Equal(new[] { "415490391", "415490390" }, package.Allocations.Select(x => x.ExternalLineId).ToArray());

        var line = order.Lines[0];
        Assert.Equal("20242024", line.Sku);
        Assert.Equal("8690000000001", line.Barcode);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(292.8m, line.UnitPrice);
        Assert.Equal(10, line.VatRate);
        Assert.Equal("Erkek Klasik Ayakkabı Kahverengi 42", order.Lines[1].Title);
    }

    [Fact]
    public void CustomerAndAddressSnapshotsUseTheSharedFieldNames()
    {
        var order = N11OrderMapper.Packages(DocumentedPage).Items.Single();

        using var customer = JsonDocument.Parse(order.CustomerSnapshotJson);
        Assert.Equal("N11 müşteri", customer.RootElement.GetProperty("customerName").GetString());
        Assert.Equal("N11", customer.RootElement.GetProperty("customerFirstName").GetString());
        Assert.Equal("müşteri", customer.RootElement.GetProperty("customerLastName").GetString());
        Assert.Equal("n11@n11.com", customer.RootElement.GetProperty("email").GetString());
        Assert.False(customer.RootElement.TryGetProperty("lines", out _));

        using var shipping = JsonDocument.Parse(order.ShipmentAddressSnapshotJson);
        Assert.Equal("Reşitpaşa Mah Arı 3 D:8-9", shipping.RootElement.GetProperty("fullAddress").GetString());
        Assert.Equal("Sarıyer", shipping.RootElement.GetProperty("district").GetString());
        Assert.Equal("5xxxxxxxxx", shipping.RootElement.GetProperty("phone").GetString());
        using var billing = JsonDocument.Parse(order.InvoiceAddressSnapshotJson);
        Assert.Equal(1, billing.RootElement.GetProperty("invoiceType").GetInt32());
    }

    [Fact]
    public void SplitPackagesReplaceTheUnpackedOriginalInTheOrderMerge()
    {
        const string page = """
            {"totalPages":1,"page":0,"content":[
              {"orderNumber":"500","id":"P0","shipmentPackageStatus":"UnPacked","lastModifiedDate":1000,"lines":[{"orderLineId":1,"quantity":3,"price":10,"stockCode":"A"}]},
              {"orderNumber":"500","id":"P1","shipmentPackageStatus":"Picking","lastModifiedDate":1000,"lines":[{"orderLineId":1,"quantity":2,"price":10,"stockCode":"A"}]},
              {"orderNumber":"500","id":"P2","shipmentPackageStatus":"Picking","lastModifiedDate":1000,"lines":[{"orderLineId":2,"quantity":1,"price":10,"stockCode":"A"}]}
            ]}
            """;

        var rows = N11OrderMapper.Packages(page).Items;
        var merged = TrendyolJsonMapper.MergeOrderPackages(rows, "500")!;

        Assert.Equal("P0", rows.Single(x => x.Packages[0].ExternalPackageId == "P1").Packages[0].OriginExternalPackageId);
        Assert.Null(rows.Single(x => x.Packages[0].ExternalPackageId == "P0").Packages[0].OriginExternalPackageId);
        Assert.Equal(2, merged.Lines.Single(x => x.ExternalLineId == "1").Quantity);
        Assert.Equal(1, merged.Lines.Single(x => x.ExternalLineId == "2").Quantity);
    }

    [Fact]
    public void PagesContinueUntilTheLastPageAndBadPackagesBecomeIssues()
    {
        const string page = """
            {"totalPages":3,"page":1,"content":[
              {"orderNumber":"600","id":"P9","shipmentPackageStatus":"Created","lastModifiedDate":1000,"lines":[]},
              {"id":"P8","shipmentPackageStatus":"Created","lines":[]}
            ]}
            """;

        var result = N11OrderMapper.Packages(page);

        Assert.Empty(result.Items);
        Assert.True(result.HasMore);
        Assert.Equal("2", result.NextCursor);
        Assert.Equal(2, result.Issues!.Count);
    }

    [Fact]
    public void LocationDeliveryWithoutPackageNumberUsesTheCampaignCode()
    {
        const string page = """
            {"totalPages":1,"page":0,"content":[{"orderNumber":"700","id":null,"cargoTrackingNumber":"KMP-1","shipmentPackageStatus":"Created","lastModifiedDate":1000,"lines":[{"orderLineId":5,"quantity":1,"price":20,"stockCode":"B"}]}]}
            """;

        Assert.Equal("cargo:KMP-1", N11OrderMapper.Packages(page).Items.Single().Packages[0].ExternalPackageId);
    }

    [Fact]
    public void HistoryWindowsNeverExceedFifteenDaysOrStartBeforeNovember2024()
    {
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        var start = N11OrderHistoryPolicy.Start(full: true, watermark: null, TimeSpan.FromMinutes(2), now);
        var windows = N11OrderHistoryPolicy.Windows(start, now);

        Assert.Equal(now.AddMonths(-3), start);
        Assert.All(windows, window => Assert.True(window.End - window.Start <= TimeSpan.FromDays(15)));
        Assert.Equal(start, windows[0].Start);
        Assert.Equal(now, windows[^1].End);
        Assert.Equal(N11OrderHistoryPolicy.EarliestAvailable, N11OrderHistoryPolicy.Start(true, null, TimeSpan.Zero, new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(now.AddMinutes(-12), N11OrderHistoryPolicy.Start(false, now.AddMinutes(-10), TimeSpan.FromMinutes(2), now));
    }

    [Fact]
    public void N11ActivationReadsCategoriesAndOrdersWithN11JobTypes()
    {
        var plan = MarketplaceHub.Infrastructure.Persistence.MarketplaceConnectionService.CreateInitialDataSyncPlan("N11", Guid.NewGuid(), includeHepsiburadaCatalog: true);

        Assert.Equal(new[] { MarketplaceJobTypes.N11ReferenceSync, MarketplaceJobTypes.N11OrderRecoverySync }, plan.Select(x => x.JobType).ToArray());
        Assert.Equal(MarketplaceJobTypes.N11OrderSync, MarketplaceJobTypes.ForPlatform("N11", MarketplaceJobTypes.OrderSync));
        Assert.Equal(MarketplaceJobTypes.ReturnSync, MarketplaceJobTypes.ForPlatform("N11", MarketplaceJobTypes.ReturnSync));
        Assert.True(MarketplaceHub.Infrastructure.Persistence.ScheduledJobProducer.SupportsN11ScheduledPolicy("ORDERS"));
        Assert.False(MarketplaceHub.Infrastructure.Persistence.ScheduledJobProducer.SupportsN11ScheduledPolicy("RETURNS"));
        // Cross-channel stock reaches n11 through scheduled stock reconciliation.
        Assert.True(MarketplaceHub.Infrastructure.Persistence.ScheduledJobProducer.SupportsN11ScheduledPolicy("STOCK_RECONCILE_SHORT"));
        Assert.True(MarketplaceHub.Infrastructure.Persistence.ScheduledJobProducer.SupportsN11ScheduledPolicy("STOCK_RECONCILE_DAILY"));
        // No n11 returns integration yet, so the return write switch is not offered.
        Assert.False(MarketplaceHub.Infrastructure.Persistence.ScheduledJobProducer.SupportsN11ScheduledPolicy(MarketplaceExternalWritePolicies.Return));
    }

    [Fact]
    public void OrderEndpointsFilterByModificationAndOrderNumber()
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(1000);
        var end = DateTimeOffset.FromUnixTimeMilliseconds(2000);

        Assert.Equal("rest/delivery/v1/shipmentPackages?startDate=1000&endDate=2000&page=0&size=100&orderByField=true&orderByDirection=ASC", N11HttpClient.ModifiedShipmentPackages(start, end, 0, 100));
        Assert.Equal("rest/delivery/v1/shipmentPackages?orderNumber=203%2F1&page=0&size=100", N11HttpClient.OrderNumberPackages("203/1"));
    }
}
