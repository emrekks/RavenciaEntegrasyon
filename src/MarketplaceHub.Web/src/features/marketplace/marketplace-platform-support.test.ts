import { describe, expect, it } from 'vitest'
import { isActiveMarketplaceConnection, isMarketplacePlatformSelected, isOrderMarketplacePlatform, marketplacePlatformOptions, supportsSyncPolicy, supportsSyncPolicyManagement, syncPolicyIntervalChoices } from './marketplace-platform-support'

describe('Hepsiburada marketplace workspace support', () => {
  it('includes Hepsiburada in order platform filters', () => {
    expect(isOrderMarketplacePlatform('HEPSIBURADA')).toBe(true)
    expect(isOrderMarketplacePlatform(' hepsiburada ')).toBe(true)
    expect(isOrderMarketplacePlatform('TRENDYOL')).toBe(true)
    expect(isOrderMarketplacePlatform('TRENDYOL_EFATURAM')).toBe(false)
  })

  it('keeps supported platforms available in filters before their data is loaded', () => {
    const options = marketplacePlatformOptions([])

    expect(options.map(option => option.value)).toEqual(['HEPSIBURADA', 'SHOPIFY', 'TRENDYOL'])
    expect(options.find(option => option.value === 'HEPSIBURADA')?.label).toBe('Hepsiburada')
  })

  it('does not treat the Trendyol e-invoice provider as an order platform', () => {
    const options = marketplacePlatformOptions([{ platformCode: 'TRENDYOL_EFATURAM', displayName: 'Stage E-Faturam' }])

    expect(options.some(option => option.value === 'TRENDYOL_EFATURAM')).toBe(false)
  })

  it('uses connection labels and compares normalized platform codes', () => {
    const options = marketplacePlatformOptions([{ platformCode: 'hepsiburada', displayName: 'Ravencia HB' }])

    expect(options.find(option => option.value === 'HEPSIBURADA')?.label).toBe('Ravencia HB')
    expect(isMarketplacePlatformSelected(['HEPSIBURADA'], 'hepsiburada')).toBe(true)
    expect(isMarketplacePlatformSelected(['SHOPIFY'], 'hepsiburada')).toBe(false)
  })

  it('offers verified Hepsiburada connections in the order sync source list', () => {
    expect(isActiveMarketplaceConnection({ platformCode: ' hepsiburada ', status: ' verified ' })).toBe(true)
    expect(isActiveMarketplaceConnection({ platformCode: 'HEPSIBURADA', status: 'DISCONNECTED' })).toBe(false)
    expect(isActiveMarketplaceConnection({ platformCode: 'TRENDYOL_EFATURAM', status: 'VERIFIED' })).toBe(false)
  })

  it('exposes read-only Hepsiburada order lifecycle synchronization', () => {
    expect(supportsSyncPolicyManagement('HEPSIBURADA')).toBe(true)
    expect(['ORDERS', 'ORDER_RECOVERY', 'ORDER_LIFECYCLE', 'ORDER_INVOICE_RECONCILIATION', 'RETURNS'].every(resource => supportsSyncPolicy('HEPSIBURADA', resource))).toBe(true)
    expect(supportsSyncPolicy('HEPSIBURADA', 'PRICE_WRITE')).toBe(false)
  })

  it('does not offer immediate runs for read-only sync policies', () => {
    const intervals: Array<[number, string]> = [[0, 'Anında'], [30, '30 saniye'], [60, '1 dakika']]

    expect(syncPolicyIntervalChoices(intervals, false)).toEqual([[30, '30 saniye'], [60, '1 dakika']])
    expect(syncPolicyIntervalChoices(intervals, true)).toEqual(intervals)
  })
})
