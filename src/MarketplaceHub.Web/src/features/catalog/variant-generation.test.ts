import { describe, expect, it } from 'vitest'
import { buildSequentialVariantCode, buildSequentialVariantIdentifiers, buildVariantGenerationDefaults, resolveVariantSyncAttributeIds } from './variant-generation'

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

  it('creates matching barcode and SKU values from the model code for every variant', () => {
    expect(buildSequentialVariantIdentifiers('MZ049BOC', 4)).toEqual([
      { barcode: 'MZ049BOC-01', sku: 'MZ049BOC-01' },
      { barcode: 'MZ049BOC-02', sku: 'MZ049BOC-02' },
      { barcode: 'MZ049BOC-03', sku: 'MZ049BOC-03' },
      { barcode: 'MZ049BOC-04', sku: 'MZ049BOC-04' },
    ])
  })

  it('normalizes model codes, expands past two digits, and rejects invalid generation requests', () => {
    expect(buildSequentialVariantCode(' MZ 049BÖC ', 1)).toBe('MZ-049BOC-01')
    expect(buildSequentialVariantCode('MZ049BOC', 100)).toBe('MZ049BOC-100')
    expect(buildSequentialVariantIdentifiers('', 2)).toEqual([])
    expect(buildSequentialVariantIdentifiers('MZ049BOC', 0)).toEqual([])
    expect(buildSequentialVariantIdentifiers('MZ049BOC', 1001)).toEqual([])
  })

  it('produces unique generated identifiers for a large variant set', () => {
    const identifiers = buildSequentialVariantIdentifiers('MZ049BOC', 100)

    expect(new Set(identifiers.map(item => item.barcode)).size).toBe(100)
    expect(new Set(identifiers.map(item => item.sku)).size).toBe(100)
    expect(identifiers.every(item => item.barcode === item.sku)).toBe(true)
  })
})

describe('bulk variant option sync', () => {
  it('syncs every available option group when none is explicitly selected', () => {
    expect(resolveVariantSyncAttributeIds([], [], ['color', 'size'])).toEqual(['color', 'size'])
  })

  it('syncs selected groups while retaining the active unselected axis', () => {
    expect(resolveVariantSyncAttributeIds(['color', 'size'], ['color'], ['color', 'size'])).toEqual(['color', 'size'])
  })

  it('does not duplicate axes when a selected group is already active', () => {
    expect(resolveVariantSyncAttributeIds(['size', 'color'], ['color'], ['color', 'size'])).toEqual(['size', 'color'])
  })
})
