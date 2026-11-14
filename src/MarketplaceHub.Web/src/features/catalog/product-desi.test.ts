import { describe, expect, it } from 'vitest'
import { bulkFieldIssue, bulkMaxDesi } from './bulk-field-run'
import { maxProductDesi, parseProductDesi, productDesiTooLarge } from './product-desi'
import { editorWarrantyMonths, missingRequiredEditorFields } from './product-editor/required-fields'
import { toggleProductTransferGroup } from './products/ProductTransferDialog'

describe('one desi limit', () => {
  it('is 9999 in the editor and in Toplu Değer Ata', () => {
    expect(maxProductDesi).toBe(9999)
    expect(bulkMaxDesi).toBe(maxProductDesi)
    expect(bulkFieldIssue('desi', '9999')).toBeNull()
    expect(bulkFieldIssue('desi', '9999,01')).toContain('9.999')
    expect(productDesiTooLarge('9999')).toBe(false)
    expect(productDesiTooLarge('10000')).toBe(true)
    expect(productDesiTooLarge('')).toBe(false)
  })
})

describe('Garanti Süresi as a new card sends it', () => {
  it('keeps 0 as no warranty and an empty field as missing', () => {
    expect(editorWarrantyMonths('0')).toBe(0)
    expect(editorWarrantyMonths('24')).toBe(24)
    expect(editorWarrantyMonths(' ')).toBeNull()
  })
})

describe('Excel import fields', () => {
  it('puts Marka in Ürün Bilgileri, after Ürün Adı', () => {
    expect(toggleProductTransferGroup(['COST'], ['TITLE', 'BRAND', 'BARCODE', 'MODEL_CODE'])).toEqual(['TITLE', 'BRAND', 'BARCODE', 'MODEL_CODE', 'COST'])
    expect(toggleProductTransferGroup(['COST'], ['TITLE', 'BRAND', 'BARCODE', 'MODEL_CODE'], ['TITLE', 'BRAND', 'COST'])).toEqual(['TITLE', 'BRAND', 'COST'])
  })
})

describe('a half desi', () => {
  it('reads 0.5 and 0,5 alike and keeps the required Desi satisfied', () => {
    expect(parseProductDesi('0.5')).toBe(0.5)
    expect(parseProductDesi('0,5')).toBe(0.5)
    expect(parseProductDesi('')).toBe(0)
    expect(parseProductDesi('abc')).toBe(0)
    const form = { title: 'a', brandId: 'b', categoryId: 'c', modelCode: 'm', baseSku: 's', barcode: 'x', listPrice: '10', salePrice: '9', warrantyMonths: '0', deliveryDays: '1', desi: '0.5' }
    const options = { automaticBarcodes: false }
    expect(missingRequiredEditorFields(form, options)).toEqual([])
    expect(missingRequiredEditorFields({ ...form, desi: '0,5' }, options)).toEqual([])
    expect(missingRequiredEditorFields({ ...form, desi: '0' }, options)).toEqual(['desi'])
    expect(missingRequiredEditorFields({ ...form, desi: '' }, options)).toEqual(['desi'])
  })
})
