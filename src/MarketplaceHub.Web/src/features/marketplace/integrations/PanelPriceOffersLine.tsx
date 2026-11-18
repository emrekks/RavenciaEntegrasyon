import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import type { ApiAnswer } from '../../../shared/api/api-answer'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { Button, useConfirmDialog } from '../../../shared/components'
import { failureNotice } from '../../../shared/notifications'
import type { IntegrationFeedback } from '../shared'
import { panelPriceOfferNotice, panelPriceOfferSummary, panelPriceOffersPath, panelPriceOffersQueryKey, type PanelPriceOfferPreview } from './panel-price-offers'
import './panel-price-offers.css'

// Under Otomatik Dış Yazmalar: the connection's linked sizes without an offer and, for the owner, "Eksik Teklifleri Aç"
// (offers at the panel price, counted as sent: nothing goes to the marketplace when they open). The server lets only
// the owner or an administrator open them (403 otherwise, shown as the failure).
export function PanelPriceOffersLine({ connectionId, onFeedback }: { connectionId: string; onFeedback: (feedback: IntegrationFeedback) => void }) {
  const client = useQueryClient()
  const [confirm, confirmDialog] = useConfirmDialog()
  const preview = useQuery({ queryKey: panelPriceOffersQueryKey(connectionId), queryFn: ({ signal }) => hubApi<PanelPriceOfferPreview>(panelPriceOffersPath(connectionId), { signal }), staleTime: 30_000 })
  const open = useMutation({
    mutationFn: () => hubApi<ApiAnswer<'postConnectionsByIdPanelPriceOffers'>>(panelPriceOffersPath(connectionId), { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }),
    onSuccess: async result => {
      onFeedback({ kind: 'success', title: 'Eksik Teklifler Açıldı', message: `${result.opened.toLocaleString('tr-TR')} beden için panel fiyatıyla teklif açıldı; pazaryerine bir şey gönderilmedi.${result.missingPanelPrice ? ` Panel fiyatı olmayan ${result.missingPanelPrice.toLocaleString('tr-TR')} beden açılmadı.` : ''}${result.missingVat ? ` KDV oranı olmayan ${result.missingVat.toLocaleString('tr-TR')} beden açılmadı.` : ''}` })
      await client.invalidateQueries({ queryKey: panelPriceOffersQueryKey(connectionId) })
    },
    onError: reason => onFeedback(failureNotice(reason, 'Eksik teklifler açılamadı.'))
  })
  const summary = preview.data ? panelPriceOfferSummary(preview.data) : null
  if (!summary) return null
  const openMissing = async () => {
    const accepted = await confirm({ title: 'Eksik Teklifler Açılsın mı?', message: panelPriceOfferNotice(preview.data).join(' '), confirmLabel: 'Teklifleri Aç' })
    if (accepted) open.mutate()
  }
  return <div className="panel-price-offers-line" data-panel-price-offers>
    <span>{summary.text}</span>
    {summary.canOpen ? <Button variant="secondary" className="panel-price-offers-open" disabled={open.isPending} onClick={() => void openMissing()}>Eksik Teklifleri Aç</Button> : null}
    {confirmDialog}
  </div>
}
