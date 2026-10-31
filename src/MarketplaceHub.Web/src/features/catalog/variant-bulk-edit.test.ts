import { describe, expect, it } from 'vitest'
import { applyVariantBulkEditValue, toggleVariantGroupRow, updateVariantGroupSelection, variantGroupRowMates, variantBulkEditIssue, variantGroupSelectionState, type VariantBulkEditRow } from './variant-bulk-edit'

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
    expect(variantBulkEditIssue('stock', 2.5)).toBe('Stok değeri tam sayı olmalıdır.')
    expect(variantBulkEditIssue('costPrice', -1)).toBe('Sıfır veya daha büyük geçerli bir değer girin.')
  })

  it('leaves the sale/list-price comparison to save', () => {
    expect(variantBulkEditIssue('salePrice', 110)).toBeNull()
    expect(variantBulkEditIssue('listPrice', 85)).toBeNull()
    expect(variantBulkEditIssue('salePrice', 95)).toBeNull()
    expect(variantBulkEditIssue('listPrice', 100)).toBeNull()
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

describe('variant group rows', () => {
  const colors = ['haki', 'kahve', 'kiremit', 'lacivert', 'siyah']

  it('finds the groups in the same two-column row', () => {
    expect(variantGroupRowMates(colors, 'kiremit', 2)).toEqual(['kiremit', 'lacivert'])
    expect(variantGroupRowMates(colors, 'lacivert', 2)).toEqual(['kiremit', 'lacivert'])
    expect(variantGroupRowMates(colors, 'siyah', 2)).toEqual(['siyah'])
    expect(variantGroupRowMates(colors, 'kahve', 1)).toEqual(['kahve'])
    expect(variantGroupRowMates(colors, 'yok', 2)).toEqual([])
  })

  it('opens and closes a row together and leaves other rows alone', () => {
    expect(toggleVariantGroupRow(['siyah'], colors, 'lacivert', 2)).toEqual(['siyah', 'kiremit', 'lacivert'])
    expect(toggleVariantGroupRow(['kiremit', 'lacivert', 'siyah'], colors, 'kiremit', 2)).toEqual(['siyah'])
    expect(toggleVariantGroupRow(['haki'], colors, 'kahve', 1)).toEqual(['haki', 'kahve'])
    expect(toggleVariantGroupRow(['haki', 'kahve'], colors, 'haki', 1)).toEqual(['kahve'])
  })

  it('closes the whole row when one of its groups was left open from a one-column view', () => {
    expect(toggleVariantGroupRow(['haki'], colors, 'haki', 2)).toEqual([])
    expect(toggleVariantGroupRow(['haki'], colors, 'kahve', 2)).toEqual(['haki', 'kahve'])
  })
})
