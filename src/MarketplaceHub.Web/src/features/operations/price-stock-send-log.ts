import { formatDateRange } from '../../shared/date-range'
import { istanbulDayBoundary } from '../../shared/format-date'
import { formatMoney } from '../../shared/format-money'
import { timeRangeLabel, timeRangeStart, type JobListTimeRange } from './job-list-query'

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

export type SendLogOptionGroup = { label: string; values: string[] }

// The filter menus group the codes by what they mean, the most common first in each group. A sent line still waiting
// for its answer is neither a success nor a problem, so it has a group of its own.
export const sendLogResultGroups: SendLogOptionGroup[] = [
  { label: 'Başarılı', values: ['APPLIED', 'UNCHANGED'] },
  { label: 'Sorunlu', values: ['REJECTED', 'FAILED', 'UNKNOWN'] },
  { label: 'Bekleyen', values: ['SUBMITTED'] },
  { label: 'Diğer', values: ['SUPERSEDED'] }
]

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

// Otomatik: the panel sent it on its own after an order, a stock change or a check; Panelden: someone changed or sent
// it by hand; Aktarım: stock read from a marketplace and passed on.
export const sendLogTriggerGroups: SendLogOptionGroup[] = [
  { label: 'Otomatik', values: ['ORDER', 'STOCK_CHANGE', 'AUTOMATIC', 'RECONCILE'] },
  { label: 'Panelden', values: ['PANEL_SEND', 'MANUAL_STOCK', 'PRICE_EDIT'] },
  { label: 'Aktarım', values: ['STOCK_IMPORT'] }
]

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
  results: string[]
  triggers: string[]
  /** The heading's range (Son 24 Saat, Son 7 Gün, Tüm Zamanlar); chosen days below override it. */
  range: JobListTimeRange
  /** Istanbul calendar days, YYYY-MM-DD, or '' for no bound. */
  from: string
  to: string
}

// The log keeps 90 days, so Tüm Zamanlar is its whole history: the view opens on it, as it did before it had a range.
export const emptySendLogFilters: SendLogFilters = { search: '', platforms: [], results: [], triggers: [], range: 'all', from: '', to: '' }

const hasDays = (filters: Pick<SendLogFilters, 'from' | 'to'>) => Boolean(filters.from || filters.to)

/** How many filters the Filtrele panel holds: each of platform, result and trigger, and the chosen days once. */
export function sendLogPanelFilterCount(filters: SendLogFilters) {
  return [filters.platforms.length, filters.results.length, filters.triggers.length, hasDays(filters)].filter(Boolean).length
}

/** How many filters narrow the list: the search, the panel's filters, and a range other than Tüm Zamanlar. */
export function sendLogFilterCount(filters: SendLogFilters) {
  return sendLogPanelFilterCount(filters) + (filters.search.trim() ? 1 : 0) + (!hasDays(filters) && filters.range !== 'all' ? 1 : 0)
}

export function hasSendLogFilters(filters: SendLogFilters) {
  return sendLogFilterCount(filters) > 0
}

/** The range button's text: the chosen days when there are any, else the range. */
export function sendLogRangeLabel(filters: SendLogFilters) {
  return hasDays(filters) ? formatDateRange({ from: filters.from, to: filters.to }) : timeRangeLabel(filters.range)
}

// The summary above the log: the result groups of the Sonuç menu, each a toggle of its whole group, drawn with the job
// list's state glyphs. Bekleyen waits for the marketplace's answer; Diğer is a line a newer value replaced.
export type SendLogSummaryKey = 'success' | 'problem' | 'pending' | 'other'
export const sendLogSummary: Array<{ key: SendLogSummaryKey; label: string; glyph: string; values: string[] }> = [
  { key: 'success', label: 'Başarılı', glyph: 'success', values: sendLogResultGroups[0].values },
  { key: 'problem', label: 'Sorunlu', glyph: 'error', values: sendLogResultGroups[1].values },
  { key: 'pending', label: 'Bekleyen', glyph: 'waiting', values: sendLogResultGroups[2].values },
  { key: 'other', label: 'Yeni Sürüm', glyph: 'cancelled', values: sendLogResultGroups[3].values }
]

/** Each summary group's total from the server's counts; an unknown result counts nowhere. */
export function sendLogSummaryCounts(rows: ReadonlyArray<{ result: string; count: number }>) {
  return Object.fromEntries(sendLogSummary.map(group => [group.key, rows.reduce((total, row) => total + (group.values.includes(row.result) ? row.count : 0), 0)])) as Record<SendLogSummaryKey, number>
}

/** A summary group is on when every result of it is picked. */
export function sendLogSummaryOn(results: string[], values: string[]) {
  return values.every(value => results.includes(value))
}

/** Turns a summary group on (adds its results) or off (drops them), keeping the other picked results. */
export function toggleSendLogSummary(results: string[], values: string[]) {
  return sendLogSummaryOn(results, values) ? results.filter(value => !values.includes(value)) : [...new Set([...results, ...values])]
}

// The send time bounds: the chosen Istanbul days, else the range counted back from now.
function sendLogTimeParams(params: URLSearchParams, filters: SendLogFilters, now: number) {
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

function sendLogCommonParams(params: URLSearchParams, filters: SendLogFilters) {
  const search = filters.search.trim().slice(0, 100)
  if (search) params.set('search', search)
  if (filters.platforms.length) params.set('platforms', [...new Set(filters.platforms.map(code => code.toUpperCase()))].join(','))
}

/** The summary's request: the list's filters without the result filter (?search=…&platforms=…&triggers=…&from=…&to=…). */
export function sendLogCountsQueryString(filters: SendLogFilters, now = Date.now()) {
  const params = new URLSearchParams()
  sendLogCommonParams(params, filters)
  if (filters.triggers.length) params.set('triggers', [...new Set(filters.triggers)].join(','))
  sendLogTimeParams(params, filters, now)
  const query = params.toString()
  return query ? `?${query}` : ''
}

/** The instant an Istanbul day starts (or ends) as a UTC ISO string; '' for an empty or unreadable day. */
export function istanbulDayToIso(day: string, endOfDay = false) {
  const boundary = istanbulDayBoundary(day, endOfDay)
  if (!boundary) return ''
  const instant = new Date(boundary)
  return Number.isNaN(instant.getTime()) ? '' : instant.toISOString()
}

/** The request for one page: ?limit=50&search=…&platforms=…&results=…&triggers=…&from=…&to=…&order=asc&after=… */
export function sendLogQueryString(filters: SendLogFilters, after: string | null = null, limit = sendLogPageSize, oldestFirst = false, now = Date.now()) {
  const params = new URLSearchParams()
  params.set('limit', String(Math.min(Math.max(1, limit), 100)))
  sendLogCommonParams(params, filters)
  if (filters.results.length) params.set('results', [...new Set(filters.results)].join(','))
  if (filters.triggers.length) params.set('triggers', [...new Set(filters.triggers)].join(','))
  sendLogTimeParams(params, filters, now)
  if (oldestFirst) params.set('order', 'asc')
  if (after) params.set('after', after)
  return `?${params.toString()}`
}
