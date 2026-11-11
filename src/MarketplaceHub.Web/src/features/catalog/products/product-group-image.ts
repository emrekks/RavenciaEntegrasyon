import type { Product } from '../shared'

// A model row's picture (owner, 12.11.2026: "51 de 1. görsel yeşil ama kahve duruyor?"): a model is one card with one
// gallery, so the row shows the family gallery's first picture as the editor does (familyImageUrl, never the size
// chart), not the newest colour card's own; without one, the first colour card's front picture that exists.
export function productGroupImage(group: { primary: Product; products: Product[] }) {
  return group.products.map(product => product.familyImageUrl).find(Boolean)
    ?? group.primary.primaryImageUrl
    ?? group.products.map(product => product.primaryImageUrl).find(Boolean)
    ?? null
}
