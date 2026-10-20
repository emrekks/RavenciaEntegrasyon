export type OrderNavigationSummary = {
  new: number
  processing: number
  // Orders waiting in the warehouse queue: the same orders the dashboard's "Bekleyen Siparişler" card counts.
  pending?: number
}

// The menu badge shows the warehouse queue the dashboard shows, so both say the same number; an older server
// without that count falls back to new and processing packages.
export function orderNavigationCount(summary: OrderNavigationSummary | null | undefined): number | undefined {
  if (!summary) return undefined
  return summary.pending ?? summary.new + summary.processing
}
