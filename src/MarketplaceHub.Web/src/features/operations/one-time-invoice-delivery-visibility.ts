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

type OneTimeDeliveryCandidate = {
  elevated: boolean
  jobType: string
  jobStatus: string
  lastErrorCode: string | null
  attempts: readonly InvoiceDeliveryAttempt[]
  invoice: { status: string; invoiceNumber: string | null; externalPackageId: string | null } | null
}

// Whether the job drawer offers the one-time invoice delivery. Which orders may use it is the server's decision (it
// refuses any other order with an error the drawer shows); the panel no longer carries its own order numbers (W32).
export function canOfferOneTimeInvoiceDelivery(candidate: OneTimeDeliveryCandidate): boolean {
  const { invoice } = candidate
  return candidate.elevated
    && candidate.jobType === 'INVOICE_MARKETPLACE_DELIVERY'
    && hasEligibleOneTimeInvoiceSourceFailure(candidate.lastErrorCode, candidate.attempts)
    && ['BLOCKED', 'MANUAL_REVIEW', 'DEAD'].includes(candidate.jobStatus)
    && invoice != null
    && ['ACCEPTED', 'MARKETPLACEFAILED', 'MANUALREVIEW'].includes(invoice.status.toUpperCase())
    && Boolean(invoice.invoiceNumber && invoice.externalPackageId)
}
