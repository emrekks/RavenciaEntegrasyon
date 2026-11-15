using MarketplaceHub.Infrastructure.Jobs;

namespace MarketplaceHub.Infrastructure.Adapters;

// The request timeout of a call that first waits for a shared marketplace quota token. Waiting for the token
// has its own, longer cap; the request timeout starts only when the rate-limit handler releases the request
// (through the handler's QuotaAcquired option), so a call queued behind the quota is not cancelled as a
// timeout before it was ever sent. QuotaAcquired tells the two apart when the token is cancelled.
// KUYRUK-1: a worker shutdown also ends the wait while no token is held (QuotaWaitShutdown); the call then ends as a
// local quota wait like any other wait that never got its token. StoppedBeforeStart: do not send at all.
internal sealed class QuotaAwareTimeout : IDisposable
{
    internal static readonly TimeSpan MaximumQuotaWait = TimeSpan.FromMinutes(5);

    private readonly CancellationTokenSource source;
    private readonly QuotaWaitShutdown shutdown;
    private readonly TimeSpan requestTimeout;
    private volatile bool quotaAcquired;

    public QuotaAwareTimeout(HttpRequestMessage request, HttpRequestOptionsKey<Action> quotaAcquiredKey, TimeSpan requestTimeout, CancellationToken cancellationToken)
    {
        source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.requestTimeout = requestTimeout;
        source.CancelAfter(requestTimeout + MaximumQuotaWait);
        shutdown = new QuotaWaitShutdown(source);
        request.Options.Set(quotaAcquiredKey, OnQuotaAcquired);
        // The circuit tells this timeout from the caller's own cancellation by the caller's token.
        CallerCancellation.Mark(request, cancellationToken);
    }

    public CancellationToken Token => source.Token;

    public bool QuotaAcquired => quotaAcquired;

    public bool StoppedBeforeStart => shutdown.StoppedBeforeStart;

    private void OnQuotaAcquired()
    {
        shutdown.QuotaAcquired();
        quotaAcquired = true;
        try { source.CancelAfter(requestTimeout); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        shutdown.Dispose();
        source.Dispose();
    }
}
