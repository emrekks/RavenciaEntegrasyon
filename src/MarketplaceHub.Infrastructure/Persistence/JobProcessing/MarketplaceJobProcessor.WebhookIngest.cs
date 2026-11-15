using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    private async Task<bool> IngestWebhook(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        string raw; string externalMessageId; string resourceType;
        try { using var payload = JsonDocument.Parse(payloadJson); raw = payload.RootElement.GetProperty("rawJson").GetString() ?? ""; externalMessageId = payload.RootElement.GetProperty("externalMessageId").GetString() ?? ""; resourceType = payload.RootElement.TryGetProperty("resourceType", out var type) ? type.GetString() ?? "" : ""; }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }

        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        var source = $"{platform}_WEBHOOK";
        if (platform == "HEPSIBURADA")
        {
            try
            {
                using var document = JsonDocument.Parse(raw);
                // Order and claim events end through the same inbox close as every other notice (audit 2026-10-24 Q4:
                // returning early left their inbox rows unprocessed after a successful run).
                if (string.Equals(resourceType, "ORDER_EVENT", StringComparison.Ordinal))
                {
                    if (!await ReadHepsiburadaOrderEvent(tenantId, connectionId, document.RootElement, externalMessageId, OrderEventPath(externalMessageId), correlationId, cancellationToken)) return false;
                }
                // A claim notice the mapper cannot read whole: the recent claims are read again.
                else if (string.Equals(resourceType, "CLAIM_EVENT", StringComparison.Ordinal))
                {
                    if (!await SyncReturns(tenantId, connectionId, JsonSerializer.Serialize(new { connectionId }), correlationId, cancellationToken)) return false;
                }
                else if (string.Equals(resourceType, "CLAIM_PACKAGE", StringComparison.Ordinal))
                {
                    var order = HepsiburadaJsonMapper.ClaimPackageOrder(document.RootElement);
                    TrackReceived();
                    // A claim names its order's line but not its date, VAT, title or discount: for an order the panel
                    // already has only the package is merged (an older modification time keeps the order and its lines).
                    var knownOrderModifiedAt = await db.Orders.AsNoTracking()
                        .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalOrderId == order.ExternalOrderId)
                        .Select(x => (DateTimeOffset?)x.LastRemoteModifiedAt).FirstOrDefaultAsync(cancellationToken);
                    if (knownOrderModifiedAt is { } knownModifiedAt && order.LastModifiedAt >= knownModifiedAt)
                        order = order with { LastModifiedAt = knownModifiedAt.AddTicks(-1) };
                    await UpsertOrder(tenantId, connectionId, order, cancellationToken);
                    foreach (var claimElement in HepsiburadaJsonMapper.ClaimPackageClaims(document.RootElement))
                    {
                        var claim = HepsiburadaJsonMapper.ReturnClaim(claimElement);
                        await UpsertReturn(tenantId, connectionId, claim, cancellationToken);
                    }
                }
                else
                {
                    var claim = HepsiburadaJsonMapper.ReturnClaim(document.RootElement);
                    TrackReceived();
                    await UpsertReturn(tenantId, connectionId, claim, cancellationToken);
                }
            }
            catch (JsonException) { return false; }
        }
        else if (platform == "SHOPIFY")
        {
            if (!configuration.GetValue("Marketplace:PersistOrderSnapshots", true)) return true;
            string? externalOrderId = null;
            try
            {
                using var document = JsonDocument.Parse(raw);
                var orderIdProperty = string.Equals(resourceType, "REFUNDS", StringComparison.Ordinal) ? "order_id" : "id";
                if (document.RootElement.TryGetProperty(orderIdProperty, out var id))
                    externalOrderId = id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : id.GetString();
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException) { return false; }
            if (string.IsNullOrWhiteSpace(externalOrderId)) return false;
            TrackRequest();
            var order = await orders.GetAsync(Context(tenantId, connectionId, $"webhook:{externalMessageId}", $"shopify-webhook:{connectionId}:{externalMessageId}"), externalOrderId, cancellationToken);
            if (!order.IsSuccess) { TrackResultFailure(order.Error); throw JobProcessingException.FromAdapter(order.Error!); }
            TrackReceived();
            await UpsertOrder(tenantId, connectionId, order.Value!, cancellationToken, persistFinancialObservations: true);
        }
        else
        {
            AdapterPageResult<RemoteOrder> page; try { page = TrendyolJsonMapper.Orders(raw); } catch (JsonException) { return false; }
            foreach (var issue in page.Issues ?? [])
                await RecordIssue(tenantId, $"order-webhook-contract:{connectionId}:{issue.Identity}:{issue.Code}", issue.Code, issue.Message, cancellationToken);
            // The webhook carries no storefront header; orderCountryCode names the customer's
            // storefront (TR, DE, AE…), which later targeted reads ask first.
            if (configuration.GetValue("Marketplace:PersistOrderSnapshots", true))
                await UpsertOrders(tenantId, connectionId, [.. page.Items.Select(order => order with { StoreFrontCode = TrendyolWebhookStorefront(order.RawJson) })], cancellationToken);
        }
        var inbox = await db.InboxMessages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Source == source && x.ExternalMessageId == externalMessageId, cancellationToken); if (inbox is not null) inbox.ProcessedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return true;
    }
}
