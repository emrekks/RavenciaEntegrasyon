// The one desi limit of every catalog path (server ProductDesi): the product editor, Toplu Değer Ata and the Excel import
// take a desi above 0 and at most 9999, with two decimals.
export const maxProductDesi = 9999

export const productDesiTooLargeMessage = 'Desi en fazla 9.999 olabilir.'

export function productDesiTooLarge(value: string) {
  const desi = Number(value.trim().replace(',', '.'))
  return value.trim() !== '' && Number.isFinite(desi) && desi > maxProductDesi
}
