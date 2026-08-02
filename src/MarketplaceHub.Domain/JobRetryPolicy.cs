namespace MarketplaceHub.Domain;

public static class JobRetryPolicy
{
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(20),
        TimeSpan.FromHours(1)
    ];

    public const int DefaultMaxAttempts = 6;
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(2);

    public static IReadOnlyList<TimeSpan> DefaultSchedule => DefaultRetryDelays;

    public static TimeSpan HeartbeatInterval(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        return TimeSpan.FromTicks(leaseDuration.Ticks / 4);
    }

    public static TimeSpan DelayAfterAttempt(int attemptCount, Guid jobId)
    {
        if (attemptCount <= 0) throw new ArgumentOutOfRangeException(nameof(attemptCount));
        var baseDelay = DefaultRetryDelays[Math.Min(attemptCount - 1, DefaultRetryDelays.Length - 1)];
        var jitterPercent = 10 + jobId.ToByteArray()[0] % 11;
        return baseDelay + TimeSpan.FromTicks(baseDelay.Ticks * jitterPercent / 100);
    }
}
