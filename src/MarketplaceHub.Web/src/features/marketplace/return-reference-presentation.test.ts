import { describe, expect, it } from 'vitest'
import { formatMarketplaceBarcode, isSellerFaultReturn, returnCustomerNote, returnReasonPresentation, shouldShowReturnCountdown } from './return-reference-presentation'

describe('return reference presentation', () => {
  it('removes Hepsiburada zero prefixes from alphanumeric codes but preserves numeric barcodes', () => {
    expect(formatMarketplaceBarcode('HEPSIBURADA', '000MZ040RCY15')).toBe('MZ040RCY15')
    expect(formatMarketplaceBarcode('HEPSIBURADA', '0123456789012')).toBe('0123456789012')
    expect(formatMarketplaceBarcode('TRENDYOL', '000MZ040RCY15')).toBe('000MZ040RCY15')
    expect(formatMarketplaceBarcode('HEPSIBURADA', '  ')).toBeNull()
  })

  it('shows an active countdown only for action-required returns with a future deadline', () => {
    const now = Date.parse('2026-10-06T10:00:00Z')
    expect(shouldShowReturnCountdown(true, '2026-10-07T10:00:00Z', now)).toBe(true)
    expect(shouldShowReturnCountdown(false, '2026-10-07T10:00:00Z', now)).toBe(false)
    expect(shouldShowReturnCountdown(true, '2026-10-05T10:00:00Z', now)).toBe(false)
    expect(shouldShowReturnCountdown(true, 'invalid', now)).toBe(false)
  })

  it('shows the actual reason text instead of a generic Other label', () => {
    expect(returnReasonPresentation({ reasonCode: 'OTHER', reasonText: 'Beden/Ebat Büyük Geldi' }))
      .toEqual({ label: 'Beden/Ebat Büyük Geldi', explanation: null })
    expect(returnReasonPresentation({ reasonCode: 'UNKNOWN_REASON', reasonText: 'Yanlış sipariş verdim' }))
      .toEqual({ label: 'Yanlış sipariş verdim', explanation: null })
  })

  it('keeps a known return reason and shows a distinct customer explanation', () => {
    expect(returnReasonPresentation({ reasonCode: 'SIZE_TOO_LARGE', reasonText: 'Ürün beklediğimden bol oldu' }))
      .toEqual({ label: 'Bedeni / boyutu büyük geldi', explanation: 'Ürün beklediğimden bol oldu' })
  })

  it('shows the readable Hepsiburada reason title in bold with the customer words as the explanation', () => {
    expect(returnReasonPresentation({ reasonCode: 'Ürünü almaktan vazgeçtim / beğenmedim', reasonText: 'Eşime almıştım sürpriz yapacaktım' }))
      .toEqual({ label: 'Ürünü almaktan vazgeçtim / beğenmedim', explanation: 'Eşime almıştım sürpriz yapacaktım' })
    expect(returnReasonPresentation({ reasonCode: 'Bedeni / boyutu uymadı', reasonText: 'Bedeni / boyutu uymadı' }))
      .toEqual({ label: 'Bedeni / boyutu uymadı', explanation: null })
  })

  it('names a Trendyol reason by its code and keeps the customer note apart', () => {
    const reason = returnReasonPresentation({ reasonCode: 'WRONGITEM', reasonText: '3xl sipariş etmiştim 2xl geldi' })
    expect(reason.label).toBe('Yanlış ürün gönderildi')
    expect(returnCustomerNote(reason.label, '3xl sipariş etmiştim 2xl geldi')).toBe('3xl sipariş etmiştim 2xl geldi')
    const plain = returnReasonPresentation({ reasonCode: 'SMALLSIZE', reasonText: 'Bedeni/Ebatı Küçük Geldi' })
    expect(plain.label).toBe('Bedeni/Ebatı Küçük Geldi')
    expect(returnCustomerNote(plain.label, 'Bedeni/Ebatı Küçük Geldi')).toBeNull()
  })

  it('marks the reasons the seller is to blame for', () => {
    expect(isSellerFaultReturn({ reasonCode: 'WRONGITEM' }, 'Yanlış ürün gönderildi')).toBe(true)
    expect(isSellerFaultReturn({ reasonCode: 'DAMAGEDITEM' }, '')).toBe(true)
    expect(isSellerFaultReturn({ reasonCode: 'Siparişimden farklı ürün gönderildi' }, 'Siparişimden farklı ürün gönderildi')).toBe(true)
    expect(isSellerFaultReturn({ reasonCode: 'Hatalı Ürün' }, 'Hatalı Ürün')).toBe(true)
    expect(isSellerFaultReturn({ reasonCode: 'SMALLSIZE' }, 'Bedeni/Ebatı Küçük Geldi')).toBe(false)
    expect(isSellerFaultReturn({ reasonCode: 'WRONGORDER' }, 'Yanlış sipariş verdim')).toBe(false)
  })
})
