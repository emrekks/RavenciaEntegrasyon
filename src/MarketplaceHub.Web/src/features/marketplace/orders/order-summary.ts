// /orders/summary: each counter is the number of orders its tab lists for the same filters; newUnitsByConnection is the
// products of the Yeni tab's orders per store, processingUnitsByConnection those of İşleme Alınanlar (null when that tab
// was not counted); lateByConnection and resentByConnection the Geciken and (under resentNotShipped) Yeniden Gönderim
// orders per store.
export type OrderSummary = { all: number; new: number; processing: number; shipped: number; delivered: number; resent: number; onHold: number; cancelled: number; returned: number; returnInTransit: number; partiallyCancelled: number; manualReview: number; pending: number; unverified: number; paymentPending: number; undelivered?: number; late?: number; microExport?: number; microExportCountries?: Record<string, number> | null; newUnitsByConnection?: Record<string, number> | null; processingUnitsByConnection?: Record<string, number> | null; lateByConnection?: Record<string, number> | null; resentByConnection?: Record<string, number> | null }
// /orders/facets: orders per platform code and per cargo provider under the other filters and the status tab.
export type OrderFacets = { platforms: Record<string, number>; cargos: Record<string, number> }

const summaryKeys: Record<string, keyof OrderSummary | undefined> = { ALL: 'all', NEW: 'new', UNVERIFIED: 'unverified', PROCESSING: 'processing', SHIPPED: 'shipped', DELIVERED: 'delivered', RESENT: 'resent', ON_HOLD: 'onHold', CANCELLED: 'cancelled', RETURNED: 'returned', RETURN_IN_TRANSIT: 'returnInTransit', PARTIALLY_CANCELLED: 'partiallyCancelled', MANUAL_REVIEW: 'manualReview', PAYMENT_PENDING: 'paymentPending', UNDELIVERED: 'undelivered', PENDING: 'pending', LATE: 'late' }

// A tab's counter under the list's filters, or undefined when the summary does not say it (no summary, a tab it has no
// counter for): shown as "—", never as 0 and never as an unfiltered count from elsewhere (the dashboard's late total).
export function orderTabCount(tab: string, summary: OrderSummary | undefined): number | undefined {
  const key = summaryKeys[tab]
  const value = summary && key ? summary[key] : undefined
  return typeof value === 'number' ? value : undefined
}

// What a tab or card shows for a counter: "…" while the first answer is on its way, "—" when the summary could not be
// read or does not say it, the number otherwise.
export function orderCountText(value: number | undefined, state: { loading: boolean; error: boolean }) {
  if (state.error) return '—'
  if (state.loading) return '…'
  return value === undefined ? '—' : value.toLocaleString('tr-TR')
}

// A summary card's state from its query: an error wins over anything shown before (with keepPreviousData that would be
// another filter's number); loading only while no answer has come yet, so a failed request never spins forever.
export function summaryMetric(query: { data?: OrderSummary; isError: boolean; isPending: boolean }, pick: (summary: OrderSummary) => number | undefined) {
  if (query.isError) return { value: undefined, loading: false, error: true }
  if (!query.data) return { value: undefined, loading: query.isPending, error: !query.isPending }
  return { value: pick(query.data), loading: false, error: false }
}
