export type InvoiceCreationCandidate = {
  platformCode: string
  invoiceId: string | null
  invoiceStatus: string
  canCreateInvoice: boolean
  invoiceCreationEnabled: boolean
}

export type InvoiceActionFilter = 'ALL' | 'CREATABLE' | 'NOT_CREATABLE'

const retryableInvoiceStatuses = new Set(['FATURA_REDDEDILDI', 'REJECTED', 'VALIDATION_FAILED', 'MANUAL_REVIEW', 'MARKETPLACE_FAILED'])

/** Mirrors the invoice action rendered for a row, excluding only a transient in-flight request. */
export function isInvoiceCreationAvailable(item: InvoiceCreationCandidate, providerHasCredential: boolean) {
  if (!item.invoiceCreationEnabled) return false
  if (item.platformCode === 'SHOPIFY') return item.canCreateInvoice
  if (!providerHasCredential) return false
  if (item.invoiceId) return retryableInvoiceStatuses.has(item.invoiceStatus.trim().toUpperCase())
  return item.canCreateInvoice
}

export function matchesInvoiceActionFilter(item: InvoiceCreationCandidate, filter: InvoiceActionFilter, providerHasCredential: boolean) {
  const available = isInvoiceCreationAvailable(item, providerHasCredential)
  return filter === 'ALL' || (filter === 'CREATABLE' ? available : !available)
}
