import { describe, expect, it } from 'vitest'
import { productImportConnection, productImportIdentityLabel, singleProductLookupLabel } from './product-import-platforms'

describe('Hepsiburada product import support', () => {
  it('allows active and verified Hepsiburada connections for local catalog reads', () => {
    expect(productImportConnection({ platformCode: 'HEPSIBURADA', status: 'ACTIVE' })).toBe(true)
    expect(productImportConnection({ platformCode: 'hepsiburada', status: 'verified' })).toBe(true)
    expect(productImportConnection({ platformCode: 'HEPSIBURADA', status: 'DISABLED' })).toBe(false)
  })

  it('uses Hepsiburada identity labels for bulk and single product lookup', () => {
    expect(productImportIdentityLabel(['HEPSIBURADA'])).toBe('merchant SKU')
    expect(productImportIdentityLabel(['HEPSIBURADA', 'TRENDYOL'])).toBe('ürün kimliği')
    expect(singleProductLookupLabel('HEPSIBURADA')).toBe('Hepsiburada ürün ID’si')
  })
})
