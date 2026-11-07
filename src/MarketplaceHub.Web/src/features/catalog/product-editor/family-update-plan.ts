import { familyCardsInPanelOrder } from '../family-card-order'
import { preferredColorOption, variantOptionEntries, type Variant, type VariantPlatformStatus } from '../shared'
import { publishCardVariantLinks } from './publish-card-variants'

// "Platformlarda Güncelle" on a model family. The editor shows the colour cards of one model code as one product
// ("7 seçenek · 21 varyant"): Kaydet writes the variant rows, codes, desi, prices and pictures of every card, and Yayınla
// sends every card. Live 7 Nov 2026 (RYP003T): the product information update went for the opened card only (3 of 21
// variants) and nothing said so. It now goes for every card of the family that is on the marketplace, each as its own
// job; a card with no variant there is not sent and is named by its colour.
//
// Shopify and PttAVM build one marketplace product from the whole family on any card's update (Shopify's productSet,
// PttAVM's parent barcode), so there the opened card's update alone carries the family, as before.
const familyFromOneCardPlatforms = new Set(['SHOPIFY', 'PTTAVM'])

export function updatesFamilyFromOneCard(platformCode: string | null | undefined) {
  return familyFromOneCardPlatforms.has(platformCode?.trim().toUpperCase() ?? '')
}

type FamilyVariant = Pick<Variant, 'productId' | 'optionSignature' | 'options' | 'sku'> & { platformStatuses?: VariantPlatformStatus[] | null }

export type FamilyUpdateCard = { productId: string; colour: string | null; variants: number }

export type FamilyUpdatePlan = {
  /** Cards the update goes to, in the panel's colour order. */
  send: FamilyUpdateCard[]
  /** Cards with no variant on the marketplace: not sent, named in the result. */
  absent: FamilyUpdateCard[]
  /** Colour cards of the family (1 for a product without one). */
  familyCards: number
  /** Variants of the whole family. */
  familyVariants: number
}

function cardColour(variants: readonly FamilyVariant[]) {
  for (const variant of variants) {
    const colour = preferredColorOption(variantOptionEntries(variant))?.value.trim()
    if (colour) return colour
  }
  return null
}

// openedProductId: the card the editor is on; familyProductIds: the other cards of the family (the API's
// familyProductIds); variants: the family's variants with their latest marketplace links.
export function familyUpdatePlan({ openedProductId, familyProductIds, variants, connectionId, platformCode }: {
  openedProductId: string
  familyProductIds: readonly string[] | null | undefined
  variants: readonly FamilyVariant[]
  connectionId: string
  platformCode: string | null | undefined
}): FamilyUpdatePlan {
  const ids = familyCardsInPanelOrder([openedProductId, ...(familyProductIds ?? [])], variants, openedProductId)
  const cards = ids.map(productId => {
    const cardVariants = variants.filter(variant => (variant.productId ?? openedProductId) === productId)
    return { productId, colour: cardColour(cardVariants), variants: cardVariants.length, present: publishCardVariantLinks(cardVariants, connectionId).linked > 0 }
  })
  const familyVariants = cards.reduce((sum, card) => sum + card.variants, 0)
  const strip = ({ productId, colour, variants: count }: (typeof cards)[number]): FamilyUpdateCard => ({ productId, colour, variants: count })
  if (updatesFamilyFromOneCard(platformCode)) {
    const opened = cards.find(card => card.productId === openedProductId)
    return { send: opened ? [{ ...strip(opened), variants: familyVariants }] : [], absent: [], familyCards: cards.length, familyVariants }
  }
  // The opened card always goes when it is the only card: a product without a family is updated as before, and the
  // server says why when it is not on the marketplace (no link is never read as "not there" for the card itself).
  if (cards.length <= 1) return { send: cards.map(strip), absent: [], familyCards: cards.length, familyVariants }
  return {
    send: cards.filter(card => card.present).map(strip),
    absent: cards.filter(card => !card.present).map(strip),
    familyCards: cards.length,
    familyVariants
  }
}

/** The colour a card is named by in the result ("Siyah"). */
export function familyCardName(card: FamilyUpdateCard) {
  return card.colour ?? 'renksiz kart'
}

/** The line the update window shows under a marketplace: which colours and variants the update reaches there. */
export function familyUpdateReach(plan: FamilyUpdatePlan) {
  if (plan.familyCards <= 1) return ''
  const sentVariants = plan.send.reduce((sum, card) => sum + card.variants, 0)
  const reach = plan.absent.length === 0
    ? `${plan.familyCards} rengin tamamı (${sentVariants} varyant) güncellenir`
    : plan.send.length
      ? `${plan.familyCards} renkten ${plan.send.length} renk (${sentVariants} varyant) güncellenir`
      : 'Hiçbir renk güncellenmez'
  const absent = plan.absent.length ? `; bu mağazada olmayan ${plan.absent.length} renk gönderilmez: ${plan.absent.map(familyCardName).join(', ')}` : ''
  return `${reach}${absent}.`
}
