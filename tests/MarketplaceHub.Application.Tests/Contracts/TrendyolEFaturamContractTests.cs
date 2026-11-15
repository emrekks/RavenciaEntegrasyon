using System.Net;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Mapping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ADAPTOR-DIGER-10 (+ ADAPTOR-DIGER-4's restored status fixtures): Trendyol E-Faturam answers through the public
// mapper and the authorized send path, compared with goldens under Fixtures/EFaturam/.
public sealed class TrendyolEFaturamContractTests
{
    private const string Uuid = "96a8b0f1-7e10-4a44-8d78-eb961539bb4b";

    [Fact]
    public void StatusAnswersMapToTheCatalogsStatesAndAMissingStatusIsRefused()
    {
        using var document = ContractFixture.Json("EFaturam/earchive-status.json");
        var statuses = document.RootElement.EnumerateObject().ToDictionary(
            answer => answer.Name,
            answer => ContractFixture.Outcome(() => TrendyolEFaturamJsonMapper.InvoiceStatus(answer.Value.GetRawText(), "ref-1", Uuid)));

        ContractFixture.MatchesGolden("EFaturam/earchive-status.golden.json", statuses);
        string Canonical(string name) => ((InvoiceRemoteStatus)statuses[name]!).CanonicalStatus;
        Assert.Equal(("ACCEPTED", "CANCELLED", "REJECTED"), (Canonical("approved"), Canonical("cancelled"), Canonical("rejected")));
        Assert.False(((InvoiceRemoteStatus)statuses["docsPending"]!).IsTerminal);
        // A paged answer is read at the invoice asked for, never at the first item.
        Assert.Equal(("PENDING", "RVN2026000000001"), (Canonical("paged"), ((InvoiceRemoteStatus)statuses["paged"]!).InvoiceNumber));
        // An undocumented code is not guessed: it goes to manual review.
        Assert.Equal("MANUAL_REVIEW", Canonical("undocumented"));
        Assert.Equal(("ACCEPTED", 205), (Canonical("asText"), ((InvoiceRemoteStatus)statuses["asText"]!).GibStatusCode));
        Assert.IsNotType<InvoiceRemoteStatus>(statuses["noStatus"]);
    }

    [Fact]
    public void ACreationAnswerGivesTheInvoiceAndAnUnreadableOneStillItsUuid()
    {
        using var document = ContractFixture.Json("EFaturam/create-answers.json");
        var root = document.RootElement;
        var truncated = ContractFixture.Read("EFaturam/create-answer-truncated.txt");

        var created = TrendyolEFaturamJsonMapper.OutgoingInvoice(root.GetProperty("created").GetRawText(), "req-1");
        var withoutNumber = ContractFixture.Outcome(() => TrendyolEFaturamJsonMapper.OutgoingInvoice(root.GetProperty("withoutNumber").GetRawText(), "req-2"));
        var partial = TrendyolEFaturamJsonMapper.PartialOutgoingInvoice(root.GetProperty("withoutNumber").GetRawText(), "req-2");
        var fromTruncated = TrendyolEFaturamJsonMapper.PartialOutgoingInvoice(truncated, "req-3");

        ContractFixture.MatchesGolden("EFaturam/create-answers.golden.json", new { created, withoutNumber, partial, fromTruncated });
        Assert.Equal((Uuid, "RVN2026000000001", "10"), (created.EttnUuid, created.InvoiceNumber, created.RawStatus));
        // Without its number the full read refuses the answer; the partial read keeps the UUID so it is read back, not resent.
        Assert.IsNotType<InvoiceSubmissionResult>(withoutNumber);
        Assert.Equal((Uuid, null), (partial!.EttnUuid, partial.InvoiceNumber));
        Assert.Equal((Uuid, "UNKNOWN"), (fromTruncated!.EttnUuid, fromTruncated.RawStatus));
    }

    [Fact]
    public void TaxpayerAnswersCountOnlyLiveInvoiceReceiverAliases()
    {
        using var document = ContractFixture.Json("EFaturam/taxpayers.json");
        var lookups = document.RootElement.EnumerateObject().ToDictionary(
            answer => answer.Name, answer => TrendyolEFaturamJsonMapper.TaxPayers(answer.Value.GetRawText(), "0000000001"));

        ContractFixture.MatchesGolden("EFaturam/taxpayers.golden.json", lookups);
        Assert.Equal(EInvoiceTaxpayerState.Registered, lookups["registered"].State);
        Assert.Equal("urn:mail:defaultpk@example.test", Assert.Single(lookups["registered"].InvoiceAliases).Alias);
        Assert.Equal(("EINVOICE_TAXPAYER_IDENTITY_MISMATCH", "EINVOICE_TAXPAYER_NO_INVOICE_ALIAS"), (lookups["otherTaxId"].ErrorCode, lookups["deletedOnly"].ErrorCode));
        Assert.NotEqual(EInvoiceTaxpayerState.Registered, lookups["otherTaxId"].State);
    }

    public static TheoryData<string, CannedHttpCase> ErrorAnswers => new()
    {
        { "429 with Retry-After", new(() => CannedHttp.RateLimited(19), AdapterErrorClass.RateLimit, "EFATURAM_RATE_LIMITED", 429, TimeSpan.FromSeconds(19)) },
        { "401 token rejected", new(() => CannedHttp.Unauthorized(), AdapterErrorClass.Authentication, "EFATURAM_ACCESS_TOKEN_REJECTED", 401, null) },
        // DEVIATION: E-Faturam's send path has no HTML gateway rule (MarketplaceErrorRules.GatewayBlocked): a 403 HTML
        // page reads as EFATURAM_OPERATION_FORBIDDEN (Authentication) instead of a temporary gateway block.
        { "403 HTML gateway page", new(CannedHttp.GatewayBlocked, AdapterErrorClass.Authentication, "EFATURAM_OPERATION_FORBIDDEN", 403, null) },
        { "404 documented problem", new(() => CannedHttp.Fixture(HttpStatusCode.NotFound, "EFaturam/problem-earchive-not-found.json"), AdapterErrorClass.NotFound, "EFATURAM_RESOURCE_NOT_FOUND", 404, null) },
        { "400 validation", new(() => new CannedHttp(HttpStatusCode.BadRequest, """{"type":"about:blank","title":"Bad Request","status":400}"""), AdapterErrorClass.Validation, "EFATURAM_REQUEST_REJECTED", 400, null) },
        { "408 provider timeout", new(() => new CannedHttp(HttpStatusCode.RequestTimeout, ""), AdapterErrorClass.TransientNetwork, "EFATURAM_REMOTE_TIMEOUT", 408, TimeSpan.FromSeconds(30)) },
        { "502 server", new(() => new CannedHttp(HttpStatusCode.BadGateway, ""), AdapterErrorClass.Remote5xx, "EFATURAM_SERVER_ERROR", 502, null) }
    };

    [Theory]
    [MemberData(nameof(ErrorAnswers))]
    public async Task EachErrorAnswerIsClassifiedThroughTheAuthorizedSendPath(string name, CannedHttpCase answer)
    {
        var http = answer.Http();
        var options = Options.Create(new TrendyolEFaturamOptions());
        var client = new TrendyolEFaturamHttpClient(http, null!, new ConfigurationBuilder().Build(), options, new SafeRemoteDocumentDownloader(http, options), TimeProvider.System, NullLogger<TrendyolEFaturamHttpClient>.Instance, new TrendyolEFaturamTokenCache(TimeProvider.System), new TrendyolEFaturamTaxpayerCache(TimeProvider.System));

        var result = await client.SendAuthorized(Account(), "fake-token", HttpMethod.Get, $"api/invoice/documents/earchive/status/{Uuid}", null, CancellationToken.None);

        Assert.Equal(1, http.Requests);
        answer.AssertMatches(name, result.Error);
        if (name.StartsWith("404", StringComparison.Ordinal))
            Assert.Equal("problem:/etransformation/gateway/earchive-not-found", result.Error!.RemoteRequestId);
    }

    private static TrendyolEFaturamRequestContext Account() => new(
        new PlatformConnection { PlatformCode = "TRENDYOL_EFATURAM", Environment = "STAGE", Status = "ACTIVE", DisplayName = "E-Faturam", ExternalStoreId = "0", ApiVersion = "v1" },
        new Uri("https://efaturam.example.test/"), new TrendyolEFaturamCredentialPayload("fake@example.test", "fake-password"), new TrendyolEFaturamConnectionSettings(false), Guid.Parse("00000000-0000-4000-8000-00000000e999"));
}
