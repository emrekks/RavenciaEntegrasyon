import { describe, expect, it } from 'vitest'
import { allPlatformsFilterGroup, productPlatformFilterGroups } from './product-platform-filter'

describe('Tüm Platformlar status filter', () => {
  it('offers every state on any marketplace', () => {
    expect(allPlatformsFilterGroup.options.map(option => option.value)).toEqual(['ALL:ACTIVE', 'ALL:PARTIAL', 'ALL:APPROVAL', 'ALL:ERROR', 'ALL:PASSIVE', 'ALL:NOT_FOUND', 'ALL:OFF_SALE'])
    expect(allPlatformsFilterGroup.options.find(option => option.value === 'ALL:ERROR')?.sub).toBe('Reddedilen veya hatalı varyant var (en az bir pazaryerinde)')
  })
})

describe('product platform status filters', () => {
  it('exposes Hepsiburada beside the other marketplace status filters', () => {
    const hepsiburada = productPlatformFilterGroups.find(group => group.label === 'Hepsiburada')

    expect(hepsiburada?.options.map(option => option.value)).toEqual([
      'HEPSIBURADA:ACTIVE',
      'HEPSIBURADA:PARTIAL',
      'HEPSIBURADA:APPROVAL',
      'HEPSIBURADA:ERROR',
      'HEPSIBURADA:PASSIVE',
      'HEPSIBURADA:NOT_FOUND',
      'HEPSIBURADA:OFF_SALE'
    ])
  })

  it('offers Bulunamadı and Satışa Kapalı on every marketplace', () => {
    expect(productPlatformFilterGroups.map(group => group.label)).toEqual(['Trendyol', 'Hepsiburada', 'N11', 'Pazarama', 'PttAVM', 'Shopify'])
    for (const group of productPlatformFilterGroups)
      expect(group.options.slice(-2).map(option => option.label)).toEqual(['Bulunamadı', 'Satışa Kapalı'])
  })

  // Owner, 14 Nov 2026: the red mark had no filter, and Pasif, Bulunamadı and Satışa Kapalı all read "Eşleşme bulunamadı".
  it('offers Hatalı and Onay Bekliyor, and says what each state means', () => {
    const trendyol = productPlatformFilterGroups.find(group => group.label === 'Trendyol')!
    expect(trendyol.options.map(option => [option.label, option.sub])).toEqual([
      ['Aktif', 'Tüm varyantlar eşleşti'],
      ['Kısmi', 'Bazı varyantlar eşleşti'],
      ['Onay Bekliyor', 'Pazaryerinin onayını bekliyor'],
      ['Hatalı', 'Reddedilen veya hatalı varyant var'],
      ['Pasif', 'Bu pazaryerinde eşleşen varyant yok'],
      ['Bulunamadı', 'Pazaryeri artık bu varyantları bilmiyor'],
      ['Satışa Kapalı', 'Pazaryerinde satışa kapalı varyant var']
    ])
  })
})
