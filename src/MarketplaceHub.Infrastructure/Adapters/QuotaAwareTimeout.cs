namespace MarketplaceHub.Infrastructure.Adapters;

// The request timeout of a call that first waits for a shared marketplace quota token. Waiting for the token
// has its own, longer cap; the request timeout starts only when the rate-limit handler releases the request
// (through the handler's QuotaAcquired option), so a call queued behind the quota is not cancelled as a
// timeout before it was ever sent. QuotaAcquired tells the two apart when the token is cancelled.
internal sealed class QuotaAwareTimeout : IDisposable
{
    internal static readonly TimeSpan MaximumQuotaWait = TimeSpan.FromMinutes(5);

    private readonly CancellationTokenSource source;
    private readonly TimeSpan requestTimeout;
    private volatile bool quotaAcquired;

    public QuotaAwareTimeout(HttpRequestMessage request, HttpRequestOptionsKey<Action> quotaAcquiredKey, TimeSpan requestTimeout, CancellationToken cancellationToken)
    {
        source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.requestTimeout = requestTimeout;
        source.CancelAfter(requestTimeout + MaximumQuotaWait);
        request.Options.Set(quotaAcquiredKey, OnQuotaAcquired);
        // The circuit tells this timeout from the caller's own cancellation by the caller's token.
        CallerCancellation.Mark(request, cancellationToken);
    }

    public CancellationToken Token => source.Token;

    public bool QuotaAcquired => quotaAcquired;

    private void OnQuotaAcquired()
    {
        quotaAcquired = true;
        try { source.CancelAfter(requestTimeout); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose() => source.Dispose();
}
