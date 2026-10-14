using System.Data;
using System.Data.Common;

namespace MarketplaceHub.Api.Realtime;

/// <summary>
/// Ensures only one API instance dispatches the durable operations outbox at a time.
/// PostgreSQL releases this session lock automatically if the connection is lost.
/// </summary>
public static class OperationsRealtimeDispatchLease
{
    private const long LockKey = 0x52564156454E4349L;

    public static async Task<bool> TryAcquireAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(@lock_key);";
        AddLockKey(command);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    public static async Task ReleaseAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_unlock(@lock_key);";
        AddLockKey(command);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not true)
            throw new InvalidOperationException("Realtime outbox advisory lease was not held by this database session.");
    }

    private static void AddLockKey(DbCommand command)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_key";
        parameter.DbType = DbType.Int64;
        parameter.Value = LockKey;
        command.Parameters.Add(parameter);
    }
}
