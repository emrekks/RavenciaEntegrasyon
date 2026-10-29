import { describe, expect, it } from 'vitest'
import { publicationJobProgress, publicationJobStatusLabel } from './PublicationJobDialog'

const job = { id: 'j1', jobType: 'N11_PRODUCT_CREATE', status: 'LEASED', attemptCount: 1, maxAttempts: 5, correlationId: 'c1', createdAt: '2026-10-29T08:00:00Z', startedAt: null, completedAt: null, nextRetryAt: null, heartbeatAt: null, progressCurrent: 2, progressTotal: 5, progressPercent: 40, progressLabel: 'Renkler gönderiliyor', lastErrorCode: null, lastErrorSummary: null }

describe('publication job popup', () => {
  it('names the job status in the panel words', () => {
    expect(publicationJobStatusLabel('leased')).toBe('Çalışıyor')
    expect(publicationJobStatusLabel('RETRY_SCHEDULED')).toBe('Yeniden Denenecek')
    expect(publicationJobStatusLabel('SOMETHING_NEW')).toBe('SOMETHING_NEW')
  })

  it('shows the progress it knows and nothing when it knows none', () => {
    expect(publicationJobProgress(job)).toBe('%40 · 2/5 · Renkler gönderiliyor')
    expect(publicationJobProgress({ ...job, progressPercent: null, progressTotal: null, progressLabel: null })).toBeNull()
  })
})

describe('a card refreshed without a publication record', () => {
  it('looks the product up by the key each marketplace takes', async () => {
    const { cardLookupKey } = await import('./ProductEditorPages')
    const keys = { modelCode: 'MZ035YNC', barcode: '8681234567890', sku: 'MZ035YNC-01' }
    expect(cardLookupKey('TRENDYOL', keys)).toBe('MZ035YNC')
    expect(cardLookupKey('N11', keys)).toBe('MZ035YNC')
    expect(cardLookupKey('PAZARAMA', keys)).toBe('8681234567890')
    expect(cardLookupKey('PTTAVM', keys)).toBe('8681234567890')
    expect(cardLookupKey('SHOPIFY', { ...keys, barcode: '' })).toBe('MZ035YNC-01')
    expect(cardLookupKey('HEPSIBURADA', keys)).toBeNull()
  })
})
