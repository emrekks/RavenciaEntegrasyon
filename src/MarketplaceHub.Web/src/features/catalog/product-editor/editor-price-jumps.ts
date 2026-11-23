// The prices a product editor save writes, set against what each variant had when the editor opened, for the save's
// price-jump confirmation (price-jump.ts): the variant table's panel prices (also what Toplu Düzenle handed over), a
// per-variant marketplace price staged from Toplu Düzenle or the variant price table, and a marketplace card price the
// person typed, which goes to each variant's offer there. A new variant (nothing saved) has no previous value.
import { panelPrices } from '../quick-price-edit'
import type { PriceChange } from '../price-jump'
import { channelFieldKey, readMoneyField } from './seeded-fields'
import type { ChannelPricingDraft, StagedOfferPrices } from './marketplace-channel-pricing'
import type { Product } from '../shared'
import type { VariantDraft } from './editor-types'

export type EditorPriceJumpInput = {
  rows: ReadonlyArray<VariantDraft>
  saved: Product | null | undefined
  channelPricing: Readonly<Record<string, ChannelPricingDraft>>
  variantChannelPricing: Readonly<Record<string, StagedOfferPrices>>
  channelPriceDraft: (connectionId: string) => ChannelPricingDraft
  channelPriceEdited: (connectionId: string) => boolean
}

export function editorPriceChanges(input: EditorPriceJumpInput): PriceChange[] {
  const savedById = new Map((input.saved?.variants ?? []).map(variant => [variant.id, variant]))
  const changes: PriceChange[] = []
  const push = (key: string, before: number | null | undefined, after: number) => { if (Number.isFinite(after) && after > 0 && before !== after) changes.push({ key, before, after }) }
  for (const row of input.rows) {
    const variant = savedById.get(row.key)
    if (!input.saved) continue
    // A new variant (nothing saved) is set against the card's panel price; without one there is nothing to compare with.
    const panel = variant ? panelPrices(variant, input.saved) : { listPrice: input.saved.defaultListPrice ?? null, salePrice: input.saved.defaultSalePrice ?? null }
    push(row.key, panel.salePrice, row.salePrice)
    push(row.key, panel.listPrice, row.listPrice)
    // Every marketplace the row gets a typed price on: its offer's price before, or, for a first offer (POST), the panel
    // price the variant had (the price it sold at there until now).
    const statuses = new Map((row.platformStatuses ?? []).filter(status => status.connectionId).map(status => [status.connectionId!, status]))
    const connectionIds = new Set([...statuses.keys(), ...Object.keys(input.channelPricing), ...Object.keys(input.variantChannelPricing).filter(key => key.startsWith(`${row.key}:`)).map(key => key.slice(row.key.length + 1))])
    for (const connectionId of connectionIds) {
      const status = statuses.get(connectionId)
      const staged = input.variantChannelPricing[`${row.key}:${connectionId}`]
      // A staged price is a number already; the card's text is read as Kaydet reads it (an untouched side as loaded).
      const card = connectionId in input.channelPricing && input.channelPriceEdited(connectionId) ? input.channelPriceDraft(connectionId) : null
      const typed = staged ?? (card ? { listPrice: readMoneyField(channelFieldKey(connectionId, 'listPrice'), card.listPrice), salePrice: readMoneyField(channelFieldKey(connectionId, 'salePrice'), card.salePrice) } : null)
      if (!typed) continue
      const before = status?.offerId ? { listPrice: status.listPrice, salePrice: status.salePrice } : panel
      push(row.key, before.salePrice, typed.salePrice)
      push(row.key, before.listPrice, typed.listPrice)
    }
  }
  return changes
}
