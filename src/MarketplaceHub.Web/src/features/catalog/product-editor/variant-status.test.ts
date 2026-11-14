import { describe, expect, it } from 'vitest'
import { variantStatus } from './variant-status'

const trendyol = { platform: 'Trendyol', platformCode: 'TRENDYOL', isLinked: true }
const ok = { salePrice: 100, listPrice: 120 }

describe('variantStatus', () => {
  it('is red with the marketplace reason for a refused variant', () => {
    const status = variantStatus({ ...ok, platformStatuses: [{ ...trendyol, listingStatus: 'REJECTED', rejectionReason: 'Hatalı Görsel Linki' }] })
    expect(status.tone).toBe('error')
    expect(status.lines).toEqual([{ tone: 'error', text: 'Trendyol: Reddedildi · Hatalı Görsel Linki' }])
  })

  it('names a refusal without a reason plainly', () => {
    expect(variantStatus({ ...ok, platformStatuses: [{ ...trendyol, listingStatus: 'REJECTED' }] }).lines[0].text).toBe('Trendyol: Reddedildi')
  })

  it('is red for a variant the marketplace no longer knows', () => {
    expect(variantStatus({ ...ok, platformStatuses: [{ ...trendyol, isMissing: true }] }).tone).toBe('error')
  })

  it('is red for field problems, listed first', () => {
    const status = variantStatus({ barcodeIssue: 'Barkod eksik.', salePrice: 0, listPrice: 50, platformStatuses: [{ ...trendyol, isMatched: true }] })
    expect(status.tone).toBe('error')
    expect(status.lines.map(line => line.text)).toEqual(['Barkod eksik.', 'Satış fiyatı 0\'dan büyük olmalı.', 'Trendyol: Eşleşti'])
  })

  it('flags a list price under the sale price', () => {
    expect(variantStatus({ salePrice: 100, listPrice: 90 }).lines[0].text).toBe('Liste fiyatı satış fiyatından küçük olamaz.')
  })

  it('is green only when a marketplace has the variant and nothing is wrong', () => {
    expect(variantStatus({ ...ok, platformStatuses: [{ ...trendyol, isMatched: true }] }).tone).toBe('ok')
    expect(variantStatus({ ...ok, platformStatuses: [{ ...trendyol, isLinked: false, listingStatus: 'LIVE' }] }).tone).toBe('ok')
  })

  it('stays grey when nothing is known, never green from a missing state', () => {
    const none = variantStatus({ ...ok, platformStatuses: [{ ...trendyol, isLinked: false, listingStatus: 'UNKNOWN' }] })
    expect(none.tone).toBe('unknown')
    expect(none.lines).toEqual([{ tone: 'info', text: 'Henüz pazaryeri bilgisi yok.' }])
    expect(variantStatus({ ...ok }).tone).toBe('unknown')
  })

  // Owner, 15 Nov 2026: the light agrees with the list mark; blue while the marketplace's word is awaited.
  it('is blue while approval is pending, a refused variant was sent again or its card is being updated', () => {
    expect(variantStatus({ ...ok, platformStatuses: [{ ...trendyol, listingStatus: 'APPROVAL_PENDING' }] })).toEqual({ tone: 'pending', lines: [{ tone: 'pending', text: 'Trendyol: Onay bekliyor' }] })
    const resent = variantStatus({ ...ok, platformStatuses: [{ ...trendyol, status: 'UPDATE_UNAPPROVED_SUBMITTED', listingStatus: 'REJECTED', rejectionReason: 'Hatalı Görsel Linki' }] })
    expect(resent).toEqual({ tone: 'pending', lines: [{ tone: 'pending', text: 'Trendyol: Onaya gönderildi (yeniden), sonuç bekleniyor · Önceki ret · Hatalı Görsel Linki' }] })
    expect(variantStatus({ ...ok, platformStatuses: [{ ...trendyol, status: 'UPDATE_IN_PROGRESS', isMatched: true, listingStatus: 'LIVE' }] }).lines).toEqual([{ tone: 'pending', text: 'Trendyol: Yayında · güncelleniyor' }])
    expect(variantStatus({ ...ok, platformStatuses: [{ ...trendyol, status: 'REJECTED', listingStatus: 'REJECTED' }] }).tone).toBe('error')
  })
})
