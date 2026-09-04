import { Fragment, useEffect, useRef, useState, type ChangeEvent, type CSSProperties, type FormEvent, type KeyboardEvent, type MouseEvent } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, loadAllPages } from '../../shared/api'
import { Busy, ErrorBox } from '../../shared/components'
import '../../styles/orders.css'
import '../../styles/returns.css'
import '../../styles/typography.css'
import { code128Bars, loadPrintedShippingLabels, loadShippingLabelSettings, markShippingLabelPrinted, printedShippingLabelKey, shippingLabelFields, type ShippingLabelBlock, type ShippingLabelField, type ShippingLabelFormat, type ShippingLabelSettings } from '../shipping'
type Page<T> = { items: T[]; nextCursor: string | null; hasMore: boolean; totalCount?: number | null }
type Connection = { id: string; publicId: string; platformCode: string; environment: string; displayName: string; externalStoreId: string; status: string; apiVersion: string; lastTestedAt: string | null; lastSuccessAt: string | null; lastErrorCode: string | null; hasCredential: boolean; externalWritesEnabled: boolean; version: number }
type Capability = { code: string; supportLevel: string; sourceUrl: string | null; verifiedAt: string | null; constraintsJson: string | null; evidenceNote: string | null; version: number }
type SyncPolicy = { id: string; resourceType: string; intervalSeconds: number; overlapSeconds: number; jitterSeconds: number; enabled: boolean; version: number; lastSuccessAt: string | null; lastModifiedWatermark: string | null; healthStatus?: string; recoveryGapStatus?: string; recoveryGapDays?: number | null; lastAttemptAt?: string | null; consecutiveFailureCount?: number; lastRequestCount?: number; lastReceivedCount?: number; lastChangedCount?: number; lastInsertedCount?: number; lastUpdatedCount?: number; lastSkippedCount?: number; lastFailedCount?: number; lastRetryCount?: number; lastRateLimitCount?: number }
type OrderLine = { id: string; sku: string; barcode: string | null; title: string; quantity?: number; orderedQuantity: number; cancelledQuantity: number; shippedQuantity: number; deliveredQuantity: number; returnedQuantity: number; unitPrice: number; vatRate: number; rawStatus: string; variantId: string | null; modelCode: string | null; optionSignature: string | null; imageUrl: string | null }
type Order = { id: string; orderNumber: string; derivedStatus: string; currency: string; grossAmount: number; discountAmount: number; netAmount: number; orderedAt: string; lineCount: number; packageCount: number; version: number; connectionId: string | null; platformCode: string; platformDisplayName: string; customerName: string; customerEmail: string | null; customerTaxOrIdentityNumber: string | null; orderType: string; isMicroExport: boolean; shipmentAddressJson: string; invoiceAddressJson: string; shipmentDueAt: string | null; isDeadlineCritical: boolean; invoiceStatus: string; invoiceId: string | null; invoiceDocumentUrl: string | null; cargoProviderName: string | null; cargoTrackingNumber: string | null; primaryImageUrl: string | null; productQuantity: number; lines: OrderLine[] | null; packages: Shipment[] | null }
type OrderSummary = { all: number; new: number; processing: number; shipped: number; delivered: number; resent: number; onHold: number; cancelled: number; returned: number; returnInTransit: number; partiallyCancelled: number; manualReview: number }
type OrderSort = 'DATE_DESC' | 'DATE_ASC' | 'DUE_DESC' | 'DUE_ASC'
type OrderFilters = { search: string; status: string; platform: string; listing: string; cargo: string; invoice: string; invoiceType: string; invoiceRegion: string; label: string; sort: OrderSort; dateFrom: string; dateTo: string }
type InvoiceViewer = { id: string; orderNumber: string; invoiceType: string; status: string; currency: string; payableTotal: number; taxTotal: number; invoiceNumber: string | null; lines: Array<{ id: string; description: string; sku: string | null; unit: string; quantity: number; vatRate: number; lineTotal: number }>; documents: Array<{ id: string; documentType: string; sha256: string }> }
const initialOrderFilters: OrderFilters = { search: '', status: 'ALL', platform: 'ALL', listing: 'ALL', cargo: 'ALL', invoice: 'ALL', invoiceType: 'ALL', invoiceRegion: 'ALL', label: 'ALL', sort: 'DATE_DESC', dateFrom: '', dateTo: '' }
const orderStatusRank: Record<string, number> = { MANUAL_REVIEW: 110, RETURNED: 100, RETURN_IN_TRANSIT: 90, DELIVERED: 80, UNDELIVERED: 70, SHIPPED: 60, READY_TO_SHIP: 50, ON_HOLD: 40, PROCESSING: 30, PARTIALLY_CANCELLED: 20, NEW: 10, CANCELLED: 0 }
function aggregateOrderStatus(statuses: string[]) {
  const values = statuses.map(status => status.toUpperCase())
  if (!values.length) return 'NEW'
  if (values.includes('MANUAL_REVIEW')) return 'MANUAL_REVIEW'
  const operational = values.filter(status => status !== 'CANCELLED' && status !== 'RETURNED')
  if (operational.length) return operational.sort((left, right) => (orderStatusRank[right] ?? 110) - (orderStatusRank[left] ?? 110))[0]
  return values.includes('RETURNED') ? 'RETURNED' : 'CANCELLED'
}
async function loadOrderPage(filters: OrderFilters, limit: number, after: string | null, page = 1): Promise<Page<Order>> {
  const params = new URLSearchParams({ limit: String(limit) })
  if (after) params.set('after', after)
  if (filters.status !== 'ALL') params.set('status', filters.status)
  if (filters.search.trim()) params.set('search', filters.search.trim())
  if (filters.platform !== 'ALL') params.set('platform', filters.platform)
  if (filters.listing !== 'ALL') params.set('listing', filters.listing)
  if (filters.cargo !== 'ALL') params.set('cargo', filters.cargo)
  if (filters.dateFrom) params.set('dateFrom', `${filters.dateFrom}T00:00:00.000Z`)
  if (filters.dateTo) params.set('dateTo', `${filters.dateTo}T23:59:59.999Z`)
  params.set('sort', filters.sort)
  if (!after && page > 1) params.set('page', String(page))
  return hubApi<Page<Order>>(`/orders?${params.toString()}`)
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
function productImageFallbackUrl(barcode: string | null | undefined) {
  const value = barcode?.trim()
  return value ? `/api/v1/orders/product-image?barcode=${encodeURIComponent(value)}` : null
}
function ProductImage({ url, fallbackUrl, alt, onClick, className }: { url: string | null; fallbackUrl?: string | null; alt: string; onClick?: () => void; className?: string }) {
  const [failed, setFailed] = useState(false)
  const [source, setSource] = useState(url ?? fallbackUrl ?? '')
  useEffect(() => { setFailed(false); setSource(url ?? fallbackUrl ?? '') }, [url, fallbackUrl])
  if (failed || !source) return <span className="reference-product-placeholder" aria-label="Ürün görseli yüklenemedi">▧</span>
  const image = <img className={className} src={source} alt={alt} loading="lazy" decoding="async" referrerPolicy="no-referrer" onError={() => source !== fallbackUrl && fallbackUrl ? setSource(fallbackUrl) : setFailed(true)} />
  return onClick ? <button type="button" className="product-image-button" onClick={event => { event.preventDefault(); event.stopPropagation(); onClick() }} aria-label={`${alt} görselini büyüt`}>{image}</button> : image
}
function productLineQuantity(line: Pick<OrderLine, 'quantity' | 'orderedQuantity'>) {
  return line.quantity ?? line.orderedQuantity
}
type OrderDetail = { id: string; orderNumber: string; derivedStatus: string; currency: string; grossAmount: number; discountAmount: number; netAmount: number; orderedAt: string; connectionId: string | null; platformCode: string; platformDisplayName: string; customerName: string; customerEmail: string | null; customerPhone: string | null; customerTaxOrIdentityNumber: string | null; orderType: string; isMicroExport: boolean; isEInvoiceAvailable: boolean | null; shipmentAddressJson: string; invoiceAddressJson: string; shipmentDueAt: string | null; invoiceStatus: string; invoiceDocumentUrl: string | null; lines: OrderLine[]; packages: Shipment[]; version: number }
type CreatedInvoice = { id: string; version: number }
type InvoiceOperation = CreatedInvoice & { status: string; invoiceNumber: string | null; externalReference: string | null; lastErrorCode: string | null }
type Shipment = { id: string; orderId: string; orderNumber: string; externalPackageId: string; status: string; rawStatus: string; cargoTrackingNumber: string | null; cargoProviderName: string | null; statusOccurredAt: string; version: number; isResend: boolean }
type ShipmentDetail = { package: Shipment; allowedActions: string[]; supportedLabelFormats: string[]; isStageConnection: boolean; documents: Array<{ id: string; documentKind: string; format: string; source: string; documentVersion: number; createdAt: string; expiresAt: string | null }> }
type ReturnClaim = { id: string; externalClaimId: string; orderNumber: string; status: string; rawStatus: string; reasonText: string | null; actionDueAt: string | null; approvedAt?: string | null; systemNote?: string | null; sellerDescription?: string | null; platformApprovalReason?: string | null; platformDescription?: string | null; version: number; customerName: string; orderedAt: string | null; orderAmount: number; currency: string; cargoProviderName: string | null; cargoTrackingNumber: string | null; primaryImageUrl: string | null; productCount: number; primaryBarcode: string | null; lines: OrderLine[] | null; packageNumber: string | null; invoiceStatus: string; grossAmount: number; discountAmount: number; isMicroExport: boolean }
async function loadAllReturns(): Promise<Page<ReturnClaim>> {
  return loadAllPages<ReturnClaim>('/returns')
}
type ReturnLine = { id: string; externalLineId: string; orderLineId: string; sku: string; barcode: string | null; title: string; quantity: number; disposedQuantity: number; remainingQuantity: number; unitPrice: number; imageUrl: string | null; hasInventoryMapping: boolean; color?: string | null; size?: string | null; approvedAt?: string | null; systemNote?: string | null; reasonText?: string | null; sellerDescription?: string | null; platformApprovalReason?: string | null; platformDescription?: string | null }; type ReturnDetail = Omit<ReturnClaim, 'lines'> & { reasonCode: string | null; allowedActions: string[]; lines: ReturnLine[] | null; stockDispositionAvailable: boolean }; type ReturnIssueReason = { id: string; name: string; evidenceRequired: boolean }
type LocalCategory = { id: string; name: string; path: string; depth: number; isLeaf: boolean; isActive: boolean; version: number }
type LocalBrand = { id: string; name: string; isActive: boolean; version: number }
type LocalAttribute = { id: string; code: string; name: string; dataType: string; isActive: boolean; version: number; roles?: string[] | null; values: { id: string; value: string; sortOrder: number; isActive: boolean }[] }
type ReferenceSyncAccepted = { value?: string; id?: string; jobId?: string; Value?: string; Id?: string; JobId?: string; error?: { message?: string; Message?: string } | null; Error?: { message?: string; Message?: string } | null }
type ReferenceSyncJob = { job: { status: string; lastErrorCode?: string | null; lastErrorSummary?: string | null } }
type ReferenceSyncJobSummary = { id: string; connectionId?: string | null; jobType: string; status: string; createdAt: string; lastErrorCode?: string | null; lastErrorSummary?: string | null }
type CategoryRequirementView = { attributeId: string; isRequired: boolean; allowsCustomValue: boolean; displayOrder: number; role: 'ATTRIBUTE' | 'OPTION'; attribute: LocalAttribute }
type AttributeRequirementCommand = { attributeId: string; isRequired: boolean; allowsCustomValue: boolean; displayOrder: number; role: 'ATTRIBUTE' | 'OPTION' }
type ReferenceItem = { externalId: string; parentExternalId: string | null; name: string; path: string; depth: number; isLeaf: boolean; isActive: boolean; isRequired: boolean | null; allowsCustomValue: boolean | null; allowsMultipleValues: boolean | null }
type ReferenceData = { snapshotId: string; resourceType: string; fetchedAt: string; items: ReferenceItem[] }
type CatalogMapping = { id: string; connectionId: string; snapshotId: string; localId: string; scopeExternalId: string; externalId: string; status: string; verifiedAt: string | null; version: number }
type MappingTransferBundle = { format: 'RAVENCIA_MAPPING_BUNDLE'; version: 1; exportedAt: string; categories: LocalCategory[]; attributes: LocalAttribute[]; categoryMappings: CatalogMapping[]; attributeMappings: CatalogMapping[]; attributeValueMappings: CatalogMapping[] }
type BrandMappingTransferBundle = { format: 'RAVENCIA_BRAND_MAPPING_BUNDLE'; version: 1; exportedAt: string; brands: LocalBrand[]; mappings: CatalogMapping[] }
type MappingTransferScope = 'categories' | 'options' | 'attributes' | 'mappings'
type SearchOption = { value: string; label: string; description?: string }
const mappingPlatformDefinitions = [
  { code: 'TRENDYOL', label: 'Trendyol' },
  { code: 'HEPSIBURADA', label: 'Hepsiburada' },
  { code: 'N11', label: 'n11' },
  { code: 'PAZARAMA', label: 'Pazarama' },
  { code: 'PTTAVM', label: 'PttAVM' },
  { code: 'SHOPIFY', label: 'Shopify' }
] as const
type MappingPlatformCode = typeof mappingPlatformDefinitions[number]['code']

function SearchableSelect({ label, value, options, placeholder, disabled = false, onChange }: { label: string; value: string; options: SearchOption[]; placeholder: string; disabled?: boolean; onChange: (value: string) => void }) {
  const selected = options.find(option => option.value === value)
  const [query, setQuery] = useState('')
  const [open, setOpen] = useState(false)
  const normalized = query.trim().toLocaleLowerCase('tr-TR')
  const filtered = options.filter(option => !normalized || `${option.label} ${option.description ?? ''}`.toLocaleLowerCase('tr-TR').includes(normalized)).slice(0, normalized ? 500 : 100)
  function toggleMenu() { if (disabled) return; setQuery(''); setOpen(current => !current) }
  function choose(option: SearchOption) { onChange(option.value); setQuery(''); setOpen(false) }
  function keyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Escape') setOpen(false)
    if (event.key === 'Enter' && open && filtered.length) { event.preventDefault(); choose(filtered[0]) }
  }
  const listId = `search-select-${label.toLocaleLowerCase('tr-TR').replace(/[^a-z0-9]+/g, '-')}`
  return <label className={`searchable-select${open ? ' open' : ''}${disabled ? ' disabled' : ''}`}><span>{label}</span><button type="button" className="searchable-select-trigger" aria-expanded={open} aria-haspopup="listbox" disabled={disabled} onClick={toggleMenu}><span>{selected?.label ?? placeholder}</span><i aria-hidden="true">⌄</i></button>{open && <div id={listId} className="searchable-select-menu" role="listbox"><div className="searchable-select-search"><span aria-hidden="true">⌕</span><input autoFocus role="combobox" aria-label={`${label} ara`} aria-controls={listId} aria-expanded={open} aria-autocomplete="list" value={query} placeholder={placeholder} onKeyDown={keyDown} onChange={event => { setQuery(event.target.value); if (value) onChange('') }} onBlur={() => window.setTimeout(() => setOpen(false), 120)} /></div><div className="searchable-select-options">{filtered.length ? filtered.map(option => <button type="button" role="option" aria-selected={option.value === value} key={option.value} onMouseDown={event => { event.preventDefault(); choose(option) }}><span>{option.label}</span>{option.description && <small>{option.description}</small>}</button>) : <span className="searchable-select-empty">Eşleşen kayıt bulunamadı.</span>}</div></div>}</label>
}
function MappingViewTabs({ active }: { active: 'category' | 'brand' | 'attribute' }) {
  return <nav className="mapping-view-tabs" aria-label="Eşleştirme türü" role="tablist"><Link role="tab" aria-selected={active === 'category'} className={active === 'category' ? 'active' : ''} to="/mappings/categories">Kategori Eşleme</Link><Link role="tab" aria-selected={active === 'brand'} className={active === 'brand' ? 'active' : ''} to="/mappings/categories?view=brands">Marka Eşleme</Link></nav>
}
function idempotency() { return crypto.randomUUID() }
function wait(milliseconds: number) { return new Promise(resolve => window.setTimeout(resolve, milliseconds)) }
function displayAttributeName(value: string) { return value.replace(/\[(?:A-)?TDG\][\s_-]*/gi, '').replace(/^\(?(?:A-)?TDG\)?[\s_-]*/gi, '').replace(/_/g, ' ').replace(/\s+/g, ' ').trim() }
type IntegrationFeedback = { kind: 'success' | 'error'; message: string }
function IntegrationFeedbackToast({ feedback, onClose }: { feedback: IntegrationFeedback | null; onClose: () => void }) {
  if (!feedback) return null
  return <div className={`operation-feedback-toast ${feedback.kind}`} role={feedback.kind === 'error' ? 'alert' : 'status'} aria-live="polite"><span className="operation-feedback-icon" aria-hidden="true">{feedback.kind === 'success' ? '✓' : '!'}</span><div><strong>{feedback.kind === 'success' ? 'İşlem başarılı' : 'İşlem başarısız'}</strong><p>{feedback.message}</p></div><button type="button" onClick={onClose} aria-label="Bildirimi kapat">×</button></div>
}
function Empty({ children }: { children: string }) { return <div className="empty"><strong>Kayıt yok</strong><p>{children}</p></div> }
const statusLabels: Record<string, string> = { APPROVED: 'Onaylandı', COMPLETED: 'Tamamlandı', REJECTED: 'Reddedildi', CANCELLED: 'İptal edildi', REQUESTED: 'Talep oluşturuldu', CREATED: 'Oluşturuldu', ACTION_REQUIRED: 'İşlem bekliyor', WAITING_FOR_SHIPMENT: 'Kargo bekliyor', IN_TRANSIT: 'Taşımada', RETURN_IN_TRANSIT: 'İade taşımada', SHIPPED: 'Kargoda', SUSPENDED: 'Askıya alındı', ON_HOLD: 'Beklemede', HEALTHY: 'Sağlıklı', DEGRADED: 'Yavaşlıyor', DELAYED: 'Gecikiyor', OFFLINE: 'Çevrim dışı' }
function statusLabel(value: string) { return statusLabels[value.toUpperCase()] ?? value }
function Badge({ value }: { value: string }) { const normalized = value.toUpperCase(); const tone = normalized === 'SUPPORTED' || normalized === 'ACTIVE' || normalized === 'DELIVERED' || normalized === 'APPROVED' || normalized === 'HEALTHY' ? 'good' : normalized === 'UNKNOWN' || normalized === 'DRAFT' || normalized === 'VERIFIED' || normalized === 'DELAYED' || normalized === 'DEGRADED' ? 'warn' : 'neutral'; return <span className={`badge ${tone} status-${normalized.toLowerCase()}`} title={value}>{statusLabel(value)}</span> }
function DateText({ value }: { value: string | null }) { return <>{value ? new Date(value).toLocaleString('tr-TR') : '—'}</> }

function usableShipmentDueAt(value: string | null) { if (!value) return null; const timestamp = new Date(value).getTime(); return Number.isFinite(timestamp) && timestamp >= Date.UTC(2000, 0, 1) ? value : null }
function remainingText(value: string | null) {
  if (!value) return 'Termin bilgisi gelmedi'
  const ms = new Date(value).getTime() - Date.now()
  if (ms <= 0) return 'Süre doldu'
  const totalMinutes = Math.floor(ms / 60_000)
  const days = Math.floor(totalMinutes / (24 * 60))
  const hours = Math.floor((totalMinutes % (24 * 60)) / 60)
  const minutes = totalMinutes % 60
  if (days === 0 && hours === 0) return `${minutes} dakika kaldı`
  if (days === 0) return `0 gün ${hours} saat ${minutes > 0 ? `${minutes} dakika` : ''}`.trim()
  return `${days} gün ${hours} saat ${minutes > 0 ? `${minutes} dakika` : ''}`.trim()
}
function safeJson(value: string) { try { return JSON.parse(value || '{}') as Record<string, unknown> } catch { return {} } }
function meaningfulText(value: unknown): value is string { return typeof value === 'string' && /[\p{L}\p{N}]/u.test(value) }
function customerText(value: string) { return meaningfulText(value) ? value : 'Trendyol müşteri bilgisini maskeledi' }
function addressText(value: string) { const source = safeJson(value); const data = [source.shipmentAddress, source.invoiceAddress, source.address].find(item => item && typeof item === 'object') as Record<string, unknown> | undefined ?? source; const parts = ['fullAddress', 'address1', 'address2', 'neighborhood', 'district', 'countyName', 'city', 'countryCode', 'postalCode'].map(key => data[key]).filter(meaningfulText); return parts.length ? [...new Set(parts)].join(' · ') : 'Trendyol adres bilgisini maskeledi' }
function optionRows(value: string | null) { const parts = (value ?? '').split(/[|,·;]+/).map(item => item.trim()).filter(Boolean); const fields = parts.map(part => { const match = part.match(/^([^:=-]+)\s*[:=-]\s*(.+)$/); return match ? { label: match[1].trim(), value: match[2].trim() } : null }).filter((item): item is { label: string; value: string } => item !== null); return fields.length ? fields : value ? [{ label: 'Seçenek', value }] : [] }
function returnLineField(line: OrderLine | ReturnLine, labels: string[], property?: keyof ReturnLine) { const extra = line as Partial<ReturnLine>; const direct = property ? String(extra[property] ?? '').trim() : ''; if (direct) return direct; const signature = (line as OrderLine & { optionSignature?: string | null }).optionSignature ?? null; return optionRows(signature).find(option => labels.some(label => option.label.toLocaleLowerCase('tr-TR').includes(label.toLocaleLowerCase('tr-TR'))))?.value ?? '—' }
function longDate(value: string) { return new Intl.DateTimeFormat('tr-TR', { day: '2-digit', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' }).format(new Date(value)) }
function deliveryText(item: Order) { const shipment = item.packages?.[0]; if (item.derivedStatus === 'CANCELLED') return { label: 'İptal tarihi', value: shipment?.statusOccurredAt ? longDate(shipment.statusOccurredAt) : 'İptal zamanı bekleniyor', note: null, overdue: false }; if (item.derivedStatus === 'DELIVERED') return { label: 'Teslim edildi', value: shipment?.statusOccurredAt ? longDate(shipment.statusOccurredAt) : 'Teslimat zamanı bekleniyor', note: null, overdue: false }; if (['SHIPPED', 'UNDELIVERED'].includes(item.derivedStatus)) return { label: 'Taşıma durumunda', value: shipment?.statusOccurredAt ? longDate(shipment.statusOccurredAt) : 'Kargo zamanı bekleniyor', note: 'Kargoya teslim edildi', overdue: false }; const dueAt = usableShipmentDueAt(item.shipmentDueAt); const due = dueAt ? new Date(dueAt).getTime() : Number.NaN; const overdueDays = Number.isFinite(due) ? Math.floor((Date.now() - due) / 86_400_000) : 0; if (overdueDays > 0) return { label: '', value: `Siparişiniz ${overdueDays} gün gecikmiştir!`, note: 'Siparişinizi en kısa sürede kargoya teslim etmelisiniz.', overdue: true }; return { label: 'Kalan süre', value: dueAt ? remainingText(dueAt) : item.isMicroExport ? 'Trendyol termin bilgisi göndermedi' : 'Termin zamanı bekleniyor', note: null, overdue: false } }
function InvoiceInfoModal({ item, onClose }: { item: Order; onClose: () => void }) {
  const detail = useQuery({ queryKey: ['invoice-info-order', item.id], queryFn: () => hubApi<OrderDetail>(`/orders/${item.id}`) })
  const order = detail.data
  return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={onClose}><section className="workspace-modal invoice-info-modal" role="dialog" aria-modal="true" aria-labelledby="invoice-info-title" onMouseDown={event => event.stopPropagation()}><header><h2 id="invoice-info-title">Fatura &amp; Adres Bilgileri</h2><button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat">×</button></header>{detail.isLoading ? <Busy text="Fatura ve adres bilgileri API’den yükleniyor…" /> : detail.isError || !order ? <ErrorBox error={detail.error} /> : <><div className="invoice-address-sheet"><section><h3>Teslimat Adresi</h3><dl><dt>Ad-soyad:</dt><dd>{order.customerName}</dd><dt>Adres:</dt><dd>{addressText(order.shipmentAddressJson)}</dd></dl></section><section><h3>Fatura Adresi</h3><dl><dt>Ad-soyad:</dt><dd>{order.customerName}</dd><dt>Adres:</dt><dd>{addressText(order.invoiceAddressJson)}</dd><dt>E-Fatura Mükellefi:</dt><dd>{order.isEInvoiceAvailable === null ? 'Bilgi gelmedi' : order.isEInvoiceAvailable ? 'Evet' : 'Hayır'}</dd></dl></section><section className="invoice-contact"><dl><dt>E-posta Adresi:</dt><dd>{order.customerEmail ?? 'Bilgi gelmedi'}</dd><dt>Telefon Numarası:</dt><dd>{order.customerPhone ?? 'Bilgi gelmedi'}</dd></dl></section><p>Faturanızı <strong>Fatura işlemleri</strong> altındaki <strong>Fatura Yükle</strong> alanına PDF, JPEG, JPG veya PNG biçiminde ekleyebilirsiniz. Entegratörle çalışıyorsanız fatura bağlantısını bu alandan takip edebilirsiniz.</p></div><footer><button type="button" className="secondary" onClick={onClose}>Vazgeç</button><button type="button" onClick={() => window.print()}>Yazdır</button></footer></>}</section></div>
}

function InvoiceDraftModal({ item, provider, onClose }: { item: Order; provider: Connection | null; onClose: () => void }) {
  const client = useQueryClient()
  const [message, setMessage] = useState('')
  const [created, setCreated] = useState(false)
  const [refreshQueued, setRefreshQueued] = useState(false)
  const [activeInvoiceId, setActiveInvoiceId] = useState(item.invoiceId)
  const detail = useQuery({ queryKey: ['invoice-draft-order', item.id], queryFn: () => hubApi<OrderDetail>(`/orders/${item.id}`), refetchInterval: query => refreshQueued && !query.state.data?.packages?.length ? 3000 : false })
  const order = detail.data
  const packageId = order?.packages[0]?.id
  useEffect(() => { if (refreshQueued && packageId) { setRefreshQueued(false); setMessage('Paket bilgisi geldi. Faturayı oluşturabilirsiniz.') } }, [packageId, refreshQueued])
  useEffect(() => {
    if (!refreshQueued || packageId) return
    const timeout = window.setTimeout(() => {
      setRefreshQueued(false)
      setMessage('Paket bilgisi hâlâ gelmedi. Trendyol paketi oluşturduğunda fatura oluşturma açılacaktır.')
    }, 12000)
    return () => window.clearTimeout(timeout)
  }, [packageId, refreshQueued])
  const refreshOrder = useMutation({
    mutationFn: () => {
      if (!order?.connectionId) throw new Error('Siparişin Trendyol bağlantısı bulunamadı.')
      return hubApi(`/connections/${order.connectionId}/order-sync-jobs`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify({ externalOrderId: order.orderNumber }) })
    },
    onSuccess: async () => { setRefreshQueued(true); setMessage('Sipariş ve paket bilgisi yenileme kuyruğa alındı. Paket geldiğinde fatura oluşturma düğmesi açılacak.'); await client.invalidateQueries({ queryKey: ['invoice-draft-order', item.id] }); await client.invalidateQueries({ queryKey: ['orders'] }) },
    onError: error => setMessage(error instanceof Error ? error.message : 'Sipariş bilgisi yenilenemedi.')
  })
  const create = useMutation({
    mutationFn: async () => {
      if (!order || !packageId || !provider?.hasCredential) throw new Error('Aktif ve yetkili Trendyol E-Faturam bağlantısı gereklidir.')
      let invoice = activeInvoiceId
        ? await hubApi<InvoiceOperation>(`/invoices/${activeInvoiceId}`)
        : await hubApi<InvoiceOperation>('/invoices', { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify({ orderId: order.id, packageId, providerConnectionId: provider.id, originalInvoiceId: null }) })
      setActiveInvoiceId(invoice.id)
      if (invoice.status === 'DRAFT' || invoice.status === 'VALIDATION_FAILED')
        invoice = await hubApi<InvoiceOperation>(`/invoices/${invoice.id}/validate`, { method: 'POST', headers: { 'If-Match': `"v${invoice.version}"` } })
      const canRetryPreProviderFailure = !invoice.externalReference && ((invoice.status === 'REJECTED' && ['EFATURAM_FISCAL_PAYLOAD_INVALID', 'EFATURAM_REQUEST_REJECTED', 'EFATURAM_APPLICATION_NOT_ACTIVE'].includes(invoice.lastErrorCode ?? '')) || (invoice.status === 'SUBMITTING' && ['EFATURAM_AUTHENTICATION_FAILED', 'EFATURAM_ACCESS_TOKEN_REJECTED', 'EFATURAM_INVOICE_CREATE_PRIVILEGE_MISSING'].includes(invoice.lastErrorCode ?? '')))
      if (invoice.status === 'READY' || canRetryPreProviderFailure) {
        await hubApi(`/invoices/${invoice.id}/submit-jobs`, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${invoice.version}"` }, body: JSON.stringify({ password: '', confirmed: false }) })
        invoice = await hubApi<InvoiceOperation>(`/invoices/${invoice.id}`)
      }
      for (let attempt = 0; attempt < 30 && invoice.status === 'SUBMITTING'; attempt++) {
        await wait(1000)
        invoice = await hubApi<InvoiceOperation>(`/invoices/${invoice.id}`)
      }
      if (['SUBMITTED', 'ACCEPTED', 'MARKETPLACE_PENDING', 'COMPLETED'].includes(invoice.status)) return invoice
      if (invoice.status === 'SUBMITTING') throw new Error('Fatura E-Faturam’a gönderildi; sağlayıcı yanıtı henüz bekleniyor.')
      if (invoice.lastErrorCode === 'EFATURAM_ACCESS_TOKEN_REJECTED') throw new Error('E-Faturam oturum anahtarını reddetti. Canlı E-Faturam bağlantısını ve hesap yetkisini kontrol edin.')
      if (invoice.lastErrorCode === 'EFATURAM_APPLICATION_NOT_ACTIVE') throw new Error('E-Faturam, gönderen hesabın Stage fatura uygulamasını aktif görmüyor. Bu hesap için E-Arşiv API hizmetinin sağlayıcı tarafından etkinleştirilmesi gerekiyor.')
      throw new Error(invoice.lastErrorCode ? `E-Faturam faturayı oluşturamadı: ${invoice.lastErrorCode}` : `E-Faturam faturası oluşturulamadı (${invoice.status}).`)
    },
    onSuccess: async invoice => {
      setCreated(true)
      setMessage(invoice.invoiceNumber ? `Fatura E-Faturam’da oluşturuldu: ${invoice.invoiceNumber}` : 'Fatura E-Faturam’da başarıyla oluşturuldu.')
      await client.invalidateQueries({ queryKey: ['orders'] })
      await client.invalidateQueries({ queryKey: ['invoice-draft-order', item.id] })
      window.setTimeout(onClose, 1400)
    },
    onError: error => setMessage(error instanceof Error ? error.message : 'Fatura oluşturulamadı.')
  })
  const money = (value: number) => value.toLocaleString('tr-TR', { style: 'currency', currency: order?.currency ?? item.currency })
  return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={onClose}><section className="workspace-modal invoice-draft-modal" role="dialog" aria-modal="true" aria-labelledby="invoice-draft-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="invoice-draft-title">Fatura Oluştur</h2><p>#{item.orderNumber} siparişi için fatura bilgilerini kontrol edin.</p></div><button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat">×</button></header>{detail.isLoading ? <Busy text="Müşteri, adres ve ürün bilgileri API’den yükleniyor…" /> : detail.isError || !order ? <ErrorBox error={detail.error} /> : <><div className="invoice-draft-notice"><span aria-hidden="true">i</span><div><strong>Faturanız Trendyol E-Faturam sağlayıcısında oluşturulacaktır.</strong><p>Müşteri, fatura adresi, ürün, miktar ve vergi bilgileri Trendyol sipariş snapshot’ından alınır. Fatura oluşturulduğunda önce mali olarak doğrulanır, ardından E-Faturam’a gönderilir. Bağlantı, credential, tekrar koruması ve sağlayıcı yanıt kontrolü korunur.</p></div></div><div className="invoice-draft-customer"><span><small>Müşteri / unvan</small><strong>{customerText(order.customerName)}</strong></span><span><small>TC / vergi no</small><strong>{meaningfulText(order.customerTaxOrIdentityNumber) ? order.customerTaxOrIdentityNumber : 'Trendyol bu bilgiyi maskeledi'}</strong></span><span><small>Fatura adresi</small><strong>{addressText(order.invoiceAddressJson)}</strong></span></div><div className="invoice-draft-table" role="table"><div className="invoice-draft-head" role="row"><strong>Ürün Bilgisi</strong><strong>KDV Oranı</strong><strong>KDV Tutarı</strong><strong>Miktar</strong><strong>Birim Fiyatı</strong><strong>Toplam Tutar</strong></div>{order.lines.map(line => { const total = line.unitPrice * line.orderedQuantity; const vat = total * line.vatRate / (100 + line.vatRate); return <div role="row" key={line.id}><span><strong>{line.title}</strong><small>{line.sku}{line.modelCode ? ` · Model ${line.modelCode}` : ''}</small></span><span>%{line.vatRate}</span><span>{money(vat)}</span><span>{line.orderedQuantity} adet</span><span>{money(line.unitPrice)}</span><strong>{money(total)}</strong></div> })}</div><div className="invoice-draft-total"><span>İndirim <strong>{money(order.discountAmount)}</strong></span><span>Fatura toplamı <strong>{money(order.netAmount)}</strong></span></div>{!packageId && <div className="invoice-draft-recovery notice" role="status"><span>Bu siparişin paket bilgisi henüz gelmedi. Fatura paket oluşmadan oluşturulamaz.</span><button type="button" className="secondary" disabled={refreshOrder.isPending || refreshQueued || !order.connectionId} onClick={() => refreshOrder.mutate()}>{refreshOrder.isPending || refreshQueued ? 'Paket bilgisi yenileniyor…' : 'Paket bilgisini yenile'}</button></div>}{!provider?.hasCredential && <div role="alert" className="error invoice-draft-message">Aktif ve yetkili Trendyol E-Faturam bağlantısı gereklidir.</div>}{message && <div role="status" className={`notice invoice-draft-message ${created ? 'invoice-created-feedback' : ''}`}>{message}</div>}<footer><button type="button" className="secondary" onClick={onClose}>Vazgeç</button>{!created && <button type="button" className="invoice-continue" disabled={create.isPending || !packageId || !provider?.hasCredential} onClick={() => create.mutate()}>{create.isPending ? 'Fatura oluşturuluyor…' : 'Faturayı Oluştur'}</button>}</footer></>}</section></div>
}

function InvoiceViewerModal({ item, onClose }: { item: Order; onClose: () => void }) {
  const invoiceId = item.invoiceId
  const detail = useQuery({ queryKey: ['invoice-viewer', invoiceId], queryFn: () => hubApi<InvoiceViewer>(`/invoices/${invoiceId}`), enabled: !!invoiceId })
  const invoice = detail.data
  return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={onClose}><section className="workspace-modal invoice-viewer-modal" role="dialog" aria-modal="true" aria-labelledby="invoice-viewer-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="invoice-viewer-title">Fatura Görüntüle</h2><p>#{item.orderNumber} · {invoice?.invoiceNumber ?? 'Numara bekleniyor'}</p></div><button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat">×</button></header>{detail.isLoading ? <Busy text="Fatura yükleniyor…" /> : detail.isError || !invoice ? <ErrorBox error={detail.error} /> : <><div className="invoice-viewer-summary"><span><small>Durum</small><strong>{invoice.status}</strong></span><span><small>Fatura tipi</small><strong>{invoice.invoiceType}</strong></span><span><small>Ödenecek</small><strong>{invoice.payableTotal.toLocaleString('tr-TR', { style: 'currency', currency: invoice.currency })}</strong></span><span><small>Vergi</small><strong>{invoice.taxTotal.toLocaleString('tr-TR', { style: 'currency', currency: invoice.currency })}</strong></span></div><div className="invoice-viewer-lines" role="table"><div className="invoice-viewer-line invoice-viewer-line-head" role="row"><strong>Ürün</strong><strong>Miktar</strong><strong>KDV</strong><strong>Toplam</strong></div>{invoice.lines.map(line => <div className="invoice-viewer-line" role="row" key={line.id}><span><strong>{line.description}</strong><small>{line.sku ?? 'Stok kodu yok'}</small></span><span>{line.quantity} {line.unit}</span><span>%{line.vatRate}</span><strong>{line.lineTotal.toLocaleString('tr-TR', { style: 'currency', currency: invoice.currency })}</strong></div>)}</div>{invoice.documents.length ? <div className="invoice-viewer-documents"><strong>Belgeler</strong>{invoice.documents.map(document => <a key={document.id} href={`/api/v1/invoices/${invoice.id}/documents/${document.id}/content`} target="_blank" rel="noreferrer">{document.documentType === 'PDF' ? 'PDF faturayı aç' : 'Fatura belgesini aç'} ↗</a>)}</div> : <p className="invoice-viewer-empty">Provider belgesi henüz arşivlenmedi.</p>}<footer><button type="button" className="secondary" onClick={onClose}>Kapat</button><button type="button" onClick={() => window.print()}>Yazdır</button></footer></>}</section></div>
}

function InvoiceUploadModal({ item, provider, onClose }: { item: Order; provider: Connection | null; onClose: () => void }) {
  const client = useQueryClient()
  const [file, setFile] = useState<File | null>(null)
  const [message, setMessage] = useState('')
  const [dragging, setDragging] = useState(false)
  const detail = useQuery({ queryKey: ['invoice-upload-order', item.id], queryFn: () => hubApi<OrderDetail>(`/orders/${item.id}`) })
  const upload = useMutation({
    mutationFn: async () => {
      if (!file) throw new Error('Yüklenecek fatura dosyasını seçin.')
      if (file.size > 10 * 1024 * 1024) throw new Error('Fatura dosyası en fazla 10 MB olabilir.')
      if (!/\.(pdf|jpe?g|png)$/i.test(file.name)) throw new Error('Yalnız PDF, JPEG, JPG veya PNG dosyası yükleyebilirsiniz.')
      const order = detail.data
      if (!order) throw new Error('Sipariş bilgileri henüz yüklenmedi.')
      let invoiceId = item.invoiceId
      if (!invoiceId) {
        const packageId = order.packages[0]?.id
        if (!packageId || !provider?.hasCredential) throw new Error('Fatura taslağı için aktif ve yetkili Trendyol E-Faturam bağlantısı gereklidir.')
        const invoice = await hubApi<CreatedInvoice>('/invoices', { method: 'POST', headers: { 'Idempotency-Key': `invoice:${order.id}:${packageId}` }, body: JSON.stringify({ orderId: order.id, packageId, providerConnectionId: provider.id, originalInvoiceId: null }) })
        invoiceId = invoice.id
      }
      const form = new FormData(); form.append('file', file)
      await hubApi(`/invoices/${invoiceId}/documents/manual`, { method: 'POST', headers: { 'Idempotency-Key': `invoice-document:${invoiceId}:${file.name}:${file.size}:${file.lastModified}` }, body: form })
    },
    onSuccess: async () => { await client.invalidateQueries({ queryKey: ['orders'] }); onClose() },
    onError: error => setMessage(error instanceof Error ? error.message : 'Fatura dosyası yüklenemedi.')
  })
  function choose(selected: File | undefined) { if (selected) { setFile(selected); setMessage('') } }
  return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={onClose}><section className="workspace-modal invoice-upload-modal" role="dialog" aria-modal="true" aria-labelledby="invoice-upload-title" onMouseDown={event => event.stopPropagation()}><header><h2 id="invoice-upload-title">Fatura Yükle</h2><button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat">×</button></header>{detail.isError ? <ErrorBox error={detail.error} /> : <><label className={`invoice-dropzone${dragging ? ' dragging' : ''}`} onDragEnter={event => { event.preventDefault(); setDragging(true) }} onDragOver={event => event.preventDefault()} onDragLeave={() => setDragging(false)} onDrop={event => { event.preventDefault(); setDragging(false); choose(event.dataTransfer.files[0]) }}><input type="file" accept=".pdf,.jpeg,.jpg,.png,application/pdf,image/jpeg,image/png" onChange={event => choose(event.target.files?.[0])} /><span className="invoice-upload-icon" aria-hidden="true">⇧</span><strong>{file ? file.name : 'Fatura Dosyası Yükle'}</strong><small>{file ? `${(file.size / 1024 / 1024).toFixed(2)} MB` : 'Dosyanızı seçin ya da bu alana sürükleyin.'}</small><b>Dosya Seç</b></label>{message && <p className="error" role="alert">{message}</p>}<footer><button type="button" className="secondary" onClick={onClose}>Vazgeç</button><button type="button" disabled={!file || detail.isLoading || upload.isPending} onClick={() => upload.mutate()}>{upload.isPending ? 'Yükleniyor…' : 'Faturayı Yükle'}</button></footer></>}</section></div>
}

function customerDisplayName(item: Order) {
  if (item.customerName && item.customerName !== '—' && item.customerName.trim() !== '') return item.customerName
  const ship = safeJson(item.shipmentAddressJson)
  const shipData = ([ship.shipmentAddress, ship.address, ship].find(x => x && typeof x === 'object') ?? ship) as Record<string, unknown>
  const shipParts = [shipData.firstName ?? shipData.shippingFirstName, shipData.lastName ?? shipData.shippingLastName].filter(meaningfulText)
  const shipName = shipParts.length ? shipParts.join(' ') : (shipData.fullName as string) ?? (shipData.name as string)
  if (shipName && shipName.trim()) return shipName.trim()
  const inv = safeJson(item.invoiceAddressJson)
  const invData = ([inv.invoiceAddress, inv.address, inv].find(x => x && typeof x === 'object') ?? inv) as Record<string, unknown>
  const invParts = [invData.firstName ?? invData.invoiceFirstName, invData.lastName ?? invData.invoiceLastName].filter(meaningfulText)
  const invName = invParts.length ? invParts.join(' ') : (invData.fullName as string) ?? (invData.company as string) ?? (invData.name as string)
  if (invName && invName.trim()) return invName.trim()
  return '—'
}

type CargoCarrier = { label: string; code: string; iconUrl?: string; aliases?: string[] }
const cargoCarriers: CargoCarrier[] = [
  { label: 'Yurtiçi Kargo', code: 'YKMP', iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/4.png' },
  { label: 'Sürat Kargo', code: 'SURATMP', iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/9.png' },
  { label: 'DHL eCommerce', code: 'DHLECOMMP', iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/10.png' },
  { label: 'PTT Kargo', code: 'PTTMP', iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/19.png' },
  { label: 'Kolay Gelsin', code: 'KOLAYGELSINMP', aliases: ['SENDEOMP'], iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/38.png' },
  { label: 'Aras Kargo', code: 'ARASMP', iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/7.png' },
  { label: 'Horoz Kargo', code: 'HOROZMP', iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/6.png' },
  { label: 'CEVA Tedarik', code: 'CEVATEDARIK', aliases: ['Ceva Tedarik Marketplace'], iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/30.png' },
  { label: 'CEVA Kargo', code: 'CEVAMP', aliases: ['CEVA', 'CEVA Logistics', 'CEVA Marketplace'], iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/20.png' },
  { label: 'Trendyol Express', code: 'TEXMP', aliases: ['Trendyol Express Marketplace'], iconUrl: 'https://cdn.dsmcdn.com/seller-center/oms/nexus/cargo-provider/17.png' },
  { label: 'UPS', code: 'UPSMP' }
]
function normalizedCargo(value: string | null | undefined) { return (value ?? '').toLocaleUpperCase('tr-TR').replace(/[^\p{L}\p{N}]/gu, '') }
function cargoCarrier(value: string | null | undefined) {
  const normalized = normalizedCargo(value)
  return cargoCarriers.find(carrier => [carrier.code, carrier.label, ...(carrier.aliases ?? [])].some(candidate => normalized === normalizedCargo(candidate) || normalized.includes(normalizedCargo(candidate.replace(' Kargo', '')))))
}
function cargoLabel(value: string | null | undefined) {
  return cargoCarrier(value)?.label ?? value ?? 'Kargo bekleniyor'
}
function cargoMatches(value: string | null | undefined, carrier: CargoCarrier) {
  return cargoCarrier(value)?.code === carrier.code
}
function CargoProviderIcon({ value }: { value: string | null | undefined }) {
  const carrier = cargoCarrier(value)
  if (!carrier?.iconUrl) return <span className="cargo-provider-icon cargo-provider-icon-fallback" aria-hidden="true">{(carrier?.label ?? value ?? 'K').slice(0, 2).toUpperCase()}</span>
  return <img className="cargo-provider-icon" src={carrier.iconUrl} alt="" loading="lazy" decoding="async" referrerPolicy="no-referrer" />
}

function patchOrderShipment(order: Order, updatedShipment: Shipment): Order {
  const packages = (order.packages?.map(packageItem => packageItem.id === updatedShipment.id ? { ...packageItem, ...updatedShipment } : packageItem) ?? [updatedShipment]).sort((left, right) => new Date(right.statusOccurredAt).getTime() - new Date(left.statusOccurredAt).getTime())
  const primaryPackage = packages[0]
  return { ...order, packages, derivedStatus: aggregateOrderStatus(packages.map(item => item.status)), cargoProviderName: primaryPackage?.cargoProviderName ?? order.cargoProviderName, cargoTrackingNumber: primaryPackage?.cargoTrackingNumber ?? order.cargoTrackingNumber }
}

function moveOrderAcrossStatusCaches(client: ReturnType<typeof useQueryClient>, order: Order, updatedShipment: Shipment) {
  const updatedOrder = patchOrderShipment(order, updatedShipment)
  client.setQueriesData<Page<Order>>({ queryKey: ['orders', 'page'] }, current => current ? { ...current, items: current.items.map(item => item.id === order.id ? updatedOrder : item) } : current)
  return updatedOrder
}

function CourierChangeModal({ item, items, onClose, onConfirmed }: { item: Order; items?: Order[]; onClose: () => void; onConfirmed: (shipments: Shipment[]) => void }) {
  const targets = (items?.length ? items : [item]).flatMap(order => order.packages?.[0] ? [{ order, shipment: order.packages[0] }] : [])
  const shipment = item.packages?.[0]
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
    try {
      const updatedShipments: Shipment[] = []
      for (const target of targets) {
        const updatedShipment = await hubApi<Shipment>(`/shipments/${target.shipment.id}/instant-cargo-provider`, {
          method: 'POST',
          headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${target.shipment.version}"` },
          body: JSON.stringify({ action: 'CHANGE_CARGO_PROVIDER', payloadJson: JSON.stringify({ cargoProvider: chosen.code }) })
        })
        updatedShipments.push(updatedShipment)
      }
      setProgressMsg('Trendyol onayladı; paneldeki kargo bilgisi güncelleniyor…')
      onConfirmed(updatedShipments)
      void client.invalidateQueries({ queryKey: ['orders'] })
      onClose()
    } catch (err) {
      setErrorMsg(err instanceof Error ? `Kargo firması güncellenemedi: ${err.message}` : 'Kargo firması güncellenemedi. Paneldeki mevcut bilgi korundu.')
    } finally {
      setIsSubmitting(false)
    }
  }

  return (
    <div className="workspace-modal-backdrop" role="presentation" onMouseDown={onClose}>
      <section className="workspace-modal courier-change-modal" role="dialog" aria-modal="true" aria-labelledby="courier-change-title" onMouseDown={event => event.stopPropagation()}>
        <header>
          <div>
            <h2 id="courier-change-title">Paketi hangi firma ile göndermek istiyorsunuz?</h2>
            <p>İstek doğrudan Trendyol’a gönderilir; onay gelirse panel anında güncellenir.</p>
          </div>
          <button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat">×</button>
        </header>
        <p className="courier-description">
          <strong>{targets.length > 1 ? `${targets.length} seçili paket` : `#${item.orderNumber}`}</strong> için yeni kargo firması seçin:
        </p>
        {targets.length > 1 && <div className="bulk-operation-summary"><strong>{targets.length} paket değiştirilecek</strong><small>Seçtiğiniz kargo firması uygun tüm seçili paketlere uygulanır.</small></div>}
        <h3>Standart Kargo Firmaları <span>(0–30 desi)</span></h3>
        <div className="courier-options">
          {cargoCarriers.map(carrier => (
            <label
              key={carrier.code}
              className={selectedCarrier === carrier.label ? 'carrier-card selected' : 'carrier-card'}
              onClick={() => setSelectedCarrier(carrier.label)}
              style={{ cursor: 'pointer' }}
              >
                <input
                  type="radio"
                  name="courier"
                  value={carrier.label}
                  checked={selectedCarrier === carrier.label}
                  onChange={() => setSelectedCarrier(carrier.label)}
                />
                <span className="carrier-card-icon"><CargoProviderIcon value={carrier.label} /></span>
                <span>{carrier.label}</span>
              </label>
          ))}
        </div>
        {progressMsg && !errorMsg && <p className="courier-progress" role="status">{progressMsg}</p>}
        {errorMsg && <p className="error" role="alert" style={{ margin: '12px 24px 0' }}>{errorMsg}</p>}
        <footer>
          <button type="button" className="secondary" onClick={onClose} disabled={isSubmitting}>Vazgeç</button>
          <button type="button" disabled={isSubmitting || !targets.length} onClick={handleCarrierChange}>
            {isSubmitting ? 'İşleniyor…' : 'İşlemi Yap'}
          </button>
        </footer>
      </section>
    </div>
  )
}

function LabelBarcode({ value, compact = false }: { value: string; compact?: boolean }) {
  const bars = code128Bars(value)
  return <div className={`shipping-label-barcode${compact ? ' compact' : ''}`} aria-label={`Barkod: ${value || 'Takip numarası bekleniyor'}`}><div className="shipping-label-barcode-bars" style={{ '--barcode-module-count': bars.length } as CSSProperties}>{bars.map((isBar, index) => <i className={isBar ? 'is-bar' : undefined} key={index} />)}</div><strong>{value || 'Takip numarası bekleniyor'}</strong></div>
}

function ShippingLabelModal({ item, format, onClose, onPrinted, inline = false }: { item: Order; format?: ShippingLabelFormat; onClose: () => void; onPrinted?: () => void; inline?: boolean }) {
  const settings = loadShippingLabelSettings()
  format = format === 'sticker' ? 'sticker' : settings.defaultFormat
  const shipment = item.packages?.[0]
  const trackingNumber = shipment?.cargoTrackingNumber ?? item.cargoTrackingNumber ?? ''
  const packageNumber = shipment?.externalPackageId ?? '—'
  const address = addressText(item.shipmentAddressJson)
  const addressLines = address.split(' · ').filter(Boolean)
  const senderLines = settings.senderAddress.split(/\r?\n| · /).map(value => value.trim()).filter(Boolean)
  const style = { '--shipping-label-width': `${settings.stickerWidthMm}mm`, '--shipping-label-height': `${settings.stickerHeightMm}mm`, '--shipping-label-gap': `${settings.sectionGapMm}mm` } as CSSProperties
  const fieldValues: Record<ShippingLabelField, string> = {
    trackingNumber,
    packageNumber,
    orderNumber: `#${item.orderNumber}`,
    customerName: customerDisplayName(item),
    address,
    cargoProvider: cargoLabel(shipment?.cargoProviderName ?? item.cargoProviderName),
    senderName: settings.senderName,
    senderAddress: settings.senderAddress,
    customerEmail: item.customerEmail ?? '—'
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
      return <div className="shipping-label-block-content" style={{ textAlign: block.align }}><strong className="shipping-label-block-title">{block.title}</strong><LabelBarcode value={fieldValues[barcodeField] || (barcodeField === 'packageNumber' ? item.orderNumber : '')} compact={format === 'sticker'} />{extraFields.map(field => renderField(field))}</div>
    }
    const fields = block.fields.filter(field => settings.showCustomerPhone || field !== 'customerEmail')
    if (block.kind === 'address') return <div className="shipping-label-address-box" style={{ textAlign: block.align }}><strong className="shipping-label-block-title">{block.title}</strong>{fields.map(field => field === 'customerName' ? <strong key={field}>{fieldValues[field]}</strong> : renderField(field))}</div>
    if (block.kind === 'orderInfo') return <div className="shipping-label-meta" style={{ textAlign: block.align }}><strong className="shipping-label-block-title">{block.title}</strong>{block.text && <strong>{block.text}</strong>}{fields.map(field => renderField(field))}</div>
    if (block.kind === 'sender') return <div className="shipping-label-footer" style={{ textAlign: block.align }}><strong className="shipping-label-block-title">{block.title}</strong>{block.text && <strong>{block.text}</strong>}{fields.map(field => renderField(field))}</div>
    return <div className="shipping-label-custom-content" style={{ textAlign: block.align }}><strong className="shipping-label-block-title">{block.title}</strong>{block.text && <strong>{block.text}</strong>}{fields.map(field => renderField(field))}</div>
  }
  const labelSurface = <article className={`shipping-label-print-surface shipping-label-${format} a4-count-${settings.a4LabelsPerPage}`} style={style}>
    {settings.layout[format].map(block => <div key={block.id} className="shipping-label-positioned-block" style={block.position ? { left: `${block.position.x}%`, top: `${block.position.y}%`, width: `${block.position.width}%`, height: `${block.position.height}%`, fontSize: `${block.fontSize ?? 14}px`, color: '#000' } : { fontSize: `${block.fontSize ?? 14}px`, color: '#000' }}><Fragment>{renderLabelBlock(block)}</Fragment></div>)}
  </article>
  if (inline) return labelSurface
  return <div className="workspace-modal-backdrop shipping-label-backdrop" role="presentation" onMouseDown={onClose}>
    <section className={`workspace-modal shipping-label-modal format-${format}`} role="dialog" aria-modal="true" aria-labelledby="shipping-label-title" onMouseDown={event => event.stopPropagation()}>
      <header><div><h2 id="shipping-label-title">{format === 'a4' ? 'A4 kargo etiketi' : 'Sticker kargo etiketi'}</h2><p>#{item.orderNumber} · {cargoLabel(shipment?.cargoProviderName ?? item.cargoProviderName)}</p></div><button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat">×</button></header>
      <div className="shipping-label-preview-wrap">{labelSurface}</div>
      <footer className="shipping-label-modal-actions"><span>Etiket bilgileri sipariş ve paket kaydından dolduruldu.</span><button type="button" className="secondary" onClick={onClose}>Vazgeç</button><button type="button" onClick={() => { markShippingLabelPrinted(item.id, format); onPrinted?.(); window.print() }}>Yazdır</button></footer>
    </section>
  </div>
}

function ShippingLabelBatchModal({ items, format, onClose, onPrinted }: { items: Order[]; format: ShippingLabelFormat; onClose: () => void; onPrinted: () => void }) {
  function printLabels() {
    items.forEach(item => markShippingLabelPrinted(item.id, format))
    onPrinted()
    window.print()
  }
  return <div className="workspace-modal-backdrop shipping-label-backdrop" role="presentation" onMouseDown={onClose}>
    <section className={`workspace-modal shipping-label-modal shipping-label-batch-modal format-${format}`} role="dialog" aria-modal="true" aria-labelledby="shipping-label-batch-title" onMouseDown={event => event.stopPropagation()}>
      <header><div><h2 id="shipping-label-batch-title">Kargo stickerlarını yazdır</h2><p>{items.length} etiket · Takip numarası bulunan seçili paketler</p></div><button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat">×</button></header>
      <div className="shipping-label-batch-preview">{items.map(item => <ShippingLabelModal key={item.id} item={item} format={format} onClose={onClose} inline />)}</div>
      <footer className="shipping-label-modal-actions"><span>{items.length} etiket yazdırılmaya hazır.</span><button type="button" className="secondary" onClick={onClose}>Vazgeç</button><button type="button" onClick={printLabels}>Yazdır</button></footer>
    </section>
  </div>
}

function SingleOrderSyncModal({ activeConnection: activeConnections, onClose, onSuccess }: { activeConnection: Connection[]; onClose: () => void; onSuccess: (connectionCount: number, orderNumber: string) => void }) {
  const [orderNumber, setOrderNumber] = useState('')
  const [selectedConnectionIds, setSelectedConnectionIds] = useState<string[]>(() => activeConnections.map(connection => connection.id))
  const [syncMode, setSyncMode] = useState<'changes' | 'single'>('changes')
  const [fullScan, setFullScan] = useState(false)
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [errorMsg, setErrorMsg] = useState('')

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
    setIsSubmitting(true)
    setErrorMsg('')
    try {
      await Promise.all(selectedConnections.map(connection => hubApi(`/connections/${connection.id}/order-sync-jobs`, {
        method: 'POST',
        headers: { 'Idempotency-Key': idempotency() },
        body: JSON.stringify({ externalOrderId: syncMode === 'single' ? trimmed : null, full: syncMode === 'changes' && fullScan })
      })))
      onSuccess(selectedConnections.length, trimmed)
      onClose()
    } catch (err) {
      setErrorMsg(err instanceof Error ? err.message : 'Sipariş senkronizasyonu başlatılamadı. Seçili bağlantıların durumunu kontrol edin.')
    } finally {
      setIsSubmitting(false)
    }
  }


  return (
    <div className="workspace-modal-backdrop" role="presentation" onMouseDown={onClose}>
      <section className="workspace-modal single-order-sync-modal" role="dialog" aria-modal="true" aria-labelledby="order-sync-title" onMouseDown={e => e.stopPropagation()} style={{ maxWidth: '560px' }}>
        <header>
          <div>
            <h2 id="order-sync-title">Sipariş Senkronizasyonu</h2>
            <p>İşaretlediğiniz aktif bağlantılardan siparişleri panele alın.</p>
          </div>
          <button type="button" className="modal-close" onClick={onClose} aria-label="Kapat">×</button>
        </header>
        <form onSubmit={handleSubmit} style={{ padding: '0 24px 20px', display: 'grid', gap: '14px' }}>
          {!activeConnections.length && (
            <div className="notice" role="alert" style={{ margin: 0, borderRadius: '8px', fontSize: '0.84rem' }}>
              ⚠️ Aktif Trendyol bağlantısı bulunamadı. Platformlar sayfasından bağlantınızı etkinleştirin.
            </div>
          )}
          <fieldset className="sync-source-fieldset">
            <legend>Sipariş kaynakları</legend>
            <div className="sync-source-list">
              {activeConnections.map(connection => <label key={connection.id} className={selectedConnectionIds.includes(connection.id) ? 'sync-source-option is-selected' : 'sync-source-option'}>
                <input type="checkbox" checked={selectedConnectionIds.includes(connection.id)} onChange={event => setSelectedConnectionIds(current => event.target.checked ? [...current, connection.id] : current.filter(id => id !== connection.id))} />
                <span><strong>{connection.displayName}</strong><small>{connection.platformCode === 'TRENDYOL' ? 'Trendyol' : connection.platformCode} · {connection.environment} · Mağaza {connection.externalStoreId}</small></span>
              </label>)}
            </div>
          </fieldset>
          <div className="sync-mode-switch" role="group" aria-label="Senkronizasyon türü">
            <button type="button" className={syncMode === 'changes' ? 'active' : ''} onClick={() => { setSyncMode('changes'); setErrorMsg('') }} disabled={isSubmitting}>Yeni siparişleri çek</button>
            <button type="button" className={syncMode === 'single' ? 'active' : ''} onClick={() => { setSyncMode('single'); setErrorMsg('') }} disabled={isSubmitting}>Tekil sipariş çek</button>
          </div>
          {syncMode === 'single' ? <label className="sync-order-number-field" style={{ display: 'grid', gap: '6px' }}>
            <span style={{ fontSize: '0.85rem', fontWeight: 600 }}>Trendyol sipariş numarası</span>
            <input type="text" inputMode="numeric" value={orderNumber} onChange={e => setOrderNumber(e.target.value)} placeholder="Örn. 1014529381" disabled={isSubmitting} autoFocus style={{ padding: '10px 14px', borderRadius: '8px', fontSize: '0.95rem' }} />
          </label> : <label className="sync-mode-option"><input type="checkbox" checked={fullScan} onChange={event => setFullScan(event.target.checked)} /><span><strong>Erişilebilir tüm siparişleri tara</strong><small>Kapalıyken yalnız yeni değişiklikler ve güncellemeler alınır.</small></span></label>}
          {errorMsg && <p className="error" role="alert" style={{ margin: 0 }}>{errorMsg}</p>}
          <footer style={{ display: 'flex', justifyContent: 'flex-end', gap: '10px', marginTop: '8px' }}>
            <button type="button" className="secondary" onClick={onClose} disabled={isSubmitting}>İptal</button>
            <button type="submit" disabled={isSubmitting || !selectedConnectionIds.length}>
              {isSubmitting ? 'Senkronize ediliyor…' : syncMode === 'single' ? 'Tekil siparişi çek' : 'Siparişleri çek'}
            </button>
          </footer>
        </form>
      </section>
    </div>
  )
}


function OrderReferenceRow({ item, selected, onSelect, openMenu, onMenuChange, onInvoiceCreate, onInvoiceDetails, onInvoiceUpload, onCourierChange, onProcessOrder, processing, onPrintLabel, onHideCancelled, showCancelledActions, onPreviewImage, labelSettings, printedLabels }: { item: Order; selected: boolean; onSelect: (checked: boolean) => void; openMenu: 'invoice' | 'actions' | null; onMenuChange: (value: 'invoice' | 'actions' | null) => void; onInvoiceCreate: () => void; onInvoiceDetails: () => void; onInvoiceUpload: () => void; onCourierChange: () => void; onProcessOrder: () => void; processing?: boolean; onPrintLabel: (format: ShippingLabelFormat) => void; onHideCancelled?: () => void; showCancelledActions?: boolean; onPreviewImage: (preview: { url: string; title: string }) => void; labelSettings: ShippingLabelSettings; printedLabels: Set<string> }) {

  const lines = item.lines ?? []
  const shipment = item.packages?.[0]
  const invoiceCreationStatuses = ['FATURA_BEKLIYOR', 'FATURA_REDDEDILDI', 'FATURA_ISLENIYOR']
  const invoiceNeedsAction = invoiceCreationStatuses.includes(item.invoiceStatus)
  const [copied, setCopied] = useState(false)
  const [menuPlacement, setMenuPlacement] = useState<'down' | 'up'>('down')
  const money = (value: number) => value.toLocaleString('tr-TR', { style: 'currency', currency: item.currency })
  const delivery = deliveryText(item)
  const normalizedOrderStatus = item.derivedStatus.toUpperCase()
  const isCancelledOrder = normalizedOrderStatus === 'CANCELLED'
  const invoiceStatusLabel = item.invoiceStatus === 'FATURA_BEKLIYOR' ? 'Fatura bekleniyor' : item.invoiceStatus === 'FATURA_ISLENIYOR' || item.invoiceStatus === 'FATURA_KONTROLDE' ? 'Fatura kontrol ediliyor' : item.invoiceStatus === 'FATURA_REDDEDILDI' ? 'Fatura reddedildi' : item.invoiceStatus === 'FATURA_IPTAL' ? 'Fatura iptal edildi' : item.invoiceStatus === 'FATURA_KESILDI' ? 'Fatura kesildi' : 'Fatura durumu bilinmiyor'
  const isCargoTrackingStage = ['SHIPPED', 'UNDELIVERED'].includes(normalizedOrderStatus)
  const isCargoLabelStage = ['NEW', 'PROCESSING', 'READY_TO_SHIP'].includes(normalizedOrderStatus)
  const canChangeCargo = !['SHIPPED', 'UNDELIVERED', 'DELIVERED', 'RETURNED'].includes(normalizedOrderStatus)
  const enabledLabelFormats: ShippingLabelFormat[] = [
    ...(labelSettings.showA4Button ? ['a4' as const] : []),
    ...(labelSettings.showStickerButton ? ['sticker' as const] : [])
  ]
  showCancelledActions = showCancelledActions ?? true
  onHideCancelled = onHideCancelled ?? (() => { try { localStorage.setItem('ravencia.hiddenCancelledOrders', JSON.stringify(Array.from(new Set([...JSON.parse(localStorage.getItem('ravencia.hiddenCancelledOrders') ?? '[]').filter((id: unknown): id is string => typeof id === 'string'), item.id])))) } catch { /* Private browsing may disallow local storage. */ } window.dispatchEvent(new CustomEvent('ravencia:cancelled-order-hidden', { detail: item.id })) })
  async function copyOrderNumber() { try { await navigator.clipboard.writeText(item.orderNumber); setCopied(true); window.setTimeout(() => setCopied(false), 1400) } catch { setCopied(false) } }
  function toggleMenu(kind: 'invoice' | 'actions', event: MouseEvent<HTMLButtonElement>) {
    if (openMenu === kind) { onMenuChange(null); return }
    const trigger = event.currentTarget.getBoundingClientRect()
    const estimatedHeight = kind === 'invoice' ? 150 : 132
    const availableBelow = window.innerHeight - trigger.bottom
    setMenuPlacement(availableBelow >= estimatedHeight + 12 ? 'down' : 'up')
    onMenuChange(kind)
  }
  return <article className={`order-reference-row ${item.derivedStatus.toLowerCase()} ${item.isMicroExport ? 'micro-export' : ''} ${openMenu ? 'menu-open' : ''}`}>
    <div className="order-reference-grid">
      {isCancelledOrder ? <span className="order-select order-select-disabled" role="img" aria-label="İptal sipariş toplu seçilemez" title="İptal siparişler toplu seçilemez">—</span> : <label className="order-select"><input type="checkbox" checked={selected} onChange={event => onSelect(event.target.checked)} aria-label={`Sipariş ${item.orderNumber} seç`} /></label>}
      <div className="order-reference-meta"><div className="order-number"><strong><i className="order-package-mark" aria-hidden="true" />#{item.orderNumber}</strong><button type="button" className="order-number-copy" onClick={copyOrderNumber} aria-label={`Sipariş numarası ${item.orderNumber} kopyala`} title={copied ? 'Kopyalandı' : 'Sipariş numarasını kopyala'}><span className="copy-icon" aria-hidden="true" /></button></div>{item.isMicroExport && <span className="micro-export-chip" role="status">Mikro İhracat</span>}<small>Sipariş Tarihi: <DateText value={item.orderedAt} /></small><small>Paket No: {shipment?.externalPackageId ?? 'Bekleniyor'}</small><small>Teslimat No: {shipment?.cargoTrackingNumber ?? 'Bekleniyor'}</small><span className={delivery.overdue ? 'delivery-overdue' : ''}>{delivery.label && <>{delivery.label}: </>}<b>{delivery.value}</b>{delivery.note && <em>{delivery.note}</em>}</span></div>
      <div className="order-reference-buyer"><strong>{customerDisplayName(item)}</strong></div>
      <div className="order-reference-products">{lines.length ? lines.map(line => { const imageUrl = line.imageUrl ?? (lines.length === 1 ? item.primaryImageUrl : null); const fallbackUrl = productImageFallbackUrl(line.barcode ?? line.sku); const quantity = productLineQuantity(line); return <article key={line.id}><span className="reference-product-media">{imageUrl || fallbackUrl ? <ProductImage url={imageUrl} fallbackUrl={fallbackUrl} alt={`${line.title} ürün görseli`} onClick={() => onPreviewImage({ url: imageUrl ?? fallbackUrl!, title: line.title })} /> : <span className="reference-product-placeholder" aria-label="Ürün görseli eşleştirmesi bekleniyor">▧</span>}<span className="quantity-bubble" role="img" aria-label={`${quantity} adet`} title={`${quantity} adet`}>{quantity}</span></span><div><strong>{line.title}</strong><small>Stok Kodu: <code className="technical-text sku-value">{line.sku}</code></small>{optionRows(line.optionSignature).map(option => <small key={`${option.label}:${option.value}`}>{option.label}: {option.value}</small>)}<small>Barkod: <code className="technical-text barcode-value">{line.barcode ?? '—'}</code></small><small>Model Kodu: <code className="technical-text model-code-value">{line.modelCode ?? '—'}</code></small></div></article> }) : <div className="reference-no-product">Ürün bilgisi eşitleme bekliyor</div>}</div>
      <div className="order-reference-prices">{lines.length ? lines.map(line => <strong key={line.id}>{money(line.unitPrice)}</strong>) : <strong>{money(item.netAmount)}</strong>}</div>
      <div className="order-reference-cargo"><div className="cargo-provider-display"><CargoProviderIcon value={shipment?.cargoProviderName ?? item.cargoProviderName} /><strong>{cargoLabel(shipment?.cargoProviderName ?? item.cargoProviderName)}</strong></div><b>{shipment?.cargoTrackingNumber ?? item.cargoTrackingNumber ?? 'Takip no bekleniyor'}</b></div>
      <div className={`order-reference-invoice ${item.invoiceStatus === 'FATURA_BEKLIYOR' ? 'invoice-pending' : 'invoice-created'}`}><small>Satış Tutarı:</small><strong>{money(item.grossAmount || item.netAmount)}</strong>{item.discountAmount > 0 && <small>Satıcı İndirim Tutarı: {money(item.discountAmount)}</small>}{item.isMicroExport && <span className="micro-invoice-chip">Mikro İhracat Faturası</span>}{isCancelledOrder ? <span className="invoice-status-readonly" role="status">{invoiceStatusLabel}</span> : <>{invoiceNeedsAction && <span>{invoiceStatusLabel}</span>}{!invoiceNeedsAction && (item.invoiceId ? <a className="invoice-document-link" href={`/api/v1/invoices/${item.invoiceId}/documents/latest/content`} download>Faturayı Gör</a> : item.invoiceDocumentUrl ? <a className="invoice-document-link" href={item.invoiceDocumentUrl} download>Faturayı Gör</a> : null)}<div className="row-menu"><button type="button" className="row-menu-trigger" onClick={event => toggleMenu('invoice', event)} aria-expanded={openMenu === 'invoice'}><span>Fatura işlemleri</span><b aria-hidden="true">⌄</b></button>{openMenu === 'invoice' && <div className={`row-popover invoice-popover opens-${menuPlacement}`} role="menu">{invoiceNeedsAction ? <><button type="button" role="menuitem" className="create-invoice" onClick={onInvoiceCreate}>Fatura Oluştur</button><button type="button" role="menuitem" onClick={onInvoiceDetails}>Fatura Bilgileri</button><button type="button" role="menuitem" onClick={onInvoiceUpload}>Fatura Yükle</button></> : <>{item.invoiceId ? <a role="menuitem" className="invoice-menu-view" href={`/api/v1/invoices/${item.invoiceId}/documents/latest/content`} download>Fatura Görüntüle</a> : item.invoiceDocumentUrl ? <a role="menuitem" className="invoice-menu-view" href={item.invoiceDocumentUrl} download>Fatura Görüntüle</a> : null}<button type="button" role="menuitem" onClick={onInvoiceDetails}>Fatura Bilgileri</button><button type="button" role="menuitem" className="destructive" disabled>Fatura İptal Et</button></>}</div>}</div></>}</div>
      <div className="order-reference-actions">{isCancelledOrder ? <><div className="order-status-column"><span className="order-status-feedback cancelled" role="status"><i aria-hidden="true" />İptal edildi</span><small>İşlem yapılamaz</small></div><div className="row-menu"><button type="button" className="order-action-menu row-menu-trigger" onClick={event => toggleMenu('actions', event)} aria-expanded={openMenu === 'actions'}><span>İşlemler</span><b aria-hidden="true">⌄</b></button>{openMenu === 'actions' && <div className={`row-popover action-popover opens-${menuPlacement}`} role="menu"><button type="button" role="menuitem" className="destructive" onClick={onHideCancelled}>Tümünden gizle</button></div>}</div></> : <>{isCargoTrackingStage ? shipment?.cargoTrackingNumber ? <Link className="cargo-track-action" to={`/shipments/${shipment.id}`}>Kargo takip linki</Link> : <small className="cargo-action-unavailable">Takip numarası bekleniyor</small> : isCargoLabelStage && shipment ? <div className="order-label-actions" aria-label="Kargo etiketi yazdırma seçenekleri">{enabledLabelFormats.map(format => { const printed = printedLabels.has(printedShippingLabelKey(item.id, format)); return <button type="button" key={format} className={`order-label-action${printed ? ' is-printed' : ''}`} onClick={() => onPrintLabel(format)} title={printed ? 'Bu format daha önce yazdırıldı; yeniden yazdırabilirsiniz.' : undefined}><span>Kargo Etiketi Yazdır</span><small>· {format === 'a4' ? 'A4' : 'Sticker'}</small>{printed && <i aria-label="Yazdırıldı">✓</i>}</button> })}</div> : null}<div className="row-menu"><button type="button" className="order-action-menu row-menu-trigger" onClick={event => toggleMenu('actions', event)} aria-expanded={openMenu === 'actions'}><span>İşlemler</span><b aria-hidden="true">⌄</b></button>{openMenu === 'actions' && <div className={`row-popover action-popover opens-${menuPlacement}`} role="menu">{normalizedOrderStatus === 'NEW' ? <button type="button" role="menuitem" disabled={processing} onClick={onProcessOrder}>{processing ? 'İşleniyor…' : 'İşleme Al'}</button> : <button type="button" role="menuitem" disabled>{normalizedOrderStatus === 'DELIVERED' ? 'Teslim edildi' : 'İşleme Al'}</button>}{canChangeCargo ? <button type="button" role="menuitem" onClick={onCourierChange}>Başka Kargo Firması İle Gönder</button> : <button type="button" role="menuitem" disabled title="Kargoya teslim edilen veya teslim edilen paketlerde değişiklik yapılamaz">Kargo firması değiştirilemez</button>}<button type="button" role="menuitem" disabled>İptal Et</button></div>}</div></>}</div>
    </div>
  </article>

}
function returnGroup(status: string) { const value = status.toUpperCase(); if (['APPROVED', 'COMPLETED'].includes(value)) return 'APPROVED'; if (['REJECTED', 'CANCELLED'].includes(value)) return 'REJECTED'; if (['REQUESTED', 'CREATED'].includes(value)) return 'REQUESTED'; if (['ACTION_REQUIRED', 'WAITING_FOR_SELLER_ACTION'].includes(value)) return 'ACTION_REQUIRED'; if (['WAITING_FOR_SHIPMENT', 'IN_TRANSIT', 'RETURN_IN_TRANSIT', 'SHIPPED'].includes(value)) return 'SHIPPING'; if (value === 'DISPUTED') return 'DISPUTED'; if (['SUSPENDED', 'ON_HOLD'].includes(value)) return 'SUSPENDED'; return 'REVIEW' }
const activePlatformCodes = new Set(['TRENDYOL', 'TRENDYOL_EFATURAM'])
function credentialLabel(item: Connection) { return item.platformCode === 'TRENDYOL_EFATURAM' && item.lastErrorCode === 'EFATURAM_CONFIGURATION_UNAVAILABLE' ? 'Yenileme gerekli' : item.hasCredential ? 'Şifreli kayıtlı' : 'Bekleniyor' }
function credentialHelp(item: Connection) { return item.platformCode === 'TRENDYOL_EFATURAM' && item.lastErrorCode === 'EFATURAM_CONFIGURATION_UNAVAILABLE' ? 'E-Faturam hesap e-postası ve parolasıyla şifreli kaydı yenileyin.' : item.hasCredential ? 'Şifreli kaydedildi; değerler tekrar gösterilmez.' : 'Credential kaydı bekleniyor.' }
function connectionTestHelp(item: Connection) {
  if (item.platformCode === 'TRENDYOL_EFATURAM' && item.lastErrorCode === 'EFATURAM_ACCESS_TOKEN_REJECTED') return 'Giriş başarılı; sağlayıcı sign-in yanıtındaki taze JWT tokenını korumalı fatura API’sinde geçersiz veya süresi dolmuş olarak reddetti.'
  return item.lastErrorCode ?? 'Hata kaydı yok'
}


function ReferenceConnectionCard({ item, onSaved }: { item: Connection; onSaved: () => void }) {
  const efaturam = item.platformCode === 'TRENDYOL_EFATURAM'
  const [menuOpen, setMenuOpen] = useState(false)
  const [disconnectOpen, setDisconnectOpen] = useState(false)
  const [visibilityOpen, setVisibilityOpen] = useState(false)
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [deleteConfirmation, setDeleteConfirmation] = useState('')
  const [notice, setNotice] = useState('')
  const [externalStoreId, setExternalStoreId] = useState(item.externalStoreId)
  const [environment, setEnvironment] = useState(item.environment)
  const [apiKey, setApiKey] = useState('')
  const [apiSecret, setApiSecret] = useState('')
  const connected = item.status === 'ACTIVE' || item.status === 'VERIFIED'
  const hidden = item.status === 'HIDDEN'
  const test = useMutation({ mutationFn: () => hubApi<{ succeeded?: boolean; errorCode?: string; errorSummary?: string }>(`/connections/${item.id}/test-jobs`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: '{}' }), onSuccess: result => { setNotice(result.succeeded ? 'Bağlantı testi başarılı.' : `Bağlantı testi başarısız${result.errorCode ? `: ${result.errorCode}` : '.'}`); onSaved() }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Bağlantı testi başlatılamadı.') })
  const disconnect = useMutation({ mutationFn: () => hubApi<Connection>(`/connections/${item.id}/active`, { method: 'PUT', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ active: false }) }), onSuccess: () => { setDisconnectOpen(false); setNotice('Bağlantı pasife alındı.'); onSaved() }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Bağlantı pasife alınamadı.') })
  const activate = useMutation({ mutationFn: () => hubApi<Connection>(`/connections/${item.id}/active`, { method: 'PUT', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ active: true }) }), onSuccess: () => { setNotice('Bağlantı etkinleştirildi.'); onSaved() }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Bağlantı etkinleştirilemedi.') })
  const deepDelete = useMutation({ mutationFn: () => hubApi(`/connections/${item.id}/deep-delete`, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ confirmation: deleteConfirmation }) }), onSuccess: () => { setDeleteOpen(false); setDeleteConfirmation(''); onSaved() }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Bağlantı ve bağlı veriler silinemedi.') })
  const dataVisibility = useMutation({ mutationFn: (nextHidden: boolean) => hubApi<boolean>(`/connections/${item.id}/data-visibility`, { method: 'PUT', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ hidden: nextHidden }) }), onSuccess: (_, nextHidden) => { setVisibilityOpen(false); setNotice(nextHidden ? 'Platform verileri gizlendi; yerel kayıtlar korunuyor.' : 'Platform verileri tekrar görünür.'); onSaved() }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Platform verilerinin görünürlük ayarı değiştirilemedi.') })
  const saveDetails = useMutation({ mutationFn: async () => {
    let version = item.version
    const storeChanged = externalStoreId !== item.externalStoreId
    const environmentChanged = environment !== item.environment
    if (storeChanged || environmentChanged) {
      const updated = await hubApi<Connection>(`/connections/${item.id}`, { method: 'PATCH', headers: { 'If-Match': `"v${version}"` }, body: JSON.stringify({ displayName: item.displayName, environment, externalStoreId }) })
      version = updated.version
    }
    const hasNewCredential = !efaturam && (apiKey.trim() || apiSecret.trim())
    if (hasNewCredential) {
      if (!apiKey.trim() || !apiSecret.trim()) throw new Error('API key ve API secret birlikte girilmelidir.')
      await hubApi(`/connections/${item.id}/credential`, { method: 'PUT', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${version}"` }, body: JSON.stringify({ apiKey: apiKey.trim(), apiSecret }) })
    }
  }, onSuccess: () => { setApiKey(''); setApiSecret(''); setNotice('Entegrasyon bilgileri kaydedildi.'); onSaved() }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Entegrasyon bilgileri kaydedilemedi.') })
  const saving = saveDetails.isPending
  return <article className={`integration-card reference-integration-card ${hidden ? 'reference-integration-card-hidden' : connected ? 'reference-integration-card-active' : 'reference-integration-card-error'}`} onClickCapture={event => { const target = event.target; if (target instanceof Element && target.closest('.reference-menu-popover a')) { event.preventDefault(); setMenuOpen(false); setSettingsOpen(true) } }}>
    <div className="reference-integration-accent" aria-hidden="true" />
    <header className="reference-integration-header">
      <div className="reference-integration-brand"><span className={`reference-provider-logo ${efaturam ? 'efaturam' : 'trendyol'}`} aria-hidden="true">{efaturam ? 'e-' : 'ty'}</span><div><h2>{item.displayName}</h2><span className={`reference-connection-state ${hidden ? 'hidden' : connected ? 'connected' : 'inactive'}`}><i />{hidden ? 'Veriler gizli' : connected ? 'Bağlı & Aktif' : 'Bağlantı Pasif'}</span></div></div>
      <div className="reference-integration-menu"><button type="button" className="reference-menu-trigger" aria-label="Entegrasyon seçenekleri" aria-expanded={menuOpen} onClick={() => setMenuOpen(value => !value)}>⋮</button>{menuOpen && <div className="reference-menu-popover" role="menu"><Link to={`/integrations/${item.id}`} role="menuitem">Bağlantı ayarlarını aç</Link><button type="button" role="menuitem" disabled={dataVisibility.isPending} onClick={() => { setMenuOpen(false); hidden ? dataVisibility.mutate(false) : setVisibilityOpen(true) }}>{hidden ? 'Verileri göster' : 'Verileri gizle'}</button><button type="button" role="menuitem" onClick={() => { setMenuOpen(false); setDeleteOpen(true) }}>Bağlantıyı ve verileri sil</button></div>}</div>
    </header>
    <div className="reference-integration-body"><label className="reference-data-point"><span>{efaturam ? 'VKN / TCKN' : 'Mağaza Kimliği (Seller ID)'}</span><input className="reference-editable-field" value={externalStoreId} onChange={event => setExternalStoreId(event.target.value)} /></label><label className="reference-data-point"><span>{efaturam ? 'API Secret' : 'API Key'}</span><input className="reference-editable-field" type="password" value={efaturam ? '' : apiKey} onChange={event => setApiKey(event.target.value)} placeholder={item.hasCredential ? '••••••••••••' : undefined} autoComplete="off" /></label>{!efaturam && <label className="reference-data-point"><span>API Secret</span><input className="reference-editable-field" type="password" value={apiSecret} onChange={event => setApiSecret(event.target.value)} placeholder={item.hasCredential ? '••••••••••••' : undefined} autoComplete="new-password" /></label>}<label className="reference-data-point"><span>Ortam</span><select className="reference-editable-field" value={environment} onChange={event => setEnvironment(event.target.value)}><option value="STAGE">Stage</option><option value="PRODUCTION">Canlı</option></select></label></div>
    {notice && <div className="reference-card-notice" role="status">{notice}</div>}
    <footer className="reference-integration-footer"><button type="button" className={`reference-action ${connected ? 'secondary' : 'primary-action'}`} onClick={() => connected ? setDisconnectOpen(true) : activate.mutate()} disabled={disconnect.isPending || activate.isPending || dataVisibility.isPending || test.isPending || saving}>{disconnect.isPending ? 'Pasife alınıyor…' : activate.isPending ? 'Aktif ediliyor…' : connected ? 'Pasife al' : 'Aktif et'}</button><button type="button" className="reference-action link-action" onClick={() => test.mutate()} disabled={test.isPending || disconnect.isPending || activate.isPending || dataVisibility.isPending || saving}>{test.isPending ? 'Kontrol ediliyor…' : 'Bağlantıyı kontrol et'}</button><button type="button" className="reference-action primary-action" onClick={() => saveDetails.mutate()} disabled={saving || disconnect.isPending || activate.isPending || dataVisibility.isPending || test.isPending}>{saving ? 'Kaydediliyor…' : 'Bilgileri güncelle'}</button></footer>
    {disconnectOpen && <div className="reference-inline-confirm" role="alertdialog"><span>Bağlantı pasife alınacak ve canlı veri akışı duracaktır.</span><div><button type="button" onClick={() => setDisconnectOpen(false)}>Vazgeç</button><button type="button" onClick={() => disconnect.mutate()} disabled={disconnect.isPending}>Onayla</button></div></div>}
    {visibilityOpen && <div className="reference-inline-confirm" role="alertdialog"><span>Platformdan alınan yerel veriler gizlenecek; hiçbir kayıt silinmeyecek. Bağlantı pasif kalır ve veriler tekrar gösterilebilir.</span><div><button type="button" onClick={() => setVisibilityOpen(false)}>Vazgeç</button><button type="button" onClick={() => dataVisibility.mutate(true)} disabled={dataVisibility.isPending}>{dataVisibility.isPending ? 'Gizleniyor…' : 'Verileri gizle'}</button></div></div>}
    {deleteOpen && <div className="reference-delete-confirm" role="alertdialog"><strong>Derin temizlik</strong><span>Bağlantı ve bu mağazaya bağlı sipariş, iade, fatura, ürün ve eşleştirme kayıtları kalıcı olarak silinir.</span><label>Onay için <b>{item.displayName}</b> yazın<input value={deleteConfirmation} onChange={event => setDeleteConfirmation(event.target.value)} /></label><div><button type="button" onClick={() => { setDeleteOpen(false); setDeleteConfirmation('') }}>Vazgeç</button><button type="button" className="destructive" disabled={deleteConfirmation !== item.displayName || deepDelete.isPending} onClick={() => deepDelete.mutate()}>{deepDelete.isPending ? 'Temizleniyor…' : 'Kalıcı olarak sil'}</button></div></div>}
    {settingsOpen && <div className="integration-settings-modal-backdrop" role="presentation" onMouseDown={() => setSettingsOpen(false)}><section className="integration-settings-modal" role="dialog" aria-modal="true" aria-label={`${item.displayName} bağlantı ayarları`} onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">BAĞLANTI AYARLARI</p><h2>{item.displayName}</h2></div><button type="button" className="modal-close" onClick={() => setSettingsOpen(false)} aria-label="Bağlantı ayarlarını kapat">×</button></header><IntegrationDetailWorkspace id={item.id} inline /></section></div>}
  </article>
}

export function IntegrationsPage() {
  const client = useQueryClient()
  const [createOpen, setCreateOpen] = useState(false)
  const [createPlatform, setCreatePlatform] = useState('TRENDYOL')
  const [feedback, setFeedback] = useState<IntegrationFeedback | null>(null)
  const [filterOpen, setFilterOpen] = useState(false)
  const [filter, setFilter] = useState<'ALL' | 'ACTIVE' | 'ATTENTION'>('ALL')
  const [platformFilter, setPlatformFilter] = useState<'ALL' | 'TRENDYOL' | 'TRENDYOL_EFATURAM'>('ALL')
  useEffect(() => {
    if (!feedback) return
    const timeout = window.setTimeout(() => setFeedback(null), 5000)
    return () => window.clearTimeout(timeout)
  }, [feedback])
  useEffect(() => {
    if (!filterOpen) return
    const closeOnOutsideClick = (event: globalThis.MouseEvent) => {
      const target = event.target
      if (target instanceof Element && !target.closest('.integration-filter-wrap')) setFilterOpen(false)
    }
    document.addEventListener('mousedown', closeOnOutsideClick)
    return () => document.removeEventListener('mousedown', closeOnOutsideClick)
  }, [filterOpen])
  const query = useQuery({ queryKey: ['connections'], queryFn: () => loadAllPages<Connection>('/connections') })
  const activeConnections = query.data?.items.filter(item => activePlatformCodes.has(item.platformCode)) ?? []
  const visibleConnections = activeConnections.filter(item => (platformFilter === 'ALL' || item.platformCode === platformFilter) && (filter === 'ALL' || (filter === 'ACTIVE' ? item.status === 'ACTIVE' || item.status === 'VERIFIED' : item.status !== 'ACTIVE' && item.status !== 'VERIFIED' || Boolean(item.lastErrorCode))))
  const filterLabel = `${platformFilter === 'ALL' ? 'Tüm platformlar' : platformFilter === 'TRENDYOL' ? 'Trendyol' : 'Trendyol E-Faturam'} · ${filter === 'ACTIVE' ? 'Aktif bağlantılar' : filter === 'ATTENTION' ? 'Dikkat gerekenler' : 'Tüm durumlar'}`
  const create = useMutation({ mutationFn: (body: object) => hubApi('/connections', { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify(body) }), onSuccess: () => { setCreateOpen(false); setFeedback({ kind: 'success', message: 'Bağlantı oluşturuldu. Karttan API bilgilerini kaydedip bağlantıyı test edebilirsiniz.' }); void client.invalidateQueries({ queryKey: ['connections'] }) }, onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Bağlantı oluşturulamadı.' }) })
  function submitCreate(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); create.mutate({ displayName: data.get('displayName'), environment: data.get('environment'), externalStoreId: data.get('externalStoreId'), platformCode: createPlatform, apiVersion: createPlatform === 'TRENDYOL' ? 'V2' : '1.0.0', userAgentIdentity: createPlatform === 'TRENDYOL' ? data.get('userAgentIdentity') : null }) }
  return <section className="content f3 integrations-reference-page"><div className="integration-reference-heading"><div><h1>Platform Entegrasyonları</h1><p>Pazar yeri ve fatura sağlayıcılarınızla olan veri akışını yönetin.</p></div><div className="integration-heading-actions"><div className="integration-filter-wrap"><button type="button" className="integration-filter-button" aria-expanded={filterOpen} onClick={() => setFilterOpen(value => !value)}><span aria-hidden="true">☷</span> Filtrele</button>{filterOpen && <div className="integration-filter-menu" role="menu" aria-label="Platform ve bağlantı durumu filtresi"><strong>Bağlantı durumu</strong>{(['ALL', 'ACTIVE', 'ATTENTION'] as const).map(value => <button type="button" role="menuitemradio" aria-checked={filter === value} key={value} className={filter === value ? 'active' : ''} onClick={() => setFilter(value)}><i aria-hidden="true" />{value === 'ALL' ? 'Tüm durumlar' : value === 'ACTIVE' ? 'Aktif bağlantılar' : 'Dikkat gerekenler'}</button>)}<strong className="integration-filter-menu-group">Platform</strong>{(['ALL', 'TRENDYOL', 'TRENDYOL_EFATURAM'] as const).map(value => <button type="button" role="menuitemradio" aria-checked={platformFilter === value} key={value} className={platformFilter === value ? 'active' : ''} onClick={() => setPlatformFilter(value)}><i aria-hidden="true" />{value === 'ALL' ? 'Tüm platformlar' : value === 'TRENDYOL' ? 'Trendyol' : 'Trendyol E-Faturam'}</button>)}</div>}</div><button type="button" className="integration-add-button" onClick={() => setCreateOpen(true)}><span aria-hidden="true">＋</span> Ekle</button></div></div>
     {query.isLoading ? <Busy /> : query.isError ? <ErrorBox error={query.error} /> : <><div className="integration-filter-summary">{filterLabel}<span>{visibleConnections.length} bağlantı</span></div><div className="integration-grid">{visibleConnections.map(item => <ReferenceConnectionCard item={item} onSaved={() => { void client.invalidateQueries({ queryKey: ['connections'] }); void client.invalidateQueries({ queryKey: ['orders'] }); void client.invalidateQueries({ queryKey: ['dashboard-bootstrap'] }); void client.invalidateQueries({ queryKey: ['dashboard-revenue-series'] }) }} key={item.id} />)}</div>{!visibleConnections.length && <Empty>Bu filtreye uyan bağlantı bulunmuyor.</Empty>}{createOpen && <div className="integration-modal-backdrop" role="presentation" onMouseDown={() => setCreateOpen(false)}><form className="integration-modal" onSubmit={submitCreate} onMouseDown={event => event.stopPropagation()}><div className="integration-modal-header"><h2>Yeni bağlantı</h2><button type="button" className="modal-close" onClick={() => setCreateOpen(false)} aria-label="Pencereyi kapat">×</button></div><label>Platform<select value={createPlatform} onChange={event => setCreatePlatform(event.target.value)}><option value="TRENDYOL">Trendyol</option><option value="TRENDYOL_EFATURAM">Trendyol E-Faturam</option></select></label><label>Bağlantı adı<input name="displayName" placeholder="Örn. Trendyol Canlı" required /></label><label>Ortam<select name="environment" defaultValue="STAGE"><option value="STAGE">Stage</option><option value="PRODUCTION">Canlı</option></select></label><label>{createPlatform === 'TRENDYOL' ? 'Mağaza kimliği' : 'Yerel bağlantı kapsamı'}<input name="externalStoreId" placeholder={createPlatform === 'TRENDYOL' ? 'Mağaza ID' : 'Ravencia - Ravencia'} required /></label>{createPlatform === 'TRENDYOL' && <label>User-Agent kimliği<input name="userAgentIdentity" placeholder="Firma - Entegrasyon" required /></label>}<button type="submit" className="connection-button" disabled={create.isPending}>{create.isPending ? 'Oluşturuluyor…' : 'Bağlantıyı oluştur'}</button></form></div>}</>}
     {feedback && <IntegrationFeedbackToast feedback={feedback} onClose={() => setFeedback(null)} />}
  </section>
}

function IntegrationDetailWorkspace({ id, inline = false }: { id: string; inline?: boolean }) {
  const client = useQueryClient(); const [notice, setNotice] = useState(''); const connection = useQuery({ queryKey: ['connection', id], queryFn: () => hubApi<Connection>(`/connections/${id}`) }); const capabilities = useQuery({ queryKey: ['capabilities', id], queryFn: () => hubApi<Capability[]>(`/connections/${id}/capabilities`) }); const syncPolicies = useQuery({ queryKey: ['sync-policies', id], queryFn: () => hubApi<SyncPolicy[]>(`/connections/${id}/sync-policies`), enabled: connection.data?.platformCode === 'TRENDYOL' });
  const run = useMutation({ mutationFn: (request: { path: string; body?: object; version?: number }) => hubApi(request.path, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), ...(request.version ? { 'If-Match': `"v${request.version}"` } : {}) }, body: JSON.stringify(request.body ?? {}) }), onSuccess: () => { setNotice('İş kuyruğa alındı.'); void client.invalidateQueries({ queryKey: ['connection', id] }) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'İşlem tamamlanamadı.') })
  const saveSyncPolicy = useMutation({ mutationFn: (request: { policy: SyncPolicy; intervalSeconds: number; overlapSeconds: number; enabled: boolean }) => hubApi<SyncPolicy>(`/connections/${id}/sync-policies/${request.policy.resourceType}`, { method: 'PUT', headers: { 'If-Match': `"v${request.policy.version}"` }, body: JSON.stringify({ intervalSeconds: request.intervalSeconds, overlapSeconds: request.overlapSeconds, jitterSeconds: request.policy.jitterSeconds, enabled: request.enabled }) }), onSuccess: async () => { setNotice('Otomatik eşitleme ayarı güncellendi.'); await client.invalidateQueries({ queryKey: ['sync-policies', id] }) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Eşitleme ayarı güncellenemedi.') })
  function updateSyncPolicy(event: FormEvent<HTMLFormElement>, policy: SyncPolicy) { event.preventDefault(); const data = new FormData(event.currentTarget); saveSyncPolicy.mutate({ policy, intervalSeconds: Number(data.get('intervalSeconds')), overlapSeconds: Number(data.get('overlapSeconds') ?? policy.overlapSeconds), enabled: data.has('enabled') }) }
  async function settings(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!connection.data) return; const data = new FormData(event.currentTarget); try { const body = { displayName: data.get('displayName'), userAgentIdentity: data.get('userAgentIdentity'), externalWritesEnabled: data.has('externalWritesEnabled') }; await hubApi<Connection>(`/connections/${id}`, { method: 'PATCH', headers: { 'If-Match': `"v${connection.data.version}` + '"' }, body: JSON.stringify(body) }); setNotice(body.externalWritesEnabled ? 'Dış yazma açıldı. Yayın ve fiyat/stok işlemleri bağlantı güvenlik kontrolleriyle gönderilebilir.' : 'Salt-okunur mod etkin. Panel verileri çekebilir; Trendyol’a dış yazma yapılmaz.'); await client.invalidateQueries({ queryKey: ['connection', id] }) } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Bağlantı ayarları güncellenemedi.') } }
  async function credential(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!connection.data) return; const data = new FormData(event.currentTarget); const body = connection.data.platformCode === 'TRENDYOL_EFATURAM' ? { email: data.get('email') || null, password: data.get('password') || null } : { apiKey: data.get('apiKey'), apiSecret: data.get('apiSecret') }; try { await hubApi(`/connections/${id}/credential`, { method: 'PUT', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${connection.data.version}"` }, body: JSON.stringify(body) }); setNotice('Credential şifreli olarak yenilendi; değerler tekrar gösterilmeyecek.'); await client.invalidateQueries({ queryKey: ['connection', id] }) } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Credential kaydedilemedi.') } }
  async function evidence(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); const code = String(data.get('code') || ''); const capability = capabilities.data?.find(value => value.code === code); if (!capability) return; try { const constraints = String(data.get('constraintsJson') || '').trim(); await hubApi(`/connections/${id}/capabilities/${encodeURIComponent(code)}/evidence`, { method: 'PUT', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `\"v${capability.version}\"` }, body: JSON.stringify({ supportLevel: data.get('supportLevel'), sourceUrl: data.get('sourceUrl'), sourceVersion: data.get('sourceVersion'), environment: item.environment, storeScope: item.externalStoreId, evidenceNote: data.get('evidenceNote'), fixtureChecksum: data.get('fixtureChecksum') || null, constraintsJson: constraints || null, verifiedAt: new Date(String(data.get('verifiedAt'))).toISOString() }) }); setNotice('Capability kanıtı audit kaydıyla güncellendi.'); await client.invalidateQueries({ queryKey: ['capabilities', id] }) } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Capability kanıtı kaydedilemedi.') } }
  const rootClass = inline ? 'integration-inline-workspace' : 'content f3'
  if (connection.isLoading) return <div className={rootClass}><Busy /></div>; if (connection.isError || !connection.data) return <div className={rootClass}><ErrorBox error={connection.error} /></div>; const item = connection.data
  if (!activePlatformCodes.has(item.platformCode)) return <div className={rootClass}><Link to="/integrations" className="back">← Platformlar</Link><div className="unknown"><strong>Bağlantı aktif kapsam dışında</strong><p>ADR-016 uyarınca panel yalnız Trendyol ve Trendyol E-Faturam ile ilerler. Kapsam dışı tarihsel bağlantılara işlem sunulmaz.</p></div></div>
  const stageManual = item.environment === 'STAGE'; const readConnectionReady = item.status === 'ACTIVE' || item.status === 'VERIFIED'; const stageReady = stageManual && !item.lastErrorCode && readConnectionReady; const referenceReadSupported = item.platformCode === 'TRENDYOL' && readConnectionReady && (stageManual || capabilities.data?.some(cap => cap.code === 'REFERENCE_READ' && cap.supportLevel === 'SUPPORTED') === true); const productReadSupported = item.platformCode === 'TRENDYOL' && readConnectionReady
  return <div className={rootClass}><div className="page-heading"><div><Link to="/integrations" className="back">← Platformlar</Link><h1>{item.displayName}</h1><p className="lede">{item.environment} · {item.platformCode === 'TRENDYOL_EFATURAM' ? 'yerel kapsam' : 'mağaza'} {item.externalStoreId} · API {item.apiVersion}</p></div><Badge value={item.status} /></div>{notice && <div role="status" className="notice">{notice}</div>}
    <div className="grid"><article><small>Credential</small><strong>{credentialLabel(item)}</strong><p>{credentialHelp(item)}</p></article><article><small>Son test</small><strong><DateText value={item.lastTestedAt} /></strong><p>{connectionTestHelp(item)}</p></article><article><small>{stageManual ? 'Stage işlemleri' : 'Dış yazma'}</small><strong>{stageManual ? stageReady ? 'Hazır' : 'Bloke' : 'Kapalı'}</strong><p>{stageManual ? stageReady ? 'Manuel denemeler aktif bağlantı, credential ve teknik doğrulamalarla çalışır; sağlayıcı yanıtı iş sonucunu belirler.' : 'Son korumalı bağlantı testi başarısız. Sağlayıcı erişimi düzeltilmeden fatura gönderimi başlatılmaz.' : 'Production yazmaları master ve bağlantı anahtarlarıyla korunur.'}</p></article></div>
    {item.platformCode === 'TRENDYOL' && readConnectionReady && <div className="panel sync-policy-panel"><h2>Otomatik veritabanı eşitleme</h2><p>Sipariş ve iade akışları Trendyol’a canlı sorgu göndermeden kalıcı arka plan işleriyle yerel veritabanına işlenir. Ürün işlemleri bu otomatik akıştan ayrıdır ve yalnızca aşağıdaki manuel butonla başlatılır.</p>{syncPolicies.isLoading ? <Busy text="Eşitleme ayarları yükleniyor…" /> : syncPolicies.isError ? <ErrorBox error={syncPolicies.error} /> : <div className="sync-policy-list">{syncPolicies.data?.filter(policy => policy.resourceType !== 'PRODUCTS').map(policy => {
      const policyLabel = ({ ORDERS: 'Siparişler ve paket durumları', ORDER_RECOVERY: 'Sipariş geçmişi kurtarma', ORDER_LIFECYCLE: 'Açık sipariş yaşam döngüsü', ORDER_RECONCILE_SHORT: 'Kısa sipariş uzlaştırması', ORDER_RECONCILE_MEDIUM: 'Orta sipariş uzlaştırması', ORDER_RECONCILE_DAILY: 'Günlük sipariş uzlaştırması', ORDER_INVOICE_RECONCILIATION: 'Paket fatura kontrolü', RETURNS: 'İade talepleri', RETURN_LIFECYCLE: 'Açık iade yaşam döngüsü', RETURN_RECONCILE_SHORT: 'Kısa iade uzlaştırması', RETURN_RECONCILE_MEDIUM: 'Orta iade uzlaştırması', RETURN_RECONCILE_DAILY: 'Günlük iade uzlaştırması', STOCK_RECONCILE_SHORT: 'Kısa stok uzlaştırması', STOCK_RECONCILE_MEDIUM: 'Orta stok uzlaştırması', STOCK_RECONCILE_DAILY: 'Günlük stok uzlaştırması', REFERENCE_DATA: 'Katalog referansları' } as Record<string, string>)[policy.resourceType] ?? policy.resourceType
      const intervals: Array<[number, string]> = [[30, '30 saniye'], [60, '1 dakika'], [120, '2 dakika'], [180, '3 dakika'], [300, '5 dakika'], [600, '10 dakika'], [900, '15 dakika'], [3600, '1 saat'], [86400, '24 saat']]
      const overlaps: Array<[number, string]> = [[120, '2 dakika'], [300, '5 dakika'], [600, '10 dakika'], [900, '15 dakika']]
      return <form className="sync-policy-row" key={policy.id} onSubmit={event => updateSyncPolicy(event, policy)}><div className="sync-policy-identity"><strong>{policyLabel}</strong><Badge value={policy.healthStatus ?? 'OFFLINE'} /><small>Son tamamlanan çalışma: <DateText value={policy.lastSuccessAt} /></small><small>Son deneme: <DateText value={policy.lastAttemptAt ?? null} /></small><small>{policy.lastRequestCount ?? 0} istek · {policy.lastReceivedCount ?? 0} kayıt · {policy.lastFailedCount ?? 0} hata · {policy.lastRetryCount ?? 0} retry · {policy.lastRateLimitCount ?? 0} rate-limit{policy.consecutiveFailureCount ? ` · ${policy.consecutiveFailureCount} ardışık hata` : ''}</small>{policy.recoveryGapStatus && policy.recoveryGapStatus !== 'OK' && <small className={`sync-policy-gap sync-policy-gap-${policy.recoveryGapStatus.toLowerCase()}`}>Kurtarma aralığı: {policy.recoveryGapStatus === 'UNKNOWN' ? 'henüz watermark yok' : `${Math.round(policy.recoveryGapDays ?? 0)} gün`}</small>}</div><label>Kontrol aralığı<select name="intervalSeconds" defaultValue={policy.intervalSeconds}>{!intervals.some(([value]) => value === policy.intervalSeconds) && <option value={policy.intervalSeconds}>{policy.intervalSeconds} saniye</option>}{intervals.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label><label>Güvenlik örtüşmesi<select name="overlapSeconds" defaultValue={policy.overlapSeconds}>{!overlaps.some(([value]) => value === policy.overlapSeconds) && <option value={policy.overlapSeconds}>{Math.round(policy.overlapSeconds / 60)} dakika</option>}{overlaps.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label><label className="sync-policy-toggle"><input type="checkbox" name="enabled" defaultChecked={policy.enabled} />Aktif</label><button disabled={saveSyncPolicy.isPending}>{saveSyncPolicy.isPending ? 'Kaydediliyor…' : 'Kaydet'}</button></form>
    })}</div>}</div>}
    {productReadSupported && <div className="panel"><h2>Manuel ürün eşitleme</h2><p>Bu Trendyol mağazasındaki onaylı ürünleri, varyantları, seçenekleri ve görsel adreslerini yerel kataloğa alır. Otomatik scheduler’a bağlı değildir; yalnızca siz başlattığınızda çalışır. Trendyol’a dış yazma yapılmaz.</p><button disabled={run.isPending} onClick={() => run.mutate({ path: `/connections/${id}/product-sync-jobs` })}>{run.isPending ? 'Kuyruğa alınıyor…' : 'Ürünleri panele çek'}</button><small>İlk tarama tüm erişilebilir ürünleri sayfa sayfa alır; sonraki taramalarda değişiklik watermark’ı kullanılır.</small></div>}
    {item.platformCode === 'TRENDYOL' && item.environment === 'STAGE' && item.externalStoreId === '2738' && <div className="panel"><h2>Taze Stage test siparişi</h2><p>Yalnız resmî Stage seller 2738 test barkoduyla tek denemelik fixture oluşturur. Production ve normal dış yazma anahtarları kullanılmaz.</p><button disabled={run.isPending} onClick={() => run.mutate({ path: `/connections/${id}/stage-test-order-jobs` })}>{run.isPending ? 'Kuyruğa alınıyor…' : 'Taze Stage test siparişi oluştur'}</button></div>}
    {referenceReadSupported && <div className="panel"><h2>Salt-okunur katalog referansları</h2><p>{stageManual ? 'Aktif Stage bağlantısından kategori ve marka listelerini yerel snapshot olarak günceller. Trendyol’a yazmaz.' : 'Doğrulanmış referans okuma desteğiyle kategori ve marka listelerini yerel snapshot olarak günceller. Trendyol’a yazmaz.'}</p><div className="button-row"><button disabled={run.isPending} onClick={() => run.mutate({ path: `/connections/${id}/reference-sync-jobs?resourceType=CATEGORIES` })}>{run.isPending ? 'Kuyruğa alınıyor…' : 'Kategorileri eşitle'}</button><button className="secondary" disabled={run.isPending} onClick={() => run.mutate({ path: `/connections/${id}/reference-sync-jobs?resourceType=BRANDS` })}>{run.isPending ? 'Kuyruğa alınıyor…' : 'Markaları eşitle'}</button><Link className="button-link secondary" to="/mappings/categories">Kategori eşitleme ekranına git</Link><Link className="button-link secondary" to="/mappings/categories?view=brands">Marka eşlemelerine git</Link></div></div>}
    {item.platformCode === 'TRENDYOL' && <form className="panel form-panel integration-platform-settings" onSubmit={settings}><h2>Platform ayarları</h2><p className="form-help">Sipariş, iade ve referans verileri yerel panele otomatik alınır. Dış yazma kapalıyken panel yalnızca okur; ürün, fiyat, stok ve kargo işlemlerini Trendyol’a göndermez.</p><label>Bağlantı adı<input name="displayName" defaultValue={item.displayName} required /></label><label>User-Agent kimliği<input name="userAgentIdentity" placeholder="Firma-Adı - Entegrasyon-Adı" autoComplete="off" required /></label><label className="integration-write-toggle"><input name="externalWritesEnabled" type="checkbox" defaultChecked={item.externalWritesEnabled} /><span><strong>Trendyol’a dış yazmayı etkinleştir</strong><small>Yayınlama, fiyat/stok ve kargo aksiyonları için izin ver. Kapalıyken senkronizasyon yalnızca Trendyol’dan veri çeker.</small></span></label><div className="integration-write-warning"><strong>{item.externalWritesEnabled ? 'Dış yazma açık' : 'Salt-okunur mod açık'}</strong><span>{item.externalWritesEnabled ? 'İşlemler ayrıca bağlantı, capability ve production güvenlik kontrollerinden geçer.' : 'Bu bağlantıdaki otomatik ve manuel okuma işleri çalışır; sağlayıcıya veri gönderilmez.'}</span></div><button>Platform ayarlarını kaydet</button></form>}
    {item.platformCode === 'TRENDYOL_EFATURAM' && <article className="panel"><h2>E-Faturam hesap yetkisi</h2><p>Fatura API'si bağlı bireysel E-Faturam hesabının oturumunu kullanır. Firma ve kullanıcı kimliği yalnız sağlayıcının erişim tokenından alınır; panelde gösterilmez veya ayar olarak saklanmaz.</p><div className="card-list"><div className="record-card"><span><strong>Belge türü</strong><small>Kurumsal ve E-Fatura mükellefi: TEMELFATURA; diğer siparişler: EARSIVFATURA</small></span><Badge value="AUTO" /></div><div className="record-card"><span><strong>İnternet satışı ek alanları</strong><small>Kullanıcı ayarı değildir; gerekli olduğunda Trendyol siparişi ve resmî kargo kataloğundan otomatik hazırlanır.</small></span><Badge value="AUTO" /></div></div></article>}
    <div className="split"><form className="panel form-panel" onSubmit={credential}><h2>Credential döndür</h2>{item.platformCode === 'TRENDYOL_EFATURAM' ? <><label>Hesap e-postası<input name="email" type="email" autoComplete="username" required /></label><label>Hesap parolası<input name="password" type="password" autoComplete="new-password" required /></label><p>Bu alanlar yalnız şifreli credential kaydında tutulur ve tekrar gösterilmez.</p></> : <><label>API key<input name="apiKey" type="password" autoComplete="off" required /></label><label>API secret<input name="apiSecret" type="password" autoComplete="new-password" required /></label></>}<button disabled={run.isPending}>Şifreli kaydet</button><button type="button" className="secondary" disabled={!item.hasCredential || run.isPending} onClick={() => run.mutate({ path: `/connections/${id}/test-jobs` })}>Bağlantıyı test et</button></form>
      {stageManual ? <div className="panel"><h2>Stage operasyon durumu</h2><p>Manuel testler capability kanıtı eksikliği nedeniyle durmaz. Bağlantı, credential, girdi doğrulaması, tekrar koruması ve sağlayıcı yanıt doğrulaması korunur.</p><p>Teknik kanıt ve ayrıntılı hata kayıtları İşlem Takibi’nde tutulur.</p></div> : <div className="panel"><h2>Capability kanıtları</h2>{capabilities.isLoading ? <Busy /> : capabilities.isError ? <ErrorBox error={capabilities.error} /> : <div className="capability-list">{capabilities.data?.map(cap => <div key={cap.code}><span><strong>{cap.code}</strong><small><DateText value={cap.verifiedAt} /></small>{cap.evidenceNote && <small className="capability-note">{cap.evidenceNote}</small>}</span><Badge value={cap.supportLevel} /></div>)}</div>}</div>}</div>
    {!stageManual && capabilities.data?.length ? <form className="panel form-panel" onSubmit={evidence}><h2>Capability kanıtı kaydet</h2><p>Production write capability için resmî kanıt kaydı zorunludur.</p><label>Capability<select name="code">{capabilities.data.map(cap => <option key={cap.code}>{cap.code}</option>)}</select></label><label>Destek seviyesi<select name="supportLevel" defaultValue="UNKNOWN"><option>UNKNOWN</option><option>SUPPORTED</option><option>NOT_SUPPORTED</option></select></label><label>Resmî kaynak URL<input name="sourceUrl" type="url" placeholder="https://developers.trendyol.com veya developers.trendyolefaturam.com/..." required /></label><label>Kaynak sürümü<input name="sourceVersion" defaultValue="V2" required /></label><label>Doğrulama zamanı<input name="verifiedAt" type="datetime-local" required /></label><label>Kanıt notu<textarea name="evidenceNote" maxLength={1000} required /></label><label>Stage/SIT fixture SHA-256<input name="fixtureChecksum" minLength={64} maxLength={64} /></label><label>Kısıtlar JSON<textarea name="constraintsJson" placeholder='{"allowedActions":["PICKING","TRACKING_NUMBER"]}' /></label><button>Kanıtı kaydet</button></form> : null}
  </div>

}

export function IntegrationDetailPage() {
  const { id = '' } = useParams()
  return <IntegrationDetailWorkspace id={id} />
}

function ListPage({ eyebrow, title, description, children }: { eyebrow: string; title: string; description: string; children: React.ReactNode }) { return <section className="content f3"><div className="page-heading"><div><p className="eyebrow">{eyebrow}</p><h1>{title}</h1><p className="lede">{description}</p></div><Badge value="LIVE READ" /></div><div className="panel">{children}</div></section> }

export function OrdersPage() {
  const client = useQueryClient()
  const [processingOrderId, setProcessingOrderId] = useState<string | null>(null)
  const [searchParams, setSearchParams] = useSearchParams(); const requestedSearch = searchParams.get('search') ?? ''; const initialFilters = { ...initialOrderFilters, search: requestedSearch, status: searchParams.get('status') ?? 'ALL' }; const [filterForm, setFilterForm] = useState<OrderFilters>(initialFilters); const [filters, setFilters] = useState<OrderFilters>(initialFilters); const [advancedFilters, setAdvancedFilters] = useState(false); const [pageSize, setPageSize] = useState(50); const [page, setPage] = useState(1); const [pageInput, setPageInput] = useState('1'); const [selectedIds, setSelectedIds] = useState<string[]>([]); const [menu, setMenu] = useState<{ orderId: string; kind: 'invoice' | 'actions' } | null>(null); const [bulkOpen, setBulkOpen] = useState(false); const [bulkMenuPlacement, setBulkMenuPlacement] = useState<'up' | 'down'>('up'); const [bulkNotice, setBulkNotice] = useState(''); const [bulkNoticeVersion, setBulkNoticeVersion] = useState(0); const [invoiceInfoOrder, setInvoiceInfoOrder] = useState<Order | null>(null); const [invoiceViewerOrder, setInvoiceViewerOrder] = useState<Order | null>(null); const [invoiceDraftOrder, setInvoiceDraftOrder] = useState<Order | null>(null); const [invoiceUploadOrder, setInvoiceUploadOrder] = useState<Order | null>(null); const [courierOrder, setCourierOrder] = useState<{ item: Order; items: Order[] } | null>(null); const [shippingLabel, setShippingLabel] = useState<{ item: Order; format: ShippingLabelFormat } | null>(null); const [shippingLabelBatch, setShippingLabelBatch] = useState<{ items: Order[]; format: ShippingLabelFormat } | null>(null); const [singleSyncOpen, setSingleSyncOpen] = useState(false); const [previewImage, setPreviewImage] = useState<{ url: string; title: string } | null>(null); const [columnFilterOpen, setColumnFilterOpen] = useState<'cargo' | 'invoice' | 'label' | null>(null); const [labelSettings] = useState<ShippingLabelSettings>(() => loadShippingLabelSettings()); const [printedLabels, setPrintedLabels] = useState<Set<string>>(() => new Set(loadPrintedShippingLabels())); const [hiddenCancelledOrderIds, setHiddenCancelledOrderIds] = useState<string[]>(() => { try { const value = JSON.parse(localStorage.getItem('ravencia.hiddenCancelledOrders') ?? '[]'); return Array.isArray(value) ? value.filter((id): id is string => typeof id === 'string') : [] } catch { return [] } }); const [hiddenCancelledNoticeVersion, setHiddenCancelledNoticeVersion] = useState(0)
  function showBulkNotice(message: string) { setBulkNotice(message); setBulkNoticeVersion(current => current + 1) }


  const [pageCursors, setPageCursors] = useState<Record<number, string | null>>({ 1: null })
  const hasLocalFilters = filters.invoice !== 'ALL' || filters.invoiceType !== 'ALL' || filters.invoiceRegion !== 'ALL' || filters.label !== 'ALL'
  const orderCursor = pageCursors[page] ?? null
  const ordersQuery = useQuery({ queryKey: hasLocalFilters ? ['orders', 'filtered-all', filters] : ['orders', 'page', filters, pageSize, page, orderCursor], queryFn: () => hasLocalFilters ? loadAllOrderPages(filters) : loadOrderPage(filters, pageSize, orderCursor, page), placeholderData: keepPreviousData, staleTime: 30_000, refetchOnWindowFocus: true })
  const summary = useQuery({ queryKey: ['orders', 'summary', filters.platform], queryFn: () => hubApi<OrderSummary>(filters.platform === 'ALL' ? '/orders/summary' : `/orders/summary?platform=${encodeURIComponent(filters.platform)}`) })
  const connections = useQuery({ queryKey: ['connections', 'orders-invoice'], queryFn: () => loadAllPages<Connection>('/connections') })
  const providers = connections.data?.items.filter(x => x.platformCode === 'TRENDYOL_EFATURAM' && !x.lastErrorCode && (x.status === 'ACTIVE' || x.status === 'VERIFIED')) ?? []
  const provider = providers.find(x => x.environment === 'PRODUCTION') ?? providers.find(x => x.environment === 'STAGE') ?? null
  const trendyolConnections = connections.data?.items.filter(x => x.platformCode === 'TRENDYOL' && (x.status === 'ACTIVE' || x.status === 'VERIFIED')) ?? []
  const trendyolConnection = trendyolConnections
  const statuses = [
    ['ALL', 'Tümü'],
    ['NEW', 'Yeni'],
    ['PROCESSING', 'İşleme alınanlar'],
    ['ON_HOLD', 'Askıdaki'],
    ['RESENT', 'Yeniden Gönderimler'],
    ['SHIPPED', 'Kargoda'],
    ['DELIVERED', 'Teslim edildi'],
    ['CANCELLED', 'İptal'],
    ['PARTIALLY_CANCELLED', 'Kısmi iptal'],
  ] as const

  const allOrders = ordersQuery.data?.items ?? [];
  const all = allOrders.filter(item => !hiddenCancelledOrderIds.includes(item.id) && (filters.status !== 'ALL' || item.derivedStatus.toUpperCase() !== 'CANCELLED'));
  const ordersLoading = ordersQuery.isPending; const ordersError = ordersQuery.error
  const cargos = Array.from(new Set(all.flatMap(item => [item.packages?.[0]?.cargoProviderName ?? item.cargoProviderName].filter((value): value is string => !!value))))
  const platforms = Array.from(new Set(all.map(item => item.platformCode).filter(Boolean)))
  const invoiceStatuses = [['FATURA_BEKLIYOR', 'Fatura bekliyor'], ['FATURA_ISLENIYOR', 'Fatura işleniyor'], ['FATURA_KONTROLDE', 'Kontrolde'], ['FATURA_KESILDI', 'Fatura kesildi'], ['FATURA_REDDEDILDI', 'Reddedildi'], ['FATURA_IPTAL', 'İptal edildi']] as const
  const sortOptions = [['DATE_DESC', 'Sipariş Tarihi (Yeniden Eskiye)'], ['DATE_ASC', 'Sipariş Tarihi (Eskiden Yeniye)'], ['DUE_DESC', 'Kargoya Vermek İçin Kalan Süre (Yeniden Eskiye)'], ['DUE_ASC', 'Kargoya Vermek İçin Kalan Süre (Eskiden Yeniye)']] as const
  const getInvoiceType = (item: Order) => item.orderType.toUpperCase() === 'KURUMSAL' ? 'KURUMSAL' : 'BIREYSEL'
  const getInvoiceRegion = (item: Order) => item.isMicroExport ? 'MICRO_EXPORT' : 'TR'
  const isLabelPrinted = (item: Order) => Array.from(printedLabels).some(key => key.startsWith(`${item.id}:`))
  const updateFilter = <K extends keyof OrderFilters>(key: K, value: OrderFilters[K]) => setFilterForm(current => ({ ...current, [key]: value }))
  const applyFilterValue = <K extends keyof OrderFilters>(key: K, value: OrderFilters[K]) => { const next = { ...filterForm, [key]: value }; setFilterForm(next); setFilters(next); setPage(1) }
  const clearInvoiceFilters = () => { const next = { ...filterForm, invoice: 'ALL', invoiceType: 'ALL', invoiceRegion: 'ALL' }; setFilterForm(next); setFilters(next); setPage(1) }
  const applyFilters = () => { setFilters(filterForm); setPage(1) }
  const clearFilters = () => { setFilterForm(initialOrderFilters); setFilters(initialOrderFilters); setPage(1) }
  const selectStatus = (status: string) => { const next = { ...filterForm, status }; setFilterForm(next); setFilters(current => ({ ...current, status })); setSearchParams(current => { const params = new URLSearchParams(current); if (status === 'ALL') params.delete('status'); else params.set('status', status); return params }, { replace: true }); setPage(1) }
  function restoreHiddenCancelledOrders() {
    setHiddenCancelledOrderIds([])
    setHiddenCancelledNoticeVersion(0)
    try { localStorage.removeItem('ravencia.hiddenCancelledOrders') } catch { /* Private browsing may disallow local storage. */ }
  }
  const tabCount = (tab: string) => {
    const summaryKey = ({ ALL: 'all', NEW: 'new', PROCESSING: 'processing', SHIPPED: 'shipped', DELIVERED: 'delivered', RESENT: 'resent', ON_HOLD: 'onHold', CANCELLED: 'cancelled', RETURNED: 'returned', RETURN_IN_TRANSIT: 'returnInTransit', PARTIALLY_CANCELLED: 'partiallyCancelled', MANUAL_REVIEW: 'manualReview' } as Record<string, keyof OrderSummary | undefined>)[tab]
    if (summary.data && summaryKey && typeof summary.data[summaryKey] === 'number') return summary.data[summaryKey]
    return 0
  }
  // Status, search, platform, cargo, listing, date range, sort and pagination
  // are applied by the API. Only local presentation filters remain here.
  const items = all.filter(item => (filters.invoice === 'ALL' || item.invoiceStatus === filters.invoice) && (filters.invoiceType === 'ALL' || getInvoiceType(item) === filters.invoiceType) && (filters.invoiceRegion === 'ALL' || getInvoiceRegion(item) === filters.invoiceRegion) && (filters.label === 'ALL' || (filters.label === 'PRINTED' ? isLabelPrinted(item) : !isLabelPrinted(item))))
  function exportOrders() {
    const escapeCsv = (value: unknown) => `"${String(value ?? '').replaceAll('"', '""')}"`
    const rows = [
      ['Sipariş No', 'Platform', 'Müşteri', 'Sipariş Tarihi', 'Durum', 'Fatura Durumu', 'Kargo', 'Ürünler', 'Toplam Tutar', 'Para Birimi'],
      ...items.map(item => [
        item.orderNumber,
        item.platformDisplayName || item.platformCode,
        item.customerName,
        new Date(item.orderedAt).toLocaleString('tr-TR'),
        item.derivedStatus,
        item.invoiceStatus,
        item.packages?.[0]?.cargoProviderName ?? item.cargoProviderName ?? '',
        (item.lines ?? []).map(line => `${line.title} x${line.orderedQuantity}`).join(' | '),
        item.grossAmount.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 }),
        item.currency
      ])
    ]
    const csv = `\uFEFF${rows.map(row => row.map(escapeCsv).join(';')).join('\r\n')}`
    const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }))
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = `ravencia-siparisler-${new Date().toISOString().slice(0, 10)}.csv`
    document.body.appendChild(anchor)
    anchor.click()
    anchor.remove()
    URL.revokeObjectURL(url)
    showBulkNotice(`${items.length.toLocaleString('tr-TR')} sipariş dışa aktarıldı.`)
  }
  const totalCount = hasLocalFilters ? items.length : ordersQuery.data?.totalCount ?? 0; const totalPages = totalCount > 0 ? Math.ceil(totalCount / pageSize) : ordersQuery.data?.hasMore ? page + 1 : 1; const safePage = Math.min(page, totalPages); const pageItems = hasLocalFilters ? items.slice((safePage - 1) * pageSize, safePage * pageSize) : items; const selectablePageItems = pageItems.filter(item => item.derivedStatus.toUpperCase() !== 'CANCELLED'); const allPageSelected = selectablePageItems.length > 0 && selectablePageItems.every(item => selectedIds.includes(item.id)); const activeOrderFilterCount = [filters.search.trim(), filters.platform !== 'ALL' ? filters.platform : '', filters.listing !== 'ALL' ? filters.listing : '', filters.dateFrom, filters.dateTo, filters.cargo !== 'ALL' ? filters.cargo : '', filters.invoice !== 'ALL' ? filters.invoice : '', filters.invoiceType !== 'ALL' ? filters.invoiceType : '', filters.invoiceRegion !== 'ALL' ? filters.invoiceRegion : ''].filter(Boolean).length
  useEffect(() => { setPageInput(String(safePage)) }, [safePage])
  useEffect(() => { if (ordersQuery.data && page > totalPages) setPage(totalPages) }, [ordersQuery.data, page, totalPages])
  function submitPageJump(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const target = Number(pageInput)
    if (!Number.isInteger(target) || target < 1 || target > totalPages) {
      setPageInput(String(safePage))
      showBulkNotice(`Sayfa numarası 1 ile ${totalPages} arasında olmalıdır.`)
      return
    }
    setPage(target)
  }
  useEffect(() => {
    const next = ordersQuery.data?.nextCursor
    if (!next || pageCursors[page + 1] === next) return
    setPageCursors(current => ({ ...current, [page + 1]: next }))
  }, [ordersQuery.data?.nextCursor, page, pageCursors])
  useEffect(() => { setPageCursors({ 1: null }); setPage(1); setSelectedIds([]) }, [filters, pageSize])
  useEffect(() => { if (!requestedSearch) return; setFilterForm(current => ({ ...current, search: requestedSearch })); setFilters(current => ({ ...current, search: requestedSearch })); setPage(1) }, [requestedSearch])
  useEffect(() => {
    const closeColumnFilter = (event: PointerEvent) => {
      if (!(event.target instanceof Element)) return
      if (!event.target.closest('.order-column-filter')) setColumnFilterOpen(null)
      if (!event.target.closest('.bulk-menu-shell')) setBulkOpen(false)
      if (!event.target.closest('.row-menu')) setMenu(null)
    }
    const closeOnEscape = (event: globalThis.KeyboardEvent) => {
      if (event.key === 'Escape') setColumnFilterOpen(null)
    }
    document.addEventListener('pointerdown', closeColumnFilter)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeColumnFilter)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [])
  useEffect(() => { const onHidden = (event: Event) => { const id = (event as CustomEvent<string>).detail; if (typeof id !== 'string') return; setHiddenCancelledOrderIds(current => { const next = current.includes(id) ? current : [...current, id]; try { localStorage.setItem('ravencia.hiddenCancelledOrders', JSON.stringify(next)) } catch { /* Private browsing may disallow local storage. */ } return next }); setHiddenCancelledNoticeVersion(current => current + 1); setSelectedIds(current => current.filter(value => value !== id)); setMenu(null) }; window.addEventListener('ravencia:cancelled-order-hidden', onHidden); return () => window.removeEventListener('ravencia:cancelled-order-hidden', onHidden) }, [])
  useEffect(() => { if (!hiddenCancelledNoticeVersion) return; const timer = window.setTimeout(() => setHiddenCancelledNoticeVersion(0), 5000); return () => window.clearTimeout(timer) }, [hiddenCancelledNoticeVersion])
  useEffect(() => { const invoiceId = searchParams.get('invoice'); if (!invoiceId || !all.length) return; const item = all.find(order => order.invoiceId === invoiceId); if (!item) return; setInvoiceViewerOrder(item); setSearchParams(current => { const next = new URLSearchParams(current); next.delete('invoice'); return next }, { replace: true }) }, [all, searchParams, setSearchParams])
  function updateSelection(id: string, checked: boolean) { if (all.find(item => item.id === id)?.derivedStatus.toUpperCase() === 'CANCELLED') return; setSelectedIds(current => checked ? Array.from(new Set([...current, id])) : current.filter(value => value !== id)) }
  function togglePageSelection(checked: boolean) { setSelectedIds(current => checked ? Array.from(new Set([...current, ...selectablePageItems.map(item => item.id)])) : current.filter(id => !selectablePageItems.some(item => item.id === id))) }
  const selectedOrders = all.filter(item => selectedIds.includes(item.id) && item.derivedStatus.toUpperCase() !== 'CANCELLED')
  function toggleBulkMenu(event: MouseEvent<HTMLButtonElement>) {
    if (bulkOpen) { setBulkOpen(false); return }
    const rect = event.currentTarget.getBoundingClientRect()
    setBulkMenuPlacement(window.innerHeight - rect.bottom >= 240 ? 'down' : 'up')
    setMenu(null)
    setBulkOpen(true)
  }
  async function bulkAction(kind: 'processing' | 'courier' | 'invoice' | 'labels') {
    setBulkOpen(false)
    if (kind === 'processing') {
      const eligible = selectedOrders.filter(item => item.derivedStatus.toUpperCase() === 'NEW' && item.packages?.length)
      if (!eligible.length) { showBulkNotice('Seçimde işleme alınabilecek yeni sipariş bulunmuyor.'); return }
      if (!window.confirm(`${eligible.length} yeni sipariş Trendyol’da “İşleme Al” durumuna geçirilecek. Devam edilsin mi?`)) return
      try {
        const operationId = crypto.randomUUID()
        for (const item of eligible) { const pack = item.packages![0]; const updatedShipment = await hubApi<Shipment>(`/shipments/${pack.id}/instant-process`, { method: 'POST', headers: { 'Idempotency-Key': `bulk-picking:${pack.id}:${pack.version}:${operationId}`, 'If-Match': `"v${pack.version}"` } }); moveOrderAcrossStatusCaches(client, item, updatedShipment) }
        showBulkNotice(`${eligible.length} sipariş anlık olarak işleme alındı ve panelde güncellendi.`)
        setSelectedIds([])
        await client.refetchQueries({ queryKey: ['orders', 'summary'], type: 'active' })
        await client.refetchQueries({ queryKey: ['orders'], type: 'active' })
      } catch (error) { showBulkNotice(error instanceof Error ? error.message : 'Toplu işleme alma tamamlanamadı.') }
      return
    }
    if (kind === 'courier') { const eligible = selectedOrders.filter(item => item.packages?.length); const first = eligible[0]; if (first) setCourierOrder({ item: first, items: eligible }); else showBulkNotice('Seçimde kargo paketi bulunan sipariş yok.'); return }
    if (kind === 'invoice') { const first = selectedOrders.find(item => item.invoiceStatus === 'FATURA_BEKLIYOR'); if (first) setInvoiceDraftOrder(first); else showBulkNotice('Seçimde fatura bekleyen sipariş yok.'); return }
    const printable = selectedOrders.filter(item => item.packages?.[0]?.cargoTrackingNumber || item.cargoTrackingNumber)
    if (!printable.length) { showBulkNotice('Seçimde takip numarası bulunan sipariş yok.'); return }
    if (!labelSettings.showA4Button && !labelSettings.showStickerButton) { showBulkNotice('Kargo etiketi yazdırma ayarlardan kapatılmış.'); return }
    const format: ShippingLabelFormat = labelSettings.showStickerButton ? 'sticker' : 'a4'
    setShippingLabelBatch({ items: printable, format })
  }
  async function processSingleOrder(item: Order) {
    if (processingOrderId) return
    setMenu(null)
    setProcessingOrderId(item.id)
    showBulkNotice(`Sipariş #${item.orderNumber} API yanıtı bekleniyor…`)
    try {
      const updatedShipment = await hubApi<Shipment>(`/orders/${item.id}/instant-process`, {
        method: 'POST',
        headers: { 'Idempotency-Key': `instant-picking:${item.id}:${Date.now()}` }
      })
      moveOrderAcrossStatusCaches(client, item, updatedShipment)
      setSelectedIds(current => current.filter(id => id !== item.id))
      await client.refetchQueries({ queryKey: ['orders', 'summary'], type: 'active' })
      await client.refetchQueries({ queryKey: ['orders'], type: 'active' })
      showBulkNotice(`Sipariş #${item.orderNumber} anlık olarak işleme alındı ve panelde güncellendi.`)
    } catch (error) {
      showBulkNotice(error instanceof Error ? error.message : 'Sipariş işleme alınamadı.')
    } finally {
      setProcessingOrderId(current => current === item.id ? null : current)
    }
  }

  return <section className="content f3 orders-page"><div className="page-heading"><div><p className="eyebrow">Sipariş yönetimi</p><h1>Sipariş Yönetimi</h1><p className="lede">Tüm pazar yeri siparişlerinizi tek merkezden yönetin ve takip edin.</p></div><div className="page-heading-actions orders-reference-heading-actions"><button type="button" className="secondary orders-export-action" disabled={!items.length} onClick={exportOrders}><span className="orders-export-icon" aria-hidden="true" />Dışa Aktar</button><button type="button" className="orders-sync-action" onClick={() => setSingleSyncOpen(true)}><span aria-hidden="true">↻</span> Sipariş Senkronizasyonu</button></div></div>
     {bulkNotice && <div key={bulkNoticeVersion} className="notice order-bulk-notice" role="status">{bulkNotice}<button type="button" aria-label="Bildirimi kapat" onClick={() => setBulkNotice('')}>×</button></div>}{hiddenCancelledNoticeVersion > 0 && hiddenCancelledOrderIds.length > 0 && <div className="notice order-hidden-cancelled-notice" role="status"><span>{hiddenCancelledOrderIds.length} iptal sipariş gizli.</span><div className="order-hidden-cancelled-notice-actions"><button type="button" className="notice-dismiss" onClick={() => setHiddenCancelledNoticeVersion(0)}>Kapat</button><button type="button" onClick={restoreHiddenCancelledOrders}>Gizlenenleri geri göster</button></div></div>}
    <div className="orders-reference-filter-shell"><div className="order-tabs" role="tablist" aria-label="Sipariş durumları">{statuses.map(([value,label]) => <button type="button" role="tab" aria-selected={filters.status === value} className={filters.status === value ? 'active' : ''} key={value} onClick={() => selectStatus(value)}><span>{label}</span><b>{summary.isLoading ? '…' : summary.isError ? '—' : tabCount(value)}</b><small>Paket</small></button>)}</div>
    <section className="order-filter-panel" aria-label="Sipariş filtreleri"><header className="order-filter-heading"><div><span className="order-filter-kicker">Çalışma alanı</span><strong>Siparişleri filtrele</strong><small>Arama, kanal ve tarih seçenekleriyle görünümü daraltın.</small></div><div className="order-filter-heading-meta"><span>{totalCount.toLocaleString('tr-TR')} kayıt</span>{activeOrderFilterCount > 0 && <b>{activeOrderFilterCount} aktif filtre</b>}</div></header><div className="order-filter-primary"><label className="order-search"><span aria-hidden="true">⌕</span><input aria-label="Sipariş ara" value={filterForm.search} onChange={event => updateFilter('search', event.target.value)} placeholder="Sipariş no, müşteri, SKU veya barkod ara…" onKeyDown={event => { if (event.key === 'Enter') applyFilters() }} /></label><label>Platform<select value={filterForm.platform} onChange={event => updateFilter('platform', event.target.value)}><option value="ALL">Tüm platformlar</option>{platforms.map(value => <option key={value} value={value}>{value === 'TRENDYOL' ? 'Trendyol' : value}</option>)}</select></label><button type="button" className="filter-toggle" onClick={() => setAdvancedFilters(value => !value)} aria-expanded={advancedFilters}><span>{advancedFilters ? 'Gelişmişi gizle' : 'Gelişmiş filtreler'}</span>{activeOrderFilterCount > 0 && <b>{activeOrderFilterCount}</b>}</button><button type="button" className="secondary filter-clear" onClick={clearFilters}>Temizle</button><button type="button" className="filter-apply" onClick={applyFilters}>Uygula</button><button type="button" className="secondary single-order-sync-btn" onClick={() => setSingleSyncOpen(true)} title="Trendyol'dan sipariş numarası ile tekil sipariş çek">⚡ Tekil Sipariş Çek</button></div>{advancedFilters && <div className="order-filter-advanced"><label>Listeleme durumu<select value={filterForm.listing} onChange={event => updateFilter('listing', event.target.value)}><option value="ALL">Tüm kayıtlar</option><option value="OPEN">Açık siparişler</option><option value="CLOSED">Kapanan siparişler</option></select></label><label>Sipariş tarihi başlangıç<input type="date" value={filterForm.dateFrom} onChange={event => updateFilter('dateFrom', event.target.value)} /></label><label>Sipariş tarihi bitiş<input type="date" value={filterForm.dateTo} onChange={event => updateFilter('dateTo', event.target.value)} /></label></div>}</section><div className="orders-reference-bottom-toolbar" aria-label="Sipariş görünüm araçları"><div className="orders-bottom-filter-group"><div className="bulk-menu-shell"><button type="button" className="bulk-action" disabled={!selectedOrders.length} aria-expanded={bulkOpen} onClick={toggleBulkMenu}><span>Toplu İşlemler</span><b aria-hidden="true">⌄</b></button>{bulkOpen && <div className={`bulk-action-menu opens-${bulkMenuPlacement}`} role="menu"><button type="button" role="menuitem" onClick={() => void bulkAction('processing')}><b>01</b><span>İşleme Al<small>Yalnız yeni siparişler</small></span></button><button type="button" role="menuitem" onClick={() => void bulkAction('courier')}><b>02</b><span>Kargo firmasını değiştir<small>Seçili paketler</small></span></button><button type="button" role="menuitem" onClick={() => void bulkAction('invoice')}><b>03</b><span>Toplu fatura kes<small>Önce taslakları kontrol edin</small></span></button><button type="button" role="menuitem" onClick={() => void bulkAction('labels')}><b>04</b><span>Kargo stickerlarını yazdır<small>Takip numarası olanlar</small></span></button></div>}</div>{selectedIds.length > 0 && <span className="orders-selection-summary" role="status" aria-live="polite">{selectedIds.length} sipariş seçildi</span>}<label className="orders-bottom-select orders-sort-filter"><span>Sıralama</span><select aria-label="Sıralama" value={filterForm.sort} onChange={event => applyFilterValue('sort', event.target.value as OrderSort)}>{sortOptions.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label></div><div className="orders-bottom-pagination" aria-label="Sipariş sayfalandırma"><label className="orders-page-size-control"><span>Göster</span><select aria-label="Sayfada gösterilecek sipariş sayısı" value={pageSize} onChange={event => { setPageSize(Number(event.target.value)); setPage(1) }}><option value={20}>20</option><option value={50}>50</option><option value={100}>100</option><option value={200}>200</option></select><span>sipariş</span></label><nav className="orders-page-navigation" aria-label="Sipariş sayfaları"><button type="button" className="orders-page-nav-button" aria-label="Önceki sayfa" title="Önceki sayfa" disabled={safePage <= 1} onClick={() => setPage(safePage - 1)}>‹</button><form className="orders-page-jump" aria-label="Sayfaya git" onSubmit={submitPageJump}><label htmlFor="orders-page-number">Sayfa</label><div className="orders-page-number-field"><input id="orders-page-number" aria-label="Sayfa numarası" type="number" min={1} max={totalPages} inputMode="numeric" value={pageInput} onChange={event => setPageInput(event.target.value)} /><span className="orders-page-total" aria-label={`Toplam ${totalPages} sayfa`}>/ {totalPages}</span></div><button type="submit" className="orders-page-jump-button">Git</button></form><button type="button" className="orders-page-nav-button" aria-label="Sonraki sayfa" title="Sonraki sayfa" disabled={safePage >= totalPages} onClick={() => setPage(safePage + 1)}>›</button></nav></div></div></div>
    {ordersLoading && !all.length ? <Busy text="Yerel sipariş kayıtları yükleniyor…" /> : ordersError && !all.length ? <ErrorBox error={ordersError} /> : !all.length ? <Empty>Aktif ve kanıtlanmış bağlantıdan sipariş eşitlemesi çalıştırıldığında kayıtlar burada görünür.</Empty> : !items.length ? <Empty>Seçili durum ve filtrelerle eşleşen sipariş yok.</Empty> : <><div className="order-reference-table"><div className="order-reference-head"><label className="order-select"><input type="checkbox" checked={allPageSelected} disabled={!selectablePageItems.length} onChange={event => togglePageSelection(event.target.checked)} aria-label="Sayfadaki siparişleri seç" /></label><strong>Sipariş Bilgileri</strong><strong>Alıcı</strong><strong>Bilgiler</strong><strong>Birim Fiyat</strong><div className={filters.cargo !== 'ALL' ? 'order-column-filter has-filter' : 'order-column-filter'}><button type="button" className="order-column-filter-trigger" aria-label="Kargo filtresini aç" aria-expanded={columnFilterOpen === 'cargo'} aria-controls="orders-cargo-filter" onClick={() => setColumnFilterOpen(current => current === 'cargo' ? null : 'cargo')}><span>Kargo</span><svg className="order-filter-funnel" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16l-6 7.2V18l-4 1v-6.8L4 5z" /></svg></button>{columnFilterOpen === 'cargo' && <div id="orders-cargo-filter" className="order-column-filter-popover" role="dialog" aria-label="Kargo filtreleri"><label>Kargo firması<select aria-label="Kargo firmasına göre filtrele" value={filterForm.cargo} onChange={event => applyFilterValue('cargo', event.target.value)}><option value="ALL">Tüm kargolar</option>{cargos.map(value => <option key={value} value={value}>{cargoLabel(value)}</option>)}</select></label>{filterForm.cargo !== 'ALL' && <button type="button" className="order-column-filter-reset" onClick={() => { applyFilterValue('cargo', 'ALL'); setColumnFilterOpen(null) }}>Filtreyi temizle</button>}</div>}</div><div className={filters.invoice !== 'ALL' || filters.invoiceType !== 'ALL' || filters.invoiceRegion !== 'ALL' ? 'order-column-filter order-invoice-column-filter has-filter' : 'order-column-filter order-invoice-column-filter'}><button type="button" className="order-column-filter-trigger" aria-label="Fatura filtresini aç" aria-expanded={columnFilterOpen === 'invoice'} aria-controls="orders-invoice-filter" onClick={() => setColumnFilterOpen(current => current === 'invoice' ? null : 'invoice')}><span>Fatura</span><svg className="order-filter-funnel" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16l-6 7.2V18l-4 1v-6.8L4 5z" /></svg></button>{columnFilterOpen === 'invoice' && <div id="orders-invoice-filter" className="order-column-filter-popover invoice-filter-popover" role="dialog" aria-label="Fatura filtreleri"><label>Fatura durumu<select aria-label="Fatura durumuna göre filtrele" value={filterForm.invoice} onChange={event => applyFilterValue('invoice', event.target.value)}><option value="ALL">Durum: Tümü</option>{invoiceStatuses.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label><label>Fatura türü<select aria-label="Fatura türüne göre filtrele" value={filterForm.invoiceType} onChange={event => applyFilterValue('invoiceType', event.target.value)}><option value="ALL">Tür: Tümü</option><option value="BIREYSEL">Bireysel</option><option value="KURUMSAL">Kurumsal</option></select></label><label>Fatura bölgesi<select aria-label="Fatura bölgesine göre filtrele" value={filterForm.invoiceRegion} onChange={event => applyFilterValue('invoiceRegion', event.target.value)}><option value="ALL">Bölge: Tümü</option><option value="TR">Türkiye</option><option value="MICRO_EXPORT">Mikro ihracat</option></select></label>{(filterForm.invoice !== 'ALL' || filterForm.invoiceType !== 'ALL' || filterForm.invoiceRegion !== 'ALL') && <button type="button" className="order-column-filter-reset" onClick={() => { clearInvoiceFilters(); setColumnFilterOpen(null) }}>Filtreleri temizle</button>}</div>}</div><div className={filters.label !== 'ALL' ? 'order-column-filter has-filter order-label-column-filter' : 'order-column-filter order-label-column-filter'}><button type="button" className="order-column-filter-trigger" aria-label="Etiket filtresini aç" aria-expanded={columnFilterOpen === 'label'} aria-controls="orders-label-filter" onClick={() => setColumnFilterOpen(current => current === 'label' ? null : 'label')}><span>Durum</span><svg className="order-filter-funnel" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16l-6 7.2V18l-4 1v-6.8L4 5z" /></svg></button>{columnFilterOpen === 'label' && <div id="orders-label-filter" className="order-column-filter-popover" role="dialog" aria-label="Etiket filtreleri"><label>Etiket durumu<select aria-label="Etiket yazdırma durumuna göre filtrele" value={filterForm.label} onChange={event => applyFilterValue('label', event.target.value)}><option value="ALL">Tüm etiketler</option><option value="PRINTED">Etiketi yazdırılanlar</option><option value="NOT_PRINTED">Etiketi yazdırılmayanlar</option></select></label>{filterForm.label !== 'ALL' && <button type="button" className="order-column-filter-reset" onClick={() => { applyFilterValue('label', 'ALL'); setColumnFilterOpen(null) }}>Filtreyi temizle</button>}</div>}</div></div>{pageItems.map(item => <OrderReferenceRow item={item} key={item.id} selected={selectedIds.includes(item.id)} processing={processingOrderId === item.id} onSelect={checked => updateSelection(item.id, checked)} openMenu={menu?.orderId === item.id ? menu.kind : null} onMenuChange={kind => setMenu(kind ? { orderId: item.id, kind } : null)} onInvoiceCreate={() => { setMenu(null); setInvoiceDraftOrder(item) }} onInvoiceDetails={() => { setMenu(null); setInvoiceInfoOrder(item) }} onInvoiceUpload={() => { setMenu(null); setInvoiceUploadOrder(item) }} onCourierChange={() => { setMenu(null); setCourierOrder({ item, items: [item] }) }} onProcessOrder={() => void processSingleOrder(item)} onPrintLabel={format => { setMenu(null); setShippingLabel({ item, format }) }} onPreviewImage={setPreviewImage} labelSettings={labelSettings} printedLabels={printedLabels} />)}</div></>}
    {invoiceInfoOrder && <InvoiceInfoModal item={invoiceInfoOrder} onClose={() => setInvoiceInfoOrder(null)} />}{invoiceViewerOrder && <InvoiceViewerModal item={invoiceViewerOrder} onClose={() => setInvoiceViewerOrder(null)} />}{invoiceDraftOrder && <InvoiceDraftModal item={invoiceDraftOrder} provider={provider} onClose={() => setInvoiceDraftOrder(null)} />}{invoiceUploadOrder && <InvoiceUploadModal item={invoiceUploadOrder} provider={provider} onClose={() => setInvoiceUploadOrder(null)} />}{courierOrder && <CourierChangeModal item={courierOrder.item} items={courierOrder.items} onClose={() => setCourierOrder(null)} onConfirmed={updatedShipments => { client.setQueriesData<Page<Order>>({ queryKey: ['orders', 'page'] }, current => current ? { ...current, items: current.items.map(order => updatedShipments.reduce((updated, shipment) => updated.id === shipment.orderId ? patchOrderShipment(updated, shipment) : updated, order)) } : current); showBulkNotice(`${updatedShipments.length} paket kargo firması “${cargoLabel(updatedShipments[0]?.cargoProviderName)}” olarak güncellendi ve panelde doğrulandı.`) }} />}{shippingLabel && <ShippingLabelModal item={shippingLabel.item} format={shippingLabel.format} onClose={() => setShippingLabel(null)} onPrinted={() => setPrintedLabels(new Set(loadPrintedShippingLabels()))} />}{shippingLabelBatch && <ShippingLabelBatchModal items={shippingLabelBatch.items} format={shippingLabelBatch.format} onClose={() => setShippingLabelBatch(null)} onPrinted={() => setPrintedLabels(new Set(loadPrintedShippingLabels()))} />}{singleSyncOpen && <SingleOrderSyncModal activeConnection={trendyolConnection} onClose={() => setSingleSyncOpen(false)} onSuccess={(connectionCount, orderNo) => { showBulkNotice(orderNo ? `${connectionCount} bağlantıdan #${orderNo} sipariş senkronizasyonu başlatıldı.` : `${connectionCount} bağlantıdan yeni sipariş senkronizasyonu başlatıldı.`); void client.invalidateQueries({ queryKey: ['orders'] }); window.setTimeout(() => void client.invalidateQueries({ queryKey: ['orders'] }), 1500); window.setTimeout(() => void client.invalidateQueries({ queryKey: ['orders'] }), 3500) }} />}{previewImage && <div className="workspace-modal-backdrop product-image-backdrop" role="presentation" onMouseDown={() => setPreviewImage(null)}><section className="workspace-modal product-image-modal" role="dialog" aria-modal="true" aria-label={`${previewImage.title} büyük ürün görseli`} onMouseDown={event => event.stopPropagation()}><header><h2>{previewImage.title}</h2><button type="button" className="modal-close" onClick={() => setPreviewImage(null)} aria-label="Pencereyi kapat">×</button></header><div className="product-image-modal-body"><img src={previewImage.url} alt={`${previewImage.title} büyük ürün görseli`} /></div></section></div>}
  </section>
}


export function ShipmentsPage() {
  const [pageSize, setPageSize] = useState(20); const [pageNumber, setPageNumber] = useState(1)
  const query = useQuery({ queryKey: ['shipments'], queryFn: () => loadAllPages<Shipment>('/shipments') })
  const items = query.data?.items ?? []; const totalPages = Math.max(1, Math.ceil(items.length / pageSize)); const currentPage = Math.min(pageNumber, totalPages); const pageItems = items.slice((currentPage - 1) * pageSize, currentPage * pageSize)
  return <ListPage eyebrow="Operasyon" title="Gönderiler" description="Paket durumu geriye alınmaz; desteklenmeyen aksiyon ve etiket biçimleri gösterilmez.">{query.isLoading ? <Busy /> : query.isError ? <ErrorBox error={query.error} /> : !items.length ? <Empty>Order ingestion sonrasında shipment package kayıtları burada görünür.</Empty> : <><div className="data-table" role="table">{pageItems.map(item => <Link role="row" to={`/shipments/${item.id}`} key={item.id}><span><strong>{item.orderNumber}</strong><small>Paket {item.externalPackageId}</small></span><Badge value={item.status} /><span>{item.cargoTrackingNumber ?? '—'}</span><span><DateText value={item.statusOccurredAt} /></span></Link>)}</div><div className="order-pagination"><label>Sayfa başına <select aria-label="Sayfa başına gönderi" value={pageSize} onChange={event => { setPageSize(Number(event.target.value)); setPageNumber(1) }}>{[20, 50, 100, 200].map(value => <option key={value} value={value}>{value}</option>)}</select></label><span>{(currentPage - 1) * pageSize + 1}–{Math.min(currentPage * pageSize, items.length)} / {items.length} gönderi</span><div><button type="button" disabled={currentPage <= 1} onClick={() => setPageNumber(value => Math.max(1, value - 1))}>Önceki</button><b>Sayfa {currentPage} / {totalPages}</b><button type="button" disabled={currentPage >= totalPages} onClick={() => setPageNumber(value => Math.min(totalPages, value + 1))}>Sonraki</button></div></div></>}</ListPage>
}

export function ShipmentDetailPage() {
  const { id = '' } = useParams(); const client = useQueryClient(); const [notice, setNotice] = useState(''); const query = useQuery({ queryKey: ['shipment', id], queryFn: () => hubApi<ShipmentDetail>(`/shipments/${id}`) })
  async function action(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!query.data) return; const data = new FormData(event.currentTarget); try { JSON.parse(String(data.get('payloadJson') || '{}')); await hubApi(`/shipments/${id}/actions`, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${query.data.package.version}"` }, body: JSON.stringify({ action: data.get('action'), payloadJson: data.get('payloadJson') }) }); setNotice('Paket aksiyonu kuyruğa alındı; sonuç read-back ile doğrulanacak.'); await client.invalidateQueries({ queryKey: ['shipment', id] }) } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Aksiyon başlatılamadı.') } }
  async function label(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!query.data) return; const data = new FormData(event.currentTarget); try { await hubApi(`/shipments/${id}/common-label-jobs`, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${query.data.package.version}"` }, body: JSON.stringify({ boxQuantity: Number(data.get('boxQuantity')), volumetricHeight: Number(data.get('volumetricHeight')) }) }); setNotice('Ortak etiket işi kuyruğa alındı.'); await client.invalidateQueries({ queryKey: ['shipment', id] }) } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Etiket işi başlatılamadı.') } }
  async function labelProbe(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!query.data) return; const data = new FormData(event.currentTarget); try { await hubApi(`/shipments/${id}/label-capability-probes`, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${query.data.package.version}"` }, body: JSON.stringify({ capabilityCode: data.get('capabilityCode'), boxQuantity: Number(data.get('boxQuantity')), volumetricHeight: Number(data.get('volumetricHeight')) }) }); setNotice('Stage etiket testi kuyruğa alındı; sonuç İşlem Takibi’nde görünür.'); await client.invalidateQueries({ queryKey: ['shipment', id] }) } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Stage etiket testi başlatılamadı.') } }
  if (query.isLoading) return <section className="content"><Busy /></section>; if (query.isError || !query.data) return <section className="content"><ErrorBox error={query.error} /></section>; const item = query.data
  return <section className="content f3"><Link to="/shipments" className="back">← Gönderiler</Link><div className="page-heading"><div><p className="eyebrow">Gönderi detayı</p><h1>{item.package.orderNumber}</h1><p className="lede">Paket {item.package.externalPackageId}</p></div><Badge value={item.package.status} /></div>{notice && <div role="status" className="notice">{notice}</div>}<div className="split"><article className="panel"><h2>Paket</h2><dl className="details"><dt>Platform durumu</dt><dd>{item.package.rawStatus}</dd><dt>Takip numarası</dt><dd>{item.package.cargoTrackingNumber ?? '—'}</dd><dt>Güncelleme</dt><dd><DateText value={item.package.statusOccurredAt} /></dd></dl><h3>Belgeler</h3>{item.documents.length ? <ul className="plain-list">{item.documents.map(document => <li key={document.id}><strong>{document.documentKind} · {document.format}</strong><span>v{document.documentVersion} · {document.source}</span></li>)}</ul> : <p>Henüz belge yok.</p>}</article><article className="panel"><h2>Trendyol paket aksiyonu</h2>{item.allowedActions.length ? <form className="form-grid" onSubmit={action}><label>Aksiyon<select name="action">{item.allowedActions.map(value => <option key={value}>{value}</option>)}</select></label><label>Resmî aksiyon payload JSON<textarea name="payloadJson" defaultValue="{}" required /></label><button>Aksiyonu kuyruğa al</button></form> : <div className="unknown"><strong>Paket aksiyonu uygun değil</strong><p>Paket durumu veya production dış-yazma ayarları bu işlemi şu anda uygun kılmıyor.</p></div>}<h2>Ortak kargo etiketi</h2>{item.package.cargoTrackingNumber ? <>{item.supportedLabelFormats.length > 0 && <form className="inline-form" onSubmit={label}><label>Koli adedi<input name="boxQuantity" type="number" min="1" max="50" defaultValue="1" required /></label><label>Desi / hacim<input name="volumetricHeight" type="number" min="0" max="10000" step="0.01" defaultValue="1" required /></label><button>Etiket oluştur</button></form>}{item.isStageConnection && <details className="operation-details"><summary>Stage etiket testi</summary><form className="inline-form" onSubmit={labelProbe}><p>Yalnız test bağlantısındaki uygun pakette etiket okuma veya oluşturma sonucunu doğrular.</p><label>Test türü<select name="capabilityCode"><option value="LABEL_READ">Etiketi oku</option><option value="LABEL_WRITE">Etiket oluşturmayı dene</option></select></label><label>Koli adedi<input name="boxQuantity" type="number" min="1" max="50" defaultValue="1" required /></label><label>Desi / hacim<input name="volumetricHeight" type="number" min="0.01" max="10000" step="0.01" required /></label><button>Stage etiket testini çalıştır</button></form></details>}</> : <p>Etiket için takip numarası gerekir.</p>}</article></div></section>
}

function ReturnReferenceRow({ item, order }: { item: ReturnClaim; order: Order | null }) {
  const [previewImage, setPreviewImage] = useState<{ url: string; title: string } | null>(null)
  const [detailOpen, setDetailOpen] = useState(false)
  const canOpenReturnRecord = !['APPROVED', 'COMPLETED'].includes(item.status.toUpperCase())
  const navigate = useNavigate()
  const lines = order?.lines?.length ? order.lines : item.lines ?? []
  const currency = order?.currency ?? item.currency
  const money = (value: number) => value.toLocaleString('tr-TR', { style: 'currency', currency })
  const total = order?.grossAmount || order?.netAmount || item.grossAmount || item.orderAmount
  const invoiceStatus = order?.invoiceStatus ?? item.invoiceStatus
  const discountAmount = order?.discountAmount ?? item.discountAmount
  const invoicedTotal = Math.max(0, total - discountAmount)
  const trackingNumber = order?.packages?.[0]?.cargoTrackingNumber ?? order?.cargoTrackingNumber ?? item.cargoTrackingNumber
  const canTrack = Boolean(order?.packages?.[0]?.id && trackingNumber && (order.packages[0].status === 'SHIPPED' || order.packages[0].status === 'IN_TRANSIT' || order?.derivedStatus === 'SHIPPED'))
  return <><Link to={`/returns/${item.id}`} className="return-reference-row" role="row">
    <span className="order-reference-meta"><strong>#{item.orderNumber}</strong>{item.isMicroExport && <span className="micro-export-chip" role="status">Mikro İhracat</span>}<small>Sipariş tarihi: <DateText value={order?.orderedAt ?? item.orderedAt} /></small><small>Paket No: {order?.packages?.[0]?.externalPackageId ?? item.packageNumber ?? '—'}</small><small>Teslimat No: {trackingNumber ?? '—'}</small></span>
    <span className="order-reference-buyer"><strong>{order?.customerName ?? item.customerName}</strong></span>
     <span className="order-reference-products return-order-products">{lines.length ? lines.map(line => { const imageUrl = line.imageUrl ?? item.primaryImageUrl; const fallbackUrl = productImageFallbackUrl(line.barcode ?? item.primaryBarcode); const quantity = productLineQuantity(line); return <article key={line.id}><span className="reference-product-media"><ProductImage url={imageUrl} fallbackUrl={fallbackUrl} alt={`${line.title} ürün görseli`} onClick={() => setPreviewImage({ url: imageUrl ?? fallbackUrl!, title: line.title })} /><span className="quantity-bubble" role="img" aria-label={`${quantity} adet`} title={`${quantity} adet`}>{quantity}</span></span><div><strong>{line.title}</strong><small>Stok Kodu: <code className="technical-text sku-value">{line.sku}</code></small>{optionRows(line.optionSignature).map(option => <small key={`${option.label}:${option.value}`}>{option.label}: {option.value}</small>)}<small>Barkod: <code className="technical-text barcode-value">{line.barcode ?? '—'}</code></small><small>Model Kodu: <code className="technical-text model-code-value">{line.modelCode ?? '—'}</code></small></div></article> }) : <div className="reference-no-product"><strong>{item.productCount} ürün</strong><small>Barkod: <code className="technical-text barcode-value">{item.primaryBarcode ?? '—'}</code></small></div>}</span>
    <span className="order-reference-prices">{lines.length ? lines.map(line => <strong key={line.id}>{money(line.unitPrice)}</strong>) : <strong>{money(item.orderAmount)}</strong>}</span>
    <span className="order-reference-cargo"><span className="cargo-provider-display"><CargoProviderIcon value={order?.packages?.[0]?.cargoProviderName ?? order?.cargoProviderName ?? item.cargoProviderName} /><strong>{cargoLabel(order?.packages?.[0]?.cargoProviderName ?? order?.cargoProviderName ?? item.cargoProviderName ?? 'Kargo bilgisi yok')}</strong></span><b>{trackingNumber ?? '—'}</b>{canTrack && <button type="button" className="return-cargo-track" onClick={event => { event.preventDefault(); event.stopPropagation(); navigate(`/shipments/${order!.packages![0].id}`) }}>Kargo takip →</button>}</span>
    <span className={`order-reference-invoice ${invoiceStatus === 'FATURA_BEKLIYOR' ? 'invoice-pending' : 'invoice-created'}`}><small>Satış tutarı</small><strong>{money(total)}</strong>{discountAmount > 0 && <small>Satıcı indirimi: {money(discountAmount)}</small>}<small>Faturalandırılmış tutar</small><strong>{money(invoicedTotal)}</strong><span>{invoiceStatus === 'FATURA_BEKLIYOR' ? 'Fatura bekleniyor' : invoiceStatus === 'FATURA_KONTROLDE' ? 'Fatura kontrol ediliyor' : invoiceStatus === 'FATURA_REDDEDILDI' ? 'Fatura reddedildi' : 'Fatura kesildi'}</span></span>
    <span><b>{item.reasonText ?? 'Belirtilmedi'}</b></span>
    <span className="return-reference-status"><Badge value={item.status} />{item.actionDueAt && <small>{remainingText(item.actionDueAt)}</small>}<button type="button" className="return-detail-trigger" onClick={event => { event.preventDefault(); event.stopPropagation(); setDetailOpen(true) }}><span aria-hidden="true">↗</span> Detaylı bilgi</button></span>
  </Link>{previewImage && <div className="workspace-modal-backdrop product-image-backdrop" role="presentation" onMouseDown={() => setPreviewImage(null)}><section className="workspace-modal product-image-modal" role="dialog" aria-modal="true" aria-label={`${previewImage.title} büyük ürün görseli`} onMouseDown={event => event.stopPropagation()}><header><h2>{previewImage.title}</h2><button type="button" className="modal-close" onClick={() => setPreviewImage(null)} aria-label="Pencereyi kapat">×</button></header><img src={previewImage.url} alt={`${previewImage.title} büyük ürün görseli`} /></section></div>}{detailOpen && <div className="workspace-modal-backdrop return-detail-backdrop" role="presentation" onMouseDown={() => setDetailOpen(false)}><section className="workspace-modal return-detail-modal" role="dialog" aria-modal="true" aria-label={`${item.orderNumber} iade detayı`} onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">İADE DETAYI</p><h2>Sipariş #{item.orderNumber}</h2><p>{order?.customerName ?? item.customerName}</p></div><button type="button" className="modal-close" onClick={() => setDetailOpen(false)} aria-label="Pencereyi kapat">×</button></header><div className="return-detail-summary"><div><small>Durum</small><Badge value={item.status} /></div><div><small>İade sebebi</small><strong>{item.reasonText ?? 'Belirtilmedi'}</strong></div><div><small>Kargo</small><strong>{trackingNumber ?? 'Takip numarası yok'}</strong></div><div><small>Faturalandırılmış tutar</small><strong>{money(invoicedTotal)}</strong></div></div><h3>Ürünler</h3><div className="return-detail-products">{lines.map(line => { const color = returnLineField(line, ['renk', 'color'], 'color'); const size = returnLineField(line, ['beden', 'size'], 'size'); const approvedAt = returnLineField(line, ['onaylanma', 'approved'], 'approvedAt'); const systemNote = returnLineField(line, ['sistem notu', 'system'], 'systemNote'); const reasonText = returnLineField(line, ['sebep', 'reason'], 'reasonText'); const sellerDescription = returnLineField(line, ['satıcı açıklaması', 'seller'], 'sellerDescription'); const platformReason = returnLineField(line, ['trendyol onay nedeni', 'approval reason'], 'platformApprovalReason'); const platformDescription = returnLineField(line, ['trendyol açıklaması', 'platform description'], 'platformDescription'); return <article className="return-detail-product" key={line.id}><div className="return-detail-product-head">{line.imageUrl ? <img src={line.imageUrl} alt={`${line.title} ürün görseli`} /> : <span className="reference-product-placeholder">↩</span>}<div><strong>{line.title}</strong><small>{line.quantity ?? line.orderedQuantity} adet</small></div></div><dl className="return-detail-product-facts"><div><dt>Stok Kodu</dt><dd>{line.sku || '—'}</dd></div><div><dt>Renk</dt><dd>{color}</dd></div><div><dt>Barkod</dt><dd>{line.barcode ?? '—'}</dd></div><div><dt>Beden</dt><dd>{size}</dd></div><div><dt>Onaylanma Tarihi</dt><dd>{approvedAt !== '—' ? <DateText value={approvedAt} /> : item.approvedAt ? <DateText value={item.approvedAt} /> : '—'}</dd></div><div><dt>Sistem Notu</dt><dd>{systemNote !== '—' ? systemNote : item.systemNote ?? '—'}</dd></div><div><dt>Sebep</dt><dd>{reasonText !== '—' ? reasonText : item.reasonText ?? '—'}</dd></div><div><dt>Satıcı Açıklaması</dt><dd>{sellerDescription !== '—' ? sellerDescription : item.sellerDescription ?? '—'}</dd></div><div><dt>Trendyol Onay Nedeni</dt><dd>{platformReason !== '—' ? platformReason : item.platformApprovalReason ?? '—'}</dd></div><div><dt>Trendyol Açıklaması</dt><dd>{platformDescription !== '—' ? platformDescription : item.platformDescription ?? '—'}</dd></div></dl></article> })}</div><footer><button type="button" className="secondary" onClick={() => setDetailOpen(false)}>Kapat</button>{canOpenReturnRecord && <Link className="button-link" to={`/returns/${item.id}`} onClick={() => setDetailOpen(false)}>İade kaydını aç</Link>}</footer></section></div>}</>
}

export function ReturnsPage() {
  const [status, setStatus] = useState('ALL'); const [customer, setCustomer] = useState(''); const [orderNumber, setOrderNumber] = useState(''); const [claimCode, setClaimCode] = useState(''); const [barcode, setBarcode] = useState(''); const [reason, setReason] = useState(''); const [from, setFrom] = useState(''); const [to, setTo] = useState(''); const [draft, setDraft] = useState(true); const [pageSize, setPageSize] = useState(20); const [pageNumber, setPageNumber] = useState(1)
  const client = useQueryClient(); const [notice, setNotice] = useState('')
  const query = useQuery({ queryKey: ['returns'], queryFn: loadAllReturns })
  const connections = useQuery({ queryKey: ['connections', 'returns'], queryFn: () => loadAllPages<Connection>('/connections') })
  const trendyolConnection = connections.data?.items.find(item => item.platformCode === 'TRENDYOL' && (item.status === 'ACTIVE' || item.status === 'VERIFIED'))
  const sync = useMutation({ mutationFn: () => { if (!trendyolConnection) throw new Error('Aktif Trendyol bağlantısı bulunamadı.'); return hubApi(`/connections/${trendyolConnection.id}/return-sync-jobs`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() } }) }, onSuccess: async () => { setNotice('İade eşitlemesi kuyruğa alındı. İş tamamlandığında kayıtlar otomatik yenilenir.'); await client.invalidateQueries({ queryKey: ['returns'] }) }, onError: value => setNotice(value instanceof Error ? value.message : 'İade eşitlemesi başlatılamadı.') })
  const items = query.data?.items ?? []
  const tabs = [['ALL', 'Tüm İadeler'], ['REQUESTED', 'Talep Oluşturulan'], ['SHIPPING', 'Kargoya Verilen'], ['ACTION_REQUIRED', 'Aksiyon Bekleyen'], ['APPROVED', 'Onaylanan'], ['REJECTED', 'Reddedilen'], ['REVIEW', 'Analiz'], ['DISPUTED', 'İhtilaflı'], ['SUSPENDED', 'Askıda İadeler']] as const
  const norm = (value: string | null) => (value ?? '').toLocaleLowerCase('tr-TR')
  const reasons = Array.from(new Set(items.map(item => item.reasonText).filter((value): value is string => Boolean(value))))
  const visible = items.filter(item => {
    const date = item.orderedAt?.slice(0, 10) ?? ''
    return (status === 'ALL' || returnGroup(item.status) === status) && (!draft || (!customer || norm(item.customerName).includes(norm(customer))) && (!orderNumber || item.orderNumber.includes(orderNumber)) && (!claimCode || norm(item.externalClaimId).includes(norm(claimCode))) && (!barcode || [item.primaryBarcode ?? '', ...(item.lines ?? []).map(line => line.barcode ?? '')].some(value => norm(value).includes(norm(barcode)))) && (!reason || item.reasonText === reason) && (!from || date >= from) && (!to || date <= to))
  })
  useEffect(() => { setPageNumber(1) }, [status, customer, orderNumber, claimCode, barcode, reason, from, to, draft, pageSize])
  const totalPages = Math.max(1, Math.ceil(visible.length / pageSize)); const currentPage = Math.min(pageNumber, totalPages); const visibleItems = visible.slice((currentPage - 1) * pageSize, currentPage * pageSize)
  function clearFilters() { setCustomer(''); setOrderNumber(''); setClaimCode(''); setBarcode(''); setReason(''); setFrom(''); setTo(''); setDraft(true); setPageNumber(1) }
  return <section className="content f3 returns-page reference-returns-page">
    <div className="page-heading returns-reference-heading">
      <div><h1>İade Yönetimi</h1><p className="lede">Pazaryerlerinden gelen iade taleplerini tek merkezden izleyin ve yönetin.</p></div>
      <div className="page-heading-actions returns-reference-heading-actions"><button type="button" className="returns-sync-action" disabled={!trendyolConnection || sync.isPending} onClick={() => sync.mutate()}><span aria-hidden="true">↻</span>{sync.isPending ? 'Eşitleniyor…' : 'İade Senkronizasyonu'}</button></div>
    </div>
    <div className="returns-reference-filter-shell">
      <div className="return-reference-tabs" role="tablist" aria-label="İade durumları">{tabs.map(([value, label]) => <button key={value} type="button" role="tab" aria-selected={status === value} className={status === value ? 'active' : ''} onClick={() => setStatus(value)}><span>{label}</span><b>{value === 'ALL' ? items.length : items.filter(item => returnGroup(item.status) === value).length}</b></button>)}</div>
      <section className="return-reference-filters" aria-label="İade filtreleri"><input aria-label="Müşteri adı" placeholder="Müşteri adı" value={customer} onChange={event => setCustomer(event.target.value)} /><input aria-label="Sipariş no" placeholder="Sipariş no" value={orderNumber} onChange={event => setOrderNumber(event.target.value)} /><input aria-label="İade kodu" placeholder="İade kodu" value={claimCode} onChange={event => setClaimCode(event.target.value)} /><input aria-label="Barkod" placeholder="Barkod" value={barcode} onChange={event => setBarcode(event.target.value)} /><select aria-label="İade sebebi" value={reason} onChange={event => setReason(event.target.value)}><option value="">İade sebebi</option>{reasons.map(value => <option key={value}>{value}</option>)}</select><input aria-label="İade talep başlangıç tarihi" type="date" value={from} onChange={event => setFrom(event.target.value)} /><input aria-label="İade talep bitiş tarihi" type="date" value={to} onChange={event => setTo(event.target.value)} /><div className="return-filter-actions"><button type="button" className="secondary" onClick={clearFilters}>Temizle</button><button type="button" onClick={() => setDraft(true)}>Filtrele</button></div></section>
    </div>
    {notice && <div role="status" className="notice">{notice}</div>}
    <section className="return-reference-workspace"><header><div><h2>{tabs.find(tab => tab[0] === status)?.[1]}</h2><p>Filtreleme sonuçları: Toplam {visible.length} iade bilgisi · Sayfa {currentPage}/{totalPages}</p></div><div className="return-reference-header-actions"><label>Sayfa başına<select aria-label="Sayfa başına iade" value={pageSize} onChange={event => setPageSize(Number(event.target.value))}>{[20, 50, 100, 200].map(value => <option key={value} value={value}>{value}</option>)}</select></label></div></header>{query.isLoading ? <Busy /> : query.isError ? <ErrorBox error={query.error} /> : !visible.length ? <Empty>{items.length ? 'Seçili filtreyle eşleşen iade yok.' : 'İadeleri eşitle düğmesiyle güncel talepleri çekin.'}</Empty> : <><div className="return-reference-table" role="table"><div className="return-reference-head" role="row"><strong>Sipariş Bilgileri</strong><strong>Alıcı</strong><strong>Bilgiler</strong><strong>Birim Fiyat</strong><strong>Kargo</strong><strong>Fatura</strong><strong>İade Sebebi</strong><strong>Durum</strong></div>{visibleItems.map(item => <ReturnReferenceRow key={item.id} item={item} order={null} />)}</div><nav className="return-pagination order-pagination" aria-label="İade sayfaları"><span>{(currentPage - 1) * pageSize + 1}–{Math.min(currentPage * pageSize, visible.length)} / {visible.length}</span><div><button type="button" disabled={currentPage <= 1} onClick={() => setPageNumber(value => Math.max(1, value - 1))}>Önceki</button><strong>Sayfa {currentPage} / {totalPages}</strong><button type="button" disabled={currentPage >= totalPages} onClick={() => setPageNumber(value => Math.min(totalPages, value + 1))}>Sonraki</button></div></nav></>}</section>
  </section>
}

export function ReturnDetailPage() {
  const { id = '' } = useParams(); const client = useQueryClient(); const [notice, setNotice] = useState(''); const [evidenceAssetIds, setEvidenceAssetIds] = useState<string[]>([]); const [action, setAction] = useState('APPROVE'); const [reasonId, setReasonId] = useState('')
  const query = useQuery({ queryKey: ['return', id], queryFn: () => hubApi<ReturnDetail>(`/returns/${id}`) })
  const reasons = useQuery({ queryKey: ['return-reasons', id], queryFn: () => hubApi<ReturnIssueReason[]>(`/returns/${id}/rejection-reasons`), enabled: query.data?.allowedActions.includes('REJECT') === true, retry: false })
  const selectedReason = reasons.data?.find(x => x.id === reasonId)
  async function uploadFile(file: File) { if (!file.size) return; const body = new FormData(); body.set('file', file); try { const asset = await hubApi<{ id: string }>('/files/return-evidence', { method: 'POST', body }); setEvidenceAssetIds(ids => ids.includes(asset.id) ? ids : [...ids, asset.id]); setNotice('Kanıt güvenli özel depoya yüklendi.') } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Kanıt yüklenemedi.') } }
  async function decide(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!query.data) return; const data = new FormData(event.currentTarget); try { await hubApi(`/returns/${id}/actions`, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${query.data.version}"` }, body: JSON.stringify({ action, reasonCode: action === 'REJECT' ? reasonId : null, explanation: action === 'REJECT' ? data.get('explanation') : null, evidenceAssetIds }) }); setNotice('İade kararı kuyruğa alındı; uzak sonuç eşitlemeyle doğrulanacak.'); await client.invalidateQueries({ queryKey: ['return', id] }) } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'İade kararı başlatılamadı.') } }
  async function disposition(event: FormEvent<HTMLFormElement>, line: ReturnLine) { event.preventDefault(); const data = new FormData(event.currentTarget); try { await hubApi(`/returns/${id}/stock-dispositions`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify({ returnLineId: line.id, disposition: data.get('disposition'), quantity: Number(data.get('quantity')), reason: data.get('reason') }) }); setNotice('İade stok kararı kaydedildi. Yalnız Satılabilir seçimi eldeki stoğu artırır.'); await client.invalidateQueries({ queryKey: ['return', id] }) } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Stok kararı kaydedilemedi.') } }
  if (query.isLoading) return <section className="content"><Busy /></section>; if (query.isError || !query.data) return <section className="content"><ErrorBox error={query.error} /></section>; const item = query.data; const lines = item.lines ?? []
  return <section className="content f3 return-detail-page"><Link to="/returns" className="back">← İadeler</Link><div className="page-heading"><div><p className="eyebrow">İade detayı · Claim {item.externalClaimId}</p><h1>{item.orderNumber}</h1><p className="lede">{item.customerName} · {item.orderAmount.toLocaleString('tr-TR', { style: 'currency', currency: item.currency })}</p></div><Badge value={item.status} /></div>{notice && <div role="status" className="notice">{notice}</div>}
    <div className="detail-grid"><article className="panel"><h2>Talep ve süre</h2><dl className="details"><dt>Platform durumu</dt><dd>{item.rawStatus}</dd><dt>Neden</dt><dd>{item.reasonText ?? 'Belirtilmedi'}</dd><dt>Sipariş tarihi</dt><dd><DateText value={item.orderedAt} /></dd><dt>Otomatik işlem</dt><dd className={item.actionDueAt && new Date(item.actionDueAt).getTime() - Date.now() < 86_400_000 ? 'deadline critical' : ''}>{remainingText(item.actionDueAt)}</dd><dt>Kargo</dt><dd>{item.cargoProviderName ?? '—'} · {item.cargoTrackingNumber ?? '—'}</dd></dl></article><article className="panel"><h2>Onay / ret</h2>{item.allowedActions.length ? <form className="form-grid" onSubmit={decide}><label>Karar<select name="action" value={action} onChange={event => setAction(event.target.value)}>{item.allowedActions.filter(value => value === 'APPROVE' || value === 'REJECT').map(value => <option key={value}>{value}</option>)}</select></label>{action === 'REJECT' && <><label>Trendyol ret nedeni<select value={reasonId} onChange={event => setReasonId(event.target.value)} required><option value="">Seçin</option>{reasons.data?.map(reason => <option key={reason.id} value={reason.id}>{reason.name}</option>)}</select></label><label>Açıklama<textarea name="explanation" maxLength={500} required /></label><label>Kanıt (PDF/JPEG/PNG, en fazla 10 MiB)<input name="file" type="file" accept="application/pdf,image/jpeg,image/png" onChange={event => { const file = event.currentTarget.files?.[0]; if (file) void uploadFile(file) }} /></label><small>{evidenceAssetIds.length} kanıt hazır{selectedReason?.evidenceRequired ? ' · bu neden için zorunlu' : ''}</small></>}<button disabled={action === 'REJECT' && (!reasonId || (selectedReason?.evidenceRequired && !evidenceAssetIds.length))}>Kararı kuyruğa al</button></form> : <div className="unknown"><strong>İade aksiyonu uygun değil</strong><p>Sağlayıcının mevcut iade durumu bu kayıtta henüz onay veya ret işlemini desteklemiyor.</p></div>}</article></div>
    <div className="panel"><h2>İade paketi ürünleri ve stok kabulü</h2><p className="notice"><strong>Stok otomatik artmaz.</strong> Ürün fiziksel olarak kontrol edildikten sonra “Satılabilir” seçilirse stok artar; Karantina, Hasarlı ve Teslim Alınmadı seçimleri stoğu artırmaz.</p>{!lines.length ? <Empty>İade satırı bulunamadı.</Empty> : <div className="return-line-list">{lines.map(line => <article className="return-line" key={line.id}><ProductImage url={line.imageUrl ?? item.primaryImageUrl} fallbackUrl={productImageFallbackUrl(line.barcode) ?? productImageFallbackUrl(item.primaryBarcode)} alt={`${line.title} ürün görseli`} className="operation-image" /><span><strong>{line.title}</strong><small>{line.sku}{line.barcode ? ` · ${line.barcode}` : ''}</small><small>{line.quantity} adet · kalan karar {line.remainingQuantity}</small></span>{item.stockDispositionAvailable && line.remainingQuantity > 0 ? <form className="stock-disposition" onSubmit={event => disposition(event, line)}><select name="disposition" aria-label="Stok kabul kararı"><option value="PASS">Satılabilir — stoğa ekle</option><option value="QUARANTINE">Karantina — stoğa ekleme</option><option value="DAMAGED">Hasarlı — stoğa ekleme</option><option value="NOT_RECEIVED">Teslim alınmadı</option></select><input name="quantity" type="number" min="0.01" max={line.remainingQuantity} step="0.01" defaultValue={line.remainingQuantity} required /><input name="reason" placeholder="Kontrol notu" required /><button>Kararı kaydet</button></form> : <Badge value={line.remainingQuantity === 0 ? 'TAMAMLANDI' : line.hasInventoryMapping ? 'ONAY_BEKLIYOR' : 'STOK_ESLEME_GEREKLI'} />}</article>)}</div>}</div>
  </section>
}

export function MappingPage() {
  const [searchParams] = useSearchParams()
  useEffect(() => {
    const main = document.querySelector<HTMLElement>('.app-shell.stitch-shell > main')
    if (main) main.scrollTop = 0
  }, [])
  return searchParams.get('view') === 'brands' ? <BrandMappingPage /> : <CategoryMappingWorkspace />
}

function CategoryMappingWorkspace() {
  const client = useQueryClient()
  const [connectionId, setConnectionId] = useState(''); const [selectedPlatformCode, setSelectedPlatformCode] = useState<MappingPlatformCode>('TRENDYOL'); const [localId, setLocalId] = useState(''); const [externalId, setExternalId] = useState(''); const [notice, setNotice] = useState(''); const [categoryName, setCategoryName] = useState(''); const [categoryLibrarySearch, setCategoryLibrarySearch] = useState(''); const [categoryLibraryOpen, setCategoryLibraryOpen] = useState(false); const [categoryLibrarySort, setCategoryLibrarySort] = useState<'NAME_ASC' | 'NAME_DESC'>('NAME_ASC'); const [externalSearch, setExternalSearch] = useState(''); const [externalPickerOpen, setExternalPickerOpen] = useState(false); const [savedSearch, setSavedSearch] = useState(''); const [savedSort, setSavedSort] = useState<'NAME_ASC' | 'NAME_DESC'>('NAME_ASC'); const [advancedOpen, setAdvancedOpen] = useState(false); const [panelPickerOpen, setPanelPickerOpen] = useState(false); const [panelPickerSearch, setPanelPickerSearch] = useState(''); const [exportOpen, setExportOpen] = useState(false); const [exportSelection, setExportSelection] = useState<Record<MappingTransferScope, boolean>>({ categories: true, options: true, attributes: true, mappings: true }); const [transferOpen, setTransferOpen] = useState(false); const [transferBundle, setTransferBundle] = useState<MappingTransferBundle | null>(null); const [transferSelection, setTransferSelection] = useState<Record<MappingTransferScope, boolean>>({ categories: true, options: true, attributes: true, mappings: true }); const [transferBusy, setTransferBusy] = useState(false)
  const connections = useQuery({ queryKey: ['connections', 'mapping'], queryFn: () => loadAllPages<Connection>('/connections') })
  const localCategories = useQuery({ queryKey: ['categories', 'mapping'], queryFn: () => loadAllPages<LocalCategory>('/catalog/categories') })
  const localAttributes = useQuery({ queryKey: ['attributes', 'mapping-builder'], queryFn: () => loadAllPages<LocalAttribute>('/catalog/attributes') })
  const references = useQuery({ queryKey: ['reference-categories', connectionId], queryFn: () => hubApi<ReferenceData>(`/reference-data/categories?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId, retry: false })
  const mapping = useQuery({ queryKey: ['category-mapping', localId, connectionId], queryFn: () => hubApi<CatalogMapping | null>(`/mappings/categories/${localId}?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!localId && !!connectionId, retry: false })
  const categoryMappings = useQuery({ queryKey: ['category-mappings', connectionId], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/categories?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId, retry: false })
  const save = useMutation({ mutationFn: () => { if (!references.data || !localId || !externalId) throw new Error(`Panel kategorisi ve ${mappingPlatformDefinitions.find(item => item.code === selectedPlatformCode)?.label ?? 'platform'} kategorisi zorunludur.`); return hubApi<CatalogMapping>(`/mappings/categories/${localId}`, { method: 'PUT', headers: mapping.data ? { 'If-Match': `"v${mapping.data.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId: references.data.snapshotId, externalId, status: 'VERIFIED' }) }) }, onSuccess: async value => { setNotice('Kategori eşleştirmesi kaydedildi.'); setExternalId(value.externalId); client.setQueryData(['category-mapping', localId, connectionId], value); client.setQueryData(['category-mapping', localId, connectionId, 'embedded'], value); client.setQueryData<CatalogMapping[]>(['category-mappings', connectionId], current => current ? [...current.filter(item => item.localId !== value.localId), value] : [value]); await Promise.all([client.invalidateQueries({ queryKey: ['category-mapping', localId, connectionId] }), client.invalidateQueries({ queryKey: ['category-mapping', localId, connectionId, 'embedded'] }), client.invalidateQueries({ queryKey: ['category-mappings', connectionId] })]) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Eşleme kaydedilemedi.') })
  const createCategory = useMutation({ mutationFn: () => { if (!categoryName.trim()) throw new Error('Kategori adı zorunludur.'); return hubApi<LocalCategory>('/catalog/categories', { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify({ name: categoryName.trim(), parentId: null }) }) }, onSuccess: async category => { client.setQueryData<Page<LocalCategory>>(['categories', 'mapping'], current => current ? { ...current, items: [...current.items.filter(item => item.id !== category.id), category] } : current); setLocalId(category.id); setExternalId(''); setCategoryName(''); setNotice(`“${category.name}” panel kategorisi oluşturuldu ve seçildi.`); await client.invalidateQueries({ queryKey: ['categories', 'mapping'] }) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Kategori oluşturulamadı.') })
  useEffect(() => { setExternalId(mapping.data?.externalId ?? '') }, [mapping.data])
  useEffect(() => {
    if (!categoryLibraryOpen) return
    const closeOnSearchBarClick = (event: globalThis.MouseEvent) => {
      const target = event.target
      if (target instanceof Element && target.closest('.category-library-search-control')) setCategoryLibraryOpen(false)
    }
    document.addEventListener('click', closeOnSearchBarClick)
    return () => document.removeEventListener('click', closeOnSearchBarClick)
  }, [categoryLibraryOpen])
  useEffect(() => {
    if (!panelPickerOpen) return
    const positionPicker = () => {
      const trigger = document.querySelector<HTMLElement>('.mapping-category-builder .mapping-reference-local .panel-category-picker-trigger')
      const picker = document.querySelector<HTMLElement>('.mapping-category-builder .mapping-reference-local > .panel-category-picker')
      if (!trigger || !picker) return
      const rect = trigger.getBoundingClientRect()
      picker.style.setProperty('position', 'fixed', 'important')
      picker.style.setProperty('top', `${Math.round(rect.bottom + 6)}px`, 'important')
      picker.style.setProperty('left', `${Math.round(rect.left)}px`, 'important')
      picker.style.setProperty('width', `${Math.round(rect.width)}px`, 'important')
    }
    positionPicker()
    window.addEventListener('resize', positionPicker)
    window.addEventListener('scroll', positionPicker, true)
    return () => { window.removeEventListener('resize', positionPicker); window.removeEventListener('scroll', positionPicker, true) }
  }, [panelPickerOpen])
  const activeMappingConnections = connections.data?.items.filter(item => ['ACTIVE', 'VERIFIED'].includes(item.status.toUpperCase()) && mappingPlatformDefinitions.some(platform => platform.code === item.platformCode.toUpperCase())) ?? []
  const selectedPlatform = mappingPlatformDefinitions.find(platform => platform.code === selectedPlatformCode) ?? mappingPlatformDefinitions[0]
  const selectedPlatformConnections = activeMappingConnections.filter(item => item.platformCode.toUpperCase() === selectedPlatform.code)
  const trendyolConnections = selectedPlatformConnections
  const firstSelectedConnectionId = selectedPlatformConnections[0]?.id ?? ''
  useEffect(() => {
    setConnectionId(current => current === firstSelectedConnectionId ? current : firstSelectedConnectionId)
    setLocalId('')
    setExternalId('')
    setExternalSearch('')
    setPanelPickerOpen(false)
  }, [firstSelectedConnectionId, selectedPlatformCode])
  const activeCategories = localCategories.data?.items.filter(item => item.isActive) ?? []
  const filteredCategoryLibrary = [...activeCategories.filter(item => !categoryLibrarySearch.trim() || item.name.toLocaleLowerCase('tr-TR').includes(categoryLibrarySearch.trim().toLocaleLowerCase('tr-TR')))].sort((left, right) => categoryLibrarySort === 'NAME_DESC' ? right.name.localeCompare(left.name, 'tr') : left.name.localeCompare(right.name, 'tr'))
  const localLeaves = activeCategories.filter(item => item.isLeaf)
  const panelPickerItems = localLeaves.filter(item => !panelPickerSearch.trim() || item.path.toLocaleLowerCase('tr-TR').includes(panelPickerSearch.trim().toLocaleLowerCase('tr-TR'))).slice(0, 80)
  const externalLeaves = references.data?.items.filter(item => item.isActive && item.isLeaf) ?? []
  const archiveCategory = useMutation({ mutationFn: (category: LocalCategory) => hubApi<LocalCategory>(`/catalog/categories/${category.id}`, { method: 'PATCH', headers: { 'If-Match': `"v${category.version}"` }, body: JSON.stringify({ name: category.name, parentId: null, isActive: false }) }), onSuccess: async (_, category) => { if (localId === category.id) { setLocalId(''); setExternalId('') }; await client.invalidateQueries({ queryKey: ['categories', 'mapping'] }); setNotice(`“${category.name}” panel kategorilerinden kaldırıldı.`) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Kategori kaldırılamadı.') })
  const removeMapping = useMutation({ mutationFn: (item: CatalogMapping) => hubApi<boolean>(`/mappings/categories/${item.localId}?connectionId=${encodeURIComponent(connectionId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${item.version}"` } }), onSuccess: async (_, item) => { if (localId === item.localId) { setExternalId(''); client.setQueryData(['category-mapping', localId, connectionId], null) }; setNotice('Kategori eşleştirmesi kaldırıldı.'); await client.invalidateQueries({ queryKey: ['category-mappings', connectionId] }) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Eşleştirme kaldırılamadı.') })
  const selectedExternal = externalLeaves.find(item => item.externalId === externalId)
  const normalizedExternalSearch = externalSearch.trim().toLocaleLowerCase('tr-TR')
  const matchingExternal = externalLeaves.filter(item => !normalizedExternalSearch || `${item.path} ${item.externalId}`.toLocaleLowerCase('tr-TR').includes(normalizedExternalSearch))
  const visibleExternal = [...(selectedExternal ? [selectedExternal] : []), ...matchingExternal.filter(item => item.externalId !== selectedExternal?.externalId)].slice(0, 20)
  const savedMappings = categoryMappings.data ?? []
  const savedRows = savedMappings.map(item => ({ item, local: localLeaves.find(category => category.id === item.localId), external: externalLeaves.find(category => category.externalId === item.externalId) })).filter(row => !savedSearch.trim() || `${row.local?.path ?? row.item.localId} ${row.external?.path ?? row.item.externalId}`.toLocaleLowerCase('tr-TR').includes(savedSearch.trim().toLocaleLowerCase('tr-TR')))
  const sortedSavedRows = [...savedRows].sort((left, right) => {
    const leftName = (left.local?.path ?? left.item.localId).toLocaleLowerCase('tr-TR')
    const rightName = (right.local?.path ?? right.item.localId).toLocaleLowerCase('tr-TR')
    return savedSort === 'NAME_DESC' ? rightName.localeCompare(leftName, 'tr') : leftName.localeCompare(rightName, 'tr')
  })
  async function exportSelectedMappings() {
    if (!connectionId) { setNotice(`Aktarım için önce ${selectedPlatform.label} bağlantısı seçin.`); return }
    if (!Object.values(exportSelection).some(Boolean)) { setNotice('En az bir dışa aktarma alanı seçin.'); return }
    setTransferBusy(true)
    try {
      const attributeMappings: CatalogMapping[] = []
      const attributeValueMappings: CatalogMapping[] = []
      if (exportSelection.mappings) {
        for (const categoryMapping of savedMappings) {
          const scoped = await hubApi<CatalogMapping[]>(`/mappings/attributes?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryMapping.externalId)}`)
          attributeMappings.push(...scoped)
          for (const attributeMapping of scoped) {
            const values = await hubApi<CatalogMapping[]>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(`${categoryMapping.externalId}/${attributeMapping.externalId}`)}`)
            attributeValueMappings.push(...values)
          }
        }
      }
      const allAttributes = localAttributes.data?.items ?? []
      const selectedAttributes = allAttributes.filter(attribute => exportSelection.options && isOptionAttribute(attribute) || exportSelection.attributes && !isOptionAttribute(attribute))
      const bundle: MappingTransferBundle = { format: 'RAVENCIA_MAPPING_BUNDLE', version: 1, exportedAt: new Date().toISOString(), categories: exportSelection.categories ? activeCategories : [], attributes: selectedAttributes, categoryMappings: exportSelection.mappings ? savedMappings : [], attributeMappings, attributeValueMappings }
      const url = URL.createObjectURL(new Blob([JSON.stringify(bundle, null, 2)], { type: 'application/json;charset=utf-8' }))
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = 'ravencia-eslestirme-yedegi.json'
      anchor.rel = 'noopener'
      document.body.appendChild(anchor)
      anchor.click()
      window.setTimeout(() => { anchor.remove(); URL.revokeObjectURL(url) }, 1000)
      setExportOpen(false)
      setNotice(`${bundle.categories.length} kategori, ${bundle.attributes.length} özellik ve ${bundle.categoryMappings.length + bundle.attributeMappings.length + bundle.attributeValueMappings.length} eşleşme dışa aktarıldı.`)
    } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Eşleştirme aktarımı oluşturulamadı.') } finally { setTransferBusy(false) }
  }
  async function importMappings() {
    if (!transferBundle || !connectionId) return
    if (!Object.values(transferSelection).some(Boolean)) { setNotice('En az bir aktarım alanı seçin.'); return }
    setTransferBusy(true)
    try {
      const categoryIdMap = new Map<string, string>()
      const attributeIdMap = new Map<string, string>()
      const valueIdMap = new Map<string, string>()
      const targetCategories = [...activeCategories]
      const targetAttributes = [...(localAttributes.data?.items ?? [])]
      if (transferSelection.categories) {
        for (const source of [...transferBundle.categories].sort((left, right) => left.depth - right.depth)) {
          const parentPath = source.path.split(' / ').slice(0, -1).join(' / ')
          const existing = targetCategories.find(item => item.id === source.id || item.path === source.path || (item.depth === source.depth && item.name === source.name && !parentPath))
          const target = existing ?? await hubApi<LocalCategory>('/catalog/categories', { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify({ name: source.name, parentId: parentPath ? targetCategories.find(item => item.path === parentPath)?.id ?? null : null }) })
          categoryIdMap.set(source.id, target.id); if (!targetCategories.some(item => item.id === target.id)) targetCategories.push(target)
        }
      }
      const importAttributes = transferBundle.attributes.filter(attribute => transferSelection.options && isOptionAttribute(attribute) || transferSelection.attributes && !isOptionAttribute(attribute))
      for (const source of importAttributes) {
        const existing = targetAttributes.find(item => item.id === source.id || item.code.toLocaleLowerCase('tr-TR') === source.code.toLocaleLowerCase('tr-TR') || item.name.toLocaleLowerCase('tr-TR') === source.name.toLocaleLowerCase('tr-TR'))
        const target = existing ?? await hubApi<LocalAttribute>('/catalog/attributes', { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify({ code: source.code, name: source.name, dataType: source.dataType, selectionMode: source.values.length ? 'SINGLE' : null, unit: null, values: source.values.map((value, index) => ({ value: value.value, sortOrder: index })) }) })
        attributeIdMap.set(source.id, target.id); if (!targetAttributes.some(item => item.id === target.id)) targetAttributes.push(target)
        const missingValues = source.values.filter(value => !target.values.some(existingValue => existingValue.value.toLocaleLowerCase('tr-TR') === value.value.toLocaleLowerCase('tr-TR')))
        if (missingValues.length) await hubApi<LocalAttribute>(`/catalog/attributes/${target.id}/values`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify(missingValues.map((value, index) => ({ value: value.value, sortOrder: target.values.length + index }))) })
        for (const sourceValue of source.values) { const targetValue = target.values.find(value => value.value.toLocaleLowerCase('tr-TR') === sourceValue.value.toLocaleLowerCase('tr-TR')); if (targetValue) valueIdMap.set(sourceValue.id, targetValue.id) }
      }
      if (transferSelection.mappings) {
        if (!references.data) throw new Error('Kategori reference verisi hazır değil; önce kategori eşitlemesini tamamlayın.')
        for (const mappingItem of transferBundle.categoryMappings) {
          const targetLocalId = categoryIdMap.get(mappingItem.localId) ?? targetCategories.find(item => item.id === mappingItem.localId)?.id
          if (!targetLocalId) continue
          await hubApi(`/mappings/categories/${targetLocalId}`, { method: 'PUT', body: JSON.stringify({ connectionId, snapshotId: references.data.snapshotId, externalId: mappingItem.externalId, status: 'VERIFIED' }) })
        }
        const attributeSnapshots = new Map<string, ReferenceData>()
        for (const mappingItem of transferBundle.attributeMappings) {
          const categoryScope = mappingItem.scopeExternalId
          const snapshot = attributeSnapshots.get(categoryScope) ?? await hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes?connectionId=${encodeURIComponent(connectionId)}`)
          attributeSnapshots.set(categoryScope, snapshot)
          const targetLocalId = attributeIdMap.get(mappingItem.localId) ?? targetAttributes.find(item => item.id === mappingItem.localId)?.id
          if (!targetLocalId || !snapshot.items.some(item => item.externalId === mappingItem.externalId)) continue
          await hubApi(`/mappings/attributes/${targetLocalId}`, { method: 'PUT', body: JSON.stringify({ connectionId, snapshotId: snapshot.snapshotId, scopeExternalId: categoryScope, externalId: mappingItem.externalId, status: 'VERIFIED', role: isOptionAttribute(transferBundle.attributes.find(item => item.id === mappingItem.localId) ?? { code: '', roles: [] }) ? 'OPTION' : 'ATTRIBUTE' }) })
        }
        for (const mappingItem of transferBundle.attributeValueMappings) {
          const [categoryScope, remoteAttributeId] = mappingItem.scopeExternalId.split('/')
          const snapshot = await hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(remoteAttributeId)}/values?connectionId=${encodeURIComponent(connectionId)}`)
          const targetLocalId = valueIdMap.get(mappingItem.localId)
          if (!targetLocalId || !snapshot.items.some(item => item.externalId === mappingItem.externalId)) continue
          await hubApi(`/mappings/attribute-values/${targetLocalId}`, { method: 'PUT', body: JSON.stringify({ connectionId, snapshotId: snapshot.snapshotId, scopeExternalId: mappingItem.scopeExternalId, externalId: mappingItem.externalId, status: 'VERIFIED' }) })
        }
      }
      setTransferOpen(false); setTransferBundle(null); setNotice('Seçtiğiniz eşleştirme alanları güvenli şekilde içe aktarıldı.'); await Promise.all([client.invalidateQueries({ queryKey: ['categories', 'mapping'] }), client.invalidateQueries({ queryKey: ['attributes', 'mapping-builder'] }), client.invalidateQueries({ queryKey: ['category-mappings', connectionId] }), client.invalidateQueries({ queryKey: ['attribute-mappings'] })])
    } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Eşleştirme aktarımı uygulanamadı.') } finally { setTransferBusy(false) }
  }
  function readMappingBundle(event: ChangeEvent<HTMLInputElement>) {
    const file = event.currentTarget.files?.[0]
    event.currentTarget.value = ''
    if (!file) return
    void file.text().then(text => {
      const parsed = JSON.parse(text) as Partial<MappingTransferBundle>
      if (parsed.format !== 'RAVENCIA_MAPPING_BUNDLE' || parsed.version !== 1 || !Array.isArray(parsed.categories) || !Array.isArray(parsed.attributes)) throw new Error('Geçerli bir Ravencia eşleştirme yedeği seçin.')
      setTransferBundle({ format: 'RAVENCIA_MAPPING_BUNDLE', version: 1, exportedAt: parsed.exportedAt ?? new Date().toISOString(), categories: parsed.categories, attributes: parsed.attributes, categoryMappings: parsed.categoryMappings ?? [], attributeMappings: parsed.attributeMappings ?? [], attributeValueMappings: parsed.attributeValueMappings ?? [] })
      setTransferOpen(true)
    }).catch(reason => setNotice(reason instanceof Error ? reason.message : 'Eşleştirme yedeği okunamadı.'))
  }
  return <section className="content f3 mapping-page mapping-workspace stitch-reference-mapping"><header className="mapping-reference-heading"><div><h1>Eşleştirme Ayarları</h1><p>Lokal kategorilerinizi pazar yeri kategorileriyle eşleştirin.</p></div><div className="mapping-reference-heading-actions" aria-label="Eşleştirme yedeği işlemleri"><span>Eşleştirme yedeği</span><div className="mapping-transfer-actions"><button type="button" className="secondary" title="Dışa aktarılacak alanları seç" onClick={() => { setExportSelection({ categories: true, options: true, attributes: true, mappings: true }); setExportOpen(true) }} disabled={transferBusy || !connectionId}>{transferBusy ? 'Hazırlanıyor…' : 'Dışa aktar'}</button><label className="mapping-transfer-upload" title="JSON eşleştirme yedeğini içe aktar">İçe aktar<input type="file" accept="application/json,.json" onChange={readMappingBundle} disabled={transferBusy} /></label></div></div></header><MappingViewTabs active="category" />{notice && <div role="status" className="notice">{notice}</div>}
    <article className="mapping-reference-card mapping-category-builder"><h2><span aria-hidden="true">⌘</span>1. Yeni Kategori Eşleştirme</h2><div className="mapping-reference-grid"><section className="mapping-reference-local"><label>Panel kategorisi<button type="button" className="panel-category-picker-trigger" aria-expanded={panelPickerOpen} onClick={() => setPanelPickerOpen(value => !value)} disabled={!connectionId}><span>{localLeaves.find(item => item.id === localId)?.path ?? 'Panel kategorisi seçin...'}</span><i aria-hidden="true">⌄</i></button></label>{panelPickerOpen && <div className="panel-category-picker" role="dialog" aria-label="Panel kategorisi seçimi"><div><input autoFocus aria-label="Panel kategorilerinde ara" value={panelPickerSearch} onChange={event => setPanelPickerSearch(event.target.value)} placeholder="Panel kategorilerinde ara..." /><button type="button" aria-label="Panel kategori menüsünü kapat" onClick={() => setPanelPickerOpen(false)}>×</button></div><div role="listbox">{panelPickerItems.length ? panelPickerItems.map(item => <button type="button" role="option" aria-selected={item.id === localId} key={item.id} onClick={() => { setLocalId(item.id); setExternalId(''); setExternalSearch(''); setPanelPickerOpen(false); setNotice('') }}>{item.path}</button>) : <span>Aramaya uygun panel kategorisi bulunamadı.</span>}</div></div>}<details className="mapping-local-manager" onClick={event => { if (event.target === event.currentTarget) event.currentTarget.removeAttribute('open') }}><summary>Lokal kategorileri yönet</summary><div className="mapping-local-manager-dialog" onClick={event => event.stopPropagation()}><header><div><strong>Panel kategorileri</strong><small>Ekleyin, arayın veya kaldırın.</small></div><button type="button" aria-label="Kategori yönetim penceresini kapat" onClick={event => { event.preventDefault(); event.stopPropagation(); event.currentTarget.closest('details')?.removeAttribute('open') }}>×</button></header><form className="platform-category-create" onSubmit={event => { event.preventDefault(); createCategory.mutate() }}><label>Yeni panel kategorisi<input aria-label="Yeni panel kategorisi adı" maxLength={160} value={categoryName} onChange={event => setCategoryName(event.target.value)} placeholder="Örn. Anne Bluz" required /></label><button disabled={createCategory.isPending}>{createCategory.isPending ? 'Ekleniyor…' : '+ Kategori ekle'}</button></form><div className="category-library-tools"><div className={categoryLibraryOpen ? 'category-library-search-control is-open' : 'category-library-search-control'}><input aria-label="Eklenen panel kategorilerinde ara" value={categoryLibrarySearch} onFocus={() => setCategoryLibraryOpen(true)} onClick={() => setCategoryLibraryOpen(true)} onChange={event => setCategoryLibrarySearch(event.target.value)} placeholder="Kategori ara…" /><button type="button" className="category-library-search-toggle" aria-label={categoryLibraryOpen ? 'Kategorileri kapat' : 'Kategorileri aç'} aria-expanded={categoryLibraryOpen} onMouseDown={event => event.preventDefault()} onClick={() => setCategoryLibraryOpen(value => !value)}><span aria-hidden="true" /></button></div><select aria-label="Panel kategorilerini sırala" value={categoryLibrarySort} onChange={event => setCategoryLibrarySort(event.target.value as 'NAME_ASC' | 'NAME_DESC')}><option value="NAME_ASC">A–Z</option><option value="NAME_DESC">Z–A</option></select><span>{filteredCategoryLibrary.length.toLocaleString('tr-TR')} sonuç</span></div><div className="category-chip-list">{filteredCategoryLibrary.slice(0, 100).map(category => <span key={category.id} className={category.id === localId ? 'active' : ''}><button type="button" onClick={() => { setLocalId(category.id); setExternalId('') }}>{category.name}</button><button type="button" className="category-chip-remove" aria-label="Kategoriyi kaldır" onClick={() => archiveCategory.mutate(category)} disabled={archiveCategory.isPending}>×</button></span>)}</div></div></details></section><section className="mapping-reference-target"><label>Hedef Pazar Yeri &amp; Kategori</label><div className="mapping-target-controls"><select aria-label="Hedef pazaryeri bağlantısı" value={connectionId} onChange={event => { setConnectionId(event.target.value); setLocalId(''); setExternalId(''); setExternalSearch(''); setExternalPickerOpen(false); setNotice('') }}><option value="">Trendyol bağlantısı</option>{trendyolConnections.map(item => <option value={item.id} key={item.id}>Trendyol{trendyolConnections.length > 1 ? ` · ${item.displayName}` : ''}</option>)}</select><div className="platform-category-picker-wrap"><button type="button" className="panel-category-picker-trigger" aria-expanded={externalPickerOpen} onClick={() => setExternalPickerOpen(value => !value)} disabled={!localId || references.isError}><span>{selectedExternal ? cleanTrendyolCategoryPath(selectedExternal.path) : 'Platform kategorisi seçin...'}</span><i aria-hidden="true">⌄</i></button>{externalPickerOpen && <div className="panel-category-picker platform-category-picker" role="dialog" aria-label="Platform kategorisi seçimi"><div><input autoFocus aria-label="Platform kategorilerinde ara" value={externalSearch} onChange={event => setExternalSearch(event.target.value)} placeholder={references.isLoading ? 'Kategoriler yükleniyor...' : 'Platform kategorilerinde ara...'} /><button type="button" aria-label="Platform kategori menüsünü kapat" onClick={() => setExternalPickerOpen(false)}>×</button></div><div role="listbox">{visibleExternal.length ? visibleExternal.map(item => <button type="button" role="option" aria-selected={item.externalId === externalId} key={item.externalId} onClick={() => { setExternalId(item.externalId); setExternalPickerOpen(false) }}>{cleanTrendyolCategoryPath(item.path)}</button>) : <span>{references.isError ? 'Kategori listesi alınamadı. Bağlantı ekranından kategorileri eşitleyin.' : 'Aramayla eşleşen kategori bulunamadı.'}</span>}</div></div>}</div></div></section></div><footer><span>{references.data ? `${externalLeaves.length.toLocaleString('tr-TR')} yaprak kategori` : 'Kategori snapshot’ı bekleniyor'}{mapping.data ? ` · Kayıt v${mapping.data.version}` : ''}</span><button type="button" disabled={!localId || !externalId || save.isPending || mapping.isLoading} onClick={() => save.mutate()}><span aria-hidden="true">↗</span>{save.isPending ? 'Kaydediliyor…' : mapping.data ? 'Eşleştirmeyi Güncelle' : 'Eşleştirmeyi Kaydet'}</button></footer></article>
    <article className="mapping-reference-card mapping-option-summary panel-library-section"><div className="mapping-section-heading"><div><h2><span aria-hidden="true">☷</span>2. Ürün Seçenekleri</h2><p>Renk ve beden gibi varyant seçeneklerini panele kaydedin.</p></div></div><PanelAttributeLibraryBuilder role="OPTION" attributes={localAttributes.data?.items.filter(item => item.isActive) ?? []} onNotice={setNotice} /></article>
<article id="category-value-mapping" className="mapping-reference-card mapping-attribute-summary panel-library-section"><div className="mapping-section-heading"><div><h2><span aria-hidden="true">▤</span>3. Kategori Özellikleri ve Değerleri</h2><p>Ürün özelliklerini ve değerlerini doğrudan panele kaydedin.</p></div></div><PanelAttributeLibraryBuilder role="ATTRIBUTE" attributes={localAttributes.data?.items.filter(item => item.isActive) ?? []} onNotice={setNotice} /></article>
    <article className="mapping-reference-card mapping-saved-card">
      <header>
        <h2>4. Kayıtlı Kategori Eşleştirmeleri</h2>
        <div className="mapping-saved-header-tools">
          <div className="mapping-saved-tabs mapping-platform-tabs" role="tablist" aria-label="Kayıtlı eşleştirme platformları">
            {mappingPlatformDefinitions.map(platform => {
              const available = activeMappingConnections.some(item => item.platformCode.toUpperCase() === platform.code)
              return <button type="button" role="tab" aria-selected={selectedPlatformCode === platform.code} className={`${selectedPlatformCode === platform.code ? 'active' : ''}${available ? '' : ' is-unavailable'}`} title={available ? `${platform.label} eşleştirmelerini göster` : `${platform.label} için aktif bağlantı yok`} onClick={() => { setSelectedPlatformCode(platform.code); setLocalId(''); setExternalId(''); setExternalSearch(''); setNotice('') }}>{platform.label}</button>
            })}
          </div>
          <input aria-label="Kayıtlı eşleştirmelerde ara" value={savedSearch} onChange={event => setSavedSearch(event.target.value)} placeholder="Eşleştirme ara..." />
          <button type="button" className="mapping-icon-button mapping-sort-button" aria-label={savedSort === 'NAME_ASC' ? 'Kayıtlı eşleştirmeleri Z-A sırala' : 'Kayıtlı eşleştirmeleri A-Z sırala'} title={savedSort === 'NAME_ASC' ? 'Z-A sırala' : 'A-Z sırala'} onClick={() => setSavedSort(current => current === 'NAME_ASC' ? 'NAME_DESC' : 'NAME_ASC')}>{savedSort === 'NAME_ASC' ? 'A–Z' : 'Z–A'}</button>
        </div>
      </header>
      <div className="mapping-saved-table"><table><thead><tr><th>Panel Kategorisi</th><th>Platform</th><th>Platform Kategorisi</th><th>Durum</th><th>İşlemler</th></tr></thead><tbody>{sortedSavedRows.length ? sortedSavedRows.map(({ item, local, external }) => <tr key={item.id}><td>{local?.path ?? item.localId}</td><td><span className="trendyol-badge">{selectedPlatform.label}</span></td><td>{cleanTrendyolCategoryPath(external?.path ?? item.externalId)}</td><td><span className={`mapping-table-status ${item.status === 'VERIFIED' || item.status === 'ACTIVE' ? 'active' : 'error'}`}><i />{item.status === 'VERIFIED' || item.status === 'ACTIVE' ? 'Aktif' : item.status}</span></td><td><div className="mapping-row-actions"><button type="button" className="mapping-feature-button" aria-label="Kategori özelliklerini düzenle" onClick={() => { setLocalId(item.localId); setExternalId(item.externalId); setAdvancedOpen(true) }}>Kategori özellikleri</button><button type="button" aria-label="Eşleştirmeyi sil" disabled={removeMapping.isPending} onClick={() => { if (window.confirm('Bu kategori eşleştirmesi kaldırılsın mı?')) removeMapping.mutate(item) }}>♜</button></div></td></tr>) : <tr><td className="mapping-empty-table-cell" colSpan={5}>{categoryMappings.isLoading ? `${selectedPlatform.label} eşleştirmeleri yükleniyor…` : !connectionId ? `${selectedPlatform.label} için aktif bağlantı bulunamadı.` : 'Kayıtlı kategori eşleştirmesi bulunamadı.'}</td></tr>}</tbody></table></div>
      <footer><span>Toplam {savedMappings.length.toLocaleString('tr-TR')} eşleştirme</span><div><button type="button" disabled>‹</button><button type="button" className="active">1</button><button type="button" disabled>›</button></div></footer>
    </article>
{exportOpen && <div className="workspace-modal-backdrop mapping-transfer-backdrop" role="presentation" onMouseDown={() => { if (!transferBusy) setExportOpen(false) }}><section className="workspace-modal mapping-transfer-modal mapping-export-modal" role="dialog" aria-modal="true" aria-labelledby="mapping-export-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">EŞLEŞTİRME YEDEĞİ</p><h2 id="mapping-export-title">Dışa aktarılacak alanları seçin</h2><p>Yedeğe dahil edilecek alanları seçin. Seçimleriniz tek bir JSON dosyasında toplanır.</p></div><button type="button" className="modal-close" onClick={() => setExportOpen(false)} disabled={transferBusy} aria-label="Dışa aktarma penceresini kapat">×</button></header><div className="mapping-transfer-options mapping-export-options"><label className="mapping-transfer-all-option"><input type="checkbox" checked={Object.values(exportSelection).every(Boolean)} onChange={event => setExportSelection({ categories: event.target.checked, options: event.target.checked, attributes: event.target.checked, mappings: event.target.checked })} /><span><strong>Tümü</strong><small>Yukarıdaki tüm alanları tek seferde seçer.</small></span><em>{Object.values(exportSelection).filter(Boolean).length}/4 seçili</em></label><div className="mapping-export-scope-grid"><label><input type="checkbox" checked={exportSelection.categories} onChange={event => setExportSelection(current => ({ ...current, categories: event.target.checked }))} /><span><strong>Panel kategorileri</strong><small>Aktif kategori kayıtlarını aktarır.</small></span></label><label><input type="checkbox" checked={exportSelection.options} onChange={event => setExportSelection(current => ({ ...current, options: event.target.checked }))} /><span><strong>Ürün seçenekleri</strong><small>Renk ve beden değerlerini aktarır.</small></span></label><label><input type="checkbox" checked={exportSelection.attributes} onChange={event => setExportSelection(current => ({ ...current, attributes: event.target.checked }))} /><span><strong>Kategori özellikleri</strong><small>Özellik ve kayıtlı değerlerini aktarır.</small></span></label><label><input type="checkbox" checked={exportSelection.mappings} onChange={event => setExportSelection(current => ({ ...current, mappings: event.target.checked }))} /><span><strong>Kategori eşleştirmeleri</strong><small>Kategori, özellik ve değer eşleşmelerini aktarır.</small></span></label></div></div><footer><span className="mapping-export-footer-hint">{Object.values(exportSelection).filter(Boolean).length ? 'Seçilen alanlar dışa aktarılmaya hazır.' : 'En az bir alan seçin.'}</span><button type="button" className="secondary" onClick={() => setExportOpen(false)} disabled={transferBusy}>Vazgeç</button><button type="button" onClick={() => void exportSelectedMappings()} disabled={transferBusy || !Object.values(exportSelection).some(Boolean)}>{transferBusy ? 'Hazırlanıyor…' : 'Seçilenleri dışa aktar'}</button></footer></section></div>}
{transferOpen && transferBundle && <div className="workspace-modal-backdrop mapping-transfer-backdrop" role="presentation" onMouseDown={() => { if (!transferBusy) { setTransferOpen(false); setTransferBundle(null) } }}><section className="workspace-modal mapping-transfer-modal" role="dialog" aria-modal="true" aria-labelledby="mapping-transfer-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">EŞLEŞTİRME YEDEĞİ</p><h2 id="mapping-transfer-title">İçe aktarma alanlarını seçin</h2><p>{transferBundle.categories.length} kategori · {transferBundle.attributes.length} özellik · {transferBundle.categoryMappings.length + transferBundle.attributeMappings.length + transferBundle.attributeValueMappings.length} eşleşme</p></div><button type="button" className="modal-close" onClick={() => { setTransferOpen(false); setTransferBundle(null) }} disabled={transferBusy} aria-label="İçe aktarmayı kapat">×</button></header><div className="mapping-transfer-options">{([['categories', 'Kategoriler', 'Panel kategorilerini eksik olanları ekleyerek güncelle'], ['options', 'Ürün seçenekleri', 'option- ile başlayan seçenek başlıklarını güncelle'], ['attributes', 'Ürün özellikleri', 'Ürün özelliklerini ve değerlerini güncelle'], ['mappings', 'Eşleşmeler', 'Kategori, özellik ve değer eşleşmelerini uygula']] as Array<[MappingTransferScope, string, string]>).map(([scope, label, description]) => <label key={scope}><input type="checkbox" checked={transferSelection[scope]} onChange={event => setTransferSelection(current => ({ ...current, [scope]: event.target.checked }))} /><span><strong>{label}</strong><small>{description}</small></span></label>)}</div><p className="mapping-transfer-warning">Mevcut kayıtlar silinmez; aynı kod/ad ve değerler güncellenir, eksikler eklenir. Eşleşmeler seçili aktif Trendyol bağlantısının güncel snapshot’ı ile doğrulanır.</p><footer><button type="button" className="secondary" onClick={() => { setTransferOpen(false); setTransferBundle(null) }} disabled={transferBusy}>Vazgeç</button><button type="button" onClick={() => void importMappings()} disabled={transferBusy}>{transferBusy ? 'İçe aktarılıyor…' : 'Seçilenleri güncelle'}</button></footer></section></div>}
<details id="mapping-advanced-tools" className="mapping-advanced-tools" open={advancedOpen} onToggle={event => setAdvancedOpen(event.currentTarget.open)}><summary><span>Kategori başlıkları ve seçenekleri</span><small aria-label="Popup’ı kapat">×</small></summary>{advancedOpen && <div><section className="mapping-popup-content"><CategoryAttributeWorkspace connectionId={connectionId} localCategoryId={localId} localCategories={localLeaves} localAttributes={localAttributes.data?.items.filter(item => item.isActive) ?? []} onNotice={setNotice} /></section></div>}</details>
  </section>
}

function slug(value: string) { return value.toLocaleLowerCase('tr-TR').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || `attr-${Date.now()}` }
function attributeCodeForRole(title: string, role: 'ATTRIBUTE' | 'OPTION') { return `${role === 'OPTION' ? 'option-' : 'attribute-'}${slug(title)}` }
function isOptionAttribute(attribute: Pick<LocalAttribute, 'code' | 'roles'>) { return attribute.code.trim().toLowerCase().startsWith('option-') || attribute.roles?.some(role => role.toUpperCase() === 'OPTION') === true }

function cleanTrendyolCategoryPath(value: string) {
  return value.replace(/\[TDG\]\s*/gi, '').replace(/\(\s*TDG\s*\)\s*/gi, '').replace(/\s*\/\s*/g, ' / ').trim()
}

function AttributeCategoryAssignmentModal({ attribute, role, categories, onClose, onNotice }: { attribute: LocalAttribute; role: 'ATTRIBUTE' | 'OPTION'; categories: LocalCategory[]; onClose: () => void; onNotice: (value: string) => void }) {
  const client = useQueryClient()
  const [search, setSearch] = useState('')
  const [selectedCategoryIds, setSelectedCategoryIds] = useState<string[]>([])
  const [initialCategoryIds, setInitialCategoryIds] = useState<string[]>([])
  const [requirementsByCategory, setRequirementsByCategory] = useState<Record<string, CategoryRequirementView[]>>({})
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const categoryKey = categories.map(category => `${category.id}:${category.version}`).join(',')
  const activeCategories = categories.filter(category => category.isActive && category.isLeaf)
  const normalizedSearch = search.trim().toLocaleLowerCase('tr-TR')
  const visibleCategories = activeCategories.filter(category => !normalizedSearch || `${category.path} ${category.name}`.toLocaleLowerCase('tr-TR').includes(normalizedSearch))

  useEffect(() => {
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => { document.body.style.overflow = previousOverflow }
  }, [])

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setError('')
    void Promise.all(activeCategories.map(async category => [category.id, await hubApi<CategoryRequirementView[]>(`/catalog/categories/${category.id}/attribute-requirements`)] as const))
      .then(entries => {
        if (cancelled) return
        const nextRequirements = Object.fromEntries(entries) as Record<string, CategoryRequirementView[]>
        const nextSelected = activeCategories.filter(category => nextRequirements[category.id]?.some(item => item.attributeId === attribute.id)).map(category => category.id)
        setRequirementsByCategory(nextRequirements)
        setSelectedCategoryIds(nextSelected)
        setInitialCategoryIds(nextSelected)
      })
      .catch(reason => { if (!cancelled) setError(reason instanceof Error ? reason.message : 'Kategori kapsamı alınamadı.') })
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [attribute.id, categoryKey])

  function isSelected(categoryId: string) { return selectedCategoryIds.includes(categoryId) }
  function toggleCategory(categoryId: string) {
    setSelectedCategoryIds(current => current.includes(categoryId) ? current.filter(id => id !== categoryId) : [...current, categoryId])
  }
  function toCommand(item: CategoryRequirementView | AttributeRequirementCommand): AttributeRequirementCommand {
    return { attributeId: item.attributeId, isRequired: item.isRequired, allowsCustomValue: item.allowsCustomValue, displayOrder: item.displayOrder, role: item.role }
  }
  async function save() {
    if (loading || saving) return
    setSaving(true)
    setError('')
    const initial = new Set(initialCategoryIds)
    const selected = new Set(selectedCategoryIds)
    const changedCategories = activeCategories.filter(category => initial.has(category.id) !== selected.has(category.id))
    try {
      for (const category of changedCategories) {
        const current = requirementsByCategory[category.id] ?? []
        const existing = current.find(item => item.attributeId === attribute.id)
        const next = current.filter(item => item.attributeId !== attribute.id).map(toCommand)
        if (selected.has(category.id)) {
          const displayOrder = existing?.displayOrder ?? Math.max(-1, ...current.map(item => item.displayOrder)) + 1
          next.push({ attributeId: attribute.id, isRequired: existing?.isRequired ?? false, allowsCustomValue: existing?.allowsCustomValue ?? attribute.values.length === 0, displayOrder, role })
        }
        next.sort((left, right) => left.displayOrder - right.displayOrder)
        await hubApi(`/catalog/categories/${category.id}/attribute-requirements`, { method: 'PUT', headers: { 'If-Match': `"v${category.version}"` }, body: JSON.stringify(next) })
        await client.invalidateQueries({ queryKey: ['category-requirements', category.id] })
        client.setQueryData<Page<LocalCategory>>(['categories', 'mapping-builder-assignment'], currentCategories => currentCategories ? { ...currentCategories, items: currentCategories.items.map(item => item.id === category.id ? { ...item, version: item.version + 1 } : item) } : currentCategories)
        client.setQueryData<Page<LocalCategory>>(['categories', 'mapping'], currentCategories => currentCategories ? { ...currentCategories, items: currentCategories.items.map(item => item.id === category.id ? { ...item, version: item.version + 1 } : item) } : currentCategories)
      }
      onNotice(changedCategories.length ? `${attribute.name} için ${selectedCategoryIds.length} kategori kapsamı kaydedildi.` : 'Kategori kapsamındaki değişiklik bulunmuyor.')
      onClose()
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'Kategori kapsamı kaydedilemedi.'
      setError(message)
      onNotice(message)
    } finally { setSaving(false) }
  }

  return <div className="panel-attribute-modal panel-attribute-category-modal" role="dialog" aria-modal="true" aria-labelledby="attribute-category-assignment-title"><div className="panel-attribute-modal-card panel-attribute-category-modal-card"><header><div><strong id="attribute-category-assignment-title">{attribute.name} kategorileri</strong><small>{role === 'OPTION' ? 'Ürün seçeneği' : 'Ürün özelliği'} · görüneceği kategorileri seçin</small></div><button type="button" aria-label="Kategori kapsamı penceresini kapat" onClick={onClose} disabled={saving}>×</button></header><div className="panel-attribute-category-search"><input autoFocus aria-label="Kategorilerde ara" value={search} onChange={event => setSearch(event.target.value)} placeholder="Kategori yolu veya adı ara..." /><span>{selectedCategoryIds.length} seçili</span></div>{loading ? <div className="mapping-empty-row">Kategori kapsamları yükleniyor…</div> : error && !Object.keys(requirementsByCategory).length ? <div className="mapping-empty-row attribute-feedback is-error">{error}</div> : <div className="panel-attribute-category-list" role="listbox" aria-label="Özelliğin görüneceği kategoriler">{visibleCategories.length ? visibleCategories.map(category => <button type="button" role="option" aria-selected={isSelected(category.id)} className={isSelected(category.id) ? 'active' : ''} key={category.id} onClick={() => toggleCategory(category.id)}><span>{category.path}</span><i aria-hidden="true">{isSelected(category.id) ? '✓' : ''}</i></button>) : <div className="mapping-empty-row">Aramaya uygun kategori bulunamadı.</div>}</div>}<footer><button type="button" className="secondary" onClick={onClose} disabled={saving}>Vazgeç</button><button type="button" onClick={() => void save()} disabled={loading || saving || !!error && !Object.keys(requirementsByCategory).length}>{saving ? 'Kaydediliyor…' : 'Kapsamı kaydet'}</button></footer></div></div>
}

function PanelAttributeLibraryBuilder({ role, attributes, onNotice }: { role: 'ATTRIBUTE' | 'OPTION'; attributes: LocalAttribute[]; onNotice: (value: string) => void }) {
  const client = useQueryClient()
  const [title, setTitle] = useState(''); const [values, setValues] = useState<string[]>([]); const [valueDraft, setValueDraft] = useState(''); const [editingId, setEditingId] = useState(''); const [newValues, setNewValues] = useState(''); const [editingValue, setEditingValue] = useState<{ id: string; value: string } | null>(null); const [categoryAssignmentId, setCategoryAssignmentId] = useState(''); const [feedback, setFeedback] = useState(''); const [feedbackTone, setFeedbackTone] = useState<'success' | 'error'>('success')
  const isOption = role === 'OPTION'
  const records = attributes.filter(attribute => isOption ? isOptionAttribute(attribute) : !isOptionAttribute(attribute))
  const editingAttribute = records.find(attribute => attribute.id === editingId) ?? null
  const categoryAssignmentAttribute = records.find(attribute => attribute.id === categoryAssignmentId) ?? null
  const categories = useQuery({ queryKey: ['categories', 'mapping-builder-assignment'], queryFn: () => loadAllPages<LocalCategory>('/catalog/categories'), enabled: !!categoryAssignmentAttribute })
  function showFeedback(message: string, tone: 'success' | 'error' = 'success') { setFeedback(message); setFeedbackTone(tone) }
  useEffect(() => {
    if (!editingAttribute) return
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => { document.body.style.overflow = previousOverflow }
  }, [editingAttribute])
  function updateCache(attribute: LocalAttribute) {
    client.setQueryData<Page<LocalAttribute>>(['attributes', 'mapping-builder'], current => {
      if (!current) return current
      const found = current.items.some(item => item.id === attribute.id)
      return { ...current, items: found ? current.items.map(item => item.id === attribute.id ? attribute : item) : [...current.items, attribute] }
    })
  }
  async function create() {
    try {
      if (!title.trim()) return showFeedback(isOption ? 'Seçenek başlığı girin.' : 'Özellik başlığı girin.', 'error')
      const parsedValues = values
      const attribute = await hubApi<LocalAttribute>('/catalog/attributes', { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify({ code: attributeCodeForRole(title, role), name: title.trim(), dataType: parsedValues.length ? 'SINGLE_SELECT' : 'TEXT', selectionMode: parsedValues.length ? 'SINGLE' : null, unit: null, values: parsedValues.map((value, index) => ({ value, sortOrder: index })) }) })
      updateCache(attribute); setEditingId(attribute.id); setTitle(''); setValues([]); setValueDraft(''); showFeedback(`${attribute.name} panele kaydedildi.`); onNotice(`${attribute.name} panele kaydedildi.`)
    } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Kayıt oluşturulamadı.', 'error') }
  }
  function addInitialValues(nextValues: string[]) {
    setValues(current => {
      const merged = [...current]
      nextValues.forEach(value => { if (!merged.some(existing => existing.toLocaleLowerCase('tr-TR') === value.toLocaleLowerCase('tr-TR'))) merged.push(value) })
      return merged
    })
  }
  async function addValues(attributeId: string, source: string) {
    try {
      const parsedValues = source.split(',').map(value => value.trim()).filter(Boolean)
      if (!parsedValues.length) return showFeedback('Eklenecek değerleri Enter veya Ekle ile girin.', 'error')
      const attribute = await hubApi<LocalAttribute>(`/catalog/attributes/${attributeId}/values`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify(parsedValues.map((value, index) => ({ value, sortOrder: index }))) })
      updateCache(attribute); setNewValues(''); showFeedback('Yeni değerler panele kaydedildi.'); onNotice('Yeni değerler panele kaydedildi.')
    } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Değerler kaydedilemedi.', 'error') }
  }
  async function removeAttribute(attribute: LocalAttribute) {
    if (!window.confirm(`“${attribute.name}” ve bağlı değerleri kaldırılsın mı?`)) return
    try {
      await hubApi<LocalAttribute>(`/catalog/attributes/${attribute.id}`, { method: 'DELETE', headers: { 'If-Match': `"v${attribute.version}"` } })
      client.setQueryData<Page<LocalAttribute>>(['attributes', 'mapping-builder'], current => current ? { ...current, items: current.items.filter(item => item.id !== attribute.id) } : current)
      setEditingId(''); showFeedback(`${attribute.name} kaldırıldı.`)
    } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Başlık kaldırılamadı.', 'error') }
  }
  async function removeValue(attributeId: string, valueId: string) {
    try { const attribute = await hubApi<LocalAttribute>(`/catalog/attributes/${attributeId}/values/${valueId}`, { method: 'DELETE' }); updateCache(attribute); setEditingValue(null); showFeedback('Değer kaldırıldı.') } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Değer kaldırılamadı.', 'error') }
  }
  async function updateValue(attributeId: string, originalId: string, nextValue: string, sortOrder: number) {
    const value = nextValue.trim()
    if (!value) return showFeedback('Değer boş olamaz.', 'error')
    try {
      const attribute = await hubApi<LocalAttribute>(`/catalog/attributes/${attributeId}/values/${originalId}`, { method: 'PUT', headers: { 'If-Match': `"v${editingAttribute?.version ?? 0}"` }, body: JSON.stringify({ value, sortOrder }) })
      updateCache(attribute); setEditingValue(null); showFeedback('Değer güncellendi.')
    } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Değer güncellenemedi.', 'error') }
  }
  async function moveValue(attribute: LocalAttribute, valueId: string, direction: -1 | 1) {
    const activeValues = attribute.values.filter(value => value.isActive).slice().sort((left, right) => left.sortOrder - right.sortOrder || left.value.localeCompare(right.value, 'tr'))
    const index = activeValues.findIndex(value => value.id === valueId)
    const targetIndex = index + direction
    if (index < 0 || targetIndex < 0 || targetIndex >= activeValues.length) return
    const next = activeValues.slice()
    const [moved] = next.splice(index, 1)
    next.splice(targetIndex, 0, moved)
    try {
      const updated = await hubApi<LocalAttribute>(`/catalog/attributes/${attribute.id}/values/order`, { method: 'PUT', headers: { 'If-Match': `"v${attribute.version}"` }, body: JSON.stringify({ valueIds: next.map(value => value.id) }) })
      updateCache(updated); showFeedback('Değer sırası güncellendi.')
    } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Değer sırası güncellenemedi.', 'error') }
  }
  async function sortValuesAlphabetically(attribute: LocalAttribute, direction: 1 | -1) {
    const current = attribute.values.filter(value => value.isActive).slice().sort((left, right) => left.sortOrder - right.sortOrder || left.value.localeCompare(right.value, 'tr'))
    const next = current.slice().sort((left, right) => direction * left.value.localeCompare(right.value, 'tr-TR', { sensitivity: 'base', numeric: true }) || current.indexOf(left) - current.indexOf(right))
    if (next.every((value, index) => value.id === current[index]?.id)) {
      showFeedback(direction === 1 ? 'Değerler zaten A–Z sıralı.' : 'Değerler zaten Z–A sıralı.')
      return
    }
    try {
      const updated = await hubApi<LocalAttribute>(`/catalog/attributes/${attribute.id}/values/order`, { method: 'PUT', headers: { 'If-Match': `"v${attribute.version}"` }, body: JSON.stringify({ valueIds: next.map(value => value.id) }) })
      updateCache(updated); showFeedback(direction === 1 ? 'Değerler A–Z sıralandı.' : 'Değerler Z–A sıralandı.')
    } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Değerler sıralanamadı.', 'error') }
  }
  const editingValues = editingAttribute?.values.filter(value => value.isActive).slice().sort((left, right) => left.sortOrder - right.sortOrder || left.value.localeCompare(right.value, 'tr')) ?? []
  return <div className="panel-attribute-library"><div className="panel-attribute-create"><label>{isOption ? 'Yeni seçenek başlığı' : 'Yeni özellik başlığı'}<input value={title} onChange={event => setTitle(event.target.value)} placeholder={isOption ? 'Örn. Beden, Renk' : 'Örn. Materyal, Kol Boyu'} /></label><label>İlk değerler <TokenValueInput values={values} draft={valueDraft} onDraftChange={setValueDraft} onAdd={addInitialValues} onRemove={value => setValues(current => current.filter(item => item !== value))} placeholder="Değer yazın, Enter veya Ekle" /></label><button type="button" onClick={() => void create()}>+ Panele kaydet</button></div>{feedback && <p className={`attribute-feedback ${feedbackTone === 'success' ? 'is-success' : 'is-error'}`} role="status">{feedback}</p>}<div className="panel-attribute-records">{records.length ? records.map(attribute => <article className={attribute.id === editingId || attribute.id === categoryAssignmentId ? 'selected' : ''} key={attribute.id}><button type="button" className="panel-attribute-record-main" onClick={() => setEditingId(attribute.id)}><strong>{attribute.name}</strong><small>{attribute.values.filter(value => value.isActive).length ? `${attribute.values.filter(value => value.isActive).length} değer kaydedildi` : 'Serbest değer'}</small></button><div className="panel-attribute-record-actions"><button type="button" aria-label={`${attribute.name} düzenle`} onClick={() => setEditingId(attribute.id)}>✎</button><button type="button" className="panel-attribute-category-button" aria-label={`${attribute.name} kategorilerini düzenle`} onClick={() => setCategoryAssignmentId(attribute.id)}>Kategoriler</button><button type="button" aria-label={`${attribute.name} sil`} onClick={() => void removeAttribute(attribute)}>×</button></div></article>) : <div className="mapping-empty-row"><span>{isOption ? 'Henüz panel seçeneği oluşturulmadı.' : 'Henüz panel özelliği oluşturulmadı.'}</span></div>}</div>{editingAttribute && <div className="panel-attribute-modal" role="dialog" aria-modal="true" aria-label={`${editingAttribute.name} değer düzenleme`}><div className="panel-attribute-modal-card"><header><div><strong>{editingAttribute.name}</strong><small>{isOption ? 'Panel seçeneği' : 'Panel özelliği'} · değerleri yönetin</small></div><button type="button" aria-label="Düzenleme penceresini kapat" onClick={() => { setEditingId(''); setEditingValue(null) }}>×</button></header><div className="panel-attribute-add-value"><input value={newValues} onChange={event => setNewValues(event.target.value)} placeholder="Yeni değerleri virgülle yazın" /><button type="button" disabled={!newValues.trim()} onClick={() => void addValues(editingAttribute.id, newValues)}>+ Değer ekle</button><div className="panel-attribute-sort-actions" aria-label="Değerleri alfabetik sırala"><span>Otomatik sırala</span><button type="button" title="Değerleri A–Z sırala" aria-label="Değerleri A–Z sırala" onClick={() => void sortValuesAlphabetically(editingAttribute, 1)}>A–Z</button><button type="button" title="Değerleri Z–A sırala" aria-label="Değerleri Z–A sırala" onClick={() => void sortValuesAlphabetically(editingAttribute, -1)}>Z–A</button></div></div><div className="panel-attribute-value-editor">{editingValues.length ? editingValues.map((value, index) => <div key={value.id}>{editingValue?.id === value.id ? <input autoFocus value={editingValue.value} onChange={event => setEditingValue({ id: value.id, value: event.target.value })} /> : <span>{value.value}</span>}<div>{editingValue?.id === value.id ? <><button type="button" aria-label={`${value.value} değerini kaydet`} onClick={() => void updateValue(editingAttribute.id, value.id, editingValue.value, value.sortOrder)}>✓</button><button type="button" aria-label="Değer düzenlemeyi iptal et" onClick={() => setEditingValue(null)}>×</button></> : <><div className="panel-attribute-value-sort" aria-label={`${value.value} sırası`}><button type="button" aria-label={`${value.value} değerini yukarı taşı`} title="Yukarı taşı" disabled={index === 0} onClick={() => void moveValue(editingAttribute, value.id, -1)}>↑</button><button type="button" aria-label={`${value.value} değerini aşağı taşı`} title="Aşağı taşı" disabled={index === editingValues.length - 1} onClick={() => void moveValue(editingAttribute, value.id, 1)}>↓</button></div><button type="button" aria-label={`${value.value} değerini düzenle`} onClick={() => setEditingValue({ id: value.id, value: value.value })}>✎</button><button type="button" aria-label={`${value.value} değerini sil`} onClick={() => void removeValue(editingAttribute.id, value.id)}>×</button></>}</div></div>) : <span className="panel-value-empty">Henüz değer eklenmedi.</span>}</div></div></div>}{categoryAssignmentAttribute && <AttributeCategoryAssignmentModal attribute={categoryAssignmentAttribute} role={role} categories={categories.data?.items ?? []} onClose={() => setCategoryAssignmentId('')} onNotice={message => { showFeedback(message); onNotice(message) }} />}</div>
}

function TokenValueInput({ values, draft, onDraftChange, onAdd, onRemove, placeholder }: { values: string[]; draft: string; onDraftChange: (value: string) => void; onAdd: (values: string[]) => void; onRemove: (value: string) => void; placeholder: string }) {
  function addDraft() {
    const nextValues = draft.split(',').map(value => value.trim()).filter(Boolean)
    if (!nextValues.length) return
    onAdd(nextValues)
    onDraftChange('')
  }
  return <div className="panel-value-token-input"><div className="panel-value-token-entry"><input value={draft} onChange={event => onDraftChange(event.target.value)} onKeyDown={event => { if (event.key === 'Enter' || event.key === ',') { event.preventDefault(); addDraft() } }} placeholder={placeholder} /><button type="button" onClick={addDraft} disabled={!draft.trim()}>Ekle</button></div>{values.length > 0 && <div className="panel-value-token-list" aria-label="Eklenen ilk değerler">{values.map(value => <span className="panel-value-token" key={value}><span>{value}</span><button type="button" aria-label={`${value} değerini kaldır`} onClick={() => onRemove(value)}>×</button></span>)}</div>}</div>
}


function CategoryAttributeWorkspace({ connectionId, localCategoryId, localCategories, localAttributes, onNotice }: { connectionId: string; localCategoryId: string; localCategories: LocalCategory[]; localAttributes: LocalAttribute[]; onNotice: (value: string) => void }) {
  const client = useQueryClient()
  const [attributeCategoryId, setAttributeCategoryId] = useState(localCategoryId)
  const [section, setSection] = useState<'attributes' | 'options'>('attributes')
  const categoryMappings = useQuery({ queryKey: ['category-mappings', connectionId], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/categories?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId, retry: false })
  const matchedCategories = (categoryMappings.data ?? []).flatMap(mapping => {
    const category = localCategories.find(item => item.id === mapping.localId)
    return category ? [{ category, mapping }] : []
  })
  const matchedCategoryIds = matchedCategories.map(item => item.category.id).join(',')
  useEffect(() => {
    if (!matchedCategories.length) { if (attributeCategoryId) setAttributeCategoryId(''); return }
    if (!matchedCategories.some(item => item.category.id === attributeCategoryId)) setAttributeCategoryId(matchedCategories[0].category.id)
  }, [attributeCategoryId, matchedCategoryIds])
  useEffect(() => {
    if (localCategoryId && matchedCategories.some(item => item.category.id === localCategoryId)) setAttributeCategoryId(localCategoryId)
  }, [localCategoryId, matchedCategoryIds])
  const categoryRequirements = useQuery({ queryKey: ['category-requirements', attributeCategoryId, 'attribute-workspace'], queryFn: () => hubApi<CategoryRequirementView[]>(`/catalog/categories/${attributeCategoryId}/attribute-requirements`), enabled: !!attributeCategoryId })
  const categoryMapping = useQuery({ queryKey: ['category-mapping', attributeCategoryId, connectionId, 'embedded'], queryFn: () => hubApi<CatalogMapping | null>(`/mappings/categories/${attributeCategoryId}?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!attributeCategoryId && !!connectionId, retry: false })
  const categoryScope = categoryMapping.data?.externalId ?? ''
  const references = useQuery({ queryKey: ['reference-attributes', connectionId, categoryScope, 'embedded'], queryFn: () => hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId && !!categoryScope, retry: false })
  const sync = useMutation({ mutationFn: () => hubApi(`/connections/${connectionId}/reference-sync-jobs?resourceType=CATEGORY_ATTRIBUTES&parentExternalId=${encodeURIComponent(categoryScope)}`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: '{}' }), onSuccess: async () => { onNotice('Kategori özellikleri eşitleme kuyruğuna alındı.'); for (const delay of [500, 1000, 2000, 4000]) { await new Promise(resolve => window.setTimeout(resolve, delay)); await client.invalidateQueries({ queryKey: ['reference-attributes', connectionId, categoryScope, 'embedded'] }) } }, onError: reason => onNotice(reason instanceof Error ? reason.message : 'Kategori özellikleri eşitlenemedi.') })
  const mappings = useQuery({ queryKey: ['attribute-mappings', connectionId, categoryScope], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/attributes?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}`), enabled: !!connectionId && !!categoryScope, retry: false })
  const requirements = categoryRequirements.data ?? []
  const requirementOptionIds = new Set(requirements.filter(item => item.role === 'OPTION').map(item => item.attributeId))
  // Section 3 creates global panel attributes. They are not required to have a
  // row in category_requirements before they can be mapped to a marketplace
  // field; the mapping workspace is where that category-specific relationship
  // is established. Keep explicit OPTION requirements as a fallback for
  // existing records whose code predates the role prefix convention.
  const optionAttributes = localAttributes
    .filter(attribute => requirementOptionIds.has(attribute.id) || isOptionAttribute(attribute))
    .filter((item, index, items) => items.findIndex(candidate => candidate.id === item.id) === index)
  const optionAttributeIds = new Set(optionAttributes.map(item => item.id))
  const productAttributes = localAttributes.filter(attribute => attribute.isActive !== false && !optionAttributeIds.has(attribute.id))
  const mappingByLocal = new Map((mappings.data ?? []).map(item => [item.localId, item]))
  const optionMappings = (mappings.data ?? []).filter(item => optionAttributeIds.has(item.localId))
  const optionExternalIds = new Set(optionMappings.map(item => item.externalId))
  const productMappings = (mappings.data ?? []).filter(item => !optionAttributeIds.has(item.localId))
  const productMappingByExternal = new Map(productMappings.map(item => [item.externalId, item]))
  const usedProductLocalIds = new Set(productMappings.map(item => item.localId))
  const remoteItems = references.data?.items.filter(item => item.isActive).map(item => ({ ...item, name: displayAttributeName(item.name) })) ?? []
  const productRemoteItems = [...remoteItems.filter(item => !optionExternalIds.has(item.externalId))].sort((left, right) => Number(right.isRequired) - Number(left.isRequired) || left.name.localeCompare(right.name, 'tr'))
  const requiredItems = productRemoteItems.filter(item => item.isRequired === true)
  const mappedRequired = requiredItems.filter(item => productMappingByExternal.has(item.externalId)).length
  const productSync = useMutation({ mutationFn: () => hubApi(`/connections/${connectionId}/product-sync-jobs`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: '{}' }), onSuccess: () => { onNotice('Ürünler salt-okunur seçenek eşitleme kuyruğuna alındı; Trendyol’a yazma yapılmaz.') }, onError: reason => onNotice(reason instanceof Error ? reason.message : 'Ürün seçenekleri eşitlenemedi.') })
  if (!connectionId) return <article className="panel mapping-step"><div className="editor-section-title"><span>4</span><div><h2>Trendyol kategori ve özellik eşlemeleri</h2><p>Önce 1. adımdaki kategori eşlemesini tamamlayın.</p></div></div><div className="unknown"><strong>Bağlantı seçilmedi</strong><p>Önce aktif Trendyol bağlantısını seçin.</p></div></article>
  if (categoryMappings.isLoading) return <article className="panel mapping-step"><div className="editor-section-title"><span>4</span><div><h2>Trendyol kategori ve özellik eşlemeleri</h2><p>1. adımdaki eşleşmiş kategoriler hazırlanıyor.</p></div></div><Busy text="Kategori eşlemeleri yükleniyor…" /></article>
  if (!matchedCategories.length) return <article className="panel mapping-step"><div className="editor-section-title"><span>4</span><div><h2>Trendyol kategori ve özellik eşlemeleri</h2><p>Yalnız 1. adımda eşleşen kategorilerin özellikleri burada açılır.</p></div></div><div className="unknown"><strong>Kategori eşlemesi bekleniyor</strong><p>Önce 1. adımda en az bir panel kategorisini Trendyol kategorisiyle eşleştirin.</p></div></article>
  if (!categoryMapping.data) return <article className="panel mapping-step"><div className="editor-section-title"><span>4</span><div><h2>Trendyol kategori ve özellik eşlemeleri</h2><p>Ürün özellikleri ve seçenekleri kendi adımlarında tanımladıktan sonra değer karşılıklarını eşleyin.</p></div></div><div className="unknown"><strong>Kategori eşlemesi bekleniyor</strong><p>Önce 1. adımda panel kategorisini Trendyol kategorisiyle eşleştirin.</p></div></article>
  return <article className="panel mapping-step"><div className="editor-section-title"><span>4</span><div><h2>Kategori özellikleri</h2><p>Trendyol kategori alanlarını 2. adımdaki ürün özellikleri ve 3. adımdaki varyant seçenekleriyle eşleyin.</p></div></div><label className="matched-category-select">Eşleşmiş panel kategorisi<select value={attributeCategoryId} onChange={event => { setAttributeCategoryId(event.target.value); setSection('attributes') }}>{matchedCategories.map(({ category }) => <option key={category.id} value={category.id}>{category.path}</option>)}</select></label>{references.isLoading || mappings.isLoading || categoryRequirements.isLoading ? <Busy text="Kategori özellikleri ve eşlemeler yükleniyor…" /> : references.isError || mappings.isError || categoryRequirements.isError ? <div className="unknown"><strong>Güncel kategori özellik verisi alınamadı</strong><p>Seçili Trendyol kategorisinin özellik listesini yeniden eşitleyin.</p><button type="button" disabled={sync.isPending} onClick={() => sync.mutate()}>{sync.isPending ? 'Kuyruğa alınıyor…' : 'Kategori özelliklerini eşitle'}</button></div> : <><div className={`mapping-progress ${mappedRequired === requiredItems.length ? 'complete' : ''}`}><strong>{mappedRequired}/{requiredItems.length} ürün özelliği zorunlusu eşlendi</strong><span>{remoteItems.length} toplam alan · {productMappings.length} ürün özelliği · {optionMappings.length}/{optionAttributes.length} seçenek eşlemesi · Trendyol kategori no: {categoryScope}</span></div><div className="mapping-section-toolbar"><div className="mapping-section-tabs" role="tablist" aria-label="Kategori eşleme bölümleri"><button type="button" role="tab" aria-selected={section === 'attributes'} className={section === 'attributes' ? 'active' : ''} onClick={() => setSection('attributes')}>Ürün özellikleri <small>{productRemoteItems.length}</small></button><button type="button" role="tab" aria-selected={section === 'options'} className={section === 'options' ? 'active' : ''} onClick={() => setSection('options')}>Seçenek Eşitleme <small>{optionAttributes.length}/2</small></button></div><div className="button-row"><button type="button" className="secondary" disabled={sync.isPending} onClick={() => sync.mutate()}>{sync.isPending ? 'Alanlar yenileniyor…' : 'Kategori alanlarını yenile'}</button>{section === 'options' && optionAttributes.length > 0 && <button type="button" disabled={productSync.isPending} onClick={() => productSync.mutate()}>{productSync.isPending ? 'Ürünler eşitleniyor…' : 'Ürün seçeneklerini salt-okunur eşitle'}</button>}</div></div>{section === 'attributes' ? <div className="embedded-attribute-grid">{productRemoteItems.map(item => <CategoryAttributeCard key={item.externalId} connectionId={connectionId} categoryScope={categoryScope} snapshotId={references.data!.snapshotId} localAttributes={productAttributes} remoteAttribute={item} existingMapping={productMappingByExternal.get(item.externalId) ?? null} usedLocalIds={usedProductLocalIds} onNotice={onNotice} />)}</div> : <>{optionAttributes.length ? <div className="embedded-attribute-grid option-mapping-grid">{optionAttributes.map(attribute => <OptionMappingCard key={attribute.id} connectionId={connectionId} categoryScope={categoryScope} snapshotId={references.data!.snapshotId} localAttribute={attribute} remoteAttributes={remoteItems.filter(item => !productMappingByExternal.has(item.externalId))} existingMapping={mappingByLocal.get(attribute.id) ?? null} onNotice={onNotice} />)}</div> : <div className="unknown"><strong>Seçenek eşleme tanımlı değil</strong><p>3. adımda en fazla iki seçenek başlığı oluşturun veya ürün özelliklerinden taşıyın.</p></div>}</>}</>}</article>
}

function OptionMappingCard({ connectionId, categoryScope, snapshotId, localAttribute, remoteAttributes, existingMapping, onNotice }: { connectionId: string; categoryScope: string; snapshotId: string; localAttribute: LocalAttribute; remoteAttributes: ReferenceItem[]; existingMapping: CatalogMapping | null; onNotice: (value: string) => void }) {
  const client = useQueryClient(); const [externalId, setExternalId] = useState(existingMapping?.externalId ?? ''); const [showValues, setShowValues] = useState(false)
  useEffect(() => { setExternalId(existingMapping?.externalId ?? ''); setShowValues(false) }, [existingMapping?.externalId])
  const selectedRemote = remoteAttributes.find(item => item.externalId === externalId)
  async function save() {
    try {
      if (!externalId) throw new Error('Trendyol seçenek karşılığını seçin.')
      await hubApi<CatalogMapping>(`/mappings/attributes/${localAttribute.id}`, { method: 'PUT', headers: existingMapping ? { 'If-Match': `"v${existingMapping.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId, scopeExternalId: categoryScope, externalId, status: 'VERIFIED', role: 'OPTION' }) })
      onNotice(`${localAttribute.name} seçenek eşlemesi kaydedildi.`); await client.invalidateQueries({ queryKey: ['attribute-mappings', connectionId, categoryScope] })
    } catch (reason) { onNotice(reason instanceof Error ? reason.message : 'Seçenek eşlemesi kaydedilemedi.') }
  }
  async function remove() {
    if (!existingMapping) return
    try { await hubApi<boolean>(`/mappings/attributes/${localAttribute.id}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}`, { method: 'DELETE', headers: { 'If-Match': `"v${existingMapping.version}"` } }); setExternalId(''); onNotice(`${localAttribute.name} seçenek eşlemesi kaldırıldı.`); await client.invalidateQueries({ queryKey: ['attribute-mappings', connectionId, categoryScope] }) } catch (reason) { onNotice(reason instanceof Error ? reason.message : 'Seçenek eşlemesi kaldırılamadı.') }
  }
  const mapped = !!existingMapping && existingMapping.externalId === externalId
  return <section className={`attribute-mapping-card option-mapping-card ${mapped ? '' : 'unmapped'}`}><div className="attribute-mapping-card-head"><span className="attr-source">OPT</span><div><strong>Panel: {localAttribute.name}</strong><small>{mapped ? `Trendyol: ${selectedRemote?.name ?? existingMapping?.externalId} · eşlendi` : 'Varyant üreten panel seçeneği'}</small></div></div><div className="mapping-fields compact"><label>Trendyol seçenek/özellik karşılığı<select aria-label={`${localAttribute.name} Trendyol seçenek karşılığı`} value={externalId} onChange={event => setExternalId(event.target.value)}><option value="">Karşılık seçin</option>{remoteAttributes.map(item => <option key={item.externalId} value={item.externalId}>{item.name}{item.isRequired ? ' · zorunlu' : ''}</option>)}</select></label>{mapped ? <button type="button" className="secondary" onClick={() => void remove()}>Eşlemeyi kaldır</button> : <button type="button" onClick={() => void save()} disabled={!externalId}>Kaydet</button>}</div>{mapped && localAttribute.values.length > 0 && <><button type="button" className="value-mapping-toggle" onClick={() => setShowValues(value => !value)}>{showValues ? 'Seçenek değerlerini gizle' : `Seçenek değerlerini eşle (${localAttribute.values.length})`}</button>{showValues && selectedRemote && <AttributeValueMappingEditor connectionId={connectionId} categoryScope={categoryScope} attribute={localAttribute} externalAttributeId={selectedRemote.externalId} />}</>}</section>
}

function CategoryAttributeCard({ connectionId, categoryScope, snapshotId, localAttributes, remoteAttribute, existingMapping, usedLocalIds, onNotice }: { connectionId: string; categoryScope: string; snapshotId: string; localAttributes: LocalAttribute[]; remoteAttribute: ReferenceItem; existingMapping: CatalogMapping | null; usedLocalIds: Set<string>; onNotice: (value: string) => void }) {
  const client = useQueryClient(); const [localId, setLocalId] = useState(existingMapping?.localId ?? ''); const [showValues, setShowValues] = useState(false)
  useEffect(() => { setLocalId(existingMapping?.localId ?? '') }, [existingMapping?.localId])
  const selectedAttribute = localAttributes.find(item => item.id === localId)
  const selectableAttributes = localAttributes.filter(item => item.id === localId || !usedLocalIds.has(item.id))
  async function save() {
    try {
      if (!localId) throw new Error('Entegrasyon ürün özelliğini seçin.')
       await hubApi<CatalogMapping>(`/mappings/attributes/${localId}`, { method: 'PUT', body: JSON.stringify({ connectionId, snapshotId, scopeExternalId: categoryScope, externalId: remoteAttribute.externalId, status: 'VERIFIED', role: 'ATTRIBUTE' }) })
      onNotice(`${remoteAttribute.name} eşlemesi kaydedildi.`)
      await client.invalidateQueries({ queryKey: ['attribute-mappings', connectionId, categoryScope] })
    } catch (reason) { onNotice(reason instanceof Error ? reason.message : 'Özellik eşlemesi kaydedilemedi.') }
  }
  async function remove() {
    if (!existingMapping) return
    try {
      await hubApi<boolean>(`/mappings/attributes/${existingMapping.localId}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}`, { method: 'DELETE', headers: { 'If-Match': `"v${existingMapping.version}"` } })
      setShowValues(false); onNotice(`${remoteAttribute.name} eşlemesi kaldırıldı.`)
      await client.invalidateQueries({ queryKey: ['attribute-mappings', connectionId, categoryScope] })
    } catch (reason) { onNotice(reason instanceof Error ? reason.message : 'Özellik eşlemesi kaldırılamadı.') }
  }
  const mapped = existingMapping?.externalId === remoteAttribute.externalId
  const required = remoteAttribute.isRequired === true
  return <section className={`attribute-mapping-card ${required ? 'required' : ''} ${required && !mapped ? 'unmapped' : ''}`}><div className="attribute-mapping-card-head"><span className="attr-source">TY</span><div><strong>{remoteAttribute.name}</strong><small>{required ? 'Zorunlu alan' : ''}{mapped ? ' · eşlendi' : ''}</small></div></div><div className="mapping-fields compact"><label>Entegrasyon ürün özelliği<select value={localId} onChange={event => setLocalId(event.target.value)}><option value="">Özellik seçin</option>{selectableAttributes.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>{mapped ? <button type="button" className="secondary" onClick={() => void remove()}>Eşlemeyi kaldır</button> : <button type="button" onClick={() => void save()} disabled={!localId}>Kaydet</button>}</div>{selectedAttribute && mapped && selectedAttribute.values.length > 0 && <><button type="button" className="value-mapping-toggle" onClick={() => setShowValues(value => !value)}>{showValues ? 'Değer eşlemelerini gizle' : `Değer eşlemelerini aç (${selectedAttribute.values.length})`}</button>{showValues && <AttributeValueMappingEditor connectionId={connectionId} categoryScope={categoryScope} attribute={selectedAttribute} externalAttributeId={remoteAttribute.externalId} />}</>}</section>
}

export function AttributeMappingPage() {
  const client = useQueryClient(); const [connectionId, setConnectionId] = useState(''); const [categoryId, setCategoryId] = useState(''); const [localId, setLocalId] = useState(''); const [externalId, setExternalId] = useState(''); const [notice, setNotice] = useState('')
  const connections = useQuery({ queryKey: ['connections', 'attribute-mapping'], queryFn: () => loadAllPages<Connection>('/connections') })
  const categories = useQuery({ queryKey: ['categories', 'attribute-mapping'], queryFn: () => loadAllPages<LocalCategory>('/catalog/categories') })
  const localAttributes = useQuery({ queryKey: ['attributes', 'mapping'], queryFn: () => loadAllPages<LocalAttribute>('/catalog/attributes') })
  const categoryMapping = useQuery({ queryKey: ['category-mapping', categoryId, connectionId], queryFn: () => hubApi<CatalogMapping | null>(`/mappings/categories/${categoryId}?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!categoryId && !!connectionId, retry: false })
  const categoryScope = categoryMapping.data?.externalId ?? ''
  const references = useQuery({ queryKey: ['reference-attributes', connectionId, categoryScope], queryFn: () => hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId && !!categoryScope, retry: false })
  const mapping = useQuery({ queryKey: ['attribute-mapping', localId, connectionId, categoryScope], queryFn: () => hubApi<CatalogMapping | null>(`/mappings/attributes/${localId}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}`), enabled: !!localId && !!connectionId && !!categoryScope, retry: false })
  const sync = useMutation({ mutationFn: () => hubApi(`/connections/${connectionId}/reference-sync-jobs?resourceType=CATEGORY_ATTRIBUTES&parentExternalId=${encodeURIComponent(categoryScope)}`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: '{}' }), onSuccess: () => setNotice('Kategori özellikleri salt-okunur eşitleme kuyruğuna alındı.'), onError: reason => setNotice(reason instanceof Error ? reason.message : 'Özellik eşitleme başlatılamadı.') })
  const save = useMutation({ mutationFn: () => {
    if (!references.data || !localId || !externalId) throw new Error('Bağlantı, kategori, panel özelliği ve Trendyol özelliği zorunludur.')
     return hubApi<CatalogMapping>(`/mappings/attributes/${localId}`, { method: 'PUT', headers: mapping.data ? { 'If-Match': `"v${mapping.data.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId: references.data.snapshotId, scopeExternalId: categoryScope, externalId, status: 'VERIFIED', role: 'ATTRIBUTE' }) })
  }, onSuccess: async value => { setNotice('Özellik eşlemesi doğrulandı ve kategori kapsamında kaydedildi.'); setExternalId(value.externalId); await client.invalidateQueries({ queryKey: ['attribute-mapping', localId, connectionId, categoryScope] }) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Özellik eşlemesi kaydedilemedi.') })
  useEffect(() => { if (mapping.data) setExternalId(mapping.data.externalId) }, [mapping.data])
  const activeConnections = connections.data?.items.filter(item => item.platformCode === 'TRENDYOL' && (item.status === 'ACTIVE' || item.status === 'VERIFIED')) ?? []; const localLeaves = categories.data?.items.filter(item => item.isActive && item.isLeaf) ?? []; const activeAttributes = localAttributes.data?.items.filter(item => item.isActive) ?? []; const remoteAttributes = references.data?.items.filter(item => item.isActive) ?? []
  return <section className="content f3 mapping-page"><div className="page-heading"><div><p className="eyebrow">Eşleştirme ayarları</p><h1>Özellik eşlemeleri</h1><p className="lede">Yerel özellikleri güncel Trendyol V2 kategori özelliği snapshot’ına kategori kapsamında eşleyin.</p></div><Badge value="SAFE READ" /></div>{notice && <div role="status" className="notice">{notice}</div>}
    <div className="button-row"><Link className="button-link secondary" to="/mappings/categories">Kategoriler</Link><Link className="button-link secondary" to="/mappings/categories?view=brands">Markalar</Link><Link className="button-link" to="/mappings/attributes">Özellikler</Link></div>
    <article className="panel mapping-step"><div className="editor-section-title"><span>1</span><div><h2>Kategori kapsamını seçin</h2><p>Özellik kimlikleri seçili ve doğrulanmış Trendyol yaprak kategorisi kapsamında tutulur.</p></div></div><div className="mapping-fields"><label>Özellik için aktif Trendyol bağlantısı<select aria-label="Özellik için aktif Trendyol bağlantısı" value={connectionId} onChange={event => { setConnectionId(event.target.value); setCategoryId(''); setLocalId(''); setExternalId(''); setNotice('') }}><option value="">Bağlantı seçin</option>{activeConnections.map(item => <option value={item.id} key={item.id}>{item.displayName} · {item.externalStoreId}</option>)}</select></label><label>Panel yaprak kategorisi<select aria-label="Özellik kapsamı panel kategorisi" value={categoryId} onChange={event => { setCategoryId(event.target.value); setLocalId(''); setExternalId(''); setNotice('') }} disabled={!connectionId}><option value="">Kategori seçin</option>{localLeaves.map(item => <option value={item.id} key={item.id}>{item.path}</option>)}</select></label></div>
      {categoryId && !categoryMapping.isLoading && !categoryMapping.data ? <div className="unknown"><strong>Kategori eşlemesi gerekli</strong><p>Özellikleri almadan önce panel kategorisini Trendyol yaprak kategorisiyle eşleyin.</p><Link className="button-link" to="/mappings/categories">Kategori eşlemelerine git</Link></div> : categoryScope && references.isError ? <div className="unknown"><strong>Güncel özellik snapshot’ı yok</strong><p>Seçili kategori için yalnız salt-okunur özellik listesini eşitleyin.</p><button disabled={sync.isPending} onClick={() => sync.mutate()}>{sync.isPending ? 'Kuyruğa alınıyor…' : 'Kategori özelliklerini eşitle'}</button></div> : null}</article>
    {references.data && <article className="panel mapping-step"><div className="editor-section-title"><span>2</span><div><h2>Özelliği eşleyin</h2><p>Bu işlem yalnız yerel mapping kaydı oluşturur; Trendyol’a yazmaz.</p></div></div><div className="mapping-fields"><label>Panel özelliği<select aria-label="Panel özelliği" value={localId} onChange={event => { setLocalId(event.target.value); setExternalId(''); setNotice('') }}><option value="">Özellik seçin</option>{activeAttributes.map(item => <option value={item.id} key={item.id}>{item.name} · {item.dataType}</option>)}</select></label><label>Trendyol kategori özelliği<select aria-label="Trendyol kategori özelliği" value={externalId} onChange={event => setExternalId(event.target.value)} disabled={!localId || mapping.isLoading}><option value="">Özellik seçin</option>{remoteAttributes.map(item => <option value={item.externalId} key={item.externalId}>{item.name}{item.isRequired ? ' · zorunlu' : ''}{item.allowsCustomValue ? ' · serbest değer' : ''}</option>)}</select></label></div><div className="mapping-action"><span>{remoteAttributes.length.toLocaleString('tr-TR')} özellik · kategori {categoryScope}{mapping.data ? ` · mevcut eşleme v${mapping.data.version}` : ''}</span><button disabled={!localId || !externalId || save.isPending || mapping.isLoading} onClick={() => save.mutate()}>{save.isPending ? 'Kaydediliyor…' : mapping.data ? 'Eşlemeyi güncelle' : 'Eşlemeyi doğrula ve kaydet'}</button></div>{mapping.data && localId && <AttributeValueMappingEditor connectionId={connectionId} categoryScope={categoryScope} attribute={activeAttributes.find(item => item.id === localId)!} externalAttributeId={mapping.data.externalId} />}</article>}
  </section>
}

function AttributeValueMappingEditor({ connectionId, categoryScope, attribute, externalAttributeId }: { connectionId: string; categoryScope: string; attribute: LocalAttribute; externalAttributeId: string }) {
  const client = useQueryClient(); const [notice, setNotice] = useState(''); const [selections, setSelections] = useState<Record<string, string>>({}); const [saving, setSaving] = useState(false); const valueScope = `${categoryScope}/${externalAttributeId}`
  const references = useQuery({ queryKey: ['reference-attribute-values', connectionId, valueScope], queryFn: () => hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(externalAttributeId)}/values?connectionId=${encodeURIComponent(connectionId)}`), retry: false })
  const mappings = useQuery({ queryKey: ['attribute-value-mappings', connectionId, valueScope], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(valueScope)}`), retry: false })
  const sync = useMutation({ mutationFn: () => hubApi(`/connections/${connectionId}/reference-sync-jobs?resourceType=ATTRIBUTE_VALUES&parentExternalId=${encodeURIComponent(valueScope)}`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: '{}' }), onSuccess: () => setNotice('Özellik değerleri salt-okunur eşitleme kuyruğuna alındı.'), onError: reason => setNotice(reason instanceof Error ? reason.message : 'Değer eşitleme başlatılamadı.') })
  const localValues = attribute.values?.filter(item => item.isActive) ?? []; const remoteValues = references.data?.items.filter(item => item.isActive) ?? []; const mappingByLocal = new Map((mappings.data ?? []).map(item => [item.localId, item]))
  useEffect(() => { if (mappings.data) setSelections(Object.fromEntries(mappings.data.map(item => [item.localId, item.externalId]))) }, [mappings.data])
  async function saveAll() {
    if (!references.data) return
    const chosen = Object.values(selections).filter(Boolean)
    if (new Set(chosen).size !== chosen.length) return setNotice('Aynı Trendyol değeri birden fazla panel değerine eşlenemez.')
    setSaving(true); setNotice('')
    try {
      var changed = 0
      for (const localValue of localValues) {
        const externalId = selections[localValue.id] ?? ''
        const existing = mappingByLocal.get(localValue.id)
        if (!externalId && existing) {
          await hubApi<boolean>(`/mappings/attribute-values/${localValue.id}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(valueScope)}`, { method: 'DELETE', headers: { 'If-Match': `"v${existing.version}"` } })
          changed++
          continue
        }
        if (!externalId || existing?.externalId === externalId) continue
        await hubApi<CatalogMapping>(`/mappings/attribute-values/${localValue.id}`, { method: 'PUT', headers: existing ? { 'If-Match': `"v${existing.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId: references.data.snapshotId, scopeExternalId: valueScope, externalId, status: 'VERIFIED' }) })
        changed++
      }
      await client.invalidateQueries({ queryKey: ['attribute-value-mappings', connectionId, valueScope] })
      setNotice(changed ? `${changed} değer eşlemesi kaydedildi.` : 'Değer eşlemelerinde değişiklik yok.')
    } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Değer eşlemeleri kaydedilemedi.') } finally { setSaving(false) }
  }
  if (!localValues.length) return <div className="unknown"><strong>Serbest değer özelliği</strong><p>Bu yerel özellik seçim listesi taşımıyor; Trendyol özelliği serbest değer kabul etmiyorsa yayınlama kapısı işlemi reddeder.</p></div>
  if (references.isError) return <div className="unknown"><strong>Güncel özellik değerleri snapshot’ı yok</strong><p>Seçili kategori ve özellik için salt-okunur değer listesini eşitleyin.</p>{notice && <p role="status">{notice}</p>}<button disabled={sync.isPending} onClick={() => sync.mutate()}>{sync.isPending ? 'Kuyruğa alınıyor…' : 'Özellik değerlerini eşitle'}</button></div>
  if (references.isLoading || mappings.isLoading) return <Busy text="Özellik değerleri ve mevcut eşlemeler yükleniyor…" />
  if (mappings.isError) return <ErrorBox error={mappings.error} />
  const used = new Set(Object.values(selections).filter(Boolean))
  return <div className="mapping-step nested value-mapping-editor"><div className="value-mapping-heading"><div><h3>Değer eşleştirmeleri</h3><p>{localValues.length} panel değeri · {remoteValues.length} Trendyol değeri</p></div><button type="button" disabled={saving} onClick={() => void saveAll()}>{saving ? 'Kaydediliyor…' : 'Tüm eşlemeleri kaydet'}</button></div>{notice && <p role="status" className="notice">{notice}</p>}<div className="value-mapping-rows">{localValues.map(localValue => <label key={localValue.id} className="value-mapping-row"><span>{localValue.value}</span><b>→</b><select aria-label={`${localValue.value} Trendyol değeri`} value={selections[localValue.id] ?? ''} onChange={event => setSelections(current => ({ ...current, [localValue.id]: event.target.value }))}><option value="">Trendyol değeri seçin</option>{remoteValues.map(remote => <option key={remote.externalId} value={remote.externalId} disabled={used.has(remote.externalId) && selections[localValue.id] !== remote.externalId}>{remote.name}</option>)}</select><small>{mappingByLocal.has(localValue.id) ? 'Eşlendi' : 'Bekliyor'}</small></label>)}</div></div>
}

export function BrandMappingPage() {
  const client = useQueryClient(); const [connectionId, setConnectionId] = useState(''); const [localId, setLocalId] = useState(''); const [externalId, setExternalId] = useState(''); const [notice, setNotice] = useState(''); const [brandName, setBrandName] = useState(''); const [brandExternalSearch, setBrandExternalSearch] = useState(''); const [brandExternalPickerOpen, setBrandExternalPickerOpen] = useState(false); const [brandEditOpen, setBrandEditOpen] = useState(false); const [editingBrandMappingId, setEditingBrandMappingId] = useState(''); const [brandTransferBusy, setBrandTransferBusy] = useState(false); const [brandTransferBundle, setBrandTransferBundle] = useState<BrandMappingTransferBundle | null>(null); const [brandTransferOpen, setBrandTransferOpen] = useState(false); const [brandTransferSelection, setBrandTransferSelection] = useState({ brands: true, mappings: true })
  const connections = useQuery({ queryKey: ['connections', 'brand-mapping'], queryFn: () => loadAllPages<Connection>('/connections') })
  const localBrands = useQuery({ queryKey: ['brands', 'mapping'], queryFn: () => loadAllPages<LocalBrand>('/catalog/brands') })
  const references = useQuery({ queryKey: ['reference-brands', connectionId], queryFn: () => hubApi<ReferenceData>(`/reference-data/brands?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId, retry: false })
  const mapping = useQuery({ queryKey: ['brand-mapping', localId, connectionId], queryFn: () => hubApi<CatalogMapping | null>(`/mappings/brands/${localId}?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!localId && !!connectionId, retry: false })
  const brandMappings = useQuery({ queryKey: ['brand-mappings', connectionId], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/brands?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId, retry: false })
  const syncBrands = useMutation({
    mutationFn: async () => {
      if (!connectionId) throw new Error('Aktif Trendyol bağlantısı bulunamadı.')
      const requestedAt = Date.now()
      const queued = await hubApi<ReferenceSyncAccepted | string>(`/connections/${connectionId}/reference-sync-jobs?resourceType=BRANDS`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: '{}' })
      let jobId = typeof queued === 'string' ? queued : queued.value ?? queued.id ?? queued.jobId ?? queued.Value ?? queued.Id ?? queued.JobId
      const queuedError = typeof queued === 'string' ? null : queued.error?.message ?? queued.error?.Message ?? queued.Error?.message ?? queued.Error?.Message
      for (let attempt = 0; attempt < 180; attempt++) {
        await wait(attempt === 0 ? 500 : 1000)
        if (!jobId) {
          const jobs = await hubApi<ReferenceSyncJobSummary[]>('/jobs')
          const candidate = jobs.find(item => item.connectionId === connectionId && item.jobType === 'REFERENCE_SYNC' && new Date(item.createdAt).getTime() >= requestedAt - 5000)
          if (candidate) jobId = candidate.id
        }
        if (!jobId) continue
        const job = await hubApi<ReferenceSyncJob>(`/jobs/${jobId}`)
        const status = job.job.status.toUpperCase()
        if (status === 'SUCCEEDED') return hubApi<ReferenceData>(`/reference-data/brands?connectionId=${encodeURIComponent(connectionId)}`)
        if (['BLOCKED', 'DEAD', 'CANCELLED', 'MANUAL_REVIEW'].includes(status)) throw new Error(job.job.lastErrorSummary ?? job.job.lastErrorCode ?? 'Marka listesi güncellenemedi.')
      }
      throw new Error(queuedError ?? 'Marka listesi güncelleniyor. Birkaç saniye sonra yeniden deneyin.')
    },
    onMutate: () => setNotice('Trendyol marka listesi güncelleniyor…'),
    onSuccess: async data => { client.setQueryData(['reference-brands', connectionId], data); setNotice(`${data.items.filter(item => item.isActive).length.toLocaleString('tr-TR')} marka güncel olarak yüklendi.`); await client.invalidateQueries({ queryKey: ['reference-brands', connectionId] }) },
    onError: reason => setNotice(reason instanceof Error ? reason.message : 'Marka listesi güncellenemedi.')
  })
  const save = useMutation({ mutationFn: () => { if (!references.data || !localId || !externalId) throw new Error('Bağlantı, panel markası ve Trendyol markası zorunludur.'); return hubApi<CatalogMapping>(`/mappings/brands/${localId}`, { method: 'PUT', headers: mapping.data ? { 'If-Match': `"v${mapping.data.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId: references.data.snapshotId, externalId, status: 'VERIFIED' }) }) }, onSuccess: async value => { setNotice('Marka eşlemesi doğrulandı ve kaydedildi.'); setExternalId(value.externalId); setBrandEditOpen(false); setEditingBrandMappingId(''); client.setQueryData(['brand-mapping', localId, connectionId], value); client.setQueryData<CatalogMapping[]>(['brand-mappings', connectionId], current => current ? [...current.filter(item => item.localId !== value.localId), value] : [value]); await Promise.all([client.invalidateQueries({ queryKey: ['brand-mapping', localId, connectionId] }), client.invalidateQueries({ queryKey: ['brand-mappings', connectionId] })]) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Eşleme kaydedilemedi.') })
  const createBrand = useMutation({ mutationFn: () => { if (!brandName.trim()) throw new Error('Marka adı zorunludur.'); return hubApi<LocalBrand>('/catalog/brands', { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify({ name: brandName.trim() }) }) }, onSuccess: async brand => { await client.invalidateQueries({ queryKey: ['brands', 'mapping'] }); setLocalId(brand.id); setExternalId(''); setBrandName(''); setNotice(`“${brand.name}” panel markası oluşturuldu ve seçildi.`) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Marka oluşturulamadı.') })
  useEffect(() => { setExternalId(mapping.data?.externalId ?? '') }, [mapping.data])
  const trendyolConnections = connections.data?.items.filter(item => item.platformCode === 'TRENDYOL' && (item.status === 'ACTIVE' || item.status === 'VERIFIED')) ?? []
  useEffect(() => { if (!connectionId && trendyolConnections.length) setConnectionId(trendyolConnections[0].id) }, [connectionId, trendyolConnections])
  const autoSyncAttempted = useRef(new Set<string>())
  useEffect(() => {
    if (!connectionId || references.isLoading || references.data || !references.isError || syncBrands.isPending || autoSyncAttempted.current.has(connectionId)) return
    autoSyncAttempted.current.add(connectionId)
    syncBrands.mutate()
  }, [connectionId, references.data, references.isError, references.isLoading, syncBrands.isPending])
  const activeLocalBrands = localBrands.data?.items.filter(item => item.isActive) ?? []
  const externalBrands = references.data?.items.filter(item => item.isActive) ?? []
  const selectedExternalBrand = externalBrands.find(item => item.externalId === externalId)
  const normalizedBrandExternalSearch = brandExternalSearch.trim().toLocaleLowerCase('tr-TR')
  const matchingExternalBrands = externalBrands.filter(item => !normalizedBrandExternalSearch || `${item.name} ${item.externalId}`.toLocaleLowerCase('tr-TR').includes(normalizedBrandExternalSearch))
  const visibleExternalBrands = [...(selectedExternalBrand ? [selectedExternalBrand] : []), ...matchingExternalBrands.filter(item => item.externalId !== selectedExternalBrand?.externalId)].slice(0, 20)
  const savedBrandMappings = brandMappings.data ?? []
  const localBrandById = new Map(activeLocalBrands.map(item => [item.id, item]))
  const externalBrandById = new Map(externalBrands.map(item => [item.externalId, item]))
  const archiveBrand = useMutation({ mutationFn: (brand: LocalBrand) => hubApi<LocalBrand>(`/catalog/brands/${brand.id}`, { method: 'PATCH', headers: { 'If-Match': `"v${brand.version}"` }, body: JSON.stringify({ name: brand.name, isActive: false }) }), onSuccess: async (_, brand) => { if (localId === brand.id) { setLocalId(''); setExternalId('') }; await client.invalidateQueries({ queryKey: ['brands', 'mapping'] }); setNotice(`“${brand.name}” panel markalarından kaldırıldı.`) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Marka kaldırılamadı.') })
  const removeMapping = useMutation({ mutationFn: (item: CatalogMapping) => hubApi(`/mappings/brands/${item.localId}?connectionId=${encodeURIComponent(connectionId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${item.version}"` } }), onSuccess: async (_, item) => { if (localId === item.localId) setExternalId(''); await Promise.all([client.invalidateQueries({ queryKey: ['brand-mapping', item.localId, connectionId] }), client.invalidateQueries({ queryKey: ['brand-mappings', connectionId] })]); setNotice('Marka eşleştirmesi kaldırıldı.') }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Marka eşleştirmesi kaldırılamadı.') })
  async function exportBrandMappings() {
    if (!connectionId) { setNotice('Aktarım için önce Trendyol bağlantısı seçin.'); return }
    setBrandTransferBusy(true)
    try {
      const bundle: BrandMappingTransferBundle = { format: 'RAVENCIA_BRAND_MAPPING_BUNDLE', version: 1, exportedAt: new Date().toISOString(), brands: activeLocalBrands, mappings: savedBrandMappings }
      const url = URL.createObjectURL(new Blob([JSON.stringify(bundle, null, 2)], { type: 'application/json;charset=utf-8' }))
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = 'ravencia-marka-eslestirme-yedegi.json'
      anchor.rel = 'noopener'
      document.body.appendChild(anchor)
      anchor.click()
      window.setTimeout(() => { anchor.remove(); URL.revokeObjectURL(url) }, 1000)
      setNotice(`${bundle.brands.length} marka ve ${bundle.mappings.length} marka eşleştirmesi dışa aktarıldı.`)
    } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Marka eşleştirme aktarımı oluşturulamadı.') } finally { setBrandTransferBusy(false) }
  }
  async function importBrandMappings() {
    if (!brandTransferBundle || !connectionId) return
    if (!Object.values(brandTransferSelection).some(Boolean)) { setNotice('En az bir marka aktarım alanı seçin.'); return }
    if (brandTransferSelection.mappings && !references.data) { setNotice('Marka eşlemelerini aktarmak için güncel Trendyol marka listesini eşitleyin.'); return }
    setBrandTransferBusy(true)
    try {
      const allLocalBrands = localBrands.data?.items ?? []
      const brandIdMap = new Map<string, string>()
      let created = 0
      let mapped = 0
      let skipped = 0
      for (const source of brandTransferBundle.brands) {
        const normalizedName = source.name.trim().toLocaleLowerCase('tr-TR')
        const existing = allLocalBrands.find(item => item.id === source.id || item.name.trim().toLocaleLowerCase('tr-TR') === normalizedName)
        let target = existing
        if (brandTransferSelection.brands) {
          if (target && !target.isActive) {
            target = await hubApi<LocalBrand>(`/catalog/brands/${target.id}`, { method: 'PATCH', headers: { 'If-Match': `"v${target.version}"` }, body: JSON.stringify({ name: target.name, isActive: true }) })
          } else if (!target) {
            target = await hubApi<LocalBrand>('/catalog/brands', { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify({ name: source.name.trim() }) })
            created++
          }
        }
        if (target) brandIdMap.set(source.id, target.id)
      }
      if (brandTransferSelection.mappings) {
        const externalIds = new Set(externalBrands.map(brand => brand.externalId))
        for (const item of brandTransferBundle.mappings) {
          const targetLocalId = brandIdMap.get(item.localId) ?? allLocalBrands.find(brand => brand.id === item.localId)?.id
          if (!targetLocalId || !externalIds.has(item.externalId)) { skipped++; continue }
          await hubApi<CatalogMapping>(`/mappings/brands/${targetLocalId}`, { method: 'PUT', body: JSON.stringify({ connectionId, snapshotId: references.data?.snapshotId, externalId: item.externalId, status: 'VERIFIED' }) })
          mapped++
        }
      }
      setBrandTransferOpen(false); setBrandTransferBundle(null); setNotice(`${created} marka ve ${mapped} marka eşleştirmesi içe aktarıldı.${skipped ? ` ${skipped} eşleştirme güncel referansta bulunamadığı için atlandı.` : ''}`); await Promise.all([client.invalidateQueries({ queryKey: ['brands', 'mapping'] }), client.invalidateQueries({ queryKey: ['brand-mapping', localId, connectionId] }), client.invalidateQueries({ queryKey: ['brand-mappings', connectionId] })])
    } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Marka eşleştirme aktarımı uygulanamadı.') } finally { setBrandTransferBusy(false) }
  }
  function readBrandMappingBundle(event: ChangeEvent<HTMLInputElement>) {
    const file = event.currentTarget.files?.[0]
    event.currentTarget.value = ''
    if (!file) return
    void file.text().then(text => {
      const parsed = JSON.parse(text) as Partial<BrandMappingTransferBundle>
      if (parsed.format !== 'RAVENCIA_BRAND_MAPPING_BUNDLE' || parsed.version !== 1 || !Array.isArray(parsed.brands) || !Array.isArray(parsed.mappings)) throw new Error('Geçerli bir Ravencia marka eşleştirme yedeği seçin.')
      setBrandTransferSelection({ brands: true, mappings: true })
      setBrandTransferBundle({ format: 'RAVENCIA_BRAND_MAPPING_BUNDLE', version: 1, exportedAt: parsed.exportedAt ?? new Date().toISOString(), brands: parsed.brands, mappings: parsed.mappings })
      setBrandTransferOpen(true)
    }).catch(reason => setNotice(reason instanceof Error ? reason.message : 'Marka eşleştirme yedeği okunamadı.'))
  }
  return <section className="content f3 mapping-page mapping-workspace stitch-reference-mapping"><header className="mapping-reference-heading"><div><h1>Eşleştirme Ayarları</h1><p>Lokal markalarınızı pazar yeri markalarıyla eşleştirin.</p></div><div className="mapping-reference-heading-actions" aria-label="Marka eşleştirme yedeği işlemleri"><span>Marka yedeği</span><div className="mapping-transfer-actions"><button type="button" className="secondary" title="Marka eşleştirme yedeğini JSON olarak dışa aktar" onClick={() => void exportBrandMappings()} disabled={brandTransferBusy || !connectionId}>{brandTransferBusy ? 'Hazırlanıyor…' : 'Dışa aktar'}</button><label className="mapping-transfer-upload" title="JSON marka eşleştirme yedeğini içe aktar">İçe aktar<input type="file" accept="application/json,.json" onChange={readBrandMappingBundle} disabled={brandTransferBusy} /></label></div></div></header><MappingViewTabs active="brand" />{notice && <div role="status" className="notice">{notice}</div>}
    <article className="mapping-reference-card brand-mapping-console">
      <h2><span aria-hidden="true">⌘</span>1. Marka Eşleme</h2><p className="brand-mapping-intro">Panel markalarınızı Trendyol’un güncel marka referanslarıyla eşleyin. Değişiklikler yalnız yerel eşleme kaydına yazılır.</p>
      <section className="brand-library-panel"><div className="brand-library-heading"><div><strong>Panel markaları</strong><small>Mevcut markayı seçin veya yeni bir marka oluşturun.</small></div><span>{activeLocalBrands.length} kayıt</span></div><form className="platform-category-create brand-library-create" onSubmit={event => { event.preventDefault(); createBrand.mutate() }}><label>Yeni panel markası<input aria-label="Yeni panel markası adı" maxLength={160} value={brandName} onChange={event => setBrandName(event.target.value)} placeholder="Örn. Ravencia" required /></label><button disabled={createBrand.isPending}>{createBrand.isPending ? 'Ekleniyor…' : '+ Marka ekle'}</button></form><div className="category-chip-list brand-library-list" aria-label="Eklenen panel markaları">{activeLocalBrands.length ? activeLocalBrands.map(brand => <span key={brand.id} className={brand.id === localId ? 'active' : ''}><button type="button" onClick={() => { setLocalId(brand.id); setExternalId(''); setBrandExternalSearch(''); setBrandExternalPickerOpen(false); setNotice('') }}>{brand.name}</button><button type="button" className="category-chip-remove" aria-label={`${brand.name} markasını kaldır`} onClick={() => archiveBrand.mutate(brand)} disabled={archiveBrand.isPending}>×</button></span>) : <small>Henüz panel markası eklenmedi.</small>}</div></section>
      <div className="mapping-pair-grid brand-mapping-pair-grid"><section className="mapping-side local"><div className="mapping-side-head"><span>Panel kataloğu</span><small>Ürünlerinizde kullandığınız yerel markayı seçin.</small></div><SearchableSelect label="Panel markası" value={localId} options={activeLocalBrands.map(item => ({ value: item.id, label: item.name, description: 'Panel markası' }))} placeholder="Panel markalarında ara" disabled={!connectionId} onChange={value => { setLocalId(value); setExternalId(''); setBrandExternalSearch(''); setBrandExternalPickerOpen(false); setNotice('') }} /></section><div className="mapping-link-rail" aria-hidden="true"><span>⇄</span><small>Eşleştir</small></div><section className="mapping-side remote"><div className="mapping-side-head"><div className="mapping-side-head-copy"><span>Hedef Pazar Yeri &amp; Marka</span><small>Bağlantıyı seçin, güncel marka referansından karşılığını belirleyin.</small></div><button type="button" className="mapping-reference-refresh" aria-label="Trendyol marka listesini güncelle" title="Marka listesini güncelle" disabled={!connectionId || syncBrands.isPending} onClick={() => syncBrands.mutate()}><span aria-hidden="true">{syncBrands.isPending ? '…' : '↻'}</span></button></div><div className="mapping-target-controls brand-mapping-target-controls"><select aria-label="Hedef pazaryeri bağlantısı" value={connectionId} onChange={event => { setConnectionId(event.target.value); setLocalId(''); setExternalId(''); setBrandExternalSearch(''); setBrandExternalPickerOpen(false); setNotice('') }}><option value="">Trendyol bağlantısı</option>{trendyolConnections.map(item => <option value={item.id} key={item.id}>Trendyol{trendyolConnections.length > 1 ? ` · ${item.displayName}` : ''}</option>)}</select><div className="platform-category-picker-wrap"><button type="button" className="panel-category-picker-trigger" aria-expanded={brandExternalPickerOpen} onClick={() => setBrandExternalPickerOpen(value => !value)} disabled={!connectionId || !localId || references.isLoading || references.isError || syncBrands.isPending}><span>{selectedExternalBrand?.name ?? (references.isLoading || syncBrands.isPending ? 'Markalar yükleniyor…' : 'Platform markası seçin...')}</span><i aria-hidden="true">⌄</i></button>{brandExternalPickerOpen && <div className="panel-category-picker platform-category-picker brand-reference-picker" role="dialog" aria-label="Platform markası seçimi"><div><input autoFocus aria-label="Platform markalarında ara" value={brandExternalSearch} onChange={event => setBrandExternalSearch(event.target.value)} placeholder="Marka adı veya marka no ara..." /><button type="button" aria-label="Platform marka menüsünü kapat" onClick={() => setBrandExternalPickerOpen(false)}>×</button></div><div role="listbox">{visibleExternalBrands.length ? visibleExternalBrands.map(item => <button type="button" role="option" aria-selected={item.externalId === externalId} key={item.externalId} onClick={() => { setExternalId(item.externalId); setBrandExternalPickerOpen(false) }}><span>{item.name}</span><small>Marka no: {item.externalId}</small></button>) : <span>{references.isError ? 'Marka listesi alınamadı. Yenileme düğmesini kullanın.' : 'Aramayla eşleşen marka bulunamadı.'}</span>}</div></div>}</div></div></section></div>
      {connections.isError || localBrands.isError ? <ErrorBox error={connections.error ?? localBrands.error} /> : connectionId ? <div className={`mapping-action brand-reference-action${references.isError ? ' has-error' : ''}`}><span>{references.isLoading || syncBrands.isPending ? 'Trendyol marka listesi getiriliyor…' : references.data ? <><strong>{externalBrands.length.toLocaleString('tr-TR')}</strong> marka · {new Date(references.data.fetchedAt).toLocaleString('tr-TR')}{mapping.data ? ` · Kayıt v${mapping.data.version}` : ''}</> : 'Marka listesi alınamadı. Yenileme düğmesini kullanın.'}</span>{references.data && <button type="button" disabled={!localId || !externalId || save.isPending || mapping.isLoading} onClick={() => save.mutate()}>{save.isPending ? 'Kaydediliyor…' : mapping.data ? 'Eşleştirmeyi güncelle' : 'Eşleştirmeyi kaydet'}</button>}</div> : null}
    </article>
    {connectionId && <article className="mapping-reference-card mapping-saved-card brand-mapping-saved-card"><header><h2>2. Kayıtlı Marka Eşleştirmeleri</h2><div><span className="mapping-record-count">{savedBrandMappings.length.toLocaleString('tr-TR')} kayıt</span></div></header><div className="mapping-saved-table"><table><thead><tr><th>Panel Markası</th><th>Platform</th><th>Trendyol Markası</th><th>Durum</th><th>İşlemler</th></tr></thead><tbody>{brandMappings.isLoading ? <tr><td colSpan={5}>Kayıtlı marka eşleştirmeleri yükleniyor…</td></tr> : brandMappings.isError ? <tr><td colSpan={5}>Kayıtlı marka eşleştirmeleri alınamadı.</td></tr> : savedBrandMappings.length ? savedBrandMappings.map(item => <tr key={item.id} className={editingBrandMappingId === item.id ? 'is-editing' : ''}><td>{localBrandById.get(item.localId)?.name ?? item.localId}</td><td><span className="trendyol-badge">Trendyol</span></td><td className="brand-mapping-inline-editor">{editingBrandMappingId === item.id ? <SearchableSelect label="" value={externalId} options={externalBrands.map(brand => ({ value: brand.externalId, label: brand.name, description: `Trendyol marka no: ${brand.externalId}` }))} placeholder="Trendyol markası ara" disabled={references.isLoading || references.isError} onChange={setExternalId} /> : externalBrandById.get(item.externalId)?.name ?? item.externalId}</td><td><span className={`mapping-table-status ${item.status === 'VERIFIED' || item.status === 'ACTIVE' ? 'active' : 'error'}`}><i />{item.status === 'VERIFIED' || item.status === 'ACTIVE' ? 'Aktif' : item.status}</span></td><td><div className="mapping-row-actions">{editingBrandMappingId === item.id ? <><button type="button" className="mapping-icon-button" aria-label="Marka eşleştirmesini kaydet" disabled={!externalId || save.isPending} onClick={() => save.mutate()}><span aria-hidden="true">✓</span></button><button type="button" className="mapping-icon-button" aria-label="Marka düzenlemeyi iptal et" onClick={() => { setEditingBrandMappingId(''); setExternalId(item.externalId) }}>×</button></> : <><button type="button" className="mapping-icon-button" aria-label="Marka eşleştirmesini düzenle" onClick={() => { setLocalId(item.localId); setExternalId(item.externalId); setEditingBrandMappingId(item.id); setBrandEditOpen(false); setNotice('') }}><span aria-hidden="true">✎</span></button><button type="button" aria-label="Eşleştirmeyi sil" disabled={removeMapping.isPending} onClick={() => { if (window.confirm('Bu marka eşleştirmesi kaldırılsın mı?')) removeMapping.mutate(item) }}>♜</button></>}</div></td></tr>) : <tr><td colSpan={5}>Henüz kaydedilmiş marka eşleştirmesi bulunmuyor.</td></tr>}</tbody></table></div><footer><span>Toplam {savedBrandMappings.length.toLocaleString('tr-TR')} eşleştirme</span></footer></article>}
    {brandTransferOpen && brandTransferBundle && <div className="workspace-modal-backdrop mapping-transfer-backdrop" role="presentation" onMouseDown={() => { if (!brandTransferBusy) { setBrandTransferOpen(false); setBrandTransferBundle(null) } }}><section className="workspace-modal mapping-transfer-modal" role="dialog" aria-modal="true" aria-labelledby="brand-transfer-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">MARKA YEDEĞİ</p><h2 id="brand-transfer-title">İçe aktarma alanlarını seçin</h2><p>{brandTransferBundle.brands.length} marka · {brandTransferBundle.mappings.length} eşleştirme</p></div><button type="button" className="modal-close" onClick={() => { setBrandTransferOpen(false); setBrandTransferBundle(null) }} disabled={brandTransferBusy} aria-label="Marka içe aktarmayı kapat">×</button></header><div className="mapping-transfer-options"><label><input type="checkbox" checked={brandTransferSelection.brands} onChange={event => setBrandTransferSelection(current => ({ ...current, brands: event.target.checked }))} /><span><strong>Panel markaları</strong><small>Eksik markaları ekler, pasif markaları yeniden etkinleştirir.</small></span></label><label><input type="checkbox" checked={brandTransferSelection.mappings} onChange={event => setBrandTransferSelection(current => ({ ...current, mappings: event.target.checked }))} /><span><strong>Marka eşleştirmeleri</strong><small>Mevcut Trendyol marka referanslarıyla eşleştirmeleri günceller.</small></span></label></div><p className="mapping-transfer-warning">Mevcut kayıtlar silinmez; aynı marka adları güncellenir, eksik olanlar eklenir. Güncel Trendyol marka referansında bulunmayan eşleştirmeler atlanır.</p><footer><button type="button" className="secondary" onClick={() => { setBrandTransferOpen(false); setBrandTransferBundle(null) }} disabled={brandTransferBusy}>Vazgeç</button><button type="button" onClick={() => void importBrandMappings()} disabled={brandTransferBusy}>{brandTransferBusy ? 'İçe aktarılıyor…' : 'Seçilenleri güncelle'}</button></footer></section></div>}
    {brandEditOpen && <div className="brand-mapping-modal-backdrop" role="presentation" onMouseDown={() => setBrandEditOpen(false)}><section className="brand-mapping-modal" role="dialog" aria-modal="true" aria-labelledby="brand-mapping-edit-title" onMouseDown={event => event.stopPropagation()}><header><div><small>Marka eşleme</small><h2 id="brand-mapping-edit-title">Marka eşleştirmesini düzenle</h2><p>Panel markası için Trendyol karşılığını güncelleyin.</p></div><button type="button" className="modal-close" aria-label="Marka eşleme penceresini kapat" onClick={() => setBrandEditOpen(false)}>×</button></header><div className="brand-mapping-modal-body"><div className="brand-mapping-selected-local"><small>Panel markası</small><strong>{localBrandById.get(localId)?.name ?? localId}</strong></div><SearchableSelect label="Trendyol markası" value={externalId} options={externalBrands.map(item => ({ value: item.externalId, label: item.name, description: `Trendyol marka no: ${item.externalId}` }))} placeholder={references.isLoading ? 'Markalar yükleniyor…' : 'Marka adı veya marka no ara'} disabled={references.isLoading || references.isError} onChange={setExternalId} /></div><footer><button type="button" className="secondary" onClick={() => setBrandEditOpen(false)}>Vazgeç</button><button type="button" disabled={!localId || !externalId || save.isPending || mapping.isLoading} onClick={() => save.mutate()}>{save.isPending ? 'Kaydediliyor…' : 'Değişikliği kaydet'}</button></footer></section></div>}
  </section>
}
