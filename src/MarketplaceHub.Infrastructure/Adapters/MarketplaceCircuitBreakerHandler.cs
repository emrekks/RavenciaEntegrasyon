using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace MarketplaceHub.Infrastructure.Adapters;

// Circuit breaker for marketplace hosts without their own resilience handler (Hepsiburada,
// Shopify). Five consecutive failures (5xx, 408, network errors) open the circuit for 30 s; a 429
// with Retry-After opens it for that long (at most 15 min). While open, calls are answered locally:
// 429 for a quota stop, so the job is deferred without losing an attempt, otherwise 503. After the
// break one probe request is let through; its result closes or reopens the circuit. It sits outside the
// marketplace's rate-limit handler, so a call refused by an open circuit never takes a quota token.
public sealed class MarketplaceCircuitBreakerHandler(MarketplaceCircuitBreakerState state, TimeProvider timeProvider) : DelegatingHandler
{
    internal const int FailureThreshold = 5;
    internal static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaximumQuotaBreak = TimeSpan.FromMinutes(15);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var circuit = state.For(CircuitKeyFor(request.RequestUri));
        if (!circuit.TryEnter(timeProvider.GetUtcNow(), out var probe, out var openResponse)) return openResponse!;
        var recorded = false;
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            var status = (int)response.StatusCode;
            if (status == 429)
                circuit.RecordQuota(timeProvider.GetUtcNow(), RetryAfter(response.Headers.RetryAfter));
            else
                circuit.Record(timeProvider.GetUtcNow(), succeeded: status < 500 && status != 408);
            recorded = true;
            return response;
        }
        catch (HttpRequestException)
        {
            circuit.Record(timeProvider.GetUtcNow(), succeeded: false);
            recorded = true;
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            circuit.Record(timeProvider.GetUtcNow(), succeeded: false);
            recorded = true;
            throw;
        }
        finally
        {
            if (probe && !recorded) circuit.ReleaseProbe();
        }
    }

    // Host plus the first two path segments: one failing API family (e.g. invoice delivery that keeps
    // answering 500 for one package) must not stop the other calls to the same host.
    internal static string CircuitKeyFor(Uri? uri)
    {
        if (uri is null) return "unknown";
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? uri.Host : $"{uri.Host}/{string.Join('/', segments.Take(2))}";
    }

    private TimeSpan? RetryAfter(RetryConditionHeaderValue? header)
    {
        if (header?.Delta is { } delta && delta > TimeSpan.Zero) return delta;
        if (header?.Date is { } date && date - timeProvider.GetUtcNow() is var wait && wait > TimeSpan.Zero) return wait;
        return null;
    }
}

public sealed class MarketplaceCircuitBreakerState
{
    private readonly ConcurrentDictionary<string, MarketplaceCircuit> circuits = new(StringComparer.OrdinalIgnoreCase);

    internal MarketplaceCircuit For(string host) => circuits.GetOrAdd(host, static _ => new MarketplaceCircuit());
}

internal sealed class MarketplaceCircuit
{
    private readonly object sync = new();
    private int consecutiveFailures;
    private DateTimeOffset? openUntil;
    private bool openedForQuota;
    private bool probeActive;

    public bool TryEnter(DateTimeOffset now, out bool probe, out HttpResponseMessage? openResponse)
    {
        lock (sync)
        {
            probe = false;
            openResponse = null;
            if (openUntil is null) return true;
            if (now < openUntil || probeActive)
            {
                openResponse = OpenResponse(openedForQuota, openUntil.Value > now ? openUntil.Value - now : TimeSpan.FromSeconds(1));
                return false;
            }
            probeActive = true;
            probe = true;
            return true;
        }
    }

    public void Record(DateTimeOffset now, bool succeeded)
    {
        lock (sync)
        {
            probeActive = false;
            if (succeeded)
            {
                consecutiveFailures = 0;
                openUntil = null;
                openedForQuota = false;
                return;
            }
            consecutiveFailures++;
            if (openUntil is not null || consecutiveFailures >= MarketplaceCircuitBreakerHandler.FailureThreshold)
            {
                openUntil = now.Add(MarketplaceCircuitBreakerHandler.BreakDuration);
                openedForQuota = false;
            }
        }
    }

    public void RecordQuota(DateTimeOffset now, TimeSpan? retryAfter)
    {
        lock (sync)
        {
            probeActive = false;
            if (retryAfter is not { } wait || wait <= TimeSpan.Zero) return;
            openUntil = now.Add(wait > MarketplaceCircuitBreakerHandler.MaximumQuotaBreak ? MarketplaceCircuitBreakerHandler.MaximumQuotaBreak : wait);
            openedForQuota = true;
        }
    }

    public void ReleaseProbe()
    {
        lock (sync) probeActive = false;
    }

    private static HttpResponseMessage OpenResponse(bool quota, TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(quota ? HttpStatusCode.TooManyRequests : HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"code\":\"LOCAL_CIRCUIT_OPEN\"}")
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : retryAfter);
        return response;
    }
}
