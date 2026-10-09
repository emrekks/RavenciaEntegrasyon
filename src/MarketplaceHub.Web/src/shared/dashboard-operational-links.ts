export const dashboardOperationalLinks = {
  pendingOrders: '/orders?status=NEW',
  pendingReturns: '/returns?status=ACTION_REQUIRED',
  pendingInvoices: '/invoices?tab=UNINVOICED',
  dueInvoices: '/invoices?tab=DUE_SOON'
} as const

const returnStatusValues = ['ALL', 'REQUESTED', 'SHIPPING', 'ACTION_REQUIRED', 'APPROVED', 'REJECTED', 'REVIEW', 'DISPUTED', 'SUSPENDED'] as const
const invoiceTabValues = ['ALL', 'UNINVOICED', 'INVOICED', 'DUE_SOON', 'HIDDEN'] as const

export function resolveReturnStatus(value: string | null) {
  return returnStatusValues.find(status => status === value) ?? 'ALL'
}

export function resolveInvoiceTab(value: string | null) {
  return invoiceTabValues.find(tab => tab === value) ?? 'UNINVOICED'
}
