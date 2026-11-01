import { describe, expect, it } from 'vitest'
import { dailyCancellationCounts, dayCardFilters, microTodayParams } from './daily-cards'
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
    expect(dayCardFilters('micro', '2026-11-02')).toEqual({ micro: 'MICRO', status: 'ALL', dateFrom: '2026-11-02', dateTo: '2026-11-02', cancelFrom: '', cancelTo: '' })
    expect(dayCardFilters('cancelled', '2026-11-02')).toEqual({ micro: 'ALL', status: 'CANCELLED', dateFrom: '', dateTo: '', cancelFrom: '2026-11-02', cancelTo: '2026-11-02' })
    expect(dayCardFilters('partial', '2026-11-02')).toMatchObject({ status: 'PARTIALLY_CANCELLED', cancelFrom: '2026-11-02', cancelTo: '2026-11-02' })
  })

  it('sets exactly the date filter its tab offers, so the date filters keep them as they are', () => {
    for (const card of ['micro', 'cancelled', 'partial'] as const) {
      const filters = dayCardFilters(card, '2026-11-02')
      expect(tabDateFilters(filters.status, filters)).toEqual({ dateFrom: filters.dateFrom, dateTo: filters.dateTo, cancelFrom: filters.cancelFrom, cancelTo: filters.cancelTo })
    }
  })
})
