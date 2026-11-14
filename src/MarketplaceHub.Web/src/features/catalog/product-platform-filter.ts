import { platformDisplayNames, platformDisplayOrder } from '../../shared/platform-names'

export type ProductPlatformFilterGroup = {
  label: string
  options: Array<{ value: string; label: string; sub: string }>
}

// The states the products list's platform marks show, each with what it means (server ProductListingHealth):
// ACTIVE: every variant of the family is matched there. PARTIAL: some are. PASSIVE: none is matched and none was lost
// (the family is not on that marketplace). NOT_FOUND: every variant once matched or published there is one the
// marketplace no longer knows. OFF_SALE: a matched variant the marketplace closed to sale for a reason other than a
// stock of 0 (the stock filter finds those). ERROR: a card the marketplace refused or whose send failed (the red mark).
// APPROVAL: a card the marketplace still reviews.
const platformFilterStates = [
  { state: 'ACTIVE', label: 'Aktif', sub: 'Tüm varyantlar eşleşti' },
  { state: 'PARTIAL', label: 'Kısmi', sub: 'Bazı varyantlar eşleşti' },
  { state: 'APPROVAL', label: 'Onay Bekliyor', sub: 'Pazaryerinin onayını bekliyor' },
  { state: 'ERROR', label: 'Hatalı', sub: 'Reddedilen veya hatalı varyant var' },
  { state: 'PASSIVE', label: 'Pasif', sub: 'Bu pazaryerinde eşleşen varyant yok' },
  { state: 'NOT_FOUND', label: 'Bulunamadı', sub: 'Pazaryeri artık bu varyantları bilmiyor' },
  { state: 'OFF_SALE', label: 'Satışa Kapalı', sub: 'Pazaryerinde satışa kapalı varyant var' }
]

const platformFilterPlatforms = platformDisplayOrder.map(code => ({ code, label: platformDisplayNames[code] }))

export const productPlatformFilterGroups: ProductPlatformFilterGroup[] = platformFilterPlatforms.map(platform => ({
  label: platform.label,
  options: platformFilterStates.map(item => ({ value: `${platform.code}:${item.state}`, label: item.label, sub: item.sub }))
}))

// Tüm Platformlar › a state (owner, 14 Nov 2026): the family is in that state on at least one marketplace (server ALL).
export const allPlatformsFilterGroup: ProductPlatformFilterGroup = {
  label: 'Tüm Platformlar',
  options: platformFilterStates.map(item => ({ value: `ALL:${item.state}`, label: item.label, sub: `${item.sub} (en az bir pazaryerinde)` }))
}
