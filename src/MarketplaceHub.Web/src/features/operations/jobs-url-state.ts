import type { JobCategory } from './job-categories'
import type { JobListTimeRange } from './job-list-query'
import type { JobDirection } from './job-presentation'

// İşlem Takibi's filters and page live in the address (W16), so Back, a refresh or a shared link opens the same view.
//   ?kategori=siparis&yon=yazma&durum=hata,bekliyor&statu=DEAD&aralik=7g&ara=corr&sayfa=2&adet=50&platform=trendyol
// Every default is left out of the address. `platform` keeps its own reader (job-list-query.ts).

export type JobStatusChip = 'success' | 'running' | 'waiting' | 'error' | 'cancelled'
export type JobStatusCode = 'PENDING' | 'LEASED' | 'RETRY_SCHEDULED' | 'BLOCKED' | 'MANUAL_REVIEW' | 'SUCCEEDED' | 'DEAD' | 'CANCELLED'

export type JobsViewState = {
  category: JobCategory
  direction: '' | JobDirection
  chips: JobStatusChip[]
  status: '' | JobStatusCode
  timeRange: JobListTimeRange
  search: string
  page: number
  pageSize: number
}

const categorySlugs: Record<JobCategory, string> = { ALL: '', ORDERS: 'siparis', PRICE_INVENTORY: 'fiyat-stok', CATALOG: 'urun', INVOICES: 'fatura', RETURNS: 'iade', SYSTEM: 'sistem' }
const directionSlugs: Record<JobDirection, string> = { Okuma: 'okuma', Yazma: 'yazma', Bildirim: 'bildirim', 'Panel İçi': 'panel' }
const chipSlugs: Record<JobStatusChip, string> = { success: 'basarili', running: 'calisiyor', waiting: 'bekliyor', error: 'hata', cancelled: 'iptal' }
const rangeSlugs: Record<JobListTimeRange, string> = { '24h': '', '7d': '7g', today: 'bugun', week: 'hafta', month: 'ay', '30d': '30g', year: 'yil', all: 'tum' }
const statusCodes: readonly JobStatusCode[] = ['PENDING', 'LEASED', 'RETRY_SCHEDULED', 'BLOCKED', 'MANUAL_REVIEW', 'SUCCEEDED', 'DEAD', 'CANCELLED']
export const jobPageSizes = [20, 50, 100, 200] as const
const defaultPageSize = 20

function fromSlug<T extends string>(slugs: Record<T, string>, value: string | null, fallback: T): T {
  if (!value) return fallback
  return (Object.keys(slugs) as T[]).find(key => slugs[key] === value) ?? fallback
}

export function readJobsViewState(params: URLSearchParams): JobsViewState {
  const chipKeys = Object.keys(chipSlugs) as JobStatusChip[]
  const chips = (params.get('durum') ?? '').split(',').flatMap(value => chipKeys.filter(chip => chipSlugs[chip] === value))
  const status = (params.get('statu') ?? '').toUpperCase()
  const page = Number(params.get('sayfa'))
  const pageSize = Number(params.get('adet'))
  // Sistem & Test has no tab in the list, so ?kategori=sistem would filter by a category nobody can see or leave:
  // it opens Tüm İşlemler.
  const category = fromSlug<JobCategory>(categorySlugs, params.get('kategori'), 'ALL')
  return {
    category: category === 'SYSTEM' ? 'ALL' : category,
    direction: (Object.keys(directionSlugs) as JobDirection[]).find(key => directionSlugs[key] === params.get('yon')) ?? '',
    chips: [...new Set(chips)],
    status: (statusCodes as readonly string[]).includes(status) ? status as JobStatusCode : '',
    timeRange: fromSlug(rangeSlugs, params.get('aralik'), '24h'),
    search: (params.get('ara') ?? '').slice(0, 200),
    page: Number.isInteger(page) && page > 1 ? page : 1,
    pageSize: (jobPageSizes as readonly number[]).includes(pageSize) ? pageSize : defaultPageSize
  }
}

/**
 * Writes a change into the address, keeping unrelated parameters (platform). Any filter change takes the list back to
 * its first page unless the change itself names the page.
 */
export function writeJobsViewState(current: URLSearchParams, patch: Partial<JobsViewState>): URLSearchParams {
  const next = { ...readJobsViewState(current), ...patch }
  if (patch.page === undefined && Object.keys(patch).length > 0) next.page = 1
  const params = new URLSearchParams(current)
  const set = (name: string, value: string) => { if (value) params.set(name, value); else params.delete(name) }
  set('kategori', categorySlugs[next.category])
  set('yon', next.direction ? directionSlugs[next.direction] : '')
  set('durum', next.chips.map(chip => chipSlugs[chip]).join(','))
  set('statu', next.status)
  set('aralik', rangeSlugs[next.timeRange])
  set('ara', next.search)
  set('sayfa', next.page > 1 ? String(next.page) : '')
  set('adet', next.pageSize !== defaultPageSize ? String(next.pageSize) : '')
  return params
}

// The Loglar tab (the price/stock send log) is a view of its own beside the job categories: ?sekme=loglar. The job
// filters stay in the address while it is open, so going back to a category finds them as they were.
const logsTabParam = 'sekme'
const logsTabSlug = 'loglar'

// The main tabs of İşlem Takibi: the job list (no ?sekme), Loglar (?sekme=loglar), API Sağlık
// (?sekme=api-saglik, the price/stock sending health per connection), Kuyruk ve Kota (?sekme=kuyruk-kota) and Bildirimler
// (?sekme=bildirimler, the notification center). An unknown value opens the job list.
export type JobsMainTab = 'jobs' | 'logs' | 'health' | 'queue' | 'notifications'
const mainTabSlugs: Record<Exclude<JobsMainTab, 'jobs'>, string> = { logs: logsTabSlug, health: 'api-saglik', queue: 'kuyruk-kota', notifications: 'bildirimler' }

export function jobsMainTab(params: URLSearchParams): JobsMainTab {
  const slug = params.get(logsTabParam)
  return (Object.keys(mainTabSlugs) as Array<keyof typeof mainTabSlugs>).find(tab => mainTabSlugs[tab] === slug) ?? 'jobs'
}

export function withJobsMainTab(current: URLSearchParams, tab: JobsMainTab): URLSearchParams {
  const params = new URLSearchParams(current)
  if (tab === 'jobs') params.delete(logsTabParam)
  else params.set(logsTabParam, mainTabSlugs[tab])
  if (tab !== 'logs') params.delete(logViewParam)
  return params
}

// Loglar has two views: Ürün Hareketleri (what changed on the products; the default, no parameter) and Pazaryeri
// Gönderimleri (the price/stock send log, ?kayit=gonderimler).
const logViewParam = 'kayit'
const sendsLogSlug = 'gonderimler'
export type JobsLogView = 'activity' | 'sends'

export function jobsLogView(params: URLSearchParams): JobsLogView {
  return params.get(logViewParam) === sendsLogSlug ? 'sends' : 'activity'
}

export function withJobsLogView(current: URLSearchParams, view: JobsLogView): URLSearchParams {
  const params = new URLSearchParams(current)
  if (view === 'sends') params.set(logViewParam, sendsLogSlug)
  else params.delete(logViewParam)
  return params
}
