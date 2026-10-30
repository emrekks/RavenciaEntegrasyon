import { useMemo, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { DialogSurface, UiIcon } from '../../../shared/components'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { platformLogoClass, platformLogoSource } from '../../../shared/platform-logos'
import type { MarketplaceConnection, Product } from '../shared'
import { bulkPublishModelTooltip, bulkPublishPlatformName, summarizeBulkPublishModels, summarizeBulkPublishStatus, type BulkPublishModelTooltip, type BulkPublishStatusSummary } from './bulk-publish-status'
import './bulk-publish-model-tip.css'
import '../product-editor/product-editor-design-system.css'

type BulkPublicationFailure = { productId: string; productTitle: string; connectionId: string; code: string; message: string }
type BulkPublicationResult = { queued: number; skipped: number; failed: BulkPublicationFailure[] }

// The server takes the cards in slices so a long selection never runs into one request's time limit.
const CHUNK_SIZE = 50

// The card's frame and place, as on a product's own publish popup: not yet published first, then refused, in
// progress, and live (all selected records on it) last.
export function bulkPublishCardState(summary: BulkPublishStatusSummary | null): 'unpublished' | 'error' | 'working' | 'live' {
  if (!summary) return 'unpublished'
  if (summary.allLive || summary.tone === 'success') return 'live'
  if (summary.tone === 'danger') return 'error'
  if (summary.tone === 'info' || summary.tone === 'warning') return 'working'
  return 'unpublished'
}

// Product list "Platformlara Yayınla" for several cards: the same popup as a single product's "Platformlarda Yayınla"
// (the editor's publish step), one card per connection with an on/off switch. Cards already on a marketplace (matched,
// live or still in progress) are skipped by the server; a missing marketplace price is filled from the general price, as
// the editor does. `products` (the selected records the list holds) only feeds each card's "Yayın Durumu" summary.
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
  // A selected id the list no longer holds counts as unknown in the summary instead of being dropped from the total.
  const selectedRecords = useMemo(() => {
    if (!products) return null
    const byId = new Map(products.map(product => [product.id, product]))
    return productIds.map(id => byId.get(id) ?? null)
  }, [productIds, products])
  const summaries = useMemo(() => new Map(connections.map(connection => [connection.id, selectedRecords ? summarizeBulkPublishStatus(selectedRecords, connection) : null])), [connections, selectedRecords])
  const modelTips = useMemo(() => new Map(connections.map(connection => [connection.id, selectedRecords?.length ? bulkPublishModelTooltip(summarizeBulkPublishModels(selectedRecords, connection)) : null])), [connections, selectedRecords])
  const locked = running || Boolean(result)
  const close = () => { if (!running) onClose() }

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
  // "Tümünü Seç" picks every platform a card can be selected on: one where the selected records are not all live yet.
  const selectableIds = connections.filter(connection => !summaries.get(connection.id)?.allLive).map(connection => connection.id)
  const allSelectableSelected = selectableIds.length > 0 && selectableIds.every(id => connectionIds.includes(id))
  const toggleAll = () => setConnectionIds(allSelectableSelected ? [] : selectableIds)
  return createPortal(
    <div className="workspace-modal-backdrop product-publish-popup-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) close() }}>
      <DialogSurface as="section" onClose={close} className="workspace-modal product-publish-popup-modal" aria-labelledby="bulk-publish-title" onMouseDown={event => event.stopPropagation()}>
        <header><div><p className="eyebrow">Toplu İşlem</p><h2 id="bulk-publish-title">Platformlarda Yayınla</h2><p>Seçili {cardCount.toLocaleString('tr-TR')} ürün kartı ({productIds.length.toLocaleString('tr-TR')} katalog kaydı) seçtiğiniz pazar yerlerine gönderilir. Zaten yayında ya da işlemde olanlar atlanır.</p></div><button type="button" className="modal-close" onClick={close} disabled={running} aria-label="Yayın penceresini kapat"><UiIcon name="close" /></button></header>
        <div className="product-publish-popup-body"><div className="product-add-page product-publish-popup"><section className="product-publish-step" aria-label="Toplu ürün yayınlama"><div className="product-publish-layout"><div className="product-publish-main">
          {connections.length
            ? <div className="publish-platform-grid" role="group" aria-label="Yayın platformları">{connections.map(connection => <BulkPublishPlatformCard key={connection.id} connection={connection} summary={summaries.get(connection.id) ?? null} modelTip={modelTips.get(connection.id) ?? null} selected={connectionIds.includes(connection.id)} locked={locked} onToggle={() => toggle(connection.id)} />)}</div>
            : <div className="publish-connections-empty"><strong>Aktif Bağlantı Bulunamadı</strong><p>Yayınlama için önce Platformlar sayfasından aktif bir bağlantı oluşturun.</p></div>}
          {error && <div className="publish-platform-warning" role="alert"><UiIcon name="alert" /><div><strong>Yayın İşleri Oluşturulamadı</strong><p>{error}</p></div></div>}
          <div className="publish-selection-footer">
            <div><strong>{result ? `${result.queued} kuyruğa alındı · ${result.skipped} atlandı · ${result.failed.length} gönderilemedi` : selectedCount ? `${selectedCount} platform seçildi` : 'Yayın platformu seçilmedi'}</strong><small>{running ? `${progress.toLocaleString('tr-TR')} / ${productIds.length.toLocaleString('tr-TR')} kayıt işlendi` : result ? (result.failed.length ? 'Gönderilemeyenlerin nedeni bildirimlerde.' : 'Sonuçlar ürün listesinde güncellenir.') : selectedCount ? 'Seçilen her mağazada ayrı yayın kuyruğu ve kontrol listesi bulunur.' : 'Yayınlanacak mağazaları kartların üstünden seçin.'}</small></div>
            {!selectedCount && !result && <span className="publish-selection-warning"><UiIcon name="alert" /> En az bir platform seçin</span>}
            {!result && selectableIds.length > 0 && <button type="button" className="secondary publish-select-all" disabled={locked} onClick={toggleAll}>{allSelectableSelected ? 'Seçimi Kaldır' : 'Tümünü Seç'}</button>}
            {result ? <button type="button" onClick={onClose}>Kapat</button> : <button type="button" disabled={running || !selectedCount} onClick={() => void publish()}>{running ? 'Kuyruğa Alınıyor…' : 'Yayınla'}</button>}
          </div>
        </div></div></section></div></div>
      </DialogSurface>
    </div>,
    document.querySelector('main') ?? document.body)
}

function BulkPublishPlatformCard({ connection, summary, modelTip, selected, locked, onToggle }: {
  connection: MarketplaceConnection
  summary: BulkPublishStatusSummary | null
  modelTip: BulkPublishModelTooltip | null
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
  return <article className={`publish-platform-card is-state-${bulkPublishCardState(summary)}${selected ? ' selected' : ''}`}>
    <button type="button" className="publish-platform-card-head" onClick={onToggle} aria-pressed={selected} aria-describedby={statusId} disabled={locked || allLive} title={allLive ? 'Seçili kayıtlar bu platformda zaten yayında.' : undefined}>
      <span className={`publish-platform-mark ${connection.platformCode.trim().toLocaleLowerCase('tr-TR')}`}>{logo ? <img className={`publish-platform-logo ${platformLogoClass(connection.platformCode)}`} src={logo} alt="" aria-hidden="true" /> : <b aria-hidden="true">{storeName.trim().charAt(0).toLocaleUpperCase('tr-TR') || 'R'}</b>}</span>
      <span><strong>{storeName}</strong><small>{subtitle}</small></span>
      <i className={`publish-platform-toggle${selected ? ' on' : ''}`} aria-hidden="true"><b /></i>
    </button>
    <dl className="publish-platform-facts">
      <div><dt>Mağaza</dt><dd title={connection.externalStoreId ? `Mağaza no: ${connection.externalStoreId}` : undefined}>{connection.displayName || connection.externalStoreId || '—'}</dd></div>
      <div><dt>Platform</dt><dd>{platformName}</dd></div>
      <div><dt>Bağlantı</dt><dd><span className="publish-platform-status active"><i aria-hidden="true" />Aktif Bağlantı</span></dd></div>
      <div className="publish-platform-fact-publication"><dt>Yayın Durumu</dt><dd id={statusId}>{summary ? <BulkPublishStatusPill summary={summary} modelTip={modelTip} platformName={storeName} tipId={`${statusId}-models`} /> : '—'}</dd>{summary?.detail && <small>{summary.detail}</small>}</div>
    </dl>
  </article>
}

// The "Yayın Durumu" pill: on hover or keyboard focus a tooltip lists each selected model code's live records on this
// store, the incomplete ones first. It is drawn on the page body, fixed to the screen, so the card and the popup's
// scrolling body never clip it.
function BulkPublishStatusPill({ summary, modelTip, platformName, tipId }: { summary: BulkPublishStatusSummary; modelTip: BulkPublishModelTooltip | null; platformName: string; tipId: string }) {
  const pillRef = useRef<HTMLSpanElement>(null)
  const [place, setPlace] = useState<{ top: number; left: number; above: boolean } | null>(null)
  const hasTip = Boolean(modelTip?.rows.length)
  const show = () => {
    const rect = pillRef.current?.getBoundingClientRect()
    if (!rect || !hasTip) return
    const spaceBelow = window.innerHeight - rect.bottom
    const above = spaceBelow < 300 && rect.top > spaceBelow
    setPlace({ top: above ? rect.top - 8 : rect.bottom + 8, left: Math.min(window.innerWidth - 16, Math.max(rect.right, 16 + 320)), above })
  }
  const hide = () => setPlace(null)
  return <>
    {/* eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex, jsx-a11y/no-static-element-interactions -- tooltip trigger: focusable so keyboards can read the per model list, Escape hides it */}
    <span ref={pillRef} className={`publish-platform-status status-${summary.tone}${hasTip ? ' has-model-tip' : ''}`} tabIndex={hasTip ? 0 : undefined} aria-describedby={place ? tipId : undefined} onMouseEnter={show} onMouseLeave={hide} onFocus={show} onBlur={hide} onKeyDown={event => { if (event.key === 'Escape' && place) { event.stopPropagation(); hide() } }}><i aria-hidden="true" />{summary.label}</span>
    {place && modelTip && createPortal(<div id={tipId} role="tooltip" className={`bulk-publish-model-tip${place.above ? ' is-above' : ''}`} style={{ top: place.top, left: place.left }}>
      <strong>{platformName} · Model Kodlarına Göre</strong>
      <ul>{modelTip.rows.map(row => <li key={row.modelCode} className={row.complete ? 'is-complete' : row.missing ? 'is-missing' : 'is-unknown'}><b>{row.modelCode}</b><span>{row.text}</span></li>)}</ul>
      {modelTip.moreCount > 0 && <small>ve {modelTip.moreCount.toLocaleString('tr-TR')} model daha</small>}
    </div>, document.body)}
  </>
}
