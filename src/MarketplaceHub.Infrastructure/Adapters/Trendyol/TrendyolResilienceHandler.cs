using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed class TrendyolResilienceHandler : DelegatingHandler
{
    private readonly TrendyolOptions options;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim concurrency;
    private readonly object stateLock = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> requestStarts = new(StringComparer.Ordinal);
    private int consecutiveFailures;
    private DateTimeOffset? circuitOpenUntil;
    private bool halfOpenRequestActive;

    public TrendyolResilienceHandler(IOptions<TrendyolOptions> options, TimeProvider timeProvider)
    {
        this.options = options.Value;
        this.timeProvider = timeProvider;
        concurrency = new SemaphoreSlim(Math.Clamp(this.options.MaxConcurrency, 1, 64));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken);
        var halfOpen = false;
        try
        {
            if (!TryEnterCircuit(out halfOpen)) return CircuitOpenResponse();
            await WaitForRateWindowAsync("global", options.RequestsPerInterval, options.RequestInterval, cancellationToken);
            if (RateBucketFor(request) is { } bucket)
                await WaitForRateWindowAsync(bucket, options.OrderRequestsPerInterval, options.OrderRequestInterval, cancellationToken);
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                var statusCode = (int)response.StatusCode;
                var circuitSucceeded = statusCode < 500 && statusCode is not (408 or 429);
                RecordResult(circuitSucceeded, halfOpen);
                return response;
            }
            catch (HttpRequestException)
            {
                RecordResult(false, halfOpen);
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                RecordResult(false, halfOpen);
                throw;
            }
        }
        finally
        {
            concurrency.Release();
        }
    }

    private bool TryEnterCircuit(out bool halfOpen)
    {
        lock (stateLock)
        {
            halfOpen = false;
            if (circuitOpenUntil is null) return true;
            if (timeProvider.GetUtcNow() < circuitOpenUntil) return false;
            if (halfOpenRequestActive) return false;
            halfOpenRequestActive = true;
            halfOpen = true;
            return true;
        }
    }

    private void RecordResult(bool succeeded, bool halfOpen)
    {
        lock (stateLock)
        {
            if (succeeded)
            {
                consecutiveFailures = 0;
                circuitOpenUntil = null;
                halfOpenRequestActive = false;
                return;
            }

            halfOpenRequestActive = false;
            consecutiveFailures++;
            if (halfOpen || consecutiveFailures >= Math.Clamp(options.CircuitFailureThreshold, 2, 50))
                circuitOpenUntil = timeProvider.GetUtcNow().Add(options.CircuitBreakDuration <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : options.CircuitBreakDuration);
        }
    }

    private async Task WaitForRateWindowAsync(string bucket, int configuredLimit, TimeSpan configuredInterval, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(configuredLimit, 1, 10_000);
        var interval = configuredInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : configuredInterval;
        while (true)
        {
            TimeSpan delay;
            lock (stateLock)
            {
                var now = timeProvider.GetUtcNow();
                if (!requestStarts.TryGetValue(bucket, out var starts))
                {
                    starts = new Queue<DateTimeOffset>();
                    requestStarts[bucket] = starts;
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
