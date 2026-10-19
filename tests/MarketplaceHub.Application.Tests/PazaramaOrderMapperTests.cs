using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PazaramaOrderMapperTests
{
    private const string Money7 = """{"currency":"TL","value":7,"valueInt":700,"valueString":"7,00 TL"}""";

    private static string Item(string id, int status, string productCode = "testurunu001", string stockCode = "212", int quantity = 1, string extra = "") =>
        $$"""
        {"orderItemId":"{{id}}","orderItemStatus":{{status}},"orderItemStatusName":"x",
         "orderItemStatusHistory":[{"historyStatus":3,"historyCreatedDate":"2026-02-17T17:23:33.16+03:00"},{"historyStatus":12,"historyCreatedDate":"2026-02-17T17:24:54.865+03:00"}],
         "shipmentCode":"PZ00250056371","estimatedShippingDate":"2026-02-18T19:00:00+03:00","deliveryType":1,"quantity":{{quantity}},
         "listPrice":{"currency":"TL","value":9},"salePrice":{{Money7}},"totalPrice":{{Money7}},"discountAmount":{"currency":"TL","value":0},
         "cargo":{"companyName":"Aras Kargo","trackingNumber":"655675687","trackingUrl":"www.araskargo.com"},
         "product":{"productId":"2bd93964","name":"yeni ürün test ürünü ","imageURL":"https://img.pzrmcdn.com/a.png","variantOptionDisplay":"Siyah","stockCode":"{{stockCode}}","code":"{{productCode}}","vatRate":10}{{extra}}}
        """;

    private static string Order(string items, string number = "283534307") =>
        $$"""
        {"data":[{"orderId":"3099b7db-6a35-498d-a45a-4f6dd57a2ecb","orderNumber":{{number}},"orderDate":"2026-02-17 17:23","orderAmount":7,"currency":"TL","paymentType":1,
          "customerId":"d9a521ed","customerName":"Ayşe Nur Yılmaz","customerEmail":"ayse@example.com",
          "shipmentAddress":{"nameSurname":"Ayşe Nur Yılmaz","cityName":"Ankara","districtName":"Altındağ","neighborhoodName":"Karapürçek Mah","addressDetail":"306. sk no:4/9","displayAddressText":"Karapürçek Mah Altındağ/Ankara","phoneNumber":"05350000000","postalCode":"06360"},
          "billingAddress":{"nameSurname":"Ayşe Nur Yılmaz","cityName":"Ankara","identityNumber":"11111111110","invoiceType":1,"isEInvoiceObliged":false},
          "items":[{{items}}],"channelCodeName":"Pazarama"}]}
        """;

    private static RemoteOrder Single(string items, string number = "283534307")
    {
        using var json = JsonDocument.Parse(Order(items, number));
        return PazaramaOrderMapper.Page(json.RootElement, 100).Items.Single();
    }

    [Fact]
    public void AnOrderBecomesOneRemoteOrderWithOnePackage()
    {
        var order = Single(Item("a1", 3));

        Assert.Equal("283534307", order.ExternalOrderId);
        Assert.Equal("TRY", order.Currency);
        Assert.Equal(new DateTimeOffset(2026, 2, 17, 17, 23, 0, TimeSpan.FromHours(3)), order.OrderedAt);
        // PostgreSQL accepts only UTC offsets, so every instant of the order must be UTC.
        Assert.Equal(TimeSpan.Zero, order.OrderedAt.Offset);
        Assert.Equal(TimeSpan.Zero, order.LastModifiedAt.Offset);
        Assert.Equal(TimeSpan.Zero, order.ShipmentDueAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, order.Packages.Single().OccurredAt.Offset);
        Assert.Equal(7m, order.GrossAmount);
        Assert.Equal(7m, order.NetAmount);
        Assert.Equal(new DateTimeOffset(2026, 2, 18, 19, 0, 0, TimeSpan.FromHours(3)), order.ShipmentDueAt);
        var line = order.Lines.Single();
        Assert.Equal("a1", line.ExternalLineId);
        Assert.Equal("212", line.Sku);
        Assert.Equal("testurunu001", line.Barcode);
        Assert.Equal(7m, line.UnitPrice);
        Assert.Equal(10m, line.VatRate);
        Assert.Contains("https://img.pzrmcdn.com/a.png", line.SourceSnapshotJson);
        var package = Assert.Single(order.Packages);
        Assert.Equal("order:283534307", package.ExternalPackageId);
        Assert.Equal("Created", package.RawStatus);
        Assert.Equal("PZ00250056371", package.CargoTrackingNumber);
        Assert.Equal("Aras Kargo", package.CargoProviderExternalId);
    }

    [Theory]
    [InlineData(3, "Created", ShipmentPackageStatus.New)]
    [InlineData(12, "Picking", ShipmentPackageStatus.Processing)]
    [InlineData(18, "CancelRequested", ShipmentPackageStatus.OnHold)]
    [InlineData(5, "Shipped", ShipmentPackageStatus.Shipped)]
    [InlineData(16, "Shipped", ShipmentPackageStatus.Shipped)]
    [InlineData(19, "Shipped", ShipmentPackageStatus.Shipped)]
    [InlineData(11, "Delivered", ShipmentPackageStatus.Delivered)]
    [InlineData(14, "Undelivered", ShipmentPackageStatus.Undelivered)]
    [InlineData(6, "Cancelled", ShipmentPackageStatus.Cancelled)]
    [InlineData(13, "Cancelled", ShipmentPackageStatus.Cancelled)]
    [InlineData(10, "Returned", ShipmentPackageStatus.Returned)]
    public void ItemStatusesBecomeThePanelPackageStatuses(int code, string raw, ShipmentPackageStatus expected)
    {
        Assert.Equal(raw, PazaramaOrderMapper.ItemStatus(code));
        Assert.Equal(expected, TrendyolShipmentPackageStatusPolicy.FromRemote(PazaramaOrderMapper.PackageStatus([raw])));
    }

    [Fact]
    public void AnItemBeingCancelledHoldsThePackageItShares()
    {
        Assert.Equal("CancelRequested", PazaramaOrderMapper.PackageStatus(["Picking", "CancelRequested", "Created"]));
        Assert.Equal(ShipmentPackageStatus.OnHold, ShipmentPackageStatusPolicy.FromRemote("CancelRequested"));
    }

    [Fact]
    public void AnUnknownStatusCodeNeedsManualReview()
    {
        var raw = PazaramaOrderMapper.ItemStatus(99);

        Assert.Equal(ShipmentPackageStatus.ManualReview, TrendyolShipmentPackageStatusPolicy.FromRemote(raw));
    }

    [Fact]
    public void ACancelledItemDoesNotHideTheRestOfTheOrder()
    {
        var order = Single(Item("a1", 6) + "," + Item("a2", 12, "bbb", "213"));

        Assert.Equal("Picking", order.Packages.Single().RawStatus);
        var cancelled = order.Packages.Single().Allocations.Single(allocation => allocation.ExternalLineId == "a1");
        Assert.Equal(1m, cancelled.CancelledQuantity);
        Assert.Equal(1m, order.Lines.Single(line => line.ExternalLineId == "a1").CancelledQuantity);
    }

    [Fact]
    public void AllItemsCancelledCancelsThePackage() =>
        Assert.Equal("Cancelled", Single(Item("a1", 6) + "," + Item("a2", 13, "bbb", "213")).Packages.Single().RawStatus);

    [Fact]
    public void ThePackageFollowsTheFurthestActiveItem()
    {
        var order = Single(Item("a1", 5) + "," + Item("a2", 11, "bbb", "213"));

        Assert.Equal("Delivered", order.Packages.Single().RawStatus);
        var allocations = order.Packages.Single().Allocations;
        Assert.Equal(1m, allocations.Single(allocation => allocation.ExternalLineId == "a1").ShippedQuantity);
        Assert.Equal(0m, allocations.Single(allocation => allocation.ExternalLineId == "a1").DeliveredQuantity);
        Assert.Equal(1m, allocations.Single(allocation => allocation.ExternalLineId == "a2").DeliveredQuantity);
    }

    [Fact]
    public void QuantityAndPricesAreSummedAcrossItems()
    {
        var order = Single(Item("a1", 3, quantity: 2) + "," + Item("a2", 3, "bbb", "213"));

        Assert.Equal(21m, order.GrossAmount);
        Assert.Equal(2m, order.Lines.First().Quantity);
        Assert.Equal(2, order.Lines.Count);
    }

    [Fact]
    public void TheCustomerSnapshotCarriesNamesInvoiceIdentityAndThePazaramaOrderId()
    {
        var order = Single(Item("a1", 3));

        using var customer = JsonDocument.Parse(order.CustomerSnapshotJson);
        var root = customer.RootElement;
        Assert.Equal("Ayşe Nur", root.GetProperty("customerFirstName").GetString());
        Assert.Equal("Yılmaz", root.GetProperty("customerLastName").GetString());
        Assert.Equal("ayse@example.com", root.GetProperty("email").GetString());
        Assert.Equal("3099b7db-6a35-498d-a45a-4f6dd57a2ecb", root.GetProperty("pazaramaOrderId").GetString());
        Assert.Equal("11111111110", root.GetProperty("tcIdentityNumber").GetString());
        Assert.Equal("Ayşe Nur Yılmaz", MarketplaceSalesService.ResolveCustomerName(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson));
    }

    [Fact]
    public void TheShipmentAddressGetsTheFieldsThePanelReads()
    {
        var order = Single(Item("a1", 3));

        using var address = JsonDocument.Parse(order.ShipmentAddressSnapshotJson);
        var root = address.RootElement;
        Assert.Equal("306. sk no:4/9", root.GetProperty("address1").GetString());
        Assert.Equal("Karapürçek Mah Altındağ/Ankara", root.GetProperty("fullAddress").GetString());
        Assert.Equal("Ankara", root.GetProperty("city").GetString());
        Assert.Equal("05350000000", root.GetProperty("phone").GetString());
        Assert.Equal("Ayşe Nur", root.GetProperty("firstName").GetString());
    }

    [Fact]
    public void AnItemWithoutIdOrPriceIsReportedAndTheOrderIsSkippedWhenNothingIsLeft()
    {
        using var json = JsonDocument.Parse(Order("""{"orderItemStatus":3,"quantity":1}"""));

        var page = PazaramaOrderMapper.Page(json.RootElement, 100);

        Assert.Empty(page.Items);
        Assert.Equal(2, page.Issues!.Count);
    }

    [Fact]
    public void ADataEnvelopeAroundTheListIsAccepted()
    {
        using var inner = JsonDocument.Parse(Order(Item("a1", 3)));
        var list = inner.RootElement.GetProperty("data").GetRawText();
        using var json = JsonDocument.Parse("{\"data\":{\"orders\":" + list + "}}");

        Assert.Single(PazaramaOrderMapper.Page(json.RootElement, 100).Items);
    }

    [Fact]
    public void AFullPageMeansThereMayBeMoreAndAShortPageIsTheLast()
    {
        using var one = JsonDocument.Parse(Order(Item("a1", 3)));

        Assert.True(PazaramaOrderMapper.Page(one.RootElement, 1).HasMore);
        Assert.False(PazaramaOrderMapper.Page(one.RootElement, 100).HasMore);
    }

    [Fact]
    public void AnEmptyDataListIsAnEmptyPage()
    {
        using var empty = JsonDocument.Parse("""{"data":[],"success":true}""");
        using var missing = JsonDocument.Parse("""{"data":null,"success":true}""");

        Assert.Empty(PazaramaOrderMapper.Page(empty.RootElement, 100).Items);
        Assert.Empty(PazaramaOrderMapper.Page(missing.RootElement, 100).Items);
    }

    [Fact]
    public void ABodyWithoutADataListIsAContractError()
    {
        using var json = JsonDocument.Parse("""{"success":true}""");

        Assert.Throws<JsonException>(() => PazaramaOrderMapper.Page(json.RootElement, 100));
    }

    [Fact]
    public void FullScansReadThreeMonthsInWindowsAndIncrementalRunsAFewDays()
    {
        var now = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

        var fullStart = PazaramaOrderHistoryPolicy.StartDay(true, null, now);
        var windows = PazaramaOrderHistoryPolicy.Windows(fullStart, now);

        Assert.Equal(new DateTime(2026, 7, 15), fullStart);
        Assert.All(windows, window => Assert.True(window.End - window.Start <= TimeSpan.FromDays(28)));
        Assert.Equal(windows[0].End, windows[1].Start);
        Assert.Equal(new DateTime(2026, 10, 16), windows[^1].End);

        var incremental = PazaramaOrderHistoryPolicy.StartDay(false, now.AddMinutes(-2), now);
        Assert.Equal(new DateTime(2026, 10, 13), incremental);
        Assert.Single(PazaramaOrderHistoryPolicy.Windows(incremental, now));
    }

    [Fact]
    public void AnIncrementalRunAfterALongPauseReadsFromBeforeTheWatermark()
    {
        var now = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateTime(2026, 10, 3), PazaramaOrderHistoryPolicy.StartDay(false, now.AddDays(-10), now));
    }

    [Fact]
    public void TheEndDayIsTomorrowBecauseTheEndDateIsExcludedAndTurkeyIsAheadOfUtc()
    {
        var lateUtc = new DateTimeOffset(2026, 10, 15, 22, 30, 0, TimeSpan.Zero);

        Assert.Equal(new DateTime(2026, 10, 17), PazaramaOrderHistoryPolicy.Windows(new DateTime(2026, 10, 14), lateUtc)[^1].End);
    }

    [Fact]
    public void PazaramaSchedulesTheOrderReadsAndKeepsWritesOffByDefault()
    {
        Assert.True(ScheduledJobProducer.SupportsPazaramaScheduledPolicy("ORDERS"));
        Assert.True(ScheduledJobProducer.SupportsPazaramaScheduledPolicy("ORDER_RECOVERY"));
        Assert.True(ScheduledJobProducer.SupportsPazaramaScheduledPolicy("ORDER_LIFECYCLE"));
        Assert.True(ScheduledJobProducer.SupportsPazaramaScheduledPolicy("QUESTIONS"));
        Assert.True(ScheduledJobProducer.SupportsPazaramaScheduledPolicy("RETURNS"));
        Assert.True(ScheduledJobProducer.SupportsPazaramaScheduledPolicy("REFERENCE_DATA"));
        Assert.False(ScheduledJobProducer.SupportsPazaramaScheduledPolicy("STOCK_RECONCILE_SHORT"));
        Assert.False(ScheduledJobProducer.DefaultPolicyEnabled("PAZARAMA", MarketplaceExternalWritePolicies.Price));
        Assert.True(ScheduledJobProducer.DefaultPolicyEnabled("PAZARAMA", "ORDERS"));
    }

    [Fact]
    public void PazaramaOrderJobsShareTheOrderLockAndAreCoalescableReads()
    {
        foreach (var type in new[] { MarketplaceJobTypes.PazaramaOrderSync, MarketplaceJobTypes.PazaramaOrderRecoverySync, MarketplaceJobTypes.PazaramaOrderStatusSync })
        {
            Assert.Equal("orders", MarketplaceSyncExecutionLock.GroupFor(type));
            Assert.True(MarketplaceSyncExecutionLock.IsCoalescableRead(type));
            Assert.Contains(type, MarketplaceJobTypes.ProcessorJobTypes);
        }
        Assert.Equal(MarketplaceJobTypes.PazaramaOrderStatusSync, MarketplaceJobTypes.ForPlatform("PAZARAMA", MarketplaceJobTypes.OrderStatusSync));
        Assert.Equal(JobPriorityPolicy.Realtime, JobPriorityPolicy.For(MarketplaceJobTypes.PazaramaOrderSync, false));
    }
}
