import { describe, expect, it } from 'vitest'
import { ApiRequestError } from '../../shared/api'
import { editorDataStamp, editorFieldErrors, editorSeedKey, editorSnapshot, editorStockUpdates, editorVariantCostUpdate, editorVariantDesiUpdate, editorVat, editorVatRateIssue, VAT_RATE_REQUIRED_MESSAGE } from './product-editor-state'

describe('product editor state', () => {
  it('builds one seed key for the seed and for a version bump made by the editor itself', () => {
    expect(editorSeedKey('edit', 'p1', 4)).toBe('edit:p1:4')
    expect(editorSeedKey('edit', 'p1', 4)).not.toBe(editorSeedKey('copy', 'p1', 4))
    expect(editorSeedKey('edit', 'p1', 4, 'abc')).toBe('edit:p1:4:abc')
    expect(editorSeedKey('edit', 'p1', 4, 'abc').startsWith('edit:p1:')).toBe(true)
  })

  it('stamps what the product version misses: a variant price, its stock and its channel offers', () => {
    const base = [{ id: 'v1', version: 3, inventoryVersion: 5, onHand: 4, platformStatuses: [{ connectionId: 'c1', offerId: 'o1', offerVersion: 2 }, { connectionId: 'c2', offerId: null, offerVersion: null }] }]
    const stamp = editorDataStamp(base)
    expect(editorDataStamp(base)).toBe(stamp)
    expect(editorDataStamp([{ ...base[0], version: 4 }])).not.toBe(stamp)
    expect(editorDataStamp([{ ...base[0], onHand: 5, inventoryVersion: 6 }])).not.toBe(stamp)
    expect(editorDataStamp([{ ...base[0], platformStatuses: [{ connectionId: 'c1', offerId: 'o1', offerVersion: 3 }, base[0].platformStatuses[1]] }])).not.toBe(stamp)
    expect(editorDataStamp([{ ...base[0], platformStatuses: [base[0].platformStatuses[0], { connectionId: 'c2', offerId: 'o2', offerVersion: 1 }] }])).not.toBe(stamp)
  })

  it('sends stock only for edited rows, against the stock loaded into the form', () => {
    const updates = editorStockUpdates([
      { key: 'v1', sku: 'A-1', stock: 5, loadedStock: 5 },
      { key: 'v2', sku: 'a-2', stock: 9, loadedStock: 4 },
      { key: 'new-row', sku: 'A-3', stock: 3 },
      { key: 'new-zero', sku: 'A-4', stock: 0 }
    ], [{ id: 'v1', sku: 'A-1' }, { id: 'v2', sku: 'A-2' }, { id: 'v3', sku: 'A-3' }, { id: 'v4', sku: 'A-4' }])
    expect(updates).toEqual([
      { variantId: 'v2', sku: 'A-2', onHand: 9, expectedOnHand: 4 },
      { variantId: 'v3', sku: 'A-3', onHand: 3, expectedOnHand: 0 }
    ])
  })

  it('sends a changed cost with the cost it was loaded with, and no cost for an untouched or cleared row', () => {
    // The server compares loadedCostPrice with the stored cost and refuses a save over a cost set meanwhile.
    expect(editorVariantCostUpdate({ costPrice: 45, loadedCostPrice: 40 })).toEqual({ costPrice: 45, loadedCostPrice: 40 })
    expect(editorVariantCostUpdate({ costPrice: 30, loadedCostPrice: null })).toEqual({ costPrice: 30, loadedCostPrice: null })
    expect(editorVariantCostUpdate({ costPrice: 40, loadedCostPrice: 40 })).toEqual({ costPrice: null })
    expect(editorVariantCostUpdate({ costPrice: null, loadedCostPrice: 40 })).toEqual({ costPrice: null })
    expect(JSON.parse(JSON.stringify(editorVariantCostUpdate({ costPrice: 12.5, loadedCostPrice: null })))).toEqual({ costPrice: 12.5, loadedCostPrice: null })
  })

  it('sends the desi only when the user changed it, with each variant\'s stored desi as loaded', () => {
    expect(editorVariantDesiUpdate(3, 2, 2)).toEqual({ desi: 3, loadedDesi: 2 })
    expect(editorVariantDesiUpdate(3, 2, undefined)).toEqual({ desi: 3, loadedDesi: null })
    // Untouched: a desi set per variant (e.g. by the products import) is left as it is.
    expect(editorVariantDesiUpdate(2, 2, 5)).toEqual({})
    expect(editorVariantDesiUpdate(0, 2, 2)).toEqual({})
  })

  it('ignores platform prices when deciding whether the form has unsaved edits', () => {
    const base = { form: { title: 'T' }, attributeSelections: {}, attributeTextValues: {}, familyMediaOrder: [], pendingMediaFileCount: 0, removedVariantIds: [] }
    const row = { key: 'v1', optionSignature: 'Renk: Mavi', options: { Renk: 'Mavi' }, attributeValueIds: {}, sku: 'A', barcode: '', stock: 1, salePrice: 2, listPrice: 3, costPrice: 0, mediaRefs: [] }
    const before = editorSnapshot({ ...base, variantRows: [row] })
    expect(editorSnapshot({ ...base, variantRows: [{ ...row, platformStatuses: [{ offerVersion: 2 }] } as typeof row] })).toBe(before)
    expect(editorSnapshot({ ...base, variantRows: [{ ...row, stock: 2 }] })).not.toBe(before)
  })

  it('maps server field errors onto editor fields', () => {
    const error = new ApiRequestError('Doğrulama hatası', 422, 'VALIDATION_FAILED', { title: ['Başlık zorunludur.'], variantUpdates: ['Stok kodları tekrarlı.'], 'variants[0].sku': ['SKU boş.'], categoryId: ['Kategori yaprak olmalı.'], unknown: ['x'] })
    expect(editorFieldErrors(error)).toEqual({ title: 'Başlık zorunludur.', variants: 'Stok kodları tekrarlı. SKU boş.', categoryId: 'Kategori yaprak olmalı.' })
    expect(editorFieldErrors(new Error('x'))).toEqual({})
  })

  it('shows the server KDV refusal under KDV Oranı', () => {
    const error = new ApiRequestError('Doğrulama hatası', 422, 'VALIDATION_FAILED', { vatRate: ['KDV oranı zorunludur.'] })
    expect(editorFieldErrors(error)).toEqual({ vatRate: 'KDV oranı zorunludur.' })
  })

  it('opens KDV from the card, then the primary offer, and empty when neither knows it', () => {
    expect(editorVat({ vatRate: 20, vatInclusion: 'EXCLUDED' }, { vatRate: 10, vatInclusion: 'INCLUDED' })).toEqual({ vatRate: '20', vatIncluded: 'EXCLUDED' })
    expect(editorVat({ vatRate: null, vatInclusion: null }, { vatRate: 1, vatInclusion: 'INCLUDED' })).toEqual({ vatRate: '1', vatIncluded: 'INCLUDED' })
    expect(editorVat({ vatRate: 0, vatInclusion: 'INCLUDED' }, { vatRate: 10, vatInclusion: 'INCLUDED' })).toEqual({ vatRate: '0', vatIncluded: 'INCLUDED' })
    expect(editorVat({ vatRate: null }, { vatRate: null })).toEqual({ vatRate: '10', vatIncluded: 'INCLUDED' })
    expect(editorVat(undefined, undefined)).toEqual({ vatRate: '10', vatIncluded: 'INCLUDED' })
  })

  it('requires a KDV Oranı between 0 and 100', () => {
    expect(editorVatRateIssue('')).toBe(VAT_RATE_REQUIRED_MESSAGE)
    expect(editorVatRateIssue('  ')).toBe(VAT_RATE_REQUIRED_MESSAGE)
    expect(editorVatRateIssue('120')).toBe(VAT_RATE_REQUIRED_MESSAGE)
    expect(editorVatRateIssue('abc')).toBe(VAT_RATE_REQUIRED_MESSAGE)
    expect(editorVatRateIssue('0')).toBeUndefined()
    expect(editorVatRateIssue('10')).toBeUndefined()
  })
})
