import type { JobCategory } from './job-categories'
import type { JobListTimeRange } from './job-list-query'

// İşlem Takibi's filters and page live in the address (W16), so Back, a refresh or a shared link opens the same view.
//   ?kategori=siparis&durum=hata,bekliyor&statu=DEAD&aralik=7g&ara=corr&sayfa=2&adet=50&platform=trendyol
// Every default is left out of the address. `platform` keeps its own reader (job-list-query.ts).

export type JobStatusChip = 'success' | 'running' | 'waiting' | 'error' | 'cancelled'
export type JobStatusCode = 'PENDING' | 'LEASED' | 'RETRY_SCHEDULED' | 'BLOCKED' | 'MANUAL_REVIEW' | 'SUCCEEDED' | 'DEAD' | 'CANCELLED'

export type JobsViewState = {
  category: JobCategory
  chips: JobStatusChip[]
  status: '' | JobStatusCode
  timeRange: JobListTimeRange
  search: string
  page: number
  pageSize: number
}

const categorySlugs: Record<JobCategory, string> = { ALL: '', ORDERS: 'siparis', PRICE_INVENTORY: 'fiyat-stok', CATALOG: 'urun', INVOICES: 'fatura', RETURNS: 'iade', SYSTEM: 'sistem' }
const chipSlugs: Record<JobStatusChip, string> = { success: 'basarili', running: 'calisiyor', waiting: 'bekliyor', error: 'hata', cancelled: 'iptal' }
const rangeSlugs: Record<JobListTimeRange, string> = { '24h': '', '7d': '7g', all: 'tum' }
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
  return {
    category: fromSlug(categorySlugs, params.get('kategori'), 'ALL'),
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

export function jobsLogsTabOpen(params: URLSearchParams) {
  return params.get(logsTabParam) === logsTabSlug
}

export function withJobsLogsTab(current: URLSearchParams, open: boolean): URLSearchParams {
  const params = new URLSearchParams(current)
  if (open) params.set(logsTabParam, logsTabSlug)
  else params.delete(logsTabParam)
  return params
}
