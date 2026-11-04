import { formatMoney } from '../../shared/format-money'
import { istanbulDayToIso, sendLogRangeLabel } from './price-stock-send-log'
import { timeRangeStart, type JobListTimeRange } from './job-list-query'

// İşlem Takibi › Loglar › Ürün Hareketleri: what changed on a product (stock, price, sale status, returns), the panel's
// own changes and the marketplaces' alike, read from GET /product-activity. These helpers turn a row into the text the
// list shows and the filters into the request; they read no state of their own.

export type ProductActivityKind = 'STOCK' | 'PRICE' | 'STATUS' | 'RETURN'

export type ProductActivityEntry = {
  id: string
  occurredAt: string
  kind: ProductActivityKind | string
  type: string
  productId: string | null
  productTitle: string | null
  imageUrl: string | null
  variantId: string | null
  sku: string | null
  barcode: string | null
  modelCode: string | null
  optionText: string | null
  quantityDelta: number | null
  previousSalePrice: number | null
  newSalePrice: number | null
  previousListPrice: number | null
  newListPrice: number | null
  currency: string | null
  previousStatus: string | null
  newStatus: string | null
  source: string
  note: string | null
  connectionId: string | null
  platformCode: string | null
  storeName: string | null
  orderId: string | null
  orderNumber: string | null
  returnId: string | null
  returnNumber: string | null
  actorUserId: string | null
  actorName: string | null
}

export type ProductActivityPage = { items: ProductActivityEntry[]; nextCursor: string | null; hasMore: boolean }

export const activityPageSize = 50

// The summary above the list: one toggle per kind, in the order the owner reads them.
export const activityKinds: Array<{ key: ProductActivityKind; label: string; icon: 'package' | 'tag' | 'activity' | 'packageReturn' }> = [
  { key: 'STOCK', label: 'Stok', icon: 'package' },
  { key: 'PRICE', label: 'Fiyat', icon: 'tag' },
  { key: 'STATUS', label: 'Durum', icon: 'activity' },
  { key: 'RETURN', label: 'İade', icon: 'packageReturn' }
]

export function activityKindLabel(kind: string) {
  return activityKinds.find(item => item.key === kind)?.label ?? kind
}

const statuses: Record<string, string> = { ACTIVE: 'Satışa Açık', ARCHIVED: 'Satışa Kapalı', DRAFT: 'Taslak' }

/** The sale status in the panel's words; an unknown code is shown as it came. */
export function activityStatusLabel(status: string | null) {
  if (!status) return 'Bilinmiyor'
  return statuses[status] ?? status
}

const sources: Record<string, string> = {
  ORDER: 'Sipariş',
  RETURN: 'İade',
  MANUAL: 'Elle',
  EDITOR: 'Ürün Düzenleme',
  BULK_EDIT: 'Toplu Düzenleme',
  PRICE_EDIT: 'Fiyat Düzenleme',
  FILE_IMPORT: 'Excel',
  MARKETPLACE_IMPORT: 'Pazaryeri İçe Aktarımı',
  PUBLISH: 'Toplu Yayın',
  CLASSIC_IMPORT: 'İçe Aktarım'
}

/** Where the change came from, in words; an unknown code is shown as it came. */
export function activitySourceLabel(source: string) {
  return sources[source] ?? source
}

export type ActivityChangeTone = 'up' | 'down' | 'neutral' | 'info'
export type ActivityChange = { text: string; tone: ActivityChangeTone; title?: string }

const quantityFormat = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 4 })

// A whole amount without kuruş (₺799), any other with two digits (₺749,90).
function price(value: number, currency: string | null) {
  return formatMoney(value, currency || 'TRY', { minimumFractionDigits: Number.isInteger(value) ? 0 : 2, maximumFractionDigits: 2 })
}

function priceChange(previous: number | null, next: number | null, currency: string | null) {
  if (next === null || next === undefined) return null
  if (previous === null || previous === undefined) return price(next, currency)
  return `${price(previous, currency)} → ${price(next, currency)}`
}

/**
 * The change chip: "+1 Stok" / "−2 Stok", "₺899 → ₺950", "Satışa Açık → Satışa Kapalı". A shortfall or a return kept out
 * of stock changed no stock: it says what happened instead. A price without one before is "₺950" (set).
 */
export function activityChange(entry: ProductActivityEntry): ActivityChange {
  const amount = entry.quantityDelta === null ? '' : quantityFormat.format(Math.abs(entry.quantityDelta))
  switch (entry.type) {
    case 'ORDER_STOCK_SHORTFALL':
      return { text: `Stok Yetersiz · ${amount} Eksik`, tone: 'info', title: 'Sipariş geldiğinde stok yetmedi; stok değişmedi, eksik adet stok gelince düşülür.' }
    case 'RETURN_QUARANTINE':
      return { text: `${amount} Adet Karantinada`, tone: 'info', title: 'İade ürün stoğa eklenmedi.' }
    case 'RETURN_DAMAGED':
      return { text: `${amount} Adet Hasarlı`, tone: 'info', title: 'İade ürün stoğa eklenmedi.' }
    case 'RETURN_NOTRECEIVED':
      return { text: `${amount} Adet Gelmedi`, tone: 'info', title: 'İade ürün stoğa eklenmedi.' }
    case 'STATUS':
      return { text: `${activityStatusLabel(entry.previousStatus)} → ${activityStatusLabel(entry.newStatus)}`, tone: entry.newStatus === 'ACTIVE' ? 'up' : 'neutral' }
    case 'OFFER_PRICE':
    case 'DEFAULT_PRICE': {
      const sale = priceChange(entry.previousSalePrice, entry.newSalePrice, entry.currency)
      const list = entry.previousListPrice !== entry.newListPrice ? priceChange(entry.previousListPrice, entry.newListPrice, entry.currency) : null
      const tone: ActivityChangeTone = entry.previousSalePrice === null || entry.newSalePrice === null ? 'neutral' : entry.newSalePrice > entry.previousSalePrice ? 'up' : entry.newSalePrice < entry.previousSalePrice ? 'down' : 'neutral'
      if (sale) return { text: sale, tone, title: list ? `Liste fiyatı: ${list}` : undefined }
      return { text: list ? `Liste: ${list}` : 'Fiyat', tone: 'neutral' }
    }
  }
  if (entry.quantityDelta === null) return { text: '—', tone: 'neutral' }
  if (entry.quantityDelta === 0) return { text: '0 Stok', tone: 'neutral' }
  return entry.quantityDelta > 0 ? { text: `+${amount} Stok`, tone: 'up' } : { text: `−${amount} Stok`, tone: 'down' }
}

/** What the change was, beside its source: "Satış", "Sipariş İptali", "Açılış Stoğu", "Mağaza Fiyatı", "Panel Fiyatı"... */
export function activityTypeLabel(entry: ProductActivityEntry) {
  switch (entry.type) {
    case 'ORDER_RECEIVED': return entry.quantityDelta !== null && entry.quantityDelta > 0 ? 'Sipariş İptali / Değişikliği' : 'Satış'
    case 'ORDER_STOCK_SHORTFALL': return 'Stok Yetersiz'
    // The editor's first stock was a manual change before it became an opening row; it reads as one either way.
    case 'MANUAL_ADJUSTMENT': return entry.note?.trim() === 'İlk ürün stoğu' ? 'Açılış Stoğu' : 'Stok Düzenleme'
    case 'MARKETPLACE_STOCK_IMPORT': return 'Pazaryeri Stoğu'
    case 'IMPORT_OPENING': return 'Açılış Stoğu'
    case 'RETURN_PASS': return 'İade Stoğa Eklendi'
    case 'RETURN_QUARANTINE':
    case 'RETURN_DAMAGED':
    case 'RETURN_NOTRECEIVED': return 'İade Stoğa Eklenmedi'
    case 'OFFER_PRICE': return 'Mağaza Fiyatı'
    case 'DEFAULT_PRICE': return 'Panel Fiyatı'
    case 'STATUS': return 'Satış Durumu'
    default: return activityKindLabel(entry.kind)
  }
}

/** "Sipariş #123", "İade #R-9", or the source's name. */
export function activitySourceText(entry: ProductActivityEntry) {
  if (entry.orderNumber && entry.source === 'ORDER') return `Sipariş #${entry.orderNumber.replace(/^#+/, '')}`
  if (entry.returnNumber && entry.source === 'RETURN') return `İade #${entry.returnNumber.replace(/^#+/, '')}`
  return activitySourceLabel(entry.source)
}

/** The page a row leads to: its order, its return, else its product; null when none is known. */
export function activityLink(entry: ProductActivityEntry): string | null {
  if (entry.source === 'RETURN' && entry.returnId) return `/returns/${encodeURIComponent(entry.returnId)}`
  if (entry.orderNumber) return `/orders?${new URLSearchParams({ search: entry.orderNumber.replace(/^#+/, '') })}`
  if (entry.productId) return `/products/${encodeURIComponent(entry.productId)}`
  return null
}

/** Who made it: the user's name, "Sistem" for a marketplace or order the panel read, "—" when unknown. */
export function activityActor(entry: ProductActivityEntry) {
  if (entry.actorName) return entry.actorName
  if (entry.actorUserId) return 'Bilinmeyen Kullanıcı'
  return ['ORDER', 'MARKETPLACE_IMPORT', 'CLASSIC_IMPORT'].includes(entry.source) ? 'Sistem' : '—'
}

export type ActivityFilters = {
  search: string
  platforms: string[]
  kinds: string[]
  range: JobListTimeRange
  /** Istanbul calendar days, YYYY-MM-DD, or '' for no bound. */
  from: string
  to: string
}

export const emptyActivityFilters: ActivityFilters = { search: '', platforms: [], kinds: [], range: 'all', from: '', to: '' }

const hasDays = (filters: Pick<ActivityFilters, 'from' | 'to'>) => Boolean(filters.from || filters.to)

/** How many filters the Filtrele panel holds: platform, kind, and the chosen days once. */
export function activityPanelFilterCount(filters: ActivityFilters) {
  return [filters.platforms.length, filters.kinds.length, hasDays(filters)].filter(Boolean).length
}

/** How many filters narrow the list: the search, the panel's filters, and a range other than Tüm Zamanlar. */
export function activityFilterCount(filters: ActivityFilters) {
  return activityPanelFilterCount(filters) + (filters.search.trim() ? 1 : 0) + (!hasDays(filters) && filters.range !== 'all' ? 1 : 0)
}

export function activityRangeLabel(filters: ActivityFilters) {
  return sendLogRangeLabel({ ...filters, results: [], triggers: [] })
}

/** Turns a kind on or off in the kind filter. */
export function toggleActivityKind(kinds: string[], kind: string) {
  return kinds.includes(kind) ? kinds.filter(value => value !== kind) : [...kinds, kind]
}

/** Each kind's total from the server's counts; a kind with no row is 0. */
export function activityKindCounts(rows: ReadonlyArray<{ kind: string; count: number }>) {
  return Object.fromEntries(activityKinds.map(item => [item.key, rows.filter(row => row.kind === item.key).reduce((total, row) => total + row.count, 0)])) as Record<ProductActivityKind, number>
}

function commonParams(params: URLSearchParams, filters: ActivityFilters, now: number) {
  const search = filters.search.trim().slice(0, 100)
  if (search) params.set('search', search)
  if (filters.platforms.length) params.set('platforms', [...new Set(filters.platforms.map(code => code.toUpperCase()))].join(','))
  if (hasDays(filters)) {
    const from = istanbulDayToIso(filters.from)
    const to = istanbulDayToIso(filters.to, true)
    if (from) params.set('from', from)
    if (to) params.set('to', to)
    return
  }
  const start = timeRangeStart(filters.range, now)
  if (start) params.set('from', start)
}

/** The request for one page: ?limit=50&kinds=…&search=…&platforms=…&from=…&to=…&after=… */
export function activityQueryString(filters: ActivityFilters, after: string | null = null, limit = activityPageSize, now = Date.now()) {
  const params = new URLSearchParams()
  params.set('limit', String(Math.min(Math.max(1, limit), 100)))
  if (filters.kinds.length) params.set('kinds', [...new Set(filters.kinds)].join(','))
  commonParams(params, filters, now)
  if (after) params.set('after', after)
  return `?${params.toString()}`
}

/** The summary's request: the list's filters without the kind filter. */
export function activityCountsQueryString(filters: ActivityFilters, now = Date.now()) {
  const params = new URLSearchParams()
  commonParams(params, filters, now)
  const query = params.toString()
  return query ? `?${query}` : ''
}
