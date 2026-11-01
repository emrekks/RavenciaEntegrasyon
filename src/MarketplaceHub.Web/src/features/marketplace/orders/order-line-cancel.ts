import { statusLabel } from '../../../shared/status-labels'
import { titleTr } from '../returns-ui'
import type { Order, OrderLine } from '../shared'

// How an order line was cancelled. A line cancelled whole is struck through and marked İptal; a line cancelled in
// part keeps its look and says how many are left ("2 → 1 adet · 1 iptal").
export type LineCancel = { ordered: number; cancelled: number; active: number; whole: boolean; partial: boolean }

export function lineCancel(line: Pick<OrderLine, 'orderedQuantity' | 'cancelledQuantity'>): LineCancel {
  const ordered = Math.max(0, line.orderedQuantity ?? 0)
  const cancelled = Math.min(ordered, Math.max(0, line.cancelledQuantity ?? 0))
  const active = ordered - cancelled
  return { ordered, cancelled, active, whole: cancelled > 0 && active === 0, partial: cancelled > 0 && active > 0 }
}

// The text under a partly cancelled line's price; null for any other line.
export function partialQuantityText(line: Pick<OrderLine, 'orderedQuantity' | 'cancelledQuantity'>) {
  const state = lineCancel(line)
  return state.partial ? `${state.ordered} → ${state.active} adet · ${state.cancelled} iptal` : null
}

const isCancelledWhole = (order: Pick<Order, 'derivedStatus'>) => order.derivedStatus.toUpperCase() === 'CANCELLED'

// An order cancelled in part: some products cancelled while the order itself was not.
export function isPartlyCancelled(order: Pick<Order, 'derivedStatus' | 'lines'>) {
  return !isCancelledWhole(order) && (order.lines ?? []).some(line => lineCancel(line).cancelled > 0)
}

// The lines a list row shows: in the İptal tab a partly cancelled order shows only what was cancelled of it; every
// other view shows all its lines, the cancelled ones struck through.
export function rowLines(order: Pick<Order, 'derivedStatus' | 'lines'>, tab: string) {
  const lines = order.lines ?? []
  return tab === 'CANCELLED' && isPartlyCancelled(order) ? lines.filter(line => lineCancel(line).cancelled > 0) : lines
}

// Where the rest of a partly cancelled order is, for its row in the İptal tab ("Kalan 2 ürün: Kargoda").
export function remainingText(order: Pick<Order, 'derivedStatus' | 'lines'>) {
  const left = (order.lines ?? []).reduce((sum, line) => sum + lineCancel(line).active, 0)
  return `Kalan ${left.toLocaleString('tr-TR')} ürün: ${titleTr(statusLabel(order.derivedStatus))}`
}
