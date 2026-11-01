import type { VariantPlatformStatus } from '../shared'

// Pazaryerlerine Özel Fiyat on a new card: a marketplace with an entry has its own price, one without sells at the
// main (panel) price. Every switch starts off and its two inputs show 0 until the user turns it on and types a price.
export type ChannelPricingDraft = { listPrice: string; salePrice: string }

export const emptyChannelPricingDraft: ChannelPricingDraft = { listPrice: '0', salePrice: '0' }

// What the inputs show: the special price when the switch is on, else 0 (the panel price is used, not these).
export function channelPricingInputs(draft: ChannelPricingDraft | undefined): ChannelPricingDraft {
  return draft ?? emptyChannelPricingDraft
}

// A copied card keeps only the marketplaces whose saved price differs from the main price; a platform that sold at
// the main price (or has no price) starts off like on any new card.
export function copiedChannelPricing(platforms: VariantPlatformStatus[], listPrice: number, salePrice: number): Record<string, ChannelPricingDraft> {
  return Object.fromEntries(platforms.flatMap(platform => {
    if (!platform.connectionId) return []
    const platformList = platform.listPrice ?? listPrice
    const platformSale = platform.salePrice ?? salePrice
    if (platformList === listPrice && platformSale === salePrice) return []
    return [[platform.connectionId, { listPrice: String(platformList), salePrice: String(platformSale) }] as const]
  }))
}

export type SavedChannelOffer = { id: string; version: number; listPrice: number; salePrice: number; currency: string }
// One saved (variant, marketplace) offer written into the form's rows at once: when a later write of the same save
// fails, the offers already written keep their new versions, so the next try does not send a stale version (412).
export function rowsWithSavedOffer<R extends { key: string; platformStatuses?: VariantPlatformStatus[] | null }>(rows: ReadonlyArray<R>, rowKey: string, connectionId: string, saved: SavedChannelOffer): R[] {
  return rows.map(row => row.key !== rowKey ? row : {
    ...row,
    platformStatuses: row.platformStatuses?.map(platform => platform.connectionId === connectionId
      ? { ...platform, offerId: saved.id, listPrice: saved.listPrice, salePrice: saved.salePrice, currency: saved.currency, offerVersion: saved.version }
      : platform)
  })
}

// The Idempotency-Key of a new offer's POST, kept per (variant, marketplace) slot: a resend of the same price after a
// lost answer reuses it (the offer is created once), a different price gets a new key (the server refuses one key
// with another body). The caller forgets the slot once the server answered.
export function channelOfferPostKey(store: Map<string, { key: string; body: string }>, slot: string, body: string, freshKey: () => string) {
  const previous = store.get(slot)
  if (previous && previous.body === body) return previous.key
  const key = freshKey()
  store.set(slot, { key, body })
  return key
}
