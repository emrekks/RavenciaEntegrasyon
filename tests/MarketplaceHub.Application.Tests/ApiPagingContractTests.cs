using System.Text.Json;
using MarketplaceHub.Api.Common;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-8: the paging contract new endpoints use; its wire names are the panel's (CursorPage<T> in the web).
public sealed class ApiPagingContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void CursorPage_HasTheCursorListShape()
    {
        Assert.Equal("{\"items\":[1,2],\"nextCursor\":\"c\",\"hasMore\":true,\"totalCount\":7,\"supportedFilters\":null}", JsonSerializer.Serialize(CursorPage.Of<int>([1, 2], "c", 7), Web));
        Assert.False(CursorPage.Of<int>([], null).HasMore);
    }

    [Fact]
    public void NumberedPage_HasThePageNumberedShape() =>
        Assert.Equal("{\"items\":[\"a\"],\"page\":2,\"pageSize\":50,\"totalCount\":51}", JsonSerializer.Serialize(new NumberedPage<string>(["a"], 2, 50, 51), Web));

    [Theory]
    [InlineData(null, null, 1, 50, 0)]
    [InlineData(3, 20, 3, 20, 40)]
    public void NumberedPageRequest_ReadsPageAndSize(int? page, int? pageSize, int expectedPage, int expectedSize, int skip)
    {
        var request = NumberedPageRequest.From(page, pageSize);
        Assert.Equal((expectedPage, expectedSize, skip), (request.Page, request.PageSize, request.Skip));
    }

    [Fact]
    public void PageRequests_RefuseOutOfRangeValuesLikeTheEndpoints()
    {
        Assert.Equal("limit 1-200 arasında olmalıdır.", Assert.Throws<ArgumentException>(() => CursorPageRequest.From(0, null)).Message);
        Assert.Throws<ArgumentException>(() => NumberedPageRequest.From(0, 10));
        Assert.Equal(new CursorPageRequest(50, null), CursorPageRequest.From(null, " "));
    }
}
