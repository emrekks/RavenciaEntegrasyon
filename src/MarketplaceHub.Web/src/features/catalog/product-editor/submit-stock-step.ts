// The stock of a save, after the pictures (WEB-SAYFALAR-10).
import { editorStockUpdates } from '../product-editor-state'
import { applyVariantStockSets, setVariantOnHand } from '../variant-stock-set'
import type { Product } from '../shared'
import type { VariantDraft } from './editor-types'

// Only rows whose stock the user changed are sent, as absolute values against the stock loaded into the form; a stock
// changed meanwhile (order, return, another tab) is refused for that row instead of being overwritten. A new variant's
// stock went with the variant itself (its Açılış Stoğu).
export async function runStockStep(rows: VariantDraft[], variants: Product['variants']) {
  const result = await applyVariantStockSets(editorStockUpdates(rows, variants), update => setVariantOnHand(update, 'Ürün düzenleme stoğu'))
  return { ...result, warning: result.failed.length ? `Stok kaydedilemedi: ${result.failed.map(item => `${item.sku}: ${item.message}`).join('; ')}` : null }
}
