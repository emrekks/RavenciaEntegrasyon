using MarketplaceHub.Api.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MappingLookupResponseTests
{
    // A panel category with no Hepsiburada mapping: the lookup answers a JSON null, so the publish check says "not
    // mapped" instead of reading an empty body as a failed request.
    [Fact]
    public async Task NoMapping_IsAJsonNullNotAnEmptyBody()
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance).BuildServiceProvider() };
        context.Response.Body = new MemoryStream();

        await CatalogEndpoints.MappingOrNull(null).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType);
        context.Response.Body.Position = 0;
        Assert.Equal("null", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }
}
