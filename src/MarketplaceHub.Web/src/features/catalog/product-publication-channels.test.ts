import { describe, expect, it } from 'vitest'
import { productPublicationChannelMode } from './product-publication-channels'

describe('product publication channel availability', () => {
  it.each(['TRENDYOL', 'HEPSIBURADA'])('%s supports the guarded marketplace publication flow', platform => {
    expect(productPublicationChannelMode(platform, 'ACTIVE')).toBe('PUBLISH')
  })

  it('keeps Shopify read only until its external-write gate is enabled', () => {
    expect(productPublicationChannelMode('SHOPIFY', 'ACTIVE')).toBe('READ_ONLY')
    expect(productPublicationChannelMode('SHOPIFY', 'ACTIVE', true)).toBe('PUBLISH')
  })

  it('keeps live-only N11 read only until its external-write gate is enabled', () => {
    expect(productPublicationChannelMode('N11', 'ACTIVE')).toBe('READ_ONLY')
    expect(productPublicationChannelMode('N11', 'ACTIVE', true)).toBe('PUBLISH')
    expect(productPublicationChannelMode('N11', 'PASSIVE', true)).toBeNull()
  })

  it('does not show inactive connections as publication channels', () => {
    expect(productPublicationChannelMode('HEPSIBURADA', 'PASSIVE')).toBeNull()
  })
})
