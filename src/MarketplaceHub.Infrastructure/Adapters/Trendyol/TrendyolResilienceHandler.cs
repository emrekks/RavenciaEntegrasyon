using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed class TrendyolResilienceHandler : DelegatingHandler
{
    // Invoked once the request has its quota token, right before it is sent; the caller starts the
    // network timeout there so time spent queueing for the quota is not counted as a timeout.
    public static readonly HttpRequestOptionsKey<Action> QuotaAcquiredKey = new("MarketplaceHub.Trendyol.QuotaAcquired");

    private readonly TrendyolOptions options;
    private readonly TimeProvider timeProvider;
    private readonly TrendyolResilienceState state;
    private readonly ISharedRateLimiter? sharedLimiter;
    private readonly ILogger<TrendyolResilienceHandler>? logger;

    public TrendyolResilienceHandler(IOptions<TrendyolOptions> options, TimeProvider timeProvider, TrendyolResilienceState state, ISharedRateLimiter? sharedLimiter = null, ILogger<TrendyolResilienceHandler>? logger = null)
    {
        this.options = options.Value;
        this.timeProvider = timeProvider;
        this.state = state;
        this.sharedLimiter = sharedLimiter;
        this.logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Wait for the quota before taking a concurrency slot, so reads queued behind the order-read
        // spacing do not hold the slots that product and stock requests need.
        await WaitForQuotaAsync(request, cancellationToken);
        await state.Concurrency.WaitAsync(cancellationToken);
        var halfOpen = false;
        var resultRecorded = false;
        var circuitKey = CircuitKeyFor(request);
        try
        {
            if (!TryEnterCircuit(circuitKey, out halfOpen, out var circuitRetryAfter)) return CircuitOpenResponse(circuitRetryAfter);
            if (request.Options.TryGetValue(QuotaAcquiredKey, out var onQuotaAcquired)) onQuotaAcquired();
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                var statusCode = (int)response.StatusCode;
                var circuitSucceeded = statusCode < 500 && statusCode is not (408 or 429);
                var remoteRetryAfter = statusCode == 429 ? RetryAfterDelay(response.Headers.RetryAfter) : null;
                if (statusCode == 429 && RateBucketFor(request) is { } rateLimitedBucket)
                    LogTierChange(rateLimitedBucket, tier => tier.RecordRateLimited(timeProvider.GetUtcNow()));
                RecordResult(circuitKey, circuitSucceeded, halfOpen, remoteRetryAfter);
                resultRecorded = true;
                return response;
            }
            catch (HttpRequestException)
            {
                RecordResult(circuitKey, false, halfOpen);
                resultRecorded = true;
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                RecordResult(circuitKey, false, halfOpen);
                resultRecorded = true;
                throw;
            }
        }
        finally
        {
            if (halfOpen && !resultRecorded) ReleaseHalfOpen(circuitKey);
            state.Concurrency.Release();
        }
    }

    private bool TryEnterCircuit(string circuitKey, out bool halfOpen, out TimeSpan? retryAfter)
    {
        lock (state.SyncRoot)
        {
            var now = timeProvider.GetUtcNow();
            state.TrimIdleState(now, TimeSpan.FromMinutes(1));
            var circuit = state.CircuitFor(circuitKey);
            circuit.LastTouchedAt = now;
            halfOpen = false;
            retryAfter = null;
            if (circuit.OpenUntil is null) return true;
            if (now < circuit.OpenUntil)
            {
                retryAfter = circuit.OpenUntil.Value - now;
                return false;
            }
            if (circuit.HalfOpenRequestActive) return false;
            circuit.HalfOpenRequestActive = true;
            halfOpen = true;
            return true;
        }
    }

    private void RecordResult(string circuitKey, bool succeeded, bool halfOpen, TimeSpan? remoteRetryAfter = null)
    {
        lock (state.SyncRoot)
        {
            var now = timeProvider.GetUtcNow();
            var circuit = state.CircuitFor(circuitKey);
            circuit.LastTouchedAt = now;
            if (succeeded)
            {
                circuit.ConsecutiveFailures = 0;
                circuit.OpenUntil = null;
                circuit.HalfOpenRequestActive = false;
                return;
            }

            circuit.HalfOpenRequestActive = false;
            circuit.ConsecutiveFailures++;
            if (remoteRetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero)
            {
                var boundedRetryAfter = TimeSpan.FromMinutes(Math.Min(retryAfter.TotalMinutes, 15));
                circuit.ConsecutiveFailures = Math.Max(circuit.ConsecutiveFailures, Math.Clamp(options.CircuitFailureThreshold, 2, 50));
                circuit.OpenUntil = now.Add(boundedRetryAfter);
                return;
            }
            if (halfOpen || circuit.ConsecutiveFailures >= Math.Clamp(options.CircuitFailureThreshold, 2, 50))
                circuit.OpenUntil = circuit.LastTouchedAt.Add(options.CircuitBreakDuration <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : options.CircuitBreakDuration);
        }
    }

    private void ReleaseHalfOpen(string circuitKey)
    {
        lock (state.SyncRoot)
        {
            var circuit = state.CircuitFor(circuitKey);
            circuit.HalfOpenRequestActive = false;
            circuit.LastTouchedAt = timeProvider.GetUtcNow();
        }
    }

    // The seller quota is shared by the API and every worker, so it is counted in PostgreSQL. If the
    // shared limiter cannot be reached, the in-process windows keep this process within the quota.
    private async Task WaitForQuotaAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var globalBucket = GlobalRateBucketFor(request);
        int? learnedTotal = null;
        if (RateBucketFor(request) is { } sellerBucket)
            lock (state.SyncRoot) learnedTotal = state.OrderTierFor(sellerBucket).Limit(options.OrderRequestsPerInterval);
        var orderQuota = OrderQuotaFor(request, options, TrendyolQuotaLane.IsBackground, learnedTotal);
        var quotaWaitStarted = timeProvider.GetTimestamp();
        if (sharedLimiter is not null)
        {
            try
            {
                await SharedRateLimits.WaitAsync(sharedLimiter, $"trendyol:{globalBucket}", SharedRateLimits.ForWindow(options.RequestsPerInterval, options.RequestInterval), timeProvider, cancellationToken);
                if (orderQuota is { } quota)
                {
                    quotaWaitStarted = timeProvider.GetTimestamp();
                    await SharedRateLimits.WaitAsync(sharedLimiter, $"trendyol:{quota.Bucket}", SharedRateLimits.ForWindow(quota.Limit, options.OrderRequestInterval), timeProvider, cancellationToken);
                    RecordQuotaPressure(request, quotaWaitStarted);
                    if (quota.Spacing > TimeSpan.Zero)
                        await SharedRateLimits.WaitAsync(sharedLimiter, $"trendyol:{quota.SpacingBucket}", SharedRateLimits.ForSpacing(quota.Spacing), timeProvider, cancellationToken);
                }
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger?.LogWarning(exception, "Shared Trendyol rate limiter unavailable; using the in-process limiter");
            }
        }
        await WaitForRateWindowAsync(globalBucket, options.RequestsPerInterval, options.RequestInterval, cancellationToken);
        if (orderQuota is { } localQuota)
        {
            quotaWaitStarted = timeProvider.GetTimestamp();
            await WaitForRateWindowAsync(localQuota.Bucket, localQuota.Limit, options.OrderRequestInterval, cancellationToken);
            RecordQuotaPressure(request, quotaWaitStarted);
            if (localQuota.Spacing > TimeSpan.Zero)
                await WaitForRateWindowAsync(localQuota.SpacingBucket, int.MaxValue, options.OrderRequestInterval, cancellationToken, localQuota.Spacing);
        }
    }

    // A read that had to wait for the order quota itself (not for the page spacing) shows the quota is full.
    private void RecordQuotaPressure(HttpRequestMessage request, long waitStarted)
    {
        if (timeProvider.GetElapsedTime(waitStarted) < TimeSpan.FromMilliseconds(500) || RateBucketFor(request) is not { } sellerBucket) return;
        LogTierChange(sellerBucket, tier => tier.RecordPressure(timeProvider.GetUtcNow()));
    }

    private void LogTierChange(string sellerBucket, Func<TrendyolOrderTier, bool> record)
    {
        int? limit = null;
        lock (state.SyncRoot)
        {
            var tier = state.OrderTierFor(sellerBucket);
            if (record(tier)) limit = tier.Limit(options.OrderRequestsPerInterval);
        }
        if (limit is { } changed) logger?.LogInformation("Trendyol order quota for {SellerBucket} is now taken as {Limit} requests a minute", sellerBucket, changed);
    }

    internal sealed record OrderQuota(string Bucket, int Limit, string SpacingBucket, TimeSpan Spacing);

    // The seller's order quota covers the stream and Order V2. It is split so background reads
    // can never use up what a new-order read or a user action needs. Trendyol recommends five
    // seconds between stream calls; that spacing is kept between the pages of one query, while
    // a read that starts a new query waits only for the shorter start spacing.
    internal static OrderQuota? OrderQuotaFor(HttpRequestMessage request, TrendyolOptions options, bool background, int? learnedTotal = null)
    {
        if (RateBucketFor(request) is not { } orderBucket) return null;
        var (foreground, backgroundLimit) = OrderQuotaSplit(options, learnedTotal);
        var limit = background ? backgroundLimit : foreground;
        var bucket = $"{orderBucket}:{(background ? "bg" : "fg")}";
        if (!IsOrderStream(request)) return new(bucket, limit, $"{orderBucket}:spacing", TimeSpan.Zero);
        var nextPage = request.RequestUri?.Query.Contains("nextCursor=", StringComparison.OrdinalIgnoreCase) == true;
        return nextPage
            ? new(bucket, limit, $"{orderBucket}:spacing", options.OrderRequestMinimumInterval)
            : new(bucket, limit, $"{orderBucket}:spacing:start", options.OrderStreamStartMinimumInterval);
    }

    // Requests per order interval for new-order reads and user actions, and for everything else.
    internal static (int Foreground, int Background) OrderQuotaSplit(TrendyolOptions options, int? learnedTotal = null)
    {
        var total = Math.Max(2, learnedTotal ?? options.OrderRequestsPerInterval);
        var foreground = Math.Clamp((int)Math.Round(total * Math.Clamp(options.OrderForegroundShare, 0.1, 0.9)), 1, total - 1);
        return (foreground, total - foreground);
    }

    internal static bool IsOrderStream(HttpRequestMessage request) =>
        request.RequestUri?.AbsolutePath.Contains("/orders/stream", StringComparison.OrdinalIgnoreCase) == true;

    private async Task WaitForRateWindowAsync(string bucket, int configuredLimit, TimeSpan configuredInterval, CancellationToken cancellationToken, TimeSpan? minimumSpacing = null)
    {
        var limit = Math.Clamp(configuredLimit, 1, 10_000);
        var interval = configuredInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : configuredInterval;
        var spacing = minimumSpacing is { } configuredSpacing && configuredSpacing > TimeSpan.Zero ? configuredSpacing : TimeSpan.Zero;
        while (true)
        {
            TimeSpan delay;
            lock (state.SyncRoot)
            {
                var now = timeProvider.GetUtcNow();
                state.TrimIdleState(now, interval);
                if (!state.RequestStarts.TryGetValue(bucket, out var starts))
                {
                    starts = new Queue<DateTimeOffset>();
                    state.RequestStarts[bucket] = starts;
                }
                state.RateBucketLastTouched[bucket] = now;
                while (starts.TryPeek(out var oldest) && now - oldest >= interval) starts.Dequeue();
                var spacingDelay = state.RateBucketLastStarted.TryGetValue(bucket, out var lastStarted)
                    ? lastStarted.Add(spacing) - now
                    : TimeSpan.Zero;
                var windowDelay = starts.Count < limit ? TimeSpan.Zero : interval - (now - starts.Peek());
                delay = spacingDelay > windowDelay ? spacingDelay : windowDelay;
                if (delay <= TimeSpan.Zero)
                {
                    starts.Enqueue(now);
                    state.RateBucketLastStarted[bucket] = now;
                    return;
                }
                state.RateBucketWaiters[bucket] = state.RateBucketWaiters.GetValueOrDefault(bucket) + 1;
            }
            try
            {
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(10), timeProvider, cancellationToken);
            }
            finally
            {
                lock (state.SyncRoot)
                {
                    if (state.RateBucketWaiters.TryGetValue(bucket, out var waiters))
                    {
                        if (waiters <= 1) state.RateBucketWaiters.Remove(bucket);
                        else state.RateBucketWaiters[bucket] = waiters - 1;
                    }
                    state.RateBucketLastTouched[bucket] = timeProvider.GetUtcNow();
                }
            }
        }
    }

    internal static string? RateBucketFor(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is not { } uri) return null;
        var path = uri.AbsolutePath;
        if (!path.Contains("/orders/stream", StringComparison.OrdinalIgnoreCase) && !path.Contains("/v2/orders", StringComparison.OrdinalIgnoreCase)) return null;
        const string marker = "/sellers/";
        var sellerStart = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (sellerStart < 0) return null;
        sellerStart += marker.Length;
        var sellerEnd = path.IndexOf('/', sellerStart);
        if (sellerEnd <= sellerStart) return null;
        var sellerId = path[sellerStart..sellerEnd];
        return string.IsNullOrWhiteSpace(sellerId) ? null : $"orders:{uri.Host}:{sellerId}";
    }

    internal static string GlobalRateBucketFor(HttpRequestMessage request)
    {
        var host = request.RequestUri?.Host ?? "unknown";
        var identity = request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(identity) ? $"global:{host}:anonymous" : $"global:{host}:{identity}";
    }

    // One circuit per seller and endpoint family. Trendyol counts its quotas per service, so a 429 from
    // the order read quota (stream and Order V2) must not stop the seller's stock, price, claim or
    // invoice requests, which have quotas of their own.
    internal static string CircuitKeyFor(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        if (uri is null) return "unknown:global";
        const string marker = "/sellers/";
        var path = uri.AbsolutePath;
        var markerStart = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerStart >= 0)
        {
            var sellerStart = markerStart + marker.Length;
            var sellerEnd = path.IndexOf('/', sellerStart);
            if (sellerEnd < 0) sellerEnd = path.Length;
            if (sellerEnd > sellerStart)
                return $"{uri.Host}:seller:{path[sellerStart..sellerEnd]}:{CircuitFamily(request, path, markerStart, sellerEnd)}";
        }
        var identity = request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(identity) ? $"{uri.Host}:global" : $"{uri.Host}:client:{identity}";
    }

    // "order-reads" for the shared order quota; otherwise the service before /sellers/ and the resource
    // after the seller id, e.g. "inventory:products", "order:claims", "integration:seller-invoice-links".
    private static string CircuitFamily(HttpRequestMessage request, string path, int markerStart, int sellerEnd)
    {
        if (RateBucketFor(request) is not null) return "order-reads";
        var before = path[..markerStart].TrimEnd('/');
        var service = before[(before.LastIndexOf('/') + 1)..];
        var resource = path[sellerEnd..].Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(segment => !segment.Equals("v2", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return $"{(service.Length == 0 ? "root" : service)}:{resource}".ToLowerInvariant();
    }

    private TimeSpan? RetryAfterDelay(System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero) return delta;
        if (retryAfter?.Date is { } date)
        {
            var delay = date - timeProvider.GetUtcNow();
            if (delay > TimeSpan.Zero) return delay;
        }
        return null;
    }

    private static HttpResponseMessage CircuitOpenResponse(TimeSpan? retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"code\":\"LOCAL_CIRCUIT_OPEN\"}")
        };
        var delay = retryAfter is { } configuredDelay && configuredDelay > TimeSpan.Zero ? configuredDelay : TimeSpan.FromSeconds(1);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }
}

// HttpClientFactory creates the delegating handler per handler lifetime. The
// limiter and circuit state must outlive that handler, otherwise every worker
// scope gets a fresh limiter and concurrent jobs can collectively exceed the
// seller's Trendyol quota.
public sealed class TrendyolResilienceState
{
    internal const int MaxTrackedStateEntries = 4096;

    public TrendyolResilienceState(IOptions<TrendyolOptions> options) =>
        Concurrency = new SemaphoreSlim(Math.Clamp(options.Value.MaxConcurrency, 1, 64));

    public SemaphoreSlim Concurrency { get; }
    public object SyncRoot { get; } = new();
    public Dictionary<string, Queue<DateTimeOffset>> RequestStarts { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> RateBucketWaiters { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, DateTimeOffset> RateBucketLastTouched { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, DateTimeOffset> RateBucketLastStarted { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, TrendyolCircuitState> Circuits { get; } = new(StringComparer.Ordinal);
    // The learned order quota tier per seller (one entry per Trendyol seller this process serves).
    private Dictionary<string, TrendyolOrderTier> OrderTiers { get; } = new(StringComparer.Ordinal);

    internal TrendyolOrderTier OrderTierFor(string sellerBucket)
    {
        if (!OrderTiers.TryGetValue(sellerBucket, out var tier))
        {
            tier = new TrendyolOrderTier();
            OrderTiers[sellerBucket] = tier;
        }
        return tier;
    }

    internal void TrimIdleState(DateTimeOffset now, TimeSpan rateInterval)
    {
        var staleRateKeys = RequestStarts
            .Where(pair => pair.Value.Count == 0
                && !RateBucketWaiters.ContainsKey(pair.Key)
                && RateBucketLastTouched.TryGetValue(pair.Key, out var touched)
                && now - touched >= rateInterval)
            .OrderBy(pair => RateBucketLastTouched[pair.Key])
            .Take(Math.Max(0, RequestStarts.Count - MaxTrackedStateEntries))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in staleRateKeys)
        {
            RequestStarts.Remove(key);
            RateBucketLastTouched.Remove(key);
            RateBucketLastStarted.Remove(key);
        }

        var staleCircuitKeys = Circuits
            .Where(pair => !pair.Value.HalfOpenRequestActive
                && (pair.Value.OpenUntil is null || pair.Value.OpenUntil <= now)
                && pair.Value.ConsecutiveFailures == 0
                && now - pair.Value.LastTouchedAt >= rateInterval)
            .OrderBy(pair => pair.Value.LastTouchedAt)
            .Take(Math.Max(0, Circuits.Count - MaxTrackedStateEntries))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in staleCircuitKeys) Circuits.Remove(key);
    }

    public TrendyolCircuitState CircuitFor(string key)
    {
        if (!Circuits.TryGetValue(key, out var circuit))
        {
            circuit = new TrendyolCircuitState();
            Circuits[key] = circuit;
        }
        return circuit;
    }
}

public sealed class TrendyolCircuitState
{
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? OpenUntil { get; set; }
    public bool HalfOpenRequestActive { get; set; }
    public DateTimeOffset LastTouchedAt { get; set; }
}
