using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Persistence;

// Order notifications (webhooks) a user turns on for a connection. The panel creates the
// receiving address (a secret route plus Basic Auth credentials of its own), registers it at the
// marketplace and keeps the marketplace's webhook id. Only Trendyol registers through its API.
public sealed class InstantNotificationService(
    AppDbContext db,
    IWebhookRegistrationPort registrations,
    TokenHasher tokenHasher,
    IDataProtectionProvider dataProtection,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<InstantNotificationService> logger) : IInstantNotificationService
{
    // Panel-managed subscriptions are marked by their Basic Auth user name prefix.
    internal const string ManagedUserPrefix = "ravencia-";
    private static readonly string[] ForbiddenUrlParts = ["trendyol", "dolap", "localhost"];
    private readonly IDataProtector protector = dataProtection.CreateProtector("MarketplaceHub.WebhookVerifier.v1");

    public async Task<ServiceResult<InstantNotificationView>> StatusAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(tenantId, connectionId, cancellationToken);
        if (connection is null) return NotFound();
        if (connection.PlatformCode != "TRENDYOL") return ServiceResult<InstantNotificationView>.Ok(new("UNSUPPORTED", null, null, null, 0, "Bu pazaryeri için anlık bildirim henüz panelden açılamıyor."));
        var subscription = await ManagedSubscriptionAsync(tenantId, connectionId, cancellationToken);
        if (subscription is null) return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, null, "OFF", null, cancellationToken));

        var remote = await registrations.ListWebhooksAsync(Context(tenantId, connectionId, correlationId, "webhook-status"), cancellationToken);
        if (!remote.IsSuccess)
            return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, subscription, "UNKNOWN", $"Trendyol'daki durum okunamadı: {remote.Error!.SafeMessage}", cancellationToken));
        var match = remote.Value!.FirstOrDefault(webhook => webhook.Id == subscription.ExternalSubscriptionId);
        var state = match is null ? "MISSING" : match.Status == "ACTIVE" ? "ACTIVE" : "PASSIVE";
        var gaps = ScheduledJobProducer.WebhookGaps(await db.SyncCursors.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == WebhookPollingPolicy.HealthCursorResource)
            .Select(x => x.OpaqueCursor)
            .FirstOrDefaultAsync(cancellationToken));
        var healthy = WebhookPollingPolicy.IsHealthy(subscription.LastReceivedAt, subscription.VerifiedAt, gaps, timeProvider.GetUtcNow());
        var message = state switch
        {
            "MISSING" => "Trendyol'da bu bildirim kaydı bulunamadı; kapatıp yeniden açabilirsiniz.",
            "PASSIVE" => "Trendyol bildirimleri durdurmuş; panel bir saat içinde yeniden açar.",
            _ when !healthy => "Bildirimler son saatlerde beklenen siparişleri getirmedi; sipariş kontrolü dakikada bir sürüyor.",
            _ => "Bildirimler düzenli geliyor; sipariş kontrolü yedek olarak 15 dakikada bir yapılıyor."
        };
        return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, subscription, state, message, cancellationToken));
    }

    public async Task<ServiceResult<InstantNotificationView>> EnableAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(tenantId, connectionId, cancellationToken);
        if (connection is null) return NotFound();
        if (connection.PlatformCode != "TRENDYOL")
            return ServiceResult<InstantNotificationView>.Fail("INSTANT_NOTIFICATIONS_UNSUPPORTED", "Bu pazaryeri için anlık bildirim henüz panelden açılamıyor.", 422);
        if (connection.Status is not ("ACTIVE" or "VERIFIED"))
            return ServiceResult<InstantNotificationView>.Fail("CONNECTION_INACTIVE", "Bağlantı pasif olduğu için anlık bildirim açılamaz.", 422);
        if (PublicBaseUrl() is not { } baseUrl)
            return ServiceResult<InstantNotificationView>.Fail("WEBHOOK_PUBLIC_URL_INVALID", "Panelin dışarıdan erişilen HTTPS adresi tanımlı değil ya da Trendyol'un kabul etmediği bir ifade içeriyor.", 422);
        var existing = await ManagedSubscriptionAsync(tenantId, connectionId, cancellationToken);
        if (existing is not null) return await StatusAsync(tenantId, connectionId, correlationId, cancellationToken);

        var routeToken = TokenHasher.NewToken();
        var username = ManagedUserPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        // The receiver accepts only active subscriptions, so the row is active before Trendyol
        // is told about it; a first delivery that arrives meanwhile is not rejected.
        var subscription = new WebhookSubscription
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            RouteTokenHash = tokenHasher.Hash(routeToken),
            AuthenticationType = "BASIC_AUTHENTICATION",
            ProtectedVerifierSecret = protector.Protect(JsonSerializer.Serialize(new VerifierPayload(username, password, null, null))),
            Status = "ACTIVE",
            Version = 1
        };
        db.WebhookSubscriptions.Add(subscription);
        await db.SaveChangesAsync(cancellationToken);

        var url = $"{baseUrl}/api/v1/hooks/{connection.PublicId:D}/{routeToken}";
        var created = await registrations.CreateWebhookAsync(Context(tenantId, connectionId, correlationId, "webhook-create"), url, username, password, cancellationToken);
        if (!created.IsSuccess)
        {
            db.WebhookSubscriptions.Remove(subscription);
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<InstantNotificationView>.Fail("WEBHOOK_REGISTRATION_FAILED", $"Trendyol bildirim kaydını kabul etmedi: {created.Error!.SafeMessage}", created.Error.HttpStatus is >= 400 and < 500 ? 422 : 502);
        }
        subscription.ExternalSubscriptionId = created.Value;
        subscription.VerifiedAt = timeProvider.GetUtcNow();
        subscription.Version++;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Trendyol order webhook {WebhookId} registered for connection {ConnectionId}", created.Value, connectionId);
        return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, subscription, "ACTIVE", null, cancellationToken));
    }

    public async Task<ServiceResult<InstantNotificationView>> DisableAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(tenantId, connectionId, cancellationToken);
        if (connection is null) return NotFound();
        var subscription = await ManagedSubscriptionAsync(tenantId, connectionId, cancellationToken, tracked: true);
        if (subscription is null) return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, null, "OFF", null, cancellationToken));
        if (subscription.ExternalSubscriptionId is { } externalId)
        {
            var deleted = await registrations.DeleteWebhookAsync(Context(tenantId, connectionId, correlationId, "webhook-delete"), externalId, cancellationToken);
            if (!deleted.IsSuccess)
                return ServiceResult<InstantNotificationView>.Fail("WEBHOOK_REMOVAL_FAILED", $"Trendyol bildirim kaydı silinemedi: {deleted.Error!.SafeMessage}", 502);
        }
        subscription.Status = "DELETED";
        subscription.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, null, "OFF", null, cancellationToken));
    }

    public async Task<int> ReactivatePassiveAsync(CancellationToken cancellationToken)
    {
        var subscriptions = await (from subscription in db.WebhookSubscriptions.AsNoTracking()
                                   join connection in db.PlatformConnections.AsNoTracking() on subscription.ConnectionId equals connection.Id
                                   where subscription.Status == "ACTIVE" && subscription.ExternalSubscriptionId != null
                                       && connection.PlatformCode == "TRENDYOL" && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")
                                   select new { subscription.TenantId, subscription.ConnectionId, subscription.ExternalSubscriptionId })
            .ToListAsync(cancellationToken);
        var reactivated = 0;
        foreach (var subscription in subscriptions)
        {
            var context = Context(subscription.TenantId, subscription.ConnectionId, $"webhook-upkeep:{subscription.ConnectionId:N}", "webhook-upkeep");
            var remote = await registrations.ListWebhooksAsync(context, cancellationToken);
            if (!remote.IsSuccess) continue;
            var match = remote.Value!.FirstOrDefault(webhook => webhook.Id == subscription.ExternalSubscriptionId);
            if (match is null || match.Status == "ACTIVE") continue;
            var activated = await registrations.ActivateWebhookAsync(context, subscription.ExternalSubscriptionId!, cancellationToken);
            if (activated.IsSuccess)
            {
                reactivated++;
                logger.LogInformation("Trendyol order webhook {WebhookId} was passive and has been reactivated", subscription.ExternalSubscriptionId);
            }
            else logger.LogWarning("Trendyol order webhook {WebhookId} could not be reactivated: {Code}", subscription.ExternalSubscriptionId, activated.Error?.Code);
        }
        return reactivated;
    }

    // The receiving address must be HTTPS and, for Trendyol, must not name Trendyol, Dolap or localhost.
    internal string? PublicBaseUrl()
    {
        var configured = configuration["Marketplace:PublicBaseUrl"]?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(configured) || !Uri.TryCreate(configured, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        return ForbiddenUrlParts.Any(part => configured.Contains(part, StringComparison.OrdinalIgnoreCase)) ? null : configured;
    }

    private async Task<InstantNotificationView> ViewAsync(PlatformConnection connection, WebhookSubscription? subscription, string state, string? message, CancellationToken cancellationToken)
    {
        var since = timeProvider.GetUtcNow().AddHours(-24);
        var ingestType = MarketplaceJobTypes.ForPlatform(connection.PlatformCode, MarketplaceJobTypes.WebhookIngest);
        var received = subscription is null ? 0 : await db.IntegrationJobs.AsNoTracking()
            .CountAsync(job => job.TenantId == connection.TenantId && job.ConnectionId == connection.Id && job.JobType == ingestType && job.CreatedAt >= since, cancellationToken);
        // The route token is a secret; the page shows only where notifications arrive.
        var endpoint = PublicBaseUrl() is { } baseUrl ? $"{baseUrl}/api/v1/hooks/{connection.PublicId:D}/…" : null;
        return new(state, endpoint, subscription?.VerifiedAt, subscription?.LastReceivedAt, received, message);
    }

    private Task<PlatformConnection?> ConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);

    private Task<WebhookSubscription?> ManagedSubscriptionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken, bool tracked = false)
    {
        var query = db.WebhookSubscriptions.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "ACTIVE" && x.ExternalSubscriptionId != null);
        return (tracked ? query : query.AsNoTracking()).OrderByDescending(x => x.Id).FirstOrDefaultAsync(cancellationToken);
    }

    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string purpose) =>
        new(tenantId, connectionId, correlationId, $"{purpose}:{connectionId:N}", timeProvider.GetUtcNow().AddSeconds(30));

    private static ServiceResult<InstantNotificationView> NotFound() =>
        ServiceResult<InstantNotificationView>.Fail("RESOURCE_NOT_FOUND", "Bağlantı bulunamadı.", 404);

    private sealed record VerifierPayload(string? Username, string? Password, string? ApiKey, string? ClientSecret);
}
