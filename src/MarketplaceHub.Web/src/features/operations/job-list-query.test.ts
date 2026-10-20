import { describe, expect, it } from 'vitest'
import { jobCountsQueryString, jobListQueryString, jobPageQueryString, jobPlatformFromSearch, jobPlatformOptions, jobTypesForCategory } from './job-list-query'

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

describe('jobTypesForCategory', () => {
  const rows = [{ jobType: 'ORDER_SYNC' }, { jobType: 'PRICE_INVENTORY_SYNC' }, { jobType: 'ORDER_SYNC' }, { jobType: 'SHIPMENT_SYNC' }]

  it('lists the distinct matching types in a stable order', () => {
    expect(jobTypesForCategory(rows, type => /ORDER|SHIPMENT/.test(type), false)).toEqual(['ORDER_SYNC', 'SHIPMENT_SYNC'])
  })

  it('is unknown for all jobs or before the counts arrive, empty for a tab without jobs', () => {
    expect(jobTypesForCategory(rows, () => true, true)).toBeNull()
    expect(jobTypesForCategory(undefined, () => true, false)).toBeNull()
    expect(jobTypesForCategory(rows, type => type.includes('INVOICE'), false)).toEqual([])
  })
})
