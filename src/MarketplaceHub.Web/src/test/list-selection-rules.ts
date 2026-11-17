// WEB-SAYFALAR-11: what each list does with its row selection (shared/list/useListSelection). The page tests
// (*.selection-rules.test.tsx) check every cell on the real page. Owner, 16 Nov 2026 ("Hepsi korusun"): the four lists
// behave alike, as Ürünler did: a page change keeps the selection, and a picked row that left the page still counts and
// takes part in every bulk action.
//
// clears          the selection is empty afterwards
// keeps           the rows picked stay picked
// counted         the selected row is gone from the refetched page, and the bulk bar still counts it (and a bulk
//                 action still names it)
export type SelectionEvent = 'filter' | 'search' | 'tab' | 'sort' | 'pageSize' | 'page' | 'refetch' | 'rowGone'
export type SelectionOutcome = 'clears' | 'keeps' | 'counted'
export type SelectionList = 'products' | 'orders' | 'invoices' | 'returns'

export const listSelectionRules: Record<SelectionList, Record<SelectionEvent, SelectionOutcome>> = {
  products: { filter: 'clears', search: 'clears', tab: 'clears', sort: 'clears', pageSize: 'clears', page: 'keeps', refetch: 'keeps', rowGone: 'counted' },
  orders: { filter: 'clears', search: 'clears', tab: 'clears', sort: 'clears', pageSize: 'clears', page: 'keeps', refetch: 'keeps', rowGone: 'counted' },
  invoices: { filter: 'clears', search: 'clears', tab: 'clears', sort: 'clears', pageSize: 'clears', page: 'keeps', refetch: 'keeps', rowGone: 'counted' },
  returns: { filter: 'clears', search: 'clears', tab: 'clears', sort: 'clears', pageSize: 'clears', page: 'keeps', refetch: 'keeps', rowGone: 'counted' }
}

export const selectionEvents = Object.keys(listSelectionRules.products) as SelectionEvent[]
