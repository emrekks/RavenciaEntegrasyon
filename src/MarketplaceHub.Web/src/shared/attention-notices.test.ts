import { afterEach, describe, expect, it } from 'vitest'
import { appendNotification, clearNotificationHistory, markNotificationRead, readNotificationHistory, syncAttentionNotices, type AttentionNotice } from './notifications'
import { publicationStatusLabel, publicationStatusNote, publicationStatusTone } from '../features/catalog/publication-status'
import { publishStatusSteps } from '../features/catalog/product-editor/publish-status-steps'

const failing: AttentionNotice = {
  key: 'sync-cursor:c1:RETURNS', revision: '100', code: 'SYNC_CURSOR_FAILING', title: 'Senkronizasyon Art Arda Başarısız',
  message: 'İade okuma art arda 3 kez başarısız oldu (son hata: REMOTE_CONTRACT_INVALID).', connectionId: 'c1', platformCode: 'TRENDYOL',
  storeName: 'Ravencia', resourceType: 'RETURNS', failureCount: 3, lastAttemptAt: '2026-11-06T10:00:00Z', sinceAt: '2026-11-05T19:00:00Z', lastError: 'REMOTE_CONTRACT_INVALID', items: []
}
const expired: AttentionNotice = {
  key: 'approval-expired:c2', revision: 'p1,p2', code: 'PRODUCT_APPROVAL_DEADLINE_EXPIRED', title: 'Pazarama Onayı Sonuçlanmadı',
  message: "2 ürünün Pazarama onayı kontrol süresi içinde sonuçlanmadı; onaylandığı ya da reddedildiği bilinmiyor.", connectionId: 'c2', platformCode: 'PAZARAMA',
  storeName: 'Pazarama Mağaza', resourceType: 'PRODUCT_APPROVAL', failureCount: 2, lastAttemptAt: '2026-11-06T16:39:00Z', sinceAt: null, lastError: 'PRODUCT_APPROVAL_DEADLINE_EXPIRED',
  items: [{ productId: 'p1', label: 'Elbise', barcodes: ['MZ0301'] }, { productId: 'p2', label: 'Gömlek', barcodes: ['MZ0302', 'MZ0303'] }]
}

const attention = () => readNotificationHistory().filter(item => item.groupKey?.startsWith('attention:'))

describe('background notices in the notification center', () => {
  afterEach(() => localStorage.clear())

  it('raises one entry per notice naming the products, failure count and last attempt', () => {
    expect(syncAttentionNotices([failing, expired])).toBe(true)
    const entries = attention()
    expect(entries).toHaveLength(2)
    const approval = entries.find(item => item.code === 'PRODUCT_APPROVAL_DEADLINE_EXPIRED')!
    expect(approval.kind).toBe('error')
    expect(approval.read).toBe(false)
    expect(approval.items?.map(item => item.label)).toEqual(['Elbise · MZ0301', 'Gömlek · MZ0302, MZ0303'])
    expect(approval.details?.find(detail => detail.label === 'Süresi Dolan Kontrol')?.value).toBe('2')
    expect(approval.details?.some(detail => detail.label === 'Son Deneme')).toBe(true)
    const cursor = entries.find(item => item.code === 'SYNC_CURSOR_FAILING')!
    expect(cursor.details?.find(detail => detail.label === 'Art Arda Hata')?.value).toBe('3')
    expect(cursor.href).toBe('/integrations/c1')
  })

  it('updates the same streak in place, raises a new revision again and clears a notice that is no longer listed', () => {
    syncAttentionNotices([failing])
    const first = attention()[0]
    // Read, then the streak grows: one entry, still read, with the new count.
    markNotificationRead(first.id)
    syncAttentionNotices([{ ...failing, failureCount: 5, message: 'İade okuma art arda 5 kez başarısız oldu.' }])
    let entries = attention()
    expect(entries).toHaveLength(1)
    expect(entries[0].id).toBe(first.id)
    expect(entries[0].read).toBe(true)
    expect(entries[0].details?.[0].value).toBe('5')
    // Nothing changed: nothing written.
    expect(syncAttentionNotices([{ ...failing, failureCount: 5, message: 'İade okuma art arda 5 kez başarısız oldu.' }])).toBe(false)
    // The read succeeded: the notice is gone.
    syncAttentionNotices([])
    expect(attention()).toHaveLength(0)
    // A new streak after the success is raised again, unread.
    syncAttentionNotices([{ ...failing, revision: '200' }])
    entries = attention()
    expect(entries).toHaveLength(1)
    expect(entries[0].read).toBe(false)
  })

  it('does not bring back a notice the person cleared until its revision changes, and leaves other notifications alone', () => {
    appendNotification({ title: 'Ürün Oluşturuldu', message: 'Ürün kataloğa eklendi.', kind: 'success', record: true })
    syncAttentionNotices([expired])
    clearNotificationHistory()
    expect(syncAttentionNotices([expired])).toBe(false)
    expect(attention()).toHaveLength(0)
    syncAttentionNotices([{ ...expired, revision: 'p1,p2,p3' }])
    expect(attention()).toHaveLength(1)
  })
})

describe('a publication card whose approval check ran out of time', () => {
  it('says the outcome is unknown in the warning tone, never refused', () => {
    expect(publicationStatusLabel('MANUAL_REVIEW', 'MANUAL_REVIEW', 'PAZARAMA', 'PRODUCT_APPROVAL_DEADLINE_EXPIRED')).toBe('Onay Sonucu Bilinmiyor')
    expect(publicationStatusTone('MANUAL_REVIEW', 'MANUAL_REVIEW', 'PRODUCT_APPROVAL_DEADLINE_EXPIRED')).toBe('warning')
    expect(publicationStatusNote('PRODUCT_APPROVAL_DEADLINE_EXPIRED', 'PAZARAMA')).toContain('onaylandığı ya da reddedildiği bilinmiyor')
    const steps = publishStatusSteps({ requiredGaps: 0, optionalWarnings: 0, live: false, fallbackLabel: '', actualStatus: 'MANUAL_REVIEW', rejectionCode: 'PRODUCT_APPROVAL_DEADLINE_EXPIRED' })
    expect(steps.label).toBe('Onay Sonucu Bilinmiyor')
    expect(steps.steps).toEqual(['done', 'done', 'fail', 'idle'])
    // Another review keeps its own reading.
    expect(publicationStatusTone('MANUAL_REVIEW', 'MANUAL_REVIEW', 'PRODUCT_APPROVAL_STATE_INCOMPLETE')).toBe('danger')
  })
})
