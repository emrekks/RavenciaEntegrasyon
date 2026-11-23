import { readMoneyField } from './seeded-fields'
import { parseProductDesi } from '../product-desi'
import { parseTrMeasure } from '../../../shared/tr-number'

// The product editor's required fields (owner, 10 Nov 2026: "bunlar zorunlu alandır, boş olamazlar"). Every save but
// Taslak Olarak Kaydet stops while one is empty: on a new card, on an edit and before publishing. A card that opens with
// one empty still opens; the field is framed red until it is filled. Garanti Süresi takes 0 (no warranty); empty is
// missing. Stok Kodu and Barkod are made from the model code while Barkodları Otomatik Oluştur is on, and on a card with variants each row holds its own (the card shows them read-only), so they are not
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

// Desi and the prices are the editor's text, or a saved product's stored numbers (the list's Platformlara Güncelle):
// a number is compared as it is, never written out and read again (review 23 Nov: String(0.5) read by the Turkish rule
// held every product with desi 0,5 back as "Zorunlu eksik: Desi").
export type RequiredFieldsForm = Record<'title' | 'brandId' | 'categoryId' | 'modelCode' | 'baseSku' | 'barcode' | 'warrantyMonths' | 'deliveryDays', string> & Record<'listPrice' | 'salePrice' | 'desi', string | number>

const filled = (value: string | number) => typeof value === 'number' ? Number.isFinite(value) : value.trim() !== ''
const positiveDesi = (value: string | number) => (typeof value === 'number' ? value : parseProductDesi(value)) > 0
const positivePrice = (key: 'listPrice' | 'salePrice', value: string | number) => (typeof value === 'number' ? value : readMoneyField(key, value)) > 0

// The eleven required fields in the order the page shows them, the same order as the server's list
// (ProductRequiredFields.Fields, src/MarketplaceHub.Application/ProductRequiredFields.cs; a test holds them equal).
export const REQUIRED_EDITOR_FIELDS: readonly RequiredEditorField[] = ['title', 'brandId', 'categoryId', 'modelCode', 'baseSku', 'barcode', 'desi', 'warrantyMonths', 'deliveryDays', 'listPrice', 'salePrice']

// The required fields that are empty, in the order the page shows them.
export function missingRequiredEditorFields(form: RequiredFieldsForm, options: { automaticBarcodes: boolean; hasVariants?: boolean }): RequiredEditorField[] {
  // On a card with variants each row holds its own codes (owner, 11 Nov 2026: the card's fields are read-only there).
  const codesElsewhere = options.automaticBarcodes || Boolean(options.hasVariants)
  const given: Record<RequiredEditorField, boolean> = {
    title: filled(form.title),
    brandId: filled(form.brandId),
    categoryId: filled(form.categoryId),
    modelCode: filled(form.modelCode),
    baseSku: codesElsewhere || filled(form.baseSku),
    barcode: codesElsewhere || filled(form.barcode),
    desi: positiveDesi(form.desi),
    warrantyMonths: filled(form.warrantyMonths),
    deliveryDays: filled(form.deliveryDays),
    // Prices must be above 0, not only filled.
    listPrice: positivePrice('listPrice', form.listPrice),
    salePrice: positivePrice('salePrice', form.salePrice)
  }
  return REQUIRED_EDITOR_FIELDS.filter(field => !given[field])
}

// The message under one field: a price typed as 0 must be above 0 (owner, 10 Nov 2026: "0 olmayacak onlarda").
export function requiredFieldMessage(field: RequiredEditorField, form: RequiredFieldsForm) {
  const label = requiredEditorFieldLabels[field]
  if ((field === 'listPrice' || field === 'salePrice') && filled(form[field])) return `${label} 0'dan büyük olmalı.`
  // A typed desi the Turkish rule cannot read ("1.5") is told so (review 23 Nov), not "zorunludur".
  if (field === 'desi' && typeof form.desi === 'string' && filled(form.desi)) return parseTrMeasure(form.desi) === null ? 'Desi okunamadı; virgül kullanın (ör. 1,5).' : "Desi 0'dan büyük olmalı."
  return `${label} zorunludur.`
}

// Garanti Süresi as a save sends it: 0 is "no warranty", an empty field is null, so the server can tell a card created
// without one (refused unless it is a draft) from one created with no warranty.
export function editorWarrantyMonths(value: string): number | null {
  return value.trim() === '' ? null : Number(value)
}

// Garanti Süresi as the form shows a saved card's (owner, 15 Nov 2026: "0 ile boş farklı"): 0 is "0" (no warranty), an
// empty warranty stays empty so the editor asks for it (a required field); a new form starts at "0".
export function editorWarrantyText(months: number | null | undefined) {
  return months == null ? '' : String(months)
}

// Garanti Süresi as an edit save sends it: the months (0 included), or for an emptied field an explicit clear. Leaving
// warrantyMonths out (or null) keeps the card's value on the server, so an emptied field must say so; only a draft can
// be saved that way (a card on sale with it empty is refused as a missing required field).
export function editorWarrantyUpdate(value: string): { warrantyMonths: number } | { warrantyMonths: null; clearWarrantyMonths: true } {
  const months = editorWarrantyMonths(value)
  return months === null ? { warrantyMonths: null, clearWarrantyMonths: true } : { warrantyMonths: months }
}

export function requiredFieldsMessage(missing: readonly RequiredEditorField[]) {
  return `Zorunlu alanları doldurun: ${missing.map(field => requiredEditorFieldLabels[field]).join(', ')}.`
}
