// WEB-SAYFALAR-11: what each list does today with its row selection, pinned before the four pages moved onto
// shared/list/useListSelection. The page tests (*.selection-rules.test.tsx) check every cell on the real page. Where the
// pages differ, the difference is kept (a useListSelection option) and waits for the owner.
//
// clears          the selection is empty afterwards
// keeps           the rows picked stay picked
// counted         the selected row is gone from the refetched page, and the bulk bar still counts it (and a bulk
//                 action still names it)
// uncounted       the selected row is gone from the refetched page, and the bulk bar no longer counts it; the pick is
//                 remembered, so the row is ticked again if a later refetch brings it back
export type SelectionEvent = 'filter' | 'search' | 'tab' | 'sort' | 'pageSize' | 'page' | 'refetch' | 'rowGone'
export type SelectionOutcome = 'clears' | 'keeps' | 'counted' | 'uncounted'
export type SelectionList = 'products' | 'orders' | 'invoices' | 'returns'

export const listSelectionRules: Record<SelectionList, Record<SelectionEvent, SelectionOutcome>> = {
  products: { filter: 'clears', search: 'clears', tab: 'clears', sort: 'clears', pageSize: 'clears', page: 'keeps', refetch: 'keeps', rowGone: 'counted' },
  orders: { filter: 'clears', search: 'clears', tab: 'clears', sort: 'clears', pageSize: 'clears', page: 'clears', refetch: 'keeps', rowGone: 'uncounted' },
  invoices: { filter: 'clears', search: 'clears', tab: 'clears', sort: 'clears', pageSize: 'clears', page: 'clears', refetch: 'keeps', rowGone: 'uncounted' },
  returns: { filter: 'clears', search: 'clears', tab: 'clears', sort: 'clears', pageSize: 'clears', page: 'clears', refetch: 'keeps', rowGone: 'uncounted' }
}

export const selectionEvents = Object.keys(listSelectionRules.products) as SelectionEvent[]
