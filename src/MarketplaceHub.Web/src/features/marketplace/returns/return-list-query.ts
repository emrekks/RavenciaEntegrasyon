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
  // Cards of the current tab per choice under the other filters: platform code, cargo, reason key, invoice label.
  platformCounts?: Record<string, number>
  cargoCounts?: Record<string, number>
  reasonCounts?: Record<string, number>
  invoiceStatusCounts?: Record<string, number>
  // The metric cards: rejected returns whose package back to the customer has not been handed to cargo (and those
  // whose package state is not known), and the returns approved and rejected since 00:00 Istanbul.
  metrics?: { rejectedAwaitingCargo: number; rejectedCargoUnknown: number; todayApproved: number; todayRejected: number } | null
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

// The list's order (GET /returns?sort=…). The return request date (the marketplace's own date of the claim) is the
// default on every tab; the approval date only exists on Onaylanan. A claim the marketplace has not dated is listed
// last in either direction.
export const RETURN_SORTS = ['REQUESTED_DESC', 'REQUESTED_ASC', 'ORDERED_DESC', 'ORDERED_ASC', 'APPROVED_DESC', 'APPROVED_ASC'] as const
export type ReturnSort = typeof RETURN_SORTS[number]
export const DEFAULT_RETURN_SORT: ReturnSort = 'REQUESTED_DESC'
const returnSortLabels: Record<ReturnSort, string> = {
  REQUESTED_DESC: 'En Yeni İade Talebi',
  REQUESTED_ASC: 'En Eski İade Talebi',
  ORDERED_DESC: 'En Yeni Sipariş',
  ORDERED_ASC: 'En Eski Sipariş',
  APPROVED_DESC: 'En Yeni Onay',
  APPROVED_ASC: 'En Eski Onay'
}
const approvalSort = (sort: ReturnSort) => sort === 'APPROVED_DESC' || sort === 'APPROVED_ASC'

export function returnSortOptions(status: string): Array<[ReturnSort, string]> {
  return RETURN_SORTS.filter(sort => status === 'APPROVED' || !approvalSort(sort)).map(sort => [sort, returnSortLabels[sort]])
}

// The order a tab is asked for: an approval order chosen on Onaylanan falls back to the default elsewhere.
export function effectiveReturnSort(status: string, sort: ReturnSort): ReturnSort {
  return status !== 'APPROVED' && approvalSort(sort) ? DEFAULT_RETURN_SORT : sort
}
