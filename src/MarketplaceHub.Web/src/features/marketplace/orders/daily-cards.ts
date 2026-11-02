// The cancellation cards above the order list show today (the Istanbul day), and the micro-export card the pending
// micro-export orders, whatever the list's own filters are. Each opens the list on exactly what it counted.

// GET /dashboard/cancellations: one store's cancellations of the day. The Genel Bakış card reads the same rows, so both
// count an order cancelled whole, and one cancelled in part, the same way (a cancellation of unknown time is not counted).
// wholeUnits are the cancelled units of the orders cancelled whole, partialUnits those cancelled of the orders that stay
// open (an older API answers without them).
export type DailyCancellationRow = { connectionId: string; cancelledOrders: number; partialOrders: number; cancelledUnits: number; unknownTimeOrders: number; unknownTimeUnits: number; wholeUnits?: number; partialUnits?: number }

// Orders cancelled whole and in part today over every store, with their cancelled units (undefined from an older API);
// undefined while the rows are not read.
export function dailyCancellationCounts(stores: DailyCancellationRow[] | undefined) {
  if (!stores) return undefined
  const units = (pick: (row: DailyCancellationRow) => number | undefined) => stores.some(row => pick(row) === undefined) ? undefined : stores.reduce((sum, row) => sum + (pick(row) ?? 0), 0)
  return {
    whole: stores.reduce((sum, row) => sum + row.cancelledOrders, 0),
    partial: stores.reduce((sum, row) => sum + row.partialOrders, 0),
    wholeUnits: units(row => row.wholeUnits),
    partialUnits: units(row => row.partialUnits)
  }
}

// The micro-export card's request: the order summary's PENDING tab alone over the micro-export orders, which carries
// their count and countries. Pending is the overview's set (not yet handed to the carrier); no date.
export function microPendingParams() {
  return new URLSearchParams({ tabs: 'PENDING', micro: 'true' }).toString()
}

// The lists the cards open: the pending micro-export orders, orders cancelled today (whole, or in part), and the resends
// that have not been handed to the carrier (not about today).
export type DayCard = 'micro' | 'cancelled' | 'partial' | 'resent'

// The list a card opens: the micro-export list in the pending tab (Bekleyen, no date), or İptal / Kısmi İptal on the orders
// cancelled that day. Only the date filter the tab offers is set (order-date-filters.ts), the other is cleared.
export function dayCardFilters(card: DayCard, day: string) {
  if (card === 'resent') return { kind: 'RESENT_UNSHIPPED', status: 'RESENT', dateFrom: '', dateTo: '', cancelFrom: '', cancelTo: '' }
  return card === 'micro'
    ? { kind: 'MICRO', status: 'PENDING', dateFrom: '', dateTo: '', cancelFrom: '', cancelTo: '' }
    : { kind: 'ALL', status: card === 'cancelled' ? 'CANCELLED' : 'PARTIALLY_CANCELLED', dateFrom: '', dateTo: '', cancelFrom: day, cancelTo: day }
}

// The Yeniden Gönderim card's request: the order summary's RESENT tab alone over the resends not handed to the carrier,
// which carries their count and their stores (the same set the Yeniden Gönderimler list shows under that filter).
export function resentPendingParams() {
  return new URLSearchParams({ tabs: 'RESENT', resentNotShipped: 'true' }).toString()
}

// GET /dashboard/bootstrap, the part the page reads: the late count and the stores' names (the Yeni Sipariş hover).
export type DashboardBootstrap = {
  metrics: { lateOrders: number; uninvoicedInvoices: number }
  channels?: Array<{ connectionId: string; platformCode: string; displayName: string }>
}

// The Yeni Sipariş and İşleme Alınan cards' products: the summary's units per store summed, and a row per store (biggest first) for the
// hover. undefined while the summary is not read or an older answer holds no units.
export function storeUnits(unitsByConnection: Record<string, number> | null | undefined, channels: DashboardBootstrap['channels']) {
  if (!unitsByConnection) return undefined
  const byId = new Map((channels ?? []).map(channel => [channel.connectionId, channel]))
  const rows = Object.entries(unitsByConnection).filter(([, value]) => value > 0).map(([connectionId, value]) => ({ key: connectionId, code: byId.get(connectionId)?.platformCode ?? '', name: byId.get(connectionId)?.displayName ?? 'Kaldırılmış mağaza', units: value }))
    .sort((left, right) => right.units - left.units || left.name.localeCompare(right.name, 'tr-TR'))
  return { units: rows.reduce((sum, row) => sum + row.units, 0), rows }
}

type Channels = DashboardBootstrap['channels']
const storeOf = (channels: Channels, connectionId: string) => {
  const channel = (channels ?? []).find(item => item.connectionId === connectionId)
  return { key: connectionId, code: channel?.platformCode ?? '', name: channel?.displayName ?? 'Kaldırılmış mağaza' }
}

// The Geciken and Yeniden Gönderim cards' hover: their orders per store, the biggest first; [] when there are none and
// undefined while unread (or from an older API).
export function storeCounts(byConnection: Record<string, number> | null | undefined, channels: Channels) {
  if (!byConnection) return undefined
  return Object.entries(byConnection).filter(([, value]) => value > 0).map(([connectionId, packages]) => ({ ...storeOf(channels, connectionId), packages }))
    .sort((left, right) => right.packages - left.packages || left.name.localeCompare(right.name, 'tr-TR'))
}

// The İptal card's hover: today's cancellations per store, the orders cancelled whole with their units and the partly
// cancelled ones with the units cancelled of them (units undefined from an older API). The biggest first.
export function cancellationStores(stores: DailyCancellationRow[] | undefined, channels: Channels) {
  if (!stores) return undefined
  return stores.filter(row => row.cancelledOrders > 0 || row.partialOrders > 0)
    .map(row => ({ ...storeOf(channels, row.connectionId), packages: row.cancelledOrders, units: row.wholeUnits, partial: row.partialOrders, partialUnits: row.partialUnits }))
    .sort((left, right) => right.packages - left.packages || right.partial - left.partial || left.name.localeCompare(right.name, 'tr-TR'))
}
