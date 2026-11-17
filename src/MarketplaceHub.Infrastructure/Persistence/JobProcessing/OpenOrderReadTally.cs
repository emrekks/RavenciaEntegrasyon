using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// What one open-order (lifecycle) scan heard back. A scan that asked and got not one answer, every failure being the
// marketplace's passing trouble or a refusal of the connection (network, 5xx, quota, sign-in), read nothing: it must
// not finish as a success and move its cursor's last success and watermark (17 Nov 2026: Pazarama's token service
// answered empty for every request and the order status scan still ended SUCCEEDED every two minutes). It fails with
// the first such error instead, so the job is retried or held as that error says. A scan with at least one answer
// (a read, or a marketplace "not found") succeeded in part: it stays a success and its failures are counted on the job.
// A failure that is not passing (a refused request, an unreadable answer) is the scan's own finding about one order and
// leaves the scan a success as before.
internal sealed class OpenOrderReadTally
{
    private int answers;
    private AdapterError? firstFailure;
    private bool onlyPassingFailures = true;

    public int Failures { get; private set; }

    public void Answered() => answers++;

    public void Failed(AdapterError? error)
    {
        Failures++;
        firstFailure ??= error;
        if (!IsPassing(error)) onlyPassingFailures = false;
    }

    // The error to fail the scan with, or null when the scan heard something or a failure was not a passing one.
    public AdapterError? NothingRead() => answers == 0 && Failures > 0 && onlyPassingFailures ? firstFailure : null;

    internal static bool IsPassing(AdapterError? error) => error?.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx
        or AdapterErrorClass.RateLimit or AdapterErrorClass.Authentication;
}
