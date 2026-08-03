using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class F3WebhookService(AppDbContext db, TokenHasher tokenHasher, IWebhookVerifier verifier, ShopifyWebhookVerifier shopifyVerifier, TimeProvider timeProvider) : IF3WebhookService
{
    public async Task<ServiceResult<bool>> ReceiveAsync(Guid connectionPublicId, string routeToken, ReadOnlyMemory<byte> rawBody, IReadOnlyDictionary<string, string> headers, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.PublicId == connectionPublicId && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == ShopifyContract.PlatformCode) && x.Status == "ACTIVE", cancellationToken); if (connection is null) return NotFound();
        var subscriptions = await db.WebhookSubscriptions.AsNoTracking().Where(x => x.TenantId == connection.TenantId && x.ConnectionId == connection.Id && x.Status == "ACTIVE").ToListAsync(cancellationToken); var subscription = subscriptions.SingleOrDefault(x => SafeVerify(routeToken, x.RouteTokenHash)); if (subscription is null) return NotFound();
        var selectedVerifier = connection.PlatformCode == ShopifyContract.PlatformCode ? (IWebhookVerifier)shopifyVerifier : verifier;
        var verified = await selectedVerifier.VerifyAsync(rawBody, headers, connection.Id, subscription.Id, cancellationToken); if (!verified.IsSuccess) return ServiceResult<bool>.Fail(verified.Error!.Code, verified.Error.SafeMessage, verified.Error.HttpStatus ?? 422);
        var source = connection.PlatformCode == ShopifyContract.PlatformCode ? "SHOPIFY_WEBHOOK" : "TRENDYOL_WEBHOOK"; var jobType = connection.PlatformCode == ShopifyContract.PlatformCode ? ShopifyContract.WebhookIngestJob : F3JobTypes.WebhookIngest;
        var envelope = verified.Value!; var exists = await db.InboxMessages.AsNoTracking().AnyAsync(x => x.TenantId == connection.TenantId && x.Source == source && x.ExternalMessageId == envelope.ExternalMessageId, cancellationToken); if (exists) return ServiceResult<bool>.Ok(true);
        var now = timeProvider.GetUtcNow(); db.InboxMessages.Add(new InboxMessage { Id = Guid.CreateVersion7(), TenantId = connection.TenantId, Source = source, ExternalMessageId = envelope.ExternalMessageId, PayloadHash = envelope.PayloadHash, ReceivedAt = now });
        var payload = JsonSerializer.Serialize(new { connectionId = connection.Id, externalMessageId = envelope.ExternalMessageId, rawJson = envelope.RawJson }); var dedup = $"webhook:{connection.Id}:{envelope.ExternalMessageId}"; db.IntegrationJobs.Add(new IntegrationJob { Id = Guid.CreateVersion7(), TenantId = connection.TenantId, ConnectionId = connection.Id, JobType = jobType, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), JobDedupKey = dedup, EffectIdempotencyKey = dedup, AvailableAt = now, CorrelationId = correlationId, Version = 1 });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsDuplicateCandidate(exception))
        {
            db.ChangeTracker.Clear();
            var committed = await db.InboxMessages.AsNoTracking().AnyAsync(x => x.TenantId == connection.TenantId && x.Source == source && x.ExternalMessageId == envelope.ExternalMessageId && x.PayloadHash == envelope.PayloadHash, cancellationToken)
                && await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == connection.TenantId && x.JobType == jobType && x.JobDedupKey == dedup, cancellationToken);
            if (!committed) throw;
        }
        await db.WebhookSubscriptions.Where(x => x.TenantId == connection.TenantId && x.Id == subscription.Id && x.Status == "ACTIVE").ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastReceivedAt, now).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }
    private static bool IsDuplicateCandidate(DbUpdateException exception) => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    private bool SafeVerify(string token, string hash) { try { return tokenHasher.Verify(token, hash); } catch (FormatException) { return false; } }
    private static ServiceResult<bool> NotFound() => ServiceResult<bool>.Fail("WEBHOOK_ROUTE_NOT_FOUND", "Webhook route bulunamadı.", 404);
}
