// Siparişler (/orders).
import type { ApiAnswer } from '../../../shared/api/api-answer'
import { useEffect, useEffectEvent, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, hubPoll } from '../../../shared/api'
import { StickyActionBar, cargoFilterOptions, cargoLabel } from '../../../shared/components'
import { sendWithVersionedIdempotencyKey } from '../../../shared/idempotency'
import { useMediaQuery } from '../../../shared/hooks/useMediaQuery'
import { NoticeToast } from '../../../shared/components/NoticeToast'
import { statusLabel } from '../../../shared/status-labels'
import { DateRangePicker } from '../../../shared/DateRangePicker'
import { RrAnchor, RrError, RrFilterCard, RrIcon, RrLoading, RrPageSize, titleTr } from '../../../shared/ui/rr-components'
import { appendNotification, normalizeNotificationKind, notificationError, type NotificationContext } from '../../../shared/notifications'
import { orderLinkOnlyTabs, orderStatusLabel, orderStatusMoreTabs, orderStatusTabs, resolveOrderStatusTab } from '../order-status-tabs'
import { useTabSlide } from '../../../shared/tab-slide'
import { loadPrintedShippingLabels, useShippingLabelSettings, type ShippingLabelFormat } from '../../shipping'
import { isActiveMarketplaceConnection, isMarketplacePlatformSelected, marketplacePlatformOptions } from '../marketplace-platform-support'
import { ViewTabsWithMore, viewTabLabel } from '../../../shared/view-tabs'
import { formatDateTime, istanbulDateKey, istanbulDayBoundary } from '../../../shared/format-date'
import { readUrlList, readUrlNumber, readUrlText, useListUrlSync } from '../../../shared/list-url-state'
import { initialSort, rememberSort, storedPageSize } from '../../../shared/sticky-sort'
import { useCursorPager } from '../../../shared/list/useCursorPager'
import { useListSelection } from '../../../shared/list/useListSelection'
import { OrderMetricCards } from './OrderMetricCards'
import { useMetricSource } from '../use-metric-source'
import { useResendReminder } from './resend-reminder'
import { ManualResendDialog } from './ManualResendDialog'
import { orderCountText, orderTabCount, summaryMetric, type OrderFacets } from './order-summary'
import { orderExportNotice, ordersToExport, readAllOrderPages } from './order-export'
import { applyFilterPatch } from './order-filter-apply'
import { isCancelTab, tabDateFilters, tabKind } from './order-date-filters'
import { activeDayCard, cancellationStores, dailyCancellationCounts, dayCardCloseFilters, dayCardFilters, microPendingParams, resentPendingParams, storeCounts, storeUnits, type DayCard } from './daily-cards'
import { Connection, IntegrationFeedback, Order, Page, ReturnColumnPop, ReturnHeadCell, ReturnPlatformPop, Shipment, displayOrderNumber, orderDisplayShipment, productLineQuantity } from '../shared'
import { insideSelector, useDismissable } from '../../../shared/hooks/useDismissable'
import { ListEmptyState } from '../../../shared/components/EmptyState'
import { connectionQueries, queryKeys } from '../../../shared/api/query-keys'
import { invoiceCreationDisabledHelp, type ProductImagePreview, isMicroExportLabelBlocked, isPendingAction, type IntegrationFeedbackHandler, orderNotificationContext, canChangeOrderCargo } from './orders-page/order-page-helpers'
import { ProductImagePreviewModal, InvoiceInfoModal, InvoiceDraftModal, InvoiceViewerModal, InvoiceUploadModal } from './orders-page/OrderInvoiceModals'
import { CourierChangeModal, ShippingLabelModal, ShippingLabelBatchModal, SingleOrderSyncModal } from './orders-page/OrderShippingModals'
import { OrderReferenceRow } from './orders-page/OrderReferenceRow'
import { PendingRows, useProgressiveRows } from '../../../shared/progressive-rows'
import { HepsiburadaPackageCreateModal, ShopifyOrderCsvImportModal, ShopifyOrderStatusModal, ShopifyBulkStatusModal } from './orders-page/OrderPlatformModals'

const orderStatusTabOrder = orderStatusTabs.map(([value]) => value)

type OrderSort = 'DATE_DESC' | 'DATE_ASC' | 'DUE_DESC' | 'DUE_ASC' | 'SHIP_DESC' | 'SHIP_ASC'
type OrderFilters = { search: string; status: string; platform: string; platforms: string[]; listing: string; cargo: string; invoice: string; invoiceType: string; invoiceRegion: string; label: string; sort: OrderSort; dateFrom: string; dateTo: string; cancelFrom: string; cancelTo: string; cancelPart: string; kind: string }

// The shipping-deadline sort only means something for orders still to be shipped (Yeni, İşleme Alınanlar): other tabs
// neither offer it nor keep it.
const dueSortTabs = ['NEW', 'PROCESSING']
const orderSorts: readonly OrderSort[] = ['DATE_DESC', 'DATE_ASC', 'DUE_DESC', 'DUE_ASC', 'SHIP_DESC', 'SHIP_ASC']
const orderPageSizes = [20, 50, 100, 200]
// The list state an orders address carries (W16): filters, platforms, page size and page.
function orderListFromUrl(params: URLSearchParams) {
  const status = resolveOrderStatusTab(params.get('status'))
  const filters: OrderFilters = { ...initialOrderFilters, search: readUrlText(params, 'search'), status, listing: readUrlText(params, 'listing', 'ALL'), cargo: readUrlText(params, 'cargo', 'ALL'), invoice: readUrlText(params, 'invoice', 'ALL'), invoiceType: readUrlText(params, 'invoiceType', 'ALL'), invoiceRegion: readUrlText(params, 'invoiceRegion', 'ALL'), label: readUrlText(params, 'label', 'ALL'), sort: keepSort(status, initialSort('orders', orderSorts, 'DATE_DESC', params.get('sort'))), dateFrom: readUrlText(params, 'from'), dateTo: readUrlText(params, 'to'), cancelFrom: readUrlText(params, 'cancelFrom'), cancelTo: readUrlText(params, 'cancelTo'), cancelPart: readUrlText(params, 'cancelPart', 'ALL'), kind: readUrlText(params, 'kind', 'ALL') }
  return { filters: { ...filters, ...tabDateFilters(status, filters), kind: tabKind(status, filters.kind) }, platforms: readUrlList(params, 'platforms'), pageSize: readUrlNumber(params, 'size', storedPageSize('orders', orderPageSizes, 50), orderPageSizes), page: readUrlNumber(params, 'page', 1) }
}
const keepSort = (status: string, sort: OrderFilters['sort']): OrderFilters['sort'] => dueSortTabs.includes(status) || (sort !== 'DUE_ASC' && sort !== 'DUE_DESC') ? sort : 'DATE_DESC'
const initialOrderFilters: OrderFilters = { search: '', status: 'ALL', platform: 'ALL', platforms: [], listing: 'ALL', cargo: 'ALL', invoice: 'ALL', invoiceType: 'ALL', invoiceRegion: 'ALL', label: 'ALL', sort: 'DATE_DESC', dateFrom: '', dateTo: '', cancelFrom: '', cancelTo: '', cancelPart: 'ALL', kind: 'ALL' }
const orderStatusRank: Record<string, number> = { MANUAL_REVIEW: 110, RETURNED: 100, RETURN_IN_TRANSIT: 90, DELIVERED: 80, UNDELIVERED: 70, SHIPPED: 60, READY_TO_SHIP: 50, ON_HOLD: 40, PROCESSING: 30, PARTIALLY_CANCELLED: 20, UNVERIFIED: 15, NEW: 10, PAYMENT_PENDING: 5, CANCELLED: 0 }

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
async function loadOrderPage(filters: OrderFilters, limit: number, after: string | null, page = 1, signal?: AbortSignal): Promise<Page<Order>> {
  const params = new URLSearchParams({ limit: String(limit) })
  orderFilterParams(filters).forEach((value, key) => params.set(key, value))
  if (after) params.set('after', after)
  if (filters.status !== 'ALL') params.set('status', filters.status)
  params.set('sort', filters.sort)
  if (!after && page > 1) params.set('page', String(page))
  return hubApi<ApiAnswer<'getOrders'>>(`/orders?${params.toString()}`, { signal })
}
// The list filters as query parameters (status, sort and paging aside): the list and its tab counters
// read the same set (audit 2026-10-20 S12).
function orderFilterParams(filters: OrderFilters) {
  const params = new URLSearchParams()
  if (filters.search.trim()) params.set('search', filters.search.trim())
  // One unified platforms filter: the chosen platforms, else the single platform picked (the server reads platforms
  // first and the old single platform only without it, so the rows are the same).
  const platforms = filters.platforms.length ? filters.platforms : filters.platform !== 'ALL' ? [filters.platform] : []
  if (platforms.length) params.set('platforms', platforms.join(','))
  if (filters.listing !== 'ALL') params.set('listing', filters.listing)
  if (filters.cargo !== 'ALL') params.set('cargo', filters.cargo)
  if (filters.invoice !== 'ALL') params.set('invoice', filters.invoice)
  if (filters.invoiceType !== 'ALL') params.set('invoiceType', filters.invoiceType)
  if (filters.invoiceRegion !== 'ALL') params.set('invoiceRegion', filters.invoiceRegion)
  // "Etiket Durumu" is the server's filter (labelPrintedAt kept per package).
  if (filters.label !== 'ALL') params.set('labelPrinted', filters.label === 'PRINTED' ? 'true' : 'false')
  if (filters.dateFrom) params.set('dateFrom', istanbulDayBoundary(filters.dateFrom))
  if (filters.dateTo) params.set('dateTo', istanbulDayBoundary(filters.dateTo, true))
  // Orders with a line cancelled on these Istanbul days (the dashboard's İptaller card opens today's).
  if (filters.cancelFrom) params.set('cancelFrom', istanbulDayBoundary(filters.cancelFrom))
  if (filters.cancelTo) params.set('cancelTo', istanbulDayBoundary(filters.cancelTo, true))
  // Orders with some products cancelled, open or closed (a delivered one is not in the Kısmi İptal tab).
  if (filters.cancelPart === 'PARTIAL') params.set('partialCancel', 'true')
  // Trendyol micro-export orders: the orders the row's flag icon marks (the summary card counts the same ones).
  if (filters.kind === 'MICRO') params.set('micro', 'true')
  // Only the resends whose package has not been handed to the carrier (the list applies it in the Yeniden Gönderimler tab).
  if (filters.kind === 'RESENT_UNSHIPPED') params.set('resentNotShipped', 'true')
  return params
}
async function loadAllOrderPages(filters: OrderFilters): Promise<Page<Order> & { truncated: boolean; serverTotal: number }> {
  const read = await readAllOrderPages((limit, page) => loadOrderPage(filters, limit, null, page))
  return { items: read.items, nextCursor: null, hasMore: false, totalCount: read.items.length, truncated: read.truncated, serverTotal: read.totalCount }
}

// A row of the page with its store's invoice setting, as a pick keeps it (useListSelection).
type SelectableOrder = Order & { invoiceCreationEnabled: boolean }

function patchOrderShipment(order: Order, updatedShipment: Shipment): Order {
  const packages = (order.packages?.map(packageItem => packageItem.id === updatedShipment.id ? { ...packageItem, ...updatedShipment } : packageItem) ?? [updatedShipment]).sort((left, right) => new Date(right.statusOccurredAt).getTime() - new Date(left.statusOccurredAt).getTime())
  const primaryPackage = orderDisplayShipment({ ...order, packages })
  return { ...order, packages, derivedStatus: aggregateOrderStatus(packages.map(item => item.status)), cargoProviderName: primaryPackage?.cargoProviderName ?? order.cargoProviderName, cargoTrackingNumber: primaryPackage?.cargoTrackingNumber ?? order.cargoTrackingNumber }
}
function moveOrderAcrossStatusCaches(client: ReturnType<typeof useQueryClient>, order: Order, updatedShipment: Shipment) {
  const updatedOrder = patchOrderShipment(order, updatedShipment)
  client.setQueriesData<Page<Order>>({ queryKey: queryKeys.orders('page') }, current => current ? { ...current, items: current.items.map(item => item.id === order.id ? updatedOrder : item) } : current)
  return updatedOrder
}

export function OrdersPage() {
  const client = useQueryClient()
  const [processingOrderId, setProcessingOrderId] = useState<string | null>(null)
  const [hepsiburadaPackageOrder, setHepsiburadaPackageOrder] = useState<Order | null>(null)
  const [bulkShopifyStatus, setBulkShopifyStatus] = useState<{ kind: 'order' | 'invoice'; items: Order[] } | null>(null)
  const [searchParams, setSearchParams] = useSearchParams(); const requestedSearch = searchParams.get('search') ?? ''; const [initialList] = useState(() => orderListFromUrl(searchParams)); const [filterForm, setFilterForm] = useState<OrderFilters>(initialList.filters); const [filters, setFilters] = useState<OrderFilters>(initialList.filters); const [selectedPlatforms, setSelectedPlatforms] = useState<string[]>(initialList.platforms); const [pageSize, setPageSize] = useState(initialList.pageSize); const [jumpPage, setJumpPage] = useState(''); const [menu, setMenu] = useState<{ orderId: string; kind: 'invoice' | 'actions' } | null>(null); const [bulkNotice, setBulkNotice] = useState(''); const [bulkNoticeKind, setBulkNoticeKind] = useState<IntegrationFeedback['kind'] | 'pending'>('success'); const [bulkNoticeVersion, setBulkNoticeVersion] = useState(0); const [exporting, setExporting] = useState(false); const [invoiceInfoOrder, setInvoiceInfoOrder] = useState<Order | null>(null); const [invoiceViewerOrder, setInvoiceViewerOrder] = useState<Order | null>(null); const [invoiceDraftOrder, setInvoiceDraftOrder] = useState<Order | null>(null); const [invoiceUploadOrder, setInvoiceUploadOrder] = useState<Order | null>(null); const [shopifyStatusOrder, setShopifyStatusOrder] = useState<Order | null>(null); const [shopifyCsvImportOpen, setShopifyCsvImportOpen] = useState(false); const [courierOrder, setCourierOrder] = useState<{ item: Order; items: Order[] } | null>(null); const [shippingLabel, setShippingLabel] = useState<{ item: Order; format: ShippingLabelFormat } | null>(null); const [shippingLabelBatch, setShippingLabelBatch] = useState<{ items: Order[]; format: ShippingLabelFormat } | null>(null); const [singleSyncOpen, setSingleSyncOpen] = useState(false); const [previewImage, setPreviewImage] = useState<ProductImagePreview | null>(null); const [, setColumnFilterOpen] = useState<'cargo' | 'invoice' | 'label' | 'status' | null>(null); const { settings: labelSettings } = useShippingLabelSettings(); const [printedLabels, setPrintedLabels] = useState<Set<string>>(() => new Set(loadPrintedShippingLabels()))
  useEffect(() => {
    const refreshPrintedLabels = () => setPrintedLabels(new Set(loadPrintedShippingLabels()))
    window.addEventListener('storage', refreshPrintedLabels)
    return () => window.removeEventListener('storage', refreshPrintedLabels)
  }, [])
  function showBulkNotice(message: string, kind: IntegrationFeedback['kind'] | 'pending' = 'success', context?: NotificationContext) {
    // A running operation stays visible until its result replaces it or the user closes it.
    const normalizedKind = kind === 'pending' ? kind : normalizeNotificationKind(message, kind)
    setBulkNotice(message); setBulkNoticeKind(normalizedKind)
    if (normalizedKind !== 'pending') appendNotification({ ...context, message, kind: normalizedKind })
    setBulkNoticeVersion(current => current + 1)
  }
  // A modal about one order: its notices name that order and its store.
  const orderFeedback = (item: Order): IntegrationFeedbackHandler => (message, kind, context) => showBulkNotice(message, kind, { ...orderNotificationContext(item), ...context })
  useEffect(() => {
    if (!bulkNotice || bulkNoticeKind === 'pending') return
    const timeout = window.setTimeout(() => setBulkNotice(''), 5000)
    return () => window.clearTimeout(timeout)
  }, [bulkNotice, bulkNoticeKind, bulkNoticeVersion])
  // Another filter, tab, search, sort, platform or page size goes back to page 1, forgets the cursors and clears the
  // selection; a page change keeps it, and the orders picked on other pages count and take part in every bulk action
  // from the copy taken when they were picked (owner, 16 Nov 2026, WEB-SAYFALAR-11).
  const orderListKey = JSON.stringify([filters, selectedPlatforms, pageSize])
  const pager = useCursorPager({ resetKey: orderListKey, initialPage: initialList.page })
  const page = pager.page
  const setPage = pager.setPage
  const { rememberCursor } = pager
  const selection = useListSelection<SelectableOrder>({ filterKey: orderListKey, rowOf: id => pageItems.find(item => item.id === id) })
  const selectedIds = selection.selectedIds
  const setSelectedIds = selection.set
  // "Etiket Durumu" is filtered by the server (labelPrintedAt kept per package), like every other list filter: an address
  // opened with it reads one page (WEB-SAYFALAR-8: it read the whole order history first, until the first answer said
  // the server filters it).
  const orderCursor = pager.cursor
  const orderQueryFilters: OrderFilters = { ...filters, sort: keepSort(filters.status, filters.sort), platform: 'ALL', platforms: selectedPlatforms ?? [] }
  const ordersQuery = useQuery({ queryKey: queryKeys.orders('page', orderQueryFilters, pageSize, page, orderCursor), queryFn: ({ signal }) => loadOrderPage(orderQueryFilters, pageSize, orderCursor, page, signal), placeholderData: keepPreviousData, staleTime: 30_000, refetchOnWindowFocus: true })
  // The tab counters count the orders each tab lists for the same filters (date, search, platforms, cargo,
  // invoice, label), so a counter equals its tab's total (audit 2026-10-20 S12).
  const summaryParams = orderFilterParams(orderQueryFilters).toString()
  // The column filters' counts: orders per platform and cargo under the other filters and the status tab.
  const facetParams = new URLSearchParams(summaryParams); if (orderQueryFilters.status !== 'ALL') facetParams.set('status', orderQueryFilters.status)
  const facets = useQuery({ queryKey: queryKeys.orders('facets', facetParams.toString()), queryFn: ({ signal }) => hubApi<ApiAnswer<'getOrdersFacets'>>(`/orders/facets?${facetParams.toString()}`, { signal }), staleTime: 30_000 })
  const facetCounts = (key: keyof OrderFacets) => facets.isError ? undefined : facets.data?.[key] ?? null
  // The cancellation cards count today (the Istanbul day) whatever the list's filters are; the cancellations
  // are the Genel Bakış card's own rows, so both count them the same way.
  const day = istanbulDateKey()
  // The resends that have not been handed to the carrier, counted as the Yeniden Gönderimler list counts them, per store.
  const resentNotShipped = useQuery({ queryKey: queryKeys.orders('summary', 'resent-not-shipped'), queryFn: ({ signal }) => hubPoll<ApiAnswer<'getOrdersSummary'>>(`/orders/summary?${resentPendingParams()}`, { signal }), staleTime: 30_000, refetchInterval: 60_000 })
  // The micro-export card: the pending micro-export orders (not yet handed to the carrier, any day) and their countries.
  const microPending = useQuery({ queryKey: queryKeys.orders('summary', 'micro-pending'), queryFn: ({ signal }) => hubPoll<ApiAnswer<'getOrdersSummary'>>(`/orders/summary?${microPendingParams()}`, { signal }), staleTime: 30_000, refetchInterval: 60_000 })
  const cancellationsToday = useQuery({ queryKey: queryKeys.dashboard('cancellations', day), queryFn: ({ signal }) => hubPoll<ApiAnswer<'getDashboardCancellations'>>(`/dashboard/cancellations?day=${day}`, { signal }), staleTime: 30_000, refetchInterval: 60_000 })
  const todayCancellations = dailyCancellationCounts(cancellationsToday.data?.stores)
  const summary = useQuery({ queryKey: queryKeys.orders('summary', 'filtered', summaryParams), queryFn: ({ signal }) => hubApi<ApiAnswer<'getOrdersSummary'>>(summaryParams ? `/orders/summary?${summaryParams}` : '/orders/summary', { signal }), placeholderData: keepPreviousData })
  // The bootstrap names the stores of the cards' hover lists; its unfiltered late total is never shown for the list.
  const lateOrdersQuery = useQuery({ queryKey: queryKeys.dashboardBootstrap(), queryFn: ({ signal }) => hubApi<ApiAnswer<'getDashboardBootstrap'>>('/dashboard/bootstrap', { signal }), staleTime: 30_000 })
  // The Yeni Sipariş and İşleme Alınan cards' products per store: the filtered summary's units, named by the bootstrap's stores.
  const newUnits = storeUnits(summary.data?.newUnitsByConnection, lateOrdersQuery.data?.channels, summary.data?.newByConnection)
  const processingUnits = storeUnits(summary.data?.processingUnitsByConnection, lateOrdersQuery.data?.channels, summary.data?.processingByConnection)
  // The stores of the Geciken, İptal and Yeniden Gönderim cards (their hover).
  const channels = lateOrdersQuery.data?.channels
  const lateStores = storeCounts(summary.data?.lateByConnection, channels)
  const cancelStores = cancellationStores(cancellationsToday.data?.stores, channels)
  const resentStores = storeCounts(resentNotShipped.data?.resentByConnection, channels, resentNotShipped.data?.resentExchangeByConnection)
  const resendReminder = useResendReminder()
  // The Yeniden Gönderim gear's window: re-shipments added by marketplace and order number (owner, 23 Nov 2026).
  const [manualResendOpen, setManualResendOpen] = useState(false)
  const noBuyer = useMediaQuery('(min-width: 1025px) and (max-width: 1240px)')
  const connections = useQuery(connectionQueries.list<Connection>())
  const providers = connections.data?.items.filter(x => x.platformCode === 'TRENDYOL_EFATURAM' && !x.lastErrorCode && (x.status === 'ACTIVE' || x.status === 'VERIFIED')) ?? []
  const marketplaceConnections = connections.data?.items.filter(isActiveMarketplaceConnection) ?? []
  // Without an active marketplace connection no card has a data source: its big number is a dash, not a zero (shared/metric-value.ts).
  const noCardSource = useMetricSource() === false
  const cardSource = <T extends { value: number | undefined }>(metric: T): T => noCardSource ? { ...metric, value: undefined } : metric
  const shopifyConnections = connections.data?.items.filter(x => x.platformCode === 'SHOPIFY') ?? []
  const allOrders = ordersQuery.data?.items ?? [];
  const all = allOrders.filter(item => isMarketplacePlatformSelected(selectedPlatforms, item.platformCode));
  const ordersLoading = ordersQuery.isPending; const ordersError = ordersQuery.error
  // Every cargo of the filtered orders (the facet counts), with the page's own and the picked one.
  const cargos = Array.from(new Set([...Object.keys(facets.data?.cargos ?? {}).sort((left, right) => left.localeCompare(right, 'tr-TR')), ...all.flatMap(item => [orderDisplayShipment(item)?.cargoProviderName ?? item.cargoProviderName].filter((value): value is string => !!value)), ...(filters.cargo !== 'ALL' ? [filters.cargo] : [])]))
  const platformOptions = marketplacePlatformOptions(connections.data?.items ?? [], allOrders.map(item => ({ platformCode: item.platformCode, displayName: item.platformDisplayName })))
  const invoiceStatuses = [['FATURA_BEKLIYOR', 'Fatura Bekliyor'], ['FATURA_ISLENIYOR', 'Fatura İletiliyor'], ['FATURA_PLATFORMA_AKTARILMADI', 'Fatura İletilemedi'], ['FATURA_INCELEME_GEREKLI', 'İnceleme Gerekli'], ['FATURA_KONTROLDE', 'Faturası Mevcut'], ['FATURA_REDDEDILDI', 'Fatura Oluşturulamadı'], ['FATURA_BELIRSIZ', 'Fatura Sonucu Belirsiz'], ['FATURA_IPTAL', 'Fatura İptal']] as const
  // The cancellation cards show today and the micro-export card the pending ones; each opens the list on exactly what it counted.
  const openCard = activeDayCard(filters)
  const openDayCard = (card: DayCard) => { const patch = card === openCard ? dayCardCloseFilters() : dayCardFilters(card, istanbulDateKey()); const next = applyFilterPatch(filters, filterForm, { ...patch, sort: keepSort(patch.status, filterForm.sort) }); setFilterForm(next.form); setFilters(next.applied); setPage(1) }
  const applyFilterValue = <K extends keyof OrderFilters>(key: K, value: OrderFilters[K]) => { if (key === 'sort') rememberSort('orders', String(value)); const next = applyFilterPatch(filters, filterForm, { [key]: value } as Partial<OrderFilters>); setFilterForm(next.form); setFilters(next.applied); setPage(1) }
  // Temizle is grey while every filter, applied or still typed in the card, is at its default.
  const hasFiltersToClear = (selectedPlatforms ?? []).length > 0 || [filters, filterForm].some(form => (Object.keys(initialOrderFilters) as Array<keyof OrderFilters>).some(key => key !== 'platform' && key !== 'platforms' && key !== 'sort' && form[key] !== initialOrderFilters[key]))
  const resetFilters = () => { const next = { ...initialOrderFilters, sort: filters.sort }; setFilterForm(next); setFilters(next); setSelectedPlatforms([]); setPage(1); setColumnFilterOpen(null); setSearchParams(current => { const params = new URLSearchParams(current); params.delete('search'); params.delete('status'); return params }, { replace: true }) }
  const clearInvoiceFilters = () => { const next = applyFilterPatch(filters, filterForm, { invoice: 'ALL', invoiceType: 'ALL', invoiceRegion: 'ALL' }); setFilterForm(next.form); setFilters(next.applied); setPage(1) }
  const selectStatus = (requestedStatus: string) => { const status = resolveOrderStatusTab(requestedStatus); const next = { ...filterForm, ...tabDateFilters(status, filterForm), kind: tabKind(status, filterForm.kind), status, sort: keepSort(status, filterForm.sort) }; setFilterForm(next); setFilters(current => ({ ...current, ...tabDateFilters(status, current), kind: tabKind(status, current.kind), status, sort: keepSort(status, current.sort) })); setSearchParams(current => { const params = new URLSearchParams(current); if (status === 'ALL') params.delete('status'); else params.set('status', status); return params }, { replace: true }); setPage(1) }
  // A tab's counter under the list's filters; a failed summary or a counter it does not give reads "—" (never 0, never
  // the dashboard's unfiltered total).
  const tabCount = (tab: string) => orderTabCount(tab, summary.data)
  // The same figure as the Yeniden Gönderim card: the resends not handed to the carrier, hand-added ones included.
  const resentWaiting = resentNotShipped.isError ? 0 : resentNotShipped.data?.resent ?? 0
  const tabCountText = (tab: string) => orderCountText(tabCount(tab), { loading: summary.isLoading, error: summary.isError })
  const todayCancellationMetric = (pick: (counts: NonNullable<typeof todayCancellations>) => number | undefined) => cancellationsToday.isError ? { value: undefined, loading: false, error: true } : { value: todayCancellations ? pick(todayCancellations) : undefined, loading: !todayCancellations, error: false }
  // Status, search, platform, cargo, listing, date range, sort and pagination
  // are applied by the API. Only local presentation filters remain here.
  const items = all
  async function exportOrders() {
    setExporting(true)
    try {
    // The orders the list shows: filtered by the server's labelPrintedAt (the list's own filter); never re-filtered here
    // with this browser's print marks, so the file and the list always agree.
    const read = await loadAllOrderPages(orderQueryFilters)
    const exportItems = ordersToExport(items, read)
    const cut = read
    // A text cell that a spreadsheet would read as a formula starts with an apostrophe (audit 2026-10-20 S19).
    const escapeCsv = (value: unknown) => `"${csvSafeText(String(value ?? '')).replaceAll('"', '""')}"`
    const rows = [
      ['Sipariş No', 'Platform', 'Müşteri', 'Sipariş Tarihi', 'Durum', 'Fatura Durumu', 'Kargo', 'Ürünler', 'Toplam Tutar', 'Para Birimi'],
      ...exportItems.map(item => [
        item.orderNumber,
        item.platformDisplayName || item.platformCode,
        item.customerName,
        formatDateTime(item.orderedAt),
        titleTr(orderStatusLabel(item.derivedStatus)),
        item.invoiceStatus ? titleTr(statusLabel(item.invoiceStatus)) : '',
        cargoLabel(orderDisplayShipment(item)?.cargoProviderName ?? item.cargoProviderName),
        (item.lines ?? []).filter(line => productLineQuantity(line) > 0).map(line => `${line.title} x${productLineQuantity(line)}`).join(' | '),
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
    // Stopped at the cap: said plainly, never a silent partial file.
    const notice = orderExportNotice(rows.length - 1, { truncated: cut?.truncated ?? false, totalCount: cut?.serverTotal ?? rows.length - 1 })
    showBulkNotice(notice.message, notice.kind)
    } catch (reason) {
      showBulkNotice(reason instanceof Error ? reason.message : 'Sipariş dışa aktarımı tamamlanamadı.', 'error')
    } finally {
      setExporting(false)
    }
  }
  const totalOrderCount = ordersQuery.data?.totalCount ?? items.length
  const totalPages = Math.max(1, Math.ceil(totalOrderCount / pageSize))
  const pageItems = items.map(item => ({ ...item, invoiceCreationEnabled: connections.data?.items.find(connection => connection.id === item.connectionId)?.invoiceCreationEnabled ?? false })); const selectablePageItems = pageItems.filter(item => item.derivedStatus.toUpperCase() !== 'CANCELLED'); const allPageSelected = selectablePageItems.length > 0 && selectablePageItems.every(item => selectedIds.includes(item.id)); const selectedOrders = selection.selectedRows(pageItems, item => item.id); const shopifySelectedOrders = selectedOrders.filter(item => item.platformCode === 'SHOPIFY'); const cargoEditableOrders = selectedOrders.filter(canChangeOrderCargo); const cargoChangeHelp = cargoEditableOrders.length < selectedOrders.length ? `Kargo firması yalnız kargoya verilmemiş Trendyol paketlerinde değiştirilebilir; seçili ${selectedOrders.length - cargoEditableOrders.length} sipariş atlanır.` : undefined; const shopifyInvoiceCreationBlocked = shopifySelectedOrders.some(item => !item.invoiceId && !item.invoiceCreationEnabled); const labelBatchItems = selectedOrders.filter(item => Boolean(orderDisplayShipment(item)?.cargoTrackingNumber ?? item.cargoTrackingNumber) && !isMicroExportLabelBlocked(item))
  const hasNextPage = Boolean(ordersQuery.data?.nextCursor)
  function goToPreviousPage() { setPage(current => Math.max(1, current - 1)) }
  function goToNextPage() { if (!hasNextPage) return; const next = ordersQuery.data?.nextCursor; if (next) rememberCursor(page + 1, next); setPage(current => current + 1) }
  function goToPage(nextPage: number) { const target = Math.min(Math.max(1, nextPage), totalPages); if (target === page) return; setPage(target) }
  // As before the move, the cursor is kept from the answer shown, a placeholder (the page before) included.
  useEffect(() => {
    const next = ordersQuery.data?.nextCursor
    if (next) rememberCursor(page + 1, next)
  }, [ordersQuery.data?.nextCursor, page, rememberCursor])
  useListUrlSync({ search: filters.search, status: filters.status, platforms: selectedPlatforms, listing: filters.listing, cargo: filters.cargo, invoice: filters.invoice, invoiceType: filters.invoiceType, invoiceRegion: filters.invoiceRegion, label: filters.label, sort: filters.sort, from: filters.dateFrom, to: filters.dateTo, cancelFrom: filters.cancelFrom, cancelTo: filters.cancelTo, cancelPart: filters.cancelPart, kind: filters.kind, size: pageSize, page }, { status: 'ALL', listing: 'ALL', cancelPart: 'ALL', kind: 'ALL', cargo: 'ALL', invoice: 'ALL', invoiceType: 'ALL', invoiceRegion: 'ALL', label: 'ALL', sort: 'DATE_DESC', size: 50, page: 1 })
  // A link that brings a search (the quick search) while the list is open; the list's own address is not one.
  const applyRequestedSearch = useEffectEvent((value: string) => { if (!value || value === filters.search) return; setFilterForm(current => ({ ...current, search: value })); setFilters(current => ({ ...current, search: value })); setPage(1) })
  useEffect(() => { applyRequestedSearch(requestedSearch) }, [requestedSearch])
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
  useDismissable(Boolean(menu), [], () => setMenu(null), { isInside: insideSelector('.row-menu') })
  useEffect(() => { const invoiceId = searchParams.get('invoice'); if (!invoiceId || !all.length) return; const item = all.find(order => order.invoiceId === invoiceId); if (!item) return; setInvoiceViewerOrder(item); setSearchParams(current => { const next = new URLSearchParams(current); next.delete('invoice'); return next }, { replace: true }) }, [all, searchParams, setSearchParams])
  function updateSelection(id: string, checked: boolean) { if (all.find(item => item.id === id)?.derivedStatus.toUpperCase() === 'CANCELLED') return; setSelectedIds(current => checked ? Array.from(new Set([...current, id])) : current.filter(value => value !== id)) }
  function togglePageSelection(checked: boolean) { setSelectedIds(current => checked ? Array.from(new Set([...current, ...selectablePageItems.map(item => item.id)])) : current.filter(id => !selectablePageItems.some(item => item.id === id))) }
  async function processSingleOrder(item: Order) {
    if (processingOrderId) return
    setMenu(null)
    setProcessingOrderId(item.id)
    showBulkNotice(`Sipariş ${displayOrderNumber(item.orderNumber)} API yanıtı bekleniyor…`, 'info')
    try {
      // One key per order version: a double click or a resend after a lost answer reaches the same request record; after
      // an HTTP answer the next attempt gets a fresh key.
      const updatedShipment = await sendWithVersionedIdempotencyKey(`instant-picking:${item.id}:v${item.version}`, key => hubApi<Shipment>(`/orders/${item.id}/instant-process`, {
        method: 'POST',
        headers: { 'Idempotency-Key': key }
      }))
      moveOrderAcrossStatusCaches(client, item, updatedShipment)
      setSelectedIds(current => current.filter(id => id !== item.id))
      await client.refetchQueries({ queryKey: queryKeys.orders('summary'), type: 'active' })
      await client.refetchQueries({ queryKey: queryKeys.orders(), type: 'active' })
      showBulkNotice(`Sipariş ${displayOrderNumber(item.orderNumber)} anlık olarak işleme alındı ve panelde güncellendi.`, 'success', { ...orderNotificationContext(item), title: 'Sipariş İşleme Alındı', operation: 'Siparişi işleme alma' })
    } catch (error) {
      if (isPendingAction(error)) { showBulkNotice(error.message, 'info', { ...orderNotificationContext(item), operation: 'Siparişi işleme alma' }); void client.invalidateQueries({ queryKey: queryKeys.orders() }) }
      else { const failure = notificationError(error, 'Sipariş işleme alınamadı.'); showBulkNotice(failure.message, 'error', { ...orderNotificationContext(item), title: 'Sipariş İşleme Alınamadı', operation: 'Siparişi işleme alma', code: failure.code }) }
    } finally {
      setProcessingOrderId(current => current === item.id ? null : current)
    }
  }

  const cargoOptions = [{ value: 'ALL', label: 'Tüm Kargolar' }, ...cargoFilterOptions(cargos)]
  const invoiceOptions = [{ value: 'ALL', label: 'Tüm Fatura Durumları' }, ...invoiceStatuses.map(([value, label]) => ({ value, label }))]
  const invoiceTypeOptions = [{ value: 'ALL', label: 'Tüm Türler' }, { value: 'BIREYSEL', label: 'Bireysel' }, { value: 'KURUMSAL', label: 'Kurumsal' }, { value: 'KURUMSAL_VERGI_NO_YOK', label: 'Kurumsal · Vergi No Yok' }]
  const invoiceRegionOptions = [{ value: 'ALL', label: 'Tüm Bölgeler' }, { value: 'TR', label: 'Türkiye' }, { value: 'MICRO_EXPORT', label: 'Mikro İhracat' }]
  const cancelPartOptions = [{ value: 'ALL', label: 'Tüm Siparişler' }, { value: 'PARTIAL', label: 'Kısmi İptali Olanlar' }]
  // Sipariş Türü: every order, the micro-export ones, and (in Yeniden Gönderimler only) the resends not yet handed to the carrier.
  const kindOptions = [{ value: 'ALL', label: 'Tüm Siparişler' }, { value: 'MICRO', label: 'Mikro İhracat Siparişleri' }, ...(filters.status === 'RESENT' ? [{ value: 'RESENT_UNSHIPPED', label: 'Kargoya Verilmeyenler' }] : [])]
  const labelOptions = [{ value: 'ALL', label: 'Tüm Etiketler' }, { value: 'PRINTED', label: 'Etiketi Yazdırılanlar' }, { value: 'NOT_PRINTED', label: 'Etiketi Yazdırılmayanlar' }]
  const statusOptions = orderStatusTabs.map(([value, label]) => ({ value, label: value === 'ALL' ? 'Tüm Siparişler' : label }))
  const sortOptions: Array<[OrderFilters['sort'], string]> = ([['DATE_DESC', 'En Yeni Siparişler'], ['DATE_ASC', 'En Eski Siparişler'], ['DUE_ASC', 'Kargo Termini Yaklaşanlar'], ['DUE_DESC', 'Kargo Termini Uzak Olanlar'], ['SHIP_DESC', 'Son Kargoya Verilenler'], ['SHIP_ASC', 'İlk Kargoya Verilenler']] as Array<[OrderFilters['sort'], string]>)
    .filter(([value]) => dueSortTabs.includes(filters.status) || (value !== 'DUE_ASC' && value !== 'DUE_DESC'))
  const sheetField = (label: string, current: string, options: Array<{ value: string; label: string }>, pick: (value: string) => void) => <div className="rr-fld rr-sheet-only"><small>{label}</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle}><span>{options.find(option => option.value === current)?.label ?? options[0]?.label}</span><RrIcon name="chev" /></button>}>{close => <ReturnColumnPop label={label} options={options} value={current} onPick={value => { pick(value); close() }} />}</RrAnchor></div>
  const invoicePop = (close: () => void) => { const group = (label: string, options: Array<{ value: string; label: string }>, current: string, key: 'invoice' | 'invoiceType' | 'invoiceRegion', clear?: import('react').ReactNode) => <><div className="rr-ph"><span>{label}</span>{clear}</div>{options.map((option, index) => { const on = current !== 'ALL' ? option.value === current : index === 0; return <button key={option.value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => { applyFilterValue(key, option.value); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{option.label}</span></button> })}</>; const any = filterForm.invoice !== 'ALL' || filterForm.invoiceType !== 'ALL' || filterForm.invoiceRegion !== 'ALL'; return <div className="rr-pop" role="dialog" aria-label="Fatura Filtresi" style={{ minWidth: 260 }}>{group('Fatura Durumu', invoiceOptions, filterForm.invoice, 'invoice', any ? <button type="button" className="rr-btn rr-link" onClick={() => { clearInvoiceFilters(); close() }}>Temizle</button> : null)}<div className="rr-sep" />{group('Fatura Türü', invoiceTypeOptions, filterForm.invoiceType, 'invoiceType')}<div className="rr-sep" />{group('Fatura Bölgesi', invoiceRegionOptions, filterForm.invoiceRegion, 'invoiceRegion')}</div> }
  const firstPage = Math.max(1, Math.min(page - 1, totalPages - 3)); const pageList = Array.from({ length: Math.min(4, totalPages) }, (_, index) => firstPage + index)
  const tabLabel = orderStatusTabs.find(([value]) => value === filters.status)?.[1] ?? 'Tümü'
  const shopifyBulkButtons = shopifySelectedOrders.length > 0 && <><button type="button" className="rr-btn" onClick={() => setBulkShopifyStatus({ kind: 'order', items: shopifySelectedOrders })}><RrIcon name="edit" />Shopify Sipariş Durumu ({shopifySelectedOrders.length})</button><button type="button" className="rr-btn" disabled={shopifyInvoiceCreationBlocked} title={shopifyInvoiceCreationBlocked ? invoiceCreationDisabledHelp : undefined} onClick={() => setBulkShopifyStatus({ kind: 'invoice', items: shopifySelectedOrders })}><RrIcon name="edit" />Shopify Fatura Durumu ({shopifySelectedOrders.length})</button></>
  // pageItems is rebuilt on every render, so the cards are keyed by what the orders hold rather than by the array.
  const pageItemsKey = JSON.stringify(pageItems)
  // The first rows are drawn in the frame that shows a tab or page, the rest right after it (shared/progressive-rows.tsx).
  const progressiveOrders = useProgressiveRows(pageItems, item => item.id)
  // The order cards are kept while their own inputs stay the same, so opening or closing a window (a label preview, an
  // invoice) does not redraw every card on the page first.
  // pageItems is rebuilt on every render, so the rows are memoised by its content (pageItemsKey); the handlers read only
  // state that is listed here or that their setters keep current.
  // eslint-disable-next-line react-hooks/exhaustive-deps -- keyed by pageItemsKey on purpose (see above)
  const orderRows = useMemo(() => progressiveOrders.rows.map(item => <OrderReferenceRow item={item} key={item.id} noBuyer={noBuyer} selected={selectedIds.includes(item.id)} processing={processingOrderId === item.id} onSelect={checked => updateSelection(item.id, checked)} openMenu={menu?.orderId === item.id ? menu.kind : null} onMenuChange={kind => setMenu(kind ? { orderId: item.id, kind } : null)} onInvoiceCreate={() => { setMenu(null); if (item.platformCode === 'SHOPIFY') setInvoiceUploadOrder(item); else setInvoiceDraftOrder(item) }} onInvoiceDetails={() => { setMenu(null); setInvoiceInfoOrder(item) }} onInvoiceUpload={() => { setMenu(null); setInvoiceUploadOrder(item) }} onCourierChange={() => { setMenu(null); setCourierOrder({ item, items: [item] }) }} onOrderStatusChange={() => { setMenu(null); setShopifyStatusOrder(item) }} onProcessOrder={() => void processSingleOrder(item)} onCreatePackage={() => { setMenu(null); setHepsiburadaPackageOrder(item) }} onPrintLabel={format => { if (isMicroExportLabelBlocked(item)) { showBulkNotice('Mikro ihracat faturası kesilmeden kargo etiketi yazdırılamaz.', 'error'); return } setMenu(null); setShippingLabel({ item, format }) }} onPreviewImage={setPreviewImage} labelSettings={labelSettings} printedLabels={printedLabels} tab={filters.status} showCancelled={filters.cancelPart === 'PARTIAL'} />), [pageItemsKey, progressiveOrders.rows.length, filters.status, filters.cancelPart, noBuyer, selectedIds, processingOrderId, menu, labelSettings, printedLabels])
  // A status tab slides the list in from its side and starts the page from its top (shared/tab-slide.tsx).
  const statusSlide = useTabSlide(filters.status, orderStatusTabOrder)
  return <section className="content f3 rr rr-orders">
    <div className="rr-ph"><div><div className="rr-crumb">Pazaryeri Operasyonu</div><h1>Siparişler</h1></div><div className="rr-acts"><button type="button" className="rr-btn" disabled={!items.length || exporting} aria-busy={exporting} title={exporting ? 'Dışa Aktarılıyor…' : undefined} onClick={() => void exportOrders()}><RrIcon name="export" />Dışa Aktar</button><button type="button" className="rr-btn" onClick={() => setShopifyCsvImportOpen(true)}><RrIcon name="import" />İçe Aktar</button><button type="button" className="rr-btn rr-primary" onClick={() => setSingleSyncOpen(true)}><RrIcon name="refresh" /><span className="rr-long">Sipariş Senkronizasyonu</span><span className="rr-short">Senkronize Et</span></button></div></div>
    <OrderMetricCards newOrders={cardSource(summaryMetric(summary, value => value.new))} newUnits={newUnits?.units} newRows={newUnits?.rows} newCargo={summary.data?.newByCargo} processing={cardSource(summaryMetric(summary, value => value.processing))} processingUnits={processingUnits?.units} processingRows={processingUnits?.rows} processingCargo={summary.data?.processingByCargo} late={cardSource(summaryMetric(summary, value => value.late))} microExport={cardSource(summaryMetric(microPending, value => value.microExport))} microCountries={microPending.isError ? undefined : microPending.data?.microExportCountries ?? undefined} resent={cardSource(summaryMetric(resentNotShipped, value => value.resent))} resentRows={resentStores} resentReminder={resendReminder.count} onOpenResentRecords={() => setManualResendOpen(true)} lateRows={lateStores} cancelledRows={cancelStores} onOpenDay={openDayCard} activeCard={openCard} cancelled={cardSource(todayCancellationMetric(counts => counts.whole + counts.partial))} cancelledUnits={todayCancellations?.wholeUnits} partiallyCancelled={todayCancellationMetric(counts => counts.partial)} partialUnits={todayCancellations?.partialUnits} />
    {bulkNotice && <NoticeToast key={bulkNoticeVersion} kind={bulkNoticeKind} message={bulkNotice} onClose={() => setBulkNotice('')} durationMs={bulkNoticeKind === 'pending' ? undefined : 5000} />}
    <div className="rr-fshell">
      <ViewTabsWithMore className="order-tabs" more={orderStatusMoreTabs} ariaLabel="Sipariş durumları" value={filters.status} onChange={selectStatus} items={orderStatusTabs.filter(([value]) => !orderLinkOnlyTabs.includes(value) || value === filters.status).map(([value, label]) => { const counted = summary.isError ? undefined : tabCount(value); return { value, label: viewTabLabel(({ ALL: 'all', NEW: 'new', PROCESSING: 'processing', SHIPPED: 'shipping', DELIVERED: 'delivered', CANCELLED: 'cancelled', RESENT: 'resend', ON_HOLD: 'paused', PARTIALLY_CANCELLED: 'partial', UNDELIVERED: 'attention', RETURN_IN_TRANSIT: 'return', RETURNED: 'return', MANUAL_REVIEW: 'review', PAYMENT_PENDING: 'waiting', UNVERIFIED: 'attention', PENDING: 'due', LATE: 'attention' } as const)[value], label), count: tabCountText(value), countTone: (value === 'NEW' && !summary.isLoading && (counted ?? 0) > 0) || (value === 'RESENT' && resentWaiting > 0) ? 'warn' as const : undefined } })} />
      <RrFilterCard active={[(selectedPlatforms ?? []).length > 0, Boolean(filters.dateFrom), Boolean(filters.cancelFrom), filters.cancelPart !== 'ALL', filters.kind !== 'ALL', filters.cargo !== 'ALL', filters.invoice !== 'ALL', filters.invoiceType !== 'ALL', filters.invoiceRegion !== 'ALL', filters.label !== 'ALL', filters.sort !== 'DATE_DESC'].filter(Boolean).length}><div className="rr-filters rr-has-sort">
        <label className="rr-fld rr-search"><small>Sipariş Ara</small><span style={{ position: 'relative' }}><RrIcon name="search" style={{ position: 'absolute', left: 11, top: 10, color: 'var(--rr-subtle)' }} /><input className="rr-in" style={{ paddingLeft: 34 }} aria-label="Sipariş ara" value={filterForm.search} onChange={event => setFilterForm(current => ({ ...current, search: event.target.value }))} onKeyDown={event => { if (event.key === 'Enter') { setFilters(filterForm); setPage(1) } }} placeholder="Sipariş no, müşteri, barkod" /></span></label>
        {!isCancelTab(filters.status) && <DateRangePicker className="rr-fld rr-date" from={filterForm.dateFrom} to={filterForm.dateTo} onChange={range => { const next = applyFilterPatch(filters, filterForm, { dateFrom: range.from, dateTo: range.to }); setFilterForm(next.form); setFilters(next.applied); setPage(1) }} label="Sipariş Tarihi" placeholder="Başlangıç – Bitiş" icon={<RrIcon name="cal" />} chevron={<RrIcon name="chev" />} />}
        {isCancelTab(filters.status) && <DateRangePicker className="rr-fld rr-date" from={filterForm.cancelFrom} to={filterForm.cancelTo} onChange={range => { const next = applyFilterPatch(filters, filterForm, { cancelFrom: range.from, cancelTo: range.to }); setFilterForm(next.form); setFilters(next.applied); setPage(1) }} label="İptal Tarihi" placeholder="Başlangıç – Bitiş" icon={<RrIcon name="cal" />} chevron={<RrIcon name="chev" />} />}
        <div className="rr-fld"><small>Sıralama</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} onClick={toggle}><span>{sortOptions.find(option => option[0] === keepSort(filters.status, filterForm.sort))?.[1]}</span><RrIcon name="chev" /></button>}>{close => <div className="rr-pop rr-right" role="listbox" style={{ minWidth: 250 }}>{sortOptions.map(([value, label]) => { const on = value === keepSort(filters.status, filterForm.sort); return <button key={value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => { applyFilterValue('sort', value); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{label}</span></button> })}</div>}</RrAnchor></div>
        {sheetField('Kargo Firması', filterForm.cargo, cargoOptions, value => applyFilterValue('cargo', value))}
        {sheetField('Fatura Durumu', filterForm.invoice, invoiceOptions, value => applyFilterValue('invoice', value))}
        {sheetField('Fatura Türü', filterForm.invoiceType, invoiceTypeOptions, value => applyFilterValue('invoiceType', value))}
        {sheetField('Fatura Bölgesi', filterForm.invoiceRegion, invoiceRegionOptions, value => applyFilterValue('invoiceRegion', value))}
        {sheetField('Etiket Durumu', filterForm.label, labelOptions, value => applyFilterValue('label', value))}
        {sheetField('Ürün İptali', filterForm.cancelPart, cancelPartOptions, value => applyFilterValue('cancelPart', value))}
        {sheetField('Sipariş Türü', filterForm.kind, kindOptions, value => applyFilterValue('kind', value))}
        {sheetField('Sipariş Durumu', filters.status, statusOptions, selectStatus)}
        <div className="rr-fbtns"><button type="button" className="rr-btn rr-clear" onClick={resetFilters} disabled={!hasFiltersToClear}>Temizle</button><button type="button" className="rr-btn rr-primary" onClick={() => { setFilters(filterForm); setPage(1) }}>Filtrele</button></div>
      </div></RrFilterCard>
    </div>
    {bulkShopifyStatus && <ShopifyBulkStatusModal kind={bulkShopifyStatus.kind} items={bulkShopifyStatus.items} onClose={() => setBulkShopifyStatus(null)} onFeedback={showBulkNotice} onUpdated={orderIds => setSelectedIds(current => current.filter(id => !orderIds.includes(id)))} />}
    <div key={statusSlide.key} className={`rr-panel${statusSlide.className ? ` ${statusSlide.className}` : ''}`}><div className="rr-panel-h"><div><h2>{tabLabel === 'Tümü' ? 'Tüm Siparişler' : tabLabel}</h2><p>Toplam {totalOrderCount.toLocaleString('tr-TR')} sipariş</p></div><RrPageSize remember="orders" value={pageSize} sizes={[20, 50, 100, 200]} onChange={setPageSize} /></div>
      <div className={`rr-tblwrap${ordersQuery.isPlaceholderData ? ' is-stale-data' : ''}`} aria-busy={ordersQuery.isPlaceholderData || undefined}>{ordersLoading && !all.length ? <RrLoading text="Yerel sipariş kayıtları yükleniyor…" /> : ordersError && !all.length ? <RrError error={ordersError} onRetry={() => void ordersQuery.refetch()} /> : <table className={`rr-tbl rr-has-select${noBuyer ? ' rr-no-buyer' : ''}`}>
        <colgroup><col className="rr-c-order" />{!noBuyer && <col className="rr-c-buyer" />}<col className="rr-c-prod" /><col className="rr-c-price" /><col className="rr-c-cargo" /><col className="rr-c-inv" /><col className="rr-c-act" /></colgroup>
        <thead><tr>
          <ReturnHeadCell label="Sipariş Bilgileri" active={selectedPlatforms.length > 0} extra={<input type="checkbox" className="rr-cb" checked={allPageSelected} disabled={!selectablePageItems.length} onChange={event => togglePageSelection(event.target.checked)} aria-label="Sayfadaki siparişleri seç" style={{ marginRight: 8 }} />} pop={() => <ReturnPlatformPop title="Platform" options={platformOptions} counts={facetCounts('platforms')} selected={selectedPlatforms} onChange={setSelectedPlatforms} />} />
          {!noBuyer && <ReturnHeadCell label="Alıcı" active={filters.kind !== 'ALL'} pop={close => <ReturnColumnPop label="Sipariş Türü" options={kindOptions} value={filterForm.kind} onPick={value => { applyFilterValue('kind', value); close() }} />} />}
          <ReturnHeadCell label="Bilgiler" active={filters.cancelPart !== 'ALL'} pop={close => <ReturnColumnPop label="Ürün İptali" options={cancelPartOptions} value={filterForm.cancelPart} onPick={value => { applyFilterValue('cancelPart', value); close() }} />} />
          <ReturnHeadCell label="Birim Fiyat" />
          <ReturnHeadCell label="Kargo" active={filters.cargo !== 'ALL'} pop={close => <ReturnColumnPop label="Kargo Firması" width={280} options={cargoOptions} counts={facetCounts('cargos')} value={filterForm.cargo} onPick={value => { applyFilterValue('cargo', value); close() }} />} />
          <ReturnHeadCell label="Fatura" active={filters.invoice !== 'ALL' || filters.invoiceType !== 'ALL' || filters.invoiceRegion !== 'ALL'} pop={invoicePop} />
          <ReturnHeadCell label="İşlemler" active={filters.label !== 'ALL'} pop={close => <ReturnColumnPop label="Etiket Durumu" options={labelOptions} value={filterForm.label} right onPick={value => { applyFilterValue('label', value); close() }} />} />
        </tr></thead>
        <tbody ref={progressiveOrders.containerRef}>{!pageItems.length ? <tr><td className="rr-empty-row" colSpan={noBuyer ? 6 : 7}><ListEmptyState icon="orders" noun="Sipariş" hasActiveFilters={hasFiltersToClear} filteredHint="Seçili filtrelerle eşleşen sipariş yok." emptyHint="Pazaryerlerinden gelen siparişler burada listelenir." onClear={resetFilters} /></td></tr> : <>{orderRows}<PendingRows height={progressiveOrders.pendingHeight} colSpan={noBuyer ? 6 : 7} /></>}</tbody>
      </table>}</div>
      {pageItems.length > 0 && <div className="rr-pager"><span>Toplam <b style={{ color: 'var(--rr-ink)' }}>{totalOrderCount.toLocaleString('tr-TR')}</b> adet</span><div className="rr-pages"><button type="button" aria-label="Önceki" disabled={page <= 1 || ordersQuery.isFetching} onClick={goToPreviousPage}><RrIcon name="left" /></button>{pageList.map(value => <button key={value} type="button" className={value === page ? 'rr-on' : undefined} data-of={value === page ? totalPages : undefined} aria-current={value === page ? 'page' : undefined} disabled={ordersQuery.isFetching} onClick={() => goToPage(value)}>{value}</button>)}<button type="button" aria-label="Sonraki" disabled={!hasNextPage || ordersQuery.isFetching} onClick={goToNextPage}><RrIcon name="right" /></button></div><label className="rr-jump"><span className="rr-jump-label">Sayfaya Git</span><input className="rr-in" type="number" min={1} max={totalPages} placeholder={String(page)} aria-label="Sayfa numarası" value={jumpPage} onChange={event => setJumpPage(event.target.value)} onKeyDown={event => { if (event.key === 'Enter' && Number(jumpPage) >= 1) { goToPage(Math.floor(Number(jumpPage))); setJumpPage('') } }} /><button type="button" className="rr-btn" onClick={() => { if (Number(jumpPage) >= 1) { goToPage(Math.floor(Number(jumpPage))); setJumpPage('') } }}>Git</button></label></div>}
    </div>
    <StickyActionBar open={selectedOrders.length > 0} label="Toplu sipariş işlemleri" className="rr-bulk" lead={<span className="rr-grow">{selectedOrders.length} sipariş seçildi{shopifySelectedOrders.length > 0 && shopifySelectedOrders.length < selectedOrders.length && <small className="rr-bulk-note">Shopify toplu işlemleri yalnızca seçili Shopify siparişlerine uygulanır.</small>}</span>}><button type="button" className="rr-btn rr-quiet rr-bulk-clear" data-dock-clear data-dock-count={selectedOrders.length} aria-label={`${selectedOrders.length} seçili, seçimi kaldır`} onClick={() => setSelectedIds([])}><RrIcon name="close" /><span className="rr-bulk-lbl">Seçimi Temizle</span></button>{!allPageSelected && selectablePageItems.length > 0 && <button type="button" className="rr-btn rr-bulk-act rr-bulk-all" data-dock-keep onClick={() => togglePageSelection(true)}><span className="rr-bulk-lbl">Sayfadakileri Seç ({selectablePageItems.length})</span></button>}{cargoEditableOrders.length > 0 && <button type="button" className="rr-btn rr-bulk-act" title={cargoChangeHelp} aria-label={`Kargo Firması Değiştir (${cargoEditableOrders.length})`} onClick={() => setCourierOrder({ item: cargoEditableOrders[0], items: cargoEditableOrders })}><RrIcon name="truck" /><span className="rr-bulk-lbl">Kargo Firması Değiştir ({cargoEditableOrders.length})</span><span className="rr-bulk-badge" aria-hidden="true">{cargoEditableOrders.length}</span></button>}{shopifyBulkButtons}{labelBatchItems.length > 0 && <button type="button" className="rr-btn rr-primary rr-bulk-act" title="Etiketleri Yazdır" aria-label={`Etiketleri Yazdır (${labelBatchItems.length})`} onClick={() => setShippingLabelBatch({ items: labelBatchItems, format: labelSettings.defaultFormat })}><RrIcon name="print" /><span className="rr-bulk-lbl">Etiketleri Yazdır ({labelBatchItems.length})</span><span className="rr-bulk-badge" aria-hidden="true">{labelBatchItems.length}</span></button>}</StickyActionBar>
     {invoiceInfoOrder && <InvoiceInfoModal item={invoiceInfoOrder} onClose={() => setInvoiceInfoOrder(null)} />}{invoiceViewerOrder && <InvoiceViewerModal item={invoiceViewerOrder} onClose={() => setInvoiceViewerOrder(null)} />}{invoiceDraftOrder && <InvoiceDraftModal item={invoiceDraftOrder} providers={providers} onClose={() => setInvoiceDraftOrder(null)} onFeedback={orderFeedback(invoiceDraftOrder)} />}{invoiceUploadOrder && <InvoiceUploadModal item={invoiceUploadOrder} onClose={() => setInvoiceUploadOrder(null)} onFeedback={orderFeedback(invoiceUploadOrder)} />}{shopifyStatusOrder && <ShopifyOrderStatusModal item={shopifyStatusOrder} onClose={() => setShopifyStatusOrder(null)} onFeedback={orderFeedback(shopifyStatusOrder)} />}{shopifyCsvImportOpen && <ShopifyOrderCsvImportModal connections={shopifyConnections} onClose={() => setShopifyCsvImportOpen(false)} onFeedback={showBulkNotice} />}{courierOrder && <CourierChangeModal item={courierOrder.item} items={courierOrder.items} onClose={() => setCourierOrder(null)} onConfirmed={updatedShipments => { client.setQueriesData<Page<Order>>({ queryKey: queryKeys.orders('page') }, current => current ? { ...current, items: current.items.map(order => updatedShipments.reduce((updated, shipment) => updated.id === shipment.orderId ? patchOrderShipment(updated, shipment) : updated, order)) } : current); showBulkNotice(`${updatedShipments.length} paket kargo firması “${cargoLabel(updatedShipments[0]?.cargoProviderName)}” olarak güncellendi ve panelde doğrulandı.`) }} />}{shippingLabel && <ShippingLabelModal item={shippingLabel.item} settings={labelSettings} format={shippingLabel.format} onClose={() => setShippingLabel(null)} onPrinted={() => setPrintedLabels(new Set(loadPrintedShippingLabels()))} />}{shippingLabelBatch && <ShippingLabelBatchModal items={shippingLabelBatch.items.filter(item => !isMicroExportLabelBlocked(item))} settings={labelSettings} format={shippingLabelBatch.format} onClose={() => setShippingLabelBatch(null)} onPrinted={() => setPrintedLabels(new Set(loadPrintedShippingLabels()))} />}{singleSyncOpen && <SingleOrderSyncModal activeConnection={marketplaceConnections} onClose={() => setSingleSyncOpen(false)} onSuccess={(connectionCount, orderNo) => { showBulkNotice(orderNo ? `${connectionCount} bağlantıdan #${orderNo} sipariş senkronizasyonu başlatıldı.` : `${connectionCount} bağlantıdan yeni sipariş senkronizasyonu başlatıldı.`, 'info'); void client.invalidateQueries({ queryKey: queryKeys.orders() }); window.setTimeout(() => void client.invalidateQueries({ queryKey: queryKeys.orders() }), 1500); window.setTimeout(() => void client.invalidateQueries({ queryKey: queryKeys.orders() }), 3500) }} />}{previewImage && <ProductImagePreviewModal preview={previewImage} onClose={() => setPreviewImage(null)} />}{manualResendOpen && <ManualResendDialog legacyCount={resendReminder.count} onClearLegacy={resendReminder.clear} onClose={() => setManualResendOpen(false)} />}
    {hepsiburadaPackageOrder && <HepsiburadaPackageCreateModal item={hepsiburadaPackageOrder} onClose={() => setHepsiburadaPackageOrder(null)} onCreated={packageNumber => { const packagedOrder = hepsiburadaPackageOrder; setHepsiburadaPackageOrder(null); showBulkNotice(`Hepsiburada paketi ${packageNumber} oluşturuldu; siparişin güncel hali pazaryerinden yeniden okunacak.`, 'success', { ...orderNotificationContext(packagedOrder), title: 'Hepsiburada Paketi Oluşturuldu', operation: 'Paket oluşturma', details: [{ label: 'Paket No', value: packageNumber }] }); void client.invalidateQueries({ queryKey: queryKeys.orders() }) }} />}
  </section>
}
