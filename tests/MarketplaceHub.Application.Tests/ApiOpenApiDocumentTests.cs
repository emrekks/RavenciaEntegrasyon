using System.Runtime.CompilerServices;
using MarketplaceHub.Api.Common;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-8: the OpenAPI description of the panel's API, kept in the repository as the contract the web's typed
// client is to be generated from (ApiContract/openapi.v1.json next to this file). No route serves the document; it
// is generated here from the API's endpoints. A route or a request/response type that changes changes the file: run
// with MARKETPLACEHUB_UPDATE_OPENAPI=1 once and commit the result.
public sealed class ApiOpenApiDocumentTests(ApiPipelineHost fixture) : IClassFixture<ApiPipelineHost>
{
    private const string ContractFile = "ApiContract/openapi.v1.json";

    private async Task<string> GenerateAsync()
    {
        _ = fixture.Host.CreateClient();
        var document = await fixture.Host.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1").GetOpenApiDocumentAsync();
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1);
        return json.ReplaceLineEndings("\n").TrimEnd() + "\n";
    }

    [Fact]
    public async Task TheContractFileIsTheGeneratedDocument()
    {
        var actual = await GenerateAsync();
        var path = Path.Combine(Here(), ContractFile);
        if (Environment.GetEnvironmentVariable("MARKETPLACEHUB_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);
            return;
        }
        var expected = (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n");
        Assert.True(expected == actual, $"{ContractFile} is out of date; run this test with MARKETPLACEHUB_UPDATE_OPENAPI=1 and commit the file.");
    }

    [Fact]
    public async Task TheDocumentIsStable() => Assert.Equal(await GenerateAsync(), await GenerateAsync());

    // Every panel route is in the document with its method.
    [Fact]
    public async Task EveryPanelRouteIsDescribed()
    {
        _ = fixture.Host.CreateClient();
        var document = await fixture.Host.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1").GetOpenApiDocumentAsync();
        var missing = ApiRouteTable.Routes(fixture.Host)
            .Where(route => route.Pattern.StartsWith("/api/v1/", StringComparison.Ordinal) && route.Method != "*")
            .Where(route =>
            {
                var path = System.Text.RegularExpressions.Regex.Replace(route.Pattern, @"\{\*{0,2}([^}:]+)(:[^}]+)?\}", "{$1}");
                // A group's root route ("/api/v1/jobs/") is described without its trailing slash.
                return !(document.Paths.TryGetValue(path, out var item) || document.Paths.TryGetValue(path.TrimEnd('/'), out item)) || item.Operations is null
                    || !item.Operations.ContainsKey(HttpMethod.Parse(route.Method));
            })
            .Select(route => $"{route.Method} {route.Pattern}").ToList();
        Assert.True(missing.Count == 0, "Not in the OpenAPI document:\n" + string.Join("\n", missing));
        Assert.Equal("Ravencia Marketplace Hub API", document.Info.Title);
        Assert.Null(document.Servers is { Count: > 0 } ? document.Servers : null);
    }

    // The document is not served: no route of the API answers it, in production or in tests.
    [Fact]
    public void NoRouteServesTheDocument() =>
        Assert.DoesNotContain(ApiRouteTable.Routes(fixture.Host), route => route.Pattern.Contains("openapi", StringComparison.OrdinalIgnoreCase));

    private static string Here([CallerFilePath] string source = "") => Path.GetDirectoryName(source)!;
}
