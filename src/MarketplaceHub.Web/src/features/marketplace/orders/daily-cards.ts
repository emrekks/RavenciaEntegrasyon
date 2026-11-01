// The micro-export and cancellation cards above the order list show today (the Istanbul day), whatever the list's own
// filters are. Each opens the list on exactly what it counted.

// GET /dashboard/cancellations: one store's cancellations of the day. The Genel Bakış card reads the same rows, so both
// count an order cancelled whole, and one cancelled in part, the same way (a cancellation of unknown time is not counted).
export type DailyCancellationRow = { connectionId: string; cancelledOrders: number; partialOrders: number; cancelledUnits: number; unknownTimeOrders: number; unknownTimeUnits: number }

// Orders cancelled whole and orders cancelled in part today, over every store; undefined while the rows are not read.
export function dailyCancellationCounts(stores: DailyCancellationRow[] | undefined) {
  if (!stores) return undefined
  return { whole: stores.reduce((sum, row) => sum + row.cancelledOrders, 0), partial: stores.reduce((sum, row) => sum + row.partialOrders, 0) }
}

// The micro-export card's request: the order summary's ALL tab alone (which carries the micro-export count and its
// countries), under the order-date filter of the day and nothing else.
export function microTodayParams(day: string, boundary: (day: string, endOfDay?: boolean) => string) {
  return new URLSearchParams({ tabs: 'ALL', dateFrom: boundary(day), dateTo: boundary(day, true) }).toString()
}

export type DayCard = 'micro' | 'cancelled' | 'partial'

// The list a card opens: the micro-export list of the day's orders in every tab, or İptal / Kısmi İptal on the orders
// cancelled that day. Only the date filter the tab offers is set (order-date-filters.ts), the other is cleared.
export function dayCardFilters(card: DayCard, day: string) {
  return card === 'micro'
    ? { micro: 'MICRO', status: 'ALL', dateFrom: day, dateTo: day, cancelFrom: '', cancelTo: '' }
    : { micro: 'ALL', status: card === 'cancelled' ? 'CANCELLED' : 'PARTIALLY_CANCELLED', dateFrom: '', dateTo: '', cancelFrom: day, cancelTo: day }
}
