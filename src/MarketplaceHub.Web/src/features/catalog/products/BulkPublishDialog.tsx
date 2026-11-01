import { useMemo, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { DialogSurface, UiIcon } from '../../../shared/components'
import { newIdempotencyKey } from '../../../shared/idempotency'
import type { NotificationDetail } from '../../../shared/notifications'
import { platformLogoClass, platformLogoSource } from '../../../shared/platform-logos'
import type { MarketplaceConnection, Product } from '../shared'
import { addBulkPublicationResult, bulkPublishFailureDetails, bulkPublishMissingVat, bulkPublishRequestBody, bulkPublishResultMessage, type BulkPublicationResult } from './bulk-publish-run'
import { bulkPublishPlan, useBulkPublicationReadiness, type BulkConnectionReadiness } from './bulk-publish-readiness'
import { bulkPublishModelTooltip, bulkPublishPlatformName, summarizeBulkPublishModels, summarizeBulkPublishStatus, type BulkPublishModelTooltip, type BulkPublishStatusSummary } from './bulk-publish-status'
import { MissingValueMappingDialog } from '../MissingValueMappingDialog'
import { CategoryMappingDialog } from '../CategoryMappingDialog'
import { PublishIssueBadge } from '../PublishIssueBadge'
import { publishPopoverBounds } from '../publish-popover-placement'
import type { PublicationValueMappingFix } from '../publication-attribute-readiness'
import './bulk-publish-model-tip.css'
import '../product-editor/product-editor-design-system.css'
import '../product-editor/product-editor-hydration.css'

export type BulkPublishNotice = { title: string; message: string; kind: 'success' | 'error' | 'info'; details?: NotificationDetail[] }

const emptyResult: BulkPublicationResult = { queued: 0, skipped: 0, failed: [] }

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
// the editor does, with the VAT of the product card (its KDV Oranı), else of the record's offer elsewhere; the popup
// asks for no VAT. `products` (the selected records the list holds) feeds each card's "Yayın Durumu" summary, keeps a
// model family in one slice and counts the records with no VAT anywhere. Refused records reach the notification center
// with their reason.
export function BulkPublishDialog({ productIds, cardCount, connections, products, onClose, onDone }: {
  productIds: string[]
  cardCount: number
  connections: MarketplaceConnection[]
  products?: Product[]
  onClose: () => void
  onDone: (notice: BulkPublishNotice) => void
}) {
  const client = useQueryClient()
  const [connectionIds, setConnectionIds] = useState<string[]>([])
  const [running, setRunning] = useState(false)
  const [progress, setProgress] = useState(0)
  const [result, setResult] = useState<BulkPublicationResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  // The slices of the run and the first one not sent yet: a run cut short (network, server) goes on from there. Each
  // slice names its stores: a store with records that have required gaps gets only its ready records.
  const planRef = useRef<{ chunks: Array<{ productIds: string[]; connectionIds: string[] }>; total: number } | null>(null)
  const [resumeAt, setResumeAt] = useState<number | null>(null)
  // A selected id the list no longer holds counts as unknown in the summary instead of being dropped from the total.
  const selectedRecords = useMemo(() => {
    if (!products) return null
    const byId = new Map(products.map(product => [product.id, product]))
    return productIds.map(id => byId.get(id) ?? null)
  }, [productIds, products])
  const modelTips = useMemo(() => new Map(connections.map(connection => [connection.id, selectedRecords?.length ? bulkPublishModelTooltip(summarizeBulkPublishModels(selectedRecords, connection)) : null])), [connections, selectedRecords])
  // Records with no KDV Oranı on the card and no offer to take one from: the server refuses them, so the popup says so.
  const missingVat = useMemo(() => bulkPublishMissingVat(selectedRecords), [selectedRecords])
  const knownRecords = useMemo(() => (selectedRecords ?? []).filter((record): record is Product => record !== null), [selectedRecords])
  // The editor's publish checks on every selected record; the cards stay masked until all are checked.
  const readiness = useBulkPublicationReadiness(productIds, connections, selectedRecords)
  const checking = !readiness.done
  // A record the list has no status for, and whose publication status says it is not out there, counts as not published.
  const summaries = useMemo(() => new Map(connections.map(connection => {
    if (!selectedRecords) return [connection.id, null]
    const notPublished = new Set((readiness.byConnection.get(connection.id)?.entries ?? []).filter(entry => entry.notPublished).map(entry => entry.productId))
    const records = selectedRecords.map((record, index) => notPublished.has(productIds[index]) ? { platformStatuses: [{ platform: connection.displayName, platformCode: connection.platformCode, status: 'UNLINKED' }] } as Pick<Product, 'platformStatuses'> : record)
    return [connection.id, summarizeBulkPublishStatus(records, connection)]
  })), [connections, productIds, readiness.byConnection, selectedRecords])
  const resumable = resumeAt !== null
  const locked = running || Boolean(result) || resumable
  const close = () => { if (!running) onClose() }

  function toggle(connectionId: string) {
    setConnectionIds(ids => ids.includes(connectionId) ? ids.filter(id => id !== connectionId) : [...ids, connectionId])
  }

  async function publish(resume: boolean) {
    const plan = resume && planRef.current ? planRef.current : bulkPublishPlan(productIds, knownRecords, connectionIds, readiness.byConnection)
    planRef.current = plan
    const first = resume ? resumeAt ?? 0 : 0
    let index = first
    let total = resume ? result ?? emptyResult : emptyResult
    // What this call sent, for its notification: a resumed run does not report the first part's refusals again.
    let sent = emptyResult
    setRunning(true); setError(null); setResumeAt(null)
    if (!resume) { setResult(null); setProgress(0) }
    try {
      for (; index < plan.chunks.length; index++) {
        const chunk = plan.chunks[index]
        const part = await hubApi<BulkPublicationResult>('/products/bulk-publication-jobs', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey('bulk-publication') }, body: bulkPublishRequestBody(chunk.productIds, chunk.connectionIds) })
        total = addBulkPublicationResult(total, part); sent = addBulkPublicationResult(sent, part)
        setResult(total)
        setProgress(plan.chunks.slice(0, index + 1).reduce((count, item) => count + item.productIds.length, 0))
      }
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Yayın işleri oluşturulamadı.')
      // Nothing sent yet: the popup stays as it was and Yayınla starts again; otherwise the rest can be sent.
      if (index === 0) { setResult(null); planRef.current = null }
      else setResumeAt(index)
    } finally {
      setRunning(false)
    }
    if (index === first) return
    const interrupted = index < plan.chunks.length
    await Promise.all([client.invalidateQueries({ queryKey: ['products'] }), client.invalidateQueries({ queryKey: ['publication-status'] })])
    const refused = sent.failed.length > 0
    onDone({
      title: interrupted ? 'Toplu Yayın Yarıda Kesildi' : !refused ? 'Toplu Yayın Kuyruğa Alındı' : sent.queued ? 'Toplu Yayın Kısmen Gönderildi' : 'Toplu Yayın Gönderilemedi',
      message: bulkPublishResultMessage(sent, interrupted),
      kind: interrupted || (refused && !sent.queued) ? 'error' : refused ? 'info' : 'success',
      details: refused ? bulkPublishFailureDetails(sent.failed, connections, knownRecords) : undefined
    })
  }

  const selectedCount = connectionIds.length
  // "Tümünü Seç" picks every platform a card can be selected on: one where the selected records are not all live yet and
  // not all held back by required gaps.
  const selectableIds = connections.filter(connection => !summaries.get(connection.id)?.allLive && !allRecordsBlocked(readiness.byConnection.get(connection.id))).map(connection => connection.id)
  const partlyHeldBack = connectionIds.some(id => (readiness.byConnection.get(id)?.blocked.length ?? 0) > 0)
  const allSelectableSelected = selectableIds.length > 0 && selectableIds.every(id => connectionIds.includes(id))
  const toggleAll = () => setConnectionIds(allSelectableSelected ? [] : selectableIds)
  return createPortal(
    <div className="workspace-modal-backdrop product-publish-popup-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) close() }}>
      <DialogSurface as="section" onClose={close} className="workspace-modal product-publish-popup-modal" aria-labelledby="bulk-publish-title" onMouseDown={event => event.stopPropagation()}>
        <header><div><p className="eyebrow">Toplu İşlem</p><h2 id="bulk-publish-title">Platformlarda Yayınla</h2><p>Seçili {cardCount.toLocaleString('tr-TR')} ürün kartı ({productIds.length.toLocaleString('tr-TR')} katalog kaydı) seçtiğiniz pazar yerlerine gönderilir. Zaten yayında ya da işlemde olanlar atlanır.</p></div><button type="button" className="modal-close" onClick={close} disabled={running} aria-label="Yayın penceresini kapat"><UiIcon name="close" /></button></header>
        <div className="product-publish-popup-body"><div className="product-add-page product-publish-popup"><section className="product-publish-step" aria-label="Toplu ürün yayınlama"><div className="product-publish-layout"><div className="product-publish-main">
          {connections.length
            ? <div className="publish-platform-grid" role="group" aria-label="Yayın platformları">{connections.map(connection => <BulkPublishPlatformCard key={connection.id} connection={connection} summary={summaries.get(connection.id) ?? null} modelTip={modelTips.get(connection.id) ?? null} readiness={checking ? null : readiness.byConnection.get(connection.id) ?? null} selected={connectionIds.includes(connection.id)} locked={locked} onToggle={() => toggle(connection.id)} onMappingsSaved={readiness.recheck} />)}</div>
            : <div className="publish-connections-empty"><strong>Aktif Bağlantı Bulunamadı</strong><p>Yayınlama için önce Platformlar sayfasından aktif bir bağlantı oluşturun.</p></div>}
          {error && <div className="publish-platform-warning" role="alert"><UiIcon name="alert" /><div><strong>Yayın İşleri Oluşturulamadı</strong><p>{error}</p></div></div>}
          {missingVat > 0 && !result && <div className="bulk-publish-vat" role="note">
            <UiIcon name="alert" />
            <small>{missingVat.toLocaleString('tr-TR')} kaydın ürün kartında KDV oranı yok; mağazanın varsayılan KDV oranı yoksa gönderilmez. Ürünü açıp KDV Oranı seçip kaydedin.</small>
          </div>}
          <div className="publish-selection-footer">
            <div><strong>{result ? `${result.queued} kuyruğa alındı · ${result.skipped} atlandı · ${result.failed.length} gönderilemedi` : selectedCount ? `${selectedCount} platform seçildi` : 'Yayın platformu seçilmedi'}</strong><small>{running ? `${progress.toLocaleString('tr-TR')} / ${(planRef.current?.total ?? productIds.length).toLocaleString('tr-TR')} kayıt işlendi` : resumable ? `${progress.toLocaleString('tr-TR')} / ${(planRef.current?.total ?? productIds.length).toLocaleString('tr-TR')} kayıt gönderildi; kalanları yeniden gönderebilirsiniz.` : result ? (result.failed.length ? 'Gönderilemeyenler ve nedenleri bildirimlerde.' : 'Sonuçlar ürün listesinde güncellenir.') : checking ? `Yayın hazırlığı kontrol ediliyor: ${readiness.checked.toLocaleString('tr-TR')} / ${readiness.total.toLocaleString('tr-TR')} ürün kontrol edildi` : partlyHeldBack ? 'Zorunlu eksiği olan ürünler gönderilmez; yalnız hazır olanlar gönderilir.' : selectedCount ? 'Seçilen her mağazada ayrı yayın kuyruğu ve kontrol listesi bulunur.' : 'Yayınlanacak mağazaları kartların üstünden seçin.'}</small></div>
            {!selectedCount && !result && <span className="publish-selection-warning"><UiIcon name="alert" /> En az bir platform seçin</span>}
            {!result && (checking || selectableIds.length > 0) && <button type="button" className="secondary publish-select-all" disabled={locked || checking} onClick={toggleAll}>{allSelectableSelected ? 'Seçimi Kaldır' : 'Tümünü Seç'}</button>}
            {resumable
              ? <button type="button" disabled={running} onClick={() => void publish(true)}>{running ? 'Kuyruğa Alınıyor…' : 'Kalanları Gönder'}</button>
              : result && !running ? <button type="button" onClick={onClose}>Kapat</button> : <button type="button" disabled={running || checking || !selectedCount} onClick={() => void publish(false)}>{running ? 'Kuyruğa Alınıyor…' : 'Yayınla'}</button>}
          </div>
        </div></div></section></div></div>
      </DialogSurface>
    </div>,
    document.querySelector('main') ?? document.body)
}

// Every record that is not already out there has required gaps: nothing would go, so the card cannot be selected.
function allRecordsBlocked(readiness: BulkConnectionReadiness | null | undefined) {
  return Boolean(readiness && readiness.blocked.length > 0 && readiness.readyCount === 0)
}

// The value mappings the store's records lack, one group per marketplace category (the fix window maps one category's
// values and shares them with the connection's other categories).
function fixGroups(readiness: BulkConnectionReadiness | null) {
  const groups = new Map<string, Map<string, PublicationValueMappingFix>>()
  for (const entry of readiness?.entries ?? []) {
    if (entry.skipped || !entry.categoryScope || !entry.fixes.length) continue
    const fixes = groups.get(entry.categoryScope) ?? new Map<string, PublicationValueMappingFix>()
    for (const fix of entry.fixes) fixes.set(`${fix.externalAttributeId}:${fix.localValueId}`, fix)
    groups.set(entry.categoryScope, fixes)
  }
  return [...groups.entries()].map(([categoryScope, fixes]) => ({ categoryScope, fixes: [...fixes.values()] }))
}

// The panel categories the store has no current mapping of, each once.
function unmappedCategories(readiness: BulkConnectionReadiness | null) {
  const categories = new Map<string, string>()
  for (const entry of readiness?.entries ?? []) if (!entry.skipped && entry.categoryMapping && entry.categoryId) categories.set(entry.categoryId, entry.categoryName || 'Ürün')
  return [...categories.entries()].map(([id, name]) => ({ id, name }))
}

const shownBlockedProducts = 6

function BulkPublishPlatformCard({ connection, summary, modelTip, readiness, selected, locked, onToggle, onMappingsSaved }: {
  connection: MarketplaceConnection
  summary: BulkPublishStatusSummary | null
  modelTip: BulkPublishModelTooltip | null
  // The editor's checks on the selected records; null while they run (the card is masked until then).
  readiness: BulkConnectionReadiness | null
  selected: boolean
  locked: boolean
  onToggle: () => void
  onMappingsSaved: () => void
}) {
  const logo = platformLogoSource(connection.platformCode)
  const platformName = bulkPublishPlatformName(connection.platformCode)
  const storeName = connection.displayName || connection.externalStoreId || platformName
  const settling = readiness === null
  const blocked = readiness?.blocked ?? []
  const checkedCount = (readiness?.entries.length ?? 0) - (readiness?.skippedCount ?? 0)
  const allBlocked = allRecordsBlocked(readiness)
  const someBlocked = blocked.length > 0 && !allBlocked
  const allLive = Boolean(summary?.allLive) && !selected
  const subtitle = settling ? 'Yayın hazırlığı kontrol ediliyor' : allLive ? 'Seçili kayıtların tümü zaten yayında' : allBlocked ? 'Zorunlu eksikler giderilmeden seçilemez' : someBlocked ? (selected ? 'Yayın için seçildi · yalnız hazır olanlar gönderilir' : 'Yalnız hazır olanlar gönderilir') : selected ? 'Yayın için seçildi' : 'Yayın için seçilmedi'
  const statusId = `bulk-publish-status-${connection.id}`
  const [fixScope, setFixScope] = useState<string | null>(null)
  const [categoryToMap, setCategoryToMap] = useState<{ id: string; name: string } | null>(null)
  const categories = unmappedCategories(readiness)
  const groups = fixGroups(readiness)
  const checkFailed = (readiness?.entries ?? []).some(entry => entry.checkFailed)
  const valueFixCount = groups.reduce((count, group) => count + group.fixes.length, 0)
  // What the red badge's popover offers: map each unmapped category, complete each category's value mappings, check again.
  const fixActions = [
    ...categories.map(category => ({ key: `category:${category.id}`, label: categories.length > 1 ? `${category.name} Kategorisini Eşle` : 'Kategori Eşle', onRun: () => setCategoryToMap(category) })),
    ...groups.map(group => ({ key: `values:${group.categoryScope}`, label: group.fixes.length > 1 ? `Değerleri Eşle (${group.fixes.length})` : 'Değeri Eşle', onRun: () => setFixScope(group.categoryScope) })),
    ...(checkFailed ? [{ key: 'retry', label: 'Yeniden Dene', onRun: onMappingsSaved }] : [])
  ]
  const openGroup = groups.find(group => group.categoryScope === fixScope)
  const state = settling ? 'settling' : allBlocked ? 'error' : bulkPublishCardState(summary)
  const format = (value: number) => value.toLocaleString('tr-TR')
  const readinessDetail = blocked.length ? [`${format(readiness?.readyCount ?? 0)} hazır`, `${format(blocked.length)} eksik`, ...(readiness?.skippedCount ? [`${format(readiness.skippedCount)} zaten yayında`] : [])].join(' · ') : null
  const detail = readinessDetail ?? summary?.detail
  return <article className={`publish-platform-card is-state-${state}${selected && !settling ? ' selected' : ''}${settling ? ' is-settling' : ''}`} aria-busy={settling || undefined}>
    <button type="button" className="publish-platform-card-head" onClick={onToggle} aria-pressed={selected} aria-describedby={statusId} disabled={locked || settling || allLive || allBlocked} title={allLive ? 'Seçili kayıtlar bu platformda zaten yayında.' : allBlocked ? 'Seçili ürünlerin hepsinde zorunlu eksik var.' : undefined}>
      <span className={`publish-platform-mark ${connection.platformCode.trim().toLocaleLowerCase('tr-TR')}`}>{logo ? <img className={`publish-platform-logo ${platformLogoClass(connection.platformCode)}`} src={logo} alt="" aria-hidden="true" /> : <b aria-hidden="true">{storeName.trim().charAt(0).toLocaleUpperCase('tr-TR') || 'R'}</b>}</span>
      <span><strong>{storeName}</strong><small>{subtitle}</small></span>
      <i className={`publish-platform-toggle${selected && !settling ? ' on' : ''}`} aria-hidden="true"><b /></i>
    </button>
    <dl className="publish-platform-facts">
      <div><dt>Mağaza</dt><dd title={connection.externalStoreId ? `Mağaza no: ${connection.externalStoreId}` : undefined}>{connection.displayName || connection.externalStoreId || '—'}</dd></div>
      <div><dt>Platform</dt><dd>{platformName}</dd></div>
      <div><dt>Bağlantı</dt><dd><span className="publish-platform-status active"><i aria-hidden="true" />Aktif Bağlantı</span></dd></div>
      <div className="publish-platform-fact-publication"><dt>Yayın Durumu</dt><dd id={statusId}>{settling
        ? <span className="publish-platform-status status-neutral"><i aria-hidden="true" />Kontrol ediliyor</span>
        : blocked.length
          ? <PublishIssueBadge id={`${statusId}-issues`} label={`${format(blocked.length)}/${format(checkedCount)} üründe zorunlu eksik`} count={valueFixCount || undefined} heading="Zorunlu Eksiği Olan Ürünler" rows={blocked.slice(0, shownBlockedProducts).map(entry => ({ key: entry.productId, title: entry.title, href: `/products/${entry.productId}`, detail: `${entry.issues[0].attribute}: ${entry.issues[0].detail}${entry.issues.length > 1 ? ` (+${entry.issues.length - 1})` : ''}` }))} moreText={blocked.length > shownBlockedProducts ? `ve ${format(blocked.length - shownBlockedProducts)} ürün daha` : undefined} actions={fixActions} />
          : summary ? <BulkPublishStatusPill summary={summary} modelTip={modelTip} platformName={storeName} tipId={`${statusId}-models`} /> : '—'}</dd>{!settling && detail && <small>{detail}</small>}</div>
    </dl>
    {categoryToMap && <CategoryMappingDialog connectionId={connection.id} platformName={storeName} categoryId={categoryToMap.id} categoryName={categoryToMap.name} onClose={() => setCategoryToMap(null)} onSaved={() => onMappingsSaved()} />}
    {openGroup && <MissingValueMappingDialog connectionId={connection.id} platformName={storeName} categoryScope={openGroup.categoryScope} fixes={openGroup.fixes} onClose={() => setFixScope(null)} onSaved={() => onMappingsSaved()} />}
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
    if (!pillRef.current || !hasTip) return
    // The room below ends at the popup's Yayınla bar, not at the window's edge.
    const { bottom, anchor: rect } = publishPopoverBounds(pillRef.current)
    const spaceBelow = bottom - rect.bottom
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
