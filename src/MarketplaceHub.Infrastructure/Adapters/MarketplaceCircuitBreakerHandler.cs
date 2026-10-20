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

    // G11: a request that will first wait for a quota token (its rate-limit handler calls PassProbeGate once the
    // token is taken) claims the half-open probe only then, so a probe queued for minutes behind the quota no
    // longer keeps every other request out with 503s.
    internal static readonly HttpRequestOptionsKey<Func<HttpResponseMessage?>> ProbeGateKey = new("MarketplaceHub.Circuit.ProbeGate");

    // Called by a rate-limit handler right after the quota token: null lets the request go, otherwise the
    // local open-circuit answer is returned instead of sending it.
    internal static HttpResponseMessage? PassProbeGate(HttpRequestMessage request) =>
        request.Options.TryGetValue(ProbeGateKey, out var gate) ? gate() : null;

    private static bool WaitsForQuota(HttpRequestMessage request) =>
        request.Options.TryGetValue(Hepsiburada.HepsiburadaRateLimitHandler.QuotaAcquiredKey, out _)
        || request.Options.TryGetValue(N11.N11RateLimitHandler.QuotaAcquiredKey, out _)
        || request.Options.TryGetValue(Pazarama.PazaramaRateLimitHandler.QuotaAcquiredKey, out _);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var circuit = state.For(CircuitKeyFor(request.RequestUri));
        var deferProbe = WaitsForQuota(request);
        if (!circuit.TryEnter(timeProvider.GetUtcNow(), deferProbe, out var probe, out var openResponse)) return openResponse!;
        var refusedAtGate = false;
        if (deferProbe)
            request.Options.Set(ProbeGateKey, () =>
            {
                if (!circuit.TryClaimDeferredProbe(timeProvider.GetUtcNow(), out var claimed, out var refused))
                {
                    refusedAtGate = true;
                    return refused;
                }
                probe = claimed;
                return null;
            });
        var recorded = false;
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            // Answered locally at the gate: nothing reached the marketplace, so the circuit learns nothing.
            if (refusedAtGate)
            {
                recorded = true;
                return response;
            }
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

    // deferProbe: past the break the request is let on towards its quota wait without claiming the probe;
    // TryClaimDeferredProbe claims it once the token is held.
    public bool TryEnter(DateTimeOffset now, bool deferProbe, out bool probe, out HttpResponseMessage? openResponse)
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
            if (deferProbe) return true;
            probeActive = true;
            probe = true;
            return true;
        }
    }

    public bool TryClaimDeferredProbe(DateTimeOffset now, out bool probe, out HttpResponseMessage? openResponse)
    {
        lock (sync)
        {
            probe = false;
            openResponse = null;
            // Another probe closed the circuit while this request waited: it goes as an ordinary request.
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
