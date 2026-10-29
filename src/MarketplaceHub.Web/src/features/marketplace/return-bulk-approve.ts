// Texts of the bulk return approval: the question asked before the decisions are sent and the failure report after.

export type BulkApproveScope = { platformCode: string; orderNumber: string; units: number }

/** Distinct orders (per marketplace) and the product units the selected returns approve. */
export function bulkApproveCounts(items: readonly BulkApproveScope[]): { orders: number; units: number } {
  const orders = new Set(items.map(item => `${item.platformCode.toUpperCase()}:${item.orderNumber.trim()}`))
  const units = items.reduce((sum, item) => sum + Math.max(0, item.units), 0)
  return { orders: orders.size, units }
}

/** "2 sipariş ve 3 ürün onaylanacak. Onaylamak istiyor musunuz?" */
export function bulkApproveConfirmText(items: readonly BulkApproveScope[]): string {
  const { orders, units } = bulkApproveCounts(items)
  return `${orders.toLocaleString('tr-TR')} sipariş ve ${units.toLocaleString('tr-TR')} ürün onaylanacak. Onaylamak istiyor musunuz?`
}

/**
 * Names the orders whose approval failed, so the toast says which ones to look at after the popups close. A reason
 * shared by every failure is added once; differing reasons stay in the notification's per-order details.
 */
export function bulkApproveFailureText(failures: ReadonlyArray<{ orderNumber: string; message: string }>): string {
  if (failures.length === 0) return ''
  const orders = Array.from(new Set(failures.map(failure => failure.orderNumber))).join(', ')
  const reasons = new Set(failures.map(failure => failure.message.trim()).filter(Boolean))
  const reason = reasons.size === 1 ? ` Neden: ${[...reasons][0]!.replace(/[.\s]+$/u, '')}.` : ''
  return `Onaylanamayan ${failures.length === 1 ? 'sipariş' : 'siparişler'}: ${orders}.${reason}`
}
