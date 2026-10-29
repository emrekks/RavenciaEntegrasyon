import { useMemo, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { platformLogoClass, platformLogoSource } from '../../../shared/platform-logos'
import { RrIcon, RrModal } from '../../marketplace/returns-ui'
import type { MarketplaceConnection, Product } from '../shared'
import { bulkPublishPlatformName, summarizeBulkPublishStatus, type BulkPublishStatusSummary } from './bulk-publish-status'
import './bulk-publish-dialog.css'

type BulkPublicationFailure = { productId: string; productTitle: string; connectionId: string; code: string; message: string }
type BulkPublicationResult = { queued: number; skipped: number; failed: BulkPublicationFailure[] }

// The server takes the cards in slices so a long selection never runs into one request's time limit.
const CHUNK_SIZE = 50

// Product list "Platformlara Yayınla": the selected cards to the chosen marketplaces, laid out like the product
// editor's publish step (one card per connection with an on/off switch). Cards already on a marketplace (matched, live
// or still in progress) are skipped by the server; a missing marketplace price is filled from the general price, as the
// editor does. `products` (the selected records the list holds) only feeds each card's "Yayın Durumu" summary.
export function BulkPublishDialog({ productIds, cardCount, connections, products, onClose, onDone }: {
  productIds: string[]
  cardCount: number
  connections: MarketplaceConnection[]
  products?: Product[]
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
  // A selected id the list no longer holds counts as unknown in the summary instead of being dropped from the total.
  const selectedRecords = useMemo(() => {
    if (!products) return null
    const byId = new Map(products.map(product => [product.id, product]))
    return productIds.map(id => byId.get(id) ?? null)
  }, [productIds, products])
  const summaries = useMemo(() => new Map(connections.map(connection => [connection.id, selectedRecords ? summarizeBulkPublishStatus(selectedRecords, connection) : null])), [connections, selectedRecords])
  const locked = running || Boolean(result)

  function toggle(connectionId: string) {
    setConnectionIds(ids => ids.includes(connectionId) ? ids.filter(id => id !== connectionId) : [...ids, connectionId])
  }

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

  const selectedCount = connectionIds.length
  const foot = <div className="bulk-publish-footer">
    <div className="bulk-publish-footer-text">
      <strong>{selectedCount ? `${selectedCount} platform seçildi` : 'Yayın platformu seçilmedi'}</strong>
      <small>{running ? `${progress.toLocaleString('tr-TR')} / ${productIds.length.toLocaleString('tr-TR')} kayıt işlendi` : result ? 'Yayın işleri kuyruğa alındı; sonuçlar ürün listesinde güncellenir.' : selectedCount ? 'Seçilen her mağazada ayrı yayın kuyruğu açılır.' : 'Yayınlanacak mağazaları kartların üstünden seçin.'}</small>
    </div>
    {!selectedCount && !result && <span className="publish-selection-warning"><RrIcon name="alert" /> En az bir platform seçin</span>}
    <div className="bulk-publish-footer-actions">
      <button type="button" className="rr-btn" onClick={onClose} disabled={running}>{result ? 'Kapat' : 'Vazgeç'}</button>
      {!result && <button type="button" className="rr-btn rr-primary" onClick={() => void publish()} disabled={running || !selectedCount}><RrIcon name="export" />{running ? 'Kuyruğa Alınıyor…' : 'Yayınla'}</button>}
    </div>
  </div>

  return <RrModal crumb="Toplu İşlem" title="Platformlara Yayınla" desc={`Seçili ${cardCount.toLocaleString('tr-TR')} ürün kartı (${productIds.length.toLocaleString('tr-TR')} katalog kaydı) seçtiğiniz pazar yerlerine gönderilir. Zaten yayında ya da işlemde olanlar atlanır.`} size="xl" modalClass="bulk-publish-dialog" locked={running} onClose={onClose} foot={foot}>
    {connections.length ? <div className="publish-platform-grid" role="group" aria-label="Yayın platformları">
      {connections.map(connection => <BulkPublishPlatformCard key={connection.id} connection={connection} summary={summaries.get(connection.id) ?? null} selected={connectionIds.includes(connection.id)} locked={locked} onToggle={() => toggle(connection.id)} />)}
    </div> : <div className="rr-callout rr-warn"><RrIcon name="alert" /><span>Yayına uygun, aktif bir pazar yeri bağlantısı bulunamadı. Önce Platformlar sayfasından aktif bir bağlantı oluşturun.</span></div>}
    <div className="rr-callout"><RrIcon name="alert" /><span>Pazar yeri fiyatı olmayan varyantlara genel fiyat yazılır. Kategori ve özellik eşlemesi eksik olan kartlar o platformda gönderilmez; nedeni aşağıda listelenir.</span></div>
    {error && <div className="rr-callout rr-warn"><RrIcon name="alert" /><span>{error}</span></div>}
    {result && <section className="rr-dsec" aria-live="polite">
      <header><div className="rr-h3">Sonuç</div><p>{result.queued} kuyruğa alındı · {result.skipped} atlandı · {result.failed.length} gönderilemedi</p></header>
      {result.failed.length > 0 && <ul className="bulk-publish-failures">{result.failed.map(item => <li key={`${item.productId}:${item.connectionId}`}><b>{item.productTitle}</b> · {connectionName(item.connectionId)}: {item.message}</li>)}</ul>}
    </section>}
  </RrModal>
}

function BulkPublishPlatformCard({ connection, summary, selected, locked, onToggle }: {
  connection: MarketplaceConnection
  summary: BulkPublishStatusSummary | null
  selected: boolean
  locked: boolean
  onToggle: () => void
}) {
  const logo = platformLogoSource(connection.platformCode)
  const platformName = bulkPublishPlatformName(connection.platformCode)
  const storeName = connection.displayName || connection.externalStoreId || platformName
  const allLive = Boolean(summary?.allLive) && !selected
  const subtitle = allLive ? 'Seçili kayıtların tümü zaten yayında' : selected ? 'Yayın için seçildi' : 'Yayın için seçilmedi'
  const statusId = `bulk-publish-status-${connection.id}`
  return <article className={`publish-platform-card${selected ? ' selected' : ''}`}>
    <button type="button" className="publish-platform-card-head" onClick={onToggle} aria-pressed={selected} aria-describedby={statusId} disabled={locked || allLive} title={allLive ? 'Seçili kayıtlar bu platformda zaten yayında.' : undefined}>
      <span className={`publish-platform-mark ${connection.platformCode.trim().toLocaleLowerCase('tr-TR')}`}>{logo ? <img className={`publish-platform-logo ${platformLogoClass(connection.platformCode)}`} src={logo} alt="" aria-hidden="true" /> : <b aria-hidden="true">{storeName.trim().charAt(0).toLocaleUpperCase('tr-TR') || 'R'}</b>}</span>
      <span><strong>{storeName}</strong><small>{subtitle}</small></span>
      <i className={`publish-platform-toggle${selected ? ' on' : ''}`} aria-hidden="true"><b /></i>
    </button>
    <dl className="publish-platform-facts">
      <div><dt>Mağaza</dt><dd title={connection.externalStoreId ? `Mağaza no: ${connection.externalStoreId}` : undefined}>{connection.displayName || connection.externalStoreId || '—'}</dd></div>
      <div><dt>Platform</dt><dd>{platformName}</dd></div>
      <div><dt>Bağlantı</dt><dd><span className="publish-platform-status active"><i aria-hidden="true" />Aktif Bağlantı</span></dd></div>
      <div className="publish-platform-fact-publication"><dt>Yayın Durumu</dt><dd id={statusId}>{summary ? <span className={`publish-platform-status status-${summary.tone}`} title={summary.detail ?? undefined}><i aria-hidden="true" />{summary.label}</span> : '—'}</dd>{summary?.detail && <small>{summary.detail}</small>}</div>
    </dl>
  </article>
}
