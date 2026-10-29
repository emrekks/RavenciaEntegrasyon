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
