import { describe, expect, it } from 'vitest'
import { isPublicationStatusPending, publicationStatusLabel, publicationStatusTone } from './publication-status'

describe('product publication status', () => {
  it('describes the marketplace result rather than the connection state', () => {
    expect(publicationStatusLabel('LIVE')).toBe('Yayında')
    expect(publicationStatusLabel('UPDATE_IN_PROGRESS')).toBe('Trendyol güncellemeyi kontrol ediyor')
    expect(publicationStatusLabel('UNKNOWN')).toBe('Henüz yayınlanmadı')
  })

  it('keeps polling while either the listing or its job is active', () => {
    expect(isPublicationStatusPending('BATCH_IN_PROGRESS', 'RUNNING')).toBe(true)
    expect(isPublicationStatusPending('UNKNOWN', 'PENDING')).toBe(true)
    expect(isPublicationStatusPending('LIVE', 'COMPLETED')).toBe(false)
  })

  it('uses distinct tones for live, pending and rejected results', () => {
    expect(publicationStatusTone('LIVE')).toBe('success')
    expect(publicationStatusTone('QUEUED')).toBe('info')
    expect(publicationStatusTone('CREATE_REJECTED')).toBe('danger')
  })
})
