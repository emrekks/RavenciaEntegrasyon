namespace MarketplaceHub.Infrastructure.Adapters;

// An adapter sends with its own timeout token (a linked source, QuotaAwareTimeout or HttpClient.Timeout), so
// inside the handler pipeline a request timeout and the caller's own cancellation both arrive as a cancelled
// token. The adapter names the caller's token on the request; a circuit then counts a cancellation as a
// failure only when the caller did not cancel (a timeout: the platform hangs), never when the job was
// cancelled or the host is shutting down.
internal static class CallerCancellation
{
    internal static readonly HttpRequestOptionsKey<CancellationToken> Key = new("MarketplaceHub.CallerCancellation");

    public static void Mark(HttpRequestMessage request, CancellationToken callerToken) => request.Options.Set(Key, callerToken);

    // Without the mark only a cancellation that did not come through the handler's own token counts.
    public static bool IsTimeout(HttpRequestMessage request, CancellationToken handlerToken) =>
        request.Options.TryGetValue(Key, out var callerToken)
            ? !callerToken.IsCancellationRequested
            : !handlerToken.IsCancellationRequested;
}
