import { describe, expect, it } from 'vitest'
import { isPublicationStatusJobRunning, isPublicationStatusPending, missingPublicationChecks, publicationStatusLabel, publicationStatusNote, publicationStatusTone } from './publication-status'

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
    expect(isPublicationStatusJobRunning('PENDING')).toBe(true)
    expect(isPublicationStatusJobRunning('RETRY_SCHEDULED')).toBe(false)
  })

  it('shows an unknown listing as not published after a successful missing-product check', () => {
    expect(publicationStatusLabel('UNKNOWN', 'SUCCEEDED')).toBe('Henüz yayınlanmadı')
  })

  it('returns only incomplete publication checks', () => {
    const checks = [
      { title: 'Görseller', ok: true },
      { title: 'Kategori', ok: false },
      { title: 'Varyant', ok: true }
    ]
    expect(missingPublicationChecks(checks).map(check => check.title)).toEqual(['Kategori'])
  })

  it('uses distinct tones for live, pending and rejected results', () => {
    expect(publicationStatusTone('LIVE')).toBe('success')
    expect(publicationStatusTone('QUEUED')).toBe('info')
    expect(publicationStatusTone('CREATE_REJECTED')).toBe('danger')
  })

  it('hides the split-content note while preserving live status and other rejection codes', () => {
    expect(publicationStatusTone('LIVE')).toBe('success')
    expect(publicationStatusTone('PARTIAL_LIVE')).toBe('success')
    expect(publicationStatusNote('PRODUCT_APPROVAL_CONTENT_SPLIT')).toBeNull()
    expect(publicationStatusNote('PRODUCT_CREATE_REJECTED')).toBe('Red kodu: PRODUCT_CREATE_REJECTED')
  })
})
