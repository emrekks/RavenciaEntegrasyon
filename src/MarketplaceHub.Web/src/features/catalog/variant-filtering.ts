export type VariantOptionFilterGroup = {
  id: string
  values: Array<{ id: string; value: string }>
}

export type VariantOptionFilterSelections = Record<string, string[]>

export function filterVariantsByOptions<T, G extends VariantOptionFilterGroup>(
  rows: T[],
  groups: G[],
  selections: VariantOptionFilterSelections,
  matchesValue: (row: T, group: G, value: { id: string; value: string }) => boolean
) {
  return rows.filter(row => groups.every(group => {
    const selectedValueIds = selections[group.id] ?? []
    return selectedValueIds.length === 0
      || group.values.some(value => selectedValueIds.includes(value.id) && matchesValue(row, group, value))
  }))
}

export function selectVariantDraftsByKeys<T>(drafts: Record<string, T>, keys: string[]): Record<string, T> {
  const selectedKeys = new Set(keys)
  return Object.fromEntries(Object.entries(drafts).filter(([key]) => selectedKeys.has(key)))
}
