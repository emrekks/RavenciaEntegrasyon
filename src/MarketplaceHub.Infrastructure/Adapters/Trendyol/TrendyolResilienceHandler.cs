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
    private readonly Queue<DateTimeOffset> requestStarts = new();
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
            await WaitForRateWindowAsync(cancellationToken);
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                RecordResult(response.IsSuccessStatusCode || (int)response.StatusCode < 500, halfOpen);
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

    private async Task WaitForRateWindowAsync(CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(options.RequestsPerInterval, 1, 10_000);
        var interval = options.RequestInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : options.RequestInterval;
        while (true)
        {
            TimeSpan delay;
            lock (stateLock)
            {
                var now = timeProvider.GetUtcNow();
                while (requestStarts.TryPeek(out var oldest) && now - oldest >= interval) requestStarts.Dequeue();
                if (requestStarts.Count < limit)
                {
                    requestStarts.Enqueue(now);
                    return;
                }
                delay = interval - (now - requestStarts.Peek());
            }
            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(10), timeProvider, cancellationToken);
        }
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
