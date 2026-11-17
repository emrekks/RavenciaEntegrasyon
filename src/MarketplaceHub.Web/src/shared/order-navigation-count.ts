import type { ApiAnswer } from './api/api-answer'
import { hubPoll } from './api'
import { queryKeys } from './api/query-keys'

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
// A screen that mounts it reads it again, unless another screen read it a moment ago (W2, scan 2026-11-15: the shell's
// badge read it and the lazily loaded overview, mounting a moment later, read the same answer again).
const pendingOrdersFreshMs = 5_000
export const pendingOrdersQuery = {
  queryKey: queryKeys.orders('summary', 'pending'),
  queryFn: ({ signal }: { signal: AbortSignal }) => hubPoll<ApiAnswer<'getOrdersSummary'>>('/orders/summary?tabs=PENDING', { signal }),
  staleTime: 30_000,
  refetchOnMount: (query: { state: { dataUpdatedAt: number } }) => Date.now() - query.state.dataUpdatedAt < pendingOrdersFreshMs ? false : 'always' as const,
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
