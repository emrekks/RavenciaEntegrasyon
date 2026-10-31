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
