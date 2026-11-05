using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The common rules of 5 Nov 2026 (pazaryeri farkları, items 1, 2 and 7), one case per marketplace:
// - an order record the mapper refuses while its number is readable joins the re-read list (RereadOrderId), and a read
//   by that number answers with the refusal as a contract error, not "not found";
// - an order without its own order date is refused, never dated by its last change, a package event or 1970;
// - a currency the panel does not know is refused, never stored as lira or empty ("TL" is lira);
// - one unreadable line never stops the page.
public sealed class OrderRefusalRulesTests
{
    [Fact]
    public void TrendyolPackageWithoutItsOrderDateIsRefusedAndReadAgainInsteadOfTakingItsLastChange()
    {
        var page = TrendyolJsonMapper.Orders("""
            {"content":[
              {"id":"pkg-1","orderNumber":"ord-1","currencyCode":"TRY","status":"Created","lastModifiedDate":1760000000000,"lines":[{"lineId":"l1","stockCode":"S","productName":"P","quantity":1,"lineItemPrice":10,"vatRate":20}]},
              {"id":"pkg-2","orderNumber":"ord-2","orderDate":1760000000000,"currencyCode":"TRY","status":"Created","lines":[{"lineId":"l2","stockCode":"S","productName":"P","quantity":1,"lineItemPrice":10,"vatRate":20}]}
            ]}
            """);

        Assert.Equal("ord-2", Assert.Single(page.Items).ExternalOrderId);
        var issue = Assert.Single(page.Issues!);
        Assert.Equal(("ORDER_PACKAGE_INVALID", "pkg-1", "ord-1"), (issue.Code, issue.Identity, issue.RereadOrderId));
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"currencyCode\":null")]
    [InlineData(",\"currencyCode\":\"\"")]
    [InlineData(",\"currencyCode\":\"LIRA\"")]
    public void TrendyolPackageWithoutAKnownCurrencyIsSkippedWithAnIssueInsteadOfFailingTheSave(string currency)
    {
        var page = TrendyolJsonMapper.Orders($$"""
            {"content":[{"id":"pkg-1","orderNumber":"ord-1","orderDate":1760000000000{{currency}},"status":"Created","lines":[{"lineId":"l1","stockCode":"S","productName":"P","quantity":1,"lineItemPrice":10,"vatRate":20}]}]}
            """);

        Assert.Empty(page.Items);
        Assert.Equal("ord-1", Assert.Single(page.Issues!).RereadOrderId);
    }

    [Fact]
    public void TrendyolPackageTakesItsCurrencyCodeInCapitals()
    {
        var page = TrendyolJsonMapper.Orders("""
            {"content":[{"id":"pkg-1","orderNumber":"ord-1","orderDate":1760000000000,"currencyCode":"eur","status":"Created","lines":[{"lineId":"l1","stockCode":"S","productName":"P","quantity":1,"lineItemPrice":10,"vatRate":20}]}]}
            """);

        Assert.Equal("EUR", Assert.Single(page.Items).Currency);
    }

    [Fact]
    public void HepsiburadaPaidOrderLineThatCannotBeReadDoesNotThrowAndNamesItsOrder()
    {
        using var json = JsonDocument.Parse("""
            {"orderNumber":"HB-1","id":"line-1","merchantSku":"SKU-1","quantity":1,"orderDate":"2026-10-01T10:00:00Z"}
            """);

        var order = HepsiburadaJsonMapper.PaidOrderLine(json.RootElement, out var refusal);

        Assert.Null(order);
        Assert.Equal((UnreadableOrderLines.IssueCode, "HB-1"), (refusal!.Code, refusal.RereadOrderId));
    }

    [Theory]
    [InlineData("""{"orderNumber":"HB-2","id":"line-1","merchantSku":"SKU-1","quantity":1,"price":10}""")]
    [InlineData("""{"orderNumber":"HB-2","id":"line-1","merchantSku":"SKU-1","quantity":1,"price":10,"currency":"XX1","orderDate":"2026-10-01T10:00:00Z"}""")]
    public void HepsiburadaPaidOrderLineWithoutAnOrderDateOrWithAnUnknownCurrencyIsRefusedForARead(string item)
    {
        using var json = JsonDocument.Parse(item);

        var order = HepsiburadaJsonMapper.PaidOrderLine(json.RootElement, out var refusal);

        Assert.Null(order);
        Assert.Equal(("ORDER_PACKAGE_INVALID", "HB-2"), (refusal!.Code, refusal.RereadOrderId));
    }

    [Fact]
    public void HepsiburadaPaidOrderLineWithoutAnyCurrencyIsLira()
    {
        using var json = JsonDocument.Parse("""{"orderNumber":"HB-3","id":"line-1","merchantSku":"SKU-1","quantity":1,"price":10,"orderDate":"2026-10-01T10:00:00Z"}""");

        var order = HepsiburadaJsonMapper.PaidOrderLine(json.RootElement, out var refusal);

        Assert.Null(refusal);
        Assert.Equal("TRY", order!.Currency);
    }

    [Fact]
    public void HepsiburadaPackageRowWithoutTheOrderDateIsNotDatedByThePackageEvent()
    {
        using var json = JsonDocument.Parse("""
            {"packageNumber":"PKG-1","orderNumber":"HB-4","status":"Packaged","lastStatusUpdateDate":"2026-09-28T12:15:00Z",
             "items":[{"id":"line-1","lineItemId":"line-1","merchantSku":"SKU-1","quantity":1,"price":10.0}]}
            """);

        // The row is refused; the package list then reads the order by its number (HepsiburadaPackagePolicy.OrderToReadCode).
        Assert.Throws<JsonException>(() => HepsiburadaJsonMapper.OrderPackage(json.RootElement));
    }

    [Fact]
    public void PazaramaLiraIsReadFromItsTlAndAnUnknownCurrencyIsRefused()
    {
        Assert.Equal("TRY", PazaramaSingle(PazaramaOrder("\"currency\":\"TL\",")).Items.Single().Currency);
        Assert.Equal("TRY", PazaramaSingle(PazaramaOrder("")).Items.Single().Currency);

        var unknown = PazaramaSingle(PazaramaOrder("\"currency\":\"Lira\","));
        Assert.Empty(unknown.Items);
        Assert.Equal("283534307", Assert.Single(unknown.Issues!).RereadOrderId);
    }

    [Fact]
    public void PazaramaOrderWithoutItsOrderDateIsRefusedInsteadOfTakingAnItemsLastChange()
    {
        var page = PazaramaSingle(PazaramaOrder("\"currency\":\"TL\",").Replace("\"orderDate\":\"2026-02-17 17:23\",", "", StringComparison.Ordinal));

        Assert.Empty(page.Items);
        var issue = Assert.Single(page.Issues!);
        Assert.Equal(("ORDER_PACKAGE_INVALID", "283534307"), (issue.Code, issue.RereadOrderId));
    }

    [Fact]
    public void PazaramaOrderWithAnUnreadableItemTakesPazaramasOwnOrderAmount()
    {
        var unreadableItem = """{"orderItemId":"a2","orderItemStatus":3,"quantity":1,"product":{"stockCode":"S2"}}""";
        var page = PazaramaSingle(PazaramaOrder("\"currency\":\"TL\",", orderAmount: "19", extraItem: unreadableItem));

        var order = Assert.Single(page.Items);
        var package = Assert.Single(order.Packages);
        Assert.Equal(1, package.UnreadableLineCount);
        Assert.Equal((19m, 19m), (package.GrossAmount, package.NetAmount));
        Assert.Equal((19m, 19m), (order.GrossAmount, order.NetAmount));
        Assert.DoesNotContain(page.Issues!, issue => issue.Code == UnreadableOrderLines.AmountIssueCode);

        var withoutTotal = PazaramaSingle(PazaramaOrder("\"currency\":\"TL\",", orderAmount: null, extraItem: unreadableItem));
        Assert.Equal(7m, Assert.Single(withoutTotal.Items).GrossAmount);
        Assert.Contains(withoutTotal.Issues!, issue => issue.Code == UnreadableOrderLines.AmountIssueCode && issue.Identity == "order:283534307");
    }

    [Fact]
    public void ShopifyLineWithoutASkuKeepsNoneInsteadOfAMadeUpCode()
    {
        using var json = JsonDocument.Parse("""
            {"id":"gid://shopify/Order/1","name":"#1001","createdAt":"2026-10-19T10:00:00Z","updatedAt":"2026-10-19T12:00:00Z","cancelledAt":null,
             "displayFinancialStatus":"PAID","email":null,"phone":null,
             "currentTotalPriceSet":{"shopMoney":{"amount":100.00,"currencyCode":"TRY"}},
             "totalDiscountsSet":{"shopMoney":{"amount":0.00,"currencyCode":"TRY"}},
             "lineItems":{"nodes":[{"id":"gid://shopify/LineItem/7","quantity":1,"currentQuantity":1,"originalUnitPriceSet":{"shopMoney":{"amount":100.00,"currencyCode":"TRY"}},"variant":null}]},
             "fulfillments":[],"refunds":[]}
            """);

        var line = Assert.Single(ShopifyHttpClient.MapOrder(json.RootElement).Lines);

        Assert.Equal("", line.Sku);
        Assert.Equal("Shopify ürünü", line.Title);
    }

    [Fact]
    public void ANumberedReadOfARefusedOrderIsAContractErrorNotNotFound()
    {
        var issues = new[]
        {
            new AdapterPageIssue("ORDER_PACKAGE_INVALID", "pkg-9", "başka sipariş", "ord-9"),
            new AdapterPageIssue("ORDER_PACKAGE_INVALID", "pkg-1", "Sipariş paketi pkg-1 içeri alınmadı: siparişin kendi tarihi yok.", "ord-1")
        };

        var error = RefusedOrders.ReadError(issues, " ord-1 ");

        Assert.Equal(AdapterErrorClass.ContractViolation, error!.Class);
        Assert.Contains("tarihi yok", error.SafeMessage, StringComparison.Ordinal);
        Assert.Null(RefusedOrders.ReadError(issues, "ord-2"));
        Assert.Null(RefusedOrders.ReadError(null, "ord-1"));
    }

    [Theory]
    [InlineData("TL", "TRY")]
    [InlineData(" try ", "TRY")]
    [InlineData("usd", "USD")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("Türk Lirası", null)]
    [InlineData("12A", null)]
    public void CurrencyCodesAreReadAndUnknownOnesStayUnknown(string? raw, string? expected) =>
        Assert.Equal(expected, RefusedOrders.Currency(raw));

    [Fact]
    public void ARefusalListOverflowNamesTheOrdersItCouldNotKeep()
    {
        var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var full = RejectedOrderRereadPolicy.Note([], Enumerable.Range(0, RejectedOrderRereadPolicy.MaxTracked).Select(i => $"A{i:D3}"), now);

        var entries = RejectedOrderRereadPolicy.Note(full, ["B001", "B002"], now.AddMinutes(1), out var dropped);

        Assert.Equal(RejectedOrderRereadPolicy.MaxTracked, entries.Count);
        Assert.Equal(2, dropped.Count);
        Assert.DoesNotContain(entries, entry => dropped.Contains(entry.OrderId));
        Assert.Contains(dropped[0], RejectedOrderRereadPolicy.OverflowSummary(dropped), StringComparison.Ordinal);

        RejectedOrderRereadPolicy.Note([], ["C1"], now, out var none);
        Assert.Empty(none);
    }

    private static string PazaramaOrder(string currency, string? orderAmount = "7", string? extraItem = null)
    {
        var amount = orderAmount is null ? "" : "\"orderAmount\":" + orderAmount + ",";
        var extra = extraItem is null ? "" : "," + extraItem;
        const string item = """
            {"orderItemId":"a1","orderItemStatus":3,"quantity":1,
             "orderItemStatusHistory":[{"historyStatus":3,"historyCreatedDate":"2026-02-17T17:23:33.16+03:00"}],
             "salePrice":{"currency":"TL","value":7},"totalPrice":{"currency":"TL","value":7},"discountAmount":{"currency":"TL","value":0},
             "product":{"name":"Ürün","stockCode":"212","code":"testurunu001","vatRate":10}}
            """;
        return "{\"data\":[{\"orderId\":\"3099b7db\",\"orderNumber\":283534307,\"orderDate\":\"2026-02-17 17:23\"," + amount + currency
            + "\"paymentType\":1,\"items\":[" + item + extra + "]}]}";
    }

    private static AdapterPageResult<RemoteOrder> PazaramaSingle(string json)
    {
        using var document = JsonDocument.Parse(json);
        return PazaramaOrderMapper.Page(document.RootElement, 100);
    }
}
