export type OrderNavigationSummary = {
  new: number
  processing: number
}

export function orderNavigationCount(summary: OrderNavigationSummary | null | undefined): number | undefined {
  if (!summary) return undefined
  return summary.new + summary.processing
}
