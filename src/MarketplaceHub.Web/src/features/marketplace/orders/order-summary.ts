// /orders/summary: each counter is the number of orders its tab lists for the same filters.
export type OrderSummary = { all: number; new: number; processing: number; shipped: number; delivered: number; resent: number; onHold: number; cancelled: number; returned: number; returnInTransit: number; partiallyCancelled: number; manualReview: number; pending: number; unverified: number; paymentPending: number; undelivered?: number; late?: number; microExport?: number; microExportCountries?: Record<string, number> | null }
// /orders/facets: orders per platform code and per cargo provider under the other filters and the status tab.
export type OrderFacets = { platforms: Record<string, number>; cargos: Record<string, number> }

const summaryKeys: Record<string, keyof OrderSummary | undefined> = { ALL: 'all', NEW: 'new', UNVERIFIED: 'unverified', PROCESSING: 'processing', SHIPPED: 'shipped', DELIVERED: 'delivered', RESENT: 'resent', ON_HOLD: 'onHold', CANCELLED: 'cancelled', RETURNED: 'returned', RETURN_IN_TRANSIT: 'returnInTransit', PARTIALLY_CANCELLED: 'partiallyCancelled', MANUAL_REVIEW: 'manualReview', PAYMENT_PENDING: 'paymentPending', UNDELIVERED: 'undelivered', PENDING: 'pending', LATE: 'late' }

// A tab's counter. The Geciken tab counts the filtered late orders like every other tab; the unfiltered dashboard
// count stands in only while an older API answers without `late`.
export function orderTabCount(tab: string, summary: OrderSummary | undefined, dashboardLate?: number) {
  const key = summaryKeys[tab]
  const value = summary && key ? summary[key] : undefined
  if (typeof value === 'number') return value
  if (tab === 'LATE') return dashboardLate ?? 0
  return 0
}
