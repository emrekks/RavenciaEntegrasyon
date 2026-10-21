// Siparişler (/orders).
import { copyText } from '../../../shared/copy-text'
import { Fragment, useEffect, useLayoutEffect, useMemo, useRef, useState, type ChangeEvent, type CSSProperties, type FormEvent } from 'react'
import { createPortal } from 'react-dom'
import { Link, useSearchParams } from 'react-router'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, loadAllPages } from '../../../shared/api'
import { Busy, cargoCarrier, cargoCarriers, cargoLabel, cargoMatches, ErrorBox, UiIcon, DialogSurface } from '../../../shared/components'
import { newIdempotencyKey, sendWithVersionedIdempotencyKey, versionedIdempotencyKey } from '../../../shared/idempotency'
import { safeExternalUrl } from '../../../shared/safe-url'
import { useMediaQuery } from '../../../shared/hooks/useMediaQuery'
import { setManualInvoiceStatus, type ManualInvoiceStatus } from '../../invoicing/manual-invoice-status'
import { NoticeToast } from '../../../shared/components/NoticeToast'
import { statusLabel } from '../../../shared/status-labels'
import { DateRangePicker } from '../../../shared/DateRangePicker'
import { RrAnchor, RrBadge, RrError, RrFilterCard, RrIcon, RrImagePreview, RrLoading, RrLogo, RrModal, RrPageSize, RrRing, RrThumb, RrWordmark, titleTr, type RingTone } from '../returns-ui'
import { useScrollLock } from '../../../shared/hooks/useScrollLock'
import { appendNotification, normalizeNotificationKind } from '../../../shared/notifications'
import { orderLinkOnlyTabs, orderStatusMoreTabs, orderStatusTabs, resolveOrderStatusTab } from '../order-status-tabs'
import { onHoldOrderStatusText, overdueShipmentDays } from '../order-delivery-presentation'
import { addressBlockFields, code128Bars, loadPrintedShippingLabels, shippingLabelPageCss, markShippingLabelPrinted, printedShippingLabelKey, shippingLabelFields, useShippingLabelSettings, type ShippingLabelBlock, type ShippingLabelField, type ShippingLabelFormat, type ShippingLabelSettings } from '../../shipping'
import { isActiveMarketplaceConnection, isMarketplacePlatformSelected, marketplacePlatformLabel, marketplacePlatformOptions } from '../marketplace-platform-support'
import { productImageFallbackUrls as createProductImageFallbackUrls } from '../product-image-lookups'
import { visibleOrderLineVariantOptions } from '../order-line-variant-options'
import { invoiceProviderForEnvironment } from '../../invoicing/invoice-creation-availability'
import { InvoiceWorkspacePreviewModal, type InvoicePreviewTarget } from '../../invoicing/InvoiceWorkspacePreviewModal'
import { ViewTabsWithMore, viewTabLabel } from '../../../shared/view-tabs'
import { formatDateTime, formatIstanbul, fromDateTimeLocal, istanbulDateKey, istanbulDayBoundary } from '../../../shared/format-date'
import { formatMoney } from '../../../shared/format-money'
import { labelPersonalData, loadOrderLabelDetails, type OrderLabelDetail } from './shipping-label-data'
import { OrderMetricCards } from './OrderMetricCards'
import { Connection, IntegrationFeedback, Order, OrderAddress, OrderLine, Page, ProductTitleLink, RETURN_NO_PLATFORM, ReturnColumnPop, ReturnFactLines, ReturnHeadCell, ReturnInvoiceRing, ReturnPlatformPop, Shipment, displayOrderNumber, orderDisplayShipment, productLineQuantity, remainingText, returnDateText, returnLineFacts, returnPlatformName, usableShipmentDueAt } from '../shared'

const invoiceCreationDisabledHelp = 'Fatura oluşturmak için Entegrasyonlar > bu bağlantı ayarlarından “Fatura oluşturma” seçeneğini açın.'
type ProductImagePreview = { url: string; fallbackUrl?: string | null; fallbackUrls?: string[]; title: string; modelCode?: string | null; variant?: string | null }
type OrderSummary = { all: number; new: number; processing: number; shipped: number; delivered: number; resent: number; onHold: number; cancelled: number; returned: number; returnInTransit: number; partiallyCancelled: number; manualReview: number; pending: number; unverified: number; paymentPending: number; undelivered?: number }
type OrderSort = 'DATE_DESC' | 'DATE_ASC' | 'DUE_DESC' | 'DUE_ASC' | 'SHIP_DESC' | 'SHIP_ASC'
type OrderFilters = { search: string; status: string; platform: string; platforms: string[]; listing: string; cargo: string; invoice: string; invoiceType: string; invoiceRegion: string; label: string; sort: OrderSort; dateFrom: string; dateTo: string }
type InvoiceViewer = { id: string; orderNumber: string; invoiceType: string; status: string; currency: string; payableTotal: number; taxTotal: number; invoiceNumber: string | null; lines: Array<{ id: string; description: string; sku: string | null; unit: string; quantity: number; vatRate: number; lineTotal: number }>; documents: Array<{ id: string; documentType: string; sha256: string }> }
// The shipping-deadline sort only means something for orders still to be shipped (Yeni, İşleme Alınanlar): other tabs
// neither offer it nor keep it.
const dueSortTabs = ['NEW', 'PROCESSING']
const keepSort = (status: string, sort: OrderFilters['sort']): OrderFilters['sort'] => dueSortTabs.includes(status) || (sort !== 'DUE_ASC' && sort !== 'DUE_DESC') ? sort : 'DATE_DESC'
const initialOrderFilters: OrderFilters = { search: '', status: 'ALL', platform: 'ALL', platforms: [], listing: 'ALL', cargo: 'ALL', invoice: 'ALL', invoiceType: 'ALL', invoiceRegion: 'ALL', label: 'ALL', sort: 'DATE_DESC', dateFrom: '', dateTo: '' }
const orderStatusRank: Record<string, number> = { MANUAL_REVIEW: 110, RETURNED: 100, RETURN_IN_TRANSIT: 90, DELIVERED: 80, UNDELIVERED: 70, SHIPPED: 60, READY_TO_SHIP: 50, ON_HOLD: 40, PROCESSING: 30, PARTIALLY_CANCELLED: 20, UNVERIFIED: 15, NEW: 10, PAYMENT_PENDING: 5, CANCELLED: 0 }
function hepsiburadaPackageStatusHistoryCutoff(now = new Date()): Date {
  const cutoff = new Date(now)
  const day = cutoff.getUTCDate()
  cutoff.setUTCDate(1)
  cutoff.setUTCMonth(cutoff.getUTCMonth() - 1)
  const lastDay = new Date(Date.UTC(cutoff.getUTCFullYear(), cutoff.getUTCMonth() + 1, 0)).getUTCDate()
  cutoff.setUTCDate(Math.min(day, lastDay))
  return cutoff
}
function aggregateOrderStatus(statuses: string[]) {
  const values = statuses.map(status => status.toUpperCase())
  if (!values.length) return 'NEW'
  if (values.includes('MANUAL_REVIEW')) return 'MANUAL_REVIEW'
  // As the server (ShipmentPackageStatusPolicy.Aggregate, audit 2026-10-20 S3): a package that still needs work
  // decides, the earliest step first; payment pending only when nothing else needs work.
  const open = values.filter(status => ['NEW', 'PARTIALLY_CANCELLED', 'PROCESSING', 'ON_HOLD', 'READY_TO_SHIP'].includes(status))
  if (open.length) return open.sort((left, right) => (orderStatusRank[left] ?? 0) - (orderStatusRank[right] ?? 0))[0]
  if (values.includes('PAYMENT_PENDING')) return 'PAYMENT_PENDING'
  const operational = values.filter(status => status !== 'CANCELLED' && status !== 'RETURNED')
  if (operational.length) return operational.sort((left, right) => (orderStatusRank[right] ?? 110) - (orderStatusRank[left] ?? 110))[0]
  return values.includes('RETURNED') ? 'RETURNED' : 'CANCELLED'
}
// A spreadsheet reads a cell starting with = + - @ (or a tab / carriage return) as a formula: such text gets a leading apostrophe.
function csvSafeText(text: string) {
  return /^[=+\-@\t\r]/.test(text) ? `'${text}` : text
}
async function loadOrderPage(filters: OrderFilters, limit: number, after: string | null, page = 1): Promise<Page<Order>> {
  const params = new URLSearchParams({ limit: String(limit) })
  orderFilterParams(filters, true).forEach((value, key) => params.set(key, value))
  if (after) params.set('after', after)
  if (filters.status !== 'ALL') params.set('status', filters.status)
  params.set('sort', filters.sort)
  if (!after && page > 1) params.set('page', String(page))
  return hubApi<Page<Order>>(`/orders?${params.toString()}`)
}
// The list filters as query parameters (status, sort and paging aside): the list and its tab counters
// read the same set (audit 2026-10-20 S12).
function orderFilterParams(filters: OrderFilters, serverLabelFilter: boolean) {
  const params = new URLSearchParams()
  if (filters.search.trim()) params.set('search', filters.search.trim())
  if (filters.platform !== 'ALL') params.set('platform', filters.platform)
  if (filters.platforms.length) params.set('platforms', filters.platforms.join(','))
  if (filters.listing !== 'ALL') params.set('listing', filters.listing)
  if (filters.cargo !== 'ALL') params.set('cargo', filters.cargo)
  if (filters.invoice !== 'ALL') params.set('invoice', filters.invoice)
  if (filters.invoiceType !== 'ALL') params.set('invoiceType', filters.invoiceType)
  if (filters.invoiceRegion !== 'ALL') params.set('invoiceRegion', filters.invoiceRegion)
  if (serverLabelFilter && filters.label !== 'ALL') params.set('labelPrinted', filters.label === 'PRINTED' ? 'true' : 'false')
  if (filters.dateFrom) params.set('dateFrom', istanbulDayBoundary(filters.dateFrom))
  if (filters.dateTo) params.set('dateTo', istanbulDayBoundary(filters.dateTo, true))
  return params
}
async function loadAllOrderPages(filters: OrderFilters): Promise<Page<Order>> {
  const limit = 200
  const items: Order[] = []
  let page = 1
  let totalCount = 0
  while (page <= 100) {
    const result = await loadOrderPage(filters, limit, null, page)
    items.push(...result.items)
    totalCount = result.totalCount ?? items.length
    if (!result.hasMore || !result.items.length || items.length >= totalCount) break
    page++
  }
  return { items, nextCursor: null, hasMore: false, totalCount: items.length }
}
const invoiceCreationStatuses = new Set(['FATURA_BEKLIYOR', 'FATURA_REDDEDILDI', 'FATURA_ISLENIYOR'])
function isInvoicePending(item: Pick<Order, 'invoiceStatus'>) { return invoiceCreationStatuses.has(item.invoiceStatus) }
function isMicroExportLabelBlocked(item: Pick<Order, 'isMicroExport' | 'invoiceStatus'>) { return item.isMicroExport && isInvoicePending(item) }
function ProductImagePreviewModal({ preview, onClose }: { preview: ProductImagePreview; onClose: () => void }) {
  const sources = [...new Set([preview.url, ...(preview.fallbackUrls ?? []), preview.fallbackUrl].filter((value): value is string => Boolean(value)))]
  const variant = visibleOrderLineVariantOptions(preview.variant ?? null).map(option => `${option.label}: ${option.value}`).join(' · ')
  return <RrImagePreview title={preview.title} crumb={[preview.modelCode ? `Model Kodu: ${preview.modelCode}` : '', variant].filter(Boolean).join(' · ')} sources={sources} onClose={onClose} />
}
type OrderDetail = { id: string; orderNumber: string; derivedStatus: string; currency: string; grossAmount: number; discountAmount: number; netAmount: number; orderedAt: string; connectionId: string | null; platformCode: string; platformDisplayName: string; customerName: string; customerEmail: string | null; customerPhone: string | null; customerTaxOrIdentityNumber: string | null; orderType: string; isMicroExport: boolean; isEInvoiceAvailable: boolean | null; shipmentAddressJson: string; invoiceAddressJson: string; shipmentDueAt: string | null; invoiceStatus: string; invoiceDocumentUrl: string | null; lines: OrderLine[]; packages: Shipment[]; version: number }
// The server could not confirm an action yet (it reads the marketplace and updates the panel) or
// queued it because the marketplace quota was full: neither is a failure, and neither should be resent.
function isPendingAction(error: unknown): error is ApiRequestError {
  return error instanceof ApiRequestError && (error.code === 'EXTERNAL_EFFECT_VERIFYING' || error.code === 'ACTION_QUEUED')
}
type IntegrationFeedbackHandler = (message: string, kind: IntegrationFeedback['kind'] | 'pending') => void
function safeJson(value: string) { try { return JSON.parse(value || '{}') as Record<string, unknown> } catch { return {} } }
function meaningfulText(value: unknown): value is string { return typeof value === 'string' && /[\p{L}\p{N}]/u.test(value) }
// Some marketplaces (n11) send the street line on its own and the district and
// city as separate fields; the label and address views append the missing ones.
function completeAddressLine(fullAddress: string, data: Record<string, unknown>) { const lowered = fullAddress.toLocaleLowerCase('tr-TR'); const missing = [data.district, data.city].filter(meaningfulText).map(item => item.trim()).filter(item => !lowered.includes(item.toLocaleLowerCase('tr-TR'))); return missing.length ? [fullAddress, missing.join(' / ')].join(' · ') : fullAddress }
function addressText(value: string, platformCode = 'TRENDYOL') { const source = safeJson(value); return addressDataText([source.shipmentAddress, source.invoiceAddress, source.address].find(item => item && typeof item === 'object') as Record<string, unknown> | undefined ?? source, platformCode) }
function listAddressText(address: OrderAddress | null | undefined, platformCode = 'TRENDYOL') { return addressDataText((address ?? {}) as Record<string, unknown>, platformCode) }
function addressDataText(data: Record<string, unknown>, platformCode: string) { const fullAddress = typeof data.fullAddress === 'string' ? data.fullAddress.trim() : ''; const pointPrefix = data.isDeliveryPoint === true ? `Teslimat noktası${typeof data.deliveryPointType === 'string' ? ` (${data.deliveryPointType})` : ''}: ` : ''; if (meaningfulText(fullAddress)) return pointPrefix + completeAddressLine(fullAddress, data); const seen = new Set<string>(); const parts = ['address1', 'address2', 'neighborhood', 'district', 'countyName', 'city', 'province', 'provinceCode', 'country', 'countryCode', 'zip', 'postalCode'].map(key => data[key]).filter(meaningfulText).map(item => item.trim()).filter(item => { const normalized = item.toLocaleLowerCase('tr-TR'); if (seen.has(normalized)) return false; seen.add(normalized); return true }); return parts.length ? pointPrefix + parts.join(' · ') : platformCode === 'SHOPIFY' ? 'Shopify adres bilgisi bulunamadı' : 'Trendyol adres bilgisini maskeledi' }
function longDate(value: string) { return formatIstanbul(new Date(value), { day: '2-digit', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' }) }
function deliveryText(item: Order) { const shipment = orderDisplayShipment(item); const rawStatuses = [...(item.packages ?? []).map(value => value.rawStatus), ...(item.lines ?? []).map(value => value.rawStatus)]; if (item.derivedStatus === 'CANCELLED') return { label: 'İptal tarihi', value: shipment?.statusOccurredAt ? longDate(shipment.statusOccurredAt) : 'İptal zamanı bekleniyor', note: null, overdue: false }; if (item.derivedStatus === 'DELIVERED') return { label: 'Teslim edildi', value: shipment?.statusOccurredAt ? longDate(shipment.statusOccurredAt) : 'Teslimat zamanı bekleniyor', note: null, overdue: false }; if (item.derivedStatus === 'UNVERIFIED') return { label: 'Sipariş durumu', value: 'Durumu doğrulanmadı', note: 'Paket veya güncel sipariş yaşam döngüsü bilgisi bulunmadığından teslimat durumu doğrulanamadı.', overdue: false }; if (item.derivedStatus === 'PAYMENT_PENDING') return { label: 'Sipariş durumu', value: 'Trendyol ödeme onayı bekleniyor', note: 'Ödeme onaylanana kadar paket işlemleri kullanılamaz.', overdue: false }; if (item.platformCode.trim().toUpperCase() === 'HEPSIBURADA' && item.derivedStatus === 'UNDELIVERED') return { label: 'Sipariş durumu', value: onHoldOrderStatusText(item.platformCode, rawStatuses), note: null, overdue: false }; if (['SHIPPED', 'UNDELIVERED'].includes(item.derivedStatus)) return { label: 'Taşıma durumunda', value: shipment?.statusOccurredAt ? longDate(shipment.statusOccurredAt) : 'Kargo zamanı bekleniyor', note: 'Kargoya teslim edildi', overdue: false }; if (item.derivedStatus === 'ON_HOLD') return { label: 'Sipariş durumu', value: onHoldOrderStatusText(item.platformCode, rawStatuses), note: null, overdue: false }; const dueAt = usableShipmentDueAt(item.shipmentDueAt); const overdueDays = overdueShipmentDays(item.derivedStatus, dueAt); if (overdueDays > 0) return { label: '', value: `Siparişiniz ${overdueDays} gün gecikmiştir!`, note: 'Siparişinizi en kısa sürede kargoya teslim etmelisiniz.', overdue: true }; const urgent = Boolean(dueAt && new Date(dueAt).getTime() - Date.now() <= 86_400_000); return { label: 'Kalan süre', value: dueAt ? remainingText(dueAt) : item.isMicroExport ? 'Trendyol termin bilgisi göndermedi' : 'Termin zamanı bekleniyor', note: null, overdue: urgent } }
function InvoiceInfoModal({ item, onClose }: { item: Order; onClose: () => void }) {
  const detail = useQuery({ queryKey: ['invoice-info-order', item.id], queryFn: () => hubApi<OrderDetail>(`/orders/${item.id}`) })
  const order = detail.data
  const missing = (value: string | null | undefined) => value ? value : 'Bilgi gelmedi'
  return <RrModal crumb={displayOrderNumber(item.orderNumber)} title="Fatura ve Adres Bilgileri" size="md" onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose}>Kapat</button><button type="button" className="rr-btn rr-primary" disabled={!order} onClick={() => window.print()}><RrIcon name="print" />Yazdır</button></>}>
    {detail.isLoading ? <RrLoading text="Fatura ve adres bilgileri yükleniyor…" /> : detail.isError || !order ? <RrError error={detail.error} onRetry={() => void detail.refetch()} /> : <>
      <div className="rr-two-cards"><section className="rr-dsec"><header><div className="rr-h3">Teslimat Adresi</div></header><dl className="rr-kv"><dt>Ad Soyad</dt><dd>{order.customerName}</dd><dt>Adres</dt><dd>{addressText(order.shipmentAddressJson, order.platformCode)}</dd></dl></section><section className="rr-dsec"><header><div className="rr-h3">Fatura Adresi</div></header><dl className="rr-kv"><dt>Ad Soyad</dt><dd>{order.customerName}</dd><dt>Adres</dt><dd>{addressText(order.invoiceAddressJson, order.platformCode)}</dd><dt>E-Fatura Mükellefi</dt><dd>{order.isEInvoiceAvailable === null ? 'Bilgi gelmedi' : order.isEInvoiceAvailable ? 'Evet' : 'Hayır'}</dd></dl></section></div>
      <dl className="rr-kv"><dt>E-posta Adresi</dt><dd className={order.customerEmail ? undefined : 'rr-empty'}>{missing(order.customerEmail)}</dd><dt>Telefon Numarası</dt><dd className={order.customerPhone ? 'rr-num' : 'rr-empty'}>{missing(order.customerPhone)}</dd></dl>
      <p className="rr-lead">Faturayı başka bir yerde kestiyseniz Fatura İşlemleri › Fatura Yükle ile siparişe ekleyin. PDF, JPEG, JPG veya PNG kabul edilir.</p>
    </>}
  </RrModal>
}
function InvoiceDraftModal({ item, providers, onClose, onFeedback }: { item: Order; providers: Connection[]; onClose: () => void; onFeedback: IntegrationFeedbackHandler }) {
  const client = useQueryClient()
  const detail = useQuery({ queryKey: ['invoice-draft-order', item.id], queryFn: () => hubApi<OrderDetail>(`/orders/${item.id}`) })
  const order = detail.data
  const marketplace = providers.find(connection => connection.id === order?.connectionId)
  const provider = invoiceProviderForEnvironment(providers, marketplace?.environment)
  const packageId = order ? orderDisplayShipment(order)?.id : undefined
  if (detail.isLoading) return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={onClose}><DialogSurface as="section" onClose={onClose} className="workspace-modal invoice-draft-modal" aria-label="Fatura önizlemesi" onMouseDown={event => event.stopPropagation()}><Busy text="Sipariş, paket ve fatura verileri okunuyor…" /></DialogSurface></div>
  if (detail.isError || !order) return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={onClose}><DialogSurface as="section" onClose={onClose} className="workspace-modal invoice-draft-modal" aria-label="Fatura önizlemesi" onMouseDown={event => event.stopPropagation()}><ErrorBox error={detail.error} onRetry={() => void detail.refetch()} /><button type="button" className="secondary" onClick={onClose}>Kapat</button></DialogSurface></div>
  if (!packageId || !provider) return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={onClose}><DialogSurface as="section" onClose={onClose} className="workspace-modal invoice-draft-modal" aria-labelledby="invoice-draft-blocked-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="invoice-draft-blocked-title">Fatura önizlemesi açılamadı</h2><p>{displayOrderNumber(order.orderNumber)}</p></div><button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><p className="notice" role="status">{!packageId ? 'Fatura için pazaryeri paket bilgisi henüz yok. Paket eşitlemesinden sonra yeniden deneyin.' : `Sipariş ortamında (${marketplace?.environment ?? 'bilinmiyor'}) etkin ve credential’ı olan E-Faturam bağlantısı bulunamadı.`}</p><footer><button type="button" className="secondary" onClick={onClose}>Kapat</button></footer></DialogSurface></div>
  const target: InvoicePreviewTarget = { orderId: order.id, packageId, providerConnectionId: provider.id }
  return <InvoiceWorkspacePreviewModal targets={[target]} onClose={onClose} onNotice={(text, kind) => onFeedback(text, kind)} onComplete={() => { void Promise.all([client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-summary'] })]) }} />
}
function InvoiceViewerModal({ item, onClose }: { item: Order; onClose: () => void }) {
  const invoiceId = item.invoiceId
  const detail = useQuery({ queryKey: ['invoice-viewer', invoiceId], queryFn: () => hubApi<InvoiceViewer>(`/invoices/${invoiceId}`), enabled: !!invoiceId })
  const invoice = detail.data
  const money = (value: number) => formatMoney(value, invoice?.currency ?? item.currency)
  return <RrModal crumb="Fatura" title={displayOrderNumber(item.orderNumber)} desc={`${invoice?.invoiceNumber ?? 'Numara bekleniyor'}${invoice ? ` · ${titleTr(statusLabel(invoice.invoiceType))}` : ''}`} size="md" onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose}>Kapat</button><button type="button" className="rr-btn rr-primary" disabled={!invoice} onClick={() => window.print()}><RrIcon name="print" />Yazdır</button></>}>
    {detail.isLoading ? <RrLoading text="Fatura yükleniyor…" /> : detail.isError || !invoice ? <RrError error={detail.error} onRetry={() => void detail.refetch()} /> : <>
      <div className="rr-facts"><div><small>Durum</small><RrBadge label={titleTr(statusLabel(invoice.status))} /></div><div><small>Fatura Tipi</small><b>{titleTr(statusLabel(invoice.invoiceType))}</b></div><div><small>Ödenecek</small><b className="rr-num">{money(invoice.payableTotal)}</b></div><div><small>Vergi</small><b className="rr-num">{money(invoice.taxTotal)}</b></div></div>
      <table className="rr-ltbl"><thead><tr><th>Ürün</th><th>Miktar</th><th>KDV</th><th>Toplam</th></tr></thead><tbody>{invoice.lines.map(line => <tr key={line.id}><td><b>{line.description}</b><small>{line.sku ?? 'Stok kodu yok'}</small></td><td className="rr-num">{line.quantity} {line.unit}</td><td className="rr-num">%{line.vatRate}</td><td className="rr-num"><b>{money(line.lineTotal)}</b></td></tr>)}</tbody></table>
      <div className="rr-h3">Belgeler</div>
      {invoice.documents.length ? <div style={{ display: 'grid', gap: 6, justifyItems: 'start' }}>{invoice.documents.map(document => <a key={document.id} className="rr-btn rr-link" href={`/api/v1/invoices/${invoice.id}/documents/${document.id}/content`} target="_blank" rel="noreferrer"><RrIcon name="doc" />{document.documentType === 'PDF' ? 'PDF Faturayı Aç' : 'Fatura Belgesini Aç'}<RrIcon name="open" /></a>)}</div> : <p className="rr-lead">Sağlayıcı belgesi henüz arşivlenmedi.</p>}
    </>}
  </RrModal>
}
function InvoiceUploadModal({ item, onClose, onFeedback }: { item: Order; onClose: () => void; onFeedback: IntegrationFeedbackHandler }) {
  const client = useQueryClient()
  const [file, setFile] = useState<File | null>(null)
  const [invoiceUrl, setInvoiceUrl] = useState('')
  const [invoiceNumber, setInvoiceNumber] = useState('')
  const [issuedAt, setIssuedAt] = useState('')
  const [message, setMessage] = useState('')
  const [savedDocumentId, setSavedDocumentId] = useState<string | null>(null)
  const [documentRequestKey, setDocumentRequestKey] = useState(() => `manual-invoice-document:${item.id}:${newIdempotencyKey()}`)
  const [dragging, setDragging] = useState(false)
  const detail = useQuery({ queryKey: ['invoice-upload-order', item.id], queryFn: () => hubApi<OrderDetail>(`/orders/${item.id}`) })
  const saveDocument = useMutation({
    mutationFn: async () => {
      if (Boolean(file) === Boolean(invoiceUrl.trim())) throw new Error('PDF dosyası veya HTTPS fatura bağlantısından yalnızca birini girin.')
      if (file && file.size > 10 * 1024 * 1024) throw new Error('Fatura dosyası en fazla 10 MB olabilir.')
      if (file && !/\.pdf$/i.test(file.name)) throw new Error('Yalnız PDF dosyası yükleyebilirsiniz.')
      const order = detail.data
      if (!order) throw new Error('Sipariş bilgileri henüz yüklenmedi.')
      if (!order.packages?.some(packageItem => packageItem.status !== 'CANCELLED')) throw new Error('Manuel fatura belgesi için siparişte etkin bir paket bulunamadı.')
      const form = new FormData()
      form.append('orderId', order.id)
      form.append('invoiceUrl', invoiceUrl.trim())
      form.append('invoiceNumber', invoiceNumber.trim())
      form.append('issuedAt', issuedAt ? fromDateTimeLocal(issuedAt) : '')
      if (file) form.append('file', file)
      return hubApi<{ id: string }>('/invoice-workspace/manual-documents', { method: 'POST', headers: { 'Idempotency-Key': documentRequestKey }, body: form })
    },
    onSuccess: async result => {
      setSavedDocumentId(result.id)
      const notice = 'Manuel belge siparişe kaydedildi. Pazaryerine henüz gönderilmedi; isterseniz aşağıdaki ayrı düğmeden iletebilirsiniz.'
      setMessage(notice)
      onFeedback('Manuel fatura belgesi kaydedildi; pazaryerine gönderim başlatılmadı.', 'success')
      await Promise.all([client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['invoice-viewer'] }), client.invalidateQueries({ queryKey: ['invoice-workspace'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-summary'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] })])
    },
    onError: error => { const errorMessage = error instanceof Error ? error.message : 'Manuel fatura belgesi kaydedilemedi.'; setMessage(errorMessage); onFeedback(errorMessage, 'error') }
  })
  const deliverDocument = useMutation({
    mutationFn: async () => {
      if (!savedDocumentId) throw new Error('Önce manuel fatura belgesini kaydedin.')
      if (!issuedAt) throw new Error('Platforma iletim için fatura tarihi gereklidir.')
      if (item.platformCode === 'SHOPIFY' && !invoiceNumber.trim()) throw new Error('Shopify siparişine iletim için fatura numarası gereklidir.')
      return hubApi<{ jobIds: string[]; queuedPackageCount: number; inspectionOnlyPackageCount: number }>(`/invoice-workspace/manual-documents/${savedDocumentId}/delivery-jobs`, { method: 'POST', headers: { 'Idempotency-Key': `manual-order-invoice-delivery:${savedDocumentId}:${newIdempotencyKey()}` } })
    },
    onSuccess: async result => {
      const notice = result.inspectionOnlyPackageCount > 0
        ? 'Önceki iletim sonucu belirsiz. Yalnızca pazaryeri durumu okunacak; belge tekrar gönderilmeyecek.'
        : `${result.queuedPackageCount} paket için manuel fatura iletimi kuyruğa alındı.`
      setMessage(notice)
      onFeedback(notice, 'info')
      await Promise.all([client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }), client.invalidateQueries({ queryKey: ['jobs'] })])
    },
    onError: error => { const errorMessage = error instanceof Error ? error.message : 'Manuel fatura iletimi kuyruğa alınamadı.'; setMessage(errorMessage); onFeedback(errorMessage, 'error') }
  })
  function changed() { setSavedDocumentId(null); setMessage(''); setDocumentRequestKey(`manual-invoice-document:${item.id}:${newIdempotencyKey()}`) }
  function choose(selected: File | undefined) { if (selected) { setFile(selected); setInvoiceUrl(''); changed() } }
  const hasOneSource = Boolean(file) !== Boolean(invoiceUrl.trim())
  const busy = saveDocument.isPending || deliverDocument.isPending
  const failed = deliverDocument.isError || saveDocument.isError
  return <RrModal title="Siparişe Manuel Fatura Belgesi Ekle" desc={`${displayOrderNumber(item.orderNumber)} · Kaydetmek mali fatura oluşturmaz ve platforma gönderim başlatmaz.`} size="md" locked={busy} onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose} disabled={busy}>Kapat</button><button type="button" className="rr-btn" disabled={!hasOneSource || saveDocument.isPending || detail.isLoading} onClick={() => saveDocument.mutate()}>{saveDocument.isPending ? 'Kaydediliyor…' : 'Belgeyi Kaydet'}</button><button type="button" className="rr-btn rr-primary" disabled={!savedDocumentId || !issuedAt || (item.platformCode === 'SHOPIFY' && !invoiceNumber.trim()) || deliverDocument.isPending} onClick={() => deliverDocument.mutate()} title={!savedDocumentId ? 'Önce belgeyi kaydedin.' : !issuedAt ? 'Fatura tarihi gereklidir.' : undefined}>{deliverDocument.isPending ? 'Kuyruğa alınıyor…' : 'Platforma İlet'}</button></>}>
    {detail.isError ? <RrError error={detail.error} onRetry={() => void detail.refetch()} /> : detail.isLoading ? <RrLoading text="Sipariş ve paket bilgileri yükleniyor…" /> : <>
      <div className="rr-dgrid"><label className="rr-fld"><small>Fatura Numarası</small><input className="rr-in" value={invoiceNumber} onChange={event => { setInvoiceNumber(event.target.value); changed() }} disabled={busy} maxLength={64} placeholder="RVC2026000000412" /></label><label className="rr-fld"><small>Fatura Tarihi</small><input className="rr-in" type="datetime-local" value={issuedAt} onChange={event => { setIssuedAt(event.target.value); changed() }} disabled={busy} /></label><label className="rr-fld rr-full"><small>Mevcut HTTPS Fatura Bağlantısı</small><input className="rr-in" type="url" value={invoiceUrl} onChange={event => { setInvoiceUrl(event.target.value); if (event.target.value) setFile(null); changed() }} disabled={busy} placeholder="https://…" /></label></div>
      <div className="rr-divider"><span>veya PDF dosyası</span></div>
      <label className={`rr-drop${dragging ? ' rr-on' : ''}`} onDragEnter={event => { event.preventDefault(); setDragging(true) }} onDragOver={event => event.preventDefault()} onDragLeave={() => setDragging(false)} onDrop={event => { event.preventDefault(); setDragging(false); choose(event.dataTransfer.files[0]) }}><input type="file" hidden accept=".pdf,application/pdf" disabled={busy} onChange={event => choose(event.target.files?.[0])} /><RrIcon name="upload" /><b>{file ? file.name : 'PDF Dosyasını Seçin'}</b><small>{file ? `${(file.size / 1024 / 1024).toFixed(2)} MB` : 'PDF · en fazla 10 MB'}</small><span className="rr-btn" style={{ marginTop: 6 }}>Dosya Seç</span></label>
    </>}
    {message && <div className={`rr-callout${failed ? ' rr-danger' : ''}`} role={failed ? 'alert' : 'status'}><RrIcon name={failed ? 'alert' : 'info'} /><span>{message}</span></div>}
  </RrModal>
}
function ShopifyInvoiceStatusModal({ item, onClose, onFeedback }: { item: Order; onClose: () => void; onFeedback: IntegrationFeedbackHandler }) {
  const client = useQueryClient()
  const [status, setStatus] = useState<'PENDING' | 'UPLOADED'>(item.invoiceStatus === 'FATURA_YUKLENDI' ? 'UPLOADED' : 'PENDING')
  const [message, setMessage] = useState('')
  const detail = useQuery({ queryKey: ['invoice-status-order', item.id], queryFn: () => hubApi<OrderDetail>(`/orders/${item.id}`) })
  const update = useMutation({
    mutationFn: async () => {
      if (!detail.data || detail.data.platformCode !== 'SHOPIFY') throw new Error('Manuel fatura durumu yalnız Shopify siparişlerinde kullanılabilir.')
      const packageId = orderDisplayShipment(detail.data)?.id
      if (!packageId) throw new Error('Fatura durumu için Shopify sipariş paketi bulunamadı.')
      if (status === 'UPLOADED' && !item.invoiceDocumentUrl) throw new Error('Faturası yüklendi durumuna geçmeden önce mevcut yerel fatura kaydına belge ekleyin.')
      return setManualInvoiceStatus([packageId], status)
    },
    onSuccess: async () => {
      const successMessage = status === 'UPLOADED' ? 'Fatura durumu “Faturası yüklendi” olarak güncellendi.' : 'Fatura durumu “Fatura bekliyor” olarak güncellendi.'
      onFeedback(successMessage, 'success')
      await Promise.all([client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['invoice-viewer'] }), client.invalidateQueries({ queryKey: ['invoice-workspace'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-summary'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] })])
      onClose()
    },
    onError: error => {
      const errorMessage = error instanceof Error ? error.message : 'Fatura durumu güncellenemedi.'
      setMessage(errorMessage)
      onFeedback(errorMessage, 'error')
    }
  })
  return <RrModal crumb="Shopify" title="Fatura Durumunu Değiştir" desc={`${displayOrderNumber(item.orderNumber)} · Bu değişiklik yalnızca paneldeki paket durumunu günceller.`} size="sm" locked={update.isPending} onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose} disabled={update.isPending}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => update.mutate()} disabled={update.isPending || !detail.data || (status === 'UPLOADED' && !item.invoiceDocumentUrl)} title={status === 'UPLOADED' && !item.invoiceDocumentUrl ? 'Önce mevcut fatura kaydına belge ekleyin.' : undefined}>{update.isPending ? 'Kaydediliyor…' : 'Durumu Kaydet'}</button></>}>
    {detail.isLoading ? <RrLoading text="Sipariş ve fatura bilgileri yükleniyor…" /> : detail.isError || !detail.data ? <RrError error={detail.error} onRetry={() => void detail.refetch()} /> : <>
      <label className="rr-fld"><small>Yeni Fatura Durumu</small><select className="rr-in" value={status} onChange={event => setStatus(event.target.value as 'PENDING' | 'UPLOADED')} disabled={update.isPending}><option value="PENDING">Fatura Bekliyor</option><option value="UPLOADED">Faturası Yüklendi</option></select></label>
      {status === 'UPLOADED' && !item.invoiceDocumentUrl && <div className="rr-callout rr-warn" role="status"><RrIcon name="alert" /><span>Yüklenen belgeyle ilişkilendirilmiş yerel fatura bağlantısı yok. Önce belgeyi mevcut fatura kaydına ekleyin.</span></div>}
      {message && <div className="rr-callout rr-danger" role="alert"><RrIcon name="alert" /><span>{message}</span></div>}
    </>}
  </RrModal>
}
function customerDisplayName(item: Order) {
  if (item.customerName && item.customerName !== '—' && item.customerName.trim() !== '') return item.customerName
  // The server already resolves the name from the customer, invoice and delivery snapshots.
  const shipName = item.shipmentAddress?.fullName
  return shipName && meaningfulText(shipName) ? shipName.trim() : '—'
}
function patchOrderShipment(order: Order, updatedShipment: Shipment): Order {
  const packages = (order.packages?.map(packageItem => packageItem.id === updatedShipment.id ? { ...packageItem, ...updatedShipment } : packageItem) ?? [updatedShipment]).sort((left, right) => new Date(right.statusOccurredAt).getTime() - new Date(left.statusOccurredAt).getTime())
  const primaryPackage = orderDisplayShipment({ ...order, packages })
  return { ...order, packages, derivedStatus: aggregateOrderStatus(packages.map(item => item.status)), cargoProviderName: primaryPackage?.cargoProviderName ?? order.cargoProviderName, cargoTrackingNumber: primaryPackage?.cargoTrackingNumber ?? order.cargoTrackingNumber }
}
function moveOrderAcrossStatusCaches(client: ReturnType<typeof useQueryClient>, order: Order, updatedShipment: Shipment) {
  const updatedOrder = patchOrderShipment(order, updatedShipment)
  client.setQueriesData<Page<Order>>({ queryKey: ['orders', 'page'] }, current => current ? { ...current, items: current.items.map(item => item.id === order.id ? updatedOrder : item) } : current)
  return updatedOrder
}
// The panel changes a package's carrier only on Trendyol (the server refuses the others with
// CARGO_PROVIDER_CHANGE_UNSUPPORTED), and only before the package is handed to the carrier.
function canChangeOrderCargo(item: Pick<Order, 'platformCode' | 'derivedStatus'>) {
  return item.platformCode.trim().toUpperCase() === 'TRENDYOL' && !['PAYMENT_PENDING', 'SHIPPED', 'UNDELIVERED', 'DELIVERED', 'RETURNED', 'RETURN_IN_TRANSIT', 'CANCELLED'].includes(item.derivedStatus.trim().toUpperCase())
}
function CourierChangeModal({ item, items, onClose, onConfirmed }: { item: Order; items?: Order[]; onClose: () => void; onConfirmed: (shipments: Shipment[]) => void }) {
  // Packages of other marketplaces are left out before anything is sent.
  const targets = (items?.length ? items : [item]).filter(canChangeOrderCargo).flatMap(order => { const shipment = orderDisplayShipment(order); return shipment ? [{ order, shipment }] : [] })
  const shipment = orderDisplayShipment(item)
  const currentCarrier = cargoCarriers.find(carrier => cargoMatches(shipment?.cargoProviderName ?? item.cargoProviderName, carrier))?.label ?? cargoCarriers[0].label
  const [selectedCarrier, setSelectedCarrier] = useState<string>(currentCarrier)
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [errorMsg, setErrorMsg] = useState('')
  const [progressMsg, setProgressMsg] = useState('')
  const client = useQueryClient()

  async function handleCarrierChange() {
    if (!targets.length) {
      setErrorMsg('Paket kaydı bulunamadı.')
      return
    }
    const chosen = cargoCarriers.find(c => c.label === selectedCarrier)
    if (!chosen) return
    setIsSubmitting(true)
    setErrorMsg('')
    setProgressMsg('Kargo firması Trendyol’a anlık olarak gönderiliyor…')
    // Every package is tried; one refused package does not stop the others, and the result says
    // exactly which changed, which are still waiting for Trendyol and which were refused.
    const updatedShipments: Shipment[] = []
    const pending: string[] = []
    const failed: Array<{ orderNumber: string; message: string }> = []
    try {
      for (const target of targets) {
        try {
          const updatedShipment = await hubApi<Shipment>(`/shipments/${target.shipment.id}/instant-cargo-provider`, {
            method: 'POST',
            headers: { 'Idempotency-Key': `instant-cargo:${target.shipment.id}:v${target.shipment.version}:${chosen.code}`, 'If-Match': `"v${target.shipment.version}"` },
            body: JSON.stringify({ action: 'CHANGE_CARGO_PROVIDER', payloadJson: JSON.stringify({ cargoProvider: chosen.code }) })
          })
          updatedShipments.push(updatedShipment)
        } catch (err) {
          if (isPendingAction(err)) pending.push(err.message)
          else failed.push({ orderNumber: displayOrderNumber(target.order.orderNumber), message: err instanceof Error ? err.message : 'Bilinmeyen hata.' })
        }
      }
      if (updatedShipments.length) onConfirmed(updatedShipments)
      void client.invalidateQueries({ queryKey: ['orders'] })
      if (!failed.length && !pending.length) { onClose(); return }
      const changed = updatedShipments.length ? `${updatedShipments.length} paket güncellendi. ` : ''
      if (failed.length) {
        const first = failed[0]
        setErrorMsg(targets.length === 1
          ? `Kargo firması güncellenemedi: ${first.message}`
          : `${changed}${failed.length} paket güncellenemedi (ilki ${first.orderNumber}: ${first.message})${pending.length ? `; ${pending.length} paket Trendyol yanıtını bekliyor` : ''}.`)
      } else setProgressMsg(targets.length === 1 ? pending[0] : `${changed}${pending.length} paket Trendyol yanıtını bekliyor; sonuç geldiğinde panel güncellenir.`)
    } finally {
      setIsSubmitting(false)
    }
  }

  const currentLabel = cargoCarriers.find(carrier => cargoMatches(shipment?.cargoProviderName ?? item.cargoProviderName, carrier))?.label
  return <RrModal crumb={targets.length > 1 ? `${targets.length} seçili paket` : displayOrderNumber(item.orderNumber)} title="Paketi Hangi Firma ile Göndermek İstiyorsunuz?" desc="İstek doğrudan Trendyol’a gönderilir; onay gelirse panel anında güncellenir." size="lg" locked={isSubmitting} onClose={onClose} foot={<><span className="rr-note">Seçili: <b>{selectedCarrier}</b></span><button type="button" className="rr-btn" onClick={onClose} disabled={isSubmitting}>Vazgeç</button><button type="button" className="rr-btn rr-primary" disabled={isSubmitting || !targets.length} onClick={handleCarrierChange}>{isSubmitting ? 'İşleniyor…' : 'İşlemi Yap'}</button></>}>
    {targets.length > 1 && <div className="rr-callout"><RrIcon name="info" /><span><b>{targets.length} paket değiştirilecek.</b> Seçtiğiniz kargo firması uygun tüm seçili paketlere uygulanır.</span></div>}
    <div className="rr-h3">Standart Kargo Firmaları (0–30 Desi)</div>
    <div className="rr-cgrid" role="radiogroup">{cargoCarriers.map(carrier => { const icon = cargoCarrier(carrier.label)?.iconUrl; return <label key={carrier.code} className={`rr-ccard${selectedCarrier === carrier.label ? ' rr-on' : ''}`}><input type="radio" name="courier" value={carrier.label} checked={selectedCarrier === carrier.label} onChange={() => setSelectedCarrier(carrier.label)} /><span className="rr-ci">{icon ? <img className="rr-cargo-img" src={icon} alt="" referrerPolicy="no-referrer" /> : <span className="rr-cmono">{carrier.label.split(' ').map(word => word[0]).join('').slice(0, 2).toLocaleUpperCase('tr-TR')}</span>}</span><b>{carrier.label}</b>{carrier.label === currentLabel && <small>Şu anki firma</small>}</label> })}</div>
    {progressMsg && !errorMsg && <div className="rr-callout" role="status"><RrIcon name="clock" size={16} /><span>{progressMsg}</span></div>}
    {errorMsg && <div className="rr-callout rr-danger" role="alert"><RrIcon name="alert" /><span>{errorMsg}</span></div>}
  </RrModal>
}
function LabelBarcode({ value, compact = false }: { value: string; compact?: boolean }) {
  const bars = code128Bars(value)
  return <div className={`shipping-label-barcode${compact ? ' compact' : ''}`} aria-label={`Barkod: ${value || 'Takip numarası bekleniyor'}`}><div className="shipping-label-barcode-bars" style={{ '--barcode-module-count': bars.length } as CSSProperties}>{bars.map((isBar, index) => <i className={isBar ? 'is-bar' : undefined} key={index} />)}</div><strong>{value || 'Takip numarası bekleniyor'}</strong></div>
}
function ShippingLabelModal({ item, settings, format, onClose, onPrinted, inline = false, detail: suppliedDetail }: { item: Order; settings: ShippingLabelSettings; format?: ShippingLabelFormat; onClose: () => void; onPrinted?: () => void; inline?: boolean; detail?: OrderLabelDetail }) {
  format = format === 'sticker' ? 'sticker' : settings.defaultFormat
  // The buyer's e-mail (and name and address) are read from the order detail when the label is printed.
  const detailQuery = useQuery({ queryKey: ['order-label-detail', item.id], queryFn: () => hubApi<OrderLabelDetail>(`/orders/${item.id}`), enabled: !inline && !suppliedDetail, staleTime: 30_000 })
  const detail = suppliedDetail ?? detailQuery.data
  const personal = labelPersonalData(item, detail)
  const shipment = orderDisplayShipment(item)
  const trackingNumber = shipment?.cargoTrackingNumber ?? item.cargoTrackingNumber ?? ''
  const packageNumber = shipment?.externalPackageId ?? '—'
  const address = listAddressText(personal.shipmentAddress, item.platformCode)
  const addressLines = address.split(' · ').filter(Boolean)
  const senderLines = settings.senderAddress.split(/\r?\n| · /).map(value => value.trim()).filter(Boolean)
  const style = { '--shipping-label-width': `${settings.stickerWidthMm}mm`, '--shipping-label-height': `${settings.stickerHeightMm}mm`, '--shipping-label-gap': `${settings.sectionGapMm}mm` } as CSSProperties
  const fieldValues: Record<ShippingLabelField, string> = {
    trackingNumber,
    packageNumber,
    orderNumber: displayOrderNumber(item.orderNumber),
    customerName: customerDisplayName({ ...item, customerName: personal.customerName, shipmentAddress: personal.shipmentAddress }),
    address,
    cargoProvider: cargoLabel(shipment?.cargoProviderName ?? item.cargoProviderName),
    senderName: settings.senderName,
    senderAddress: settings.senderAddress,
    customerEmail: personal.customerEmail
  }
  function fieldLabel(field: ShippingLabelField) { return shippingLabelFields.find(option => option.id === field)?.label ?? field }
  function renderField(field: ShippingLabelField) {
    if (field === 'address' || field === 'senderAddress') {
      const lines = field === 'address' ? addressLines : senderLines
      return lines.length ? lines.map((line, index) => <span key={`${field}-${line}-${index}`}>{line}</span>) : <span>—</span>
    }
    return <span key={field}><small>{fieldLabel(field)}</small><b>{fieldValues[field] || '—'}</b></span>
  }
  function renderLabelBlock(block: ShippingLabelBlock) {
    const barcodeField = block.kind === 'trackingBarcode' ? 'trackingNumber' : block.kind === 'packageBarcode' ? 'packageNumber' : null
    if (barcodeField && block.fields.includes(barcodeField)) {
      const extraFields = block.fields.filter(field => field !== barcodeField)
      return <div className="shipping-label-block-content" style={{ textAlign: block.align }}><LabelBarcode value={fieldValues[barcodeField] || (barcodeField === 'packageNumber' ? item.orderNumber : '')} compact={format === 'sticker'} />{extraFields.map(field => renderField(field))}</div>
    }
    const fields = block.fields.filter(field => settings.showCustomerPhone || field !== 'customerEmail')
    // The address block prints its own fields, the recipient's name first (audit 2026-10-20 S1).
    if (block.kind === 'address') return <div className="shipping-label-address-box" style={{ textAlign: block.align }}><strong className="shipping-label-block-title">Teslimat adresi</strong>{addressBlockFields(fields).map(field => <Fragment key={field}>{renderField(field)}</Fragment>)}</div>
    if (block.kind === 'orderInfo') return <div className="shipping-label-meta" style={{ textAlign: block.align }}>{block.text && <strong>{block.text}</strong>}{fields.map(field => renderField(field))}</div>
    if (block.kind === 'sender') return <div className="shipping-label-footer" style={{ textAlign: block.align }}>{block.text && <strong>{block.text}</strong>}{fields.map(field => renderField(field))}</div>
    return <div className="shipping-label-custom-content" style={{ textAlign: block.align }}><strong className="shipping-label-block-title">{block.title}</strong>{block.text && <strong>{block.text}</strong>}{fields.map(field => renderField(field))}</div>
  }
  const labelSurface = <article className={`shipping-label-print-surface shipping-label-${format} a4-count-${settings.a4LabelsPerPage}`} style={style}>
    {settings.layout[format].map(block => <div key={block.id} className="shipping-label-positioned-block" style={block.position ? { left: `${block.position.x}%`, top: `${block.position.y}%`, width: `${block.position.width}%`, height: `${block.position.height}%`, fontSize: `${block.fontSize ?? 14}px`, color: '#000', transform: block.rotation ? `rotate(${block.rotation}deg)` : undefined, transformOrigin: 'center' } : { fontSize: `${block.fontSize ?? 14}px`, color: '#000' }}><Fragment>{renderLabelBlock(block)}</Fragment></div>)}
  </article>
  if (inline) return labelSurface
  const detailReady = Boolean(detail)
  return <RrModal crumb="Yazdırma Önizlemesi" title={format === 'a4' ? 'A4 Kargo Etiketi' : 'Sticker Kargo Etiketi'} desc={`${displayOrderNumber(item.orderNumber)} · ${cargoLabel(shipment?.cargoProviderName ?? item.cargoProviderName)}`} size="md" layerClass="shipping-label-backdrop" modalClass={`shipping-label-modal format-${format}`} onClose={onClose} foot={<><span className="rr-note">Etiket bilgileri sipariş ve paket kaydından dolduruldu.</span><button type="button" className="rr-btn" onClick={onClose}>Vazgeç</button><button type="button" className="rr-btn rr-primary" disabled={!detailReady} onClick={() => { window.print(); recordShippingLabelsPrinted([item.id], format, () => onPrinted?.()) }}><RrIcon name="print" />Yazdır</button></>}>
    <style>{shippingLabelPageCss(format, settings)}</style>
    {detailQuery.isError && !detailReady ? <RrError error={detailQuery.error} onRetry={() => void detailQuery.refetch()} /> : !detailReady ? <RrLoading text="Alıcı bilgileri yükleniyor…" /> : <div className="shipping-label-preview-wrap rr-label-wrap">{labelSurface}</div>}
  </RrModal>
}
// After the print dialog closed (window.print blocks until then): the local mark, and the server keeps
// the first print per order so every computer sees it, for a single label too (audit 2026-10-20 S8).
function recordShippingLabelsPrinted(ids: readonly string[], format: ShippingLabelFormat, onPrinted: () => void) {
  ids.forEach(id => markShippingLabelPrinted(id, format))
  onPrinted()
  void Promise.allSettled(ids.map(id => hubApi<void>(`/orders/${id}/label-printed`, { method: 'POST' }))).then(() => onPrinted())
}
function ShippingLabelBatchModal({ items, settings, format, onClose, onPrinted }: { items: Order[]; settings: ShippingLabelSettings; format: ShippingLabelFormat; onClose: () => void; onPrinted: () => void }) {
  // Each selected order's detail is read (a few at a time) for the personal fields the labels print.
  const ids = items.map(item => item.id)
  const details = useQuery({ queryKey: ['order-label-details', ids], queryFn: () => loadOrderLabelDetails(ids, id => hubApi<OrderLabelDetail>(`/orders/${id}`)), staleTime: 30_000 })
  function printLabels() {
    if (!details.data) return
    window.print()
    recordShippingLabelsPrinted(items.map(item => item.id), format, onPrinted)
  }
  return <RrModal crumb="Toplu Yazdırma" title="Kargo Stickerlarını Yazdır" desc={`${items.length} etiket · Takip numarası bulunan seçili paketler`} size="md" layerClass="shipping-label-backdrop" modalClass={`shipping-label-modal shipping-label-batch-modal format-${format}`} onClose={onClose} foot={<><span className="rr-note">{details.data ? `${items.length} etiket yazdırılmaya hazır.` : details.isError ? 'Alıcı bilgileri alınamadı.' : 'Alıcı bilgileri yükleniyor…'}</span><button type="button" className="rr-btn" onClick={onClose}>Vazgeç</button><button type="button" className="rr-btn rr-primary" disabled={!details.data} onClick={printLabels}><RrIcon name="print" />Yazdır</button></>}>
    <style>{shippingLabelPageCss(format, settings)}</style>
    {details.isError && <RrError error={details.error} onRetry={() => void details.refetch()} />}
    <div className="rr-sum rr-screen-only">{items.map(item => { const shipment = orderDisplayShipment(item); return <div key={item.id}><RrLogo code={item.platformCode} /><span>{displayOrderNumber(item.orderNumber)} · {customerDisplayName(item)}<small>{cargoLabel(shipment?.cargoProviderName ?? item.cargoProviderName)} · {shipment?.cargoTrackingNumber ?? item.cargoTrackingNumber ?? '—'}</small></span><RrBadge label={(['a4', 'sticker'] as ShippingLabelFormat[]).some(value => loadPrintedShippingLabels().includes(printedShippingLabelKey(item.id, value))) ? 'Yazdırıldı' : 'Bekliyor'} glyph={undefined} /></div> })}</div>
    {details.data && <div className="shipping-label-batch-preview rr-print-only">{items.map(item => <ShippingLabelModal key={item.id} item={item} settings={settings} format={format} onClose={onClose} inline detail={details.data.get(item.id)} />)}</div>}
  </RrModal>
}
function SingleOrderSyncModal({ activeConnection: activeConnections, onClose, onSuccess }: { activeConnection: Connection[]; onClose: () => void; onSuccess: (connectionCount: number, orderNumber: string) => void }) {
  const [orderNumber, setOrderNumber] = useState('')
  const [packageNumber, setPackageNumber] = useState('')
  const [selectedConnectionIds, setSelectedConnectionIds] = useState<string[]>(() => activeConnections.map(connection => connection.id))
  const [connectionsInitialized, setConnectionsInitialized] = useState(() => activeConnections.length > 0)
  const [syncMode, setSyncMode] = useState<'changes' | 'single'>('changes')
  const [fullScan, setFullScan] = useState(false)
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [errorMsg, setErrorMsg] = useState('')
  const selectedSyncConnection = selectedConnectionIds.length === 1
    ? activeConnections.find(connection => connection.id === selectedConnectionIds[0])
    : undefined
  const supportsPackageLookup = selectedSyncConnection?.platformCode === 'TRENDYOL' || selectedSyncConnection?.platformCode === 'HEPSIBURADA'

  useEffect(() => {
    if (connectionsInitialized || !activeConnections.length) return
    setSelectedConnectionIds(activeConnections.map(connection => connection.id))
    setConnectionsInitialized(true)
  }, [activeConnections, connectionsInitialized])

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    const trimmed = orderNumber.trim()
    const selectedConnections = activeConnections.filter(connection => selectedConnectionIds.includes(connection.id))
    if (syncMode === 'single' && !trimmed) {
      setErrorMsg('Tekil sipariş çekmek için sipariş numarası girin.')
      return
    }
    if (!selectedConnections.length) {
      setErrorMsg('En az bir aktif bağlantı seçin.')
      return
    }
    const packageLookup = syncMode === 'single'
      && selectedConnections.length === 1
      && ['TRENDYOL', 'HEPSIBURADA'].includes(selectedConnections[0].platformCode)
      && Boolean(packageNumber.trim())
    setIsSubmitting(true)
    setErrorMsg('')
    try {
      await Promise.all(selectedConnections.map(connection => hubApi(`/connections/${connection.id}/order-sync-jobs`, {
        method: 'POST',
        headers: { 'Idempotency-Key': newIdempotencyKey() },
        body: JSON.stringify({ externalOrderId: syncMode === 'single' ? trimmed : null, full: syncMode === 'changes' && fullScan, packageNumber: packageLookup ? packageNumber.trim() : null })
      })))
      onSuccess(selectedConnections.length, trimmed)
      onClose()
    } catch (err) {
      setErrorMsg(err instanceof Error ? err.message : 'Sipariş senkronizasyonu başlatılamadı. Seçili bağlantıların durumunu kontrol edin.')
    } finally {
      setIsSubmitting(false)
    }
  }


  return <RrModal crumb="Sipariş Kaynakları" title="Sipariş Senkronizasyonu" desc="İşaretlediğiniz aktif bağlantılardan siparişleri panele alın." size="md" locked={isSubmitting} onClose={onClose} foot={<><span className="rr-note">{selectedConnectionIds.length} bağlantı seçili</span><button type="button" className="rr-btn" onClick={onClose} disabled={isSubmitting}>İptal</button><button type="submit" form="order-sync-form" className="rr-btn rr-primary" disabled={isSubmitting || !selectedConnectionIds.length}><RrIcon name="refresh" />{isSubmitting ? 'Senkronize ediliyor…' : syncMode === 'single' ? 'Tekil Siparişi Çek' : 'Siparişleri Çek'}</button></>}>
    <form id="order-sync-form" style={{ display: 'contents' }} onSubmit={handleSubmit}>
      {!activeConnections.length && <div className="rr-callout rr-warn" role="alert"><RrIcon name="alert" /><span>Aktif Trendyol, Shopify, Hepsiburada veya N11 bağlantısı bulunamadı. Entegrasyonlar sayfasından bağlantınızı etkinleştirin.</span></div>}
      <div className="rr-h3">Sipariş Kaynakları</div>
      <div className="rr-srcs">{activeConnections.map(connection => { const on = selectedConnectionIds.includes(connection.id); return <label key={connection.id} className={`rr-src${on ? ' rr-on' : ''}`}><input type="checkbox" className="rr-cb" checked={on} onChange={event => setSelectedConnectionIds(current => event.target.checked ? [...current, connection.id] : current.filter(id => id !== connection.id))} /><RrLogo code={connection.platformCode} /><span><b>{connection.displayName}</b><small>{marketplacePlatformLabel(connection.platformCode)} · {connection.environment} · Mağaza {connection.externalStoreId}</small></span></label> })}</div>
      <div className="rr-seg" role="group" aria-label="Senkronizasyon türü"><button type="button" className={syncMode === 'changes' ? 'rr-on' : undefined} onClick={() => { setSyncMode('changes'); setErrorMsg('') }} disabled={isSubmitting}>Yeni Siparişleri Çek</button><button type="button" className={syncMode === 'single' ? 'rr-on' : undefined} onClick={() => { setSyncMode('single'); setErrorMsg('') }} disabled={isSubmitting}>Tekil Sipariş Çek</button></div>
      {syncMode === 'single' ? <div className="rr-dgrid"><label className="rr-fld"><small>Pazar Yeri Sipariş Numarası</small><input className="rr-in" type="text" value={orderNumber} onChange={e => setOrderNumber(e.target.value)} placeholder="Örn. 1014529381 veya Shopify sipariş adı" disabled={isSubmitting} data-autofocus /></label>{selectedSyncConnection && supportsPackageLookup && <label className="rr-fld"><small>Paket Numarası (İsteğe Bağlı)</small><input className="rr-in" type="text" value={packageNumber} onChange={e => setPackageNumber(e.target.value)} placeholder={selectedSyncConnection.platformCode === 'TRENDYOL' ? 'Örn. 419428742' : 'Örn. 5435694424'} disabled={isSubmitting} /><em>{selectedSyncConnection.platformCode === 'TRENDYOL' ? 'Sipariş numarası bulunamazsa bu paket numarasıyla salt okunur olarak okunur.' : 'Eski teslimat durumunu salt okunur olarak yeniler.'}</em></label>}</div> : <RrHintCheck checked={fullScan} onChange={setFullScan} label="Erişilebilir tüm siparişleri tara" hint="Kapalıyken yalnız yeni değişiklikler ve güncellemeler alınır." />}
      {errorMsg && <div className="rr-callout rr-danger" role="alert"><RrIcon name="alert" /><span>{errorMsg}</span></div>}
    </form>
  </RrModal>
}
function OrderReferenceRow({ item, selected, onSelect, selectionMode, onMenuChange, onInvoiceCreate, onInvoiceDetails, onInvoiceUpload, onCourierChange, onOrderStatusChange, onProcessOrder, onCreatePackage, processing, onPrintLabel, onPreviewImage, labelSettings, printedLabels, noBuyer = false }: { item: Order; selected: boolean; onSelect: (checked: boolean) => void; selectionMode: boolean; openMenu: 'invoice' | 'actions' | null; onMenuChange: (value: 'invoice' | 'actions' | null) => void; onInvoiceCreate: () => void; onInvoiceDetails: () => void; onInvoiceUpload: () => void; onCourierChange: () => void; onOrderStatusChange: () => void; onProcessOrder: () => void; onCreatePackage: () => void; processing?: boolean; onPrintLabel: (format: ShippingLabelFormat) => void; onPreviewImage: (preview: ProductImagePreview) => void; labelSettings: ShippingLabelSettings; printedLabels: Set<string>; noBuyer?: boolean }) {

  const lines = item.lines ?? []
  const shipment = orderDisplayShipment(item)
  const invoiceNeedsAction = isInvoicePending(item)
  const invoiceCreationEnabled = item.invoiceCreationEnabled ?? false
  const invoiceCreationTooltip = invoiceCreationDisabledHelp
  const labelBlockedByInvoice = isMicroExportLabelBlocked(item)
  const [copied, setCopied] = useState(false)
  const [invoiceStatusModalOpen, setInvoiceStatusModalOpen] = useState(false)
  const money = (value: number) => formatMoney(value, item.currency)
  const delivery = deliveryText(item)
  const normalizedOrderStatus = item.derivedStatus.toUpperCase()
  const isShopifyOrder = item.platformCode === 'SHOPIFY'
  const isHepsiburadaOrder = item.platformCode === 'HEPSIBURADA'
  const isHepsiburadaUnverifiedWithoutPackage = item.derivedStatus === 'UNVERIFIED' && item.platformCode.trim().toUpperCase() === 'HEPSIBURADA' && (item.packages?.length ?? 0) === 0
  const hepsiburadaPackageHistoryExpired = isHepsiburadaUnverifiedWithoutPackage && Date.parse(item.orderedAt) < hepsiburadaPackageStatusHistoryCutoff().getTime()
  const isCancelledOrder = normalizedOrderStatus === 'CANCELLED'
  const saleTotal = item.grossAmount || item.netAmount
  const hasDiscount = item.discountAmount > 0
  const invoiceableTotal = Math.max(0, saleTotal - item.discountAmount)
  const isCargoTrackingStage = ['SHIPPED', 'UNDELIVERED'].includes(normalizedOrderStatus)
  const isCargoLabelStage = ['NEW', 'PROCESSING', 'READY_TO_SHIP'].includes(normalizedOrderStatus)
  const canChangeCargo = canChangeOrderCargo(item)
  const enabledLabelFormats: ShippingLabelFormat[] = [
    ...(labelSettings.showA4Button ? ['a4' as const] : []),
    ...(labelSettings.showStickerButton ? ['sticker' as const] : [])
  ]
  async function copyOrderNumber() { try { if (!await copyText(item.orderNumber)) throw new Error('copy'); setCopied(true); window.setTimeout(() => setCopied(false), 1400) } catch { setCopied(false) } }
  const rowRef = useRef<HTMLTableRowElement>(null)
  // Each unit price sits level with its product line (desktop table only).
  useLayoutEffect(() => {
    const row = rowRef.current
    if (!row) return
    const align = () => { const products = row.querySelectorAll<HTMLElement>('.rr-pline'), prices = row.querySelectorAll<HTMLElement>('.rr-pr'); const table = window.innerWidth > 1100; prices.forEach((price, index) => { price.style.height = table && products[index] ? `${products[index].getBoundingClientRect().height}px` : '' }) }
    align()
    if (typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(align)
    row.querySelectorAll('.rr-pline').forEach(element => observer.observe(element))
    return () => observer.disconnect()
  }, [lines.length, noBuyer])
  const [detailOpen, setDetailOpen] = useState(false)
  const [detailActionsOpen, setDetailActionsOpen] = useState(false)
  // The order detail sheet covers the page on phones only; there the page behind it holds still.
  const phone = useMediaQuery('(max-width: 760px)')
  useScrollLock(detailOpen && phone)
  const packageNoText = shipment?.externalPackageId ?? (isHepsiburadaUnverifiedWithoutPackage ? 'Paket bilgisi doğrulanmadı' : isHepsiburadaOrder ? 'Henüz paketlenmedi' : 'Bekleniyor')
  const deliveryNoText = shipment?.cargoTrackingNumber ?? item.cargoTrackingNumber ?? (isHepsiburadaUnverifiedWithoutPackage ? 'Gönderi bilgisi doğrulanmadı' : isHepsiburadaOrder ? shipment ? 'Kargoya verildiğinde gelir' : 'Paket oluşturulunca gelir' : 'Takip no bekleniyor')
  const closeDetailThen = (run: () => void) => () => { setDetailOpen(false); setDetailActionsOpen(false); run() }
  const detailPrimaryAction = isCancelledOrder ? null : isCargoTrackingStage ? (shipment?.cargoTrackingNumber ? <Link className="rr-btn rr-primary" to={`/shipments/${shipment.id}`}>Kargo Takip</Link> : null) : isCargoLabelStage && shipment ? <>{enabledLabelFormats.map(format => <button type="button" key={format} className="rr-btn rr-primary" disabled={labelBlockedByInvoice} onClick={closeDetailThen(() => onPrintLabel(format))}><RrIcon name="print" />{format === 'a4' ? 'A4' : 'Sticker'} Etiketi Yazdır</button>)}</> : null
  const detailActions: Array<{ key: string; label: string; disabled?: boolean; run: () => void }> = isCancelledOrder ? [] : [
    ...(isShopifyOrder ? [{ key: 'status', label: 'Sipariş Durumunu Değiştir', run: onOrderStatusChange }] : normalizedOrderStatus === 'NEW' ? [{ key: 'process', label: processing ? 'İşleniyor…' : 'İşleme Al', disabled: processing, run: isHepsiburadaOrder ? onCreatePackage : onProcessOrder }] : []),
    ...(isHepsiburadaOrder && !['SHIPPED', 'UNDELIVERED', 'DELIVERED', 'RETURNED'].includes(normalizedOrderStatus) ? [{ key: 'package', label: 'Paket Oluştur', run: onCreatePackage }] : []),
    ...(canChangeCargo ? [{ key: 'courier', label: 'Başka Kargo Firması İle Gönder', run: onCourierChange }] : []),
    ...(invoiceNeedsAction ? [{ key: 'invoice-create', label: 'Fatura Oluştur', disabled: !invoiceCreationEnabled, run: onInvoiceCreate }] : []),
    { key: 'invoice-info', label: 'Fatura Bilgileri', run: onInvoiceDetails },
    ...(invoiceNeedsAction ? [{ key: 'invoice-upload', label: 'Fatura Yükle', disabled: !invoiceCreationEnabled && !item.invoiceId, run: onInvoiceUpload }] : [])
  ]
  const detailQuickAction = detailActions.find(action => action.key === 'process')
  const detailMenuActions = detailActions.filter(action => action !== detailQuickAction)
  const [ringTone, ringProgress, ringLabel] = orderRingState(item.derivedStatus)
  const ringDetail = orderRingDetail(delivery)
  // Time left (or days late) before shipping moves from the status ring to the bottom of the order info, in red under a day.
  const dueLine = delivery.label === 'Kalan süre' || delivery.overdue ? ringDetail : null
  const facts = (line: OrderLine) => returnLineFacts(item.platformCode, line)
  const lineImage = (line: OrderLine) => line.imageUrl ?? (lines.length === 1 ? item.primaryImageUrl : null)
  const openPreview = (line: OrderLine) => { const imageUrl = lineImage(line); const fallbackUrls = createProductImageFallbackUrls([line.barcode, line.sku], item.connectionId); onPreviewImage({ url: imageUrl ?? fallbackUrls[0]!, fallbackUrls, title: line.title, modelCode: line.modelCode, variant: line.optionSignature }) }
  const trackingNumber = shipment?.cargoTrackingNumber ?? item.cargoTrackingNumber
  const cargoName = shipment?.cargoProviderName ?? item.cargoProviderName
  const cargoIcon = cargoCarrier(cargoName)?.iconUrl
  const labelFormat: ShippingLabelFormat | undefined = enabledLabelFormats.includes(labelSettings.defaultFormat) ? labelSettings.defaultFormat : enabledLabelFormats[0]
  // The server's first-print time counts on every computer; the local mark covers a failed call (audit 2026-10-20 S8).
  const labelPrinted = Boolean(item.labelPrintedAt) || (labelFormat ? printedLabels.has(printedShippingLabelKey(item.id, labelFormat)) : false)
  const canPrintLabels = isCargoLabelStage && Boolean(shipment) && enabledLabelFormats.length > 0
  const invoiceDocumentHref = item.invoiceId ? `/api/v1/invoices/${item.invoiceId}/documents/latest/content` : item.invoiceDocumentUrl
  const invoiceItems: Array<[ 'doc' | 'info' | 'edit' | 'upload' | 'open' | 'close', string, () => void, string?, boolean?, string?] | 'hr'> = invoiceNeedsAction
    ? [['doc', 'Fatura Oluştur', onInvoiceCreate, '', !invoiceCreationEnabled, invoiceCreationTooltip], ['info', 'Fatura Bilgileri', onInvoiceDetails], ...(isShopifyOrder ? [['edit', 'Fatura Durumunu Değiştir', () => setInvoiceStatusModalOpen(true), '', !invoiceCreationEnabled && !item.invoiceId, invoiceCreationTooltip] as ['edit', string, () => void, string, boolean, string]] : []), ['upload', 'Fatura Yükle', onInvoiceUpload, '', !invoiceCreationEnabled && !item.invoiceId, invoiceCreationTooltip]]
    : [...(invoiceDocumentHref ? [['open', 'Faturayı Gör', () => window.open(invoiceDocumentHref, '_blank', 'noopener')] as ['open', string, () => void]] : []), ['info', 'Fatura Bilgileri', onInvoiceDetails], ...(isShopifyOrder ? [['edit', 'Fatura Durumunu Değiştir', () => setInvoiceStatusModalOpen(true), '', !invoiceCreationEnabled && !item.invoiceId, invoiceCreationTooltip] as ['edit', string, () => void, string, boolean, string]] : []), 'hr', ['close', 'Fatura İptal Et', () => undefined, 'rr-danger', true, 'Fatura iptali şimdilik pazar yerinin panelinden yapılır.']]
  const actionItems: Array<['edit' | 'play' | 'box' | 'truck' | 'print' | 'ban', string, () => void, string?, boolean?, string?] | 'hr'> = [
    ...(isShopifyOrder ? [['edit', 'Sipariş Durumunu Değiştir', onOrderStatusChange] as ['edit', string, () => void]] : []),
    // Shopify has no picking step; on Hepsiburada an order is taken into work by creating its package.
    ...(isShopifyOrder ? [] : [normalizedOrderStatus === 'NEW' ? ['play', processing ? 'İşleniyor…' : 'İşleme Al', isHepsiburadaOrder ? onCreatePackage : onProcessOrder, '', processing] as ['play', string, () => void, string, boolean] : ['play', normalizedOrderStatus === 'DELIVERED' ? 'Teslim Edildi' : 'İşleme Al', () => undefined, '', true, 'Sipariş zaten işleme alınmış.'] as ['play', string, () => void, string, boolean, string]]),
    ...(isHepsiburadaOrder && !['SHIPPED', 'UNDELIVERED', 'DELIVERED', 'RETURNED'].includes(normalizedOrderStatus) ? [['box', 'Paket Oluştur', onCreatePackage] as ['box', string, () => void]] : []),
    ...(canChangeCargo ? [['truck', 'Başka Kargo Firması İle Gönder', onCourierChange] as ['truck', string, () => void]] : []),
    ...(canPrintLabels ? ['hr' as const, ...enabledLabelFormats.map(format => ['print', `${format === 'a4' ? 'A4' : 'Sticker'} Etiketi Yazdır`, () => onPrintLabel(format), '', labelBlockedByInvoice, 'Mikro ihracat faturası kesilmeden kargo etiketi yazdırılamaz.'] as ['print', string, () => void, string, boolean, string])] : []),
    'hr', ['ban', 'İptal Et', () => undefined, 'rr-danger', true, 'Sipariş iptali pazar yerinin panelinden yapılır.'],
  ]
  const menu = (items: typeof actionItems | typeof invoiceItems, label: string) => <div className="rr-menu" role="menu" aria-label={label}>{items.map((entry, index) => entry === 'hr' ? <hr key={`hr-${index}`} /> : <button key={entry[1]} type="button" role="menuitem" className={entry[3] || undefined} disabled={entry[4]} title={entry[4] ? entry[5] : undefined} onClick={() => { onMenuChange(null); entry[2]() }}><RrIcon name={entry[0]} />{entry[1]}</button>)}</div>
  const detailSheet = detailOpen ? createPortal(<div className="rr-layer"><DialogSurface as="div" onClose={() => setDetailOpen(false)} className="rr-sheet rr-sheet-full" aria-label={`Sipariş ${item.orderNumber} detayı`}>
    <header className="rr-sheet-h"><button type="button" className="rr-icon-btn" aria-label="Detayı kapat" onClick={() => setDetailOpen(false)}><RrIcon name="left" /></button><b>Sipariş Detayı</b><span /></header>
    <div className="rr-sheet-b">
      <article className="rr-scard"><header><div className="rr-h3">Sipariş Bilgileri</div><RrRing tone={ringTone} progress={ringProgress} label={ringLabel} detail={ringDetail.text} detailClass={ringDetail.cls} /></header><dl className="rr-kv"><dt>Platform</dt><dd>{item.platformDisplayName || returnPlatformName(item.platformCode)}</dd><dt>Sipariş Numarası</dt><dd className="rr-num">{displayOrderNumber(item.orderNumber)}<button type="button" className="rr-icon-btn" style={{ width: 22, height: 22 }} onClick={copyOrderNumber} aria-label={`Sipariş numarası ${item.orderNumber} kopyala`}><RrIcon name={copied ? 'check' : 'copy'} size={14} /></button></dd><dt>Sipariş Tarihi</dt><dd className="rr-num">{returnDateText(item.orderedAt)}</dd><dt>Paket No</dt><dd className="rr-num">{packageNoText}</dd><dt>Alıcı</dt><dd>{customerDisplayName(item)}</dd>{(item.platformInfo ?? []).map(info => <Fragment key={info.label}><dt>{info.label}</dt><dd>{safeExternalUrl(info.url) ? <a className="rr-btn rr-link" href={safeExternalUrl(info.url)!} target="_blank" rel="noreferrer noopener">{info.value}</a> : info.value}</dd></Fragment>)}</dl></article>
      <article className="rr-scard"><header><div className="rr-h3">Ürünler</div></header>{lines.length ? <div className="rr-plines">{lines.map(line => <div className="rr-pline" key={line.id}><RrThumb src={lineImage(line)} quantity={productLineQuantity(line)} alt={line.title} onOpen={() => openPreview(line)} /><div><b>{line.title}</b><ReturnFactLines facts={facts(line)} /><small><span>Birim Fiyatı:</span> <b className="rr-num">{money(line.unitPrice)}</b></small></div></div>)}</div> : <p className="rr-lead">Ürün bilgisi eşitleme bekliyor</p>}</article>
      <article className="rr-scard"><header><div className="rr-h3">Kargo Bilgileri</div></header><dl className="rr-kv"><dt>Kargo Firması</dt><dd>{cargoLabel(cargoName)}</dd><dt>Kargo Kodu</dt><dd className="rr-num">{deliveryNoText}</dd>{item.shippedAt && <><dt>Kargoya Veriliş</dt><dd className="rr-num">{returnDateText(item.shippedAt)}</dd></>}</dl></article>
      <article className="rr-scard"><header><div className="rr-h3">Fatura Bilgileri</div><ReturnInvoiceRing status={item.invoiceStatus} isMicroExport={item.isMicroExport} /></header><dl className="rr-kv">{hasDiscount && <><dt>Satış Tutarı</dt><dd className="rr-num">{money(saleTotal)}</dd><dt>Satıcı İndirimi</dt><dd className="rr-num">{money(item.discountAmount)}</dd></>}<dt>Faturalanacak Tutar</dt><dd className="rr-num"><b>{money(invoiceableTotal)}</b></dd>{!invoiceNeedsAction && invoiceDocumentHref && <><dt>Fatura Belgesi</dt><dd><a className="rr-btn rr-link" href={invoiceDocumentHref} download>Faturayı Gör</a></dd></>}</dl></article>
    </div>
    <footer className="rr-sheet-f">
      {detailActionsOpen && detailMenuActions.length > 0 && <div className="rr-menu rr-sheet-menu" role="menu">{detailMenuActions.map(action => <button type="button" role="menuitem" key={action.key} disabled={action.disabled} onClick={closeDetailThen(action.run)}>{action.label}</button>)}</div>}
      {detailMenuActions.length > 0 && <button type="button" className="rr-btn" aria-haspopup="menu" aria-expanded={detailActionsOpen} onClick={() => setDetailActionsOpen(open => !open)}>İşlemler<RrIcon name="chev" /></button>}
      {detailQuickAction && <button type="button" className="rr-btn" disabled={detailQuickAction.disabled} onClick={closeDetailThen(detailQuickAction.run)}><RrIcon name="play" />{detailQuickAction.label}</button>}
      {detailPrimaryAction}
    </footer>
  </DialogSurface></div>, document.body) : null
  const rowClasses = [selected ? 'rr-sel' : '', delivery.overdue ? 'rr-due' : ''].filter(Boolean).join(' ')
  return <><tr ref={rowRef} className={rowClasses || undefined} onClick={event => { if (!window.matchMedia('(max-width: 760px)').matches) return; const target = event.target as Element; if (target.closest('button, a, input, select, textarea, label, .rr-menu, .rr-pop, .rr-thumb')) return; if (selectionMode) return; setDetailOpen(true) }}>
    <td className="rr-wide"><div className="rr-ordercell">{isCancelledOrder ? <span style={{ width: 16, flex: 'none' }} title="İptal siparişler toplu seçilemez" /> : <input type="checkbox" className="rr-cb" checked={selected} onChange={event => onSelect(event.target.checked)} aria-label={`Sipariş ${item.orderNumber} seç`} />}<div><RrWordmark code={item.platformCode} name={item.platformDisplayName || returnPlatformName(item.platformCode)} /><span className={`rr-on-no${delivery.overdue ? ' rr-crit' : ''}`}>{displayOrderNumber(item.orderNumber)}<button type="button" className="rr-icon-btn" title={copied ? 'Kopyalandı' : 'Kopyala'} aria-label={copied ? `Sipariş numarası ${item.orderNumber} kopyalandı` : `Sipariş numarası ${item.orderNumber} kopyala`} style={{ width: 22, height: 22 }} onClick={() => void copyOrderNumber()}><RrIcon name={copied ? 'check' : 'copy'} size={14} /></button></span>{noBuyer && <small className="rr-buyer-inline"><b>Alıcı:</b> {customerDisplayName(item)}</small>}<small className="rr-num"><b>Sipariş Tarihi:</b> {returnDateText(item.orderedAt)}</small><small className="rr-num"><b>Paket No:</b> {packageNoText}</small><small className="rr-num"><b>Teslimat No:</b> {shipment?.cargoTrackingNumber ?? (isHepsiburadaUnverifiedWithoutPackage ? 'Gönderi bilgisi doğrulanmadı' : isHepsiburadaOrder ? shipment ? 'Kargoya verildiğinde gelir' : 'Paket oluşturulunca gelir' : 'Bekleniyor')}</small>{dueLine && <small className={`rr-due${delivery.overdue ? ' is-late' : ''}`}><b>Kargo Süresi:</b> {dueLine.text}</small>}{hepsiburadaPackageHistoryExpired && <small className="rr-note-line">Hepsiburada kargo durum listeleri son bir ayı kapsıyor; daha eski paket kaydı API’den doğrulanamıyor.</small>}</div></div></td>
    {!noBuyer && <td className="rr-mid" data-l="Alıcı"><div className="rr-cust"><b>{customerDisplayName(item)}</b></div></td>}
    <td className="rr-wide" data-l="Bilgiler"><div className="rr-plines">{lines.length ? lines.map(line => <div className="rr-pline" key={line.id}><RrThumb src={lineImage(line)} quantity={productLineQuantity(line)} alt={line.title} onOpen={() => openPreview(line)} /><div><b title={line.title}><ProductTitleLink platformCode={item.platformCode} line={line} /></b><small className="rr-card-only"><span>Birim Fiyatı:</span> <b className="rr-num">{money(line.unitPrice)}</b></small>{facts(line).map(([label, value]) => <small key={label} title={`${label}: ${value}`}><span>{label}:</span> <b className="rr-num">{value}</b></small>)}</div></div>) : <div className="rr-pline"><RrThumb src={item.primaryImageUrl} alt="Ürün" /><div><b>Ürün bilgisi eşitleme bekliyor</b></div></div>}</div></td>
    <td className="rr-mid rr-price-col" data-l="Birim Fiyat"><div className="rr-prices">{lines.length ? lines.map(line => { const quantity = productLineQuantity(line); return <div className="rr-pr" key={line.id}><b className="rr-num">{money(line.unitPrice)}</b>{quantity > 1 && <small>{quantity} adet</small>}</div> }) : <div className="rr-pr"><b className="rr-num">{money(item.netAmount)}</b></div>}</div></td>
    <td className="rr-mid" data-l="Kargo"><div className="rr-cargocell">{cargoIcon ? <img className="rr-cargo-img" src={cargoIcon} alt={cargoLabel(cargoName)} title={cargoLabel(cargoName)} referrerPolicy="no-referrer" loading="lazy" /> : <span className="rr-muted">{cargoName?.trim() ? cargoLabel(cargoName) : 'Kargo bekleniyor'}</span>}<small className="rr-num">{trackingNumber ?? (isHepsiburadaUnverifiedWithoutPackage ? 'Gönderi durumu doğrulanmadı' : 'Takip no bekleniyor')}</small>{item.shippedAt && <small className="rr-shipdate" title="Kargoya veriliş tarihi"><span>Kargoya Veriliş</span><b className="rr-num">{returnDateText(item.shippedAt)}</b></small>}{isCargoTrackingStage && shipment?.cargoTrackingNumber && <Link className="rr-btn rr-link" style={{ fontSize: 12 }} to={`/shipments/${shipment.id}`}>Kargo Takip <RrIcon name="open" /></Link>}</div></td>
    <td data-l="Fatura"><div className="rr-invcell">{hasDiscount && <><span className="rr-m"><span>Satış Tutarı</span><b className="rr-num">{money(saleTotal)}</b></span><span className="rr-m"><span>Satıcı İndirimi</span><b className="rr-num">{money(item.discountAmount)}</b></span></>}<span className="rr-m rr-pay"><span>Faturalanacak Tutar</span><b className="rr-num">{money(invoiceableTotal)}</b></span><ReturnInvoiceRing status={item.invoiceStatus} isMicroExport={item.isMicroExport} />{!isCancelledOrder && <RrAnchor trigger={(open, toggle) => <button type="button" className="rr-btn rr-link rr-inv-trigger" aria-haspopup="menu" aria-expanded={open} onClick={toggle}>Fatura İşlemleri <RrIcon name="chev" /></button>}>{() => menu(invoiceItems, `${displayOrderNumber(item.orderNumber)} fatura işlemleri`)}</RrAnchor>}</div></td>
    <td className="rr-mid" data-l="İşlemler">{isCancelledOrder ? <div className="rr-actcell"><span className="rr-muted-note">İşlem yapılamaz</span></div> : <div className="rr-actcell">{canPrintLabels && labelFormat && <button type="button" className={`rr-btn rr-lbl${labelPrinted ? ' rr-printed' : ''}`} disabled={labelBlockedByInvoice} title={labelBlockedByInvoice ? 'Mikro ihracat faturası kesilmeden kargo etiketi yazdırılamaz.' : labelPrinted ? `${labelFormat === 'a4' ? 'A4' : 'Sticker'} etiketi daha önce yazdırıldı; tekrar yazdırılabilir.` : `${labelFormat === 'a4' ? 'A4' : 'Sticker'} Etiketi Yazdır`} onClick={() => onPrintLabel(labelFormat)}><RrIcon name="print" />Etiket</button>}<RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} aria-haspopup="menu" aria-expanded={open} onClick={toggle}><span>İşlemler</span><RrIcon name="chev" /></button>}>{() => menu(actionItems, `${displayOrderNumber(item.orderNumber)} sipariş işlemleri`)}</RrAnchor></div>}</td>
  </tr>
    {detailSheet}
    {invoiceStatusModalOpen && <ShopifyInvoiceStatusModal item={item} onClose={() => setInvoiceStatusModalOpen(false)} onFeedback={(message, kind) => { if (kind !== 'pending') appendNotification(message, normalizeNotificationKind(message, kind)) }} />}
  </>
}
function HepsiburadaPackageCreateModal({ item, onClose, onCreated }: { item: Order; onClose: () => void; onCreated: (packageNumber: string) => void }) {
  const lines = item.lines ?? []
  const availableQuantity = (line: OrderLine) => Math.max(0, line.orderedQuantity - line.cancelledQuantity - (line.allocatedQuantity ?? 0))
  const firstLine = lines.find(line => availableQuantity(line) > 0)
  const [selected, setSelected] = useState<Record<string, number>>(() => firstLine ? { [firstLine.id]: availableQuantity(firstLine) } : {})
  const [error, setError] = useState('')
  const [saving, setSaving] = useState(false)
  const selectedCount = Object.keys(selected).filter(id => selected[id] > 0).length

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!selectedCount || saving) return
    const data = new FormData(event.currentTarget)
    const lineItems = Object.entries(selected).filter(([, quantity]) => quantity > 0).map(([orderLineId, quantity]) => ({ orderLineId, quantity }))
    setSaving(true)
    setError('')
    const body = JSON.stringify({
      barcode: data.get('barcode'),
      cargoCompany: data.get('cargoCompany'),
      carrier: data.get('carrier'),
      creationReason: data.get('creationReason'),
      deci: Number(data.get('deci')),
      parcelQuantity: Number(data.get('parcelQuantity')),
      shippingAddressLabel: data.get('shippingAddressLabel'),
      shippingModel: data.get('shippingModel'),
      lineItems,
    })
    try {
      // The same key for a resend of the same form on the same order version (a lost answer reaches Hepsiburada once);
      // a corrected form, or any attempt after the server answered with an error (a 422), goes out with a new key.
      const result = await sendWithVersionedIdempotencyKey(versionedIdempotencyKey('hb-package', item.id, item.version, body), key => hubApi<{ packageNumber: string }>(`/orders/${item.id}/packages`, {
        method: 'POST',
        headers: { 'Idempotency-Key': key, 'If-Match': `"v${item.version}"` },
        body,
      }))
      onCreated(result.packageNumber)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Hepsiburada paketi oluşturulamadı.')
    } finally {
      setSaving(false)
    }
  }

  const facts: Array<[string, string]> = [['Müşteri', customerDisplayName(item)], ['Sipariş Tarihi', returnDateText(item.orderedAt)], ['Paket No', orderDisplayShipment(item)?.externalPackageId ?? 'Henüz paketlenmedi']]
  return <RrModal crumb="Hepsiburada Siparişi" title="Kargo Paketi Oluştur" desc={`${displayOrderNumber(item.orderNumber)} · ${customerDisplayName(item)}`} size="lg" locked={saving} onClose={onClose} foot={<><span className="rr-note">Seçili kalem: <b>{selectedCount}</b></span><button type="button" className="rr-btn" onClick={onClose} disabled={saving}>Vazgeç</button><button type="submit" form="hb-package-form" className="rr-btn rr-primary" disabled={saving || !selectedCount || !lines.length}><RrIcon name="box" />{saving ? 'Paket oluşturuluyor…' : 'Hepsiburada’da Paketi Oluştur'}</button></>}>
    <form id="hb-package-form" style={{ display: 'contents' }} onSubmit={submit}>
      <section className="rr-osum" aria-label="Sipariş bilgileri"><span><RrLogo code={item.platformCode} />{displayOrderNumber(item.orderNumber)}</span><dl>{facts.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl></section>
      <div className="rr-h3">Sipariş Kalemleri</div>
      {lines.length ? <div className="rr-apr" role="table"><div className="rr-apr-h" role="row"><span role="columnheader">Bilgiler</span><span role="columnheader">Pakete Girecek Adet</span></div>{lines.map(line => { const available = availableQuantity(line); const on = (selected[line.id] ?? 0) > 0; return <div className="rr-apr-r" role="row" key={line.id}><label className="rr-apr-info"><input type="checkbox" className="rr-cb" checked={on} disabled={available <= 0 || saving} onChange={event => setSelected(current => { const next = { ...current }; if (event.target.checked) next[line.id] = available; else delete next[line.id]; return next })} /><RrThumb src={line.imageUrl ?? item.primaryImageUrl} quantity={productLineQuantity(line)} large alt={line.title} /><span className="rr-ptext"><b>{line.title}</b><small>{line.sku} · kalan {available} adet</small></span></label><div className="rr-apr-side">{on ? <input className="rr-in rr-num" aria-label={`${line.title} paket adedi`} type="number" min="1" max={available} step="1" value={selected[line.id]} disabled={saving} onChange={event => setSelected(current => ({ ...current, [line.id]: Math.min(available, Math.max(0, Number(event.target.value) || 0)) }))} /> : <span className="rr-qty-static">{available} Adet</span>}</div></div> })}</div> : <p className="rr-lead">Bu sipariş için paketlenebilir satır bulunamadı.</p>}
      <div className="rr-dgrid">{[['barcode', 'Paket Barkodu', 'text', 128], ['cargoCompany', 'Kargo Firması Kodu', 'text', 100], ['carrier', 'Taşıyıcı', 'text', 100], ['creationReason', 'Oluşturma Nedeni', 'text', 100], ['deci', 'Desi', 'number', 0], ['parcelQuantity', 'Paket Adedi', 'number', 0], ['shippingAddressLabel', 'Gönderim Adresi Etiketi', 'text', 100], ['shippingModel', 'Gönderim Modeli', 'text', 100]].map(([name, label, type, max]) => <label key={name as string} className="rr-fld"><small>{label}</small><input className={`rr-in${type === 'number' ? ' rr-num' : ''}`} name={name as string} type={type as string} {...(type === 'number' ? { min: name === 'deci' ? 0 : 1, max: name === 'deci' ? 10000 : 100, step: 1, defaultValue: 1 } : { maxLength: max as number })} required /></label>)}</div>
      <div className="rr-callout"><RrIcon name="info" /><span>Paket oluşturma Hepsiburada’ya dış yazma yapar. Önce aynı pakete uygun sipariş kalemleri doğrulanır; mağaza/ortam kanıtı ve bağlantı yazma kapıları kapalıysa işlem reddedilir.</span></div>
      {error && <div className="rr-callout rr-danger" role="alert"><RrIcon name="alert" /><span>{error}</span></div>}
    </form>
  </RrModal>
}
const shopifyOrderStatuses = ['NEW', 'PROCESSING', 'ON_HOLD', 'READY_TO_SHIP', 'SHIPPED', 'UNDELIVERED', 'DELIVERED', 'RETURN_IN_TRANSIT', 'RETURNED', 'CANCELLED'] as const
type ShopifyOrderCsvImportResult = { fileRows: number; fileOrders: number; matchedOrders: number; updatedOrders: number; updatedLines: number; unmatchedOrders: number; ambiguousOrders: number; unmatchedLines: number; unmatchedOrderNumbers: string[]; issues: string[] }
function ShopifyOrderCsvImportModal({ connections, onClose, onFeedback }: { connections: Connection[]; onClose: () => void; onFeedback: IntegrationFeedbackHandler }) {
  const client = useQueryClient()
  const [file, setFile] = useState<File | null>(null)
  const [connectionId, setConnectionId] = useState(() => connections.length === 1 ? connections[0].id : '')
  const [result, setResult] = useState<ShopifyOrderCsvImportResult | null>(null)
  const [fileError, setFileError] = useState('')
  const upload = useMutation({
    mutationFn: async () => {
      if (!file) throw new Error('Önce Shopify CSV dosyasını seçin.')
      const body = new FormData()
      body.append('file', file)
      if (connectionId) body.append('connectionId', connectionId)
      return hubApi<ShopifyOrderCsvImportResult>('/orders/shopify-csv-import', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body })
    },
    onSuccess: async value => {
      setResult(value)
      onFeedback(`${value.updatedOrders} Shopify siparişi ve ${value.updatedLines} ürün satırı CSV dışa aktarımından güncellendi.`, value.unmatchedOrders || value.issues.length ? 'info' : 'success')
      await client.invalidateQueries({ queryKey: ['orders'] })
      await client.invalidateQueries({ queryKey: ['orders', 'summary'] })
    },
    onError: reason => onFeedback(reason instanceof Error ? reason.message : 'Shopify CSV aktarımı tamamlanamadı.', 'error')
  })

  function chooseFile(event: ChangeEvent<HTMLInputElement>) {
    const selected = event.currentTarget.files?.[0] ?? null
    setResult(null)
    setFileError('')
    if (selected && !selected.name.toLowerCase().endsWith('.csv')) { setFile(null); setFileError('Yalnız .csv uzantılı Shopify sipariş dışa aktarım dosyası kabul edilir.'); return }
    if (selected && selected.size > 10 * 1024 * 1024) { setFile(null); setFileError('Dosya en fazla 10 MiB olabilir.'); return }
    setFile(selected)
  }

  return <RrModal crumb="Shopify" title="Shopify Siparişlerini Aktar" desc="Shopify sipariş dışa aktarım CSV dosyası, sipariş numarasına göre mevcut Shopify siparişleriyle eşleştirilir. Yeni sipariş oluşturulmaz." size="md" locked={upload.isPending} onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose} disabled={upload.isPending}>Kapat</button><button type="button" className="rr-btn rr-primary" onClick={() => upload.mutate()} disabled={!file || connections.length === 0 || upload.isPending}>{upload.isPending ? 'Aktarılıyor…' : 'Shopify Siparişlerini Aktar'}</button></>}>
    {connections.length > 1 && <label className="rr-fld"><small>Shopify Bağlantısı</small><select className="rr-in" aria-label="Shopify bağlantısı" value={connectionId} onChange={event => setConnectionId(event.target.value)}><option value="">Bağlantılar arasında eşleştir</option>{connections.map(connection => <option key={connection.id} value={connection.id}>{connection.displayName} · {connection.externalStoreId}</option>)}</select></label>}
    <div className="rr-callout"><RrIcon name="info" /><span>Sipariş tutarı, alıcı/adres ve mevcut ürün satırı bilgileri aktarılır. Sipariş durumu, miktarlar ve kargo/takip bilgileri korunur; yeni sipariş açılmaz.</span></div>
    {connections.length === 0 && <div className="rr-callout rr-warn" role="status"><RrIcon name="alert" /><span>Shopify bağlantısı görünmüyor. Sipariş içe aktarmak için önce bir Shopify bağlantısı kurun.</span></div>}
    <div className="rr-fld"><small>Shopify Sipariş CSV Dosyası</small><label className="rr-drop"><input type="file" hidden accept=".csv,text/csv" onChange={chooseFile} disabled={upload.isPending} /><RrIcon name="upload" /><b>{file ? file.name : 'Dosya Seç veya Sürükle'}</b><small>{file ? `${(file.size / 1024).toLocaleString('tr-TR', { maximumFractionDigits: 0 })} KB` : 'CSV · en fazla 10 MiB'}</small></label><em>Shopify yönetim panelinden indirilen .csv dosyasını seçin (en fazla 10 MiB).</em></div>
    {fileError && <div className="rr-callout rr-danger" role="alert"><RrIcon name="alert" /><span>{fileError}</span></div>}
    {result && <section className="rr-dsec" aria-live="polite"><header><div className="rr-h3">Aktarım Sonucu</div><p>{result.fileRows} CSV satırı / {result.fileOrders} sipariş · {result.unmatchedOrders} eşleşmeyen · {result.ambiguousOrders} belirsiz · {result.unmatchedLines} eşleşmeyen ürün satırı</p></header><div className="rr-facts"><div><small>Eşleşen Sipariş</small><b className="rr-num">{result.matchedOrders}</b></div><div><small>Güncellenen Sipariş</small><b className="rr-num">{result.updatedOrders}</b></div><div><small>Güncellenen Ürün Satırı</small><b className="rr-num">{result.updatedLines}</b></div></div>{(result.unmatchedOrderNumbers.length > 0 || result.issues.length > 0) && <dl className="rr-kv">{result.unmatchedOrderNumbers.length > 0 && <><dt>Eşleşmeyen Siparişler</dt><dd className="rr-num">{result.unmatchedOrderNumbers.join(', ')}</dd></>}{result.issues.length > 0 && <><dt>Notlar</dt><dd>{result.issues.join(' · ')}</dd></>}</dl>}</section>}
  </RrModal>
}
function ShopifyOrderStatusModal({ item, onClose, onFeedback }: { item: Order; onClose: () => void; onFeedback: IntegrationFeedbackHandler }) {
  const client = useQueryClient()
  const [status, setStatus] = useState<(typeof shopifyOrderStatuses)[number]>(() => shopifyOrderStatuses.includes(item.derivedStatus as (typeof shopifyOrderStatuses)[number]) ? item.derivedStatus as (typeof shopifyOrderStatuses)[number] : 'NEW')
  const update = useMutation({
    mutationFn: () => hubApi(`/orders/${item.id}/shopify-status`, { method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ status }) }),
    onSuccess: async () => {
      const message = `Shopify sipariş durumu “${statusLabel(status)}” olarak güncellendi.`
      onFeedback(message, 'success')
      await client.invalidateQueries({ queryKey: ['orders'] })
      onClose()
    },
    onError: reason => onFeedback(reason instanceof Error ? reason.message : 'Shopify sipariş durumu güncellenemedi.', 'error')
  })
  return <RrModal crumb="Shopify" title="Sipariş Durumunu Değiştir" desc={`${displayOrderNumber(item.orderNumber)} · ${customerDisplayName(item)}`} size="sm" locked={update.isPending} onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose} disabled={update.isPending}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => update.mutate()} disabled={update.isPending}>{update.isPending ? 'Kaydediliyor…' : 'Durumu Kaydet'}</button></>}>
    <label className="rr-fld"><small>Yeni Sipariş Durumu</small><select className="rr-in" value={status} onChange={event => setStatus(event.target.value as (typeof shopifyOrderStatuses)[number])}>{shopifyOrderStatuses.map(value => <option key={value} value={value}>{titleTr(statusLabel(value))}</option>)}</select></label>
    <div className="rr-callout"><RrIcon name="info" /><span>Durum yalnızca panelde güncellenir; Shopify’daki sipariş ve kargo durumu değişmez. Daha yeni uzaktan gelen durum manuel seçimin üzerine geçer.</span></div>
  </RrModal>
}
function ShopifyBulkStatusModal({ kind, items, onClose, onFeedback, onUpdated }: { kind: 'order' | 'invoice'; items: Order[]; onClose: () => void; onFeedback: IntegrationFeedbackHandler; onUpdated: (orderIds: string[]) => void }) {
  const client = useQueryClient()
  const [status, setStatus] = useState('')
  const isInvoice = kind === 'invoice'
  const invoiceStatusBlocked = isInvoice && status === 'UPLOADED' && items.some(item => !item.invoiceDocumentUrl)
  const update = useMutation({
    mutationFn: async () => {
      if (!status) throw new Error('Uygulanacak durumu seçin.')
      const results: Array<{ item: Order; ok: boolean; error?: string }> = new Array(items.length)
      let nextIndex = 0
      const worker = async () => {
        while (true) {
          const index = nextIndex++
          if (index >= items.length) return
          const item = items[index]
          try {
            if (item.platformCode !== 'SHOPIFY') throw new Error('Bu işlem yalnızca Shopify siparişlerinde kullanılabilir.')
            if (isInvoice) {
              const order = await hubApi<OrderDetail>(`/orders/${item.id}`)
              if (order.platformCode !== 'SHOPIFY') throw new Error('Sipariş Shopify bağlantısına ait değil.')
              const packageId = orderDisplayShipment(order)?.id
              if (!packageId) throw new Error('Fatura durumu için Shopify sipariş paketi bulunamadı.')
              if (status === 'UPLOADED' && !item.invoiceDocumentUrl) throw new Error('Faturası yüklendi durumuna geçmeden önce yerel fatura kaydına belge ekleyin.')
              // in the invoice mode the window offers only PENDING and UPLOADED
              await setManualInvoiceStatus([packageId], status as ManualInvoiceStatus)
            } else {
              await hubApi(`/orders/${item.id}/shopify-status`, { method: 'PUT', headers: { 'Idempotency-Key': `shopify-order-status:${item.id}:${status}:${newIdempotencyKey()}`, 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ status }) })
            }
            results[index] = { item, ok: true }
          } catch (error) {
            results[index] = { item, ok: false, error: error instanceof Error ? error.message : 'İstek başarısız oldu.' }
          }
        }
      }
      await Promise.all(Array.from({ length: Math.min(4, items.length) }, () => worker()))
      return results
    },
    onSuccess: async results => {
      await client.invalidateQueries({ queryKey: ['orders'] })
      const succeeded = results.filter(result => result.ok)
      const failed = results.filter(result => !result.ok)
      onUpdated(succeeded.map(result => result.item.id))
      const statusName = isInvoice
        ? status === 'UPLOADED' ? 'Faturası yüklendi' : 'Fatura bekliyor'
        : statusLabel(status)
      const noun = isInvoice ? 'fatura durumu' : 'sipariş durumu'
      const prefix = `${succeeded.length}/${items.length} Shopify siparişinin ${noun} “${statusName}” olarak güncellendi.`
      const failureDetails = failed.slice(0, 4).map(result => `${displayOrderNumber(result.item.orderNumber)}: ${result.error}`).join(' · ')
      const remainingFailures = failed.length > 4 ? ` · ${failed.length - 4} ek hata` : ''
      onFeedback(failed.length ? `${prefix} Başarısız: ${failureDetails}${remainingFailures}` : prefix, failed.length === 0 ? 'success' : succeeded.length ? 'info' : 'error')
      onClose()
    }
  })
  const validStatus = isInvoice ? status === 'PENDING' || status === 'UPLOADED' : shopifyOrderStatuses.includes(status as (typeof shopifyOrderStatuses)[number])
  return <RrModal crumb="Shopify" title={isInvoice ? 'Toplu Fatura Durumu Değiştir' : 'Toplu Sipariş Durumu Değiştir'} desc={`${items.length} seçili Shopify siparişi · Shopify dışındaki kayıtlar etkilenmez.`} size="sm" locked={update.isPending} onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose} disabled={update.isPending}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => update.mutate()} disabled={!validStatus || update.isPending || invoiceStatusBlocked} title={invoiceStatusBlocked ? 'Önce yerel fatura belgelerini ekleyin.' : undefined}>{update.isPending ? 'Güncelleniyor…' : `${items.length} Siparişi Güncelle`}</button></>}>
    <label className="rr-fld"><small>{isInvoice ? 'Yeni Fatura Durumu' : 'Yeni Sipariş Durumu'}</small><select className="rr-in" value={status} onChange={event => setStatus(event.target.value)} disabled={update.isPending}><option value="">Durum seçin</option>{isInvoice ? <><option value="PENDING">Fatura Bekliyor</option><option value="UPLOADED">Faturası Yüklendi</option></> : shopifyOrderStatuses.map(value => <option key={value} value={value}>{titleTr(statusLabel(value))}</option>)}</select></label>
    {invoiceStatusBlocked && <div className="rr-callout rr-warn"><RrIcon name="alert" /><span>“Faturası Yüklendi” için seçili Shopify siparişlerinin tamamında yerel fatura belgesi bulunmalıdır.</span></div>}
    {!isInvoice && <div className="rr-callout"><RrIcon name="info" /><span>Bu işlem yalnızca panel sipariş durumunu günceller; Shopify’daki sipariş veya kargo durumu değişmez.</span></div>}
  </RrModal>
}
export function OrdersPage() {
  const client = useQueryClient()
  const [processingOrderId, setProcessingOrderId] = useState<string | null>(null)
  const [hepsiburadaPackageOrder, setHepsiburadaPackageOrder] = useState<Order | null>(null)
  const [bulkShopifyStatus, setBulkShopifyStatus] = useState<{ kind: 'order' | 'invoice'; items: Order[] } | null>(null)
  const [searchParams, setSearchParams] = useSearchParams(); const requestedSearch = searchParams.get('search') ?? ''; const initialFilters = { ...initialOrderFilters, search: requestedSearch, status: resolveOrderStatusTab(searchParams.get('status')) }; const [filterForm, setFilterForm] = useState<OrderFilters>(initialFilters); const [filters, setFilters] = useState<OrderFilters>(initialFilters); const [selectedPlatforms, setSelectedPlatforms] = useState<string[]>([]); const [pageSize, setPageSize] = useState(50); const [page, setPage] = useState(1); const [jumpPage, setJumpPage] = useState(''); const [selectedIds, setSelectedIds] = useState<string[]>([]); const [menu, setMenu] = useState<{ orderId: string; kind: 'invoice' | 'actions' } | null>(null); const [bulkNotice, setBulkNotice] = useState(''); const [bulkNoticeKind, setBulkNoticeKind] = useState<IntegrationFeedback['kind'] | 'pending'>('success'); const [bulkNoticeVersion, setBulkNoticeVersion] = useState(0); const [exporting, setExporting] = useState(false); const [invoiceInfoOrder, setInvoiceInfoOrder] = useState<Order | null>(null); const [invoiceViewerOrder, setInvoiceViewerOrder] = useState<Order | null>(null); const [invoiceDraftOrder, setInvoiceDraftOrder] = useState<Order | null>(null); const [invoiceUploadOrder, setInvoiceUploadOrder] = useState<Order | null>(null); const [shopifyStatusOrder, setShopifyStatusOrder] = useState<Order | null>(null); const [shopifyCsvImportOpen, setShopifyCsvImportOpen] = useState(false); const [courierOrder, setCourierOrder] = useState<{ item: Order; items: Order[] } | null>(null); const [shippingLabel, setShippingLabel] = useState<{ item: Order; format: ShippingLabelFormat } | null>(null); const [shippingLabelBatch, setShippingLabelBatch] = useState<{ items: Order[]; format: ShippingLabelFormat } | null>(null); const [singleSyncOpen, setSingleSyncOpen] = useState(false); const [previewImage, setPreviewImage] = useState<ProductImagePreview | null>(null); const [, setColumnFilterOpen] = useState<'cargo' | 'invoice' | 'label' | 'status' | null>(null); const { settings: labelSettings } = useShippingLabelSettings(); const [printedLabels, setPrintedLabels] = useState<Set<string>>(() => new Set(loadPrintedShippingLabels()))
  useEffect(() => {
    const refreshPrintedLabels = () => setPrintedLabels(new Set(loadPrintedShippingLabels()))
    window.addEventListener('storage', refreshPrintedLabels)
    return () => window.removeEventListener('storage', refreshPrintedLabels)
  }, [])
  function showBulkNotice(message: string, kind: IntegrationFeedback['kind'] | 'pending' = 'success') {
    // A running operation stays visible until its result replaces it or the user closes it.
    const normalizedKind = kind === 'pending' ? kind : normalizeNotificationKind(message, kind)
    setBulkNotice(message); setBulkNoticeKind(normalizedKind)
    if (normalizedKind !== 'pending') appendNotification(message, normalizedKind)
    setBulkNoticeVersion(current => current + 1)
  }
  useEffect(() => {
    if (!bulkNotice || bulkNoticeKind === 'pending') return
    const timeout = window.setTimeout(() => setBulkNotice(''), 5000)
    return () => window.clearTimeout(timeout)
  }, [bulkNotice, bulkNoticeKind, bulkNoticeVersion])
  const [pageCursors, setPageCursors] = useState<Record<number, string | null>>({ 1: null })
  // "Etiket Durumu" is filtered by the server once it reports the filter (labelPrintedAt kept per package); until then the
  // whole order history is read and filtered here, with the print marks of this browser.
  const [serverLabelFilter, setServerLabelFilter] = useState(false)
  const hasLocalFilters = filters.label !== 'ALL' && !serverLabelFilter
  const orderCursor = pageCursors[page] ?? null
  const orderQueryFilters: OrderFilters = { ...filters, sort: keepSort(filters.status, filters.sort), platform: 'ALL', platforms: selectedPlatforms ?? [] }
  const ordersQuery = useQuery({ queryKey: hasLocalFilters ? ['orders', 'filtered-all', orderQueryFilters] : ['orders', 'page', orderQueryFilters, pageSize, page, orderCursor], queryFn: () => hasLocalFilters ? loadAllOrderPages(orderQueryFilters) : loadOrderPage(orderQueryFilters, pageSize, orderCursor, page), placeholderData: keepPreviousData, staleTime: 30_000, refetchOnWindowFocus: true })
  useEffect(() => { if (ordersQuery.data?.supportedFilters?.includes('labelPrinted')) setServerLabelFilter(true) }, [ordersQuery.data])
  // The tab counters count the orders each tab lists for the same filters (date, search, platforms, cargo,
  // invoice, label), so a counter equals its tab's total (audit 2026-10-20 S12).
  const summaryParams = orderFilterParams(orderQueryFilters, serverLabelFilter).toString()
  const summary = useQuery({ queryKey: ['orders', 'summary', 'filtered', summaryParams], queryFn: () => hubApi<OrderSummary>(summaryParams ? `/orders/summary?${summaryParams}` : '/orders/summary'), placeholderData: keepPreviousData })
  // Metric cards: late orders come from the dashboard summary, invoices waiting to be cut from the invoice workspace.
  const lateOrdersQuery = useQuery({ queryKey: ['dashboard-bootstrap'], queryFn: () => hubApi<{ metrics: { lateOrders: number; uninvoicedInvoices: number } }>('/dashboard/bootstrap'), staleTime: 30_000 })
  const invoiceSummaryQuery = useQuery({ queryKey: ['invoice-workspace-summary'], queryFn: () => hubApi<{ dueSoonCount: number; uninvoicedCount?: number }>('/invoice-workspace/summary'), staleTime: 30_000 })
  const noBuyer = useMediaQuery('(min-width: 1101px) and (max-width: 1240px)')
  const connections = useQuery({ queryKey: ['connections', 'orders-invoice'], queryFn: () => loadAllPages<Connection>('/connections') })
  const providers = connections.data?.items.filter(x => x.platformCode === 'TRENDYOL_EFATURAM' && !x.lastErrorCode && (x.status === 'ACTIVE' || x.status === 'VERIFIED')) ?? []
  const marketplaceConnections = connections.data?.items.filter(isActiveMarketplaceConnection) ?? []
  const shopifyConnections = connections.data?.items.filter(x => x.platformCode === 'SHOPIFY') ?? []
  const allOrders = ordersQuery.data?.items ?? [];
  const all = allOrders.filter(item => isMarketplacePlatformSelected(selectedPlatforms, item.platformCode));
  const ordersLoading = ordersQuery.isPending; const ordersError = ordersQuery.error
  const cargos = Array.from(new Set(all.flatMap(item => [orderDisplayShipment(item)?.cargoProviderName ?? item.cargoProviderName].filter((value): value is string => !!value))))
  const platformOptions = marketplacePlatformOptions(connections.data?.items ?? [], allOrders.map(item => ({ platformCode: item.platformCode, displayName: item.platformDisplayName })))
  const invoiceStatuses = [['FATURA_BEKLIYOR', 'Fatura Bekliyor'], ['FATURA_ISLENIYOR', 'Fatura İletiliyor'], ['FATURA_PLATFORMA_AKTARILMADI', 'Fatura İletilemedi'], ['FATURA_KONTROLDE', 'Faturası Mevcut'], ['FATURA_REDDEDILDI', 'Fatura Oluşturulamadı'], ['FATURA_BELIRSIZ', 'Fatura Sonucu Belirsiz'], ['FATURA_IPTAL', 'Fatura İptal']] as const
  const isLabelPrinted = (item: Order) => Boolean(item.labelPrintedAt) || (['a4', 'sticker'] as ShippingLabelFormat[]).some(format => printedLabels.has(printedShippingLabelKey(item.id, format)))
  const applyFilterValue = <K extends keyof OrderFilters>(key: K, value: OrderFilters[K]) => { const next = { ...filterForm, [key]: value }; setFilterForm(next); setFilters(next); setPage(1) }
  const resetFilters = () => { const next = { ...initialOrderFilters }; setFilterForm(next); setFilters(next); setSelectedPlatforms([]); setPage(1); setColumnFilterOpen(null); setSearchParams(current => { const params = new URLSearchParams(current); params.delete('search'); params.delete('status'); return params }, { replace: true }) }
  const clearInvoiceFilters = () => { const next = { ...filterForm, invoice: 'ALL', invoiceType: 'ALL', invoiceRegion: 'ALL' }; setFilterForm(next); setFilters(next); setPage(1) }
  const selectStatus = (requestedStatus: string) => { const status = resolveOrderStatusTab(requestedStatus); const next = { ...filterForm, status, sort: keepSort(status, filterForm.sort) }; setFilterForm(next); setFilters(current => ({ ...current, status, sort: keepSort(status, current.sort) })); setSearchParams(current => { const params = new URLSearchParams(current); if (status === 'ALL') params.delete('status'); else params.set('status', status); return params }, { replace: true }); setPage(1) }
  const tabCount = (tab: string) => {
    // The late view counts what the dashboard's "Geciken Siparişler" card counts.
    if (tab === 'LATE') return lateOrdersQuery.data?.metrics.lateOrders ?? 0
    const summaryKey = ({ ALL: 'all', NEW: 'new', UNVERIFIED: 'unverified', PROCESSING: 'processing', SHIPPED: 'shipped', DELIVERED: 'delivered', RESENT: 'resent', ON_HOLD: 'onHold', CANCELLED: 'cancelled', RETURNED: 'returned', RETURN_IN_TRANSIT: 'returnInTransit', PARTIALLY_CANCELLED: 'partiallyCancelled', MANUAL_REVIEW: 'manualReview', PAYMENT_PENDING: 'paymentPending', UNDELIVERED: 'undelivered', PENDING: 'pending' } as Record<string, keyof OrderSummary | undefined>)[tab]
    if (summary.data && summaryKey && typeof summary.data[summaryKey] === 'number') return summary.data[summaryKey]
    return 0
  }
  // Status, search, platform, cargo, listing, date range, sort and pagination
  // are applied by the API. Only local presentation filters remain here.
  const items = all.filter(item => !hasLocalFilters || (filters.label === 'PRINTED' ? isLabelPrinted(item) : !isLabelPrinted(item)))
  async function exportOrders() {
    setExporting(true)
    try {
    const exportItems = hasLocalFilters ? items : (await loadAllOrderPages(orderQueryFilters)).items
    // A text cell that a spreadsheet would read as a formula starts with an apostrophe (audit 2026-10-20 S19).
    const escapeCsv = (value: unknown) => `"${csvSafeText(String(value ?? '')).replaceAll('"', '""')}"`
    const rows = [
      ['Sipariş No', 'Platform', 'Müşteri', 'Sipariş Tarihi', 'Durum', 'Fatura Durumu', 'Kargo', 'Ürünler', 'Toplam Tutar', 'Para Birimi'],
      ...exportItems.filter(item => filters.label === 'ALL' || (filters.label === 'PRINTED' ? isLabelPrinted(item) : !isLabelPrinted(item))).map(item => [
        item.orderNumber,
        item.platformDisplayName || item.platformCode,
        item.customerName,
        formatDateTime(item.orderedAt),
        titleTr(statusLabel(item.derivedStatus)),
        item.invoiceStatus ? titleTr(statusLabel(item.invoiceStatus)) : '',
        cargoLabel(orderDisplayShipment(item)?.cargoProviderName ?? item.cargoProviderName),
        (item.lines ?? []).map(line => `${line.title} x${productLineQuantity(line)}`).join(' | '),
        item.netAmount.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 }),
        item.currency
      ])
    ]
    const csv = `\uFEFF${rows.map(row => row.map(escapeCsv).join(';')).join('\r\n')}`
    const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }))
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = `ravencia-siparisler-${istanbulDateKey()}.csv`
    document.body.appendChild(anchor)
    anchor.click()
    anchor.remove()
    URL.revokeObjectURL(url)
    showBulkNotice(`${(rows.length - 1).toLocaleString('tr-TR')} sipariş dışa aktarıldı.`)
    } catch (reason) {
      showBulkNotice(reason instanceof Error ? reason.message : 'Sipariş dışa aktarımı tamamlanamadı.', 'error')
    } finally {
      setExporting(false)
    }
  }
  const totalOrderCount = hasLocalFilters ? items.length : ordersQuery.data?.totalCount ?? items.length
  const totalPages = Math.max(1, Math.ceil(totalOrderCount / pageSize))
  const pageItems = (hasLocalFilters ? items.slice((page - 1) * pageSize, page * pageSize) : items).map(item => ({ ...item, invoiceCreationEnabled: connections.data?.items.find(connection => connection.id === item.connectionId)?.invoiceCreationEnabled ?? false })); const selectablePageItems = pageItems.filter(item => item.derivedStatus.toUpperCase() !== 'CANCELLED'); const allPageSelected = selectablePageItems.length > 0 && selectablePageItems.every(item => selectedIds.includes(item.id)); const selectedOrders = pageItems.filter(item => selectedIds.includes(item.id)); const shopifySelectedOrders = selectedOrders.filter(item => item.platformCode === 'SHOPIFY'); const cargoEditableOrders = selectedOrders.filter(canChangeOrderCargo); const cargoChangeHelp = cargoEditableOrders.length < selectedOrders.length ? `Kargo firması yalnız kargoya verilmemiş Trendyol paketlerinde değiştirilebilir; seçili ${selectedOrders.length - cargoEditableOrders.length} sipariş atlanır.` : undefined; const shopifyInvoiceCreationBlocked = shopifySelectedOrders.some(item => !item.invoiceId && !item.invoiceCreationEnabled); const labelBatchItems = selectedOrders.filter(item => Boolean(orderDisplayShipment(item)?.cargoTrackingNumber ?? item.cargoTrackingNumber) && !isMicroExportLabelBlocked(item))
  const hasNextPage = hasLocalFilters ? page < totalPages : Boolean(ordersQuery.data?.nextCursor)
  function goToPreviousPage() { setPage(current => Math.max(1, current - 1)); setSelectedIds([]) }
  function goToNextPage() { if (!hasNextPage) return; const next = ordersQuery.data?.nextCursor; if (next) setPageCursors(current => ({ ...current, [page + 1]: next })); setPage(current => hasLocalFilters ? Math.min(totalPages, current + 1) : current + 1); setSelectedIds([]) }
  function goToPage(nextPage: number) { const target = Math.min(Math.max(1, nextPage), totalPages); if (target === page) return; setPage(target); setSelectedIds([]) }
  useEffect(() => {
    const next = ordersQuery.data?.nextCursor
    if (!next || pageCursors[page + 1] === next) return
    setPageCursors(current => ({ ...current, [page + 1]: next }))
  }, [ordersQuery.data?.nextCursor, page, pageCursors])
  useEffect(() => { setPageCursors({ 1: null }); setPage(1); setSelectedIds([]) }, [filters, selectedPlatforms, pageSize])
  useEffect(() => { if (!requestedSearch) return; setFilterForm(current => ({ ...current, search: requestedSearch })); setFilters(current => ({ ...current, search: requestedSearch })); setPage(1) }, [requestedSearch])
  useEffect(() => {
    const requestedStatus = searchParams.get('status')
    const resolvedStatus = resolveOrderStatusTab(requestedStatus)
    if (!requestedStatus || requestedStatus.trim().toUpperCase() === resolvedStatus) return
    setFilterForm(current => ({ ...current, status: resolvedStatus }))
    setFilters(current => ({ ...current, status: resolvedStatus }))
    setSearchParams(current => {
      const params = new URLSearchParams(current)
      if (resolvedStatus === 'ALL') params.delete('status')
      else params.set('status', resolvedStatus)
      return params
    }, { replace: true })
  }, [searchParams, setSearchParams])
  useEffect(() => {
    const closeColumnFilter = (event: PointerEvent) => {
      if (!(event.target instanceof Element)) return
      if (!event.target.closest('.order-column-filter')) setColumnFilterOpen(null)
      if (!event.target.closest('.row-menu')) setMenu(null)
    }
    const closeOnEscape = (event: globalThis.KeyboardEvent) => {
      if (event.key === 'Escape') { setColumnFilterOpen(null); setMenu(null) }
    }
    document.addEventListener('pointerdown', closeColumnFilter)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeColumnFilter)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [])
  useEffect(() => { const invoiceId = searchParams.get('invoice'); if (!invoiceId || !all.length) return; const item = all.find(order => order.invoiceId === invoiceId); if (!item) return; setInvoiceViewerOrder(item); setSearchParams(current => { const next = new URLSearchParams(current); next.delete('invoice'); return next }, { replace: true }) }, [all, searchParams, setSearchParams])
  function updateSelection(id: string, checked: boolean) { if (all.find(item => item.id === id)?.derivedStatus.toUpperCase() === 'CANCELLED') return; setSelectedIds(current => checked ? Array.from(new Set([...current, id])) : current.filter(value => value !== id)) }
  const [selectionMode, setSelectionMode] = useState(false)
  const [, setSelectionActionsOpen] = useState(false)
  function exitSelectionMode() { setSelectionMode(false); setSelectionActionsOpen(false); setSelectedIds([]) }
  function togglePageSelection(checked: boolean) { setSelectedIds(current => checked ? Array.from(new Set([...current, ...selectablePageItems.map(item => item.id)])) : current.filter(id => !selectablePageItems.some(item => item.id === id))) }
  async function processSingleOrder(item: Order) {
    if (processingOrderId) return
    setMenu(null)
    setProcessingOrderId(item.id)
    showBulkNotice(`Sipariş ${displayOrderNumber(item.orderNumber)} API yanıtı bekleniyor…`, 'info')
    try {
      const updatedShipment = await hubApi<Shipment>(`/orders/${item.id}/instant-process`, {
        method: 'POST',
        // One key per order version: a double click or a retry reaches the same request record.
        headers: { 'Idempotency-Key': `instant-picking:${item.id}:v${item.version}` }
      })
      moveOrderAcrossStatusCaches(client, item, updatedShipment)
      setSelectedIds(current => current.filter(id => id !== item.id))
      await client.refetchQueries({ queryKey: ['orders', 'summary'], type: 'active' })
      await client.refetchQueries({ queryKey: ['orders'], type: 'active' })
      showBulkNotice(`Sipariş ${displayOrderNumber(item.orderNumber)} anlık olarak işleme alındı ve panelde güncellendi.`)
    } catch (error) {
      if (isPendingAction(error)) { showBulkNotice(error.message, 'info'); void client.invalidateQueries({ queryKey: ['orders'] }) }
      else showBulkNotice(error instanceof Error ? error.message : 'Sipariş işleme alınamadı.', 'error')
    } finally {
      setProcessingOrderId(current => current === item.id ? null : current)
    }
  }

  const cargoOptions = [{ value: 'ALL', label: 'Tüm Kargolar' }, ...cargos.map(value => ({ value, label: cargoLabel(value) }))]
  const invoiceOptions = [{ value: 'ALL', label: 'Tüm Fatura Durumları' }, ...invoiceStatuses.map(([value, label]) => ({ value, label }))]
  const invoiceTypeOptions = [{ value: 'ALL', label: 'Tüm Türler' }, { value: 'BIREYSEL', label: 'Bireysel' }, { value: 'KURUMSAL', label: 'Kurumsal' }]
  const invoiceRegionOptions = [{ value: 'ALL', label: 'Tüm Bölgeler' }, { value: 'TR', label: 'Türkiye' }, { value: 'MICRO_EXPORT', label: 'Mikro İhracat' }]
  const labelOptions = [{ value: 'ALL', label: 'Tüm Etiketler' }, { value: 'PRINTED', label: 'Etiketi Yazdırılanlar' }, { value: 'NOT_PRINTED', label: 'Etiketi Yazdırılmayanlar' }]
  const statusOptions = orderStatusTabs.map(([value, label]) => ({ value, label: value === 'ALL' ? 'Tüm Siparişler' : label }))
  const sortOptions: Array<[OrderFilters['sort'], string]> = ([['DATE_DESC', 'En Yeni Siparişler'], ['DATE_ASC', 'En Eski Siparişler'], ['DUE_ASC', 'Kargo Termini Yaklaşanlar'], ['DUE_DESC', 'Kargo Termini Uzak Olanlar'], ['SHIP_DESC', 'Son Kargoya Verilenler'], ['SHIP_ASC', 'İlk Kargoya Verilenler']] as Array<[OrderFilters['sort'], string]>)
    .filter(([value]) => dueSortTabs.includes(filters.status) || (value !== 'DUE_ASC' && value !== 'DUE_DESC'))
  const platformTriggerLabel = selectedPlatforms.length === 0 || selectedPlatforms.includes(RETURN_NO_PLATFORM) ? 'Tüm Platformlar' : selectedPlatforms.length === 1 ? platformOptions.find(option => option.value === selectedPlatforms[0])?.label ?? selectedPlatforms[0] : `${selectedPlatforms.length} Platform Seçili`
  const sheetField = (label: string, current: string, options: Array<{ value: string; label: string }>, pick: (value: string) => void) => <div className="rr-fld rr-sheet-only"><small>{label}</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle}><span>{options.find(option => option.value === current)?.label ?? options[0]?.label}</span><RrIcon name="chev" /></button>}>{close => <ReturnColumnPop label={label} options={options} value={current} onPick={value => { pick(value); close() }} />}</RrAnchor></div>
  const invoicePop = (close: () => void) => { const group = (label: string, options: Array<{ value: string; label: string }>, current: string, key: 'invoice' | 'invoiceType' | 'invoiceRegion') => <><div className="rr-ph">{label}</div>{options.map((option, index) => { const on = current !== 'ALL' ? option.value === current : index === 0; return <button key={option.value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => { applyFilterValue(key, option.value); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{option.label}</span></button> })}</>; const any = filterForm.invoice !== 'ALL' || filterForm.invoiceType !== 'ALL' || filterForm.invoiceRegion !== 'ALL'; return <div className="rr-pop" role="dialog" style={{ minWidth: 260 }}>{group('Fatura Durumu', invoiceOptions, filterForm.invoice, 'invoice')}<div className="rr-sep" />{group('Fatura Türü', invoiceTypeOptions, filterForm.invoiceType, 'invoiceType')}<div className="rr-sep" />{group('Fatura Bölgesi', invoiceRegionOptions, filterForm.invoiceRegion, 'invoiceRegion')}{any && <div className="rr-foot"><span /><button type="button" className="rr-btn rr-quiet" style={{ height: 28 }} onClick={() => { clearInvoiceFilters(); close() }}>Filtreleri Temizle</button></div>}</div> }
  const firstPage = Math.max(1, Math.min(page - 1, totalPages - 3)); const pageList = Array.from({ length: Math.min(4, totalPages) }, (_, index) => firstPage + index)
  const tabLabel = orderStatusTabs.find(([value]) => value === filters.status)?.[1] ?? 'Tümü'
  const shopifyBulkButtons = shopifySelectedOrders.length > 0 && <><button type="button" className="rr-btn" onClick={() => setBulkShopifyStatus({ kind: 'order', items: shopifySelectedOrders })}><RrIcon name="edit" />Shopify Sipariş Durumu ({shopifySelectedOrders.length})</button><button type="button" className="rr-btn" disabled={shopifyInvoiceCreationBlocked} title={shopifyInvoiceCreationBlocked ? invoiceCreationDisabledHelp : undefined} onClick={() => setBulkShopifyStatus({ kind: 'invoice', items: shopifySelectedOrders })}><RrIcon name="edit" />Shopify Fatura Durumu ({shopifySelectedOrders.length})</button></>
  // pageItems is rebuilt on every render, so the cards are keyed by what the orders hold rather than by the array.
  const pageItemsKey = JSON.stringify(pageItems)
  // The order cards are kept while their own inputs stay the same, so opening or closing a window (a label preview, an
  // invoice) does not redraw every card on the page first.
  // pageItems is rebuilt on every render, so the rows are memoised by its content (pageItemsKey); the handlers read only
  // state that is listed here or that their setters keep current.
  // eslint-disable-next-line react-hooks/exhaustive-deps -- keyed by pageItemsKey on purpose (see above)
  const orderRows = useMemo(() => pageItems.map(item => <OrderReferenceRow item={item} key={item.id} noBuyer={noBuyer} selectionMode={selectionMode} selected={selectedIds.includes(item.id)} processing={processingOrderId === item.id} onSelect={checked => updateSelection(item.id, checked)} openMenu={menu?.orderId === item.id ? menu.kind : null} onMenuChange={kind => setMenu(kind ? { orderId: item.id, kind } : null)} onInvoiceCreate={() => { setMenu(null); if (item.platformCode === 'SHOPIFY') setInvoiceUploadOrder(item); else setInvoiceDraftOrder(item) }} onInvoiceDetails={() => { setMenu(null); setInvoiceInfoOrder(item) }} onInvoiceUpload={() => { setMenu(null); setInvoiceUploadOrder(item) }} onCourierChange={() => { setMenu(null); setCourierOrder({ item, items: [item] }) }} onOrderStatusChange={() => { setMenu(null); setShopifyStatusOrder(item) }} onProcessOrder={() => void processSingleOrder(item)} onCreatePackage={() => { setMenu(null); setHepsiburadaPackageOrder(item) }} onPrintLabel={format => { if (isMicroExportLabelBlocked(item)) { showBulkNotice('Mikro ihracat faturası kesilmeden kargo etiketi yazdırılamaz.', 'error'); return } setMenu(null); setShippingLabel({ item, format }) }} onPreviewImage={setPreviewImage} labelSettings={labelSettings} printedLabels={printedLabels} />), [pageItemsKey, noBuyer, selectionMode, selectedIds, processingOrderId, menu, labelSettings, printedLabels])
  return <section className="content f3 rr rr-orders">
    <div className="rr-ph"><div><div className="rr-crumb">Pazaryeri Operasyonu</div><h1>Siparişler</h1><p>Tüm pazaryerlerinden gelen siparişleri tek yerden hazırlayın, etiketleyin ve takip edin.</p></div><div className="rr-acts"><button type="button" className="rr-btn" disabled={!items.length || exporting} aria-busy={exporting} title={exporting ? 'Dışa aktarılıyor…' : undefined} onClick={() => void exportOrders()}><RrIcon name="export" />Dışa Aktar</button><button type="button" className="rr-btn" onClick={() => setShopifyCsvImportOpen(true)}><RrIcon name="import" />İçe Aktar</button><button type="button" className="rr-btn rr-primary" onClick={() => setSingleSyncOpen(true)}><RrIcon name="refresh" /><span className="rr-long">Sipariş Senkronizasyonu</span><span className="rr-short">Senkronize Et</span></button></div></div>
    <OrderMetricCards newOrders={{ value: summary.data?.new, loading: !summary.data }} processing={{ value: summary.data?.processing, loading: !summary.data }} late={{ value: lateOrdersQuery.data?.metrics.lateOrders, loading: lateOrdersQuery.isLoading }} shipped={{ value: summary.data?.shipped, loading: !summary.data }} uninvoiced={{ value: invoiceSummaryQuery.data?.uninvoicedCount ?? lateOrdersQuery.data?.metrics.uninvoicedInvoices, loading: invoiceSummaryQuery.isLoading }} />
    {bulkNotice && <NoticeToast key={bulkNoticeVersion} kind={bulkNoticeKind} message={bulkNotice} onClose={() => setBulkNotice('')} durationMs={bulkNoticeKind === 'pending' ? undefined : 5000} />}
    <div className="rr-fshell">
      <ViewTabsWithMore className="order-tabs" more={orderStatusMoreTabs} ariaLabel="Sipariş durumları" value={filters.status} onChange={selectStatus} items={orderStatusTabs.filter(([value]) => !orderLinkOnlyTabs.includes(value) || value === filters.status).map(([value, label]) => { const count = summary.isLoading ? '…' : summary.isError ? '—' : tabCount(value); return { value, label: viewTabLabel(({ ALL: 'all', NEW: 'new', PROCESSING: 'processing', SHIPPED: 'shipping', DELIVERED: 'done', CANCELLED: 'cancelled', RESENT: 'resend', ON_HOLD: 'paused', PARTIALLY_CANCELLED: 'partial', UNDELIVERED: 'attention', RETURN_IN_TRANSIT: 'return', RETURNED: 'return', MANUAL_REVIEW: 'review', PAYMENT_PENDING: 'waiting', UNVERIFIED: 'attention', PENDING: 'due', LATE: 'attention' } as const)[value], label), count: typeof count === 'number' ? count.toLocaleString('tr-TR') : count, countTone: value === 'NEW' && typeof count === 'number' && count > 0 ? 'warn' as const : undefined } })} />
      <RrFilterCard active={[(selectedPlatforms ?? []).length > 0, Boolean(filters.dateFrom), filters.cargo !== 'ALL', filters.invoice !== 'ALL', filters.invoiceType !== 'ALL', filters.invoiceRegion !== 'ALL', filters.label !== 'ALL', filters.sort !== 'DATE_DESC'].filter(Boolean).length}><div className="rr-filters rr-has-sort">
        <label className="rr-fld rr-search"><small>Sipariş Ara</small><span style={{ position: 'relative' }}><RrIcon name="search" style={{ position: 'absolute', left: 11, top: 10, color: 'var(--rr-subtle)' }} /><input className="rr-in" style={{ paddingLeft: 34 }} aria-label="Sipariş ara" value={filterForm.search} onChange={event => setFilterForm(current => ({ ...current, search: event.target.value }))} onKeyDown={event => { if (event.key === 'Enter') { setFilters(filterForm); setPage(1) } }} placeholder="Sipariş no, müşteri veya barkod ara" /></span></label>
        <div className="rr-fld"><small>Platform</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle} disabled={!platformOptions.length}><span>{platformTriggerLabel}</span><RrIcon name="chev" /></button>}>{() => <ReturnPlatformPop title="Platform" options={platformOptions} selected={selectedPlatforms} onChange={setSelectedPlatforms} />}</RrAnchor></div>
        <DateRangePicker className="rr-fld rr-date" from={filterForm.dateFrom} to={filterForm.dateTo} onChange={range => { const next = { ...filterForm, dateFrom: range.from, dateTo: range.to }; setFilterForm(next); setFilters(next); setPage(1) }} label="Sipariş Tarihi" placeholder="Başlangıç – Bitiş" icon={<RrIcon name="cal" />} chevron={<RrIcon name="chev" />} />
        <div className="rr-fld"><small>Sıralama</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle}><span>{sortOptions.find(option => option[0] === keepSort(filters.status, filterForm.sort))?.[1]}</span><RrIcon name="chev" /></button>}>{close => <div className="rr-pop rr-right" role="listbox" style={{ minWidth: 250 }}>{sortOptions.map(([value, label]) => { const on = value === keepSort(filters.status, filterForm.sort); return <button key={value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => { applyFilterValue('sort', value); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{label}</span></button> })}</div>}</RrAnchor></div>
        {sheetField('Kargo Firması', filterForm.cargo, cargoOptions, value => applyFilterValue('cargo', value))}
        {sheetField('Fatura Durumu', filterForm.invoice, invoiceOptions, value => applyFilterValue('invoice', value))}
        {sheetField('Fatura Türü', filterForm.invoiceType, invoiceTypeOptions, value => applyFilterValue('invoiceType', value))}
        {sheetField('Fatura Bölgesi', filterForm.invoiceRegion, invoiceRegionOptions, value => applyFilterValue('invoiceRegion', value))}
        {sheetField('Etiket Durumu', filterForm.label, labelOptions, value => applyFilterValue('label', value))}
        {sheetField('Sipariş Durumu', filters.status, statusOptions, selectStatus)}
        <div className="rr-fbtns"><button type="button" className="rr-btn" onClick={resetFilters}>Temizle</button><button type="button" className="rr-btn rr-primary" onClick={() => { setFilters(filterForm); setPage(1) }}>Filtrele</button></div>
      </div></RrFilterCard>
    </div>
    {selectionMode && <div className="rr-selbar" role="region" aria-label="Seçim modu"><button type="button" className="rr-btn rr-quiet" onClick={exitSelectionMode}>İptal</button><span className="rr-grow" /><RrAnchor trigger={(open, toggle) => <button type="button" className="rr-btn" aria-haspopup="menu" aria-expanded={open} disabled={selectedOrders.length === 0} onClick={toggle}>İşlemler ({selectedOrders.length})<RrIcon name="chev" /></button>}>{close => <div className="rr-pop rr-right" role="menu">{shopifySelectedOrders.length > 0 && <><button type="button" className="rr-opt" onClick={() => { close(); setBulkShopifyStatus({ kind: 'order', items: shopifySelectedOrders }) }}><RrIcon name="edit" /><span className="rr-grow">Shopify Sipariş Durumu ({shopifySelectedOrders.length})</span></button><button type="button" className="rr-opt" disabled={shopifyInvoiceCreationBlocked} onClick={() => { close(); setBulkShopifyStatus({ kind: 'invoice', items: shopifySelectedOrders }) }}><RrIcon name="edit" /><span className="rr-grow">Shopify Fatura Durumu ({shopifySelectedOrders.length})</span></button></>}{cargoEditableOrders.length > 0 && <button type="button" className="rr-opt" title={cargoChangeHelp} onClick={() => { close(); setCourierOrder({ item: cargoEditableOrders[0], items: cargoEditableOrders }) }}><RrIcon name="truck" /><span className="rr-grow">Kargo Firması Değiştir ({cargoEditableOrders.length})</span></button>}{labelBatchItems.length > 0 && <button type="button" className="rr-opt" onClick={() => { close(); setShippingLabelBatch({ items: labelBatchItems, format: labelSettings.defaultFormat }) }}><RrIcon name="print" /><span className="rr-grow">Etiketleri Yazdır ({labelBatchItems.length})</span></button>}{shopifySelectedOrders.length === 0 && cargoEditableOrders.length === 0 && labelBatchItems.length === 0 && <p className="rr-lead" style={{ padding: '6px 10px' }}>Seçili siparişler için uygun işlem yok.</p>}</div>}</RrAnchor><button type="button" className="rr-btn" onClick={() => togglePageSelection(!allPageSelected)} disabled={!selectablePageItems.length}>{allPageSelected ? 'Seçimi Kaldır' : 'Tümünü Seç'}</button></div>}
    {bulkShopifyStatus && <ShopifyBulkStatusModal kind={bulkShopifyStatus.kind} items={bulkShopifyStatus.items} onClose={() => setBulkShopifyStatus(null)} onFeedback={showBulkNotice} onUpdated={orderIds => setSelectedIds(current => current.filter(id => !orderIds.includes(id)))} />}
    <div className="rr-panel"><div className="rr-panel-h"><div><h2>{tabLabel === 'Tümü' ? 'Tüm Siparişler' : tabLabel}</h2><p>Toplam {totalOrderCount.toLocaleString('tr-TR')} sipariş</p></div><RrPageSize value={pageSize} sizes={[20, 50, 100, 200]} onChange={setPageSize} /></div>
      <div className="rr-tblwrap">{ordersLoading && !all.length ? <RrLoading text="Yerel sipariş kayıtları yükleniyor…" /> : ordersError && !all.length ? <RrError error={ordersError} onRetry={() => void ordersQuery.refetch()} /> : <table className={`rr-tbl rr-has-select${noBuyer ? ' rr-no-buyer' : ''}`}>
        <colgroup><col className="rr-c-order" />{!noBuyer && <col className="rr-c-buyer" />}<col className="rr-c-prod" /><col className="rr-c-price" /><col className="rr-c-cargo" /><col className="rr-c-inv" /><col className="rr-c-act" /></colgroup>
        <thead><tr>
          <ReturnHeadCell label="Sipariş Bilgileri" active={selectedPlatforms.length > 0} extra={<input type="checkbox" className="rr-cb" checked={allPageSelected} disabled={!selectablePageItems.length} onChange={event => togglePageSelection(event.target.checked)} aria-label="Sayfadaki siparişleri seç" style={{ marginRight: 8 }} />} pop={() => <ReturnPlatformPop title="Platform" options={platformOptions} selected={selectedPlatforms} onChange={setSelectedPlatforms} />} />
          {!noBuyer && <ReturnHeadCell label="Alıcı" />}
          <ReturnHeadCell label="Bilgiler" />
          <ReturnHeadCell label="Birim Fiyat" />
          <ReturnHeadCell label="Kargo" active={filters.cargo !== 'ALL'} pop={close => <ReturnColumnPop label="Kargo Firması" options={cargoOptions} value={filterForm.cargo} onPick={value => { applyFilterValue('cargo', value); close() }} />} />
          <ReturnHeadCell label="Fatura" active={filters.invoice !== 'ALL' || filters.invoiceType !== 'ALL' || filters.invoiceRegion !== 'ALL'} pop={invoicePop} />
          <ReturnHeadCell label="İşlemler" active={filters.label !== 'ALL'} pop={close => <ReturnColumnPop label="Etiket Durumu" options={labelOptions} value={filterForm.label} right onPick={value => { applyFilterValue('label', value); close() }} />} />
        </tr></thead>
        <tbody>{!pageItems.length ? <tr><td className="rr-empty-row" colSpan={noBuyer ? 6 : 7}><div className="rr-empty"><span className="rr-ei"><RrIcon name="box" /></span><b>Sipariş Bulunamadı</b><span className="rr-lead">Seçili filtrelerle eşleşen sipariş yok.</span><button type="button" className="rr-btn rr-quiet" style={{ marginTop: 4 }} onClick={resetFilters}>Filtreleri Temizle</button></div></td></tr> : orderRows}</tbody>
      </table>}</div>
      {pageItems.length > 0 && <div className="rr-pager"><span>Toplam <b style={{ color: 'var(--rr-ink)' }}>{totalOrderCount.toLocaleString('tr-TR')}</b> adet</span><div className="rr-pages"><button type="button" aria-label="Önceki" disabled={page <= 1 || ordersQuery.isFetching} onClick={goToPreviousPage}><RrIcon name="left" /></button>{pageList.map(value => <button key={value} type="button" className={value === page ? 'rr-on' : undefined} aria-current={value === page ? 'page' : undefined} disabled={ordersQuery.isFetching} onClick={() => goToPage(value)}>{value}</button>)}<button type="button" aria-label="Sonraki" disabled={!hasNextPage || ordersQuery.isFetching} onClick={goToNextPage}><RrIcon name="right" /></button></div><label className="rr-jump">Sayfaya Git<input className="rr-in" type="number" min={1} max={totalPages} placeholder={`1–${totalPages}`} aria-label="Sayfa numarası" value={jumpPage} onChange={event => setJumpPage(event.target.value)} onKeyDown={event => { if (event.key === 'Enter' && Number(jumpPage) >= 1) { goToPage(Math.floor(Number(jumpPage))); setJumpPage('') } }} /><button type="button" className="rr-btn" onClick={() => { if (Number(jumpPage) >= 1) { goToPage(Math.floor(Number(jumpPage))); setJumpPage('') } }}>Git</button></label></div>}
    </div>
    {selectedOrders.length > 0 && <div className="rr-bulk" role="group" aria-label="Toplu sipariş işlemleri"><span className="rr-grow">{selectedOrders.length} sipariş seçildi{shopifySelectedOrders.length > 0 && shopifySelectedOrders.length < selectedOrders.length && <small className="rr-bulk-note">Shopify toplu işlemleri yalnızca seçili Shopify siparişlerine uygulanır.</small>}</span><button type="button" className="rr-btn rr-quiet" onClick={() => setSelectedIds([])}>Seçimi Temizle</button>{cargoEditableOrders.length > 0 && <button type="button" className="rr-btn" title={cargoChangeHelp} onClick={() => setCourierOrder({ item: cargoEditableOrders[0], items: cargoEditableOrders })}><RrIcon name="truck" />Kargo Firması Değiştir ({cargoEditableOrders.length})</button>}{shopifyBulkButtons}{labelBatchItems.length > 0 && <button type="button" className="rr-btn rr-primary" onClick={() => setShippingLabelBatch({ items: labelBatchItems, format: labelSettings.defaultFormat })}><RrIcon name="print" />Etiketleri Yazdır ({labelBatchItems.length})</button>}</div>}
     {invoiceInfoOrder && <InvoiceInfoModal item={invoiceInfoOrder} onClose={() => setInvoiceInfoOrder(null)} />}{invoiceViewerOrder && <InvoiceViewerModal item={invoiceViewerOrder} onClose={() => setInvoiceViewerOrder(null)} />}{invoiceDraftOrder && <InvoiceDraftModal item={invoiceDraftOrder} providers={providers} onClose={() => setInvoiceDraftOrder(null)} onFeedback={showBulkNotice} />}{invoiceUploadOrder && <InvoiceUploadModal item={invoiceUploadOrder} onClose={() => setInvoiceUploadOrder(null)} onFeedback={showBulkNotice} />}{shopifyStatusOrder && <ShopifyOrderStatusModal item={shopifyStatusOrder} onClose={() => setShopifyStatusOrder(null)} onFeedback={showBulkNotice} />}{shopifyCsvImportOpen && <ShopifyOrderCsvImportModal connections={shopifyConnections} onClose={() => setShopifyCsvImportOpen(false)} onFeedback={showBulkNotice} />}{courierOrder && <CourierChangeModal item={courierOrder.item} items={courierOrder.items} onClose={() => setCourierOrder(null)} onConfirmed={updatedShipments => { client.setQueriesData<Page<Order>>({ queryKey: ['orders', 'page'] }, current => current ? { ...current, items: current.items.map(order => updatedShipments.reduce((updated, shipment) => updated.id === shipment.orderId ? patchOrderShipment(updated, shipment) : updated, order)) } : current); showBulkNotice(`${updatedShipments.length} paket kargo firması “${cargoLabel(updatedShipments[0]?.cargoProviderName)}” olarak güncellendi ve panelde doğrulandı.`) }} />}{shippingLabel && <ShippingLabelModal item={shippingLabel.item} settings={labelSettings} format={shippingLabel.format} onClose={() => setShippingLabel(null)} onPrinted={() => setPrintedLabels(new Set(loadPrintedShippingLabels()))} />}{shippingLabelBatch && <ShippingLabelBatchModal items={shippingLabelBatch.items.filter(item => !isMicroExportLabelBlocked(item))} settings={labelSettings} format={shippingLabelBatch.format} onClose={() => setShippingLabelBatch(null)} onPrinted={() => setPrintedLabels(new Set(loadPrintedShippingLabels()))} />}{singleSyncOpen && <SingleOrderSyncModal activeConnection={marketplaceConnections} onClose={() => setSingleSyncOpen(false)} onSuccess={(connectionCount, orderNo) => { showBulkNotice(orderNo ? `${connectionCount} bağlantıdan #${orderNo} sipariş senkronizasyonu başlatıldı.` : `${connectionCount} bağlantıdan yeni sipariş senkronizasyonu başlatıldı.`, 'info'); void client.invalidateQueries({ queryKey: ['orders'] }); window.setTimeout(() => void client.invalidateQueries({ queryKey: ['orders'] }), 1500); window.setTimeout(() => void client.invalidateQueries({ queryKey: ['orders'] }), 3500) }} />}{previewImage && <ProductImagePreviewModal preview={previewImage} onClose={() => setPreviewImage(null)} />}
    {hepsiburadaPackageOrder && <HepsiburadaPackageCreateModal item={hepsiburadaPackageOrder} onClose={() => setHepsiburadaPackageOrder(null)} onCreated={packageNumber => { setHepsiburadaPackageOrder(null); showBulkNotice(`Hepsiburada paketi ${packageNumber} oluşturuldu; sipariş read-back kuyruğa alındı.`); void client.invalidateQueries({ queryKey: ['orders'] }); void client.invalidateQueries({ queryKey: ['shipments'] }) }} />}
  </section>
}
// Order status as a ring: how far the order has come, coloured by its state.
function orderRingState(status: string): [RingTone, number, string] {
  switch (status.trim().toUpperCase()) {
    case 'NEW': case 'CREATED': case 'AWAITING': case 'PENDING': return ['prog', 0.2, 'Yeni']
    case 'PROCESSING': case 'PICKING': case 'PREPARING': case 'READY_TO_SHIP': case 'INVOICED': return ['wait', 0.45, 'İşleme Alındı']
    case 'SHIPPED': case 'IN_TRANSIT': return ['move', 0.7, 'Kargoda']
    case 'DELIVERED': return ['ok', 1, 'Teslim Edildi']
    case 'CANCELLED': return ['bad', 1, 'İptal Edildi']
    case 'UNDELIVERED': return ['bad', 0.7, 'Teslim Edilemedi']
    case 'RETURN_IN_TRANSIT': return ['move', 0.5, 'İade Taşımada']
    case 'RETURNED': return ['off', 1, 'İade Edildi']
    case 'PAYMENT_PENDING': return ['wait', 0.1, 'Ödeme Bekleniyor']
    case 'ON_HOLD': return ['off', 0, 'Askıda']
    case 'UNVERIFIED': return ['off', 0, 'Doğrulanmadı']
    default: return ['off', 0, titleTr(statusLabel(status))]
  }
}
// The line under the ring: time left, days late, or when the order was shipped, delivered or cancelled.
function orderRingDetail(delivery: { label: string; value: string; note: string | null; overdue: boolean }): { text: string; cls?: string } {
  const late = /^Siparişiniz (\d+) gün gecikmiştir!$/u.exec(delivery.value)
  if (late) return { text: `${late[1]} gün gecikti`, cls: 'bad' }
  if (delivery.label === 'Kalan süre') return { text: /kaldı|doldu|bekleniyor|göndermedi/u.test(delivery.value) ? delivery.value : `${delivery.value} kaldı`, cls: delivery.overdue ? 'crit' : undefined }
  if (delivery.label === 'Taşıma durumunda') return { text: delivery.note ?? delivery.value }
  return { text: delivery.value }
}
// A checkbox with its explanation. On phones the explanation stays folded behind an info button and opens on a tap.
function RrHintCheck({ checked, onChange, label, hint }: { checked: boolean; onChange: (value: boolean) => void; label: string; hint: string }) {
  const [hintOpen, setHintOpen] = useState(false)
  return <><label className="rr-cklabel"><input type="checkbox" className="rr-cb" checked={checked} onChange={event => onChange(event.target.checked)} />{label}<button type="button" className="rr-hint-btn" aria-label="Açıklamayı göster" aria-expanded={hintOpen} onClick={event => { event.preventDefault(); setHintOpen(open => !open) }}><RrIcon name="info" size={15} /></button></label><p className={`rr-lead rr-hint${hintOpen ? ' rr-on' : ''}`} style={{ marginTop: -8 }}>{hint}</p></>
}
