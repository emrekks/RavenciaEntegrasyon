namespace MarketplaceHub.Application;

// İşlem Takibi › API Sağlık › "API Sağlığı": per marketplace connection, six signals read only from data the hub already
// stores (job attempts and their error codes, sync cursors and policies, webhook subscriptions, connection backoffs).
// A signal the data cannot answer is reported as such (Measured = false), never filled in.

/// <summary>Whether the marketplace has refused the connection's key: the newest refusal and the newest success after it.</summary>
public sealed record ApiHealthAuthSignal(string State, DateTimeOffset? LastRejectedAt, string? LastRejectedCode, DateTimeOffset? LastOkAt);

/// <summary>The read stream that is furthest behind its schedule: seconds since its last success against its expected interval.</summary>
public sealed record ApiHealthLagSignal(string Stream, long LagSeconds, int ExpectedSeconds, string Level);

/// <summary>The last 24 hours of finished attempts, split by who the code says is at fault.</summary>
public sealed record ApiHealthErrorRateSignal(int Attempts, int Failed, int RemoteSide, int OurSide, int RateLimited, int WorkflowWait);

/// <summary>Attempts that ended in a 429 (a 429 the adapter retried inside one attempt is not recorded), the jobs waiting on a quota refusal and any active quota pause.</summary>
public sealed record ApiHealthRateLimitSignal(int Refused24h, int WaitingJobs, DateTimeOffset? PausedUntil, int LastRunRefusals);

/// <summary>The oldest due pending or retry job: how long it has been runnable, and its type.</summary>
public sealed record ApiHealthQueueSignal(long AgeSeconds, string JobType, int Pending);

/// <summary>Where a webhook is used: the last notification, the scheduler's own verdict, and when the order read last succeeded.</summary>
public sealed record ApiHealthWebhookSignal(bool Used, DateTimeOffset? LastReceivedAt, bool Healthy, DateTimeOffset? LastOrderReadAt, int? FallbackReadSeconds);

public sealed record ApiHealthConnectionSignals(
    Guid ConnectionId,
    string PlatformCode,
    string DisplayName,
    ApiHealthAuthSignal Auth,
    ApiHealthLagSignal? Lag,
    ApiHealthErrorRateSignal ErrorRate,
    ApiHealthRateLimitSignal RateLimit,
    ApiHealthQueueSignal? Queue,
    ApiHealthWebhookSignal Webhook);

public sealed record ApiHealthSignalsView(DateTimeOffset GeneratedAt, IReadOnlyList<ApiHealthConnectionSignals> Connections);

public interface IApiHealthSignalsService
{
    Task<ApiHealthSignalsView> SignalsAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>How a job attempt's error code reads as a signal. The adapters name the code after the HTTP answer (401/403 =
/// *_CREDENTIALS_REJECTED, 429 = *_RATE_LIMITED, 5xx = *_REMOTE_ERROR, 408 and timeouts = *_TIMEOUT, no answer = *_NETWORK_ERROR,
/// other 4xx = *_REQUEST_REJECTED); a code that is none of these (a flow waiting, a contract mismatch) is the hub's own business.</summary>
public static class ApiHealthSignalPolicy
{
    public const string AuthValid = "valid";
    public const string AuthRejected = "rejected";
    public const string AuthUnknown = "unknown";
    public const string LevelOk = "ok";
    public const string LevelWarn = "warn";
    public const string LevelFault = "fault";
    public const double LagWarnFactor = 3;
    public const double LagFaultFactor = 10;

    public static bool IsAuthRefusal(string? code) => ConnectionRetestPolicy.IsAuthRefusal(code);

    public static bool IsRateLimited(string? code) => code is not null && (code.EndsWith("_RATE_LIMITED", StringComparison.Ordinal) || code is "REMOTE_RATE_LIMITED");

    public static bool IsRemoteSide(string? code) =>
        code is not null && (code.EndsWith("_REMOTE_ERROR", StringComparison.Ordinal) || code.EndsWith("_TIMEOUT", StringComparison.Ordinal)
            || code.EndsWith("_NETWORK_ERROR", StringComparison.Ordinal) || code.EndsWith("_GATEWAY_BLOCKED", StringComparison.Ordinal)
            || code is "REMOTE_SERVER_ERROR" or "REMOTE_TIMEOUT" or "REMOTE_UNAVAILABLE");

    public static bool IsOurSide(string? code) =>
        code is not null && (code.EndsWith("_REQUEST_REJECTED", StringComparison.Ordinal) || IsAuthRefusal(code));

    /// <summary>The split of a failed attempt's code: remote, ours, quota or none of them (a flow waiting or the hub's own rule).</summary>
    public static ApiHealthErrorRateSignal ErrorRate(IEnumerable<(bool Succeeded, string? ErrorCode)> attempts)
    {
        int total = 0, failed = 0, remote = 0, ours = 0, quota = 0, wait = 0;
        foreach (var (succeeded, code) in attempts)
        {
            total++;
            if (succeeded) continue;
            failed++;
            if (IsRateLimited(code)) quota++;
            else if (IsRemoteSide(code)) remote++;
            else if (IsOurSide(code)) ours++;
            else wait++;
        }
        return new ApiHealthErrorRateSignal(total, failed, remote, ours, quota, wait);
    }

    /// <summary>"Failed ÷ attempts" counts only the attempts the marketplace answered badly or not at all: remote, ours and quota.
    /// Attempts that ended in a flow waiting (an approval pending, a batch still running) are not failures of the API.</summary>
    public static double? FailureRate(ApiHealthErrorRateSignal signal) =>
        signal.Attempts == 0 ? null : (double)(signal.RemoteSide + signal.OurSide + signal.RateLimited) / signal.Attempts;

    /// <summary>Warn above 3x the expected interval since the last success, a fault above 10x.</summary>
    public static string LagLevel(long lagSeconds, int expectedSeconds)
    {
        if (expectedSeconds <= 0) return LevelOk;
        var ratio = (double)lagSeconds / expectedSeconds;
        return ratio > LagFaultFactor ? LevelFault : ratio > LagWarnFactor ? LevelWarn : LevelOk;
    }

    /// <summary>The stream furthest behind its schedule (by the lag over its interval); null when no stream has a measurable last success.</summary>
    public static ApiHealthLagSignal? WorstLag(IEnumerable<(string Stream, DateTimeOffset? LastSuccessAt, int ExpectedSeconds)> streams, DateTimeOffset now)
    {
        ApiHealthLagSignal? worst = null;
        double worstRatio = -1;
        foreach (var (stream, last, expected) in streams)
        {
            if (last is null || expected <= 0) continue;
            var lag = Math.Max(0, (long)(now - last.Value).TotalSeconds);
            var ratio = (double)lag / expected;
            if (ratio <= worstRatio) continue;
            worstRatio = ratio;
            worst = new ApiHealthLagSignal(stream, lag, expected, LagLevel(lag, expected));
        }
        return worst;
    }

    /// <summary>Valid until a refusal is newer than every success after it; unknown while neither was ever seen.</summary>
    public static ApiHealthAuthSignal Auth(DateTimeOffset? lastRejectedAt, string? lastRejectedCode, DateTimeOffset? lastOkAt, DateTimeOffset? pausedUntil, DateTimeOffset now)
    {
        var stillRejected = lastRejectedAt is not null && (lastOkAt is null || lastOkAt < lastRejectedAt) || pausedUntil is not null && pausedUntil > now;
        if (stillRejected) return new(AuthRejected, lastRejectedAt, lastRejectedCode, lastOkAt);
        if (lastOkAt is null && lastRejectedAt is null) return new(AuthUnknown, null, null, null);
        return new(AuthValid, lastRejectedAt, lastRejectedCode, lastOkAt);
    }
}
