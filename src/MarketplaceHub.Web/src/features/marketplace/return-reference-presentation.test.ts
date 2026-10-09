import { describe, expect, it } from 'vitest'
import { formatMarketplaceBarcode, returnReasonPresentation, shouldShowReturnCountdown, sortApprovedReturns } from './return-reference-presentation'

describe('return reference presentation', () => {
  it('removes Hepsiburada zero prefixes from alphanumeric codes but preserves numeric barcodes', () => {
    expect(formatMarketplaceBarcode('HEPSIBURADA', '000MZ040RCY15')).toBe('MZ040RCY15')
    expect(formatMarketplaceBarcode('HEPSIBURADA', '0123456789012')).toBe('0123456789012')
    expect(formatMarketplaceBarcode('TRENDYOL', '000MZ040RCY15')).toBe('000MZ040RCY15')
    expect(formatMarketplaceBarcode('HEPSIBURADA', '  ')).toBeNull()
  })

  it('sorts approved returns by order date or approval date in both directions', () => {
    const returns = [
      { id: 'b', orderedAt: '2026-10-02T00:00:00Z', approvedAt: '2026-10-04T00:00:00Z' },
      { id: 'a', orderedAt: '2026-10-03T00:00:00Z', approvedAt: '2026-10-05T00:00:00Z' },
      { id: 'c', orderedAt: '2026-10-01T00:00:00Z', approvedAt: null },
    ]
    expect(sortApprovedReturns(returns, 'ORDERED_DESC').map(item => item.id)).toEqual(['a', 'b', 'c'])
    expect(sortApprovedReturns(returns, 'ORDERED_ASC').map(item => item.id)).toEqual(['c', 'b', 'a'])
    expect(sortApprovedReturns(returns, 'APPROVED_DESC').map(item => item.id)).toEqual(['a', 'b', 'c'])
    expect(sortApprovedReturns(returns, 'APPROVED_ASC').map(item => item.id)).toEqual(['b', 'a', 'c'])
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
})
