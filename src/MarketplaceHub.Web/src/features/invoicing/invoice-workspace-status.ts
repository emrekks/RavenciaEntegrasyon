import { adjustmentRequiredStatus, invoiceAwaitsProviderAnswer, providerAnswerAwaitedStatus } from './invoice-copy'

export type InvoiceWorkspaceStatusInput = {
  invoiceStatus: string
  canCreateInvoice: boolean
  platformInvoiceStatus?: string | null
  invoiceErrorCode?: string | null
}

export type InvoiceWorkspaceStatusTone = 'success' | 'warning' | 'danger' | 'info'

export function isInvoiceFailureStatus(value: string | null | undefined) {
  return ['FATURA_REDDEDILDI', 'FATURA_PLATFORMA_AKTARILMADI', 'REJECTED', 'VALIDATION_FAILED', 'MANUAL_REVIEW', 'MARKETPLACE_FAILED'].includes(value?.trim().toUpperCase() ?? '')
}

/**
 * The one invoice status the workspace list and the detail window both show. An invoice that needs correcting (its
 * order was cancelled or returned) or that failed comes first: the marketplace still reporting "invoiced" must not
 * paint such a row green.
 */
export function invoiceWorkspaceStatus(item: InvoiceWorkspaceStatusInput): { value: string; tone: InvoiceWorkspaceStatusTone } {
  if (item.invoiceStatus === adjustmentRequiredStatus) return { value: item.invoiceStatus, tone: 'warning' }
  // A failed invoice the operator can create again keeps reading "Fatura Bekliyor", as before.
  if (isInvoiceFailureStatus(item.invoiceStatus) && !item.canCreateInvoice) return { value: item.invoiceStatus, tone: 'danger' }
  if (['INVOICED', 'RECEIVED'].includes(item.platformInvoiceStatus?.trim().toUpperCase() ?? '')) return { value: 'FATURA_KONTROLDE', tone: 'success' }
  if (item.invoiceStatus === 'FATURA_INCELENECEK') return { value: 'FATURA_INCELENECEK', tone: 'warning' }
  if (item.canCreateInvoice || item.invoiceStatus === 'FATURA_BEKLIYOR') return { value: 'FATURA_BEKLIYOR', tone: 'warning' }
  if (isInvoiceFailureStatus(item.invoiceStatus)) return { value: item.invoiceStatus, tone: 'danger' }
  if (invoiceAwaitsProviderAnswer(item.invoiceStatus, item.invoiceErrorCode)) return { value: providerAnswerAwaitedStatus, tone: 'warning' }
  if (item.invoiceStatus === 'FATURA_BELIRSIZ') return { value: item.invoiceStatus, tone: 'warning' }
  if (item.invoiceStatus === 'FATURA_PLATFORMA_BEKLIYOR' || item.invoiceStatus === 'FATURA_DOGRULANAMADI') return { value: item.invoiceStatus, tone: 'warning' }
  if (['FATURA_YUKLENDI', 'FATURA_KESILDI', 'COMPLETED'].includes(item.invoiceStatus)) return { value: item.invoiceStatus, tone: 'success' }
  return { value: item.invoiceStatus || 'FATURA_BILINMIYOR', tone: 'info' }
}
