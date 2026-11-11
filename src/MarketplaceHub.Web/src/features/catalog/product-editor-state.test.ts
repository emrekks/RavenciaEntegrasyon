import { describe, expect, it } from 'vitest'
import { ApiRequestError } from '../../shared/api'
import { SAME_SAVE_CODE_HOLDER, variantCodeFieldIssueText } from './platform-code-rules'
import { editorAwaitingSeed, editorDataStamp, editorFieldErrors, editorOpeningStock, editorSeedKey, editorSnapshot, editorSnapshotWithWebColor, editorStockUpdates, editorOpenedDesi, editorVariantCostUpdate, editorVariantDesiUpdate, editorVat, editorVatRateIssue, rekeySavedVariantRows, VAT_RATE_REQUIRED_MESSAGE, variantCodeConflictsFrom, variantCodeKey } from './product-editor-state'

describe('saved variant rows and code conflicts', () => {
  it('gives rows still under a temporary key the id of the saved variant with their stock code', () => {
    // Live 2026-11-02: 21 new rows kept their temporary keys after the save and the next Kaydet created them again.
    const rows = [{ key: 'v1', sku: 'MZ021Y-CD13' }, { key: 'tmp-1', sku: 'mz021y-cd-01' }, { key: 'tmp-2', sku: 'NEW-9' }, { key: 'tmp-3', sku: '' }]
    const saved = [{ id: 'v1', sku: 'MZ021Y-CD13' }, { id: 'v2', sku: 'MZ021Y-CD-01' }]
    expect(rekeySavedVariantRows(rows, saved).map(row => row.key)).toEqual(['v1', 'v2', 'tmp-2', 'tmp-3'])
    const unchanged = [{ key: 'v1', sku: 'MZ021Y-CD13' }]
    expect(rekeySavedVariantRows(unchanged, saved)).toBe(unchanged)
  })

  it('reads the codes a refused save names, with the card holding each', () => {
    const error = new ApiRequestError('Stok kodu başka bir üründe kullanılıyor', 409, 'SKU_CONFLICT_REVIEW_REQUIRED', { variantCodeConflicts: ['sku\tMZ021Y-CD-01\tİndigo Bluz', 'barcode\tMZ021Y-CD-02\t', 'junk'] })
    expect(variantCodeConflictsFrom(error)).toEqual({ [variantCodeKey('sku', 'MZ021Y-CD-01')]: 'İndigo Bluz', [variantCodeKey('barcode', 'mz021y-cd-02')]: 'başka bir ürün' })
    expect(variantCodeConflictsFrom(new Error('x'))).toEqual({})
  })

  it('marks a code the save gave to two of its own rows on every row holding it (O7)', () => {
    const error = new ApiRequestError('Aynı kod bu kayıtta birden çok varyanta verilmiş', 409, 'VARIANT_CODE_CONFLICT_REVIEW_REQUIRED', { variantCodeConflicts: ['sku\tMZ071-M\t\tSAME_SAVE\tvariant:v1,new:0'] })
    const conflicts = variantCodeConflictsFrom(error)
    expect(conflicts).toEqual({ [variantCodeKey('sku', 'mz071-m')]: SAME_SAVE_CODE_HOLDER })
    expect(variantCodeFieldIssueText([], 'sku', 'MZ071-M', conflicts[variantCodeKey('sku', 'MZ071-M')])).toBe('MZ071-M iki varyantta kullanılıyor')
  })
})

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
    // The "Özel Fiyat" choice is stored without a new offer version.
    const followed = editorDataStamp([{ ...base[0], platformStatuses: [{ ...base[0].platformStatuses[0], followsPanelPrice: true }, base[0].platformStatuses[1]] }])
    expect(followed).not.toBe(stamp)
    expect(editorDataStamp([{ ...base[0], platformStatuses: [{ ...base[0].platformStatuses[0], followsPanelPrice: false }, base[0].platformStatuses[1]] }])).not.toBe(followed)
  })

  it('carries a new variant\'s opening stock as a non-negative number', () => {
    expect(editorOpeningStock(7)).toBe(7)
    expect(editorOpeningStock('12')).toBe(12)
    expect(editorOpeningStock(0)).toBe(0)
    expect(editorOpeningStock(-3)).toBe(0)
    expect(editorOpeningStock('')).toBe(0)
    expect(editorOpeningStock(undefined)).toBe(0)
  })

  it('sends stock only for edited existing rows, against the stock loaded into the form', () => {
    // A new row's stock went with the variant (its opening stock), so it is not set again afterwards.
    const updates = editorStockUpdates([
      { key: 'v1', sku: 'A-1', stock: 5, loadedStock: 5 },
      { key: 'v2', sku: 'a-2', stock: 9, loadedStock: 4 },
      { key: 'new-row', sku: 'A-3', stock: 3 },
      { key: 'new-zero', sku: 'A-4', stock: 0 }
    ], [{ id: 'v1', sku: 'A-1' }, { id: 'v2', sku: 'A-2' }, { id: 'v3', sku: 'A-3' }, { id: 'v4', sku: 'A-4' }])
    expect(updates).toEqual([
      { variantId: 'v2', sku: 'A-2', onHand: 9, expectedOnHand: 4 }
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

  it('sends the model desi only when the owner edited the field, to every variant whose stored desi differs', () => {
    // The desi is the model's (owner, 11.11.2026): edited, it goes to every size of every colour, with the stored desi as loaded.
    expect(editorVariantDesiUpdate(3, true, 2)).toEqual({ desi: 3, loadedDesi: 2 })
    expect(editorVariantDesiUpdate(3, true, undefined)).toEqual({ desi: 3, loadedDesi: null })
    expect(editorVariantDesiUpdate(3, true, null)).toEqual({ desi: 3, loadedDesi: null })
    // Typed again with the value the field opened with: the colours that held another desi (or none) take it too.
    expect(editorVariantDesiUpdate(2, true, 5)).toEqual({ desi: 2, loadedDesi: 5 })
    expect(editorVariantDesiUpdate(2, true, 2)).toEqual({})
    // Untouched: nothing is written, a differing or missing desi stays as each variant holds it (the editor names it).
    expect(editorVariantDesiUpdate(2, false, 5)).toEqual({})
    expect(editorVariantDesiUpdate(2, false, null)).toEqual({})
    expect(editorVariantDesiUpdate(0, true, 2)).toEqual({})
  })

  it('opens the desi field empty when no variant has a desi, never with a guessed 1', () => {
    expect(editorOpenedDesi([{ desi: null }, { desi: null }])).toBeNull()
    expect(editorOpenedDesi([])).toBeNull()
    expect(editorOpenedDesi([{ desi: null }, { desi: 3 }])).toBe(3)
    expect(editorOpenedDesi([{ desi: 2 }, { desi: 3 }])).toBe(2)
    // The value most variants hold, whichever variant comes first (the opened colour's rows came first).
    expect(editorOpenedDesi([{ desi: 3 }, { desi: 1 }, { desi: 1 }, { desi: null }])).toBe(1)
  })

  it('ignores platform prices when deciding whether the form has unsaved edits', () => {
    const base = { form: { title: 'T' }, attributeSelections: {}, attributeTextValues: {}, familyMediaOrder: [], pendingMediaFileCount: 0, removedVariantIds: [] }
    const row = { key: 'v1', optionSignature: 'Renk: Mavi', options: { Renk: 'Mavi' }, attributeValueIds: {}, sku: 'A', barcode: '', stock: 1, salePrice: 2, listPrice: 3, costPrice: 0, mediaRefs: [] }
    const before = editorSnapshot({ ...base, variantRows: [row] })
    expect(editorSnapshot({ ...base, variantRows: [{ ...row, platformStatuses: [{ offerVersion: 2 }] } as typeof row] })).toBe(before)
    expect(editorSnapshot({ ...base, variantRows: [{ ...row, stock: 2 }] })).not.toBe(before)
  })

  it('counts the marketplace prices and the Web Color typed in the editor as unsaved edits', () => {
    const base = { form: { title: 'T' }, variantRows: [], attributeSelections: {}, attributeTextValues: {}, familyMediaOrder: [], pendingMediaFileCount: 0, removedVariantIds: [], webColor: { autoEnabled: true, manualValueId: '' } }
    const before = editorSnapshot(base)
    expect(editorSnapshot({ ...base, channelPricing: { ty: { listPrice: '150', salePrice: '120' } } })).not.toBe(before)
    expect(editorSnapshot({ ...base, variantChannelPricing: { 'v1:ty': { listPrice: '150', salePrice: '150' } } })).not.toBe(before)
    expect(editorSnapshot({ ...base, webColor: { autoEnabled: false, manualValueId: 'wc1' } })).not.toBe(before)
    // The order the stores were switched on in does not matter.
    const a = { listPrice: '1', salePrice: '1' }
    expect(editorSnapshot({ ...base, channelPricing: { x: a, y: a } })).toBe(editorSnapshot({ ...base, channelPricing: { y: a, x: a } }))
    // The Web Color an edit is filled with later becomes the snapshot's own: no unsaved edit.
    const seeded = { autoEnabled: false, manualValueId: 'wc1' }
    expect(editorSnapshotWithWebColor(before, seeded)).toBe(editorSnapshot({ ...base, webColor: seeded }))
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

describe('editorAwaitingSeed', () => {
  it('masks an edit or a copy until the product and the category and brand lists are in', () => {
    expect(editorAwaitingSeed({ seeding: true, productPending: true, listsPending: false })).toBe(true)
    expect(editorAwaitingSeed({ seeding: true, productPending: false, listsPending: true })).toBe(true)
    expect(editorAwaitingSeed({ seeding: true, productPending: false, listsPending: false })).toBe(false)
  })

  it('never masks a new product', () => {
    expect(editorAwaitingSeed({ seeding: false, productPending: true, listsPending: true })).toBe(false)
  })
})
