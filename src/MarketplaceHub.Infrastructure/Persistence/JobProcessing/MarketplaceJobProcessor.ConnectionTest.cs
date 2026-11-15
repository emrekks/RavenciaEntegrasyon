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
    private async Task<bool> TestConnection(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "SHOPIFY" || x.PlatformCode == "HEPSIBURADA" || x.PlatformCode == "N11" || x.PlatformCode == "PAZARAMA" || x.PlatformCode == "PTTAVM"), cancellationToken); if (connection is null) return false; var now = timeProvider.GetUtcNow(); connection.LastTestedAt = now;
        IConnectionPort port = connections;
        var context = Context(tenantId, connectionId, correlationId, "connection-test"); TrackRequest(); var result = await port.TestAsync(context, cancellationToken); if (!result.IsSuccess) { TrackResultFailure(result.Error); connection.LastErrorCode = result.Error!.Code; connection.LastErrorClass = result.Error.Class.ToString(); connection.Version++; await db.SaveChangesAsync(cancellationToken); throw JobProcessingException.FromAdapter(result.Error!); }
        TrackRequest(); var discovery = await port.DiscoverCapabilitiesAsync(context, cancellationToken); if (!discovery.IsSuccess) { TrackResultFailure(discovery.Error); connection.LastErrorCode = discovery.Error!.Code; connection.LastErrorClass = discovery.Error.Class.ToString(); connection.Version++; await db.SaveChangesAsync(cancellationToken); throw JobProcessingException.FromAdapter(discovery.Error!); }
        if (connection.PlatformCode == "SHOPIFY")
        {
            connection.ApiVersion = result.Value!.ApiVersion;
            connection.Environment = result.Value.Environment;
            connection.ExternalStoreId = result.Value.ExternalStoreId;
        }
        // PTT AVM: the store the API user belongs to (a store name typed as the Mağaza ID gives way to it).
        if (connection.PlatformCode == "PTTAVM") connection.ExternalStoreId = result.Value!.ExternalStoreId;
        foreach (var _ in discovery.Value!) TrackReceived();
        // The support each capability holds after this test: a probe that could not tell leaves a recent SUPPORTED in place
        // (CapabilityEvidencePolicy.KeepsEarlierSupport), so a transient failure neither downgrades it nor fails the test.
        var supportedAfter = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidence in discovery.Value!)
        {
            var capability = await db.PlatformCapabilities.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == evidence.Code, cancellationToken);
            if (capability is null)
            {
                capability = new PlatformCapability
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    ConnectionId = connectionId,
                    Code = evidence.Code,
                    ApiVersion = connection.ApiVersion,
                    Environment = connection.Environment,
                    StoreScope = connection.ExternalStoreId,
                    Version = 0
                };
                db.PlatformCapabilities.Add(capability);
            }
            if (!CapabilityEvidencePolicy.KeepsEarlierSupport(capability, evidence, now)) CapabilityEvidencePolicy.ApplyEvidence(capability, evidence);
            if (capability.SupportLevel == CapabilitySupportLevel.Supported) supportedAfter.Add(evidence.Code);
        }
        if (connection.PlatformCode is "HEPSIBURADA" or "N11" or "PAZARAMA" or "PTTAVM" && discovery.Value is not null
            && !new[] { MarketplaceCapabilities.ProductRead, MarketplaceCapabilities.OrderRead }.All(supportedAfter.Contains))
        {
            connection.LastErrorCode = $"{connection.PlatformCode}_REQUIRED_READ_CAPABILITY";
            connection.LastErrorClass = nameof(AdapterErrorClass.NotSupported);
            connection.Version++;
            await db.SaveChangesAsync(cancellationToken);
            throw JobProcessingException.FromAdapter(new AdapterError(AdapterErrorClass.NotSupported, connection.LastErrorCode,
                $"{(connection.PlatformCode switch { "N11" => "N11", "PAZARAMA" => "Pazarama", "PTTAVM" => "PttAVM", _ => "Hepsiburada" })} bağlantısı etkinleştirilemez: ürün ve sipariş okuma yeteneklerinin ikisi de doğrulanmalıdır.", null, null, null));
        }
        if (connection.PlatformCode == "SHOPIFY" && discovery.Value is not null
            && !new[] { MarketplaceCapabilities.ProductRead, MarketplaceCapabilities.OrderRead }.All(code => discovery.Value.Any(item => item.Code == code && item.SupportLevel == "SUPPORTED")))
        {
            connection.LastErrorCode = "SHOPIFY_REQUIRED_READ_SCOPE";
            connection.LastErrorClass = nameof(AdapterErrorClass.Authentication);
            connection.Version++;
            await db.SaveChangesAsync(cancellationToken);
            var failedCapabilities = discovery.Value
                .Where(item => item.Code is MarketplaceCapabilities.ProductRead or MarketplaceCapabilities.OrderRead)
                .Where(item => !string.Equals(item.SupportLevel, "SUPPORTED", StringComparison.Ordinal))
                .Select(item => item.EvidenceNote)
                .Where(note => !string.IsNullOrWhiteSpace(note))
                .ToArray();
            var detail = failedCapabilities.Length == 0
                ? "Ürün ve sipariş okuma izinleri doğrulanamadı."
                : string.Join(" ", failedCapabilities);
            throw JobProcessingException.FromAdapter(new AdapterError(AdapterErrorClass.Authentication, "SHOPIFY_REQUIRED_READ_SCOPE", $"{detail} Gerekli izinler: read_products, read_inventory, read_orders ve read_locations.", 403, null, null));
        }
        connection.LastSuccessAt = now; connection.LastErrorCode = null; connection.LastErrorClass = null; if (connection.Status == "DRAFT") connection.Status = "VERIFIED"; connection.Version++; await db.SaveChangesAsync(cancellationToken); return true;
    }
}
