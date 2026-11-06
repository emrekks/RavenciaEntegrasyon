using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit 2026-10-24 Q3: webhook deliveries that lock with the return reads run beside those that lock with the order
// reads, so they must not write the same telemetry cursor (its Version is a concurrency token; the later run failed
// its telemetry write and was retried although its data was saved).
public sealed class WebhookIngestTelemetryTests
{
    private static string Payload(string resourceType) =>
        JsonSerializer.Serialize(new { rawJson = "{}", externalMessageId = "m-1", resourceType });

    [Theory]
    [InlineData(MarketplaceJobTypes.HepsiburadaWebhookIngest, "ORDER_EVENT", "CLAIM_EVENT")]
    [InlineData(MarketplaceJobTypes.HepsiburadaWebhookIngest, "CLAIM_PACKAGE", "RETURNS")]
    [InlineData(MarketplaceJobTypes.HepsiburadaWebhookIngest, "ORDER_EVENT", "RETURNS")]
    public void Deliveries_in_different_lock_groups_write_different_telemetry_cursors(string jobType, string orderResource, string returnResource)
    {
        var orderPayload = Payload(orderResource);
        var returnPayload = Payload(returnResource);
        Assert.NotEqual(
            MarketplaceSyncExecutionLock.WebhookIngestGroup(MarketplaceSyncExecutionLock.WebhookResourceType(orderPayload)),
            MarketplaceSyncExecutionLock.WebhookIngestGroup(MarketplaceSyncExecutionLock.WebhookResourceType(returnPayload)));

        Assert.NotEqual(MarketplaceJobProcessor.TelemetryResource(jobType, orderPayload), MarketplaceJobProcessor.TelemetryResource(jobType, returnPayload));
        Assert.Equal("WEBHOOK_INGEST", MarketplaceJobProcessor.TelemetryResource(jobType, orderPayload));
        Assert.Equal(MarketplaceJobProcessor.WebhookIngestReturnsResource, MarketplaceJobProcessor.TelemetryResource(jobType, returnPayload));
    }

    [Fact]
    public void Other_jobs_and_unreadable_payloads_keep_their_cursor()
    {
        Assert.Equal("WEBHOOK_INGEST", MarketplaceJobProcessor.TelemetryResource(MarketplaceJobTypes.WebhookIngest, Payload("ORDERS")));
        Assert.Equal("WEBHOOK_INGEST", MarketplaceJobProcessor.TelemetryResource(MarketplaceJobTypes.ShopifyWebhookIngest, Payload("REFUNDS")));
        Assert.Equal("WEBHOOK_INGEST", MarketplaceJobProcessor.TelemetryResource(MarketplaceJobTypes.WebhookIngest, null));
        Assert.Equal("WEBHOOK_INGEST", MarketplaceJobProcessor.TelemetryResource(MarketplaceJobTypes.WebhookIngest, "not json"));
        Assert.Equal("RETURNS", MarketplaceJobProcessor.TelemetryResource(MarketplaceJobTypes.ReturnSync, Payload("RETURNS")));
        Assert.Equal("ORDERS_HOT", MarketplaceJobProcessor.TelemetryResource(MarketplaceJobTypes.OrderSync, "{}"));
    }
}

// Audit 2026-10-24 Q4: a Hepsiburada ORDER_EVENT that ran successfully marks its inbox row processed, like every other
// notice. Runs against PostgreSQL; the rows it adds are removed afterwards.
[Collection("postgres-integration")]
public sealed class HepsiburadaWebhookInboxPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task A_successful_order_event_marks_its_inbox_row_processed()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"hb-inbox-{Guid.NewGuid():N}", DisplayName = "hb-inbox", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "HEPSIBURADA", Environment = "PRODUCTION", DisplayName = "Hepsiburada", ExternalStoreId = $"merchant-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        // An event that names no order, package or line: nothing is read from Hepsiburada and the run succeeds.
        var messageId = $"{connection.Id:N}:order_event:{Guid.NewGuid():N}";
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.InboxMessages.Add(new InboxMessage { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Source = "HEPSIBURADA_WEBHOOK", ExternalMessageId = messageId, PayloadHash = Guid.NewGuid().ToString("N"), ReceivedAt = now });
        await db.SaveChangesAsync();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var payload = JsonSerializer.Serialize(new { rawJson = "{}", externalMessageId = messageId, resourceType = "ORDER_EVENT" });

        var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.HepsiburadaWebhookIngest, payload, "hb-inbox", CancellationToken.None);

        Assert.True(result.Succeeded);
        var processedAt = await db.InboxMessages.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.ExternalMessageId == messageId).Select(x => x.ProcessedAt).SingleAsync();
        Assert.NotNull(processedAt);
        await transaction.RollbackAsync();
    }
}
