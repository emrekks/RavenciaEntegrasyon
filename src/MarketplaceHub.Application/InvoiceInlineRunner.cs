namespace MarketplaceHub.Application;

/// <summary>Result of running an invoice's follow-up jobs right away. Status is COMPLETED, IN_PROGRESS or FAILED.</summary>
public sealed record InlineInvoiceOutcome(string Status, string Message, string? ErrorCode = null);

public interface IInvoiceInlineRunner
{
    /// <summary>Runs the queued job and the jobs it unlocks for the invoice now, within a short time budget, instead of waiting for the worker.</summary>
    Task<InlineInvoiceOutcome> RunAsync(Guid tenantId, Guid invoiceId, Guid firstJobId, CancellationToken cancellationToken);
}
