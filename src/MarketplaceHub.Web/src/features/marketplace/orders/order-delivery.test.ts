import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Order } from '../shared'
import { deliveryDueLine, deliveryText, orderRingDetail } from './order-delivery'

const order = (patch: Partial<Order>): Order => ({ id: 'o1', orderNumber: '1', derivedStatus: 'NEW', platformCode: 'TRENDYOL', isMicroExport: false, shipmentDueAt: null, packages: [], lines: [], ...patch }) as Order

afterEach(() => { vi.useRealTimers() })

describe('order delivery line', () => {
  it('shows the time left with "kaldı" and keeps the due line for an order still to be shipped', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-11-01T09:00:00Z'))
    const delivery = deliveryText(order({ shipmentDueAt: '2026-11-03T09:00:00Z' }))
    expect(delivery.kind).toBe('remaining')
    const ring = orderRingDetail(delivery)
    expect(ring.text).toMatch(/kaldı$/u)
    expect(ring.cls).toBeUndefined()
    expect(deliveryDueLine(delivery)).toEqual(ring)
  })

  it('marks the remaining time red inside the last day', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-11-01T09:00:00Z'))
    const ring = orderRingDetail(deliveryText(order({ shipmentDueAt: '2026-11-01T15:00:00Z' })))
    expect(ring.cls).toBe('crit')
  })

  it('says the shipped order was handed to the carrier', () => {
    const delivery = deliveryText(order({ derivedStatus: 'SHIPPED' }))
    expect(delivery.kind).toBe('shipping')
    expect(orderRingDetail(delivery).text).toBe('Kargoya teslim edildi')
    expect(deliveryDueLine(delivery)).toBeNull()
  })

  it('shows an undelivered Hepsiburada order as on every other marketplace', () => {
    const hepsiburada = deliveryText(order({ derivedStatus: 'UNDELIVERED', platformCode: 'HEPSIBURADA', packages: [{ rawStatus: 'Undelivered' }] as Order['packages'] }))
    const trendyol = deliveryText(order({ derivedStatus: 'UNDELIVERED', platformCode: 'TRENDYOL' }))
    expect(hepsiburada).toEqual(trendyol)
    expect(hepsiburada.kind).toBe('shipping')
  })

  it('counts the days an overdue order is late', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-11-05T09:00:00Z'))
    const delivery = deliveryText(order({ shipmentDueAt: '2026-11-02T08:00:00Z' }))
    expect(orderRingDetail(delivery)).toEqual({ text: '3 gün gecikti', cls: 'bad' })
    expect(deliveryDueLine(delivery)).not.toBeNull()
  })
})
