import { priceInputNumber } from './card-prices'

// The product editor's required fields (owner, 10 Nov 2026: "bunlar zorunlu alandır, boş olamazlar"). Every save but
// Taslak Olarak Kaydet stops while one is empty: on a new card, on an edit and before publishing. A card that opens with
// one empty still opens; the field is framed red until it is filled. Garanti Süresi takes 0 (no warranty); empty is
// missing. Stok Kodu and Barkod are made from the model code while Barkodları Otomatik Oluştur is on, so they are not
// asked for then. Liste Fiyatı and Satış Fiyatı must be above 0.
export type RequiredEditorField = 'title' | 'brandId' | 'categoryId' | 'modelCode' | 'baseSku' | 'barcode' | 'listPrice' | 'salePrice' | 'warrantyMonths' | 'deliveryDays' | 'desi'

export const requiredEditorFieldLabels: Record<RequiredEditorField, string> = {
  title: 'Ürün Adı',
  brandId: 'Marka',
  categoryId: 'Panel Kategorisi',
  modelCode: 'Model Kodu',
  baseSku: 'Stok Kodu',
  barcode: 'Barkod',
  listPrice: 'Liste Fiyatı',
  salePrice: 'Satış Fiyatı',
  warrantyMonths: 'Garanti Süresi',
  deliveryDays: 'Teslimat Süresi',
  desi: 'Desi'
}

export type RequiredFieldsForm = Record<'title' | 'brandId' | 'categoryId' | 'modelCode' | 'baseSku' | 'barcode' | 'listPrice' | 'salePrice' | 'warrantyMonths' | 'deliveryDays' | 'desi', string>

const filled = (value: string) => value.trim() !== ''
const positivePrice = (value: string) => priceInputNumber(value) > 0

// The required fields that are empty, in the order the page shows them.
export function missingRequiredEditorFields(form: RequiredFieldsForm, options: { automaticBarcodes: boolean }): RequiredEditorField[] {
  const checks: Array<[RequiredEditorField, boolean]> = [
    ['title', filled(form.title)],
    ['brandId', filled(form.brandId)],
    ['categoryId', filled(form.categoryId)],
    ['modelCode', filled(form.modelCode)],
    ['baseSku', options.automaticBarcodes || filled(form.baseSku)],
    ['barcode', options.automaticBarcodes || filled(form.barcode)],
    ['desi', filled(form.desi)],
    ['warrantyMonths', filled(form.warrantyMonths)],
    ['deliveryDays', filled(form.deliveryDays)],
    ['listPrice', positivePrice(form.listPrice)],
    ['salePrice', positivePrice(form.salePrice)]
  ]
  return checks.filter(([, ok]) => !ok).map(([field]) => field)
}

// The message under one field: a price typed as 0 must be above 0 (owner, 10 Nov 2026: "0 olmayacak onlarda").
export function requiredFieldMessage(field: RequiredEditorField, form: RequiredFieldsForm) {
  const label = requiredEditorFieldLabels[field]
  if ((field === 'listPrice' || field === 'salePrice') && filled(form[field])) return `${label} 0'dan büyük olmalı.`
  return `${label} zorunludur.`
}

export function requiredFieldsMessage(missing: readonly RequiredEditorField[]) {
  return `Zorunlu alanları doldurun: ${missing.map(field => requiredEditorFieldLabels[field]).join(', ')}.`
}
