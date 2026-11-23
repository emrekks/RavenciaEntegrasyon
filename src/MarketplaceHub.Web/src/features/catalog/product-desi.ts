import { parseTrMeasure } from '../../shared/tr-number'
// The one desi limit of every catalog path (server ProductDesi): the product editor, Toplu Değer Ata and the Excel import
// take a desi above 0 and at most 9999, with two decimals.
export const maxProductDesi = 9999

export const productDesiTooLargeMessage = 'Desi en fazla 9.999 olabilir.'

export function productDesiTooLarge(value: string) {
  return parseProductDesi(value) > maxProductDesi
}

// A desi as the owner types it, in Turkish: "0,5" is a half, "1.250" is 1250 (never 1.25); a point used as a decimal point
// ("0.5") could mean two things and, like anything that is not a number, is 0 (the desi checks then ask for one).
export function parseProductDesi(value: string): number {
  return parseTrMeasure(value.trim()) ?? 0
}
