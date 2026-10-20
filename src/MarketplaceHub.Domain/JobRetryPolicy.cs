namespace MarketplaceHub.Domain;

public static class JobRetryPolicy
{
    // About eight and a half minutes over the five retries, so a job outlasts a marketplace outage
    // of several minutes instead of failing for good within two.
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5)
    ];

    public const int DefaultMaxAttempts = 6;
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(2);

    public static IReadOnlyList<TimeSpan> DefaultSchedule => DefaultRetryDelays;

    public static TimeSpan HeartbeatInterval(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        return TimeSpan.FromTicks(leaseDuration.Ticks / 4);
    }

    // A heartbeat that fails (a database blip) is not a lost lease: the job keeps running while its
    // lease would still be valid at the next heartbeat.
    public static bool KeepRunningAfterHeartbeatFailure(TimeSpan sinceLastRenewal, TimeSpan leaseDuration) =>
        sinceLastRenewal + HeartbeatInterval(leaseDuration) < leaseDuration;

    public static TimeSpan DelayAfterAttempt(int attemptCount, Guid jobId)
    {
        if (attemptCount <= 0) throw new ArgumentOutOfRangeException(nameof(attemptCount));
        var baseDelay = DefaultRetryDelays[Math.Min(attemptCount - 1, DefaultRetryDelays.Length - 1)];
        var jitterPercent = JitterPercent(jobId);
        return baseDelay + TimeSpan.FromTicks(baseDelay.Ticks * jitterPercent / 100);
    }

    // 0-20 % from a hash of the whole job id. The first byte alone is part of a UUIDv7's timestamp, so jobs
    // created within the same few seconds used to get the same jitter and retried together.
    public static int JitterPercent(Guid jobId)
    {
        var hash = 2166136261u;
        foreach (var value in jobId.ToByteArray()) hash = (hash ^ value) * 16777619u;
        return (int)(hash % 21);
    }
}

public static class InvoiceDeliveryRetryPolicy
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30)
    ];

    public static IReadOnlyList<TimeSpan> Schedule => RetryDelays;

    public static TimeSpan DelayAfterAttempt(int attemptCount, TimeSpan? providerRetryAfter = null)
    {
        if (attemptCount <= 0) throw new ArgumentOutOfRangeException(nameof(attemptCount));

        var policyDelay = RetryDelays[Math.Min(attemptCount - 1, RetryDelays.Length - 1)];
        return providerRetryAfter is { } requested && requested > policyDelay
            ? requested
            : policyDelay;
    }
}
