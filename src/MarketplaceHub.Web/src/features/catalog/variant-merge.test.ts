import { describe, expect, it } from 'vitest'
import { buildVariantGenerationDefaults, mergeGeneratedVariantRows } from './variant-generation'
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

  // Owner, 10 Nov 2026: a new variant's first price is the card's Liste/Satış (section 2); rows already there keep theirs.
  it('gives only the new rows the card prices, with or without automatic barcodes', () => {
    const cardPrices = buildVariantGenerationDefaults({ modelCode: 'MC', sequence: 1, automaticBarcodes: false, fallbackSalePrice: 549.9, fallbackListPrice: 699.9 })
    const existing = row('x', 'Renk:Mor', { salePrice: 400, listPrice: 450 })
    const added = row('y', 'Renk:Bordo', { salePrice: cardPrices.salePrice, listPrice: cardPrices.listPrice })
    for (const keepExisting of [false, true]) for (const automaticBarcodes of [false, true]) {
      const merged = mergeGeneratedVariantRows([existing], [row('x2', 'Renk:Mor'), added], { key: variantOptionSetKey, sort: byKey, keepExisting, automaticBarcodes, modelCode: 'MC', fallbackSalePrice: 549.9, fallbackListPrice: 699.9 })
      expect(merged.find(item => item.optionSignature === 'Renk:Mor')).toMatchObject({ key: 'x', salePrice: 400, listPrice: 450 })
      expect(merged.find(item => item.optionSignature === 'Renk:Bordo')).toMatchObject({ key: 'y', salePrice: 549.9, listPrice: 699.9 })
    }
  })

  it('numbers every row again on a new product with automatic barcodes', () => {
    const merged = mergeGeneratedVariantRows([row('x', 'Renk:Mor', { barcode: 'OLD' })], [row('y', 'Renk:Bordo')], { key: variantOptionSetKey, sort: byKey, keepExisting: false, automaticBarcodes: true, modelCode: 'MC', fallbackSalePrice: 0, fallbackListPrice: 0 })
    expect(merged.map(item => [item.key, item.barcode])).toEqual([['y', 'MC-01'], ['x', 'MC-02']])
  })
})

// Owner, 11.11.2026 (RYP003T): 2XL rows added to a saved family came with stock 3; a new row starts with no stock and the
// saved rows keep theirs, with automatic barcodes on or off.
describe('the stock of an added variant row', () => {
  type StockRow = Row & { stock: number }
  const savedRows: StockRow[] = [{ ...row('s1', 'Beden:L_Renk:Haki', { sku: 'RYP003T01', barcode: 'RYP003T01' }), stock: 3 }, { ...row('s2', 'Beden:XL_Renk:Haki', { sku: 'RYP003T02', barcode: 'RYP003T02' }), stock: 5 }]
  const generatedRows: StockRow[] = ['Beden:L_Renk:Haki', 'Beden:XL_Renk:Haki', 'Beden:2XL_Renk:Haki'].map((signature, index) => ({ ...row(`g${index}`, signature, buildVariantGenerationDefaults({ modelCode: 'RYP003T', sequence: index + 1, automaticBarcodes: false, fallbackSalePrice: 725, fallbackListPrice: 900 })), stock: buildVariantGenerationDefaults({ modelCode: 'RYP003T', sequence: index + 1, automaticBarcodes: false, fallbackSalePrice: 725, fallbackListPrice: 900 }).stock }))
  it('starts empty, while the saved rows keep theirs', () => {
    for (const automaticBarcodes of [false, true]) {
      const merged = mergeGeneratedVariantRows<StockRow>(savedRows, generatedRows, { key: variantOptionSetKey, sort: rows => [...rows].sort((left, right) => left.optionSignature.localeCompare(right.optionSignature, 'tr')), keepExisting: true, automaticBarcodes, modelCode: 'RYP003T', fallbackSalePrice: 725, fallbackListPrice: 900 })
      const added = merged.find(item => item.optionSignature.includes('2XL'))!
      expect(added).toMatchObject({ stock: 0, salePrice: 725, listPrice: 900 })
      expect(merged.filter(item => item.key.startsWith('s')).map(item => item.stock)).toEqual([3, 5])
    }
  })
})
