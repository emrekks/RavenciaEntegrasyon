import { describe, expect, it } from 'vitest'
import { cardPrices, priceInputNumber } from './card-prices'
import { missingRequiredEditorFields, requiredFieldMessage, requiredFieldsMessage, type RequiredFieldsForm } from './required-fields'

describe('card prices', () => {
  it('reads a decimal comma, a point and thousands points', () => {
    expect(priceInputNumber('549,90')).toBe(549.9)
    expect(priceInputNumber('549.90')).toBe(549.9)
    expect(priceInputNumber('1.249,90')).toBe(1249.9)
    expect(priceInputNumber('')).toBe(0)
    expect(priceInputNumber('abc')).toBeNaN()
  })

  it('gives an empty list price the sale price and refuses a list price below the sale price', () => {
    expect(cardPrices('', '549,90')).toEqual({ listPrice: 549.9, salePrice: 549.9, issue: null })
    expect(cardPrices('699,90', '549,90')).toEqual({ listPrice: 699.9, salePrice: 549.9, issue: null })
    expect(cardPrices('500', '549,90').issue).toBe('Liste fiyatı satış fiyatından küçük olamaz.')
    expect(cardPrices('-1', '0').issue).toBe('Fiyatlar negatif olamaz.')
    expect(cardPrices('x', '10').issue).toMatch(/sayı olmalıdır/)
  })
})

describe('required editor fields', () => {
  const complete: RequiredFieldsForm = { title: 'Bluz', brandId: 'b1', categoryId: 'c1', modelCode: 'MZ1', baseSku: 'MZ1', barcode: 'MZ1-01', listPrice: '699,90', salePrice: '549,90', warrantyMonths: '0', deliveryDays: '1', desi: '1' }

  it('takes a complete card, with Garanti Süresi 0 meaning no warranty', () => {
    expect(missingRequiredEditorFields(complete, { automaticBarcodes: false })).toEqual([])
  })

  it('lists every empty field in page order, prices of 0 included', () => {
    const empty = Object.fromEntries(Object.keys(complete).map(key => [key, ''])) as RequiredFieldsForm
    expect(missingRequiredEditorFields(empty, { automaticBarcodes: false })).toEqual(['title', 'brandId', 'categoryId', 'modelCode', 'baseSku', 'barcode', 'desi', 'warrantyMonths', 'deliveryDays', 'listPrice', 'salePrice'])
    expect(missingRequiredEditorFields({ ...complete, listPrice: '0', salePrice: '0,00' }, { automaticBarcodes: false })).toEqual(['listPrice', 'salePrice'])
    expect(requiredFieldsMessage(['brandId', 'listPrice'])).toBe('Zorunlu alanları doldurun: Marka, Liste Fiyatı.')
  })

  it("says a price of 0 must be above 0 and an empty one is required", () => {
    expect(requiredFieldMessage('salePrice', { ...complete, salePrice: '0' })).toBe("Satış Fiyatı 0'dan büyük olmalı.")
    expect(requiredFieldMessage('salePrice', { ...complete, salePrice: '' })).toBe('Satış Fiyatı zorunludur.')
    expect(requiredFieldMessage('warrantyMonths', { ...complete, warrantyMonths: '' })).toBe('Garanti Süresi zorunludur.')
  })

  it('does not ask for Stok Kodu and Barkod while automatic barcodes make them from the model code', () => {
    expect(missingRequiredEditorFields({ ...complete, baseSku: '', barcode: '' }, { automaticBarcodes: true })).toEqual([])
  })
})
