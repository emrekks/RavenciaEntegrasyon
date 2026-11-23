using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class MarketplaceWebhookService(AppDbContext db, TokenHasher tokenHasher, IWebhookVerifier verifier, TimeProvider timeProvider) : IMarketplaceWebhookService
{
    public async Task<ServiceResult<bool>> ReceiveAsync(Guid connectionPublicId, string routeToken, ReadOnlyMemory<byte> rawBody, IReadOnlyDictionary<string, string> headers, string correlationId, CancellationToken cancellationToken)
    {
        // The address names a connection: its own public id, or the one a definition moved from another connection keeps.
        var candidates = await (from item in db.WebhookSubscriptions.AsNoTracking()
                                join owner in db.PlatformConnections.AsNoTracking() on new { item.TenantId, item.ConnectionId } equals new { owner.TenantId, ConnectionId = owner.Id }
                                where item.Status == "ACTIVE" && (item.RoutePublicId == connectionPublicId || (item.RoutePublicId == null && owner.PublicId == connectionPublicId))
                                    && (owner.PlatformCode == "TRENDYOL" || owner.PlatformCode == "SHOPIFY" || owner.PlatformCode == "HEPSIBURADA") && AnsweringConnectionStatuses.Contains(owner.Status)
                                select new { Subscription = item, Connection = owner }).ToListAsync(cancellationToken);
        var match = candidates.SingleOrDefault(x => SafeVerify(routeToken, x.Subscription.RouteTokenHash));
        if (match is null)
        {
            await NoteRouteRefusedAsync(connectionPublicId, routeToken, cancellationToken);
            return NotFound();
        }
        var (connection, subscription) = (match.Connection, match.Subscription);
        var verified = await verifier.VerifyAsync(rawBody, headers, connection.Id, subscription.Id, cancellationToken);
        // A stale Shopify delivery is answered 200 and dropped: a refusal would only make Shopify retry the same old delivery.
        // A stale product delete (one held back during a deploy) still starts the link check at once: its body is not
        // read, the check finds the deleted codes itself, so the card does not show them as live until the nightly scan.
        if (!verified.IsSuccess && verified.Error!.Code == MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyWebhookVerifier.StaleCode)
            return connection.PlatformCode == "SHOPIFY" && connection.Status is ("ACTIVE" or "VERIFIED") && StaleStartsListingVerify(headers, connection.ExternalStoreId, subscription.AuthenticationType == "ROUTE_TOKEN")
                ? await QueueListingVerifyAsync(connection, subscription.Id, correlationId, cancellationToken)
                : ServiceResult<bool>.Ok(false);
        if (!verified.IsSuccess)
        {
            await NoteRejectedAsync(connection, verified.Error!.Code, verified.Error.SafeMessage, cancellationToken);
            return ServiceResult<bool>.Fail(verified.Error!.Code, verified.Error.SafeMessage, verified.Error.HttpStatus ?? 422);
        }
        // A paused connection (Pasife Al, or its data hidden) keeps its address: an authenticated delivery is answered 2xx,
        // recorded and not processed. A 404 made Hepsiburada mail the owner about orders of the 12-14 Nov 2026 pause;
        // reactivation re-reads the connection's orders from the marketplace anyway.
        if (connection.Status is not ("ACTIVE" or "VERIFIED"))
        {
            await NoteRejectedAsync(connection, PausedConnectionCode, "bağlantı pasifken geldi; kabul edildi ama işlenmedi (bağlantı yeniden etkinleştirilince siparişler pazaryerinden yeniden okunur)", cancellationToken, received: true);
            return ServiceResult<bool>.Ok(false);
        }
        var envelope = verified.Value!;
        // A Hepsiburada body names the merchant it belongs to. One of another merchant (a test store's feed still sent to
        // an address that was moved to the live store) is answered 200 and dropped, and the panel is told.
        if (connection.PlatformCode == "HEPSIBURADA" && ForeignMerchant(envelope.RawJson, connection.ExternalStoreId) is { } foreign)
        {
            await NoteRejectedAsync(connection, MerchantMismatchCode, $"başka bir mağazanın ({foreign}) bildirimi geldi ve işlenmedi; bu adresi o mağaza için tanımlıysa Hepsiburada'dan kaldırmasını isteyin", cancellationToken);
            return ServiceResult<bool>.Ok(false);
        }
        // The same check on every notification that names its store: a Trendyol package names its supplierId, a Shopify
        // delivery its shop in X-Shopify-Shop-Domain. Another store's notification is answered 200, recorded and dropped.
        if (connection.PlatformCode == "TRENDYOL" && ForeignSupplier(envelope.RawJson, connection.ExternalStoreId) is { } foreignSupplier)
        {
            await NoteRejectedAsync(connection, MerchantMismatchCode, $"başka bir satıcının ({foreignSupplier}) bildirimi geldi ve işlenmedi; bu adres o satıcı için Trendyol'da tanımlıysa oradan kaldırın", cancellationToken);
            return ServiceResult<bool>.Ok(false);
        }
        if (connection.PlatformCode == "SHOPIFY")
        {
            var shopDomain = headers.FirstOrDefault(x => string.Equals(x.Key, "X-Shopify-Shop-Domain", StringComparison.OrdinalIgnoreCase)).Value;
            // A subscription admitted by its route alone (no secret, nothing signed) must name this store; a signed one
            // that does not name it is still this app's delivery.
            var routeOnly = subscription.AuthenticationType == "ROUTE_TOKEN";
            if (ForeignShop(shopDomain, connection.ExternalStoreId, required: routeOnly) is { } foreignShop)
            {
                await NoteRejectedAsync(connection, MerchantMismatchCode, $"başka bir mağazanın ({foreignShop}) bildirimi geldi ve işlenmedi; bu adres o mağaza için Shopify'da tanımlıysa oradan kaldırın", cancellationToken);
                return ServiceResult<bool>.Ok(false);
            }
            // A topic the panel does not subscribe to is not taken for an order: answered 200, recorded, not processed.
            if (envelope.ResourceType == MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyWebhookVerifier.UnknownEventResourceType)
            {
                var topic = headers.FirstOrDefault(x => string.Equals(x.Key, "X-Shopify-Topic", StringComparison.OrdinalIgnoreCase)).Value;
                await NoteRejectedAsync(connection, UnknownEventCode, $"panelin tanımadığı bir Shopify olayı geldi ({SafeTopic(topic)}); kaydedildi, sipariş olarak işlenmedi", cancellationToken, received: true);
                return ServiceResult<bool>.Ok(false);
            }
        }
        // A product deleted in Shopify names only the product id, which the panel does not keep: the delivery starts the
        // connection's link verification now instead of at night. The scan misses the deleted codes and the lookups confirm
        // them NOT_FOUND, so the card reads "Bulunamadı" and can be published again. One run per connection in ten minutes.
        if (connection.PlatformCode == "SHOPIFY" && envelope.ResourceType == MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyWebhookVerifier.ProductDeleteResourceType)
            return await QueueListingVerifyAsync(connection, subscription.Id, correlationId, cancellationToken);
        var source = $"{connection.PlatformCode}_WEBHOOK";
        var jobType = MarketplaceJobTypes.ForPlatform(connection.PlatformCode, MarketplaceJobTypes.WebhookIngest);
        var externalMessageId = connection.PlatformCode == "HEPSIBURADA"
            ? $"{connection.Id:N}:{envelope.ExternalMessageId}"
            : envelope.ExternalMessageId;
        var dedup = $"webhook:{connection.Id}:{envelope.ExternalMessageId}";
        var now = timeProvider.GetUtcNow();
        var payload = JsonSerializer.Serialize(new { connectionId = connection.Id, externalMessageId, resourceType = envelope.ResourceType, rawJson = envelope.RawJson });
        var inbox = new InboxMessage { Id = Guid.CreateVersion7(), TenantId = connection.TenantId, Source = source, ExternalMessageId = externalMessageId, PayloadHash = envelope.PayloadHash, ReceivedAt = now };
        var job = IntegrationJobFactory.New(new IntegrationJobRequest(connection.TenantId, connection.Id, jobType, payload, dedup, correlationId, now) { TriggerType = "webhook", LockGroup = MarketplaceSyncExecutionLock.WebhookIngestGroup(envelope.ResourceType) });
        // Either answer is 200: a repeated delivery (Trendyol sends the same package twice, milliseconds apart) is already queued.
        await WebhookInboxWriter.InsertAsync(db, inbox, job, cancellationToken);
        await db.WebhookSubscriptions.Where(x => x.TenantId == connection.TenantId && x.Id == subscription.Id && x.Status == "ACTIVE").ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastReceivedAt, now).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
        await ResolveRejectedAsync(connection, now, cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    // A delivery that got through shows the earlier refusals are over (a corrected secret or address): the
    // connection's open refusal records close, so the webhook panel no longer shows the old reason. A refusal that
    // comes again reopens its record (NoteRejectedAsync). Another merchant's feed is not about this connection's
    // own deliveries and stays open.
    internal static string RejectedPrefix(Guid connectionId) => $"webhook-rejected:{connectionId:N}:";

    internal const string MerchantMismatchCode = "WEBHOOK_MERCHANT_MISMATCH";
    internal const string UnknownEventCode = "WEBHOOK_UNKNOWN_EVENT";
    internal const string PausedConnectionCode = "WEBHOOK_CONNECTION_PAUSED";

    // Connection states whose webhook address still answers. Only ACTIVE and VERIFIED deliveries are processed; those of a
    // DISABLED (Pasife Al) or HIDDEN connection are acknowledged and recorded, so the marketplace never sees a 404 for them.
    public static readonly string[] AnsweringConnectionStatuses = ["ACTIVE", "VERIFIED", "DISABLED", "HIDDEN"];

    private Task<int> ResolveRejectedAsync(PlatformConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var prefix = RejectedPrefix(connection.Id);
        var merchantMismatch = prefix + MerchantMismatchCode;
        var unknownEvent = prefix + UnknownEventCode;
        // Calls to a closed definition go on whatever the open one receives; that warning closes on its own terms.
        var callsRejected = prefix + CallsRejectedCode;
        return db.OperationalIssues
            .Where(x => x.TenantId == connection.TenantId && x.Status != IssueStatus.Resolved && x.DedupeKey.StartsWith(prefix) && x.DedupeKey != merchantMismatch && x.DedupeKey != unknownEvent && x.DedupeKey != callsRejected)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, IssueStatus.Resolved).SetProperty(x => x.ResolvedAt, now), cancellationToken);
    }
    private async Task<ServiceResult<bool>> QueueListingVerifyAsync(PlatformConnection connection, Guid subscriptionId, string correlationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var dedup = $"webhook:listing-verify:{connection.Id:N}:{now.ToUnixTimeSeconds() / 600}";
        // J4 (KUYRUK-8): a verification with this key in any state is the verification.
        var payload = JsonSerializer.Serialize(new { connectionId = connection.Id });
        if ((await Jobs.JobEnqueuer.EnqueueAsync(db, new IntegrationJobRequest(connection.TenantId, connection.Id, MarketplaceJobTypes.ListingVerify, payload, dedup, correlationId, now) { Priority = JobPriorityPolicy.For(MarketplaceJobTypes.ListingVerify, userInitiated: false), TriggerType = "webhook" }, JobDedupPolicy.ReuseAnyWithKey, cancellationToken)).Created)
        {
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException exception) when (IsDuplicateCandidate(exception)) { db.ChangeTracker.Clear(); }
        }
        await db.WebhookSubscriptions.Where(x => x.TenantId == connection.TenantId && x.Id == subscriptionId && x.Status == "ACTIVE").ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastReceivedAt, now).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
        await ResolveRejectedAsync(connection, now, cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    // The route gate refuses an old or wrong route token before the body is read, so ReceiveAsync never sees it; the
    // refusal is still kept when the address is known. An address of a definition the panel closed (its route and token
    // match a closed row) marks that row and opens a warning: 19-23 Nov 2026 Hepsiburada called a closed definition moved
    // from a deleted connection for four days and nothing showed it. Any other address of a receiving connection is an
    // old or wrong token. An address that names nothing leaves no trace.
    public async Task<string?> NoteRouteRefusedAsync(Guid connectionPublicId, string routeToken, CancellationToken cancellationToken)
    {
        if (await ClosedDefinitionAsync(connectionPublicId, routeToken, cancellationToken) is { } closed)
        {
            var now = timeProvider.GetUtcNow();
            var aMinuteAgo = now.AddMinutes(-1);
            await db.WebhookSubscriptions
                .Where(x => x.TenantId == closed.Connection.TenantId && x.Id == closed.SubscriptionId && x.Status != "ACTIVE" && (x.LastRefusedAt == null || x.LastRefusedAt < aMinuteAgo))
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastRefusedAt, now), cancellationToken);
            var platform = closed.Connection.PlatformCode switch { "HEPSIBURADA" => "Hepsiburada", "SHOPIFY" => "Shopify", _ => "Trendyol" };
            var reason = closed.Connection.PlatformCode == "HEPSIBURADA"
                ? "Hepsiburada kapalı webhook adresine istek gönderiyor; Önceki Tanımı Geri Yükle ile tanımı geri yükleyin ya da Hepsiburada'daki adresi güncelleyin"
                : $"{platform} kapalı webhook adresine istek gönderiyor; webhook'u açın ya da {platform}'daki kaydı kaldırın";
            await NoteRejectedAsync(closed.Connection, CallsRejectedCode, reason, cancellationToken, issueCode: CallsRejectedCode);
            return CallsRejectedCode;
        }
        if (await ReceivingConnectionAsync(connectionPublicId, cancellationToken) is not { } connection) return null;
        await NoteRouteTokenMismatchAsync(connection, cancellationToken);
        return RouteTokenMismatchCode;
    }

    // The closed definition (any state but ACTIVE) whose address this is: the route names it (its moved-from public id,
    // or its connection's own) and the token matches its hash. Its connection must be one that receives deliveries.
    private async Task<(PlatformConnection Connection, Guid SubscriptionId)?> ClosedDefinitionAsync(Guid connectionPublicId, string routeToken, CancellationToken cancellationToken)
    {
        var closed = await (from item in db.WebhookSubscriptions.AsNoTracking()
                            join owner in db.PlatformConnections.AsNoTracking() on new { item.TenantId, item.ConnectionId } equals new { owner.TenantId, ConnectionId = owner.Id }
                            where item.Status != "ACTIVE" && (item.RoutePublicId == connectionPublicId || (item.RoutePublicId == null && owner.PublicId == connectionPublicId))
                                && (owner.PlatformCode == "TRENDYOL" || owner.PlatformCode == "SHOPIFY" || owner.PlatformCode == "HEPSIBURADA") && AnsweringConnectionStatuses.Contains(owner.Status)
                            select new { item.Id, item.RouteTokenHash, Connection = owner }).ToListAsync(cancellationToken);
        var match = closed.FirstOrDefault(x => SafeVerify(routeToken, x.RouteTokenHash));
        return match is null ? null : (match.Connection, match.Id);
    }

    // The receiving connection an address names: by its own public id, else the one a definition moved to it (in any
    // state) carries in its address.
    private async Task<PlatformConnection?> ReceivingConnectionAsync(Guid connectionPublicId, CancellationToken cancellationToken)
    {
        var receiving = db.PlatformConnections.AsNoTracking().Where(x => (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "SHOPIFY" || x.PlatformCode == "HEPSIBURADA") && AnsweringConnectionStatuses.Contains(x.Status));
        return await receiving.SingleOrDefaultAsync(x => x.PublicId == connectionPublicId, cancellationToken)
            ?? await receiving.Where(x => db.WebhookSubscriptions.Any(item => item.TenantId == x.TenantId && item.ConnectionId == x.Id && item.RoutePublicId == connectionPublicId)).FirstOrDefaultAsync(cancellationToken);
    }

    internal const string RouteTokenMismatchCode = "WEBHOOK_ROUTE_TOKEN_MISMATCH";
    // The marketplace calls the address of a definition the panel closed; the warning closes once the definition is open
    // again (Önceki Tanımı Geri Yükle) or after a day without calls (InstantNotificationService's hourly upkeep).
    internal const string CallsRejectedCode = "WEBHOOK_CALLS_REJECTED";

    private Task NoteRouteTokenMismatchAsync(PlatformConnection connection, CancellationToken cancellationToken) =>
        NoteRejectedAsync(connection, RouteTokenMismatchCode, "adres eski ya da hatalı (abonelik yenilendiyse pazaryerindeki adresi güncelleyin)", cancellationToken);

    // A refused delivery for a known connection (old address, wrong password, a body that does not verify) leaves an
    // operational issue, so a marketplace that calls and is turned away is told apart from one that never calls; the
    // log alone is lost with every restart. One row per connection and code, touched at most once a minute.
    private async Task NoteRejectedAsync(PlatformConnection connection, string code, string reason, CancellationToken cancellationToken, bool received = false, string? issueCode = null)
    {
        var now = timeProvider.GetUtcNow();
        var key = RejectedPrefix(connection.Id) + code;
        var summary = received ? $"{connection.DisplayName} bildirimi alındı ({code}): {reason}" : $"{connection.DisplayName} bildirimi reddedildi ({code}): {reason}";
        if (summary.Length > 1000) summary = summary[..1000];
        try
        {
            var touched = await db.OperationalIssues
                .Where(x => x.TenantId == connection.TenantId && x.DedupeKey == key && x.LastSeenAt < now.AddMinutes(-1))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, IssueStatus.Open)
                    .SetProperty(x => x.Summary, summary)
                    .SetProperty(x => x.LastSeenAt, now)
                    .SetProperty(x => x.OccurrenceCount, x => x.OccurrenceCount + 1), cancellationToken);
            if (touched > 0 || await db.OperationalIssues.AsNoTracking().AnyAsync(x => x.TenantId == connection.TenantId && x.DedupeKey == key, cancellationToken)) return;
            db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = connection.TenantId, DedupeKey = key, Code = issueCode ?? (received ? code : "WEBHOOK_DELIVERY_REJECTED"), Summary = summary, Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateCandidate(exception)) { db.ChangeTracker.Clear(); }
    }

    // The first merchant id in the body (at any depth up to a few levels: the root of a claim, each item of an order) that
    // is not the connection's own, or null when every one matches or the body names none.
    internal static string? ForeignMerchant(string rawJson, string merchantId)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return Foreign(document.RootElement, 0);
        }
        catch (JsonException) { return null; }

        string? Foreign(JsonElement element, int depth)
        {
            if (depth > 4) return null;
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    if (Foreign(item, depth + 1) is { } found) return found;
                return null;
            }
            if (element.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, "merchantId", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is { Length: > 0 } value && !string.Equals(value.Trim(), merchantId.Trim(), StringComparison.OrdinalIgnoreCase))
                    return value;
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && Foreign(property.Value, depth + 1) is { } found) return found;
            }
            return null;
        }
    }

    // The first supplierId of a Trendyol package page ({"content":[...]}) that is not the connection's seller id, or null
    // when every package names this seller or none names one. Trendyol sends it as a number (documented sample).
    internal static string? ForeignSupplier(string pageJson, string supplierId)
    {
        try
        {
            using var document = JsonDocument.Parse(pageJson);
            if (!document.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
            foreach (var package in content.EnumerateArray())
            {
                if (package.ValueKind != JsonValueKind.Object || !package.TryGetProperty("supplierId", out var value)) continue;
                var named = value.ValueKind switch
                {
                    JsonValueKind.Number => value.GetRawText(),
                    JsonValueKind.String => value.GetString(),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(named) && !string.Equals(named.Trim(), supplierId.Trim(), StringComparison.OrdinalIgnoreCase)) return named.Trim();
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    // The shop X-Shopify-Shop-Domain names ("store.myshopify.com") when it is not the connection's store (kept as the
    // shop name, with or without the domain), or null when it is. A missing header is foreign only when required (a
    // route-only subscription, whose deliveries nothing else ties to the store).
    internal static string? ForeignShop(string? shopDomain, string connectionShop, bool required)
    {
        static string Shop(string value)
        {
            var shop = value.Trim().ToLowerInvariant();
            return shop.EndsWith(".myshopify.com", StringComparison.Ordinal) ? shop[..^".myshopify.com".Length] : shop;
        }
        if (string.IsNullOrWhiteSpace(shopDomain)) return required ? "mağaza adı yok" : null;
        return string.Equals(Shop(shopDomain), Shop(connectionShop), StringComparison.Ordinal) ? null : SafeTopic(shopDomain);
    }

    // A stale Shopify delivery (it passed the signature check) that still starts the link check: a product delete of
    // this store. The topic is the one the panel subscribes to for products; an order topic waits for the order check.
    internal static bool StaleStartsListingVerify(IReadOnlyDictionary<string, string> headers, string connectionShop, bool routeOnly)
    {
        string? Header(string name) => headers.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        var topic = Header("X-Shopify-Topic");
        return !string.IsNullOrWhiteSpace(topic)
            && MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyWebhookVerifier.ResourceType(topic) == MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyWebhookVerifier.ProductDeleteResourceType
            && ForeignShop(Header("X-Shopify-Shop-Domain"), connectionShop, routeOnly) is null;
    }

    // A header value shown in the panel: trimmed, at most 100 characters.
    private static string SafeTopic(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return "adı yok";
        return text.Length <= 100 ? text : text[..100];
    }

    private static bool IsDuplicateCandidate(DbUpdateException exception) => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    private bool SafeVerify(string token, string hash) { try { return tokenHasher.Verify(token, hash); } catch (FormatException) { return false; } }
    private static ServiceResult<bool> NotFound() => ServiceResult<bool>.Fail("WEBHOOK_ROUTE_NOT_FOUND", "Webhook route bulunamadı.", 404);
}
