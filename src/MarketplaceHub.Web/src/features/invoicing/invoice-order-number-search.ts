// E-Faturam keeps its own reference on this invoice's provider connection: "not found by our id" proves nothing, so the
// "no invoice at the provider" confirmation asks for a portal search by the order number and states it to the server
// (scan D-2, 13 Nov 2026).
export const uuidNotHonouredCode = 'EFATURAM_UUID_NOT_HONOURED'

export function needsOrderNumberSearch(item: { invoiceErrorCode?: string | null }) {
  return item.invoiceErrorCode === uuidNotHonouredCode
}
