export type VariantOptionEntry = { name: string; value: string }

function normalizeOptionName(name: string) {
  return name.replace(/[\s_-]+/g, '').toLocaleUpperCase('tr-TR')
}

export function mergeVariantOptionEntries(options: VariantOptionEntry[], signatureOptions: VariantOptionEntry[]) {
  const merged = new Map<string, VariantOptionEntry>()
  for (const option of [...options, ...signatureOptions]) {
    const name = option.name.trim()
    const value = option.value.trim()
    if (!name || !value) continue
    merged.set(normalizeOptionName(name), { name, value })
  }
  return [...merged.values()]
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
