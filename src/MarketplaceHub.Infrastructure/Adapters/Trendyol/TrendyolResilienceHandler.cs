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
        var circuitKey = CircuitKeyFor(request);
        try
        {
            if (!TryEnterCircuit(circuitKey, out halfOpen)) return CircuitOpenResponse();
            await WaitForRateWindowAsync("global", options.RequestsPerInterval, options.RequestInterval, cancellationToken);
            if (RateBucketFor(request) is { } bucket)
                await WaitForRateWindowAsync(bucket, options.OrderRequestsPerInterval, options.OrderRequestInterval, cancellationToken);
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                var statusCode = (int)response.StatusCode;
                var circuitSucceeded = statusCode < 500 && statusCode is not (408 or 429);
                RecordResult(circuitKey, circuitSucceeded, halfOpen);
                return response;
            }
            catch (HttpRequestException)
            {
                RecordResult(circuitKey, false, halfOpen);
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                RecordResult(circuitKey, false, halfOpen);
                throw;
            }
        }
        finally { state.Concurrency.Release(); }
    }

    private bool TryEnterCircuit(string circuitKey, out bool halfOpen)
    {
        lock (state.SyncRoot)
        {
            var circuit = state.CircuitFor(circuitKey);
            halfOpen = false;
            if (circuit.OpenUntil is null) return true;
            if (timeProvider.GetUtcNow() < circuit.OpenUntil) return false;
            if (circuit.HalfOpenRequestActive) return false;
            circuit.HalfOpenRequestActive = true;
            halfOpen = true;
            return true;
        }
    }

    private void RecordResult(string circuitKey, bool succeeded, bool halfOpen)
    {
        lock (state.SyncRoot)
        {
            var circuit = state.CircuitFor(circuitKey);
            if (succeeded)
            {
                circuit.ConsecutiveFailures = 0;
                circuit.OpenUntil = null;
                circuit.HalfOpenRequestActive = false;
                return;
            }

            circuit.HalfOpenRequestActive = false;
            circuit.ConsecutiveFailures++;
            if (halfOpen || circuit.ConsecutiveFailures >= Math.Clamp(options.CircuitFailureThreshold, 2, 50))
                circuit.OpenUntil = timeProvider.GetUtcNow().Add(options.CircuitBreakDuration <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : options.CircuitBreakDuration);
        }
    }

    private async Task WaitForRateWindowAsync(string bucket, int configuredLimit, TimeSpan configuredInterval, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(configuredLimit, 1, 10_000);
        var interval = configuredInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : configuredInterval;
        while (true)
        {
            TimeSpan delay;
            lock (state.SyncRoot)
            {
                var now = timeProvider.GetUtcNow();
                if (!state.RequestStarts.TryGetValue(bucket, out var starts))
                {
                    starts = new Queue<DateTimeOffset>();
                    state.RequestStarts[bucket] = starts;
                }
                while (starts.TryPeek(out var oldest) && now - oldest >= interval) starts.Dequeue();
                if (starts.Count < limit)
                {
                    starts.Enqueue(now);
                    return;
                }
                delay = interval - (now - starts.Peek());
            }
            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(10), timeProvider, cancellationToken);
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
        return string.IsNullOrWhiteSpace(sellerId) ? null : $"orders:{sellerId}";
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
        return $"{uri.Host}:global";
    }

    private static HttpResponseMessage CircuitOpenResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"code\":\"LOCAL_CIRCUIT_OPEN\"}")
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        return response;
    }
}

// HttpClientFactory creates the delegating handler per handler lifetime. The
// limiter and circuit state must outlive that handler, otherwise every worker
// scope gets a fresh limiter and concurrent jobs can collectively exceed the
// seller's Trendyol quota.
public sealed class TrendyolResilienceState
{
    public TrendyolResilienceState(IOptions<TrendyolOptions> options) =>
        Concurrency = new SemaphoreSlim(Math.Clamp(options.Value.MaxConcurrency, 1, 64));

    public SemaphoreSlim Concurrency { get; }
    public object SyncRoot { get; } = new();
    public Dictionary<string, Queue<DateTimeOffset>> RequestStarts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, TrendyolCircuitState> Circuits { get; } = new(StringComparer.Ordinal);

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
}
