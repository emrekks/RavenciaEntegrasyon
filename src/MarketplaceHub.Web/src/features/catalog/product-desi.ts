// The one desi limit of every catalog path (server ProductDesi): the product editor, Toplu Değer Ata and the Excel import
// take a desi above 0 and at most 9999, with two decimals.
export const maxProductDesi = 9999

export const productDesiTooLargeMessage = 'Desi en fazla 9.999 olabilir.'

export function productDesiTooLarge(value: string) {
  return parseProductDesi(value) > maxProductDesi
}

// A desi as the owner types it: "0.5" and "0,5" are both a half; anything that is not a number is 0.
export function parseProductDesi(value: string): number {
  const desi = Number(value.trim().replace(',', '.'))
  return Number.isFinite(desi) ? desi : 0
}
