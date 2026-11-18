import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { hubApi } from '../../../shared/api'
import type { ApiAnswer } from '../../../shared/api/api-answer'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { useConfirmDialog } from '../../../shared/components'
import { queryKeys } from '../../../shared/api/query-keys'
import { panelPriceFillConfirmLines, panelPriceFillPath, panelPriceFillPreviewKey, panelPriceFillPreviewPath, panelPriceFillSummary, type PanelPriceFillPreview } from './panel-price-fill'
import './panel-price-fill.css'

// In Ürünleri İçe / Dışa Aktar: the one-off fill of empty panel prices from the first marketplace (owner, 18 Nov 2026).
// Shown only while some can be filled; the server lets only the owner or an administrator apply it.
export function PanelPriceFillPanel() {
  const client = useQueryClient()
  const [confirm, confirmDialog] = useConfirmDialog()
  const [message, setMessage] = useState<string | null>(null)
  const preview = useQuery({ queryKey: panelPriceFillPreviewKey, queryFn: ({ signal }) => hubApi<PanelPriceFillPreview>(panelPriceFillPreviewPath, { signal }), staleTime: 30_000 })
  const fill = useMutation({
    mutationFn: (expectedCount: number) => hubApi<ApiAnswer<'postChannelOffersPanelPriceFill'>>(panelPriceFillPath, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ expectedCount }) }),
    onSuccess: async result => {
      setMessage(`${result.filled.toLocaleString('tr-TR')} bedenin panel fiyatı dolduruldu; ${result.offersRepriced.toLocaleString('tr-TR')} teklif panel fiyatına çekildi. Pazaryerine bir şey gönderilmedi.`)
      await Promise.all([client.invalidateQueries({ queryKey: panelPriceFillPreviewKey }), client.invalidateQueries({ queryKey: queryKeys.products() })])
    },
    onError: reason => setMessage(reason instanceof Error ? reason.message : 'Panel fiyatları doldurulamadı.')
  })
  const summary = preview.data ? panelPriceFillSummary(preview.data) : null
  if (!summary && !message) return null
  const start = async () => {
    if (!preview.data) return
    const accepted = await confirm({ title: 'Panel Fiyatları Doldurulsun mu?', message: panelPriceFillConfirmLines(preview.data).join(' '), confirmLabel: 'Panel Fiyatlarını Doldur' })
    if (accepted) fill.mutate(preview.data.fillable)
  }
  return <div className="panel-price-fill" data-panel-price-fill>
    {summary ? <span>{summary}</span> : null}
    {summary ? <button type="button" className="rr-btn panel-price-fill-btn" disabled={fill.isPending} onClick={() => void start()}>Panel Fiyatlarını İlk Pazaryeri Fiyatından Doldur</button> : null}
    {message ? <span className="panel-price-fill-note" role="status">{message}</span> : null}
    {confirmDialog}
  </div>
}
