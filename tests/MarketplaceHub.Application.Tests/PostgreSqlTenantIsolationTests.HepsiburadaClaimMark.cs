using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// J5 (15 Nov 2026): a Hepsiburada order built from a claim carries the return-claim mark. The Hepsiburada upsert merges the
// read's customer snapshot into the stored one (keeping stored keys), so without care the mark would outlive the real
// order read and the order would count as claim-built for good. A real read drops it; a claim-built read keeps it.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlFact]
    public async Task J5_ARealHepsiburadaOrderReadClearsTheClaimBuiltMark()
    {
        var tenant = NewTenant("j5-hb-claim-mark");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "HEPSIBURADA";
        connection.Environment = "PRODUCTION";
        await SeedTenantAndConnectionAsync(tenant, connection);
        const string orderNumber = "j5-order-1";
        var claimBuilt = new RemoteOrder(orderNumber, orderNumber, fixture.Now.AddDays(-3), DateTimeOffset.UnixEpoch, "TRY", 50m, 0m, 50m,
            TrendyolJsonMapper.WithReturnClaimReadModelMark("""{"customerName":"Alıcı"}"""), "{}", "{}",
            [new RemoteOrderLine("j5-line-1", "SKU-J5", null, "Ürün", 1m, 50m, null, "ClaimCreated", "{}")], [], "{}");
        var real = claimBuilt with
        {
            LastModifiedAt = fixture.Now.AddDays(-2),
            CustomerSnapshotJson = """{"customerName":"Alıcı","customerId":"c-1"}""",
            Lines = [new RemoteOrderLine("j5-line-1", "SKU-J5", null, "Ürün", 1m, 50m, 20m, "Delivered", "{}")]
        };
        var port = new SequenceOrderPort(claimBuilt, real);
        try
        {
            await RunClaimMarkReadAsync(tenant.Id, connection.Id, port, orderNumber);
            Assert.True(TrendyolJsonMapper.IsReturnClaimReadModelSnapshot(await StoredCustomerSnapshotAsync(tenant.Id, orderNumber)));

            await RunClaimMarkReadAsync(tenant.Id, connection.Id, port, orderNumber);
            var snapshot = await StoredCustomerSnapshotAsync(tenant.Id, orderNumber);
            Assert.False(TrendyolJsonMapper.IsReturnClaimReadModelSnapshot(snapshot));
            Assert.Contains("c-1", snapshot, StringComparison.Ordinal);
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.JobVariantResults.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    private async Task RunClaimMarkReadAsync(Guid tenantId, Guid connectionId, IOrderPort port, string orderNumber)
    {
        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, port, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var result = await processor.ProcessAsync(tenantId, connectionId, MarketplaceJobTypes.HepsiburadaOrderSync, $$"""{"externalOrderId":"{{orderNumber}}"}""", "j5-claim-mark", CancellationToken.None);
        Assert.Equal(JobCompletionKind.Succeeded, result.Kind);
    }

    private async Task<string> StoredCustomerSnapshotAsync(Guid tenantId, string orderNumber)
    {
        await using var check = fixture.CreateContext();
        return await check.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.ExternalOrderId == orderNumber).Select(x => x.CustomerSnapshotJson).SingleAsync();
    }

    // Answers each order read with the next order of the list.
    private sealed class SequenceOrderPort(params RemoteOrder[] answers) : IOrderPort
    {
        private int next;

        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteOrder>.Success(answers[Math.Min(next++, answers.Length - 1)]));

        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => throw new NotSupportedException();
        public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
