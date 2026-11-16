import { describe, expect, it } from 'vitest'
import { EMPTY_EDITOR_FORM } from './editor-form'
import { REQUIRED_EDITOR_FIELDS, requiredEditorFieldLabels } from './required-fields'
import { requiredFieldsOutcome, validateEditor, type EditorSaveValidationInput } from './validate-editor'
import type { VariantDraft } from './editor-types'

// node:fs without @types/node, the way dashboard-sade-css.test.ts reads files; the URL is built from a variable so the
// bundler does not turn it into an asset address.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url

// The server's list of the editor's required fields (ProductRequiredFields.Fields), read from its source so the two
// cannot drift apart: the field names come from its constants, the labels from the Fields table. KDV Oranı (vatRate) is
// not one of the eleven editor fields (the editor checks it apart) and is left out.
function serverRequiredFields() {
  const source = fs.readFileSync(new URL('../../../../../MarketplaceHub.Application/ProductRequiredFields.cs', testFileUrl), 'utf8')
  const constants = new Map([...source.matchAll(/public const string (\w+) = "([^"]+)";/g)].map(match => [match[1], match[2]]))
  const table = /Fields =\s*\[([\s\S]*?)\];/.exec(source)?.[1] ?? ''
  return [...table.matchAll(/\((\w+), "([^"]+)"\)/g)]
    .map(match => ({ field: constants.get(match[1]) ?? `?${match[1]}`, label: match[2] }))
    .filter(item => item.field !== 'vatRate')
}

const complete = { ...EMPTY_EDITOR_FORM, title: 'Elbise', brandId: 'b1', categoryId: 'c1', modelCode: 'M1', baseSku: 'S1', barcode: 'B1', listPrice: '100', salePrice: '90', vatRate: '20' }
const row = (extra: Partial<VariantDraft> = {}): VariantDraft => ({ key: 'r1', optionSignature: 'Tek Ürün', options: {}, attributeValueIds: {}, sku: 'S1', barcode: 'B1', stock: 0, salePrice: 90, listPrice: 100, costPrice: null, mediaRefs: [], ...extra })
const saveInput = (extra: Partial<EditorSaveValidationInput> = {}): EditorSaveValidationInput => ({
  form: { ...complete, description: '<p>Açıklama</p>' }, automaticBarcodes: false, hasVariants: false, rows: [row()], editing: false, initialDesi: 1, cardPriceIssue: null, variantAttributeIds: [], requirements: [],
  colorOptionRequirement: undefined, webColorRequirement: undefined, webColor: { autoEnabled: true, manualValueId: '' }, attributeSelections: {}, attributeTextValues: {}, mediaUrls: [], mediaFileCount: 0, copySourceVariants: null, selectedChannels: [], ...extra
})

describe('the required fields', () => {
  it('are the server’s eleven, in its order and with its labels', () => {
    const server = serverRequiredFields()
    expect(server).toHaveLength(11)
    expect(REQUIRED_EDITOR_FIELDS.map(field => ({ field, label: requiredEditorFieldLabels[field] }))).toEqual(server)
  })

  it('count a price of 0 as missing: prices must be above 0', () => {
    expect(validateEditor({ form: complete, automaticBarcodes: false, hasVariants: false }, { kind: 'live' }).missingRequired).toEqual([])
    expect(validateEditor({ form: { ...complete, listPrice: '0', salePrice: '0,00' }, automaticBarcodes: false, hasVariants: false }, { kind: 'live' }).missingRequired).toEqual(['listPrice', 'salePrice'])
    expect(validateEditor({ form: { ...EMPTY_EDITOR_FORM, warrantyMonths: '', desi: '' }, automaticBarcodes: false, hasVariants: false }, { kind: 'live' }).missingRequired).toEqual([...REQUIRED_EDITOR_FIELDS.filter(field => field !== 'deliveryDays')])
  })

  it('let a draft be incomplete, keep a new card or a draft as a draft on Kaydet, and stop an active card’s save', () => {
    const missing = validateEditor({ form: { ...complete, brandId: '' }, automaticBarcodes: false, hasVariants: false }, { kind: 'live' }).missingRequired
    expect(missing).toEqual(['brandId'])
    expect(requiredFieldsOutcome(missing, { explicitDraft: true, plainSave: false, draftAllowed: true })).toEqual({ missing: [], autoDraft: false, stop: false })
    expect(requiredFieldsOutcome(missing, { explicitDraft: false, plainSave: true, draftAllowed: true })).toEqual({ missing: ['brandId'], autoDraft: true, stop: false })
    // A card on sale (no draft allowed) and publishing (not a plain save) stop.
    expect(requiredFieldsOutcome(missing, { explicitDraft: false, plainSave: true, draftAllowed: false })).toEqual({ missing: ['brandId'], autoDraft: false, stop: true })
    expect(requiredFieldsOutcome(missing, { explicitDraft: false, plainSave: false, draftAllowed: true })).toEqual({ missing: ['brandId'], autoDraft: false, stop: true })
    expect(requiredFieldsOutcome([], { explicitDraft: false, plainSave: false, draftAllowed: false }).stop).toBe(false)
  })
})

describe('validateEditor', () => {
  it('names an invalid VAT apart from the eleven', () => {
    expect(validateEditor({ form: { ...complete, vatRate: '' }, automaticBarcodes: false, hasVariants: false }, { kind: 'live' }).vatRateIssue).toBeTruthy()
    expect(validateEditor({ form: complete, automaticBarcodes: false, hasVariants: false }, { kind: 'live' }).vatRateIssue).toBeUndefined()
  })

  it('passes a complete card and refuses a variant price of 0 or a list price below the sale price', () => {
    expect(validateEditor(saveInput(), { kind: 'save', requireCompleteCatalog: true, requirePublicationReadiness: true }).issues).toEqual([])
    expect(validateEditor(saveInput({ rows: [row({ salePrice: 0, listPrice: 0 })] }), { kind: 'save', requireCompleteCatalog: true, requirePublicationReadiness: true }).issues).toEqual(['Her varyantta liste ve satış fiyatı 0\'dan büyük olmalı.'])
    expect(validateEditor(saveInput({ rows: [row({ salePrice: 120 })] }), { kind: 'save', requireCompleteCatalog: false, requirePublicationReadiness: false }).issues).toEqual(['Her varyantta liste fiyatı satış fiyatından küçük olamaz.'])
  })

  it('asks for the description only when the catalog must be complete', () => {
    const input = saveInput({ form: { ...complete, description: '' } })
    expect(validateEditor(input, { kind: 'save', requireCompleteCatalog: true, requirePublicationReadiness: false }).issues).toEqual(['Açıklama zorunludur.'])
    expect(validateEditor(input, { kind: 'save', requireCompleteCatalog: false, requirePublicationReadiness: false }).issues).toEqual([])
  })

  it('checks a picked marketplace’s price before publishing', () => {
    const input = saveInput({ mediaUrls: ['https://cdn.example.com/a.jpg'], selectedChannels: [{ id: 'ty', name: 'Trendyol Mağaza', draft: { listPrice: '80', salePrice: '90' } }] })
    expect(validateEditor(input, { kind: 'save', requireCompleteCatalog: true, requirePublicationReadiness: true }).issues).toEqual(['Trendyol Mağaza için liste fiyatı satış fiyatından küçük olamaz; satış fiyatı sıfırdan büyük olmalıdır.'])
    expect(validateEditor(input, { kind: 'save', requireCompleteCatalog: true, requirePublicationReadiness: false }).issues).toEqual([])
  })

  it('stops a draft only on what the server refuses', () => {
    expect(validateEditor(saveInput({ form: { ...EMPTY_EDITOR_FORM, title: 'Taslak' } }), { kind: 'draft' }).issues).toEqual([])
    expect(validateEditor(saveInput({ rows: [row({ sku: '' })], form: { ...EMPTY_EDITOR_FORM, warrantyMonths: '300' } }), { kind: 'draft' }).issues).toEqual([
      'Stok kodu boş 1 varyant satırı var. Her varyant stok koduyla saklanır ve panel stok kodu üretmez; bu satırlara stok kodu girin veya satırları silin. Diğer alanlar taslakta boş kalabilir.',
      'Garanti süresi 0-240 ay arasında tam sayı olmalıdır.'
    ])
  })
})
