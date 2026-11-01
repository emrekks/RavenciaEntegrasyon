import { describe, expect, it } from 'vitest'
import { productImportConnection, singleProductLookupLabel } from './product-import-platforms'

describe('Hepsiburada product import support', () => {
  it('allows active and verified Hepsiburada connections for local catalog reads', () => {
    expect(productImportConnection({ platformCode: 'HEPSIBURADA', status: 'ACTIVE' })).toBe(true)
    expect(productImportConnection({ platformCode: 'hepsiburada', status: 'verified' })).toBe(true)
    expect(productImportConnection({ platformCode: 'HEPSIBURADA', status: 'DISABLED' })).toBe(false)
  })

  it('uses the Hepsiburada identity label for single product lookup', () => {
    expect(singleProductLookupLabel('HEPSIBURADA')).toBe('Hepsiburada ürün ID’si')
  })
})
