// The size chart dialog's grid: one row per Beden value (the panel's order), one column per measurement in cm. The
// numbers go to the server exactly as typed (comma or dot); the chart is drawn from them and nothing is invented.

export type SizeChartMeasurements = { columns: string[]; rows: Array<{ size: string; values: Array<number | null> }> }
export type SizeChartDraft = { columns: string[]; rows: Array<{ size: string; values: string[] }> }
export type SizeChartPayload = { columns: string[]; rows: Array<{ size: string; values: number[] }> }
export type SizeChartValidation =
  | { ok: true; payload: SizeChartPayload }
  | { ok: false; message: string; cellErrors: Record<string, string>; columnErrors: Record<number, string> }

export const defaultSizeChartColumns = ['Boy', 'En']
export const presetSizeChartColumns = ['Göğüs', 'Bel', 'Kol Boyu']
export const maximumSizeChartColumns = 8
export const maximumSizeChartColumnLength = 24
export const maximumSizeChartValue = 500

export function sizeChartCellKey(rowIndex: number, columnIndex: number) {
  return `${rowIndex}:${columnIndex}`
}

export function sizeChartColumnLabel(name: string) {
  return `${name} (cm)`
}

// "64", "64,5", "64.5" → 64.5; anything else is a Turkish message.
export function parseSizeChartValue(text: string): number | string {
  const value = text.trim()
  if (!value) return 'Ölçü girin.'
  if (!/^\d{1,3}([.,]\d)?$/u.test(value)) return /^\d+([.,]\d+)?$/u.test(value) ? 'En fazla bir ondalık basamak girin.' : 'Yalnız sayı girin.'
  const number = Number(value.replace(',', '.'))
  if (!(number > 0)) return "Ölçü 0'dan büyük olmalı."
  if (number > maximumSizeChartValue) return `Ölçü en fazla ${maximumSizeChartValue} cm olabilir.`
  return number
}

export function normalizeColumnName(name: string) {
  return name.trim().replace(/\s+/gu, ' ')
}

function columnIssue(name: string, index: number, columns: readonly string[]): string | null {
  const normalized = normalizeColumnName(name)
  if (!normalized) return 'Kolon adı girin.'
  if (normalized.length > maximumSizeChartColumnLength) return `Kolon adı en fazla ${maximumSizeChartColumnLength} karakter olabilir.`
  const key = normalized.toLocaleLowerCase('tr-TR')
  if (columns.some((other, otherIndex) => otherIndex < index && normalizeColumnName(other).toLocaleLowerCase('tr-TR') === key)) return 'Bu kolon zaten var.'
  return null
}

// Whether a new column of this name can be added (null) or why not.
export function newColumnIssue(name: string, columns: readonly string[]): string | null {
  if (columns.length >= maximumSizeChartColumns) return `En fazla ${maximumSizeChartColumns} kolon eklenebilir.`
  return columnIssue(name, columns.length, [...columns, name])
}

export function validateSizeChartDraft(draft: SizeChartDraft): SizeChartValidation {
  const cellErrors: Record<string, string> = {}
  const columnErrors: Record<number, string> = {}
  if (!draft.rows.length) return { ok: false, message: 'Ürünün Beden değeri yok.', cellErrors, columnErrors }
  if (!draft.columns.length) return { ok: false, message: 'En az bir ölçü kolonu gerekir.', cellErrors, columnErrors }
  if (draft.columns.length > maximumSizeChartColumns) return { ok: false, message: `En fazla ${maximumSizeChartColumns} kolon olabilir.`, cellErrors, columnErrors }
  draft.columns.forEach((name, index) => { const issue = columnIssue(name, index, draft.columns); if (issue) columnErrors[index] = issue })
  const rows = draft.rows.map((row, rowIndex) => ({
    size: row.size,
    values: draft.columns.map((_, columnIndex) => {
      const parsed = parseSizeChartValue(row.values[columnIndex] ?? '')
      if (typeof parsed === 'string') { cellErrors[sizeChartCellKey(rowIndex, columnIndex)] = parsed; return 0 }
      return parsed
    })
  }))
  const cellCount = Object.keys(cellErrors).length
  const columnCount = Object.keys(columnErrors).length
  if (cellCount || columnCount) {
    const message = columnCount ? 'Kolon adlarını düzeltin.' : cellCount === 1 ? 'Bir hücre eksik veya hatalı.' : `${cellCount} hücre eksik veya hatalı.`
    return { ok: false, message, cellErrors, columnErrors }
  }
  return { ok: true, payload: { columns: draft.columns.map(normalizeColumnName), rows } }
}

function formatValue(value: number | null | undefined) {
  return value == null ? '' : String(value).replace('.', ',')
}

// The grid the dialog opens with: the product's sizes in the panel's order, the saved columns (else Boy and En) and
// the saved numbers of the sizes that still exist.
export function initialSizeChartDraft(sizes: readonly string[], saved: SizeChartMeasurements | null | undefined): SizeChartDraft {
  const columns = saved?.columns.length ? saved.columns.slice(0, maximumSizeChartColumns) : [...defaultSizeChartColumns]
  const savedRows = new Map((saved?.rows ?? []).map(row => [row.size.trim().toLocaleLowerCase('tr-TR'), row]))
  return {
    columns,
    rows: sizes.map(size => {
      const savedRow = saved ? savedRows.get(size.trim().toLocaleLowerCase('tr-TR')) : undefined
      return { size, values: columns.map((_, index) => formatValue(savedRow?.values[index])) }
    })
  }
}

export function addSizeChartColumn(draft: SizeChartDraft, name: string): SizeChartDraft {
  return { columns: [...draft.columns, normalizeColumnName(name)], rows: draft.rows.map(row => ({ ...row, values: [...row.values, ''] })) }
}

export function removeSizeChartColumn(draft: SizeChartDraft, index: number): SizeChartDraft {
  if (draft.columns.length <= 1) return draft
  return { columns: draft.columns.filter((_, position) => position !== index), rows: draft.rows.map(row => ({ ...row, values: row.values.filter((_, position) => position !== index) })) }
}

export function setSizeChartCell(draft: SizeChartDraft, rowIndex: number, columnIndex: number, value: string): SizeChartDraft {
  return { ...draft, rows: draft.rows.map((row, position) => position !== rowIndex ? row : { ...row, values: row.values.map((current, index) => index === columnIndex ? value : current) }) }
}

// Turkish-aware: "BEDEN", "beden", "Beden " all count.
export function isSizeOptionLabel(label: string | null | undefined) {
  return (label ?? '').trim().toLocaleLowerCase('tr-TR') === 'beden'
}
