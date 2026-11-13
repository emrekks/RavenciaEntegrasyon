namespace MarketplaceHub.Application;

// One measure for every marketplace write (product create/update, price/stock, package actions): a write that failed
// with a network fault, an unexplained server fault (Remote5xx), an unreadable answer or a local bug may have been
// applied. Its outcome is unknown, so it is read back (or left for a look) before anything is sent again; a blind resend
// could apply it twice. A refusal the marketplace explained (validation, conflict, credentials, quota, a gateway block
// that never reached the API) is known not to have been applied.
public static class WriteOutcomePolicy
{
    public static bool IsUncertain(AdapterError error) =>
        error.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx or AdapterErrorClass.ContractViolation or AdapterErrorClass.InternalBug;
}

// A request refused by the panel's own circuit breaker (the marketplace answered several faults in a row, so the family
// is paused for a short while) never left the server. Every adapter answers it as RateLimit with the code
// {PREFIX}_LOCAL_CIRCUIT_OPEN: not an uncertain write, no ExternalEffect left behind as unclear, the job keeps its
// attempt and runs again once the circuit's break is over.
public static class LocalCircuitOpen
{
    public const string Suffix = "_LOCAL_CIRCUIT_OPEN";

    public static string Code(string codePrefix) => codePrefix + Suffix;

    public static bool IsCode(string? code) => code is not null && code.EndsWith(Suffix, StringComparison.Ordinal);
}
