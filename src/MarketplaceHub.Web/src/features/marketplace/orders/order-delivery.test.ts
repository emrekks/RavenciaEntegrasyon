import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Order } from '../shared'
import { deliveryDueLine, deliveryText, dueLineTitle, orderRingDetail, undeliveredReturn } from './order-delivery'

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
    expect(deliveryDueLine(delivery)).toEqual({ ...ring, text: ring.text.replace(/ kaldı$/u, '') })
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

  it('says a returned order was returned instead of counting days late past its old deadline', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-11-17T09:00:00Z'))
    const returned = deliveryText(order({ derivedStatus: 'RETURNED', shipmentDueAt: '2026-11-11T20:59:00Z' }))
    expect(returned.kind).toBe('status')
    expect(returned.value).toBe('İade Edildi')
    expect(returned.overdue).toBe(false)
    expect(deliveryDueLine(returned)).toBeNull()
    expect(deliveryText(order({ derivedStatus: 'RETURN_IN_TRANSIT', shipmentDueAt: '2026-11-11T20:59:00Z' })).value).toBe('İade Yolda')
    vi.useRealTimers()
  })

  it('tells a parcel that could not be delivered and came back from a customer return', () => {
    const cameBack = order({ derivedStatus: 'RETURNED', lines: [{ rawStatus: 'UnDeliveredAndReturned' }] as Order['lines'] })
    expect(undeliveredReturn(cameBack)).toBe(true)
    expect(deliveryText(cameBack).value).toBe('İade Döndü')
    const customerReturn = order({ derivedStatus: 'RETURNED', lines: [{ rawStatus: 'Returned' }] as Order['lines'] })
    expect(undeliveredReturn(customerReturn)).toBe(false)
    expect(deliveryText(customerReturn).value).toBe('İade Edildi')
    expect(undeliveredReturn(order({ derivedStatus: 'SHIPPED', lines: [{ rawStatus: 'UnDeliveredAndReturned' }] as Order['lines'] }))).toBe(false)
  })

  it('counts the days an overdue order is late', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-11-05T09:00:00Z'))
    const delivery = deliveryText(order({ shipmentDueAt: '2026-11-02T08:00:00Z' }))
    expect(orderRingDetail(delivery)).toEqual({ text: '3 gün gecikti', cls: 'bad' })
    expect(deliveryDueLine(delivery)).not.toBeNull()
    expect(dueLineTitle(delivery)).toMatch(/^Geciken/u)
  })

  it('names a red line with time left "Süre Doluyor", never "Geciken"', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-11-01T09:00:00Z'))
    expect(dueLineTitle(deliveryText(order({ shipmentDueAt: '2026-11-01T15:00:00Z' })))).toMatch(/^Süre Doluyor/u)
    expect(dueLineTitle(deliveryText(order({ shipmentDueAt: '2026-11-03T09:00:00Z' })))).toBeUndefined()
  })
})
