export type ProductPlatformFilterGroup = {
  label: string
  options: Array<{ value: string; label: string }>
}

// NOT_FOUND: every variant once matched or published there is one the marketplace no longer knows.
const platformFilterStates = [
  { state: 'ACTIVE', label: 'Aktif' },
  { state: 'PARTIAL', label: 'Kısmi' },
  { state: 'PASSIVE', label: 'Pasif' },
  { state: 'NOT_FOUND', label: 'Bulunamadı' }
]

const platformFilterPlatforms = [
  { code: 'TRENDYOL', label: 'Trendyol' },
  { code: 'SHOPIFY', label: 'Shopify' },
  { code: 'HEPSIBURADA', label: 'Hepsiburada' },
  { code: 'N11', label: 'N11' },
  { code: 'PAZARAMA', label: 'Pazarama' },
  { code: 'PTTAVM', label: 'PTT AVM' }
]

export const productPlatformFilterGroups: ProductPlatformFilterGroup[] = platformFilterPlatforms.map(platform => ({
  label: platform.label,
  options: platformFilterStates.map(item => ({ value: `${platform.code}:${item.state}`, label: item.label }))
}))
