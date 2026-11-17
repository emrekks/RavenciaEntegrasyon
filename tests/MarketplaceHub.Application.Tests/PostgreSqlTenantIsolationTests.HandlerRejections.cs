using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// J1 (15 Nov 2026): a handler that stopped before doing anything answered `false`, which the dispatcher turned into the
// generic Blocked F3_JOB_REJECTED ("İşlem bu kaydın şu anki durumunda yapılamaz ya da mağaza bu işlemi desteklemiyor").
// Each lasting cause now ends Blocked with its own code and a plain summary, nothing is sent, and a job that has nothing to
// do on the store (the Trendyol cargo check on another marketplace) succeeds with a note.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlFact]
    public async Task J1_PackageActionForAPackageThePanelNoLongerHasNamesTheMissingPackage()
    {
        var (tenant, connection, _, actionJob, _, _) = await SeedEffectPackageAsync("j1-ship-missing");
        var port = new EffectOrderPort(null);
        try
        {
            var missing = JsonSerializer.Serialize(new ShipmentActionJobPayload(actionJob.Id, Guid.CreateVersion7(), "PICKING", "{}"));
            var result = await RunEffectOrderJobAsync(tenant.Id, connection.Id, port, MarketplaceJobTypes.ShipmentAction, missing);
            AssertRejected(result, "SHIPMENT_PACKAGE_NOT_FOUND");

            var unreadable = await RunEffectOrderJobAsync(tenant.Id, connection.Id, port, MarketplaceJobTypes.ShipmentAction, "{\"jobId\":");
            AssertRejected(unreadable, "SHIPMENT_ACTION_PAYLOAD_INVALID");
            Assert.Equal(0, port.PackageActions);
            await using var check = fixture.CreateContext();
            Assert.False(await check.ExternalEffectRecords.AnyAsync(x => x.TenantId == tenant.Id));
        }
        finally { await CleanupEffectPackageAsync(tenant.Id); }
    }

    [PostgreSqlFact]
    public async Task J1_ReturnDecisionThatIsGoneOrUnreadableNamesItsCause()
    {
        var tenant = NewTenant("j1-return");
        var connection = NewQuestionConnection(tenant);
        await SeedTenantAndConnectionAsync(tenant, connection);
        var port = new EffectReturnPort(null);
        try
        {
            var gone = await RunHandlerRejectionJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ReturnAction, $$"""{"decisionId":"{{Guid.CreateVersion7()}}"}""", returns: port);
            AssertRejected(gone, "RETURN_DECISION_NOT_FOUND");
            var unreadable = await RunHandlerRejectionJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ReturnAction, "{}", returns: port);
            AssertRejected(unreadable, "RETURN_ACTION_PAYLOAD_INVALID");
            Assert.Equal(0, port.Executions);
        }
        finally { await DeleteInvoiceTestTenantAsync(tenant.Id); }
    }

    [PostgreSqlFact]
    public async Task J1_WebhookWithAnUnreadableBodyNamesIt()
    {
        var tenant = NewTenant("j1-webhook");
        var connection = NewQuestionConnection(tenant);
        await SeedTenantAndConnectionAsync(tenant, connection);
        try
        {
            var payload = JsonSerializer.Serialize(new { rawJson = "{not json", externalMessageId = "j1-message" });
            var result = await RunHandlerRejectionJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.WebhookIngest, payload);
            AssertRejected(result, "WEBHOOK_BODY_UNREADABLE");
        }
        finally { await DeleteInvoiceTestTenantAsync(tenant.Id); }
    }

    [PostgreSqlFact]
    public async Task J1_CatalogStructureImportWithoutItsRunNamesIt()
    {
        var tenant = NewTenant("j1-structure");
        var connection = NewQuestionConnection(tenant);
        await SeedTenantAndConnectionAsync(tenant, connection);
        try
        {
            var result = await RunHandlerRejectionJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.CatalogStructureImport, $$"""{"runId":"{{Guid.CreateVersion7()}}"}""");
            AssertRejected(result, "CATALOG_STRUCTURE_RUN_MISSING");
        }
        finally { await DeleteInvoiceTestTenantAsync(tenant.Id); }
    }

    [PostgreSqlFact]
    public async Task J1_ReferenceReadForACategoryOutsideTheTreeNamesIt()
    {
        var tenant = NewTenant("j1-reference");
        var connection = NewQuestionConnection(tenant);
        await SeedTenantAndConnectionAsync(tenant, connection);
        try
        {
            var result = await RunHandlerRejectionJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ReferenceSync, """{"resourceType":"CATEGORY_ATTRIBUTES","parentExternalId":"no-such-category"}""");
            AssertRejected(result, "REFERENCE_SCOPE_INVALID");
        }
        finally { await DeleteInvoiceTestTenantAsync(tenant.Id); }
    }

    [PostgreSqlFact]
    public async Task J1_TrendyolCargoCheckOnAnotherStoreSucceedsWithANote()
    {
        var tenant = NewTenant("j1-cargo");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "HEPSIBURADA";
        await SeedTenantAndConnectionAsync(tenant, connection);
        try
        {
            var result = await RunHandlerRejectionJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation, "{}");
            Assert.Equal(JobCompletionKind.Succeeded, result.Kind);
            Assert.Equal("CARGO_INFO_NOT_APPLICABLE", result.ErrorCode);
            Assert.Matches("[çğışöüİ]", result.ErrorSummary!);
        }
        finally { await DeleteInvoiceTestTenantAsync(tenant.Id); }
    }

    // KUYRUK-9 (16 Nov 2026): the order read of every marketplace and the product import name their cause too.
    [PostgreSqlTheory]
    [InlineData("TRENDYOL")]
    [InlineData("SHOPIFY")]
    [InlineData("HEPSIBURADA")]
    [InlineData("N11")]
    [InlineData("PAZARAMA")]
    [InlineData("PTTAVM")]
    public async Task Kuyruk9_OrderReadWithAnUnreadableRequestNamesIt(string platform)
    {
        var tenant = NewTenant($"k9-orders-{platform.ToLowerInvariant()}");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = platform;
        await SeedTenantAndConnectionAsync(tenant, connection);
        try
        {
            var result = await RunHandlerRejectionJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ForPlatform(platform, MarketplaceJobTypes.OrderSync), "{not json");
            AssertRejected(result, "ORDER_SYNC_PAYLOAD_INVALID");
        }
        finally { await DeleteInvoiceTestTenantAsync(tenant.Id); }
    }

    [PostgreSqlFact]
    public async Task Kuyruk9_ProductImportOnAStoreOutsideItsEnvironmentsNamesIt()
    {
        var tenant = NewTenant("k9-products");
        var connection = NewQuestionConnection(tenant);
        connection.Environment = "SANDBOX";
        await SeedTenantAndConnectionAsync(tenant, connection);
        try
        {
            var result = await RunHandlerRejectionJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ProductSync, "{}");
            AssertRejected(result, "PRODUCT_IMPORT_CONNECTION_NOT_ELIGIBLE");
        }
        finally { await DeleteInvoiceTestTenantAsync(tenant.Id); }
    }

    private static void AssertRejected(JobExecutionResult result, string code)
    {
        Assert.Equal(JobCompletionKind.Blocked, result.Kind);
        Assert.Equal(code, result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorSummary));
        Assert.Matches("[çğışöüÇĞİŞÖÜ]", result.ErrorSummary!);
    }

    private async Task SeedTenantAndConnectionAsync(Tenant tenant, PlatformConnection connection)
    {
        await using var seedDb = fixture.CreateContext();
        seedDb.Tenants.Add(tenant);
        seedDb.PlatformConnections.Add(connection);
        await seedDb.SaveChangesAsync();
    }

    private async Task<JobExecutionResult> RunHandlerRejectionJobAsync(Guid tenantId, Guid connectionId, string jobType, string payload, IReturnPort? returns = null)
    {
        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, returns!, null!, null!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build(), fixture.TimeProvider);
        return await processor.ProcessAsync(tenantId, connectionId, jobType, payload, "j1-rejections", CancellationToken.None);
    }
}
