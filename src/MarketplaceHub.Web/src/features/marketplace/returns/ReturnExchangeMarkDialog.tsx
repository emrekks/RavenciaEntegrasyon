import { useMutation, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { queryKeys } from '../../../shared/api/query-keys'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { notificationError } from '../../../shared/notifications'
import { RrModal } from '../../../shared/ui/rr-components'

export const EXCHANGE_MARK_TEXT = 'Bu işlem Trendyol\'a hiçbir şey göndermez; kaydı panelde Değişim Onaylandı olarak gösterir ve Onaylananlar\'a taşır. Reddedilmiş iade zaten fatura işlemi gerektirmez.'
export const EXCHANGE_UNMARK_TEXT = 'Bu işlem Trendyol\'a hiçbir şey göndermez; elle konan Değişim Onaylandı işaretini kaldırır ve kaydı Trendyol\'un bildirdiği gibi gösterir.'

/**
 * Owner, 18 Nov 2026: "Değişim Onaylandı Olarak İşaretle" / "İşareti Kaldır" for a Trendyol exchange request Trendyol
 * answered Rejected (POST /returns/{id}/exchange-approved, owners and administrators only). Panel only.
 */
export function ReturnExchangeMarkDialog({ item, marked, onClose, onDone }: { item: { id: string; version: number; orderNumber: string }; marked: boolean; onClose: () => void; onDone: (message: string, kind: 'success' | 'error') => void }) {
  const client = useQueryClient()
  const save = useMutation({
    mutationFn: () => hubApi(`/returns/${item.id}/exchange-approved`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ marked }) }),
    onSuccess: async () => {
      onDone(marked ? 'İade Değişim Onaylandı olarak işaretlendi.' : 'Değişim Onaylandı işareti kaldırıldı.', 'success')
      await client.invalidateQueries({ queryKey: queryKeys.return(item.id) })
      await client.invalidateQueries({ queryKey: queryKeys.returns() })
      // The orders list carries the claim's version for its "Kargoya Verildi" mark (exchangeResend.claimVersion): a stale one
      // answered 412 there after this mark (scan O6, 23 Nov 2026).
      await client.invalidateQueries({ queryKey: queryKeys.orders() })
      onClose()
    },
    onError: error => onDone(notificationError(error, 'Değişim işareti kaydedilemedi.').message, 'error')
  })
  const title = marked ? 'Değişim Onaylandı Olarak İşaretle' : 'İşareti Kaldır'
  return <RrModal crumb="Trendyol Değişim Talebi" title={title} desc={item.orderNumber} size="sm" onClose={onClose}
    foot={<><button type="button" className="rr-btn rr-quiet" onClick={onClose} disabled={save.isPending}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => save.mutate()} disabled={save.isPending}>{title}</button></>}>
    <p className="rr-exchange-mark-text">{marked ? EXCHANGE_MARK_TEXT : EXCHANGE_UNMARK_TEXT}</p>
  </RrModal>
}
