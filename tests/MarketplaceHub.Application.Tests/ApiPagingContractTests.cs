using System.Text.Json;
using MarketplaceHub.Api.Common;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-8: the paging contract new endpoints use; its wire names are the panel's (CursorPage<T> in the web).
public sealed class ApiPagingContractTests(ApiPipelineHost fixture) : IClassFixture<ApiPipelineHost>
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

    // Lists that return the first N rows and page nothing: a limit alone.
    private static readonly HashSet<string> LimitOnly = new(StringComparer.Ordinal)
    {
        "GET /api/v1/jobs", "GET /api/v1/reference-data/brands/search", "GET /api/v1/settings/ai/usage"
    };

    // The document describes each list's paging as it is on the wire: every operation that reads a paging parameter is
    // in PagingContract's table with its x-paging format, its range parameters are query parameters of the operation,
    // and the cursor formats answer the cursor page (PageResult).
    [Fact]
    public async Task TheDocumentDescribesEveryListsPagingAsItIs()
    {
        _ = fixture.Host.CreateClient();
        var document = await fixture.Host.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1").GetOpenApiDocumentAsync();
        var paging = new HashSet<string>(StringComparer.Ordinal) { "limit", "after", "page", "pageNumber", "pageSize" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (path, item) in document.Paths)
        {
            foreach (var (method, operation) in item.Operations ?? [])
            {
                var key = $"{method.Method} {path}";
                var query = (operation.Parameters ?? []).Where(parameter => parameter.In == ParameterLocation.Query).Select(parameter => parameter.Name!).ToHashSet(StringComparer.Ordinal);
                var reads = query.Where(paging.Contains).ToList();
                if (method != HttpMethod.Get || !PagingContract.PagedLists.TryGetValue(path, out var format))
                {
                    if (reads.Count > 0 && !LimitOnly.Contains(key)) problems.Add($"{key} reads {string.Join(",", reads)} but is not in PagingContract.");
                    continue;
                }
                seen.Add(path);
                if (operation.Extensions?.TryGetValue("x-paging", out var extension) != true || ((JsonNodeExtension)extension!).Node.GetValue<string>() != format.Name)
                    problems.Add($"{key}: x-paging is not {format.Name}.");
                foreach (var parameter in format.Ranges.Keys.Where(parameter => !query.Contains(parameter))) problems.Add($"{key}: no query parameter {parameter}.");
                if (format.Name is PagingContract.Cursor or PagingContract.CursorWithPage
                    && (operation.Responses?["200"].Content?["application/json"].Schema as OpenApiSchemaReference)?.Reference.Id?.StartsWith("PageResultOf", StringComparison.Ordinal) != true)
                    problems.Add($"{key}: a cursor list that does not answer PageResult.");
            }
        }
        problems.AddRange(PagingContract.PagedLists.Keys.Where(path => !seen.Contains(path)).Select(path => $"{path} is in PagingContract but not in the document."));
        Assert.True(problems.Count == 0, string.Join("\n", problems));

        var limit = (OpenApiParameter)document.Paths["/api/v1/products"].Operations![HttpMethod.Get].Parameters!.Single(parameter => parameter.Name == "limit");
        Assert.Equal(("1", "200", 50), (limit.Schema!.Minimum, limit.Schema.Maximum, limit.Schema.Default!.GetValue<int>()));
        var sendLog = (OpenApiParameter)document.Paths["/api/v1/price-stock-send-log"].Operations![HttpMethod.Get].Parameters!.Single(parameter => parameter.Name == "limit");
        Assert.Null(sendLog.Schema!.Maximum);
        Assert.Contains("brings any other value", sendLog.Description, StringComparison.Ordinal);
    }
}
