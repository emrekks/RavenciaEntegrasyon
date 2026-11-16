using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ISLEYICI-11 (16 Nov 2026): the read loops that keep their own cursor state got the shared guards. A Trendyol claim list
// that names the page just read as the next one, or never ends, stops with its own code; the read's watermark stays.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlFact]
    public async Task Isleyici11_TrendyolReturnReconciliationOfAListThatNeverEndsStopsAtItsCap()
    {
        var (tenant, connection, _) = await SeedReadGuardConnectionAsync("isleyici11-ty-reconcile", "TRENDYOL", "RETURNS");
        var port = new NamedPageReturnPort(next: null);
        try
        {
            var result = await RunReadGuardJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ReturnReconciliation, """{"lookbackDays":3}""", returns: port);
            Assert.Equal(JobCompletionKind.ManualReview, result.Kind);
            Assert.Equal("TRENDYOL_RETURN_RECONCILE_PAGE_CAP", result.ErrorCode);
            Assert.Equal(ReturnReadService.TrendyolReturnPageCap, port.Calls);
        }
        finally { await DeleteReadGuardTenantAsync(tenant.Id); }
    }

    [PostgreSqlFact]
    public async Task Isleyici11_TrendyolReturnReadOfAPageNamingItselfStopsWithoutMovingTheWatermark()
    {
        var (tenant, connection, watermark) = await SeedReadGuardConnectionAsync("isleyici11-ty-returns", "TRENDYOL", "RETURNS");
        var port = new NamedPageReturnPort(next: "0");
        try
        {
            var result = await RunReadGuardJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ReturnSync, returns: port);
            Assert.Equal(JobCompletionKind.ManualReview, result.Kind);
            Assert.Equal("TRENDYOL_RETURN_PAGE_REPEATED", result.ErrorCode);
            Assert.Equal(1, port.Calls);
            await AssertWatermarkUnchangedAsync(tenant.Id, "RETURNS", watermark);
        }
        finally { await DeleteReadGuardTenantAsync(tenant.Id); }
    }

    // Empty claim pages that always say there is another one, naming `next` as the next page.
    private sealed class NamedPageReturnPort(string? next) : IReturnPort
    {
        public int Calls { get; private set; }

        public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(new AdapterPageResult<RemoteReturnClaim>([], next, true)));
        }

        public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteReturnClaim>> GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
