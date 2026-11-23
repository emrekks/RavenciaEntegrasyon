import { useMutation, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { queryKeys } from '../../../shared/api/query-keys'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { notificationError } from '../../../shared/notifications'
import { RrModal } from '../../../shared/ui/rr-components'
import './order-row-marks.css'
import '../returns/return-request-tag.css'

export const MANUAL_SHIP_TEXT = 'Bu işlem pazaryerine hiçbir şey göndermez; elle eklenen yeniden gönderimi panelde Kargoya Verildi (Elle) olarak gösterir. Sipariş Yeniden Gönderim sayısından ve Kargoya Verilmeyenler listesinden çıkar, Yeniden Gönderimler\'de kalır.'
export const MANUAL_UNSHIP_TEXT = 'Bu işlem pazaryerine hiçbir şey göndermez; elle konan Kargoya Verildi işaretini kaldırır ve yeniden gönderim yeniden kargoya verilmesi beklenen olarak sayılır.'

/**
 * Owner, 23 Nov 2026: "Kargoya Verildi Olarak İşaretle" / "İşareti Kaldır" for a re-shipment the owner added by hand
 * (POST /manual-resends/{id}/shipped, owners and administrators only). Panel only.
 */
export function ManualResendShipDialog({ record, marked, onClose, onDone }: { record: { id: string; version: number; orderNumber: string }; marked: boolean; onClose: () => void; onDone: (message: string, kind: 'success' | 'error') => void }) {
  const client = useQueryClient()
  const save = useMutation({
    mutationFn: () => hubApi(`/manual-resends/${record.id}/shipped`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey(), 'If-Match': `"v${record.version}"` }, body: JSON.stringify({ marked }) }),
    onSuccess: async () => {
      onDone(marked ? 'Yeniden gönderim Kargoya Verildi olarak işaretlendi.' : 'Kargoya Verildi işareti kaldırıldı.', 'success')
      await client.invalidateQueries({ queryKey: queryKeys.orders() })
      onClose()
    },
    onError: error => onDone(notificationError(error, 'Kargoya verildi işareti kaydedilemedi.').message, 'error')
  })
  const title = marked ? 'Kargoya Verildi Olarak İşaretle' : 'İşareti Kaldır'
  return <RrModal crumb="Elle Eklenen Yeniden Gönderim" title={title} desc={record.orderNumber} size="sm" onClose={onClose}
    foot={<><button type="button" className="rr-btn rr-quiet" onClick={onClose} disabled={save.isPending}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => save.mutate()} disabled={save.isPending}>{title}</button></>}>
    <p className="rr-exchange-mark-text">{marked ? MANUAL_SHIP_TEXT : MANUAL_UNSHIP_TEXT}</p>
  </RrModal>
}

/** "Elle Eklendi" beside an order the owner added to Yeniden Gönderimler by hand; no tag when it is no such order. */
export function ManualResendTag({ resend }: { resend: { shipped: boolean } | null | undefined }) {
  if (!resend) return null
  return <span className="rr-buyer-tag rr-return-exchange" title={resend.shipped ? 'Elle eklenen yeniden gönderim · kargoya verildi (elle işaretlendi)' : 'Elle eklenen yeniden gönderim · kargoya verilmesi bekleniyor'}>Elle Eklendi</span>
}
