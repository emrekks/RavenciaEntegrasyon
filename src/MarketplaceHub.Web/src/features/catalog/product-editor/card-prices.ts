// The card's own Liste Fiyatı and Satış Fiyatı (section 2 of the product editor). On a card without variants they are
// its price; on a card with variants they are the card's default price (Product.DefaultListPrice/DefaultSalePrice) and
// the first price of every variant row added later (buildVariantGenerationDefaults). Saving them never sends a price to
// a marketplace: a variant's own row price is what a store gets.

// A typed price: Turkish decimal comma ("549,90") or a point; "1.249,90" reads as 1249.9. Empty is 0 (not filled); text
// that is not a number is NaN.
export function priceInputNumber(value: string): number {
  const text = value.trim().replace(/\s/g, '')
  if (!text) return 0
  const normalized = text.includes(',') ? text.replace(/\./g, '').replace(',', '.') : text
  return /^-?(\d*\.?\d+|\d+\.)$/.test(normalized) ? Number(normalized) : Number.NaN
}

export type CardPrices = { listPrice: number; salePrice: number; issue: string | null }

// The card's prices as saved and as a new variant row takes them: an empty list price takes the sale price (like a new
// row does), and a filled list price is never below the sale price, as on every variant row.
export function cardPrices(listText: string, saleText: string): CardPrices {
  const list = priceInputNumber(listText)
  const sale = priceInputNumber(saleText)
  if (!Number.isFinite(list) || !Number.isFinite(sale)) return { listPrice: 0, salePrice: 0, issue: 'Liste ve satış fiyatı sayı olmalıdır (ondalık için virgül kullanılabilir).' }
  if (list < 0 || sale < 0) return { listPrice: 0, salePrice: 0, issue: 'Fiyatlar negatif olamaz.' }
  const listPrice = list > 0 ? list : sale
  return { listPrice, salePrice: sale, issue: listPrice < sale ? 'Liste fiyatı satış fiyatından küçük olamaz.' : null }
}
