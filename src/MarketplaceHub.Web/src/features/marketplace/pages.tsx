import { marketplaceProductSearchUrl } from '../questions/marketplace-product-link'
import { Fragment, useEffect, useLayoutEffect, useMemo, useRef, useState, type ChangeEvent, type CSSProperties, type FormEvent, type KeyboardEvent, type MouseEvent, type ReactNode } from 'react'
import { createPortal, flushSync } from 'react-dom'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router'
import { keepPreviousData, useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, loadAllPages } from '../../shared/api'
import { Busy, cargoCarrier, cargoCarriers, cargoLabel, cargoMatches, ErrorBox, Modal, Pagination, Tabs, UiIcon } from '../../shared/components'
import { MessageToast, NoticeToast } from '../../shared/components/NoticeToast'
import { invoiceStatusDisplayLabel, mappingStatusLabel, statusLabel, statusTone } from '../../shared/status-labels'
import { platformLogoClass, platformLogoSource } from '../../shared/platform-logos'
import { DateRangePicker } from '../../shared/DateRangePicker'
import { MobileActionMenu } from '../../shared/MobileActionMenu'
import './reference-filters.css'
import './value-mapping-modal.css'
import './mapping-workspace.css'
import './category-workbench.css'
import './n11-price-stock-send.css'
import './order-invoice-cell.css'
import './orders-responsive.css'
import './marketplace-polish.css'
import './integrations-sade.css'
import './integration-settings-sade.css'
import { PanelValueMultiSelect } from './PanelValueMultiSelect'
import { matchesReturnReferenceFilters } from './return-reference-filters'
import { returnReasonPresentation } from './return-reference-presentation'
import { RrAnchor, RrBadge, RrError, RrFilterCard, RrIcon, RrImagePreview, RrLoading, RrLogo, RrModal, RrRing, RrThumb, RrWordmark, titleTr, type RingTone } from './returns-ui'
import './orders-sade.css'
import { externalWriteEvidenceState, externalWriteGroupState } from './external-write-group-state'
import { useScrollLock } from '../../shared/hooks/useScrollLock'
import { appendNotification, integrationNotificationTarget, isExternalWriteMessage, normalizeNotificationKind, type NotificationDetail, type NotificationTarget } from '../../shared/notifications'
import { resolveReturnStatus } from '../../shared/dashboard-operational-links'
import { sortByTurkishName } from './attribute-sorting'
import { orderStatusTabs, resolveOrderStatusTab } from './order-status-tabs'
import { onHoldOrderStatusText, overdueShipmentDays } from './order-delivery-presentation'
import { code128Bars, loadPrintedShippingLabels, markShippingLabelPrinted, printedShippingLabelKey, shippingLabelFields, useShippingLabelSettings, type ShippingLabelBlock, type ShippingLabelField, type ShippingLabelFormat, type ShippingLabelSettings } from '../shipping'
import { formatPanelColorValue, usesCustomPanelColorValue } from './color-value-format'
import { AttributeDefaultsPanel } from './AttributeDefaultsPanel'
import { MappingConflictsPanel } from './MappingConflictsPanel'
import { autoMatchScore, canonicalKey, valueMatchScore } from '../../shared/value-matching'
import { attributeValueMappingNeedsSave, isTemplateFilledRow, suggestValueMappings, type ValueMappingSuggestion, visiblePanelValues, normalizeReferenceValueLabel, planDirectReferenceValues, planMissingReferencePanelValues, planReferencePanelMappings, togglePanelValueReferenceSelection, updatePanelValueReferenceSelection, valueMappingRowClassName } from './attribute-value-mapping'
import { isReferenceSyncJobTerminal, shouldAutoSyncHepsiburadaValues } from './reference-value-sync'
import { activeMappingConnectionsForPlatform, mappingPlatformDefinitions, mappingPlatformLabel, type MappingPlatformCode } from './mapping-platforms'
import { resolveAttributeMappingRole } from './mapping-attribute-role'
import { suggestCategoryMappings } from './category-suggestions'
import { isActiveMarketplaceConnection, isMarketplacePlatformSelected, marketplacePlatformLabel, marketplacePlatformOptions, supportsSyncPolicy, supportsSyncPolicyManagement, syncPolicyIntervalChoices } from './marketplace-platform-support'
import { syncPolicyHealthPresentation } from './sync-policy-health'
import { SyncPolicyStalledAlert } from './sync-policy-stalled-alert'
import { connectionDataResetGroups, connectionDataResetOptions, type ConnectionDataResetScope } from './connection-data-reset'
import { activeReturnSyncConnections, enqueueReturnSyncs } from './return-sync'
import { ReturnApproveDialog, type ReturnApproveRow } from './ReturnApproveDialog'
import { ReturnBulkDecisionDialog, type BulkDecisionItem } from './ReturnBulkDecisionDialog'
import { isActionableReturnLine, splitMixedReturnClaims, visibleReturnClaims } from './return-claim-visibility'
import { canPrintReturnLabel } from './return-label-visibility'
import { shopifyStoreDisplayName } from './shopify-store-name'
import { shouldLoadConnectionCapabilities } from './connection-capability-query'
import { formatMarketplaceBarcode, shouldShowReturnCountdown, sortApprovedReturns, type ApprovedReturnSort } from './return-reference-presentation'
import { productImageFallbackUrls as createProductImageFallbackUrls } from './product-image-lookups'
import { visibleOrderLineVariantOptions } from './order-line-variant-options'
import { invoiceProviderForEnvironment } from '../invoicing/invoice-creation-availability'
import { InvoiceWorkspacePreviewModal, type InvoicePreviewTarget } from '../invoicing/InvoiceWorkspacePreviewModal'
import { ViewTabsWithMore, viewTabLabel } from '../../shared/view-tabs'
type Page<T> = { items: T[]; nextCursor: string | null; hasMore: boolean; totalCount?: number | null }
type Connection = { id: string; publicId: string; platformCode: string; environment: string; displayName: string; externalStoreId: string; status: string; apiVersion: string; lastTestedAt: string | null; lastSuccessAt: string | null; lastErrorCode: string | null; hasCredential: boolean; externalWritesEnabled: boolean; invoiceCreationEnabled: boolean; n11ShipmentTemplate?: string | null; n11PreparingDay?: number | null; version: number }
type Capability = { code: string; supportLevel: string; environment: string; storeScope: string; apiVersion: string; sourceUrl: string | null; requiredScope?: string | null; evidenceNote?: string | null; verifiedAt: string | null; verifiedForConnection: boolean }
type SyncPolicy = { id: string; resourceType: string; intervalSeconds: number; overlapSeconds: number; jitterSeconds: number; enabled: boolean; version: number; lastSuccessAt: string | null; lastModifiedWatermark: string | null; lastCursorAdvancedAt?: string | null; cursorStagnantSince?: string | null; cursorProgressStatus?: string; healthStatus?: string; recoveryGapStatus?: string; recoveryGapDays?: number | null; lastAttemptAt?: string | null; lastDurationMs?: number | null; consecutiveFailureCount?: number; lastRequestCount?: number; lastReceivedCount?: number; lastChangedCount?: number; lastInsertedCount?: number; lastUpdatedCount?: number; lastSkippedCount?: number; lastFailedCount?: number; lastRetryCount?: number; lastRateLimitCount?: number; connectionBacklogCount?: number; connectionOldestBacklogAt?: string | null; connectionManualReviewCount?: number; connectionDeadJobCount24h?: number; requiresExternalWrites?: boolean }
type OrderLine = { id: string; sku: string; barcode: string | null; title: string; quantity?: number; orderedQuantity: number; cancelledQuantity: number; shippedQuantity: number; allocatedQuantity?: number; deliveredQuantity: number; returnedQuantity: number; unitPrice: number; vatRate: number; rawStatus: string; variantId: string | null; modelCode: string | null; optionSignature: string | null; imageUrl: string | null }
type OrderInfoItem = { label: string; value: string; url?: string | null }
type Order = { id: string; orderNumber: string; derivedStatus: string; currency: string; grossAmount: number; discountAmount: number; netAmount: number; orderedAt: string; lineCount: number; packageCount: number; version: number; connectionId: string | null; platformCode: string; platformDisplayName: string; customerName: string; customerEmail: string | null; customerTaxOrIdentityNumber: string | null; orderType: string; isMicroExport: boolean; shipmentAddressJson: string; invoiceAddressJson: string; shipmentDueAt: string | null; isDeadlineCritical: boolean; invoiceStatus: string; invoiceId: string | null; invoiceDocumentUrl: string | null; cargoProviderName: string | null; cargoTrackingNumber: string | null; primaryImageUrl: string | null; productQuantity: number; lines: OrderLine[] | null; packages: Shipment[] | null; invoiceCreationEnabled?: boolean; platformInfo?: OrderInfoItem[] | null }
const invoiceCreationDisabledHelp = 'Fatura oluşturmak için Entegrasyonlar > bu bağlantı ayarlarından “Fatura oluşturma” seçeneğini açın.'
type ProductImagePreview = { url: string; fallbackUrl?: string | null; fallbackUrls?: string[]; title: string; modelCode?: string | null; variant?: string | null }
type OrderSummary = { all: number; new: number; processing: number; shipped: number; delivered: number; resent: number; onHold: number; cancelled: number; returned: number; returnInTransit: number; partiallyCancelled: number; manualReview: number; pending: number; unverified: number; paymentPending: number }
type OrderSort = 'DATE_DESC' | 'DATE_ASC' | 'DUE_DESC' | 'DUE_ASC'
type OrderFilters = { search: string; status: string; platform: string; platforms: string[]; listing: string; cargo: string; invoice: string; invoiceType: string; invoiceRegion: string; label: string; sort: OrderSort; dateFrom: string; dateTo: string }
type InvoiceViewer = { id: string; orderNumber: string; invoiceType: string; status: string; currency: string; payableTotal: number; taxTotal: number; invoiceNumber: string | null; lines: Array<{ id: string; description: string; sku: string | null; unit: string; quantity: number; vatRate: number; lineTotal: number }>; documents: Array<{ id: string; documentType: string; sha256: string }> }
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
  if (filters.platforms.length) params.set('platforms', filters.platforms.join(','))
  if (filters.listing !== 'ALL') params.set('listing', filters.listing)
  if (filters.cargo !== 'ALL') params.set('cargo', filters.cargo)
  if (filters.invoice !== 'ALL') params.set('invoice', filters.invoice)
  if (filters.invoiceType !== 'ALL') params.set('invoiceType', filters.invoiceType)
  if (filters.invoiceRegion !== 'ALL') params.set('invoiceRegion', filters.invoiceRegion)
  if (filters.dateFrom) params.set('dateFrom', turkeyDayBoundary(filters.dateFrom))
  if (filters.dateTo) params.set('dateTo', turkeyDayBoundary(filters.dateTo, true))
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
function turkeyDayBoundary(date: string, endOfDay = false) {
  return `${date}T${endOfDay ? '23:59:59.999' : '00:00:00.000'}+03:00`
}
const invoiceCreationStatuses = new Set(['FATURA_BEKLIYOR', 'FATURA_REDDEDILDI', 'FATURA_ISLENIYOR'])
function isInvoicePending(item: Pick<Order, 'invoiceStatus'>) { return invoiceCreationStatuses.has(item.invoiceStatus) }
function isMicroExportLabelBlocked(item: Pick<Order, 'isMicroExport' | 'invoiceStatus'>) { return item.isMicroExport && isInvoicePending(item) }
function ProductImagePreviewModal({ preview, onClose }: { preview: ProductImagePreview; onClose: () => void }) {
  const sources = [...new Set([preview.url, ...(preview.fallbackUrls ?? []), preview.fallbackUrl].filter((value): value is string => Boolean(value)))]
  const variant = visibleOrderLineVariantOptions(preview.variant ?? null).map(option => `${option.label}: ${option.value}`).join(' · ')
  return <RrImagePreview title={preview.title} crumb={[preview.modelCode ? `Model Kodu: ${preview.modelCode}` : '', variant].filter(Boolean).join(' · ')} sources={sources} onClose={onClose} />
}
function MappingDeleteIcon() {
  return <UiIcon name="close" className="mapping-delete-icon" />
}
function ProductTitleLink({ platformCode, line }: { platformCode: string; line: { title: string; barcode?: string | null; sku?: string | null; modelCode?: string | null } }) {
  const href = marketplaceProductSearchUrl(platformCode, line.barcode, line.sku, line.modelCode, line.title)
  return href ? <a className="product-title-link" href={href} target="_blank" rel="noopener noreferrer" onClick={event => event.stopPropagation()}>{line.title}</a> : <>{line.title}</>
}
function productLineQuantity(line: { quantity?: number; orderedQuantity?: number; cancelledQuantity?: number }) {
  const orderedQuantity = line.orderedQuantity ?? line.quantity ?? 0
  return Math.max(0, orderedQuantity - (line.cancelledQuantity ?? 0))
}
type OrderDetail = { id: string; orderNumber: string; derivedStatus: string; currency: string; grossAmount: number; discountAmount: number; netAmount: number; orderedAt: string; connectionId: string | null; platformCode: string; platformDisplayName: string; customerName: string; customerEmail: string | null; customerPhone: string | null; customerTaxOrIdentityNumber: string | null; orderType: string; isMicroExport: boolean; isEInvoiceAvailable: boolean | null; shipmentAddressJson: string; invoiceAddressJson: string; shipmentDueAt: string | null; invoiceStatus: string; invoiceDocumentUrl: string | null; lines: OrderLine[]; packages: Shipment[]; version: number }
type Shipment = { id: string; orderId: string; orderNumber: string; externalPackageId: string; status: string; rawStatus: string; cargoTrackingNumber: string | null; cargoProviderName: string | null; statusOccurredAt: string; version: number; isResend: boolean }
type ShipmentDetail = { package: Shipment; allowedActions: string[]; supportedLabelFormats: string[]; isStageConnection: boolean; platformCode: string; documents: Array<{ id: string; documentKind: string; format: string; source: string; documentVersion: number; createdAt: string; expiresAt: string | null }> }
type RemoteCargoCompany = { shortName: string; name: string }
type ReturnClaim = { id: string; externalClaimId: string; orderNumber: string; status: string; rawStatus: string; reasonText: string | null; reasonCode?: string | null; actionDueAt: string | null; approvedAt?: string | null; systemNote?: string | null; sellerDescription?: string | null; platformApprovalReason?: string | null; platformDescription?: string | null; version: number; customerName: string; orderedAt: string | null; orderAmount: number; currency: string; cargoProviderName: string | null; cargoTrackingNumber: string | null; cargoTrackingLink?: string | null; orderCargoProviderName?: string | null; orderCargoTrackingNumber?: string | null; primaryImageUrl: string | null; productCount: number; primaryBarcode: string | null; lines: OrderLine[] | null; packageNumber: string | null; invoiceStatus: string; grossAmount: number; discountAmount: number; isMicroExport: boolean; connectionId: string | null; platformCode: string; platformDisplayName: string; lineStatuses?: Record<string, string> | null; rowKey?: string }
type ReturnFilters = { status: string; search: string; from: string; to: string }
async function loadAllReturns(filters: ReturnFilters): Promise<Page<ReturnClaim>> {
  const params = new URLSearchParams()
  if (filters.status !== 'ALL') params.set('status', filters.status)
  if (filters.search.trim()) params.set('search', filters.search.trim())
  if (filters.from) params.set('dateFrom', turkeyDayBoundary(filters.from))
  if (filters.to) params.set('dateTo', turkeyDayBoundary(filters.to, true))
  return loadAllPages<ReturnClaim>(`/returns?${params.toString()}`)
}
function matchesReturnReferenceSearch(item: ReturnClaim, value: string) {
  const term = value.trim().toLocaleLowerCase('tr-TR')
  if (!term) return true
  return [item.customerName, item.orderNumber, item.externalClaimId, item.primaryBarcode, item.reasonText, item.reasonCode, ...(item.lines ?? []).flatMap(line => [line.sku, line.barcode, line.title])]
    .some(candidate => candidate?.toLocaleLowerCase('tr-TR').includes(term))
}
type ReturnLine = { status?: string | null; id: string; externalLineId: string; orderLineId: string; sku: string; barcode: string | null; title: string; quantity: number; disposedQuantity: number; remainingQuantity: number; unitPrice: number; imageUrl: string | null; hasInventoryMapping: boolean; optionSignature?: string | null; modelCode?: string | null; color?: string | null; size?: string | null; approvedAt?: string | null; systemNote?: string | null; reasonText?: string | null; sellerDescription?: string | null; platformApprovalReason?: string | null; platformDescription?: string | null }; type ReturnDetail = Omit<ReturnClaim, 'lines'> & { reasonCode: string | null; allowedActions: string[]; lines: ReturnLine[] | null; stockDispositionAvailable: boolean; externalWritesEnabled: boolean; decisionPending?: boolean }; type ReturnIssueReason = { id: string; name: string; evidenceRequired: boolean }
type LocalCategory = { id: string; name: string; path: string; depth: number; isLeaf: boolean; isActive: boolean; version: number }
type LocalBrand = { id: string; name: string; isActive: boolean; version: number }
type LocalAttribute = { id: string; code: string; name: string; dataType: string; isActive: boolean; version: number; roles?: string[] | null; values: { id: string; value: string; sortOrder: number; isActive: boolean }[] }
type CategoryRequirementView = { attributeId: string; isRequired: boolean; allowsCustomValue: boolean; displayOrder: number; role: 'ATTRIBUTE' | 'OPTION'; attribute: LocalAttribute }
type AttributeRequirementCommand = { attributeId: string; isRequired: boolean; allowsCustomValue: boolean; displayOrder: number; role: 'ATTRIBUTE' | 'OPTION' }
type ReferenceItem = { externalId: string; parentExternalId: string | null; name: string; path: string; depth: number; isLeaf: boolean; isActive: boolean; isRequired: boolean | null; allowsCustomValue: boolean | null; allowsMultipleValues: boolean | null }
type ReferenceData = { snapshotId: string; resourceType: string; fetchedAt: string; items: ReferenceItem[] }
type CatalogMapping = { id: string; connectionId: string; snapshotId: string; localId: string; scopeExternalId: string; externalId: string; status: string; verifiedAt: string | null; version: number; missingRequiredAttributeCount?: number | null; source?: string | null }
type MappingTransferBundle = { format: 'RAVENCIA_MAPPING_BUNDLE'; version: 1; exportedAt: string; categories: LocalCategory[]; attributes: LocalAttribute[]; categoryMappings: CatalogMapping[]; attributeMappings: CatalogMapping[]; attributeValueMappings: CatalogMapping[] }
type BrandMappingTransferBundle = { format: 'RAVENCIA_BRAND_MAPPING_BUNDLE'; version: 1; exportedAt: string; brands: LocalBrand[]; mappings: CatalogMapping[] }
type MappingTransferScope = 'categories' | 'options' | 'attributes' | 'mappings'
type SearchOption = { value: string; label: string; description?: string }
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
  return <label className={`searchable-select${open ? ' open' : ''}${disabled ? ' disabled' : ''}`}><span>{label}</span><button type="button" className="searchable-select-trigger" aria-expanded={open} aria-haspopup="listbox" disabled={disabled} onClick={toggleMenu}><span>{selected?.label ?? placeholder}</span><UiIcon name="chevronDown" /></button>{open && <div id={listId} className="searchable-select-menu" role="listbox"><div className="searchable-select-search"><UiIcon name="search" /><input autoFocus role="combobox" aria-label={`${label} ara`} aria-controls={listId} aria-expanded={open} aria-autocomplete="list" value={query} placeholder={placeholder} onKeyDown={keyDown} onChange={event => { setQuery(event.target.value); if (value) onChange('') }} onBlur={() => window.setTimeout(() => setOpen(false), 120)} /></div><div className="searchable-select-options">{filtered.length ? filtered.map(option => <button type="button" role="option" aria-selected={option.value === value} key={option.value} onMouseDown={event => { event.preventDefault(); choose(option) }}><span>{option.label}</span>{option.description && <small>{option.description}</small>}</button>) : <span className="searchable-select-empty">Eşleşen kayıt bulunamadı.</span>}</div></div>}</label>
}
function MappingViewTabs({ active }: { active: 'category' | 'brand' | 'attribute' }) {
  return <nav className="mapping-view-tabs rv-tabs rv-view-tabs" aria-label="Eşleştirme türü" role="tablist"><Link role="tab" aria-selected={active === 'category'} className={active === 'category' ? 'active is-active' : ''} to="/mappings/categories"><span className="rv-tab-label">{viewTabLabel('category', 'Kategori Eşleme')}</span></Link><Link role="tab" aria-selected={active === 'brand'} className={active === 'brand' ? 'active is-active' : ''} to="/mappings/categories?view=brands"><span className="rv-tab-label">{viewTabLabel('brand', 'Marka Eşleme')}</span></Link></nav>
}
function idempotency() { return crypto.randomUUID() }
function wait(milliseconds: number) { return new Promise(resolve => window.setTimeout(resolve, milliseconds)) }
function displayAttributeName(value: string) { return value.replace(/\[(?:A-)?TDG\][\s_-]*/gi, '').replace(/^\(?(?:A-)?TDG\)?[\s_-]*/gi, '').replace(/_/g, ' ').replace(/\s+/g, ' ').trim() }
function normalizedAttributeKey(value: string) { return value.replace(/[\s_-]+/g, '').toLocaleUpperCase('tr-TR') }
function isWebColorAttributeName(value: string) { return ['WEBCOLOR', 'WEBCOLOUR', 'WEBRENK'].includes(normalizedAttributeKey(value)) }
function isPanelColorAttributeName(value: string) { return ['RENK', 'COLOR', 'COLOUR'].includes(normalizedAttributeKey(value)) }
function isVariantOptionAttributeName(value: string) { return isPanelColorAttributeName(value) || isWebColorAttributeName(value) || ['BEDEN', 'SIZE'].includes(normalizedAttributeKey(value)) }
function isSizeAttributeName(value: string) {
  const key = normalizedAttributeKey(value).replace(/[^0-9A-Z]/g, '')
  return key === 'BEDEN' || key === 'SIZE' || key.endsWith('BEDEN') || key.endsWith('SIZE')
}
function isSizeAttribute(attribute: Pick<LocalAttribute, 'name' | 'code'>) { return isSizeAttributeName(attribute.name) || isSizeAttributeName(attribute.code) }
type IntegrationFeedback = { kind: 'success' | 'error' | 'info'; message: string; details?: NotificationDetail[]; target?: NotificationTarget | null }
type IntegrationFeedbackHandler = (message: string, kind: IntegrationFeedback['kind'] | 'pending') => void
function IntegrationFeedbackToast({ feedback, onClose }: { feedback: IntegrationFeedback | null; onClose: () => void }) {
  useEffect(() => {
    if (feedback) appendNotification(feedback.message, feedback.kind, feedback.target, feedback.details)
  }, [feedback])
  if (!feedback) return null
  return <NoticeToast key={`${feedback.kind}:${feedback.message}`} kind={feedback.kind} message={feedback.message} onClose={onClose} durationMs={5000} />
}
function Empty({ children }: { children: string }) { return <div className="empty"><span className="empty-icon" aria-hidden="true"><UiIcon name="box" /></span><strong>Kayıt yok</strong><p>{children}</p></div> }
function Badge({ value, tooltip }: { value: string; tooltip?: string }) { const normalized = value.trim().toUpperCase(); return <span className={`badge ${statusTone(value)} status-${normalized.toLowerCase()}`} title={tooltip ?? value}><i aria-hidden="true" />{statusLabel(value)}</span> }
function DateText({ value }: { value: string | null }) { return <>{value ? new Date(value).toLocaleString('tr-TR') : '—'}</> }

function usableShipmentDueAt(value: string | null) { if (!value) return null; const timestamp = new Date(value).getTime(); return Number.isFinite(timestamp) && timestamp >= Date.UTC(2000, 0, 1) ? value : null }
function isUrgentReturnDeadline(value: string | null) { const usable = usableShipmentDueAt(value); return usable !== null && new Date(usable).getTime() - Date.now() <= 24 * 60 * 60 * 1000 }
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
function printReturnLabel(item: ReturnClaim, lines: OrderLine[]) {
  const barcodeValue = item.cargoTrackingNumber?.trim() || item.packageNumber?.trim() || item.externalClaimId
  const bars = code128Bars(barcodeValue)
  const barcode = bars.map((isBar, index) => isBar ? `<rect x="${index}" y="0" width="1" height="48"/>` : '').join('')
  const escape = (value: string) => value.replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]!)
  const products = lines.map(line => `<li>${escape(line.title)} · ${productLineQuantity(line)} adet${line.sku ? ` · ${escape(line.sku)}` : ''}</li>`).join('')
  const popup = window.open('', '_blank', 'popup,width=760,height=650')
  if (!popup) return
  popup.document.write(`<!doctype html><html lang="tr"><head><meta charset="utf-8"><title>İade etiketi #${escape(item.orderNumber)}</title><style>body{font:14px Arial,sans-serif;color:#111;margin:0;padding:24px}.label{width:100%;max-width:680px;margin:auto;border:2px solid #111;padding:20px;box-sizing:border-box}.head{display:flex;justify-content:space-between;gap:16px;border-bottom:1px solid #999;padding-bottom:12px}.eyebrow{font-size:11px;letter-spacing:1px;text-transform:uppercase;color:#555}h1{font-size:22px;margin:5px 0}.meta{display:grid;grid-template-columns:1fr 1fr;gap:14px;margin:18px 0}.meta div{display:grid;gap:5px}.meta small{color:#555}.barcode{width:100%;height:70px;margin:16px 0}.barcode rect{fill:#111}.number{text-align:center;font-size:18px;font-weight:700;letter-spacing:2px}.products{border-top:1px solid #999;padding-top:12px}.products ul{padding-left:18px;line-height:1.6}@media print{body{padding:0}.label{max-width:none;min-height:250mm;page-break-inside:avoid}}</style></head><body><main class="label"><div class="head"><div><span class="eyebrow">${escape(item.platformDisplayName)} · İade paketi</span><h1>Sipariş #${escape(item.orderNumber)}</h1></div><strong>${escape(statusLabel(item.status))}</strong></div><div class="meta"><div><small>İade kayıt no</small><strong>${escape(item.externalClaimId)}</strong></div><div><small>Kargo firması</small><strong>${escape(item.cargoProviderName || 'Belirtilmemiş')}</strong></div><div><small>Kargo takip no</small><strong>${escape(item.cargoTrackingNumber || 'Henüz iletilmedi')}</strong></div><div><small>Paket no</small><strong>${escape(item.packageNumber || '—')}</strong></div></div><svg class="barcode" viewBox="0 0 ${bars.length} 48" role="img" aria-label="${escape(barcodeValue)}">${barcode}</svg><div class="number">${escape(barcodeValue)}</div><section class="products"><strong>Ürünler</strong><ul>${products || '<li>Ürün ayrıntısı yok</li>'}</ul></section></main></body></html>`)
  popup.document.close()
  popup.focus()
  popup.print()
}
function safeJson(value: string) { try { return JSON.parse(value || '{}') as Record<string, unknown> } catch { return {} } }
function meaningfulText(value: unknown): value is string { return typeof value === 'string' && /[\p{L}\p{N}]/u.test(value) }
function displayOrderNumber(value: string) { const normalized = value.trim().replace(/^#+/, ''); return `#${normalized || '—'}` }
// Some marketplaces (n11) send the street line on its own and the district and
// city as separate fields; the label and address views append the missing ones.
function completeAddressLine(fullAddress: string, data: Record<string, unknown>) { const lowered = fullAddress.toLocaleLowerCase('tr-TR'); const missing = [data.district, data.city].filter(meaningfulText).map(item => item.trim()).filter(item => !lowered.includes(item.toLocaleLowerCase('tr-TR'))); return missing.length ? [fullAddress, missing.join(' / ')].join(' · ') : fullAddress }
function addressText(value: string, platformCode = 'TRENDYOL') { const source = safeJson(value); const data = [source.shipmentAddress, source.invoiceAddress, source.address].find(item => item && typeof item === 'object') as Record<string, unknown> | undefined ?? source; const fullAddress = typeof data.fullAddress === 'string' ? data.fullAddress.trim() : ''; if (meaningfulText(fullAddress)) return completeAddressLine(fullAddress, data); const seen = new Set<string>(); const parts = ['address1', 'address2', 'neighborhood', 'district', 'countyName', 'city', 'province', 'provinceCode', 'country', 'countryCode', 'zip', 'postalCode'].map(key => data[key]).filter(meaningfulText).map(item => item.trim()).filter(item => { const normalized = item.toLocaleLowerCase('tr-TR'); if (seen.has(normalized)) return false; seen.add(normalized); return true }); return parts.length ? parts.join(' · ') : platformCode === 'SHOPIFY' ? 'Shopify adres bilgisi bulunamadı' : 'Trendyol adres bilgisini maskeledi' }
function optionRows(value: string | null) { const parts = (value ?? '').split(/[|,·;]+/).map(item => item.trim()).filter(Boolean); const fields = parts.map(part => { const match = part.match(/^([^:=-]+)\s*[:=-]\s*(.+)$/); return match ? { label: match[1].trim(), value: match[2].trim() } : null }).filter((item): item is { label: string; value: string } => item !== null); return fields.length ? fields : value ? [{ label: 'Seçenek', value }] : [] }
function returnLineField(line: OrderLine | ReturnLine, labels: string[], property?: keyof ReturnLine) { const extra = line as Partial<ReturnLine>; const direct = property ? String(extra[property] ?? '').trim() : ''; if (direct) return direct; const signature = (line as OrderLine & { optionSignature?: string | null }).optionSignature ?? null; return optionRows(signature).find(option => labels.some(label => option.label.toLocaleLowerCase('tr-TR').includes(label.toLocaleLowerCase('tr-TR'))))?.value ?? '—' }
function returnOptionalValue(value: unknown) { const normalized = String(value ?? '').trim(); return normalized && normalized !== '—' ? normalized : null }
function returnLineFieldValue(line: OrderLine | ReturnLine, labels: string[], property?: keyof ReturnLine) { return returnOptionalValue(returnLineField(line, labels, property)) }
function longDate(value: string) { return new Intl.DateTimeFormat('tr-TR', { day: '2-digit', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' }).format(new Date(value)) }
function deliveryText(item: Order) { const shipment = orderDisplayShipment(item); const rawStatuses = [...(item.packages ?? []).map(value => value.rawStatus), ...(item.lines ?? []).map(value => value.rawStatus)]; if (item.derivedStatus === 'CANCELLED') return { label: 'İptal tarihi', value: shipment?.statusOccurredAt ? longDate(shipment.statusOccurredAt) : 'İptal zamanı bekleniyor', note: null, overdue: false }; if (item.derivedStatus === 'DELIVERED') return { label: 'Teslim edildi', value: shipment?.statusOccurredAt ? longDate(shipment.statusOccurredAt) : 'Teslimat zamanı bekleniyor', note: null, overdue: false }; if (item.derivedStatus === 'UNVERIFIED') return { label: 'Sipariş durumu', value: 'Durumu doğrulanmadı', note: 'Paket veya güncel sipariş yaşam döngüsü bilgisi bulunmadığından teslimat durumu doğrulanamadı.', overdue: false }; if (item.derivedStatus === 'PAYMENT_PENDING') return { label: 'Sipariş durumu', value: 'Trendyol ödeme onayı bekleniyor', note: 'Ödeme onaylanana kadar paket işlemleri kullanılamaz.', overdue: false }; if (item.platformCode.trim().toUpperCase() === 'HEPSIBURADA' && item.derivedStatus === 'UNDELIVERED') return { label: 'Sipariş durumu', value: onHoldOrderStatusText(item.platformCode, rawStatuses), note: null, overdue: false }; if (['SHIPPED', 'UNDELIVERED'].includes(item.derivedStatus)) return { label: 'Taşıma durumunda', value: shipment?.statusOccurredAt ? longDate(shipment.statusOccurredAt) : 'Kargo zamanı bekleniyor', note: 'Kargoya teslim edildi', overdue: false }; if (item.derivedStatus === 'ON_HOLD') return { label: 'Sipariş durumu', value: onHoldOrderStatusText(item.platformCode, rawStatuses), note: null, overdue: false }; const dueAt = usableShipmentDueAt(item.shipmentDueAt); const overdueDays = overdueShipmentDays(item.derivedStatus, dueAt); if (overdueDays > 0) return { label: '', value: `Siparişiniz ${overdueDays} gün gecikmiştir!`, note: 'Siparişinizi en kısa sürede kargoya teslim etmelisiniz.', overdue: true }; const urgent = Boolean(dueAt && new Date(dueAt).getTime() - Date.now() <= 86_400_000); return { label: 'Kalan süre', value: dueAt ? remainingText(dueAt) : item.isMicroExport ? 'Trendyol termin bilgisi göndermedi' : 'Termin zamanı bekleniyor', note: null, overdue: urgent } }
function orderDisplayShipment(item: Pick<Order, 'platformCode' | 'packages'>) { const packages = item.packages ?? []; if (item.platformCode !== 'SHOPIFY') return packages[0]; const visiblePackages = packages.filter(shipment => !(shipment.externalPackageId.startsWith('order:') && shipment.externalPackageId.endsWith(':remainder'))); return visiblePackages.find(shipment => Boolean(shipment.cargoTrackingNumber)) ?? visiblePackages.find(shipment => shipment.status !== 'CANCELLED') ?? visiblePackages[0] }
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
  if (detail.isLoading) return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={onClose}><section className="workspace-modal invoice-draft-modal" role="dialog" aria-modal="true" aria-label="Fatura önizlemesi" onMouseDown={event => event.stopPropagation()}><Busy text="Sipariş, paket ve fatura verileri okunuyor…" /></section></div>
  if (detail.isError || !order) return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={onClose}><section className="workspace-modal invoice-draft-modal" role="dialog" aria-modal="true" aria-label="Fatura önizlemesi" onMouseDown={event => event.stopPropagation()}><ErrorBox error={detail.error} onRetry={() => void detail.refetch()} /><button type="button" className="secondary" onClick={onClose}>Kapat</button></section></div>
  if (!packageId || !provider) return <div className="workspace-modal-backdrop" role="presentation" onMouseDown={onClose}><section className="workspace-modal invoice-draft-modal" role="dialog" aria-modal="true" aria-labelledby="invoice-draft-blocked-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="invoice-draft-blocked-title">Fatura önizlemesi açılamadı</h2><p>{displayOrderNumber(order.orderNumber)}</p></div><button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><p className="notice" role="status">{!packageId ? 'Fatura için pazaryeri paket bilgisi henüz yok. Paket eşitlemesinden sonra yeniden deneyin.' : `Sipariş ortamında (${marketplace?.environment ?? 'bilinmiyor'}) etkin ve credential’ı olan E-Faturam bağlantısı bulunamadı.`}</p><footer><button type="button" className="secondary" onClick={onClose}>Kapat</button></footer></section></div>
  const target: InvoicePreviewTarget = { orderId: order.id, packageId, providerConnectionId: provider.id }
  return <InvoiceWorkspacePreviewModal targets={[target]} onClose={onClose} onNotice={(text, kind) => onFeedback(text, kind)} onComplete={() => { void Promise.all([client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-summary'] })]) }} />
}

function InvoiceViewerModal({ item, onClose }: { item: Order; onClose: () => void }) {
  const invoiceId = item.invoiceId
  const detail = useQuery({ queryKey: ['invoice-viewer', invoiceId], queryFn: () => hubApi<InvoiceViewer>(`/invoices/${invoiceId}`), enabled: !!invoiceId })
  const invoice = detail.data
  const money = (value: number) => value.toLocaleString('tr-TR', { style: 'currency', currency: invoice?.currency ?? item.currency })
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
  const [documentRequestKey, setDocumentRequestKey] = useState(() => `manual-invoice-document:${item.id}:${idempotency()}`)
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
      form.append('issuedAt', issuedAt ? new Date(issuedAt).toISOString() : '')
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
      return hubApi<{ jobIds: string[]; queuedPackageCount: number; inspectionOnlyPackageCount: number }>(`/invoice-workspace/manual-documents/${savedDocumentId}/delivery-jobs`, { method: 'POST', headers: { 'Idempotency-Key': `manual-order-invoice-delivery:${savedDocumentId}:${idempotency()}` } })
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
  function changed() { setSavedDocumentId(null); setMessage(''); setDocumentRequestKey(`manual-invoice-document:${item.id}:${idempotency()}`) }
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
      return hubApi('/invoice-workspace/manual-status', { method: 'PUT', headers: { 'Idempotency-Key': `shopify-invoice-status:${packageId}:${status}:${idempotency()}` }, body: JSON.stringify({ packageIds: [packageId], status }) })
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

function CourierChangeModal({ item, items, onClose, onConfirmed }: { item: Order; items?: Order[]; onClose: () => void; onConfirmed: (shipments: Shipment[]) => void }) {
  const targets = (items?.length ? items : [item]).flatMap(order => { const shipment = orderDisplayShipment(order); return shipment ? [{ order, shipment }] : [] })
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

function ShippingLabelModal({ item, settings, format, onClose, onPrinted, inline = false }: { item: Order; settings: ShippingLabelSettings; format?: ShippingLabelFormat; onClose: () => void; onPrinted?: () => void; inline?: boolean }) {
  format = format === 'sticker' ? 'sticker' : settings.defaultFormat
  const shipment = orderDisplayShipment(item)
  const trackingNumber = shipment?.cargoTrackingNumber ?? item.cargoTrackingNumber ?? ''
  const packageNumber = shipment?.externalPackageId ?? '—'
  const address = addressText(item.shipmentAddressJson, item.platformCode)
  const addressLines = address.split(' · ').filter(Boolean)
  const senderLines = settings.senderAddress.split(/\r?\n| · /).map(value => value.trim()).filter(Boolean)
  const style = { '--shipping-label-width': `${settings.stickerWidthMm}mm`, '--shipping-label-height': `${settings.stickerHeightMm}mm`, '--shipping-label-gap': `${settings.sectionGapMm}mm` } as CSSProperties
  const fieldValues: Record<ShippingLabelField, string> = {
    trackingNumber,
    packageNumber,
    orderNumber: displayOrderNumber(item.orderNumber),
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
      return <div className="shipping-label-block-content" style={{ textAlign: block.align }}><LabelBarcode value={fieldValues[barcodeField] || (barcodeField === 'packageNumber' ? item.orderNumber : '')} compact={format === 'sticker'} />{extraFields.map(field => renderField(field))}</div>
    }
    const fields = block.fields.filter(field => settings.showCustomerPhone || field !== 'customerEmail')
    if (block.kind === 'address') return <div className="shipping-label-address-box" style={{ textAlign: block.align }}><strong className="shipping-label-block-title">Teslimat adresi</strong>{renderField('address')}</div>
    if (block.kind === 'orderInfo') return <div className="shipping-label-meta" style={{ textAlign: block.align }}>{block.text && <strong>{block.text}</strong>}{fields.map(field => renderField(field))}</div>
    if (block.kind === 'sender') return <div className="shipping-label-footer" style={{ textAlign: block.align }}>{block.text && <strong>{block.text}</strong>}{fields.map(field => renderField(field))}</div>
    return <div className="shipping-label-custom-content" style={{ textAlign: block.align }}><strong className="shipping-label-block-title">{block.title}</strong>{block.text && <strong>{block.text}</strong>}{fields.map(field => renderField(field))}</div>
  }
  const labelSurface = <article className={`shipping-label-print-surface shipping-label-${format} a4-count-${settings.a4LabelsPerPage}`} style={style}>
    {settings.layout[format].map(block => <div key={block.id} className="shipping-label-positioned-block" style={block.position ? { left: `${block.position.x}%`, top: `${block.position.y}%`, width: `${block.position.width}%`, height: `${block.position.height}%`, fontSize: `${block.fontSize ?? 14}px`, color: '#000', transform: block.rotation ? `rotate(${block.rotation}deg)` : undefined, transformOrigin: 'center' } : { fontSize: `${block.fontSize ?? 14}px`, color: '#000' }}><Fragment>{renderLabelBlock(block)}</Fragment></div>)}
  </article>
  if (inline) return labelSurface
  return <RrModal crumb="Yazdırma Önizlemesi" title={format === 'a4' ? 'A4 Kargo Etiketi' : 'Sticker Kargo Etiketi'} desc={`${displayOrderNumber(item.orderNumber)} · ${cargoLabel(shipment?.cargoProviderName ?? item.cargoProviderName)}`} size="md" layerClass="shipping-label-backdrop" modalClass={`shipping-label-modal format-${format}`} onClose={onClose} foot={<><span className="rr-note">Etiket bilgileri sipariş ve paket kaydından dolduruldu.</span><button type="button" className="rr-btn" onClick={onClose}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => { markShippingLabelPrinted(item.id, format); onPrinted?.(); window.print() }}><RrIcon name="print" />Yazdır</button></>}>
    <div className="shipping-label-preview-wrap rr-label-wrap">{labelSurface}</div>
  </RrModal>
}

function ShippingLabelBatchModal({ items, settings, format, onClose, onPrinted }: { items: Order[]; settings: ShippingLabelSettings; format: ShippingLabelFormat; onClose: () => void; onPrinted: () => void }) {
  function printLabels() {
    items.forEach(item => markShippingLabelPrinted(item.id, format))
    onPrinted()
    window.print()
  }
  return <RrModal crumb="Toplu Yazdırma" title="Kargo Stickerlarını Yazdır" desc={`${items.length} etiket · Takip numarası bulunan seçili paketler`} size="md" layerClass="shipping-label-backdrop" modalClass={`shipping-label-modal shipping-label-batch-modal format-${format}`} onClose={onClose} foot={<><span className="rr-note">{items.length} etiket yazdırılmaya hazır.</span><button type="button" className="rr-btn" onClick={onClose}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={printLabels}><RrIcon name="print" />Yazdır</button></>}>
    <div className="rr-sum rr-screen-only">{items.map(item => { const shipment = orderDisplayShipment(item); return <div key={item.id}><RrLogo code={item.platformCode} /><span>{displayOrderNumber(item.orderNumber)} · {customerDisplayName(item)}<small>{cargoLabel(shipment?.cargoProviderName ?? item.cargoProviderName)} · {shipment?.cargoTrackingNumber ?? item.cargoTrackingNumber ?? '—'}</small></span><RrBadge label={(['a4', 'sticker'] as ShippingLabelFormat[]).some(value => loadPrintedShippingLabels().includes(printedShippingLabelKey(item.id, value))) ? 'Yazdırıldı' : 'Bekliyor'} glyph={undefined} /></div> })}</div>
    <div className="shipping-label-batch-preview rr-print-only">{items.map(item => <ShippingLabelModal key={item.id} item={item} settings={settings} format={format} onClose={onClose} inline />)}</div>
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
        headers: { 'Idempotency-Key': idempotency() },
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
      {syncMode === 'single' ? <div className="rr-dgrid"><label className="rr-fld"><small>Pazar Yeri Sipariş Numarası</small><input className="rr-in" type="text" value={orderNumber} onChange={e => setOrderNumber(e.target.value)} placeholder="Örn. 1014529381 veya Shopify sipariş adı" disabled={isSubmitting} autoFocus /></label>{selectedSyncConnection && supportsPackageLookup && <label className="rr-fld"><small>Paket Numarası (İsteğe Bağlı)</small><input className="rr-in" type="text" value={packageNumber} onChange={e => setPackageNumber(e.target.value)} placeholder={selectedSyncConnection.platformCode === 'TRENDYOL' ? 'Örn. 419428742' : 'Örn. 5435694424'} disabled={isSubmitting} /><em>{selectedSyncConnection.platformCode === 'TRENDYOL' ? 'Sipariş numarası bulunamazsa bu paket numarasıyla salt okunur olarak okunur.' : 'Eski teslimat durumunu salt okunur olarak yeniler.'}</em></label>}</div> : <RrHintCheck checked={fullScan} onChange={setFullScan} label="Erişilebilir tüm siparişleri tara" hint="Kapalıyken yalnız yeni değişiklikler ve güncellemeler alınır." />}
      {errorMsg && <div className="rr-callout rr-danger" role="alert"><RrIcon name="alert" /><span>{errorMsg}</span></div>}
    </form>
  </RrModal>
}


function OrderReferenceRow({ item, selected, onSelect, selectionMode, onEnterSelection, onMenuChange, onInvoiceCreate, onInvoiceDetails, onInvoiceUpload, onCourierChange, onOrderStatusChange, onProcessOrder, onCreatePackage, processing, onPrintLabel, onPreviewImage, labelSettings, printedLabels, noBuyer = false }: { item: Order; selected: boolean; onSelect: (checked: boolean) => void; selectionMode: boolean; onEnterSelection: () => void; openMenu: 'invoice' | 'actions' | null; onMenuChange: (value: 'invoice' | 'actions' | null) => void; onInvoiceCreate: () => void; onInvoiceDetails: () => void; onInvoiceUpload: () => void; onCourierChange: () => void; onOrderStatusChange: () => void; onProcessOrder: () => void; onCreatePackage: () => void; processing?: boolean; onPrintLabel: (format: ShippingLabelFormat) => void; onPreviewImage: (preview: ProductImagePreview) => void; labelSettings: ShippingLabelSettings; printedLabels: Set<string>; noBuyer?: boolean }) {

  const lines = item.lines ?? []
  const shipment = orderDisplayShipment(item)
  const invoiceNeedsAction = isInvoicePending(item)
  const invoiceCreationEnabled = item.invoiceCreationEnabled ?? false
  const invoiceCreationTooltip = invoiceCreationDisabledHelp
  const labelBlockedByInvoice = isMicroExportLabelBlocked(item)
  const [copied, setCopied] = useState(false)
  const [invoiceStatusModalOpen, setInvoiceStatusModalOpen] = useState(false)
  const money = (value: number) => value.toLocaleString('tr-TR', { style: 'currency', currency: item.currency })
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
  const canChangeCargo = item.platformCode !== 'SHOPIFY' && item.platformCode !== 'HEPSIBURADA' && item.platformCode !== 'N11' && !['PAYMENT_PENDING', 'SHIPPED', 'UNDELIVERED', 'DELIVERED', 'RETURNED'].includes(normalizedOrderStatus)
  const enabledLabelFormats: ShippingLabelFormat[] = [
    ...(labelSettings.showA4Button ? ['a4' as const] : []),
    ...(labelSettings.showStickerButton ? ['sticker' as const] : [])
  ]
  async function copyOrderNumber() { try { await navigator.clipboard.writeText(item.orderNumber); setCopied(true); window.setTimeout(() => setCopied(false), 1400) } catch { setCopied(false) } }
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
  const longPressTimer = useRef<number | null>(null)
  const longPressFired = useRef(false)
  const [detailActionsOpen, setDetailActionsOpen] = useState(false)
  useEffect(() => { if (!detailOpen) return; document.body.classList.add('mobile-filters-open'); return () => document.body.classList.remove('mobile-filters-open') }, [detailOpen])
  const packageNoText = shipment?.externalPackageId ?? (isHepsiburadaUnverifiedWithoutPackage ? 'Paket bilgisi doğrulanmadı' : isHepsiburadaOrder ? 'Henüz paketlenmedi' : 'Bekleniyor')
  const deliveryNoText = shipment?.cargoTrackingNumber ?? item.cargoTrackingNumber ?? (isHepsiburadaUnverifiedWithoutPackage ? 'Gönderi bilgisi doğrulanmadı' : isHepsiburadaOrder ? shipment ? 'Kargoya verildiğinde gelir' : 'Paket oluşturulunca gelir' : 'Takip no bekleniyor')
  const closeDetailThen = (run: () => void) => () => { setDetailOpen(false); setDetailActionsOpen(false); run() }
  const detailPrimaryAction = isCancelledOrder ? null : isCargoTrackingStage ? (shipment?.cargoTrackingNumber ? <Link className="rr-btn rr-primary" to={`/shipments/${shipment.id}`}>Kargo Takip</Link> : null) : isCargoLabelStage && shipment ? <>{enabledLabelFormats.map(format => <button type="button" key={format} className="rr-btn rr-primary" disabled={labelBlockedByInvoice} onClick={closeDetailThen(() => onPrintLabel(format))}><RrIcon name="print" />{format === 'a4' ? 'A4' : 'Sticker'} Etiketi Yazdır</button>)}</> : null
  const detailActions: Array<{ key: string; label: string; disabled?: boolean; run: () => void }> = isCancelledOrder ? [] : [
    ...(isShopifyOrder ? [{ key: 'status', label: 'Sipariş Durumunu Değiştir', run: onOrderStatusChange }] : normalizedOrderStatus === 'NEW' ? [{ key: 'process', label: processing ? 'İşleniyor…' : 'İşleme Al', disabled: processing, run: onProcessOrder }] : []),
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
  const facts = (line: OrderLine) => returnLineFacts(item.platformCode, line)
  const lineImage = (line: OrderLine) => line.imageUrl ?? (lines.length === 1 ? item.primaryImageUrl : null)
  const openPreview = (line: OrderLine) => { const imageUrl = lineImage(line); const fallbackUrls = createProductImageFallbackUrls([line.barcode, line.sku], item.connectionId); onPreviewImage({ url: imageUrl ?? fallbackUrls[0]!, fallbackUrls, title: line.title, modelCode: line.modelCode, variant: line.optionSignature }) }
  const trackingNumber = shipment?.cargoTrackingNumber ?? item.cargoTrackingNumber
  const cargoName = shipment?.cargoProviderName ?? item.cargoProviderName
  const cargoIcon = cargoCarrier(cargoName)?.iconUrl
  const labelFormat: ShippingLabelFormat | undefined = enabledLabelFormats.includes(labelSettings.defaultFormat) ? labelSettings.defaultFormat : enabledLabelFormats[0]
  const labelPrinted = labelFormat ? printedLabels.has(printedShippingLabelKey(item.id, labelFormat)) : false
  const canPrintLabels = isCargoLabelStage && Boolean(shipment) && enabledLabelFormats.length > 0
  const invoiceDocumentHref = item.invoiceId ? `/api/v1/invoices/${item.invoiceId}/documents/latest/content` : item.invoiceDocumentUrl
  const invoiceItems: Array<[ 'doc' | 'info' | 'edit' | 'upload' | 'open' | 'close', string, () => void, string?, boolean?, string?] | 'hr'> = invoiceNeedsAction
    ? [['doc', 'Fatura Oluştur', onInvoiceCreate, '', !invoiceCreationEnabled, invoiceCreationTooltip], ['info', 'Fatura Bilgileri', onInvoiceDetails], ...(isShopifyOrder ? [['edit', 'Fatura Durumunu Değiştir', () => setInvoiceStatusModalOpen(true), '', !invoiceCreationEnabled && !item.invoiceId, invoiceCreationTooltip] as ['edit', string, () => void, string, boolean, string]] : []), ['upload', 'Fatura Yükle', onInvoiceUpload, '', !invoiceCreationEnabled && !item.invoiceId, invoiceCreationTooltip]]
    : [...(invoiceDocumentHref ? [['open', 'Faturayı Gör', () => window.open(invoiceDocumentHref, '_blank', 'noopener')] as ['open', string, () => void]] : []), ['info', 'Fatura Bilgileri', onInvoiceDetails], ...(isShopifyOrder ? [['edit', 'Fatura Durumunu Değiştir', () => setInvoiceStatusModalOpen(true), '', !invoiceCreationEnabled && !item.invoiceId, invoiceCreationTooltip] as ['edit', string, () => void, string, boolean, string]] : []), 'hr', ['close', 'Fatura İptal Et', () => undefined, 'rr-danger', true, 'Fatura iptali şimdilik pazar yerinin panelinden yapılır.']]
  const actionItems: Array<['edit' | 'play' | 'box' | 'truck' | 'print' | 'ban', string, () => void, string?, boolean?, string?] | 'hr'> = [
    ...(isShopifyOrder ? [['edit', 'Sipariş Durumunu Değiştir', onOrderStatusChange] as ['edit', string, () => void]] : []),
    normalizedOrderStatus === 'NEW' ? ['play', processing ? 'İşleniyor…' : 'İşleme Al', onProcessOrder, '', processing] : ['play', normalizedOrderStatus === 'DELIVERED' ? 'Teslim Edildi' : 'İşleme Al', () => undefined, '', true, 'Sipariş zaten işleme alınmış.'],
    ...(isHepsiburadaOrder && !['SHIPPED', 'UNDELIVERED', 'DELIVERED', 'RETURNED'].includes(normalizedOrderStatus) ? [['box', 'Paket Oluştur', onCreatePackage] as ['box', string, () => void]] : []),
    ...(canChangeCargo ? [['truck', 'Başka Kargo Firması İle Gönder', onCourierChange] as ['truck', string, () => void]] : []),
    ...(canPrintLabels ? ['hr' as const, ...enabledLabelFormats.map(format => ['print', `${format === 'a4' ? 'A4' : 'Sticker'} Etiketi Yazdır`, () => onPrintLabel(format), '', labelBlockedByInvoice, 'Mikro ihracat faturası kesilmeden kargo etiketi yazdırılamaz.'] as ['print', string, () => void, string, boolean, string])] : []),
    'hr', ['ban', 'İptal Et', () => undefined, 'rr-danger', true, 'Sipariş iptali pazar yerinin panelinden yapılır.'],
  ]
  const menu = (items: typeof actionItems | typeof invoiceItems, label: string) => <div className="rr-menu" role="menu" aria-label={label}>{items.map((entry, index) => entry === 'hr' ? <hr key={`hr-${index}`} /> : <button key={entry[1]} type="button" role="menuitem" className={entry[3] || undefined} disabled={entry[4]} title={entry[4] ? entry[5] : undefined} onClick={() => { onMenuChange(null); entry[2]() }}><RrIcon name={entry[0]} />{entry[1]}</button>)}</div>
  const detailSheet = detailOpen ? createPortal(<div className="rr-layer"><div className="rr-sheet rr-sheet-full" role="dialog" aria-modal="true" aria-label={`Sipariş ${item.orderNumber} detayı`}>
    <header className="rr-sheet-h"><button type="button" className="rr-icon-btn" aria-label="Detayı kapat" onClick={() => setDetailOpen(false)}><RrIcon name="left" /></button><b>Sipariş Detayı</b><span /></header>
    <div className="rr-sheet-b">
      <article className="rr-scard"><header><div className="rr-h3">Sipariş Bilgileri</div><RrRing tone={ringTone} progress={ringProgress} label={ringLabel} detail={ringDetail.text} detailClass={ringDetail.cls} /></header><dl className="rr-kv"><dt>Platform</dt><dd>{item.platformDisplayName || returnPlatformName(item.platformCode)}</dd><dt>Sipariş Numarası</dt><dd className="rr-num">{displayOrderNumber(item.orderNumber)}<button type="button" className="rr-icon-btn" style={{ width: 22, height: 22 }} onClick={copyOrderNumber} aria-label={`Sipariş numarası ${item.orderNumber} kopyala`}><RrIcon name={copied ? 'check' : 'copy'} size={14} /></button></dd><dt>Sipariş Tarihi</dt><dd className="rr-num">{returnDateText(item.orderedAt)}</dd><dt>Paket No</dt><dd className="rr-num">{packageNoText}</dd><dt>Alıcı</dt><dd>{customerDisplayName(item)}</dd>{(item.platformInfo ?? []).map(info => <Fragment key={info.label}><dt>{info.label}</dt><dd>{info.url ? <a className="rr-btn rr-link" href={info.url} target="_blank" rel="noreferrer noopener">{info.value}</a> : info.value}</dd></Fragment>)}</dl></article>
      <article className="rr-scard"><header><div className="rr-h3">Ürünler</div><RrBadge label={`${item.productQuantity || lines.length} Adet`} glyph="idle" /></header>{lines.length ? <div className="rr-plines">{lines.map(line => <div className="rr-pline" key={line.id}><RrThumb src={lineImage(line)} quantity={productLineQuantity(line)} alt={line.title} onOpen={() => openPreview(line)} /><div><b>{line.title}</b><ReturnFactLines facts={facts(line)} /><small><span>Birim Fiyatı:</span> <b className="rr-num">{money(line.unitPrice)}</b></small></div></div>)}</div> : <p className="rr-lead">Ürün bilgisi eşitleme bekliyor</p>}</article>
      <article className="rr-scard"><header><div className="rr-h3">Kargo Bilgileri</div></header><dl className="rr-kv"><dt>Kargo Firması</dt><dd>{cargoLabel(cargoName)}</dd><dt>Kargo Kodu</dt><dd className="rr-num">{deliveryNoText}</dd></dl></article>
      <article className="rr-scard"><header><div className="rr-h3">Fatura Bilgileri</div><ReturnInvoiceRing status={item.invoiceStatus} isMicroExport={item.isMicroExport} /></header><dl className="rr-kv">{hasDiscount && <><dt>Satış Tutarı</dt><dd className="rr-num">{money(saleTotal)}</dd><dt>Satıcı İndirimi</dt><dd className="rr-num">{money(item.discountAmount)}</dd></>}<dt>Faturalanacak Tutar</dt><dd className="rr-num"><b>{money(invoiceableTotal)}</b></dd>{!invoiceNeedsAction && invoiceDocumentHref && <><dt>Fatura Belgesi</dt><dd><a className="rr-btn rr-link" href={invoiceDocumentHref} download>Faturayı Gör</a></dd></>}</dl></article>
    </div>
    <footer className="rr-sheet-f">
      {detailActionsOpen && detailMenuActions.length > 0 && <div className="rr-menu rr-sheet-menu" role="menu">{detailMenuActions.map(action => <button type="button" role="menuitem" key={action.key} disabled={action.disabled} onClick={closeDetailThen(action.run)}>{action.label}</button>)}</div>}
      {detailMenuActions.length > 0 && <button type="button" className="rr-btn" aria-haspopup="menu" aria-expanded={detailActionsOpen} onClick={() => setDetailActionsOpen(open => !open)}>İşlemler<RrIcon name="chev" /></button>}
      {detailQuickAction && <button type="button" className="rr-btn" disabled={detailQuickAction.disabled} onClick={closeDetailThen(detailQuickAction.run)}><RrIcon name="play" />{detailQuickAction.label}</button>}
      {detailPrimaryAction}
    </footer>
  </div></div>, document.body) : null
  const rowClasses = [selected ? 'rr-sel' : '', delivery.overdue ? 'rr-due' : ''].filter(Boolean).join(' ')
  return <><tr ref={rowRef} className={rowClasses || undefined} onPointerDown={event => { if (!isCancelledOrder && event.pointerType !== 'mouse' && window.matchMedia('(max-width: 760px)').matches && !(event.target as Element).closest('button, a, input, select, textarea, label')) { longPressFired.current = false; if (longPressTimer.current) window.clearTimeout(longPressTimer.current); longPressTimer.current = window.setTimeout(() => { longPressFired.current = true; flushSync(() => { onEnterSelection(); onSelect(!selected) }); navigator.vibrate?.(15) }, 480) } }} onPointerUp={() => { if (longPressTimer.current) window.clearTimeout(longPressTimer.current) }} onPointerLeave={() => { if (longPressTimer.current) window.clearTimeout(longPressTimer.current) }} onPointerCancel={() => { if (longPressTimer.current) window.clearTimeout(longPressTimer.current) }} onContextMenu={event => { if (window.matchMedia('(max-width: 760px)').matches) event.preventDefault() }} onClick={event => { if (!window.matchMedia('(max-width: 760px)').matches) return; if (longPressFired.current) { longPressFired.current = false; return } const target = event.target as Element; if (target.closest('button, a, input, select, textarea, label, .rr-menu, .rr-pop, .rr-thumb')) return; if (selectionMode) { if (!isCancelledOrder) onSelect(!selected); return } setDetailOpen(true) }}>
    <td className="rr-wide"><div className="rr-ordercell">{isCancelledOrder ? <span style={{ width: 16, flex: 'none' }} title="İptal siparişler toplu seçilemez" /> : <input type="checkbox" className="rr-cb" checked={selected} onChange={event => onSelect(event.target.checked)} aria-label={`Sipariş ${item.orderNumber} seç`} />}<div><RrWordmark code={item.platformCode} name={item.platformDisplayName || returnPlatformName(item.platformCode)} /><span className={`rr-on-no${delivery.overdue ? ' rr-crit' : ''}`}>{displayOrderNumber(item.orderNumber)}<button type="button" className="rr-icon-btn" title={copied ? 'Kopyalandı' : 'Kopyala'} aria-label={copied ? `Sipariş numarası ${item.orderNumber} kopyalandı` : `Sipariş numarası ${item.orderNumber} kopyala`} style={{ width: 22, height: 22 }} onClick={() => void copyOrderNumber()}><RrIcon name={copied ? 'check' : 'copy'} size={14} /></button></span>{noBuyer && <small className="rr-buyer-inline"><b>Alıcı:</b> {customerDisplayName(item)}</small>}<small className="rr-num"><b>Sipariş Tarihi:</b> {returnDateText(item.orderedAt)}</small><small className="rr-num"><b>Paket No:</b> {packageNoText}</small><small className="rr-num"><b>Teslimat No:</b> {shipment?.cargoTrackingNumber ?? (isHepsiburadaUnverifiedWithoutPackage ? 'Gönderi bilgisi doğrulanmadı' : isHepsiburadaOrder ? shipment ? 'Kargoya verildiğinde gelir' : 'Paket oluşturulunca gelir' : 'Bekleniyor')}</small>{hepsiburadaPackageHistoryExpired && <small className="rr-note-line">Hepsiburada kargo durum listeleri son bir ayı kapsıyor; daha eski paket kaydı API’den doğrulanamıyor.</small>}</div></div></td>
    {!noBuyer && <td className="rr-mid" data-l="Alıcı"><div className="rr-cust"><b>{customerDisplayName(item)}</b></div></td>}
    <td className="rr-wide" data-l="Bilgiler"><div className="rr-plines">{lines.length ? lines.map(line => <div className="rr-pline" key={line.id}><RrThumb src={lineImage(line)} quantity={productLineQuantity(line)} alt={line.title} onOpen={() => openPreview(line)} /><div><b title={line.title}><ProductTitleLink platformCode={item.platformCode} line={line} /></b><small className="rr-card-only"><span>Birim Fiyatı:</span> <b className="rr-num">{money(line.unitPrice)}</b></small>{facts(line).map(([label, value]) => <small key={label} title={`${label}: ${value}`}><span>{label}:</span> <b className="rr-num">{value}</b></small>)}</div></div>) : <div className="rr-pline"><RrThumb src={item.primaryImageUrl} alt="Ürün" /><div><b>Ürün bilgisi eşitleme bekliyor</b></div></div>}</div></td>
    <td className="rr-mid rr-price-col" data-l="Birim Fiyat"><div className="rr-prices">{lines.length ? lines.map(line => { const quantity = productLineQuantity(line); return <div className="rr-pr" key={line.id}><b className="rr-num">{money(line.unitPrice)}</b>{quantity > 1 && <small>{quantity} adet</small>}</div> }) : <div className="rr-pr"><b className="rr-num">{money(item.netAmount)}</b></div>}</div></td>
    <td className="rr-mid" data-l="Kargo"><div className="rr-cargocell">{cargoIcon ? <img className="rr-cargo-img" src={cargoIcon} alt={cargoLabel(cargoName)} title={cargoLabel(cargoName)} referrerPolicy="no-referrer" loading="lazy" /> : <span className="rr-muted">{cargoName?.trim() ? cargoLabel(cargoName) : 'Kargo bekleniyor'}</span>}<small className="rr-num">{trackingNumber ?? (isHepsiburadaUnverifiedWithoutPackage ? 'Gönderi durumu doğrulanmadı' : 'Takip no bekleniyor')}</small>{isCargoTrackingStage && shipment?.cargoTrackingNumber && <Link className="rr-btn rr-link" style={{ fontSize: 12 }} to={`/shipments/${shipment.id}`}>Kargo Takip <RrIcon name="open" /></Link>}</div></td>
    <td data-l="Fatura"><div className="rr-invcell">{hasDiscount && <><span className="rr-m"><span>Satış Tutarı</span><b className="rr-num">{money(saleTotal)}</b></span><span className="rr-m"><span>Satıcı İndirimi</span><b className="rr-num">{money(item.discountAmount)}</b></span></>}<span className="rr-m rr-pay"><span>Faturalanacak Tutar</span><b className="rr-num">{money(invoiceableTotal)}</b></span><ReturnInvoiceRing status={item.invoiceStatus} isMicroExport={item.isMicroExport} />{!isCancelledOrder && <RrAnchor trigger={(open, toggle) => <button type="button" className="rr-btn rr-link rr-inv-trigger" aria-haspopup="menu" aria-expanded={open} onClick={toggle}>Fatura İşlemleri <RrIcon name="chev" /></button>}>{() => menu(invoiceItems, `${displayOrderNumber(item.orderNumber)} fatura işlemleri`)}</RrAnchor>}</div></td>
    <td className="rr-mid" data-l="Durum"><RrRing tone={ringTone} progress={ringProgress} label={ringLabel} detail={ringDetail.text} detailClass={ringDetail.cls} /></td>
    <td className="rr-mid" data-l="İşlemler">{isCancelledOrder ? <div className="rr-actcell"><span className="rr-muted-note">İşlem yapılamaz</span></div> : <div className="rr-actcell">{canPrintLabels && labelFormat && <button type="button" className={`rr-btn rr-lbl${labelPrinted ? ' rr-printed' : ''}`} disabled={labelBlockedByInvoice} title={labelBlockedByInvoice ? 'Mikro ihracat faturası kesilmeden kargo etiketi yazdırılamaz.' : labelPrinted ? `${labelFormat === 'a4' ? 'A4' : 'Sticker'} etiketi daha önce yazdırıldı; tekrar yazdırılabilir.` : `${labelFormat === 'a4' ? 'A4' : 'Sticker'} Etiketi Yazdır`} onClick={() => onPrintLabel(labelFormat)}><RrIcon name="print" />Etiket</button>}<RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} aria-haspopup="menu" aria-expanded={open} onClick={toggle}><span>İşlemler</span><RrIcon name="chev" /></button>}>{() => menu(actionItems, `${displayOrderNumber(item.orderNumber)} sipariş işlemleri`)}</RrAnchor></div>}</td>
  </tr>
    {detailSheet}
    {invoiceStatusModalOpen && <ShopifyInvoiceStatusModal item={item} onClose={() => setInvoiceStatusModalOpen(false)} onFeedback={(message, kind) => { if (kind !== 'pending') appendNotification(message, normalizeNotificationKind(message, kind)) }} />}
  </>
}


function HepsiburadaPackageCreateModal({ item, onClose, onCreated }: { item: Order; onClose: () => void; onCreated: (packageNumber: string) => void }) {
  const lines = item.lines ?? []
  const requestIdempotencyKey = useRef(idempotency())
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
    try {
      const result = await hubApi<{ packageNumber: string }>(`/orders/${item.id}/packages`, {
        method: 'POST',
        headers: { 'Idempotency-Key': requestIdempotencyKey.current, 'If-Match': `"v${item.version}"` },
        body: JSON.stringify({
          barcode: data.get('barcode'),
          cargoCompany: data.get('cargoCompany'),
          carrier: data.get('carrier'),
          creationReason: data.get('creationReason'),
          deci: Number(data.get('deci')),
          parcelQuantity: Number(data.get('parcelQuantity')),
          shippingAddressLabel: data.get('shippingAddressLabel'),
          shippingModel: data.get('shippingModel'),
          lineItems,
        }),
      })
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
function returnGroup(status: string) { const value = status.toUpperCase(); if (['APPROVED', 'COMPLETED'].includes(value)) return 'APPROVED'; if (['REJECTED', 'CANCELLED'].includes(value)) return 'REJECTED'; if (['REQUESTED', 'CREATED'].includes(value)) return 'REQUESTED'; if (['ACTION_REQUIRED', 'WAITING_FOR_SELLER_ACTION'].includes(value)) return 'ACTION_REQUIRED'; if (['WAITING_FOR_SHIPMENT', 'IN_TRANSIT', 'RETURN_IN_TRANSIT', 'SHIPPED'].includes(value)) return 'SHIPPING'; if (value === 'DISPUTED') return 'DISPUTED'; if (['SUSPENDED', 'ON_HOLD'].includes(value)) return 'SUSPENDED'; return 'REVIEW' }
const activePlatformCodes = new Set(['TRENDYOL', 'TRENDYOL_EFATURAM', 'SHOPIFY', 'HEPSIBURADA', 'N11', 'PAZARAMA'])
function credentialLabel(item: Connection) { return item.platformCode === 'TRENDYOL_EFATURAM' && item.lastErrorCode === 'EFATURAM_CONFIGURATION_UNAVAILABLE' ? 'Yenileme gerekli' : item.hasCredential ? 'Şifreli kayıtlı' : 'Bekleniyor' }
function hepsiburadaCredentialHelp(item: Connection) {
  if (item.lastErrorCode === 'HEPSIBURADA_AUTHENTICATION_UNVERIFIED') return 'Bu ortamda HTTP Basic Auth etkin değil; dış istek gönderilmedi.'
  if (item.lastErrorCode === 'HEPSIBURADA_CREDENTIALS_REJECTED' || item.lastErrorCode === 'HEPSIBURADA_AUTHENTICATION_FAILED') return 'Hepsiburada HTTP 401 verdi: entegratör kullanıcı adı ve servis anahtarı eşleşmiyor veya canlı ortam için değil. Aynı aktif Entegratörlerim kaydındaki kullanıcı adı ile canlı servis anahtarını birlikte yeniden şifreli kaydedin.'
  if (item.lastErrorCode === 'HEPSIBURADA_ACCESS_FORBIDDEN') return 'Hepsiburada HTTP 403 verdi: mağaza veya endpoint erişimi reddedildi. Mağaza ID’sini ve entegratörün mağaza/API yetkisini kontrol edin.'
  return item.hasCredential
    ? 'Hepsiburada HTTP Basic Auth için entegratör kullanıcı adı ile aynı kaydın servis anahtarını kullanır. Mağaza ID’si ayrı kapsam bilgisidir; gizli değerler şifreli saklanır.'
    : 'Hepsiburada servis anahtarı bekleniyor.'
}
function credentialHelp(item: Connection) { return item.platformCode === 'HEPSIBURADA' ? hepsiburadaCredentialHelp(item) : item.platformCode === 'PAZARAMA' ? item.hasCredential ? 'Client Id ve Client Secret şifreli kaydedildi; değerler tekrar gösterilmez.' : 'Pazarama API bilgileri bekleniyor. Pazarama satıcı panelinde Hesabım → Entegrasyon Bilgileri altından alınır.' : item.platformCode === 'N11' ? item.hasCredential ? 'App Key ve App Secret şifreli kaydedildi; değerler tekrar gösterilmez.' : 'N11 API anahtarı bekleniyor. N11 satıcı panelinde Hesabım → API Hesapları altından alınır.' : item.platformCode === 'TRENDYOL_EFATURAM' && item.lastErrorCode === 'EFATURAM_CONFIGURATION_UNAVAILABLE' ? 'E-Faturam hesap e-postası ve parolasıyla şifreli kaydı yenileyin.' : item.platformCode === 'SHOPIFY' ? item.hasCredential ? 'Uygulama tokenı şifreli kaydedildi; değer tekrar gösterilmez. Son 60 günden eski siparişler için read_all_orders izni gerekir.' : 'Shopify uygulama tokenı bekleniyor.' : item.hasCredential ? 'Şifreli kaydedildi; değerler tekrar gösterilmez.' : 'Credential kaydı bekleniyor.' }
function connectionTestHelp(item: Connection) {
  if (item.platformCode === 'HEPSIBURADA' && (item.lastErrorCode === 'HEPSIBURADA_CREDENTIALS_REJECTED' || item.lastErrorCode === 'HEPSIBURADA_AUTHENTICATION_FAILED')) return 'Hepsiburada HTTP 401 verdi. Satıcı Paneli’nde Bilgilerim → Entegrasyon → Entegratör Bilgileri bölümündeki aynı aktif kaydın kullanıcı adı ve canlı servis anahtarını kullanın; servis anahtarını yenilediyseniz burada tekrar şifreli kaydedin.'
  if (item.platformCode === 'HEPSIBURADA' && item.lastErrorCode === 'HEPSIBURADA_ACCESS_FORBIDDEN') return 'Hepsiburada HTTP 403 verdi. Mağaza ID’sini ve entegratörün bu mağazaya/API uçlarına erişim yetkisini kontrol edin.'
  if (item.platformCode === 'HEPSIBURADA' && item.lastErrorCode === 'HEPSIBURADA_AUTHENTICATION_UNVERIFIED') return 'Kimlik doğrulama yapılandırması etkin olmadığı için istek gönderilmedi.'
  if (item.platformCode === 'HEPSIBURADA') return item.lastErrorCode ?? 'Bağlantı testi en fazla bir sipariş kaydı okur; pazaryerine yazma yapmaz.'
  if (item.platformCode === 'N11' && item.lastErrorCode === 'N11_CREDENTIALS_REJECTED') return 'N11 anahtarı reddetti. Satıcı panelinde Hesabım → API Hesapları ekranındaki güncel App Key ve App Secret’ı kaydedip yeniden test edin.'
  if (item.platformCode === 'N11' && item.lastErrorCode === 'N11_CREDENTIAL_FORMAT_INVALID') return 'Kayıtlı App Key veya App Secret içinde geçersiz karakter var. Değerleri N11 satıcı panelinden yeniden kopyalayıp kaydedin.'
  if (item.platformCode === 'N11' && item.lastErrorCode === 'N11_REST_ACCESS_DENIED') return 'Anahtarlar doğru ama bu API hesabında N11’in yeni (REST) servisleri kapalı. sellerintegration@n11.com adresine yazıp REST API erişiminin açılmasını isteyin.'
  if (item.platformCode === 'N11' && item.lastErrorCode === 'N11_STORE_MISMATCH') return 'Bu API anahtarı başka bir N11 mağazasına ait. Mağaza adını N11’deki gibi yazın ya da doğru mağazanın anahtarını girin.'
  if (item.platformCode === 'N11') return item.lastErrorCode ?? 'Bağlantı testi ürün ve sipariş listesinden en fazla birer kayıt okur; N11’e yazma yapmaz.'
  if (item.platformCode === 'PAZARAMA' && item.lastErrorCode === 'PAZARAMA_CREDENTIALS_REJECTED') return 'Pazarama Client Id ve Client Secret çiftini kabul etmedi. Satıcı panelinde Hesabım → Entegrasyon Bilgileri’ndeki güncel değerleri kaydedip yeniden test edin; orada yeni anahtar üretildiyse eskisi geçersiz olur.'
  if (item.platformCode === 'PAZARAMA' && item.lastErrorCode === 'PAZARAMA_CREDENTIAL_FORMAT_INVALID') return 'Kayıtlı Client Id veya Client Secret değerinde geçersiz karakter var. Değerleri Pazarama satıcı panelinden yeniden kopyalayıp kaydedin.'
  if (item.platformCode === 'PAZARAMA' && item.lastErrorCode === 'PAZARAMA_SCOPE_DENIED') return 'Pazarama bu API hesabına entegrasyon yetkisi vermiyor. Pazarama destekle API erişimini açtırın.'
  if (item.platformCode === 'PAZARAMA') return item.lastErrorCode ?? 'Bağlantı testi ürün, sipariş ve marka listesinden en fazla birer kayıt okur; Pazarama’ya yazma yapmaz.'
  if (item.platformCode === 'TRENDYOL_EFATURAM' && item.lastErrorCode === 'EFATURAM_ACCESS_TOKEN_REJECTED') return 'Giriş başarılı; sağlayıcı sign-in yanıtındaki taze JWT tokenını korumalı fatura API’sinde geçersiz veya süresi dolmuş olarak reddetti.'
  if (item.platformCode === 'SHOPIFY' && item.lastErrorCode === 'SHOPIFY_REQUIRED_READ_SCOPE') return 'Shopify uygulama tokenında read_products, read_inventory, read_orders ve read_locations okuma izinleri açık olmalı. Fiyat/stok dış yazması için write_products ve write_inventory gerekir. Son 60 günden eski siparişler için ayrıca read_all_orders erişimi gerekir. İzinleri açtıktan sonra yeni token oluşturup burada şifreli kaydedin.'
  if (item.platformCode === 'SHOPIFY') return item.lastErrorCode ? `Shopify doğrulaması başarısız: ${item.lastErrorCode}` : 'Mağaza, izinler, para birimi ve depolar son testte kontrol edilir.'
  return item.lastErrorCode ?? 'Hata kaydı yok'
}

function ReferenceActionDialog({ id, title, description, confirmLabel, confirmDisabled, destructive = false, onClose, onConfirm, children }: { id: string; title: string; description: string; confirmLabel: string; confirmDisabled?: boolean; destructive?: boolean; onClose: () => void; onConfirm: () => void; children?: ReactNode }) {
  return <div className="workspace-modal-backdrop reference-action-backdrop" role="presentation" onMouseDown={onClose}><section className="workspace-modal reference-action-modal" role="alertdialog" aria-modal="true" aria-labelledby={`${id}-title`} onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">BAĞLANTI İŞLEMİ</p><h2 id={`${id}-title`}>{title}</h2></div><button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header><div className="reference-action-body"><p>{description}</p>{children}</div><footer><button type="button" className="secondary" onClick={onClose}>Vazgeç</button><button type="button" className={destructive ? 'destructive' : undefined} onClick={() => onConfirm()} disabled={confirmDisabled}>{confirmLabel}</button></footer></section></div>
}

function ConnectionDataResetDialog({ item, scopes, confirmation, busy, onToggleScope, onChangeConfirmation, onClose, onConfirm }: { item: Connection; scopes: ConnectionDataResetScope[]; confirmation: string; busy: boolean; onToggleScope: (scope: ConnectionDataResetScope, checked: boolean) => void; onChangeConfirmation: (value: string) => void; onClose: () => void; onConfirm: () => void }) {
  const groups = connectionDataResetGroups(item.platformCode)
  return <div className="workspace-modal-backdrop reference-action-backdrop" role="presentation" onMouseDown={() => { if (!busy) onClose() }}>
    <section className="workspace-modal reference-action-modal connection-data-reset-modal" role="alertdialog" aria-modal="true" aria-labelledby={`reset-data-${item.id}-title`} onMouseDown={event => event.stopPropagation()}>
      <header><div><p className="eyebrow">BAĞLANTI VERİLERİ</p><h2 id={`reset-data-${item.id}-title`}>Verileri sıfırla</h2></div><button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat" disabled={busy}><UiIcon name="close" /></button></header>
      <div className="reference-action-body">
        <p><strong>{item.displayName}</strong> bağlantısında temizlenecek veri türlerini seçin. İşlem yalnız bu bağlantının yerel kayıtlarını etkiler; pazaryerine yazma yapmaz.</p>
        <fieldset className="connection-data-reset-options">
          <legend>Sıfırlanacak verileri seçin</legend>
          {groups.map(group => <section className="connection-data-reset-group" key={group.label}><div className="connection-data-reset-group-heading"><strong>{group.label}</strong><small>{group.description}</small></div>{group.options.map(option => <label key={option.scope} className="connection-data-reset-option"><input type="checkbox" checked={scopes.includes(option.scope)} onChange={event => onToggleScope(option.scope, event.target.checked)} disabled={busy} /><span><strong>{option.label}</strong><small>{option.description}</small></span></label>)}</section>)}
        </fieldset>
        <label className="reference-action-confirmation"><span>Onay için <b>{item.displayName}</b> yazın</span><input value={confirmation} onChange={event => onChangeConfirmation(event.target.value)} disabled={busy} autoComplete="off" /></label>
      </div>
      <footer><button type="button" className="secondary" onClick={onClose} disabled={busy}>Vazgeç</button><button type="button" className="destructive" onClick={onConfirm} disabled={!scopes.length || confirmation !== item.displayName || busy}>{busy ? 'Sıfırlanıyor…' : 'Seçilen verileri sıfırla'}</button></footer>
    </section>
  </div>
}


function ReferenceConnectionCard({ item, onSaved, onFeedback }: { item: Connection; onSaved: () => void; onFeedback: (feedback: IntegrationFeedback) => void }) {
  const client = useQueryClient()
  const efaturam = item.platformCode === 'TRENDYOL_EFATURAM'
  const shopify = item.platformCode === 'SHOPIFY'
  const [menuOpen, setMenuOpen] = useState(false)
  const [disconnectOpen, setDisconnectOpen] = useState(false)
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [resetOpen, setResetOpen] = useState(false)
  const [resetScopes, setResetScopes] = useState<ConnectionDataResetScope[]>([])
  const [resetConfirmation, setResetConfirmation] = useState('')
  const [settingsOpen, setSettingsOpen] = useState(false)
  const referenceMenuRef = useRef<HTMLDivElement>(null)
  const referenceMenuPopoverRef = useRef<HTMLDivElement>(null)
  const [menuPlacement, setMenuPlacement] = useState<'down' | 'up'>('down')
  const [menuMaxHeight, setMenuMaxHeight] = useState<number | null>(null)
  const [deleteConfirmation, setDeleteConfirmation] = useState('')
  const connected = item.status === 'ACTIVE' || item.status === 'VERIFIED'
  const hidden = item.status === 'HIDDEN'
  const supportsInitialDataSync = item.platformCode === 'TRENDYOL' || item.platformCode === 'SHOPIFY' || item.platformCode === 'HEPSIBURADA' || item.platformCode === 'N11'
  useEffect(() => {
    if (!settingsOpen) return
    const previousOverflow = document.body.style.overflow
    const closeOnEscape = (event: globalThis.KeyboardEvent) => { if (event.key === 'Escape') setSettingsOpen(false) }
    document.body.style.overflow = 'hidden'
    document.addEventListener('keydown', closeOnEscape)
    return () => { document.body.style.overflow = previousOverflow; document.removeEventListener('keydown', closeOnEscape) }
  }, [settingsOpen])
  useEffect(() => {
    if (!menuOpen) return
    const closeOnOutsidePointer = (event: globalThis.PointerEvent) => {
      if (!referenceMenuRef.current?.contains(event.target as Node)) setMenuOpen(false)
    }
    const closeOnEscape = (event: globalThis.KeyboardEvent) => { if (event.key === 'Escape') setMenuOpen(false) }
    document.addEventListener('pointerdown', closeOnOutsidePointer)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeOnOutsidePointer)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [menuOpen])
  const test = useMutation({ mutationFn: () => hubApi<{ succeeded?: boolean; errorCode?: string; errorSummary?: string }>(`/connections/${item.id}/test-jobs`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: '{}' }), onSuccess: async result => { onFeedback({ kind: result.succeeded ? 'success' : 'error', message: result.succeeded ? 'Bağlantı testi başarılı; yetenek kontrolleri yenilendi. Dış yazma yapılmadı.' : `Bağlantı testi başarısız${result.errorCode ? `: ${result.errorCode}` : '.'}${result.errorSummary ? ` — ${result.errorSummary}` : ''}` }); await Promise.all([client.invalidateQueries({ queryKey: ['connection', item.id] }), client.invalidateQueries({ queryKey: ['capabilities', item.id] }), client.invalidateQueries({ queryKey: ['sync-policies', item.id] })]); onSaved() }, onError: reason => onFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Bağlantı testi başlatılamadı.' }) })
  const initialDataSync = useMutation({ mutationFn: () => hubApi<{ jobId: string }>(`/connections/${item.id}/initial-data-sync-jobs`, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${item.version}"` }, body: '{}' }), onSuccess: async () => { onFeedback({ kind: 'success', message: `${item.displayName} için platformun izin verdiği en geniş geçmiş aralığını tarayan veri çekimi kuyruğa alındı.` }); await Promise.all([client.invalidateQueries({ queryKey: ['jobs'] }), client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['returns'] }), client.invalidateQueries({ queryKey: ['products'] }), client.invalidateQueries({ queryKey: ['dashboard-bootstrap'] })]) }, onError: reason => onFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'İlk veri çekimi başlatılamadı.' }) })
  const disconnect = useMutation({ mutationFn: () => hubApi<Connection>(`/connections/${item.id}/active`, { method: 'PUT', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ active: false }) }), onSuccess: () => { setDisconnectOpen(false); onFeedback({ kind: 'success', message: 'Bağlantı pasife alındı.' }); onSaved() }, onError: reason => onFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Bağlantı pasife alınamadı.' }) })
  const activate = useMutation({ mutationFn: () => hubApi<Connection>(`/connections/${item.id}/active`, { method: 'PUT', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ active: true }) }), onSuccess: () => { onFeedback({ kind: 'success', message: 'Bağlantı etkinleştirildi.' }); onSaved() }, onError: reason => onFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Bağlantı etkinleştirilemedi.' }) })
  const deepDelete = useMutation({ mutationFn: () => hubApi(`/connections/${item.id}/deep-delete`, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ confirmation: deleteConfirmation }) }), onSuccess: () => { setDeleteOpen(false); setDeleteConfirmation(''); onFeedback({ kind: 'success', message: 'Bağlantı ve bağlı veriler silindi.' }); onSaved() }, onError: reason => onFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Bağlantı ve bağlı veriler silinemedi.' }) })
  const resetData = useMutation({ mutationFn: () => hubApi(`/connections/${item.id}/data-reset`, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ scopes: resetScopes, confirmation: resetConfirmation }) }), onSuccess: () => { const labels = connectionDataResetOptions(item.platformCode).filter(option => resetScopes.includes(option.scope)).map(option => option.label.toLocaleLowerCase('tr-TR')).join(', '); setResetOpen(false); setResetScopes([]); setResetConfirmation(''); onFeedback({ kind: 'success', message: `${item.displayName} bağlantısında seçilen veriler sıfırlandı: ${labels}. Otomatik eşitleme açıksa kayıtlar yeniden çekilebilir.` }); onSaved() }, onError: reason => onFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Seçilen bağlantı verileri sıfırlanamadı.' }) })
  const menuActions = [
    { key: 'settings', label: 'Bağlantı ayarlarını aç', disabled: false, run: () => { setMenuOpen(false); setSettingsOpen(true) } },
    { key: 'connection', label: connected ? 'Bağlantıyı pasife al' : 'Bağlantıyı aktifleştir', disabled: disconnect.isPending || activate.isPending, run: () => { setMenuOpen(false); connected ? setDisconnectOpen(true) : activate.mutate() } },
    { key: 'test', label: 'Bağlantıyı kontrol et', displayLabel: test.isPending ? 'Kontrol ediliyor…' : 'Bağlantıyı kontrol et', disabled: test.isPending, run: () => { setMenuOpen(false); test.mutate() } },
    { key: 'delete', label: 'Bağlantıyı sil', disabled: false, run: () => { setMenuOpen(false); setDeleteOpen(true) } },
    ...(supportsInitialDataSync ? [{ key: 'initial-sync', label: 'Verileri çek', displayLabel: initialDataSync.isPending ? 'Kuyruğa alınıyor…' : 'Verileri çek', disabled: !connected || !item.hasCredential || initialDataSync.isPending, run: () => { setMenuOpen(false); initialDataSync.mutate() } }] : []),
    { key: 'reset', label: 'Verileri sıfırla', disabled: resetData.isPending, run: () => { setMenuOpen(false); setResetScopes([]); setResetConfirmation(''); setResetOpen(true) } },
  ].sort((left, right) => left.label.localeCompare(right.label, 'tr-TR'))
  function updateMenuPlacement(trigger: HTMLButtonElement) {
    const triggerRect = trigger.getBoundingClientRect()
    const estimatedHeight = Math.min(420, menuActions.length * 40 + 12)
    const gap = 8
    const viewportPadding = 12
    const below = Math.max(0, window.innerHeight - triggerRect.bottom - gap - viewportPadding)
    const above = Math.max(0, triggerRect.top - gap - viewportPadding)
    const next = below >= estimatedHeight ? 'down' : above >= estimatedHeight ? 'up' : below >= above ? 'down' : 'up'
    const available = next === 'up' ? above : below
    setMenuPlacement(next)
    setMenuMaxHeight(Math.min(420, available))
  }
  useLayoutEffect(() => {
    if (!menuOpen) {
      setMenuMaxHeight(null)
      return
    }
    const updatePlacement = () => {
      const trigger = referenceMenuRef.current?.querySelector<HTMLButtonElement>('.reference-menu-trigger')
      if (trigger) updateMenuPlacement(trigger)
    }
    updatePlacement()
    window.addEventListener('resize', updatePlacement)
    window.addEventListener('scroll', updatePlacement, true)
    return () => {
      window.removeEventListener('resize', updatePlacement)
      window.removeEventListener('scroll', updatePlacement, true)
    }
  }, [menuOpen])
  function toggleReferenceMenu(event: MouseEvent<HTMLButtonElement>) {
    if (menuOpen) {
      setMenuOpen(false)
      return
    }
    updateMenuPlacement(event.currentTarget)
    setMenuOpen(true)
  }
  const privateDataHidden = hidden || !connected
  return <article className={`integration-card reference-integration-card ${hidden ? 'reference-integration-card-hidden' : connected ? 'reference-integration-card-active' : 'reference-integration-card-error'}${menuOpen ? ' reference-integration-card-menu-open' : ''}`} onClickCapture={event => { const target = event.target; if (target instanceof Element && target.closest('.reference-menu-popover a')) { event.preventDefault(); setMenuOpen(false); setSettingsOpen(true) } }}>
    <div className="reference-integration-accent" aria-hidden="true" />
    <header className="reference-integration-header">
      <div className="reference-integration-brand"><img className={`reference-provider-logo ${platformLogoClass(item.platformCode)}`} src={platformLogoSource(item.platformCode) ?? '/platforms/trendyol.png'} alt="" aria-hidden="true" /><div><h2>{item.displayName}</h2><span className={`reference-connection-state ${hidden ? 'hidden' : connected ? 'connected' : 'inactive'}`}><MappingStateGlyph state={connected && !hidden ? 'done' : 'none'} />{hidden ? 'Veriler gizli' : connected ? 'Bağlı ve aktif' : 'Pasif'}</span></div></div>
      <div className="reference-integration-menu" ref={referenceMenuRef}><button type="button" className="reference-menu-trigger" aria-label="Entegrasyon seçenekleri" aria-expanded={menuOpen} onClick={toggleReferenceMenu}><UiIcon name="moreVertical" /></button>{menuOpen && <div ref={referenceMenuPopoverRef} className={`reference-menu-popover opens-${menuPlacement}`} style={menuMaxHeight === null ? undefined : { maxHeight: `${menuMaxHeight}px` }} role="menu">{menuActions.map(action => <button type="button" key={action.key} role="menuitem" disabled={action.disabled} onClick={action.run}>{action.displayLabel ?? action.label}</button>)}</div>}</div>
    </header>
      {privateDataHidden ? <div className="reference-integration-body is-passive" role="status" aria-label={hidden ? 'Veriler gizli' : 'Bağlantı pasif; bilgiler gizli'}><div className="reference-data-point"><span>{efaturam ? 'VKN / TCKN' : shopify ? 'Shopify mağazası' : item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada mağaza ID' : 'Mağaza Kimliği (Seller ID)'}</span><strong>Gizli</strong></div><div className="reference-data-point"><span>Kimlik bilgileri</span><strong>Gizli</strong></div><div className="reference-data-point"><span>Ortam</span><strong>{shopify ? 'Canlı' : item.environment === 'PRODUCTION' ? 'Canlı' : 'Stage'}</strong></div><div className="reference-data-point"><span>Dış yazma</span><strong>Kapalı</strong></div></div> : <div className="reference-integration-body"><div className="reference-data-point"><span>{efaturam ? 'VKN / TCKN' : shopify ? 'Shopify mağazası' : item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada mağaza ID' : 'Mağaza Kimliği (Seller ID)'}</span><strong>{shopify ? shopifyStoreDisplayName(item.externalStoreId) || 'Tanımlanmadı' : item.externalStoreId || 'Tanımlanmadı'}</strong></div><div className="reference-data-point"><span>Kimlik bilgileri</span><strong>{credentialLabel(item)}</strong></div><div className="reference-data-point"><span>Ortam</span><strong>{shopify ? 'Canlı' : item.environment === 'PRODUCTION' ? 'Canlı' : 'Stage'}</strong></div><div className="reference-data-point"><span>Dış yazma</span><strong>{item.externalWritesEnabled ? 'Açık' : 'Salt-okunur'}</strong></div></div>}
    <footer className="reference-integration-footer"><span>{shopify ? 'E-Ticaret' : efaturam ? 'E-Fatura' : 'Pazaryeri'}</span><button type="button" className="reference-action" onClick={() => setSettingsOpen(true)} disabled={disconnect.isPending || activate.isPending || test.isPending}><UiIcon name="settings" /> Yönet</button></footer>
    {disconnectOpen && <ReferenceActionDialog id={`disconnect-${item.id}`} title="Bağlantıyı pasife al" description="Bağlantı pasife alınacak ve canlı veri akışı duracaktır." confirmLabel={disconnect.isPending ? 'Pasife alınıyor…' : 'Onayla'} confirmDisabled={disconnect.isPending} onClose={() => setDisconnectOpen(false)} onConfirm={() => disconnect.mutate()} />}
     {deleteOpen && <ReferenceActionDialog id={`delete-${item.id}`} title="Bağlantıyı sil" description="Bağlantı ve bu mağazaya bağlı sipariş, iade, fatura, ürün ve eşleştirme kayıtları kalıcı olarak silinir." confirmLabel={deepDelete.isPending ? 'Temizleniyor…' : 'Kalıcı olarak sil'} confirmDisabled={deleteConfirmation !== item.displayName || deepDelete.isPending} destructive onClose={() => { setDeleteOpen(false); setDeleteConfirmation('') }} onConfirm={() => deepDelete.mutate()}><label className="reference-action-confirmation">Onay için <b>{item.displayName}</b> yazın<input value={deleteConfirmation} onChange={event => setDeleteConfirmation(event.target.value)} /></label></ReferenceActionDialog>}
    {resetOpen && <ConnectionDataResetDialog item={item} scopes={resetScopes} confirmation={resetConfirmation} busy={resetData.isPending} onToggleScope={(scope, checked) => setResetScopes(current => checked ? [...new Set([...current, scope])] : current.filter(value => value !== scope))} onChangeConfirmation={setResetConfirmation} onClose={() => { setResetOpen(false); setResetScopes([]); setResetConfirmation('') }} onConfirm={() => resetData.mutate()} />}
    {settingsOpen && <div className="integration-settings-modal-backdrop" role="presentation" onMouseDown={() => setSettingsOpen(false)}><section className="integration-settings-modal" role="dialog" aria-modal="true" aria-label={`${item.displayName} bağlantı ayarları`} onMouseDown={event => event.stopPropagation()}><IntegrationDetailWorkspace id={item.id} inline onClose={() => setSettingsOpen(false)} /></section></div>}
  </article>
}

export function IntegrationsPage() {
  const client = useQueryClient()
  const [createOpen, setCreateOpen] = useState(false)
  const [createPlatform, setCreatePlatform] = useState('TRENDYOL')
  const [feedback, setFeedback] = useState<IntegrationFeedback | null>(null)
  const [filterOpen, setFilterOpen] = useState(false)
  const [view, setView] = useState<'ALL' | 'ACTIVE'>('ALL')
  const [filter, setFilter] = useState<'ALL' | 'ACTIVE' | 'ATTENTION'>('ALL')
  const [platformFilter, setPlatformFilter] = useState<'ALL' | 'TRENDYOL' | 'TRENDYOL_EFATURAM' | 'SHOPIFY' | 'HEPSIBURADA' | 'N11' | 'PAZARAMA'>('ALL')
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
  const visibleConnections = activeConnections.filter(item => (view === 'ALL' || item.status === 'ACTIVE' || item.status === 'VERIFIED') && (platformFilter === 'ALL' || item.platformCode === platformFilter) && (filter === 'ALL' || (filter === 'ACTIVE' ? item.status === 'ACTIVE' || item.status === 'VERIFIED' : item.status !== 'ACTIVE' && item.status !== 'VERIFIED' || Boolean(item.lastErrorCode))))
  const filterLabel = `${view === 'ACTIVE' ? 'Bağlı Olanlar' : 'Tümü'} · ${platformFilter === 'ALL' ? 'Tüm Platformlar' : platformFilter === 'TRENDYOL' ? 'Trendyol' : platformFilter === 'SHOPIFY' ? 'Shopify' : platformFilter === 'HEPSIBURADA' ? 'Hepsiburada' : platformFilter === 'N11' ? 'N11' : platformFilter === 'PAZARAMA' ? 'Pazarama' : 'Trendyol E-Faturam'} · ${filter === 'ACTIVE' ? 'Aktif Bağlantılar' : filter === 'ATTENTION' ? 'Dikkat Gerekenler' : 'Tüm Durumlar'}`
  const create = useMutation({ mutationFn: (body: object) => hubApi('/connections', { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify(body) }), onSuccess: () => { setCreateOpen(false); setFeedback({ kind: 'success', message: 'Bağlantı oluşturuldu. Gönderilen kimlik bilgileri varsa şifreli saklandı; doğrulama isteği ayrıca başlatılmalıdır.' }); void client.invalidateQueries({ queryKey: ['connections'] }) }, onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Bağlantı oluşturulamadı.' }) })
  function submitCreate(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const data = new FormData(event.currentTarget); create.mutate({ displayName: data.get('displayName'), environment: createPlatform === 'SHOPIFY' || createPlatform === 'N11' || createPlatform === 'PAZARAMA' ? 'PRODUCTION' : data.get('environment'), externalStoreId: data.get('externalStoreId'), platformCode: createPlatform, apiVersion: createPlatform === 'TRENDYOL' ? 'V2' : createPlatform === 'SHOPIFY' ? '2026-07' : createPlatform === 'HEPSIBURADA' ? 'V1.0' : createPlatform === 'N11' || createPlatform === 'PAZARAMA' ? 'V1' : '1.0.0', userAgentIdentity: createPlatform === 'TRENDYOL' ? data.get('userAgentIdentity') : null, shopifyAccessToken: createPlatform === 'SHOPIFY' ? data.get('shopifyAccessToken') : null, hepsiburadaIntegratorUsername: createPlatform === 'HEPSIBURADA' ? data.get('hepsiburadaIntegratorUsername') : null, hepsiburadaServiceKey: createPlatform === 'HEPSIBURADA' ? data.get('hepsiburadaServiceKey') : null, n11AppKey: createPlatform === 'N11' ? data.get('n11AppKey') : null, n11AppSecret: createPlatform === 'N11' ? data.get('n11AppSecret') : null, pazaramaClientId: createPlatform === 'PAZARAMA' ? data.get('pazaramaClientId') : null, pazaramaClientSecret: createPlatform === 'PAZARAMA' ? data.get('pazaramaClientSecret') : null }) }
  return <section className="content f3 integrations-reference-page">
    <div className="integration-reference-heading rv-page-head"><div><p className="eyebrow">Bağlantılar</p><h1>Entegrasyonlar</h1><p className="rv-page-lead">Pazaryeri ve e-fatura bağlantılarınızı yönetin, eşitleme ayarlarını düzenleyin.</p></div><div className="integration-heading-actions"><div className="integration-filter-wrap"><button type="button" className="integration-filter-button" aria-expanded={filterOpen} onClick={() => setFilterOpen(value => !value)}><UiIcon name="listSortAscending" /> Filtrele</button>{filterOpen && <div className="integration-filter-menu" role="menu" aria-label="Platform ve bağlantı durumu filtresi"><strong>Bağlantı Durumu</strong>{(['ALL', 'ACTIVE', 'ATTENTION'] as const).map(value => <button type="button" role="menuitemradio" aria-checked={filter === value} key={value} className={filter === value ? 'active' : ''} onClick={() => setFilter(value)}><i aria-hidden="true" />{value === 'ALL' ? 'Tüm Durumlar' : value === 'ACTIVE' ? 'Aktif Bağlantılar' : 'Dikkat Gerekenler'}</button>)}<strong className="integration-filter-menu-group">Platform</strong>{(['ALL', 'TRENDYOL', 'TRENDYOL_EFATURAM', 'SHOPIFY', 'HEPSIBURADA', 'N11', 'PAZARAMA'] as const).map(value => <button type="button" role="menuitemradio" aria-checked={platformFilter === value} key={value} className={platformFilter === value ? 'active' : ''} onClick={() => setPlatformFilter(value)}><i aria-hidden="true" />{value === 'ALL' ? 'Tüm Platformlar' : value === 'TRENDYOL' ? 'Trendyol' : value === 'SHOPIFY' ? 'Shopify' : value === 'HEPSIBURADA' ? 'Hepsiburada' : value === 'N11' ? 'N11' : value === 'PAZARAMA' ? 'Pazarama' : 'Trendyol E-Faturam'}</button>)}</div>}</div><button type="button" className="integration-add-button" onClick={() => setCreateOpen(true)}><UiIcon name="plus" /> Ekle</button></div></div>
    <Tabs className="integration-view-tabs rv-view-tabs" ariaLabel="Entegrasyon görünümü" value={view} onChange={nextValue => setView(nextValue as 'ALL' | 'ACTIVE')} items={[{ value: 'ALL', label: viewTabLabel('all', 'Tümü') }, { value: 'ACTIVE', label: viewTabLabel('connected', 'Bağlı Olanlar') }]} />
    {query.isLoading ? <Busy /> : query.isError ? <ErrorBox error={query.error} onRetry={() => void query.refetch()} /> : <>
      <div className="integration-filter-summary">{filterLabel}<span>{visibleConnections.length} bağlantı</span></div>
      <div className="integration-grid">{visibleConnections.map(item => <ReferenceConnectionCard item={item} onFeedback={setFeedback} onSaved={() => { void client.invalidateQueries({ queryKey: ['connections'] }); void client.invalidateQueries({ queryKey: ['orders'] }); void client.invalidateQueries({ queryKey: ['dashboard-bootstrap'] }); void client.invalidateQueries({ queryKey: ['dashboard-revenue-series'] }) }} key={item.id} />)}</div>
      {!visibleConnections.length && <Empty>Bu filtreye uyan bağlantı bulunmuyor.</Empty>}
      {createOpen && <div className="integration-modal-backdrop" role="presentation" onMouseDown={() => setCreateOpen(false)}><form className="integration-modal im-sade" role="dialog" aria-modal="true" aria-label="Yeni bağlantı" onSubmit={submitCreate} onMouseDown={event => event.stopPropagation()}>
        <div className="integration-modal-header"><div><h2>Yeni Bağlantı</h2><p>Platformu seçin, mağaza ve API bilgilerini girin.</p></div><button type="button" className="modal-close" onClick={() => setCreateOpen(false)} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></div>
        <div className="im-platforms" role="radiogroup" aria-label="Platform">{([['TRENDYOL', 'Trendyol'], ['SHOPIFY', 'Shopify'], ['HEPSIBURADA', 'Hepsiburada'], ['N11', 'N11'], ['PAZARAMA', 'Pazarama'], ['TRENDYOL_EFATURAM', 'E-Faturam']] as const).map(([code, name]) => <button type="button" role="radio" aria-checked={createPlatform === code} key={code} className={createPlatform === code ? 'is-on' : ''} onClick={() => setCreatePlatform(code)}><img className={platformLogoClass(code)} src={platformLogoSource(code) ?? '/platforms/trendyol.png'} alt="" aria-hidden="true" /><span>{name}</span></button>)}</div><div className="im-fields">
        <label>{createPlatform === 'HEPSIBURADA' ? 'Mağaza Adı' : 'Bağlantı Adı'}<input name="displayName" placeholder={createPlatform === 'TRENDYOL' ? 'Örn. Trendyol Canlı' : createPlatform === 'HEPSIBURADA' ? 'Hepsiburada mağaza adı' : undefined} required /></label>
        {createPlatform === 'SHOPIFY' || createPlatform === 'N11' || createPlatform === 'PAZARAMA' ? null : <label>Ortam<select name="environment" defaultValue="STAGE"><option value="STAGE">Stage / SIT</option><option value="PRODUCTION">Canlı</option></select></label>}
        <label>{createPlatform === 'TRENDYOL' ? 'Mağaza Kimliği' : createPlatform === 'SHOPIFY' ? 'Shopify Mağaza Adresi' : createPlatform === 'HEPSIBURADA' ? 'Hepsiburada Mağaza ID' : createPlatform === 'N11' ? 'N11 Mağaza Adı' : createPlatform === 'PAZARAMA' ? 'Pazarama Mağaza Adı' : 'Yerel Bağlantı Kapsamı'}<input name="externalStoreId" placeholder={createPlatform === 'TRENDYOL' ? 'Mağaza ID' : createPlatform === 'SHOPIFY' ? 'magazaniz.myshopify.com' : createPlatform === 'HEPSIBURADA' ? 'Mağaza ID' : createPlatform === 'N11' ? 'N11’deki mağaza adınız' : createPlatform === 'PAZARAMA' ? 'Pazarama’daki mağaza adınız' : 'Ravencia - Ravencia'} required />{createPlatform === 'PAZARAMA' && <small>Panelde bağlantıyı ayırt etmek için kullanılır; Pazarama API’si mağaza adını doğrulamaz.</small>}{createPlatform === 'N11' && <small>N11’de görünen mağaza adını yazın. Bağlantı testi API anahtarının bu mağazaya ait olduğunu kontrol eder.</small>}{createPlatform === 'SHOPIFY' && <small>Mağaza kısa adı veya tam myshopify.com adresi kullanılabilir.</small>}{createPlatform === 'HEPSIBURADA' && <small>Hepsiburada Entegratör Bilgileri ekranındaki mağaza ID’yi girin. SIT ve canlı bağlantılar için ayrı kayıt oluşturun.</small>}</label>
        {createPlatform === 'SHOPIFY' && <label>Shopify Uygulama Tokenı<input name="shopifyAccessToken" type="password" autoComplete="off" placeholder="shpat_…" required /><small>Shopify Admin API tokenı şifreli saklanır ve tekrar gösterilmez.</small></label>}
        {createPlatform === 'HEPSIBURADA' && <><label>Hepsiburada Entegratör Kullanıcı Adı<input name="hepsiburadaIntegratorUsername" autoComplete="username" required /><small>Hepsiburada Partner panelinde bu mağaza için eklediğiniz Entegratör Bilgileri kullanıcı adı. Mağaza ID’sinden farklıdır.</small></label><label>Hepsiburada Servis Anahtarı<input name="hepsiburadaServiceKey" type="password" autoComplete="new-password" required /><small>Servis anahtarı aynı entegratör kaydından alınmalıdır. Her iki değer şifreli saklanır.</small></label></>}
        {createPlatform === 'PAZARAMA' && <><label>Pazarama Client ID<input name="pazaramaClientId" autoComplete="off" required /><small>Pazarama satıcı panelinde Hesabım → Entegrasyon Bilgileri altından alınır.</small></label><label>Pazarama Client Secret<input name="pazaramaClientSecret" type="password" autoComplete="new-password" required /><small>Şifreli saklanır ve tekrar gösterilmez. Pazarama bu değeri yalnız bir kez gösterir; panelde yeni anahtar üretilirse eskisi anında geçersiz olur.</small></label></>}
        {createPlatform === 'N11' && <><label>N11 API Anahtarı (App Key)<input name="n11AppKey" autoComplete="off" required /><small>N11 satıcı panelinde Hesabım → API Hesapları altından alınır.</small></label><label>N11 API Şifresi (App Secret)<input name="n11AppSecret" type="password" autoComplete="new-password" required /><small>Her iki değer şifreli saklanır ve tekrar gösterilmez.</small></label></>}
        {createPlatform === 'TRENDYOL' && <label>User-Agent Kimliği<input name="userAgentIdentity" placeholder="Firma - Entegrasyon" required /></label>}
        </div><div className="im-foot"><button type="button" className="secondary" onClick={() => setCreateOpen(false)}>Vazgeç</button><button type="submit" className="connection-button" disabled={create.isPending}>{create.isPending ? 'Oluşturuluyor…' : 'Bağlantıyı Oluştur'}</button></div>
      </form></div>}
    </>}
     {feedback && <IntegrationFeedbackToast feedback={feedback} onClose={() => setFeedback(null)} />}
  </section>
}

type SyncPolicyEditorModalProps = {
  policy: SyncPolicy
  label: string
  intervals: Array<[number, string]>
  overlaps: Array<[number, string]>
  healthHelp: string
  purpose: string
  externalWriteBlocked: boolean
  saving: boolean
  onClose: () => void
  onSave: (values: { intervalSeconds: number; overlapSeconds: number; enabled: boolean }) => void
}

function SyncPolicyEditorModal({ policy, label, intervals, overlaps, healthHelp, purpose, externalWriteBlocked, saving, onClose, onSave }: SyncPolicyEditorModalProps) {
  const availableIntervals = syncPolicyIntervalChoices(intervals, policy.requiresExternalWrites === true)
  const minimumInterval = policy.requiresExternalWrites === true ? 0 : 30
  const [intervalSeconds, setIntervalSeconds] = useState(Math.max(minimumInterval, policy.intervalSeconds))
  const [overlapSeconds, setOverlapSeconds] = useState(policy.overlapSeconds)
  const [enabled, setEnabled] = useState(policy.enabled)
  const effectiveEnabled = enabled && !externalWriteBlocked
  const number = (value: number | undefined) => (value ?? 0).toLocaleString('tr-TR')
  const timeLabel = (value: string | null | undefined) => value ? <DateText value={value ?? null} /> : 'Henüz yok'
  const recoveryGapLabel = ({ OK: 'Normal', WARNING: 'Uyarı', CRITICAL: 'Kritik', UNKNOWN: 'Bilinmiyor' } as Record<string, string>)[policy.recoveryGapStatus ?? 'UNKNOWN'] ?? 'Bilinmiyor'
  const cursorProgressLabel = ({ UNKNOWN: 'Henüz ölçülemedi', NO_DATA: 'Bu kontrolde yeni kayıt yok', CURRENT: 'İmleç ilerliyor', STALLED: 'Kayıt geliyor, imleç ilerlemedi' } as Record<string, string>)[policy.cursorProgressStatus ?? 'UNKNOWN'] ?? 'Henüz ölçülemedi'
  const cursorStalled = policy.cursorProgressStatus?.trim().toUpperCase() === 'STALLED'
  return <div className="workspace-modal-backdrop sync-policy-modal-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) onClose() }}><section className="workspace-modal sync-policy-modal sp-sade" role="dialog" aria-modal="true" aria-label={`${label} ayarları`} onMouseDown={event => event.stopPropagation()}><header><div><h2>{label}</h2><p>{purpose}</p></div><button type="button" className="modal-close" onClick={onClose} aria-label="Akış ayarlarını kapat"><UiIcon name="close" /></button></header><form className="sync-policy-modal-form" onSubmit={event => { event.preventDefault(); onSave({ intervalSeconds, overlapSeconds, enabled }) }}><div className="sync-policy-modal-body"><label className="sync-policy-modal-state cs-switch-row"><span><strong>Otomatik Çalışma</strong><small>{externalWriteBlocked ? 'Dış yazma kapalı olduğu için bu akış çalıştırılmaz.' : healthHelp}</small></span><em className="cs-switch-state">{effectiveEnabled ? 'Açık' : 'Kapalı'}</em><input type="checkbox" role="switch" className="cs-switch" checked={effectiveEnabled} onChange={event => setEnabled(event.target.checked)} disabled={externalWriteBlocked} /></label><div className="sync-policy-modal-fields"><label><span>Kontrol Sıklığı</span><select value={intervalSeconds} onChange={event => setIntervalSeconds(Number(event.target.value))} disabled={externalWriteBlocked}>{!availableIntervals.some(([value]) => value === intervalSeconds) && <option value={intervalSeconds}>{intervalSeconds} saniye</option>}{availableIntervals.map(([value, optionLabel]) => <option key={value} value={value}>{optionLabel}</option>)}</select></label><label><span>Tekrar Payı</span><select value={overlapSeconds} onChange={event => setOverlapSeconds(Number(event.target.value))} disabled={externalWriteBlocked}>{!overlaps.some(([value]) => value === overlapSeconds) && <option value={overlapSeconds}>{Math.round(overlapSeconds / 60)} dakika</option>}{overlaps.map(([value, optionLabel]) => <option key={value} value={value}>{optionLabel}</option>)}</select></label></div>{cursorStalled && <SyncPolicyStalledAlert cursorStagnantSince={policy.cursorStagnantSince} lastCursorAdvancedAt={policy.lastCursorAdvancedAt} />}<section className="sync-policy-modal-telemetry" aria-label="Senkronizasyon ölçümleri"><h3>Son Senkronizasyon Ölçümü</h3><dl><div><dt>Son Deneme</dt><dd>{timeLabel(policy.lastAttemptAt)}</dd></div><div><dt>Son Başarılı</dt><dd>{timeLabel(policy.lastSuccessAt)}</dd></div><div><dt>Son İmleç İlerlemesi</dt><dd>{timeLabel(policy.lastCursorAdvancedAt)}</dd></div><div><dt>Durgunluk Takibi Başlangıcı</dt><dd>{timeLabel(policy.cursorStagnantSince)}</dd></div><div><dt>İmleç Durumu</dt><dd>{cursorProgressLabel}</dd></div><div><dt>Süre</dt><dd>{policy.lastDurationMs == null ? '—' : `${number(policy.lastDurationMs)} ms`}</dd></div><div><dt>Ardışık Hata</dt><dd>{number(policy.consecutiveFailureCount)}</dd></div><div><dt>API İsteği</dt><dd>{number(policy.lastRequestCount)}</dd></div><div><dt>Alınan / Değişen</dt><dd>{number(policy.lastReceivedCount)} / {number(policy.lastChangedCount)}</dd></div><div><dt>Eklenen / Güncellenen</dt><dd>{number(policy.lastInsertedCount)} / {number(policy.lastUpdatedCount)}</dd></div><div><dt>Atlanan / Hatalı</dt><dd>{number(policy.lastSkippedCount)} / {number(policy.lastFailedCount)}</dd></div><div><dt>Tekrar / HTTP 429</dt><dd>{number(policy.lastRetryCount)} / {number(policy.lastRateLimitCount)}</dd></div><div><dt>Kurtarma Aralığı</dt><dd>{recoveryGapLabel}{policy.recoveryGapDays == null ? '' : ` · ${number(Math.round(policy.recoveryGapDays * 10) / 10)} gün`}</dd></div></dl></section><section className="sync-policy-modal-telemetry sync-policy-modal-backlog" aria-label="Bağlantı kuyruğu"><h3>Bağlantı Kuyruğu · Tüm Akışlar</h3><dl><div><dt>Açık Kuyruk İşi</dt><dd>{number(policy.connectionBacklogCount)}</dd></div><div><dt>En Eski Açık İş</dt><dd>{timeLabel(policy.connectionOldestBacklogAt)}</dd></div><div><dt>Manuel İnceleme</dt><dd>{number(policy.connectionManualReviewCount)}</dd></div><div><dt>Son 24 Saatte Ölen İş</dt><dd>{number(policy.connectionDeadJobCount24h)}</dd></div></dl></section></div><footer><button type="button" className="secondary" onClick={onClose}>Vazgeç</button><button type="submit" disabled={saving || externalWriteBlocked}>{saving ? 'Kaydediliyor…' : 'Ayarı Kaydet'}</button></footer></form></section></div>
}

// First controlled n11 write: one stock code at a time, then everything that
// changed. Both go through the regular price-stock job and its n11 task check.
// Every Pazarama package action changes the live store; the dialog says what it will do before it is queued.
function pazaramaActionWarning(action: string) {
  switch (action) {
    case 'PICKING': return 'Sipariş Pazarama’da “Hazırlanıyor (12)” durumuna alınacak. Anlaşmalı kargo süreci başlar; kargoya verilmezse otomatik iade olur ve satıcı puanı düşer. Devam edilsin mi?'
    case 'NOT_SUPPLIED': return 'Ürünler “Tedarik Edilemedi” olarak işaretlenecek ve müşteriye para iadesi yapılacak. Bu işlem geri alınamaz. Devam edilsin mi?'
    case 'CANCEL_APPROVE': return 'Siparişin karar bekleyen iptal talepleri ONAYLANACAK; müşteriye para iadesi yapılır. Devam edilsin mi?'
    case 'CANCEL_REJECT': return 'Siparişin karar bekleyen iptal talepleri REDDEDİLECEK; talep Pazarama yönetimine düşer. Devam edilsin mi?'
    default: return 'Bu işlem Pazarama’ya gönderilecek. Devam edilsin mi?'
  }
}

// Pazarama: close mapped products for sale (or reopen them) by barcode. A closed product takes no orders and no price/stock updates.
function PazaramaSaleStatusPanel({ connectionId, writesEnabled, connectionActive }: { connectionId: string; writesEnabled: boolean; connectionActive: boolean }) {
  const [barcodes, setBarcodes] = useState('')
  const [feedback, setFeedback] = useState<IntegrationFeedback | null>(null)
  const codes = barcodes.split(/[\s,;]+/).map(code => code.trim()).filter(Boolean)
  const send = useMutation({
    mutationFn: (close: boolean) => hubApi<{ jobId?: string }>(`/connections/${connectionId}/sale-status-jobs`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify({ barcodes: codes, close }) }),
    onSuccess: (_, close) => setFeedback({ kind: 'success', message: `${codes.length} barkod için satışa ${close ? 'kapatma' : 'açma'} isteği kuyruğa alındı. Sonucu İşlem Takibi’nde görebilirsiniz.` }),
    onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Satışa açma/kapatma başlatılamadı.' })
  })
  const blocked = !writesEnabled || !connectionActive || codes.length === 0 || codes.length > 100 || send.isPending
  const confirmAndSend = (close: boolean) => { if (window.confirm(close ? `${codes.length} ürün Pazarama’da satışa KAPATILACAK; kapalı ürün sipariş almaz. Devam edilsin mi?` : `${codes.length} ürün Pazarama’da satışa AÇILACAK. Devam edilsin mi?`)) send.mutate(close) }
  return <section className="panel n11-price-stock-send">
    <h2>Pazarama’da Satışa Aç / Kapat</h2>
    <p>{!connectionActive ? 'İşlem için bağlantı aktif veya doğrulanmış olmalı.' : !writesEnabled ? 'İşlem için yukarıdaki dış yazma anahtarını açın.' : 'Panelde bir varyanta bağlı Pazarama barkodlarını girin (en fazla 100; boşluk, virgül veya satır sonuyla ayırın).'}</p>
    <label>Barkodlar<textarea value={barcodes} onChange={event => setBarcodes(event.target.value)} rows={3} placeholder="Örn. 1101010103531" autoComplete="off" /></label>
    <div className="n11-price-stock-send-actions">
      <button type="button" className="secondary" disabled={blocked} onClick={() => confirmAndSend(true)}>Satışa Kapat</button>
      <button type="button" disabled={blocked} onClick={() => confirmAndSend(false)}>Satışa Aç</button>
    </div>
    <IntegrationFeedbackToast feedback={feedback} onClose={() => setFeedback(null)} />
  </section>
}

type PazaramaFinance = { readAt: string; transactions: Array<{ transactionCode: string; status: string; amount: number; commission: number; allowance: number; couponDiscount: number; integrationAmount: number; cargoDebt: number; stoppage: number; installments: number; transactionDate: string | null; transferredDate: string | null }>; commissionInvoices: Array<{ orderNumber: string; invoiceNumber: string | null; invoiceDate: string | null; vatAmount: number; operationStatus: string | null }> }

// Pazarama finance of one order (commission, allowance, transfer date): a live read when asked for, never stored.
function PazaramaFinancePanel({ orderId }: { orderId: string }) {
  const [requested, setRequested] = useState(false)
  const finance = useQuery({ queryKey: ['pazarama-finance', orderId], queryFn: () => hubApi<PazaramaFinance>(`/orders/${orderId}/finance`), enabled: requested, retry: false, staleTime: 60_000 })
  const money = (value: number) => value.toLocaleString('tr-TR', { style: 'currency', currency: 'TRY' })
  const day = (value: string | null) => value ? new Date(value).toLocaleDateString('tr-TR') : '—'
  return <article className="panel">
    <h2>Pazarama finans</h2>
    <p>Komisyon, hakediş ve aktarım tarihi Pazarama’dan o anda okunur; panelde saklanmaz.</p>
    {!requested && <button type="button" onClick={() => setRequested(true)}>Finans bilgisini getir</button>}
    {requested && finance.isLoading && <Busy />}
    {requested && finance.isError && <ErrorBox error={finance.error} onRetry={() => void finance.refetch()} />}
    {finance.data && (finance.data.transactions.length === 0
      ? <p role="status">Pazarama bu sipariş için henüz finans hareketi döndürmedi.</p>
      : <dl className="details">{finance.data.transactions.map(row => <Fragment key={row.transactionCode}>
        <dt>{row.status} · {day(row.transactionDate)}</dt>
        <dd>Tutar {money(row.amount)} · Komisyon {money(row.commission)} · Hakediş {money(row.allowance)} · Aktarım {row.transferredDate ? day(row.transferredDate) : 'bekliyor'}<br /><small>Kargo {money(row.cargoDebt)} · Entegrasyon {money(row.integrationAmount)} · Stopaj {money(row.stoppage)} · Kupon {money(row.couponDiscount)}</small></dd>
      </Fragment>)}</dl>)}
    {finance.data && finance.data.commissionInvoices.length > 0 && <>
      <h3>Komisyon faturaları</h3>
      <ul className="plain-list">{finance.data.commissionInvoices.map(invoice => <li key={invoice.invoiceNumber ?? invoice.orderNumber}><strong>{invoice.invoiceNumber ?? '—'}</strong><span>{invoice.operationStatus ?? ''} · KDV {money(invoice.vatAmount)} · {day(invoice.invoiceDate)}</span></li>)}</ul>
    </>}
  </article>
}

function PriceStockSendPanel({ connectionId, writesEnabled, connectionActive, platformName, codeLabel, codeAccusative, codeExample }: { connectionId: string; writesEnabled: boolean; connectionActive: boolean; platformName: string; codeLabel: string; codeAccusative: string; codeExample: string }) {
  const [stockCode, setStockCode] = useState('')
  const [feedback, setFeedback] = useState<IntegrationFeedback | null>(null)
  const send = useMutation({
    mutationFn: (code: string | null) => hubApi<{ jobId?: string }>(`/connections/${connectionId}/price-inventory-sync-jobs${code ? `?stockCode=${encodeURIComponent(code)}` : ''}`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: '{}' }),
    onSuccess: (_, code) => setFeedback({ kind: 'success', message: code ? `${code} için fiyat-stok gönderimi kuyruğa alındı. Sonucu İşlem Takibi’nde görebilirsiniz.` : `Değişen tüm ${platformName} fiyat ve stokları kuyruğa alındı. Sonucu İşlem Takibi’nde görebilirsiniz.` }),
    onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Fiyat-stok gönderimi başlatılamadı.' })
  })
  const blocked = !writesEnabled || !connectionActive
  return <section className="panel n11-price-stock-send">
    <h2>{platformName}’{platformName === 'Pazarama' ? 'ya' : 'e'} Fiyat ve Stok Gönder</h2>
    <p>{!connectionActive ? 'Gönderim için bağlantı aktif veya doğrulanmış olmalı.' : !writesEnabled ? (platformName === 'Pazarama' ? 'Gönderim için yukarıdaki dış yazma anahtarını açın.' : 'Gönderim için yukarıdaki dış yazma anahtarını ve fiyat-stok akışını açın.') : `İlk gönderimi tek bir ${codeLabel} ile yapıp sonucu ${platformName} panelinde kontrol edin; sorun yoksa değişen tüm ürünleri gönderin.`}</p>
    <form onSubmit={event => { event.preventDefault(); if (stockCode.trim()) send.mutate(stockCode.trim()) }}>
      <label>{platformName} {codeLabel.split(' ').map(word => word.charAt(0).toLocaleUpperCase('tr-TR') + word.slice(1)).join(' ')}<input value={stockCode} onChange={event => setStockCode(event.target.value)} placeholder={`Örn. ${codeExample}`} autoComplete="off" /></label>
      <div className="n11-price-stock-send-actions">
        <button type="submit" disabled={blocked || !stockCode.trim() || send.isPending}>{send.isPending ? 'Kuyruğa alınıyor…' : `Bu ${codeAccusative.charAt(0).toLocaleUpperCase('tr-TR')}${codeAccusative.slice(1)} Gönder`}</button>
        <button type="button" className="secondary" disabled={blocked || send.isPending} onClick={() => send.mutate(null)}>Değişen Tüm Ürünleri Gönder</button>
      </div>
    </form>
    <IntegrationFeedbackToast feedback={feedback} onClose={() => setFeedback(null)} />
  </section>
}

function N11PublishSettingsPanel({ connectionId, displayName, version, shipmentTemplate, preparingDay }: { connectionId: string; displayName: string; version: number; shipmentTemplate: string | null | undefined; preparingDay: number | null | undefined }) {
  const client = useQueryClient()
  const [template, setTemplate] = useState(shipmentTemplate ?? '')
  const [days, setDays] = useState(String(preparingDay ?? 3))
  const [feedback, setFeedback] = useState<IntegrationFeedback | null>(null)
  const templates = useQuery({ queryKey: ['n11-shipment-templates', connectionId], queryFn: () => hubApi<string[]>(`/connections/${connectionId}/n11-shipment-templates`), staleTime: 5 * 60_000, retry: false })
  const templateChoices = templates.data ?? []
  const parsedDays = Number(days)
  const daysValid = Number.isInteger(parsedDays) && parsedDays >= 1 && parsedDays <= 30
  const save = useMutation({
    mutationFn: () => hubApi<Connection>(`/connections/${connectionId}`, { method: 'PATCH', headers: { 'If-Match': `"v${version}"` }, body: JSON.stringify({ displayName, n11ShipmentTemplate: template.trim(), n11PreparingDay: parsedDays }) }),
    onSuccess: async result => { client.setQueryData(['connection', connectionId], result); await client.invalidateQueries({ queryKey: ['connections'] }); setFeedback({ kind: 'success', message: 'N11 ürün açma ayarları kaydedildi.' }) },
    onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'N11 ürün açma ayarları kaydedilemedi.' })
  })
  return <section className="panel n11-price-stock-send">
    <h2>N11 Ürün Açma Ayarları</h2>
    <p>N11’e yeni ürün açarken her ürüne bu kargo şablonu ve hazırlık süresi uygulanır. Şablonlar N11 satıcı panelinizden okunur.</p>
    <form onSubmit={event => { event.preventDefault(); if (daysValid) save.mutate() }}>
      <label>Kargo Şablonu{templateChoices.length ? <select value={template} onChange={event => setTemplate(event.target.value)}><option value="">Şablon seçin</option>{template && !templateChoices.includes(template) && <option value={template}>{template} (N11’de bulunamadı)</option>}{templateChoices.map(name => <option key={name} value={name}>{name}</option>)}</select> : <input value={template} onChange={event => setTemplate(event.target.value)} maxLength={100} placeholder={templates.isLoading ? 'N11’deki şablonlar yükleniyor…' : 'N11 satıcı panelindeki şablon adı'} autoComplete="off" />}{templates.isError && <small className="connection-settings-field-help">N11’deki şablonlar okunamadı; adı elle yazabilirsiniz.</small>}{templates.isSuccess && !templateChoices.length && <small className="connection-settings-field-help">N11 panelinde kargo şablonu bulunamadı; önce N11’de bir şablon oluşturun.</small>}</label>
      <label>Hazırlık Süresi (Gün)<input type="number" min={1} max={30} value={days} onChange={event => setDays(event.target.value)} /></label>
      <div className="n11-price-stock-send-actions">
        <button type="submit" disabled={!daysValid || save.isPending}>{save.isPending ? 'Kaydediliyor…' : 'Ayarları Kaydet'}</button>
      </div>
    </form>
    <IntegrationFeedbackToast feedback={feedback} onClose={() => setFeedback(null)} />
  </section>
}

type SettingsIconName = 'info' | 'sync' | 'write' | 'invoice' | 'key' | 'tool'
const settingsIconPaths: Record<SettingsIconName, string[]> = {
  info: ['M8 7.2v3.6', 'M8 5h.01'],
  sync: ['M13 6.5A5 5 0 0 0 4 4.5L3 6', 'M3 2.5V6h3.5', 'M3 9.5a5 5 0 0 0 9 2l1-1.5', 'M13 13.5V10H9.5'],
  write: ['M3 13h3l7-7-3-3-7 7z'],
  invoice: ['M4 2.5h6l2 2V13.5H4z', 'M6 7h4', 'M6 9.5h4'],
  key: ['m7.6 8.4 5.4-5.4', 'M11 5l1.5 1.5'],
  tool: ['M10.5 2.5a3 3 0 0 0-3.4 4l-4.6 4.6 2.4 2.4 4.6-4.6a3 3 0 0 0 4-3.4l-1.8 1.8-1.6-.4-.4-1.6z']
}
// Section icons in the same 1.6 line weight as the status shapes.
function SettingsIcon({ name }: { name: SettingsIconName }) {
  return <svg className="cs-icon" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{name === 'info' && <circle cx="8" cy="8" r="6.2" />}{name === 'key' && <circle cx="5.5" cy="10.5" r="3" />}{settingsIconPaths[name].map(path => <path key={path} d={path} />)}</svg>
}

function IntegrationDetailWorkspace({ id, inline = false, onClose }: { id: string; inline?: boolean; onClose?: () => void }) {
  const client = useQueryClient(); const [feedback, setFeedback] = useState<IntegrationFeedback | null>(null); const [syncPolicyOpen, setSyncPolicyOpen] = useState<SyncPolicy | null>(null); const connection = useQuery({ queryKey: ['connection', id], queryFn: () => hubApi<Connection>(`/connections/${id}`), refetchOnMount: 'always' }); const syncPolicies = useQuery({ queryKey: ['sync-policies', id], queryFn: () => hubApi<SyncPolicy[]>(`/connections/${id}/sync-policies`), enabled: supportsSyncPolicyManagement(connection.data?.platformCode ?? ''), refetchInterval: 30_000, refetchIntervalInBackground: false }); const capabilities = useQuery({ queryKey: ['capabilities', id], queryFn: () => hubApi<Capability[]>(`/connections/${id}/capabilities`), enabled: shouldLoadConnectionCapabilities(connection.data?.platformCode) });
  useEffect(() => {
    if (!feedback) return
    const timeout = window.setTimeout(() => setFeedback(null), 5000)
    return () => window.clearTimeout(timeout)
  }, [feedback])
  const run = useMutation({ mutationFn: (request: { path: string; body?: object; version?: number }) => hubApi<{ succeeded?: boolean; errorCode?: string; errorSummary?: string }>(request.path, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), ...(request.version ? { 'If-Match': `"v${request.version}"` } : {}) }, body: JSON.stringify(request.body ?? {}) }), onSuccess: async (result, request) => { const isConnectionTest = request.path.endsWith('/test-jobs'); if (isConnectionTest) { const succeeded = result.succeeded === true; setFeedback({ kind: succeeded ? 'success' : 'error', message: succeeded ? 'Bağlantı testi başarılı; yetenek kontrolleri yenilendi. Dış yazma yapılmadı.' : `Bağlantı testi başarısız${result.errorCode ? `: ${result.errorCode}` : ''}${result.errorSummary ? ` — ${result.errorSummary}` : '.'}` }) } else if (request.path.endsWith('/stage-test-order-jobs')) setFeedback({ kind: 'info', message: 'Stage test siparişi kuyruğa alındı; sonucu İşlem Takibi’nde görebilirsiniz.' }); else setFeedback({ kind: 'success', message: 'İşlem başarıyla tamamlandı.' }); const invalidations = [client.invalidateQueries({ queryKey: ['connection', id] })]; if (isConnectionTest) invalidations.push(client.invalidateQueries({ queryKey: ['capabilities', id] }), client.invalidateQueries({ queryKey: ['sync-policies', id] })); await Promise.all(invalidations) }, onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'İşlem tamamlanamadı.' }) })
   const saveSyncPolicy = useMutation({ mutationFn: (request: { policy: SyncPolicy; intervalSeconds: number; overlapSeconds: number; enabled: boolean; closeModal?: boolean }) => hubApi<SyncPolicy>('/connections/' + id + '/sync-policies/' + request.policy.resourceType, { method: 'PUT', headers: { 'If-Match': '"v' + request.policy.version + '"' }, body: JSON.stringify({ intervalSeconds: request.intervalSeconds, overlapSeconds: request.overlapSeconds, jitterSeconds: request.policy.jitterSeconds, enabled: request.enabled }) }), onSuccess: async (_, request) => { if (request.closeModal) setSyncPolicyOpen(null); setFeedback({ kind: 'success', message: request.closeModal ? 'Otomatik eşitleme ayarı güncellendi.' : 'Akış ' + (request.enabled ? 'açıldı.' : 'kapatıldı.') }); await client.invalidateQueries({ queryKey: ['sync-policies', id] }) }, onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Eşitleme ayarı güncellenemedi.' }) })
   const saveFlowGroup = useMutation({ mutationFn: (request: { policies: SyncPolicy[]; enabled: boolean; label: string }) => Promise.all(request.policies.map(policy => hubApi<SyncPolicy>('/connections/' + id + '/sync-policies/' + policy.resourceType, { method: 'PUT', headers: { 'If-Match': '"v' + policy.version + '"' }, body: JSON.stringify({ intervalSeconds: policy.intervalSeconds, overlapSeconds: policy.overlapSeconds, jitterSeconds: policy.jitterSeconds, enabled: request.enabled }) }))), onSuccess: async (_, request) => { setFeedback({ kind: 'success', message: `${request.label} ${request.enabled ? 'açıldı' : 'kapatıldı'}.` }); await client.invalidateQueries({ queryKey: ['sync-policies', id] }) }, onError: async reason => { setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Akışlar güncellenemedi.' }); await client.invalidateQueries({ queryKey: ['sync-policies', id] }) } })
   const saveExternalWriteGroup = useMutation({ mutationFn: (request: { policies: SyncPolicy[]; enabled: boolean }) => Promise.all(request.policies.map(policy => hubApi<SyncPolicy>('/connections/' + id + '/sync-policies/' + policy.resourceType, { method: 'PUT', headers: { 'If-Match': '"v' + policy.version + '"' }, body: JSON.stringify({ intervalSeconds: policy.intervalSeconds, overlapSeconds: policy.overlapSeconds, jitterSeconds: policy.jitterSeconds, enabled: request.enabled }) }))), onSuccess: async (_, request) => { setFeedback({ kind: 'success', message: `${connection.data?.displayName ?? 'Entegrasyon'} için dış yazma grubu ${request.enabled ? 'açıldı' : 'kapatıldı'}.`, details: [{ label: 'Entegrasyon', value: `${connection.data?.platformCode ?? ''} · ${connection.data?.displayName ?? id}` }, { label: 'Grup', value: request.policies.map(policy => policy.resourceType).join(', ') }], target: { href: `/integrations/${id}`, label: 'Entegrasyona Git' } }); await client.invalidateQueries({ queryKey: ['sync-policies', id] }) }, onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Dış yazma grubu güncellenemedi.' }) })
  async function credential(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!connection.data) return
    const data = new FormData(event.currentTarget)
    const body = connection.data.platformCode === 'TRENDYOL_EFATURAM'
      ? { email: data.get('email') || null, password: data.get('password') || null }
      : connection.data.platformCode === 'SHOPIFY'
        ? { shopifyAccessToken: data.get('shopifyAccessToken') }
        : connection.data.platformCode === 'HEPSIBURADA'
          ? { hepsiburadaIntegratorUsername: data.get('hepsiburadaIntegratorUsername'), hepsiburadaServiceKey: data.get('hepsiburadaServiceKey') }
          : { apiKey: data.get('apiKey'), apiSecret: data.get('apiSecret') }
    try {
      await hubApi(`/connections/${id}/credential`, { method: 'PUT', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${connection.data.version}"` }, body: JSON.stringify(body) })
      setFeedback({ kind: 'success', message: 'Kimlik bilgisi şifreli olarak yenilendi; değerler tekrar gösterilmeyecek.' })
      await client.invalidateQueries({ queryKey: ['connection', id] })
    } catch (reason) {
      setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Kimlik bilgisi kaydedilemedi.' })
    }
  }
  const rootClass = inline ? 'integration-inline-workspace' : 'content f3'
  const [externalWritesEnabled, setExternalWritesEnabled] = useState(false)
   const [externalStoreId, setExternalStoreId] = useState('')
   const [displayName, setDisplayName] = useState('')
  const [settingsSection, setSettingsSection] = useState('genel')
  const [flowFilter, setFlowFilter] = useState<'all' | 'issue' | 'wait' | 'off'>('all')
  useEffect(() => { if (connection.data) setExternalWritesEnabled(connection.data.externalWritesEnabled) }, [connection.data?.externalWritesEnabled])
   useEffect(() => { if (connection.data) setExternalStoreId(connection.data.platformCode === 'SHOPIFY' ? shopifyStoreDisplayName(connection.data.externalStoreId) : connection.data.externalStoreId) }, [connection.data?.externalStoreId])
   useEffect(() => { if (connection.data) setDisplayName(connection.data.displayName) }, [connection.data?.displayName])
  const saveConnectionDetails = useMutation({ mutationFn: () => { if (!connection.data) throw new Error('Bağlantı bilgisi yükleniyor.'); const name = displayName.trim(); const store = externalStoreId.trim(); if (!name) throw new Error('Kayıt adı zorunludur.'); if (!store) throw new Error('Mağaza adı zorunludur.'); return hubApi<Connection>(`/connections/${id}`, { method: 'PATCH', headers: { 'If-Match': `"v${connection.data.version}"` }, body: JSON.stringify({ displayName: name, externalStoreId: store }) }) }, onSuccess: async result => { setDisplayName(result.displayName); setExternalStoreId(result.platformCode === 'SHOPIFY' ? shopifyStoreDisplayName(result.externalStoreId) : result.externalStoreId); setFeedback({ kind: 'success', message: 'Kayıt adı ve mağaza bilgileri güncellendi. Yeniden bağlantı testi gerekiyor.' }); await client.invalidateQueries({ queryKey: ['connection', id] }); await client.invalidateQueries({ queryKey: ['connections'] }) }, onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Bağlantı bilgileri güncellenemedi.' }) })
  const saveExternalWrites = useMutation({ mutationFn: (enabled: boolean) => { if (!connection.data) throw new Error('Bağlantı bilgisi yükleniyor.'); return hubApi<Connection>(`/connections/${id}`, { method: 'PATCH', headers: { 'If-Match': `"v${connection.data.version}"` }, body: JSON.stringify({ displayName: connection.data.displayName, externalWritesEnabled: enabled }) }) }, onSuccess: async (_, enabled) => { setExternalWritesEnabled(enabled); setFeedback({ kind: 'success', message: enabled ? `${connection.data?.displayName ?? 'Entegrasyon'} için dış yazma açıldı.` : `${connection.data?.displayName ?? 'Entegrasyon'} salt-okunur moda alındı; dış yazma ve bağlı otomatik akışlar kapatıldı.`, details: [{ label: 'Entegrasyon', value: `${connection.data?.platformCode ?? ''} · ${connection.data?.displayName ?? id}` }], target: { href: `/integrations/${id}`, label: 'Entegrasyona Git' } }); await client.invalidateQueries({ queryKey: ['connection', id] }); await client.invalidateQueries({ queryKey: ['capabilities', id] }); await client.invalidateQueries({ queryKey: ['connections'] }); await client.invalidateQueries({ queryKey: ['sync-policies', id] }) }, onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Dış yazma ayarı güncellenemedi.' }) })
  const saveInvoiceCreation = useMutation({ mutationFn: (enabled: boolean) => { if (!connection.data) throw new Error('Bağlantı bilgisi yükleniyor.'); return hubApi<Connection>(`/connections/${id}`, { method: 'PATCH', headers: { 'If-Match': `"v${connection.data.version}"` }, body: JSON.stringify({ displayName: connection.data.displayName, invoiceCreationEnabled: enabled }) }) }, onSuccess: async result => { client.setQueryData(['connection', id], result); setFeedback({ kind: 'success', message: result.invoiceCreationEnabled ? 'Bu bağlantı için fatura oluşturma açıldı.' : 'Bu bağlantı için fatura oluşturma kapatıldı; mevcut belgeler ve fatura kayıtları korunuyor.' }); await Promise.all([client.invalidateQueries({ queryKey: ['connections'] }), client.invalidateQueries({ queryKey: ['orders'] }), client.invalidateQueries({ queryKey: ['invoice-workspace'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-summary'] }), client.invalidateQueries({ queryKey: ['invoice-workspace-page'] })]) }, onError: reason => setFeedback({ kind: 'error', message: reason instanceof Error ? reason.message : 'Fatura oluşturma ayarı güncellenemedi.' }) })
  if (connection.isLoading) return <div className={rootClass}><Busy /></div>; if (connection.isError || !connection.data) return <div className={rootClass}><ErrorBox error={connection.error} onRetry={() => void connection.refetch()} /></div>; const item = connection.data
  if (!activePlatformCodes.has(item.platformCode)) return <div className={rootClass}><Link to="/integrations" className="back"><UiIcon name="arrowLeft" /> Platformlar</Link><div className="unknown"><strong>Bağlantı aktif kapsam dışında</strong><p>Bu platform için işlem paneli henüz etkin değil. Kapsam dışı tarihsel bağlantılara işlem sunulmaz.</p></div></div>
  const stageManual = item.platformCode !== 'SHOPIFY' && item.environment === 'STAGE'; const displayEnvironment = item.platformCode === 'SHOPIFY' ? 'PRODUCTION' : item.environment; const readConnectionReady = item.status === 'ACTIVE' || item.status === 'VERIFIED'; const stageReady = stageManual && !item.lastErrorCode && readConnectionReady
   const efaturam = item.platformCode === 'TRENDYOL_EFATURAM'
   const externalWritePlatform = item.platformCode === 'SHOPIFY' ? 'Shopify' : item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada' : item.platformCode === 'N11' ? 'N11' : item.platformCode === 'PAZARAMA' ? 'Pazarama' : efaturam ? 'E-Faturam' : 'Trendyol'
   const externalWriteGroups: Array<{ key: string; label: string; resourceTypes: string[]; description: string; capabilityCodes?: string[] }> = item.platformCode === 'SHOPIFY'
     ? [
         { key: 'automatic-external-writes', label: 'Otomatik Dış Yazmalar', resourceTypes: ['PRICE_WRITE', 'STOCK_WRITE'], description: 'Açıkken paneldeki fiyat ve stok değişiklikleri Shopify mağazasına gönderilir.', capabilityCodes: ['PRICE_WRITE', 'INVENTORY_WRITE'] },
         { key: 'manual-external-writes', label: 'Manuel Dış Yazmalar', resourceTypes: ['SHIPMENT_WRITE', 'RETURN_WRITE'], description: 'Mevcut fulfillment kargo takip bilgisi ve iade onay/ret kararları Shopify’a gönderilir. Shopify Admin API bu panelden taşıyıcı etiketi satın almaz veya üretmez.', capabilityCodes: ['SHIPMENT_WRITE', 'RETURN_READ', 'RETURN_WRITE'] }
       ]
     : item.platformCode === 'PAZARAMA'
     ? [
         { key: 'automatic-external-writes', label: 'Otomatik Dış Yazmalar', resourceTypes: ['PRICE_WRITE', 'STOCK_WRITE'], description: 'Açıkken paneldeki Pazarama fiyatı ve stok değişikliklerini Pazarama’ya otomatik gönderir. Kapalıyken sistem bu güncellemeleri kendiliğinden göndermez; panelden elle başlattığınız fiyat-stok gönderimi bu ayardan bağımsızdır, yalnız dış yazma anahtarı açık olmalıdır. Ürün açma ve iade kararları sonraki aşamalarda açılacak.' },
         { key: 'manual-external-writes', label: 'Manuel Dış Yazmalar', resourceTypes: ['SHIPMENT_WRITE', 'RETURN_WRITE'], description: 'Açıkken panelden başlattığınız sipariş işleme alma, tedarik edilemedi, paket bölme/güncelleme, işçilik bedeli, iptal talebi kararlarını ve iade onay/retlerini Pazarama’ya gönderir. Kapalıyken bu işlemler Pazarama’ya aktarılmaz.' }
       ]
     : item.platformCode === 'N11'
     ? [
         { key: 'automatic-external-writes', label: 'Otomatik Dış Yazmalar', resourceTypes: ['PRICE_WRITE', 'STOCK_WRITE'], description: 'Açıkken paneldeki N11 fiyatı ve stok değişikliklerini N11’e otomatik gönderir. Kapalıyken bu güncellemeler N11’e aktarılmaz.' },
         { key: 'manual-external-writes', label: 'Manuel Dış Yazmalar', resourceTypes: ['SHIPMENT_WRITE', 'RETURN_WRITE'], description: 'Açıkken panelden başlattığınız sipariş işleme alma, paket bölme ve iade onaylarını N11’e gönderir. İade reddi N11 satıcı panelinden yapılır. Kapalıyken bu işlemler N11’e aktarılmaz.' }
       ]
     : item.platformCode === 'HEPSIBURADA'
     ? [
         { key: 'automatic-external-writes', label: 'Otomatik Dış Yazmalar', resourceTypes: ['PRICE_WRITE', 'STOCK_WRITE'], description: 'Açıkken paneldeki fiyat ve stok değişikliklerini Hepsiburada’ya otomatik gönderir. Kapalıyken bu güncellemeler Hepsiburada’ya aktarılmaz.', capabilityCodes: ['PRICE_WRITE', 'INVENTORY_WRITE'] },
         { key: 'manual-external-writes', label: 'Manuel Dış Yazmalar', resourceTypes: ['SHIPMENT_WRITE', 'RETURN_WRITE'], description: 'Açıkken panelden başlattığınız kargo, etiket, paket ve iade işlemlerini Hepsiburada’ya gönderir. Kapalıyken bu işlemler dış platforma aktarılmaz.', capabilityCodes: ['SHIPMENT_WRITE', 'RETURN_WRITE'] }
       ]
     : [
         { key: 'automatic-external-writes', label: 'Otomatik Dış Yazmalar', resourceTypes: ['PRICE_WRITE', 'STOCK_WRITE'], description: `Açıkken paneldeki fiyat ve stok değişikliklerini ${externalWritePlatform}’a otomatik gönderir. Kapalıyken bu güncellemeler ${externalWritePlatform}’a aktarılmaz.` },
         { key: 'manual-external-writes', label: 'Manuel Dış Yazmalar', resourceTypes: ['SHIPMENT_WRITE', 'RETURN_WRITE'], description: `Açıkken panelden başlattığınız kargo, etiket ve iade kararlarını ${externalWritePlatform}’a gönderir. Kapalıyken bu işlemler dış platforma aktarılmaz.` }
       ]
  const syncPolicyCategories: Array<{ key: string; label: string; resourceTypes: string[]; kind?: 'external-write-parent'; description?: string }> = [
    { key: 'orders', label: 'Sipariş Akışları', resourceTypes: ['ORDERS', 'ORDER_RECOVERY', 'ORDER_LIFECYCLE'] },
    { key: 'returns', label: 'İade Akışları', resourceTypes: ['RETURNS', 'RETURN_LIFECYCLE'] },
    { key: 'reconciliation', label: 'Uzlaştırma Akışları', resourceTypes: ['ORDER_RECONCILE_SHORT', 'ORDER_RECONCILE_MEDIUM', 'ORDER_RECONCILE_DAILY', 'RETURN_RECONCILE_SHORT', 'RETURN_RECONCILE_MEDIUM', 'RETURN_RECONCILE_DAILY', 'STOCK_RECONCILE_SHORT', 'STOCK_RECONCILE_MEDIUM', 'STOCK_RECONCILE_DAILY'] },
     { key: 'external-writes', label: 'Dış yazma akışları', resourceTypes: externalWriteGroups.flatMap(group => group.resourceTypes), kind: 'external-write-parent' },
    { key: 'invoices', label: 'Faturalama Akışları', resourceTypes: ['ORDER_INVOICE_RECONCILIATION'] },
    { key: 'reference', label: 'Referans Veri Akışları', resourceTypes: ['REFERENCE_DATA'] }
  ]
  const visibleSyncPolicies = syncPolicies.data?.filter(policy => !['PRODUCTS', 'PRODUCT_WRITE', 'PRICE_STOCK_WRITE'].includes(policy.resourceType) && supportsSyncPolicy(item.platformCode, policy.resourceType)) ?? []
  const categorizedResourceTypes = new Set(syncPolicyCategories.flatMap(category => category.resourceTypes))
  const uncategorizedSyncPolicies = visibleSyncPolicies.filter(policy => !categorizedResourceTypes.has(policy.resourceType))
   const syncPolicyGroups: Array<{ key: string; label: string; resourceTypes: string[]; kind?: 'external-write-parent'; description?: string; policies: SyncPolicy[] }> = [
     ...syncPolicyCategories.map(category => ({ ...category, policies: visibleSyncPolicies.filter(policy => category.resourceTypes.includes(policy.resourceType)) })).filter(group => group.policies.length && !(item.platformCode === 'SHOPIFY' && group.kind === 'external-write-parent')),
     ...(uncategorizedSyncPolicies.length ? [{ key: 'other', label: 'Diğer Akışlar', resourceTypes: [], policies: uncategorizedSyncPolicies }] : [])
   ]
  const renderExternalWriteGroup = (externalGroup: typeof externalWriteGroups[number]) => {
    const policies = visibleSyncPolicies.filter(policy => externalGroup.resourceTypes.includes(policy.resourceType))
    const requiredCapabilities = externalGroup.capabilityCodes?.map(code => ({ code, capability: capabilities.data?.find(value => value.code === code) })) ?? []
    const evidence = externalWriteEvidenceState(requiredCapabilities.map(value => ({ code: value.code, ...value.capability })), externalWritePlatform, capabilities.isLoading)
    const state = externalWriteGroupState(policies, item.externalWritesEnabled, evidence.blocked, capabilities.isLoading, evidence.label)

    return <article className={'sync-policy-row ' + (state.enabled ? 'is-enabled' : 'is-disabled') + (state.partiallyEnabled ? ' is-partial' : '')} key={externalGroup.key} data-external-write-group={externalGroup.key}><div className="sync-policy-card-summary"><strong>{externalGroup.label}</strong><small>{externalGroup.description}</small></div><label className="sync-policy-card-switch" title={[externalGroup.label, state.label, evidence.details].filter(Boolean).join(' · ')}><span>{state.label}</span><input type="checkbox" checked={state.configuredEnabled} disabled={saveExternalWriteGroup.isPending || state.disabled} onChange={event => saveExternalWriteGroup.mutate({ policies, enabled: event.target.checked })} aria-label={externalGroup.label + ' seçeneğini ' + (state.configuredEnabled ? 'kapat' : 'aç')} /><i aria-hidden="true" /></label></article>
  }
  const syncPolicyLabel = (resourceType: string) => resourceType === 'ORDER_RECONCILE_DAILY' && item.platformCode === 'SHOPIFY' ? 'Üç Günlük Sipariş Uzlaştırması' : ({ ORDERS: 'Siparişler ve Paket Durumları', ORDER_RECOVERY: 'Sipariş Geçmişi Kurtarma', ORDER_LIFECYCLE: 'Açık Sipariş Yaşam Döngüsü', ORDER_RECONCILE_SHORT: 'Kısa Sipariş Uzlaştırması', ORDER_RECONCILE_MEDIUM: 'Orta Sipariş Uzlaştırması', ORDER_RECONCILE_DAILY: 'Günlük Sipariş Uzlaştırması', ORDER_INVOICE_RECONCILIATION: 'Paket Fatura Kontrolü', RETURNS: 'İade Talepleri', RETURN_LIFECYCLE: 'Açık İade Yaşam Döngüsü', RETURN_RECONCILE_SHORT: 'Kısa İade Uzlaştırması', RETURN_RECONCILE_MEDIUM: 'Orta İade Uzlaştırması', RETURN_RECONCILE_DAILY: 'Günlük İade Uzlaştırması', STOCK_RECONCILE_SHORT: 'Kısa Stok Uzlaştırması', STOCK_RECONCILE_MEDIUM: 'Orta Stok Uzlaştırması', STOCK_RECONCILE_DAILY: 'Günlük Stok Uzlaştırması', PRICE_WRITE: 'Fiyat Gönderimleri', STOCK_WRITE: 'Stok Gönderimleri', SHIPMENT_WRITE: 'Kargo ve Etiket İşlemleri', RETURN_WRITE: 'İade Kararları', REFERENCE_DATA: 'Katalog Referansları', ORDER_CARGO_INFO: 'Kargo Bilgisi Eşitleme', QUESTIONS: 'Müşteri Soruları' } as Record<string, string>)[resourceType] ?? resourceType
  const syncPolicyIntervals: Array<[number, string]> = [[0, 'Anında'], [30, '30 saniye'], [60, '1 dakika'], [120, '2 dakika'], [180, '3 dakika'], [300, '5 dakika'], [480, '8 dakika'], [540, '9 dakika'], [600, '10 dakika'], [900, '15 dakika'], [1800, '30 dakika'], [2700, '45 dakika'], [3600, '1 saat'], [10800, '3 saat'], [86400, '24 saat'], [259200, '3 gün']]
  const syncPolicyOverlaps: Array<[number, string]> = [[120, '2 dakika'], [300, '5 dakika'], [600, '10 dakika'], [900, '15 dakika']]
  const syncPolicyHealthHelp = (policy: SyncPolicy) => {
    const success = policy.lastSuccessAt ? `Son başarılı kontrol: ${new Date(policy.lastSuccessAt).toLocaleString('tr-TR')}.` : 'Henüz başarılı kontrol kaydı yok.'
    const failures = [
      (policy.lastFailedCount ?? 0) > 0 ? `Son kontrolde ${policy.lastFailedCount} kayıt güncellenemedi.` : '',
      (policy.consecutiveFailureCount ?? 0) > 0 ? `${policy.consecutiveFailureCount} ardışık akış hatası var.` : ''
    ].filter(Boolean).join(' ')
    if (!policy.enabled) return `Bu akış kapalı. ${success}`
    if (policy.healthStatus === 'OFFLINE') return policy.lastSuccessAt
      ? `${success} Beklenen çalışma aralığı aşıldı; akış güncellenmedi. Pazaryeri bağlantısı açık olabilir, bu uyarı yalnızca bu akışın son çalışmasının eski olduğunu gösterir.`
      : 'Bu akış henüz başarıyla çalışmadı. İlk başarılı çalışması bekleniyor; bu durum tek başına pazaryeri bağlantısının kapalı olduğu anlamına gelmez.'
    return failures ? `${failures} ${success}` : `${success} Durum, son deneme ve kayıt hatalarına göre güncellenir.`
  }
  const syncPolicyDescription = (resourceType: string) => ({
    ORDERS: item.platformCode === 'SHOPIFY' ? 'Shopify siparişlerini okur ve ödeme, gönderim, iptal ve iade durumlarını panele taşır. İlk tarama son 3 takvim ayını kapsar; Shopify API erişimi read_all_orders izni yoksa son 60 günle sınırlıdır.' : item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada siparişlerini ve paket bilgilerini salt okunur olarak panele alır.' : 'Trendyol’daki yeni ve güncellenen siparişleri panele alır; ürün, müşteri, paket ve kargo bilgilerini yerel çalışma ekranlarında güncel tutar.',
     ORDER_RECOVERY: item.platformCode === 'SHOPIFY' ? 'Tam tarama Shopify’ın erişime açtığı tüm sipariş geçmişini yeniden okur. Varsayılan erişim son 60 günle sınırlıdır; daha eski kayıtlar için read_all_orders izni gerekir.' : item.platformCode === 'HEPSIBURADA' ? 'Normal taramada kaçırılan Hepsiburada siparişlerini ve paketlerini tamamlar. Sipariş ve paket listelemeleri offset ile en fazla 10 kayıtlık sayfalar halinde, tarih filtreleri de en fazla 24 saatlik aralıklarla çekilir.' : 'Normal sipariş taramasında kaçırılmış veya geçmiş tarihli siparişleri yeniden arayarak eksik kayıtları tamamlar.',
     ORDER_LIFECYCLE: item.platformCode === 'SHOPIFY' ? 'Açık Shopify siparişlerinin ödeme, paket, kargo ve teslimat durumlarını izler; değişiklikleri panelde günceller.' : item.platformCode === 'HEPSIBURADA' ? 'Açık Hepsiburada siparişlerinin paket ve taşıma durumlarını salt okunur sipariş detay servisiyle yeniler; her turda en fazla 25 sipariş kontrol edilir.' : 'Açık siparişlerin paket, kargo ve teslimat durumlarını izler; durum değişikliklerini panelde günceller.',
    ORDER_RECONCILE_SHORT: 'Yakın zamanda değişen siparişleri kısa aralıklarla kontrol ederek gecikmeli veya kaçırılmış durum değişikliklerini yakalar.',
    ORDER_RECONCILE_MEDIUM: 'Gün içinde açık kalan siparişleri orta sıklıkta tarayarak yerel sipariş durumunu Trendyol ile karşılaştırır.',
    ORDER_RECONCILE_DAILY: item.platformCode === 'SHOPIFY' ? 'Üç günde bir Shopify siparişlerini uzun dönemli uzlaştırır; uzun süre fark kalan kayıtları yeniden kontrol eder.' : 'Günlük uzlaştırma ile siparişlerin yerel ve Trendyol kayıtlarını yeniden karşılaştırır; uzun süre fark kalanları düzeltme taramasına alır.',
    ORDER_INVOICE_RECONCILIATION: item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada sipariş detayındaki hasInvoice bilgisini okuyarak fatura durumunu panelde güncel tutar.' : 'Paketlerin fatura durumunu kontrol eder; kesilen, bekleyen veya işlenemeyen faturaların paneldeki durumunu güncel tutar.',
    RETURNS: item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada’daki yeni ve güncellenen iade taleplerini salt okunur olarak panele alır.' : 'Trendyol’daki yeni ve güncellenen iade taleplerini panele alır; iade nedeni, ürün satırları ve aksiyon durumunu günceller.',
    RETURN_LIFECYCLE: 'Açık iadelerin kargo, teslim alma, inceleme ve sonuçlanma adımlarını takip eder.',
    RETURN_RECONCILE_SHORT: 'Yakın zamanda değişen iadeleri kısa aralıklarla kontrol ederek gecikmiş durumları yakalar.',
    RETURN_RECONCILE_MEDIUM: 'Açık iadeleri orta sıklıkta Trendyol ile karşılaştırır ve yerel iade kaydını güncel tutar.',
    RETURN_RECONCILE_DAILY: 'Günlük iade uzlaştırmasıyla uzun süre değişmeyen veya eksik kalan iade kayıtlarını yeniden doğrular.',
    STOCK_RECONCILE_SHORT: 'Paneldeki stok ile Trendyol stok bilgisini kısa aralıklarla karşılaştırır ve farkları tespit eder.',
    STOCK_RECONCILE_MEDIUM: 'Stok farklarını orta sıklıkta kontrol eder ve gün içindeki değişikliklerin kaçırılmasını önler.',
    STOCK_RECONCILE_DAILY: 'Gün sonunda stok kayıtlarını yeniden karşılaştırarak kalıcı farkları ve eksik güncellemeleri belirler.',
     PRICE_WRITE: `Panelde yapılan fiyat değişikliklerini ${externalWritePlatform}’a gönderir. Kapalıysa fiyat güncellemeleri dış platforma yazılmaz.`,
     STOCK_WRITE: `Paneldeki stok değişikliklerini ${externalWritePlatform}’a gönderir. Kapalıysa stok güncellemeleri dış platforma yazılmaz.`,
     SHIPMENT_WRITE: `Kargo firması, etiket ve paket dış yazma işlemlerini ${externalWritePlatform}’a aktarır.`,
     RETURN_WRITE: item.platformCode === 'SHOPIFY' ? 'İade kararlarını Shopify sipariş akışına aktarır; yanıt beklenerek paneldeki iade durumu güncellenir.' : 'İade kabul veya ret kararlarını Trendyol API’sine doğrudan gönderir; yanıt beklenerek paneldeki iade durumu güncellenir.',
     REFERENCE_DATA: `${externalWritePlatform} kategori, özellik ve diğer katalog referanslarını okuyarak eşleştirme ekranlarını güncel tutar.`
  } as Record<string, string>)[resourceType] ?? 'Bu akış, bağlantının ilgili veri veya dış yazma sürecini otomatik olarak yönetir.'
  // Flows as rows of one table: state from the health presentation, issues are late, partly failing or stalled flows.
  const flowGroups = syncPolicyGroups.filter(group => group.kind !== 'external-write-parent').map(group => ({ ...group, rows: group.policies.map(policy => {
    const externalWriteBlocked = policy.requiresExternalWrites === true && !item.externalWritesEnabled
    const effectiveEnabled = policy.enabled && !externalWriteBlocked
    const health = syncPolicyHealthPresentation(policy, effectiveEnabled)
    const issue = effectiveEnabled && (health.tone === 'delayed' || health.tone === 'degraded' || health.tone === 'stalled' || (health.tone === 'offline' && !!policy.lastSuccessAt))
    const waiting = effectiveEnabled && health.tone === 'offline' && !policy.lastSuccessAt
    const glyph: FieldState = health.tone === 'healthy' ? 'done' : issue ? 'partial' : 'none'
    return { policy, label: syncPolicyLabel(policy.resourceType), externalWriteBlocked, effectiveEnabled, health, issue, waiting, glyph }
  }) }))
  const allFlows = flowGroups.flatMap(group => group.rows)
  type FlowRow = typeof allFlows[number]
  const flowCounts = { all: allFlows.length, ok: allFlows.filter(row => row.glyph === 'done').length, issue: allFlows.filter(row => row.issue).length, wait: allFlows.filter(row => row.waiting).length, off: allFlows.filter(row => !row.effectiveEnabled).length }
  const flowShare = (count: number) => `${allFlows.length ? (count / allFlows.length) * 100 : 0}%`
  const passesFlowFilter = (row: FlowRow) => flowFilter === 'all' || (flowFilter === 'issue' && row.issue) || (flowFilter === 'wait' && row.waiting) || (flowFilter === 'off' && !row.effectiveEnabled)
  const attentionFlows = allFlows.filter(row => row.issue || row.waiting)
  const flowsAvailable = supportsSyncPolicyManagement(item.platformCode) && readConnectionReady
  const invoiceSupported = item.platformCode === 'TRENDYOL' || item.platformCode === 'SHOPIFY' || item.platformCode === 'N11' || item.platformCode === 'PAZARAMA'
  const writeSupported = item.platformCode === 'TRENDYOL' || item.platformCode === 'HEPSIBURADA' || item.platformCode === 'SHOPIFY' || item.platformCode === 'N11' || item.platformCode === 'PAZARAMA' || efaturam
  const stageTestAvailable = (item.platformCode === 'TRENDYOL' && item.environment === 'STAGE' && item.externalStoreId === '2738') || (item.platformCode === 'HEPSIBURADA' && item.environment === 'STAGE')
  const toolsAvailable = item.platformCode === 'N11' || item.platformCode === 'PAZARAMA' || stageTestAvailable || stageManual
  const overview = <>
    <div className="cs-stats"><div><small>Kimlik Bilgisi</small><b>{credentialLabel(item)}</b><span>{credentialHelp(item)}</span></div><div><small>Son Doğrulama</small><b><DateText value={item.lastTestedAt} /></b><span>{connectionTestHelp(item)}</span></div><div><small>{stageManual ? 'Stage İşlemleri' : 'Pazaryerine Yazma'}</small><b>{stageManual ? stageReady ? 'Hazır' : 'Bloke' : item.externalWritesEnabled ? 'Açık' : 'Kapalı'}</b><span>{stageManual ? stageReady ? 'Manuel denemeler doğrulamalarla çalışır.' : 'Bağlantı testi düzeltilmeden işlem başlatılmaz.' : 'Ana anahtar ve bağlantı anahtarıyla korunur.'}</span></div></div>
    {flowsAvailable && allFlows.length > 0 && <div className="cs-card"><p className="cs-label">Akış Sağlığı</p><div className="cs-health-big"><b>{flowCounts.ok} / {flowCounts.all}</b><span>Akış Güncel</span></div><div className="cs-meter" aria-hidden="true"><i className="is-ok" style={{ width: flowShare(flowCounts.ok) }} /><i className="is-warn" style={{ width: flowShare(flowCounts.issue) }} /><i className="is-rest" style={{ width: flowShare(flowCounts.wait + flowCounts.off) }} /></div><div className="cs-legend"><span><i className="is-ok" />Güncel {flowCounts.ok}</span><span><i className="is-warn" />Dikkat {flowCounts.issue}</span><span><i className="is-rest" />Bekleyen / Kapalı {flowCounts.wait + flowCounts.off}</span></div></div>}
    {flowsAvailable && allFlows.length > 0 && <div className="cs-card"><p className="cs-label">Dikkat İsteyenler</p>{attentionFlows.length ? attentionFlows.map(row => <div className="cs-issue" key={row.policy.id}><MappingStateGlyph state={row.glyph} /><div><b>{row.label}</b><small>{row.health.label}{row.policy.lastSuccessAt ? <> · son başarılı <DateText value={row.policy.lastSuccessAt} /></> : null}</small></div><span className="cs-issue-actions"><button type="button" className="secondary" onClick={() => setSyncPolicyOpen(row.policy)}>Ayarlar</button><Link className="cs-link" to="/jobs">İşlem Takibi</Link></span></div>) : <p className="cs-empty">Dikkat isteyen akış yok.</p>}</div>}
  </>
  const flowTable = syncPolicies.isLoading ? <Busy text="Eşitleme ayarları yükleniyor…" /> : syncPolicies.isError ? <ErrorBox error={syncPolicies.error} onRetry={() => void syncPolicies.refetch()} /> : <>
    <div className="cs-chips">{([['all', 'Tümü', flowCounts.all], ['issue', 'Dikkat', flowCounts.issue], ['wait', 'Bekleyen', flowCounts.wait], ['off', 'Kapalı', flowCounts.off]] as const).map(([key, label, count]) => <button type="button" key={key} className={`cs-chip${flowFilter === key ? ' is-on' : ''}`} aria-pressed={flowFilter === key} onClick={() => setFlowFilter(key)}>{label}<b>{count}</b></button>)}</div>
    <div className="cs-ftable" role="table" aria-label="Veri eşitleme akışları">
      <div className="cs-frow is-head" role="row"><span role="columnheader">Akış</span><span role="columnheader">Durum</span><span role="columnheader">Son Başarılı</span><span aria-hidden="true" /></div>
      {flowGroups.map(group => { const rows = group.rows.filter(passesFlowFilter); if (!rows.length) return null; const allOn = group.policies.every(policy => policy.enabled); return <Fragment key={group.key}>
        <div className="cs-fgroup"><b>{group.label}</b><span className="cs-count">{rows.length}</span><span className="cs-grow" /><button type="button" className="secondary cs-quiet" disabled={saveFlowGroup.isPending} onClick={() => saveFlowGroup.mutate({ policies: group.policies, enabled: !allOn, label: group.label })}>{allOn ? 'Tümünü Kapat' : 'Tümünü Aç'}</button></div>
        {rows.map(row => <div className="cs-frow" role="row" key={row.policy.id}><button type="button" className="cs-fname" title="Çalışma sıklığı ve ayrıntılar" onClick={() => setSyncPolicyOpen(row.policy)}>{row.label}</button><span className={`cs-fstate${row.issue ? ' is-warn' : ''}`}><MappingStateGlyph state={row.glyph} />{row.health.label}</span><span className="cs-ftime"><DateText value={row.policy.lastSuccessAt ?? null} /></span><span className="cs-switch-field"><em className="cs-switch-state">{row.effectiveEnabled ? 'Açık' : 'Kapalı'}</em><input type="checkbox" role="switch" className="cs-switch" checked={row.effectiveEnabled} disabled={saveSyncPolicy.isPending || row.externalWriteBlocked} onChange={event => saveSyncPolicy.mutate({ policy: row.policy, intervalSeconds: row.policy.intervalSeconds, overlapSeconds: row.policy.overlapSeconds, enabled: event.target.checked })} aria-label={`${row.label} akışını ${row.effectiveEnabled ? 'kapat' : 'aç'}`} /></span></div>)}
      </Fragment> })}
      {!allFlows.some(passesFlowFilter) && <p className="cs-empty">Bu filtrede akış yok.</p>}
    </div>
  </>
  const settingsSections: Array<{ key: string; label: string; icon: SettingsIconName; badge?: number; node: ReactNode }> = [
    { key: 'genel', label: 'Genel Bakış', icon: 'info', badge: flowCounts.issue, node: overview },
    ...(flowsAvailable ? [{ key: 'akis', label: 'Veri Eşitleme', icon: 'sync' as const, badge: flowCounts.issue, node: <><div className="cs-sec-head"><h3>Veri Eşitleme Akışları</h3><p>Kapalı akış veri çekmez; açınca kaldığı yerden devam eder. Çalışma sıklığı için akışın adına tıklayın.</p></div>{flowTable}</> }] : []),
    ...(writeSupported ? [{ key: 'yaz', label: 'Pazaryerine Yazma', icon: 'write' as const, node: <>{(item.platformCode === 'TRENDYOL' || item.platformCode === 'HEPSIBURADA' || item.platformCode === 'SHOPIFY' || item.platformCode === 'N11' || item.platformCode === 'PAZARAMA' || efaturam) && <section className={`panel integration-detail-write-panel${item.platformCode === 'SHOPIFY' ? ' shopify-write-controls' : ''}`}><div className="integration-detail-write-heading"><div><h2>{externalWritePlatform}’a Dış Yazma</h2><p>{item.platformCode === 'SHOPIFY' ? 'Ürün, fiyat/stok ve manuel kargo/iade izinlerini ayrı ayrı yönetin. Ana anahtar kapalıysa Shopify’a hiçbir veri gönderilmez. Taşıyıcı etiketi üretimi desteklenmez.' : efaturam ? 'Fatura yalnızca siz önizlemeyi onayladığınızda, sipariş başına bir kez kesilir; hiçbir zaman otomatik oluşturulmaz. Anahtar kapalıyken E-Faturam’a fatura gönderilmez.' : item.platformCode === 'PAZARAMA' ? 'Otomatik dış yazmalar fiyat ve stok değişikliklerini yönetir. Anahtar kapalıyken Pazarama’ya hiçbir veri gönderilmez; iade kararı ve ürün açma sonraki aşamalarda eklenecek.' : item.platformCode === 'N11' ? 'Otomatik dış yazmalar fiyat ve stok değişikliklerini, manuel dış yazmalar ise sipariş işleme alma, paket bölme ve iade kararlarını yönetir.' : item.platformCode === 'HEPSIBURADA' ? 'Ürün/fiyat/stok otomatik gönderimleri varsayılan olarak kapalıdır. Manuel dış yazmalar yalnızca destek kanıtı olan paket veya iade işlemlerinde ayrıca açılabilir.' : 'Otomatik dış yazmalar fiyat ve stok değişikliklerini, manuel dış yazmalar ise kargo, etiket ve iade kararlarını yönetir.'}</p></div><span className={`integration-detail-write-status ${externalWritesEnabled ? 'is-enabled' : ''}`}>{externalWritesEnabled ? 'Açık' : 'Kapalı'}</span></div><label className="integration-write-toggle"><em className="cs-switch-state">{externalWritesEnabled ? 'Açık' : 'Kapalı'}</em><input type="checkbox" role="switch" checked={externalWritesEnabled} disabled={saveExternalWrites.isPending || !readConnectionReady} onChange={event => saveExternalWrites.mutate(event.target.checked)} /><span><strong>{item.platformCode === 'SHOPIFY' ? 'Shopify Dış Yazma Ana Anahtarı' : `${externalWritePlatform}’a Dış Yazma`}</strong><small>{item.platformCode === 'SHOPIFY' ? externalWritesEnabled ? 'Ana anahtar açık. Otomatik ve manuel izinleri aşağıda ayrı ayrı yönetin.' : 'Kapalıyken ürün ve diğer dış yazma grupları devre dışı kalır.' : efaturam ? (externalWritesEnabled ? 'Açık. Onayladığınız fatura istekleri E-Faturam’a gönderilir.' : 'Kapalıyken panel E-Faturam’dan veri okur; fatura oluşturmaz.') : externalWritesEnabled ? 'Açık akışlar aşağıdaki otomatik ve manuel seçeneklerden ayrı ayrı yönetilir.' : `Panel ${externalWritePlatform}’dan veri çeker; platforma dışarı veri göndermez.`}</small></span></label>{item.platformCode === 'SHOPIFY' && (syncPolicies.isLoading ? <Busy text="Shopify dış yazma seçenekleri yükleniyor…" /> : syncPolicies.isError ? <ErrorBox error={syncPolicies.error} onRetry={() => void syncPolicies.refetch()} /> : <div className="shopify-write-toggle-list" aria-label="Shopify otomatik ve manuel dış yazma seçenekleri">{externalWriteGroups.map(renderExternalWriteGroup)}</div>)}</section>}
        {item.platformCode !== 'SHOPIFY' && flowsAvailable && syncPolicyGroups.some(group => group.kind === 'external-write-parent') && <div className="cs-write-groups">{externalWriteGroups.map(renderExternalWriteGroup)}</div>}</> }] : []),
    ...(invoiceSupported ? [{ key: 'fatura', label: 'Faturalama', icon: 'invoice' as const, node: <>{(item.platformCode === 'TRENDYOL' || item.platformCode === 'SHOPIFY' || item.platformCode === 'N11' || item.platformCode === 'PAZARAMA') && <section className="panel integration-detail-write-panel invoice-creation-setting-panel" aria-labelledby="invoice-creation-setting-title"><div className="integration-detail-write-heading"><div><p className="eyebrow">FATURALAMA</p><h2 id="invoice-creation-setting-title">Fatura Oluşturma</h2><p>Bu ayar yalnızca {item.displayName} bağlantısındaki siparişleri etkiler.</p></div></div><label className="integration-write-toggle"><em className="cs-switch-state">{item.invoiceCreationEnabled ? 'Açık' : 'Kapalı'}</em><input type="checkbox" role="switch" checked={item.invoiceCreationEnabled} disabled={saveInvoiceCreation.isPending} onChange={event => saveInvoiceCreation.mutate(event.target.checked)} /><span><strong>Bu Bağlantıda Fatura Oluşturma</strong><small>{item.invoiceCreationEnabled ? 'Fatura oluşturma ve yeni fatura takip kaydı açma kullanılabilir.' : 'Kapalıyken fatura oluşturma düğmeleri pasif olur. Mevcut fatura kayıtları ve yüklenmiş belgeler etkilenmez.'}</small></span></label></section>}</> }] : []),
    { key: 'magaza', label: 'Mağaza ve Kimlik', icon: 'key', node: <>{item.platformCode === 'TRENDYOL_EFATURAM' && <article className="panel"><h2>E-Faturam Hesap Yetkisi</h2><p>Fatura API'si bağlı bireysel E-Faturam hesabının oturumunu kullanır. Firma ve kullanıcı kimliği yalnız sağlayıcının erişim tokenından alınır; panelde gösterilmez veya ayar olarak saklanmaz.</p><div className="card-list"><div className="record-card"><span><strong>Belge Türü</strong><small>Kurumsal ve E-Fatura mükellefi: TEMELFATURA; diğer siparişler: EARSIVFATURA</small></span><Badge value="AUTO" /></div><div className="record-card"><span><strong>İnternet Satışı Ek Alanları</strong><small>Kullanıcı ayarı değildir; gerekli olduğunda Trendyol siparişi ve resmî kargo kataloğundan otomatik hazırlanır.</small></span><Badge value="AUTO" /></div></div></article>}
        <section className="panel connection-settings-panel"><div className="connection-settings-section"><h2>Mağaza Bilgileri</h2><p>{item.platformCode === 'HEPSIBURADA' ? 'Mağaza adını ve Mağaza ID’sini burada güncelleyin. Bağlantı testi önce entegratör kullanıcı adını, 401 yanıtında Mağaza ID’sini Basic Auth kullanıcı adı olarak dener; başarılı olan değer şifreli kayda alınır. Servis anahtarı şifreli saklanır.' : 'Bağlantı kayıt adı ve mağaza bilgilerini buradan güncelleyin.'}</p><form onSubmit={event => { event.preventDefault(); saveConnectionDetails.mutate() }}><div className="connection-settings-fields"><label>{item.platformCode === 'HEPSIBURADA' ? 'Mağaza Adı' : 'Kayıt Adı'}<input value={displayName} onChange={event => setDisplayName(event.target.value)} autoComplete="off" required /></label><label>{item.platformCode === 'TRENDYOL_EFATURAM' ? 'VKN / TCKN' : item.platformCode === 'SHOPIFY' ? 'Shopify Mağaza Adresi' : item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada Mağaza ID' : item.platformCode === 'N11' ? 'N11 Mağaza Adı' : item.platformCode === 'PAZARAMA' ? 'Pazarama Mağaza Adı' : 'Mağaza Kimliği (Seller ID)'}<input value={externalStoreId} onChange={event => setExternalStoreId(event.target.value)} autoComplete="off" required /></label></div><div className="connection-settings-input-row">{item.platformCode !== 'HEPSIBURADA' && <span className="connection-settings-field-help">Kartlarda ve bağlantı başlığında gösterilir.</span>}<button disabled={saveConnectionDetails.isPending}>{saveConnectionDetails.isPending ? 'Kaydediliyor…' : 'Bilgileri Kaydet'}</button></div>{item.platformCode === 'SHOPIFY' && <small className="connection-settings-field-help">Tam Shopify mağaza adresi: magazaniz.myshopify.com</small>}</form></div><div className="connection-settings-divider" /><form className={`connection-settings-section ${item.platformCode === 'SHOPIFY' ? 'is-shopify' : ''}`} onSubmit={credential}><h2>{item.platformCode === 'SHOPIFY' ? 'Shopify Uygulama Yetkisi' : item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada Servis Kimlik Bilgileri' : 'API Bilgileri'}</h2>{item.platformCode === 'TRENDYOL_EFATURAM' ? <><label>Hesap E-Postası<input name="email" type="email" autoComplete="username" required /></label><label>Hesap Parolası<input name="password" type="password" autoComplete="new-password" placeholder={item.hasCredential ? '••••••••••••' : undefined} required /></label><p>Bu alanlar yalnız şifreli credential kaydında tutulur ve tekrar gösterilmez.</p></> : item.platformCode === 'SHOPIFY' ? <><label>Shopify Uygulama Tokenı<input name="shopifyAccessToken" type="password" autoComplete="off" placeholder={item.hasCredential ? '••••••••••••' : 'shpat_…'} required /><p>Okuma için read_products, read_inventory, read_orders, read_locations; ürün/fiyat yazması için write_products ve write_inventory; Online Store yayınını bulup etkinleştirmek için read_publications ve write_publications; iadeleri okuyup onaylamak için read_returns (veya read_marketplace_returns) ile write_returns (veya write_marketplace_returns); fulfillment takibi için write_assigned_fulfillment_orders, write_merchant_managed_fulfillment_orders veya write_third_party_fulfillment_orders izni gerekir. Shopify mağaza kullanıcısında ayrıca fulfill_and_ship_orders yetkisi bulunmalıdır. İzinleri uygulamaya ekleyip mağazayı yeniden yetkilendirin, ardından yeni tokenı burada şifreli kaydedip bağlantıyı tekrar test edin. Taşıyıcı etiketi Shopify’dan satın alınmaz veya üretilmez.</p></label></> : item.platformCode === 'HEPSIBURADA' ? <><label>Hepsiburada Entegratör Kullanıcı Adı<input name="hepsiburadaIntegratorUsername" autoComplete="username" required /></label><label>Hepsiburada Servis Anahtarı<input name="hepsiburadaServiceKey" type="password" autoComplete="new-password" placeholder={item.hasCredential ? '••••••••••••' : undefined} required /></label></> : <><label>{item.platformCode === 'N11' ? 'N11 API Anahtarı (App Key)' : item.platformCode === 'PAZARAMA' ? 'Pazarama Client ID' : 'API Key'}<input name="apiKey" type="text" autoComplete="off" spellCheck={false} placeholder={item.hasCredential ? 'Kayıtlı anahtarı değiştirmek için yazın' : undefined} required /></label><label>{item.platformCode === 'N11' ? 'N11 API Şifresi (App Secret)' : item.platformCode === 'PAZARAMA' ? 'Pazarama Client Secret' : 'API Secret'}<input name="apiSecret" type="password" autoComplete="new-password" placeholder={item.hasCredential ? '••••••••••••' : undefined} required /></label></>}<button disabled={run.isPending}>{run.isPending ? 'Kaydediliyor…' : 'Şifreli Kaydet'}</button><button type="button" className="secondary" disabled={!item.hasCredential || run.isPending} onClick={() => run.mutate({ path: `/connections/${id}/test-jobs` })}>Bağlantıyı Test Et</button></form></section></> },
    ...(toolsAvailable ? [{ key: 'arac', label: 'Araçlar', icon: 'tool' as const, node: <>{item.platformCode === 'N11' && <N11PublishSettingsPanel key={item.version} connectionId={id} displayName={item.displayName} version={item.version} shipmentTemplate={item.n11ShipmentTemplate} preparingDay={item.n11PreparingDay} />}
        {item.platformCode === 'N11' && <PriceStockSendPanel connectionId={id} writesEnabled={externalWritesEnabled} connectionActive={item.status === 'ACTIVE' || item.status === 'VERIFIED'} platformName="N11" codeLabel="stok kodu" codeAccusative="stok kodunu" codeExample="BS74297" />}
        {item.platformCode === 'PAZARAMA' && <PazaramaSaleStatusPanel connectionId={id} writesEnabled={externalWritesEnabled} connectionActive={item.status === 'ACTIVE' || item.status === 'VERIFIED'} />}
        {item.platformCode === 'PAZARAMA' && <PriceStockSendPanel connectionId={id} writesEnabled={externalWritesEnabled} connectionActive={item.status === 'ACTIVE' || item.status === 'VERIFIED'} platformName="Pazarama" codeLabel="barkod" codeAccusative="barkodu" codeExample="BS74297" />}
        {((item.platformCode === 'TRENDYOL' && item.environment === 'STAGE' && item.externalStoreId === '2738') || (item.platformCode === 'HEPSIBURADA' && item.environment === 'STAGE')) && <div className="panel"><h2>Taze Stage Test Siparişi</h2><p>Yalnız Stage ortamında tek seferlik test siparişi oluşturur. Hepsiburada için bağlı hbSku’su olan LIVE listing seçilir. Tıklayınca sağlayıcının test ortamında sipariş yaratılır.</p><button type="button" disabled={!item.hasCredential || run.isPending} onClick={() => run.mutate({ path: `/connections/${id}/stage-test-order-jobs` })}>{run.isPending ? 'Kuyruğa alınıyor…' : 'Taze Stage Test Siparişi Oluştur'}</button></div>}
        {stageManual && <div className="panel"><h2>Stage Operasyon Durumu</h2><p>Manuel testler bağlantı, credential, girdi doğrulaması, tekrar koruması ve sağlayıcı yanıt doğrulamasıyla çalışır.</p><p>Teknik ayrıntılı hata kayıtları İşlem Takibi’nde tutulur.</p></div>}</> }] : [])
  ]
  const activeSection = settingsSections.find(section => section.key === settingsSection) ?? settingsSections[0]
  const metaLine = `${displayEnvironment === 'PRODUCTION' ? 'Production' : 'Stage'} · ${efaturam ? 'Yerel kapsam' : `Mağaza ${item.externalStoreId}`} · API ${item.apiVersion}`
    return <div className={`${rootClass} cs-workspace${inline ? ' is-inline' : ''}`}>
    <header className="cs-head">{!inline && <Link to="/integrations" className="cs-back"><UiIcon name="arrowLeft" /> Platformlar</Link>}<img className={`cs-logo ${platformLogoClass(item.platformCode)}`} src={platformLogoSource(item.platformCode) ?? '/platforms/trendyol.png'} alt="" aria-hidden="true" /><div className="cs-head-copy"><h2>{item.displayName}</h2><p>{metaLine}</p></div><span className="cs-grow" /><span className={`cs-tag${readConnectionReady ? ' is-on' : ''}`}><MappingStateGlyph state={readConnectionReady ? 'done' : 'none'} />{statusLabel(item.status)}</span><span className="cs-tag">{stageManual ? 'Manuel akış' : 'Canlı akış'}</span>{onClose && <button type="button" className="cs-close" onClick={onClose} aria-label="Bağlantı ayarlarını kapat"><UiIcon name="close" /></button>}</header>
    <div className="cs-body"><nav className="cs-nav" aria-label="Ayar bölümleri">{settingsSections.map(section => <button type="button" key={section.key} className={section.key === activeSection.key ? 'is-on' : undefined} aria-current={section.key === activeSection.key ? 'true' : undefined} onClick={() => setSettingsSection(section.key)}><SettingsIcon name={section.icon} /><span>{section.label}</span>{section.badge ? <span className="cs-count is-warn">{section.badge}</span> : <span />}</button>)}</nav><section className="cs-pane" key={activeSection.key}>{activeSection.node}</section></div>
    {syncPolicyOpen && <SyncPolicyEditorModal policy={syncPolicyOpen} label={syncPolicyLabel(syncPolicyOpen.resourceType)} intervals={syncPolicyIntervals} overlaps={syncPolicyOverlaps} healthHelp={syncPolicyHealthHelp(syncPolicyOpen)} purpose={syncPolicyDescription(syncPolicyOpen.resourceType)} externalWriteBlocked={syncPolicyOpen.requiresExternalWrites === true && !item.externalWritesEnabled} saving={saveSyncPolicy.isPending} onClose={() => setSyncPolicyOpen(null)} onSave={values => saveSyncPolicy.mutate({ policy: syncPolicyOpen, ...values, closeModal: true })} />}
    <IntegrationFeedbackToast feedback={feedback} onClose={() => setFeedback(null)} />
  </div>

}

export function IntegrationDetailPage() {
  const { id = '' } = useParams()
  return <IntegrationDetailWorkspace id={id} />
}

function ListPage({ eyebrow, title, description: _description, children }: { eyebrow: string; title: string; description: string; children: React.ReactNode }) { return <section className="content f3"><div className="page-heading"><div><p className="eyebrow">{eyebrow}</p><h1>{title}</h1></div><Badge value="LIVE READ" /></div><div className="panel">{children}</div></section> }

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
      return hubApi<ShopifyOrderCsvImportResult>('/orders/shopify-csv-import', { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body })
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
    mutationFn: () => hubApi(`/orders/${item.id}/shopify-status`, { method: 'PUT', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify({ status }) }),
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
              await hubApi('/invoice-workspace/manual-status', { method: 'PUT', headers: { 'Idempotency-Key': `shopify-invoice-status:${packageId}:${status}:${idempotency()}` }, body: JSON.stringify({ packageIds: [packageId], status }) })
            } else {
              await hubApi(`/orders/${item.id}/shopify-status`, { method: 'PUT', headers: { 'Idempotency-Key': `shopify-order-status:${item.id}:${status}:${idempotency()}` }, body: JSON.stringify({ status }) })
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
  }, [bulkNoticeVersion])
  const [pageCursors, setPageCursors] = useState<Record<number, string | null>>({ 1: null })
  const hasLocalFilters = filters.label !== 'ALL'
  const orderCursor = pageCursors[page] ?? null
  const orderQueryFilters: OrderFilters = { ...filters, platform: 'ALL', platforms: selectedPlatforms ?? [] }
  const ordersQuery = useQuery({ queryKey: hasLocalFilters ? ['orders', 'filtered-all', orderQueryFilters] : ['orders', 'page', orderQueryFilters, pageSize, page, orderCursor], queryFn: () => hasLocalFilters ? loadAllOrderPages(orderQueryFilters) : loadOrderPage(orderQueryFilters, pageSize, orderCursor, page), placeholderData: keepPreviousData, staleTime: 30_000, refetchOnWindowFocus: true })
  const summary = useQuery({ queryKey: ['orders', 'summary', selectedPlatforms], queryFn: async () => {
    const platforms = selectedPlatforms?.length ? selectedPlatforms : [null]
    const summaries = await Promise.all(platforms.map(platform => hubApi<OrderSummary>(platform ? `/orders/summary?platform=${encodeURIComponent(platform)}` : '/orders/summary')))
    const keys: (keyof OrderSummary)[] = ['all', 'new', 'processing', 'shipped', 'delivered', 'resent', 'onHold', 'cancelled', 'returned', 'returnInTransit', 'partiallyCancelled', 'manualReview', 'pending', 'unverified', 'paymentPending']
    return keys.reduce((total, key) => { total[key] = summaries.reduce((sum, value) => sum + value[key], 0); return total }, { all: 0, new: 0, processing: 0, shipped: 0, delivered: 0, resent: 0, onHold: 0, cancelled: 0, returned: 0, returnInTransit: 0, partiallyCancelled: 0, manualReview: 0, pending: 0, unverified: 0, paymentPending: 0 } satisfies OrderSummary)
  } })
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
  const invoiceStatuses = [['FATURA_BEKLIYOR', 'Fatura Bekliyor'], ['FATURA_ISLENIYOR', 'Fatura İletiliyor'], ['FATURA_PLATFORMA_AKTARILMADI', 'Fatura İletilemedi'], ['FATURA_KONTROLDE', 'Faturası Mevcut'], ['FATURA_REDDEDILDI', 'Fatura Oluşturulamadı'], ['FATURA_IPTAL', 'Fatura İptal']] as const
  const isLabelPrinted = (item: Order) => (['a4', 'sticker'] as ShippingLabelFormat[]).some(format => printedLabels.has(printedShippingLabelKey(item.id, format)))
  const applyFilterValue = <K extends keyof OrderFilters>(key: K, value: OrderFilters[K]) => { const next = { ...filterForm, [key]: value }; setFilterForm(next); setFilters(next); setPage(1) }
  const resetFilters = () => { const next = { ...initialOrderFilters }; setFilterForm(next); setFilters(next); setSelectedPlatforms([]); setPage(1); setColumnFilterOpen(null); setSearchParams(current => { const params = new URLSearchParams(current); params.delete('search'); params.delete('status'); return params }, { replace: true }) }
  const clearInvoiceFilters = () => { const next = { ...filterForm, invoice: 'ALL', invoiceType: 'ALL', invoiceRegion: 'ALL' }; setFilterForm(next); setFilters(next); setPage(1) }
  const selectStatus = (requestedStatus: string) => { const status = resolveOrderStatusTab(requestedStatus); const next = { ...filterForm, status }; setFilterForm(next); setFilters(current => ({ ...current, status })); setSearchParams(current => { const params = new URLSearchParams(current); if (status === 'ALL') params.delete('status'); else params.set('status', status); return params }, { replace: true }); setPage(1) }
  const tabCount = (tab: string) => {
    const summaryKey = ({ ALL: 'all', NEW: 'new', UNVERIFIED: 'unverified', PROCESSING: 'processing', SHIPPED: 'shipped', DELIVERED: 'delivered', RESENT: 'resent', ON_HOLD: 'onHold', CANCELLED: 'cancelled', RETURNED: 'returned', RETURN_IN_TRANSIT: 'returnInTransit', PARTIALLY_CANCELLED: 'partiallyCancelled', MANUAL_REVIEW: 'manualReview', PAYMENT_PENDING: 'paymentPending' } as Record<string, keyof OrderSummary | undefined>)[tab]
    if (summary.data && summaryKey && typeof summary.data[summaryKey] === 'number') return summary.data[summaryKey]
    return 0
  }
  // Status, search, platform, cargo, listing, date range, sort and pagination
  // are applied by the API. Only local presentation filters remain here.
  const items = all.filter(item => filters.label === 'ALL' || (filters.label === 'PRINTED' ? isLabelPrinted(item) : !isLabelPrinted(item)) )
  async function exportOrders() {
    setExporting(true)
    try {
    const exportItems = hasLocalFilters ? items : (await loadAllOrderPages(orderQueryFilters)).items
    const escapeCsv = (value: unknown) => `"${String(value ?? '').replaceAll('"', '""')}"`
    const rows = [
      ['Sipariş No', 'Platform', 'Müşteri', 'Sipariş Tarihi', 'Durum', 'Fatura Durumu', 'Kargo', 'Ürünler', 'Toplam Tutar', 'Para Birimi'],
      ...exportItems.filter(item => filters.label === 'ALL' || (filters.label === 'PRINTED' ? isLabelPrinted(item) : !isLabelPrinted(item))).map(item => [
        item.orderNumber,
        item.platformDisplayName || item.platformCode,
        item.customerName,
        new Date(item.orderedAt).toLocaleString('tr-TR'),
        item.derivedStatus,
        item.invoiceStatus,
        item.packages?.[0]?.cargoProviderName ?? item.cargoProviderName ?? '',
        (item.lines ?? []).map(line => `${line.title} x${productLineQuantity(line)}`).join(' | '),
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
    showBulkNotice(`${(rows.length - 1).toLocaleString('tr-TR')} sipariş dışa aktarıldı.`)
    } catch (reason) {
      showBulkNotice(reason instanceof Error ? reason.message : 'Sipariş dışa aktarımı tamamlanamadı.', 'error')
    } finally {
      setExporting(false)
    }
  }
  const totalOrderCount = hasLocalFilters ? items.length : ordersQuery.data?.totalCount ?? items.length
  const totalPages = Math.max(1, Math.ceil(totalOrderCount / pageSize))
  const pageItems = (hasLocalFilters ? items.slice((page - 1) * pageSize, page * pageSize) : items).map(item => ({ ...item, invoiceCreationEnabled: connections.data?.items.find(connection => connection.id === item.connectionId)?.invoiceCreationEnabled ?? false })); const selectablePageItems = pageItems.filter(item => item.derivedStatus.toUpperCase() !== 'CANCELLED'); const allPageSelected = selectablePageItems.length > 0 && selectablePageItems.every(item => selectedIds.includes(item.id)); const selectedOrders = pageItems.filter(item => selectedIds.includes(item.id)); const shopifySelectedOrders = selectedOrders.filter(item => item.platformCode === 'SHOPIFY'); const cargoEditableOrders = selectedOrders.filter(item => item.platformCode !== 'SHOPIFY'); const shopifyInvoiceCreationBlocked = shopifySelectedOrders.some(item => !item.invoiceId && !item.invoiceCreationEnabled); const labelBatchItems = selectedOrders.filter(item => Boolean(orderDisplayShipment(item)?.cargoTrackingNumber ?? item.cargoTrackingNumber) && !isMicroExportLabelBlocked(item))
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
        headers: { 'Idempotency-Key': `instant-picking:${item.id}:${Date.now()}` }
      })
      moveOrderAcrossStatusCaches(client, item, updatedShipment)
      setSelectedIds(current => current.filter(id => id !== item.id))
      await client.refetchQueries({ queryKey: ['orders', 'summary'], type: 'active' })
      await client.refetchQueries({ queryKey: ['orders'], type: 'active' })
      showBulkNotice(`Sipariş ${displayOrderNumber(item.orderNumber)} anlık olarak işleme alındı ve panelde güncellendi.`)
    } catch (error) {
      showBulkNotice(error instanceof Error ? error.message : 'Sipariş işleme alınamadı.', 'error')
    } finally {
      setProcessingOrderId(current => current === item.id ? null : current)
    }
  }

  const metricValue = (value: number | undefined, loading: boolean) => loading ? '…' : (value ?? 0).toLocaleString('tr-TR')
  const cargoOptions = [{ value: 'ALL', label: 'Tüm Kargolar' }, ...cargos.map(value => ({ value, label: cargoLabel(value) }))]
  const invoiceOptions = [{ value: 'ALL', label: 'Tüm Fatura Durumları' }, ...invoiceStatuses.map(([value, label]) => ({ value, label }))]
  const invoiceTypeOptions = [{ value: 'ALL', label: 'Tüm Türler' }, { value: 'BIREYSEL', label: 'Bireysel' }, { value: 'KURUMSAL', label: 'Kurumsal' }]
  const invoiceRegionOptions = [{ value: 'ALL', label: 'Tüm Bölgeler' }, { value: 'TR', label: 'Türkiye' }, { value: 'MICRO_EXPORT', label: 'Mikro İhracat' }]
  const labelOptions = [{ value: 'ALL', label: 'Tüm Etiketler' }, { value: 'PRINTED', label: 'Etiketi Yazdırılanlar' }, { value: 'NOT_PRINTED', label: 'Etiketi Yazdırılmayanlar' }]
  const statusOptions = orderStatusTabs.map(([value, label]) => ({ value, label: value === 'ALL' ? 'Tüm Siparişler' : label }))
  const sortOptions: Array<[OrderFilters['sort'], string]> = [['DATE_DESC', 'En Yeni Siparişler'], ['DATE_ASC', 'En Eski Siparişler'], ['DUE_DESC', 'Teslim Tarihi Yaklaşanlar'], ['DUE_ASC', 'Teslim Tarihi Uzak Olanlar']]
  const platformTriggerLabel = selectedPlatforms.length === 0 || selectedPlatforms.includes(RETURN_NO_PLATFORM) ? 'Tüm Platformlar' : selectedPlatforms.length === 1 ? platformOptions.find(option => option.value === selectedPlatforms[0])?.label ?? selectedPlatforms[0] : `${selectedPlatforms.length} Platform Seçili`
  const sheetField = (label: string, current: string, options: Array<{ value: string; label: string }>, pick: (value: string) => void) => <div className="rr-fld rr-sheet-only"><small>{label}</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle}><span>{options.find(option => option.value === current)?.label ?? options[0]?.label}</span><RrIcon name="chev" /></button>}>{close => <ReturnColumnPop label={label} options={options} value={current} onPick={value => { pick(value); close() }} />}</RrAnchor></div>
  const invoicePop = (close: () => void) => { const group = (label: string, options: Array<{ value: string; label: string }>, current: string, key: 'invoice' | 'invoiceType' | 'invoiceRegion') => <><div className="rr-ph">{label}</div>{options.map((option, index) => { const on = current !== 'ALL' ? option.value === current : index === 0; return <button key={option.value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => { applyFilterValue(key, option.value); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{option.label}</span></button> })}</>; const any = filterForm.invoice !== 'ALL' || filterForm.invoiceType !== 'ALL' || filterForm.invoiceRegion !== 'ALL'; return <div className="rr-pop" role="dialog" style={{ minWidth: 260 }}>{group('Fatura Durumu', invoiceOptions, filterForm.invoice, 'invoice')}<div className="rr-sep" />{group('Fatura Türü', invoiceTypeOptions, filterForm.invoiceType, 'invoiceType')}<div className="rr-sep" />{group('Fatura Bölgesi', invoiceRegionOptions, filterForm.invoiceRegion, 'invoiceRegion')}{any && <div className="rr-foot"><span /><button type="button" className="rr-btn rr-quiet" style={{ height: 28 }} onClick={() => { clearInvoiceFilters(); close() }}>Filtreleri Temizle</button></div>}</div> }
  const firstPage = Math.max(1, Math.min(page - 1, totalPages - 3)); const pageList = Array.from({ length: Math.min(4, totalPages) }, (_, index) => firstPage + index)
  const tabLabel = orderStatusTabs.find(([value]) => value === filters.status)?.[1] ?? 'Tümü'
  const shopifyBulkButtons = shopifySelectedOrders.length > 0 && <><button type="button" className="rr-btn" onClick={() => setBulkShopifyStatus({ kind: 'order', items: shopifySelectedOrders })}><RrIcon name="edit" />Shopify Sipariş Durumu ({shopifySelectedOrders.length})</button><button type="button" className="rr-btn" disabled={shopifyInvoiceCreationBlocked} title={shopifyInvoiceCreationBlocked ? invoiceCreationDisabledHelp : undefined} onClick={() => setBulkShopifyStatus({ kind: 'invoice', items: shopifySelectedOrders })}><RrIcon name="edit" />Shopify Fatura Durumu ({shopifySelectedOrders.length})</button></>
  return <section className="content f3 rr rr-orders">
    <div className="rr-ph"><div><div className="rr-crumb">Pazaryeri Operasyonu</div><h1>Siparişler</h1><p>Tüm pazaryerlerinden gelen siparişleri tek yerden hazırlayın, etiketleyin ve takip edin.</p></div><div className="rr-acts"><button type="button" className="rr-btn" disabled={!items.length || exporting} aria-busy={exporting} title={exporting ? 'Dışa aktarılıyor…' : undefined} onClick={() => void exportOrders()}><RrIcon name="export" />Dışa Aktar</button><button type="button" className="rr-btn" onClick={() => setShopifyCsvImportOpen(true)}><RrIcon name="import" />İçe Aktar</button><button type="button" className="rr-btn rr-primary" onClick={() => setSingleSyncOpen(true)}><RrIcon name="refresh" /><span className="rr-long">Sipariş Senkronizasyonu</span><span className="rr-short">Senkronize Et</span></button></div></div>
    <div className="rr-metrics"><article className="rr-warn"><small>Yeni Sipariş</small><strong>{metricValue(summary.data?.new, summary.isLoading)}</strong><span>işleme alınmayı bekliyor</span></article><article className="rr-bad"><small>Geciken</small><strong>{metricValue(lateOrdersQuery.data?.metrics.lateOrders, lateOrdersQuery.isLoading)}</strong><span>kargoya verme süresi doldu</span></article><article><small>Kargoda</small><strong>{metricValue(summary.data?.shipped, summary.isLoading)}</strong><span>alıcıya doğru yolda</span></article><article><small>Fatura Bekleyen</small><strong>{metricValue(invoiceSummaryQuery.data?.uninvoicedCount ?? lateOrdersQuery.data?.metrics.uninvoicedInvoices, invoiceSummaryQuery.isLoading)}</strong><span>kesilmesi gereken fatura</span></article></div>
    {bulkNotice && <NoticeToast key={bulkNoticeVersion} kind={bulkNoticeKind} message={bulkNotice} onClose={() => setBulkNotice('')} durationMs={bulkNoticeKind === 'pending' ? undefined : 5000} />}
    <div className="rr-fshell">
      <ViewTabsWithMore className="order-tabs" more={['PARTIALLY_CANCELLED', 'RESENT', 'ON_HOLD']} ariaLabel="Sipariş durumları" value={filters.status} onChange={selectStatus} items={orderStatusTabs.map(([value, label]) => { const count = summary.isLoading ? '…' : summary.isError ? '—' : tabCount(value); return { value, label: viewTabLabel(({ ALL: 'all', NEW: 'new', PROCESSING: 'processing', SHIPPED: 'shipping', DELIVERED: 'done', CANCELLED: 'cancelled', RESENT: 'resend', ON_HOLD: 'paused', PARTIALLY_CANCELLED: 'partial' } as const)[value], label), count: typeof count === 'number' ? count.toLocaleString('tr-TR') : count, countTone: value === 'NEW' && typeof count === 'number' && count > 0 ? 'warn' as const : undefined } })} />
      <RrFilterCard active={[(selectedPlatforms ?? []).length > 0, Boolean(filters.dateFrom), filters.cargo !== 'ALL', filters.invoice !== 'ALL', filters.invoiceType !== 'ALL', filters.invoiceRegion !== 'ALL', filters.label !== 'ALL', filters.sort !== 'DATE_DESC'].filter(Boolean).length}><div className="rr-filters rr-has-sort">
        <label className="rr-fld rr-search"><small>Sipariş Ara</small><span style={{ position: 'relative' }}><RrIcon name="search" style={{ position: 'absolute', left: 11, top: 10, color: 'var(--rr-subtle)' }} /><input className="rr-in" style={{ paddingLeft: 34 }} aria-label="Sipariş ara" value={filterForm.search} onChange={event => setFilterForm(current => ({ ...current, search: event.target.value }))} onKeyDown={event => { if (event.key === 'Enter') { setFilters(filterForm); setPage(1) } }} placeholder="Sipariş no, müşteri veya barkod ara" /></span></label>
        <div className="rr-fld"><small>Platform</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle} disabled={!platformOptions.length}><span>{platformTriggerLabel}</span><RrIcon name="chev" /></button>}>{() => <ReturnPlatformPop options={platformOptions} selected={selectedPlatforms} onChange={setSelectedPlatforms} />}</RrAnchor></div>
        <DateRangePicker className="rr-fld rr-date" from={filterForm.dateFrom} to={filterForm.dateTo} onChange={range => { const next = { ...filterForm, dateFrom: range.from, dateTo: range.to }; setFilterForm(next); setFilters(next); setPage(1) }} label="Sipariş Tarihi" placeholder="Başlangıç – Bitiş" icon={<RrIcon name="cal" />} chevron={<RrIcon name="chev" />} />
        <div className="rr-fld"><small>Sıralama</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle}><span>{sortOptions.find(option => option[0] === filterForm.sort)?.[1]}</span><RrIcon name="chev" /></button>}>{close => <div className="rr-pop rr-right" role="listbox" style={{ minWidth: 250 }}>{sortOptions.map(([value, label]) => { const on = value === filterForm.sort; return <button key={value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => { applyFilterValue('sort', value); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{label}</span></button> })}</div>}</RrAnchor></div>
        {sheetField('Kargo Firması', filterForm.cargo, cargoOptions, value => applyFilterValue('cargo', value))}
        {sheetField('Fatura Durumu', filterForm.invoice, invoiceOptions, value => applyFilterValue('invoice', value))}
        {sheetField('Fatura Türü', filterForm.invoiceType, invoiceTypeOptions, value => applyFilterValue('invoiceType', value))}
        {sheetField('Fatura Bölgesi', filterForm.invoiceRegion, invoiceRegionOptions, value => applyFilterValue('invoiceRegion', value))}
        {sheetField('Etiket Durumu', filterForm.label, labelOptions, value => applyFilterValue('label', value))}
        {sheetField('Sipariş Durumu', filters.status, statusOptions, selectStatus)}
        <div className="rr-fbtns"><button type="button" className="rr-btn" onClick={resetFilters}>Temizle</button><button type="button" className="rr-btn rr-primary" onClick={() => { setFilters(filterForm); setPage(1) }}>Filtrele</button></div>
      </div></RrFilterCard>
    </div>
    {selectionMode && <div className="rr-selbar" role="region" aria-label="Seçim modu"><button type="button" className="rr-btn rr-quiet" onClick={exitSelectionMode}>İptal</button><span className="rr-grow" /><RrAnchor trigger={(open, toggle) => <button type="button" className="rr-btn" aria-haspopup="menu" aria-expanded={open} disabled={selectedOrders.length === 0} onClick={toggle}>İşlemler ({selectedOrders.length})<RrIcon name="chev" /></button>}>{close => <div className="rr-pop rr-right" role="menu">{shopifySelectedOrders.length > 0 && <><button type="button" className="rr-opt" onClick={() => { close(); setBulkShopifyStatus({ kind: 'order', items: shopifySelectedOrders }) }}><RrIcon name="edit" /><span className="rr-grow">Shopify Sipariş Durumu ({shopifySelectedOrders.length})</span></button><button type="button" className="rr-opt" disabled={shopifyInvoiceCreationBlocked} onClick={() => { close(); setBulkShopifyStatus({ kind: 'invoice', items: shopifySelectedOrders }) }}><RrIcon name="edit" /><span className="rr-grow">Shopify Fatura Durumu ({shopifySelectedOrders.length})</span></button></>}{cargoEditableOrders.length > 0 && <button type="button" className="rr-opt" onClick={() => { close(); setCourierOrder({ item: cargoEditableOrders[0], items: cargoEditableOrders }) }}><RrIcon name="truck" /><span className="rr-grow">Kargo Firması Değiştir</span></button>}{labelBatchItems.length > 0 && <button type="button" className="rr-opt" onClick={() => { close(); setShippingLabelBatch({ items: labelBatchItems, format: labelSettings.defaultFormat }) }}><RrIcon name="print" /><span className="rr-grow">Etiketleri Yazdır ({labelBatchItems.length})</span></button>}{shopifySelectedOrders.length === 0 && cargoEditableOrders.length === 0 && labelBatchItems.length === 0 && <p className="rr-lead" style={{ padding: '6px 10px' }}>Seçili siparişler için uygun işlem yok.</p>}</div>}</RrAnchor><button type="button" className="rr-btn" onClick={() => togglePageSelection(!allPageSelected)} disabled={!selectablePageItems.length}>{allPageSelected ? 'Seçimi Kaldır' : 'Tümünü Seç'}</button></div>}
    {bulkShopifyStatus && <ShopifyBulkStatusModal kind={bulkShopifyStatus.kind} items={bulkShopifyStatus.items} onClose={() => setBulkShopifyStatus(null)} onFeedback={showBulkNotice} onUpdated={orderIds => setSelectedIds(current => current.filter(id => !orderIds.includes(id)))} />}
    <div className="rr-panel"><div className="rr-panel-h"><div><h2>{tabLabel === 'Tümü' ? 'Tüm Siparişler' : tabLabel}</h2><p>Toplam {totalOrderCount.toLocaleString('tr-TR')} sipariş</p></div><label className="rr-psize">Sayfa Başına<select className="rr-in" value={pageSize} onChange={event => setPageSize(Number(event.target.value))}>{[20, 50, 100, 200].map(size => <option key={size} value={size}>{size}</option>)}</select></label></div>
      <div className="rr-tblwrap">{ordersLoading && !all.length ? <RrLoading text="Yerel sipariş kayıtları yükleniyor…" /> : ordersError && !all.length ? <RrError error={ordersError} onRetry={() => void ordersQuery.refetch()} /> : <table className={`rr-tbl rr-has-select${noBuyer ? ' rr-no-buyer' : ''}`}>
        <colgroup><col className="rr-c-order" />{!noBuyer && <col className="rr-c-buyer" />}<col className="rr-c-prod" /><col className="rr-c-price" /><col className="rr-c-cargo" /><col className="rr-c-inv" /><col className="rr-c-status" /><col className="rr-c-act" /></colgroup>
        <thead><tr>
          <ReturnHeadCell label="Sipariş Bilgileri" active={selectedPlatforms.length > 0} extra={<input type="checkbox" className="rr-cb" checked={allPageSelected} disabled={!selectablePageItems.length} onChange={event => togglePageSelection(event.target.checked)} aria-label="Sayfadaki siparişleri seç" style={{ marginRight: 8 }} />} pop={() => <ReturnPlatformPop options={platformOptions} selected={selectedPlatforms} onChange={setSelectedPlatforms} />} />
          {!noBuyer && <ReturnHeadCell label="Alıcı" />}
          <ReturnHeadCell label="Bilgiler" />
          <ReturnHeadCell label="Birim Fiyat" />
          <ReturnHeadCell label="Kargo" active={filters.cargo !== 'ALL'} pop={close => <ReturnColumnPop label="Kargo Firması" options={cargoOptions} value={filterForm.cargo} onPick={value => { applyFilterValue('cargo', value); close() }} />} />
          <ReturnHeadCell label="Fatura" active={filters.invoice !== 'ALL' || filters.invoiceType !== 'ALL' || filters.invoiceRegion !== 'ALL'} pop={invoicePop} />
          <ReturnHeadCell label="Durum" active={filters.status !== 'ALL'} pop={close => <ReturnColumnPop label="Sipariş Durumu" options={statusOptions} value={filters.status} right onPick={value => { selectStatus(value); close() }} />} />
          <ReturnHeadCell label="İşlemler" active={filters.label !== 'ALL'} pop={close => <ReturnColumnPop label="Etiket Durumu" options={labelOptions} value={filterForm.label} right onPick={value => { applyFilterValue('label', value); close() }} />} />
        </tr></thead>
        <tbody>{!pageItems.length ? <tr><td className="rr-empty-row" colSpan={noBuyer ? 7 : 8}><div className="rr-empty"><span className="rr-ei"><RrIcon name="box" /></span><b>Sipariş Bulunamadı</b><span className="rr-lead">Seçili filtrelerle eşleşen sipariş yok.</span><button type="button" className="rr-btn rr-quiet" style={{ marginTop: 4 }} onClick={resetFilters}>Filtreleri Temizle</button></div></td></tr> : pageItems.map(item => <OrderReferenceRow item={item} key={item.id} noBuyer={noBuyer} selectionMode={selectionMode} onEnterSelection={() => setSelectionMode(true)} selected={selectedIds.includes(item.id)} processing={processingOrderId === item.id} onSelect={checked => updateSelection(item.id, checked)} openMenu={menu?.orderId === item.id ? menu.kind : null} onMenuChange={kind => setMenu(kind ? { orderId: item.id, kind } : null)} onInvoiceCreate={() => { setMenu(null); if (item.platformCode === 'SHOPIFY') setInvoiceUploadOrder(item); else setInvoiceDraftOrder(item) }} onInvoiceDetails={() => { setMenu(null); setInvoiceInfoOrder(item) }} onInvoiceUpload={() => { setMenu(null); setInvoiceUploadOrder(item) }} onCourierChange={() => { setMenu(null); setCourierOrder({ item, items: [item] }) }} onOrderStatusChange={() => { setMenu(null); setShopifyStatusOrder(item) }} onProcessOrder={() => void processSingleOrder(item)} onCreatePackage={() => { setMenu(null); setHepsiburadaPackageOrder(item) }} onPrintLabel={format => { if (isMicroExportLabelBlocked(item)) { showBulkNotice('Mikro ihracat faturası kesilmeden kargo etiketi yazdırılamaz.', 'error'); return } setMenu(null); setShippingLabel({ item, format }) }} onPreviewImage={setPreviewImage} labelSettings={labelSettings} printedLabels={printedLabels} />)}</tbody>
      </table>}</div>
      {pageItems.length > 0 && <div className="rr-pager"><span>Toplam <b style={{ color: 'var(--rr-ink)' }}>{totalOrderCount.toLocaleString('tr-TR')}</b> adet</span><div className="rr-pages"><button type="button" aria-label="Önceki" disabled={page <= 1 || ordersQuery.isFetching} onClick={goToPreviousPage}><RrIcon name="left" /></button>{pageList.map(value => <button key={value} type="button" className={value === page ? 'rr-on' : undefined} aria-current={value === page ? 'page' : undefined} disabled={ordersQuery.isFetching} onClick={() => goToPage(value)}>{value}</button>)}<button type="button" aria-label="Sonraki" disabled={!hasNextPage || ordersQuery.isFetching} onClick={goToNextPage}><RrIcon name="right" /></button></div><label className="rr-jump">Sayfaya Git<input className="rr-in" type="number" min={1} max={totalPages} placeholder={`1–${totalPages}`} aria-label="Sayfa numarası" value={jumpPage} onChange={event => setJumpPage(event.target.value)} onKeyDown={event => { if (event.key === 'Enter' && Number(jumpPage) >= 1) { goToPage(Math.floor(Number(jumpPage))); setJumpPage('') } }} /><button type="button" className="rr-btn" onClick={() => { if (Number(jumpPage) >= 1) { goToPage(Math.floor(Number(jumpPage))); setJumpPage('') } }}>Git</button></label></div>}
    </div>
    {selectedOrders.length > 0 && <div className="rr-bulk" role="group" aria-label="Toplu sipariş işlemleri"><span className="rr-grow">{selectedOrders.length} sipariş seçildi{shopifySelectedOrders.length > 0 && shopifySelectedOrders.length < selectedOrders.length && <small className="rr-bulk-note">Shopify toplu işlemleri yalnızca seçili Shopify siparişlerine uygulanır.</small>}</span><button type="button" className="rr-btn rr-quiet" onClick={() => setSelectedIds([])}>Seçimi Temizle</button>{cargoEditableOrders.length > 0 && <button type="button" className="rr-btn" onClick={() => setCourierOrder({ item: cargoEditableOrders[0], items: cargoEditableOrders })}><RrIcon name="truck" />Kargo Firması Değiştir</button>}{shopifyBulkButtons}{labelBatchItems.length > 0 && <button type="button" className="rr-btn rr-primary" onClick={() => setShippingLabelBatch({ items: labelBatchItems, format: labelSettings.defaultFormat })}><RrIcon name="print" />Etiketleri Yazdır ({labelBatchItems.length})</button>}</div>}
     {invoiceInfoOrder && <InvoiceInfoModal item={invoiceInfoOrder} onClose={() => setInvoiceInfoOrder(null)} />}{invoiceViewerOrder && <InvoiceViewerModal item={invoiceViewerOrder} onClose={() => setInvoiceViewerOrder(null)} />}{invoiceDraftOrder && <InvoiceDraftModal item={invoiceDraftOrder} providers={providers} onClose={() => setInvoiceDraftOrder(null)} onFeedback={showBulkNotice} />}{invoiceUploadOrder && <InvoiceUploadModal item={invoiceUploadOrder} onClose={() => setInvoiceUploadOrder(null)} onFeedback={showBulkNotice} />}{shopifyStatusOrder && <ShopifyOrderStatusModal item={shopifyStatusOrder} onClose={() => setShopifyStatusOrder(null)} onFeedback={showBulkNotice} />}{shopifyCsvImportOpen && <ShopifyOrderCsvImportModal connections={shopifyConnections} onClose={() => setShopifyCsvImportOpen(false)} onFeedback={showBulkNotice} />}{courierOrder && <CourierChangeModal item={courierOrder.item} items={courierOrder.items} onClose={() => setCourierOrder(null)} onConfirmed={updatedShipments => { client.setQueriesData<Page<Order>>({ queryKey: ['orders', 'page'] }, current => current ? { ...current, items: current.items.map(order => updatedShipments.reduce((updated, shipment) => updated.id === shipment.orderId ? patchOrderShipment(updated, shipment) : updated, order)) } : current); showBulkNotice(`${updatedShipments.length} paket kargo firması “${cargoLabel(updatedShipments[0]?.cargoProviderName)}” olarak güncellendi ve panelde doğrulandı.`) }} />}{shippingLabel && <ShippingLabelModal item={shippingLabel.item} settings={labelSettings} format={shippingLabel.format} onClose={() => setShippingLabel(null)} onPrinted={() => setPrintedLabels(new Set(loadPrintedShippingLabels()))} />}{shippingLabelBatch && <ShippingLabelBatchModal items={shippingLabelBatch.items.filter(item => !isMicroExportLabelBlocked(item))} settings={labelSettings} format={shippingLabelBatch.format} onClose={() => setShippingLabelBatch(null)} onPrinted={() => setPrintedLabels(new Set(loadPrintedShippingLabels()))} />}{singleSyncOpen && <SingleOrderSyncModal activeConnection={marketplaceConnections} onClose={() => setSingleSyncOpen(false)} onSuccess={(connectionCount, orderNo) => { showBulkNotice(orderNo ? `${connectionCount} bağlantıdan #${orderNo} sipariş senkronizasyonu başlatıldı.` : `${connectionCount} bağlantıdan yeni sipariş senkronizasyonu başlatıldı.`, 'info'); void client.invalidateQueries({ queryKey: ['orders'] }); window.setTimeout(() => void client.invalidateQueries({ queryKey: ['orders'] }), 1500); window.setTimeout(() => void client.invalidateQueries({ queryKey: ['orders'] }), 3500) }} />}{previewImage && <ProductImagePreviewModal preview={previewImage} onClose={() => setPreviewImage(null)} />}
    {hepsiburadaPackageOrder && <HepsiburadaPackageCreateModal item={hepsiburadaPackageOrder} onClose={() => setHepsiburadaPackageOrder(null)} onCreated={packageNumber => { setHepsiburadaPackageOrder(null); showBulkNotice(`Hepsiburada paketi ${packageNumber} oluşturuldu; sipariş read-back kuyruğa alındı.`); void client.invalidateQueries({ queryKey: ['orders'] }); void client.invalidateQueries({ queryKey: ['shipments'] }) }} />}
  </section>
}

export function ShipmentsPage() {
  const [pageSize, setPageSize] = useState(20); const [pageNumber, setPageNumber] = useState(1)
  const query = useQuery({ queryKey: ['shipments'], queryFn: () => loadAllPages<Shipment>('/shipments') })
  const items = query.data?.items ?? []; const totalPages = Math.max(1, Math.ceil(items.length / pageSize)); const currentPage = Math.min(pageNumber, totalPages); const pageItems = items.slice((currentPage - 1) * pageSize, currentPage * pageSize)
  return <ListPage eyebrow="Operasyon" title="Gönderiler" description="Paket durumu geriye alınmaz; desteklenmeyen aksiyon ve etiket biçimleri gösterilmez.">{query.isLoading ? <Busy /> : query.isError ? <ErrorBox error={query.error} onRetry={() => void query.refetch()} /> : !items.length ? <Empty>Order ingestion sonrasında shipment package kayıtları burada görünür.</Empty> : <><div className="data-table" role="table">{pageItems.map(item => <Link role="row" to={`/shipments/${item.id}`} key={item.id}><span><strong>{item.orderNumber}</strong><small>Paket {item.externalPackageId}</small></span><Badge value={item.status} /><span>{item.cargoTrackingNumber ?? '—'}</span><span><DateText value={item.statusOccurredAt} /></span></Link>)}</div><div className="order-pagination"><label>Sayfa başına <select aria-label="Sayfa başına gönderi" value={pageSize} onChange={event => { setPageSize(Number(event.target.value)); setPageNumber(1) }}>{[20, 50, 100, 200].map(value => <option key={value} value={value}>{value}</option>)}</select></label><span>Toplam {items.length.toLocaleString('tr-TR')} adet</span><Pagination className="pagination-controls" page={currentPage} totalPages={totalPages} onPageChange={setPageNumber} onPrevious={() => setPageNumber(value => Math.max(1, value - 1))} onNext={() => setPageNumber(value => Math.min(totalPages, value + 1))} disabled={query.isFetching} /></div></>}</ListPage>
}

export function ShipmentDetailPage() {
  const { id = '' } = useParams(); const client = useQueryClient(); const [notice, setNotice] = useState(''); const [selectedCargoShortName, setSelectedCargoShortName] = useState(''); const query = useQuery({ queryKey: ['shipment', id], queryFn: () => hubApi<ShipmentDetail>(`/shipments/${id}`) })
  const carriers = useQuery({ queryKey: ['shipment-carriers', id], queryFn: () => hubApi<RemoteCargoCompany[]>(`/shipments/${id}/changeable-cargo-companies`), enabled: query.data?.platformCode === 'HEPSIBURADA' && query.data.allowedActions.includes('CHANGE_CARGO_PROVIDER'), retry: false })
  useEffect(() => { if (carriers.data?.length && !carriers.data.some(item => item.shortName === selectedCargoShortName)) setSelectedCargoShortName(carriers.data[0].shortName) }, [carriers.data, selectedCargoShortName])
  useEffect(() => {
    if (!notice) return
    const kind = notificationKindFromMessage(notice)
    appendNotification(notice, kind)
    document.documentElement.dataset.rvShipmentFeedback = kind
    const timeout = window.setTimeout(() => setNotice(''), kind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice])
  async function action(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!query.data) return; const data = new FormData(event.currentTarget); const n11Cancels = query.data.platformCode === 'N11' && data.get('action') === 'SPLIT_BY_QUANTITY' && String(data.get('payloadJson') || '').includes('"cancelledItems"'); if (query.data.platformCode === 'N11' && !window.confirm(n11Cancels ? 'Bu işlem N11’de ürünleri İPTAL EDER ve geri alınamaz. Devam edilsin mi?' : 'Bu işlem N11’e gönderilecek. Devam edilsin mi?')) return; if (query.data.platformCode === 'PAZARAMA' && !window.confirm(pazaramaActionWarning(String(data.get('action') ?? '')))) return; try { JSON.parse(String(data.get('payloadJson') || '{}')); await hubApi(`/shipments/${id}/actions`, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${query.data.package.version}"` }, body: JSON.stringify({ action: data.get('action'), payloadJson: data.get('payloadJson') }) }); setNotice('Paket aksiyonu kuyruğa alındı; sonuç read-back ile doğrulanacak.'); await client.invalidateQueries({ queryKey: ['shipment', id] }) } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Aksiyon başlatılamadı.') } }
  async function hbAction(actionName: 'UNPACK' | 'CHANGE_CARGO_PROVIDER', payloadJson = '{}') { if (!query.data) return; if (actionName === 'UNPACK' && !window.confirm('Bu paket Hepsiburada’da açılacak. Devam edilsin mi?')) return; try { await hubApi(`/shipments/${id}/actions`, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${query.data.package.version}"` }, body: JSON.stringify({ action: actionName, payloadJson }) }); setNotice(`${actionName === 'UNPACK' ? 'Paket açma' : 'Kargo değişikliği'} kuyruğa alındı; Hepsiburada yanıtı sipariş read-back ile doğrulanacak.`); await client.invalidateQueries({ queryKey: ['shipment', id] }); await client.invalidateQueries({ queryKey: ['shipment-carriers', id] }) } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Paket aksiyonu başlatılamadı.') } }
  async function label(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!query.data) return; const data = new FormData(event.currentTarget); const requestedFormat = query.data.platformCode === 'HEPSIBURADA' ? String(data.get('format') ?? 'ZPL').toUpperCase() : undefined; try { await hubApi(`/shipments/${id}/common-label-jobs`, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${query.data.package.version}"` }, body: JSON.stringify({ boxQuantity: query.data.platformCode === 'HEPSIBURADA' ? 1 : Number(data.get('boxQuantity')), volumetricHeight: query.data.platformCode === 'HEPSIBURADA' ? 0 : Number(data.get('volumetricHeight')), ...(requestedFormat ? { format: requestedFormat } : {}) }) }); setNotice(query.data.platformCode === 'HEPSIBURADA' ? `Hepsiburada ${requestedFormat} etiket okuma işi kuyruğa alındı.` : 'Ortak etiket işi kuyruğa alındı.'); await client.invalidateQueries({ queryKey: ['shipment', id] }) } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Etiket işi başlatılamadı.') } }
  async function labelProbe(event: FormEvent<HTMLFormElement>) { event.preventDefault(); if (!query.data) return; const data = new FormData(event.currentTarget); try { await hubApi(`/shipments/${id}/label-capability-probes`, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${query.data.package.version}"` }, body: JSON.stringify({ capabilityCode: data.get('capabilityCode'), boxQuantity: Number(data.get('boxQuantity')), volumetricHeight: Number(data.get('volumetricHeight')) }) }); setNotice('Stage etiket testi kuyruğa alındı; sonuç İşlem Takibi’nde görünür.'); await client.invalidateQueries({ queryKey: ['shipment', id] }) } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Stage etiket testi başlatılamadı.') } }
  if (query.isLoading) return <section className="content"><Busy /></section>; if (query.isError || !query.data) return <section className="content"><ErrorBox error={query.error} onRetry={() => void query.refetch()} /></section>; const item = query.data
  const hasLabelTarget = item.platformCode === 'HEPSIBURADA' ? Boolean(item.package.externalPackageId) : Boolean(item.package.cargoTrackingNumber)
  return <section className="content f3"><Link to="/shipments" className="back"><UiIcon name="arrowLeft" /> Gönderiler</Link><div className="page-heading"><div><p className="eyebrow">{item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada gönderisi' : 'Gönderi detayı'}</p><h1>{item.package.orderNumber}</h1><p className="lede">Paket {item.package.externalPackageId}</p></div><Badge value={item.package.status} /></div>{notice && <MessageToast message={notice} onClose={() => setNotice('')} />}<div className="split"><article className="panel"><h2>Paket</h2><dl className="details"><dt>Platform durumu</dt><dd>{statusLabel(item.package.rawStatus)}</dd><dt>Kargo firması</dt><dd>{item.package.cargoProviderName ?? '—'}</dd><dt>Takip numarası</dt><dd>{item.package.cargoTrackingNumber ?? '—'}</dd><dt>Güncelleme</dt><dd><DateText value={item.package.statusOccurredAt} /></dd></dl><h3>Belgeler</h3>{item.documents.length ? <ul className="plain-list">{item.documents.map(document => <li key={document.id}><strong>{document.documentKind} · {document.format}</strong><span>v{document.documentVersion} · {document.source}</span><a href={`/api/v1/shipments/${item.package.id}/documents/${document.id}/content`} download>Belgeyi indir</a></li>)}</ul> : <p>Henüz belge yok.</p>}</article><article className="panel"><h2>{item.platformCode === 'HEPSIBURADA' ? 'Hepsiburada paket işlemleri' : item.platformCode === 'N11' ? 'N11 paket aksiyonu' : item.platformCode === 'PAZARAMA' ? 'Pazarama paket aksiyonu' : 'Trendyol paket aksiyonu'}</h2>{item.platformCode === 'HEPSIBURADA' ? <>{item.allowedActions.includes('CHANGE_CARGO_PROVIDER') && <form className="inline-form" onSubmit={event => { event.preventDefault(); void hbAction('CHANGE_CARGO_PROVIDER', JSON.stringify({ CargoCompanyShortName: selectedCargoShortName })) }}><label>Bu pakete izin verilen kargo firması<select value={selectedCargoShortName} onChange={event => setSelectedCargoShortName(event.target.value)} disabled={carriers.isLoading || !carriers.data?.length}><option value="">Kargo firması seçin</option>{(carriers.data ?? []).map(carrier => <option key={carrier.shortName} value={carrier.shortName}>{carrier.name} · {carrier.shortName}</option>)}</select></label><button type="submit" disabled={!selectedCargoShortName || carriers.isLoading}>Kargo firmasını değiştir</button></form>}{carriers.isError && <ErrorBox error={carriers.error} onRetry={() => void carriers.refetch()} />}{carriers.isSuccess && !carriers.data.length && <p role="status">Hepsiburada bu paket için değiştirilebilir kargo firması döndürmedi.</p>}{item.allowedActions.includes('UNPACK') && <button type="button" className="secondary" onClick={() => void hbAction('UNPACK')}>Paketi aç</button>}{!item.allowedActions.length && <div className="unknown"><strong>Paket aksiyonu uygun değil</strong><p>SHIPMENT_WRITE kanıtı, bağlantı yazma ayarı veya paket durumu bu işlemi şu anda uygun kılmıyor.</p></div>}<p className="notice">Kargo seçeneği Hepsiburada’nın bu paket için döndürdüğü izinli firmalardan gelir.</p></> : item.allowedActions.length ? <form className="form-grid" onSubmit={action}><label>Aksiyon<select name="action">{item.allowedActions.map(value => <option key={value} value={value}>{statusLabel(value)}</option>)}</select></label><label>Resmî aksiyon payload JSON<textarea name="payloadJson" defaultValue="{}" required /></label>{item.platformCode === 'PAZARAMA' && <small className="n11-action-hint">Pazarama — İşleme alma ve tedarik edilemedi için {'{}'} yeterli (paketteki tüm kalemler). Paket böl / güncelle: {'{"packages":[{"orderLineIds":["kalem-guid"],"sellerAddressId":null,"cargoCompanyId":null}]}'} · İşçilik: {'{"items":[{"orderLineId":"kalem-guid","laborCost":100,"processCount":1}]}'} · İptal talebi kararı için {'{}'} (siparişin karar bekleyen talepleri). Anlaşmalı kargoda 12’den sonra süreç Pazarama’da kendiliğinden ilerler.</small>}{item.platformCode === 'N11' && <small className="n11-action-hint">N11 örnekleri — Miktarla böl / iptal: {'{"splitPackages":[{"packageDetails":[{"orderLineId":1,"quantities":1}]}],"cancelledItems":[{"cancelReasonId":61,"orderLineId":1,"quantity":1}]}'} · İşçilik: {'{"laborCostDetails":[{"orderLineId":1,"totalLaborCostExcludingVAT":100,"laborVatRate":20}]}'} · Toplama talebi (HLZ/CEVA/BL): {'{"collectionRequestDetails":[{"id":"paketKodu","orderLineId":1,"boxQuantity":1,"desi":10,"shipmentCompany":"HLZ"}]}'}. İptal yalnız bölmeyle birlikte yapılır; geri alınamaz.</small>}<button>Aksiyonu kuyruğa al</button></form> : <div className="unknown"><strong>Paket aksiyonu uygun değil</strong><p>Paket durumu veya production dış-yazma ayarları bu işlemi şu anda uygun kılmıyor.</p></div>}<h2>Ortak kargo etiketi</h2>{hasLabelTarget ? <>{item.supportedLabelFormats.length > 0 && <form className="inline-form" onSubmit={label}>{item.platformCode === 'HEPSIBURADA' && <label>Etiket biçimi<select name="format" defaultValue={item.supportedLabelFormats.includes('ZPL') ? 'ZPL' : item.supportedLabelFormats[0]}>{item.supportedLabelFormats.map(format => <option key={format} value={format}>{format === 'BASE64ZPL' ? 'Base64 ZPL' : format}</option>)}</select></label>}{item.platformCode !== 'HEPSIBURADA' && <><label>Koli adedi<input name="boxQuantity" type="number" min="1" max="50" defaultValue="1" required /></label><label>Desi / hacim<input name="volumetricHeight" type="number" min="0" max="10000" step="0.01" defaultValue="1" required /></label></>}<button>{item.platformCode === 'HEPSIBURADA' ? 'Etiketi al' : 'Etiket oluştur'}</button></form>}{item.isStageConnection && item.platformCode === 'TRENDYOL' && <details className="operation-details"><summary>Stage etiket testi</summary><form className="inline-form" onSubmit={labelProbe}><p>Yalnız test bağlantısındaki uygun pakette etiket okuma veya oluşturma sonucunu doğrular.</p><label>Test türü<select name="capabilityCode"><option value="LABEL_READ">Etiketi oku</option><option value="LABEL_WRITE">Etiket oluşturmayı dene</option></select></label><label>Koli adedi<input name="boxQuantity" type="number" min="1" max="50" defaultValue="1" required /></label><label>Desi / hacim<input name="volumetricHeight" type="number" min="0.01" max="10000" step="0.01" required /></label><button>Stage etiket testini çalıştır</button></form></details>}</> : <p>Etiket için paket kimliği gerekir.</p>}</article>{item.platformCode === 'PAZARAMA' && <PazaramaFinancePanel orderId={item.package.orderId} />}</div></section>
}

// Returns list row (docs/tasarim-onerileri/iadeler-yeni.html): order info, buyer, products with a unit price column whose
// prices line up with each product, cargo, invoice, reason, a status ring and the "İşlemler" menu.
function returnDateText(value: string | null | undefined) { if (!value) return '—'; const date = new Date(value); return Number.isNaN(date.getTime()) ? value : date.toLocaleString('tr-TR', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' }) }
function returnRemaining(value: string | null) { const text = remainingText(value); return /doldu|gelmedi/u.test(text) ? text : `${text.replace(/\s*kaldı$/u, '')} kaldı` }
function returnRingTone(status: string): [RingTone, number] {
  switch (returnGroup(status)) {
    case 'REQUESTED': return ['prog', 0.2]
    case 'SHIPPING': return ['move', 0.45]
    case 'ACTION_REQUIRED': return ['wait', 0.7]
    case 'APPROVED': return ['ok', 1]
    case 'REJECTED': return ['bad', 1]
    case 'DISPUTED': return ['bad', 0.66]
    case 'REVIEW': return ['move', 0.66]
    default: return ['off', 0]
  }
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
const invoiceRing: Record<string, [RingTone, number]> = { 'Fatura Bekliyor': ['wait', 0.2], 'Fatura İletiliyor': ['move', 0.66], 'Faturası Mevcut': ['ok', 1], 'Fatura İletilemedi': ['bad', 0.66], 'Fatura Oluşturulamadı': ['bad', 0.33], 'Fatura İptal': ['off', 0] }
const invoiceFailureText: Record<string, string> = { 'Fatura İletilemedi': 'Fatura pazar yerine iletilemedi. Ayrıntılar Faturalar sayfasında.', 'Fatura Oluşturulamadı': 'Fatura oluşturulamadı. Ayrıntılar Faturalar sayfasında.' }
// A checkbox with its explanation. On phones the explanation stays folded behind an info button and opens on a tap.
function RrHintCheck({ checked, onChange, label, hint }: { checked: boolean; onChange: (value: boolean) => void; label: string; hint: string }) {
  const [hintOpen, setHintOpen] = useState(false)
  return <><label className="rr-cklabel"><input type="checkbox" className="rr-cb" checked={checked} onChange={event => onChange(event.target.checked)} />{label}<button type="button" className="rr-hint-btn" aria-label="Açıklamayı göster" aria-expanded={hintOpen} onClick={event => { event.preventDefault(); setHintOpen(open => !open) }}><RrIcon name="info" size={15} /></button></label><p className={`rr-lead rr-hint${hintOpen ? ' rr-on' : ''}`} style={{ marginTop: -8 }}>{hint}</p></>
}

function ReturnInvoiceRing({ status, isMicroExport }: { status: string; isMicroExport: boolean }) {
  const base = invoiceStatusDisplayLabel(status, false)
  const label = invoiceStatusDisplayLabel(status, isMicroExport)
  const [tone, progress] = invoiceRing[base] ?? ['off', 0]
  // Short states ("Faturası Mevcut") stay on one line; long ones break only between word pairs, so
  // "Mikro İhracat Faturası Bekliyor" takes two lines in the narrow column instead of three.
  const words = label.split(' ')
  const shown = label.length <= 18 ? label.replace(/ /g, ' ') : words.length >= 4 ? words.reduce<string[]>((pairs, word, index) => index % 2 ? [...pairs.slice(0, -1), `${pairs[pairs.length - 1]} ${word}`] : [...pairs, word], []).join(' ') : label
  const ring = <RrRing tone={tone} progress={progress} label={shown} />
  return invoiceFailureText[base] ? <span className="rr-reason-tip" tabIndex={0}>{ring}<span className="rr-reason-pop" role="tooltip"><b>{label}</b>{invoiceFailureText[base]}</span></span> : ring
}
// Codes first (barcode, stock code, model code), then the variant (colour, size).
function returnLineFacts(platformCode: string | null | undefined, line: OrderLine | ReturnLine): Array<[string, string]> {
  const facts: Array<[string, string | null | undefined]> = [['Barkod', formatMarketplaceBarcode(platformCode ?? '', line.barcode) ?? line.barcode], ['Stok Kodu', line.sku], ['Model Kodu', (line as { modelCode?: string | null }).modelCode], ['Renk', returnLineFieldValue(line, ['renk', 'color'], 'color')], ['Beden', returnLineFieldValue(line, ['beden', 'size'], 'size')]]
  return facts.filter((fact): fact is [string, string] => Boolean(fact[1]?.trim()))
}
// Colour and size share one line, split by a short divider.
function ReturnFactLines({ facts }: { facts: Array<[string, string]> }) {
  const variant = facts.filter(([label]) => label === 'Renk' || label === 'Beden')
  return <>{facts.filter(([label]) => label !== 'Renk' && label !== 'Beden').map(([label, value]) => <small key={label}><span>{label}:</span> {value}</small>)}{variant.length > 0 && <small className="rr-pair">{variant.map(([label, value], index) => <Fragment key={label}>{index > 0 && <i className="rr-sep-v" aria-hidden="true" />}<span>{label}:</span>&nbsp;{value}</Fragment>)}</small>}</>
}
function ReturnImagePreview({ title, imageUrl, facts, onClose }: { title: string; imageUrl: string | null | undefined; facts: Array<[string, string]>; onClose: () => void }) {
  const crumb = facts.filter(([label]) => label === 'Model Kodu' || label === 'Renk' || label === 'Beden').map(([label, value]) => `${label}: ${value}`).join(' · ')
  return <RrModal crumb={crumb || undefined} title={title} size="img" onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose}>Kapat</button></>}><div className="rr-img-preview">{imageUrl ? <img src={imageUrl} alt={title} referrerPolicy="no-referrer" /> : <RrIcon name="image" />}</div></RrModal>
}
function ReturnLabelPreview({ item, lines, onClose }: { item: ReturnClaim; lines: OrderLine[]; onClose: () => void }) {
  const track = item.cargoTrackingNumber?.trim() || item.packageNumber?.trim() || item.externalClaimId
  return <RrModal crumb="Yazdırma Önizlemesi" title="İade Etiketi Yazdır" desc={`${displayOrderNumber(item.orderNumber)} · Etiket yeni pencerede açılır ve yazdırma başlar.`} size="md" onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose}>Vazgeç</button><button type="button" className="rr-btn rr-primary" onClick={() => { printReturnLabel(item, lines); onClose() }}><RrIcon name="print" />Yazdır</button></>}>
    <div style={{ display: 'grid', placeItems: 'center' }}><div className="rr-rlabel"><div className="rr-hd"><div><small>{returnPlatformName(item.platformCode)} · İade Paketi</small><h4>Sipariş {displayOrderNumber(item.orderNumber)}</h4></div><b>{titleTr(statusLabel(item.status))}</b></div><div className="rr-mt"><div><small>İade Kayıt No</small><b>{item.externalClaimId}</b></div><div><small>Kargo Firması</small><b>{cargoLabel(item.cargoProviderName)}</b></div><div><small>Kargo Takip No</small><b>{track}</b></div><div><small>Müşteri</small><b>{item.customerName}</b></div></div><div className="rr-bars" /><div className="rr-no">{item.packageNumber ?? track}</div><ul>{lines.map(line => <li key={line.id}>{line.title} · {productLineQuantity(line)} adet{line.sku ? ` · ${line.sku}` : ''}</li>)}</ul></div></div>
  </RrModal>
}
// Facts strip of the decision and detail popups.
function ReturnDetailFacts({ item, plain, invoicedTotal, money }: { item: ReturnClaim | ReturnDetail; plain?: boolean; invoicedTotal: number; money: (value: number) => string }) {
  const state = titleTr(statusLabel(item.status))
  return <div className="rr-facts"><div><small>Durum</small>{plain ? <RrBadge label={state} /> : <b>{state}</b>}</div>{item.reasonText && <div><small>İade Sebebi</small><b>{returnReasonPresentation(item).label}</b></div>}{item.cargoTrackingNumber && <div><small>Kargo</small><b className="rr-num">{item.cargoTrackingNumber}</b></div>}<div><small>Faturalandırılmış Tutar</small><b className="rr-num">{money(item.orderAmount || invoicedTotal)}</b></div>{item.orderedAt && <div><small>Talep Tarihi</small><b className="rr-num">{returnDateText(item.orderedAt)}</b></div>}{plain && item.approvedAt && <div><small>Onaylanma Tarihi</small><b className="rr-num">{returnDateText(item.approvedAt)}</b></div>}</div>
}
function ReturnReferenceRow({ item, order, onNotice, selection, selectColumn, noBuyer }: { item: ReturnClaim; order: Order | null; onNotice?: ReturnNoticeHandler; selection?: { checked: boolean; onChange: () => void }; selectColumn?: boolean; noBuyer?: boolean }) {
  const [previewLine, setPreviewLine] = useState<OrderLine | null>(null)
  const [labelOpen, setLabelOpen] = useState(false)
  const [copiedOrderNumber, setCopiedOrderNumber] = useState(false)
  const [dialogMode, setDialogMode] = useState<'DETAIL' | 'APPROVE' | 'REJECT' | null>(null)
  const [selectedReturnLineIds, setSelectedReturnLineIds] = useState<string[]>([])
  const rowRef = useRef<HTMLTableRowElement>(null)
  const detailQuery = useQuery({ queryKey: ['return', item.id], queryFn: () => hubApi<ReturnDetail>(`/returns/${item.id}`), enabled: dialogMode !== null, staleTime: 30_000 })
  const navigate = useNavigate()
  const lines = order?.lines?.length ? order.lines : item.lines ?? []
  const currency = order?.currency ?? item.currency
  const money = (value: number) => value.toLocaleString('tr-TR', { style: 'currency', currency })
  const total = order?.grossAmount || order?.netAmount || item.grossAmount || item.orderAmount
  const invoiceStatus = order?.invoiceStatus ?? item.invoiceStatus
  const discountAmount = order?.discountAmount ?? item.discountAmount
  const hasDiscount = Number.isFinite(discountAmount) && discountAmount > 0
  const invoicedTotal = Math.max(0, total - discountAmount)
  const orderShipment = order ? orderDisplayShipment(order) : null
  const rejected = returnGroup(item.status) === 'REJECTED'
  const hasOutboundShipment = rejected && Boolean(item.cargoTrackingNumber?.trim() || item.cargoTrackingLink?.trim())
  const trackingNumber = rejected ? item.cargoTrackingNumber : item.cargoTrackingNumber ?? orderShipment?.cargoTrackingNumber ?? item.orderCargoTrackingNumber
  const cargoProvider = rejected ? item.cargoProviderName : item.cargoProviderName ?? orderShipment?.cargoProviderName ?? item.orderCargoProviderName
  const canTrackReturn = Boolean(item.cargoTrackingLink && /^https:\/\//i.test(item.cargoTrackingLink))
  const canTrackOrder = !rejected && Boolean(orderShipment?.id && trackingNumber?.trim())
  const detailItem = detailQuery.data ?? item
  const detailLines = detailQuery.data?.lines ?? lines
  const detailCurrency = detailItem.currency ?? currency
  const detailMoney = (value: number) => value.toLocaleString('tr-TR', { style: 'currency', currency: detailCurrency })
  const reasonPresentation = returnReasonPresentation(item)
  const decisionAvailable = detailQuery.data?.allowedActions.some(value => value === 'APPROVE' || value === 'REJECT') === true
  const marketplaceName = returnPlatformName(detailItem.platformCode)
  // Approvals use the product-selection dialog; Hepsiburada adds its refund/change choice, and its pre-approval review keeps the older flow.
  const approveIsHepsiburada = detailItem.platformCode?.toUpperCase() === 'HEPSIBURADA'
  const useApproveDialog = !approveIsHepsiburada || detailItem.rawStatus.replace(/[_-]/gu, '').toUpperCase() !== 'AWAITINGPREAPPROVAL'
  const approveRows: ReturnApproveRow[] = (detailQuery.data?.lines ?? []).filter(isActionableReturnLine).map(line => ({ id: line.id, orderLineId: line.orderLineId, title: line.title, imageUrl: line.imageUrl, quantity: line.remainingQuantity > 0 ? line.remainingQuantity : line.quantity, sku: line.sku, barcode: formatMarketplaceBarcode(detailItem.platformCode, line.barcode) ?? line.barcode, color: returnLineFieldValue(line, ['renk', 'color'], 'color') ?? null, size: returnLineFieldValue(line, ['beden', 'size'], 'size') ?? null, modelCode: line.modelCode ?? null, hasInventoryMapping: line.hasInventoryMapping }))
  const actionRequired = returnGroup(item.status) === 'ACTION_REQUIRED'
  const urgentDeadline = isUrgentReturnDeadline(item.actionDueAt)
  const activeCountdown = shouldShowReturnCountdown(actionRequired || hasOutboundShipment, item.actionDueAt)
  const canPrint = canPrintReturnLabel(item.status, hasOutboundShipment)
  const [ringTone, ringProgress] = returnRingTone(item.status)
  const customer = order?.customerName ?? item.customerName
  useEffect(() => {
    if (!dialogMode || !detailQuery.data) return
    setSelectedReturnLineIds(detailQuery.data.lines?.filter(isActionableReturnLine).map(line => line.id) ?? [])
  }, [dialogMode, detailQuery.data?.id])
  // Each unit price sits level with its product line (desktop table only).
  useLayoutEffect(() => {
    const row = rowRef.current
    if (!row) return
    const align = () => {
      const products = row.querySelectorAll<HTMLElement>('.rr-pline'), prices = row.querySelectorAll<HTMLElement>('.rr-pr')
      const table = window.innerWidth > 1100
      prices.forEach((price, index) => { price.style.height = table && products[index] ? `${products[index].getBoundingClientRect().height}px` : '' })
    }
    align()
    if (typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(align)
    row.querySelectorAll('.rr-pline').forEach(element => observer.observe(element))
    return () => observer.disconnect()
  }, [lines.length, noBuyer])
  function toggleReturnLine(lineId: string) { setSelectedReturnLineIds(current => current.includes(lineId) ? current.filter(id => id !== lineId) : [...current, lineId]) }
  async function copyReturnOrderNumber() { try { await navigator.clipboard.writeText(item.orderNumber); setCopiedOrderNumber(true); window.setTimeout(() => setCopiedOrderNumber(false), 1400) } catch { setCopiedOrderNumber(false) } }
  const menuItems: Array<[ 'check' | 'close' | 'open' | 'print', string, () => void, string?, boolean?] | 'hr'> = actionRequired
    ? [['check', 'İadeyi Onayla', () => setDialogMode('APPROVE')], ['close', 'İade Ret Talebi', () => setDialogMode('REJECT'), 'rr-danger'], 'hr', ['open', 'Detaylı Bilgi', () => setDialogMode('DETAIL')], ['print', 'İade Etiketi Yazdır', () => setLabelOpen(true), '', !canPrint]]
    : canPrint ? [['print', 'İade Etiketi Yazdır', () => setLabelOpen(true)], 'hr', ['open', 'Detaylı Bilgi', () => setDialogMode('DETAIL')]]
    : [['open', 'Detaylı Bilgi', () => setDialogMode('DETAIL')], 'hr', ['print', 'İade Etiketi Yazdır', () => setLabelOpen(true), '', true]]
  const cargoIcon = cargoCarrier(cargoProvider)?.iconUrl
  const reasonNote = item.platformCode?.toUpperCase() === 'TRENDYOL' && item.reasonText?.trim() ? ['Müşteri Notu', item.reasonText.trim()] : reasonPresentation.explanation ? ['Açıklama', reasonPresentation.explanation] : null
  const closeDialog = () => setDialogMode(null)
  return <><tr ref={rowRef} className={[selection?.checked ? 'rr-sel' : '', urgentDeadline && actionRequired ? 'rr-due' : ''].filter(Boolean).join(' ') || undefined}>
    <td className="rr-wide"><div className="rr-ordercell">{selectColumn && (selection ? <input type="checkbox" className="rr-cb" checked={selection.checked} onChange={selection.onChange} aria-label={`${displayOrderNumber(item.orderNumber)} iadesini toplu onaya dahil et`} /> : <span style={{ width: 16, flex: 'none' }} />)}<div><RrWordmark code={item.platformCode} name={item.platformDisplayName || returnPlatformName(item.platformCode)} /><span className={`rr-on-no${urgentDeadline && actionRequired ? ' rr-crit' : ''}`}>{displayOrderNumber(item.orderNumber)}<button type="button" className="rr-icon-btn" title={copiedOrderNumber ? 'Kopyalandı' : 'Kopyala'} aria-label={copiedOrderNumber ? `Sipariş numarası ${item.orderNumber} kopyalandı` : `Sipariş numarası ${item.orderNumber} kopyala`} style={{ width: 22, height: 22 }} onClick={() => void copyReturnOrderNumber()}><RrIcon name={copiedOrderNumber ? 'check' : 'copy'} size={14} /></button></span>{noBuyer && <small className="rr-buyer-inline"><b>Alıcı:</b> {customer}</small>}<small className="rr-num"><b>Sipariş Tarihi:</b> {returnDateText(order?.orderedAt ?? item.orderedAt)}</small><small className="rr-num"><b>Paket No:</b> {orderShipment?.externalPackageId ?? item.packageNumber ?? '—'}</small><small className="rr-num"><b>Kargo Teslimat No:</b> {trackingNumber?.trim() || 'Henüz platformdan iletilmedi'}</small></div></div></td>
    {!noBuyer && <td className="rr-mid" data-l="Alıcı"><div className="rr-cust"><b>{customer}</b></div></td>}
    <td className="rr-wide" data-l="Bilgiler"><div className="rr-plines">{lines.length ? lines.map(line => { const imageUrl = line.imageUrl ?? item.primaryImageUrl; const quantity = productLineQuantity(line); const facts = returnLineFacts(item.platformCode, line); return <div className="rr-pline" key={line.id}><RrThumb src={imageUrl} quantity={quantity} alt={line.title} onOpen={() => setPreviewLine(line)} /><div><b title={line.title}><ProductTitleLink platformCode={item.platformCode} line={line} /></b><small className="rr-card-only"><span>Birim Fiyatı:</span> <b className="rr-num">{money(line.unitPrice)}</b></small>{facts.map(([label, value]) => <small key={label} title={`${label}: ${value}`}><span>{label}:</span> <b className="rr-num">{value}</b></small>)}</div></div> }) : <div className="rr-pline"><RrThumb src={item.primaryImageUrl} alt={`${item.productCount} ürün`} /><div><b>{item.productCount} ürün</b><small><span>Barkod:</span> <b className="rr-num">{formatMarketplaceBarcode(item.platformCode, item.primaryBarcode) ?? '—'}</b></small></div></div>}</div></td>
    <td className="rr-mid rr-price-col" data-l="Birim Fiyat"><div className="rr-prices">{lines.length ? lines.map(line => { const quantity = productLineQuantity(line); return <div className="rr-pr" key={line.id}><b className="rr-num">{money(line.unitPrice)}</b>{quantity > 1 && <small>{quantity} adet</small>}</div> }) : <div className="rr-pr"><b className="rr-num">{money(item.orderAmount)}</b></div>}</div></td>
    <td className="rr-mid" data-l="Kargo"><div className="rr-cargocell">{cargoIcon ? <img className="rr-cargo-img" src={cargoIcon} alt={cargoLabel(cargoProvider)} title={cargoLabel(cargoProvider)} referrerPolicy="no-referrer" loading="lazy" /> : <span className="rr-muted">{cargoProvider?.trim() ? cargoLabel(cargoProvider) : 'Kargo bilgisi bekleniyor'}</span>}<small className="rr-num">{trackingNumber?.trim() || '—'}</small>{canTrackReturn ? <a className="rr-btn rr-link" style={{ fontSize: 12 }} href={item.cargoTrackingLink!} target="_blank" rel="noopener noreferrer">Kargo Takip <RrIcon name="open" /></a> : canTrackOrder && orderShipment && <button type="button" className="rr-btn rr-link" style={{ fontSize: 12 }} onClick={() => navigate(`/shipments/${orderShipment.id}`)}>Paket Detayı <RrIcon name="open" /></button>}</div></td>
    <td data-l="Fatura"><div className="rr-invcell">{hasDiscount && <><span className="rr-m"><span>Satış Tutarı</span><b className="rr-num">{money(total)}</b></span><span className="rr-m"><span>Satıcı İndirimi</span><b className="rr-num">{money(discountAmount)}</b></span></>}<span className="rr-m rr-pay"><span>Faturalanacak Tutar</span><b className="rr-num">{money(invoicedTotal)}</b></span><ReturnInvoiceRing status={invoiceStatus} isMicroExport={item.isMicroExport} /></div></td>
    <td data-l="İade Sebebi"><div className="rr-reasoncell"><b>{reasonPresentation.label}</b>{reasonNote && <small title={reasonNote[1]}><strong>{reasonNote[0]}:</strong> {reasonNote[1]}</small>}</div></td>
    <td className="rr-mid" data-l="Durum"><RrRing tone={ringTone} progress={ringProgress} label={titleTr(statusLabel(item.status))} detail={activeCountdown ? returnRemaining(item.actionDueAt) : undefined} detailClass={activeCountdown && urgentDeadline ? 'crit' : undefined} /></td>
    <td className="rr-mid" data-l="İşlemler"><div className="rr-actcell"><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} aria-haspopup="menu" aria-expanded={open} onClick={toggle}><span>İşlemler</span><RrIcon name="chev" /></button>}>{close => <div className="rr-menu" role="menu" aria-label={`${displayOrderNumber(item.orderNumber)} iade işlemleri`}>{menuItems.map((entry, index) => entry === 'hr' ? <hr key={`hr-${index}`} /> : <button key={entry[1]} type="button" role="menuitem" className={entry[3] || undefined} disabled={entry[4]} title={entry[4] ? 'Bu iade için etiket henüz oluşmadı.' : undefined} onClick={() => { close(); entry[2]() }}><RrIcon name={entry[0]} />{entry[1]}</button>)}</div>}</RrAnchor><button type="button" className="rr-btn rr-link" onClick={() => navigate(`/returns/${item.id}`)}>Detayları Aç <RrIcon name="open" /></button></div></td>
  </tr>
    {previewLine && <ReturnImagePreview title={previewLine.title} imageUrl={previewLine.imageUrl ?? item.primaryImageUrl} facts={returnLineFacts(item.platformCode, previewLine)} onClose={() => setPreviewLine(null)} />}
    {labelOpen && <ReturnLabelPreview item={item} lines={lines} onClose={() => setLabelOpen(false)} />}
    {dialogMode === 'APPROVE' && useApproveDialog && <ReturnApproveDialog open claimId={item.id} version={detailItem.version} marketplaceName={marketplaceName} order={{ connectionId: detailItem.connectionId ?? item.connectionId ?? null, platformCode: detailItem.platformCode, platformName: marketplaceName, orderNumber: displayOrderNumber(detailItem.orderNumber), customer: detailItem.customerName, orderedAt: detailItem.orderedAt, packageNumber: item.packageNumber, claimCode: item.externalClaimId, reason: reasonPresentation.label }} rows={approveRows} wholeClaim={approveIsHepsiburada} loading={detailQuery.isLoading} error={detailQuery.isError ? detailQuery.error : null} onClose={closeDialog} onDone={(message, kind, details, target) => notifyReturn(onNotice, message, kind, details, target)} />}
    {dialogMode && !(dialogMode === 'APPROVE' && useApproveDialog) && <RrModal crumb={`${marketplaceName} İade`} title={dialogMode === 'APPROVE' ? 'İade Onayı' : dialogMode === 'REJECT' ? 'İade Ret Talebi' : 'Detaylı Bilgi'} desc={`Sipariş ${displayOrderNumber(detailItem.orderNumber)} · ${detailItem.customerName}`} size="lg" onClose={closeDialog} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={closeDialog}>Kapat</button></>}>
      {detailQuery.isLoading ? <RrLoading text="İade detayları yükleniyor…" /> : detailQuery.isError || !detailQuery.data ? <RrError error={detailQuery.error} onRetry={() => void detailQuery.refetch()} /> : <>
        <ReturnDetailFacts item={detailItem} plain={dialogMode === 'DETAIL'} invoicedTotal={invoicedTotal} money={detailMoney} />
        <div className="rr-h3">Ürünler</div>
        {dialogMode === 'DETAIL' ? detailLines.map(line => {
          const approvedAt = returnLineFieldValue(line, ['onaylanma', 'approved'], 'approvedAt') ?? detailItem.approvedAt
          const systemNote = returnLineFieldValue(line, ['sistem notu', 'system'], 'systemNote') ?? returnOptionalValue(detailItem.systemNote)
          const reasonText = returnLineFieldValue(line, ['sebep', 'reason'], 'reasonText') ?? detailItem.reasonText
          const sellerDescription = returnLineFieldValue(line, ['satıcı açıklaması', 'seller'], 'sellerDescription') ?? returnOptionalValue(detailItem.sellerDescription)
          const platformReason = returnLineFieldValue(line, ['trendyol onay nedeni', 'approval reason'], 'platformApprovalReason') ?? returnOptionalValue(detailItem.platformApprovalReason)
          const platformDescription = returnLineFieldValue(line, ['trendyol açıklaması', 'platform description'], 'platformDescription') ?? returnOptionalValue(detailItem.platformDescription)
          const fields: Array<[string, string, string?]> = [['Onaylanma Tarihi', approvedAt ? returnDateText(approvedAt) : '-', approvedAt ? undefined : 'rr-empty'], ['Sistem Notu', systemNote ?? '-', systemNote ? 'rr-sys' : 'rr-empty'], ['Sebep', reasonText ?? '-', reasonText ? undefined : 'rr-empty'], ['Satıcı Açıklaması', sellerDescription ?? '-', sellerDescription ? undefined : 'rr-empty'], [`${marketplaceName} Onay Nedeni`, platformReason ?? '-', platformReason ? undefined : 'rr-empty'], [`${marketplaceName} Açıklaması`, platformDescription ?? '-', platformDescription ? undefined : 'rr-empty']]
          return <article className="rr-refline" key={line.id}><div className="rr-rh"><RrThumb src={line.imageUrl} quantity={productLineQuantity(line as OrderLine)} large alt={line.title} /><div><b>{line.title}<button type="button" className="rr-icon-btn" title="Ürün adını kopyala" aria-label="Ürün adını kopyala" style={{ width: 22, height: 22 }} onClick={() => { void navigator.clipboard?.writeText(line.title).catch(() => undefined) }}><RrIcon name="copy" /></button></b><ReturnFactLines facts={returnLineFacts(detailItem.platformCode, line)} /></div></div><dl className="rr-kv">{fields.map(([label, value, className]) => <Fragment key={label}><dt>{label}</dt><dd className={className}>{value}</dd></Fragment>)}</dl></article>
        }) : <div className="rr-pcards">{detailLines.map(line => {
          const selectable = decisionAvailable
          const selected = selectedReturnLineIds.includes(line.id)
          const facts = returnLineFacts(detailItem.platformCode, line)
          const ordered = ['Barkod', 'Stok Kodu', 'Renk', 'Beden', 'Model Kodu'].map(label => facts.find(fact => fact[0] === label)).filter((fact): fact is [string, string] => Boolean(fact))
          const reason = returnLineFieldValue(line, ['sebep', 'reason'], 'reasonText') ?? detailItem.reasonText
          return <article className={`rr-pcard${selectable && selected ? ' rr-on' : ''}`} key={line.id} onClick={selectable ? () => toggleReturnLine(line.id) : undefined} onKeyDown={selectable ? event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); toggleReturnLine(line.id) } } : undefined} role={selectable ? 'button' : undefined} tabIndex={selectable ? 0 : undefined} aria-pressed={selectable ? selected : undefined}><div className="rr-pc-h"><RrThumb src={line.imageUrl} quantity={line.quantity} large alt={line.title} /><span className="rr-ptext"><b>{line.title}</b><small>{line.quantity} adet · {detailMoney(line.unitPrice)}</small></span>{selectable && <label className="rr-cklabel" onClick={event => event.stopPropagation()}><input type="checkbox" className="rr-cb" checked={selected} onChange={() => toggleReturnLine(line.id)} />İşleme Dahil</label>}</div><dl className="rr-pairs">{ordered.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}{reason && <div><dt>Sebep</dt><dd>{reason}</dd></div>}</dl></article>
        })}</div>}
        {dialogMode === 'DETAIL' ? <ReturnInlineActions item={detailQuery.data} onNotice={onNotice} /> : <ReturnDecisionActions item={detailQuery.data} selectedLineIds={selectedReturnLineIds} onSelectedLineIdsChange={setSelectedReturnLineIds} onNotice={onNotice} onComplete={closeDialog} initialAction={dialogMode} showActionPicker={false} />}
      </>}
    </RrModal>}
  </>
}

function returnPlatformName(platformCode: string | null | undefined) {
  switch (platformCode?.toUpperCase()) {
    case 'HEPSIBURADA': return 'Hepsiburada'
    case 'N11': return 'N11'
    case 'PAZARAMA': return 'Pazarama'
    case 'SHOPIFY': return 'Shopify'
    case 'TRENDYOL': return 'Trendyol'
    default: return 'Pazar yeri'
  }
}

function returnPlatformStatusText(rawStatus: string | null | undefined, marketplaceName: string) {
  if (!rawStatus) return ''
  const normalized = rawStatus.trim().toUpperCase().replace(/[\s-]+/g, '_').replace(/[^A-Z0-9_]/g, '')
  const known: Record<string, string> = {
    WAITINGFRAUDCHECK: `${marketplaceName} incelemesi bekleniyor`,
    WAITING_FRAUD_CHECK: `${marketplaceName} incelemesi bekleniyor`,
    WAITING_APPROVAL: `${marketplaceName} onayı bekleniyor`,
    APPROVED: `${marketplaceName} tarafından onaylandı`,
    REJECTED: `${marketplaceName} tarafından reddedildi`,
  }
  return known[normalized] ?? statusLabel(rawStatus)
}

type ReturnNoticeKind = 'success' | 'error' | 'info'
type ReturnNoticeHandler = (message: string, kind?: ReturnNoticeKind) => void

function returnPlatformStatusKind(rawStatus: string | null | undefined): ReturnNoticeKind {
  if (!rawStatus) return 'success'
  const normalized = rawStatus.trim().toUpperCase().replace(/[\s-]+/g, '_').replace(/[^A-Z0-9_]/g, '')
  if (/(WAITING|PENDING|PROCESS|FRAUD|REVIEW|APPROVAL)/u.test(normalized)) return 'info'
  if (/(REJECT|DECLIN|FAIL|ERROR)/u.test(normalized)) return 'error'
  if (/(APPROV|ACCEPT|COMPLETE|SUCCESS)/u.test(normalized)) return 'success'
  return 'info'
}

function notificationKindFromMessage(message: string): ReturnNoticeKind {
  const normalized = message.toLocaleLowerCase('tr-TR')
  if (/(hata|başarısız|kaydedilemedi|güncellenemedi|silinemedi|oluşturulamadı|alınamadı|bulunamadı|zorunlu|seçin|geçersiz|uygulanamadı|okunamadı|yenilenemedi|eşlenemedi)/u.test(normalized)) return 'error'
  if (/(kuyruğa|işleniyor|bekleniyor|salt-okunur|değişiklik yok|henüz)/u.test(normalized)) return 'info'
  return 'success'
}

function notifyReturn(onNotice: ReturnNoticeHandler | undefined, message: string, kind: ReturnNoticeKind = 'info', details?: NotificationDetail[], target?: NotificationTarget | null) {
  appendNotification(message, kind, target, details)
  onNotice?.(message, kind)
}

function ReturnReceiptButton({ item, onNotice }: { item: ReturnDetail; onNotice?: ReturnNoticeHandler }) {
  const client = useQueryClient()
  const receive = useMutation({
    mutationFn: () => hubApi(`/returns/${item.id}/received`, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${item.version}"` } }),
    onSuccess: async () => {
      notifyReturn(onNotice, 'İade paketi teslim alındı. Onay veya ret işlemleri açıldı.', 'success')
      await client.invalidateQueries({ queryKey: ['return', item.id] })
      await client.invalidateQueries({ queryKey: ['returns'] })
    },
    onError: error => notifyReturn(onNotice, error instanceof Error ? error.message : 'İade teslim alındı olarak işaretlenemedi.', 'error')
  })
  if (!item.allowedActions.includes('RECEIVE')) return null
  return <section className="rr-dsec"><header><div className="rr-h3">Kargo Teslimi</div><p>İade paketi fiziksel olarak elinize ulaştıysa teslim aldığınızı işaretleyin. Ardından aynı pencereden onay veya ret kararı verebilirsiniz.</p></header><div><button type="button" className="rr-btn" disabled={receive.isPending} onClick={() => receive.mutate()}><RrIcon name="box" />{receive.isPending ? 'Kaydediliyor…' : 'Teslim Aldım'}</button></div></section>
}

type ReturnDecisionDraft = { action: 'APPROVE' | 'REJECT' | 'PREAPPROVAL_CONFIRM'; reasonCode: string | null; explanation: string | null; evidenceAssetIds: string[]; returnLineIds: string[]; reasonLabel: string | null; finalizedWith: 'Refund' | 'Change' | null }

type ReturnDecisionActionsProps = { item: ReturnDetail; onNotice?: ReturnNoticeHandler; onComplete?: () => void; selectedLineIds?: string[]; onSelectedLineIdsChange?: (ids: string[]) => void; initialAction?: ReturnDecisionDraft['action']; showActionPicker?: boolean }

function ReturnDecisionActions({ item, onNotice, onComplete, selectedLineIds: controlledSelectedLineIds, onSelectedLineIdsChange, initialAction, showActionPicker = true }: ReturnDecisionActionsProps) {
  const client = useQueryClient(); const lines = (item.lines ?? []).filter(isActionableReturnLine); const isHepsiburada = item.platformCode?.toUpperCase() === 'HEPSIBURADA'; const isAwaitingPreApproval = isHepsiburada && item.rawStatus.replace(/[_-]/gu, '').toUpperCase() === 'AWAITINGPREAPPROVAL'; const [action, setAction] = useState<ReturnDecisionDraft['action']>(() => initialAction ?? (isAwaitingPreApproval ? 'PREAPPROVAL_CONFIRM' : 'APPROVE')); const [reasonId, setReasonId] = useState(''); const [finalizedWith, setFinalizedWith] = useState<'Refund' | 'Change' | ''>(''); const [evidenceAssetIds, setEvidenceAssetIds] = useState<string[]>([]); const [localSelectedLineIds, setLocalSelectedLineIds] = useState<string[]>(() => lines.map(line => line.id)); const [confirmation, setConfirmation] = useState<ReturnDecisionDraft | null>(null); const isControlled = controlledSelectedLineIds !== undefined; const selectedLineIds = controlledSelectedLineIds ?? localSelectedLineIds; const updateSelectedLineIds = onSelectedLineIdsChange ?? setLocalSelectedLineIds; const externalWritesDisabled = item.externalWritesEnabled !== true
  const marketplaceName = returnPlatformName(item.platformCode)
  const fileInput = useRef<HTMLInputElement>(null)
  useEffect(() => { if (!isControlled) setLocalSelectedLineIds(lines.map(line => line.id)); setConfirmation(null); setAction(initialAction ?? (isAwaitingPreApproval ? 'PREAPPROVAL_CONFIRM' : 'APPROVE')) }, [item.id, item.version, item.lines, isControlled, isAwaitingPreApproval, initialAction])
  const reasons = useQuery({ queryKey: ['return-reasons', item.id], queryFn: () => hubApi<ReturnIssueReason[]>(`/returns/${item.id}/rejection-reasons`), enabled: item.allowedActions.includes('REJECT'), retry: false })
  const decision = useMutation({
    mutationFn: (payload: ReturnDecisionDraft) => hubApi<ReturnDetail>(`/returns/${item.id}/actions`, { method: 'POST', headers: { 'Idempotency-Key': idempotency(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ action: payload.action, reasonCode: payload.reasonCode, explanation: payload.explanation, evidenceAssetIds: payload.evidenceAssetIds, returnLineIds: payload.returnLineIds, finalizedWith: payload.finalizedWith }) }),
    onSuccess: async (result, payload) => {
      setConfirmation(null)
      const response = result.rawStatus ? ` ${marketplaceName} durumu: ${returnPlatformStatusText(result.rawStatus, marketplaceName)}.` : ''
      const message = payload.action === 'PREAPPROVAL_CONFIRM'
        ? `${displayOrderNumber(item.orderNumber)} siparişi için inceleme geri gönderim talebi ${marketplaceName}’a iletildi.${response}`
        : result.status === 'APPROVED'
        ? `${displayOrderNumber(item.orderNumber)} siparişinin iade kabul kararı ${marketplaceName}’a iletildi.${response}`
        : result.status === 'REJECTED'
          ? `${displayOrderNumber(item.orderNumber)} siparişinin iade ret kararı ${marketplaceName}’a iletildi.${response}`
          : result.rawStatus
            ? `${displayOrderNumber(item.orderNumber)} siparişinin iade kararı ${marketplaceName}’a iletildi; sonuç henüz kesinleşmedi.${response}`
            : `${displayOrderNumber(item.orderNumber)} siparişinin iade kararı iletildi ancak ${marketplaceName}’dan yanıt alınamadı.`
      const kind = payload.action === 'PREAPPROVAL_CONFIRM' ? 'info' : result.status === 'REJECTED' ? 'error' : result.status === 'APPROVED' ? returnPlatformStatusKind(result.rawStatus) : 'info'
      notifyReturn(onNotice, message, kind, [{ label: 'Sipariş', value: `${displayOrderNumber(item.orderNumber)} · ${item.customerName}`, tone: kind === 'error' ? 'error' : undefined }, { label: 'Pazar Yeri', value: marketplaceName }], { href: `/returns?search=${encodeURIComponent(item.orderNumber.replace(/^#/, ''))}`, label: 'İadeyi Gör' })
      await client.invalidateQueries({ queryKey: ['return', item.id] })
      await client.invalidateQueries({ queryKey: ['returns'] })
      onComplete?.()
    },
    onError: error => {
      const reason = error instanceof Error ? error.message : 'İade kararı başlatılamadı.'
      notifyReturn(onNotice, `${displayOrderNumber(item.orderNumber)} siparişinin iade kararı ${marketplaceName}’a iletilemedi: ${reason}`, 'error', [{ label: 'Sipariş', value: `${displayOrderNumber(item.orderNumber)} · ${item.customerName}` }, { label: 'Pazar Yeri', value: marketplaceName }, { label: 'Neden', value: reason, tone: 'error' }], isExternalWriteMessage(reason) ? integrationNotificationTarget(item.connectionId, marketplaceName) : { href: `/returns?search=${encodeURIComponent(item.orderNumber.replace(/^#/, ''))}`, label: 'İadeyi Gör' })
    }
  })
  async function uploadFile(file: File) {
    if (!file.size) return
    const body = new FormData(); body.set('file', file)
    try { const asset = await hubApi<{ id: string }>('/files/return-evidence', { method: 'POST', body }); setEvidenceAssetIds(ids => ids.includes(asset.id) ? ids : [...ids, asset.id]); notifyReturn(onNotice, 'İade kanıtı güvenli depoya yüklendi.', 'success') } catch (error) { notifyReturn(onNotice, error instanceof Error ? error.message : 'Kanıt yüklenemedi.', 'error') }
  }
  const decisionActions = item.allowedActions.filter(value => value === 'APPROVE' || value === 'REJECT' || value === 'PREAPPROVAL_CONFIRM')
  const decisionLabel = (value: string) => value === 'PREAPPROVAL_CONFIRM' ? 'İnceleme İçin Geri Gönder' : value === 'APPROVE' ? 'Onayla' : value === 'REJECT' ? 'Reddet' : titleTr(statusLabel(value))
  if (!decisionActions.length) return null
  if (initialAction && !decisionActions.includes(initialAction)) return <section className="rr-dsec"><header><div className="rr-h3">Bu İşlem Kullanılamıyor</div><p>İade durumu, {marketplaceName} tarafında bu kararın gönderilmesine izin vermiyor.</p></header></section>
  const selectedReason = reasons.data?.find(reason => reason.id === reasonId)
  function requestDecision(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (externalWritesDisabled) { notifyReturn(onNotice, 'Dış yazma kapalı olduğu için iade kararı gönderilemez.', 'error'); return }
    if (isHepsiburada ? !lines.length : !selectedLineIds.length) { notifyReturn(onNotice, 'İşlem yapılacak en az bir ürün seçin.', 'error'); return }
    const data = new FormData(event.currentTarget)
    if (isHepsiburada && action === 'APPROVE' && !finalizedWith) { notifyReturn(onNotice, 'Hepsiburada talebi için ücret iadesi veya ürün değişimi seçin.', 'error'); return }
    setConfirmation({ action, reasonCode: action === 'REJECT' ? reasonId : null, explanation: action === 'REJECT' ? String(data.get('explanation') ?? '') : null, evidenceAssetIds: [...evidenceAssetIds], returnLineIds: isHepsiburada ? lines.map(line => line.id) : [...selectedLineIds], reasonLabel: action === 'REJECT' ? selectedReason?.name ?? null : null, finalizedWith: isHepsiburada && action === 'APPROVE' && finalizedWith ? finalizedWith : null })
  }
  const selectedLines = isHepsiburada ? lines : lines.filter(line => selectedLineIds.includes(line.id))
  const allLinesSelected = lines.length > 0 && selectedLineIds.length === lines.length
  const reject = action === 'REJECT'
  const submitLabel = reject ? 'Ret Kararını Gönder' : action === 'PREAPPROVAL_CONFIRM' ? 'İnceleme Talebi Oluştur' : 'İadeyi Onayla'
  const confirmKind = confirmation?.action === 'REJECT' ? 'reject' : confirmation?.action === 'PREAPPROVAL_CONFIRM' ? 'pre' : 'approve'
  const confirmExtra: Array<[string, string]> = !confirmation ? [] : confirmation.action === 'REJECT' ? [['Ret Nedeni', confirmation.reasonLabel ?? '—'], ['Açıklama', confirmation.explanation || '—']] : confirmation.action === 'PREAPPROVAL_CONFIRM' ? [['İnceleme Nedeni', 'Ürün incelemesi']] : confirmation.finalizedWith ? [['Onay Türü', confirmation.finalizedWith === 'Refund' ? 'Ücret İadesi' : 'Ürün Değişimi']] : []
  return <section className="rr-dsec">
    <header><div className="rr-h3">İade İşlemi</div><p>{isAwaitingPreApproval ? 'Ön onay talebinde iade, değişim, ret veya inceleme için geri gönderim kararını seçin.' : 'İade paketi kontrol edildikten sonra kabul veya ret kararını buradan gönderin.'}</p><p>Birden fazla ürün varsa ürün kartlarına tıklayarak hangi ürünlerin işleme alınacağını seçin. Karar gönderilmeden önce seçiminizi özet penceresinde onaylayabilirsiniz.</p></header>
    {externalWritesDisabled && <div className="rr-callout rr-warn" role="status"><RrIcon name="alert" /><span><b>Dış Yazma Kapalı.</b> Bu iade için onay veya ret gönderilemez.</span></div>}
    {isControlled && <div className="rr-selnote"><strong>{selectedLineIds.length} / {lines.length} ürün seçildi</strong><span>Ürün seçimini yukarıdaki kartlardan değiştirebilirsiniz.</span></div>}
    {!isControlled && lines.length > 1 && !isHepsiburada && <div className="rr-selnote"><label className="rr-cklabel"><input type="checkbox" className="rr-cb" checked={allLinesSelected} onChange={event => updateSelectedLineIds(event.target.checked ? lines.map(line => line.id) : [])} /><strong>{selectedLineIds.length} / {lines.length} ürün seçildi</strong></label>{lines.map(line => <label key={line.id} className="rr-cklabel"><input type="checkbox" className="rr-cb" checked={selectedLineIds.includes(line.id)} onChange={event => updateSelectedLineIds(event.target.checked ? [...selectedLineIds, line.id] : selectedLineIds.filter(id => id !== line.id))} />{line.title} · {line.quantity.toLocaleString('tr-TR')} adet</label>)}</div>}
    <form style={{ display: 'contents' }} onSubmit={requestDecision}>
      <div className="rr-dgrid">
        {showActionPicker ? <label className="rr-fld"><small>Karar</small><select className="rr-in" value={action} onChange={event => { setAction(event.target.value as ReturnDecisionDraft['action']); setReasonId(''); setFinalizedWith(''); setEvidenceAssetIds([]) }}>{decisionActions.map(value => <option key={value} value={value}>{decisionLabel(value)}</option>)}</select></label> : <div className="rr-fld"><small>İşlem</small><b>{reject ? 'İade Ret Talebi' : action === 'PREAPPROVAL_CONFIRM' ? 'İnceleme İçin Geri Gönder' : 'İadeyi Onayla'}</b></div>}
        {isHepsiburada && action === 'APPROVE' && <label className="rr-fld"><small>Onay Türü</small><select className="rr-in" value={finalizedWith} onChange={event => setFinalizedWith(event.target.value as 'Refund' | 'Change' | '')} required><option value="">Seçin</option><option value="Refund">Ücret İadesi</option><option value="Change">Ürün Değişimi</option></select></label>}
        {reject && <><label className="rr-fld"><small>{marketplaceName} Ret Nedeni</small><select className="rr-in" value={reasonId} onChange={event => setReasonId(event.target.value)} required disabled={reasons.isLoading || reasons.isError}><option value="">{reasons.isLoading ? 'Ret nedenleri yükleniyor…' : reasons.isError ? 'Ret nedenleri alınamadı' : 'Seçin'}</option>{reasons.data?.map(reason => <option key={reason.id} value={reason.id}>{reason.name}</option>)}</select>{reasons.isError ? <em className="rr-err">{marketplaceName} ret nedenleri alınamadı. İade detayını yenileyip tekrar deneyin.</em> : selectedReason && <em>{selectedReason.evidenceRequired ? 'Bu ret nedeni için kanıt dosyası zorunludur.' : 'Bu ret nedeni için açıklama zorunludur.'}</em>}</label>
          <label className="rr-fld rr-full"><small>{marketplaceName} Açıklaması</small><textarea className="rr-in" name="explanation" maxLength={500} placeholder="Ret kararının nedenini yazın…" required /></label>
          <div className="rr-fld rr-full"><small>Kanıt (PDF/JPEG/PNG, En Fazla 10 MiB)</small><div className="rr-drop" role="button" tabIndex={0} onClick={() => fileInput.current?.click()} onKeyDown={event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); fileInput.current?.click() } }} onDragOver={event => event.preventDefault()} onDrop={event => { event.preventDefault(); const file = event.dataTransfer.files?.[0]; if (file) void uploadFile(file) }}><RrIcon name="upload" /><b>Dosya Seç veya Sürükle</b><small>PDF, JPEG veya PNG · en fazla 10 MiB</small><input ref={fileInput} type="file" hidden accept="application/pdf,image/jpeg,image/png" onChange={event => { const file = event.currentTarget.files?.[0]; if (file) void uploadFile(file); event.currentTarget.value = '' }} /></div><em>{evidenceAssetIds.length} kanıt hazır{selectedReason?.evidenceRequired ? ' · bu neden için zorunlu' : ''}</em></div></>}
      </div>
      {isHepsiburada && lines.length > 1 && <em className="rr-lead">Hepsiburada kararı bu talepteki tüm ürün satırlarına uygulanır.</em>}
      {isAwaitingPreApproval && action === 'PREAPPROVAL_CONFIRM' && <em className="rr-lead">Hepsiburada’ya ürün incelemesi için geri gönderim ön onayı iletilecek.</em>}
      <div style={{ display: 'flex', justifyContent: 'flex-end' }}><button type="submit" className={`rr-btn ${reject ? 'rr-solid' : 'rr-primary'}`} disabled={externalWritesDisabled || decision.isPending || !lines.length || (isHepsiburada && action === 'APPROVE' && !finalizedWith) || (reject && (reasons.isLoading || reasons.isError || !reasonId || (selectedReason?.evidenceRequired && !evidenceAssetIds.length)))} title={externalWritesDisabled ? 'Dış yazma kapalı' : undefined}>{submitLabel}</button></div>
    </form>
    {confirmation && <RrModal title={confirmKind === 'reject' ? 'Ret Kararını Onayla' : confirmKind === 'pre' ? 'İnceleme İçin Geri Gönderimi Onayla' : 'İade Kararını Onayla'} desc={confirmKind === 'pre' ? 'Hepsiburada’ya ürün incelemesi için geri gönderim ön onayı iletilecek.' : 'Seçilen karar sağlayıcıya gönderilecek; yanıt alındığında durumu burada gösterilecek.'} size="md" onClose={() => { if (!decision.isPending) setConfirmation(null) }} foot={<><span className="rr-note" /><button type="button" className="rr-btn" disabled={decision.isPending} onClick={() => setConfirmation(null)}>Vazgeç</button><button type="button" className={`rr-btn ${confirmKind === 'reject' ? 'rr-solid' : 'rr-primary'}`} disabled={externalWritesDisabled || decision.isPending} onClick={() => decision.mutate(confirmation)} title={externalWritesDisabled ? 'Dış yazma kapalı' : undefined}>{decision.isPending ? 'Gönderiliyor…' : 'Kararı Onayla'}</button></>}>
      <div><span className={`rr-decision-tag rr-${confirmKind === 'reject' ? 'reject' : 'approve'}`}>{confirmKind === 'reject' ? 'Reddet' : confirmKind === 'pre' ? 'İncelemeye Geri Gönder' : 'Onayla'}</span></div>
      <div className="rr-h3">Seçilen Ürünler</div>
      <div className="rr-sum">{selectedLines.map(line => { const color = returnLineFieldValue(line, ['renk', 'color'], 'color'); const size = returnLineFieldValue(line, ['beden', 'size'], 'size'); return <div key={line.id}><RrThumb src={line.imageUrl} quantity={line.quantity} alt={line.title} /><span>{line.title}<small>Stok Kodu: {line.sku || '—'}{color ? ` · Renk: ${color}` : ''}{size ? ` · Beden: ${size}` : ''}</small></span><b className="rr-num">{line.unitPrice.toLocaleString('tr-TR', { style: 'currency', currency: item.currency })}</b></div> })}</div>
      {confirmExtra.length > 0 && <dl className="rr-kv">{confirmExtra.map(([label, value]) => <Fragment key={label}><dt>{label}</dt><dd>{value}</dd></Fragment>)}</dl>}
      <p className="rr-lead">{selectedLines.length} ürün satırı işleme alınacak.</p>
    </RrModal>}
  </section>
}

function ReturnInlineActions({ item, onNotice, selectedLineIds, onSelectedLineIdsChange, decisions = false }: { item: ReturnDetail; onNotice?: ReturnNoticeHandler; selectedLineIds?: string[]; onSelectedLineIdsChange?: (ids: string[]) => void; decisions?: boolean }) {
  const marketplaceName = returnPlatformName(item.platformCode)
  if (item.decisionPending) return <div className="rr-callout"><RrIcon name="info" /><span><b>Karar {marketplaceName}’a Gönderildi.</b> Bu iade kararı {marketplaceName}’a anlık olarak iletildi. {marketplaceName}’dan kesin durum yanıtı gelene kadar yeni bir karar gönderilemez.</span></div>
  return <><ReturnReceiptButton item={item} onNotice={onNotice} />{decisions && <ReturnDecisionActions item={item} onNotice={onNotice} selectedLineIds={selectedLineIds} onSelectedLineIdsChange={onSelectedLineIdsChange} />}</>
}

// Column filter popover: one radio list, "Filtreyi Temizle" once a value is picked.
function ReturnColumnPop({ label, options, value, onPick, right, width = 240 }: { label: string; options: Array<{ value: string; label: string }>; value: string; onPick: (value: string) => void; right?: boolean; width?: number }) {
  const active = value !== 'ALL'
  return <div className={`rr-pop${right ? ' rr-right' : ''}`} role="dialog" style={{ minWidth: width }}><div className="rr-ph">{label}</div>{options.map((option, index) => { const on = active ? option.value === value : index === 0; return <button key={option.value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => onPick(option.value)}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{option.label}</span></button> })}{active && <div className="rr-foot"><span /><button type="button" className="rr-btn rr-quiet" style={{ height: 28 }} onClick={() => onPick('ALL')}>Filtreyi Temizle</button></div>}</div>
}
function ReturnPlatformPop({ options, selected, onChange }: { options: Array<{ value: string; label: string }>; selected: string[]; onChange: (value: string[]) => void }) {
  const all = selected.length === 0
  const checked = (code: string) => all || selected.includes(code)
  const toggle = (code: string, on: boolean) => { const current = all ? options.map(option => option.value) : selected.filter(value => value !== RETURN_NO_PLATFORM); const next = on ? Array.from(new Set([...current, code])) : current.filter(value => value !== code); onChange(next.length === options.length ? [] : next.length === 0 ? [RETURN_NO_PLATFORM] : next) }
  return <div className="rr-pop" role="dialog" style={{ minWidth: 260 }}><div className="rr-ph">İade Platformu<button type="button" className="rr-btn rr-link" onClick={() => onChange(all ? [RETURN_NO_PLATFORM] : [])}>{all ? 'Tümünü Kaldır' : 'Tümünü Seç'}</button></div>{options.map(option => <label key={option.value} className="rr-opt"><input type="checkbox" className="rr-cb" checked={checked(option.value)} onChange={event => toggle(option.value, event.target.checked)} /><RrLogo code={option.value} /><span className="rr-grow">{option.label}</span></label>)}</div>
}
const RETURN_NO_PLATFORM = '__NONE__'
function ReturnHeadCell({ label, active, extra, pop }: { label: string; active?: boolean; extra?: ReactNode; pop?: (close: () => void) => ReactNode }) {
  return <th className={active ? 'rr-has-filter' : undefined}><span className="rr-hf">{extra}{label}{pop && <RrAnchor trigger={(_open, toggle) => <button type="button" className="rr-fbtn" onClick={toggle} aria-label={`${label} filtresini aç`}><RrIcon name="filter" /></button>}>{pop}</RrAnchor>}</span></th>
}
function useMediaQuery(query: string) {
  const [matches, setMatches] = useState(() => typeof window !== 'undefined' && window.matchMedia(query).matches)
  useEffect(() => { const list = window.matchMedia(query); const update = () => setMatches(list.matches); update(); list.addEventListener('change', update); window.addEventListener('resize', update); return () => { list.removeEventListener('change', update); window.removeEventListener('resize', update) } }, [query])
  return matches
}

export function ReturnsPage() {
  const tabs = [['ALL', 'Tüm İadeler'], ['REQUESTED', 'Talep Oluşturulan'], ['SHIPPING', 'Kargoya Verilen'], ['ACTION_REQUIRED', 'Aksiyon Bekleyen'], ['APPROVED', 'Onaylanan'], ['REJECTED', 'Reddedilen'], ['REVIEW', 'Analiz'], ['DISPUTED', 'İhtilaflı'], ['SUSPENDED', 'Askıda İadeler']] as const
  const [searchParams, setSearchParams] = useSearchParams()
  const requestedStatus = searchParams.get('status') ?? 'ALL'
  const initialStatus = resolveReturnStatus(requestedStatus)
  const [status, setStatus] = useState<string>(initialStatus); const [search, setSearch] = useState(() => searchParams.get('search') ?? ''); const [from, setFrom] = useState(''); const [to, setTo] = useState(''); const [pageSize, setPageSize] = useState(20); const [pageNumber, setPageNumber] = useState(1); const [jumpPage, setJumpPage] = useState(''); const [selectedPlatforms, setSelectedPlatforms] = useState<string[]>([]); const [cargoFilter, setCargoFilter] = useState('ALL'); const [reasonFilter, setReasonFilter] = useState('ALL'); const [invoiceStatusFilter, setInvoiceStatusFilter] = useState('ALL'); const [approvedSort, setApprovedSort] = useState<ApprovedReturnSort>('APPROVED_DESC')
  const client = useQueryClient(); const [notice, setNoticeState] = useState(''); const [noticeKind, setNoticeKind] = useState<ReturnNoticeKind>('info')
  function setNotice(message: string, kind: ReturnNoticeKind = 'info') { setNoticeState(message); setNoticeKind(kind) }
  const handleNotice = setNotice
  useEffect(() => {
    if (!notice) return
    const timeout = window.setTimeout(() => setNoticeState(''), noticeKind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice, noticeKind])
  const returnFilters: ReturnFilters = { status, search, from, to }
  const [appliedFilters, setAppliedFilters] = useState<ReturnFilters>(returnFilters)
  useEffect(() => {
    setStatus(initialStatus)
    setAppliedFilters(current => current.status === initialStatus ? current : { ...current, status: initialStatus })
  }, [initialStatus])
  const query = useQuery({ queryKey: ['returns', appliedFilters], queryFn: () => loadAllReturns(appliedFilters), refetchInterval: 30_000 })
  const countFilters: ReturnFilters = { ...appliedFilters, status: 'ALL' }
  const countsQuery = useQuery({ queryKey: ['returns', 'counts', countFilters], queryFn: () => loadAllReturns(countFilters), enabled: appliedFilters.status !== 'ALL', refetchInterval: 30_000 })
  const connections = useQuery({ queryKey: ['connections', 'returns'], queryFn: () => loadAllPages<Connection>('/connections') })
  const returnConnections = activeReturnSyncConnections(connections.data?.items ?? [])
  const sync = useMutation({ mutationFn: () => enqueueReturnSyncs(returnConnections, connection => hubApi(`/connections/${connection.id}/return-sync-jobs`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() } })), onSuccess: async result => { notifyReturn(handleNotice, result.failed ? `${result.succeeded} bağlantıda iade eşitlemesi kuyruğa alındı; ${result.failed} bağlantıda başlatılamadı.` : `${result.succeeded} bağlantıda iade eşitlemesi kuyruğa alındı. İş tamamlandığında tüm iadeler otomatik yenilenir.`, result.failed ? 'error' : 'info'); await client.invalidateQueries({ queryKey: ['returns'] }) }, onError: value => notifyReturn(handleNotice, value instanceof Error ? value.message : 'İade eşitlemesi başlatılamadı.', 'error') })
  const [bulkIds, setBulkIds] = useState<string[]>([])
  const [bulkMode, setBulkMode] = useState<'APPROVE' | 'REJECT' | null>(null)
  // On 1101-1240px screens the buyer column folds into the order cell so the remaining columns keep their room.
  const noBuyer = useMediaQuery('(min-width: 1101px) and (max-width: 1240px)')
  // A split card belongs only to the tab of its own group.
  const items = visibleReturnClaims(splitMixedReturnClaims(query.data?.items ?? [])).filter(item => !item.rowKey || appliedFilters.status === 'ALL' || returnGroup(item.status) === appliedFilters.status).filter(item => matchesReturnReferenceSearch(item, appliedFilters.search))
  const countItems = visibleReturnClaims(splitMixedReturnClaims(appliedFilters.status === 'ALL' ? query.data?.items ?? [] : countsQuery.data?.items ?? [])).filter(item => matchesReturnReferenceSearch(item, appliedFilters.search))
  const invoiceStatuses = [['FATURA_BEKLIYOR', 'Fatura Bekliyor'], ['FATURA_ISLENIYOR', 'Fatura İletiliyor'], ['FATURA_PLATFORMA_AKTARILMADI', 'Fatura İletilemedi'], ['FATURA_KONTROLDE', 'Faturası Mevcut'], ['FATURA_REDDEDILDI', 'Fatura Oluşturulamadı'], ['FATURA_IPTAL', 'Fatura İptal']] as const
  const reasonOptions = new Map<string, string>()
  for (const item of items) {
    const value = item.reasonCode?.trim() || item.reasonText?.trim()
    if (value) reasonOptions.set(value, returnReasonPresentation(item).label)
  }
  const reasons = Array.from(reasonOptions, ([value, label]) => ({ value, label }))
  const platformOptions = marketplacePlatformOptions(connections.data?.items ?? [], items.map(item => ({ platformCode: item.platformCode, displayName: item.platformDisplayName || returnPlatformName(item.platformCode) })))
  const cargoOptions = Array.from(new Set(items.map(item => item.cargoProviderName?.trim()).filter((value): value is string => Boolean(value)))).sort((left, right) => left.localeCompare(right, 'tr-TR'))
  const matchesLocalFilters = (item: ReturnClaim) => matchesReturnReferenceFilters(item, { platforms: selectedPlatforms, cargo: cargoFilter, reason: reasonFilter })
  const matchesInvoiceStatus = (item: ReturnClaim) => (invoiceStatusFilter === 'ALL' || item.invoiceStatus === invoiceStatusFilter) && matchesLocalFilters(item)
  const filteredCountItems = countItems.filter(matchesInvoiceStatus)
  const visible = items.filter(matchesInvoiceStatus)
  // Bulk approval is offered only for returns that wait for the seller; Hepsiburada's pre-approval review keeps its own step.
  const bulkEligible = (claim: ReturnClaim) => status === 'ACTION_REQUIRED' && returnGroup(claim.status) === 'ACTION_REQUIRED' && claim.rawStatus.replace(/[_-]/gu, '').toUpperCase() !== 'AWAITINGPREAPPROVAL'
  const bulkItems = visible.filter(bulkEligible)
  const bulkSelected = bulkItems.filter(claim => bulkIds.includes(claim.id))
  const allBulkSelected = bulkItems.length > 0 && bulkSelected.length === bulkItems.length
  useEffect(() => { setPageNumber(1) }, [appliedFilters, pageSize])
  const sortedVisible = status === 'APPROVED' ? sortApprovedReturns(visible, approvedSort) : visible
  const totalPages = Math.max(1, Math.ceil(sortedVisible.length / pageSize)); const currentPage = Math.min(pageNumber, totalPages); const visibleItems = sortedVisible.slice((currentPage - 1) * pageSize, currentPage * pageSize)
  const firstPage = Math.max(1, Math.min(currentPage - 1, totalPages - 3)); const pageList = Array.from({ length: Math.min(4, totalPages) }, (_, index) => firstPage + index)
  function applyFilters() { setAppliedFilters(returnFilters); setPageNumber(1) }
  function selectStatus(value: string) { const next = { ...returnFilters, status: value }; setStatus(value); setAppliedFilters(next); setSearchParams(current => { const params = new URLSearchParams(current); if (value === 'ALL') params.delete('status'); else params.set('status', value); return params }, { replace: true }); setPageNumber(1) }
  function selectInvoiceStatus(value: string) { setInvoiceStatusFilter(value); setPageNumber(1) }
  function clearFilters() { const next: ReturnFilters = { status: 'ALL', search: '', from: '', to: '' }; setStatus(next.status); setSearch(next.search); setFrom(next.from); setTo(next.to); setSelectedPlatforms([]); setCargoFilter('ALL'); setReasonFilter('ALL'); setInvoiceStatusFilter('ALL'); setAppliedFilters(next); setSearchParams(current => { const params = new URLSearchParams(current); params.delete('status'); return params }, { replace: true }); setPageNumber(1) }
  const tabCount = (value: string) => (appliedFilters.status === 'ALL' ? query.isLoading : countsQuery.isLoading) ? '…' : (appliedFilters.status === 'ALL' ? query.isError : countsQuery.isError) ? '—' : value === 'ALL' ? filteredCountItems.length : filteredCountItems.filter(item => returnGroup(item.status) === value).length
  const metric = (value: string) => { const count = tabCount(value); return typeof count === 'number' ? count.toLocaleString('tr-TR') : count }
  const platformTriggerLabel = selectedPlatforms.length === 0 || selectedPlatforms.includes(RETURN_NO_PLATFORM) ? 'Tüm Platformlar' : selectedPlatforms.length === 1 ? platformOptions.find(option => option.value === selectedPlatforms[0])?.label ?? selectedPlatforms[0] : `${selectedPlatforms.length} Platform Seçili`
  const cargoPopOptions = [{ value: 'ALL', label: 'Tüm Kargolar' }, ...cargoOptions.map(value => ({ value, label: cargoLabel(value) }))]
  const invoicePopOptions = [{ value: 'ALL', label: 'Tüm Fatura Durumları' }, ...invoiceStatuses.map(([value, label]) => ({ value, label }))]
  const reasonPopOptions = [{ value: 'ALL', label: 'Tüm Sebepler' }, ...reasons]
  const statusPopOptions = tabs.map(([value, label]) => ({ value, label }))
  const sortOptions: Array<[ApprovedReturnSort, string, 'down' | 'up']> = [['APPROVED_DESC', 'Onaylanma Tarihi', 'down'], ['APPROVED_ASC', 'Onaylanma Tarihi', 'up'], ['ORDERED_DESC', 'Sipariş Tarihi', 'down'], ['ORDERED_ASC', 'Sipariş Tarihi', 'up']]
  const currentSort = sortOptions.find(option => option[0] === approvedSort) ?? sortOptions[0]
  const sheetField = (label: string, current: string, options: Array<{ value: string; label: string }>, pick: (value: string) => void, right?: boolean) => <div className="rr-fld rr-sheet-only"><small>{label}</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle}><span>{options.find(option => option.value === current)?.label ?? options[0]?.label}</span><RrIcon name="chev" /></button>}>{close => <ReturnColumnPop label={label} options={options} value={current} right={right} onPick={value => { pick(value); close() }} />}</RrAnchor></div>
  const selectColumn = bulkItems.length > 0
  const tabLabel = tabs.find(tab => tab[0] === status)?.[1] ?? 'Tüm İadeler'
  return <section className="content f3 rr rr-returns">
    <div className="rr-ph"><div><div className="rr-crumb">Pazaryeri Operasyonu</div><h1>İadeler</h1><p>İade taleplerini inceleyin, onaylayın veya reddedin; kargo ve fatura durumunu izleyin.</p></div><div className="rr-acts"><button type="button" className="rr-btn" disabled={returnConnections.length === 0 || sync.isPending} onClick={() => sync.mutate()}><RrIcon name="refresh" />{sync.isPending ? 'Eşitleniyor…' : 'İade Senkronizasyonu'}</button></div></div>
    <div className="rr-metrics"><article className="rr-warn"><small>Aksiyon Bekleyen</small><strong>{metric('ACTION_REQUIRED')}</strong><span>satıcı kararı bekliyor</span></article><article><small>Kargoya Verilen</small><strong>{metric('SHIPPING')}</strong><span>müşteriden yola çıktı</span></article><article><small>Onaylanan</small><strong>{metric('APPROVED')}</strong><span>kabul kararı iletildi</span></article><article><small>Tüm İadeler</small><strong>{metric('ALL')}</strong><span>iade çalışma alanı</span></article></div>
    <div className="rr-fshell">
      <ViewTabsWithMore className="return-reference-tabs" more={['REJECTED', 'REVIEW', 'DISPUTED', 'SUSPENDED']} ariaLabel="İade durumları" value={status} onChange={selectStatus} items={tabs.map(([value, label]) => { const count = tabCount(value); return { value, label: viewTabLabel(({ ALL: 'all', REQUESTED: 'new', SHIPPING: 'shipping', ACTION_REQUIRED: 'waiting', APPROVED: 'done', REJECTED: 'cancelled', REVIEW: 'review', DISPUTED: 'disputed', SUSPENDED: 'paused' } as const)[value], label), count: typeof count === 'number' ? count.toLocaleString('tr-TR') : count, countTone: value === 'ACTION_REQUIRED' && typeof count === 'number' && count > 0 ? 'warn' as const : undefined } })} />
      <RrFilterCard active={[selectedPlatforms.length > 0, Boolean(from), cargoFilter !== 'ALL', invoiceStatusFilter !== 'ALL', reasonFilter !== 'ALL'].filter(Boolean).length}><div className={`rr-filters${status === 'APPROVED' ? ' rr-has-sort' : ''}`}>
        <label className="rr-fld rr-search"><small>İade Ara</small><span style={{ position: 'relative' }}><RrIcon name="search" style={{ position: 'absolute', left: 11, top: 10, color: 'var(--rr-subtle)' }} /><input className="rr-in" style={{ paddingLeft: 34 }} aria-label="İadelerde ara" placeholder="Müşteri, sipariş, iade kodu veya barkod ara" value={search} onChange={event => setSearch(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') applyFilters() }} /></span></label>
        <div className="rr-fld"><small>İade Platformu</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle} disabled={!platformOptions.length}><span>{platformTriggerLabel}</span><RrIcon name="chev" /></button>}>{() => <ReturnPlatformPop options={platformOptions} selected={selectedPlatforms} onChange={setSelectedPlatforms} />}</RrAnchor></div>
        <DateRangePicker className="rr-fld rr-date" from={from} to={to} onChange={range => { setFrom(range.from); setTo(range.to) }} label="İade Talep Tarihi" placeholder="Başlangıç – Bitiş" icon={<RrIcon name="cal" />} chevron={<RrIcon name="chev" />} />
        {status === 'APPROVED' && <div className="rr-fld"><small>Sıralama</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle}><span>{currentSort[1]} · {currentSort[2] === 'down' ? 'Yeniden Eskiye' : 'Eskiden Yeniye'}</span><RrIcon name={currentSort[2]} /><RrIcon name="chev" /></button>}>{close => <div className="rr-pop rr-right" role="listbox" style={{ minWidth: 250 }}>{sortOptions.map(([value, label, direction]) => { const on = value === approvedSort; return <button key={value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => { setApprovedSort(value); setPageNumber(1); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{label}</span><small>{direction === 'down' ? 'Yeniden Eskiye' : 'Eskiden Yeniye'}</small><RrIcon name={direction} /></button> })}</div>}</RrAnchor></div>}
        {sheetField('Kargo Firması', cargoFilter, cargoPopOptions, setCargoFilter)}
        {sheetField('Fatura Durumu', invoiceStatusFilter, invoicePopOptions, selectInvoiceStatus)}
        {sheetField('İade Sebebi', reasonFilter, reasonPopOptions, setReasonFilter)}
        {sheetField('İade Durumu', status, statusPopOptions, selectStatus)}
        <div className="rr-fbtns"><button type="button" className="rr-btn" onClick={clearFilters}>Temizle</button><button type="button" className="rr-btn rr-primary" onClick={applyFilters}>Filtrele</button></div>
      </div></RrFilterCard>
    </div>
    {notice && <NoticeToast key={`${noticeKind}:${notice}`} kind={noticeKind} message={notice} onClose={() => setNoticeState('')} durationMs={noticeKind === 'info' ? 7000 : 5500} />}
    <div className="rr-panel"><div className="rr-panel-h"><div><h2>{tabLabel}</h2><p>Toplam {visible.length.toLocaleString('tr-TR')} iade bilgisi</p></div><label className="rr-psize">Sayfa Başına<select className="rr-in" value={pageSize} onChange={event => setPageSize(Number(event.target.value))}>{[20, 50, 100, 200].map(size => <option key={size} value={size}>{size}</option>)}</select></label></div>
      <div className="rr-tblwrap">{query.isLoading ? <RrLoading /> : query.isError ? <RrError error={query.error} onRetry={() => void query.refetch()} /> : <table className={`rr-tbl${selectColumn ? ' rr-has-select' : ''}${noBuyer ? ' rr-no-buyer' : ''}`}>
        <colgroup><col className="rr-c-order" />{!noBuyer && <col className="rr-c-buyer" />}<col className="rr-c-prod" /><col className="rr-c-price" /><col className="rr-c-cargo" /><col className="rr-c-inv" /><col className="rr-c-reason" /><col className="rr-c-status" /><col className="rr-c-act" /></colgroup>
        <thead><tr>
          <ReturnHeadCell label="Sipariş Bilgileri" active={selectedPlatforms.length > 0} extra={selectColumn ? <input type="checkbox" className="rr-cb" checked={allBulkSelected} onChange={() => setBulkIds(allBulkSelected ? [] : bulkItems.map(claim => claim.id))} aria-label="Tüm iadeleri seç" style={{ marginRight: 8 }} /> : undefined} pop={() => <ReturnPlatformPop options={platformOptions} selected={selectedPlatforms} onChange={setSelectedPlatforms} />} />
          {!noBuyer && <ReturnHeadCell label="Alıcı" />}
          <ReturnHeadCell label="Bilgiler" />
          <ReturnHeadCell label="Birim Fiyat" />
          <ReturnHeadCell label="Kargo" active={cargoFilter !== 'ALL'} pop={close => <ReturnColumnPop label="Kargo Firması" options={cargoPopOptions} value={cargoFilter} onPick={value => { setCargoFilter(value); close() }} />} />
          <ReturnHeadCell label="Fatura" active={invoiceStatusFilter !== 'ALL'} pop={close => <ReturnColumnPop label="Fatura Durumu" options={invoicePopOptions} value={invoiceStatusFilter} onPick={value => { selectInvoiceStatus(value); close() }} />} />
          <ReturnHeadCell label="İade Sebebi" active={reasonFilter !== 'ALL'} pop={close => <ReturnColumnPop label="İade Sebebi" options={reasonPopOptions} value={reasonFilter} right onPick={value => { setReasonFilter(value); close() }} />} />
          <ReturnHeadCell label="Durum" active={status !== 'ALL'} pop={close => <ReturnColumnPop label="İade Durumu" options={statusPopOptions} value={status} right onPick={value => { selectStatus(value); close() }} />} />
          <ReturnHeadCell label="İşlemler" />
        </tr></thead>
        <tbody>{visibleItems.map(item => <ReturnReferenceRow key={item.rowKey ?? item.id} selectColumn={selectColumn} noBuyer={noBuyer} selection={bulkEligible(item) ? { checked: bulkIds.includes(item.id), onChange: () => setBulkIds(current => current.includes(item.id) ? current.filter(id => id !== item.id) : [...current, item.id]) } : undefined} item={item} order={null} onNotice={setNotice} />)}{!visible.length && <tr><td className="rr-empty-row" colSpan={noBuyer ? 8 : 9}><div className="rr-empty"><span className="rr-ei"><RrIcon name="box" /></span><b>{query.data?.items.length ? 'İade Bulunamadı' : 'Henüz İade Yok'}</b><span className="rr-lead">{query.data?.items.length ? 'Seçili filtreyle eşleşen gösterilebilir iade yok.' : 'İadeleri eşitle düğmesiyle güncel talepleri çekin.'}</span>{query.data?.items.length ? <button type="button" className="rr-btn rr-quiet" style={{ marginTop: 4 }} onClick={clearFilters}>Filtreleri Temizle</button> : null}</div></td></tr>}</tbody>
      </table>}</div>
      {!query.isLoading && !query.isError && visible.length > 0 && <div className="rr-pager"><span>Toplam <b style={{ color: 'var(--rr-ink)' }}>{visible.length.toLocaleString('tr-TR')}</b> adet</span><div className="rr-pages"><button type="button" aria-label="Önceki" disabled={currentPage <= 1} onClick={() => setPageNumber(Math.max(1, currentPage - 1))}><RrIcon name="left" /></button>{pageList.map(page => <button key={page} type="button" className={page === currentPage ? 'rr-on' : undefined} aria-current={page === currentPage ? 'page' : undefined} onClick={() => setPageNumber(page)}>{page}</button>)}<button type="button" aria-label="Sonraki" disabled={currentPage >= totalPages} onClick={() => setPageNumber(Math.min(totalPages, currentPage + 1))}><RrIcon name="right" /></button></div><label className="rr-jump">Sayfaya Git<input className="rr-in" type="number" min={1} max={totalPages} placeholder={`1–${totalPages}`} aria-label="Sayfa numarası" value={jumpPage} onChange={event => setJumpPage(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') { const page = Number(jumpPage); if (page >= 1) { setPageNumber(Math.min(totalPages, Math.floor(page))); setJumpPage('') } } }} /><button type="button" className="rr-btn" onClick={() => { const page = Number(jumpPage); if (page >= 1) { setPageNumber(Math.min(totalPages, Math.floor(page))); setJumpPage('') } }}>Git</button></label></div>}
    </div>
    {bulkSelected.length > 0 && <div className="rr-bulk" role="group" aria-label="Toplu işlemler"><span className="rr-grow">{bulkSelected.length} iade seçildi</span><button type="button" className="rr-btn rr-quiet" onClick={() => setBulkIds([])}>Vazgeç</button><button type="button" className="rr-btn rr-danger" onClick={() => setBulkMode('REJECT')}><RrIcon name="close" />Reddet</button><button type="button" className="rr-btn rr-primary" onClick={() => setBulkMode('APPROVE')}><RrIcon name="check" />Onayla</button></div>}
    {bulkMode && <ReturnBulkDecisionDialog mode={bulkMode} items={bulkSelected.map((claim): BulkDecisionItem => ({ id: claim.id, version: claim.version, orderNumber: displayOrderNumber(claim.orderNumber), customerName: claim.customerName, orderedAt: claim.orderedAt, packageNumber: claim.packageNumber, connectionId: claim.connectionId, platformCode: claim.platformCode, platformName: claim.platformDisplayName || returnPlatformName(claim.platformCode), lines: (claim.lines ?? []).map(line => ({ id: line.id, title: line.title, imageUrl: line.imageUrl ?? claim.primaryImageUrl, quantity: productLineQuantity(line), sku: line.sku, barcode: formatMarketplaceBarcode(claim.platformCode, line.barcode) ?? line.barcode, color: returnLineFieldValue(line, ['renk', 'color'], 'color') ?? null, size: returnLineFieldValue(line, ['beden', 'size'], 'size') ?? null, modelCode: line.modelCode ?? null })) }))} onClose={() => { setBulkMode(null); setBulkIds([]) }} onDone={(message, kind, details, target) => notifyReturn(handleNotice, message, kind, details, target)} />}
  </section>
}

// The separate detail route (/returns/:id), as the proposal's detail page.
export function ReturnDetailPage() {
  const { id = '' } = useParams(); const client = useQueryClient(); const [notice, setNoticeState] = useState(''); const [noticeKind, setNoticeKind] = useState<ReturnNoticeKind>('info')
  function setNotice(message: string, kind: ReturnNoticeKind = 'info') { setNoticeState(message); setNoticeKind(kind) }
  useEffect(() => {
    if (!notice) return
    const timeout = window.setTimeout(() => setNoticeState(''), noticeKind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice, noticeKind])
  const query = useQuery({ queryKey: ['return', id], queryFn: () => hubApi<ReturnDetail>(`/returns/${id}`) })
  async function disposition(event: FormEvent<HTMLFormElement>, line: ReturnLine) { event.preventDefault(); const data = new FormData(event.currentTarget); try { await hubApi(`/returns/${id}/stock-dispositions`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify({ returnLineId: line.id, disposition: data.get('disposition'), quantity: Number(data.get('quantity')), reason: data.get('reason') }) }); notifyReturn(setNotice, 'İade stok kararı kaydedildi. Satılabilir seçimi stoğu artırır.', 'success'); await client.invalidateQueries({ queryKey: ['return', id] }) } catch (reason) { notifyReturn(setNotice, reason instanceof Error ? reason.message : 'Stok kararı kaydedilemedi.', 'error') } }
  if (query.isLoading) return <section className="content f3 rr"><RrLoading /></section>; if (query.isError || !query.data) return <section className="content f3 rr"><RrError error={query.error} onRetry={() => void query.refetch()} /></section>; const item = query.data; const lines = item.lines ?? []; const marketplaceName = returnPlatformName(item.platformCode)
  const reason = returnReasonPresentation(item)
  const critical = Boolean(item.actionDueAt && new Date(item.actionDueAt).getTime() - Date.now() < 86_400_000)
  return <section className="content f3 rr"><div className="rr-dpage">
    <div className="rr-ph"><div><Link to="/returns" className="rr-btn rr-link"><RrIcon name="left" /> İadeler</Link><div className="rr-crumb" style={{ marginTop: 6 }}>İade Detayı · Claim {item.externalClaimId}</div><h1>{displayOrderNumber(item.orderNumber)}</h1><p>{item.customerName} · {item.orderAmount.toLocaleString('tr-TR', { style: 'currency', currency: item.currency })}</p></div><div className="rr-acts"><RrBadge label={titleTr(statusLabel(item.status))} /></div></div>
    {notice && <NoticeToast key={`${noticeKind}:${notice}`} kind={noticeKind} message={notice} onClose={() => setNoticeState('')} durationMs={noticeKind === 'info' ? 7000 : 5500} />}
    <div className="rr-two"><article><div className="rr-h3">Talep ve Süre</div><dl className="rr-kv"><dt>Platform Durumu</dt><dd>{titleTr(statusLabel(item.rawStatus))}</dd><dt>Neden</dt><dd>{reason.label}{reason.explanation && <> · {reason.explanation}</>}</dd><dt>Sipariş Tarihi</dt><dd className="rr-num">{returnDateText(item.orderedAt)}</dd><dt>Otomatik İşlem</dt><dd style={critical ? { color: 'var(--rr-warn)' } : undefined}>{returnRemaining(item.actionDueAt)}</dd><dt>Kargo</dt><dd className="rr-num">{cargoLabel(item.cargoProviderName)} · {item.cargoTrackingNumber ?? '—'}</dd></dl></article>
      <article><div className="rr-h3">{marketplaceName} İade Kararı</div>{item.decisionPending ? <ReturnInlineActions item={item} onNotice={setNotice} /> : <>{item.allowedActions.includes('RECEIVE') && <ReturnReceiptButton item={item} onNotice={setNotice} />}{item.allowedActions.includes('APPROVE') || item.allowedActions.includes('REJECT') ? <ReturnDecisionActions item={item} onNotice={setNotice} /> : !item.allowedActions.includes('RECEIVE') && <div className="rr-callout"><RrIcon name="info" /><span><b>İade Aksiyonu Uygun Değil.</b> Sağlayıcının mevcut iade durumu bu kayıtta henüz onay veya ret işlemini desteklemiyor.</span></div>}</>}</article></div>
    {/* Stock intake is decided while a return is being approved; a return that is already approved or rejected does not ask again. */}
    {!['APPROVED', 'REJECTED'].includes(returnGroup(item.status)) && <>
    <article><div className="rr-h3">İade Paketi Ürünleri ve Stok Kabulü</div><div className="rr-callout rr-warn"><RrIcon name="alert" /><span><b>Stok otomatik artmaz.</b> Ürün fiziksel olarak kontrol edildikten sonra “Satılabilir” seçilirse stok artar; Karantina, Hasarlı ve Teslim Alınmadı seçimleri stoğu artırmaz.</span></div>
      {!lines.length ? <div className="rr-empty"><span className="rr-ei"><RrIcon name="box" /></span><b>İade Satırı Bulunamadı</b></div> : lines.map(line => <div className="rr-disp" key={line.id}><RrThumb src={line.imageUrl ?? item.primaryImageUrl} quantity={line.quantity} large alt={line.title} /><span className="rr-ptext"><b>{line.title}</b><small>{line.sku}{line.barcode ? ` · ${line.barcode}` : ''}</small><small>{line.quantity} adet · kalan karar {line.remainingQuantity}</small></span>{item.stockDispositionAvailable && line.remainingQuantity > 0 ? <form onSubmit={event => disposition(event, line)}><select className="rr-in" name="disposition" aria-label="Stok kabul kararı"><option value="PASS">Satılabilir — Stoğa Ekle</option><option value="QUARANTINE">Karantina — Stoğa Ekleme</option><option value="DAMAGED">Hasarlı — Stoğa Ekleme</option><option value="NOT_RECEIVED">Teslim Alınmadı</option></select><input className="rr-in rr-num" name="quantity" type="number" min="0.01" max={line.remainingQuantity} step="0.01" defaultValue={line.remainingQuantity} required aria-label="Adet" /><input className="rr-in" name="reason" placeholder="Kontrol notu" required aria-label="Kontrol notu" /><button type="submit" className="rr-btn rr-primary">Kararı Kaydet</button></form> : <div style={{ gridColumn: 2, display: 'flex', flexWrap: 'wrap', gap: 8 }}><RrBadge label={line.remainingQuantity === 0 ? 'Tamamlandı' : line.hasInventoryMapping ? 'Onay Bekliyor' : 'Stok Eşlemesi Gerekli'} /></div>}</div>)}
    </article>
    </>}
  </div></section>
}

export function MappingPage() {
  const [searchParams] = useSearchParams()
  useEffect(() => {
    const main = document.querySelector<HTMLElement>('.app-shell.stitch-shell > main')
    if (main) main.scrollTop = 0
  }, [])
  return searchParams.get('view') === 'brands' ? <BrandMappingPage /> : <CategoryMappingWorkspace />
}

const supportedAttributeMappingPlatforms = mappingPlatformDefinitions.filter(platform => platform.code === 'TRENDYOL' || platform.code === 'HEPSIBURADA' || platform.code === 'N11' || platform.code === 'PAZARAMA')

function CategoryMappingWorkspace() {
  const client = useQueryClient()
  const [connectionId, setConnectionId] = useState(''); const [selectedPlatformCode, setSelectedPlatformCode] = useState<MappingPlatformCode>('TRENDYOL'); const [localId, setLocalId] = useState(''); const [externalId, setExternalId] = useState(''); const [notice, setNotice] = useState(''); const [categoryName, setCategoryName] = useState(''); const [categoryLibrarySearch, setCategoryLibrarySearch] = useState(''); const [categoryLibraryOpen, setCategoryLibraryOpen] = useState(false); const [categoryLibrarySort, setCategoryLibrarySort] = useState<'NAME_ASC' | 'NAME_DESC'>('NAME_ASC'); const [externalSearch, setExternalSearch] = useState(''); const [externalPickerOpen, setExternalPickerOpen] = useState(false); const [savedSearch, setSavedSearch] = useState(''); const [savedSort, setSavedSort] = useState<'NAME_ASC' | 'NAME_DESC'>('NAME_ASC'); const [savedPage, setSavedPage] = useState(1); const [advancedOpen, setAdvancedOpen] = useState(false); const [panelPickerOpen, setPanelPickerOpen] = useState(false); const [panelPickerSearch, setPanelPickerSearch] = useState(''); const [exportOpen, setExportOpen] = useState(false); const [exportSelection, setExportSelection] = useState<Record<MappingTransferScope, boolean>>({ categories: true, options: true, attributes: true, mappings: true }); const [transferOpen, setTransferOpen] = useState(false); const [transferBundle, setTransferBundle] = useState<MappingTransferBundle | null>(null); const [transferSelection, setTransferSelection] = useState<Record<MappingTransferScope, boolean>>({ categories: true, options: true, attributes: true, mappings: true }); const [transferBusy, setTransferBusy] = useState(false)
  const autoSelectedConnectionPlatform = useRef(false)
  useScrollLock(exportOpen || transferOpen || advancedOpen || panelPickerOpen, true)
  useEffect(() => {
    if (!notice) return
    const kind = notificationKindFromMessage(notice)
    appendNotification(notice, kind)
    document.documentElement.dataset.rvMappingFeedback = kind
    const timeout = window.setTimeout(() => setNotice(''), kind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice])
  useEffect(() => {
    if (!advancedOpen) return
    const closeOnEscape = (event: globalThis.KeyboardEvent) => {
      if (event.key !== 'Escape') return
      event.preventDefault()
      setAdvancedOpen(false)
    }
    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [advancedOpen])
  const connections = useQuery({ queryKey: ['connections', 'mapping'], queryFn: () => loadAllPages<Connection>('/connections') })
  useEffect(() => {
    if (!connections.data || autoSelectedConnectionPlatform.current) return
    const availablePlatform = mappingPlatformDefinitions.find(platform => activeMappingConnectionsForPlatform(connections.data.items, platform.code).length > 0)
    if (!availablePlatform) return
    autoSelectedConnectionPlatform.current = true
    setSelectedPlatformCode(current => activeMappingConnectionsForPlatform(connections.data!.items, current).length ? current : availablePlatform.code)
  }, [connections.data])
  const localCategories = useQuery({ queryKey: ['categories', 'mapping'], queryFn: () => loadAllPages<LocalCategory>('/catalog/categories') })
  const localAttributes = useQuery({ queryKey: ['attributes', 'mapping-builder'], queryFn: () => loadAllPages<LocalAttribute>('/catalog/attributes') })
  const references = useQuery({ queryKey: ['reference-categories', connectionId], queryFn: () => hubApi<ReferenceData>(`/reference-data/categories?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId, retry: false })
  const mapping = useQuery({ queryKey: ['category-mapping', localId, connectionId], queryFn: () => hubApi<CatalogMapping | null>(`/mappings/categories/${localId}?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!localId && !!connectionId, retry: false })
  const categoryMappings = useQuery({ queryKey: ['category-mappings', connectionId], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/categories?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId, retry: false })
  const syncCategories = useMutation({ mutationFn: () => {
    if (!connectionId) throw new Error(`Aktif ${mappingPlatformLabel(selectedPlatformCode)} bağlantısı bulunamadı.`)
    return hubApi(`/connections/${connectionId}/reference-sync-jobs?resourceType=CATEGORIES`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: '{}' })
  }, onSuccess: async () => {
    setNotice(`${mappingPlatformLabel(selectedPlatformCode)} kategori listesi salt-okunur eşitleme kuyruğuna alındı.`)
    for (const delay of [500, 1000, 2000, 4000]) {
      await wait(delay)
      await client.invalidateQueries({ queryKey: ['reference-categories', connectionId] })
    }
  }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Kategori eşitleme başlatılamadı.') })
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
      const pickerWidth = Math.round(rect.width)
      const gap = 6
      const viewportPadding = 16
      const spaceBelow = Math.max(0, window.innerHeight - rect.bottom - gap - viewportPadding)
      const spaceAbove = Math.max(0, rect.top - gap - viewportPadding)
      const naturalHeight = Math.min(420, Math.max(0, picker.scrollHeight))
      const opensUp = naturalHeight > spaceBelow && spaceAbove > spaceBelow
      const availableHeight = Math.max(120, Math.min(420, opensUp ? spaceAbove : spaceBelow))
      const pickerHeight = Math.min(naturalHeight || availableHeight, availableHeight)
      const top = opensUp ? rect.top - pickerHeight - gap : Math.min(rect.bottom + gap, window.innerHeight - pickerHeight - viewportPadding)
      const left = Math.min(Math.max(16, rect.left), Math.max(16, window.innerWidth - pickerWidth - 16))
      picker.style.setProperty('position', 'fixed')
      picker.style.setProperty('top', `${Math.round(top)}px`)
      picker.style.setProperty('left', `${Math.round(left)}px`)
      picker.style.setProperty('width', `${pickerWidth}px`)
      picker.style.setProperty('max-height', `${Math.round(availableHeight)}px`)
    }
    positionPicker()
    document.querySelector<HTMLInputElement>('.mapping-category-builder .mapping-reference-local > .panel-category-picker input')?.focus({ preventScroll: true })
    window.addEventListener('resize', positionPicker)
    window.addEventListener('scroll', positionPicker, true)
    return () => { window.removeEventListener('resize', positionPicker); window.removeEventListener('scroll', positionPicker, true) }
  }, [panelPickerOpen])
  useEffect(() => {
    if (!panelPickerOpen) return
    const closeOnOutsidePointer = (event: globalThis.PointerEvent) => {
      const target = event.target
      if (!(target instanceof Node)) return
      const trigger = document.querySelector<HTMLElement>('.mapping-category-builder .mapping-reference-local .panel-category-picker-trigger')
      const picker = document.querySelector<HTMLElement>('.mapping-category-builder .mapping-reference-local > .panel-category-picker')
      if (!trigger?.contains(target) && !picker?.contains(target)) setPanelPickerOpen(false)
    }
    const closeOnEscape = (event: globalThis.KeyboardEvent) => { if (event.key === 'Escape') setPanelPickerOpen(false) }
    document.addEventListener('pointerdown', closeOnOutsidePointer)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeOnOutsidePointer)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [panelPickerOpen])
  useEffect(() => {
    if (!externalPickerOpen) return
    const closeOnOutsidePointer = (event: globalThis.PointerEvent) => {
      const target = event.target
      if (!(target instanceof Node)) return
      const trigger = document.querySelector<HTMLElement>('.mapping-category-builder .mapping-reference-target .panel-category-picker-trigger')
      const picker = document.querySelector<HTMLElement>('.mapping-category-builder .mapping-reference-target .panel-category-picker')
      if (!trigger?.contains(target) && !picker?.contains(target)) setExternalPickerOpen(false)
    }
    const closeOnEscape = (event: globalThis.KeyboardEvent) => { if (event.key === 'Escape') setExternalPickerOpen(false) }
    document.addEventListener('pointerdown', closeOnOutsidePointer)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeOnOutsidePointer)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [externalPickerOpen])
  const activeMappingConnections = connections.data?.items.filter(item => mappingPlatformDefinitions.some(platform => platform.code === item.platformCode.toUpperCase()) && ['ACTIVE', 'VERIFIED'].includes(item.status.toUpperCase())) ?? []
  // Platforms without an active connection are hidden from the tabs; until any connection is known every tab stays.
  const shownMappingPlatforms = mappingPlatformDefinitions.filter(platform => !activeMappingConnections.length || activeMappingConnections.some(item => item.platformCode.toUpperCase() === platform.code))
  const selectedPlatform = mappingPlatformDefinitions.find(platform => platform.code === selectedPlatformCode) ?? mappingPlatformDefinitions[0]
  const selectedPlatformConnections = activeMappingConnectionsForPlatform(activeMappingConnections, selectedPlatform.code)
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
  const externalLeaves = useMemo(() => references.data?.items.filter(item => item.isActive && item.isLeaf) ?? [], [references.data])
  const archiveCategory = useMutation({ mutationFn: (category: LocalCategory) => hubApi<LocalCategory>(`/catalog/categories/${category.id}`, { method: 'PATCH', headers: { 'If-Match': `"v${category.version}"` }, body: JSON.stringify({ name: category.name, parentId: null, isActive: false }) }), onSuccess: async (_, category) => { if (localId === category.id) { setLocalId(''); setExternalId('') }; await client.invalidateQueries({ queryKey: ['categories', 'mapping'] }); setNotice(`“${category.name}” panel kategorilerinden kaldırıldı.`) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Kategori kaldırılamadı.') })
  const removeMapping = useMutation({ mutationFn: (item: CatalogMapping) => hubApi<boolean>(`/mappings/categories/${item.localId}?connectionId=${encodeURIComponent(connectionId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${item.version}"` } }), onSuccess: async (_, item) => { if (localId === item.localId) { setExternalId(''); client.setQueryData(['category-mapping', localId, connectionId], null) }; setNotice('Kategori eşleştirmesi kaldırıldı.'); await client.invalidateQueries({ queryKey: ['category-mappings', connectionId] }) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Eşleştirme kaldırılamadı.') })
  const selectedExternal = externalLeaves.find(item => item.externalId === externalId)
  const selectedLocal = localLeaves.find(item => item.id === localId)
  // Up to three likely marketplace categories for a panel category that has no mapping yet; a click only selects, Save still decides.
  const categorySuggestions = useMemo(() => selectedLocal && !mapping.data && !mapping.isLoading ? suggestCategoryMappings(selectedLocal, externalLeaves) : [], [selectedLocal, mapping.data, mapping.isLoading, externalLeaves])
  const normalizedExternalSearch = externalSearch.trim().toLocaleLowerCase('tr-TR')
  const matchingExternal = externalLeaves.filter(item => !normalizedExternalSearch || `${item.path} ${item.name} ${item.externalId}`.toLocaleLowerCase('tr-TR').includes(normalizedExternalSearch))
  const visibleExternal = [...(selectedExternal ? [selectedExternal] : []), ...matchingExternal.filter(item => item.externalId !== selectedExternal?.externalId)].slice(0, 20)
  const savedMappings = categoryMappings.data ?? []
  const savedRows = savedMappings.map(item => ({ item, local: localLeaves.find(category => category.id === item.localId), external: externalLeaves.find(category => category.externalId === item.externalId) })).filter(row => !savedSearch.trim() || `${row.local?.path ?? row.item.localId} ${row.external?.path ?? row.item.externalId}`.toLocaleLowerCase('tr-TR').includes(savedSearch.trim().toLocaleLowerCase('tr-TR')))
  const sortedSavedRows = [...savedRows].sort((left, right) => {
    const leftName = (left.local?.path ?? left.item.localId).toLocaleLowerCase('tr-TR')
    const rightName = (right.local?.path ?? right.item.localId).toLocaleLowerCase('tr-TR')
    return savedSort === 'NAME_DESC' ? rightName.localeCompare(leftName, 'tr') : leftName.localeCompare(rightName, 'tr')
  })
  const savedPageSize = 20
  const savedPageCount = Math.max(1, Math.ceil(sortedSavedRows.length / savedPageSize))
  const currentSavedPage = Math.min(savedPage, savedPageCount)
  const visibleSavedRows = sortedSavedRows.slice((currentSavedPage - 1) * savedPageSize, currentSavedPage * savedPageSize)
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
          await hubApi(`/mappings/attributes/${targetLocalId}`, { method: 'PUT', body: JSON.stringify({ connectionId, snapshotId: snapshot.snapshotId, scopeExternalId: categoryScope, externalId: mappingItem.externalId, status: 'VERIFIED', role: isOptionAttribute(transferBundle.attributes.find(item => item.id === mappingItem.localId) ?? { code: '', name: '', roles: [] }) ? 'OPTION' : 'ATTRIBUTE' }) })
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
  const localCategoryManagerSection = (
    <article className="mapping-reference-card mapping-local-category-builder">
      <div className="mapping-section-heading"><div><h2><UiIcon name="addCategory" />1. Yeni Kategori Ekle</h2><p>Panel kategorilerini ekleyin, arayın veya kaldırın.</p></div></div>
      <div className="mapping-local-manager-dialog mapping-local-manager-dialog-standalone">
        <form className="platform-category-create" onSubmit={event => { event.preventDefault(); createCategory.mutate() }}><label>Yeni Panel Kategorisi<input aria-label="Yeni panel kategorisi adı" maxLength={160} value={categoryName} onChange={event => setCategoryName(event.target.value)} placeholder="Örn. Anne Bluz" required /></label><button disabled={createCategory.isPending}>{createCategory.isPending ? 'Ekleniyor…' : 'Kategori Ekle'}</button></form>
        <div className="category-library-tools"><div className={categoryLibraryOpen ? 'category-library-search-control is-open' : 'category-library-search-control'}><input aria-label="Eklenen panel kategorilerinde ara" value={categoryLibrarySearch} onFocus={() => setCategoryLibraryOpen(true)} onClick={() => setCategoryLibraryOpen(true)} onChange={event => setCategoryLibrarySearch(event.target.value)} placeholder="Kategori ara…" /><button type="button" className="category-library-search-toggle" aria-label={categoryLibraryOpen ? 'Kategorileri kapat' : 'Kategorileri aç'} aria-expanded={categoryLibraryOpen} onMouseDown={event => event.preventDefault()} onClick={() => setCategoryLibraryOpen(value => !value)}><span aria-hidden="true" /></button></div><select aria-label="Panel kategorilerini sırala" value={categoryLibrarySort} onChange={event => setCategoryLibrarySort(event.target.value as 'NAME_ASC' | 'NAME_DESC')}><option value="NAME_ASC">A–Z</option><option value="NAME_DESC">Z–A</option></select><span>{filteredCategoryLibrary.length.toLocaleString('tr-TR')} sonuç</span></div>
        <div className="category-library-list">{filteredCategoryLibrary.slice(0, 100).map(category => <span key={category.id} className={category.id === localId ? 'active' : ''}><button type="button" onClick={() => { setLocalId(category.id); setExternalId('') }}>{category.name}</button><button type="button" className="category-library-remove" aria-label="Kategoriyi kaldır" onClick={() => archiveCategory.mutate(category)} disabled={archiveCategory.isPending}><UiIcon name="close" /></button></span>)}</div>
      </div>
    </article>
  )
  function selectMappingConnection(nextConnectionId: string) {
    setConnectionId(nextConnectionId)
    setLocalId('')
    setExternalId('')
    setExternalSearch('')
    setSavedPage(1)
    setAdvancedOpen(false)
    setExternalPickerOpen(false)
    setPanelPickerOpen(false)
    setNotice('')
  }
  function selectMappingPlatform(code: MappingPlatformCode) {
    setSelectedPlatformCode(code)
    setConnectionId(activeMappingConnectionsForPlatform(activeMappingConnections, code)[0]?.id ?? '')
    setLocalId('')
    setExternalId('')
    setExternalSearch('')
    setExternalPickerOpen(false)
    setPanelPickerOpen(false)
    setNotice('')
  }
  const mappingPlatformTabs = (
    <>
      <div className="mapping-category-platform-tabs mapping-platform-tabs rv-tabs rv-view-tabs" role="tablist" aria-label="Kategori eşleştirme pazaryeri">
        {shownMappingPlatforms.map(platform => {
          const available = activeMappingConnections.some(item => item.platformCode.toUpperCase() === platform.code)
          return <button type="button" role="tab" aria-selected={selectedPlatformCode === platform.code} className={`${selectedPlatformCode === platform.code ? 'active is-active' : ''}${available ? '' : ' is-unavailable'}`} title={available ? `${platform.label} kategorilerini göster` : `${platform.label} için aktif bağlantı yok`} onClick={() => selectMappingPlatform(platform.code)}><span className="rv-tab-label">{platformLogoSource(platform.code) && <img className="rv-view-tab-logo" src={platformLogoSource(platform.code)!} alt="" aria-hidden="true" />}{platform.label}</span></button>
        })}
      </div>
      {!connectionId ? <div className="unknown mapping-connection-empty" role="status"><strong>{selectedPlatform.label} bağlantısı gerekli</strong><p>Kategori referanslarını alıp eşleme yapabilmek için önce aktif bir {selectedPlatform.label} bağlantısı kurun.</p><Link className="button-link" to="/integrations">Entegrasyonlara git</Link></div> : <div className="mapping-reference-refresh-row">{selectedPlatformConnections.length > 1 && <label className="mapping-reference-connection-select">Bağlantı<select aria-label="Mağaza bağlantısı" value={connectionId} onChange={event => selectMappingConnection(event.target.value)}>{selectedPlatformConnections.map(item => <option value={item.id} key={item.id}>{item.displayName} · {item.environment === 'PRODUCTION' ? 'Canlı' : 'Stage'} · {item.externalStoreId}</option>)}</select></label>}</div>}
    </>
  )
  return <section className="content f3 mapping-page mapping-workspace stitch-reference-mapping"><header className="mapping-reference-heading"><div><h1>Eşleştirme Ayarları</h1></div><div className="mapping-header-tools"><div className="mapping-reference-heading-actions" aria-label="Eşleştirme yedeği işlemleri"><div className="mapping-transfer-actions"><MobileActionMenu><button type="button" className="secondary" title="Dışa aktarılacak alanları seç" onClick={() => { setExportSelection({ categories: true, options: true, attributes: true, mappings: true }); setExportOpen(true) }} disabled={transferBusy || !connectionId}><UiIcon name="download" size={18} />{transferBusy ? 'Hazırlanıyor…' : 'Dışa Aktar'}</button><label className="mapping-transfer-upload" title="JSON eşleştirme yedeğini içe aktar"><UiIcon name="import" size={18} />İçe Aktar<input type="file" accept="application/json,.json" onChange={readMappingBundle} disabled={transferBusy} /></label></MobileActionMenu></div></div></div></header><MappingViewTabs active="category" />{notice && <MessageToast message={notice} onClose={() => setNotice('')} />}{localCategoryManagerSection}
     <article className="mapping-reference-card mapping-category-builder"><header className="mapping-builder-head"><h2><UiIcon name="link" />2. Yeni Kategori Eşleştirme</h2>{connectionId && <button type="button" className="secondary" disabled={syncCategories.isPending} onClick={() => syncCategories.mutate()}><UiIcon name="refresh" />{syncCategories.isPending ? 'Kuyruğa alınıyor…' : 'Platformdan Veri Güncelle'}</button>}</header>{mappingPlatformTabs}<div className="mapping-reference-grid"><div className="mapping-link-rail" aria-hidden="true"><UiIcon name="link" /><span>Eşleştir</span></div><section className="mapping-reference-local"><label>Panel Kategorisi<button type="button" className="panel-category-picker-trigger" aria-expanded={panelPickerOpen} onClick={() => setPanelPickerOpen(value => !value)} disabled={!connectionId}><span>{localLeaves.find(item => item.id === localId)?.path ?? 'Panel kategorisi seçin...'}</span><UiIcon name="chevronDown" /></button></label>{panelPickerOpen && <div className="panel-category-picker" role="dialog" aria-label="Panel kategorisi seçimi"><div><input aria-label="Panel kategorilerinde ara" value={panelPickerSearch} onChange={event => setPanelPickerSearch(event.target.value)} placeholder="Panel kategorilerinde ara..." /></div><div role="listbox">{panelPickerItems.length ? panelPickerItems.map(item => <button type="button" role="option" aria-selected={item.id === localId} key={item.id} onClick={() => { setLocalId(item.id); setExternalId(''); setExternalSearch(''); setPanelPickerOpen(false); setNotice('') }}>{item.path}</button>) : <span>Aramaya uygun panel kategorisi bulunamadı.</span>}</div></div>}<details className="mapping-local-manager" onClick={event => { if (event.target === event.currentTarget) event.currentTarget.removeAttribute('open') }}><summary><UiIcon name="settings" /><span><strong>Lokal Kategorileri Yönet</strong><small>Kategori ekleyin, arayın veya arşivleyin.</small></span><UiIcon name="chevronDown" /></summary><div className="mapping-local-manager-dialog" onClick={event => event.stopPropagation()}><header><div><strong>1. Yeni Kategori Ekle</strong><small>Panel kategorileri ekleyin, arayın veya kaldırın.</small></div><button type="button" aria-label="Kategori yönetim penceresini kapat" onClick={event => { event.preventDefault(); event.stopPropagation(); event.currentTarget.closest('details')?.removeAttribute('open') }}><UiIcon name="close" /></button></header><form className="platform-category-create" onSubmit={event => { event.preventDefault(); createCategory.mutate() }}><label>Yeni Panel Kategorisi<input aria-label="Yeni panel kategorisi adı" maxLength={160} value={categoryName} onChange={event => setCategoryName(event.target.value)} placeholder="Örn. Anne Bluz" required /></label><button disabled={createCategory.isPending}>{createCategory.isPending ? 'Ekleniyor…' : 'Kategori Ekle'}</button></form><div className="category-library-tools"><div className={categoryLibraryOpen ? 'category-library-search-control is-open' : 'category-library-search-control'}><input aria-label="Eklenen panel kategorilerinde ara" value={categoryLibrarySearch} onFocus={() => setCategoryLibraryOpen(true)} onClick={() => setCategoryLibraryOpen(true)} onChange={event => setCategoryLibrarySearch(event.target.value)} placeholder="Kategori ara…" /><button type="button" className="category-library-search-toggle" aria-label={categoryLibraryOpen ? 'Kategorileri kapat' : 'Kategorileri aç'} aria-expanded={categoryLibraryOpen} onMouseDown={event => event.preventDefault()} onClick={() => setCategoryLibraryOpen(value => !value)}><span aria-hidden="true" /></button></div><select aria-label="Panel kategorilerini sırala" value={categoryLibrarySort} onChange={event => setCategoryLibrarySort(event.target.value as 'NAME_ASC' | 'NAME_DESC')}><option value="NAME_ASC">A–Z</option><option value="NAME_DESC">Z–A</option></select><span>{filteredCategoryLibrary.length.toLocaleString('tr-TR')} sonuç</span></div><div className="category-chip-list">{filteredCategoryLibrary.slice(0, 100).map(category => <span key={category.id} className={category.id === localId ? 'active' : ''}><button type="button" onClick={() => { setLocalId(category.id); setExternalId('') }}>{category.name}</button><button type="button" className="category-chip-remove" aria-label="Kategoriyi kaldır" onClick={() => archiveCategory.mutate(category)} disabled={archiveCategory.isPending}><UiIcon name="close" /></button></span>)}</div></div></details></section><section className="mapping-reference-target"><label>{selectedPlatform.label} Kategorisi</label><div className="mapping-target-controls"><div className="platform-category-picker-wrap"><button type="button" className="panel-category-picker-trigger" aria-expanded={externalPickerOpen} onClick={() => setExternalPickerOpen(value => !value)} disabled={!connectionId || !localId || references.isError || references.isLoading}><span title={selectedExternal ? cleanMarketplaceCategoryPath(selectedExternal.path) : undefined}>{selectedExternal ? cleanMarketplaceCategoryPath(selectedExternal.path) : `${selectedPlatform.label} kategorisi seçin...`}</span><UiIcon name="chevronDown" /></button>{externalPickerOpen && <div className="panel-category-picker platform-category-picker" role="dialog" aria-label={`${selectedPlatform.label} kategorisi seçimi`}><div><input autoFocus aria-label={`${selectedPlatform.label} kategorilerinde ara`} value={externalSearch} onChange={event => setExternalSearch(event.target.value)} placeholder={references.isLoading ? 'Kategoriler yükleniyor...' : `${selectedPlatform.label} kategorilerinde ara...`} /></div><div role="listbox">{visibleExternal.length ? visibleExternal.map(item => { const path = cleanMarketplaceCategoryPath(item.path); const ambiguous = path === item.name && externalLeaves.filter(category => cleanMarketplaceCategoryPath(category.path) === path).length > 1; return <button type="button" role="option" aria-selected={item.externalId === externalId} key={item.externalId} onClick={() => { setExternalId(item.externalId); setExternalPickerOpen(false) }} title={path}><span>{path}</span>{ambiguous && <small>Kategori no: {item.externalId}</small>}</button> }) : <span>{references.isError ? 'Kategori listesi alınamadı. Salt-okunur eşitlemeyi yeniden deneyin.' : 'Aramayla eşleşen kategori bulunamadı.'}</span>}</div></div>}</div></div></section></div>{categorySuggestions.length > 0 && <div className="category-suggestions" aria-label="Önerilen kategoriler"><span>Önerilen {selectedPlatform.label} kategorileri</span>{categorySuggestions.map(item => <button type="button" key={item.externalId} aria-pressed={item.externalId === externalId} className={item.externalId === externalId ? 'active' : ''} title={`Benzerlik ${item.score}/100`} onClick={() => { setExternalId(item.externalId); setExternalPickerOpen(false) }}>{cleanMarketplaceCategoryPath(item.path)}</button>)}</div>}<footer><span>{references.isLoading ? 'Kategori listesi yükleniyor…' : references.data ? `${externalLeaves.length.toLocaleString('tr-TR')} yaprak kategori · Güncellenme ${new Date(references.data.fetchedAt).toLocaleString('tr-TR')}` : references.isError ? 'Güncel kategori listesi bulunamadı' : 'Kategori listesi henüz alınmadı'}{mapping.data ? ` · Kayıt v${mapping.data.version}` : ''}</span><button type="button" disabled={!localId || !externalId || save.isPending || mapping.isLoading} onClick={() => save.mutate()}><UiIcon name="externalLink" />{save.isPending ? 'Kaydediliyor…' : mapping.data ? 'Eşleştirmeyi Güncelle' : 'Eşleştirmeyi Kaydet'}</button></footer></article>
     <article className="mapping-reference-card mapping-option-summary panel-library-section"><div className="mapping-section-heading"><div><h2><UiIcon name="grid" />3. Ürün Seçenekleri</h2><p>Renk ve beden gibi varyant seçeneklerini panele kaydedin.</p></div></div><PanelAttributeLibraryBuilder role="OPTION" attributes={localAttributes.data?.items.filter(item => item.isActive) ?? []} onNotice={setNotice} /></article>
 <article id="category-value-mapping" className="mapping-reference-card mapping-attribute-summary panel-library-section"><div className="mapping-section-heading"><div><h2><UiIcon name="slidersHorizontal" />4. Kategori Özellikleri ve Değerleri</h2><p>Ürün özelliklerini ve değerlerini doğrudan panele kaydedin.</p></div></div><PanelAttributeLibraryBuilder role="ATTRIBUTE" attributes={localAttributes.data?.items.filter(item => item.isActive) ?? []} onNotice={setNotice} /></article>
    <article className="mapping-reference-card mapping-saved-card">
      <header>
        <h2><UiIcon name="equal" />5. Kayıtlı Kategori Eşleştirmeleri</h2>
        <div className="mapping-saved-header-tools">
          <div className="mapping-saved-tabs mapping-platform-tabs rv-tabs rv-view-tabs" role="tablist" aria-label="Kayıtlı eşleştirme platformları">
            {shownMappingPlatforms.filter(platform => platform.code !== 'SHOPIFY').map(platform => {
              const available = activeMappingConnections.some(item => item.platformCode.toUpperCase() === platform.code)
              return <button type="button" role="tab" aria-selected={selectedPlatformCode === platform.code} className={`${selectedPlatformCode === platform.code ? 'active is-active' : ''}${available ? '' : ' is-unavailable'}`} title={available ? `${platform.label} eşleştirmelerini göster` : `${platform.label} için aktif bağlantı yok`} onClick={() => { setSelectedPlatformCode(platform.code); setSavedPage(1); setLocalId(''); setExternalId(''); setExternalSearch(''); setNotice('') }}><span className="rv-tab-label">{platformLogoSource(platform.code) && <img className="rv-view-tab-logo" src={platformLogoSource(platform.code)!} alt="" aria-hidden="true" />}{platform.label}</span></button>
            })}
          </div>
          <input aria-label="Kayıtlı eşleştirmelerde ara" value={savedSearch} onChange={event => { setSavedSearch(event.target.value); setSavedPage(1) }} placeholder="Eşleştirme ara..." />
          <button type="button" className="mapping-icon-button mapping-sort-button" aria-label={savedSort === 'NAME_ASC' ? 'Kayıtlı eşleştirmeleri Z-A sırala' : 'Kayıtlı eşleştirmeleri A-Z sırala'} title={savedSort === 'NAME_ASC' ? 'Z-A sırala' : 'A-Z sırala'} onClick={() => { setSavedSort(current => current === 'NAME_ASC' ? 'NAME_DESC' : 'NAME_ASC'); setSavedPage(1) }}>{savedSort === 'NAME_ASC' ? 'A–Z' : 'Z–A'}</button>
        </div>
      </header>
      <div className="mapping-saved-table"><table><thead><tr><th>Panel Kategorisi</th><th>Platform</th><th>Platform Kategorisi</th><th>Durum</th><th>İşlemler</th></tr></thead><tbody>{sortedSavedRows.length ? visibleSavedRows.map(({ item, local, external }) => { const status = categoryMappingStatus(item); return <tr key={item.id}><td>{local?.path ?? item.localId}</td><td><span className="ms-platform"><img className={platformLogoClass(selectedPlatform.code)} src={platformLogoSource(selectedPlatform.code) ?? '/platforms/trendyol.png'} alt="" aria-hidden="true" />{selectedPlatform.label}</span></td><td>{cleanMarketplaceCategoryPath(external?.path ?? item.externalId)}</td><td><span className={`mapping-table-status ${status.className}`} title={status.title}><MappingStateGlyph state={status.className === 'active' ? 'done' : status.className === 'pending' ? 'none' : 'partial'} />{status.label}</span></td><td><div className="mapping-row-actions">{selectedPlatformCode !== 'SHOPIFY' && <button type="button" className="mapping-feature-button" aria-label="Kategori özelliklerini düzenle" onClick={() => { setLocalId(item.localId); setExternalId(item.externalId); setAdvancedOpen(true) }}>Kategori Özellikleri</button>}<button type="button" className="mapping-icon-button mapping-delete-button" aria-label="Eşleştirmeyi sil" disabled={removeMapping.isPending} onClick={() => { if (window.confirm('Bu kategori eşleştirmesi kaldırılsın mı?')) removeMapping.mutate(item) }}><MappingDeleteIcon /></button></div></td></tr> }) : <tr><td className="mapping-empty-table-cell" colSpan={5}>{categoryMappings.isLoading ? `${selectedPlatform.label} eşleştirmeleri yükleniyor…` : !connectionId ? `${selectedPlatform.label} için aktif bağlantı bulunamadı.` : <div className="empty mapping-table-empty">Kayıtlı kategori eşleştirmesi bulunamadı.</div>}</td></tr>}</tbody></table></div>
      <footer><span>Toplam {sortedSavedRows.length.toLocaleString('tr-TR')} eşleştirme</span><Pagination className="pagination-controls" page={currentSavedPage} totalPages={savedPageCount} hasNext={currentSavedPage < savedPageCount} onPageChange={setSavedPage} onPrevious={() => setSavedPage(value => Math.max(1, value - 1))} onNext={() => setSavedPage(value => Math.min(savedPageCount, value + 1))} disabled={categoryMappings.isFetching} ariaLabel="Kayıtlı kategori eşleştirmeleri sayfaları" /></footer>
    </article>
{exportOpen && <div className="workspace-modal-backdrop mapping-transfer-backdrop" role="presentation" onMouseDown={() => { if (!transferBusy) setExportOpen(false) }}><section className="workspace-modal mapping-transfer-modal mapping-export-modal" role="dialog" aria-modal="true" aria-labelledby="mapping-export-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="mapping-export-title">Dışa Aktarılacak Alanları Seçin</h2><p>Yedeğe dahil edilecek alanları seçin. Seçimleriniz tek bir JSON dosyasında toplanır.</p></div><button type="button" className="modal-close" onClick={() => setExportOpen(false)} disabled={transferBusy} aria-label="Dışa aktarma penceresini kapat"><UiIcon name="close" /></button></header><div className="mapping-transfer-options mapping-export-options"><label className="mapping-transfer-all-option"><input type="checkbox" checked={Object.values(exportSelection).every(Boolean)} onChange={event => setExportSelection({ categories: event.target.checked, options: event.target.checked, attributes: event.target.checked, mappings: event.target.checked })} /><span><strong>Tümü</strong><small>Yukarıdaki tüm alanları tek seferde seçer.</small></span><em>{Object.values(exportSelection).filter(Boolean).length}/4 seçili</em></label><div className="mapping-export-scope-grid"><label><input type="checkbox" checked={exportSelection.categories} onChange={event => setExportSelection(current => ({ ...current, categories: event.target.checked }))} /><span><strong>Panel Kategorileri</strong><small>Aktif kategori kayıtlarını aktarır.</small></span></label><label><input type="checkbox" checked={exportSelection.options} onChange={event => setExportSelection(current => ({ ...current, options: event.target.checked }))} /><span><strong>Ürün Seçenekleri</strong><small>Renk ve beden değerlerini aktarır.</small></span></label><label><input type="checkbox" checked={exportSelection.attributes} onChange={event => setExportSelection(current => ({ ...current, attributes: event.target.checked }))} /><span><strong>Kategori Özellikleri</strong><small>Özellik ve kayıtlı değerlerini aktarır.</small></span></label><label><input type="checkbox" checked={exportSelection.mappings} onChange={event => setExportSelection(current => ({ ...current, mappings: event.target.checked }))} /><span><strong>Kategori Eşleştirmeleri</strong><small>Kategori, özellik ve değer eşleşmelerini aktarır.</small></span></label></div></div><footer><span className="mapping-export-footer-hint">{Object.values(exportSelection).filter(Boolean).length ? 'Seçilen alanlar dışa aktarılmaya hazır.' : 'En az bir alan seçin.'}</span><button type="button" className="secondary" onClick={() => setExportOpen(false)} disabled={transferBusy}>Vazgeç</button><button type="button" onClick={() => void exportSelectedMappings()} disabled={transferBusy || !Object.values(exportSelection).some(Boolean)}>{transferBusy ? 'Hazırlanıyor…' : 'Seçilenleri Dışa Aktar'}</button></footer></section></div>}
{transferOpen && transferBundle && <div className="workspace-modal-backdrop mapping-transfer-backdrop" role="presentation" onMouseDown={() => { if (!transferBusy) { setTransferOpen(false); setTransferBundle(null) } }}><section className="workspace-modal mapping-transfer-modal" role="dialog" aria-modal="true" aria-labelledby="mapping-transfer-title" onMouseDown={event => event.stopPropagation()}><header><div><h2 id="mapping-transfer-title">İçe Aktarma Alanlarını Seçin</h2><p>{transferBundle.categories.length} kategori · {transferBundle.attributes.length} özellik · {transferBundle.categoryMappings.length + transferBundle.attributeMappings.length + transferBundle.attributeValueMappings.length} eşleşme</p></div><button type="button" className="modal-close" onClick={() => { setTransferOpen(false); setTransferBundle(null) }} disabled={transferBusy} aria-label="İçe aktarmayı kapat"><UiIcon name="close" /></button></header><div className="mapping-transfer-options">{([['categories', 'Kategoriler', 'Panel kategorilerini eksik olanları ekleyerek güncelle'], ['options', 'Ürün Seçenekleri', 'option- ile başlayan seçenek başlıklarını güncelle'], ['attributes', 'Ürün Özellikleri', 'Ürün özelliklerini ve değerlerini güncelle'], ['mappings', 'Eşleşmeler', 'Kategori, özellik ve değer eşleşmelerini uygula']] as Array<[MappingTransferScope, string, string]>).map(([scope, label, description]) => <label key={scope}><input type="checkbox" checked={transferSelection[scope]} onChange={event => setTransferSelection(current => ({ ...current, [scope]: event.target.checked }))} /><span><strong>{label}</strong><small>{description}</small></span></label>)}</div><p className="mapping-transfer-warning">Mevcut kayıtlar silinmez; aynı kod/ad ve değerler güncellenir, eksikler eklenir. Eşleşmeler seçili aktif {selectedPlatform.label} bağlantısının güncel snapshot’ı ile doğrulanır.</p><footer><button type="button" className="secondary" onClick={() => { setTransferOpen(false); setTransferBundle(null) }} disabled={transferBusy}>Vazgeç</button><button type="button" onClick={() => void importMappings()} disabled={transferBusy}>{transferBusy ? 'İçe aktarılıyor…' : 'Seçilenleri Güncelle'}</button></footer></section></div>}
      {advancedOpen && <div className="workspace-modal-backdrop mapping-advanced-backdrop" role="presentation" onMouseDown={() => setAdvancedOpen(false)}><section className="workspace-modal mapping-advanced-modal" role="dialog" aria-modal="true" aria-labelledby="mapping-advanced-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">KATEGORİ ÇALIŞMA ALANI</p><h2 id="mapping-advanced-title">Kategori alanları ve değerleri</h2><p>Pazaryeri alanlarını panel özellikleriyle eşleyin ve değer eşlemelerini tamamlayın.</p></div><button type="button" className="modal-close" onClick={() => setAdvancedOpen(false)} aria-label="Kategori çalışma alanını kapat"><UiIcon name="close" /></button></header><div className="mapping-advanced-modal-body"><section className="mapping-popup-content"><CategoryAttributeWorkspace platformCode={selectedPlatformCode} connectionId={connectionId} localCategoryId={localId} localCategories={localLeaves} localAttributes={localAttributes.data?.items.filter(item => item.isActive) ?? []} onNotice={setNotice} /></section></div></section></div>}
  </section>
}

function slug(value: string) { return value.toLocaleLowerCase('tr-TR').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || `attr-${Date.now()}` }
function attributeCodeForRole(title: string, role: 'ATTRIBUTE' | 'OPTION') { return `${role === 'OPTION' ? 'option-' : 'attribute-'}${slug(title)}` }
function isOptionAttribute(attribute: Pick<LocalAttribute, 'code' | 'name' | 'roles'>) {
  const normalizedName = normalizedAttributeKey(attribute.name)
  return attribute.code.trim().toLowerCase().startsWith('option-')
    || attribute.roles?.some(role => role.toUpperCase() === 'OPTION') === true
    || isVariantOptionAttributeName(normalizedName)
}

function cleanMarketplaceCategoryPath(value: string) {
  return value.replace(/\[TDG\]\s*/gi, '').replace(/\(\s*TDG\s*\)\s*/gi, '').replace(/\s*\/\s*/g, ' / ').trim()
}

function categoryMappingStatus(item: CatalogMapping) {
  if (item.status !== 'VERIFIED' && item.status !== 'ACTIVE') return { className: 'error', label: mappingStatusLabel(item.status), title: 'Kategori eşleştirmesi doğrulanmadı.' }
  if (item.missingRequiredAttributeCount === null || item.missingRequiredAttributeCount === undefined) return { className: 'pending', label: 'Kontrol bekliyor', title: 'Zorunlu kategori alanları için güncel kontrol verisi henüz hazır değil.' }
  if (item.missingRequiredAttributeCount > 0) return { className: 'warning', label: 'Zorunlu Alan Eksik', title: `${item.missingRequiredAttributeCount} zorunlu kategori alanı eşlenmedi.` }
  return { className: 'active', label: 'Aktif', title: 'Kategori ve zorunlu alan eşlemeleri hazır.' }
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
  const visibleCategories = activeCategories.filter(category => !normalizedSearch || `${category.path} ${category.name}`.toLocaleLowerCase('tr-TR').includes(normalizedSearch)).slice().sort((left, right) => left.path.localeCompare(right.path, 'tr-TR', { sensitivity: 'base', numeric: true }))

  useScrollLock(true, true)

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

  return <div className="panel-attribute-modal panel-attribute-category-modal" role="dialog" aria-modal="true" aria-labelledby="attribute-category-assignment-title"><div className="panel-attribute-modal-card panel-attribute-category-modal-card"><header><div><strong id="attribute-category-assignment-title">{attribute.name} Kategorileri</strong><small>{role === 'OPTION' ? 'Ürün seçeneği' : 'Ürün özelliği'} · görüneceği kategorileri seçin</small></div><button type="button" aria-label="Kategori kapsamı penceresini kapat" onClick={onClose} disabled={saving}><UiIcon name="close" /></button></header><div className="panel-attribute-category-search"><input autoFocus aria-label="Kategorilerde ara" value={search} onChange={event => setSearch(event.target.value)} placeholder="Kategori yolu veya adı ara..." /><span>{selectedCategoryIds.length} seçili</span></div>{loading ? <div className="panel-attribute-category-list panel-attribute-category-list-loading" role="status" aria-live="polite" aria-busy="true" aria-label="Kategori kapsamları yükleniyor">{Array.from({ length: Math.max(12, activeCategories.length) }, (_, index) => <span className="panel-attribute-category-skeleton" key={index} aria-hidden="true" />)}</div> : error && !Object.keys(requirementsByCategory).length ? <div className="mapping-empty-row attribute-feedback is-error">{error}</div> : <div className="panel-attribute-category-list" role="listbox" aria-label="Özelliğin görüneceği kategoriler">{visibleCategories.length ? visibleCategories.map(category => <button type="button" role="option" aria-selected={isSelected(category.id)} className={isSelected(category.id) ? 'active' : ''} key={category.id} onClick={() => toggleCategory(category.id)}><span>{category.path}</span><i aria-hidden="true">{isSelected(category.id) ? <UiIcon name="check" /> : null}</i></button>) : <div className="mapping-empty-row">Aramaya uygun kategori bulunamadı.</div>}</div>}<footer><button type="button" className="secondary" onClick={onClose} disabled={saving}>Vazgeç</button><button type="button" onClick={() => void save()} disabled={loading || saving || !!error && !Object.keys(requirementsByCategory).length}>{saving ? 'Kaydediliyor…' : 'Kapsamı Kaydet'}</button></footer></div></div>
}

function PanelAttributeLibraryBuilder({ role, attributes, onNotice }: { role: 'ATTRIBUTE' | 'OPTION'; attributes: LocalAttribute[]; onNotice: (value: string) => void }) {
  const client = useQueryClient()
  const [title, setTitle] = useState(''); const [values, setValues] = useState<string[]>([]); const [valueDraft, setValueDraft] = useState(''); const [editingId, setEditingId] = useState(''); const [newValues, setNewValues] = useState(''); const [editingValue, setEditingValue] = useState<{ id: string; value: string } | null>(null); const [categoryAssignmentId, setCategoryAssignmentId] = useState(''); const [feedback, setFeedback] = useState(''); const [feedbackTone, setFeedbackTone] = useState<'success' | 'error'>('success'); const [sortDirection, setSortDirection] = useState<1 | -1>(1)
  const isOption = role === 'OPTION'
  const matchingRecords = attributes.filter(attribute => !isWebColorAttributeName(attribute.name) && (isOption ? isOptionAttribute(attribute) : !isOptionAttribute(attribute)))
  const records = isOption ? matchingRecords : sortByTurkishName(matchingRecords)
  const editingAttribute = records.find(attribute => attribute.id === editingId) ?? null
  const categoryAssignmentAttribute = records.find(attribute => attribute.id === categoryAssignmentId) ?? null
  const categories = useQuery({ queryKey: ['categories', 'mapping-builder-assignment'], queryFn: () => loadAllPages<LocalCategory>('/catalog/categories'), enabled: !!categoryAssignmentAttribute })
  function showFeedback(message: string, tone: 'success' | 'error' = 'success') { setFeedback(message); setFeedbackTone(tone) }
  useScrollLock(Boolean(editingAttribute), true)
  useEffect(() => { setSortDirection(1) }, [editingId])
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
  async function sortValuesAlphabetically(attribute: LocalAttribute, direction: 1 | -1) {
    const current = attribute.values.filter(value => value.isActive).slice().sort((left, right) => left.sortOrder - right.sortOrder || left.value.localeCompare(right.value, 'tr'))
    const next = current.slice().sort((left, right) => direction * left.value.localeCompare(right.value, 'tr-TR', { sensitivity: 'base', numeric: true }) || current.indexOf(left) - current.indexOf(right))
    if (next.every((value, index) => value.id === current[index]?.id)) {
      showFeedback(direction === 1 ? 'Değerler zaten A–Z sıralı.' : 'Değerler zaten Z–A sıralı.')
      return true
    }
    try {
      const updated = await hubApi<LocalAttribute>(`/catalog/attributes/${attribute.id}/values/order`, { method: 'PUT', headers: { 'If-Match': `"v${attribute.version}"` }, body: JSON.stringify({ valueIds: next.map(value => value.id) }) })
      updateCache(updated)
      return true
    } catch (reason) { showFeedback(reason instanceof Error ? reason.message : 'Değerler sıralanamadı.', 'error'); return false }
  }
  async function toggleSortValues(attribute: LocalAttribute) {
    const direction = sortDirection
    if (await sortValuesAlphabetically(attribute, direction)) setSortDirection(direction === 1 ? -1 : 1)
  }
  const editingValues = editingAttribute?.values.filter(value => value.isActive).slice().sort((left, right) => left.sortOrder - right.sortOrder || left.value.localeCompare(right.value, 'tr')) ?? []
  return <div className="panel-attribute-library"><div className="panel-attribute-create"><label>{isOption ? 'Yeni Seçenek Başlığı' : 'Yeni Özellik Başlığı'}<input value={title} onChange={event => setTitle(event.target.value)} placeholder={isOption ? 'Örn. Beden, Renk' : 'Örn. Materyal, Kol Boyu'} /></label><label>İlk Değerler <TokenValueInput values={values} draft={valueDraft} onDraftChange={setValueDraft} onAdd={addInitialValues} onRemove={value => setValues(current => current.filter(item => item !== value))} placeholder="Değer yazın, Enter veya Ekle" /></label><button type="button" onClick={() => void create()}><UiIcon name="save" />Panele Kaydet</button></div>{feedback && <p className={`attribute-feedback ${feedbackTone === 'success' ? 'is-success' : 'is-error'}`} role="status">{feedback}</p>}<div className="panel-attribute-records">{records.length ? records.map(attribute => <article className={attribute.id === editingId || attribute.id === categoryAssignmentId ? 'selected' : ''} key={attribute.id}><button type="button" className="panel-attribute-record-main" onClick={() => setEditingId(attribute.id)}><strong>{attribute.name}</strong><small>{attribute.values.filter(value => value.isActive).length ? `${attribute.values.filter(value => value.isActive).length} değer kaydedildi` : 'Serbest değer'}</small></button><div className="panel-attribute-record-actions"><button type="button" aria-label={`${attribute.name} düzenle`} onClick={() => setEditingId(attribute.id)}><UiIcon name="edit" /></button><button type="button" className="panel-attribute-category-button" aria-label={`${attribute.name} kategorilerini düzenle`} onClick={() => setCategoryAssignmentId(attribute.id)}>Kategoriler</button><button type="button" aria-label={`${attribute.name} sil`} onClick={() => void removeAttribute(attribute)}><UiIcon name="close" /></button></div></article>) : <div className="mapping-empty-row"><span>{isOption ? 'Henüz panel seçeneği oluşturulmadı.' : 'Henüz panel özelliği oluşturulmadı.'}</span></div>}</div>{editingAttribute && <div className="panel-attribute-modal" role="dialog" aria-modal="true" aria-label={`${editingAttribute.name} değer düzenleme`}><div className="panel-attribute-modal-card"><header><div><strong>{editingAttribute.name}</strong><small>{isOption ? 'Panel seçeneği' : 'Panel özelliği'} · değerleri yönetin</small></div><button type="button" aria-label="Düzenleme penceresini kapat" onClick={() => { setEditingId(''); setEditingValue(null) }}><UiIcon name="close" /></button></header><div className="panel-attribute-add-value"><input value={newValues} onChange={event => setNewValues(event.target.value)} placeholder="Yeni değerleri virgülle yazın" /><button type="button" disabled={!newValues.trim()} onClick={() => void addValues(editingAttribute.id, newValues)}>Değer Ekle</button><div className="panel-attribute-sort-actions" aria-label="Değerleri alfabetik sırala"><button type="button" title={`Değerleri ${sortDirection === 1 ? 'A–Z' : 'Z–A'} sırala`} aria-label={`Değerleri ${sortDirection === 1 ? 'A–Z' : 'Z–A'} sırala`} onClick={() => void toggleSortValues(editingAttribute)}>{sortDirection === 1 ? 'A–Z' : 'Z–A'}</button></div></div><div className="panel-attribute-value-editor">{editingValues.length ? editingValues.map(value => <div key={value.id}>{editingValue?.id === value.id ? <input autoFocus value={editingValue.value} onChange={event => setEditingValue({ id: value.id, value: event.target.value })} /> : <span>{value.value}</span>}<div>{editingValue?.id === value.id ? <><button type="button" aria-label={`${value.value} değerini kaydet`} onClick={() => void updateValue(editingAttribute.id, value.id, editingValue.value, value.sortOrder)}><UiIcon name="check" /></button><button type="button" aria-label="Değer düzenlemeyi iptal et" onClick={() => setEditingValue(null)}><UiIcon name="close" /></button></> : <><button type="button" aria-label={`${value.value} değerini düzenle`} onClick={() => setEditingValue({ id: value.id, value: value.value })}><UiIcon name="edit" /></button><button type="button" aria-label={`${value.value} değerini sil`} onClick={() => void removeValue(editingAttribute.id, value.id)}><UiIcon name="close" /></button></>}</div></div>) : <span className="panel-value-empty">Henüz değer eklenmedi.</span>}</div></div></div>}{categoryAssignmentAttribute && <AttributeCategoryAssignmentModal attribute={categoryAssignmentAttribute} role={role} categories={categories.data?.items ?? []} onClose={() => setCategoryAssignmentId('')} onNotice={message => { showFeedback(message); onNotice(message) }} />}</div>
}

function TokenValueInput({ values, draft, onDraftChange, onAdd, onRemove, placeholder }: { values: string[]; draft: string; onDraftChange: (value: string) => void; onAdd: (values: string[]) => void; onRemove: (value: string) => void; placeholder: string }) {
  function addDraft() {
    const nextValues = draft.split(',').map(value => value.trim()).filter(Boolean)
    if (!nextValues.length) return
    onAdd(nextValues)
    onDraftChange('')
  }
  return <div className="panel-value-token-input"><div className="panel-value-token-entry"><input value={draft} onChange={event => onDraftChange(event.target.value)} onKeyDown={event => { if (event.key === 'Enter' || event.key === ',') { event.preventDefault(); addDraft() } }} placeholder={placeholder} /><button type="button" onClick={addDraft} disabled={!draft.trim()}>Ekle</button></div>{values.length > 0 && <div className="panel-value-token-list" aria-label="Eklenen ilk değerler">{values.map(value => <span className="panel-value-token" key={value}><span>{value}</span><button type="button" aria-label={`${value} değerini kaldır`} onClick={() => onRemove(value)}><UiIcon name="close" /></button></span>)}</div>}</div>
}


function CategoryAttributeWorkspace({ platformCode, connectionId, localCategoryId, localCategories, localAttributes, onNotice }: { platformCode: MappingPlatformCode; connectionId: string; localCategoryId: string; localCategories: LocalCategory[]; localAttributes: LocalAttribute[]; onNotice: (value: string) => void }) {
  const client = useQueryClient()
  const platformLabel = mappingPlatformLabel(platformCode)
  const [attributeCategoryId, setAttributeCategoryId] = useState(localCategoryId)
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
  // Same cache entry as the category mapping card, so the target path rarely costs a request.
  const categoryReferences = useQuery({ queryKey: ['reference-categories', connectionId], queryFn: () => hubApi<ReferenceData>(`/reference-data/categories?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId && !!categoryScope, retry: false, staleTime: 5 * 60_000 })
  const targetCategoryPath = cleanMarketplaceCategoryPath(categoryReferences.data?.items.find(item => item.externalId === categoryScope)?.path ?? '') || (categoryScope ? `Kategori no: ${categoryScope}` : '')
  const targetCategoryMapping = matchedCategories.find(item => item.category.id === attributeCategoryId)?.mapping
  const targetCategoryStatus = targetCategoryMapping ? categoryMappingStatus(targetCategoryMapping) : null
  const references = useQuery({ queryKey: ['reference-attributes', connectionId, categoryScope, 'embedded'], queryFn: () => hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId && !!categoryScope, retry: false })
  const sync = useMutation({ mutationFn: () => hubApi(`/connections/${connectionId}/reference-sync-jobs?resourceType=CATEGORY_ATTRIBUTES&parentExternalId=${encodeURIComponent(categoryScope)}`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: '{}' }), onSuccess: async () => { onNotice(`${platformLabel} kategori özellikleri salt-okunur eşitleme kuyruğuna alındı.`); for (const delay of [500, 1000, 2000, 4000]) { await new Promise(resolve => window.setTimeout(resolve, delay)); await client.invalidateQueries({ queryKey: ['reference-attributes', connectionId, categoryScope, 'embedded'] }) } }, onError: reason => onNotice(reason instanceof Error ? reason.message : 'Kategori özellikleri eşitlenemedi.') })
  const mappings = useQuery({ queryKey: ['attribute-mappings', connectionId, categoryScope], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/attributes?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}`), enabled: !!connectionId && !!categoryScope, retry: false })
  const requirements = categoryRequirements.data ?? []
  const remoteItems = references.data?.items.filter(item => item.isActive).map(item => ({ ...item, name: displayAttributeName(item.name) })) ?? []
  const categoryAttributes = requirements.map(item => item.attribute).filter(attribute => attribute.isActive !== false)
  const allPanelAttributes = [...localAttributes, ...categoryAttributes].filter((attribute, index, items) => items.findIndex(candidate => candidate.id === attribute.id) === index)
  const panelColorAttribute = allPanelAttributes.find(attribute => attribute.isActive !== false && isPanelColorAttributeName(attribute.name))
    ?? allPanelAttributes.find(attribute => attribute.isActive !== false && isWebColorAttributeName(attribute.name))
  const requirementOptionIds = new Set(requirements.filter(item => item.role === 'OPTION').map(item => item.attributeId))
  const webColorRemote = platformCode === 'TRENDYOL' ? remoteItems.find(item => isWebColorAttributeName(item.name)) : undefined
  // Section 3 creates global panel attributes. They are not required to have a
  // row in category_requirements before they can be mapped to a marketplace
  // field; the mapping workspace is where that category-specific relationship
  // is established. Keep explicit OPTION requirements as a fallback for
  // existing records whose code predates the role prefix convention.
  const optionAttributes = platformCode === 'TRENDYOL' ? allPanelAttributes
    .filter(attribute => requirementOptionIds.has(attribute.id) || isOptionAttribute(attribute))
    // Web Color and the real Renk field can share the panel Renk source, but
    // they are separate marketplace mappings. Keep panel Renk in the normal
    // option list so both cards can be configured independently.
    .filter(attribute => !isWebColorAttributeName(attribute.name))
    .filter((item, index, items) => items.findIndex(candidate => candidate.id === item.id) === index)
    .sort((left, right) => Number(requirements.some(item => item.role === 'OPTION' && item.attributeId === right.id && item.isRequired)) - Number(requirements.some(item => item.role === 'OPTION' && item.attributeId === left.id && item.isRequired)) || left.name.localeCompare(right.name, 'tr')) : []
  const allMappings = mappings.data ?? []
  const existingWebColorMapping = webColorRemote ? allMappings.find(item => item.externalId === webColorRemote.externalId) ?? null : null
  const specialColorAttribute = allPanelAttributes.find(item => item.id === existingWebColorMapping?.localId) ?? panelColorAttribute
  const specialColorOption = webColorRemote && specialColorAttribute ? { attribute: specialColorAttribute, remote: webColorRemote } : null
  const optionAttributeIds = new Set([...optionAttributes.map(item => item.id), ...(specialColorOption ? [specialColorOption.attribute.id] : [])])
  const sizeRemoteItems = remoteItems.filter(item => isSizeAttributeName(item.name))
  const sizeRemoteExternalIds = new Set(sizeRemoteItems.map(item => item.externalId))
  const sizeOptionAttributeIds = new Set(optionAttributes.filter(isSizeAttribute).map(item => item.id))
  const productAttributes = allPanelAttributes.filter(attribute => attribute.isActive !== false)
  const categoryOptionIds = new Set(requirements.filter(item => item.role === 'OPTION').map(item => item.attributeId))
  const colorRemoteExternalIds = new Set(remoteItems.filter(item => isPanelColorAttributeName(item.name)).map(item => item.externalId))
  const colorLocalAttributeIds = new Set(optionAttributes.filter(attribute => isPanelColorAttributeName(attribute.name)).map(attribute => attribute.id))
  const optionMappings = allMappings.filter(item => optionAttributeIds.has(item.localId) && !colorRemoteExternalIds.has(item.externalId))
  const optionAttributeNames = new Set([...optionAttributes.filter(attribute => !sizeOptionAttributeIds.has(attribute.id)), ...(specialColorOption ? [specialColorOption.attribute] : [])].map(attribute => normalizedAttributeKey(attribute.name)))
  const optionExternalIds = new Set(optionMappings.filter(item => !sizeRemoteExternalIds.has(item.externalId)).map(item => item.externalId))
  const productMappings = platformCode === 'TRENDYOL' ? allMappings.filter(item => !optionAttributeIds.has(item.localId) || sizeRemoteExternalIds.has(item.externalId) || (colorLocalAttributeIds.has(item.localId) && colorRemoteExternalIds.has(item.externalId))) : allMappings
  const productMappingByExternal = new Map(productMappings.map(item => [item.externalId, item]))
  const optionRemoteItems = platformCode === 'TRENDYOL' ? remoteItems.filter(item => isVariantOptionAttributeName(item.name) && !isPanelColorAttributeName(item.name) && !sizeRemoteExternalIds.has(item.externalId) && item.externalId !== webColorRemote?.externalId && !isWebColorAttributeName(item.name)).sort((left, right) => Number(right.isRequired) - Number(left.isRequired) || left.name.localeCompare(right.name, 'tr')) : []
  const productRemoteItems = (platformCode === 'TRENDYOL' ? remoteItems.filter(item => !optionExternalIds.has(item.externalId) && item.externalId !== webColorRemote?.externalId && !isWebColorAttributeName(item.name) && (!optionAttributeNames.has(normalizedAttributeKey(item.name)) || isPanelColorAttributeName(item.name))) : remoteItems).slice().sort((left, right) => Number(right.isRequired) - Number(left.isRequired) || left.name.localeCompare(right.name, 'tr'))
  const optionCount = new Set([...optionAttributes.map(item => item.id), ...(specialColorOption ? [specialColorOption.attribute.id] : [])]).size
  const productSync = useMutation({ mutationFn: () => hubApi(`/connections/${connectionId}/product-sync-jobs`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: '{}' }), onSuccess: () => { onNotice('Ürünler salt-okunur eşitleme kuyruğuna alındı; pazar yerine yazma yapılmaz.') }, onError: reason => onNotice(reason instanceof Error ? reason.message : 'Ürün eşitleme başlatılamadı.') })
  // Bulk "same name" matching for the whole category: first the headings (attributes and option groups), then every value of the mapped headings.
  const [bulkBusy, setBulkBusy] = useState<'' | 'HEADINGS' | 'VALUES' | 'TEMPLATES'>('')
  const [selectedEntryKey, setSelectedEntryKey] = useState('')
  const [extraPanel, setExtraPanel] = useState<'' | 'defaults' | 'conflicts'>('')
  const [collapsedGroups, setCollapsedGroups] = useState<string[]>([])
  // Same cache entries as the two panels, so the header buttons can show how many items each holds.
  const attributeDefaultsCount = useQuery({ queryKey: ['attribute-defaults', connectionId], queryFn: () => hubApi<unknown[]>(`/connections/${connectionId}/attribute-defaults`), retry: false, enabled: !!connectionId && (platformCode === 'TRENDYOL' || platformCode === 'HEPSIBURADA') }).data?.length ?? 0
  const mappingConflictData = useQuery({ queryKey: ['mapping-conflicts', connectionId], queryFn: () => hubApi<{ sameCategory?: unknown[]; crossCategory?: unknown[] }>(`/mappings/conflicts?connectionId=${encodeURIComponent(connectionId)}`), retry: false, staleTime: 30_000, enabled: !!connectionId }).data
  const mappingConflictCount = (mappingConflictData?.sameCategory?.length ?? 0) + (mappingConflictData?.crossCategory?.length ?? 0)
  // Value progress of every mapped field, read from the same cache entries the value table uses.
  const mappedValueFields = [...productRemoteItems.filter(item => productMappingByExternal.has(item.externalId)), ...(webColorRemote && existingWebColorMapping ? [webColorRemote] : [])]
  const valueMappingQueries = useQueries({ queries: mappedValueFields.map(item => ({ queryKey: ['attribute-value-mappings', connectionId, `${categoryScope}/${item.externalId}`], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(`${categoryScope}/${item.externalId}`)}`), retry: false, staleTime: 30_000, enabled: !!connectionId && !!categoryScope })) })
  const valueMappingsByExternal = new Map(mappedValueFields.map((item, index) => [item.externalId, valueMappingQueries[index]?.data]))
  const attributeNameHints = useQuery({ queryKey: ['attribute-name-hints', connectionId], queryFn: () => hubApi<Record<string, string[]>>(`/mappings/attribute-name-hints?connectionId=${encodeURIComponent(connectionId)}`), retry: false, staleTime: 5 * 60_000, enabled: !!connectionId })
  // Brings over the attribute and value decisions made for the connection's other categories.
  async function fillFromOtherCategories() {
    if (bulkBusy) return
    setBulkBusy('TEMPLATES'); onNotice('')
    try {
      const result = await hubApi<{ attributes: number; values: number }>(`/mappings/templates/fill?connectionId=${encodeURIComponent(connectionId)}&categoryExternalId=${encodeURIComponent(categoryScope)}`, { method: 'POST', body: '{}' })
      await client.invalidateQueries({ queryKey: ['attribute-mappings', connectionId, categoryScope] })
      await client.invalidateQueries({ queryKey: ['attribute-value-mappings', connectionId] })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      onNotice(result.attributes || result.values
        ? `Diğer kategorilerdeki eşlemelerden ${result.attributes} başlık ve ${result.values} değer getirildi.`
        : 'Diğer kategorilerden getirilecek yeni eşleme yok. Değer listesi henüz gelmemiş başlıklar, liste gelince otomatik dolar.')
    } catch (reason) {
      onNotice(reason instanceof Error ? reason.message : 'Diğer kategorilerden doldurma tamamlanamadı.')
    } finally { setBulkBusy('') }
  }
  async function matchHeadingsByName() {
    if (!references.data || bulkBusy) return
    setBulkBusy('HEADINGS'); onNotice('')
    const snapshotIdNow = references.data.snapshotId
    let matched = 0; let bridged = 0; let ambiguous = 0; let failed = 0
    const put = async (localAttribute: LocalAttribute, remote: ReferenceItem, role: 'ATTRIBUTE' | 'OPTION', viaHint = false) => {
      try {
        await hubApi<CatalogMapping>(`/mappings/attributes/${localAttribute.id}`, { method: 'PUT', body: JSON.stringify({ connectionId, snapshotId: snapshotIdNow, scopeExternalId: categoryScope, externalId: remote.externalId, status: 'VERIFIED', role }) })
        if (viaHint) bridged++; else matched++
      } catch { failed++ }
    }
    try {
      const localByName = new Map<string, LocalAttribute[]>()
      for (const attribute of productAttributes) localByName.set(normalizedAttributeKey(attribute.name), [...(localByName.get(normalizedAttributeKey(attribute.name)) ?? []), attribute])
      // Second pass for headings without a same-named panel attribute: synonyms (Materyal ≈ Kumaş
      // Tipi) and the name the panel attribute has on the other marketplace (platform bridge).
      const hintedByKey = new Map<string, LocalAttribute[]>()
      for (const attribute of productAttributes) {
        const keys = new Set([canonicalKey(attribute.name), ...(attributeNameHints.data?.[attribute.id] ?? []).map(canonicalKey)])
        for (const key of keys) hintedByKey.set(key, [...(hintedByKey.get(key) ?? []), attribute])
      }
      for (const remote of productRemoteItems) {
        if (productMappingByExternal.has(remote.externalId)) continue
        const candidates = localByName.get(normalizedAttributeKey(remote.name)) ?? []
        if (candidates.length > 1) { ambiguous++; continue }
        if (candidates.length === 1) { await put(candidates[0], remote, categoryOptionIds.has(candidates[0].id) || isOptionAttribute(candidates[0]) ? 'OPTION' : 'ATTRIBUTE'); continue }
        const hinted = [...new Set(hintedByKey.get(canonicalKey(remote.name)) ?? [])]
        if (hinted.length === 1) await put(hinted[0], remote, categoryOptionIds.has(hinted[0].id) || isOptionAttribute(hinted[0]) ? 'OPTION' : 'ATTRIBUTE', true)
        else if (hinted.length > 1) ambiguous++
      }
      for (const option of optionAttributes.filter(item => !sizeOptionAttributeIds.has(item.id))) {
        if (optionMappings.some(mapping => mapping.localId === option.id && optionRemoteItems.some(remote => remote.externalId === mapping.externalId))) continue
        const remotes = optionRemoteItems.filter(remote => normalizedAttributeKey(remote.name) === normalizedAttributeKey(option.name))
        if (remotes.length > 1) { ambiguous++; continue }
        if (remotes.length === 1) await put(option, remotes[0], 'OPTION')
      }
      await client.invalidateQueries({ queryKey: ['attribute-mappings', connectionId, categoryScope] })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      onNotice(matched || bridged
        ? `${matched ? `${matched} başlık aynı adlı panel özelliğiyle` : ''}${matched && bridged ? ', ' : ''}${bridged ? `${bridged} başlık eş anlamlısı ya da diğer pazaryerindeki eşlemesi üzerinden` : ''} eşlendi.${ambiguous ? ` Birden fazla adayı olan ${ambiguous} başlık atlandı.` : ''}${failed ? ` ${failed} başlık eşlenemedi.` : ''}`
        : failed ? `Başlık eşlemeleri kaydedilemedi (${failed}).` : `Yeni eşleşen başlık bulunamadı.${ambiguous ? ` Birden fazla adayı olan ${ambiguous} başlık atlandı.` : ''}`)
    } finally { setBulkBusy('') }
  }
  async function matchAllValuesByName() {
    if (!references.data || bulkBusy) return
    setBulkBusy('VALUES'); onNotice('')
    let pairs = 0; let headings = 0; let skipped = 0; let unavailable = 0; let failed = 0
    try {
      const seen = new Set<string>()
      for (const mapping of allMappings) {
        if (mapping.status !== 'VERIFIED' || seen.has(`${mapping.localId}|${mapping.externalId}`)) continue
        seen.add(`${mapping.localId}|${mapping.externalId}`)
        const local = allPanelAttributes.find(item => item.id === mapping.localId)
        const remote = references.data.items.find(item => item.externalId === mapping.externalId && item.isActive)
        const localValues = (local?.values ?? []).filter(item => item.isActive)
        if (!local || !remote || !localValues.length) continue
        if (platformCode === 'TRENDYOL' && usesCustomPanelColorValue(remote.name)) continue
        const valueScope = `${categoryScope}/${mapping.externalId}`
        try {
          const [valueReferences, existing] = await Promise.all([
            hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(mapping.externalId)}/values?connectionId=${encodeURIComponent(connectionId)}`),
            hubApi<CatalogMapping[]>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(valueScope)}`)
          ])
          const plan = planDirectReferenceValues(localValues, valueReferences.items.filter(item => item.isActive))
          let touched = false
          for (const pair of plan.mappings) {
            const current = existing.find(item => item.localId === pair.localId && item.externalId === pair.externalId)
            if (!attributeValueMappingNeedsSave(current, pair.localId, pair.externalId, valueReferences.snapshotId)) continue
            // a panel value goes to one marketplace value (Hepsiburada may reuse it), so never add a second target
            if (platformCode !== 'HEPSIBURADA' && existing.some(item => item.localId === pair.localId && item.externalId !== pair.externalId)) { skipped++; continue }
            try {
              await hubApi<CatalogMapping>(`/mappings/attribute-values/${pair.localId}`, { method: 'PUT', headers: current ? { 'If-Match': `"v${current.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId: valueReferences.snapshotId, scopeExternalId: valueScope, externalId: pair.externalId, status: 'VERIFIED' }) })
              pairs++; touched = true
            } catch { failed++ }
          }
          if (touched) headings++
        } catch { unavailable++ }
      }
      await client.invalidateQueries({ queryKey: ['attribute-value-mappings'] })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      const extra = `${skipped ? ` ${skipped} değer başka bir karşılığa bağlı olduğu için atlandı.` : ''}${unavailable ? ` ${unavailable} başlığın değer listesi okunamadı; önce alanları yenileyin.` : ''}${failed ? ` ${failed} değer eşlenemedi.` : ''}`
      onNotice(pairs ? `${headings} başlıkta ${pairs} değer aynı adlı karşılığıyla eşlendi.${extra}` : `Yeni birebir eşleşen değer bulunamadı.${extra}`)
    } finally { setBulkBusy('') }
  }
  if (!connectionId) return <article className="panel mapping-step"><div className="editor-section-title"><span>4</span><div><h2>{platformLabel} kategori ve özellik eşlemeleri</h2><p>Önce 1. adımdaki kategori eşlemesini tamamlayın.</p></div></div><div className="unknown"><strong>Bağlantı seçilmedi</strong><p>Önce aktif {platformLabel} bağlantısını seçin.</p></div></article>
  if (categoryMappings.isLoading) return <article className="panel mapping-step"><div className="editor-section-title"><span>4</span><div><h2>{platformLabel} kategori ve özellik eşlemeleri</h2><p>1. adımdaki eşleşmiş kategoriler hazırlanıyor.</p></div></div><Busy text="Kategori eşlemeleri yükleniyor…" /></article>
  if (!matchedCategories.length) return <article className="panel mapping-step"><div className="editor-section-title"><span>4</span><div><h2>{platformLabel} kategori ve özellik eşlemeleri</h2><p>Yalnız 1. adımda eşleşen kategorilerin özellikleri burada açılır.</p></div></div><div className="unknown"><strong>Kategori eşlemesi bekleniyor</strong><p>Önce 1. adımda en az bir panel kategorisini {platformLabel} kategorisiyle eşleştirin.</p></div></article>
  if (!categoryMapping.data) return <article className="panel mapping-step"><div className="editor-section-title"><span>4</span><div><h2>{platformLabel} kategori ve özellik eşlemeleri</h2><p>Ürün özellikleri ve seçenekleri kendi adımlarında tanımladıktan sonra değer karşılıklarını eşleyin.</p></div></div><div className="unknown"><strong>Kategori eşlemesi bekleniyor</strong><p>Önce 1. adımda panel kategorisini {platformLabel} kategorisiyle eşleştirin.</p></div></article>

  const mappingEntries: MappingEntry[] = [
    ...(specialColorOption ? [(() => { const colorValues = (allPanelAttributes.find(attribute => attribute.id === existingWebColorMapping?.localId)?.values ?? []).filter(value => value.isActive); const colorDone = new Set((valueMappingsByExternal.get(specialColorOption.remote.externalId) ?? []).filter(mapping => colorValues.some(value => value.id === mapping.localId)).map(mapping => mapping.localId)).size; const colorState: FieldState = !existingWebColorMapping ? 'none' : colorValues.length && colorDone < colorValues.length ? 'partial' : 'done'; return { key: 'web-color', name: 'Web Color', required: true, state: colorState, meta: !existingWebColorMapping ? 'Eşlenmedi' : colorValues.length ? (colorDone < colorValues.length ? `${colorDone}/${colorValues.length} değer` : `${colorValues.length} değer`) : 'Eşlendi', node: <WebColorMappingCard connectionId={connectionId} categoryScope={categoryScope} snapshotId={references.data!.snapshotId} localAttribute={specialColorOption.attribute} localAttributes={productAttributes} categoryOptionIds={categoryOptionIds} remoteAttribute={specialColorOption.remote} existingMapping={existingWebColorMapping} existingMappings={allMappings} onNotice={onNotice} /> } })()] : []),
    ...(optionRemoteItems.length > 0 ? optionAttributes.filter(item => !sizeOptionAttributeIds.has(item.id)).map(item => {
      const mapped = optionMappings.some(mapping => mapping.localId === item.id && optionRemoteItems.some(remote => remote.externalId === mapping.externalId))
      return { key: `option-${item.id}`, name: item.name, required: false, state: (mapped ? 'done' : 'none') as FieldState, meta: mapped ? 'Seçenek' : 'Eşlenmedi', node: <OptionMappingCard connectionId={connectionId} categoryScope={categoryScope} snapshotId={references.data!.snapshotId} localAttribute={item} remoteAttributes={optionRemoteItems} existingMapping={optionMappings.find(mapping => mapping.localId === item.id && optionRemoteItems.some(remote => remote.externalId === mapping.externalId)) ?? null} onNotice={onNotice} /> }
    }) : []),
    ...productRemoteItems.map(item => {
      const current = productMappingByExternal.get(item.externalId) ?? null
      const local = current ? productAttributes.find(attribute => attribute.id === current.localId) : undefined
      const activeValues = local?.values.filter(value => value.isActive) ?? []
      const needsValues = !!current && activeValues.length > 0 && item.allowsCustomValue !== true && !(platformCode === 'TRENDYOL' && usesCustomPanelColorValue(item.name))
      const saved = valueMappingsByExternal.get(item.externalId)
      const done = needsValues && saved ? new Set(saved.filter(mapping => activeValues.some(value => value.id === mapping.localId)).map(mapping => mapping.localId)).size : null
      const state: FieldState = !current ? 'none' : needsValues && done !== null && done < activeValues.length ? 'partial' : 'done'
      const meta = !current ? 'Eşlenmedi' : !needsValues ? 'Eşlendi' : done !== null && done < activeValues.length ? `${done}/${activeValues.length} değer` : `${activeValues.length} değer`
      return { key: `attribute-${item.externalId}`, name: item.name, required: item.isRequired === true, state, meta, remote: item, mapping: current }
    })
  ]
  const requiredEntries = mappingEntries.filter(entry => entry.required)
  const optionalEntries = mappingEntries.filter(entry => !entry.required)
  const activeEntry = mappingEntries.find(entry => entry.key === selectedEntryKey) ?? mappingEntries.find(entry => entry.required && entry.state !== 'done') ?? mappingEntries[0]
  const activeIndex = activeEntry ? mappingEntries.indexOf(activeEntry) : -1
  const nextOpenEntry = [...mappingEntries.slice(activeIndex + 1), ...mappingEntries.slice(0, Math.max(0, activeIndex))].sort((left, right) => Number(right.required) - Number(left.required)).find(entry => entry.state !== 'done')
  const entryGroups = [{ title: 'Zorunlu', items: requiredEntries }, { title: 'İsteğe bağlı', items: optionalEntries }].filter(group => group.items.length > 0)
  const requiredReady = requiredEntries.filter(entry => entry.state === 'done').length
  const openRequired = requiredEntries.length - requiredReady
  const footerHint = openRequired ? `${openRequired} zorunlu alan bekliyor` : 'Zorunlu alanların hepsi hazır'
  const goToNext = nextOpenEntry ? () => setSelectedEntryKey(nextOpenEntry.key) : undefined
  const dataLoading = references.isLoading || mappings.isLoading || categoryRequirements.isLoading
  const dataFailed = references.isError || mappings.isError || categoryRequirements.isError
  return <article className="panel mapping-step cm-card">
    <header className="cm-page-head"><h2>Kategori eşleştirmeleri</h2><p>{platformLabel} alanlarını panel özellikleri ve değerleriyle eşleyin.</p></header>
    <div className="cm-context">
      <label className="cm-field"><small>Panel Kategorisi</small><select value={attributeCategoryId} onChange={event => setAttributeCategoryId(event.target.value)}>{matchedCategories.map(({ category }) => <option key={category.id} value={category.id}>{category.path}</option>)}</select></label>
      <span className="cm-link cm-context-link" aria-hidden="true"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round"><path d="M6.6 9.4 9.4 6.6" /><path d="M7.4 4.6 8.6 3.4a2.6 2.6 0 0 1 3.7 3.7l-1.2 1.2" /><path d="M8.6 11.4 7.4 12.6a2.6 2.6 0 0 1-3.7-3.7l1.2-1.2" /></svg></span>
      <div className="cm-field cm-target"><small>{platformLabel} Kategorisi</small><span className="cm-path"><strong title={targetCategoryPath}>{targetCategoryPath}</strong>{targetCategoryStatus && <span className={`mapping-table-status ${targetCategoryStatus.className}`} title={targetCategoryStatus.title}>{targetCategoryStatus.label}</span>}</span></div>
      {!dataLoading && !dataFailed && requiredEntries.length > 0 && <div className="cm-progress" aria-label={`Zorunlu alanlar: ${requiredReady} / ${requiredEntries.length}`}><div><span>Zorunlu Alanlar</span><b>{requiredReady} / {requiredEntries.length}</b></div><div className="cm-track"><i style={{ width: `${Math.round(requiredReady / requiredEntries.length * 100)}%` }} /></div></div>}
      {!dataLoading && !dataFailed && <div className="cm-actions" role="toolbar" aria-label="Kategori eşleme işlemleri">
      <button type="button" className="secondary" title="Önce adı aynı olan panel başlıklarını, bulunamazsa eş anlamlısını (ör. Materyal ≈ Kumaş Tipi) ya da diğer pazaryerinde eşlendiği adı toplu eşler. Birden fazla aday varsa atlar." disabled={sync.isPending || bulkBusy !== ''} onClick={() => void matchHeadingsByName()}>{bulkBusy === 'HEADINGS' ? 'Başlıklar eşleniyor…' : 'Başlıkları Eşle'}</button>
      <button type="button" className="secondary" title="Eşlenmiş tüm başlıklarda adı aynı olan değerleri toplu eşler" disabled={sync.isPending || bulkBusy !== ''} onClick={() => void matchAllValuesByName()}>{bulkBusy === 'VALUES' ? 'Değerler eşleniyor…' : 'Tüm Değerleri Eşle'}</button>
      <button type="button" className="secondary" title={`Aynı ${platformLabel} başlığını kullanan diğer kategorilerde yaptığınız başlık ve değer eşlemelerini bu kategoriye getirir. Elle yaptığınız ve sildiğiniz eşlemelere dokunmaz.`} disabled={sync.isPending || bulkBusy !== ''} onClick={() => void fillFromOtherCategories()}>{bulkBusy === 'TEMPLATES' ? 'Getiriliyor…' : 'Kategorilerden Doldur'}</button>
      {(platformCode === 'TRENDYOL' || platformCode === 'HEPSIBURADA') && categoryScope && <button type="button" className="secondary" title={`Ürünlerde boş kalan alanlar için ${platformLabel}’a gönderilecek sabit değerler`} onClick={() => setExtraPanel('defaults')}>Platform Varsayılanları <span className="cm-count">{attributeDefaultsCount}</span></button>}
      <button type="button" className={`secondary${mappingConflictCount ? ' cm-has-conflicts' : ''}`} title={`Aynı panel değeri birden fazla ${platformLabel} değerine bağlanmış eşlemeler`} onClick={() => setExtraPanel('conflicts')}>Gözden Geçirilecek <span className="cm-count">{mappingConflictCount}</span></button>
      {platformCode === 'TRENDYOL' && optionCount > 0 && <button type="button" className="secondary" title="Trendyol’daki ürünleri salt-okunur okuyarak ürün seçeneklerini günceller; Trendyol’a yazma yapmaz." disabled={productSync.isPending} onClick={() => productSync.mutate()}>{productSync.isPending ? 'Ürünler eşitleniyor…' : 'Seçenekleri Eşitle'}</button>}
      <button type="button" className="secondary" title={`Bu kategorinin ${platformLabel} alan listesini salt-okunur olarak yeniden okur.`} disabled={sync.isPending} onClick={() => sync.mutate()}><UiIcon name="refresh" />{sync.isPending ? 'Güncelleniyor…' : 'Alanları Güncelle'}</button>
      </div>}
    </div>
    {dataLoading ? <Busy text="Kategori özellikleri ve eşlemeler yükleniyor…" /> : dataFailed ? <div className="unknown"><strong>Güncel kategori özellik verisi alınamadı</strong><p>Seçili {platformLabel} kategorisinin özellik listesini yeniden eşitleyin.</p><button type="button" disabled={sync.isPending} onClick={() => sync.mutate()}>{sync.isPending ? 'Kuyruğa alınıyor…' : 'Kategori özelliklerini eşitle'}</button></div> : <>
      <div className="cm-layout">
        <nav className="cm-nav" aria-label="Kategori özellikleri">
          {entryGroups.map(group => <Fragment key={group.title}>
            <button type="button" className={`cm-section${collapsedGroups.includes(group.title) ? ' is-collapsed' : ''}`} aria-expanded={!collapsedGroups.includes(group.title)} onClick={() => setCollapsedGroups(current => current.includes(group.title) ? current.filter(title => title !== group.title) : [...current, group.title])}><svg className="cm-section-chevron" viewBox="0 0 16 16" aria-hidden="true"><path d="m4.5 6.5 3.5 3.5 3.5-3.5" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" /></svg><span>{group.title}</span><span>{group.items.filter(entry => entry.state === 'done').length} / {group.items.length}</span></button>
            {!collapsedGroups.includes(group.title) && group.items.map(entry => <button type="button" key={entry.key} className={`cm-item${entry.key === activeEntry?.key ? ' is-selected' : ''}`} aria-current={entry.key === activeEntry?.key ? 'true' : undefined} onClick={() => setSelectedEntryKey(entry.key)}><MappingStateGlyph state={entry.state} required={entry.required} /><span className="cm-name">{entry.name}</span><span className="cm-meta">{entry.meta}</span></button>)}
          </Fragment>)}
        </nav>
        <section className="cm-detail" key={activeEntry?.key ?? 'empty'}>
          {!activeEntry ? <p className="cm-empty">Bu kategoride eşlenecek {platformLabel} alanı yok.</p>
            : activeEntry.remote ? <FieldDetail platformCode={platformCode} platformLabel={platformLabel} connectionId={connectionId} categoryScope={categoryScope} snapshotId={references.data!.snapshotId} localAttributes={activeEntry.localAttributes ?? productAttributes} categoryOptionIds={categoryOptionIds} remote={activeEntry.remote} mapping={activeEntry.mapping ?? null} role={activeEntry.role} required={activeEntry.required} footerHint={footerHint} onNext={goToNext} onNotice={onNotice} />
            : <>
              <FieldHeading name={activeEntry.name} required={activeEntry.required} platformLabel={platformLabel} />
              <div className="cm-legacy-card">{activeEntry.node}</div>
              <FieldFooter hint={footerHint} onNext={goToNext} />
            </>}
        </section>
      </div>
      <div className="cm-legend" aria-label="Durum simgeleri"><span><MappingStateGlyph state="done" /> Tamam</span><span><MappingStateGlyph state="partial" /> Değerleri Eksik</span><span><MappingStateGlyph state="none" /> Eşlenmedi</span></div>
    </>}
    <Modal open={extraPanel === 'defaults'} title="Platform Varsayılanları" description={`Ürünlerde boş kalan alanlar için ${platformLabel}’a gönderilecek sabit değerler.`} onClose={() => setExtraPanel('')} className="cm-extra-modal">
      {categoryScope && references.data && <AttributeDefaultsPanel connectionId={connectionId} categoryScope={categoryScope} platformLabel={platformLabel} remoteAttributes={references.data.items} onNotice={onNotice} expanded />}
    </Modal>
    <Modal open={extraPanel === 'conflicts'} title="Gözden Geçirilecek Eşlemeler" description={`Aynı panel değeri birden fazla ${platformLabel} değerine bağlanmış eşlemeler.`} onClose={() => setExtraPanel('')} className="cm-extra-modal">
      {mappingConflictCount ? <MappingConflictsPanel connectionId={connectionId} platformLabel={platformLabel} onNotice={onNotice} expanded /> : <p className="cm-empty">Gözden geçirilecek eşleme yok.</p>}
    </Modal>
  </article>
}

type FieldState = 'done' | 'partial' | 'none'
type MappingEntry = { key: string; name: string; required: boolean; state: FieldState; meta: string; node?: ReactNode; remote?: ReferenceItem; mapping?: CatalogMapping | null; localAttributes?: LocalAttribute[]; role?: 'ATTRIBUTE' | 'OPTION' }

// Status is read from the shape: a filled check when ready, a clock when values are still missing,
// an outlined plus when the field has no panel attribute yet.
function MappingStateGlyph({ state, required = false }: { state: FieldState; required?: boolean }) {
  if (state === 'done') return <svg className="cm-glyph is-done" viewBox="0 0 16 16" aria-label="Tamam" role="img"><circle cx="8" cy="8" r="7" fill="currentColor" /><path d="m5 8.2 2.1 2.1 3.9-4.2" fill="none" stroke="var(--rv-color-surface)" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" /></svg>
  if (state === 'partial') return <svg className="cm-glyph is-partial" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" aria-label="Değerleri eksik" role="img"><circle cx="8" cy="8" r="6.2" opacity=".35" /><path d="M8 1.8a6.2 6.2 0 0 1 5.9 4.3" /><path d="M8 5.6v2.6l1.8 1.1" /></svg>
  return <svg className={`cm-glyph${required ? ' is-required' : ''}`} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" aria-label="Eşlenmedi" role="img"><circle cx="8" cy="8" r="6.2" strokeDasharray="2.6 2.2" /></svg>
}

function FieldHeading({ name, required }: { name: string; required: boolean; platformLabel?: string }) {
  return <>
    <div className="cm-detail-head"><h4>{name}</h4><span className={`cm-tag${required ? ' is-required' : ''}`}>{required ? 'Zorunlu' : 'İsteğe bağlı'}</span></div>
  </>
}

function FieldFooter({ hint, dirty = false, saving = false, onSave, onNext }: { hint: string; dirty?: boolean; saving?: boolean; onSave?: () => void; onNext?: () => void }) {
  return <div className={`cm-foot${dirty ? ' is-dirty' : ''}`}>
    <span>{dirty ? 'Kaydedilmemiş değişiklikler var' : hint}</span>
    <span className="cm-grow" />
    {onNext && <button type="button" className="secondary" onClick={onNext}>Sonraki Eksik</button>}
    <button type="button" disabled={!onSave || !dirty || saving} onClick={onSave}>{saving ? 'Kaydediliyor…' : 'Kaydet'}</button>
  </div>
}

// One marketplace field: which panel attribute feeds it, then a table of the attribute's values.
function FieldDetail({ platformCode, platformLabel, connectionId, categoryScope, snapshotId, localAttributes, categoryOptionIds, remote, mapping, role: fixedRole, required, footerHint, onNext, onNotice }: { platformCode: MappingPlatformCode; platformLabel: string; connectionId: string; categoryScope: string; snapshotId: string; localAttributes: LocalAttribute[]; categoryOptionIds: Set<string>; remote: ReferenceItem; mapping: CatalogMapping | null; role?: 'ATTRIBUTE' | 'OPTION'; required: boolean; footerHint: string; onNext?: () => void; onNotice: (value: string) => void }) {
  const client = useQueryClient()
  const [busy, setBusy] = useState(false)
  const selectable = localAttributes.filter(item => item.isActive !== false)
  const panelOptions = selectable.filter(item => categoryOptionIds.has(item.id) || isOptionAttribute(item))
  const panelAttributes = selectable.filter(item => !categoryOptionIds.has(item.id) && !isOptionAttribute(item))
  const selected = mapping ? localAttributes.find(item => item.id === mapping.localId) : undefined
  const values = selected?.values.filter(value => value.isActive) ?? []
  const customPanelColorValues = platformCode === 'TRENDYOL' && usesCustomPanelColorValue(remote.name)
  async function choose(localId: string) {
    if (busy || (mapping?.localId ?? '') === localId) return
    setBusy(true)
    try {
      if (mapping) await hubApi<boolean>(`/mappings/attributes/${mapping.localId}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}&externalId=${encodeURIComponent(mapping.externalId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${mapping.version}"` } })
      if (localId) {
        const chosen = localAttributes.find(item => item.id === localId)
        const role = fixedRole ?? (categoryOptionIds.has(localId) || (chosen && isOptionAttribute(chosen)) ? 'OPTION' : 'ATTRIBUTE')
        await hubApi<CatalogMapping>(`/mappings/attributes/${localId}`, { method: 'PUT', body: JSON.stringify({ connectionId, snapshotId, scopeExternalId: categoryScope, externalId: remote.externalId, status: 'VERIFIED', role }) })
      }
      onNotice(localId ? `${remote.name} eşlemesi kaydedildi.` : `${remote.name} eşlemesi kaldırıldı.`)
    } catch (reason) {
      onNotice(reason instanceof Error ? reason.message : 'Özellik eşlemesi kaydedilemedi.')
    } finally {
      await client.invalidateQueries({ queryKey: ['attribute-mappings', connectionId, categoryScope] })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      setBusy(false)
    }
  }
  return <>
    <FieldHeading name={remote.name} required={required} platformLabel={platformLabel} />
    <div className="cm-form">
      <div className="cm-field"><small>{platformLabel} Alanı</small><span className="cm-readonly">{remote.name}</span></div>
      <span className="cm-link" aria-hidden="true"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round"><path d="M6.6 9.4 9.4 6.6" /><path d="M7.4 4.6 8.6 3.4a2.6 2.6 0 0 1 3.7 3.7l-1.2 1.2" /><path d="M8.6 11.4 7.4 12.6a2.6 2.6 0 0 1-3.7-3.7l1.2-1.2" /></svg></span>
      <div className="cm-field"><small>Panel Özelliği</small><div className="cm-select-row">
        <select aria-label={`${remote.name} için panel özelliği`} value={mapping?.localId ?? ''} disabled={busy} onChange={event => void choose(event.target.value)}>
          <option value="">Seçin…</option>
          {panelOptions.length > 0 && <optgroup label="Panel seçenekleri">{panelOptions.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</optgroup>}
          {panelAttributes.length > 0 && <optgroup label="Panel özellikleri">{panelAttributes.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</optgroup>}
        </select>
      </div></div>
      <button type="button" className="secondary cm-quiet cm-unlink" disabled={!mapping || busy} onClick={() => void choose('')}>Kaldır</button>
    </div>
    {!mapping || !selected ? <><p className="cm-empty">Bir panel özelliği seçtiğinizde değerleri burada eşlersiniz.</p><FieldFooter hint={footerHint} onNext={onNext} /></>
      : !values.length ? <><p className="cm-empty">“{selected.name}” özelliğinde henüz değer yok. Değerleri özellik kütüphanesinden ekleyin.</p><FieldFooter hint={footerHint} onNext={onNext} /></>
      : customPanelColorValues || remote.allowsCustomValue === true ? <><div className="cm-legacy-card"><AttributeValueMappingEditor connectionId={connectionId} categoryScope={categoryScope} attribute={selected} externalAttributeId={remote.externalId} platformLabel={platformLabel} customPanelColorValues={customPanelColorValues} marketplaceAllowsFreeText={remote.allowsCustomValue === true} required={required} /></div><FieldFooter hint={footerHint} onNext={onNext} /></>
      : <FieldValueTable key={`${categoryScope}/${remote.externalId}/${selected.id}`} platformCode={platformCode} platformLabel={platformLabel} connectionId={connectionId} categoryScope={categoryScope} attribute={selected} remote={remote} required={required} footerHint={footerHint} onNext={onNext} onNotice={onNotice} />}
  </>
}

function groupValueSelections(items: readonly CatalogMapping[]) {
  return items.reduce<Record<string, string[]>>((current, item) => ({ ...current, [item.externalId]: [...(current[item.externalId] ?? []), item.localId] }), {})
}

// Panel values down the side, the marketplace value each one is sent as on the right.
function FieldValueTable({ platformCode, platformLabel, connectionId, categoryScope, attribute, remote, required, footerHint, onNext, onNotice }: { platformCode: MappingPlatformCode; platformLabel: string; connectionId: string; categoryScope: string; attribute: LocalAttribute; remote: ReferenceItem; required: boolean; footerHint: string; onNext?: () => void; onNotice: (value: string) => void }) {
  const client = useQueryClient()
  const valueScope = `${categoryScope}/${remote.externalId}`
  const references = useQuery({ queryKey: ['reference-attribute-values', connectionId, valueScope], queryFn: () => hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(remote.externalId)}/values?connectionId=${encodeURIComponent(connectionId)}`), retry: false })
  const mappings = useQuery({ queryKey: ['attribute-value-mappings', connectionId, valueScope], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(valueScope)}`), retry: false })
  const [selections, setSelections] = useState<Record<string, string[]> | null>(null)
  const [search, setSearch] = useState('')
  const [onlyMissing, setOnlyMissing] = useState(false)
  const [saving, setSaving] = useState(false)
  useEffect(() => { if (mappings.data && selections === null) setSelections(groupValueSelections(mappings.data)) }, [mappings.data, selections])
  // A Hepsiburada value may reuse a panel value that already feeds another Hepsiburada value.
  const allowReuse = platformCode === 'HEPSIBURADA'
  if (references.isError) return <><div className="cm-legacy-card"><AttributeValueMappingEditor connectionId={connectionId} categoryScope={categoryScope} attribute={attribute} externalAttributeId={remote.externalId} platformLabel={platformLabel} autoSyncMissingValues={platformCode === 'HEPSIBURADA' && remote.allowsCustomValue === false} allowReusablePanelValues={allowReuse} required={required} /></div><FieldFooter hint={footerHint} onNext={onNext} /></>
  if (mappings.isError) return <ErrorBox error={mappings.error} onRetry={() => void mappings.refetch()} />
  if (references.isLoading || mappings.isLoading || !selections || !references.data || !mappings.data) return <Busy text="Değerler ve eşlemeler yükleniyor…" />
  const snapshotId = references.data.snapshotId
  const saved = groupValueSelections(mappings.data)
  const localValues = attribute.values.filter(item => item.isActive).slice().sort((left, right) => left.value.localeCompare(right.value, 'tr-TR', { sensitivity: 'base', numeric: true }))
  const mappedExternalIds = new Set(mappings.data.map(item => item.externalId))
  const remoteValues = references.data.items.filter(item => item.isActive || mappedExternalIds.has(item.externalId)).slice().sort((left, right) => left.name.localeCompare(right.name, 'tr-TR', { sensitivity: 'base', numeric: true }))
  const knownExternalIds = new Set(remoteValues.map(item => item.externalId))
  const targetOf = (current: Record<string, string[]>, localId: string) => Object.keys(current).find(externalId => current[externalId].includes(localId)) ?? ''
  const sameIds = (left: readonly string[], right: readonly string[]) => left.length === right.length && left.every(id => right.includes(id))
  const dirty = [...new Set([...Object.keys(saved), ...Object.keys(selections)])].some(externalId => !sameIds(selections[externalId] ?? [], saved[externalId] ?? []))
  const doneCount = localValues.filter(item => targetOf(selections, item.id)).length
  const query = search.trim().toLocaleLowerCase('tr-TR')
  const rows = localValues.filter(item => (!query || item.value.toLocaleLowerCase('tr-TR').includes(query)) && (!onlyMissing || !targetOf(selections, item.id)))
  function setTarget(localId: string, externalId: string) {
    setSelections(current => {
      const base = current ?? {}
      const previous = targetOf(base, localId)
      const next: Record<string, string[]> = {}
      for (const [key, ids] of Object.entries(base)) {
        const kept = allowReuse && key !== previous ? ids : ids.filter(id => id !== localId)
        if (kept.length) next[key] = kept
      }
      if (externalId) next[externalId] = [...(next[externalId] ?? []).filter(id => id !== localId), localId]
      return next
    })
  }
  function matchSameNames() {
    const plan = planDirectReferenceValues(localValues, references.data!.items.filter(item => item.isActive))
    const open = plan.mappings.filter(item => !targetOf(selections!, item.localId))
    if (open.length) setSelections(current => open.reduce((next, item) => ({ ...next, [item.externalId]: [...(next[item.externalId] ?? []).filter(id => id !== item.localId), item.localId] }), current ?? {}))
    onNotice(open.length ? `${open.length} değer aynı adlı ${platformLabel} değeriyle eşlendi; kalıcı olması için Kaydet’e basın.` : 'Yeni birebir eşleşen değer bulunamadı.')
  }
  async function save() {
    if (!selections || saving) return
    setSaving(true)
    let changed = 0
    try {
      for (const [externalId, ids] of Object.entries(saved)) {
        const wanted = selections[externalId] ?? []
        for (const localId of ids) {
          if (wanted.includes(localId)) continue
          const existing = mappings.data!.find(item => item.externalId === externalId && item.localId === localId)
          if (!existing) continue
          await hubApi<boolean>(`/mappings/attribute-values/${localId}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(valueScope)}&externalId=${encodeURIComponent(externalId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${existing.version}"` } })
          changed++
        }
      }
      for (const planned of planReferencePanelMappings(selections)) {
        if (!knownExternalIds.has(planned.externalId)) continue
        const existing = mappings.data!.find(item => item.externalId === planned.externalId && item.localId === planned.localId)
        if (!attributeValueMappingNeedsSave(existing, planned.localId, planned.externalId, snapshotId)) continue
        await hubApi<CatalogMapping>(`/mappings/attribute-values/${planned.localId}`, { method: 'PUT', headers: existing ? { 'If-Match': `"v${existing.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId, scopeExternalId: valueScope, externalId: planned.externalId, status: 'VERIFIED', updateTemplate: true }) })
        changed++
      }
      onNotice(changed ? `${remote.name}: ${changed} değer eşlemesi kaydedildi.` : 'Değer eşlemelerinde değişiklik yok.')
    } catch (reason) {
      onNotice(reason instanceof Error ? reason.message : 'Değer eşlemeleri kaydedilemedi.')
    } finally {
      await client.invalidateQueries({ queryKey: ['attribute-value-mappings', connectionId, valueScope] })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      setSelections(null)
      setSaving(false)
    }
  }
  return <>
    <div className="cm-vtools">
      <div className="cm-vtools-title"><h5>Değer Eşlemeleri</h5><span>{doneCount} / {localValues.length}</span></div>
      <div className="cm-vtools-actions">
        <input className="cm-search" type="search" value={search} onChange={event => setSearch(event.target.value)} placeholder="Değer ara" aria-label="Panel değeri ara" />
        <button type="button" className={`secondary cm-toggle${onlyMissing ? ' is-on' : ''}`} aria-pressed={onlyMissing} onClick={() => setOnlyMissing(value => !value)}>Yalnızca Eksikler</button>
        <button type="button" className="secondary" onClick={matchSameNames}>Birebir Eşle</button>
      </div>
    </div>
    <div className="cm-vtable" role="table" aria-label={`${remote.name} değer eşlemeleri`}>
      <div className="cm-vrow is-head" role="row"><span role="columnheader">Panel Değeri</span><span role="columnheader">{platformLabel} Değeri</span><span aria-hidden="true" /></div>
      <div className="cm-vscroll">
        {rows.length ? rows.map(item => {
          const target = targetOf(selections, item.id)
          return <div className={`cm-vrow${target ? '' : ' is-open'}`} role="row" key={item.id}>
            <span className="cm-pv" role="cell">{item.value}</span>
            <span role="cell"><select aria-label={`${item.value} için ${platformLabel} değeri`} value={target} onChange={event => setTarget(item.id, event.target.value)}><option value="">Seçin…</option>{remoteValues.map(option => <option key={option.externalId} value={option.externalId}>{option.name}</option>)}</select></span>
            <span className="cm-vstate" role="cell"><MappingStateGlyph state={target ? 'done' : 'none'} /></span>
          </div>
        }) : <p className="cm-empty">{onlyMissing ? 'Eşlenmemiş değer kalmadı.' : 'Aramaya uyan değer yok.'}</p>}
      </div>
    </div>
    <FieldFooter hint={footerHint} dirty={dirty} saving={saving} onSave={() => void save()} onNext={onNext} />
  </>
}


function MappingValueModal({ open, title, description, onClose, children }: { open: boolean; title: string; description: string; onClose: () => void; children: ReactNode }) {
  const displayedDescription = description.startsWith('Panel değerlerini mevcut Trendyol seçeneklerine eşleyin.')
    ? 'Trendyol değerlerini karşılık gelen panel değerleriyle eşleyin. Trendyol’a yeni değer gönderilmez.'
    : description
  return <Modal open={open} title={title} description={displayedDescription} onClose={onClose} className="mapping-values-modal">{children}</Modal>
}

export function OptionMappingCard({ connectionId, categoryScope, snapshotId, localAttribute, remoteAttributes, existingMapping, onNotice }: { connectionId: string; categoryScope: string; snapshotId: string; localAttribute: LocalAttribute; remoteAttributes: ReferenceItem[]; existingMapping: CatalogMapping | null; onNotice: (value: string) => void }) {
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
    try { await hubApi<boolean>(`/mappings/attributes/${localAttribute.id}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}&externalId=${encodeURIComponent(existingMapping.externalId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${existingMapping.version}"` } }); setExternalId(''); onNotice(`${localAttribute.name} seçenek eşlemesi kaldırıldı.`); await client.invalidateQueries({ queryKey: ['attribute-mappings', connectionId, categoryScope] }) } catch (reason) { onNotice(reason instanceof Error ? reason.message : 'Seçenek eşlemesi kaldırılamadı.') }
  }
  const mapped = !!existingMapping && existingMapping.externalId === externalId
  const isColorOption = isPanelColorAttributeName(localAttribute.name)
  const customPanelColorValues = usesCustomPanelColorValue(selectedRemote?.name ?? '')
  const cardTitle = isColorOption ? 'Renk' : `Panel: ${localAttribute.name}`
  const cardDescription = mapped
    ? (isColorOption ? 'Trendyol’dan çekilen düz renk · Kaynak: Panel Renk' : `Trendyol: ${selectedRemote?.name ?? existingMapping?.externalId}`)
    : (isColorOption ? 'Trendyol’dan çekilen düz renk alanı' : 'Varyant üreten panel seçeneği')
  return <section className={`attribute-mapping-card option-mapping-card ${mapped ? '' : 'unmapped'}`}><div className="attribute-mapping-card-head"><span className="attr-source">OPT</span><div><strong>{cardTitle}</strong><small>{cardDescription}</small></div></div><div className="mapping-fields compact"><label>Trendyol seçenek/özellik karşılığı<select aria-label={`${localAttribute.name} Trendyol seçenek karşılığı`} value={externalId} onChange={event => setExternalId(event.target.value)}><option value="">Karşılık seçin</option>{remoteAttributes.map(item => <option key={item.externalId} value={item.externalId}>{item.name}{item.isRequired ? ' · zorunlu' : ''}</option>)}</select></label>{mapped ? <button type="button" className="secondary" onClick={() => void remove()}>Eşlemeyi kaldır</button> : <button type="button" onClick={() => void save()} disabled={!externalId}>Kaydet</button>}</div>{mapped && localAttribute.values.length > 0 && <><button type="button" className="value-mapping-toggle" onClick={() => setShowValues(value => !value)}>{showValues ? 'Seçenek değerleri açık' : customPanelColorValues ? `Gönderilecek renkleri gör (${localAttribute.values.length})` : `Seçenek değerlerini eşle (${localAttribute.values.length})`}</button><MappingValueModal open={showValues && Boolean(selectedRemote)} title={customPanelColorValues ? 'Trendyol’a gönderilecek panel renkleri' : `${localAttribute.name} değer eşlemeleri`} description={customPanelColorValues ? 'Normal Renk alanı sabit değer listesi kullanmaz; panel renkleri baş harfleri büyük biçimde gönderilir.' : 'Panel değerlerini mevcut Trendyol seçeneklerine eşleyin. Yeni değer Trendyol’a gönderilmez.'} onClose={() => setShowValues(false)}>{selectedRemote && <AttributeValueMappingEditor connectionId={connectionId} categoryScope={categoryScope} attribute={localAttribute} externalAttributeId={selectedRemote.externalId} customPanelColorValues={customPanelColorValues} required={selectedRemote.isRequired === true} />}</MappingValueModal></>}</section>
}

export function WebColorMappingCard({ connectionId, categoryScope, snapshotId, localAttribute, localAttributes, categoryOptionIds, remoteAttribute, existingMapping, existingMappings, onNotice }: { connectionId: string; categoryScope: string; snapshotId: string; localAttribute: LocalAttribute; localAttributes: LocalAttribute[]; categoryOptionIds: Set<string>; remoteAttribute: ReferenceItem; existingMapping: CatalogMapping | null; existingMappings: CatalogMapping[]; onNotice: (value: string) => void }) {
  const client = useQueryClient(); const [localId, setLocalId] = useState(existingMapping?.localId ?? localAttribute.id); const [showValues, setShowValues] = useState(false)
  useEffect(() => { setLocalId(existingMapping?.localId ?? localAttribute.id) }, [existingMapping?.localId, localAttribute.id])
  const selectableAttributes = [localAttribute, ...localAttributes].filter((attribute, index, items) => attribute.isActive !== false && items.findIndex(candidate => candidate.id === attribute.id) === index)
  void showValues; void setShowValues; void remove; void categoryOptionIds
  const selectedAttribute = selectableAttributes.find(attribute => attribute.id === localId) ?? localAttribute
  const currentMapping = existingMappings.find(item => item.localId === localId && item.externalId === remoteAttribute.externalId) ?? (existingMapping?.localId === localId ? existingMapping : null)
  const mapped = currentMapping?.externalId === remoteAttribute.externalId
  async function save() {
    try {
      if (!localId) throw new Error('Web Color için panel özelliğini seçin.')
      await hubApi<CatalogMapping>(`/mappings/attributes/${localId}`, { method: 'PUT', headers: currentMapping ? { 'If-Match': `"v${currentMapping.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId, scopeExternalId: categoryScope, externalId: remoteAttribute.externalId, status: 'VERIFIED', role: 'ATTRIBUTE' }) })
      onNotice(`${selectedAttribute.name} → Trendyol Web Color eşlemesi kaydedildi.`); setShowValues(true); await client.invalidateQueries({ queryKey: ['attribute-mappings', connectionId, categoryScope] })
    } catch (reason) { onNotice(reason instanceof Error ? reason.message : 'Web Color eşlemesi kaydedilemedi.') }
  }
  async function remove() {
    if (!currentMapping) return
    try { await hubApi<boolean>(`/mappings/attributes/${currentMapping.localId}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}&externalId=${encodeURIComponent(currentMapping.externalId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${currentMapping.version}"` } }); setShowValues(false); onNotice('Panel Renk → Trendyol Web Color eşlemesi kaldırıldı.'); await client.invalidateQueries({ queryKey: ['attribute-mappings', connectionId, categoryScope] }) } catch (reason) { onNotice(reason instanceof Error ? reason.message : 'Web Color eşlemesi kaldırılamadı.') }
  }
  return <>
    <div className="cm-form">
      <div className="cm-field"><small>Trendyol Alanı</small><span className="cm-readonly">Web Color</span></div>
      <span className="cm-link" aria-hidden="true"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round"><path d="M6.6 9.4 9.4 6.6" /><path d="M7.4 4.6 8.6 3.4a2.6 2.6 0 0 1 3.7 3.7l-1.2 1.2" /><path d="M8.6 11.4 7.4 12.6a2.6 2.6 0 0 1-3.7-3.7l1.2-1.2" /></svg></span>
      <div className="cm-field"><small>Panel Özelliği</small><span className="cm-readonly">{selectedAttribute.name}</span></div>
      {!mapped && <button type="button" className="cm-webcolor-link" onClick={() => void save()}>Bağla</button>}
    </div>
    {mapped ? (selectedAttribute.values.length > 0 ? <div className="cm-webcolor"><WebColorValueMappingEditor connectionId={connectionId} categoryScope={categoryScope} attribute={selectedAttribute} externalAttributeId={remoteAttribute.externalId} /></div> : <p className="cm-empty">Panel {selectedAttribute.name} özelliğinde henüz değer yok.</p>)
      : <p className="cm-empty">Web Color, panel {selectedAttribute.name} değerlerinden gönderilir. Değer eşlemek için önce bağlayın.</p>}
  </>
}

export function AttributeMappingPage() {
  const client = useQueryClient()
  const [selectedPlatformCode, setSelectedPlatformCode] = useState<MappingPlatformCode>('TRENDYOL')
  const [connectionId, setConnectionId] = useState('')
  const [categoryId, setCategoryId] = useState('')
  const [localId, setLocalId] = useState('')
  const [externalId, setExternalId] = useState('')
  const [notice, setNotice] = useState('')
  useEffect(() => {
    if (!notice) return
    const kind = notificationKindFromMessage(notice)
    appendNotification(notice, kind)
    document.documentElement.dataset.rvMappingFeedback = kind
    const timeout = window.setTimeout(() => setNotice(''), kind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice])
  const connections = useQuery({ queryKey: ['connections', 'attribute-mapping'], queryFn: () => loadAllPages<Connection>('/connections') })
  const autoSelectedMappingPlatform = useRef(false)
  useEffect(() => {
    if (!connections.data || autoSelectedMappingPlatform.current) return
    autoSelectedMappingPlatform.current = true
    const items = connections.data.items
    const selectedConnections = activeMappingConnectionsForPlatform(items, selectedPlatformCode)
    const platform = selectedConnections.length
      ? mappingPlatformDefinitions.find(item => item.code === selectedPlatformCode)
      : supportedAttributeMappingPlatforms.find(item => activeMappingConnectionsForPlatform(items, item.code).length > 0)
    if (!platform) return
    const platformConnections = activeMappingConnectionsForPlatform(items, platform.code)
    setSelectedPlatformCode(platform.code)
    if (platformConnections.length) setConnectionId(platformConnections[0].id)
  }, [connections.data, selectedPlatformCode])
  const categories = useQuery({ queryKey: ['categories', 'attribute-mapping'], queryFn: () => loadAllPages<LocalCategory>('/catalog/categories') })
  const localAttributes = useQuery({ queryKey: ['attributes', 'mapping'], queryFn: () => loadAllPages<LocalAttribute>('/catalog/attributes') })
  const categoryRequirements = useQuery({ queryKey: ['category-requirements', categoryId, 'attribute-mapping'], queryFn: () => hubApi<CategoryRequirementView[]>(`/catalog/categories/${categoryId}/attribute-requirements`), enabled: !!categoryId })
  const categoryMapping = useQuery({ queryKey: ['category-mapping', categoryId, connectionId], queryFn: () => hubApi<CatalogMapping | null>(`/mappings/categories/${categoryId}?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!categoryId && !!connectionId, retry: false })
  const categoryScope = categoryMapping.data?.externalId ?? ''
  const references = useQuery({ queryKey: ['reference-attributes', connectionId, categoryScope], queryFn: () => hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId && !!categoryScope, retry: false })
  const mapping = useQuery({ queryKey: ['attribute-mapping', localId, connectionId, categoryScope], queryFn: () => hubApi<CatalogMapping | null>(`/mappings/attributes/${localId}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(categoryScope)}`), enabled: !!localId && !!connectionId && !!categoryScope, retry: false })
  const sync = useMutation({
    mutationFn: () => hubApi(`/connections/${connectionId}/reference-sync-jobs?resourceType=CATEGORY_ATTRIBUTES&parentExternalId=${encodeURIComponent(categoryScope)}`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: '{}' }),
    onSuccess: async () => {
      setNotice(`${mappingPlatformLabel(selectedPlatformCode)} kategori özellikleri salt-okunur eşitleme kuyruğuna alındı.`)
      for (const delay of [500, 1000, 2000, 4000]) {
        await wait(delay)
        await client.invalidateQueries({ queryKey: ['reference-attributes', connectionId, categoryScope] })
      }
    },
    onError: reason => setNotice(reason instanceof Error ? reason.message : 'Özellik eşitleme başlatılamadı.')
  })
  const save = useMutation({
    mutationFn: () => {
      if (!references.data || !localId || !externalId) throw new Error(`Bağlantı, kategori, panel özelliği ve ${mappingPlatformLabel(selectedPlatformCode)} özelliği zorunludur.`)
      if (categoryRequirements.isLoading) throw new Error('Panel kategori özelliği rolü yükleniyor; eşlemeyi tekrar deneyin.')
      const localAttribute = localAttributes.data?.items.find(item => item.id === localId)
      const role = resolveAttributeMappingRole(categoryRequirements.data?.find(item => item.attributeId === localId)?.role, localAttribute ? isOptionAttribute(localAttribute) : false)
      return hubApi<CatalogMapping>(`/mappings/attributes/${localId}`, { method: 'PUT', headers: mapping.data ? { 'If-Match': `"v${mapping.data.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId: references.data.snapshotId, scopeExternalId: categoryScope, externalId, status: 'VERIFIED', role }) })
    },
    onSuccess: async value => {
      setNotice('Özellik eşlemesi doğrulandı ve kategori kapsamında kaydedildi.')
      setExternalId(value.externalId)
      await client.invalidateQueries({ queryKey: ['attribute-mapping', localId, connectionId, categoryScope] })
    },
    onError: reason => setNotice(reason instanceof Error ? reason.message : 'Özellik eşlemesi kaydedilemedi.')
  })
  useEffect(() => { if (mapping.data) setExternalId(mapping.data.externalId) }, [mapping.data])
  const activeConnections = activeMappingConnectionsForPlatform(connections.data?.items ?? [], selectedPlatformCode)
  const selectedPlatformLabel = mappingPlatformLabel(selectedPlatformCode)
  const localLeaves = categories.data?.items.filter(item => item.isActive && item.isLeaf) ?? []
  const activeAttributes = localAttributes.data?.items.filter(item => item.isActive) ?? []
  const remoteAttributes = references.data?.items.filter(item => item.isActive) ?? []
  function selectPlatform(platformCode: MappingPlatformCode) {
    setSelectedPlatformCode(platformCode)
    setConnectionId('')
    setCategoryId('')
    setLocalId('')
    setExternalId('')
    setNotice('')
  }
  return <section className="content f3 mapping-page">
    <div className="page-heading"><div><p className="eyebrow">Eşleştirme ayarları</p><h1>Özellik eşlemeleri</h1></div><Badge value="SAFE READ" /></div>
    {notice && <MessageToast message={notice} onClose={() => setNotice('')} />}
    <div className="button-row"><Link className="button-link secondary" to="/mappings/categories">Kategoriler</Link><Link className="button-link secondary" to="/mappings/categories?view=brands">Markalar</Link><Link className="button-link" to="/mappings/attributes">Özellikler</Link></div>
    <div className="mapping-category-platform-tabs mapping-platform-tabs rv-tabs rv-view-tabs" role="tablist" aria-label="Özellik eşleştirme pazaryeri">
      {supportedAttributeMappingPlatforms.map(platform => <button type="button" role="tab" aria-selected={selectedPlatformCode === platform.code} className={`${selectedPlatformCode === platform.code ? 'active is-active' : ''}${activeMappingConnectionsForPlatform(connections.data?.items ?? [], platform.code).length ? '' : ' is-unavailable'}`} title={activeMappingConnectionsForPlatform(connections.data?.items ?? [], platform.code).length ? `${platform.label} özelliği eşlemelerini göster` : `${platform.label} için aktif bağlantı yok`} key={platform.code} onClick={() => selectPlatform(platform.code)}><span className="rv-tab-label">{platformLogoSource(platform.code) && <img className="rv-view-tab-logo" src={platformLogoSource(platform.code)!} alt="" aria-hidden="true" />}{platform.label}</span></button>)}
    </div>
    <article className="panel mapping-step">
      <div className="editor-section-title"><span>1</span><div><h2>Kategori kapsamını seçin</h2><p>Özellik kimlikleri seçili ve doğrulanmış {selectedPlatformLabel} yaprak kategorisi kapsamında tutulur.</p></div></div>
      <div className="mapping-fields">
        <label>{selectedPlatformLabel} için aktif bağlantı<select aria-label={`${selectedPlatformLabel} için aktif bağlantı`} value={connectionId} onChange={event => { setConnectionId(event.target.value); setCategoryId(''); setLocalId(''); setExternalId(''); setNotice('') }}><option value="">Bağlantı seçin</option>{activeConnections.map(item => <option value={item.id} key={item.id}>{item.displayName} · {item.externalStoreId}</option>)}</select></label>
        <label>Panel yaprak kategorisi<select aria-label="Özellik kapsamı panel kategorisi" value={categoryId} onChange={event => { setCategoryId(event.target.value); setLocalId(''); setExternalId(''); setNotice('') }} disabled={!connectionId}><option value="">Kategori seçin</option>{localLeaves.map(item => <option value={item.id} key={item.id}>{item.path}</option>)}</select></label>
      </div>
      {!activeConnections.length ? <div className="unknown"><strong>{selectedPlatformLabel} bağlantısı gerekli</strong><p>Özellik referanslarını eşleyebilmek için önce aktif bir bağlantı kurun.</p><Link className="button-link" to="/integrations">Entegrasyonlara git</Link></div> : categoryId && !categoryMapping.isLoading && !categoryMapping.data ? <div className="unknown"><strong>Kategori eşlemesi gerekli</strong><p>Özellikleri almadan önce panel kategorisini {selectedPlatformLabel} yaprak kategorisiyle eşleyin.</p><Link className="button-link" to="/mappings/categories">Kategori eşlemelerine git</Link></div> : categoryScope && references.isError ? <div className="unknown"><strong>Güncel özellik snapshot’ı yok</strong><p>Seçili kategori için yalnız salt-okunur özellik listesini eşitleyin.</p><button type="button" disabled={sync.isPending} onClick={() => sync.mutate()}>{sync.isPending ? 'Kuyruğa alınıyor…' : 'Kategori özelliklerini eşitle'}</button></div> : null}
    </article>
    {references.data && <article className="panel mapping-step">
      <div className="editor-section-title"><span>2</span><div><h2>Özelliği eşleyin</h2><p>Bu işlem yalnız yerel eşleme kaydı oluşturur; pazar yerine yazmaz.</p></div></div>
      <div className="mapping-fields">
        <label>Panel özelliği<select aria-label="Panel özelliği" value={localId} onChange={event => { setLocalId(event.target.value); setExternalId(''); setNotice('') }}><option value="">Özellik seçin</option>{activeAttributes.map(item => <option value={item.id} key={item.id}>{item.name} · {item.dataType}</option>)}</select></label>
        <label>{selectedPlatformLabel} kategori özelliği<select aria-label={`${selectedPlatformLabel} kategori özelliği`} value={externalId} onChange={event => setExternalId(event.target.value)} disabled={!localId || mapping.isLoading}><option value="">Özellik seçin</option>{remoteAttributes.map(item => <option value={item.externalId} key={item.externalId}>{item.name}{item.isRequired ? ' · zorunlu' : ''}{item.allowsCustomValue ? ' · serbest değer' : ''}</option>)}</select></label>
      </div>
      <div className="mapping-action"><span>{remoteAttributes.length.toLocaleString('tr-TR')} özellik · kategori {categoryScope}{mapping.data ? ` · mevcut eşleme v${mapping.data.version}` : ''}</span><button type="button" disabled={!localId || !externalId || save.isPending || mapping.isLoading || categoryRequirements.isLoading} onClick={() => save.mutate()}>{save.isPending ? 'Kaydediliyor…' : mapping.data ? 'Eşlemeyi güncelle' : 'Eşlemeyi doğrula ve kaydet'}</button></div>
      {mapping.data && localId && <AttributeValueMappingEditor connectionId={connectionId} categoryScope={categoryScope} attribute={activeAttributes.find(item => item.id === localId)!} externalAttributeId={mapping.data.externalId} platformLabel={selectedPlatformLabel} customPanelColorValues={selectedPlatformCode === 'TRENDYOL' && usesCustomPanelColorValue(remoteAttributes.find(item => item.externalId === mapping.data!.externalId)?.name ?? '')} marketplaceAllowsFreeText={remoteAttributes.find(item => item.externalId === mapping.data!.externalId)?.allowsCustomValue === true} autoSyncMissingValues={selectedPlatformCode === 'HEPSIBURADA' && remoteAttributes.find(item => item.externalId === mapping.data!.externalId)?.allowsCustomValue === false} allowReusablePanelValues={selectedPlatformCode === 'HEPSIBURADA'} required={remoteAttributes.find(item => item.externalId === mapping.data!.externalId)?.isRequired === true} />}
    </article>}
  </section>
}
function WebColorValueMappingEditor({ connectionId, categoryScope, attribute, externalAttributeId }: { connectionId: string; categoryScope: string; attribute: LocalAttribute; externalAttributeId: string }) {
  const client = useQueryClient(); const [notice, setNotice] = useState(''); const [selections, setSelections] = useState<Record<string, string>>({}); const [selectedRemoteId, setSelectedRemoteId] = useState(''); const [saving, setSaving] = useState(false); const [showGroups, setShowGroups] = useState(false); const valueScope = `${categoryScope}/${externalAttributeId}`
  const references = useQuery({ queryKey: ['reference-attribute-values', connectionId, valueScope], queryFn: () => hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(externalAttributeId)}/values?connectionId=${encodeURIComponent(connectionId)}`), retry: false })
  const mappings = useQuery({ queryKey: ['attribute-value-mappings', connectionId, valueScope], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(valueScope)}`), retry: false })
  const sync = useMutation({ mutationFn: () => hubApi(`/connections/${connectionId}/reference-sync-jobs?resourceType=ATTRIBUTE_VALUES&parentExternalId=${encodeURIComponent(valueScope)}`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: '{}' }), onSuccess: () => setNotice('Web Color değerleri salt-okunur eşitleme kuyruğuna alındı.'), onError: reason => setNotice(reason instanceof Error ? reason.message : 'Web Color değerleri eşitlenemedi.') })
  useEffect(() => {
    if (!notice) return
    const kind = notificationKindFromMessage(notice)
    appendNotification(notice, kind)
    document.documentElement.dataset.rvMappingFeedback = kind
    const timeout = window.setTimeout(() => setNotice(''), kind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice])
  const localValues = (attribute.values?.filter(item => item.isActive) ?? []).slice().sort((left, right) => left.value.localeCompare(right.value, 'tr-TR', { sensitivity: 'base', numeric: true })); const remoteValues = (references.data?.items.filter(item => item.isActive) ?? []).slice().sort((left, right) => left.name.localeCompare(right.name, 'tr-TR', { sensitivity: 'base', numeric: true })); const mappingByLocal = new Map((mappings.data ?? []).map(item => [item.localId, item]))
  useEffect(() => { if (mappings.data) setSelections(Object.fromEntries(mappings.data.map(item => [item.localId, item.externalId]))) }, [mappings.data])
  useEffect(() => {
    if (!remoteValues.length) return
    setSelectedRemoteId(current => current && remoteValues.some(item => item.externalId === current) ? current : remoteValues.find(item => Object.values(selections).includes(item.externalId))?.externalId ?? remoteValues[0].externalId)
  }, [remoteValues, selections])
  const selectedLocalIds = new Set(localValues.filter(item => selections[item.id] === selectedRemoteId).map(item => item.id))
  const assignedGroups = remoteValues.map(remote => ({ remote, locals: localValues.filter(value => selections[value.id] === remote.externalId) })).filter(group => group.locals.length > 0)
  function toggleLocalValue(localId: string) {
    if (!selectedRemoteId) return
    setSelections(current => {
      const next = { ...current }
      if (next[localId] === selectedRemoteId) delete next[localId]
      else next[localId] = selectedRemoteId
      return next
    })
  }
  async function saveAll() {
    if (!references.data) return
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
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      setNotice(changed ? `${changed} Web Color eşlemesi kaydedildi.` : 'Web Color eşlemelerinde değişiklik yok.')
    } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Web Color eşlemeleri kaydedilemedi.') } finally { setSaving(false) }
  }
  if (!localValues.length) return <div className="unknown"><strong>Panel renk değeri bulunamadı</strong><p>Önce Panel Renk özelliğine aktif değerler ekleyin.</p></div>
  if (references.isError) return <div className="unknown"><strong>Güncel Web Color değerleri yok</strong><p>Web Color değer listesini salt-okunur olarak eşitleyin.</p>{notice && <p role="status">{notice}</p>}<button type="button" disabled={sync.isPending} onClick={() => sync.mutate()}>{sync.isPending ? 'Kuyruğa alınıyor…' : 'Web Color değerlerini eşitle'}</button></div>
  if (references.isLoading || mappings.isLoading) return <Busy text="Web Color değerleri ve mevcut eşlemeler yükleniyor…" />
  if (mappings.isError) return <ErrorBox error={mappings.error} onRetry={() => void mappings.refetch()} />
  return <div className="web-color-value-editor"><div className="value-mapping-heading">{showGroups ? <strong className="cm-webcolor-title">Kayıtlı Eşlemeler</strong> : <label className="cm-webcolor-pick">Web Color Değeri<select aria-label="Web Color değeri" value={selectedRemoteId} onChange={event => setSelectedRemoteId(event.target.value)}><option value="">Web Color seçin</option>{remoteValues.map(remote => <option key={remote.externalId} value={remote.externalId}>{remote.name}</option>)}</select></label>}<span className="cm-webcolor-actions"><button type="button" className="secondary" disabled={!assignedGroups.length} onClick={() => setShowGroups(value => !value)}>{showGroups ? 'Eşlemeye Dön' : `Kayıtlı Eşlemeleri Gör (${assignedGroups.length})`}</button><button type="button" disabled={saving} onClick={() => void saveAll()}>{saving ? 'Kaydediliyor…' : 'Tüm Eşlemeleri Kaydet'}</button></span></div>{notice && <MessageToast message={notice} onClose={() => setNotice('')} />}{!showGroups && <><div className="web-color-panel-value-picker"><div className="web-color-panel-value-picker-head"><strong>Karşılık Gelen Panel Renkleri</strong><span>{selectedLocalIds.size} seçildi</span></div><div className="web-color-panel-value-grid">{localValues.map(localValue => { const selected = selectedLocalIds.has(localValue.id); return <label key={localValue.id} className={`web-color-panel-value${selected ? ' selected' : ''}`}><input type="checkbox" checked={selected} disabled={!selectedRemoteId} onChange={() => toggleLocalValue(localValue.id)} /><span>{localValue.value}</span></label> })}</div></div></>}{showGroups && assignedGroups.length > 0 && <div className="web-color-assignment-groups"><div className="cm-webcolor-groups-head"><span>Web Color</span><span>Panel Renkleri</span></div><div>{assignedGroups.map(group => <p key={group.remote.externalId}><span>{group.remote.name}</span><small>{group.locals.map(value => value.value).join(' · ')}</small></p>)}</div></div>}</div>
}

const autoQueuedHepsiburadaValueSyncs = new Set<string>()

function AttributeValueMappingEditor({ connectionId, categoryScope, attribute, externalAttributeId, platformLabel = 'Trendyol', customPanelColorValues = false, marketplaceAllowsFreeText = false, autoSyncMissingValues = false, allowReusablePanelValues = false, required = false }: { connectionId: string; categoryScope: string; attribute: LocalAttribute; externalAttributeId: string; platformLabel?: string; customPanelColorValues?: boolean; marketplaceAllowsFreeText?: boolean; autoSyncMissingValues?: boolean; allowReusablePanelValues?: boolean; required?: boolean }) {
  const client = useQueryClient(); const [notice, setNotice] = useState(''); const [selections, setSelections] = useState<Record<string, string[]>>({}); const [unmatchedOpen, setUnmatchedOpen] = useState(false); const [selectionsReady, setSelectionsReady] = useState(false); const [initializedValueScope, setInitializedValueScope] = useState(''); const [saving, setSaving] = useState(false); const [autoMapping, setAutoMapping] = useState(false); const [quickValueOpen, setQuickValueOpen] = useState(false); const [quickValueDraft, setQuickValueDraft] = useState(''); const [quickValueSaving, setQuickValueSaving] = useState(false); const [editingQuickValue, setEditingQuickValue] = useState<{ id: string; value: string } | null>(null); const [syncJobId, setSyncJobId] = useState<string | null>(null); const [shareWithOtherCategories, setShareWithOtherCategories] = useState(true); const [showUnusedValues, setShowUnusedValues] = useState(false); const handledSyncJobs = useRef(new Set<string>()); const valueScope = `${categoryScope}/${externalAttributeId}`
  const referenceKey = ['reference-attribute-values', connectionId, valueScope] as const
  const references = useQuery({ queryKey: referenceKey, queryFn: () => hubApi<ReferenceData>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(externalAttributeId)}/values?connectionId=${encodeURIComponent(connectionId)}`), retry: false, enabled: !customPanelColorValues && !marketplaceAllowsFreeText })
  const mappings = useQuery({ queryKey: ['attribute-value-mappings', connectionId, valueScope], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(valueScope)}`), retry: false, enabled: !customPanelColorValues && !marketplaceAllowsFreeText })
  const [suggestionReview, setSuggestionReview] = useState<ValueMappingSuggestion[]>([])
  // Names the panel values carry on the other marketplaces: extra hints for the suggestions.
  const nameHints = useQuery({ queryKey: ['marketplace-name-hints', attribute.id, connectionId], queryFn: () => hubApi<{ attributeNames: string[]; valueNames: Record<string, string[]> }>(`/mappings/name-hints?attributeId=${encodeURIComponent(attribute.id)}&connectionId=${encodeURIComponent(connectionId)}`), retry: false, staleTime: 5 * 60_000, enabled: !customPanelColorValues && !marketplaceAllowsFreeText })
  const usedValues = useQuery({ queryKey: ['attribute-used-values', attribute.id], queryFn: () => hubApi<string[]>(`/catalog/attributes/${attribute.id}/used-values`), retry: false, staleTime: 60_000, enabled: !customPanelColorValues && !marketplaceAllowsFreeText })
  const sync = useMutation({ mutationFn: () => hubApi<{ jobId: string }>(`/connections/${connectionId}/reference-sync-jobs?resourceType=ATTRIBUTE_VALUES&parentExternalId=${encodeURIComponent(valueScope)}`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: '{}' }), onSuccess: result => { setSyncJobId(result.jobId); setNotice(`${platformLabel} değer listesi salt-okunur olarak eşitleniyor…`) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Değer eşitleme başlatılamadı.') })
  const syncJob = useQuery({ queryKey: ['reference-value-sync-job', syncJobId], queryFn: () => hubApi<{ job: { status: string; lastErrorCode: string | null; lastErrorSummary: string | null } }>(`/jobs/${syncJobId}`, { cache: 'no-store' }), enabled: Boolean(syncJobId), refetchInterval: query => query.state.data && isReferenceSyncJobTerminal(query.state.data.job.status) ? false : 1000, refetchIntervalInBackground: true, retry: 1 })
  const syncBusy = sync.isPending || Boolean(syncJobId)
  useEffect(() => {
    if (!autoSyncMissingValues || !references.isError || !(references.error instanceof ApiRequestError) || references.error.code !== 'REFERENCE_SNAPSHOT_UNAVAILABLE') return
    const autoSyncKey = `${connectionId}:${valueScope}`
    if (!shouldAutoSyncHepsiburadaValues({ allowsCustomValue: false, snapshotMissing: true, syncPending: syncBusy, alreadyRequested: autoQueuedHepsiburadaValueSyncs.has(autoSyncKey) })) return
    autoQueuedHepsiburadaValueSyncs.add(autoSyncKey)
    sync.mutate()
  }, [autoSyncMissingValues, connectionId, references.isError, references.error, valueScope, syncBusy, sync.mutate])
  useEffect(() => {
    const jobId = syncJobId
    const job = syncJob.data?.job
    if (!jobId || !job || !isReferenceSyncJobTerminal(job.status) || handledSyncJobs.current.has(jobId)) return
    handledSyncJobs.current.add(jobId)
    setSyncJobId(null)
    if (job.status === 'SUCCEEDED') {
      void client.invalidateQueries({ queryKey: referenceKey }).then(() => {
        const latest = client.getQueryData<ReferenceData>(referenceKey)
        const latestQuery = client.getQueryState<ReferenceData>(referenceKey)
        setNotice(latestQuery?.status === 'error'
          ? `${platformLabel} eşitleme işi tamamlandı ancak güncel değer snapshot’ı tekrar okunamadı.`
          : latest?.items.length
            ? `${platformLabel} değer listesi yenilendi. Yeni seçenek geldiyse eşleyip tüm eşlemeleri kaydedin.`
            : `${platformLabel} eşitlemesi tamamlandı; bu kategori özelliği için pazar yeri seçilebilir değer döndürmedi.`)
      })
      return
    }
    setNotice(`${platformLabel} değer eşitlemesi ${job.status.toLocaleLowerCase('tr-TR')} durumunda tamamlandı${job.lastErrorSummary ? `: ${job.lastErrorSummary}` : job.lastErrorCode ? ` (${job.lastErrorCode})` : '.'}`)
  }, [client, connectionId, platformLabel, valueScope, syncJob.data?.job, syncJobId])
  useEffect(() => {
    if (!notice) return
    const kind = notificationKindFromMessage(notice)
    appendNotification(notice, kind)
    document.documentElement.dataset.rvMappingFeedback = kind
    const timeout = window.setTimeout(() => setNotice(''), kind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice])
  const localValues = (attribute.values?.filter(item => item.isActive) ?? []).slice().sort((left, right) => left.value.localeCompare(right.value, 'tr-TR', { sensitivity: 'base', numeric: true }))
  const allRemoteValues = references.data?.items ?? []
  const mappingsByExternal = new Map<string, CatalogMapping[]>()
  for (const item of mappings.data ?? []) mappingsByExternal.set(item.externalId, [...(mappingsByExternal.get(item.externalId) ?? []), item])
  const mappingFor = (externalId: string, localId: string) => mappingsByExternal.get(externalId)?.find(item => item.localId === localId)
  const sameIds = (left: readonly string[], right: readonly string[]) => left.length === right.length && left.every(id => right.includes(id))
  const remoteNameByExternal = new Map(allRemoteValues.map(item => [item.externalId, item.name]))
  const assignedPanelValues: Record<string, string> = {}
  for (const [externalId, localIds] of Object.entries(selections)) for (const localId of localIds) assignedPanelValues[localId] = remoteNameByExternal.get(externalId) ?? externalId
  const usedValueFilter = visiblePanelValues(localValues, usedValues.data, Object.values(selections).flat(), showUnusedValues)
  const unmatchedPanelValues = usedValueFilter.visible.filter(item => !assignedPanelValues[item.id])
  const mappedExternalIds = new Set((mappings.data ?? []).map(item => item.externalId))
  const remoteValues = allRemoteValues.filter(item => item.isActive || mappedExternalIds.has(item.externalId)).slice().sort((left, right) => left.name.localeCompare(right.name, 'tr-TR', { sensitivity: 'base', numeric: true }))
  const activeRemoteValues = allRemoteValues.filter(item => item.isActive)
  const directPlan = planDirectReferenceValues(localValues, activeRemoteValues)
  const missingPanelValues = planMissingReferencePanelValues(localValues, activeRemoteValues, selections)
  const unsavedExternalIds = new Set(remoteValues.filter(item => !sameIds(selections[item.externalId] ?? [], (mappingsByExternal.get(item.externalId) ?? []).map(mapping => mapping.localId))).map(item => item.externalId))
  const availableDirectMappings = directPlan.mappings.filter(item => !unsavedExternalIds.has(item.externalId))
  const directMappingWorkCount = availableDirectMappings.filter(item => attributeValueMappingNeedsSave(mappingFor(item.externalId, item.localId), item.localId, item.externalId, references.data?.snapshotId ?? '')).length
  const templateFilledCount = new Set((mappings.data ?? []).filter(item => item.source === 'TEMPLATE').map(item => item.localId)).size
  function updateAttributeCache(updated: LocalAttribute) {
    for (const queryKey of [['attributes', 'mapping-builder'], ['attributes', 'mapping']] as const) {
      client.setQueryData<Page<LocalAttribute>>(queryKey, current => current ? { ...current, items: current.items.map(item => item.id === updated.id ? updated : item) } : current)
    }
  }
  useEffect(() => {
    setSelections({})
    setSelectionsReady(false)
    setInitializedValueScope('')
    setSuggestionReview([])
  }, [valueScope])
  useEffect(() => {
    if (!mappings.data || initializedValueScope === valueScope) return
    const grouped = mappings.data.reduce<Record<string, string[]>>((current, item) => {
      current[item.externalId] = [...(current[item.externalId] ?? []), item.localId]
      return current
    }, {})
    setSelections(grouped)
    setSelectionsReady(true)
    setInitializedValueScope(valueScope)
  }, [mappings.data, valueScope, initializedValueScope])
  if (customPanelColorValues) {
    if (!localValues.length) return <div className="unknown"><strong>Panel renk değeri bulunamadı</strong><p>Normal Trendyol Renk alanı için panelde etkin renk seçeneği ekleyin.</p></div>
    return <div className="custom-panel-color-values"><div className="custom-panel-color-values-heading"><strong>Gönderilecek panel renkleri</strong><span>{localValues.length} renk · Trendyol’da değer eşlemesi gerekmiyor</span></div><div className="custom-panel-color-values-list">{localValues.map(localValue => <div className="custom-panel-color-value" key={localValue.id}><span>{localValue.value}</span><strong>{formatPanelColorValue(localValue.value)}</strong></div>)}</div></div>
  }
  if (marketplaceAllowsFreeText) return <div className="unknown"><strong>{platformLabel} alanı serbest metin kabul ediyor</strong><p>Bu özellik için sabit pazar yeri değer listesi bulunmadığından değer eşlemesi gerekmiyor.</p></div>
  async function saveAll() {
    if (!references.data || !selectionsReady) return
    setSaving(true); setNotice('')
    try {
      var changed = 0
      for (const remoteValue of remoteValues) {
        const wanted = selections[remoteValue.externalId] ?? []
        for (const existing of mappingsByExternal.get(remoteValue.externalId) ?? []) {
          if (wanted.includes(existing.localId)) continue
          await hubApi<boolean>(`/mappings/attribute-values/${existing.localId}?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(valueScope)}&externalId=${encodeURIComponent(remoteValue.externalId)}`, { method: 'DELETE', headers: { 'If-Match': `"v${existing.version}"` } })
          changed++
        }
      }
      for (const planned of planReferencePanelMappings(selections)) {
        if (!remoteValues.some(item => item.externalId === planned.externalId)) continue
        const existing = mappingFor(planned.externalId, planned.localId)
        if (!attributeValueMappingNeedsSave(existing, planned.localId, planned.externalId, references.data.snapshotId)) continue
        await hubApi<CatalogMapping>(`/mappings/attribute-values/${planned.localId}`, { method: 'PUT', headers: existing ? { 'If-Match': `"v${existing.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId: references.data.snapshotId, scopeExternalId: valueScope, externalId: planned.externalId, status: 'VERIFIED', updateTemplate: shareWithOtherCategories }) })
        changed++
      }
      await client.invalidateQueries({ queryKey: ['attribute-value-mappings', connectionId, valueScope] })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      setNotice(changed ? `${changed} değer eşlemesi kaydedildi.` : 'Değer eşlemelerinde değişiklik yok.')
    } catch (reason) { setNotice(reason instanceof Error ? reason.message : 'Değer eşlemeleri kaydedilemedi.') } finally { setSaving(false) }
  }
  async function addQuickValues() {
    const requested = quickValueDraft.split(',').map(value => value.trim()).filter(Boolean)
    const seen = new Set<string>()
    const values = requested.filter(value => {
      const normalized = normalizeReferenceValueLabel(value)
      if (seen.has(normalized)) return false
      seen.add(normalized)
      return !localValues.some(existing => normalizeReferenceValueLabel(existing.value) === normalized)
    })
    if (!values.length) {
      setNotice(requested.length ? 'Girilen değerler zaten bu özellikte bulunuyor.' : 'Eklemek için en az bir değer yazın.')
      return
    }
    setQuickValueSaving(true)
    try {
      const updated = await hubApi<LocalAttribute>(`/catalog/attributes/${attribute.id}/values`, {
        method: 'POST',
        headers: { 'Idempotency-Key': idempotency() },
        body: JSON.stringify(values.map((value, index) => ({ value, sortOrder: localValues.length + index })))
      })
      updateAttributeCache(updated)
      await client.invalidateQueries({ queryKey: ['attributes'] })
      setQuickValueDraft('')
      setQuickValueOpen(false)
      setNotice(`${values.length} değer “${attribute.name}” özelliğine eklendi.`)
    } catch (reason) {
      setNotice(reason instanceof Error ? reason.message : 'Değer eklenemedi.')
    } finally { setQuickValueSaving(false) }
  }
  async function createMissingPanelValues() {
    if (!references.data || !selectionsReady || !missingPanelValues.length || quickValueSaving) return
    setQuickValueSaving(true)
    setNotice('')
    try {
      const updated = await hubApi<LocalAttribute>(`/catalog/attributes/${attribute.id}/values`, {
        method: 'POST',
        headers: { 'Idempotency-Key': idempotency() },
        body: JSON.stringify(missingPanelValues.map((value, index) => ({ value, sortOrder: localValues.length + index })))
      })
      updateAttributeCache(updated)
      await client.invalidateQueries({ queryKey: ['attributes'] })
      setNotice(`${missingPanelValues.length} panel değeri oluşturuldu. Şimdi “Birebir eşleşenleri eşle” düğmesini kullanabilirsiniz.`)
    } catch (reason) {
      setNotice(reason instanceof Error ? reason.message : 'Eksik panel değerleri oluşturulamadı.')
    } finally { setQuickValueSaving(false) }
  }
  async function updateQuickValue(valueId: string, nextValue: string, sortOrder: number) {
    const value = nextValue.trim()
    if (!value) {
      setNotice('Değer boş olamaz.')
      return
    }
    setQuickValueSaving(true)
    try {
      const updated = await hubApi<LocalAttribute>(`/catalog/attributes/${attribute.id}/values/${valueId}`, {
        method: 'PUT',
        headers: { 'If-Match': `"v${attribute.version}"` },
        body: JSON.stringify({ value, sortOrder })
      })
      updateAttributeCache(updated)
      await client.invalidateQueries({ queryKey: ['attributes'] })
      setEditingQuickValue(null)
      setNotice('Değer güncellendi.')
    } catch (reason) {
      setNotice(reason instanceof Error ? reason.message : 'Değer güncellenemedi.')
    } finally { setQuickValueSaving(false) }
  }
  async function removeQuickValue(valueId: string) {
    setQuickValueSaving(true)
    try {
      const updated = await hubApi<LocalAttribute>(`/catalog/attributes/${attribute.id}/values/${valueId}`, { method: 'DELETE' })
      updateAttributeCache(updated)
      await client.invalidateQueries({ queryKey: ['attributes'] })
      setEditingQuickValue(null)
      setNotice('Değer kaldırıldı.')
    } catch (reason) {
      setNotice(reason instanceof Error ? reason.message : 'Değer kaldırılamadı.')
    } finally { setQuickValueSaving(false) }
  }
  async function mapDirectMatchingValues() {
    if (!references.data || !selectionsReady || (!directMappingWorkCount && !unsavedExternalIds.size) || autoMapping || syncBusy) return
    if (!directMappingWorkCount) {
      setNotice('Yeni birebir eşleşme bulunamadı. Kaydedilmemiş panel seçimleriniz korundu; kalıcı olması için “Tüm eşlemeleri kaydet” seçin.')
      return
    }
    setAutoMapping(true)
    setNotice('')
    let mappedCount = 0
    try {
      for (const planned of availableDirectMappings) {
        const existing = mappingFor(planned.externalId, planned.localId)
        if (!attributeValueMappingNeedsSave(existing, planned.localId, planned.externalId, references.data.snapshotId)) continue
        await hubApi<CatalogMapping>(`/mappings/attribute-values/${planned.localId}`, {
          method: 'PUT',
          headers: existing ? { 'If-Match': `"v${existing.version}"` } : {},
          body: JSON.stringify({ connectionId, snapshotId: references.data.snapshotId, scopeExternalId: valueScope, externalId: planned.externalId, status: 'VERIFIED' })
        })
        mappedCount++
      }
      setSelections(current => availableDirectMappings.reduce((next, planned) => updatePanelValueReferenceSelection(next, planned.externalId, [...(next[planned.externalId] ?? []), planned.localId], allowReusablePanelValues), current))
      await client.invalidateQueries({ queryKey: ['attribute-value-mappings', connectionId, valueScope] })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      const summary = `${mappedCount} panel değeri mevcut ${platformLabel} karşılığıyla eşlendi.`
      setNotice(directPlan.ambiguousCount ? `${summary} Adı birden fazla eşleşen ${directPlan.ambiguousCount} seçenek otomatik eşlenmedi.` : summary)
    } catch (reason) {
      await client.invalidateQueries({ queryKey: ['attribute-value-mappings', connectionId, valueScope] })
      await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
      setNotice(reason instanceof Error ? `Otomatik eşleme tamamlanamadı: ${reason.message}` : 'Otomatik eşleme tamamlanamadı.')
    } finally { setAutoMapping(false) }
  }
  // Confidence-based suggestions for the panel values still unmatched: same names, folded letters
  // and synonyms are selected at once; weaker matches wait in a list for approval. Nothing is saved
  // until "Tüm eşlemeleri kaydet".
  function suggestMappings() {
    const result = suggestValueMappings(unmatchedPanelValues, activeRemoteValues, valueMatchScore, autoMatchScore, nameHints.data?.valueNames ?? {})
    if (result.automatic.length) setSelections(current => result.automatic.reduce((next, item) => togglePanelValueReferenceSelection(next, item.externalId, item.localId, allowReusablePanelValues), current))
    setSuggestionReview(result.review)
    setNotice(result.automatic.length || result.review.length
      ? `${result.automatic.length} eşleme yüksek güvenle seçildi${result.review.length ? `, ${result.review.length} öneri onayınızı bekliyor` : ''}. Kalıcı olması için “Tüm eşlemeleri kaydet” seçin.`
      : 'Eşleşmemiş değerler için güvenilir bir öneri bulunamadı.')
  }
  function approveSuggestions(items: readonly ValueMappingSuggestion[]) {
    setSelections(current => items.reduce((next, item) => assignedPanelValues[item.localId] ? next : togglePanelValueReferenceSelection(next, item.externalId, item.localId, allowReusablePanelValues), current))
    setSuggestionReview(current => current.filter(item => !items.includes(item)))
  }
  const closeQuickValueModal = () => { if (quickValueSaving) return; setQuickValueOpen(false); setEditingQuickValue(null) }
  const quickValueModal = quickValueOpen && createPortal(<div className="panel-attribute-modal value-mapping-quick-value-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) closeQuickValueModal() }}><section className="panel-attribute-modal-card panel-attribute-quick-modal-card" role="dialog" aria-modal="true" aria-labelledby="mapping-quick-value-title" onMouseDown={event => event.stopPropagation()}><header><div><strong id="mapping-quick-value-title">{attribute.name}</strong><small>Panel özelliği · bu başlığa yeni değer ekleyin</small></div><button type="button" aria-label="Değer ekleme penceresini kapat" onClick={closeQuickValueModal} disabled={quickValueSaving}><UiIcon name="close" /></button></header><form className="panel-attribute-add-value panel-attribute-quick-add-value" onSubmit={event => { event.preventDefault(); void addQuickValues() }}><input autoFocus value={quickValueDraft} onChange={event => setQuickValueDraft(event.target.value)} placeholder="Yeni değerleri virgülle yazın" aria-label={`${attribute.name} için yeni değerler`} /><button type="submit" disabled={quickValueSaving || !quickValueDraft.trim()}>{quickValueSaving ? 'Ekleniyor…' : '+ Değer ekle'}</button></form><div className="panel-attribute-value-editor">{localValues.length ? localValues.map(value => <div key={value.id}>{editingQuickValue?.id === value.id ? <input autoFocus value={editingQuickValue.value} aria-label={`${value.value} değerini düzenle`} onChange={event => setEditingQuickValue({ id: value.id, value: event.target.value })} /> : <span>{value.value}</span>}<div>{editingQuickValue?.id === value.id ? <><button type="button" aria-label={`${value.value} değerini kaydet`} disabled={quickValueSaving} onClick={() => void updateQuickValue(value.id, editingQuickValue.value, value.sortOrder)}><UiIcon name="check" /></button><button type="button" aria-label="Değer düzenlemeyi iptal et" disabled={quickValueSaving} onClick={() => setEditingQuickValue(null)}><UiIcon name="close" /></button></> : <><button type="button" aria-label={`${value.value} değerini düzenle`} disabled={quickValueSaving} onClick={() => setEditingQuickValue({ id: value.id, value: value.value })}><UiIcon name="edit" /></button><button type="button" aria-label={`${value.value} değerini sil`} disabled={quickValueSaving} onClick={() => void removeQuickValue(value.id)}><UiIcon name="close" /></button></>}</div></div>) : <span className="panel-value-empty">Henüz değer eklenmedi.</span>}</div><footer><button type="button" className="secondary" onClick={closeQuickValueModal} disabled={quickValueSaving}>Kapat</button></footer></section></div>, document.body)
  if (references.isError) return <div className="unknown"><strong>Güncel özellik değerleri snapshot’ı yok</strong><p>Seçili kategori ve özellik için pazar yerinin salt-okunur değer listesini eşitleyin.</p>{notice && <p role="status">{notice}</p>}{syncJob.isError && <p role="alert">Eşitleme işi durumu okunamadı; işlem takibinden iş kaydını kontrol edin.</p>}<div className="value-mapping-heading-actions"><button type="button" className="secondary" disabled={quickValueSaving} onClick={() => setQuickValueOpen(true)}>Hızlı değer oluştur</button><button type="button" disabled={syncBusy} onClick={() => sync.mutate()}>{syncBusy ? 'Eşitleniyor…' : `${platformLabel} değerlerini eşitle`}</button></div>{quickValueModal}</div>
  if (references.isLoading || mappings.isLoading) return <Busy text="Özellik değerleri ve mevcut eşlemeler yükleniyor…" />
  if (mappings.isError) return <ErrorBox error={mappings.error} onRetry={() => void mappings.refetch()} />
  return <div className="mapping-step nested value-mapping-editor">
    <div className="value-mapping-heading">
      <div><h3>Değer eşleştirmeleri</h3><p>{platformLabel} değerlerini karşılık gelen panel değerleriyle eşleyin.{allowReusablePanelValues ? ' Aynı panel değeri birden fazla Hepsiburada seçeneği için kullanılabilir.' : ''}</p></div>
    </div>
    <div className="value-mapping-commandbar" role="toolbar" aria-label="Değer eşleme işlemleri">
      <div className="value-mapping-command-group" role="group" aria-label="Otomatik eşleme">
        <button type="button" className="secondary" title={`Eşleşmemiş panel değerleri için ${platformLabel} değeri önerir: aynı ad, Türkçe harf farkı ve eş anlamlılar (ör. Black → Siyah) doğrudan seçilir; zayıf benzerlikler onayınıza sunulur.`} disabled={!selectionsReady || autoMapping || saving || syncBusy || !unmatchedPanelValues.length || !activeRemoteValues.length} onClick={suggestMappings}><UiIcon name="sparkle" />Önerileri getir</button>
        <button type="button" className="secondary" title={`Aynı adlı panel ve ${platformLabel} değerlerini eşler.${directPlan.ambiguousCount ? ` Belirsiz ${directPlan.ambiguousCount} eşleşme atlanır.` : ''}`} disabled={!selectionsReady || autoMapping || saving || syncBusy || quickValueSaving || (directMappingWorkCount === 0 && unsavedExternalIds.size === 0)} onClick={() => void mapDirectMatchingValues()}><UiIcon name="equal" />{autoMapping ? 'Eşleştiriliyor…' : 'Birebir eşle'}</button>
      </div>
      <div className="value-mapping-command-group" role="group" aria-label="Panel değeri ekleme">
        <button type="button" className="secondary" title="Yeni bir panel değeri ekler." disabled={quickValueSaving || autoMapping || saving} onClick={() => setQuickValueOpen(true)}><UiIcon name="plus" />Hızlı değer</button>
        <button type="button" className="secondary" title="Eşlemesi boş pazar yeri değerlerinin adlarıyla panel değerleri oluşturur. Aynı adlı tekrarları tek değer olarak ekler." disabled={!selectionsReady || !missingPanelValues.length || quickValueSaving || autoMapping || saving || syncBusy} onClick={() => void createMissingPanelValues()}><UiIcon name="layers" />{quickValueSaving ? 'Ekleniyor…' : 'Boş değerleri oluştur'}{!quickValueSaving && missingPanelValues.length > 0 && <b className="value-mapping-command-count">{missingPanelValues.length}</b>}</button>
      </div>
      <div className="value-mapping-command-group is-end" role="group" aria-label="Liste ve paylaşım">
        <button type="button" className="secondary" title={`Güncel ${platformLabel} değer listesini salt-okunur olarak yeniden okur.`} disabled={syncBusy || autoMapping || saving} onClick={() => sync.mutate()}><UiIcon name="refresh" />{syncBusy ? 'Yenileniyor…' : 'Listeyi yenile'}</button>
        <button type="button" role="switch" aria-checked={shareWithOtherCategories} className={`secondary value-mapping-share-switch${shareWithOtherCategories ? ' is-on' : ''}`} title="Açıkken bu eşlemeler, aynı özelliği listeleyen diğer kategorilere de otomatik uygulanır. Kapalıyken yalnızca bu kategoride kalır." onClick={() => setShareWithOtherCategories(current => !current)}><i aria-hidden="true" /><span>Diğer kategorilerde de kullan</span></button>
      </div>
    </div>
    {notice && <MessageToast message={notice} onClose={() => setNotice('')} />}
    {!!localValues.length && !!remoteValues.length && (() => {
      const total = usedValueFilter.visible.length
      const matched = total - unmatchedPanelValues.length
      const shown = unmatchedOpen ? unmatchedPanelValues : unmatchedPanelValues.slice(0, 6)
      return <div className={`value-mapping-unmatched ${unmatchedPanelValues.length ? 'has-unmatched' : 'is-complete'}`} role="status">
        <div className="value-mapping-unmatched-head">
          <strong>{unmatchedPanelValues.length ? `${unmatchedPanelValues.length} panel değeri henüz eşleşmedi` : `Tüm panel değerleri bir ${platformLabel} değeriyle eşleşti`}</strong>
          <small>{matched} / {total} eşleşti{templateFilledCount > 0 ? ` · ${templateFilledCount} değer diğer kategorilerden geldi` : ''}</small>
        </div>
        {(usedValueFilter.hiddenCount > 0 || (showUnusedValues && (usedValues.data?.length ?? 0) > 0)) && <p className="value-mapping-used-filter">
          {showUnusedValues ? 'Tüm panel değerleri gösteriliyor.' : `Ürünlerde kullanılan ${usedValueFilter.visible.length} değer gösteriliyor.`}
          <button type="button" onClick={() => setShowUnusedValues(current => !current)}>{showUnusedValues ? 'Yalnızca kullanılanları göster' : `Kullanılmayan ${usedValueFilter.hiddenCount} değeri de göster`}</button>
        </p>}
        <i className="value-mapping-unmatched-bar" aria-hidden="true"><b style={{ width: `${Math.round((matched / Math.max(total, 1)) * 100)}%` }} /></i>
        {unmatchedPanelValues.length > 0 && <div className={`value-mapping-unmatched-chips${unmatchedOpen ? ' is-open' : ''}`}>
          {shown.map(item => <span key={item.id}>{item.value}</span>)}
          {unmatchedPanelValues.length > 6 && <button type="button" onClick={() => setUnmatchedOpen(current => !current)}>{unmatchedOpen ? 'Daha az göster' : `+${unmatchedPanelValues.length - 6} daha göster`}</button>}
        </div>}
      </div>
    })()}
    {suggestionReview.length > 0 && <div className="value-mapping-suggestions" role="region" aria-label="Onay bekleyen öneriler">
      <div className="value-mapping-suggestions-head">
        <strong>Onay bekleyen öneriler ({suggestionReview.length})</strong>
        <button type="button" onClick={() => approveSuggestions(suggestionReview)}>Tümünü onayla</button>
        <button type="button" className="secondary" onClick={() => setSuggestionReview([])}>Listeyi kapat</button>
      </div>
      <ul>
        {suggestionReview.map(item => <li key={`${item.localId}:${item.externalId}`}>
          <span>{item.localLabel}</span><b aria-hidden="true">→</b><strong>{item.remoteName}</strong>
          <small title="Benzerlik puanı">%{item.score} benzer</small>
          <button type="button" onClick={() => approveSuggestions([item])}>Onayla</button>
          <button type="button" className="secondary" onClick={() => setSuggestionReview(current => current.filter(entry => entry !== item))}>Atla</button>
        </li>)}
      </ul>
    </div>}
    <div className="value-mapping-rows">
      {!remoteValues.length && <div className="unknown"><strong>Bu özellik için seçilebilir değer bulunamadı</strong><p>{platformLabel} referans listesi başarıyla okundu ancak kategori özelliği sabit seçenek döndürmedi.</p></div>}
      {remoteValues.map(remoteValue => {
        const selectedPanelIds = selections[remoteValue.externalId] ?? []
        const fromTemplate = isTemplateFilledRow(mappingsByExternal.get(remoteValue.externalId) ?? [], selectedPanelIds)
        return <div key={remoteValue.externalId} className={valueMappingRowClassName(selectedPanelIds.length > 0, required)}>
          <span>{remoteValue.name}{fromTemplate && <em className="value-mapping-template-badge" title="Bu eşleme, aynı özelliği kullanan başka bir kategorideki kararınızdan otomatik geldi. Değiştirip kaydederseniz sizin kararınız olur.">Şablon</em>}</span>
          <b className="value-mapping-row-link" aria-hidden="true" title="Eşleştir"><UiIcon name="link" /></b>
          <PanelValueMultiSelect label={remoteValue.name} options={usedValueFilter.visible} selectedIds={selectedPanelIds} allowReuse={allowReusablePanelValues} assignedElsewhere={Object.fromEntries(Object.entries(assignedPanelValues).filter(([localId]) => !selectedPanelIds.includes(localId)))} disabled={!localValues.length} onToggle={localId => setSelections(current => togglePanelValueReferenceSelection(current, remoteValue.externalId, localId, allowReusablePanelValues))} />
        </div>
      })}
      {!!localValues.length && !remoteValues.length && <div className="unknown"><strong>Bu özellik için seçilebilir değer bulunamadı</strong><p>{platformLabel} referans listesi başarıyla okundu ancak kategori özelliği sabit seçenek döndürmedi.</p></div>}
    </div>
    <div className="value-mapping-savebar" role="region" aria-label="Kaydetme">
      <span>{unmatchedPanelValues.length ? `Eşleşmeyen ${unmatchedPanelValues.length} panel değeri` : 'Tüm panel değerleri eşleşti'}{unsavedExternalIds.size > 0 ? <strong>{unsavedExternalIds.size} kaydedilmemiş değişiklik</strong> : <em>Kaydedilmemiş değişiklik yok</em>}</span>
      <button type="button" disabled={!selectionsReady || saving || syncBusy || autoMapping || quickValueSaving} onClick={() => void saveAll()}><UiIcon name="save" />{saving ? 'Kaydediliyor…' : 'Tüm eşlemeleri kaydet'}</button>
    </div>
    {quickValueModal}
  </div>
}

export function BrandMappingPage() {
  const client = useQueryClient(); const [connectionId, setConnectionId] = useState(''); const [localId, setLocalId] = useState(''); const [externalId, setExternalId] = useState(''); const [notice, setNotice] = useState(''); const [brandName, setBrandName] = useState(''); const [brandExternalSearch, setBrandExternalSearch] = useState(''); const [brandExternalPickerOpen, setBrandExternalPickerOpen] = useState(false); const [brandEditOpen, setBrandEditOpen] = useState(false); const [editingBrandMappingId, setEditingBrandMappingId] = useState(''); const [brandTransferBusy, setBrandTransferBusy] = useState(false); const [brandTransferBundle, setBrandTransferBundle] = useState<BrandMappingTransferBundle | null>(null); const [brandTransferOpen, setBrandTransferOpen] = useState(false); const [brandTransferSelection, setBrandTransferSelection] = useState({ brands: true, mappings: true })
  useEffect(() => {
    if (!notice) return
    const kind = notificationKindFromMessage(notice)
    appendNotification(notice, kind)
    document.documentElement.dataset.rvMappingFeedback = kind
    const timeout = window.setTimeout(() => setNotice(''), kind === 'info' ? 7000 : 5500)
    return () => window.clearTimeout(timeout)
  }, [notice])
  const connections = useQuery({ queryKey: ['connections', 'brand-mapping'], queryFn: () => loadAllPages<Connection>('/connections') })
  const localBrands = useQuery({ queryKey: ['brands', 'mapping'], queryFn: () => loadAllPages<LocalBrand>('/catalog/brands') })
  const references = useQuery({ queryKey: ['reference-brands', connectionId], queryFn: () => hubApi<ReferenceData>(`/reference-data/brands?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId, retry: false })
  const mapping = useQuery({ queryKey: ['brand-mapping', localId, connectionId], queryFn: () => hubApi<CatalogMapping | null>(`/mappings/brands/${localId}?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!localId && !!connectionId, retry: false })
  const brandMappings = useQuery({ queryKey: ['brand-mappings', connectionId], queryFn: () => hubApi<CatalogMapping[]>(`/mappings/brands?connectionId=${encodeURIComponent(connectionId)}`), enabled: !!connectionId, retry: false })
  const syncBrands = useMutation({
    mutationFn: () => {
      if (!connectionId) throw new Error('Aktif Trendyol bağlantısı bulunamadı.')
      return hubApi(`/connections/${connectionId}/reference-sync-jobs?resourceType=BRANDS`, { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: '{}' })
    },
    onMutate: () => setNotice(''),
    onSuccess: async () => { setNotice('Marka listesi salt-okunur eşitleme kuyruğuna alındı.'); for (const delay of [500, 1000, 2000, 4000]) { await wait(delay); await client.invalidateQueries({ queryKey: ['reference-brands', connectionId] }) } },
    onError: reason => setNotice(reason instanceof Error ? reason.message : 'Marka listesi güncellenemedi.')
  })
  const save = useMutation({ mutationFn: () => { if (!references.data || !localId || !externalId) throw new Error('Bağlantı, panel markası ve Trendyol markası zorunludur.'); return hubApi<CatalogMapping>(`/mappings/brands/${localId}`, { method: 'PUT', headers: mapping.data ? { 'If-Match': `"v${mapping.data.version}"` } : {}, body: JSON.stringify({ connectionId, snapshotId: references.data.snapshotId, externalId, status: 'VERIFIED' }) }) }, onSuccess: async value => { setNotice('Marka eşlemesi doğrulandı ve kaydedildi.'); setExternalId(value.externalId); setBrandEditOpen(false); setEditingBrandMappingId(''); client.setQueryData(['brand-mapping', localId, connectionId], value); client.setQueryData<CatalogMapping[]>(['brand-mappings', connectionId], current => current ? [...current.filter(item => item.localId !== value.localId), value] : [value]); await Promise.all([client.invalidateQueries({ queryKey: ['brand-mapping', localId, connectionId] }), client.invalidateQueries({ queryKey: ['brand-mappings', connectionId] })]) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Eşleme kaydedilemedi.') })
  const createBrand = useMutation({ mutationFn: () => { if (!brandName.trim()) throw new Error('Marka adı zorunludur.'); return hubApi<LocalBrand>('/catalog/brands', { method: 'POST', headers: { 'Idempotency-Key': idempotency() }, body: JSON.stringify({ name: brandName.trim() }) }) }, onSuccess: async brand => { await client.invalidateQueries({ queryKey: ['brands', 'mapping'] }); setLocalId(brand.id); setExternalId(''); setBrandName(''); setNotice(`“${brand.name}” panel markası oluşturuldu ve seçildi.`) }, onError: reason => setNotice(reason instanceof Error ? reason.message : 'Marka oluşturulamadı.') })
  useEffect(() => { setExternalId(mapping.data?.externalId ?? '') }, [mapping.data])
  const trendyolConnections = connections.data?.items.filter(item => item.platformCode === 'TRENDYOL' && (item.status === 'ACTIVE' || item.status === 'VERIFIED')) ?? []
  useEffect(() => { if (!connectionId && trendyolConnections.length) setConnectionId(trendyolConnections[0].id) }, [connectionId, trendyolConnections])
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
  return <section className="content f3 mapping-page mapping-workspace stitch-reference-mapping"><header className="mapping-reference-heading"><div><h1>Eşleştirme Ayarları</h1></div><div className="mapping-reference-heading-actions" aria-label="Marka eşleştirme yedeği işlemleri"><div className="mapping-transfer-actions"><button type="button" className="secondary" title="Marka eşleştirme yedeğini JSON olarak dışa aktar" onClick={() => void exportBrandMappings()} disabled={brandTransferBusy || !connectionId}><UiIcon name="download" size={18} />{brandTransferBusy ? 'Hazırlanıyor…' : 'Dışa Aktar'}</button><label className="mapping-transfer-upload" title="JSON marka eşleştirme yedeğini içe aktar"><UiIcon name="import" size={18} />İçe Aktar<input type="file" accept="application/json,.json" onChange={readBrandMappingBundle} disabled={brandTransferBusy} /></label></div></div></header><MappingViewTabs active="brand" />{notice && <MessageToast message={notice} onClose={() => setNotice('')} />}
    <article className="mapping-reference-card mapping-local-brand-builder">
      <div className="mapping-section-heading"><div><h2>1. Yeni Marka Ekle</h2><p>Panel markalarını ekleyin, arayın veya kaldırın.</p></div></div>
      <section className="brand-library-panel brand-library-panel-standalone"><div className="brand-library-heading"><div><strong>Panel Markaları</strong><small>Mevcut markayı seçin veya yeni bir marka oluşturun.</small></div><span>{activeLocalBrands.length} kayıt</span></div><form className="platform-category-create brand-library-create" onSubmit={event => { event.preventDefault(); createBrand.mutate() }}><label>Yeni Panel Markası<input aria-label="Yeni panel markası adı" maxLength={160} value={brandName} onChange={event => setBrandName(event.target.value)} placeholder="Örn. Ravencia" required /></label><button disabled={createBrand.isPending}>{createBrand.isPending ? 'Ekleniyor…' : 'Marka Ekle'}</button></form><div className="category-chip-list brand-library-list" aria-label="Eklenen panel markaları">{activeLocalBrands.length ? activeLocalBrands.map(brand => <span key={brand.id} className={brand.id === localId ? 'active' : ''}><button type="button" onClick={() => { setLocalId(brand.id); setExternalId(''); setBrandExternalSearch(''); setBrandExternalPickerOpen(false); setNotice('') }}>{brand.name}</button><button type="button" className="category-chip-remove" aria-label={`${brand.name} markasını kaldır`} onClick={() => archiveBrand.mutate(brand)} disabled={archiveBrand.isPending}><UiIcon name="close" /></button></span>) : <small>Henüz panel markası eklenmedi.</small>}</div></section>
    </article>
    <article className="mapping-reference-card brand-mapping-console">
      <header className="mapping-builder-head"><h2><UiIcon name="link" />2. Yeni Marka Eşleme</h2><button type="button" className="secondary" disabled={!connectionId || syncBrands.isPending} onClick={() => syncBrands.mutate()}><UiIcon name={syncBrands.isPending ? 'loader' : 'refresh'} />{syncBrands.isPending ? 'Kuyruğa alınıyor…' : 'Platformdan Veri Güncelle'}</button></header>
      <div className="mapping-pair-grid brand-mapping-pair-grid"><section className="mapping-side local"><div className="mapping-side-head"><span>Panel kataloğu</span><small>Ürünlerinizde kullandığınız yerel markayı seçin.</small></div><SearchableSelect label="Panel Markası" value={localId} options={activeLocalBrands.map(item => ({ value: item.id, label: item.name, description: 'Panel markası' }))} placeholder="Panel markalarında ara" disabled={!connectionId} onChange={value => { setLocalId(value); setExternalId(''); setBrandExternalSearch(''); setBrandExternalPickerOpen(false); setNotice('') }} /></section><div className="mapping-link-rail" aria-hidden="true"><UiIcon name="link" /><small>Eşleştir</small></div><section className="mapping-side remote"><div className="mapping-side-head"><div className="mapping-side-head-copy"><span>Hedef Pazar Yeri &amp; Marka</span><small>Bağlantıyı seçin, güncel marka referansından karşılığını belirleyin.</small></div></div><div className="mapping-side-field"><span>Trendyol Markası</span><div className="mapping-target-controls brand-mapping-target-controls"><select aria-label="Hedef pazaryeri bağlantısı" value={connectionId} onChange={event => { setConnectionId(event.target.value); setLocalId(''); setExternalId(''); setBrandExternalSearch(''); setBrandExternalPickerOpen(false); setNotice('') }}><option value="">Trendyol bağlantısı</option>{trendyolConnections.map(item => <option value={item.id} key={item.id}>Trendyol{trendyolConnections.length > 1 ? ` · ${item.displayName}` : ''}</option>)}</select><div className="platform-category-picker-wrap"><button type="button" className="panel-category-picker-trigger" aria-expanded={brandExternalPickerOpen} onClick={() => setBrandExternalPickerOpen(value => !value)} disabled={!connectionId || !localId || references.isLoading || references.isError || syncBrands.isPending}><span>{selectedExternalBrand?.name ?? (references.isLoading || syncBrands.isPending ? 'Markalar yükleniyor…' : 'Platform markası seçin...')}</span><UiIcon name="chevronDown" /></button>{brandExternalPickerOpen && <div className="panel-category-picker platform-category-picker brand-reference-picker" role="dialog" aria-label="Platform markası seçimi"><div><input autoFocus aria-label="Platform markalarında ara" value={brandExternalSearch} onChange={event => setBrandExternalSearch(event.target.value)} placeholder="Marka adı veya marka no ara..." /><button type="button" aria-label="Platform marka menüsünü kapat" onClick={() => setBrandExternalPickerOpen(false)}><UiIcon name="close" /></button></div><div role="listbox">{visibleExternalBrands.length ? visibleExternalBrands.map(item => <button type="button" role="option" aria-selected={item.externalId === externalId} key={item.externalId} onClick={() => { setExternalId(item.externalId); setBrandExternalPickerOpen(false) }}><span>{item.name}</span><small>Marka no: {item.externalId}</small></button>) : <span>{references.isError ? 'Marka listesi alınamadı. Yenileme düğmesini kullanın.' : 'Aramayla eşleşen marka bulunamadı.'}</span>}</div></div>}</div></div></div></section></div>
      {connections.isError || localBrands.isError ? <ErrorBox error={connections.error ?? localBrands.error} /> : connectionId && (references.data || references.isError) ? <div className={`mapping-action brand-reference-action${references.isError ? ' has-error' : ''}`}><span>{references.data ? <><strong>{externalBrands.length.toLocaleString('tr-TR')}</strong> marka · {new Date(references.data.fetchedAt).toLocaleString('tr-TR')}{mapping.data ? ` · Kayıt v${mapping.data.version}` : ''}</> : 'Marka snapshot’ı bekleniyor. Yenileme düğmesini kullanın.'}</span>{references.data && <button type="button" disabled={!localId || !externalId || save.isPending || mapping.isLoading} onClick={() => save.mutate()}>{save.isPending ? 'Kaydediliyor…' : mapping.data ? 'Eşleştirmeyi Güncelle' : 'Eşleştirmeyi Kaydet'}</button>}</div> : null}
    </article>
    {connectionId && <article className="mapping-reference-card mapping-saved-card brand-mapping-saved-card"><header><h2>3. Kayıtlı Marka Eşleştirmeleri</h2><div><span className="mapping-record-count">{savedBrandMappings.length.toLocaleString('tr-TR')} kayıt</span></div></header><div className="mapping-saved-table"><table><thead><tr><th>Panel Markası</th><th>Platform</th><th>Trendyol Markası</th><th>Durum</th><th>İşlemler</th></tr></thead><tbody>{brandMappings.isLoading ? <tr><td className="mapping-empty-table-cell" colSpan={5}>Kayıtlı marka eşleştirmeleri yükleniyor…</td></tr> : brandMappings.isError ? <tr><td className="mapping-empty-table-cell" colSpan={5}>Kayıtlı marka eşleştirmeleri alınamadı.</td></tr> : savedBrandMappings.length ? savedBrandMappings.map(item => <tr key={item.id} className={editingBrandMappingId === item.id ? 'is-editing' : ''}><td>{localBrandById.get(item.localId)?.name ?? item.localId}</td><td><span className="ms-platform"><img className={platformLogoClass('TRENDYOL')} src={platformLogoSource('TRENDYOL') ?? '/platforms/trendyol.png'} alt="" aria-hidden="true" />Trendyol</span></td><td className="brand-mapping-inline-editor">{editingBrandMappingId === item.id ? <SearchableSelect label="" value={externalId} options={externalBrands.map(brand => ({ value: brand.externalId, label: brand.name, description: `Trendyol marka no: ${brand.externalId}` }))} placeholder="Trendyol markası ara" disabled={references.isLoading || references.isError} onChange={setExternalId} /> : externalBrandById.get(item.externalId)?.name ?? item.externalId}</td><td><span className={`mapping-table-status ${item.status === 'VERIFIED' || item.status === 'ACTIVE' ? 'active' : 'error'}`}><MappingStateGlyph state={item.status === 'VERIFIED' || item.status === 'ACTIVE' ? 'done' : 'partial'} />{mappingStatusLabel(item.status)}</span></td><td><div className="mapping-row-actions">{editingBrandMappingId === item.id ? <><button type="button" className="mapping-icon-button" aria-label="Marka eşleştirmesini kaydet" disabled={!externalId || save.isPending} onClick={() => save.mutate()}><UiIcon name="check" /></button><button type="button" className="mapping-icon-button" aria-label="Marka düzenlemeyi iptal et" onClick={() => { setEditingBrandMappingId(''); setExternalId(item.externalId) }}><UiIcon name="close" /></button></> : <><button type="button" className="mapping-icon-button" aria-label="Marka eşleştirmesini düzenle" onClick={() => { setLocalId(item.localId); setExternalId(item.externalId); setEditingBrandMappingId(item.id); setBrandEditOpen(false); setNotice('') }}><UiIcon name="edit" /></button><button type="button" className="mapping-icon-button mapping-delete-button" aria-label="Eşleştirmeyi sil" disabled={removeMapping.isPending} onClick={() => { if (window.confirm('Bu marka eşleştirmesi kaldırılsın mı?')) removeMapping.mutate(item) }}><MappingDeleteIcon /></button></>}</div></td></tr>) : <tr><td className="mapping-empty-table-cell" colSpan={5}>Henüz kaydedilmiş marka eşleştirmesi bulunmuyor.</td></tr>}</tbody></table></div><footer><span>Toplam {savedBrandMappings.length.toLocaleString('tr-TR')} eşleştirme</span></footer></article>}
    {brandTransferOpen && brandTransferBundle && <div className="workspace-modal-backdrop mapping-transfer-backdrop" role="presentation" onMouseDown={() => { if (!brandTransferBusy) { setBrandTransferOpen(false); setBrandTransferBundle(null) } }}><section className="workspace-modal mapping-transfer-modal" role="dialog" aria-modal="true" aria-labelledby="brand-transfer-title" onMouseDown={event => event.stopPropagation()}><header><div><p className="eyebrow">MARKA YEDEĞİ</p><h2 id="brand-transfer-title">İçe aktarma alanlarını seçin</h2><p>{brandTransferBundle.brands.length} marka · {brandTransferBundle.mappings.length} eşleştirme</p></div><button type="button" className="modal-close" onClick={() => { setBrandTransferOpen(false); setBrandTransferBundle(null) }} disabled={brandTransferBusy} aria-label="Marka içe aktarmayı kapat"><UiIcon name="close" /></button></header><div className="mapping-transfer-options"><label><input type="checkbox" checked={brandTransferSelection.brands} onChange={event => setBrandTransferSelection(current => ({ ...current, brands: event.target.checked }))} /><span><strong>Panel markaları</strong><small>Eksik markaları ekler, pasif markaları yeniden etkinleştirir.</small></span></label><label><input type="checkbox" checked={brandTransferSelection.mappings} onChange={event => setBrandTransferSelection(current => ({ ...current, mappings: event.target.checked }))} /><span><strong>Marka eşleştirmeleri</strong><small>Mevcut Trendyol marka referanslarıyla eşleştirmeleri günceller.</small></span></label></div><p className="mapping-transfer-warning">Mevcut kayıtlar silinmez; aynı marka adları güncellenir, eksik olanlar eklenir. Güncel Trendyol marka referansında bulunmayan eşleştirmeler atlanır.</p><footer><button type="button" className="secondary" onClick={() => { setBrandTransferOpen(false); setBrandTransferBundle(null) }} disabled={brandTransferBusy}>Vazgeç</button><button type="button" onClick={() => void importBrandMappings()} disabled={brandTransferBusy}>{brandTransferBusy ? 'İçe aktarılıyor…' : 'Seçilenleri Güncelle'}</button></footer></section></div>}
    {brandEditOpen && <div className="brand-mapping-modal-backdrop" role="presentation" onMouseDown={() => setBrandEditOpen(false)}><section className="brand-mapping-modal" role="dialog" aria-modal="true" aria-labelledby="brand-mapping-edit-title" onMouseDown={event => event.stopPropagation()}><header><div><small>Marka eşleme</small><h2 id="brand-mapping-edit-title">Marka eşleştirmesini düzenle</h2><p>Panel markası için Trendyol karşılığını güncelleyin.</p></div><button type="button" className="modal-close" aria-label="Marka eşleme penceresini kapat" onClick={() => setBrandEditOpen(false)}><UiIcon name="close" /></button></header><div className="brand-mapping-modal-body"><div className="brand-mapping-selected-local"><small>Panel markası</small><strong>{localBrandById.get(localId)?.name ?? localId}</strong></div><SearchableSelect label="Trendyol markası" value={externalId} options={externalBrands.map(item => ({ value: item.externalId, label: item.name, description: `Trendyol marka no: ${item.externalId}` }))} placeholder={references.isLoading ? 'Markalar yükleniyor…' : 'Marka adı veya marka no ara'} disabled={references.isLoading || references.isError} onChange={setExternalId} /></div><footer><button type="button" className="secondary" onClick={() => setBrandEditOpen(false)}>Vazgeç</button><button type="button" disabled={!localId || !externalId || save.isPending || mapping.isLoading} onClick={() => save.mutate()}>{save.isPending ? 'Kaydediliyor…' : 'Değişikliği kaydet'}</button></footer></section></div>}
  </section>
}
