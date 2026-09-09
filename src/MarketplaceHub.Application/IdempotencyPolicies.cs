namespace MarketplaceHub.Application;

public enum ApiIdempotencyDecisionKind
{
    Replay,
    InProgress,
    Unknown,
    KeyReused
}

public static class ApiIdempotencyPolicy
{
    public static ApiIdempotencyDecisionKind Resolve(string state, string storedRequestHash, string requestHash)
    {
        if (!string.Equals(storedRequestHash, requestHash, StringComparison.Ordinal))
            return ApiIdempotencyDecisionKind.KeyReused;

        return state switch
        {
            "COMPLETED" => ApiIdempotencyDecisionKind.Replay,
            "IN_PROGRESS" => ApiIdempotencyDecisionKind.InProgress,
            _ => ApiIdempotencyDecisionKind.Unknown
        };
    }
}
