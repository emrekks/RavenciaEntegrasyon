import { describe, expect, it } from 'vitest'
import { statusLabel } from '../../shared/status-labels'
import { publicationJobInline, type PublicationJobSnapshot } from './publication-job-inline'

const job = (overrides: Partial<PublicationJobSnapshot>): PublicationJobSnapshot => ({
  jobType: 'PRODUCT_CREATE',
  status: 'LEASED',
  progressCurrent: 0,
  progressTotal: null,
  progressPercent: null,
  progressLabel: null,
  lastErrorCode: null,
  lastErrorSummary: null,
  ...overrides
})

describe('publication job inline state', () => {
  it('shows nothing for a missing, succeeded or approval-wait job', () => {
    expect(publicationJobInline(null)).toEqual({ state: 'hidden' })
    expect(publicationJobInline(job({ status: ' succeeded ' }))).toEqual({ state: 'hidden' })
    expect(publicationJobInline(job({ jobType: 'PRODUCT_APPROVAL_RECONCILE', status: 'RETRY_SCHEDULED', lastErrorCode: 'PRODUCT_APPROVAL_PENDING' }))).toEqual({ state: 'hidden' })
  })

  it('puts a running job message into the badge tooltip text', () => {
    expect(publicationJobInline(job({ lastErrorSummary: 'N11 ürün açma görevinin sonucu bekleniyor.' })))
      .toEqual({ state: 'running', message: 'Yayın işlemi: N11 ürün açma görevinin sonucu bekleniyor.' })
    expect(publicationJobInline(job({ status: 'RETRY_SCHEDULED', progressLabel: 'Varyantlar', progressPercent: 140, progressCurrent: 3, progressTotal: 4 })))
      .toEqual({ state: 'running', message: `Varyantlar: %100 · ${(3).toLocaleString('tr-TR')} / ${(4).toLocaleString('tr-TR')}` })
    expect(publicationJobInline(job({ status: 'PENDING' }))).toEqual({ state: 'running', message: `Yayın işlemi: ${statusLabel('PENDING')}` })
  })

  it('leaves one line for a job that stopped on its own', () => {
    expect(publicationJobInline(job({ status: 'BLOCKED', lastErrorSummary: 'Görsel eksik.' })))
      .toEqual({ state: 'settled', message: `${statusLabel('BLOCKED')} · Görsel eksik.`, tone: 'danger' })
    expect(publicationJobInline(job({ status: 'CANCELLED' }))).toEqual({ state: 'settled', message: statusLabel('CANCELLED'), tone: 'muted' })
  })
})
