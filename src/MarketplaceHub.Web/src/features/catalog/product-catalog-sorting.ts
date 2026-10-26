export type ProductCatalogSort = { field: 'price' | 'stock'; direction: 'asc' | 'desc' } | null

// The server sorts the whole filtered catalog by the shown price (lowest of a card's variants) or the shown total
// stock and pages that order (`sort` on /products); cards with no price or no stock record come last.
export const productCatalogSortOptions: Array<[string, string]> = [
  ['default', 'Varsayılan'],
  ['price-desc', 'Satış Fiyatına Göre Azalan'],
  ['price-asc', 'Satış Fiyatına Göre Artan'],
  ['stock-desc', 'Stok Miktarına Göre Azalan'],
  ['stock-asc', 'Stok Miktarına Göre Artan']
]

/** The `sort` value of the products list ('' for the default catalog order). */
export function productCatalogSortValue(sort: ProductCatalogSort) {
  return sort ? `${sort.field}-${sort.direction}` : ''
}

export function parseProductCatalogSort(value: string | null | undefined): ProductCatalogSort {
  const match = /^(price|stock)-(asc|desc)$/.exec(value?.trim() ?? '')
  return match ? { field: match[1] as 'price' | 'stock', direction: match[2] as 'asc' | 'desc' } : null
}

export function nextProductCatalogSort(current: ProductCatalogSort, field: 'price' | 'stock'): ProductCatalogSort {
  if (current?.field !== field) return { field, direction: 'asc' }
  if (current.direction === 'asc') return { field, direction: 'desc' }
  return null
}
