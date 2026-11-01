export const dashboardOperationalLinks = {
  // The order list's pending (warehouse queue) and late views; the dashboard counts the same orders.
  pendingOrders: '/orders?status=PENDING',
  lateOrders: '/orders?status=LATE',
  // The low-stock list opens with its window (missing variants by model) already open.
  lowStock: '/products?stock=LOW&details=low-stock',
  pendingReturns: '/returns?status=ACTION_REQUIRED',
  pendingInvoices: '/invoices?tab=UNINVOICED',
  dueInvoices: '/invoices?tab=DUE_SOON'
} as const

// The day's sold units open the order list on that Istanbul day (the list's from/to date filter).
// The day's cancellations open the İptal tab on the orders cancelled that Istanbul day (the list's cancel-date filter).
export const dayCancellationsLink = (day: string) => `/orders?${new URLSearchParams({ status: 'CANCELLED', cancelFrom: day, cancelTo: day })}`

export const dayOrdersLink = (day: string) => `/orders?${new URLSearchParams({ from: day, to: day })}`

const returnStatusValues = ['ALL', 'REQUESTED', 'SHIPPING', 'ACTION_REQUIRED', 'APPROVED', 'REJECTED', 'REVIEW', 'DISPUTED', 'SUSPENDED'] as const
const invoiceTabValues = ['ALL', 'UNINVOICED', 'INVOICED', 'DUE_SOON', 'HIDDEN'] as const

export function resolveReturnStatus(value: string | null) {
  return returnStatusValues.find(status => status === value) ?? 'ALL'
}

export function resolveInvoiceTab(value: string | null) {
  return invoiceTabValues.find(tab => tab === value) ?? 'UNINVOICED'
}
