import { useMutation, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { queryKeys } from '../../../shared/api/query-keys'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { notificationError } from '../../../shared/notifications'
import { RrModal } from '../../../shared/ui/rr-components'
import { exchangePlatform } from './exchange-resend-tag'

/** The dialog texts, naming the exchange's own marketplace (scan O3: a Hepsiburada exchange read "Trendyol'a"). */
export const exchangeShipText = (platformCode?: string | null) => `Bu işlem ${exchangePlatform(platformCode).to} hiçbir şey göndermez; değişim paketini panelde Kargoya Verildi (Elle) olarak gösterir. Paket Yeniden Gönderim sayısından ve Kargoya Verilmeyenler listesinden çıkar, Yeniden Gönderimler'de kalır.`
export const exchangeUnshipText = (platformCode?: string | null) => `Bu işlem ${exchangePlatform(platformCode).to} hiçbir şey göndermez; elle konan Kargoya Verildi işaretini kaldırır ve değişim paketi yeniden kargoya verilmesi beklenen olarak sayılır.`

/**
 * Owner, 23 Nov 2026: "Kargoya Verildi Olarak İşaretle" / "İşareti Kaldır" for an exchange resend's package (POST
 * /returns/{claimId}/replacement-shipped, owners and administrators only). Trendyol names no tracking link or status for
 * it (Hepsiburada names none on its claim), so this mark takes the row out of the waiting count. Panel only.
 */
export function ExchangeResendShipDialog({ claim, platformCode, marked, onClose, onDone }: { claim: { id: string; version: number; orderNumber: string }; platformCode?: string | null; marked: boolean; onClose: () => void; onDone: (message: string, kind: 'success' | 'error') => void }) {
  const client = useQueryClient()
  const save = useMutation({
    mutationFn: () => hubApi(`/returns/${claim.id}/replacement-shipped`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey(), 'If-Match': `"v${claim.version}"` }, body: JSON.stringify({ marked }) }),
    onSuccess: async () => {
      onDone(marked ? 'Değişim paketi Kargoya Verildi olarak işaretlendi.' : 'Kargoya Verildi işareti kaldırıldı.', 'success')
      await client.invalidateQueries({ queryKey: queryKeys.orders() })
      await client.invalidateQueries({ queryKey: queryKeys.returns() })
      onClose()
    },
    onError: error => onDone(notificationError(error, 'Kargoya verildi işareti kaydedilemedi.').message, 'error')
  })
  const title = marked ? 'Kargoya Verildi Olarak İşaretle' : 'İşareti Kaldır'
  return <RrModal crumb="Değişim Yeniden Gönderimi" title={title} desc={claim.orderNumber} size="sm" onClose={onClose}
    foot={<><button type="button" className="rr-btn rr-quiet" onClick={onClose} disabled={save.isPending}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => save.mutate()} disabled={save.isPending}>{title}</button></>}>
    <p className="rr-exchange-mark-text">{marked ? exchangeShipText(platformCode) : exchangeUnshipText(platformCode)}</p>
  </RrModal>
}
