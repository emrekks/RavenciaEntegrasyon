import { describe, expect, it } from 'vitest'
import { exactMatchConfirmMessage, exactMatchResultMessage, productTypeKey, typedProductTypeOption, type ShopifyExactMatchView } from './shopify-product-types'

const view = (matchCount: number, withoutTypeCount: number, ambiguousCount = 0): ShopifyExactMatchView => ({
  matchCount,
  withoutTypeCount,
  ambiguousCount,
  applied: false,
  matches: Array.from({ length: matchCount }, (_, index) => ({ categoryId: `c${index}`, categoryName: index ? `Kategori ${index}` : 'Bluz', productType: index ? `Tür ${index}` : 'Bluz' }))
})

describe('Shopify product types on the category mappings', () => {
  it('compares names in Turkish without case or extra spaces', () => {
    expect(productTypeKey('  İÇ   Giyim ')).toBe('iç giyim')
    expect(productTypeKey('BLUZ')).toBe(productTypeKey('bluz'))
  })

  it('offers the search text as a new product type only when the store does not hold it', () => {
    const types = [{ externalId: 'Bluz' }, { externalId: 'İç Giyim' }]
    expect(typedProductTypeOption('  Yaz Bluzu ', types)).toBe('Yaz Bluzu')
    expect(typedProductTypeOption('bluz', types)).toBeNull()
    expect(typedProductTypeOption('iç  giyim', types)).toBeNull()
    expect(typedProductTypeOption('   ', types)).toBeNull()
    expect(typedProductTypeOption('a'.repeat(256), types)).toBeNull()
    // A store with no product types yet: any name can be typed.
    expect(typedProductTypeOption('Elbise', [])).toBe('Elbise')
  })

  it('asks before mapping, with the count and what is left out', () => {
    expect(exactMatchConfirmMessage(view(0, 4))).toBeNull()
    const message = exactMatchConfirmMessage(view(12, 3))!
    expect(message).toContain('12 panel kategorisi')
    expect(message).toContain('Bluz → Bluz')
    expect(message).toContain('ve 7 kategori daha')
    expect(message).toContain("3 kategorinin Shopify'da aynı adlı türü yok")
    expect(message).toContain('Mevcut eşlemeler değişmez.')
  })

  it('reports the result in one line', () => {
    expect(exactMatchResultMessage(view(12, 3))).toBe("12 kategori eşlendi, 3 kategorinin Shopify'da aynı adlı türü yok.")
    expect(exactMatchResultMessage(view(2, 0, 1))).toBe('2 kategori eşlendi, 1 kategori için birden fazla aynı adlı tür var.')
    expect(exactMatchResultMessage(view(0, 2))).toBe("Eşlenecek aynı adlı kategori bulunamadı, 2 kategorinin Shopify'da aynı adlı türü yok.")
  })
})
