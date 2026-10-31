import { invoiceStatusLabel } from '../../shared/status-labels'
import { newIdempotencyKey } from '../../shared/idempotency'
import { invoiceFailureReason } from './invoice-failure-reasons'

// Turkish text for the codes the invoice pages receive from the server; a code without a text is shown readable, never raw.

// E-Faturam took the creation request (2xx) but its answer could not be read: the invoice may exist, so its result is
// awaited from E-Faturam and nothing is resent or confirmed absent meanwhile (InvoiceSubmissionOutcomePolicy).
export const providerAnswerAwaitedCode = 'EFATURAM_SUBMIT_ANSWER_UNREADABLE'
export const providerAnswerAwaitedStatus = 'FATURA_CEVAP_BEKLENIYOR'

export function invoiceAwaitsProviderAnswer(status: string | null | undefined, errorCode: string | null | undefined) {
  return ['FATURA_BELIRSIZ', 'UNKNOWN_RESULT'].includes(status?.trim().toUpperCase() ?? '') && errorCode === providerAnswerAwaitedCode
}

// n11, Pazarama and PTT AVM cannot report which invoice an order holds (MarketplaceInvoiceReopenPolicy): their
// "Fatura Durumu Güncelle" sets the status by hand, and "Fatura Bekliyor" reopens a package whose invoice was cancelled.
export const handSetInvoiceStatusPlatforms: ReadonlySet<string> = new Set(['N11', 'PAZARAMA', 'PTTAVM'])

/** What "Fatura Bekliyor" does to an order whose invoice was cancelled, per marketplace; null when none of them is selected. */
export function invoiceReopenNote(platformCodes: readonly string[]) {
  const platforms = new Set(platformCodes.map(code => code.trim().toUpperCase()).filter(code => handSetInvoiceStatusPlatforms.has(code)))
  if (platforms.size === 0) return null
  const notes: string[] = ['İptal edilen bir faturası olan siparişte “Fatura Bekliyor”, o faturanın linkini geçersiz sayar ve yeni fatura kesmeye izin verir.']
  if (platforms.has('N11') || platforms.has('PAZARAMA')) notes.push('n11 ve Pazarama’da yeni faturanın linki gönderildiğinde eskisinin yerine geçer.')
  if (platforms.has('PTTAVM')) notes.push('PTT AVM ikinci fatura linkini kabul edip etmediğini bildirmediği için yeni faturanın linkini PTT AVM satıcı panelinde eskisinin yerine siz yükleyin, sonra “Pazaryerinde Gördüm, Tamamla”yı seçin.')
  return notes.join(' ')
}

/** The invoice detail's actions (InvoiceDetailView.AllowedActions). Delivery goes to the order's own marketplace. */
export function invoiceDetailActionLabel(action: string, status?: string) {
  // A refused cancellation, or a pending one whose job ended, is requested again from the same button.
  if (action === 'CANCEL' && (status === 'CANCELLATION_REJECTED' || status === 'CANCELLATION_PENDING')) return 'E-Arşiv İptalini Yeniden İste'
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

/** The E-Faturam taxpayer lookup behind the invoice type (InvoiceWorkspacePreviewItem.TaxpayerStatus); null when not looked up. */
export function taxpayerStatusLabel(status: string | null | undefined) {
  return ({ REGISTERED: 'e-Fatura Mükellefi', NOT_REGISTERED: 'e-Fatura Mükellefi Değil', UNKNOWN: 'Sorgulanamadı' } as Record<string, string>)[status ?? ''] ?? null
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

/**
 * A fresh key for every delivery attempt. A key built from the document and its delivery state came again unchanged
 * after a second failure (FAILED -> FAILED), so the server replayed the old 202 and queued nothing. A double click is
 * stopped by the button (pending / queued), and a resend after a lost answer finds the still active job on the server.
 */
export function manualDeliveryIdempotencyKey(documentId: string) {
  return newIdempotencyKey(`manual-invoice-delivery:${documentId}`)
}

function readable(code: string) {
  const text = code.trim().replace(/[_-]+/g, ' ').toLowerCase()
  return text ? text.charAt(0).toUpperCase() + text.slice(1) : '—'
}
