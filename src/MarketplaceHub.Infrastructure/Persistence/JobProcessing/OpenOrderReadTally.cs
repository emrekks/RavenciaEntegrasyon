using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// What one open-order (lifecycle) scan heard back, and what the run counts as.
// 17 Nov 2026: Pazarama's token service answered empty for every request and the order status scan still ended SUCCEEDED
// every two minutes, moving its watermark and last success. An answer is a read or a marketplace "not found".
// A  no answer, every failure of a connection-level kind (no token or session, a refused sign-in, the network, a quota):
//    the job fails with the first error (retried, held or reviewed as that error says); position and watermark stay.
// B  no answer, every failure a server error (5xx) from two requests up: a batch the marketplace answers with 5xx for good
//    cannot be told from a 5xx outage, so the run counts as unread (no last success, the failure streak grows and warns
//    after three scheduled runs) in one attempt, with no retry chain; the position moves to the next batch, so such a
//    batch never holds the orders behind it, and any answer on a later run makes that run a success again.
// C  at least one answer: a success, the orders it could not ask named on the job.
// D  anything else (one request with one 5xx, different codes, an order's own contract error): as C.
internal sealed class OpenOrderReadTally
{
    private int answers;
    private AdapterError? firstFailure;
    private bool allConnectionLevel = true;
    private bool allServerErrors = true;

    public int Failures { get; private set; }

    public AdapterError? FirstFailure => firstFailure;

    public void Answered() => answers++;

    public void Failed(AdapterError? error)
    {
        Failures++;
        firstFailure ??= error;
        if (error is null || !IsConnectionLevel(error)) allConnectionLevel = false;
        if (error?.Class != AdapterErrorClass.Remote5xx || IsConnectionLevel(error)) allServerErrors = false;
    }

    public OpenOrderScanVerdict Verdict()
    {
        if (answers > 0 || Failures == 0 || firstFailure is null) return OpenOrderScanVerdict.Proceed;
        if (allConnectionLevel) return OpenOrderScanVerdict.ConnectionFailed;
        return allServerErrors && Failures >= 2 ? OpenOrderScanVerdict.Unread : OpenOrderScanVerdict.Proceed;
    }

    // A failure that is the connection's whatever the order: no token or session (a *_TOKEN_* or *CREDENTIAL* code, a
    // contract one included, which makes the job a manual review instead of a silent success), a refused sign-in, no
    // network, a quota.
    internal static bool IsConnectionLevel(AdapterError error) =>
        error.Class is AdapterErrorClass.Authentication or AdapterErrorClass.TransientNetwork or AdapterErrorClass.RateLimit
        || error.Code.Contains("_TOKEN_", StringComparison.Ordinal) || error.Code.Contains("CREDENTIAL", StringComparison.Ordinal);

    // Rule B's job result: blocked, which ends the job in its one attempt (no retry chain, no dead job), is not a success
    // (the cursor's failure streak grows and its last success stays), puts no hold on the connection, and is closed by
    // the next successful run of the same read (ScheduledReadSupersession).
    internal JobExecutionResult UnreadResult() => JobExecutionResult.Blocked(
        firstFailure!.Code,
        $"Açık siparişlerin hiçbiri okunamadı ({Failures} istek sunucu hatası verdi: {firstFailure.SafeMessage}); tur okunamadı sayıldı, tarama sonraki partiden sürecek.",
        firstFailure.RemoteRequestId);
}

internal enum OpenOrderScanVerdict { Proceed, ConnectionFailed, Unread }
