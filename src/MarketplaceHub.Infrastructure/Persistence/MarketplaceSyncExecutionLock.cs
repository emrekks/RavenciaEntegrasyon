using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed class MarketplaceSyncExecutionLock : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> localGates = new();
    private readonly AppDbContext? db;
    private readonly NpgsqlConnection? connection;
    private readonly SemaphoreSlim? localGate;
    private readonly long key;
    private readonly bool acquired;
    private readonly bool closeConnectionOnDispose;
    private int disposed;

    private MarketplaceSyncExecutionLock(AppDbContext? db, NpgsqlConnection? connection, SemaphoreSlim? localGate, long key, bool acquired, bool closeConnectionOnDispose)
    {
        this.db = db;
        this.connection = connection;
        this.localGate = localGate;
        this.key = key;
        this.acquired = acquired;
        this.closeConnectionOnDispose = closeConnectionOnDispose;
    }

    public static async Task<MarketplaceSyncExecutionLock?> TryAcquireAsync(AppDbContext db, Guid connectionId, string jobType, CancellationToken cancellationToken, string? payloadJson = null) =>
        await TryAcquireKeyAsync(db, LockKey(connectionId, await JobGroupAsync(db, jobType, payloadJson, cancellationToken)), cancellationToken);

    // The processor's lock for a job. A short job (WaitsForLock) polls up to ShortJobLockWait for it: Trendyol sends
    // two or three notices of one package in the same millisecond, the hot lane leases them together, and the one
    // holding the lock is usually done in a fraction of a second. Deferring the others used to cost them 30 s or
    // more each (audit 2026-10-24 Q1). Other jobs try once. The wait counts polls rather than reading the clock, so
    // a clock that stands still cannot make it endless.
    public static async Task<MarketplaceSyncExecutionLock?> AcquireForJobAsync(AppDbContext db, Guid connectionId, string jobType, string? payloadJson, TimeProvider clock, CancellationToken cancellationToken)
    {
        var key = LockKey(connectionId, await JobGroupAsync(db, jobType, payloadJson, cancellationToken));
        var polls = WaitsForLock(jobType) ? (int)(ShortJobLockWait.Ticks / GroupPollInterval.Ticks) : 0;
        for (var poll = 0; ; poll++)
        {
            if (await TryAcquireKeyAsync(db, key, cancellationToken) is { } acquired) return acquired;
            if (poll >= polls) return null;
            await Task.Delay(GroupPollInterval, clock, cancellationToken);
        }
    }

    internal static readonly TimeSpan ShortJobLockWait = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ShortJobRetryDelay = TimeSpan.FromSeconds(1);

    // Webhook deliveries and the read-back of uncertain marketplace writes: a single notice or a single check that
    // takes well under a second (live p95 0.8 s), so waiting a few seconds for the lock beats a deferral.
    internal static bool WaitsForLock(string jobType) =>
        jobType.EndsWith("_WEBHOOK_INGEST", StringComparison.OrdinalIgnoreCase) || jobType == MarketplaceJobTypes.ExternalEffectReconcile;

    private static async Task<string> JobGroupAsync(AppDbContext db, string jobType, string? payloadJson, CancellationToken cancellationToken) =>
        jobType == MarketplaceJobTypes.ExternalEffectReconcile && payloadJson is not null
            ? EffectReconcileGroup(await EffectActionAsync(db, payloadJson, cancellationToken))
            : jobType.EndsWith("_WEBHOOK_INGEST", StringComparison.OrdinalIgnoreCase) && WebhookIngestGroup(WebhookResourceType(payloadJson)) is { } webhookGroup
                ? webhookGroup
                : GroupFor(jobType);

    internal const string OrdersGroup = "orders";
    internal const string OrderInvoicesGroup = "order-invoices";
    internal const string FinanceGroup = "finance";
    private static readonly TimeSpan GroupPollInterval = TimeSpan.FromMilliseconds(200);

    // The API's instant actions project an accepted marketplace write onto the same orders and
    // packages the order jobs write. They wait a short while for the job lane's lock instead of
    // skipping it; null when it stayed busy for the whole wait.
    public static async Task<MarketplaceSyncExecutionLock?> AcquireGroupAsync(AppDbContext db, Guid connectionId, string group, TimeSpan wait, TimeProvider clock, CancellationToken cancellationToken)
    {
        var key = LockKey(connectionId, group);
        var deadline = clock.GetUtcNow() + wait;
        while (true)
        {
            var acquired = await TryAcquireKeyAsync(db, key, cancellationToken);
            if (acquired is not null) return acquired;
            var remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return null;
            await Task.Delay(remaining < GroupPollInterval ? remaining : GroupPollInterval, clock, cancellationToken);
        }
    }

    private static async Task<MarketplaceSyncExecutionLock?> TryAcquireKeyAsync(AppDbContext db, long key, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection() as NpgsqlConnection;
        if (!string.Equals(db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal)
            || connection is null
            || string.IsNullOrWhiteSpace(connection.ConnectionString))
        {
            // Local/test providers do not have PostgreSQL advisory locks. Keep the
            // same per-connection/per-job serialization guarantee in-process.
            var localGate = localGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
            if (!await localGate.WaitAsync(TimeSpan.Zero, cancellationToken)) return null;
            return new MarketplaceSyncExecutionLock(null, null, localGate, key, acquired: true, closeConnectionOnDispose: false);
        }

        var closeConnectionOnDispose = connection.State != System.Data.ConnectionState.Open;
        try
        {
            // Reuse EF Core's configured connection instead of rebuilding a new
            // NpgsqlConnection from ConnectionString. Npgsql intentionally omits
            // the password when exposing a connection string in some states.
            // A processor may acquire a second lane lock while its outer job
            // already holds this same connection open. Opening it twice throws
            // before PostgreSQL can evaluate the second advisory lock.
            if (closeConnectionOnDispose) await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(@key)";
            command.Parameters.AddWithValue("key", key);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is not true)
            {
                if (closeConnectionOnDispose) await connection.CloseAsync();
                return null;
            }

            return new MarketplaceSyncExecutionLock(db, connection, null, key, acquired: true, closeConnectionOnDispose: closeConnectionOnDispose);
        }
        catch
        {
            if (closeConnectionOnDispose) await connection.CloseAsync();
            throw;
        }
    }

    public static string GroupFor(string jobType)
    {
        var type = jobType.ToUpperInvariant();
        // Interactive shipment changes must not wait behind the recurring order
        // synchronization lane. They still serialize with other shipment
        // changes for the same connection, while their read-back can safely
        // merge with the order projection when the sync lane is active.
        if (type.Contains("SHIPMENT_ACTION", StringComparison.Ordinal)) return "shipment-actions";
        // Historical cargo lookups can scan many pages; keep them out of the
        // lifecycle lane so a slow metadata repair cannot hold up status polling.
        if (type == MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation) return "order-cargo-info";
        // The accounting read writes only its own tables; it neither waits behind the order reads nor holds them up.
        if (type == MarketplaceJobTypes.TrendyolFinanceSync) return FinanceGroup;
        // Trendyol invoice reconciliation has its own group again: it writes only the packages' invoice columns
        // and the order snapshot of partial return-claim orders, without checking or bumping their versions
        // (ExecuteUpdate), so it neither waits behind the long order reads nor makes them conflict. The partial
        // order it completes from a full order read is written under the "orders" lock it takes for that.
        // Shopify and Hepsiburada invoice checks project whole orders and stay with the order reads.
        if (type == MarketplaceJobTypes.OrderInvoiceReconciliation) return OrderInvoicesGroup;
        // Webhook deliveries and the read-back of uncertain marketplace writes update the same orders
        // and packages as the order reads; sharing their lock avoids version conflicts and the
        // retries (and quota) those cost.
        if (type.EndsWith("_WEBHOOK_INGEST", StringComparison.Ordinal) || type == MarketplaceJobTypes.ExternalEffectReconcile) return "orders";
        if (type.Contains("ORDER", StringComparison.Ordinal) || type.Contains("SHIPMENT", StringComparison.Ordinal)) return "orders";
        if (type.Contains("RETURN", StringComparison.Ordinal) || type.Contains("CLAIM", StringComparison.Ordinal)) return "returns";
        if (type.Contains("STOCK", StringComparison.Ordinal) || type.Contains("INVENTORY", StringComparison.Ordinal) || type.Contains("PRICE", StringComparison.Ordinal)) return "stock";
        if (type.Contains("PRODUCT", StringComparison.Ordinal) || type.Contains("CATALOG", StringComparison.Ordinal)) return "products";
        if (type.Contains("REFERENCE", StringComparison.Ordinal) || type.Contains("CATEGORY", StringComparison.Ordinal) || type.Contains("BRAND", StringComparison.Ordinal)) return "references";
        return "connection";
    }

    // The read-back of an unclear return decision writes the return claim, which the return reads write
    // too; it serializes with them. Package and order checks stay with the order reads. The same group is
    // stored on the verification job (ExternalEffectReconcilePolicy.NewJob) for the lease query.
    internal static string EffectReconcileGroup(string? action) => ExternalEffectReconcilePolicy.LockGroup(action);

    // A claim notice writes return claims, and a CLAIM_EVENT reads them again with the return reads' cursor: it
    // serializes with the return reads. Order notices and claim packages (they write orders) keep the order lock. The
    // same group is stored on the job (MarketplaceWebhookService) for the lease query.
    internal static string? WebhookIngestGroup(string? resourceType) => resourceType is "RETURNS" or "CLAIM_EVENT" ? "returns" : null;

    private static string? WebhookResourceType(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("resourceType", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static async Task<string?> EffectActionAsync(AppDbContext db, string payloadJson, CancellationToken cancellationToken)
    {
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            if (payload.RootElement.ValueKind != JsonValueKind.Object
                || !payload.RootElement.TryGetProperty("effectId", out var value)
                || !value.TryGetGuid(out var effectId)) return null;
            return await db.ExternalEffectRecords.AsNoTracking().Where(x => x.Id == effectId).Select(x => x.Action).FirstOrDefaultAsync(cancellationToken);
        }
        catch (JsonException) { return null; }
    }

    // sameTypeRunning: a job of the same type of the connection holds the lock now. Only then does a recurring
    // read coalesce into it (that run reads the same thing); behind another job of the group it waits instead
    // of being reported as done without running.
    internal static JobExecutionResult ContentionResult(string jobType, string payloadJson, string? triggerType = null, bool sameTypeRunning = true)
    {
        // A short job already waited ShortJobLockWait for the lock, so a longer read holds it: it comes back in a
        // second or so (CompleteAsync spreads it to 1-4 s) rather than 30 s. The lease query skips it while that
        // read stays leased, so the short wait does not turn into a lease-and-defer loop.
        if (WaitsForLock(jobType))
            return JobExecutionResult.Deferred("CONNECTION_OPERATION_BUSY",
                "Bu bağlantıda süren işlem bitince bildirim birkaç saniye içinde işlenecek.", ShortJobRetryDelay);
        // Work a user asked for is never folded into a run that happens to hold the lock.
        if (triggerType == "user")
            return JobExecutionResult.Deferred("CONNECTION_OPERATION_BUSY",
                "Bu bağlantıda süren işlem bitince isteğiniz hemen çalıştırılacak.", TimeSpan.FromSeconds(10));
        if (jobType is MarketplaceJobTypes.OrderInvoiceReconciliation
            or MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation
            or MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation)
            return JobExecutionResult.Deferred("ORDER_INVOICE_RECONCILIATION_BUSY",
                "Fatura durumu kontrolü, sipariş okuma işlemi tamamlanınca yeniden denenecek.", TimeSpan.FromMinutes(3));

        if (jobType is MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.PazaramaOrderSync or MarketplaceJobTypes.PttAvmOrderSync)
        {
            try
            {
                using var payload = JsonDocument.Parse(payloadJson);
                if (payload.RootElement.ValueKind == JsonValueKind.Object
                    && payload.RootElement.TryGetProperty("externalOrderId", out var id)
                    && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                    return JobExecutionResult.Deferred("TARGETED_ORDER_SYNC_BUSY",
                        "Tekil sipariş yenilemesi mevcut sipariş işleminin tamamlanmasını bekliyor.", TimeSpan.FromSeconds(30));
            }
            catch (JsonException) { }
        }
        // Only recurring read syncs may coalesce: the job already holding the lock performs the
        // same read. Writes, actions, webhooks, imports and targeted requests carry their own work
        // and must wait for the lock instead of being reported as done.
        return IsCoalescableRead(jobType) && sameTypeRunning
            ? JobExecutionResult.Success()
            : JobExecutionResult.Deferred("CONNECTION_OPERATION_BUSY",
                "Bu bağlantıda aynı türde başka bir işlem sürüyor; işlem tamamlanınca otomatik çalıştırılacak.", TimeSpan.FromSeconds(30));
    }

    internal static bool IsCoalescableRead(string jobType) => jobType is
        MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.PazaramaOrderSync or MarketplaceJobTypes.PttAvmOrderSync
        or MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.N11OrderRecoverySync or MarketplaceJobTypes.PazaramaOrderRecoverySync or MarketplaceJobTypes.PttAvmOrderRecoverySync
        or MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.HepsiburadaOrderStatusSync or MarketplaceJobTypes.PazaramaOrderStatusSync or MarketplaceJobTypes.PttAvmOrderStatusSync
        or MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation
        or MarketplaceJobTypes.OrderReconciliation or MarketplaceJobTypes.ShopifyOrderReconciliation
        or MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.HepsiburadaReturnSync or MarketplaceJobTypes.ReturnStatusSync or MarketplaceJobTypes.ReturnReconciliation
        or MarketplaceJobTypes.StockReconciliation
        or MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ShopifyProductSync or MarketplaceJobTypes.HepsiburadaProductSync
        or MarketplaceJobTypes.QuestionSync
        or MarketplaceJobTypes.TrendyolFinanceSync;

    internal static long LockKey(Guid connectionId, string group)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"sync:trendyol:{connectionId:N}:{group}"));
        return BitConverter.ToInt64(bytes, 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        if (localGate is not null)
        {
            if (acquired) localGate.Release();
            return;
        }

        if (connection is null)
            return;

        try
        {
            // An advisory lock belongs to its session: when the connection was already closed (or
            // broke) while the job ran, the lock is gone with it. Skipping the unlock then keeps the
            // original job failure visible instead of replacing it with "Connection is not open".
            if (acquired && connection.State == System.Data.ConnectionState.Open)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(@key)";
                command.Parameters.AddWithValue("key", key);
                await command.ExecuteScalarAsync();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NpgsqlException)
        {
            // Same reason: a connection that fails during the unlock has already released the lock.
        }
        finally
        {
            if (closeConnectionOnDispose && db is not null)
            {
                try { await db.Database.CloseConnectionAsync(); }
                catch (Exception exception) when (exception is InvalidOperationException or NpgsqlException) { }
            }
        }
    }
}
