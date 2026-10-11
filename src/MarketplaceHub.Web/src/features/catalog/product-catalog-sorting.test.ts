import { describe, expect, it } from 'vitest'
import { nextProductCatalogSort, productGroupMetric, sortProductGroups } from './product-catalog-sorting'

const groups = [
  { id: 'medium', products: [{ totalStock: 4, startingPrice: 20 }] },
  { id: 'high', products: [{ totalStock: 12, startingPrice: 35 }, { totalStock: 3, startingPrice: 30 }] },
  { id: 'low', products: [{ totalStock: 1, startingPrice: 5 }] },
  { id: 'no-price', products: [{ totalStock: 2, startingPrice: null }] }
]

describe('product catalog sorting', () => {
  it('starts a selected column with the largest values first and toggles direction on repeated clicks', () => {
    const stockDescending = nextProductCatalogSort(null, 'stock')
    expect(stockDescending).toEqual({ field: 'stock', direction: 'desc' })
    expect(nextProductCatalogSort(stockDescending, 'stock')).toEqual({ field: 'stock', direction: 'asc' })
    expect(nextProductCatalogSort(stockDescending, 'price')).toEqual({ field: 'price', direction: 'desc' })
  })

  it('sorts by the displayed combined stock in both directions', () => {
    expect(sortProductGroups(groups, { field: 'stock', direction: 'desc' }).map(group => group.id)).toEqual(['high', 'medium', 'no-price', 'low'])
    expect(sortProductGroups(groups, { field: 'stock', direction: 'asc' }).map(group => group.id)).toEqual(['low', 'no-price', 'medium', 'high'])
  })

  it('sorts by the lowest displayed family price and treats a missing price as zero', () => {
    expect(productGroupMetric(groups[1], 'price')).toBe(30)
    expect(sortProductGroups(groups, { field: 'price', direction: 'desc' }).map(group => group.id)).toEqual(['high', 'medium', 'low', 'no-price'])
    expect(sortProductGroups(groups, { field: 'price', direction: 'asc' }).map(group => group.id)).toEqual(['no-price', 'low', 'medium', 'high'])
  })
})
