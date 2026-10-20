import { ApiRequestError, hubApi } from '../../shared/api'
import { newIdempotencyKey } from '../../shared/idempotency'

// Stock edits never send a difference computed against a number the screen loaded earlier: the new on-hand value
// goes out as an absolute value together with the value it replaces, and the server refuses it (409 STOCK_CHANGED)
// when an order, a return or another tab changed the stock in the meantime.
export type VariantStockSet = { variantId: string; sku: string; onHand: number; expectedOnHand: number }
export type VariantStockFailure = { variantId: string; sku: string; message: string; stale: boolean }

export function setVariantOnHand(update: VariantStockSet, reason: string) {
  const sourceEventId = newIdempotencyKey()
  return hubApi(`/inventory/${update.variantId}/on-hand`, {
    method: 'PUT',
    headers: { 'Idempotency-Key': newIdempotencyKey() },
    body: JSON.stringify({ onHand: update.onHand, expectedOnHand: update.expectedOnHand, reason, sourceEventId })
  })
}

// '' and '  ' are "no value", not zero; anything else must be a whole, non-negative number.
export function parseStockInput(value: string | number): { kind: 'empty' } | { kind: 'invalid' } | { kind: 'value'; value: number } {
  const text = String(value).trim()
  if (!text) return { kind: 'empty' }
  const parsed = Number(text)
  if (!Number.isFinite(parsed) || parsed < 0 || !Number.isInteger(parsed)) return { kind: 'invalid' }
  return { kind: 'value', value: parsed }
}

export function stockTarget(action: 'SET' | 'ADD' | 'SUBTRACT', amount: number, current: number) {
  return action === 'SET' ? amount : action === 'ADD' ? current + amount : current - amount
}

export function isStaleStockError(error: unknown) {
  return error instanceof ApiRequestError && (error.code === 'STOCK_CHANGED' || error.status === 409 || error.status === 412)
}

export function stockErrorMessage(error: unknown) {
  if (isStaleStockError(error)) return error instanceof Error && error.message ? error.message : 'Stok başka bir işlemle değişti; güncel değeri kontrol edip yeniden deneyin.'
  return error instanceof Error && error.message ? error.message : 'Stok kaydedilemedi.'
}

// Runs every update even when some fail, so one stale or invalid row does not leave the rest unsaved.
export async function applyVariantStockSets(updates: VariantStockSet[], send: (update: VariantStockSet) => Promise<unknown>) {
  const saved: VariantStockSet[] = []
  const failed: VariantStockFailure[] = []
  for (const update of updates) {
    if (update.onHand === update.expectedOnHand) continue
    try {
      await send(update)
      saved.push(update)
    } catch (error) {
      failed.push({ variantId: update.variantId, sku: update.sku, message: stockErrorMessage(error), stale: isStaleStockError(error) })
    }
  }
  return { saved, failed }
}

export function bulkEditSummary(total: number, failures: Array<{ sku: string; message: string }>) {
  const succeeded = total - failures.length
  if (!failures.length) return `${total} varyant başarıyla güncellendi.`
  const details = failures.slice(0, 3).map(failure => `${failure.sku}: ${failure.message}`).join(' · ')
  const more = failures.length > 3 ? ` (+${failures.length - 3} varyant daha)` : ''
  return `${succeeded} varyant güncellendi, ${failures.length} varyant güncellenemedi. ${details}${more}`
}
