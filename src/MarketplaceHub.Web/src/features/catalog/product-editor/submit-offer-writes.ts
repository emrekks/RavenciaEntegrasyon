// The marketplace prices of a save, after the pictures and the stock (WEB-SAYFALAR-10): the "Özel Fiyat" choice switched
// on a marketplace, and every (variant, marketplace) offer price, a few at a time.
import { ApiRequestError, hubApi } from '../../../shared/api'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { mapWithConcurrency, mediaSaveProgressMessage } from './product-media-save'
import { editorOfferPriceReason, platformUpdateWritesOffers } from './platform-update-fields'
import { channelTakesListPrice, editorChannelOfferPrices, editorOfferUnchanged, offerHasSalePrice, panelFollowChoiceVariantIds, unpricedOfferWarning } from '../quick-price-edit'
import { channelOfferWriteConcurrency } from './editor-helpers'
import { parseEditorNumber } from './editor-number'
import { priceInputNumber } from './card-prices'
import { channelFieldKey, readCountField, readMoneyField } from './seeded-fields'
import { rowsBySku, skuKey } from './submit-media-step'
import type { ChannelPricingDraft } from './marketplace-channel-pricing'
import type { Product } from '../shared'
import type { ProductUpdateRequest, VariantDraft } from './editor-types'
import type { EditorForm } from './editor-form'

type Connection = { id: string; platformCode: string }

// A marketplace switched to or from "Özel Fiyat" stores the choice on the card's offers there, even when no price is
// written to it (no price changes, nothing goes to the marketplace). Returns the marketplaces with their own price now.
export async function runPanelFollowChoiceWrites(input: { connections: Connection[]; channelPricing: Record<string, ChannelPricingDraft>; initialCustomChannelIds: ReadonlySet<string>; rows: VariantDraft[] }) {
  const { channelPricing } = input
  const choiceChanged = input.connections.map(connection => connection.id).filter(connectionId => (connectionId in channelPricing) !== input.initialCustomChannelIds.has(connectionId))
  for (const connectionId of choiceChanged) {
    const followsPanelPrice = !(connectionId in channelPricing)
    const variantIds = panelFollowChoiceVariantIds(input.rows.map(row => ({ id: row.key, platformStatuses: row.platformStatuses })), connectionId, followsPanelPrice)
    if (variantIds.length) await hubApi('/channel-offers/panel-price-follow', { method: 'PUT', body: JSON.stringify({ connectionId, variantIds, followsPanelPrice }) })
  }
  return new Set(Object.keys(channelPricing))
}

// "Platformlarda Güncelle" writes a store's prices only when its send carries a price or the stock: a pick of the
// pictures alone writes none (live 10 Nov 2026, MZ052BOD).
export function offerConnectionIdsFor(platformUpdate: ProductUpdateRequest | null, targetConnectionIds: string[], connections: Connection[]) {
  return platformUpdate
    ? targetConnectionIds.filter(connectionId => platformUpdateWritesOffers(connections.find(item => item.id === connectionId)?.platformCode ?? '', platformUpdate.fields))
    : targetConnectionIds
}

export type OfferWritesInput = {
  product: Product
  rows: VariantDraft[]
  connectionIds: string[]
  connections: Connection[]
  form: Pick<EditorForm, 'currency' | 'vatRate' | 'vatIncluded' | 'safetyStock'>
  platformUpdate: ProductUpdateRequest | null
  channelPricing: Record<string, ChannelPricingDraft>
  variantChannelPricing: Record<string, ChannelPricingDraft>
  channelPriceDraft: (connectionId: string) => ChannelPricingDraft
  channelPriceEdited: (connectionId: string) => boolean
  // The marketplace's Özel Fiyat switch is as the editor opened it, on (each variant keeps its stored choice there).
  choiceUntouched?: (connectionId: string) => boolean
  connectionPlatformName: (connectionId: string) => string
  showProgress: (message: string) => void
}

export const offerChangedMeanwhileMessage = (sku: string, platformName: string) => `${sku}: ${platformName} fiyatı bu form açıkken başka bir işlemle değişti; fiyat yazılmadı. Sayfayı yenileyip tekrar deneyin.`

// Every (variant, store) price is its own row, so they are written a few at a time instead of one by one. Returns the
// warning for the variants left without a price, if any.
export async function runOfferWrites(input: OfferWritesInput): Promise<{ unpricedWarning: string | null }> {
  const { product, connections, form, platformUpdate, channelPricing } = input
  const bySku = rowsBySku(input.rows)
  const offerWrites = product.variants.flatMap(variant => {
    const row = bySku.get(skuKey(variant.sku))
    return row ? input.connectionIds.map(connectionId => ({ variant, row, connectionId })) : []
  })
  let offersWritten = 0
  const unpricedOfferSkus = new Set<string>()
  await mapWithConcurrency(offerWrites, channelOfferWriteConcurrency, async ({ variant, row, connectionId }) => {
    const pricing = input.channelPriceDraft(connectionId)
    const platform = row.platformStatuses?.find(item => item.connectionId === connectionId)
    const variantPricing = input.variantChannelPricing[`${row.key}:${connectionId}`]
    // A marketplace that follows the panel takes this variant's own panel price; the card price is only the price
    // of a marketplace switched to "Özel Fiyat".
    const offerPrices = editorChannelOfferPrices({
      custom: connectionId in channelPricing,
      panel: { listPrice: row.listPrice, salePrice: row.salePrice },
      card: { listPrice: readMoneyField(channelFieldKey(connectionId, 'listPrice'), pricing.listPrice), salePrice: readMoneyField(channelFieldKey(connectionId, 'salePrice'), pricing.salePrice) },
      cardEdited: input.channelPriceEdited(connectionId),
      variantPrice: !platformUpdate && variantPricing ? { listPrice: priceInputNumber(variantPricing.listPrice), salePrice: priceInputNumber(variantPricing.salePrice) } : null,
      offer: platform,
      choiceUntouched: input.choiceUntouched?.(connectionId) ?? false,
      takesListPrice: channelTakesListPrice(connections.find(item => item.id === connectionId)?.platformCode ?? platform?.platformCode)
    })
    // A variant without a panel price (0 or empty in the variant table, often a colour imported without one) has
    // no price to write: the server refuses a sale price of zero, and one such row used to stop the whole save
    // after the card was written (owner 7.11.2026, MZ052BOD: "Satış fiyatı sıfırdan büyük olmalıdır").
    if (!offerHasSalePrice(offerPrices)) { unpricedOfferSkus.add(row.sku); return }
    const offerCommand = { connectionId, variantId: variant.id, listPrice: offerPrices.listPrice, salePrice: offerPrices.salePrice, followsPanelPrice: offerPrices.followsPanelPrice, currency: platform?.currency ?? form.currency ?? 'TRY', vatRate: parseEditorNumber(form.vatRate || platform?.vatRate || 0), vatInclusion: form.vatIncluded || platform?.vatInclusion || 'INCLUDED', roundingMode: platform?.roundingMode ?? 'HALF_EVEN', safetyStock: readCountField('safetyStock', form.safetyStock || platform?.safetyStock || 0), status: 'ACTIVE', reason: editorOfferPriceReason(platformUpdate) }
    // An offer the variant already has is written at the version this form loaded (PATCH + If-Match), so a price
    // changed meanwhile elsewhere is refused instead of overwritten; one the write would leave as it is is skipped.
    // POST only creates: the server refuses it over an offer this form did not know of.
    try {
      if (platform?.offerId) {
        if (platform.offerVersion == null) throw new Error(`${row.sku}: platform fiyat sürümü eksik; fiyat güncellenmedi.`)
        if (!editorOfferUnchanged(platform, offerCommand)) await hubApi(`/channel-offers/${platform.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${platform.offerVersion}"` }, body: JSON.stringify(offerCommand) })
      } else {
        await hubApi('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify(offerCommand) })
      }
    } catch (reason) {
      if (reason instanceof ApiRequestError && (reason.status === 409 || reason.status === 412)) throw new Error(offerChangedMeanwhileMessage(row.sku, input.connectionPlatformName(connectionId)))
      throw reason
    }
    offersWritten++
    if (offerWrites.length > channelOfferWriteConcurrency) input.showProgress(mediaSaveProgressMessage('prices', offersWritten, offerWrites.length))
  })
  return { unpricedWarning: unpricedOfferWarning(unpricedOfferSkus) }
}
