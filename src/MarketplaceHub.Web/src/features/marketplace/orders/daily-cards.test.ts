import { describe, expect, it } from 'vitest'
import { cancellationStores, dailyCancellationCounts, dayCardFilters, microPendingParams, resentPendingParams, storeCounts, storeUnits } from './daily-cards'
import { tabDateFilters } from './order-date-filters'

const row = (cancelledOrders: number, partialOrders: number, unknownTimeOrders = 0, wholeUnits?: number, partialUnits?: number) => ({ connectionId: 'c', cancelledOrders, partialOrders, cancelledUnits: 0, unknownTimeOrders, unknownTimeUnits: 0, wholeUnits, partialUnits })

describe('daily order cards', () => {
  it('counts the orders cancelled whole and in part today over every store with their units, and nothing while unread', () => {
    expect(dailyCancellationCounts([row(2, 1, 0, 3, 1), row(1, 0, 0, 2, 0), row(0, 3, 4, 0, 5)])).toEqual({ whole: 3, partial: 4, wholeUnits: 5, partialUnits: 6 })
    expect(dailyCancellationCounts([])).toEqual({ whole: 0, partial: 0, wholeUnits: 0, partialUnits: 0 })
    // An older API without the units: the counts stand, the units stay unknown rather than 0.
    expect(dailyCancellationCounts([row(2, 1)])).toEqual({ whole: 2, partial: 1, wholeUnits: undefined, partialUnits: undefined })
    expect(dailyCancellationCounts(undefined)).toBeUndefined()
  })

  it('asks the summary for the PENDING tab alone over the micro-export orders, with no date', () => {
    const params = new URLSearchParams(microPendingParams())
    expect(params.get('tabs')).toBe('PENDING')
    expect(params.get('micro')).toBe('true')
    expect([...params.keys()]).toEqual(['tabs', 'micro'])
  })

  it('opens the pending micro-export orders, and İptal or Kısmi İptal on the day of the cancellation', () => {
    // The same set the card counts: the micro-export orders in the pending tab, any day.
    expect(dayCardFilters('micro', '2026-11-02')).toEqual({ kind: 'MICRO', status: 'PENDING', dateFrom: '', dateTo: '', cancelFrom: '', cancelTo: '' })
    expect(dayCardFilters('cancelled', '2026-11-02')).toEqual({ kind: 'ALL', status: 'CANCELLED', dateFrom: '', dateTo: '', cancelFrom: '2026-11-02', cancelTo: '2026-11-02' })
    expect(dayCardFilters('partial', '2026-11-02')).toMatchObject({ status: 'PARTIALLY_CANCELLED', cancelFrom: '2026-11-02', cancelTo: '2026-11-02' })
    // The resends not yet shipped are about no day: both date filters are cleared.
    expect(dayCardFilters('resent', '2026-11-02')).toEqual({ kind: 'RESENT_UNSHIPPED', status: 'RESENT', dateFrom: '', dateTo: '', cancelFrom: '', cancelTo: '' })
  })

  it('sets exactly the date filter its tab offers, so the date filters keep them as they are', () => {
    for (const card of ['micro', 'cancelled', 'partial', 'resent'] as const) {
      const filters = dayCardFilters(card, '2026-11-02')
      expect(tabDateFilters(filters.status, filters)).toEqual({ dateFrom: filters.dateFrom, dateTo: filters.dateTo, cancelFrom: filters.cancelFrom, cancelTo: filters.cancelTo })
    }
  })

  // The Yeni Sipariş card's products: the summary's units per store, named by the bootstrap's stores.
  it('sums the new orders\' products and lists a row per store, the biggest first', () => {
    const channels = [{ connectionId: 'ty', platformCode: 'TRENDYOL', displayName: 'Trendyol' }, { connectionId: 'pz', platformCode: 'PAZARAMA', displayName: 'Pazarama' }]
    expect(storeUnits({ pz: 1, ty: 36, gone: 0 }, channels)).toEqual({ units: 37, rows: [{ key: 'ty', code: 'TRENDYOL', name: 'Trendyol', units: 36 }, { key: 'pz', code: 'PAZARAMA', name: 'Pazarama', units: 1 }] })
    // A store that is no longer listed keeps its products and is named so; unread or older answers give nothing.
    expect(storeUnits({ x: 3 }, undefined)?.rows).toEqual([{ key: 'x', code: '', name: 'Kaldırılmış mağaza', units: 3 }])
    expect(storeUnits({}, channels)).toEqual({ units: 0, rows: [] })
    expect(storeUnits(null, channels)).toBeUndefined()
    expect(storeUnits(undefined, channels)).toBeUndefined()
  })

  it('asks the summary for the RESENT tab alone over the resends not handed to the carrier', () => {
    expect(resentPendingParams()).toBe('tabs=RESENT&resentNotShipped=true')
  })

  // The Geciken, Yeniden Gönderim and İptal cards' hover: their stores, the biggest first.
  it('lists the stores of the late orders, the resends and today\'s cancellations', () => {
    const channels = [{ connectionId: 'ty', platformCode: 'TRENDYOL', displayName: 'Trendyol' }, { connectionId: 'hb', platformCode: 'HEPSIBURADA', displayName: 'Hepsiburada' }]
    expect(storeCounts({ hb: 1, ty: 3, gone: 0 }, channels)).toEqual([{ key: 'ty', code: 'TRENDYOL', name: 'Trendyol', packages: 3 }, { key: 'hb', code: 'HEPSIBURADA', name: 'Hepsiburada', packages: 1 }])
    expect(storeCounts({}, channels)).toEqual([])
    expect(storeCounts(null, channels)).toBeUndefined()
    const cancellations = [
      { connectionId: 'hb', cancelledOrders: 0, partialOrders: 1, cancelledUnits: 2, unknownTimeOrders: 0, unknownTimeUnits: 0, wholeUnits: 0, partialUnits: 2 },
      { connectionId: 'ty', cancelledOrders: 2, partialOrders: 0, cancelledUnits: 3, unknownTimeOrders: 0, unknownTimeUnits: 0, wholeUnits: 3, partialUnits: 0 },
      { connectionId: 'x', cancelledOrders: 0, partialOrders: 0, cancelledUnits: 0, unknownTimeOrders: 1, unknownTimeUnits: 1, wholeUnits: 0, partialUnits: 0 }
    ]
    expect(cancellationStores(cancellations, channels)).toEqual([
      { key: 'ty', code: 'TRENDYOL', name: 'Trendyol', packages: 2, units: 3, partial: 0, partialUnits: 0 },
      { key: 'hb', code: 'HEPSIBURADA', name: 'Hepsiburada', packages: 0, units: 0, partial: 1, partialUnits: 2 }
    ])
    expect(cancellationStores(undefined, channels)).toBeUndefined()
  })
})
