import { hubApi } from '../../shared/api'
import { newIdempotencyKey } from '../../shared/idempotency'

// Shopify orders keep their invoice state by hand in the panel (no file is sent to Shopify). The order list, the
// invoice workspace and the bulk window all change it through this one request.
export type ManualInvoiceStatus = 'PENDING' | 'UPLOADED'

// The note shown beside an invoice label the seller set by hand (the server's manualInvoiceStatus), so a hand-set
// "Faturası Mevcut" never reads like an invoice the panel or the marketplace confirmed.
export const handSetInvoiceStatusNote = 'Elle İşaretlendi'

export function handSetInvoiceStatus(value: string | null | undefined): ManualInvoiceStatus | null {
  const normalized = value?.trim().toUpperCase()
  return normalized === 'UPLOADED' || normalized === 'PENDING' ? normalized : null
}

export function setManualInvoiceStatus(packageIds: string[], status: ManualInvoiceStatus) {
  return hubApi<{ updatedCount: number; reopenedCount?: number }>('/invoice-workspace/manual-status', {
    method: 'PUT',
    headers: { 'Idempotency-Key': newIdempotencyKey(`invoice-workspace-status:${status}`) },
    body: JSON.stringify({ packageIds, status }),
  })
}
