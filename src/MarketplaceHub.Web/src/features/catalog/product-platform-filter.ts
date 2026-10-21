export type ProductPlatformFilterGroup = {
  label: string
  options: Array<{ value: string; label: string }>
}

export const productPlatformFilterGroups: ProductPlatformFilterGroup[] = [
  { label: 'Trendyol', options: [{ value: 'TRENDYOL:ACTIVE', label: 'Aktif' }, { value: 'TRENDYOL:PARTIAL', label: 'Kısmi' }, { value: 'TRENDYOL:PASSIVE', label: 'Pasif' }] },
  { label: 'Shopify', options: [{ value: 'SHOPIFY:ACTIVE', label: 'Aktif' }, { value: 'SHOPIFY:PARTIAL', label: 'Kısmi' }, { value: 'SHOPIFY:PASSIVE', label: 'Pasif' }] },
  { label: 'Hepsiburada', options: [{ value: 'HEPSIBURADA:ACTIVE', label: 'Aktif' }, { value: 'HEPSIBURADA:PARTIAL', label: 'Kısmi' }, { value: 'HEPSIBURADA:PASSIVE', label: 'Pasif' }] },
  { label: 'N11', options: [{ value: 'N11:ACTIVE', label: 'Aktif' }, { value: 'N11:PARTIAL', label: 'Kısmi' }, { value: 'N11:PASSIVE', label: 'Pasif' }] },
  { label: 'Pazarama', options: [{ value: 'PAZARAMA:ACTIVE', label: 'Aktif' }, { value: 'PAZARAMA:PARTIAL', label: 'Kısmi' }, { value: 'PAZARAMA:PASSIVE', label: 'Pasif' }] },
  { label: 'PTT AVM', options: [{ value: 'PTTAVM:ACTIVE', label: 'Aktif' }, { value: 'PTTAVM:PARTIAL', label: 'Kısmi' }, { value: 'PTTAVM:PASSIVE', label: 'Pasif' }] }
]
