// The orders list has two date filters: Sipariş Tarihi (when the order was placed) and İptal Tarihi (when a product of it
// was cancelled). İptal Tarihi is offered only in the İptal and Kısmi İptal tabs, where it is the date that matters,
// and there it takes the place of Sipariş Tarihi, so the filter row stays on one line. A filter that is not offered in
// the tab is not kept, so the list is never narrowed by a date the filter row does not show.
export type TabDateFilters = { dateFrom: string; dateTo: string; cancelFrom: string; cancelTo: string }

export const isCancelTab = (status: string) => status === 'CANCELLED' || status === 'PARTIALLY_CANCELLED'

// The Sipariş Türü filter: all orders, the micro-export ones (any tab), or, in the Yeniden Gönderimler tab only, the
// resends whose package the carrier has not taken yet. The last one means nothing in another tab and is not kept there.
export const orderKinds = ['ALL', 'MICRO', 'RESENT_UNSHIPPED'] as const
export function tabKind(status: string, kind: string) {
  return kind === 'RESENT_UNSHIPPED' && status !== 'RESENT' ? 'ALL' : orderKinds.includes(kind as (typeof orderKinds)[number]) ? kind : 'ALL'
}

export function tabDateFilters(status: string, current: TabDateFilters): TabDateFilters {
  return isCancelTab(status)
    ? { dateFrom: '', dateTo: '', cancelFrom: current.cancelFrom, cancelTo: current.cancelTo }
    : { dateFrom: current.dateFrom, dateTo: current.dateTo, cancelFrom: '', cancelTo: '' }
}
