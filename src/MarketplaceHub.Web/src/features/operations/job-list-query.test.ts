import { describe, expect, it } from 'vitest'
import { jobTimeRangeOptions, timeRangeStart, isJobIdSearch, isJobInTimeRange, jobLookupQueryString, jobLookupTerm, jobCountsQueryString,jobDetailPollInterval, jobListHeadPollInterval, jobListMaximumAutoPages, jobListPollInterval, jobListQueryString, jobListServerPageSize, jobPageQueryString, jobPlatformsFromSearch, jobPlatformOptions, shouldLoadTowardsPage, withFreshFirstPage } from './job-list-query'

describe('jobListQueryString', () => {
  const now = Date.parse('2026-10-14T12:00:00.000Z')

  it('sends the selected time range as since', () => {
    expect(jobListQueryString('', '24h', now)).toBe('?since=2026-10-13T12%3A00%3A00.000Z')
    expect(jobListQueryString('DEAD', '7d', now)).toBe('?status=DEAD&since=2026-10-07T12%3A00%3A00.000Z')
  })

  it('omits since for all time and returns nothing without filters', () => {
    expect(jobListQueryString('', 'all', now)).toBe('')
    expect(jobListQueryString('LEASED', 'all', now)).toBe('?status=LEASED')
  })
})

describe('isJobInTimeRange (P7)', () => {
  const now = Date.parse('2026-11-01T12:00:00.000Z')
  const old = '2026-10-28T12:00:00.000Z'

  it('keeps a job open for days in the last 24 hours, like the server counts it', () => {
    expect(isJobInTimeRange({ createdAt: old, completedAt: null, status: 'RETRY_SCHEDULED' }, '24h', now)).toBe(true)
    expect(isJobInTimeRange({ createdAt: old, completedAt: null, status: 'PENDING' }, '24h', now)).toBe(true)
  })

  it('keeps an old job that finished inside the period and drops one that finished before it', () => {
    expect(isJobInTimeRange({ createdAt: old, completedAt: '2026-11-01T08:00:00.000Z', status: 'SUCCEEDED' }, '24h', now)).toBe(true)
    expect(isJobInTimeRange({ createdAt: old, completedAt: '2026-10-29T08:00:00.000Z', status: 'BLOCKED' }, '24h', now)).toBe(false)
    expect(isJobInTimeRange({ createdAt: old, completedAt: '2026-10-29T08:00:00.000Z', status: 'BLOCKED' }, '7d', now)).toBe(true)
    expect(isJobInTimeRange({ createdAt: old, completedAt: null, status: 'BLOCKED' }, 'all', now)).toBe(true)
  })
})

describe('jobPageQueryString', () => {
  const now = Date.parse('2026-10-14T12:00:00.000Z')

  it('keeps the list filters and adds the page size', () => {
    expect(jobPageQueryString('DEAD', '24h', {}, now)).toBe('?status=DEAD&since=2026-10-13T12%3A00%3A00.000Z&limit=200')
    expect(jobPageQueryString('', 'all', {}, now)).toBe('?limit=200')
  })

  it('sends the tab job types and the cursor of the previous page', () => {
    const params = new URLSearchParams(jobPageQueryString('', 'all', { jobTypes: ['ORDER_SYNC', 'SHIPMENT_SYNC'], after: 'abc_-1', limit: 50 }, now).slice(1))
    expect(params.get('jobTypes')).toBe('ORDER_SYNC,SHIPMENT_SYNC')
    expect(params.get('after')).toBe('abc_-1')
    expect(params.get('limit')).toBe('50')
  })
})

describe('platform helpers', () => {
  const now = Date.parse('2026-10-14T12:00:00.000Z')

  it('adds the platform to the page and counts requests', () => {
    expect(new URLSearchParams(jobPageQueryString('', 'all', { platform: 'N11' }, now).slice(1)).get('platform')).toBe('N11')
    expect(jobCountsQueryString('all', 'PAZARAMA', now)).toBe('?platform=PAZARAMA')
    expect(jobCountsQueryString('all', null, now)).toBe('')
  })

  it('reads the URL value as a list of platform codes and ignores anything else', () => {
    expect(jobPlatformsFromSearch('trendyol_efaturam')).toEqual(['TRENDYOL_EFATURAM'])
    expect(jobPlatformsFromSearch('trendyol, n11,trendyol')).toEqual(['TRENDYOL', 'N11'])
    expect(jobPlatformsFromSearch(null)).toEqual([])
    expect(jobPlatformsFromSearch('a,b c,<x>')).toEqual([])
  })

  it('orders the connected platforms and keeps the chosen ones', () => {
    expect(jobPlatformOptions(['TRENDYOL_EFATURAM', 'shopify', 'TRENDYOL', 'TRENDYOL'], ['N11', 'PAZARAMA'])).toEqual(['TRENDYOL', 'N11', 'PAZARAMA', 'SHOPIFY', 'TRENDYOL_EFATURAM'])
  })
})

describe('server page size', () => {
  it('reads 50 jobs per page, or one screen page when a bigger one is chosen', () => {
    expect([20, 50, 100, 200, 500].map(jobListServerPageSize)).toEqual([50, 50, 100, 200, 200])
  })
})

describe('poll intervals (W22, G24)', () => {
  it('polls the list every 30 s while only its first page is loaded and stops once older pages are read', () => {
    expect(jobListPollInterval(0)).toBe(30_000)
    expect(jobListPollInterval(1)).toBe(30_000)
    expect(jobListPollInterval(2)).toBe(false)
  })

  it('polls an open job until it finished', () => {
    expect(jobDetailPollInterval(undefined)).toBe(10_000)
    expect(jobDetailPollInterval('LEASED')).toBe(10_000)
    expect(jobDetailPollInterval('SUCCEEDED')).toBe(false)
    expect(jobDetailPollInterval('DEAD')).toBe(false)
  })
})

describe('a list with older pages loaded', () => {
  it('polls only its first page and puts it in front of the older ones', () => {
    expect(jobListHeadPollInterval(1)).toBe(false)
    expect(jobListHeadPollInterval(2)).toBe(30_000)
    const loaded = { pages: ['old-1', 'old-2', 'old-3'], pageParams: [null, 'a', 'b'] }
    expect(withFreshFirstPage(loaded, 'new-1')).toEqual({ pages: ['new-1', 'old-2', 'old-3'], pageParams: [null, 'a', 'b'] })
    expect(withFreshFirstPage({ pages: ['only'], pageParams: [null] }, 'new')).toEqual({ pages: ['only'], pageParams: [null] })
    expect(withFreshFirstPage(undefined, 'new')).toBeUndefined()
  })

  it('reads server pages towards a ?sayfa=N link until the page has rows, within a bound', () => {
    expect(shouldLoadTowardsPage(5, 2, true, 1, false)).toBe(true)
    expect(shouldLoadTowardsPage(5, 2, true, 1, true)).toBe(false)
    expect(shouldLoadTowardsPage(2, 2, true, 1, false)).toBe(false)
    expect(shouldLoadTowardsPage(5, 2, false, 1, false)).toBe(false)
    expect(shouldLoadTowardsPage(500, 2, true, jobListMaximumAutoPages, false)).toBe(false)
  })
})

describe('job lookup (search by job id, correlation id or marketplace id)', () => {
  it('looks up one identifier and nothing shorter or with spaces', () => {
    expect(jobLookupTerm('  01a1631c-aa8f-79f0-8ff6-9ccd6c21023e ')).toBe('01a1631c-aa8f-79f0-8ff6-9ccd6c21023e')
    expect(jobLookupTerm('scheduler-2a4d22341c48453db9652055a7f0f7c1')).toBe('scheduler-2a4d22341c48453db9652055a7f0f7c1')
    expect(jobLookupTerm('10987654321')).toBe('10987654321')
    expect(jobLookupTerm('abc')).toBeNull()
    expect(jobLookupTerm('stok güncelleme')).toBeNull()
    expect(jobLookupTerm('x'.repeat(201))).toBeNull()
  })

  it('reads no other filter, so the lookup ignores the time range', () => {
    expect(jobLookupQueryString('corr 1&x')).toBe('?search=corr+1%26x&limit=50')
  })

  it('knows a whole job id', () => {
    expect(isJobIdSearch('01A1631C-AA8F-79F0-8FF6-9CCD6C21023E')).toBe(true)
    expect(isJobIdSearch('01a1631c-aa8f-79f0')).toBe(false)
    expect(isJobIdSearch('corr-01a1631c-aa8f-79f0-8ff6-9ccd6c21023e')).toBe(false)
  })
})

describe('shared date presets as job ranges', () => {
  // Wednesday 2026-10-14 15:00 Istanbul; the server's since is a UTC instant at the preset's first Istanbul day.
  const now = Date.parse('2026-10-14T12:00:00.000Z')
  it('starts each preset at its first Istanbul day (weeks on Monday) and Tüm Zamanlar has no start', () => {
    expect(timeRangeStart('today', now)).toBe('2026-10-13T21:00:00.000Z')
    expect(timeRangeStart('week', now)).toBe('2026-10-11T21:00:00.000Z')
    expect(timeRangeStart('month', now)).toBe('2026-09-30T21:00:00.000Z')
    expect(timeRangeStart('30d', now)).toBe('2026-09-14T21:00:00.000Z')
    expect(timeRangeStart('year', now)).toBe('2025-12-31T21:00:00.000Z')
    expect(timeRangeStart('all', now)).toBeNull()
    expect(jobListQueryString('', 'today', now)).toBe('?since=2026-10-13T21%3A00%3A00.000Z')
  })

  it('keeps the rolling windows and offers a Son 7 Gün link only while it is chosen', () => {
    expect(timeRangeStart('24h', now)).toBe('2026-10-13T12:00:00.000Z')
    expect(jobTimeRangeOptions('24h').map(option => option.value)).toEqual(['24h', 'today', 'week', 'month', '30d', 'year', 'all'])
    expect(jobTimeRangeOptions('7d').map(option => option.value)).toContain('7d')
  })
})
