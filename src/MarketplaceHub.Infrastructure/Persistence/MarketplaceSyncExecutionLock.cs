using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Microsoft.EntityFrameworkCore;

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
            await connection.OpenAsync(cancellationToken);
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
        if (type.Contains("ORDER", StringComparison.Ordinal) || type.Contains("SHIPMENT", StringComparison.Ordinal)) return "orders";
        if (type.Contains("RETURN", StringComparison.Ordinal) || type.Contains("CLAIM", StringComparison.Ordinal)) return "returns";
        if (type.Contains("STOCK", StringComparison.Ordinal) || type.Contains("INVENTORY", StringComparison.Ordinal) || type.Contains("PRICE", StringComparison.Ordinal)) return "stock";
        if (type.Contains("PRODUCT", StringComparison.Ordinal) || type.Contains("CATALOG", StringComparison.Ordinal)) return "products";
        if (type.Contains("REFERENCE", StringComparison.Ordinal) || type.Contains("CATEGORY", StringComparison.Ordinal) || type.Contains("BRAND", StringComparison.Ordinal)) return "references";
        return "connection";
    }

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
            if (acquired)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(@key)";
                command.Parameters.AddWithValue("key", key);
                await command.ExecuteScalarAsync();
            }
        }
        finally
        {
            if (closeConnectionOnDispose && db is not null) await db.Database.CloseConnectionAsync();
        }
    }
}
