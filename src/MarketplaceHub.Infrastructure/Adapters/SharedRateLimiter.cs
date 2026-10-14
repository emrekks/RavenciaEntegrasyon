using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Adapters;

// A token bucket shared by every process through PostgreSQL (integration.take_rate_token).
public interface ISharedRateLimiter
{
    // Takes one token. Returns TimeSpan.Zero when it was taken, otherwise how long to wait.
    ValueTask<TimeSpan> TryTakeAsync(string key, double capacity, double tokensPerSecond, CancellationToken cancellationToken);
}

public sealed class PostgresSharedRateLimiter(NpgsqlDataSource dataSource) : ISharedRateLimiter
{
    public async ValueTask<TimeSpan> TryTakeAsync(string key, double capacity, double tokensPerSecond, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT integration.take_rate_token($1, $2, $3)");
        command.Parameters.Add(new NpgsqlParameter { Value = StorageKey(key) });
        command.Parameters.Add(new NpgsqlParameter { Value = capacity });
        command.Parameters.Add(new NpgsqlParameter { Value = tokensPerSecond });
        var seconds = Convert.ToDouble(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
    }

    internal static string StorageKey(string key) => key.Length <= 200
        ? key
        : $"{key[..120]}#{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32]}";
}

public static class SharedRateLimits
{
    // A sliding-window limit of `limit` requests per `window` expressed as a token bucket that can
    // never exceed it in any window: burst + rate * window <= limit.
    public static (double Capacity, double TokensPerSecond) ForWindow(int limit, TimeSpan window)
    {
        var safeLimit = Math.Max(1, limit);
        var seconds = Math.Max(0.001, window.TotalSeconds);
        var capacity = Math.Max(1, Math.Floor(safeLimit * 0.2));
        return (capacity, Math.Max(0.001, (safeLimit - capacity) / seconds));
    }

    // At most one request per `spacing`.
    public static (double Capacity, double TokensPerSecond) ForSpacing(TimeSpan spacing) =>
        (1, 1 / Math.Max(0.001, spacing.TotalSeconds));

    public static async Task WaitAsync(ISharedRateLimiter limiter, string key, (double Capacity, double TokensPerSecond) bucket, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        while (true)
        {
            var wait = await limiter.TryTakeAsync(key, bucket.Capacity, bucket.TokensPerSecond, cancellationToken);
            if (wait <= TimeSpan.Zero) return;
            var bounded = wait < TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : wait > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait;
            await Task.Delay(bounded, timeProvider, cancellationToken);
        }
    }
}
