export type VariantBulkEditField = 'stock' | 'salePrice' | 'costPrice' | 'listPrice'

export type VariantBulkEditRow = {
  key: string
  stock: number
  salePrice: number
  costPrice: number
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

export function variantBulkEditIssue(field: VariantBulkEditField, value: number, rows: VariantBulkEditRow[]) {
  if (!Number.isFinite(value) || value < 0) return 'Sıfır veya daha büyük geçerli bir değer girin.'
  if (field === 'stock' && !Number.isInteger(value)) return 'Stok değeri tam sayı olmalıdır.'
  if (field === 'salePrice' && rows.some(row => value > row.listPrice)) return 'Satış fiyatı, eşleşen varyantlardan birinin liste fiyatını aşamaz.'
  if (field === 'listPrice' && rows.some(row => value < row.salePrice)) return 'Liste fiyatı, eşleşen varyantlardan birinin satış fiyatının altında olamaz.'
  return null
}

export function applyVariantBulkEditValue<T extends VariantBulkEditRow>(rows: T[], rowKeys: ReadonlySet<string>, field: VariantBulkEditField, value: number) {
  return rows.map(row => rowKeys.has(row.key) ? { ...row, [field]: value } : row)
}
