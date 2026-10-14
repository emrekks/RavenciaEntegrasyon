export type ProductCatalogSort = { field: 'price' | 'stock'; direction: 'asc' | 'desc' } | null

type ProductGroupMetrics = {
  products: Array<{ totalStock: number; startingPrice: number | null }>
}

export function productGroupMetric(group: ProductGroupMetrics, field: 'price' | 'stock') {
  if (field === 'stock') return group.products.reduce((total, product) => total + product.totalStock, 0)
  const prices = group.products.map(product => product.startingPrice).filter((price): price is number => price != null)
  return prices.length ? Math.min(...prices) : 0
}

export function nextProductCatalogSort(current: ProductCatalogSort, field: 'price' | 'stock'): ProductCatalogSort {
  if (current?.field !== field) return { field, direction: 'asc' }
  if (current.direction === 'asc') return { field, direction: 'desc' }
  return null
}

export function sortProductGroups<T extends ProductGroupMetrics>(groups: T[], sort: ProductCatalogSort): T[] {
  if (!sort) return groups
  const sign = sort.direction === 'asc' ? 1 : -1
  return [...groups].sort((left, right) => (productGroupMetric(left, sort.field) - productGroupMetric(right, sort.field)) * sign)
}
