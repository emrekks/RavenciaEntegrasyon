import { describe, expect, it } from 'vitest'
import { jobCountsQueryString, jobDetailPollInterval, jobListHeadPollInterval, jobListMaximumAutoPages, jobListPollInterval, jobListQueryString, jobPageQueryString, jobPlatformFromSearch, jobPlatformOptions, shouldLoadTowardsPage, withFreshFirstPage } from './job-list-query'

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

  it('reads the URL value as a platform code and ignores anything else', () => {
    expect(jobPlatformFromSearch('trendyol_efaturam')).toBe('TRENDYOL_EFATURAM')
    expect(jobPlatformFromSearch(null)).toBeNull()
    expect(jobPlatformFromSearch('a,b')).toBeNull()
  })

  it('orders the connected platforms and keeps the chosen one', () => {
    expect(jobPlatformOptions(['TRENDYOL_EFATURAM', 'shopify', 'TRENDYOL', 'TRENDYOL'], 'N11')).toEqual(['TRENDYOL', 'N11', 'SHOPIFY', 'TRENDYOL_EFATURAM'])
  })
})

describe('poll intervals (W22, G24)', () => {
  it('polls the list every 15 s while only its first page is loaded and stops once older pages are read', () => {
    expect(jobListPollInterval(0)).toBe(15_000)
    expect(jobListPollInterval(1)).toBe(15_000)
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
    expect(jobListHeadPollInterval(2)).toBe(15_000)
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
