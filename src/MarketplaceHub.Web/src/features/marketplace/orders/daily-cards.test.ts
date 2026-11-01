import { describe, expect, it } from 'vitest'
import { dailyCancellationCounts, dayCardFilters, microTodayParams, pendingShipments } from './daily-cards'
import { tabDateFilters } from './order-date-filters'
import { istanbulDayBoundary } from '../../../shared/format-date'

const row = (cancelledOrders: number, partialOrders: number, unknownTimeOrders = 0) => ({ connectionId: 'c', cancelledOrders, partialOrders, cancelledUnits: 0, unknownTimeOrders, unknownTimeUnits: 0 })

describe('daily order cards', () => {
  it('counts the orders cancelled whole and in part today over every store, and nothing while unread', () => {
    expect(dailyCancellationCounts([row(2, 1), row(1, 0), row(0, 3, 4)])).toEqual({ whole: 3, partial: 4 })
    expect(dailyCancellationCounts([])).toEqual({ whole: 0, partial: 0 })
    expect(dailyCancellationCounts(undefined)).toBeUndefined()
  })

  it('asks the summary for the ALL tab alone under the order-date filter of the day', () => {
    const params = new URLSearchParams(microTodayParams('2026-11-02', istanbulDayBoundary))
    expect(params.get('tabs')).toBe('ALL')
    expect(params.get('dateFrom')).toBe('2026-11-02T00:00:00.000+03:00')
    expect(params.get('dateTo')).toBe('2026-11-02T23:59:59.999+03:00')
    expect([...params.keys()]).toEqual(['tabs', 'dateFrom', 'dateTo'])
  })

  it('opens the micro-export list of the day in every tab, and İptal or Kısmi İptal on the day of the cancellation', () => {
    expect(dayCardFilters('micro', '2026-11-02')).toEqual({ kind: 'MICRO', status: 'ALL', dateFrom: '2026-11-02', dateTo: '2026-11-02', cancelFrom: '', cancelTo: '' })
    expect(dayCardFilters('cancelled', '2026-11-02')).toEqual({ kind: 'ALL', status: 'CANCELLED', dateFrom: '', dateTo: '', cancelFrom: '2026-11-02', cancelTo: '2026-11-02' })
    expect(dayCardFilters('partial', '2026-11-02')).toMatchObject({ status: 'PARTIALLY_CANCELLED', cancelFrom: '2026-11-02', cancelTo: '2026-11-02' })
    // The resends not yet shipped and the orders to ship are about no day: both date filters are cleared.
    expect(dayCardFilters('resent', '2026-11-02')).toEqual({ kind: 'RESENT_UNSHIPPED', status: 'RESENT', dateFrom: '', dateTo: '', cancelFrom: '', cancelTo: '' })
    expect(dayCardFilters('ship', '2026-11-02')).toEqual({ kind: 'ALL', status: 'PENDING', dateFrom: '', dateTo: '', cancelFrom: '', cancelTo: '' })
  })

  it('sets exactly the date filter its tab offers, so the date filters keep them as they are', () => {
    for (const card of ['micro', 'cancelled', 'partial', 'resent', 'ship'] as const) {
      const filters = dayCardFilters(card, '2026-11-02')
      expect(tabDateFilters(filters.status, filters)).toEqual({ dateFrom: filters.dateFrom, dateTo: filters.dateTo, cancelFrom: filters.cancelFrom, cancelTo: filters.cancelTo })
    }
  })

  // Kargolanacak Ürün Adedi is the overview's Bekleyen Ürün Adedi: the bootstrap's pending products per store.
  it('reads the products to ship, their orders and a row per store from the overview bootstrap', () => {
    const bootstrap = {
      metrics: { lateOrders: 0, uninvoicedInvoices: 0, pendingOrders: 30, pendingByConnection: { ty: 29, pz: 1 }, pendingUnitsByConnection: { pz: 1, ty: 36, gone: 0 } },
      channels: [{ connectionId: 'ty', platformCode: 'TRENDYOL', displayName: 'Trendyol' }, { connectionId: 'pz', platformCode: 'PAZARAMA', displayName: 'Pazarama' }]
    }
    expect(pendingShipments(bootstrap)).toEqual({ units: 37, orders: 30, rows: [{ key: 'ty', code: 'TRENDYOL', name: 'Trendyol', units: 36 }, { key: 'pz', code: 'PAZARAMA', name: 'Pazarama', units: 1 }] })
    // A store that is no longer listed keeps its products and is named so; unread or older answers give nothing.
    expect(pendingShipments({ metrics: { lateOrders: 0, uninvoicedInvoices: 0, pendingByConnection: { x: 2 }, pendingUnitsByConnection: { x: 3 } } })?.rows).toEqual([{ key: 'x', code: '', name: 'Kaldırılmış mağaza', units: 3 }])
    expect(pendingShipments({ metrics: { lateOrders: 0, uninvoicedInvoices: 0 } })).toBeUndefined()
    expect(pendingShipments(undefined)).toBeUndefined()
  })
})
