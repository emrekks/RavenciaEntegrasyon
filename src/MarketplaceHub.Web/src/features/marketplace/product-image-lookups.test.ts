import { describe, expect, it } from 'vitest'
import { productImageFallbackUrls } from './product-image-lookups'

describe('productImageFallbackUrls', () => {
  it('tries the marketplace barcode before the merchant SKU and keeps the lookup connection scoped', () => {
    const urls = productImageFallbackUrls([' 0000MZ029YD22 ', 'HBCV0000D5OXUG'], 'connection-1')
    const lookupValues = urls.map(value => new URL(value, 'https://panel.ravencia.test').searchParams.get('barcode'))

    expect(lookupValues).toEqual(['0000MZ029YD22', 'HBCV0000D5OXUG'])
    expect(urls.every(value => value.includes('connectionId=connection-1'))).toBe(true)
  })

  it('drops empty keys and duplicate lookups', () => {
    expect(productImageFallbackUrls([null, '', 'A-1', ' A-1 '])).toHaveLength(1)
  })
})
