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

    public static async Task<MarketplaceSyncExecutionLock?> TryAcquireAsync(AppDbContext db, Guid connectionId, string jobType, CancellationToken cancellationToken)
    {
        var key = LockKey(connectionId, GroupFor(jobType));
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
        // Trendyol invoice reconciliation only reads package data and merges
        // invoice fields. Keeping it out of the full order-read lane prevents
        // a long order scan from starving invoice status refreshes.
        if (type == MarketplaceJobTypes.OrderInvoiceReconciliation) return "order-invoices";
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

    internal static JobExecutionResult ContentionResult(string jobType, string payloadJson, string? triggerType = null)
    {
        // Work a user asked for is never folded into a run that happens to hold the lock.
        if (triggerType == "user")
            return JobExecutionResult.Deferred("CONNECTION_OPERATION_BUSY",
                "Bu bağlantıda süren işlem bitince isteğiniz hemen çalıştırılacak.", TimeSpan.FromSeconds(10));
        if (jobType is MarketplaceJobTypes.OrderInvoiceReconciliation
            or MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation
            or MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation)
            return JobExecutionResult.Deferred("ORDER_INVOICE_RECONCILIATION_BUSY",
                "Fatura durumu kontrolü, sipariş okuma işlemi tamamlanınca yeniden denenecek.", TimeSpan.FromMinutes(3));

        if (jobType is MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.PazaramaOrderSync)
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
        return IsCoalescableRead(jobType)
            ? JobExecutionResult.Success()
            : JobExecutionResult.Deferred("CONNECTION_OPERATION_BUSY",
                "Bu bağlantıda aynı türde başka bir işlem sürüyor; işlem tamamlanınca otomatik çalıştırılacak.", TimeSpan.FromSeconds(30));
    }

    internal static bool IsCoalescableRead(string jobType) => jobType is
        MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.PazaramaOrderSync
        or MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.N11OrderRecoverySync or MarketplaceJobTypes.PazaramaOrderRecoverySync
        or MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.HepsiburadaOrderStatusSync or MarketplaceJobTypes.PazaramaOrderStatusSync
        or MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation
        or MarketplaceJobTypes.OrderReconciliation or MarketplaceJobTypes.ShopifyOrderReconciliation
        or MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.HepsiburadaReturnSync or MarketplaceJobTypes.ReturnStatusSync or MarketplaceJobTypes.ReturnReconciliation
        or MarketplaceJobTypes.StockReconciliation
        or MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ShopifyProductSync or MarketplaceJobTypes.HepsiburadaProductSync
        or MarketplaceJobTypes.QuestionSync;

    private static long LockKey(Guid connectionId, string group)
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
