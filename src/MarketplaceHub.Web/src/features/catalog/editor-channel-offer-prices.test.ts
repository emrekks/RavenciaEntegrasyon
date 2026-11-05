import { describe, expect, it } from 'vitest'
import { editorChannelOfferPrices, editorOfferUnchanged, initialCustomChannels, offerFollowsPanel, panelFollowerColumns, panelFollowerWrites, quickPriceColumns, typedOfferPriceChoice } from './quick-price-edit'

// A new product, panel price 100, store N11 not switched to "Özel Fiyat", and variant B given 150 in the variant price
// table: the arguments the product editor passes at its offer write.
describe('a variant price typed in the price table of a new product', () => {
  const panel = { listPrice: 100, salePrice: 100 }
  const written = editorChannelOfferPrices({ custom: false, panel, card: panel, cardEdited: true, variantPrice: { listPrice: 150, salePrice: 150 }, offer: undefined })

  it('is saved as the marketplace own price, as the edit page stores it', () => {
    expect(typedOfferPriceChoice({ listPrice: 150, salePrice: 150 }, panel, true)).toBe(false)
    expect(written).toEqual({ listPrice: 150, salePrice: 150, followsPanelPrice: false })
  })

  it('so a later panel price save leaves the typed price alone', () => {
    const variant = { defaultListPrice: 100, defaultSalePrice: 100, platformStatuses: [{ connectionId: 'n11', platformCode: 'N11', offerId: 'oB', offerVersion: 1, listPrice: written.listPrice, salePrice: written.salePrice, followsPanelPrice: written.followsPanelPrice }] }
    expect(offerFollowsPanel(variant.platformStatuses[0], panel, true)).toBe(false)
    const columns = quickPriceColumns([{ id: 'n11', displayName: 'N11', platformCode: 'N11' }])
    const custom = initialCustomChannels([{ variant, product: {} }], columns)
    expect(custom).toEqual({ n11: true })
    expect(panelFollowerWrites(variant, panelFollowerColumns(columns, custom), { listPrice: 120, salePrice: 120 })).toEqual([])
  })

  it('stays with the panel when it equals the variant panel price, and is the own price on an Özel Fiyat store', () => {
    expect(editorChannelOfferPrices({ custom: false, panel, card: panel, cardEdited: false, variantPrice: { listPrice: 100, salePrice: 100 } }).followsPanelPrice).toBe(true)
    expect(editorChannelOfferPrices({ custom: true, panel, card: panel, cardEdited: false, variantPrice: { listPrice: 100, salePrice: 100 } }).followsPanelPrice).toBe(false)
    // A sale-price-only marketplace compares the sale price alone.
    expect(editorChannelOfferPrices({ custom: false, panel: { listPrice: 120, salePrice: 100 }, card: panel, cardEdited: false, variantPrice: { listPrice: 100, salePrice: 100 }, takesListPrice: false }).followsPanelPrice).toBe(true)
  })
})

describe('a store that follows the panel', () => {
  it('takes each variant its own panel price, the card price only when switched to Özel Fiyat', () => {
    const card = { listPrice: 100, salePrice: 100 }
    expect(editorChannelOfferPrices({ custom: false, panel: { listPrice: 200, salePrice: 200 }, card, cardEdited: false })).toEqual({ listPrice: 200, salePrice: 200, followsPanelPrice: true })
    expect(editorChannelOfferPrices({ custom: true, panel: { listPrice: 200, salePrice: 200 }, card, cardEdited: true, offer: { listPrice: 180, salePrice: 180 } })).toEqual({ listPrice: 100, salePrice: 100, followsPanelPrice: false })
  })
})

describe('editorOfferUnchanged', () => {
  const offer = { listPrice: 100, salePrice: 90, currency: 'TRY', vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: 'HALF_EVEN', safetyStock: 0, offerStatus: 'ACTIVE', followsPanelPrice: true }
  const command = { listPrice: 100, salePrice: 90, currency: 'TRY', vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: 'HALF_EVEN', safetyStock: 0, status: 'ACTIVE', followsPanelPrice: true }

  it('skips an offer the write would leave as it is', () => {
    expect(editorOfferUnchanged(offer, command)).toBe(true)
    expect(editorOfferUnchanged({ ...offer, currency: 'try' }, command)).toBe(true)
  })

  it('writes a new price, policy, status or Özel Fiyat choice, and an offer whose values are unknown', () => {
    expect(editorOfferUnchanged(offer, { ...command, salePrice: 95 })).toBe(false)
    expect(editorOfferUnchanged(offer, { ...command, vatRate: 10 })).toBe(false)
    expect(editorOfferUnchanged(offer, { ...command, safetyStock: 1 })).toBe(false)
    expect(editorOfferUnchanged({ ...offer, offerStatus: 'PASSIVE' }, command)).toBe(false)
    expect(editorOfferUnchanged(offer, { ...command, followsPanelPrice: false })).toBe(false)
    expect(editorOfferUnchanged({ ...offer, followsPanelPrice: null }, command)).toBe(false)
    expect(editorOfferUnchanged({ ...offer, salePrice: null }, command)).toBe(false)
  })
})
