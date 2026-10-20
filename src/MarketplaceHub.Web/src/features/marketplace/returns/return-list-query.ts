// The Returns page asks the server for one page of cards (GET /returns) and for the tab counts and
// filter choices (GET /returns/summary); both take the same filters.
import { istanbulDayBoundary } from '../../../shared/format-date'

export type ReturnFilters = { status: string; search: string; from: string; to: string }
export type ReturnRefinements = { platforms: string[]; cargo: string; reason: string; invoiceStatus: string }
export type ReturnPaging = { sort: string | null; limit: number; after: string | null; page: number }
export type ReturnSummary = {
  tabCounts: Record<string, number>
  platforms: Array<{ platformCode: string; displayName: string }>
  cargoProviders: string[]
  reasons: Array<{ value: string; reasonCode: string | null; reasonText: string | null }>
  invoiceStatuses: string[]
  // Cards of the current tab before the platform, cargo, reason and invoice filters.
  unfilteredCount: number
}

export function returnSummaryParams(filters: ReturnFilters, refinements: ReturnRefinements) {
  const params = new URLSearchParams()
  if (filters.status !== 'ALL') params.set('status', filters.status)
  if (filters.search.trim()) params.set('search', filters.search.trim())
  if (filters.from) params.set('dateFrom', istanbulDayBoundary(filters.from))
  if (filters.to) params.set('dateTo', istanbulDayBoundary(filters.to, true))
  if (refinements.platforms.length) params.set('platforms', refinements.platforms.join(','))
  if (refinements.cargo !== 'ALL') params.set('cargo', refinements.cargo)
  if (refinements.reason !== 'ALL') params.set('reasonKey', refinements.reason)
  if (refinements.invoiceStatus !== 'ALL') params.set('invoiceStatus', refinements.invoiceStatus)
  return params
}

// A known cursor continues the list; a page jumped to without one is asked for by number.
export function returnListParams(filters: ReturnFilters, refinements: ReturnRefinements, paging: ReturnPaging) {
  const params = returnSummaryParams(filters, refinements)
  if (paging.sort) params.set('sort', paging.sort)
  params.set('limit', String(paging.limit))
  if (paging.after) params.set('after', paging.after)
  else if (paging.page > 1) params.set('page', String(paging.page))
  return params
}
