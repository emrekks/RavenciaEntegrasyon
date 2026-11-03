export type DataResetResult = { products: number; orders: number; returns: number; invoices: number; categories: number; categoryAttributes: number; brands: number; options: number }

// Each reset scope and the count the server answers for it, in the order the message lists them.
const scopeCounts: Array<{ scope: string; field: keyof DataResetResult; unit: string }> = [
  { scope: 'ORDERS', field: 'orders', unit: 'sipariş' },
  { scope: 'RETURNS', field: 'returns', unit: 'iade' },
  { scope: 'INVOICES', field: 'invoices', unit: 'fatura' },
  { scope: 'PRODUCTS', field: 'products', unit: 'ürün' },
  { scope: 'CATEGORIES', field: 'categories', unit: 'kategori' },
  { scope: 'CATEGORY_ATTRIBUTES', field: 'categoryAttributes', unit: 'kategori özelliği' },
  { scope: 'BRANDS', field: 'brands', unit: 'marka' },
  { scope: 'OPTIONS', field: 'options', unit: 'ürün seçeneği' }
]

/** The success message of a data reset: the count of every scope that was picked (orders, returns and invoices too). */
export function dataResetSummary(result: DataResetResult, scopes: readonly string[]) {
  const picked = new Set(scopes)
  const parts = scopeCounts
    .filter(item => picked.has(item.scope))
    .map(item => `${(result[item.field] ?? 0).toLocaleString('tr-TR')} ${item.unit}`)
  return parts.length ? `Temizlik tamamlandı: ${parts.join(', ')}.` : 'Temizlik tamamlandı.'
}
