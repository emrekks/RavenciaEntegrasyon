export type InvoiceCreationCandidate = {
  platformCode: string
  invoiceId: string | null
  invoiceStatus: string
  canCreateInvoice: boolean
  invoiceCreationEnabled: boolean
}

export type InvoiceActionFilter = 'ALL' | 'CREATABLE' | 'NOT_CREATABLE'
export type InvoiceSubmissionAction = 'VALIDATE' | 'SUBMIT' | 'BLOCK'
export type InvoiceProviderConnection = { id: string; platformCode: string; environment: string; status: string; hasCredential: boolean }

const retryableInvoiceStatuses = new Set(['FATURA_REDDEDILDI', 'FATURA_PLATFORMA_AKTARILMADI', 'REJECTED', 'VALIDATION_FAILED', 'MANUAL_REVIEW', 'MARKETPLACE_FAILED'])

function normalizedInvoiceEnvironment(value: string | null | undefined) {
  const environment = value?.trim().toUpperCase()
  return environment === 'STAGE' || environment === 'PRODUCTION' ? environment : null
}

/** Selects a credentialed provider only when it belongs to the marketplace's exact environment. */
export function invoiceProviderForEnvironment<T extends InvoiceProviderConnection>(connections: T[], marketplaceEnvironment: string | null | undefined): T | undefined {
  const environment = normalizedInvoiceEnvironment(marketplaceEnvironment)
  if (!environment) return undefined
  return connections.find(connection => connection.platformCode.trim().toUpperCase() === 'TRENDYOL_EFATURAM'
    && ['ACTIVE', 'VERIFIED'].includes(connection.status.trim().toUpperCase())
    && connection.hasCredential
    && normalizedInvoiceEnvironment(connection.environment) === environment)
}

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

/** Validation failures must be retried through validation before a submit job can be queued. */
export function invoiceSubmissionAction(status: string, allowedActions: string[]): InvoiceSubmissionAction {
  const normalizedStatus = status.trim().toUpperCase()
  const actions = new Set(allowedActions.map(action => action.trim().toUpperCase()))
  if (['DRAFT', 'VALIDATION_FAILED'].includes(normalizedStatus) && actions.has('VALIDATE')) return 'VALIDATE'
  if (actions.has('SUBMIT')) return 'SUBMIT'
  return 'BLOCK'
}

export function isValidatedInvoiceReadyToSubmit(status: string, allowedActions: string[]) {
  return status.trim().toUpperCase() === 'READY'
    && allowedActions.some(action => action.trim().toUpperCase() === 'SUBMIT')
}
