import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { RrIcon, RrLogo, RrModal } from '../../marketplace/returns-ui'
import type { MarketplaceConnection } from '../shared'

type BulkPublicationFailure = { productId: string; productTitle: string; connectionId: string; code: string; message: string }
type BulkPublicationResult = { queued: number; skipped: number; failed: BulkPublicationFailure[] }

// The server takes the cards in slices so a long selection never runs into one request's time limit.
const CHUNK_SIZE = 50

// Product list "Platformlara Yayınla": the selected cards to the chosen marketplaces. Cards already on a marketplace
// (matched, live or still in progress) are skipped by the server; a missing marketplace price is filled from the
// general price, as the editor does.
export function BulkPublishDialog({ productIds, cardCount, connections, onClose, onDone }: {
  productIds: string[]
  cardCount: number
  connections: MarketplaceConnection[]
  onClose: () => void
  onDone: (message: string, kind: 'success' | 'error' | 'info') => void
}) {
  const client = useQueryClient()
  const [connectionIds, setConnectionIds] = useState<string[]>([])
  const [running, setRunning] = useState(false)
  const [progress, setProgress] = useState(0)
  const [result, setResult] = useState<BulkPublicationResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const connectionName = (id: string) => connections.find(connection => connection.id === id)?.displayName ?? 'Platform'

  async function publish() {
    setRunning(true); setError(null); setResult(null); setProgress(0)
    const total: BulkPublicationResult = { queued: 0, skipped: 0, failed: [] }
    try {
      for (let start = 0; start < productIds.length; start += CHUNK_SIZE) {
        const slice = productIds.slice(start, start + CHUNK_SIZE)
        const part = await hubApi<BulkPublicationResult>('/products/bulk-publication-jobs', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey('bulk-publication') }, body: JSON.stringify({ productIds: slice, connectionIds }) })
        total.queued += part.queued; total.skipped += part.skipped; total.failed.push(...part.failed)
        setProgress(Math.min(productIds.length, start + slice.length))
      }
      setResult(total)
      await client.invalidateQueries({ queryKey: ['products'] })
      await client.invalidateQueries({ queryKey: ['publication-status'] })
      onDone(`${total.queued} yayın işi kuyruğa alındı${total.skipped ? `, ${total.skipped} zaten yayında veya işlemde` : ''}${total.failed.length ? `, ${total.failed.length} gönderilemedi` : ''}.`, total.failed.length ? 'info' : 'success')
    } catch (reason) {
      setResult(total.queued || total.skipped || total.failed.length ? total : null)
      setError(reason instanceof Error ? reason.message : 'Yayın işleri oluşturulamadı.')
    } finally {
      setRunning(false)
    }
  }

  return <RrModal crumb="Toplu İşlem" title="Platformlara Yayınla" desc={`Seçili ${cardCount.toLocaleString('tr-TR')} ürün kartı seçtiğiniz pazar yerlerine gönderilir. Zaten yayında ya da işlemde olanlar atlanır.`} size="lg" locked={running} onClose={onClose} foot={<><span className="rr-note">{running ? `${progress.toLocaleString('tr-TR')} / ${productIds.length.toLocaleString('tr-TR')} kayıt işlendi` : connectionIds.length ? <><b>{connectionIds.length} platform</b> seçili</> : 'Platform seçilmedi'}</span><button type="button" className="rr-btn" onClick={onClose} disabled={running}>{result ? 'Kapat' : 'Vazgeç'}</button>{!result && <button type="button" className="rr-btn rr-primary" onClick={() => void publish()} disabled={running || !connectionIds.length}><RrIcon name="export" />{running ? 'Kuyruğa Alınıyor…' : 'Yayınla'}</button>}</>}>
    <div className="rr-h3">Pazar Yerleri</div>
    {connections.length ? <div className="rr-srcs rr-conns">{connections.map(connection => { const selected = connectionIds.includes(connection.id); return <label key={connection.id} className={`rr-src${selected ? ' rr-on' : ''}`}><input type="checkbox" className="rr-cb" checked={selected} disabled={running || Boolean(result)} onChange={() => setConnectionIds(ids => selected ? ids.filter(id => id !== connection.id) : [...ids, connection.id])} /><RrLogo code={connection.platformCode} /><span><b><span>{connection.displayName}</span></b><small>{connection.platformCode}</small></span></label> })}</div> : <div className="rr-callout rr-warn"><RrIcon name="alert" /><span>Yayına uygun, aktif bir pazar yeri bağlantısı bulunamadı.</span></div>}
    <div className="rr-callout"><RrIcon name="alert" /><span>Pazar yeri fiyatı olmayan varyantlara genel fiyat yazılır. Kategori ve özellik eşlemesi eksik olan kartlar o platformda gönderilmez; nedeni aşağıda listelenir.</span></div>
    {error && <div className="rr-callout rr-warn"><RrIcon name="alert" /><span>{error}</span></div>}
    {result && <section className="rr-dsec" aria-live="polite">
      <header><div className="rr-h3">Sonuç</div><p>{result.queued} kuyruğa alındı · {result.skipped} atlandı · {result.failed.length} gönderilemedi</p></header>
      {result.failed.length > 0 && <ul style={{ maxHeight: 260, overflow: 'auto', margin: 0, paddingLeft: 18 }}>{result.failed.map(item => <li key={`${item.productId}:${item.connectionId}`}><b>{item.productTitle}</b> · {connectionName(item.connectionId)}: {item.message}</li>)}</ul>}
    </section>}
  </RrModal>
}
