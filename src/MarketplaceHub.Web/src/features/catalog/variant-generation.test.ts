import { describe, expect, it } from 'vitest'
import { applyGeneratedVariantCodes, applyVariantCodesByKey, buildMissingVariantCodes, buildSequentialVariantCode, buildSequentialVariantCodes, buildVariantGenerationDefaults, resolveVariantSyncAttributeIds, variantCodeChangeConfirmText } from './variant-generation'

describe('variant generation defaults', () => {
  it('leaves the stock code and the barcode of a new variant empty when automatic barcodes are off: no code is made up', () => {
    // Live 2026-11-01: new rows of MZ021Y-CD got made-up stock codes such as MZ021Y-CD13-8.
    expect(buildVariantGenerationDefaults({ modelCode: 'MZ021Y-CD', sequence: 8, automaticBarcodes: false, fallbackSalePrice: 1, fallbackListPrice: 1 })).toMatchObject({ sku: '', barcode: '' })
  })

  it('builds barcodes from the model code without Turkish letters or spaces', () => {
    expect(buildSequentialVariantCode('Gül Çiçek 01', 3)).toBe('GUL-CICEK-01-03')
  })

  it('uses the model code for sequential automatic barcodes and leaves SKU and prices empty', () => {
    const first = buildVariantGenerationDefaults({ modelCode: 'mz49bc', sequence: 1, automaticBarcodes: true, fallbackSalePrice: 549.9, fallbackListPrice: 699.9 })
    const second = buildVariantGenerationDefaults({ modelCode: 'mz49bc', sequence: 2, automaticBarcodes: true, fallbackSalePrice: 549.9, fallbackListPrice: 699.9 })

    expect(first).toEqual({ sku: '', barcode: 'MZ49BC-01', salePrice: 0, listPrice: 0 })
    expect(second).toEqual({ sku: '', barcode: 'MZ49BC-02', salePrice: 0, listPrice: 0 })
  })

  it('keeps the price defaults and empty codes when automatic barcodes are off', () => {
    expect(buildVariantGenerationDefaults({ modelCode: 'mz49bc', sequence: 3, automaticBarcodes: false, fallbackSalePrice: 549.9, fallbackListPrice: 0 })).toEqual({
      sku: '',
      barcode: '',
      salePrice: 549.9,
      listPrice: 549.9,
    })
  })

  it('keeps generated barcodes within the 40-character marketplace limit', () => {
    const result = buildVariantGenerationDefaults({ modelCode: 'M'.repeat(60), sequence: 1, automaticBarcodes: true, fallbackSalePrice: 1, fallbackListPrice: 1 })

    expect(result.barcode).toHaveLength(40)
    expect(result.barcode.endsWith('-01')).toBe(true)
  })

  it('generates model-based codes independently for a selected identifier column', () => {
    expect(buildSequentialVariantCodes('MZ051BYC', 3)).toEqual(['MZ051BYC-01', 'MZ051BYC-02', 'MZ051BYC-03'])
  })

  it('updates only the barcode field when barcodes are generated', () => {
    expect(applyGeneratedVariantCodes([{ barcode: 'old-barcode', sku: 'keep-sku' }], 'barcode', ['MZ051BYC-01'])).toEqual([
      { barcode: 'MZ051BYC-01', sku: 'keep-sku' },
    ])
  })

  it('replaces copied variant barcodes with the edited model-code range before save and preserves their SKUs', () => {
    const rows = Array.from({ length: 35 }, (_, index) => ({ barcode: `MZ051BYC-${String(index + 1).padStart(2, '0')}`, sku: `SKU-${index + 1}` }))
    const codes = buildSequentialVariantCodes('MZ052BOD', rows.length)
    const updated = applyGeneratedVariantCodes(rows, 'barcode', codes)

    expect(codes).toHaveLength(35)
    expect(updated).toHaveLength(35)
    expect(updated.map(row => row.barcode)).toEqual(codes)
    expect(updated[0]).toEqual({ barcode: 'MZ052BOD-01', sku: 'SKU-1' })
    expect(updated.at(-1)).toEqual({ barcode: 'MZ052BOD-35', sku: 'SKU-35' })
  })

  it('updates only the SKU field when stock codes are generated', () => {
    expect(applyGeneratedVariantCodes([{ barcode: 'keep-barcode', sku: 'old-sku' }], 'sku', ['MZ051BYC-01'])).toEqual([
      { barcode: 'keep-barcode', sku: 'MZ051BYC-01' },
    ])
  })

  it('normalizes model codes, expands past two digits, and rejects invalid generation requests', () => {
    expect(buildSequentialVariantCode(' MZ 049BÖC ', 1)).toBe('MZ-049BOC-01')
    expect(buildSequentialVariantCode('MZ049BOC', 100)).toBe('MZ049BOC-100')
    expect(buildSequentialVariantCodes('', 2)).toEqual([])
    expect(buildSequentialVariantCodes('MZ049BOC', 0)).toEqual([])
    expect(buildSequentialVariantCodes('MZ049BOC', 1001)).toEqual([])
  })

  it('produces unique generated codes for a large variant set', () => {
    expect(new Set(buildSequentialVariantCodes('MZ049BOC', 100)).size).toBe(100)
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

describe('column-menu variant codes', () => {
  const rows = [
    { key: 'a', barcode: 'MZ021Y-CD7', sku: 'MZ021Y-CD7' },
    { key: 'b', barcode: '', sku: '' },
    { key: 'c', barcode: 'MZ021Y-CD-03', sku: '' },
    { key: 'd', barcode: '', sku: 'KEEP' },
  ]

  it('generates codes only for the rows whose column is empty, skipping codes another row holds', () => {
    expect(buildMissingVariantCodes(rows, 'barcode', 'MZ021Y-CD')).toEqual({ b: 'MZ021Y-CD-02', d: 'MZ021Y-CD-04' })
    expect(buildMissingVariantCodes([{ key: 'x', barcode: 'M-01', sku: '' }, { key: 'y', barcode: '', sku: '' }, { key: 'z', barcode: 'M-02', sku: '' }], 'barcode', 'M')).toEqual({ y: 'M-04' })
    expect(buildMissingVariantCodes(rows, 'sku', 'MZ021Y-CD')).toEqual({ b: 'MZ021Y-CD-02', c: 'MZ021Y-CD-03' })
    expect(buildMissingVariantCodes(rows, 'sku', ' ')).toEqual({})
  })

  it('writes one column of the keyed rows and leaves the rest alone', () => {
    expect(applyVariantCodesByKey(rows, 'sku', { b: 'NEW', a: '' })).toEqual([
      { key: 'a', barcode: 'MZ021Y-CD7', sku: '' },
      { key: 'b', barcode: '', sku: 'NEW' },
      rows[2],
      rows[3],
    ])
  })

  it('words the confirmation for each choice', () => {
    expect(variantCodeChangeConfirmText({ target: 'sku', mode: 'clear', codesByKey: { a: '', d: '' } })).toMatchObject({ title: 'Tüm Stok Kodlarını Temizle', range: '', action: 'Stok Kodlarını Temizle' })
    expect(variantCodeChangeConfirmText({ target: 'barcode', mode: 'missing', codesByKey: { b: 'M-02', d: 'M-04' } })).toMatchObject({ description: 'Barkodu eksik 2 varyanta kod üretilecek; dolu kodlara dokunulmaz.', range: 'M-02 – M-04' })
  })
})
