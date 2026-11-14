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
    // Passive Trendyol webhooks this scope has already removed at Trendyol (or found gone), so RetirePausedAsync does not
    // ask Trendyol to remove them a second time.
    private readonly HashSet<string> removedAtMarketplace = new(StringComparer.Ordinal);
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
        if (subscription is null)
        {
            // A webhook closed when the connection was set Pasif (PauseForInactiveConnectionAsync) opens again with it.
            var paused = await db.WebhookSubscriptions.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == PausedStatus, cancellationToken);
            return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, null, "OFF", paused ? PausedMessage(connection) : null, cancellationToken));
        }
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

        var platform = PlatformName(connection.PlatformCode);
        // A connection that is not active refuses every delivery (the receiver admits active connections only), so the
        // registration is not called open whatever the marketplace lists.
        if (connection.Status is not ("ACTIVE" or "VERIFIED"))
            return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, subscription, "CONNECTION_INACTIVE", $"Bağlantı aktif değil; {platform}'dan gelen bildirimler bağlantı etkinleşene kadar reddediliyor. Bağlantıyı kontrol edip etkinleştirin.", cancellationToken));
        var remote = await registrations.ListWebhooksAsync(Context(tenantId, connectionId, correlationId, "webhook-status"), cancellationToken);
        if (!remote.IsSuccess)
            return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, subscription, "UNKNOWN", $"{platform}'daki durum okunamadı: {remote.Error!.SafeMessage}", cancellationToken));
        var remoteState = RemoteState(subscription.ExternalSubscriptionId!, remote.Value!);
        var gaps = ScheduledJobProducer.WebhookGaps(await db.SyncCursors.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == WebhookPollingPolicy.HealthCursorResource)
            .Select(x => x.OpaqueCursor)
            .FirstOrDefaultAsync(cancellationToken));
        var healthy = WebhookPollingPolicy.IsHealthy(subscription.LastReceivedAt, subscription.VerifiedAt, gaps, timeProvider.GetUtcNow(),
            await ScheduledJobProducer.UnnotifiedOrderAsync(db, tenantId, connectionId, subscription.LastReceivedAt ?? subscription.VerifiedAt, timeProvider.GetUtcNow(), cancellationToken));
        var state = RegisteredState(remoteState, subscription.LastReceivedAt, healthy);
        // While the notifications count as healthy the order read is only the safety net; otherwise it runs at its own cadence.
        var readMinutes = healthy ? WebhookPollingPolicy.SafetyNetSeconds / 60 : await OrderReadMinutesAsync(tenantId, connectionId, cancellationToken);
        return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, subscription, state, RegisteredMessage(connection.PlatformCode, state, healthy, readMinutes), cancellationToken));
    }

    // A registration the marketplace lists as active is "ACTIVE" only once a notification has come and the notifications
    // are healthy: before the first one it waits (WAITING), and an order that came without its notification makes it
    // QUIET. A missing or stopped registration keeps the marketplace's answer.
    internal static string RegisteredState(string remoteState, DateTimeOffset? lastReceivedAt, bool healthy) =>
        remoteState != "ACTIVE" ? remoteState
        : lastReceivedAt is null ? "WAITING"
        : healthy ? "ACTIVE"
        : "QUIET";

    internal static string RegisteredMessage(string platformCode, string state, bool healthy, int readMinutes)
    {
        var platform = PlatformName(platformCode);
        return state switch
        {
            "MISSING" => $"{platform}'da bu bildirim kaydı bulunamadı; kapatıp yeniden açabilirsiniz.",
            "PASSIVE" => platformCode == "SHOPIFY"
                ? "Shopify bildirim aboneliklerinin bir kısmını kaldırmış; panel bir saat içinde yeniden oluşturur."
                : "Trendyol bildirimleri durdurmuş; panel bir saat içinde yeniden açar.",
            "WAITING" when !healthy => $"Henüz bildirim gelmedi: webhook açıldıktan sonra gelen sipariş için {platform}'dan bildirim gelmedi. Siparişler düzenli kontrolle {readMinutes} dakikada bir gelmeye devam ediyor.",
            "WAITING" => $"Henüz bildirim gelmedi; {platform} ilk yeni siparişte ya da sipariş değişikliğinde bildirim gönderir. O zamana kadar sipariş kontrolü {readMinutes} dakikada bir yapılıyor.",
            "QUIET" => $"Son siparişin bildirimi {platform}'dan gelmedi; sipariş kontrolü {readMinutes} dakikada bir sürüyor.",
            _ => $"Bildirimler düzenli geliyor; sipariş kontrolü yedek olarak {readMinutes} dakikada bir yapılıyor."
        };
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
        // An access token typed in place of the secret would make every delivery fail its signature.
        if (connection.PlatformCode == "SHOPIFY" && clientSecret is not null && IsShopifyAccessToken(clientSecret))
            return ServiceResult<InstantNotificationView>.Fail("SHOPIFY_CLIENT_SECRET_INVALID", "Webhook gizli anahtarı alanına erişim anahtarı girilmiş. Shopify bildirimleri uygulamanın API Secret Key'i ile imzalar; o anahtarı girin ya da alanı boş bırakın.", 422);
        if (connection.Status is not ("ACTIVE" or "VERIFIED"))
            return ServiceResult<InstantNotificationView>.Fail("CONNECTION_INACTIVE", "Bağlantı pasif olduğu için anlık bildirim açılamaz.", 422);
        if (PublicBaseUrl() is not { } baseUrl)
            return ServiceResult<InstantNotificationView>.Fail("WEBHOOK_PUBLIC_URL_INVALID", "Panelin dışarıdan erişilen HTTPS adresi tanımlı değil ya da Trendyol'un kabul etmediği bir ifade içeriyor.", 422);
        var existing = await ManagedSubscriptionAsync(tenantId, connectionId, cancellationToken);
        if (existing is not null) return await StatusAsync(tenantId, connectionId, correlationId, cancellationToken);
        // A registration an earlier attempt left unclear is settled first: the marketplace may already hold it.
        if (connection.PlatformCode is "TRENDYOL" or "SHOPIFY")
        {
            await SettlePendingRegistrationsAsync(connection, TimeSpan.Zero, correlationId, cancellationToken);
            if (await ManagedSubscriptionAsync(tenantId, connectionId, cancellationToken) is not null)
            {
                await RetirePausedAsync(tenantId, connectionId, correlationId, cancellationToken);
                return await StatusAsync(tenantId, connectionId, correlationId, cancellationToken);
            }
        }

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
            // A timeout or server fault may hide a registration the marketplace made: the row is kept until the marketplace's
            // list shows whether one points at this address (adopted) or not (the row goes), never dropped blind.
            if (WriteOutcomePolicy.IsUncertain(created.Error!))
            {
                var settled = await SettleRegistrationAsync(connection, subscription, correlationId, cancellationToken);
                if (settled == RegistrationSettlement.Unknown)
                    return ServiceResult<InstantNotificationView>.Fail("WEBHOOK_REGISTRATION_UNCERTAIN", $"{PlatformName(connection.PlatformCode)} bildirim kaydının sonucu belirsiz ({created.Error!.SafeMessage.TrimEnd('.')}). Panel kaydı {PlatformName(connection.PlatformCode)}'daki listeden saatte bir kontrol eder; varsa sahiplenir, yoksa kaldırır.", 502);
                if (settled == RegistrationSettlement.Absent)
                    return ServiceResult<InstantNotificationView>.Fail("WEBHOOK_REGISTRATION_FAILED", $"{PlatformName(connection.PlatformCode)} bildirim kaydını kabul etmedi: {created.Error!.SafeMessage}", 502);
            }
            else
            {
                db.WebhookSubscriptions.Remove(subscription);
                await db.SaveChangesAsync(cancellationToken);
                return ServiceResult<InstantNotificationView>.Fail("WEBHOOK_REGISTRATION_FAILED", $"{PlatformName(connection.PlatformCode)} bildirim kaydını kabul etmedi: {created.Error!.SafeMessage}", created.Error.HttpStatus is >= 400 and < 500 ? 422 : 502);
            }
        }
        else
        {
            subscription.ExternalSubscriptionId = created.Value;
            subscription.VerifiedAt = timeProvider.GetUtcNow();
            subscription.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }
        logger.LogInformation("{Platform} order webhook {WebhookId} registered for connection {ConnectionId}", connection.PlatformCode, subscription.ExternalSubscriptionId, connectionId);
        // A webhook opened again replaces the one Pasife Al closed.
        await RetirePausedAsync(tenantId, connectionId, correlationId, cancellationToken);
        // Registered, but no notification has come yet: the same state the status read gives until the first one.
        return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, subscription, "WAITING", RegisteredMessage(connection.PlatformCode, "WAITING", healthy: true, WebhookPollingPolicy.SafetyNetSeconds / 60), cancellationToken));
    }

    public async Task<ServiceResult<InstantNotificationView>> DisableAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(tenantId, connectionId, cancellationToken);
        if (connection is null) return NotFound();
        var subscription = await ManagedSubscriptionAsync(tenantId, connectionId, cancellationToken, tracked: true);
        if (subscription is null)
        {
            // A Trendyol webhook Pasife Al set passive is still registered there: closing it (Webhook'u Kapat, Bağlantıyı
            // Sil) removes it at Trendyol and it no longer opens with the connection.
            var passive = connection.PlatformCode == "TRENDYOL"
                ? await db.WebhookSubscriptions.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == PausedStatus && x.ExternalSubscriptionId != null && x.ExternalSubscriptionId != ManualRegistration)
                    .OrderByDescending(x => x.Id).FirstOrDefaultAsync(cancellationToken)
                : null;
            if (passive is not null)
            {
                var removed = await registrations.DeleteWebhookAsync(Context(tenantId, connectionId, correlationId, "webhook-delete"), passive.ExternalSubscriptionId!, cancellationToken);
                if (!removed.IsSuccess)
                    return ServiceResult<InstantNotificationView>.Fail("WEBHOOK_REMOVAL_FAILED", $"{PlatformName(connection.PlatformCode)} bildirim kaydı silinemedi: {removed.Error!.SafeMessage}", 502);
                await CloseAsync(passive, "DELETED", cancellationToken);
                await RetirePausedAsync(tenantId, connectionId, correlationId, cancellationToken);
            }
            // A Shopify webhook Pasife Al closed has no subscription left at Shopify: Webhook'u Kapat retires its kept
            // definition, so it no longer opens again with the connection.
            if (connection.PlatformCode == "SHOPIFY")
            {
                await RetirePausedAsync(tenantId, connectionId, correlationId, cancellationToken);
                await ResolvePendingAsync(tenantId, ResumePendingKey(connectionId), cancellationToken);
            }
            return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, null, "OFF", null, cancellationToken));
        }
        if (subscription.ExternalSubscriptionId is { } externalId && externalId != ManualRegistration)
        {
            var deleted = await registrations.DeleteWebhookAsync(Context(tenantId, connectionId, correlationId, "webhook-delete"), externalId, cancellationToken);
            if (!deleted.IsSuccess)
                return ServiceResult<InstantNotificationView>.Fail("WEBHOOK_REMOVAL_FAILED", $"{PlatformName(connection.PlatformCode)} bildirim kaydı silinemedi: {deleted.Error!.SafeMessage}", 502);
        }
        // Closed by hand while the connection is Pasif (a Pasife Al whose own close failed): it is the webhook the
        // connection had, so it opens again with the connection, as one Pasife Al closed does.
        var paused = connection.Status == "DISABLED" && subscription.ExternalSubscriptionId != ManualRegistration && connection.PlatformCode is "TRENDYOL" or "SHOPIFY";
        await CloseAsync(subscription, paused ? PausedStatus : "DELETED", cancellationToken);
        await ResolvePendingAsync(tenantId, PausePendingKey(connectionId), cancellationToken);
        return ServiceResult<InstantNotificationView>.Ok(await ViewAsync(connection, null, "OFF", paused ? PausedMessage(connection) : null, cancellationToken));
    }

    private async Task CloseAsync(WebhookSubscription subscription, string status, CancellationToken cancellationToken)
    {
        subscription.Status = status;
        subscription.Version++;
        await db.SaveChangesAsync(cancellationToken);
        // The refusals were the closed definition's: a webhook opened again (with a corrected secret) starts without them.
        var rejectedPrefix = MarketplaceWebhookService.RejectedPrefix(subscription.ConnectionId);
        var now = timeProvider.GetUtcNow();
        await db.OperationalIssues
            .Where(x => x.TenantId == subscription.TenantId && x.Status != IssueStatus.Resolved && x.DedupeKey.StartsWith(rejectedPrefix))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, IssueStatus.Resolved).SetProperty(x => x.ResolvedAt, now), cancellationToken);
    }

    // Pasife Al closes the connection's webhook at the marketplace (Trendyol, Shopify) with the call Webhook'u Kapat
    // makes, and keeps the closed definition as PAUSED so that activating the connection opens it again. A failure does
    // not stop the deactivation (the receiver refuses a passive connection's deliveries anyway): the returned note says
    // so, and the hourly upkeep tries again. Hepsiburada is registered by hand and stays as it is.
    public async Task<string?> PauseForInactiveConnectionAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(tenantId, connectionId, cancellationToken);
        if (connection is null) return null;
        await ResolvePendingAsync(tenantId, ResumePendingKey(connectionId), cancellationToken);
        var subscription = await ManagedSubscriptionAsync(tenantId, connectionId, cancellationToken, tracked: true);
        // An opening that ended unclear (no marketplace id yet) may still be a live webhook at the marketplace: it is not
        // taken as "nothing to close". The record stays pending; the hourly upkeep settles the registration against the
        // marketplace's list and closes what it adopts (audit 14 Nov 2026).
        if (subscription is null && connection.PlatformCode is "TRENDYOL" or "SHOPIFY" && await HasUnclearRegistrationAsync(tenantId, connectionId, cancellationToken))
        {
            var unclearNote = $"{PlatformName(connection.PlatformCode)} webhook'unun açılışı belirsiz kaldı (pazaryerinin cevabı gelmedi). Panel saatte bir pazaryerindeki kaydı kontrol edip kapatır; bağlantı pasifken gelen bildirimler işlenmez.";
            await NotePendingAsync(tenantId, PausePendingKey(connectionId), "WEBHOOK_PAUSE_PENDING", $"{connection.DisplayName}: {unclearNote}", cancellationToken);
            return unclearNote;
        }
        if (subscription is null || connection.PlatformCode is not ("TRENDYOL" or "SHOPIFY" or "HEPSIBURADA"))
        {
            await ResolvePendingAsync(tenantId, PausePendingKey(connectionId), cancellationToken);
            return null;
        }
        if (subscription.ExternalSubscriptionId == ManualRegistration) return HepsiburadaPauseNote;
        var platform = PlatformName(connection.PlatformCode);
        // Written before the marketplace is called (InlineExecutionBudget rule): a request cut off midway (a shutdown, the
        // budget's hard stop, a crash) leaves the close pending for the hourly upkeep instead of a webhook left open on a
        // passive connection with no trace. A close that goes through resolves it below.
        await NotePendingAsync(tenantId, PausePendingKey(connectionId), "WEBHOOK_PAUSE_PENDING",
            $"{connection.DisplayName}: {platform} webhook'u pasife alınırken kapatılıyor; yarıda kalırsa panel saatte bir yeniden dener.", cancellationToken);
        // Owner, 13 Nov 2026: Trendyol's webhook is set passive (its deactivate service), keeping its id and address for
        // the activation; Shopify has no passive state, so its subscriptions are removed and made again on activation.
        var trendyol = connection.PlatformCode == "TRENDYOL";
        string? failure;
        try
        {
            var context = Context(tenantId, connectionId, correlationId, "webhook-pause");
            var closed = trendyol
                ? await registrations.DeactivateWebhookAsync(context, subscription.ExternalSubscriptionId!, cancellationToken)
                : await registrations.DeleteWebhookAsync(context, subscription.ExternalSubscriptionId!, cancellationToken);
            // A Trendyol webhook that is gone already receives nothing either; the activation makes a new one.
            failure = closed.IsSuccess || trendyol && closed.Error!.Class == AdapterErrorClass.NotFound ? null : closed.Error!.SafeMessage;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            failure = $"{platform}'a ulaşılamadı";
        }
        if (failure is not null)
        {
            var note = $"{platform} webhook'u {(trendyol ? "pasife alınamadı" : "kapatılamadı")}. Neden: {failure.TrimEnd('.')}. Bağlantı pasifken gelen bildirimler yine işlenmez; Webhook bölümünden Webhook'u Kapat'a basın, panel de saatte bir yeniden dener.";
            await NotePendingAsync(tenantId, PausePendingKey(connectionId), "WEBHOOK_PAUSE_FAILED", $"{connection.DisplayName}: {note}", cancellationToken);
            logger.LogWarning("Order webhook of passive connection {ConnectionId} could not be closed at {Platform}", connectionId, connection.PlatformCode);
            return note;
        }
        await CloseAsync(subscription, PausedStatus, cancellationToken);
        await ResolvePendingAsync(tenantId, PausePendingKey(connectionId), cancellationToken);
        logger.LogInformation("{Platform} order webhook of connection {ConnectionId} closed while the connection is passive", connection.PlatformCode, connectionId);
        return null;
    }

    // Activating a connection opens the webhook Pasife Al closed, with the call Webhook'u Aç makes (a new address and
    // registration; a Shopify webhook keeps the secret it was signed with). A failure does not stop the activation: the
    // returned note asks for Webhook'u Aç, and the hourly upkeep tries again.
    public async Task<string?> ResumeAfterActivationAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(tenantId, connectionId, cancellationToken);
        if (connection is null || connection.Status is not ("ACTIVE" or "VERIFIED")) return null;
        await ResolvePendingAsync(tenantId, PausePendingKey(connectionId), cancellationToken);
        var paused = await db.WebhookSubscriptions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == PausedStatus)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var platform = PlatformName(connection.PlatformCode);
        // A closed webhook to open again: pending before the marketplace is called, so an opening cut off midway is retried
        // hourly (FinishResumeAsync resolves it, ResumeFailedAsync keeps it with the reason).
        if (paused is not null)
            await NotePendingAsync(tenantId, ResumePendingKey(connectionId), "WEBHOOK_RESUME_PENDING",
                $"{connection.DisplayName}: {platform} webhook'u bağlantı etkinleşirken yeniden açılıyor; yarıda kalırsa panel saatte bir yeniden dener.", cancellationToken);
        string? failure;
        if (paused is null || await ManagedSubscriptionAsync(tenantId, connectionId, cancellationToken) is not null)
        {
            await RetirePausedAsync(tenantId, connectionId, correlationId, cancellationToken);
            // A Shopify registration the panel keeps as open is checked against Shopify's list: Shopify removes a
            // subscription after eight failed deliveries in a row, and one removed altogether is opened anew.
            if (connection.PlatformCode == "SHOPIFY" && await ManagedSubscriptionAsync(tenantId, connectionId, cancellationToken) is { ExternalSubscriptionId: not ManualRegistration } shopify)
            {
                try
                {
                    var check = await EnsureShopifyRegisteredAsync(tenantId, connectionId, shopify.Id, correlationId, cancellationToken);
                    failure = check.Outcome == ShopifyRegistrationCheck.Failed ? check.Failure : null;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    failure = $"{platform}'a ulaşılamadı";
                }
                if (failure is not null) return await ResumeFailedAsync(connection, failure, cancellationToken);
            }
            await FinishResumeAsync(tenantId, connectionId, cancellationToken);
            return null;
        }
        // Trendyol: the webhook Pasife Al set passive is activated again (same id and address). One Trendyol no longer has,
        // or that refuses to activate, is removed and a new one is made, as Webhook'u Aç does. A quota wait, a network or
        // server fault leaves the passive webhook as it is: the hourly upkeep activates the same one again.
        if (connection.PlatformCode == "TRENDYOL" && paused.ExternalSubscriptionId is { } pausedId && pausedId != ManualRegistration)
        {
            var (outcome, reason) = await ActivatePausedTrendyolAsync(connection, paused.Id, pausedId, correlationId, cancellationToken);
            if (outcome == PausedActivation.Activated)
            {
                await FinishResumeAsync(tenantId, connectionId, cancellationToken);
                return null;
            }
            if (outcome == PausedActivation.Later) return await ResumeFailedAsync(connection, reason ?? $"{platform}'a ulaşılamadı", cancellationToken);
        }
        try
        {
            var enabled = await EnableAsync(tenantId, connectionId, correlationId, cancellationToken, PausedClientSecret(paused));
            failure = enabled.Succeeded ? null : enabled.Error!.Message;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            failure = $"{platform}'a ulaşılamadı";
        }
        if (failure is not null) return await ResumeFailedAsync(connection, failure, cancellationToken);
        await FinishResumeAsync(tenantId, connectionId, cancellationToken);
        return null;
    }

    private async Task<string> ResumeFailedAsync(PlatformConnection connection, string failure, CancellationToken cancellationToken)
    {
        var note = $"{PlatformName(connection.PlatformCode)} webhook'u yeniden açılamadı. Neden: {failure.TrimEnd('.')}. Webhook bölümünden Webhook'u Aç'a basın; panel saatte bir yeniden dener, siparişler bu sürede düzenli kontrolle gelir.";
        await NotePendingAsync(connection.TenantId, ResumePendingKey(connection.Id), "WEBHOOK_RESUME_FAILED", $"{connection.DisplayName}: {note}", cancellationToken);
        logger.LogWarning("Order webhook of reactivated connection {ConnectionId} could not be opened again at {Platform}", connection.Id, connection.PlatformCode);
        return note;
    }

    internal enum PausedActivation { Activated, Recreate, Later }

    // Whether a failed activation of a passive Trendyol webhook waits for the next try: a quota wait or the panel's own
    // circuit (the request never left), a network or server fault. Only a refusal or a webhook Trendyol no longer has
    // makes a new one; deleting a sound passive webhook for a passing fault left ownerless copies behind.
    internal static bool ActivationRetriesLater(AdapterError? error) =>
        error?.Class is AdapterErrorClass.RateLimit or AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx;

    // Activates the Trendyol webhook Pasife Al set passive and opens its row again (the same address and credentials).
    // Recreate when Trendyol refuses or no longer has it: that webhook is removed (best effort; RetirePausedAsync tries
    // again once the new one is open, so no passive copy is left counting towards Trendyol's 15) and the caller makes a
    // new one. Later when the failure passes on its own: the row stays PAUSED and the hourly upkeep tries again.
    private async Task<(PausedActivation Outcome, string? Reason)> ActivatePausedTrendyolAsync(PlatformConnection connection, Guid pausedRowId, string externalId, string correlationId, CancellationToken cancellationToken)
    {
        var context = Context(connection.TenantId, connection.Id, correlationId, "webhook-resume");
        AdapterResult<bool> activated;
        try { activated = await registrations.ActivateWebhookAsync(context, externalId, cancellationToken); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            activated = AdapterResult<bool>.Failure(new(AdapterErrorClass.TransientNetwork, "WEBHOOK_ACTIVATE_UNREACHABLE", "Trendyol'a ulaşılamadı.", null, null, null));
        }
        if (activated.IsSuccess)
        {
            var row = await db.WebhookSubscriptions.SingleAsync(x => x.TenantId == connection.TenantId && x.Id == pausedRowId, cancellationToken);
            row.Status = "ACTIVE";
            row.Version++;
            await db.SaveChangesAsync(cancellationToken);
            await RetirePausedAsync(connection.TenantId, connection.Id, correlationId, cancellationToken);
            logger.LogInformation("Trendyol order webhook {WebhookId} of connection {ConnectionId} activated again with the connection", externalId, connection.Id);
            return (PausedActivation.Activated, null);
        }
        if (ActivationRetriesLater(activated.Error))
        {
            logger.LogWarning("Trendyol order webhook {WebhookId} of connection {ConnectionId} did not activate ({Code}); it is tried again hourly", externalId, connection.Id, activated.Error?.Code);
            return (PausedActivation.Later, activated.Error?.SafeMessage);
        }
        logger.LogWarning("Trendyol order webhook {WebhookId} of connection {ConnectionId} did not activate ({Code}); a new one is made", externalId, connection.Id, activated.Error?.Code);
        if (activated.Error?.Class == AdapterErrorClass.NotFound) removedAtMarketplace.Add(externalId);
        else
        {
            try
            {
                var removed = await registrations.DeleteWebhookAsync(context, externalId, cancellationToken);
                if (removed.IsSuccess) removedAtMarketplace.Add(externalId);
                else logger.LogWarning("Passive Trendyol order webhook {WebhookId} could not be removed: {Code}", externalId, removed.Error?.Code);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Passive Trendyol order webhook {WebhookId} could not be removed", externalId);
            }
        }
        return (PausedActivation.Recreate, null);
    }

    internal enum ShopifyRegistrationCheck { Unchanged, Restored, Recreated, Failed }

    // Checks a Shopify registration the panel keeps as open against Shopify's list: missing topics are made again, and
    // one Shopify removed altogether (eight failed deliveries in a row drop a subscription) is closed and opened anew,
    // signed with the secret it had, so the panel never shows as open a registration Shopify no longer has.
    private async Task<(ShopifyRegistrationCheck Outcome, string? Failure)> EnsureShopifyRegisteredAsync(Guid tenantId, Guid connectionId, Guid subscriptionId, string correlationId, CancellationToken cancellationToken)
    {
        var subscription = await db.WebhookSubscriptions.SingleAsync(x => x.TenantId == tenantId && x.Id == subscriptionId, cancellationToken);
        var restored = await registrations.RestoreMissingWebhooksAsync(Context(tenantId, connectionId, correlationId, "webhook-check"), subscription.ExternalSubscriptionId!, cancellationToken);
        if (restored.IsSuccess)
        {
            if (restored.Value is not { } ids) return (ShopifyRegistrationCheck.Unchanged, null);
            subscription.ExternalSubscriptionId = ids;
            subscription.Version++;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Shopify order webhooks of connection {ConnectionId} were restored", connectionId);
            return (ShopifyRegistrationCheck.Restored, null);
        }
        if (restored.Error!.Code != MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyHttpClient.WebhooksRemovedCode)
            return (ShopifyRegistrationCheck.Failed, restored.Error.SafeMessage);
        var secret = PausedClientSecret(subscription);
        // Closed as PAUSED, not DELETED: should the new opening fail (a quota wait, a refused request), the definition and
        // its secret stay for the hourly retry, which opens it again, instead of the webhook being silently gone.
        await CloseAsync(subscription, PausedStatus, cancellationToken);
        var enabled = await EnableAsync(tenantId, connectionId, correlationId, cancellationToken, secret);
        if (!enabled.Succeeded)
        {
            if (await ConnectionAsync(tenantId, connectionId, cancellationToken) is { } connection)
                await ResumeFailedAsync(connection, enabled.Error!.Message, cancellationToken);
            return (ShopifyRegistrationCheck.Failed, enabled.Error!.Message);
        }
        logger.LogInformation("Shopify had removed every order webhook of connection {ConnectionId}; they were made again", connectionId);
        return (ShopifyRegistrationCheck.Recreated, null);
    }

    // The hourly retry of a close (Pasife Al) or an opening (activation) that failed, while the connection is still in
    // the state that asked for it; a connection that has changed state since closes the record.
    public async Task<int> RetryConnectionWebhooksAsync(CancellationToken cancellationToken)
    {
        var pending = await db.OperationalIssues.AsNoTracking()
            .Where(x => x.TenantId != null && x.Status != IssueStatus.Resolved && (x.DedupeKey.StartsWith(PausePendingPrefix) || x.DedupeKey.StartsWith(ResumePendingPrefix)))
            .Select(x => new { TenantId = x.TenantId!.Value, x.DedupeKey })
            .ToListAsync(cancellationToken);
        var done = 0;
        foreach (var item in pending)
        {
            var pause = item.DedupeKey.StartsWith(PausePendingPrefix, StringComparison.Ordinal);
            if (!Guid.TryParseExact(item.DedupeKey[(pause ? PausePendingPrefix : ResumePendingPrefix).Length..], "N", out var connectionId)) continue;
            try
            {
                db.ChangeTracker.Clear();
                var connection = await ConnectionAsync(item.TenantId, connectionId, cancellationToken);
                var stillAsked = connection is not null && (pause ? connection.Status == "DISABLED" : connection.Status is "ACTIVE" or "VERIFIED");
                if (!stillAsked)
                {
                    await ResolvePendingAsync(item.TenantId, item.DedupeKey, cancellationToken);
                    continue;
                }
                var correlationId = $"webhook-upkeep:{connectionId:N}";
                var note = pause
                    ? await PauseForInactiveConnectionAsync(item.TenantId, connectionId, correlationId, cancellationToken)
                    : await ResumeAfterActivationAsync(item.TenantId, connectionId, correlationId, cancellationToken);
                if (note is null) done++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Webhook retry of connection {ConnectionId} failed", connectionId);
            }
        }
        // Registrations whose opening ended unclear (a timeout or server fault) and that are old enough not to be in
        // flight: the marketplace's list decides whether they are adopted or removed.
        db.ChangeTracker.Clear();
        var unclearConnections = await (from subscription in db.WebhookSubscriptions.AsNoTracking()
                                        join connection in db.PlatformConnections.AsNoTracking() on new { subscription.TenantId, subscription.ConnectionId } equals new { connection.TenantId, ConnectionId = connection.Id }
                                        where subscription.Status == "ACTIVE" && subscription.ExternalSubscriptionId == null && (connection.PlatformCode == "TRENDYOL" || connection.PlatformCode == "SHOPIFY")
                                        select connection).Distinct().ToListAsync(cancellationToken);
        foreach (var connection in unclearConnections)
        {
            try
            {
                db.ChangeTracker.Clear();
                var settled = await SettlePendingRegistrationsAsync(connection, PendingRegistrationGrace, $"webhook-upkeep:{connection.Id:N}", cancellationToken);
                done += settled;
                // A registration adopted for a passive connection is not left open there: it is closed as Pasife Al does
                // (a failure keeps the pause pending for the next hour).
                if (settled > 0 && connection.Status is not ("ACTIVE" or "VERIFIED"))
                {
                    db.ChangeTracker.Clear();
                    await PauseForInactiveConnectionAsync(connection.TenantId, connection.Id, $"webhook-upkeep:{connection.Id:N}", cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Unclear webhook registration of connection {ConnectionId} could not be settled", connection.Id);
            }
        }
        return done;
    }

    private Task<bool> HasUnclearRegistrationAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        db.WebhookSubscriptions.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "ACTIVE" && x.ExternalSubscriptionId == null, cancellationToken);

    // An opening younger than this may still be waiting for the marketplace's answer; the hourly upkeep leaves it alone.
    internal static readonly TimeSpan PendingRegistrationGrace = TimeSpan.FromMinutes(5);

    internal enum RegistrationSettlement { Adopted, Absent, Unknown }

    // The connection's registrations left without a marketplace id (their opening ended unclear), older than the grace,
    // settled against the marketplace's list. Returns how many were adopted or removed.
    private async Task<int> SettlePendingRegistrationsAsync(PlatformConnection connection, TimeSpan grace, string correlationId, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow() - grace;
        var unclear = (await db.WebhookSubscriptions
                .Where(x => x.TenantId == connection.TenantId && x.ConnectionId == connection.Id && x.Status == "ACTIVE" && x.ExternalSubscriptionId == null)
                .ToListAsync(cancellationToken))
            .Where(x => PreparedAt(x.Id) <= cutoff)
            .OrderByDescending(x => x.Id)
            .ToList();
        var settled = 0;
        foreach (var subscription in unclear)
            if (await SettleRegistrationAsync(connection, subscription, correlationId, cancellationToken) != RegistrationSettlement.Unknown) settled++;
        return settled;
    }

    // Reads the marketplace's webhooks and finds the ones pointing at this subscription's address (its route token). A
    // complete registration is adopted (Trendyol: one webhook; Shopify: one subscription for every topic) and any further
    // copy removed; an incomplete Shopify set is removed with the row; none found removes the row. Unknown when the
    // list (or a removal) could not be read: the row stays for the next try.
    private async Task<RegistrationSettlement> SettleRegistrationAsync(PlatformConnection connection, WebhookSubscription subscription, string correlationId, CancellationToken cancellationToken)
    {
        var context = Context(connection.TenantId, connection.Id, correlationId, "webhook-settle");
        var listed = await registrations.ListWebhooksAsync(context, cancellationToken);
        if (!listed.IsSuccess) return RegistrationSettlement.Unknown;
        var ours = listed.Value!.Where(webhook => RouteTokenHashOf(webhook.Url) is { } hash && hash == subscription.RouteTokenHash).ToList();
        var adopted = AdoptableIds(connection.PlatformCode, ours);
        var adoptedIds = adopted?.Split(MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyHttpClient.WebhookIdSeparator) ?? [];
        foreach (var extra in ours.Where(webhook => !adoptedIds.Contains(webhook.Id, StringComparer.Ordinal)))
        {
            var removed = await registrations.DeleteWebhookAsync(context, extra.Id, cancellationToken);
            if (!removed.IsSuccess && adopted is null) return RegistrationSettlement.Unknown;
        }
        if (adopted is not null)
        {
            subscription.ExternalSubscriptionId = adopted;
            subscription.VerifiedAt = timeProvider.GetUtcNow();
            subscription.Version++;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("{Platform} order webhook {WebhookId} of an unclear opening was adopted for connection {ConnectionId}", connection.PlatformCode, adopted, connection.Id);
            return RegistrationSettlement.Adopted;
        }
        db.WebhookSubscriptions.Remove(subscription);
        await db.SaveChangesAsync(cancellationToken);
        return RegistrationSettlement.Absent;
    }

    // Trendyol keeps one webhook per registration; Shopify one subscription per topic, all of which must be there.
    internal static string? AdoptableIds(string platformCode, IReadOnlyList<RemoteWebhook> ours)
    {
        if (ours.Count == 0) return null;
        if (platformCode != "SHOPIFY") return ours[0].Id;
        var byTopic = MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyHttpClient.WebhookTopics
            .Select(topic => ours.FirstOrDefault(webhook => string.Equals(webhook.Topic, topic, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return byTopic.All(webhook => webhook is not null)
            ? string.Join(MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyHttpClient.WebhookIdSeparator, byTopic.Select(webhook => webhook!.Id))
            : null;
    }

    // The hash of the route token in a receiving address (…/api/v1/hooks/{connection}/{token}), or null for any other URL.
    internal string? RouteTokenHashOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var hooks = Array.FindIndex(segments, segment => string.Equals(segment, "hooks", StringComparison.OrdinalIgnoreCase));
        return hooks < 0 || hooks + 2 >= segments.Length ? null : tokenHasher.Hash(Uri.UnescapeDataString(segments[hooks + 2]));
    }

    internal const string PausedStatus = "PAUSED";
    internal const string PausePendingPrefix = "webhook-pause:";
    internal const string ResumePendingPrefix = "webhook-resume:";
    internal static string PausePendingKey(Guid connectionId) => $"{PausePendingPrefix}{connectionId:N}";
    internal static string ResumePendingKey(Guid connectionId) => $"{ResumePendingPrefix}{connectionId:N}";
    internal const string HepsiburadaPauseNote = "Hepsiburada webhook'u elle tanımlandığı için kapatılmadı. Bağlantı pasifken gelen bildirimler işlenmez; bağlantı etkinleşince yeniden alınır.";

    private static string PausedMessage(PlatformConnection connection)
    {
        var platform = PlatformName(connection.PlatformCode);
        return connection.Status is "ACTIVE" or "VERIFIED"
            ? $"{platform} webhook'u yeniden açılamadı. Webhook bölümünden Webhook'u Aç'a basın; panel saatte bir yeniden dener, siparişler bu sürede düzenli kontrolle gelir."
            : $"{platform} webhook'u bağlantı pasife alınırken kapatıldı. Bağlantı yeniden etkinleşince panel webhook'u kendiliğinden açar.";
    }

    // Retires the definitions Pasife Al closed once another webhook is open. A Trendyol one is still registered there
    // (passive): it is removed at Trendyol first (a 404 counts as removed), so no ownerless passive webhook is left
    // counting towards Trendyol's 15. One that cannot be removed stays PAUSED with a pending record, and the hourly
    // upkeep removes it later. Shopify removed its subscriptions on Pasife Al, so its rows only close here.
    private async Task RetirePausedAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var rows = await db.WebhookSubscriptions.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == PausedStatus).ToListAsync(cancellationToken);
        if (rows.Count == 0) return;
        var connection = await ConnectionAsync(tenantId, connectionId, cancellationToken);
        var left = 0;
        foreach (var row in rows)
        {
            if (connection?.PlatformCode == "TRENDYOL" && row.ExternalSubscriptionId is { } externalId && externalId != ManualRegistration && !removedAtMarketplace.Contains(externalId))
            {
                bool removed;
                try { removed = (await registrations.DeleteWebhookAsync(Context(tenantId, connectionId, correlationId, "webhook-delete"), externalId, cancellationToken)).IsSuccess; }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested) { removed = false; }
                if (!removed)
                {
                    left++;
                    logger.LogWarning("Passive Trendyol order webhook {WebhookId} of connection {ConnectionId} could not be removed; it is tried again hourly", externalId, connectionId);
                    continue;
                }
            }
            row.Status = "DELETED";
            row.Version++;
        }
        await db.SaveChangesAsync(cancellationToken);
        if (left > 0 && connection is { Status: "ACTIVE" or "VERIFIED" })
            await NotePendingAsync(tenantId, ResumePendingKey(connectionId), "WEBHOOK_PASSIVE_LEFT",
                $"{connection.DisplayName}: Trendyol'daki eski pasif webhook kaydı silinemedi; panel saatte bir yeniden dener. Yeni webhook açık, siparişler gelir.", cancellationToken);
    }

    // The end of a resume: the pending record closes unless a passive definition is still left to remove.
    private async Task FinishResumeAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        if (await db.WebhookSubscriptions.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == PausedStatus, cancellationToken)) return;
        await ResolvePendingAsync(tenantId, ResumePendingKey(connectionId), cancellationToken);
    }

    // The Shopify app secret a paused webhook was signed with, so the reopened one verifies the same way.
    private string? PausedClientSecret(WebhookSubscription paused)
    {
        if (paused.AuthenticationType != "HMAC_SHA256") return null;
        try { return JsonSerializer.Deserialize<VerifierPayload>(BoundSecret.UnprotectForConnection(protector, paused.TenantId, paused.ConnectionId, paused.ProtectedVerifierSecret))?.ClientSecret; }
        catch (Exception exception) when (exception is CryptographicException or JsonException) { return null; }
    }

    private Task<int> ResolvePendingAsync(Guid tenantId, string key, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return db.OperationalIssues.Where(x => x.TenantId == tenantId && x.DedupeKey == key && x.Status != IssueStatus.Resolved)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, IssueStatus.Resolved).SetProperty(x => x.ResolvedAt, now), cancellationToken);
    }

    // One record per connection and direction; it is what the hourly retry works from.
    private async Task NotePendingAsync(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (summary.Length > 1000) summary = summary[..1000];
        var touched = await db.OperationalIssues.Where(x => x.TenantId == tenantId && x.DedupeKey == key)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, IssueStatus.Open)
                .SetProperty(x => x.Summary, summary)
                .SetProperty(x => x.LastSeenAt, now)
                .SetProperty(x => x.ResolvedAt, (DateTimeOffset?)null)
                .SetProperty(x => x.OccurrenceCount, x => x.OccurrenceCount + 1), cancellationToken);
        if (touched > 0) return;
        db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = code, Summary = summary, Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation }) { db.ChangeTracker.Clear(); }
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
            // One connection's fault (a database error, an unreachable marketplace) does not end the pass for the others,
            // nor the pending webhook retries that run after it in the same hourly step.
            try
            {
                var context = Context(subscription.TenantId, subscription.ConnectionId, $"webhook-upkeep:{subscription.ConnectionId:N}", "webhook-upkeep");
                if (subscription.PlatformCode == "SHOPIFY")
                {
                    // Shopify drops subscriptions after repeated failures (8 in a row): the dropped topics are made again, and
                    // a registration Shopify removed altogether is opened anew instead of being taken for live.
                    db.ChangeTracker.Clear();
                    if (!await StillOpenAsync(subscription.TenantId, subscription.ConnectionId, subscription.Id, cancellationToken)) continue;
                    var check = await EnsureShopifyRegisteredAsync(subscription.TenantId, subscription.ConnectionId, subscription.Id, $"webhook-upkeep:{subscription.ConnectionId:N}", cancellationToken);
                    if (check.Outcome is ShopifyRegistrationCheck.Restored or ShopifyRegistrationCheck.Recreated) reactivated++;
                    else if (check.Outcome == ShopifyRegistrationCheck.Failed)
                        logger.LogWarning("Shopify order webhooks of connection {ConnectionId} could not be restored: {Reason}", subscription.ConnectionId, check.Failure);
                    continue;
                }
                var remote = await registrations.ListWebhooksAsync(context, cancellationToken);
                if (!remote.IsSuccess) continue;
                var match = remote.Value!.FirstOrDefault(webhook => webhook.Id == subscription.ExternalSubscriptionId);
                if (match is null || match.Status == "ACTIVE") continue;
                // Read again right before activating: a Pasife Al made while this pass ran has set the webhook passive on
                // purpose, and activating it would undo that.
                if (!await StillOpenAsync(subscription.TenantId, subscription.ConnectionId, subscription.Id, cancellationToken)) continue;
                var activated = await registrations.ActivateWebhookAsync(context, subscription.ExternalSubscriptionId!, cancellationToken);
                if (activated.IsSuccess)
                {
                    reactivated++;
                    logger.LogInformation("Trendyol order webhook {WebhookId} was passive and has been reactivated", subscription.ExternalSubscriptionId);
                }
                else logger.LogWarning("Trendyol order webhook {WebhookId} could not be reactivated: {Code}", subscription.ExternalSubscriptionId, activated.Error?.Code);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Webhook upkeep of connection {ConnectionId} failed", subscription.ConnectionId);
            }
        }
        return reactivated;
    }

    // The registration is still open and its connection still reads (no Pasife Al since the upkeep listed it).
    private Task<bool> StillOpenAsync(Guid tenantId, Guid connectionId, Guid subscriptionId, CancellationToken cancellationToken) =>
        (from subscription in db.WebhookSubscriptions.AsNoTracking()
         join connection in db.PlatformConnections.AsNoTracking() on subscription.ConnectionId equals connection.Id
         where subscription.TenantId == tenantId && subscription.Id == subscriptionId && subscription.ConnectionId == connectionId && subscription.Status == "ACTIVE"
             && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")
         select subscription.Id).AnyAsync(cancellationToken);

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
        // A Hepsiburada definition is never verified by a call; it was opened when it was prepared.
        var manual = subscription?.ExternalSubscriptionId == ManualRegistration;
        var registeredAt = subscription?.VerifiedAt ?? (manual ? PreparedAt(subscription!.Id) : null);
        // A refusal from before the current subscription was opened belongs to the one it replaced (for example a
        // Shopify webhook reopened with another key): it is not shown against the new one.
        var rejectedPrefix = MarketplaceWebhookService.RejectedPrefix(connection.Id);
        var rejected = await db.OperationalIssues.AsNoTracking()
            .Where(x => x.TenantId == connection.TenantId && x.DedupeKey.StartsWith(rejectedPrefix) && x.Status == IssueStatus.Open
                && (registeredAt == null || x.LastSeenAt >= registeredAt))
            .OrderByDescending(x => x.LastSeenAt)
            .Select(x => new { x.LastSeenAt, x.Summary })
            .FirstOrDefaultAsync(cancellationToken);
        var previous = connection.PlatformCode == "HEPSIBURADA" && subscription?.LastReceivedAt is null
            ? await PreviousDefinitionAsync(connection.TenantId, connection.Id, subscription, tracked: false, cancellationToken)
            : null;
        var adoptable = connection.PlatformCode == "HEPSIBURADA" && subscription?.LastReceivedAt is null && connection.Status is "ACTIVE" or "VERIFIED"
            ? await AdoptableDefinitionAsync(connection, cancellationToken)
            : null;
        return new(state, endpoint, registeredAt, subscription?.LastReceivedAt, received, message, LastRejectedAt: rejected?.LastSeenAt, LastRejectedReason: rejected?.Summary,
            RouteOnly: connection.PlatformCode == "SHOPIFY" && subscription?.AuthenticationType == "ROUTE_TOKEN",
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

    // Shopify's access tokens carry a kind prefix (shpat_ Admin API, shpca_ custom app, shppa_ private app, shpua_ user);
    // a client secret is shpss_ or a bare hex string.
    internal static bool IsShopifyAccessToken(string value) =>
        new[] { "shpat_", "shpca_", "shppa_", "shpua_" }.Any(prefix => value.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string purpose) =>
        new(tenantId, connectionId, correlationId, $"{purpose}:{connectionId:N}", timeProvider.GetUtcNow().AddSeconds(30));

    private static ServiceResult<InstantNotificationView> NotFound() =>
        ServiceResult<InstantNotificationView>.Fail("RESOURCE_NOT_FOUND", "Bağlantı bulunamadı.", 404);

    private sealed record VerifierPayload(string? Username, string? Password, string? ApiKey, string? ClientSecret);
}
