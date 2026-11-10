// 24h and 7d are rolling windows (7d only keeps old links such as the dashboard's "7g" working); the others are the shared
// date presets (shared/date-range.ts) that end today, so the server's open-ended `since` is exact for them: it starts at
// the preset's first Istanbul day. Dün, Geçen Hafta and Geçen Ay end in the past and need an upper bound the jobs API
// does not have, so they are not offered here.
import { dateRangePresets } from '../../shared/date-range'
import { istanbulDateKey, istanbulDayBoundary } from '../../shared/format-date'

export type JobListTimeRange = '24h' | '7d' | 'today' | 'week' | 'month' | '30d' | 'year' | 'all'

const rollingMilliseconds: Partial<Record<JobListTimeRange, number>> = {
  '24h': 24 * 60 * 60 * 1000,
  '7d': 7 * 24 * 60 * 60 * 1000
}
const presetLabels: Partial<Record<JobListTimeRange, string>> = { today: 'Bugün', week: 'Bu Hafta', month: 'Bu Ay', '30d': 'Son 30 Gün', year: 'Bu Yıl' }

/** The range menu of İşlem Takibi's heading, shared by the job list and the send log (Loglar). Tüm Zamanlar is no range. */
export const jobTimeRanges = [['24h', 'Son 24 Saat'], ['today', 'Bugün'], ['week', 'Bu Hafta'], ['month', 'Bu Ay'], ['30d', 'Son 30 Gün'], ['year', 'Bu Yıl'], ['all', 'Tüm Zamanlar']] as const satisfies ReadonlyArray<readonly [JobListTimeRange, string]>

/** The menu's choices; a link that opens on Son 7 Gün adds it while it is the chosen one. */
export function jobTimeRangeOptions(selected: JobListTimeRange | '') {
  const options: Array<{ value: string; label: string }> = jobTimeRanges.map(([value, label]) => ({ value, label }))
  return selected === '7d' ? [...options.slice(0, 1), { value: '7d', label: 'Son 7 Gün' }, ...options.slice(1)] : options
}

export function timeRangeLabel(value: JobListTimeRange) {
  return value === '7d' ? 'Son 7 Gün' : jobTimeRanges.find(([range]) => range === value)?.[1] ?? 'Son 24 Saat'
}

/** The instant a range starts (epoch milliseconds); null for Tüm Zamanlar. */
export function timeRangeCutoff(range: JobListTimeRange, now = Date.now()): number | null {
  if (range === 'all') return null
  const rolling = rollingMilliseconds[range]
  if (rolling !== undefined) return now - rolling
  const preset = dateRangePresets(istanbulDateKey(now)).find(item => item.label === presetLabels[range])
  return preset ? Date.parse(istanbulDayBoundary(preset.range.from)) : now - rollingMilliseconds['24h']!
}

/** The instant a range starts, as a UTC ISO string; null for Tüm Zamanlar. */
export function timeRangeStart(range: JobListTimeRange, now = Date.now()) {
  const cutoff = timeRangeCutoff(range, now)
  return cutoff === null ? null : new Date(cutoff).toISOString()
}

/** Jobs per server page of the operations list when nothing else is asked for (the lookup, the counts' callers). */
export const jobPageSize = 200

// The list reads 50 jobs per server page, or one screen page when a bigger one is chosen (100, 200): 200 rows were
// ~186 KB and were read again every 15 s while most of them were never looked at. Daha Fazla Yükle, the next-page
// button and ?sayfa=N links read the next server page as before.
export const jobListFirstPageSize = 50

/** Jobs per server page of İşlem Takibi's list for the chosen screen page size. */
export function jobListServerPageSize(screenPageSize: number) {
  return Math.min(jobPageSize, Math.max(jobListFirstPageSize, screenPageSize))
}

// The server returns only the newest jobs that match, so the selected time range is sent along
// instead of downloading the whole job history and filtering it in the browser.
export function jobListQueryString(status: string, timeRange: JobListTimeRange, now = Date.now()) {
  const params = new URLSearchParams()
  if (status) params.set('status', status)
  const since = timeRangeStart(timeRange, now)
  if (since) params.set('since', since)
  const query = params.toString()
  return query ? `?${query}` : ''
}

// The server's rule for a period (JobOperationsService.InWindow), so the list keeps what the counts count: a job created
// or finished in the period, or one still open (waiting, running, to be retried) however old it is.
export function isJobInTimeRange(job: { createdAt: string; completedAt: string | null; status: string }, timeRange: JobListTimeRange, now = Date.now()) {
  const cutoff = timeRangeCutoff(timeRange, now)
  if (cutoff === null) return true
  if (job.status === 'PENDING' || job.status === 'LEASED' || job.status === 'RETRY_SCHEDULED') return true
  const created = Date.parse(job.createdAt)
  if (Number.isNaN(created) || created >= cutoff) return true
  const completed = job.completedAt ? Date.parse(job.completedAt) : Number.NaN
  return !Number.isNaN(completed) && completed >= cutoff
}

// One cursor page of the job list: the same filters as the list plus the job types of the selected tab and the
// cursor of the previous page, so every filter is applied by the server on every page.
export function jobPageQueryString(status: string, timeRange: JobListTimeRange, options: { jobTypes?: readonly string[] | null; after?: string | null; limit?: number; platform?: string | null } = {}, now = Date.now()) {
  const params = new URLSearchParams(jobListQueryString(status, timeRange, now).slice(1))
  if (options.platform) params.set('platform', options.platform)
  if (options.jobTypes?.length) params.set('jobTypes', options.jobTypes.join(','))
  params.set('limit', String(options.limit ?? jobPageSize))
  if (options.after) params.set('after', options.after)
  return `?${params.toString()}`
}

/** At most this many jobs come back for one looked-up identifier (a correlation id can name a batch). */
export const jobLookupLimit = 50

const jobIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

/** The search is a whole job id (a GUID), as notification links write it (?ara=<job id>). */
export function isJobIdSearch(search: string) {
  return jobIdPattern.test(search.trim())
}

// The search box filters the loaded pages; a term that can be one identifier (no spaces, 4–200 characters) is also
// looked up by the server, which finds the job by its id, correlation id or marketplace id however old it is
// (JobOperationsService.MatchingSearch). Null when the term is not looked up.
export function jobLookupTerm(search: string): string | null {
  const term = search.trim()
  return term.length >= 4 && term.length <= 200 && !/\s/.test(term) ? term : null
}

// The lookup reads no other filter: the list's own filters still apply to what it brings, except the time range.
export function jobLookupQueryString(term: string) {
  return `?${new URLSearchParams({ search: term, limit: String(jobLookupLimit) }).toString()}`
}

// The tab and status counts: the selected range and platform.
export function jobCountsQueryString(timeRange: JobListTimeRange, platform: string | null, now = Date.now()) {
  const params = new URLSearchParams(jobListQueryString('', timeRange, now).slice(1))
  if (platform) params.set('platform', platform)
  const query = params.toString()
  return query ? `?${query}` : ''
}

// Platforms in the selector's order; E-Faturam is the invoicing provider, the rest are marketplaces.
export const jobPlatformOrder = ['TRENDYOL', 'HEPSIBURADA', 'N11', 'PAZARAMA', 'PTTAVM', 'SHOPIFY', 'TRENDYOL_EFATURAM'] as const

// The URL value (?platform=trendyol or ?platform=trendyol,N11) as connection platform codes; none means every platform.
// The server reads the same comma list (JobEndpoints.Platforms, at most 20 codes).
export function jobPlatformsFromSearch(value: string | null): string[] {
  const codes = (value ?? '').split(',').map(code => code.trim().toUpperCase()).filter(code => /^[A-Z0-9_]{2,40}$/.test(code))
  return [...new Set(codes)].slice(0, 20)
}

// The platform choices: every platform the tenant has a connection on, plus the chosen one, in a fixed order.
export function jobPlatformOptions(connectionPlatforms: readonly string[], selected: readonly string[]) {
  const codes = new Set(connectionPlatforms.map(code => code.trim().toUpperCase()).filter(Boolean))
  for (const code of selected) codes.add(code)
  const rank = (code: string) => { const index = (jobPlatformOrder as readonly string[]).indexOf(code); return index < 0 ? jobPlatformOrder.length : index }
  return [...codes].sort((left, right) => rank(left) - rank(right) || left.localeCompare(right))
}

// How often the page polls. The list polls every 30 s while just its first page is loaded; once older pages are read
// only the first page is polled and put in front of them (refetching every loaded page cost one request per page,
// W22/G24), so a queued job that no realtime event announces still shows up. Realtime job events refresh it too (at
// once for a job that needs attention, else at most once per 15 s, app/realtime-query-keys.ts), and a refresh restarts
// the poll's clock. Yenile refreshes it all. The counts follow the same 30 s. An open job detail polls until the job
// finished.
export const jobListPollMs = 30_000
const terminalJobStatuses: ReadonlySet<string> = new Set(['SUCCEEDED', 'DEAD', 'CANCELLED'])

export function jobListPollInterval(loadedPages: number): number | false {
  return loadedPages > 1 ? false : jobListPollMs
}

/** While older pages are loaded, only the first page is polled (jobListHeadPollInterval) and put in front of them. */
export function jobListHeadPollInterval(loadedPages: number): number | false {
  return loadedPages > 1 ? jobListPollMs : false
}

/** The loaded pages with a fresh first page in front; the page rows are deduplicated where they are shown. */
export function withFreshFirstPage<Page, Param>(current: { pages: Page[]; pageParams: Param[] } | undefined, first: Page) {
  return current && current.pages.length > 1 ? { ...current, pages: [first, ...current.pages.slice(1)] } : current
}

// A ?sayfa=N link opens page N: server pages are read until that page has rows or the server has no more, at most
// this many server pages so a filter that matches nothing cannot read the whole history (100 pages of 50 reach as far
// as the 25 pages of 200 did).
export const jobListMaximumAutoPages = 100

export function shouldLoadTowardsPage(requestedPage: number, totalPages: number, hasNextPage: boolean, loadedPages: number, busy: boolean) {
  return requestedPage > totalPages && hasNextPage && !busy && loadedPages < jobListMaximumAutoPages
}

export function jobDetailPollInterval(status: string | undefined): number | false {
  return status && terminalJobStatuses.has(status) ? false : 10_000
}
