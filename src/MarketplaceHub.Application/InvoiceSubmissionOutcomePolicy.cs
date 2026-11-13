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

    // Scan D-2 (13 Nov 2026): the provider answered a submission with a reference other than the UUID the panel sent, so
    // it does not keep our UUID on that connection. A later "not found by UUID" there proves nothing: the invoice may
    // exist under the provider's own reference. The connection gets a lasting warning (UuidMismatchIssueCode) and such a
    // not-found is recorded as UuidNotHonouredCode, which asks for a portal search by order number before any resend.
    public const string UuidMismatchIssueCode = "INVOICE_PROVIDER_UUID_MISMATCH";
    public const string UuidNotHonouredCode = "EFATURAM_UUID_NOT_HONOURED";

    public static string UuidMismatchIssueKey(Guid providerConnectionId) => $"invoice-provider-uuid-mismatch:{providerConnectionId:N}";

    // Whether the reference the provider answered is the UUID that was sent. No reference at all is no mismatch.
    public static bool Matches(string sentUuid, string? answeredReference)
    {
        if (string.IsNullOrWhiteSpace(answeredReference)) return true;
        return Guid.TryParse(sentUuid, out var sent) && Guid.TryParse(answeredReference, out var answered)
            ? sent == answered
            : string.Equals(sentUuid.Trim(), answeredReference.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // Scan D-2: on such a connection the "no invoice at the provider" confirmation opens a resend only when the operator
    // states they searched the provider portal by the order number (the documented API has no such search).
    public const string OrderNumberSearchRequiredCode = "INVOICE_ORDER_NUMBER_SEARCH_REQUIRED";

    public static string OrderNumberSearchRequiredMessage(string orderNumber) =>
        $"E-Faturam bu bağlantıda faturaları kendi kimliğiyle kaydediyor; panel faturanın kesilmediğini bilemez. E-Faturam portalında {orderNumber} sipariş numarasıyla arayın ve fatura yoksa bunu onaylayarak yeniden gönderimi açın.";

    public static string UuidMismatchSummary(string? invoiceNumber, string sentUuid, string answeredReference) =>
        $"E-Faturam {invoiceNumber ?? "bir"} faturasını panelin gönderdiği kimlikle ({sentUuid}) değil kendi kimliğiyle ({answeredReference}) kaydetti. "
        + "Bu bağlantıda yanıtı kaybolan bir faturanın kimlikle bulunamaması kesilmediğini göstermez: yeniden göndermeden önce E-Faturam portalında sipariş numarasıyla arayın.";
}
