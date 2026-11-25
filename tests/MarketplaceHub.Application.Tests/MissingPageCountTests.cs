using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Jobs.Handlers;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// B-2: a page that does not send its page count used to read as the last page (the count fell back to 0, or to the
// page's own size), so the records past a full first page were never read and the window was marked read. A missing
// count is unknown: the read goes on while the pages come back full and stops at a short or empty one.
public sealed class MissingPageCountTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Trendyol_finance_page_without_totalPages_leaves_the_count_unknown()
    {
        var page = TrendyolFinanceMapper.Page("""{"page":0,"content":[{"id":"1","transactionDate":1613397671561,"credit":1}]}""", "SETTLEMENTS", "Sale", null);

        Assert.Null(page.TotalPages);
        Assert.Null(TrendyolFinanceMapper.PayoutPage("""{"content":[]}""").TotalPages);
        Assert.Null(TrendyolFinanceMapper.CargoPage("""{"content":[]}""").TotalPages);
    }

    [Fact]
    public void Trendyol_finance_read_follows_full_pages_while_the_count_is_unknown()
    {
        Assert.False(TrendyolFinanceSyncJobHandler.LastPage(0, 500, null, 500));
        Assert.True(TrendyolFinanceSyncJobHandler.LastPage(1, 499, null, 500));
        Assert.True(TrendyolFinanceSyncJobHandler.LastPage(1, 0, null, 500));
        Assert.True(TrendyolFinanceSyncJobHandler.LastPage(0, 500, 1, 500));
        Assert.False(TrendyolFinanceSyncJobHandler.LastPage(0, 3, 2, 500));
    }

    [Fact]
    public void Hepsiburada_finance_page_without_count_leaves_the_count_unknown()
    {
        using var json = JsonDocument.Parse("""{"items":[{"id":"1","amount":{"value":10,"currencyCode":"949"},"dueDate":"2026-11-10T00:00:00"}]}""");

        Assert.Null(HepsiburadaFinanceMapper.Page(json.RootElement).Count);
    }

    [Fact]
    public void N11_product_page_without_totalPages_continues_while_full()
    {
        const string product = """{"n11ProductId":1,"stockCode":"SKU-A","productMainId":"M1","title":"Ürün","brand":"Test"}""";
        using var full = JsonDocument.Parse($$"""{"content":[{{product}},{{product.Replace("SKU-A", "SKU-B")}}],"number":0}""");
        using var shortPage = JsonDocument.Parse($$"""{"content":[{{product}}],"number":1}""");

        var first = N11ProductMapper.Page(full.RootElement, pageSize: 2, requestedPage: 0);
        var last = N11ProductMapper.Page(shortPage.RootElement, pageSize: 2, requestedPage: 1);

        Assert.True(first.HasMore);
        Assert.Equal("1", first.NextCursor);
        Assert.False(last.HasMore);
    }

    [Fact]
    public void Pazarama_return_page_without_totalPages_continues_while_full()
    {
        const string refund = """{"refundId":"11111111-1111-1111-1111-111111111111","refundNumber":1,"orderNumber":1001,"refundStatus":1,"refundDate":"6 Ekim 2021 19:46"}""";
        using var full = JsonDocument.Parse($$"""{"data":{"responsePage":{"pageIndex":1},"refundList":[{{refund}},{{refund.Replace("1111-111111111111", "1111-222222222222")}}]},"success":true}""");
        using var shortPage = JsonDocument.Parse($$"""{"data":{"responsePage":{"pageIndex":2},"refundList":[{{refund}}]},"success":true}""");

        var first = PazaramaReturnMapper.Page(full.RootElement, 1, Now, pageSize: 2);
        var last = PazaramaReturnMapper.Page(shortPage.RootElement, 2, Now, pageSize: 2);

        Assert.True(first.HasMore);
        Assert.Equal("2", first.NextCursor);
        Assert.False(last.HasMore);
    }

    [Fact]
    public void Trendyol_return_page_without_totalPages_continues_while_full()
    {
        const string claim = """{"id":"c1","claimId":"c1","orderNumber":"o1","lastModifiedDate":1760000000000,"items":[{"orderLine":{"id":101},"claimItems":[{"id":"i1","claimItemStatus":{"name":"Created"}}]}]}""";
        var full = $$"""{"content":[{{claim}},{{claim.Replace("\"c1\"", "\"c2\"")}}]}""";
        var shortPage = $$"""{"content":[{{claim}}]}""";

        var first = TrendyolJsonMapper.Returns(full, pageSize: 2, requestedPage: 3);
        var last = TrendyolJsonMapper.Returns(shortPage, pageSize: 2, requestedPage: 4);

        Assert.True(first.HasMore);
        Assert.Equal("4", first.NextCursor);
        Assert.False(last.HasMore);
    }
}
