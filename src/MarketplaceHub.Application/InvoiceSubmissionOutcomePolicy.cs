namespace MarketplaceHub.Application;

/// <summary>Which fiscal-provider failures leave the outcome of an invoice submission unknown.</summary>
public static class InvoiceSubmissionOutcomePolicy
{
    // The provider adapter could not open a session, so the fiscal request was never sent.
    public const string SignInUnavailableCode = "EFATURAM_SIGNIN_UNAVAILABLE";

    // The provider answered the fiscal request with 2xx but the answer could not be read: it took the request, so the
    // invoice may well exist. Its result is read from the provider (by the UUID the answer carried, when it did);
    // until then the invoice is neither resent nor confirmed absent.
    public const string UnreadableAnswerCode = "EFATURAM_SUBMIT_ANSWER_UNREADABLE";

    public static bool IsBeforeFiscalRequest(string? errorCode) => errorCode is SignInUnavailableCode;

    // A network, 5xx or conflict answer to the fiscal request itself may hide a created invoice, so the
    // submission is not repeated blindly. A failure raised before the request is a plain retryable error.
    public static bool IsOutcomeUnknown(AdapterError error) =>
        error.Code == UnreadableAnswerCode
        || (error.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx or AdapterErrorClass.BusinessConflict
            && !IsBeforeFiscalRequest(error.Code));
}

/// <summary>
/// The invoice UUID (ETTN) the panel chooses before it sends an invoice (E-Faturam createEArchive / createOutgoingEInvoice
/// take it in invoiceInfo.invoiceUuid). With it a lost answer is settled by reading that UUID instead of sending again.
/// </summary>
public static class InvoiceProviderIdentityPolicy
{
    // E-Faturam's documented "no such invoice" answer to a status read by UUID (earchive-not-found / outgoing-einvoice-not-found).
    public const string NotFoundAtProviderCode = "EFATURAM_INVOICE_NOT_FOUND_BY_UUID";

    // A read right after the request may not see an invoice that is still being created: "not found" counts only this
    // long after the send.
    public static readonly TimeSpan NotFoundSettleDelay = TimeSpan.FromMinutes(5);

    // Derived from the invoice's own id, so it exists before anything is sent and never changes: every attempt of one
    // invoice carries the same UUID (the provider refuses a second invoice under one ETTN), a new invoice row a new one.
    // Version 4 layout, for a provider that validates the UUID version.
    public static Guid UuidFor(Guid invoiceId)
    {
        Span<byte> bytes = stackalloc byte[16];
        invoiceId.TryWriteBytes(bytes, bigEndian: true, out _);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    // The UUID an uncertain attempt sent (kept on the attempt before the request). An attempt from before this carries
    // none: nothing can be read by it and the operator checks the provider portal.
    public static string? UuidToRead(string? attemptReference) =>
        Guid.TryParse(attemptReference, out var uuid) ? uuid.ToString("D") : null;

    public static bool NotFoundIsSettled(DateTimeOffset attemptStartedAt, DateTimeOffset now) => now - attemptStartedAt >= NotFoundSettleDelay;
}
