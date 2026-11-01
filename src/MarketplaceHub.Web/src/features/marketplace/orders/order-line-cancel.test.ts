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

  // Live #R1013: a delivered Shopify order with 25 of its 26 lines cancelled listed them all, struck, in Teslim
  // Edildi. A delivered order shows what was delivered; its cancelled lines are in İptal and under Ürün İptali.
  it('shows only the delivered products of a delivered order, its cancelled ones in İptal and under Ürün İptali', () => {
    const order = { derivedStatus: 'DELIVERED', lines: [line('kept', 1, 0), ...Array.from({ length: 25 }, (_, index) => line(`gone-${index}`, 1, 1))] }
    for (const tab of ['ALL', 'DELIVERED']) expect(rowLines(order, tab).map(item => item.id)).toEqual(['kept'])
    expect(rowLines(order, 'CANCELLED')).toHaveLength(25)
    expect(rowLines(order, 'ALL', true)).toHaveLength(26)
    expect(rowLines({ derivedStatus: 'RETURNED', lines }, 'ALL').map(item => item.id)).toEqual(['a', 'b'])
    // An open order keeps its cancelled lines struck through, for the warehouse.
    expect(rowLines({ derivedStatus: 'PROCESSING', lines }, 'PROCESSING')).toHaveLength(3)
  })

  it('shows an order cancelled whole as it is', () => {
    const order = { derivedStatus: 'CANCELLED', lines: [line('a', 1, 1), line('b', 2, 2)] }
    expect(isPartlyCancelled(order)).toBe(false)
    expect(rowLines(order, 'CANCELLED').map(item => item.id)).toEqual(['a', 'b'])
  })
})
