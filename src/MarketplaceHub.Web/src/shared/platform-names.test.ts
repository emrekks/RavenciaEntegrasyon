import { describe, expect, it } from 'vitest'
import { comparePlatformCodes, connectionTitle, knownPlatformName, platformDisplayName, platformDisplayOrder } from './platform-names'

describe('platform names', () => {
  it('names every platform one way', () => {
    expect(platformDisplayOrder.map(code => platformDisplayName(code))).toEqual(['Trendyol', 'Hepsiburada', 'N11', 'Pazarama', 'PttAVM', 'Shopify'])
    expect(platformDisplayName(' pttavm ')).toBe('PttAVM')
    expect(platformDisplayName('TRENDYOL_EFATURAM')).toBe('Trendyol E-Faturam')
  })

  it('leaves an unknown code as it is', () => {
    expect(knownPlatformName('ETSY')).toBeNull()
    expect(platformDisplayName('ETSY')).toBe('ETSY')
    expect(platformDisplayName(null, 'Pazar yeri')).toBe('Pazar yeri')
  })

  it('sorts codes in the display order, the invoice provider and unknown codes last', () => {
    expect(['SHOPIFY', 'ETSY', 'TRENDYOL_EFATURAM', 'PTTAVM', 'N11', 'TRENDYOL', 'PAZARAMA', 'HEPSIBURADA'].sort(comparePlatformCodes))
      .toEqual(['TRENDYOL', 'HEPSIBURADA', 'N11', 'PAZARAMA', 'PTTAVM', 'SHOPIFY', 'TRENDYOL_EFATURAM', 'ETSY'])
  })

  it('reads the invoice provider card in full but keeps any other connection name', () => {
    expect(connectionTitle('TRENDYOL_EFATURAM', 'E-Faturam')).toBe('Trendyol E-Faturam')
    expect(connectionTitle('TRENDYOL_EFATURAM', 'Ravencia Fatura')).toBe('Ravencia Fatura')
    expect(connectionTitle('TRENDYOL', 'E-Faturam')).toBe('E-Faturam')
  })
})
