// The personal fields a shipping label prints (buyer name, delivery address, e-mail) come from the
// order detail at print time; the order list does not carry the e-mail.
import type { Order, OrderAddress } from '../shared'

export type OrderLabelDetail = { id: string; customerName: string; customerEmail: string | null; shipmentAddress?: OrderAddress | null }
export type LabelPersonalData = { customerName: string; shipmentAddress: OrderAddress | null; customerEmail: string }

// Detail requests run a few at a time so a large batch does not flood the API.
export const LABEL_DETAIL_CONCURRENCY = 4

function meaningful(value: string | null | undefined): value is string {
  return typeof value === 'string' && /[\p{L}\p{N}]/u.test(value) && value.trim() !== '—'
}

export function labelPersonalData(item: Pick<Order, 'customerName' | 'shipmentAddress'>, detail: OrderLabelDetail | null | undefined): LabelPersonalData {
  return {
    customerName: meaningful(detail?.customerName) ? detail.customerName.trim() : item.customerName,
    shipmentAddress: detail?.shipmentAddress ?? item.shipmentAddress ?? null,
    customerEmail: detail?.customerEmail?.trim() || '—'
  }
}

export async function loadOrderLabelDetails(ids: readonly string[], load: (id: string) => Promise<OrderLabelDetail>, concurrency = LABEL_DETAIL_CONCURRENCY): Promise<Map<string, OrderLabelDetail>> {
  const unique = Array.from(new Set(ids))
  const details = new Map<string, OrderLabelDetail>()
  let next = 0
  async function worker() {
    while (next < unique.length) {
      const id = unique[next++]
      details.set(id, await load(id))
    }
  }
  await Promise.all(Array.from({ length: Math.max(1, Math.min(concurrency, unique.length)) }, () => worker()))
  return details
}
