export type ProductPlatformFilterGroup = {
  label: string
  options: Array<{ value: string; label: string }>
}

export const productPlatformFilterGroups: ProductPlatformFilterGroup[] = [
  { label: 'Trendyol', options: [{ value: 'TRENDYOL:ACTIVE', label: 'Aktif' }, { value: 'TRENDYOL:PARTIAL', label: 'Kısmi' }, { value: 'TRENDYOL:PASSIVE', label: 'Pasif' }] },
  { label: 'Shopify', options: [{ value: 'SHOPIFY:ACTIVE', label: 'Aktif' }, { value: 'SHOPIFY:PARTIAL', label: 'Kısmi' }, { value: 'SHOPIFY:PASSIVE', label: 'Pasif' }] },
  { label: 'Hepsiburada', options: [{ value: 'HEPSIBURADA:ACTIVE', label: 'Aktif' }, { value: 'HEPSIBURADA:PARTIAL', label: 'Kısmi' }, { value: 'HEPSIBURADA:PASSIVE', label: 'Pasif' }] }
]
