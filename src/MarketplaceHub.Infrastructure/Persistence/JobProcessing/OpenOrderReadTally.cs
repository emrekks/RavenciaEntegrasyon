using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// What one open-order (lifecycle) scan heard back. 17 Nov 2026: Pazarama's token service answered empty for every
// request and the order status scan still ended SUCCEEDED every two minutes, moving its watermark and last success.
// An answer is a read or a marketplace "not found".
// A  no answer and every failure the connection's (no token or session, a refused sign-in, the network, a quota): the
//    job fails with the first error (retried, held or reviewed as that error says); position and watermark stay.
// Anything else, server errors only included however many: the scan goes on exactly as before and its label names the
// orders it could not ask. Known limit: while an order service answers 5xx only, the scan looks successful; only the
// label's count shows it.
internal sealed class OpenOrderReadTally
{
    private int answers;
    private AdapterError? firstFailure;
    private bool allConnectionLevel = true;

    public int Failures { get; private set; }

    // An order or package read that failed with a passing or connection error (not a "not found", not one order's own
    // contract error): the label says some orders were not read.
    public bool HadReadFailure { get; private set; }

    public AdapterError? FirstFailure => firstFailure;

    public void Answered() => answers++;

    public void Failed(AdapterError? error)
    {
        Failures++;
        firstFailure ??= error;
        if (error is null || !IsConnectionLevel(error)) allConnectionLevel = false;
        if (error is not null && (error.Class is AdapterErrorClass.Remote5xx or AdapterErrorClass.TransientNetwork or AdapterErrorClass.RateLimit || IsConnectionLevel(error))) HadReadFailure = true;
    }

    // The error that fails the scan (rule A), or null when the scan goes on.
    public AdapterError? ConnectionFailure() => answers == 0 && Failures > 0 && allConnectionLevel ? firstFailure : null;

    // A failure that is the connection's whatever the order: a refused sign-in (every *CREDENTIAL* code is one), no
    // network, a quota, or no token or session (a *_TOKEN_* code, a contract one included, which makes the job a manual
    // review instead of a silent success).
    internal static bool IsConnectionLevel(AdapterError error) =>
        error.Class is AdapterErrorClass.Authentication or AdapterErrorClass.TransientNetwork or AdapterErrorClass.RateLimit
        || error.Code.Contains("_TOKEN_", StringComparison.Ordinal);
}
