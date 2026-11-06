import { describe, expect, it } from 'vitest'
import { mergeGeneratedVariantRows } from './variant-generation'
import { variantOptionSetKey } from './variant-option-matching'

type Row = { key: string; optionSignature: string; sku: string; barcode: string; salePrice: number; listPrice: number }

const row = (key: string, optionSignature: string, extra: Partial<Row> = {}): Row => ({ key, optionSignature, sku: '', barcode: '', salePrice: 725, listPrice: 725, ...extra })
const byKey = (rows: Row[]) => [...rows].sort((left, right) => left.optionSignature.localeCompare(right.optionSignature, 'tr'))

// Live 2026-11-06 (MZ028IH): the nine saved colours are pre-selected; picking Zümrüt and pressing Ürünleri Ekle must add
// only the Zümrüt row and leave the nine saved rows (codes, prices) as they are.
const saved = ['Bordo', 'Füme', 'Gri', 'İndigo', 'Lacivert', 'Mor', 'Pembe', 'SİYAH', 'Turkuaz']
  .map((color, index) => row(`saved-${index}`, `Renk: ${color} | Beden: Standart`, { sku: `MZ028IH-${index + 1}`, barcode: `MZ028IH-${String(index + 1).padStart(2, '0')}`, salePrice: 700 + index }))
const generated = [...saved.map(item => item.optionSignature.replace('SİYAH', 'Siyah')), 'Renk: Zümrüt | Beden: Standart']
  .map((signature, index) => row(`new-${index}`, signature.replace(/Renk: (.*) \| Beden: (.*)/, 'Renk:$1_Beden:$2')))

describe('adding variant combinations', () => {
  it('reads a saved row and a generated combination of the same options as one', () => {
    expect(variantOptionSetKey('Renk: SİYAH | Beden: Standart')).toBe(variantOptionSetKey('Beden:standart_Renk:Siyah'))
    expect(variantOptionSetKey('Color: Siyah | Beden: L')).toBe(variantOptionSetKey('Renk:siyah_Beden:L'))
    expect(variantOptionSetKey('Renk: Siyah | Beden: L')).not.toBe(variantOptionSetKey('Renk: Zümrüt | Beden: L'))
  })

  it('adds only the missing colour on a saved product and keeps the saved rows untouched', () => {
    for (const automaticBarcodes of [false, true]) {
      const merged = mergeGeneratedVariantRows(saved, generated, { key: variantOptionSetKey, sort: byKey, keepExisting: true, automaticBarcodes, modelCode: 'MZ028IH', fallbackSalePrice: 725, fallbackListPrice: 725 })
      expect(merged).toHaveLength(10)
      for (const item of saved) expect(merged).toContainEqual(item)
      const added = merged.find(item => item.optionSignature.includes('Zümrüt'))!
      expect(added.key).toBe('new-9')
      expect(added.barcode).toBe(automaticBarcodes ? 'MZ028IH-10' : '')
    }
  })

  it('never repeats a combination generated twice', () => {
    const merged = mergeGeneratedVariantRows([], [row('a', 'Renk:Zümrüt'), row('b', 'Renk:ZÜMRÜT')], { key: variantOptionSetKey, sort: byKey, keepExisting: true, automaticBarcodes: false, modelCode: '', fallbackSalePrice: 0, fallbackListPrice: 0 })
    expect(merged.map(item => item.key)).toEqual(['a'])
  })

  it('numbers every row again on a new product with automatic barcodes', () => {
    const merged = mergeGeneratedVariantRows([row('x', 'Renk:Mor', { barcode: 'OLD' })], [row('y', 'Renk:Bordo')], { key: variantOptionSetKey, sort: byKey, keepExisting: false, automaticBarcodes: true, modelCode: 'MC', fallbackSalePrice: 0, fallbackListPrice: 0 })
    expect(merged.map(item => [item.key, item.barcode])).toEqual([['y', 'MC-01'], ['x', 'MC-02']])
  })
})
