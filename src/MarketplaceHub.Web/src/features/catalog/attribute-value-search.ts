export type AttributeOptionValue = { id: string; value: string }

export function filterAttributeOptionValues<T extends AttributeOptionValue>(values: T[], query: string): T[] {
  const normalizedQuery = query.trim().toLocaleLowerCase('tr-TR')
  if (!normalizedQuery) return values

  return values.filter(option => option.value.toLocaleLowerCase('tr-TR').includes(normalizedQuery))
}
