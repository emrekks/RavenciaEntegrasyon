import { describe, expect, it } from 'vitest'
import { activeProductSyncJobs, isActiveProductSyncJob } from './product-sync-tracking'

describe('product sync tracking', () => {
  it('tracks pending, leased, and retry-scheduled catalog imports', () => {
    for (const status of ['PENDING', 'LEASED', 'RETRY_SCHEDULED']) {
      expect(isActiveProductSyncJob({ jobType: 'TRENDYOL_PRODUCT_SYNC', status })).toBe(true)
    }
  })

  it('matches job type and status without depending on UI connection selection or letter case', () => {
    expect(isActiveProductSyncJob({ jobType: ' shopify_product_sync ', status: 'pending' })).toBe(true)
    expect(isActiveProductSyncJob({ jobType: ' hepsiburada_product_sync ', status: 'pending' })).toBe(true)
  })

  it('does not treat completed or unrelated jobs as active product imports', () => {
    const jobs = [
      { id: 'done', jobType: 'TRENDYOL_PRODUCT_SYNC', status: 'SUCCEEDED' },
      { id: 'blocked', jobType: 'SHOPIFY_PRODUCT_SYNC', status: 'BLOCKED' },
      { id: 'other', jobType: 'TRENDYOL_ORDER_SYNC', status: 'PENDING' },
      { id: 'active', jobType: 'TRENDYOL_PRODUCT_SYNC', status: 'LEASED' }
    ]

    expect(activeProductSyncJobs(jobs).map(job => job.id)).toEqual(['active'])
  })
})
