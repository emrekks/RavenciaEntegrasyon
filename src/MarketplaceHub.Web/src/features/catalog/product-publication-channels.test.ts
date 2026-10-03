import { describe, expect, it } from 'vitest'
import { productPublicationChannelMode } from './product-publication-channels'

describe('product publication channel availability', () => {
  it.each(['TRENDYOL', 'HEPSIBURADA'])('%s supports the guarded marketplace publication flow', platform => {
    expect(productPublicationChannelMode(platform, 'ACTIVE')).toBe('PUBLISH')
  })

  it('shows Shopify as read only while product write support is unavailable', () => {
    expect(productPublicationChannelMode('SHOPIFY', 'ACTIVE')).toBe('READ_ONLY')
  })

  it('does not show inactive connections as publication channels', () => {
    expect(productPublicationChannelMode('HEPSIBURADA', 'PASSIVE')).toBeNull()
  })
})
