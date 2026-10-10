const priorNoWriteFailureCode = 'HEPSIBURADA_CAPABILITY_NOT_ENABLED'
const deliveryAlreadyFailedCode = 'DELIVERY_ALREADY_FAILED'

type InvoiceDeliveryAttempt = { succeeded: boolean; errorCode: string | null }

export function hasEligibleOneTimeInvoiceSourceFailure(
  latestErrorCode: string | null,
  attempts: readonly InvoiceDeliveryAttempt[],
): boolean {
  if (latestErrorCode === 'HEPSIBURADA_INVOICE_DELIVERY_REPEATED_500' || latestErrorCode === priorNoWriteFailureCode) return true
  return latestErrorCode === deliveryAlreadyFailedCode
    && attempts.some(attempt => !attempt.succeeded && attempt.errorCode === priorNoWriteFailureCode)
}
