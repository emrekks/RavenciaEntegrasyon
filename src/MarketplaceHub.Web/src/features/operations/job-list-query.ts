export type JobListTimeRange = '24h' | '7d' | 'all'

const rangeMilliseconds: Record<Exclude<JobListTimeRange, 'all'>, number> = {
  '24h': 24 * 60 * 60 * 1000,
  '7d': 7 * 24 * 60 * 60 * 1000
}

// The server returns only the newest jobs that match, so the selected time range is sent along
// instead of downloading the whole job history and filtering it in the browser.
export function jobListQueryString(status: string, timeRange: JobListTimeRange, now = Date.now()) {
  const params = new URLSearchParams()
  if (status) params.set('status', status)
  if (timeRange !== 'all') params.set('since', new Date(now - rangeMilliseconds[timeRange]).toISOString())
  const query = params.toString()
  return query ? `?${query}` : ''
}
