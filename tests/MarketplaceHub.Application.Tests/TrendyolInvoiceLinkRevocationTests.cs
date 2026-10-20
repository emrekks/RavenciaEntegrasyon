using System.Net;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// F5a: the documented Trendyol "Fatura Linki Silme" request, the reads around it, and the cases where nothing is sent.
public sealed class TrendyolInvoiceLinkRevocationTests
{
    private const string PackageId = "4052072376";
    private const string OurLink = "https://panel.example.test/api/v1/public/invoice-documents/token-1/content";
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DeletesTheLinkWithTheDocumentedRequestAndProvesItWithAReadback()
    {
        var handler = new Script(
            Package("\"invoiceStatus\":\"Invoiced\",\"invoiceLink\":\"" + OurLink + "\""),
            new HttpResponseMessage(HttpStatusCode.Accepted),
            Package("\"invoiceStatus\":\"NotInvoiced\""));

        var result = await Client(handler).RevokeLinkAsync(Account(), Context(), Command(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.False(result.Value!.AlreadyAbsent);
        Assert.Equal(PackageId, result.Value.ExternalReference);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(new[] { handler.Requests[0], handler.Requests[2] }, read =>
        {
            Assert.Equal(HttpMethod.Get, read.Method);
            Assert.Equal("/integration/order/sellers/seller-1/v2/orders", read.Uri.AbsolutePath);
            Assert.Contains($"shipmentPackageIds={PackageId}", read.Uri.Query, StringComparison.Ordinal);
        });
        var delete = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, delete.Method);
        Assert.Equal("https://apigw.example.test/integration/sellers/seller-1/seller-invoice-links/delete", delete.Uri.AbsoluteUri);
        Assert.Equal("application/json", delete.ContentType);
        using var body = JsonDocument.Parse(delete.Body!);
        Assert.Equal(["serviceSourceId", "channelId", "customerId"], body.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(4052072376L, body.RootElement.GetProperty("serviceSourceId").GetInt64());
        Assert.Equal(1, body.RootElement.GetProperty("channelId").GetInt32());
        Assert.Equal(888888888L, body.RootElement.GetProperty("customerId").GetInt64());
    }

    [Fact]
    public async Task APackageWithoutAnInvoiceIsReportedAsAlreadyClearWithoutADeleteRequest()
    {
        var handler = new Script(Package("\"invoiceStatus\":\"NotInvoiced\""));

        var result = await Client(handler).RevokeLinkAsync(Account(), Context(), Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.AlreadyAbsent);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ALinkOfAnotherInvoiceIsNeverDeleted()
    {
        var handler = new Script(Package("\"invoiceStatus\":\"Invoiced\",\"invoiceNumber\":\"ABC2026000000099\",\"invoiceLink\":\"https://other.example.test/invoice.pdf\""));

        var result = await Client(handler).RevokeLinkAsync(Account(), Context(), Command(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.NotOursCode, result.Error!.Code);
        Assert.Equal(AdapterErrorClass.BusinessConflict, result.Error.Class);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task TheInvoiceNumberIdentifiesThePanelsInvoiceWhenTheLinkIsNotEchoed()
    {
        var handler = new Script(
            Package("\"invoiceStatus\":\"Invoiced\",\"invoiceNumber\":\"GIB2026000000001\""),
            new HttpResponseMessage(HttpStatusCode.Accepted),
            Package("\"invoiceStatus\":\"NotInvoiced\""));

        var result = await Client(handler).RevokeLinkAsync(Account(), Context(), Command(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ClosedWriteGatesSendNothing()
    {
        var handler = new Script();

        var closedGlobally = await Client(handler, externalWrites: false).RevokeLinkAsync(Account(), Context(), Command(), CancellationToken.None);
        var closedOnConnection = await Client(handler).RevokeLinkAsync(Account(connectionWrites: false), Context(), Command(), CancellationToken.None);
        var notAnExplicitInvoiceAction = await Client(handler).RevokeLinkAsync(Account(), Context(explicitInvoiceAction: false), Command(), CancellationToken.None);

        Assert.All(new[] { closedGlobally, closedOnConnection, notAnExplicitInvoiceAction }, result => Assert.Equal("EXTERNAL_WRITES_DISABLED", result.Error?.Code));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ALinkStillVisibleAfterAnAcceptedDeleteLeavesTheOutcomeOpen()
    {
        var handler = new Script(
            Package("\"invoiceStatus\":\"Invoiced\",\"invoiceLink\":\"" + OurLink + "\""),
            new HttpResponseMessage(HttpStatusCode.Accepted),
            Package("\"invoiceStatus\":\"Invoiced\",\"invoiceLink\":\"" + OurLink + "\""));

        var result = await Client(handler).RevokeLinkAsync(Account(), Context(), Command(), CancellationToken.None);

        Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.ReadbackPendingCode, result.Error?.Code);
        Assert.True(MarketplaceInvoiceLinkRevocationPolicy.LeavesOutcomeUnknown(result.Error!));
    }

    [Fact]
    public async Task ANotFoundAnswerIsAcceptedWhenTheReadbackShowsThePackageClear()
    {
        var handler = new Script(
            Package("\"invoiceStatus\":\"Invoiced\",\"invoiceLink\":\"" + OurLink + "\""),
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not found\"}", Encoding.UTF8, "application/json") },
            Package("\"invoiceStatus\":\"NotInvoiced\""));

        var result = await Client(handler).RevokeLinkAsync(Account(), Context(), Command(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
    }

    [Fact]
    public async Task APackageWithoutTheCustomerNumberIsNotDeleted()
    {
        var handler = new Script(Package("\"invoiceStatus\":\"Invoiced\",\"invoiceLink\":\"" + OurLink + "\"", customerId: null));

        var result = await Client(handler).RevokeLinkAsync(Account(), Context(), Command(), CancellationToken.None);

        Assert.Equal("TRENDYOL_CUSTOMER_ID_MISSING", result.Error?.Code);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ARejectedDeleteIsReturnedAsADefiniteFailure()
    {
        var handler = new Script(
            Package("\"invoiceStatus\":\"Invoiced\",\"invoiceLink\":\"" + OurLink + "\""),
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"message\":\"invalid\"}", Encoding.UTF8, "application/json") });

        var result = await Client(handler).RevokeLinkAsync(Account(), Context(), Command(), CancellationToken.None);

        Assert.Equal(AdapterErrorClass.Validation, result.Error?.Class);
        Assert.False(MarketplaceInvoiceLinkRevocationPolicy.LeavesOutcomeUnknown(result.Error!));
        Assert.Equal(2, handler.Requests.Count);
    }

    private static TrendyolHttpClient Client(Script handler, bool externalWrites = true) => new(
        new Factory(handler),
        null!,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = externalWrites ? "true" : "false" }).Build(),
        Options.Create(new TrendyolOptions()),
        new FixedClock(Now),
        NullLogger<TrendyolHttpClient>.Instance);

    private static readonly Guid ConnectionId = Guid.CreateVersion7();

    private static TrendyolRequestContext Account(bool connectionWrites = true) => new(
        new PlatformConnection { Id = ConnectionId, PlatformCode = "TRENDYOL", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "Trendyol", ExternalStoreId = "seller-1", ApiVersion = "V2" },
        new Uri("https://apigw.example.test/integration/"), "key", "secret", "seller-1 - SelfIntegration", connectionWrites);

    private static AdapterContext Context(bool explicitInvoiceAction = true) =>
        new(Guid.CreateVersion7(), ConnectionId, "link-revocation-test", "link-revocation-test", Now.AddMinutes(2), IsExplicitManualInvoiceDelivery: explicitInvoiceAction);

    private static InvoiceLinkRevocationCommand Command() => new(
        new ExternalInvoiceDeliveryReference(PackageId, "11476852228", null, "GIB2026000000001", OurLink,
            new PackageLookup(Now.AddDays(-1), new OrderLookupHint(Now.AddDays(-3), Now.AddDays(-1), "TR"), InvoiceOnly: true)),
        [OurLink, OurLink.Replace("/content", "/invoice.pdf", StringComparison.Ordinal)],
        "GIB2026000000001");

    private static HttpResponseMessage Package(string invoiceFields, long? customerId = 888888888) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"content\":[{\"shipmentPackageId\":" + PackageId + ",\"orderNumber\":\"11476852228\","
            + (customerId is { } id ? $"\"customerId\":{id}," : string.Empty)
            + "\"status\":\"Delivered\",\"orderDate\":1760700000000,\"lastModifiedDate\":1760900000000,"
            + invoiceFields + ",\"lines\":[]}],\"page\":0,\"totalPages\":1}",
            Encoding.UTF8, "application/json")
    };

    private sealed record Recorded(HttpMethod Method, Uri Uri, string? ContentType, string? Body);

    private sealed class Script(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int next;
        public List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method, request.RequestUri!, request.Content?.Headers.ContentType?.MediaType, body));
            if (next >= responses.Length) throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
            return responses[next++];
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
