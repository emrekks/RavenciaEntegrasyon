import type { QueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { markShippingLabelPrinted, type ShippingLabelFormat } from '../../shipping'

// After the print dialog closed (window.print blocks until then): the local mark, and the server keeps
// the first print per order so every computer sees it, for a single label too (audit 2026-10-20 S8).
// Once the server answered, the order lists and counters are read again: they carry `labelPrintedAt` and the
// "Etiket Yazdırıldı" filter, which the local mark alone does not move. Other screens hear of the first print through
// the server's "orders" realtime event (MarkLabelPrintedAsync) and read their order lists again too.
export function recordShippingLabelsPrinted(ids: readonly string[], format: ShippingLabelFormat, onPrinted: () => void, client: Pick<QueryClient, 'invalidateQueries'>) {
  ids.forEach(id => markShippingLabelPrinted(id, format))
  onPrinted()
  return Promise.allSettled(ids.map(id => hubApi<void>(`/orders/${id}/label-printed`, { method: 'POST' }))).then(() => {
    onPrinted()
    return client.invalidateQueries({ queryKey: ['orders'] })
  })
}
