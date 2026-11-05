import { formatColorOptionValue } from './variant-option-matching'

export type AttributeOptionValue = { id: string; value: string }

// Case, accents and the Turkish dotted/dotless i aside: "murdum" finds "Mürdüm", "INDIGO" finds "İndigo".
export function foldAttributeValueText(value: string) {
  return value
    .normalize('NFKC')
    .replace(/^["“”]+|["“”]+$/g, '')
    .trim()
    .replace(/\s+/g, ' ')
    .toLocaleLowerCase('tr-TR')
    .replace(/ı/g, 'i')
    .normalize('NFKD')
    .replace(/\p{M}/gu, '')
}

export function filterAttributeOptionValues<T extends AttributeOptionValue>(values: T[], query: string): T[] {
  const normalizedQuery = foldAttributeValueText(query)
  if (!normalizedQuery) return values

  return values.filter(option => foldAttributeValueText(option.value).includes(normalizedQuery))
}

const colorNames = ['RENK', 'RENKLER', 'COLOR', 'COLORS', 'COLOUR', 'COLOURS']
const sizeNames = ['BEDEN', 'BEDENLER', 'SIZE', 'SIZES', 'BOYUT', 'BOYUTLAR', 'NUMARA', 'NUMARALAR']

// The value a search box offers to add to an option group (Renk, Beden) when no value of the group is the typed one:
// a colour in Title Case ("zümrüt yeşili" → "Zümrüt Yeşili"), a size in capitals ("xxl" → "XXL"), anything else as typed.
// Null when the box is empty or the group already holds the value in any spelling. A new value is the panel's own; it is
// mapped to no marketplace value (the user maps it in Seçenek Eşitleme).
export function optionValueToAdd(attributeName: string, values: readonly AttributeOptionValue[], query: string): string | null {
  const typed = query.normalize('NFKC').replace(/^["“”]+|["“”]+$/g, '').trim().replace(/\s+/g, ' ')
  if (!typed || typed.length > 120) return null
  const key = foldAttributeValueText(typed)
  if (values.some(value => foldAttributeValueText(value.value) === key)) return null
  const name = attributeName.replace(/[\s_-]+/g, '').toLocaleUpperCase('tr-TR')
  if (colorNames.includes(name)) return formatColorOptionValue(typed)
  if (sizeNames.includes(name)) return typed.toLocaleUpperCase('tr-TR')
  return typed
}
