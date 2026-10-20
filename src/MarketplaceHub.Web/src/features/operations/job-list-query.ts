export type JobListTimeRange = '24h' | '7d' | 'all'

const rangeMilliseconds: Record<Exclude<JobListTimeRange, 'all'>, number> = {
  '24h': 24 * 60 * 60 * 1000,
  '7d': 7 * 24 * 60 * 60 * 1000
}

/** Jobs per server page of the operations list. */
export const jobPageSize = 200

// The server returns only the newest jobs that match, so the selected time range is sent along
// instead of downloading the whole job history and filtering it in the browser.
export function jobListQueryString(status: string, timeRange: JobListTimeRange, now = Date.now()) {
  const params = new URLSearchParams()
  if (status) params.set('status', status)
  if (timeRange !== 'all') params.set('since', new Date(now - rangeMilliseconds[timeRange]).toISOString())
  const query = params.toString()
  return query ? `?${query}` : ''
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

// The tab and status counts: the selected range and platform.
export function jobCountsQueryString(timeRange: JobListTimeRange, platform: string | null, now = Date.now()) {
  const params = new URLSearchParams(jobListQueryString('', timeRange, now).slice(1))
  if (platform) params.set('platform', platform)
  const query = params.toString()
  return query ? `?${query}` : ''
}

// Platforms in the selector's order; E-Faturam is the invoicing provider, the rest are marketplaces.
export const jobPlatformOrder = ['TRENDYOL', 'HEPSIBURADA', 'N11', 'PAZARAMA', 'SHOPIFY', 'TRENDYOL_EFATURAM'] as const

// The URL value (?platform=trendyol) as a connection platform code, or null for every platform.
export function jobPlatformFromSearch(value: string | null) {
  const code = value?.trim().toUpperCase() ?? ''
  return /^[A-Z0-9_]{2,40}$/.test(code) ? code : null
}

// The platform choices: every platform the tenant has a connection on, plus the chosen one, in a fixed order.
export function jobPlatformOptions(connectionPlatforms: readonly string[], selected: string | null) {
  const codes = new Set(connectionPlatforms.map(code => code.trim().toUpperCase()).filter(Boolean))
  if (selected) codes.add(selected)
  const rank = (code: string) => { const index = (jobPlatformOrder as readonly string[]).indexOf(code); return index < 0 ? jobPlatformOrder.length : index }
  return [...codes].sort((left, right) => rank(left) - rank(right) || left.localeCompare(right))
}

// The job types of a tab, taken from the server's per-type counts for the selected range. Null while the counts are
// unknown (the tab is then filtered in the browser only); an empty list means the tab has no job in the range.
export function jobTypesForCategory(countRows: ReadonlyArray<{ jobType: string }> | undefined, matches: (jobType: string) => boolean, allJobs: boolean) {
  if (allJobs || !countRows) return null
  return [...new Set(countRows.map(row => row.jobType).filter(matches))].sort()
}
