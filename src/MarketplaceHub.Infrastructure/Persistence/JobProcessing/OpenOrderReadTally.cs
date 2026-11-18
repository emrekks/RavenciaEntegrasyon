using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// What one open-order (lifecycle) scan heard back, and whether the scan as a whole must fail.
// 17 Nov 2026: Pazarama's token service answered empty for every request and the order status scan still ended SUCCEEDED
// every two minutes, moving its watermark and last success. A scan fails only when its failure is the connection's, not
// an order's: it asked, got no answer at all (a read or a marketplace "not found"), and every failure carries the same
// code of a connection-wide kind:
// - always: a token or session that could not be had (a *_TOKEN_* or *CREDENTIAL* code, a contract one included, which
//   makes the job a manual review instead of a silent success), a refused sign-in, the network, a quota;
// - a server error (5xx) only from two requests up, and only until the same batch has failed that way PoisonAfter runs in
//   a row (OpenScanStrikes): orders a marketplace answers with 5xx for good must not hold the scan's position for ever.
// Anything else (mixed results, different codes, one order's own contract error) leaves the scan a success; the failed
// orders are counted on the job and asked again on the next pass.
internal sealed class OpenOrderReadTally
{
    private int answers;
    private AdapterError? firstFailure;
    private bool sameCode = true;

    public int Failures { get; private set; }

    public void Answered() => answers++;

    public void Failed(AdapterError? error)
    {
        Failures++;
        if (Failures == 1) firstFailure = error;
        else if (!string.Equals(error?.Code, firstFailure?.Code, StringComparison.Ordinal)) sameCode = false;
    }

    public AdapterError? FirstFailure => firstFailure;

    public OpenOrderScanVerdict Verdict()
    {
        if (answers > 0 || Failures == 0 || firstFailure is null || !sameCode) return OpenOrderScanVerdict.Proceed;
        if (IsConnectionLevel(firstFailure)) return OpenOrderScanVerdict.Fail;
        return firstFailure.Class == AdapterErrorClass.Remote5xx && Failures >= 2 ? OpenOrderScanVerdict.FailUntilPoisoned : OpenOrderScanVerdict.Proceed;
    }

    // A failure that is the connection's whatever the order: no token or session, a refused sign-in, no network, a quota.
    internal static bool IsConnectionLevel(AdapterError error) =>
        error.Class is AdapterErrorClass.Authentication or AdapterErrorClass.TransientNetwork or AdapterErrorClass.RateLimit
        || error.Code.Contains("_TOKEN_", StringComparison.Ordinal) || error.Code.Contains("CREDENTIAL", StringComparison.Ordinal);
}

internal enum OpenOrderScanVerdict { Proceed, Fail, FailUntilPoisoned }

// How many runs in a row the same open-order batch failed with server errors only, kept in front of the scan's own
// position in the ORDER_LIFECYCLE cursor ("strikes:{n}|{position}", no migration). Any other text has no strikes.
internal static class OpenScanStrikes
{
    private const string Prefix = "strikes:";
    public const int PoisonAfter = 3;

    public static (int Strikes, string? Position) Split(string? opaque)
    {
        if (opaque is null || !opaque.StartsWith(Prefix, StringComparison.Ordinal)) return (0, opaque);
        var bar = opaque.IndexOf('|', Prefix.Length);
        if (bar < 0 || !int.TryParse(opaque.AsSpan(Prefix.Length, bar - Prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var strikes)) return (0, opaque);
        var position = opaque[(bar + 1)..];
        return (strikes, position.Length == 0 ? null : position);
    }

    public static string Join(int strikes, string? position) => $"{Prefix}{strikes}|{position}";

    public static string? Position(string? opaque) => Split(opaque).Position;
}
