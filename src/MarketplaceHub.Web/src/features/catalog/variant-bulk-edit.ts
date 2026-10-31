export type VariantBulkEditField = 'stock' | 'salePrice' | 'costPrice' | 'listPrice'

export type VariantBulkEditRow = {
  key: string
  stock: number
  salePrice: number
  costPrice: number | null
  listPrice: number
}

export function variantGroupSelectionState(variantIds: readonly string[], selectedIds: ReadonlySet<string>) {
  const selectedCount = variantIds.reduce((count, id) => count + Number(selectedIds.has(id)), 0)
  return {
    checked: variantIds.length > 0 && selectedCount === variantIds.length,
    indeterminate: selectedCount > 0 && selectedCount < variantIds.length,
  }
}

export function updateVariantGroupSelection(currentIds: readonly string[], groupIds: readonly string[], checked: boolean) {
  const group = new Set(groupIds)
  const retained = currentIds.filter(id => !group.has(id))
  return checked ? [...retained, ...groupIds] : retained
}

// List and sale price are compared on save, not here: a sale price typed before the list price is accepted.
export function variantBulkEditIssue(field: VariantBulkEditField, value: number) {
  if (!Number.isFinite(value) || value < 0) return 'Sıfır veya daha büyük geçerli bir değer girin.'
  if (field === 'stock' && !Number.isInteger(value)) return 'Stok değeri tam sayı olmalıdır.'
  return null
}

export function applyVariantBulkEditValue<T extends VariantBulkEditRow>(rows: T[], rowKeys: ReadonlySet<string>, field: VariantBulkEditField, value: number) {
  return rows.map(row => rowKeys.has(row.key) ? { ...row, [field]: value } : row)
}

// The groups sharing a grid row with `key` (row-major, `columns` per row); one column puts each group on its own row.
export function variantGroupRowMates(keys: readonly string[], key: string, columns: number) {
  const index = keys.indexOf(key)
  if (index < 0) return []
  const start = index - (index % Math.max(1, columns))
  return keys.slice(start, start + Math.max(1, columns))
}

// Opening or closing a group does the same to its row mates, so the cards in one row stay the same height.
export function toggleVariantGroupRow(openKeys: readonly string[], keys: readonly string[], key: string, columns: number) {
  const row = variantGroupRowMates(keys, key, columns)
  const rest = openKeys.filter(item => !row.includes(item))
  return openKeys.includes(key) ? rest : [...rest, ...row]
}
