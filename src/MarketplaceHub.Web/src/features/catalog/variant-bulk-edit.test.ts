import { describe, expect, it } from 'vitest'
import { applyVariantBulkEditValue, variantBulkEditIssue, type VariantBulkEditRow } from './variant-bulk-edit'

const rows: VariantBulkEditRow[] = [
  { key: 'small', stock: 1, salePrice: 80, costPrice: 20, listPrice: 100 },
  { key: 'large', stock: 2, salePrice: 90, costPrice: 30, listPrice: 120 },
]

describe('variant bulk numeric editing', () => {
  it('updates only the rows matching the active variant filters', () => {
    expect(applyVariantBulkEditValue(rows, new Set(['small']), 'stock', 7)).toEqual([
      { ...rows[0], stock: 7 },
      rows[1],
    ])
  })

  it('requires whole-number stock and non-negative values', () => {
    expect(variantBulkEditIssue('stock', 2.5, rows)).toBe('Stok değeri tam sayı olmalıdır.')
    expect(variantBulkEditIssue('costPrice', -1, rows)).toBe('Sıfır veya daha büyük geçerli bir değer girin.')
  })

  it('preserves the sale/list-price relationship for every targeted row', () => {
    expect(variantBulkEditIssue('salePrice', 110, rows)).toMatch(/liste fiyatını aşamaz/)
    expect(variantBulkEditIssue('listPrice', 85, rows)).toMatch(/satış fiyatının altında olamaz/)
    expect(variantBulkEditIssue('salePrice', 95, rows)).toBeNull()
    expect(variantBulkEditIssue('listPrice', 100, rows)).toBeNull()
  })
})
