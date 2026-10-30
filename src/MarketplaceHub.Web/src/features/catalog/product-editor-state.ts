import { ApiRequestError } from '../../shared/api'
import type { VariantStockSet } from './variant-stock-set'

// The key of the product snapshot the editor form was filled from. Every place that tells the editor "this version is
// already on screen" (the seed itself, an image deletion that bumps the version) must build it the same way, or the
// next refetch reseeds the form and drops unsaved edits.
export function editorSeedKey(mode: 'edit' | 'copy', productId: string, version: number) {
  return `${mode}:${productId}:${version}`
}

// A stable text form of what the user can edit; compared with the one taken right after the form was filled.
// Server-side state shown in the rows (platform prices) is left out: it is saved on its own and does not make the
// form "unsaved".
export function editorSnapshot(state: {
  form: Record<string, string>
  variantRows: Array<{ key: string; optionSignature: string; options: Record<string, string>; attributeValueIds: Record<string, string>; sku: string; barcode: string; stock: number; salePrice: number; listPrice: number; costPrice: number | null; mediaRefs: string[] }>
  attributeSelections: Record<string, string[]>
  attributeTextValues: Record<string, string>
  familyMediaOrder: string[]
  pendingMediaFileCount: number
  removedVariantIds: string[]
}) {
  return JSON.stringify({
    form: state.form,
    rows: state.variantRows.map(row => [row.key, row.optionSignature, row.options, row.attributeValueIds, row.sku, row.barcode, row.stock, row.salePrice, row.listPrice, row.costPrice, row.mediaRefs]),
    attributeSelections: state.attributeSelections,
    attributeTextValues: state.attributeTextValues,
    familyMediaOrder: state.familyMediaOrder,
    pendingMediaFileCount: state.pendingMediaFileCount,
    removedVariantIds: state.removedVariantIds
  })
}

// Stock changes the editor sends after the product itself was saved: only rows whose stock the user changed, each as
// an absolute value against the stock loaded into the form. A row without a loaded value is a new variant, whose
// stock the server created at 0.
export function editorStockUpdates(
  rows: Array<{ key: string; sku: string; stock: number; loadedStock?: number }>,
  savedVariants: Array<{ id: string; sku: string }>
): VariantStockSet[] {
  const rowsBySku = new Map(rows.map(row => [row.sku.trim().toLocaleUpperCase('tr-TR'), row]))
  return savedVariants.flatMap(variant => {
    const row = rowsBySku.get(variant.sku.trim().toLocaleUpperCase('tr-TR'))
    if (!row) return []
    const expectedOnHand = row.key === variant.id && row.loadedStock != null ? row.loadedStock : 0
    if (row.stock === expectedOnHand) return []
    return [{ variantId: variant.id, sku: variant.sku, onHand: row.stock, expectedOnHand }]
  })
}

// The cost fields of an existing variant's update. Only a cost the user changed is sent (null: leave the stored cost
// as it is), together with the cost the row was loaded with (null: it was empty). The server refuses the save when
// the stored cost is no longer that one, so a cost set meanwhile elsewhere (ürün içe aktarma) is never overwritten unseen.
export function editorVariantCostUpdate(row: { costPrice: number | null; loadedCostPrice?: number | null }): { costPrice: number | null; loadedCostPrice?: number | null } {
  if (row.costPrice == null || row.costPrice === row.loadedCostPrice) return { costPrice: null }
  return { costPrice: row.costPrice, loadedCostPrice: row.loadedCostPrice ?? null }
}

// The desi field of an existing variant's update. The form has one desi for the product: it is sent only when the user
// changed it from the value the form opened with, together with the variant's stored desi as loaded, so the server
// refuses a desi changed meanwhile elsewhere (ürün içe aktarma) instead of overwriting it.
export function editorVariantDesiUpdate(desi: number, openedDesi: number, loadedDesi: number | null | undefined): { desi?: number; loadedDesi?: number | null } {
  if (!(desi > 0) || desi === openedDesi) return {}
  return { desi, loadedDesi: loadedDesi ?? null }
}

// Field messages the server attached to a refused save (422 fieldErrors), keyed the way the editor shows them.
export type EditorFieldErrors = Partial<Record<'title' | 'description' | 'categoryId' | 'brandId' | 'prices' | 'variants' | 'attributes' | 'stock' | 'vatRate', string>>
const serverFieldToEditorField: Record<string, keyof EditorFieldErrors> = {
  title: 'title',
  vatrate: 'vatRate',
  vatinclusion: 'vatRate',
  description: 'description',
  categoryid: 'categoryId',
  brandid: 'brandId',
  prices: 'prices',
  defaultlistprice: 'prices',
  defaultsaleprice: 'prices',
  variants: 'variants',
  variantupdates: 'variants',
  variantstocreate: 'variants',
  variantidstodelete: 'variants',
  attributes: 'attributes',
  requirements: 'attributes',
  onhand: 'stock',
  expectedonhand: 'stock'
}

export function editorFieldErrors(error: unknown): EditorFieldErrors {
  if (!(error instanceof ApiRequestError) || !error.fieldErrors) return {}
  const result: EditorFieldErrors = {}
  for (const [field, messages] of Object.entries(error.fieldErrors)) {
    const target = serverFieldToEditorField[field.split(/[[.]/)[0].replace(/[^a-z]/gi, '').toLowerCase()]
    const message = (messages ?? []).filter(Boolean).join(' ')
    if (!target || !message) continue
    result[target] = result[target] ? `${result[target]} ${message}` : message
  }
  return result
}

// Turkey's VAT rates since 10 July 2023 (8 and 18 became 10 and 20); 0 is for exempt goods.
export const TURKISH_VAT_RATES = ['0', '1', '10', '20']
export const DEFAULT_VAT_RATE = '10'

type VatSource = { vatRate?: number | null; vatInclusion?: string | null }
// KDV Oranı and KDV Dahil mi as the editor opens them: the product card's own VAT, else its primary offer's. A card
// with neither opens at the usual %10, which the user can change before saving.
export function editorVat(product: VatSource | undefined, primaryOffer: VatSource | undefined): { vatRate: string; vatIncluded: string } {
  const source = product?.vatRate != null ? product : primaryOffer?.vatRate != null ? primaryOffer : undefined
  return { vatRate: source?.vatRate == null ? DEFAULT_VAT_RATE : String(source.vatRate), vatIncluded: source?.vatInclusion === 'EXCLUDED' ? 'EXCLUDED' : 'INCLUDED' }
}

export const VAT_RATE_REQUIRED_MESSAGE = 'KDV oranı zorunludur.'
// KDV Oranı is required on every product card: a save without a rate 0-100 is stopped before it reaches the server.
export function editorVatRateIssue(vatRate: string): string | undefined {
  const value = vatRate.trim()
  return value === '' || !Number.isFinite(Number(value)) || Number(value) < 0 || Number(value) > 100 ? VAT_RATE_REQUIRED_MESSAGE : undefined
}
