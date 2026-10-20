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
  variantRows: Array<{ key: string; optionSignature: string; options: Record<string, string>; attributeValueIds: Record<string, string>; sku: string; barcode: string; stock: number; salePrice: number; listPrice: number; costPrice: number; mediaRefs: string[] }>
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

// Field messages the server attached to a refused save (422 fieldErrors), keyed the way the editor shows them.
export type EditorFieldErrors = Partial<Record<'title' | 'description' | 'categoryId' | 'brandId' | 'prices' | 'variants' | 'attributes' | 'stock', string>>
const serverFieldToEditorField: Record<string, keyof EditorFieldErrors> = {
  title: 'title',
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
