import { describe, expect, it } from 'vitest'
import type { ProductPlatformStatus } from '../shared'
import { bulkPublishPlatformName, bulkPublishStatusBucket, productConnectionStatus, summarizeBulkPublishStatus } from './bulk-publish-status'

const trendyol = { platformCode: 'TRENDYOL', displayName: 'Ravencia Trendyol' }
const entry = (status: string, extra: Partial<ProductPlatformStatus> = {}): ProductPlatformStatus => ({ platform: 'Ravencia Trendyol', platformCode: 'TRENDYOL', status, matchedVariantCount: 0, variantCount: 4, ...extra })
const product = (...platformStatuses: ProductPlatformStatus[]) => ({ platformStatuses })

describe('bulk publish platform status', () => {
  it('finds the connection by display name and platform code', () => {
    const other = { platform: 'Ravencia Hepsiburada', platformCode: 'HEPSIBURADA', status: 'LIVE' }
    expect(productConnectionStatus(product(other, entry('LIVE')), trendyol)?.status).toBe('LIVE')
    expect(productConnectionStatus(product(other), trendyol)).toBeNull()
  })

  it('leaves a record unknown when its entry is missing, ambiguous or UNKNOWN', () => {
    expect(bulkPublishStatusBucket(productConnectionStatus({}, trendyol))).toBe('unknown')
    expect(bulkPublishStatusBucket(productConnectionStatus(product(entry('LIVE'), entry('UNLINKED')), trendyol))).toBe('unknown')
    expect(bulkPublishStatusBucket(entry('UNKNOWN'))).toBe('unknown')
    expect(bulkPublishStatusBucket(entry('UNKNOWN', { matchedVariantCount: 2 }))).toBe('linked')
  })

  it('reads the server statuses into buckets', () => {
    expect(bulkPublishStatusBucket(entry('LIVE'))).toBe('live')
    expect(bulkPublishStatusBucket(entry('PARTIAL_LIVE'))).toBe('partial')
    expect(bulkPublishStatusBucket(entry('QUEUED'))).toBe('processing')
    expect(bulkPublishStatusBucket(entry('LIVE', { isChecking: true }))).toBe('processing')
    expect(bulkPublishStatusBucket(entry('CREATE_REJECTED'))).toBe('error')
    expect(bulkPublishStatusBucket(entry('PARTIAL_LINKED', { matchedVariantCount: 2 }))).toBe('linked')
    expect(bulkPublishStatusBucket(entry('ARCHIVED'))).toBe('archived')
    expect(bulkPublishStatusBucket(entry('UNLINKED'))).toBe('notPublished')
  })

  it('says how many selected records are live and what the rest are', () => {
    const summary = summarizeBulkPublishStatus([
      ...Array.from({ length: 12 }, () => product(entry('LIVE'))),
      ...Array.from({ length: 25 }, () => product(entry('UNLINKED'))),
      product(entry('QUEUED')),
      null,
      product()
    ], trendyol)
    expect(summary.label).toBe('12/40 kayıt yayında')
    expect(summary.detail).toBe('12 yayında · 1 işlemde · 25 yayında değil · 2 durumu bilinmiyor')
    expect(summary.tone).toBe('info')
    expect(summary.allLive).toBe(false)
  })

  it('names a single shared state and marks a fully live platform', () => {
    expect(summarizeBulkPublishStatus([product(entry('UNLINKED')), product(entry('UNLINKED'))], trendyol)).toMatchObject({ label: 'Yayında değil', detail: null, tone: 'neutral', allLive: false })
    expect(summarizeBulkPublishStatus([product(entry('LIVE')), product(entry('LIVE'))], trendyol)).toMatchObject({ label: '2/2 kayıt yayında', tone: 'success', allLive: true })
    expect(summarizeBulkPublishStatus([product(entry('LIVE'))], trendyol)).toMatchObject({ label: 'Yayında', allLive: true })
    expect(summarizeBulkPublishStatus([null, product()], trendyol)).toMatchObject({ label: 'Durum bilinmiyor', tone: 'neutral', allLive: false })
    expect(summarizeBulkPublishStatus([], trendyol)).toMatchObject({ label: 'Durum bilinmiyor', allLive: false })
  })

  it('warns when some records failed there', () => {
    expect(summarizeBulkPublishStatus([product(entry('LIVE')), product(entry('REJECTED'))], trendyol)).toMatchObject({ label: '1/2 kayıt yayında', detail: '1 yayında · 1 hatalı veya incelemede', tone: 'warning' })
  })

  it('writes platform names the way the brands do', () => {
    expect(bulkPublishPlatformName('PTTAVM')).toBe('PttAVM')
    expect(bulkPublishPlatformName('N11')).toBe('n11')
    expect(bulkPublishPlatformName('TRENDYOL_EFATURAM')).toBe('E-Faturam')
  })
})
