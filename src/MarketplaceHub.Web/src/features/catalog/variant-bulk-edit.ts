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

export type VariantBulkMode = 'set' | 'increase' | 'decrease'

export const variantBulkModeLabels: Record<VariantBulkMode, string> = { set: 'Eşitle', increase: 'Arttır', decrease: 'Azalt' }

export type VariantBulkInput = { mode: VariantBulkMode, value: number | null }

export type VariantBulkInputs = Record<VariantBulkEditField, VariantBulkInput>

// One field's new value: Eşitle writes the typed number, Arttır adds it, Azalt subtracts it but never goes below 0.
// A missing cost counts as 0 for Arttır / Azalt. Prices are kept to 2 decimals, stock to a whole number.
export function applyVariantBulkMode(current: number | null, input: VariantBulkInput, field: VariantBulkEditField): { value: number | null, clamped: boolean } {
  const typed = input.value
  if (typed == null || !Number.isFinite(typed)) return { value: current, clamped: false }
  const round = (number: number) => field === 'stock' ? Math.round(number) : Math.round((number + Number.EPSILON) * 100) / 100
  if (input.mode === 'set') return { value: typed, clamped: false }
  if (typed < 0) return { value: current, clamped: false }
  const base = current ?? 0
  if (input.mode === 'increase') return { value: round(base + typed), clamped: false }
  const result = round(base - typed)
  return result < 0 ? { value: 0, clamped: true } : { value: result, clamped: false }
}

// Applies all four fields' operations to the chosen rows; `clamped` counts the variants that stopped at 0 on any field.
export function applyVariantBulkModes<T extends VariantBulkEditRow>(rows: T[], rowKeys: ReadonlySet<string>, inputs: VariantBulkInputs) {
  let clamped = 0
  const next = rows.map(row => {
    if (!rowKeys.has(row.key)) return row
    const updated: T = { ...row }
    let rowClamped = false
    for (const field of ['stock', 'salePrice', 'costPrice', 'listPrice'] as const) {
      const result = applyVariantBulkMode(row[field], inputs[field], field)
      if (result.clamped) rowClamped = true
      if (result.value != null) (updated as VariantBulkEditRow)[field] = result.value
    }
    if (rowClamped) clamped++
    return updated
  })
  return { rows: next, clamped }
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
