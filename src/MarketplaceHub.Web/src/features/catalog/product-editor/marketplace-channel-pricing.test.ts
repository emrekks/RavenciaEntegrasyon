import { describe, expect, it } from 'vitest'
import { channelPricingInputs, copiedChannelPricing, emptyChannelPricingDraft } from './marketplace-channel-pricing'

describe('marketplace channel pricing', () => {
  it('shows 0 in both inputs while the switch is off', () => {
    expect(channelPricingInputs(undefined)).toEqual({ listPrice: '0', salePrice: '0' })
    expect(emptyChannelPricingDraft).toEqual({ listPrice: '0', salePrice: '0' })
  })

  it('shows the special price once one is set', () => {
    expect(channelPricingInputs({ listPrice: '799.9', salePrice: '649.9' })).toEqual({ listPrice: '799.9', salePrice: '649.9' })
  })

  it('leaves every switch off on a copy that sold at the main price', () => {
    const platforms = [
      { platform: 'Trendyol', platformCode: 'TRENDYOL', isLinked: true, connectionId: 'ty', listPrice: 699.9, salePrice: 549.9 },
      { platform: 'Shopify', platformCode: 'SHOPIFY', isLinked: true, connectionId: 'sh', listPrice: null, salePrice: null }
    ]
    expect(copiedChannelPricing(platforms, 699.9, 549.9)).toEqual({})
  })

  it('keeps a saved special price on a copy', () => {
    const platforms = [
      { platform: 'Hepsiburada', platformCode: 'HEPSIBURADA', isLinked: true, connectionId: 'hb', listPrice: 749.9, salePrice: 599.9 },
      { platform: 'N11', platformCode: 'N11', isLinked: true, connectionId: null, listPrice: 1, salePrice: 1 }
    ]
    expect(copiedChannelPricing(platforms, 699.9, 549.9)).toEqual({ hb: { listPrice: '749.9', salePrice: '599.9' } })
  })
})
