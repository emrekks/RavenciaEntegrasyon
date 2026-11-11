import { useEffect, useMemo, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { followJobOutcomes, groupedOutcomeFeedback, outcomeTarget, type FollowedJob } from '../../../shared/job-outcome'
import type { NotificationDetail } from '../../../shared/notifications'
import { PlatformUpdateDialog, isPlatformUpdateWritable, type PlatformUpdateTarget } from '../product-editor/PlatformUpdateDialog'
import { productInformationLabel, readPlatformUpdatePreferences, readPlatformUpdateStores, type PlatformUpdateField, type PlatformUpdatePreferences } from '../product-editor/platform-update-fields'
import { mapWithConcurrency } from '../product-editor/product-media-save'
import { isProductUpdateConnection, productUpdateCapabilities } from '../platform-update-targets'
import type { MarketplaceConnection, Product } from '../shared'
import { bulkPlatformUpdateRequests, isOnMarketplace, onMarketplaceCount, productRequiredGaps, requiredGapText, type BulkUpdateRecord, type BulkUpdateRequest } from './bulk-platform-update'
import { bulkPublishPlatformName } from './bulk-publish-status'
import '../product-editor/product-editor-design-system.css'
import '../product-editor/product-editor-hydration.css'
import './bulk-platform-update.css'

export type BulkPlatformUpdateNotice = { title: string; message: string; kind: 'success' | 'error' | 'info'; details?: NotificationDetail[]; lines?: string[]; persistent?: boolean }

const checkConcurrency = 6
const sendConcurrency = 4
const shownProducts = 8

// One row per reason, with the products and stores it stopped (the first few named).
function bulkUpdateFailureDetails(failures: ReadonlyMap<string, string[]>): NotificationDetail[] {
  return [...failures.entries()].map(([reason, items]) => ({ label: reason, value: items.length > 3 ? `${items.slice(0, 3).join(', ')} ve ${items.length - 3} iş daha` : items.join(', '), tone: 'error' as const }))
}

type Checked = { product: Product | null; failed: boolean }

// Product list "Platformlara Güncelle": the editor's "Platformlarda Güncelle" window for the selected products. Each
// product is read once (its required fields, as the editor checks them before Hızlı Güncelle); the window lists them
// with their state, offers only the stores at least one of them is on, and sends nothing until Gönder.
export function BulkPlatformUpdateDialog({ productIds, cardCount, connections, products, onClose, onDone }: {
  productIds: string[]
  cardCount: number
  connections: MarketplaceConnection[]
  products: Product[]
  onClose: () => void
  onDone: (notice: BulkPlatformUpdateNotice) => void
}) {
  const client = useQueryClient()
  const [checked, setChecked] = useState<Map<string, Checked>>(new Map())
  const [preferences, setPreferences] = useState<PlatformUpdatePreferences>(() => readPlatformUpdatePreferences())
  const [submitting, setSubmitting] = useState(false)
  const listRecords = useMemo(() => new Map(products.map(product => [product.id, product])), [products])

  useEffect(() => {
    let cancelled = false
    void mapWithConcurrency(productIds, checkConcurrency, async id => {
      let result: Checked
      try {
        result = { product: await client.query({ queryKey: ['product', id], queryFn: () => hubApi<Product>(`/products/${id}`), staleTime: 15_000, retry: false }), failed: false }
      } catch {
        result = { product: null, failed: true }
      }
      if (!cancelled) setChecked(current => new Map(current).set(id, result))
    })
    return () => { cancelled = true }
  }, [client, productIds])

  // The list's record holds the marketplace standing; the product read holds the fields (and the standing when the list
  // does not hold the record).
  const records = useMemo(() => productIds.map(id => {
    const listed = listRecords.get(id)
    const read = checked.get(id)
    const product = read?.product ?? null
    return {
      id,
      title: product?.title ?? listed?.title ?? 'Ürün kartı',
      modelCode: product?.modelCode ?? listed?.modelCode ?? null,
      status: product?.status ?? listed?.status ?? '',
      platformStatuses: listed?.platformStatuses ?? product?.platformStatuses,
      gaps: product ? productRequiredGaps(product) : [],
      read: Boolean(read),
      failed: Boolean(read?.failed)
    }
  }), [checked, listRecords, productIds])
  const checking = records.some(record => !record.read)

  const updateConnections = useMemo(() => connections.filter(isProductUpdateConnection), [connections])
  const targets: PlatformUpdateTarget[] = useMemo(() => updateConnections.map(connection => {
    const count = onMarketplaceCount(records, connection)
    return {
      connection,
      capabilities: productUpdateCapabilities(connection.platformCode),
      blockReason: count ? undefined : 'Seçili ürünlerin hiçbiri bu mağazada yayında değil',
      detail: count ? `${count.toLocaleString('tr-TR')}/${records.length.toLocaleString('tr-TR')} ürün bu mağazada yayında` : undefined
    }
  }), [records, updateConnections])
  const writableIds = useMemo(() => targets.filter(isPlatformUpdateWritable).map(target => target.connection.id), [targets])
  // The stores picked last in the update window, as the editor starts it; the first time, every store the products are on.
  const [connectionIds, setConnectionIds] = useState<string[]>(() => readPlatformUpdateStores(writableIds, writableIds))
  // A store whose products turn out not to be there (the read came back) leaves the pick.
  const pickedIds = connectionIds.filter(id => writableIds.includes(id))

  // Held back: a draft, an empty required field or a product that could not be read.
  const heldBack = records.filter(record => record.failed || record.gaps.length > 0 || record.status.trim().toUpperCase() === 'DRAFT')
  const sendable = records.filter(record => !heldBack.includes(record) && updateConnections.some(connection => isOnMarketplace(record, connection)))
  const sendBlocked = checking
    ? `Ürünler kontrol ediliyor: ${records.filter(record => record.read).length.toLocaleString('tr-TR')} / ${records.length.toLocaleString('tr-TR')}`
    : !sendable.length ? 'Gönderilebilecek ürün yok: zorunlu eksikleri ürün sayfasında tamamlayın.' : ''

  const close = () => { if (!submitting) onClose() }

  async function send(fields: PlatformUpdateField[]) {
    if (checking || submitting) return
    const ready: BulkUpdateRecord[] = sendable.map(record => ({ id: record.id, title: record.title, status: record.status, platformStatuses: record.platformStatuses, gaps: record.gaps }))
    const requests = bulkPlatformUpdateRequests(ready, updateConnections, pickedIds, fields, () => newIdempotencyKey('bulk-update-group'))
    if (!requests.length) return
    setSubmitting(true)
    const followed: FollowedJob[] = []
    const failures = new Map<string, string[]>()
    const platformOf = (connectionId: string) => bulkPublishPlatformName(updateConnections.find(connection => connection.id === connectionId)?.platformCode ?? '')
    const titleOf = (productId: string) => records.find(record => record.id === productId)?.title ?? 'Ürün kartı'
    await mapWithConcurrency(requests, sendConcurrency, async (request: BulkUpdateRequest) => {
      try {
        const accepted = request.kind === 'product'
          ? await hubApi<{ jobId?: string }>(`/products/${request.productId}/update-jobs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ connectionId: request.connectionId, fields: request.fields, sendGroupId: request.sendGroupId }) })
          : await hubApi<{ jobId?: string }>(`/products/${request.productId}/connections/${request.connectionId}/price-inventory-sync-jobs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ price: request.price, stock: request.stock }) })
        const what = request.kind === 'product' ? productInformationLabel(request.fields) : request.price && request.stock ? 'fiyat-stok' : request.price ? 'fiyat' : 'stok'
        if (accepted?.jobId) followed.push({ jobId: accepted.jobId, platformName: platformOf(request.connectionId), what, label: titleOf(request.productId) })
      } catch (reason) {
        const message = reason instanceof Error ? reason.message : 'Güncelleme işi oluşturulamadı.'
        failures.set(message, [...(failures.get(message) ?? []), `${titleOf(request.productId)} · ${platformOf(request.connectionId)}`])
      }
    })
    setSubmitting(false)
    await Promise.all([client.invalidateQueries({ queryKey: ['products'] }), client.invalidateQueries({ queryKey: ['publication-status'] })])
    const failedCount = [...failures.values()].reduce((sum, items) => sum + items.length, 0)
    const queuedCount = requests.length - failedCount
    const lines = [
      `${queuedCount.toLocaleString('tr-TR')} güncelleme işi kuyruğa alındı (${ready.length.toLocaleString('tr-TR')} ürün, ${pickedIds.length.toLocaleString('tr-TR')} mağaza).`,
      ...(heldBack.length ? [`${heldBack.length.toLocaleString('tr-TR')} ürün zorunlu eksik ya da taslak olduğu için gönderilmedi.`] : []),
      ...(failedCount ? [`${failedCount.toLocaleString('tr-TR')} iş oluşturulamadı; nedenleri bildirimlerde.`] : [])
    ]
    onDone({
      title: !failedCount ? 'Toplu Güncelleme Başladı' : queuedCount ? 'Toplu Güncelleme Kısmen Başladı' : 'Toplu Güncelleme Gönderilemedi',
      message: lines.join(' '),
      lines,
      kind: failedCount ? 'error' : 'info',
      persistent: failedCount > 0,
      details: failedCount ? bulkUpdateFailureDetails(failures) : undefined
    })
    onClose()
    if (followed.length) {
      const target = outcomeTarget(followed)
      void followJobOutcomes(followed, target, outcome => onDone({ title: outcome.title, message: outcome.lines.join(' '), lines: outcome.lines, kind: outcome.kind, persistent: outcome.kind === 'error' }), {
        summarize: outcomes => groupedOutcomeFeedback(followed, outcomes, target),
        pollMs: 5_000
      })
    }
  }


  const format = (value: number) => value.toLocaleString('tr-TR')
  const stateOf = (record: typeof records[number]) => {
    if (!record.read) return { text: 'Kontrol ediliyor', tone: 'neutral' }
    if (record.failed) return { text: 'Ürün okunamadı · gönderilmez', tone: 'danger' }
    if (record.status.trim().toUpperCase() === 'DRAFT') return { text: 'Taslak · gönderilmez', tone: 'danger' }
    if (record.gaps.length) return { text: `Zorunlu eksik: ${requiredGapText(record.gaps)}`, tone: 'danger' }
    const stores = updateConnections.filter(connection => isOnMarketplace(record, connection)).length
    return stores ? { text: `Hazır · ${format(stores)} mağazada yayında`, tone: 'success' } : { text: 'Hiçbir mağazada yayında değil', tone: 'neutral' }
  }

  return <PlatformUpdateDialog
    eyebrow="Toplu İşlem"
    intro={`Seçili ${format(cardCount)} ürün kartı (${format(productIds.length)} katalog kaydı) yalnız yayında olduğu mağazalarda güncellenir.`}
    targets={targets}
    selectedIds={pickedIds}
    onSelectedIdsChange={setConnectionIds}
    fields={preferences.fields}
    onFieldsChange={fields => setPreferences(current => ({ ...current, fields }))}
    openGroups={preferences.openGroups}
    onOpenGroupsChange={openGroups => setPreferences(current => ({ ...current, openGroups }))}
    submitting={submitting}
    sendBlocked={sendBlocked}
    onClose={close}
    onSend={fields => void send(fields)}>
    <section className="platform-update-section bulk-platform-update-products" aria-labelledby="bulk-platform-update-products-title">
      <header className="platform-update-section-head"><div><h3 id="bulk-platform-update-products-title">Seçili Ürünler</h3><small>{checking ? `${format(records.filter(record => record.read).length)} / ${format(records.length)} ürün kontrol edildi` : `${format(sendable.length)} hazır${heldBack.length ? ` · ${format(heldBack.length)} gönderilmez` : ''}`}</small></div></header>
      <ul aria-label="Güncellenecek ürünler">
        {records.slice(0, shownProducts).map(record => {
          const state = stateOf(record)
          return <li key={record.id}><span><strong title={record.title}>{record.title}</strong>{record.modelCode && <small>{record.modelCode}</small>}</span><span className={`publish-platform-status status-${state.tone}`}><i aria-hidden="true" />{state.text}</span></li>
        })}
      </ul>
      {records.length > shownProducts && <small className="bulk-platform-update-more">ve {format(records.length - shownProducts)} ürün daha</small>}
    </section>
  </PlatformUpdateDialog>
}
