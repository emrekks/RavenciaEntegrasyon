using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed class TrendyolResilienceHandler : DelegatingHandler
{
    private readonly TrendyolOptions options;
    private readonly TimeProvider timeProvider;
    private readonly TrendyolResilienceState state;

    public TrendyolResilienceHandler(IOptions<TrendyolOptions> options, TimeProvider timeProvider, TrendyolResilienceState state)
    {
        this.options = options.Value;
        this.timeProvider = timeProvider;
        this.state = state;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await state.Concurrency.WaitAsync(cancellationToken);
        var halfOpen = false;
        var resultRecorded = false;
        var circuitKey = CircuitKeyFor(request);
        try
        {
            if (!TryEnterCircuit(circuitKey, out halfOpen, out var circuitRetryAfter)) return CircuitOpenResponse(circuitRetryAfter);
            await WaitForRateWindowAsync(GlobalRateBucketFor(request), options.RequestsPerInterval, options.RequestInterval, cancellationToken);
            if (RateBucketFor(request) is { } bucket)
                await WaitForRateWindowAsync(bucket, options.OrderRequestsPerInterval, options.OrderRequestInterval, cancellationToken, options.OrderRequestMinimumInterval);
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                var statusCode = (int)response.StatusCode;
                var circuitSucceeded = statusCode < 500 && statusCode is not (408 or 429);
                var remoteRetryAfter = statusCode == 429 ? RetryAfterDelay(response.Headers.RetryAfter) : null;
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

    internal static string CircuitKeyFor(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        if (uri is null) return "unknown:global";
        const string marker = "/sellers/";
        var sellerStart = uri.AbsolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (sellerStart >= 0)
        {
            sellerStart += marker.Length;
            var sellerEnd = uri.AbsolutePath.IndexOf('/', sellerStart);
            if (sellerEnd < 0) sellerEnd = uri.AbsolutePath.Length;
            if (sellerEnd > sellerStart)
                return $"{uri.Host}:seller:{uri.AbsolutePath[sellerStart..sellerEnd]}";
        }
        var identity = request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(identity) ? $"{uri.Host}:global" : $"{uri.Host}:client:{identity}";
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
