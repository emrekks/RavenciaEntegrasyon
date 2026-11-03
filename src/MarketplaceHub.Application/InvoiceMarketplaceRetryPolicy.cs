using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

public static class InvoiceMarketplaceRetryPolicy
{
    public const string RepeatedRemoteFailure = "HEPSIBURADA_INVOICE_DELIVERY_REPEATED_500";

    // An earlier delivery of the invoice to the package is on record under another identity (another invoice number or
    // package): the delivery stopped and the invoice waits in review until the owner sends it again ("Tekrar Dene").
    public const string DeliveryIdentityChanged = "DELIVERY_RETRY_PAYLOAD_CHANGED";

    public static bool IsDeliveryReview(InvoiceStatus status, string? errorCode) =>
        status == InvoiceStatus.ManualReview
        && errorCode is RepeatedRemoteFailure or InvoiceDeliveryEnvironmentPolicy.MismatchErrorCode or DeliveryIdentityChanged;

    public static bool CanRetryDelivery(InvoiceStatus status, string? errorCode) =>
        status is InvoiceStatus.Accepted or InvoiceStatus.MarketplaceFailed || IsDeliveryReview(status, errorCode);

    // The stops the owner's own retry lifts: the delivery job is told (manualDeliveryRetry) that this attempt was asked for.
    public static bool IsOwnerApprovedRetry(string? errorCode) =>
        errorCode is RepeatedRemoteFailure or DeliveryIdentityChanged;

    // The identity of one invoice's delivery to one package. A retry with the same identity is the same delivery, even when
    // its payload changed (a newer document link, a refreshed customer snapshot, the package's current lines), so the
    // external idempotency stays on it and a payload refresh no longer stops the retry.
    public static string DeliveryIdentity(Guid invoiceId, string? externalPackageId, string? invoiceNumber) =>
        $"delivery-identity:v1:{invoiceId:N}:{externalPackageId?.Trim()}:{invoiceNumber?.Trim()}";
}
