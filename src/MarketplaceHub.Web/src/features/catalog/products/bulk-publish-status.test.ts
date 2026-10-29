import { describe, expect, it } from 'vitest'
import type { ProductPlatformStatus } from '../shared'
import { bulkPublishModelTooltip, bulkPublishPlatformName, bulkPublishStatusBucket, productConnectionStatus, summarizeBulkPublishModels, summarizeBulkPublishStatus } from './bulk-publish-status'

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
    expect(summary.label).toBe('12/40 Kayıt Yayında')
    expect(summary.detail).toBe('12 yayında · 1 işlemde · 25 yayında değil · 2 durumu bilinmiyor')
    expect(summary.tone).toBe('info')
    expect(summary.allLive).toBe(false)
  })

  it('names a single shared state and marks a fully live platform', () => {
    expect(summarizeBulkPublishStatus([product(entry('UNLINKED')), product(entry('UNLINKED'))], trendyol)).toMatchObject({ label: 'Yayında değil', detail: null, tone: 'neutral', allLive: false })
    expect(summarizeBulkPublishStatus([product(entry('LIVE')), product(entry('LIVE'))], trendyol)).toMatchObject({ label: '2/2 Kayıt Yayında', tone: 'success', allLive: true })
    expect(summarizeBulkPublishStatus([product(entry('LIVE'))], trendyol)).toMatchObject({ label: 'Yayında', allLive: true })
    expect(summarizeBulkPublishStatus([null, product()], trendyol)).toMatchObject({ label: 'Durum bilinmiyor', tone: 'neutral', allLive: false })
    expect(summarizeBulkPublishStatus([], trendyol)).toMatchObject({ label: 'Durum bilinmiyor', allLive: false })
  })

  it('warns when some records failed there', () => {
    expect(summarizeBulkPublishStatus([product(entry('LIVE')), product(entry('REJECTED'))], trendyol)).toMatchObject({ label: '1/2 Kayıt Yayında', detail: '1 yayında · 1 hatalı veya incelemede', tone: 'warning' })
  })

  it('names records the marketplace no longer knows Bulunamadı', () => {
    expect(bulkPublishStatusBucket(entry('NOT_FOUND', { missingVariantCount: 4 }))).toBe('notFound')
    expect(summarizeBulkPublishStatus([product(entry('NOT_FOUND'))], trendyol)).toMatchObject({ label: 'Bulunamadı', tone: 'danger', allLive: false })
    expect(summarizeBulkPublishStatus([product(entry('LIVE')), product(entry('NOT_FOUND'))], trendyol)).toMatchObject({ label: '1/2 Kayıt Yayında', detail: '1 yayında · 1 pazaryerinde bulunamadı', tone: 'warning' })
  })

  it('writes platform names the way the brands do', () => {
    expect(bulkPublishPlatformName('PTTAVM')).toBe('PttAVM')
    expect(bulkPublishPlatformName('N11')).toBe('n11')
    expect(bulkPublishPlatformName('TRENDYOL_EFATURAM')).toBe('E-Faturam')
  })
})

describe('bulk publish status per model code', () => {
  const record = (modelCode: string | null, status: string | null, title = 'Bluz') => ({ modelCode, title, variants: [], platformStatuses: status ? [entry(status)] : [] })
  const many = (count: number, modelCode: string, status: string | null) => Array.from({ length: count }, () => record(modelCode, status))

  it('counts live records per model code, incomplete first', () => {
    const rows = summarizeBulkPublishModels([...many(24, 'MZ053TCD', 'LIVE'), ...many(3, 'RYP005', 'LIVE'), ...many(15, 'RYP005', 'UNLINKED')], trendyol)
    expect(rows).toEqual([
      { modelCode: 'RYP005', live: 3, total: 18, missing: 15, unknown: 0 },
      { modelCode: 'MZ053TCD', live: 24, total: 24, missing: 0, unknown: 0 }
    ])
    expect(bulkPublishModelTooltip(rows).rows.map(row => `${row.modelCode} · ${row.text}`)).toEqual(['RYP005 · 3/18 kayıt · 15 eksik', 'MZ053TCD · 24/24 kayıt · Tam'])
  })

  it('keeps unknown records apart from missing ones', () => {
    const rows = summarizeBulkPublishModels([record('A1', 'LIVE'), record('A1', null), null], trendyol)
    expect(rows).toEqual([
      { modelCode: 'A1', live: 1, total: 2, missing: 0, unknown: 1 },
      { modelCode: 'Yüklenmemiş kayıt', live: 0, total: 1, missing: 0, unknown: 1 }
    ])
    expect(bulkPublishModelTooltip(rows).rows[0]).toMatchObject({ text: '1/2 kayıt · 1 bilinmiyor', complete: false })
  })

  it('groups a record without a model code under its variant code or title', () => {
    const rows = summarizeBulkPublishModels([{ modelCode: null, title: 'Elbise', variants: [{ modelCode: 'V9' }], platformStatuses: [entry('LIVE')] }, record(null, 'LIVE', 'Etek')], trendyol)
    expect(rows.map(row => row.modelCode)).toEqual(['Etek', 'V9'])
  })

  it('shows at most eight models and counts the rest', () => {
    const rows = summarizeBulkPublishModels(Array.from({ length: 11 }, (_, index) => record(`M${String(index).padStart(2, '0')}`, 'LIVE')), trendyol)
    const tooltip = bulkPublishModelTooltip(rows)
    expect(tooltip.rows).toHaveLength(8)
    expect(tooltip.moreCount).toBe(3)
  })
})

describe('a bulk publication card frame', () => {
  it('follows the summary like a product popup card', async () => {
    const { bulkPublishCardState } = await import('./BulkPublishDialog')
    const summary = (tone: 'success' | 'info' | 'warning' | 'danger' | 'neutral', allLive = false) => ({ tone, allLive, label: '', detail: null }) as unknown as Parameters<typeof bulkPublishCardState>[0]
    expect(bulkPublishCardState(null)).toBe('unpublished')
    expect(bulkPublishCardState(summary('neutral'))).toBe('unpublished')
    expect(bulkPublishCardState(summary('success'))).toBe('live')
    expect(bulkPublishCardState(summary('info', true))).toBe('live')
    expect(bulkPublishCardState(summary('info'))).toBe('working')
    expect(bulkPublishCardState(summary('danger'))).toBe('error')
  })
})
