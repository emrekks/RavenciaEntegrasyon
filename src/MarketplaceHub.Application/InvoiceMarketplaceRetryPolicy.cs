using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

public static class InvoiceMarketplaceRetryPolicy
{
    public const string RepeatedRemoteFailure = "HEPSIBURADA_INVOICE_DELIVERY_REPEATED_500";

    public static bool IsDeliveryReview(InvoiceStatus status, string? errorCode) =>
        status == InvoiceStatus.ManualReview
        && errorCode is RepeatedRemoteFailure or InvoiceDeliveryEnvironmentPolicy.MismatchErrorCode;

    public static bool CanRetryDelivery(InvoiceStatus status, string? errorCode) =>
        status is InvoiceStatus.Accepted or InvoiceStatus.MarketplaceFailed || IsDeliveryReview(status, errorCode);
}
