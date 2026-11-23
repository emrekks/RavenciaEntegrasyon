import { parseTrEntry } from '../../../shared/tr-number'
import { readMoneyField } from './seeded-fields'

// The card's own Liste Fiyatı and Satış Fiyatı (section 2 of the product editor). On a card without variants they are
// its price; on a card with variants they are the card's default price (Product.DefaultListPrice/DefaultSalePrice) and
// the first price of every variant row added later (buildVariantGenerationDefaults). Saving them never sends a price to
// a marketplace: a variant's own row price is what a store gets.

// A typed card price (a text box), read by the one rule of the product screens' typed prices (parseTrEntry): "549,90" is
// 549.9, "1.250" is 1250, "1.249,90" is 1249.9. A point used as a decimal point ("549.9") could mean two things and is
// NaN, as is text that is not a number; empty is 0 (not filled). The editor fills the boxes as Turkish text
// (priceFieldText), so a loaded price reads back unchanged.
export const priceInputNumber = (value: string | number): number => typeof value === 'number' ? value : value.trim() === '' ? 0 : parseTrEntry(value) ?? Number.NaN

export type CardPrices = { listPrice: number; salePrice: number; issue: string | null }

// The card's prices as saved and as a new variant row takes them: an empty list price takes the sale price (like a new
// row does), and a filled list price is never below the sale price, as on every variant row.
export function cardPrices(listText: string, saleText: string): CardPrices {
  // An untouched box reads as loaded (seeded-fields.ts: a stored 99.999 is never "unreadable"); a typed one by the rule.
  const list = readMoneyField('listPrice', listText)
  const sale = readMoneyField('salePrice', saleText)
  if (!Number.isFinite(list) || !Number.isFinite(sale)) return { listPrice: 0, salePrice: 0, issue: 'Liste ve satış fiyatı okunamadı; nokta yalnız binlik ayırıcıdır, ondalık için virgül kullanın (1.250 ya da 1.250,50).' }
  if (list < 0 || sale < 0) return { listPrice: 0, salePrice: 0, issue: 'Fiyatlar negatif olamaz.' }
  const listPrice = list > 0 ? list : sale
  return { listPrice, salePrice: sale, issue: listPrice < sale ? 'Liste fiyatı satış fiyatından küçük olamaz.' : null }
}
