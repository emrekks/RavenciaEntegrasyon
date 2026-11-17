import { describe, expect, it } from 'vitest'
import { descriptionChangeText, readTransferPreview } from './ProductTransferDialog'

const row = (outcome: string, costChange: string | null) => ({
  rowNumber: 2, sku: 'S1', barcode: null, variantId: 'v1', productTitle: 'Ürün', variantLabel: null, oldCost: 1, newCost: 2, costChange,
  oldDesi: null, newDesi: null, desiChange: null, oldStock: null, newStock: null, stockChange: null, prices: [], outcome, message: null,
  newTitle: null, titleChange: null, oldBarcode: null, newBarcode: null, barcodeChange: null, oldModelCode: null, newModelCode: null, modelCodeChange: null,
  oldPanelSalePrice: null, newPanelSalePrice: null, oldPanelListPrice: null, newPanelListPrice: null, panelPriceChange: null,
  oldVatRate: null, newVatRate: null, vatChange: null, oldBrand: null, newBrand: null, brandChange: null, descriptionChange: null as string | null, newDescriptionLength: null as number | null
})
const preview = {
  fileName: 'f.xlsx', availableFields: ['COST', 'LATER'], fields: ['COST'], stores: [], totalRows: 2, updates: 1, costUpdates: 1, costClears: 0,
  desiUpdates: 0, desiClears: 0, stockUpdates: 0, priceUpdates: 0, unchanged: 0, skipped: 0, unmatched: 0, ambiguous: 0, errors: 0,
  rows: [row('UPDATE', 'SET'), row('LATER', 'RENAME')], rowsTruncated: false, changes: [], productChanges: null,
  titleUpdates: 0, barcodeUpdates: 0, modelCodeUpdates: 0, modelCodeClears: 0, panelPriceUpdates: 0, vatUpdates: 0, brandUpdates: 0, descriptionUpdates: 0
}

describe('the product transfer preview as the dialog reads it', () => {
  it('drops field codes it cannot name and shows an unknown outcome as an error, an unknown change as a change', () => {
    const read = readTransferPreview(preview)
    expect(read.availableFields).toEqual(['COST'])
    expect(read.rows[0]).toMatchObject({ outcome: 'UPDATE', costChange: 'SET', message: null })
    expect(read.rows[1]).toMatchObject({ outcome: 'ERROR', costChange: 'SET', message: 'Bilinmeyen sonuç: LATER' })
  })
})

describe('the description column of the preview', () => {
  it('reads the description as a field and says a changed one by its length only, never its HTML', () => {
    const read = readTransferPreview({ ...preview, availableFields: ['DESCRIPTION', 'COST'], fields: ['DESCRIPTION'], rows: [{ ...row('UPDATE', null), descriptionChange: 'SET', newDescriptionLength: 1250 }] })
    expect(read.availableFields).toEqual(['DESCRIPTION', 'COST'])
    expect(read.rows[0].descriptionChange).toBe('SET')
    expect(descriptionChangeText(read.rows[0])).toBe('Açıklama Değişti (1.250 Karakter)')
    expect(descriptionChangeText({ descriptionChange: null, newDescriptionLength: null })).toBeNull()
  })
})
