import { describe, expect, it } from 'vitest'
import { marketplaceProductSearchUrl } from './marketplace-product-link'

describe('marketplaceProductSearchUrl', () => {
  it('searches the current marketplace by barcode before other product identifiers', () => {
    expect(marketplaceProductSearchUrl('TRENDYOL', '86900001', 'SKU-1', 'MODEL-1', 'Bluz'))
      .toBe('https://www.trendyol.com/sr?q=86900001')
  })

  it('supports Hepsiburada and falls back to the product name', () => {
    expect(marketplaceProductSearchUrl('HEPSIBURADA', null, null, null, 'Kadın bluz'))
      .toBe('https://www.hepsiburada.com/ara?q=Kad%C4%B1n%20bluz')
  })

  it('does not create links for unknown platforms or missing product data', () => {
    expect(marketplaceProductSearchUrl('SHOPIFY', null, 'SKU-1')).toBeNull()
    expect(marketplaceProductSearchUrl('TRENDYOL')).toBeNull()
  })
})
