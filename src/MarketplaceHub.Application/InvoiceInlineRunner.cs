namespace MarketplaceHub.Application;

/// <summary>Result of running an invoice's follow-up jobs right away. Status is COMPLETED, IN_PROGRESS or FAILED.</summary>
public sealed record InlineInvoiceOutcome(string Status, string Message, string? ErrorCode = null);

public interface IInvoiceInlineRunner
{
    /// <summary>
    /// Runs the queued job and the jobs it unlocks for the invoice now, instead of waiting for the worker.
    /// No new step starts after <paramref name="deadline"/>; the token is the server's (shutdown and the
    /// request budget's hard stop), never the client's connection.
    /// </summary>
    Task<InlineInvoiceOutcome> RunAsync(Guid tenantId, Guid invoiceId, Guid firstJobId, DateTimeOffset deadline, CancellationToken cancellationToken);
}
