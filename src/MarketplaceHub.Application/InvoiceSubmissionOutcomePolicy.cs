namespace MarketplaceHub.Application;

/// <summary>Which fiscal-provider failures leave the outcome of an invoice submission unknown.</summary>
public static class InvoiceSubmissionOutcomePolicy
{
    // The provider adapter could not open a session, so the fiscal request was never sent.
    public const string SignInUnavailableCode = "EFATURAM_SIGNIN_UNAVAILABLE";

    public static bool IsBeforeFiscalRequest(string? errorCode) => errorCode is SignInUnavailableCode;

    // A network, 5xx or conflict answer to the fiscal request itself may hide a created invoice, so the
    // submission is not repeated blindly. A failure raised before the request is a plain retryable error.
    public static bool IsOutcomeUnknown(AdapterError error) =>
        error.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx or AdapterErrorClass.BusinessConflict
        && !IsBeforeFiscalRequest(error.Code);
}
