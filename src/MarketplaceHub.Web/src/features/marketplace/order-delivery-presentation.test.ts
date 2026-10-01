import { describe, expect, it } from 'vitest'
import { onHoldOrderStatusText, overdueShipmentDays } from './order-delivery-presentation'

describe('onHoldOrderStatusText', () => {
  it('explains Hepsiburada hold orders as undelivered', () => {
    expect(onHoldOrderStatusText('HEPSIBURADA')).toBe('Teslim edilemedi')
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
