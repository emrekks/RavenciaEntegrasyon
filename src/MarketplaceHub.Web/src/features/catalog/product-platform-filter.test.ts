import { describe, expect, it } from 'vitest'
import { productPlatformFilterGroups } from './product-platform-filter'

describe('product platform status filters', () => {
  it('exposes Hepsiburada beside the other marketplace status filters', () => {
    const hepsiburada = productPlatformFilterGroups.find(group => group.label === 'Hepsiburada')

    expect(hepsiburada?.options.map(option => option.value)).toEqual([
      'HEPSIBURADA:ACTIVE',
      'HEPSIBURADA:PARTIAL',
      'HEPSIBURADA:PASSIVE',
      'HEPSIBURADA:NOT_FOUND'
    ])
  })

  it('offers Bulunamadı on every marketplace', () => {
    expect(productPlatformFilterGroups.map(group => group.label)).toEqual(['Trendyol', 'Shopify', 'Hepsiburada', 'N11', 'Pazarama', 'PTT AVM'])
    for (const group of productPlatformFilterGroups)
      expect(group.options.at(-1)?.label).toBe('Bulunamadı')
  })
})
