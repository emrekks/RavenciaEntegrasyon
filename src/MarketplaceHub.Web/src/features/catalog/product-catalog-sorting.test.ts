import { describe, expect, it } from 'vitest'
import { nextProductCatalogSort, parseProductCatalogSort, productCatalogSortOptions, productCatalogSortValue } from './product-catalog-sorting'

describe('product catalog sorting', () => {
  it('labels price and stock sorting as catalog wide, not current-page only', () => {
    expect(productCatalogSortOptions.some(([, label]) => label.includes('Bu Sayfada'))).toBe(false)
    expect(productCatalogSortOptions.map(([value]) => value)).toEqual(['default', 'price-desc', 'price-asc', 'stock-desc', 'stock-asc'])
  })

  it('cycles a column through ascending, descending, and unsorted', () => {
    const stockAscending = nextProductCatalogSort(null, 'stock')
    expect(stockAscending).toEqual({ field: 'stock', direction: 'asc' })
    const stockDescending = nextProductCatalogSort(stockAscending, 'stock')
    expect(stockDescending).toEqual({ field: 'stock', direction: 'desc' })
    expect(nextProductCatalogSort(stockDescending, 'stock')).toBeNull()
    expect(nextProductCatalogSort(stockAscending, 'price')).toEqual({ field: 'price', direction: 'asc' })
  })

  it('sends the sort as the list value the server pages by and reads it back from the address', () => {
    expect(productCatalogSortValue(null)).toBe('')
    expect(productCatalogSortValue({ field: 'price', direction: 'desc' })).toBe('price-desc')
    expect(parseProductCatalogSort('stock-asc')).toEqual({ field: 'stock', direction: 'asc' })
    expect(parseProductCatalogSort('default')).toBeNull()
    expect(parseProductCatalogSort('price-up')).toBeNull()
    expect(parseProductCatalogSort(null)).toBeNull()
  })
})
