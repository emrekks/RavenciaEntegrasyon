// The orders list has two date filters: Sipariş Tarihi (when the order was placed) and İptal Tarihi (when a product of it
// was cancelled). İptal Tarihi is offered only in the İptal and Kısmi İptal tabs, where it is the date that matters,
// and there it takes the place of Sipariş Tarihi, so the filter row stays on one line. A filter that is not offered in
// the tab is not kept, so the list is never narrowed by a date the filter row does not show.
export type TabDateFilters = { dateFrom: string; dateTo: string; cancelFrom: string; cancelTo: string }

export const isCancelTab = (status: string) => status === 'CANCELLED' || status === 'PARTIALLY_CANCELLED'

export function tabDateFilters(status: string, current: TabDateFilters): TabDateFilters {
  return isCancelTab(status)
    ? { dateFrom: '', dateTo: '', cancelFrom: current.cancelFrom, cancelTo: current.cancelTo }
    : { dateFrom: current.dateFrom, dateTo: current.dateTo, cancelFrom: '', cancelTo: '' }
}
