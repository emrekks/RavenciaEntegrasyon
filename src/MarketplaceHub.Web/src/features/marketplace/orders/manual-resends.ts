// The Yeniden Gönderim gear's records (owner, 23 Nov 2026: "Platform seçtirsin ve sipariş no yazdırsın"): a marketplace
// connection and an order number the panel already holds. The order is listed under Yeniden Gönderimler marked
// "Elle Eklendi" until "Kargoya Verildi Olarak İşaretle". Panel only: nothing goes to a marketplace.
import type { ApiAnswer } from '../../../shared/api/api-answer'
import type { ManualResendView } from '../../../shared/api/generated/api-types'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { queryKeys } from '../../../shared/api/query-keys'
import { newIdempotencyKey } from '../../../shared/idempotency'

export type ManualResend = ManualResendView
export const manualResendsQueryKey = queryKeys.orders('manual-resends')

// What the order number field holds -> the number to send: trimmed, a leading # (as the panel shows it) dropped.
export function normalizeManualResendNumber(text: string) {
  return text.trim().replace(/^#+/, '').trim()
}

// The record's state on its row and in the window.
export function manualResendStatus(record: { shippedAt?: string | null; shipped?: boolean }) {
  return record.shipped || record.shippedAt ? 'Kargoya Verildi (Elle)' : 'Kargoya Verilmesi Bekleniyor'
}

export function useManualResends(enabled = true) {
  const client = useQueryClient()
  const query = useQuery({
    queryKey: manualResendsQueryKey,
    queryFn: ({ signal }) => hubApi<ApiAnswer<'getManualResends'>>('/manual-resends', { signal }),
    staleTime: 30_000,
    enabled
  })
  // The list, the tab, its count and the card move together: every order read is refreshed after a change.
  const refresh = () => client.invalidateQueries({ queryKey: queryKeys.orders() })
  async function add(connectionId: string, orderNumber: string) {
    const created = await hubApi<ApiAnswer<'postManualResends'>>('/manual-resends', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ connectionId, orderNumber: normalizeManualResendNumber(orderNumber) }) })
    await refresh()
    return created
  }
  async function remove(id: string) {
    await hubApi(`/manual-resends/${id}`, { method: 'DELETE', headers: { 'Idempotency-Key': newIdempotencyKey() } })
    await refresh()
  }
  return { items: query.data?.items ?? [], loading: query.isPending && enabled, error: query.error, add, remove }
}
