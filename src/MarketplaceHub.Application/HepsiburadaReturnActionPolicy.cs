namespace MarketplaceHub.Application;

public static class HepsiburadaReturnActionPolicy
{
    private static readonly IReadOnlyList<string> StandardActions = ["APPROVE", "REJECT"];
    private static readonly IReadOnlyList<string> PreApprovalActions = ["PREAPPROVAL_CONFIRM", "APPROVE", "REJECT"];

    public static bool IsAwaitingPreApproval(string? rawStatus) =>
        string.Equals(Normalize(rawStatus), "AWAITINGPREAPPROVAL", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> AllowedActions(string? rawStatus, bool decisionPending) =>
        decisionPending
            ? Array.Empty<string>()
            : IsAwaitingPreApproval(rawStatus) ? PreApprovalActions : StandardActions;

    private static string Normalize(string? value) =>
        value?.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Trim() ?? "";
}
