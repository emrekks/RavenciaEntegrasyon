// Ürünler › Toplu İşlemler › Toplu KDV Ata: KDV Oranı and KDV Dahil mi for every selected product card (all pages
// when "Tümünü Seç" is on). Only the cards change; marketplace listings keep their VAT.
import { useRef, useState } from 'react'
import { hubApi } from '../../../shared/api'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { RrIcon, RrModal } from '../../marketplace/returns-ui'
import { runInChunks, type ChunkRunResult } from '../bulk-status-run'
import { DEFAULT_VAT_RATE } from '../product-editor-state'
import { bulkVatRate, bulkVatRateOptions, bulkVatResultMessage, type BulkVatInclusion, type BulkVatResponse } from '../bulk-vat-run'

export function BulkVatDialog({ productIds, cardCount, onClose, onDone }: { productIds: string[]; cardCount: number; onClose: () => void; onDone: (outcome: { title: string; message: string; kind: 'success' | 'error' }, result: ChunkRunResult) => Promise<void> | void }) {
  const [rate, setRate] = useState(DEFAULT_VAT_RATE)
  const [inclusion, setInclusion] = useState<BulkVatInclusion>('INCLUDED')
  const [rateError, setRateError] = useState('')
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null)
  const running = useRef(false)

  async function apply() {
    const vatRate = bulkVatRate(rate)
    if (vatRate == null) { setRateError('KDV oranı seçin.'); return }
    if (running.current || !productIds.length) return
    running.current = true
    setProgress({ done: 0, total: productIds.length })
    const counts: BulkVatResponse = { updatedCount: 0, unchangedCount: 0 }
    try {
      const result = await runInChunks(productIds, 500, async chunk => {
        const response = await hubApi<BulkVatResponse>('/products/bulk-vat', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ productIds: chunk, vatRate, vatInclusion: inclusion }) })
        counts.updatedCount += response?.updatedCount ?? 0
        counts.unchangedCount += response?.unchangedCount ?? 0
      }, (done, total) => setProgress({ done, total }))
      await onDone(bulkVatResultMessage(result, counts), result)
    } finally {
      running.current = false
      setProgress(null)
    }
  }

  const busy = progress != null
  return <RrModal crumb="Toplu İşlem" title="Toplu KDV Ata" desc={`${cardCount.toLocaleString('tr-TR')} ürün kartının KDV oranı belirlenecek.`} size="sm" locked={busy} onClose={onClose} foot={<><span className="rr-note" role="status">{progress ? `${progress.done.toLocaleString('tr-TR')} / ${progress.total.toLocaleString('tr-TR')} kayıt gönderildi` : ''}</span><button type="button" className="rr-btn" onClick={onClose} disabled={busy}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => void apply()} disabled={busy}>{busy ? 'Uygulanıyor…' : 'Uygula'}</button></>}>
    <div className="rr-dgrid">
      <label className="rr-fld"><small>KDV Oranı</small><select className="rr-in" aria-label="KDV Oranı" required aria-required="true" aria-invalid={rateError ? true : undefined} value={rate} disabled={busy} onChange={event => { setRate(event.target.value); setRateError('') }}>{rate === '' && <option value="" disabled>Seçin</option>}{bulkVatRateOptions.map(option => <option key={option} value={option}>%{option}</option>)}</select>{rateError && <em className="rr-err" role="alert">{rateError}</em>}</label>
      <label className="rr-fld"><small>KDV Dahil mi</small><select className="rr-in" aria-label="KDV Dahil mi" value={inclusion} disabled={busy} onChange={event => setInclusion(event.target.value as BulkVatInclusion)}><option value="INCLUDED">Evet</option><option value="EXCLUDED">Hayır</option></select></label>
    </div>
    <div className="rr-callout"><RrIcon name="info" /><span>Yalnızca ürün kartlarının KDV bilgisi değişir. Pazar yerlerindeki mevcut ilanların KDV’si ve fiyatı değişmez; yeni yayınlanan ilanlar bu KDV ile açılır.</span></div>
  </RrModal>
}
