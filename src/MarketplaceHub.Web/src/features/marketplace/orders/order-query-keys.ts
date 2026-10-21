// One order's full record (/orders/:id), shared by the invoice windows. It sits under ['orders'] so every place that
// refreshes the order lists after an invoice, a status change or a realtime event refreshes the open windows too.
export function orderDetailQueryKey(orderId: string) {
  return ['orders', 'detail', orderId] as const
}
