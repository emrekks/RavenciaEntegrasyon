// The prices a product editor save writes, set against what each variant had when the editor opened, for the save's
// price-jump confirmation (price-jump.ts): the variant table's panel prices (also what Toplu Düzenle handed over), a
// per-variant marketplace price staged from Toplu Düzenle or the variant price table, and a marketplace card price the
// person typed, which goes to each variant's offer there. A new variant (nothing saved) has no previous value.
import { panelPrices } from '../quick-price-edit'
import type { PriceChange } from '../price-jump'
import { priceInputNumber } from './card-prices'
import type { ChannelPricingDraft } from './marketplace-channel-pricing'
import type { Product } from '../shared'
import type { VariantDraft } from './editor-types'

export type EditorPriceJumpInput = {
  rows: ReadonlyArray<VariantDraft>
  saved: Product | null | undefined
  channelPricing: Readonly<Record<string, ChannelPricingDraft>>
  variantChannelPricing: Readonly<Record<string, ChannelPricingDraft>>
  channelPriceDraft: (connectionId: string) => ChannelPricingDraft
  channelPriceEdited: (connectionId: string) => boolean
}

export function editorPriceChanges(input: EditorPriceJumpInput): PriceChange[] {
  const savedById = new Map((input.saved?.variants ?? []).map(variant => [variant.id, variant]))
  const changes: PriceChange[] = []
  const push = (key: string, before: number | null | undefined, after: number) => { if (Number.isFinite(after) && after > 0 && before !== after) changes.push({ key, before, after }) }
  for (const row of input.rows) {
    const variant = savedById.get(row.key)
    if (!variant || !input.saved) continue
    const panel = panelPrices(variant, input.saved)
    push(row.key, panel.salePrice, row.salePrice)
    push(row.key, panel.listPrice, row.listPrice)
    for (const status of row.platformStatuses ?? []) {
      if (!status.connectionId || !status.offerId) continue
      const staged = input.variantChannelPricing[`${row.key}:${status.connectionId}`]
      const typed = staged ?? (status.connectionId in input.channelPricing && input.channelPriceEdited(status.connectionId) ? input.channelPriceDraft(status.connectionId) : null)
      if (!typed) continue
      push(row.key, status.salePrice, priceInputNumber(typed.salePrice))
      push(row.key, status.listPrice, priceInputNumber(typed.listPrice))
    }
  }
  return changes
}
