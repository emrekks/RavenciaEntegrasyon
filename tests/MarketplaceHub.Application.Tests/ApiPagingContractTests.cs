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
                // Unified paging: the cursor parameters next to the old ones (deprecated), and every shape it answers
                // carries the cursor members next to its own.
                if (format.TakesCursor)
                {
                    foreach (var parameter in new[] { "limit", "after" }.Where(parameter => !query.Contains(parameter))) problems.Add($"{key}: no query parameter {parameter}.");
                    foreach (var parameter in (operation.Parameters ?? []).Where(parameter => parameter.In == ParameterLocation.Query && paging.Contains(parameter.Name!)))
                        if (parameter.Deprecated != (format.Deprecated?.Contains(parameter.Name!) == true)) problems.Add($"{key}: {parameter.Name} deprecated is {parameter.Deprecated}.");
                    var schema = operation.Responses!["200"].Content!["application/json"].Schema!;
                    var shapes = schema is OpenApiSchemaReference ? [schema] : schema.OneOf!;
                    foreach (var shape in shapes.Cast<OpenApiSchemaReference>())
                        foreach (var member in new[] { "nextCursor", "hasMore", "totalCount" }.Where(member => document.Components!.Schemas![shape.Reference.Id!].Properties?.ContainsKey(member) != true))
                            problems.Add($"{key}: {shape.Reference.Id} has no {member}.");
                }
            }
        }
        problems.AddRange(PagingContract.PagedLists.Keys.Where(path => !seen.Contains(path)).Select(path => $"{path} is in PagingContract but not in the document."));
        Assert.True(problems.Count == 0, string.Join("\n", problems));

        var limit = (OpenApiParameter)document.Paths["/api/v1/products"].Operations![HttpMethod.Get].Parameters!.Single(parameter => parameter.Name == "limit");
        Assert.Equal(("1", "200", 50), (limit.Schema!.Minimum, limit.Schema.Maximum, limit.Schema.Default!.GetValue<int>()));
        var sendLog = (OpenApiParameter)document.Paths["/api/v1/price-stock-send-log"].Operations![HttpMethod.Get].Parameters!.Single(parameter => parameter.Name == "limit");
        Assert.Null(sendLog.Schema!.Maximum);
        Assert.Contains("brings any other value", sendLog.Description, StringComparison.Ordinal);
        Assert.Equal(6, PagingContract.PagedLists.Values.Count(format => format.TakesCursor));
    }

    [Fact]
    public void OffsetCursor_NamesTheOffsetOfThePageItStarts()
    {
        Assert.Equal(2, OffsetCursor.PageIndex(OffsetCursor.Encode(40), 20));
        Assert.Null(OffsetCursor.PageIndex(" ", 20));
        Assert.Equal(OffsetCursor.Encode(20), OffsetCursor.Next(0, 20, 21));
        Assert.Null(OffsetCursor.Next(1, 20, 40));
        Assert.Throws<ArgumentException>(() => OffsetCursor.PageIndex(OffsetCursor.Encode(30), 20));
        Assert.Throws<ArgumentException>(() => OffsetCursor.PageIndex("bozuk!", 20));
        Assert.Throws<ArgumentException>(() => OffsetCursor.PageIndex(OffsetCursor.Encode(20)[..^1] + "x", 20));
        Assert.Throws<ArgumentException>(() => OffsetCursor.RefuseWhereOrderIsNotTotal(OffsetCursor.Encode(20), orderIsTotal: false));
    }
}
