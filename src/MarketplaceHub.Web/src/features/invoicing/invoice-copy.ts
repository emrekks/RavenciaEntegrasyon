import { invoiceStatusLabel } from '../../shared/status-labels'
import { versionedIdempotencyKey } from '../../shared/idempotency'
import { invoiceFailureReason } from './invoice-failure-reasons'

// Turkish text for the codes the invoice pages receive from the server; a code without a text is shown readable, never raw.

/** The invoice detail's actions (InvoiceDetailView.AllowedActions). Delivery goes to the order's own marketplace. */
export function invoiceDetailActionLabel(action: string) {
  return ({
    SUBMIT: 'E-Faturam’a Gönder',
    STAGE_CAPABILITY_PROBE: 'Test Ortamında Mali Deneme Yap',
    RECONCILE: 'Durumu Uzlaştır',
    DELIVER: 'Pazaryerine Fatura Linkini İlet',
    CANCEL: 'E-Arşiv İptal İsteği Gönder',
    VALIDATE: 'Yerel Doğrula',
  } as Record<string, string>)[action] ?? readable(action)
}

export function invoiceEnvironmentLabel(environment: string | null | undefined) {
  const code = environment?.trim().toUpperCase()
  return code === 'PRODUCTION' || code === 'LIVE' ? 'Canlı' : code === 'STAGE' || code === 'TEST' ? 'Test' : 'Ortam bilinmiyor'
}

export function invoiceTypeLabel(invoiceType: string | null | undefined) {
  return ({ EARSIVFATURA: 'e-Arşiv Fatura', TEMELFATURA: 'e-Fatura (Temel)', TICARIFATURA: 'e-Fatura (Ticari)' } as Record<string, string>)[invoiceType?.trim().toUpperCase() ?? ''] ?? readable(invoiceType ?? '')
}

/** The outcome of one order after "Onayla ve Faturayı Kes" (InvoiceWorkspaceConfirmItemResult.Status). */
export function invoiceConfirmResultLabel(status: string) {
  return ({
    QUEUED: 'Sırada',
    IN_PROGRESS: 'İşleniyor',
    COMPLETED: 'Tamamlandı',
    FAILED: 'Başarısız',
    BLOCKED: 'Durduruldu',
    CONFLICT: 'Eşzamanlı Değişiklik',
    PREVIEW_STALE: 'Önizleme Güncel Değil',
  } as Record<string, string>)[status.trim().toUpperCase()] ?? readable(status)
}

/** An invoice error code as text: the known codes by their reason, the rest as a labelled code. */
export function invoiceErrorText(code: string | null | undefined) {
  if (!code?.trim()) return 'Yok'
  const reason = invoiceFailureReason(code)
  return reason.startsWith('İletilemedi: ') ? `Hata kodu: ${code}` : reason
}

export const adjustmentRequiredStatus = 'FATURA_DUZELTME_GEREKLI'

/** The invoice status filter of the workspace; an issued invoice whose order was cancelled or returned reads "Düzeltme Gerekiyor". */
export function workspaceInvoiceStatusFilterLabel(status: string) {
  return status === adjustmentRequiredStatus ? 'Düzeltme Gerekiyor' : invoiceStatusLabel(status)
}

/** One key per manual document and delivery state: a double click reaches the same request instead of queuing twice. */
export function manualDeliveryIdempotencyKey(item: { manualInvoiceDocumentId: string | null; manualInvoiceDeliveryStatus: string | null }) {
  return versionedIdempotencyKey('manual-invoice-delivery', item.manualInvoiceDocumentId ?? 'none', item.manualInvoiceDeliveryStatus ?? 'NEW')
}

function readable(code: string) {
  const text = code.trim().replace(/[_-]+/g, ' ').toLowerCase()
  return text ? text.charAt(0).toUpperCase() + text.slice(1) : '—'
}
