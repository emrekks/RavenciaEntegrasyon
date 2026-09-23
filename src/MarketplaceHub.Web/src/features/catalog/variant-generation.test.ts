import { describe, expect, it } from 'vitest'
import { buildVariantGenerationDefaults } from './variant-generation'

describe('variant generation defaults', () => {
  it('uses the model code for sequential automatic barcodes and leaves SKU and prices empty', () => {
    const first = buildVariantGenerationDefaults({ baseSku: 'STOCK', modelCode: 'mz49bc', sequence: 1, automaticBarcodes: true, fallbackSalePrice: 549.9, fallbackListPrice: 699.9 })
    const second = buildVariantGenerationDefaults({ baseSku: 'STOCK', modelCode: 'mz49bc', sequence: 2, automaticBarcodes: true, fallbackSalePrice: 549.9, fallbackListPrice: 699.9 })

    expect(first).toEqual({ sku: '', barcode: 'MZ49BC-01', salePrice: 0, listPrice: 0 })
    expect(second).toEqual({ sku: '', barcode: 'MZ49BC-02', salePrice: 0, listPrice: 0 })
  })

  it('preserves the existing generated SKU and price defaults when automatic barcodes are off', () => {
    expect(buildVariantGenerationDefaults({ baseSku: 'MZ 49BC', modelCode: 'mz49bc', sequence: 3, automaticBarcodes: false, fallbackSalePrice: 549.9, fallbackListPrice: 0 })).toEqual({
      sku: 'MZ-49BC-3',
      barcode: '',
      salePrice: 549.9,
      listPrice: 549.9,
    })
  })

  it('keeps generated barcodes within the 40-character marketplace limit', () => {
    const result = buildVariantGenerationDefaults({ baseSku: '', modelCode: 'M'.repeat(60), sequence: 1, automaticBarcodes: true, fallbackSalePrice: 1, fallbackListPrice: 1 })

    expect(result.barcode).toHaveLength(40)
    expect(result.barcode.endsWith('-01')).toBe(true)
  })
})
