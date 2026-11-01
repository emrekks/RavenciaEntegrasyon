// The cancellation cards above the order list show today (the Istanbul day), and the micro-export card the pending
// micro-export orders, whatever the list's own filters are. Each opens the list on exactly what it counted.

// GET /dashboard/cancellations: one store's cancellations of the day. The Genel Bakış card reads the same rows, so both
// count an order cancelled whole, and one cancelled in part, the same way (a cancellation of unknown time is not counted).
export type DailyCancellationRow = { connectionId: string; cancelledOrders: number; partialOrders: number; cancelledUnits: number; unknownTimeOrders: number; unknownTimeUnits: number }

// Orders cancelled whole and orders cancelled in part today, over every store; undefined while the rows are not read.
export function dailyCancellationCounts(stores: DailyCancellationRow[] | undefined) {
  if (!stores) return undefined
  return { whole: stores.reduce((sum, row) => sum + row.cancelledOrders, 0), partial: stores.reduce((sum, row) => sum + row.partialOrders, 0) }
}

// The micro-export card's request: the order summary's PENDING tab alone over the micro-export orders, which carries
// their count and countries. Pending is the overview's and Kargolanacak's set (not yet handed to the carrier); no date.
export function microPendingParams() {
  return new URLSearchParams({ tabs: 'PENDING', micro: 'true' }).toString()
}

// The lists the cards open: the pending micro-export orders, orders cancelled today (whole, or in part), and the resends
// that have not been handed to the carrier and the pending orders whose products are to ship (not about today).
export type DayCard = 'micro' | 'cancelled' | 'partial' | 'resent' | 'ship'

// The list a card opens: the micro-export list in the pending tab (Bekleyen, no date), or İptal / Kısmi İptal on the orders
// cancelled that day. Only the date filter the tab offers is set (order-date-filters.ts), the other is cleared.
export function dayCardFilters(card: DayCard, day: string) {
  if (card === 'ship') return { kind: 'ALL', status: 'PENDING', dateFrom: '', dateTo: '', cancelFrom: '', cancelTo: '' }
  if (card === 'resent') return { kind: 'RESENT_UNSHIPPED', status: 'RESENT', dateFrom: '', dateTo: '', cancelFrom: '', cancelTo: '' }
  return card === 'micro'
    ? { kind: 'MICRO', status: 'PENDING', dateFrom: '', dateTo: '', cancelFrom: '', cancelTo: '' }
    : { kind: 'ALL', status: card === 'cancelled' ? 'CANCELLED' : 'PARTIALLY_CANCELLED', dateFrom: '', dateTo: '', cancelFrom: day, cancelTo: day }
}

// GET /dashboard/bootstrap, the part the cards read: the pending orders (the warehouse queue the overview's Bekleyen
// Siparişler counts) and their products, each per store, and the stores' names.
export type DashboardBootstrap = {
  metrics: { lateOrders: number; uninvoicedInvoices: number; pendingOrders?: number; pendingByConnection?: Record<string, number>; pendingUnitsByConnection?: Record<string, number> | null }
  channels?: Array<{ connectionId: string; platformCode: string; displayName: string }>
}

// The products to ship: the pending orders' products over every store, the number of those orders and a row per store
// (biggest first) for the hover. undefined while the bootstrap is not read or an older answer holds no products.
export function pendingShipments(bootstrap: DashboardBootstrap | undefined) {
  const units = bootstrap?.metrics.pendingUnitsByConnection
  if (!bootstrap || !units) return undefined
  const channels = new Map((bootstrap.channels ?? []).map(channel => [channel.connectionId, channel]))
  const rows = Object.entries(units).filter(([, value]) => value > 0).map(([connectionId, value]) => ({ key: connectionId, code: channels.get(connectionId)?.platformCode ?? '', name: channels.get(connectionId)?.displayName ?? 'Kaldırılmış mağaza', units: value }))
    .sort((left, right) => right.units - left.units || left.name.localeCompare(right.name, 'tr-TR'))
  return { units: rows.reduce((sum, row) => sum + row.units, 0), orders: Object.values(bootstrap.metrics.pendingByConnection ?? {}).reduce((sum, value) => sum + value, 0), rows }
}
