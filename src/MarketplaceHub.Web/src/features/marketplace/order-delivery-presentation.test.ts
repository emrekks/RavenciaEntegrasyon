import { describe, expect, it } from 'vitest'
import { isHepsiburadaClaimOnlyWithoutPackage, onHoldOrderStatusText, overdueShipmentDays } from './order-delivery-presentation'

describe('onHoldOrderStatusText', () => {
  it('identifies an explicit Hepsiburada undelivered package', () => {
    expect(onHoldOrderStatusText('HEPSIBURADA', ['Undelivered'])).toBe('Teslim edilemedi')
  })

  it('describes ClaimCreated as a claim, not a failed delivery', () => {
    expect(onHoldOrderStatusText('HEPSIBURADA', ['ClaimCreated'])).toBe('Talep açıldı')
  })

  it('keeps an unknown Hepsiburada hold state neutral', () => {
    expect(onHoldOrderStatusText('HEPSIBURADA')).toBe('Askıda')
  })

  it('keeps the standard hold label for other marketplaces', () => {
    expect(onHoldOrderStatusText('TRENDYOL')).toBe('Askıda')
  })
})

describe('overdueShipmentDays', () => {
  const now = Date.parse('2026-10-01T12:00:00Z')

  it('does not mark an on-hold order overdue even when its old deadline has passed', () => {
    expect(overdueShipmentDays('ON_HOLD', '2026-05-19T22:56:00Z', now)).toBe(0)
  })

  it('continues to calculate overdue days for an order awaiting shipment', () => {
    expect(overdueShipmentDays('NEW', '2026-09-29T12:00:00Z', now)).toBe(2)
  })
})

describe('isHepsiburadaClaimOnlyWithoutPackage', () => {
  it('recognizes claim-only rows with no locally known shipment package', () => {
    expect(isHepsiburadaClaimOnlyWithoutPackage('HEPSIBURADA', ['ClaimCreated'], 0)).toBe(true)
    expect(isHepsiburadaClaimOnlyWithoutPackage('hepsiburada', [' claim_created '], 0)).toBe(true)
  })

  it('does not hide shipment deadlines when a package or an active order line exists', () => {
    expect(isHepsiburadaClaimOnlyWithoutPackage('HEPSIBURADA', ['ClaimCreated'], 1)).toBe(false)
    expect(isHepsiburadaClaimOnlyWithoutPackage('HEPSIBURADA', ['ClaimCreated', 'Open'], 0)).toBe(false)
    expect(isHepsiburadaClaimOnlyWithoutPackage('TRENDYOL', ['ClaimCreated'], 0)).toBe(false)
  })
})
