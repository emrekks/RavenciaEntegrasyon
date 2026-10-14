import { describe, expect, it } from 'vitest'
import { allocateStock, totalUnits } from './return-stock-allocation'

describe('return stock allocation', () => {
  it('spreads the requested units over the lines in order', () => {
    const lines = [{ id: 'a', quantity: 1 }, { id: 'b', quantity: 1 }, { id: 'c', quantity: 1 }]
    expect(allocateStock(lines, 2)).toEqual([{ returnLineId: 'a', quantity: 1 }, { returnLineId: 'b', quantity: 1 }])
  })

  it('splits a line that holds more than the requested units', () => {
    expect(allocateStock([{ id: 'a', quantity: 3 }], 2)).toEqual([{ returnLineId: 'a', quantity: 2 }])
    expect(allocateStock([{ id: 'a', quantity: 2 }, { id: 'b', quantity: 2 }], 3)).toEqual([{ returnLineId: 'a', quantity: 2 }, { returnLineId: 'b', quantity: 1 }])
  })

  it('never adds more than the lines hold and ignores empty requests', () => {
    expect(allocateStock([{ id: 'a', quantity: 1 }], 5)).toEqual([{ returnLineId: 'a', quantity: 1 }])
    expect(allocateStock([{ id: 'a', quantity: 1 }], 0)).toEqual([])
    expect(totalUnits([{ id: 'a', quantity: 2 }, { id: 'b', quantity: 1 }])).toBe(3)
  })
})
