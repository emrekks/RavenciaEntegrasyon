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
    // Hepsiburada registers webhooks by hand (a request from the seller), so the panel only
    // prepares the address and credentials; its subscriptions carry this marker instead of an id.
    internal const string ManualRegistration = "MANUAL";
    internal static readonly string[] HepsiburadaPaths =
    [
        "POST /orders", "POST /packages", "PUT /lineitems/{lineitemid}/cancel",
        "PUT /packages/{packagenumber}/unpack", "PUT /packages/{packagenumber}/intransit",
        "PUT /packages/{packagenumber}/deliver", "PUT /packages/{packagenumber}/undeliver",
        "PUT /orders/{ordernumber}/shippingaddress",
        "PUT /claims/awaitingaction", "PUT /claims/awaitingpreapproval", "PUT /claims/accept", "PUT /claims/reject", "POST /claims/packages"
    ];
    private static readonly string[] ForbiddenUrlParts = ["trendyol", "dolap", "localhost"];
    private readonly IDataProtector protector = dataProtection.CreateProtector("MarketplaceHub.WebhookVerifier.v1");

    public async Task<ServiceResult<InstantNotificationView>> StatusAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(tenantId, connectionId, cancellationToken);
        if (connection is null) return NotFound();
        if (connection.PlatformCode is not ("TRENDYOL" or "HEPSIBURADA" or "SHOPIFY")) return ServiceResult<InstantNotificationView>.Ok(new("UNSUPPORTED", null, null, null, 0, "Bu pazaryeri için anlık bildirim henüz panelden açılamıyor."));
        var subscription = await ManagedSubscriptionAsync(tenantId, connectionId, cancellationToken);
        if (subscription is null) return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, null, "OFF", null, cancellationToken));
        if (subscription.ExternalSubscriptionId == ManualRegistration)
        {
            // A connection that is not active refuses every delivery (the address answers 404), so the panel says so
            // instead of waiting for Hepsiburada.
            if (connection.Status is not ("ACTIVE" or "VERIFIED"))
                return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, subscription, "CONNECTION_INACTIVE", "Bağlantı aktif değil; Hepsiburada'dan gelen bildirimler bağlantı etkinleşene kadar reddediliyor. Bağlantıyı kontrol edip etkinleştirin.", cancellationToken));
            // Nothing to ask Hepsiburada: the first notification shows that it has set the address up. After that,
            // silence alone says nothing (a store without new orders sends nothing, 23 Oct 2026 on SIT): only an order
            // the regular read brought in with no notification since shows the notifications have stopped.
            // Before the first notification, an order the regular read brought in after the definition was prepared
            // tells a definition that is not working from one that has had nothing to send.
            var now = timeProvider.GetUtcNow();
            var missed = await ScheduledJobProducer.UnnotifiedOrderAsync(db, tenantId, connectionId, subscription.LastReceivedAt ?? PreparedAt(subscription.Id), now, cancellationToken);
            var silent = subscription.LastReceivedAt is { } last && now - last > WebhookPollingPolicy.SilenceLimit;
            var manualState = subscription.LastReceivedAt is null ? "WAITING" : missed ? "QUIET" : "ACTIVE";
            var manualMessage = manualState switch
            {
                "WAITING" when missed => "Bilgiler hazırlandıktan sonra gelen sipariş için Hepsiburada'dan bildirim gelmedi; Hepsiburada'ya tanımın bu adres ve şifreyle yapıldığını sorun. Siparişler düzenli kontrolle gelmeye devam ediyor.",
                "WAITING" => "Bilgiler hazırlandığından beri yeni sipariş gelmedi; Hepsiburada adresi tanımladıysa ilk siparişle bildirim gelir. O zamana kadar siparişler düzenli kontrolle gelir.",
                "QUIET" => "Son bildirimden sonra gelen sipariş için Hepsiburada'dan bildirim gelmedi; siparişler düzenli kontrolle gelmeye devam ediyor. Hepsiburada'dan webhook tanımını kontrol etmesini isteyin.",
                _ when silent => "Bildirimler çalışıyor; son bildirimden bu yana yeni sipariş gelmedi. Sipariş kontrolü yedek olarak 8 dakikada bir yapılıyor.",
                _ => "Bildirimler geliyor; sipariş kontrolü yedek olarak 8 dakikada bir yapılıyor."
            };
            return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, subscription, manualState, manualMessage, cancellationToken));
        }

        var remote = await registrations.ListWebhooksAsync(Context(tenantId, connectionId, correlationId, "webhook-status"), cancellationToken);
        if (!remote.IsSuccess)
            return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, subscription, "UNKNOWN", $"Trendyol'daki durum okunamadı: {remote.Error!.SafeMessage}", cancellationToken));
        var state = RemoteState(subscription.ExternalSubscriptionId!, remote.Value!);
        var gaps = ScheduledJobProducer.WebhookGaps(await db.SyncCursors.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == WebhookPollingPolicy.HealthCursorResource)
            .Select(x => x.OpaqueCursor)
            .FirstOrDefaultAsync(cancellationToken));
        var healthy = WebhookPollingPolicy.IsHealthy(subscription.LastReceivedAt, subscription.VerifiedAt, gaps, timeProvider.GetUtcNow(),
            await ScheduledJobProducer.UnnotifiedOrderAsync(db, tenantId, connectionId, subscription.LastReceivedAt ?? subscription.VerifiedAt, timeProvider.GetUtcNow(), cancellationToken));
        var message = state switch
        {
            "MISSING" => $"{PlatformName(connection.PlatformCode)}'da bu bildirim kaydı bulunamadı; kapatıp yeniden açabilirsiniz.",
            "PASSIVE" => connection.PlatformCode == "SHOPIFY"
                ? "Shopify bildirim aboneliklerinin bir kısmını kaldırmış; panel bir saat içinde yeniden oluşturur."
                : "Trendyol bildirimleri durdurmuş; panel bir saat içinde yeniden açar.",
            _ when !healthy => $"Son siparişin bildirimi gelmedi; sipariş kontrolü {await OrderReadMinutesAsync(tenantId, connectionId, cancellationToken)} dakikada bir sürüyor.",
            _ => "Bildirimler düzenli geliyor; sipariş kontrolü yedek olarak 8 dakikada bir yapılıyor."
        };
        return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, subscription, state, message, cancellationToken));
    }

    // The cadence of the connection's own order read (the fallback while notifications are not coming), in whole minutes.
    private async Task<int> OrderReadMinutesAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var seconds = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ORDERS")
            .Select(x => (int?)x.IntervalSeconds)
            .FirstOrDefaultAsync(cancellationToken) ?? ScheduledOrderPollingCadencePolicy.UnnotifiedOrderIntervalSeconds;
        return Math.Max(1, (int)Math.Round(Math.Clamp(seconds, 30, ScheduledOrderPollingCadencePolicy.MaximumIntervalSeconds) / 60d));
    }

    public async Task<ServiceResult<InstantNotificationView>> EnableAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken, string? clientSecret = null)
    {
        var connection = await ConnectionAsync(tenantId, connectionId, cancellationToken);
        if (connection is null) return NotFound();
        if (connection.PlatformCode is not ("TRENDYOL" or "HEPSIBURADA" or "SHOPIFY"))
            return ServiceResult<InstantNotificationView>.Fail("INSTANT_NOTIFICATIONS_UNSUPPORTED", "Bu pazaryeri için anlık bildirim henüz panelden açılamıyor.", 422);
        // Shopify signs deliveries with the app's client secret. The connection's own secret is used
        // when it has one; without it the secret route alone admits a delivery, which is safe because
        // a Shopify notification only makes the panel read that order from Shopify again.
        clientSecret = string.IsNullOrWhiteSpace(clientSecret)
            ? connection.PlatformCode == "SHOPIFY" ? await StoredShopifyClientSecretAsync(tenantId, connectionId, cancellationToken) : null
            : clientSecret.Trim();
        if (clientSecret is { Length: > 200 })
            return ServiceResult<InstantNotificationView>.Fail("SHOPIFY_CLIENT_SECRET_INVALID", "Shopify gizli anahtarı geçersiz.", 422);
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
        var shopify = connection.PlatformCode == "SHOPIFY";
        var subscription = new WebhookSubscription
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            RouteTokenHash = tokenHasher.Hash(routeToken),
            // Shopify signs each delivery (HMAC) with the client secret instead of Basic Auth.
            AuthenticationType = shopify ? clientSecret is null ? "ROUTE_TOKEN" : "HMAC_SHA256" : "BASIC_AUTHENTICATION",
            ProtectedVerifierSecret = BoundSecret.ProtectForConnection(protector, tenantId, connectionId, JsonSerializer.Serialize(shopify ? new VerifierPayload(null, null, null, clientSecret) : new VerifierPayload(username, password, null, null))),
            Status = "ACTIVE",
            Version = 1
        };
        var url = $"{baseUrl}/api/v1/hooks/{connection.PublicId:D}/{routeToken}";
        if (connection.PlatformCode == "HEPSIBURADA")
        {
            // Prepared only: no request goes to Hepsiburada. The seller sends these details.
            subscription.ExternalSubscriptionId = ManualRegistration;
            db.WebhookSubscriptions.Add(subscription);
            await db.SaveChangesAsync(cancellationToken);
            var setup = new WebhookSetupInstructions(connection.Environment == "PRODUCTION" ? "Canlı" : "Test (SIT)", url, username, password, HepsiburadaPaths);
            return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, subscription, "WAITING", "Bilgileri Hepsiburada'ya iletin. Şifre yalnız bu ekranda gösterilir.", cancellationToken) with { Setup = setup });
        }
        db.WebhookSubscriptions.Add(subscription);
        await db.SaveChangesAsync(cancellationToken);

        var created = await registrations.CreateWebhookAsync(Context(tenantId, connectionId, correlationId, "webhook-create"), url, username, password, cancellationToken);
        if (!created.IsSuccess)
        {
            db.WebhookSubscriptions.Remove(subscription);
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<InstantNotificationView>.Fail("WEBHOOK_REGISTRATION_FAILED", $"{PlatformName(connection.PlatformCode)} bildirim kaydını kabul etmedi: {created.Error!.SafeMessage}", created.Error.HttpStatus is >= 400 and < 500 ? 422 : 502);
        }
        subscription.ExternalSubscriptionId = created.Value;
        subscription.VerifiedAt = timeProvider.GetUtcNow();
        subscription.Version++;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("{Platform} order webhook {WebhookId} registered for connection {ConnectionId}", connection.PlatformCode, created.Value, connectionId);
        return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, subscription, "ACTIVE", null, cancellationToken));
    }

    public async Task<ServiceResult<InstantNotificationView>> DisableAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(tenantId, connectionId, cancellationToken);
        if (connection is null) return NotFound();
        var subscription = await ManagedSubscriptionAsync(tenantId, connectionId, cancellationToken, tracked: true);
        if (subscription is null) return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, null, "OFF", null, cancellationToken));
        if (subscription.ExternalSubscriptionId is { } externalId && externalId != ManualRegistration)
        {
            var deleted = await registrations.DeleteWebhookAsync(Context(tenantId, connectionId, correlationId, "webhook-delete"), externalId, cancellationToken);
            if (!deleted.IsSuccess)
                return ServiceResult<InstantNotificationView>.Fail("WEBHOOK_REMOVAL_FAILED", $"{PlatformName(connection.PlatformCode)} bildirim kaydı silinemedi: {deleted.Error!.SafeMessage}", 502);
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
                                   where subscription.Status == "ACTIVE" && subscription.ExternalSubscriptionId != null && subscription.ExternalSubscriptionId != ManualRegistration
                                       && (connection.PlatformCode == "TRENDYOL" || connection.PlatformCode == "SHOPIFY") && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")
                                   select new { subscription.Id, subscription.TenantId, subscription.ConnectionId, subscription.ExternalSubscriptionId, connection.PlatformCode })
            .ToListAsync(cancellationToken);
        var reactivated = 0;
        foreach (var subscription in subscriptions)
        {
            var context = Context(subscription.TenantId, subscription.ConnectionId, $"webhook-upkeep:{subscription.ConnectionId:N}", "webhook-upkeep");
            if (subscription.PlatformCode == "SHOPIFY")
            {
                // Shopify drops subscriptions after repeated failures; the dropped topics are made again.
                var restored = await registrations.RestoreMissingWebhooksAsync(context, subscription.ExternalSubscriptionId!, cancellationToken);
                if (!restored.IsSuccess) { logger.LogWarning("Shopify order webhooks of connection {ConnectionId} could not be restored: {Code}", subscription.ConnectionId, restored.Error?.Code); continue; }
                if (restored.Value is not { } ids) continue;
                await db.WebhookSubscriptions.Where(x => x.Id == subscription.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ExternalSubscriptionId, ids).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
                reactivated++;
                logger.LogInformation("Shopify order webhooks of connection {ConnectionId} were restored", subscription.ConnectionId);
                continue;
            }
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

    // Trendyol keeps one webhook; Shopify one subscription per topic, all of which must be listed.
    internal static string RemoteState(string externalId, IReadOnlyList<RemoteWebhook> remote)
    {
        var ids = externalId.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var listed = remote.Where(webhook => ids.Contains(webhook.Id, StringComparer.Ordinal)).ToList();
        if (listed.Count == 0) return "MISSING";
        return listed.Count < ids.Length || listed.Any(webhook => webhook.Status != "ACTIVE") ? "PASSIVE" : "ACTIVE";
    }

    private static string PlatformName(string platformCode) => platformCode switch
    {
        "SHOPIFY" => "Shopify",
        "HEPSIBURADA" => "Hepsiburada",
        _ => "Trendyol"
    };

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
        var endpoint = PublicBaseUrl() is { } baseUrl ? $"{baseUrl}/api/v1/hooks/{subscription?.RoutePublicId ?? connection.PublicId:D}/…" : null;
        // The last delivery the receiver turned away (MarketplaceWebhookService): it tells a marketplace that calls
        // and is refused from one that never calls.
        var rejectedPrefix = $"webhook-rejected:{connection.Id:N}:";
        var rejected = await db.OperationalIssues.AsNoTracking()
            .Where(x => x.TenantId == connection.TenantId && x.DedupeKey.StartsWith(rejectedPrefix) && x.Status == IssueStatus.Open)
            .OrderByDescending(x => x.LastSeenAt)
            .Select(x => new { x.LastSeenAt, x.Summary })
            .FirstOrDefaultAsync(cancellationToken);
        // A Hepsiburada definition is never verified by a call; it was opened when it was prepared.
        var manual = subscription?.ExternalSubscriptionId == ManualRegistration;
        var registeredAt = subscription?.VerifiedAt ?? (manual ? PreparedAt(subscription!.Id) : null);
        var previous = connection.PlatformCode == "HEPSIBURADA" && subscription?.LastReceivedAt is null
            ? await PreviousDefinitionAsync(connection.TenantId, connection.Id, subscription, tracked: false, cancellationToken)
            : null;
        var adoptable = connection.PlatformCode == "HEPSIBURADA" && subscription?.LastReceivedAt is null && connection.Status is "ACTIVE" or "VERIFIED"
            ? await AdoptableDefinitionAsync(connection, cancellationToken)
            : null;
        return new(state, endpoint, registeredAt, subscription?.LastReceivedAt, received, message, LastRejectedAt: rejected?.LastSeenAt, LastRejectedReason: rejected?.Summary,
            PreviousPreparedAt: previous is null ? null : PreparedAt(previous.Id),
            AdoptableFrom: adoptable is null ? null : new(adoptable.Value.Source.DisplayName, adoptable.Value.Source.Environment == "PRODUCTION" ? "Canlı" : "Test (SIT)", PreparedAt(adoptable.Value.Subscription.Id)));
    }

    public async Task<ServiceResult<InstantNotificationView>> RestorePreviousAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(tenantId, connectionId, cancellationToken);
        if (connection is null) return NotFound();
        if (connection.PlatformCode != "HEPSIBURADA")
            return ServiceResult<InstantNotificationView>.Fail("INSTANT_NOTIFICATIONS_RESTORE_UNSUPPORTED", "Önceki tanım yalnız Hepsiburada için geri yüklenir.", 422);
        if (connection.Status is not ("ACTIVE" or "VERIFIED"))
            return ServiceResult<InstantNotificationView>.Fail("CONNECTION_INACTIVE", "Bağlantı pasif olduğu için webhook tanımı geri yüklenemez.", 422);
        var current = await ManagedSubscriptionAsync(tenantId, connectionId, cancellationToken, tracked: true);
        // A definition Hepsiburada already delivers to is the one it has; it is not swapped for an older one.
        if (current?.LastReceivedAt is not null)
            return ServiceResult<InstantNotificationView>.Fail("WEBHOOK_IN_USE", "Şu anki tanıma Hepsiburada'dan bildirim geliyor; önceki tanım geri yüklenmez.", 409);
        var previous = await PreviousDefinitionAsync(tenantId, connectionId, current, tracked: true, cancellationToken);
        if (previous is null)
            return ServiceResult<InstantNotificationView>.Fail("WEBHOOK_PREVIOUS_MISSING", "Geri yüklenecek önceki bir Hepsiburada tanımı yok.", 404);
        if (current is not null)
        {
            current.Status = "DELETED";
            current.Version++;
        }
        previous.Status = "ACTIVE";
        previous.Version++;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Hepsiburada webhook definition {SubscriptionId} restored for connection {ConnectionId}", previous.Id, connectionId);
        return await StatusAsync(tenantId, connectionId, correlationId, cancellationToken);
    }

    public async Task<ServiceResult<InstantNotificationView>> AdoptFromInactiveAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(tenantId, connectionId, cancellationToken);
        if (connection is null) return NotFound();
        if (connection.PlatformCode != "HEPSIBURADA")
            return ServiceResult<InstantNotificationView>.Fail("INSTANT_NOTIFICATIONS_ADOPT_UNSUPPORTED", "Başka bağlantının tanımı yalnız Hepsiburada için taşınır.", 422);
        if (connection.Status is not ("ACTIVE" or "VERIFIED"))
            return ServiceResult<InstantNotificationView>.Fail("CONNECTION_INACTIVE", "Bağlantı pasif olduğu için webhook tanımı taşınamaz.", 422);
        var current = await ManagedSubscriptionAsync(tenantId, connectionId, cancellationToken, tracked: true);
        if (current?.LastReceivedAt is not null)
            return ServiceResult<InstantNotificationView>.Fail("WEBHOOK_IN_USE", "Şu anki tanıma Hepsiburada'dan bildirim geliyor; başka tanım taşınmaz.", 409);
        if (await AdoptableDefinitionAsync(connection, cancellationToken) is not { } adoptable)
            return ServiceResult<InstantNotificationView>.Fail("WEBHOOK_ADOPTABLE_MISSING", "Taşınacak bir Hepsiburada tanımı yok: pasif bir Hepsiburada bağlantısında açık tanım bulunamadı.", 404);
        // Read again on its own to be tracked: the query that found it is a no-tracking one.
        var source = adoptable.Source;
        var moved = await db.WebhookSubscriptions.SingleAsync(x => x.TenantId == tenantId && x.Id == adoptable.Subscription.Id, cancellationToken);
        string verifier;
        try { verifier = BoundSecret.UnprotectForConnection(protector, tenantId, source.Id, moved.ProtectedVerifierSecret); }
        catch (CryptographicException) { return ServiceResult<InstantNotificationView>.Fail("WEBHOOK_SECRET_UNREADABLE", "Tanımın şifresi okunamadı; taşınamaz.", 422); }
        if (current is not null)
        {
            current.Status = "DELETED";
            current.Version++;
        }
        // The address keeps the other connection's public id; the password is protected again for this connection, and
        // what that connection received (test deliveries) is not counted here.
        moved.RoutePublicId ??= source.PublicId;
        moved.ConnectionId = connectionId;
        moved.ProtectedVerifierSecret = BoundSecret.ProtectForConnection(protector, tenantId, connectionId, verifier);
        moved.LastReceivedAt = null;
        moved.Version++;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Hepsiburada webhook definition {SubscriptionId} moved from connection {SourceConnectionId} to {ConnectionId}", moved.Id, source.Id, connectionId);
        return await StatusAsync(tenantId, connectionId, correlationId, cancellationToken);
    }

    // The newest active Hepsiburada definition of another Hepsiburada connection of the store that does not receive (not
    // active): one a test connection was given, that Hepsiburada may have been asked to set up for the live store.
    private async Task<(PlatformConnection Source, WebhookSubscription Subscription)?> AdoptableDefinitionAsync(PlatformConnection connection, CancellationToken cancellationToken)
    {
        var found = await (from subscription in db.WebhookSubscriptions.AsNoTracking().Where(x => x.TenantId == connection.TenantId && x.ConnectionId != connection.Id && x.Status == "ACTIVE" && x.ExternalSubscriptionId == ManualRegistration)
                           join source in db.PlatformConnections.AsNoTracking() on new { subscription.TenantId, subscription.ConnectionId } equals new { source.TenantId, ConnectionId = source.Id }
                           where source.PlatformCode == "HEPSIBURADA" && source.Status != "ACTIVE" && source.Status != "VERIFIED" && source.Status != "DELETED"
                           orderby subscription.Id descending
                           select new { Source = source, Subscription = subscription }).FirstOrDefaultAsync(cancellationToken);
        return found is null ? null : (found.Source, found.Subscription);
    }

    // The last closed Hepsiburada definition prepared before the current one (ids are version 7, so they order by
    // time). One closed after it was restored over is newer and is not offered back; over a definition moved here from
    // another connection, which may be older than this connection's own, the last closed one is offered whatever its age.
    private async Task<WebhookSubscription?> PreviousDefinitionAsync(Guid tenantId, Guid connectionId, WebhookSubscription? current, bool tracked, CancellationToken cancellationToken)
    {
        var query = db.WebhookSubscriptions.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "DELETED" && x.ExternalSubscriptionId == ManualRegistration);
        var previous = await (tracked ? query : query.AsNoTracking()).OrderByDescending(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        return previous is not null && current is { RoutePublicId: null } && PreparedAt(previous.Id) >= PreparedAt(current.Id) ? null : previous;
    }

    // A version 7 id begins with the Unix milliseconds of its creation: when a definition was prepared.
    internal static DateTimeOffset PreparedAt(Guid id) => DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(id.ToString("N")[..12], 16));

    private Task<PlatformConnection?> ConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);

    private Task<WebhookSubscription?> ManagedSubscriptionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken, bool tracked = false)
    {
        var query = db.WebhookSubscriptions.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "ACTIVE" && x.ExternalSubscriptionId != null);
        return (tracked ? query : query.AsNoTracking()).OrderByDescending(x => x.Id).FirstOrDefaultAsync(cancellationToken);
    }

    // The client secret of a Shopify connection saved with client credentials; token-only connections have none.
    private async Task<string?> StoredShopifyClientSecretAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var protectedPayload = await db.PlatformCredentials.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.ProtectedPayload)
            .FirstOrDefaultAsync(cancellationToken);
        if (protectedPayload is null) return null;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredShopifyCredential>(BoundSecret.UnprotectForConnection(dataProtection.CreateProtector("MarketplaceHub.PlatformCredential.v1"), tenantId, connectionId, protectedPayload));
            var secret = stored?.ClientSecret ?? stored?.ApiSecret;
            return string.IsNullOrWhiteSpace(secret) ? null : secret.Trim();
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException) { return null; }
    }

    private sealed record StoredShopifyCredential(string? ClientSecret, string? ApiSecret);

    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string purpose) =>
        new(tenantId, connectionId, correlationId, $"{purpose}:{connectionId:N}", timeProvider.GetUtcNow().AddSeconds(30));

    private static ServiceResult<InstantNotificationView> NotFound() =>
        ServiceResult<InstantNotificationView>.Fail("RESOURCE_NOT_FOUND", "Bağlantı bulunamadı.", 404);

    private sealed record VerifierPayload(string? Username, string? Password, string? ApiKey, string? ClientSecret);
}
