import { describe, expect, it } from 'vitest'
import { isPartlyCancelled, lineCancel, partialQuantityText, remainingText, rowLines } from './order-line-cancel'
import type { OrderLine } from '../shared'

const line = (id: string, orderedQuantity: number, cancelledQuantity: number) => ({ id, orderedQuantity, cancelledQuantity }) as OrderLine
// A three-product order: one untouched, two of one with one cancelled, one cancelled whole.
const lines = [line('a', 1, 0), line('b', 2, 1), line('c', 1, 1)]

describe('order line cancellations', () => {
  it('tells a line cancelled whole from one cancelled in part', () => {
    expect(lineCancel(lines[0])).toEqual({ ordered: 1, cancelled: 0, active: 1, whole: false, partial: false })
    expect(lineCancel(lines[1])).toEqual({ ordered: 2, cancelled: 1, active: 1, whole: false, partial: true })
    expect(lineCancel(lines[2])).toEqual({ ordered: 1, cancelled: 1, active: 0, whole: true, partial: false })
    // More cancelled than ordered is read as all of it.
    expect(lineCancel(line('d', 1, 3))).toMatchObject({ cancelled: 1, active: 0, whole: true })
  })

  it('writes a partly cancelled line as what was ordered, what is left and what was cancelled', () => {
    expect(partialQuantityText(lines[1])).toBe('2 → 1 adet · 1 iptal')
    expect(partialQuantityText(lines[0])).toBeNull()
    expect(partialQuantityText(lines[2])).toBeNull()
  })

  it('shows every line of a partly cancelled order, and only the cancelled ones in the İptal tab', () => {
    const order = { derivedStatus: 'SHIPPED', lines }
    expect(isPartlyCancelled(order)).toBe(true)
    for (const tab of ['ALL', 'SHIPPED', 'PARTIALLY_CANCELLED']) expect(rowLines(order, tab).map(item => item.id)).toEqual(['a', 'b', 'c'])
    expect(rowLines(order, 'CANCELLED').map(item => item.id)).toEqual(['b', 'c'])
    expect(remainingText(order)).toBe('Kalan 2 ürün: Kargoda')
  })

  it('shows an order cancelled whole as it is', () => {
    const order = { derivedStatus: 'CANCELLED', lines: [line('a', 1, 1), line('b', 2, 2)] }
    expect(isPartlyCancelled(order)).toBe(false)
    expect(rowLines(order, 'CANCELLED').map(item => item.id)).toEqual(['a', 'b'])
  })
})
