import { describe, expect, it } from 'vitest'
import { productCopyIdentifierConflicts } from './product-copy-identifiers'

describe('product copy identifier conflicts', () => {
  const source = [
    { sku: 'MZ049BOC-01', barcode: '86900001' },
    { sku: 'MZ049BOC-02', barcode: '86900002' }
  ]

  it('flags unchanged source barcodes and SKUs regardless of letter case or whitespace', () => {
    expect(productCopyIdentifierConflicts(source, [
      { sku: ' mz049boc-01 ', barcode: ' 86900001 ' },
      { sku: 'NEW-SKU', barcode: 'NEW-BARCODE' }
    ])).toEqual({ skus: true, barcodes: true })
  })

  it('allows the draft identifiers after all source values are changed', () => {
    expect(productCopyIdentifierConflicts(source, [
      { sku: 'NEW-SKU-01', barcode: '86910001' },
      { sku: 'NEW-SKU-02', barcode: '86910002' }
    ])).toEqual({ skus: false, barcodes: false })
  })

  it('ignores empty barcode values', () => {
    expect(productCopyIdentifierConflicts(source, [{ sku: 'NEW-SKU', barcode: null }])).toEqual({ skus: false, barcodes: false })
  })
})
