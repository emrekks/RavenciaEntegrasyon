import { hubPoll } from './api'

export type OrderNavigationSummary = {
  new: number
  processing: number
  // The orders the Siparişler page lists under Bekleyen (the warehouse queue).
  pending?: number
  // Those orders per store id and their products (ordered less cancelled); null or missing from an older server.
  pendingByConnection?: Record<string, number> | null
  pendingUnitsByConnection?: Record<string, number> | null
}

// One query for the Bekleyen count: the menu badge and the overview's Bekleyen Siparişler card both use it, so they read
// the same answer at the same moment and refresh together on every order change (the realtime 'orders' event refreshes
// the ['orders'] root). The card read the worker's dashboard snapshot before, which is rebuilt a minute or more after an
// order arrives, so the two disagreed (owner, 6 Nov 2026: badge 13, card 12). It polls only while the tab is on screen
// and says it is automatic, so an open tab still reaches the idle sign-out.
export const pendingOrdersQuery = {
  queryKey: ['orders', 'summary', 'pending'] as const,
  queryFn: () => hubPoll<OrderNavigationSummary>('/orders/summary?tabs=PENDING'),
  staleTime: 30_000,
  refetchOnMount: 'always' as const,
  refetchOnWindowFocus: true,
  refetchInterval: 15_000,
  refetchIntervalInBackground: false,
}

// The menu badge shows the orders the Bekleyen tab lists; an older server without that count falls back to new and
// processing.
export function orderNavigationCount(summary: OrderNavigationSummary | null | undefined): number | undefined {
  if (!summary) return undefined
  return summary.pending ?? summary.new + summary.processing
}
