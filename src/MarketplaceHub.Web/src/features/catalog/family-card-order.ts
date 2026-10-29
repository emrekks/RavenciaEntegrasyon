import { compareLabelsAlphabetically, preferredColorOption, variantOptionEntries, type Variant } from './shared'

// The colour cards of a model family in the order the panel lists their colours (product list, quick edit, the
// editor's variant rows): alphabetically by colour, cards without a colour last, the family's own order otherwise.
// Yayınla queues the cards in this order, so a marketplace that takes them as they come gets the panel's order; the
// API sends Pazarama's cards one by one in the same order (ProductFamilyColorOrder).
export function familyCardsInPanelOrder(productIds: readonly string[], variants: ReadonlyArray<Pick<Variant, 'productId' | 'optionSignature' | 'options'>>, openedProductId: string) {
  const colorOf = (productId: string) => {
    for (const variant of variants) {
      if ((variant.productId ?? openedProductId) !== productId) continue
      const color = preferredColorOption(variantOptionEntries(variant))?.value.trim()
      if (color) return color
    }
    return null
  }
  const cards = [...new Set(productIds)].map((productId, index) => ({ productId, index, color: colorOf(productId) }))
  return cards
    .sort((left, right) => {
      if (left.color && right.color) return compareLabelsAlphabetically(left.color, right.color) || left.index - right.index
      if (left.color || right.color) return left.color ? -1 : 1
      return left.index - right.index
    })
    .map(card => card.productId)
}
