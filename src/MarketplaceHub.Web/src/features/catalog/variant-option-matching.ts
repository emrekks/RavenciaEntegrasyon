export type VariantOptionEntry = { name: string; value: string }
export type VariantOptionValue = { id: string; value: string }

function normalizeOptionName(name: string) {
  return name.replace(/[\s_-]+/g, '').toLocaleUpperCase('tr-TR')
}

export function mergeVariantOptionEntries(options: VariantOptionEntry[], signatureFallbackOptions: VariantOptionEntry[]) {
  const merged = new Map<string, VariantOptionEntry>()
  for (const option of options) {
    const name = option.name.trim()
    const value = option.value.trim()
    if (!name || !value) continue
    merged.set(normalizeOptionName(name), { name, value })
  }
  for (const option of signatureFallbackOptions) {
    const name = option.name.trim()
    const value = option.value.trim()
    if (!name || !value) continue
    const key = normalizeOptionName(name)
    if (!merged.has(key)) merged.set(key, { name, value })
  }

  const entries = [...merged.values()]
  const hasRealColorOption = entries.some(option => ['RENK', 'COLOR', 'COLOUR'].includes(normalizeOptionName(option.name)))
  return hasRealColorOption
    ? entries.filter(option => !['WEBCOLOR', 'WEBCOLOUR', 'WEBRENK'].includes(normalizeOptionName(option.name)))
    : entries
}

export function normalizeVariantOptionValue(value: string) {
  return value
    .normalize('NFKC')
    .replace(/^["“”]+|["“”]+$/g, '')
    .trim()
    .replace(/\s+/g, ' ')
    .toLocaleLowerCase('tr-TR')
    .replace(/ı/g, 'i')
}

export function matchingVariantOptionValues<T extends VariantOptionValue>(values: T[], candidates: string[]) {
  const candidateKeys = new Set(candidates.map(normalizeVariantOptionValue).filter(Boolean))
  return values.filter(value => candidateKeys.has(normalizeVariantOptionValue(value.value)))
}

export function formatColorOptionValue(value: string) {
  const normalized = value.normalize('NFKC').trim().replace(/\s+/g, ' ').toLocaleLowerCase('tr-TR')
  return normalized.replace(/(^|[\s\/-])(\p{L})/gu, (_, separator: string, letter: string) => `${separator}${letter.toLocaleUpperCase('tr-TR')}`)
}

export function mergeVariantOptionValues(existing: VariantOptionValue[], incoming: VariantOptionValue[]) {
  const values = new Map<string, VariantOptionValue>()
  for (const item of [...existing, ...incoming]) {
    const key = normalizeVariantOptionValue(item.value)
    if (key && !values.has(key)) values.set(key, item)
  }
  return [...values.values()]
}
