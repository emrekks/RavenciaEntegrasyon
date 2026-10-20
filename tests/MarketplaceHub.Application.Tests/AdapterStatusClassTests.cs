using System.Net;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A remote 408 is a transient timeout (retried, never a permanent block) and a 409 is a business conflict
// on every marketplace adapter.
public sealed class AdapterStatusClassTests
{
    public static TheoryData<string> Adapters => ["HEPSIBURADA", "TRENDYOL", "N11", "PAZARAMA", "SHOPIFY"];

    [Theory]
    [MemberData(nameof(Adapters))]
    public void ARemoteRequestTimeoutIsTransient(string adapter)
    {
        var error = Map(adapter, HttpStatusCode.RequestTimeout);

        Assert.Equal(AdapterErrorClass.TransientNetwork, error.Class);
        Assert.Equal(408, error.HttpStatus);
        Assert.Equal(JobCompletionKind.Retry, JobExecutionResult.FromAdapterError(error).Kind);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public void ARemoteConflictIsABusinessConflict(string adapter)
    {
        var error = Map(adapter, HttpStatusCode.Conflict);

        Assert.Equal(AdapterErrorClass.BusinessConflict, error.Class);
        Assert.Equal(409, error.HttpStatus);
    }

    [Fact]
    public void AShopifyNotFoundIsNotFoundInsteadOfValidation() =>
        Assert.Equal(AdapterErrorClass.NotFound, ShopifyHttpClient.MapHttpError(HttpStatusCode.NotFound, "").Class);

    [Fact]
    public void ATrendyolCargoVendorCodeOnAConflictStillNamesTheCargoRefusal() =>
        Assert.Equal("CARGO_PROVIDER_CHANGE_REJECTED", TrendyolErrorMapper.FromStatus(HttpStatusCode.Conflict, null, null, "CPE002").Code);

    private static AdapterError Map(string adapter, HttpStatusCode status) => adapter switch
    {
        "HEPSIBURADA" => HepsiburadaHttpClient.Error(status, null, null),
        "TRENDYOL" => TrendyolErrorMapper.FromStatus(status, null, null),
        "N11" => N11HttpClient.Error(status, "", null),
        "PAZARAMA" => PazaramaHttpClient.Error(status, "", null),
        _ => ShopifyHttpClient.MapHttpError(status, "")
    };
}
