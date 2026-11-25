// The marketplace step of a save (Yayınla, Platformlarda Güncelle): each chosen store in turn, its update or publication
// jobs queued card by card, every outcome recorded for the save's result. Moved out of useEditorSubmit unchanged.
import type { QueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi } from '../../../shared/api'
import { newIdempotencyKey, newSendCorrelationId, sendCorrelationHeader } from '../../../shared/idempotency'
import { recordProductUpdateNotices, type ProductUpdateAccepted } from './product-update-notices'
import { type FollowedJob } from '../../../shared/job-outcome'
import { platformUpdatePlan, productInformationLabel } from './platform-update-fields'
import { publishCardPlan } from './publish-card-variants'
import { isPublicationLive, isSkippedByPublish } from '../publication-status'
import { familyCardsInPanelOrder } from '../family-card-order'
import { holdsFamilyAsOneProduct, type MarketplaceFamilyUpdate, type MarketplacePublishOutcome, type MarketplaceUpdateOutcome, type PublicationAccepted } from '../product-publication-submit'
import { familyCardName, familyUpdatePlanWithLinks } from './family-update-plan'
import { AcceptedJob, Product, preferredColorOption, variantOptionEntries } from '../shared'
import { queryKeys } from '../../../shared/api/query-keys'
import { type ProductUpdateRequest, type PublicationStatus, type SavedListingProfile, type VariantDraft } from './editor-types'
import { productSaveToastKey, republishListingProfileBody } from './editor-helpers'
import type { EditorStateResult } from './use-editor-state'
import type { EditorDataResult } from './use-editor-data'

export type MarketplaceSends = {
  publicationConnectionIds: string[]
  connectionPlatformName: (connectionId: string) => string
  platformUpdate: ProductUpdateRequest | null
  showFeedback: EditorStateResult['showFeedback']
  updates: MarketplaceUpdateOutcome[]
  connections: EditorDataResult['connections']
  product: Product
  productToEdit: EditorDataResult['productToEdit']
  linkRowsBySku: Map<string, VariantDraft>
  followedJobs: FollowedJob[]
  form: EditorStateResult['form']
  client: QueryClient
  publications: MarketplacePublishOutcome[]
  publicationErrors: Array<{ platformName: string; message: string }>
}

export async function sendToMarketplaces({ publicationConnectionIds, connectionPlatformName, platformUpdate, showFeedback, updates, connections, product, productToEdit, linkRowsBySku, followedJobs, form, client, publications, publicationErrors }: MarketplaceSends) {
  for (const [connectionIndex, connectionId] of publicationConnectionIds.entries()) {
    const platformName = connectionPlatformName(connectionId)
    // The jobs this send queues on this store share one id: İşlem Takibi lists the model's cards as one row.
    const sendHeaders = sendCorrelationHeader(newSendCorrelationId())
    const progress = publicationConnectionIds.length > 1 ? ` (${connectionIndex + 1}/${publicationConnectionIds.length})` : ''
    showFeedback(platformUpdate ? 'Güncelleme Kuyruğa Alınıyor' : 'Yayın Kuyruğa Alınıyor', platformUpdate ? `${platformName}: güncelleme işleri kuyruğa alınıyor${progress}…` : `${platformName}: yayın işleri kuyruğa alınıyor${progress}…`, 'info', { persistent: true, key: productSaveToastKey })
    const update: MarketplaceUpdateOutcome = { platformName, queued: { productInformation: false, priceInventory: false } }
    try {
      if (platformUpdate) {
        updates.push(update)
        // Only what this marketplace takes of the pick goes: the product fields it carries, and the price and/or stock.
        const updatePlatformCode = connections.data?.items.find(item => item.id === connectionId)?.platformCode
        const plan = platformUpdatePlan(updatePlatformCode ?? '', platformUpdate.fields)
        if (plan.productFields.length) {
          // The editor shows the model family as one product, so the product information goes for every colour
          // card of it that is on this marketplace (family-update-plan.ts); a card not there is named, and one
          // card's refusal does not stop the others.
          const informationLabel = productInformationLabel(plan.productFields)
          update.productInformationLabel = informationLabel
          const familyPlan = familyUpdatePlanWithLinks({ openedProductId: product.id, familyProductIds: productToEdit.data?.familyProductIds, variants: product.variants, linkRowsBySku, connectionId, platformCode: updatePlatformCode })
          const familyFailures = new Map<string, string[]>()
          const family: MarketplaceFamilyUpdate = { cards: familyPlan.familyCards, queuedCards: 0, queuedVariants: 0, absent: familyPlan.absent.map(familyCardName), failures: familyFailures }
          update.family = family
          // The cards of one send share an id: Trendyol takes them in one request per step (TrendyolUpdateGroups).
          const sendGroupId = familyPlan.send.length > 1 ? newIdempotencyKey() : undefined
          for (const card of familyPlan.send) {
            const cardName = familyCardName(card)
            try {
              const accepted = await hubApi<ProductUpdateAccepted>(`/products/${card.productId}/update-jobs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey(), ...sendHeaders }, body: JSON.stringify({ connectionId, fields: plan.productFields, sendGroupId }) })
              recordProductUpdateNotices(accepted, { productId: card.productId, productName: form.title, modelCode: form.modelCode, platformCode: updatePlatformCode, connectionId })
              if (accepted?.jobId) followedJobs.push({ jobId: accepted.jobId, platformName, what: informationLabel, update: true, ...(familyPlan.send.length > 1 ? { card: cardName } : {}) })
              family.queuedCards++
              family.queuedVariants += card.variants
              update.queued.productInformation = true
              await client.invalidateQueries({ queryKey: queryKeys.publicationStatus(card.productId, connectionId) })
            } catch (reason) {
              const failure = reason instanceof Error ? reason.message : 'Güncelleme işi oluşturulamadı.'
              familyFailures.set(failure, [...(familyFailures.get(failure) ?? []), cardName])
            }
          }
          // A product without a family keeps its old line: its single refusal is the marketplace's error.
          if (familyPlan.familyCards <= 1 && familyFailures.size) { update.family = undefined; throw new Error([...familyFailures.keys()][0]) }
        }
        if (plan.price || plan.stock) {
          const priceInventoryJob = await hubApi<AcceptedJob>(`/products/${product.id}/connections/${connectionId}/price-inventory-sync-jobs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ price: plan.price, stock: plan.stock }) })
          if (priceInventoryJob?.jobId) followedJobs.push({ jobId: priceInventoryJob.jobId, platformName, what: plan.price && plan.stock ? 'fiyat-stok' : plan.price ? 'fiyat' : 'stok', update: true })
          update.queued.priceInventory = true
          update.priceInventoryParts = { price: plan.price, stock: plan.stock }
        }
        await client.invalidateQueries({ queryKey: queryKeys.publicationStatus(product.id, connectionId) })
        continue
      }
      // Every colour card of the family (same model code) goes with the opened one; a card already live or still
      // being published on this marketplace is left as it is. One card's failure does not stop the others.
      // The cards are queued in the panel's colour order (not the opened card first), so a marketplace that takes
      // them as they come lists the colours as the products page does.
      const familyIds = familyCardsInPanelOrder([product.id, ...(productToEdit.data?.familyProductIds ?? [])], product.variants, product.id)
      // The same reason on several cards (a connection setting missing) is said once, with how many cards it stopped.
      const familyFailures = new Map<string, number[]>()
      const platformCode = connections.data?.items.find(item => item.id === connectionId)?.platformCode
      const outcome: MarketplacePublishOutcome = { platformName, platformCode: platformCode ?? '', familyCards: familyIds.length, openedCard: familyIds.indexOf(product.id), enqueuedCards: 0, enqueuedVariants: 0, skipped: { live: 0, running: 0, matched: 0 }, failures: familyFailures }
      publications.push(outcome)
      for (const [familyIndex, familyProductId] of familyIds.entries()) {
        try {
          // Every marketplace: a card whose variants are all there is left as it is; a card partly there sends only the
          // variants not there yet (new ones, or ones the marketplace lost), which the server adds to its listing.
          const cardVariants = product.variants.filter(variant => (variant.productId ?? product.id) === familyProductId)
          // The variants not there yet are sorted by colour: new sizes join their colour's listing, a new colour opens
          // its own listing (Trendyol, Hepsiburada) or joins the one product (Shopify, n11, Pazarama, PttAVM); on a
          // family split into colour cards a variant of another colour belongs on its own card.
          const plan = publishCardPlan(cardVariants.map(variant => ({
            platformStatuses: linkRowsBySku.get(variant.sku.trim().toLocaleUpperCase('tr-TR'))?.platformStatuses,
            color: preferredColorOption(variantOptionEntries(variant))?.value,
            sku: variant.sku
          })), connectionId, { colourCardsInFamily: familyIds.length > 1, familyIsOneProduct: holdsFamilyAsOneProduct(platformCode) })
          if (!plan.send && plan.reason === 'matched') { outcome.skipped.matched++; continue }
          if (!plan.send) { familyFailures.set(plan.message, [...(familyFailures.get(plan.message) ?? []), familyIndex]); continue }
          const status = await hubApi<PublicationStatus>(`/products/${familyProductId}/publication-status/${connectionId}`).catch(() => null)
          // A PttAVM family card shown as "Tekrar Deneyin" goes again; the API closes its waiting approval checks.
          const pttAvmFamily = familyIds.length > 1 || cardVariants.length > 1
          if (status && isSkippedByPublish(platformCode, status.actualStatus, status.lastJobStatus, pttAvmFamily, plan.hasMissingVariant || plan.addsToListing)) {
            if (isPublicationLive(status.actualStatus)) outcome.skipped.live++
            else outcome.skipped.running++
            continue
          }
          // A saved profile keeps its overrides (title, description, category, brand, delivery time) on a re-publish;
          // only a new profile starts without any.
          let listingProfile: SavedListingProfile | undefined
          try {
            listingProfile = await hubApi<SavedListingProfile>(`/products/${familyProductId}/listing-profiles/${connectionId}`)
          } catch (reason) {
            if (!(reason instanceof ApiRequestError) || reason.status !== 404) throw reason
          }
          await hubApi(`/products/${familyProductId}/listing-profiles/${connectionId}`, { method: 'PUT', headers: listingProfile == null ? {} : { 'If-Match': `"v${listingProfile.version}"` }, body: JSON.stringify(republishListingProfileBody(listingProfile)) })
          const publicationJob = await hubApi<PublicationAccepted>(`/products/${familyProductId}/publication-jobs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey(), ...sendHeaders }, body: JSON.stringify({ connectionId }) })
          await client.invalidateQueries({ queryKey: queryKeys.publicationStatus(familyProductId, connectionId) })
          if (publicationJob?.jobId) followedJobs.push({ jobId: publicationJob.jobId, platformName, what: 'ürün', card: familyIds.length > 1 ? preferredColorOption(variantOptionEntries(cardVariants[0] ?? product.variants[0]))?.value ?? '' : undefined })
          outcome.enqueuedCards++
          outcome.enqueuedVariants += plan.variants
          if (plan.addsToListing) {
            outcome.addedToListingCards = (outcome.addedToListingCards ?? 0) + 1
            outcome.liveVariants = (outcome.liveVariants ?? 0) + plan.liveVariants
            outcome.groups = [...(outcome.groups ?? []), ...plan.groups]
          }
          // The variants the server left out (no price, barcode, pictures of a new colour …), each with its reason.
          if (publicationJob?.skippedVariants?.length) outcome.skippedVariants = [...(outcome.skippedVariants ?? []), ...publicationJob.skippedVariants]
        } catch (reason) {
          const failure = reason instanceof Error ? reason.message : 'Yayın işi oluşturulamadı.'
          familyFailures.set(failure, [...(familyFailures.get(failure) ?? []), familyIndex])
        }
      }
      continue
    } catch (reason) {
      const failure = reason instanceof Error ? reason.message : platformUpdate ? 'Güncelleme işi oluşturulamadı.' : 'Yayın işi oluşturulamadı.'
      if (platformUpdate) update.error = failure
      else publicationErrors.push({ platformName, message: failure })
    }
  }
}
