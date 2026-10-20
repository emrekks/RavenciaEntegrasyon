import { describe, expect, it } from 'vitest'
import { isLinkRevocationStatus, linkRevocationActionLabel, linkRevocationConfirmCopy, linkRevocationNote, linkRevocationStatusLabel, linkRevocationTone, type InvoiceLinkRevocation } from './invoice-link-revocation'

const item: InvoiceLinkRevocation = { deliveryStateId: 's', packageId: 'p', platformCode: 'TRENDYOL', platformName: 'Trendyol', packageNumber: '4052072376', status: 'REVOCATION_REQUIRED', errorCode: null, mode: 'AUTOMATIC', instructions: 'x', canAct: true, blockedReason: null, updatedAt: '2026-10-20T10:00:00Z' }

describe('invoice link revocation copy', () => {
  it('names each state in plain Turkish', () => {
    expect(linkRevocationStatusLabel('REVOCATION_REQUIRED')).toBe('Kaldırılmalı')
    expect(linkRevocationStatusLabel('REVOCATION_UNKNOWN')).toBe('Sonuç Bekleniyor')
    expect(linkRevocationStatusLabel('LINK_REMOVED')).toBe('Kaldırıldı')
    expect(isLinkRevocationStatus('REVOCATION_STARTED')).toBe(true)
    expect(isLinkRevocationStatus('CONFIRMED')).toBe(false)
    expect(linkRevocationTone('LINK_REMOVED')).toBe('success')
    expect(linkRevocationTone('REVOCATION_REQUIRED')).toBe('danger')
  })

  it('offers removal for Trendyol, a check for Hepsiburada and nothing for the rest or a finished state', () => {
    expect(linkRevocationActionLabel(item)).toBe('Fatura Linkini Kaldır')
    expect(linkRevocationActionLabel({ ...item, mode: 'VERIFY_ONLY' })).toBe('Kaldırıldığını Kontrol Et')
    expect(linkRevocationActionLabel({ ...item, mode: 'MANUAL' })).toBeNull()
    expect(linkRevocationActionLabel({ ...item, status: 'LINK_REMOVED' })).toBeNull()
  })

  it('warns that removal cannot be undone and that a check sends nothing', () => {
    const removal = linkRevocationConfirmCopy(item, 'GIB2026000000001')
    expect(removal.destructive).toBe(true)
    expect(removal.message).toContain('GIB2026000000001')
    expect(removal.message).toContain('geri alınamaz')
    const check = linkRevocationConfirmCopy({ ...item, mode: 'VERIFY_ONLY', platformName: 'Hepsiburada' }, null)
    expect(check.destructive).toBe(false)
    expect(check.message).toContain('hiçbir şey gönderilmez')
  })

  it('explains an unfinished attempt', () => {
    expect(linkRevocationNote({ status: 'REVOCATION_UNKNOWN' })).toContain('önce paketi okur')
    expect(linkRevocationNote({ status: 'REVOCATION_REQUIRED' })).toBeNull()
  })
})
