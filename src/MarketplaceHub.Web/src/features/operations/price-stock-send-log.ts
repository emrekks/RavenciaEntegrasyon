import { istanbulDayBoundary } from '../../shared/format-date'
import { formatMoney } from '../../shared/format-money'

// The price/stock send log (İşlem Takibi › Loglar): one row per line of a price/stock request sent to a marketplace,
// read from GET /price-stock-send-log. These helpers turn a row into the text the table shows and the filters into
// the request; they read no state of their own.

export type PriceStockSendLogEntry = {
  id: string
  createdAt: string
  completedAt: string | null
  connectionId: string
  platformCode: string
  storeName: string | null
  jobId: string
  variantId: string
  productId: string | null
  productTitle: string | null
  imageUrl: string | null
  barcode: string
  stockCode: string | null
  modelCode: string | null
  previousQuantity: number | null
  newQuantity: number | null
  previousSalePrice: number | null
  newSalePrice: number | null
  previousListPrice: number | null
  newListPrice: number | null
  currency: string
  trigger: string
  result: string
  errorCode: string | null
  errorMessage: string | null
  externalRequestId: string | null
}

export type PriceStockSendLogPage = { items: PriceStockSendLogEntry[]; nextCursor: string | null; hasMore: boolean }

export const sendLogPageSize = 50

export type SendLogTone = 'success' | 'warning' | 'danger' | 'neutral'

const results: Record<string, { label: string; tone: SendLogTone; description: string }> = {
  SUBMITTED: { label: 'Gönderildi', tone: 'neutral', description: 'İstek pazaryerine gönderildi; sonucu bekleniyor.' },
  APPLIED: { label: 'Uygulandı', tone: 'success', description: 'Pazaryeri değerleri aldı.' },
  UNCHANGED: { label: 'Zaten Güncel', tone: 'success', description: 'Pazaryeri bu değerleri zaten taşıyordu.' },
  REJECTED: { label: 'Reddedildi', tone: 'danger', description: 'Pazaryeri satırı reddetti.' },
  FAILED: { label: 'Başarısız', tone: 'danger', description: 'Gönderim tamamlanamadı; değerler gönderilmiş sayılmadı.' },
  SUPERSEDED: { label: 'Yeni Sürüm Geldi', tone: 'warning', description: 'Yanıt gelmeden fiyat veya stok yeniden değişti; bu sonuç uygulanmadı, yeni değerler ayrıca gönderilir.' },
  UNKNOWN: { label: 'Sonuç Belirsiz', tone: 'warning', description: 'Pazaryerinin yanıtı kesinleşmedi; değerler gönderilmiş sayılmadı.' }
}

export const sendLogResultOptions = Object.keys(results)

const triggers: Record<string, string> = {
  ORDER: 'Sipariş',
  MANUAL_STOCK: 'Manuel Stok',
  PRICE_EDIT: 'Fiyat Değişikliği',
  STOCK_IMPORT: 'Pazaryeri Stok Aktarımı',
  STOCK_CHANGE: 'Stok Değişikliği',
  RECONCILE: 'Kontrol Taraması',
  PANEL_SEND: 'Panelden Gönderim',
  AUTOMATIC: 'Otomatik'
}

export const sendLogTriggerOptions = Object.keys(triggers)

/** The result's label; an unknown code is shown as it came. */
export function sendLogResultLabel(result: string) {
  return results[result]?.label ?? result
}

export function sendLogResultTone(result: string): SendLogTone {
  return results[result]?.tone ?? 'neutral'
}

/** The trigger's label; an unknown code is shown as it came. */
export function sendLogTriggerLabel(trigger: string) {
  return triggers[trigger] ?? trigger
}

/** The pill's tooltip: what the result means, then the marketplace's code and message when there is one. */
export function sendLogResultTooltip(entry: Pick<PriceStockSendLogEntry, 'result' | 'errorCode' | 'errorMessage'>) {
  const parts = [results[entry.result]?.description ?? entry.result]
  const error = [entry.errorCode, entry.errorMessage].filter(Boolean).join(': ')
  if (error) parts.push(error)
  return parts.join(' — ')
}

export type ValueChange = { text: string; omitted: boolean; changed: boolean }

const quantityFormat = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 4 })
const formatQuantity = (value: number) => quantityFormat.format(value)

// A whole amount without kuruş (₺799), any other with two digits (₺749,90).
function formatPrice(value: number, currency: string) {
  const whole = Number.isInteger(value)
  return formatMoney(value, currency, { minimumFractionDigits: whole ? 0 : 2, maximumFractionDigits: 2 })
}

function change<T>(previous: T | null, next: T | null, format: (value: T) => string): ValueChange {
  if (next === null || next === undefined) return { text: 'Gönderilmedi', omitted: true, changed: false }
  const before = previous === null || previous === undefined ? '—' : format(previous)
  return { text: `${before} → ${format(next)}`, omitted: false, changed: previous !== next }
}

/** "5 → 4"; "— → 4" when the quantity sent before is unknown; "Gönderilmedi" when the request left the stock out. */
export function quantityChange(previous: number | null, next: number | null): ValueChange {
  return change(previous, next, formatQuantity)
}

/** "₺799 → ₺749" for a price, the same rules as the stock. */
export function priceChange(previous: number | null, next: number | null, currency: string): ValueChange {
  return change(previous, next, value => formatPrice(value, currency))
}

/** The list price is shown under the sale price only when the request carried a new one. */
export function listPriceChanged(entry: Pick<PriceStockSendLogEntry, 'previousListPrice' | 'newListPrice'>) {
  return entry.newListPrice !== null && entry.newListPrice !== undefined && entry.previousListPrice !== entry.newListPrice
}

export type SendLogFilters = {
  search: string
  platforms: string[]
  result: string
  trigger: string
  /** Istanbul calendar days, YYYY-MM-DD, or '' for no bound. */
  from: string
  to: string
}

export const emptySendLogFilters: SendLogFilters = { search: '', platforms: [], result: '', trigger: '', from: '', to: '' }

export function hasSendLogFilters(filters: SendLogFilters) {
  return Boolean(filters.search.trim() || filters.platforms.length || filters.result || filters.trigger || filters.from || filters.to)
}

/** The instant an Istanbul day starts (or ends) as a UTC ISO string; '' for an empty or unreadable day. */
export function istanbulDayToIso(day: string, endOfDay = false) {
  const boundary = istanbulDayBoundary(day, endOfDay)
  if (!boundary) return ''
  const instant = new Date(boundary)
  return Number.isNaN(instant.getTime()) ? '' : instant.toISOString()
}

/** The request for one page: ?limit=50&search=…&platforms=…&results=…&triggers=…&from=…&to=…&after=… */
export function sendLogQueryString(filters: SendLogFilters, after: string | null = null, limit = sendLogPageSize) {
  const params = new URLSearchParams()
  params.set('limit', String(Math.min(Math.max(1, limit), 100)))
  const search = filters.search.trim().slice(0, 100)
  if (search) params.set('search', search)
  if (filters.platforms.length) params.set('platforms', [...new Set(filters.platforms.map(code => code.toUpperCase()))].join(','))
  if (filters.result) params.set('results', filters.result)
  if (filters.trigger) params.set('triggers', filters.trigger)
  const from = istanbulDayToIso(filters.from)
  const to = istanbulDayToIso(filters.to, true)
  if (from) params.set('from', from)
  if (to) params.set('to', to)
  if (after) params.set('after', after)
  return `?${params.toString()}`
}
