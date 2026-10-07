import { describe, expect, it } from 'vitest'
import { applyVariantBulkEditValue, updateVariantGroupSelection, variantBulkEditIssue, variantGroupSelectionState, type VariantBulkEditRow } from './variant-bulk-edit'

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

describe('quick edit color group selection', () => {
  it('selects or clears every variant in a color group without changing other selections', () => {
    expect(updateVariantGroupSelection(['other', 'small'], ['small', 'large'], true)).toEqual(['other', 'small', 'large'])
    expect(updateVariantGroupSelection(['other', 'small', 'large'], ['small', 'large'], false)).toEqual(['other'])
  })

  it('reports unchecked, partially selected, and fully selected groups', () => {
    expect(variantGroupSelectionState(['small', 'large'], new Set())).toEqual({ checked: false, indeterminate: false })
    expect(variantGroupSelectionState(['small', 'large'], new Set(['small']))).toEqual({ checked: false, indeterminate: true })
    expect(variantGroupSelectionState(['small', 'large'], new Set(['small', 'large']))).toEqual({ checked: true, indeterminate: false })
  })
})
