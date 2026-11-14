// Shopify's categories are the store's own product types (free text). On Eşleştirmeler › Kategori Eşleme › Shopify a
// panel category is mapped to one of them, or to a type typed here that the first publication creates in the store.

export type ShopifyExactMatchView = {
  matchCount: number
  withoutTypeCount: number
  ambiguousCount: number
  applied: boolean
  matches: Array<{ categoryId: string; categoryName: string; productType: string }>
}

export const shopifyProductTypeMaxLength = 255

// The server compares names the same way (ShopifyProductTypeNames.Key): trimmed, inner spaces collapsed, Turkish lower case.
export function productTypeKey(text: string) {
  return text.trim().split(/\s+/).filter(Boolean).join(' ').toLocaleLowerCase('tr-TR')
}

// The search text offered as a new product type: only when it is a usable name the store's list does not hold already.
export function typedProductTypeOption(search: string, productTypes: ReadonlyArray<{ externalId: string }>) {
  const typed = search.trim()
  if (!typed || typed.length > shopifyProductTypeMaxLength || [...typed].some(character => character < ' ')) return null
  const key = productTypeKey(typed)
  return productTypes.some(item => productTypeKey(item.externalId) === key) ? null : typed
}

function skippedParts(view: ShopifyExactMatchView) {
  return [
    view.withoutTypeCount ? `${view.withoutTypeCount.toLocaleString('tr-TR')} kategorinin Shopify'da aynı adlı türü yok` : '',
    view.ambiguousCount ? `${view.ambiguousCount.toLocaleString('tr-TR')} kategori için birden fazla aynı adlı tür var` : ''
  ].filter(Boolean)
}

// The confirmation before "Birebir Eşle" saves anything; null when there is nothing to map.
export function exactMatchConfirmMessage(view: ShopifyExactMatchView) {
  if (!view.matchCount) return null
  const examples = view.matches.slice(0, 5).map(match => `${match.categoryName} → ${match.productType}`).join(', ')
  const more = view.matchCount > 5 ? ` ve ${(view.matchCount - 5).toLocaleString('tr-TR')} kategori daha` : ''
  const skipped = skippedParts(view)
  return `${view.matchCount.toLocaleString('tr-TR')} panel kategorisi Shopify'daki aynı adlı ürün türüyle eşlenecek: ${examples}${more}.${skipped.length ? ` ${skipped.join(', ')}; bunlar eşlenmeyecek.` : ''} Mevcut eşlemeler değişmez.`
}

// The result toast, e.g. "12 kategori eşlendi, 3 kategorinin Shopify'da aynı adlı türü yok".
export function exactMatchResultMessage(view: ShopifyExactMatchView) {
  const head = view.matchCount ? `${view.matchCount.toLocaleString('tr-TR')} kategori eşlendi` : 'Eşlenecek aynı adlı kategori bulunamadı'
  return [head, ...skippedParts(view)].join(', ') + '.'
}
