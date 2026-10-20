import { describe, expect, it, vi } from 'vitest'
import { ApiRequestError } from '../../shared/api'
import { applyVariantStockSets, bulkEditSummary, parseStockInput, stockTarget } from './variant-stock-set'

describe('variant stock set', () => {
  it('treats an empty field as no value, not zero', () => {
    expect(parseStockInput('')).toEqual({ kind: 'empty' })
    expect(parseStockInput('  ')).toEqual({ kind: 'empty' })
    expect(parseStockInput('0')).toEqual({ kind: 'value', value: 0 })
    expect(parseStockInput(7)).toEqual({ kind: 'value', value: 7 })
    expect(parseStockInput('-1')).toEqual({ kind: 'invalid' })
    expect(parseStockInput('1.5')).toEqual({ kind: 'invalid' })
  })

  it('computes the absolute target for each bulk action', () => {
    expect(stockTarget('SET', 4, 10)).toBe(4)
    expect(stockTarget('ADD', 4, 10)).toBe(14)
    expect(stockTarget('SUBTRACT', 4, 10)).toBe(6)
  })

  it('keeps going after a failure and reports stale rows', async () => {
    const send = vi.fn(async (update: { variantId: string }) => {
      if (update.variantId === 'b') throw new ApiRequestError('Stok başka bir işlemle değişti; güncel stok 7.', 409, 'STOCK_CHANGED')
    })
    const result = await applyVariantStockSets([
      { variantId: 'a', sku: 'A', onHand: 5, expectedOnHand: 2 },
      { variantId: 'b', sku: 'B', onHand: 5, expectedOnHand: 3 },
      { variantId: 'c', sku: 'C', onHand: 4, expectedOnHand: 4 },
      { variantId: 'd', sku: 'D', onHand: 1, expectedOnHand: 0 }
    ], send)
    expect(send).toHaveBeenCalledTimes(3)
    expect(result.saved.map(item => item.variantId)).toEqual(['a', 'd'])
    expect(result.failed).toEqual([{ variantId: 'b', sku: 'B', message: 'Stok başka bir işlemle değişti; güncel stok 7.', stale: true }])
  })

  it('summarises a partial result truthfully', () => {
    expect(bulkEditSummary(3, [])).toBe('3 varyant başarıyla güncellendi.')
    expect(bulkEditSummary(3, [{ sku: 'B', message: 'Stok değişti' }])).toBe('2 varyant güncellendi, 1 varyant güncellenemedi. B: Stok değişti')
  })
})
