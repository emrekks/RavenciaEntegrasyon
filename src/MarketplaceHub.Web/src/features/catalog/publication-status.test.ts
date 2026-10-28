import { describe, expect, it } from 'vitest'
import { isApprovalWaitJob, isPttAvmFamilyRetryable, isPublicationLive, isPublicationSelectionDisabled, isPublicationStatusJobRunning, isPublicationStatusPending, missingPublicationChecks, publicationStatusLabel, publicationStatusNote, isSkippedByPublish, publicationStatusTone, shouldCheckPublicationAttributes } from './publication-status'

describe('product publication status', () => {
  it('describes the marketplace result rather than the connection state', () => {
    expect(publicationStatusLabel('LIVE')).toBe('Yayında')
    expect(publicationStatusLabel('UPDATE_IN_PROGRESS')).toBe('Trendyol güncellemeyi kontrol ediyor')
    expect(publicationStatusLabel('BATCH_SUBMITTED', null, 'PTTAVM')).toBe("PttAVM'ye gönderildi")
    expect(publicationStatusLabel('BATCH_IN_PROGRESS', null, 'PAZARAMA')).toBe('Pazarama kontrol ediyor')
    expect(publicationStatusLabel('BATCH_SUBMITTED', null, 'TRENDYOL')).toBe("Trendyol'a gönderildi")
    expect(publicationStatusLabel('IMPORT_SUBMITTED')).toBe("Hepsiburada'ya gönderildi")
    expect(isPublicationStatusPending('IMPORT_SUBMITTED')).toBe(true)
    expect(publicationStatusLabel('UNKNOWN')).toBe('Henüz yayınlanmadı')
  })

  it('keeps polling while either the listing or its job is active', () => {
    expect(isPublicationStatusPending('BATCH_IN_PROGRESS', 'RUNNING')).toBe(true)
    expect(isPublicationStatusPending('UNKNOWN', 'PENDING')).toBe(true)
    expect(isPublicationStatusPending('LIVE', 'COMPLETED')).toBe(false)
    expect(isPublicationStatusJobRunning('PENDING')).toBe(true)
    expect(isPublicationStatusJobRunning('RETRY_SCHEDULED')).toBe(false)
  })

  it('locks only fully live listings against selecting them for publication again', () => {
    expect(isPublicationLive('LIVE')).toBe(true)
    expect(isPublicationLive(' live ')).toBe(true)
    expect(isPublicationLive('PARTIAL_LIVE')).toBe(false)
    expect(isPublicationLive('UNKNOWN')).toBe(false)
    expect(isPublicationLive(null)).toBe(false)
  })

  it('skips attribute-readiness checks for live listings and jobs awaiting publication results', () => {
    expect(shouldCheckPublicationAttributes('category', 'LIVE', 'SUCCEEDED')).toBe(false)
    expect(shouldCheckPublicationAttributes('category', 'APPROVAL_PENDING', 'RETRY_SCHEDULED')).toBe(false)
    expect(shouldCheckPublicationAttributes('category', 'UNKNOWN', 'PENDING')).toBe(false)
    expect(shouldCheckPublicationAttributes('category', 'UNKNOWN', 'SUCCEEDED')).toBe(true)
    expect(shouldCheckPublicationAttributes(null, 'UNKNOWN', 'SUCCEEDED')).toBe(false)
  })

  it('blocks live listings and fails closed while publication status is unavailable', () => {
    expect(isPublicationSelectionDisabled('LIVE', false, false, false, false)).toBe(true)
    expect(isPublicationSelectionDisabled('LIVE', true, false, false, false)).toBe(true)
    expect(isPublicationSelectionDisabled('UNKNOWN', false, true, false, false)).toBe(true)
    expect(isPublicationSelectionDisabled('UNKNOWN', false, false, true, false)).toBe(true)
    expect(isPublicationSelectionDisabled('PARTIAL_LIVE', false, false, false, false)).toBe(false)
    expect(isPublicationSelectionDisabled('UNKNOWN', true, false, false, true)).toBe(false)
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

  it('shows a PTT AVM dry run as prepared and not sent, without a rejection code', () => {
    expect(publicationStatusLabel('DRY_RUN', 'BLOCKED')).toBe('Kuru çalıştırma, gönderilmedi')
    expect(publicationStatusTone('DRY_RUN', 'BLOCKED')).toBe('info')
    expect(publicationStatusNote('PTTAVM_VARIANT_SEND_DRY_RUN')).toBeNull()
  })

  it('shows a PTT AVM family it already holds whole as not sent, without a rejection code', () => {
    expect(publicationStatusLabel('ALREADY_PRESENT', 'SUCCEEDED')).toBe("PTT AVM'de zaten var, gönderilmedi")
    expect(publicationStatusTone('ALREADY_PRESENT', 'SUCCEEDED')).toBe('info')
    expect(publicationStatusNote('PTTAVM_VARIANTS_ALREADY_PRESENT')).toBeNull()
  })
})

describe('PTT AVM family retry', () => {
  it('lets a family card waiting for an approval that may never come, or a stopped one, go again', () => {
    expect(isPttAvmFamilyRetryable('PTTAVM', 'APPROVAL_PENDING', 'RETRY_SCHEDULED')).toBe(true)
    expect(isPttAvmFamilyRetryable('PTTAVM', 'APPROVAL_PARTIAL_PENDING', 'PENDING')).toBe(true)
    expect(isPttAvmFamilyRetryable(' pttavm ', 'MANUAL_REVIEW', 'MANUAL_REVIEW')).toBe(true)
    expect(isPttAvmFamilyRetryable('PTTAVM', 'BLOCKED', null)).toBe(true)
    expect(isPttAvmFamilyRetryable('PTTAVM', 'CREATE_REJECTED', 'FAILED')).toBe(true)
    expect(isPttAvmFamilyRetryable('PTTAVM', 'PARTIAL_FAILURE', 'SUCCEEDED')).toBe(true)
  })

  it('keeps live, in-flight and other marketplaces cards as they are', () => {
    expect(isPttAvmFamilyRetryable('PTTAVM', 'LIVE', null)).toBe(false)
    expect(isPttAvmFamilyRetryable('PTTAVM', 'BATCH_SUBMITTED', 'RETRY_SCHEDULED')).toBe(false)
    expect(isPttAvmFamilyRetryable('PTTAVM', 'APPROVAL_PENDING', 'LEASED')).toBe(false)
    expect(isPttAvmFamilyRetryable('PTTAVM', 'APPROVAL_PENDING', 'RUNNING')).toBe(false)
    expect(isPttAvmFamilyRetryable('TRENDYOL', 'APPROVAL_PENDING', 'RETRY_SCHEDULED')).toBe(false)
    expect(isPttAvmFamilyRetryable(null, 'APPROVAL_PENDING', null)).toBe(false)
  })

  it('makes Yayınla send a PTT AVM family card to try again and still skip live or in-flight cards', () => {
    expect(isSkippedByPublish('PTTAVM', 'APPROVAL_PENDING', 'RETRY_SCHEDULED', true)).toBe(false)
    expect(isSkippedByPublish('PTTAVM', 'MANUAL_REVIEW', 'MANUAL_REVIEW', true)).toBe(false)
    expect(isSkippedByPublish('PTTAVM', 'APPROVAL_PENDING', 'RETRY_SCHEDULED', false)).toBe(true)
    expect(isSkippedByPublish('PTTAVM', 'APPROVAL_PENDING', 'LEASED', true)).toBe(true)
    expect(isSkippedByPublish('PTTAVM', 'LIVE', null, true)).toBe(true)
    expect(isSkippedByPublish('PTTAVM', 'QUEUED', 'PENDING', true)).toBe(true)
    expect(isSkippedByPublish('TRENDYOL', 'APPROVAL_PENDING', null, true)).toBe(true)
    expect(isSkippedByPublish('TRENDYOL', 'CREATE_REJECTED', 'FAILED', false)).toBe(false)
    expect(isSkippedByPublish('PTTAVM', 'UNKNOWN', null, true)).toBe(false)
  })
})

describe('approval wait job', () => {
  it('is an approval check only waiting for the marketplace', () => {
    expect(isApprovalWaitJob({ jobType: 'PRODUCT_APPROVAL_RECONCILE', status: 'RETRY_SCHEDULED', lastErrorCode: 'PRODUCT_APPROVAL_PENDING' })).toBe(true)
    expect(isApprovalWaitJob({ jobType: 'PRODUCT_APPROVAL_RECONCILE', status: 'PENDING', lastErrorCode: 'HEPSIBURADA_PRODUCT_APPROVAL_PENDING' })).toBe(true)
  })

  it('is not a send, a failed or stopped check, or a check with another reason', () => {
    expect(isApprovalWaitJob({ jobType: 'PRODUCT_CREATE', status: 'RETRY_SCHEDULED', lastErrorCode: 'PRODUCT_BATCH_PENDING' })).toBe(false)
    expect(isApprovalWaitJob({ jobType: 'PRODUCT_APPROVAL_RECONCILE', status: 'MANUAL_REVIEW', lastErrorCode: 'PRODUCT_APPROVAL_PENDING' })).toBe(false)
    expect(isApprovalWaitJob({ jobType: 'PRODUCT_APPROVAL_RECONCILE', status: 'RETRY_SCHEDULED', lastErrorCode: 'PRODUCT_WRITE_IN_PROGRESS' })).toBe(false)
    expect(isApprovalWaitJob(null)).toBe(false)
  })
})
