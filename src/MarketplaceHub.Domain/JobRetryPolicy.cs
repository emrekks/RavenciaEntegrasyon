namespace MarketplaceHub.Domain;

public static class JobRetryPolicy
{
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
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
        var jitterPercent = jobId.ToByteArray()[0] % 21;
        return baseDelay + TimeSpan.FromTicks(baseDelay.Ticks * jitterPercent / 100);
    }
}
