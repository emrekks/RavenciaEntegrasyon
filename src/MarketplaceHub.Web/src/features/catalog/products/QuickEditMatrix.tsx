import { useEffect, useRef, type ReactNode } from 'react'
import { variantGroupSelectionState } from '../variant-bulk-edit'
import './quick-edit-matrix.css'

// The variants of a product as one table: a row per colour, a column per size, a box in every cell that has a variant
// (a colour-size pair no variant has is a dimmed dash). Ticking a row, a column or the corner ticks the variants in it;
// one variant is ticked by the small mark in its cell. Arrow keys, Tab and Enter move between the boxes.
export type MatrixLine = { key: string; label: string }
export type MatrixPosition = { row: number; col: number }

// Cells are keyed by colour and size; a pair with several variants (another option tells them apart) keeps them all.
export function matrixCellKey(colorKey: string, size: string) { return `${colorKey}\u0000${size}` }

export function buildMatrix<T>(items: readonly T[], colorKeyOf: (item: T) => string, sizeOf: (item: T) => string, compareSizes: (left: string, right: string) => number, colors: readonly MatrixLine[]) {
  const cells = new Map<string, T[]>()
  const sizes = new Set<string>()
  const present = new Set<string>()
  for (const item of items) {
    const color = colorKeyOf(item); const size = sizeOf(item)
    sizes.add(size); present.add(color)
    const key = matrixCellKey(color, size)
    cells.set(key, [...(cells.get(key) ?? []), item])
  }
  return { rows: colors.filter(color => present.has(color.key)), sizes: [...sizes].sort(compareSizes), cells }
}

export function QuickEditMatrix<T>({ rows, sizes, cells, idOf, labelOf, selected, onSelect, renderCell, caption }: {
  rows: readonly MatrixLine[]
  sizes: readonly string[]
  cells: ReadonlyMap<string, T[]>
  idOf: (item: T) => string
  labelOf: (item: T) => string
  selected: ReadonlySet<string>
  onSelect: (ids: string[], checked: boolean) => void
  renderCell: (item: T, position: MatrixPosition) => ReactNode
  caption: string
}) {
  const idsIn = (predicate: (colorKey: string, size: string) => boolean) => rows.flatMap(row => sizes.flatMap(size => predicate(row.key, size) ? (cells.get(matrixCellKey(row.key, size)) ?? []).map(idOf) : []))
  const allIds = idsIn(() => true)
  const tick = (ids: string[]) => variantGroupSelectionState(ids, selected)
  // A tri-state box: the browser's own indeterminate flag cannot be set from markup.
  const box = (ids: string[], label: string) => {
    const state = tick(ids)
    return <MatrixCheck checked={state.checked} mixed={state.indeterminate} label={label} disabled={!ids.length} onChange={checked => onSelect(ids, checked)} />
  }
  const wrap = useRef<HTMLDivElement>(null)
  // Native listener: the wrapper is not a control, so a React handler on it would be a lint error and add nothing.
  const rowCount = rows.length; const colCount = sizes.length
  useEffect(() => {
    const element = wrap.current
    if (!element) return
    const move = (event: globalThis.KeyboardEvent) => {
    const input = event.target as HTMLInputElement
    if (!(input instanceof HTMLInputElement) || input.dataset.mr === undefined) return
    const key = event.key
    if (key !== 'ArrowUp' && key !== 'ArrowDown' && key !== 'ArrowLeft' && key !== 'ArrowRight' && key !== 'Enter') return
    // Left and right move the caret first; only at an edge of the text (or in a number box) they leave the cell.
    const atStart = input.selectionStart == null || input.selectionStart === 0
    const atEnd = input.selectionEnd == null || input.selectionEnd === input.value.length
    if ((key === 'ArrowLeft' && !atStart) || (key === 'ArrowRight' && !atEnd)) return
    let row = Number(input.dataset.mr); let col = Number(input.dataset.mc)
    const [stepRow, stepCol] = key === 'ArrowUp' ? [-1, 0] : key === 'ArrowLeft' ? [0, -1] : key === 'ArrowRight' ? [0, 1] : [1, 0]
    event.preventDefault()
    // The next box in that direction, skipping dashes.
    for (;;) {
      row += stepRow; col += stepCol
      if (row < 0 || col < 0 || row >= rowCount || col >= colCount) return
      const next = wrap.current?.querySelector<HTMLInputElement>(`input[data-mr="${row}"][data-mc="${col}"]`)
      if (next) { next.focus(); next.select?.(); return }
    }
    }
    element.addEventListener('keydown', move)
    return () => element.removeEventListener('keydown', move)
  }, [rowCount, colCount])
  return <div className="rr-mx-wrap" ref={wrap} role="region" aria-label={caption}>
    <table className="rr-mx">
      <caption className="sr-only">{caption}</caption>
      <thead><tr>
        <th scope="col" className="rr-mx-corner">{box(allIds, 'Tüm varyantları seç')}<span className="sr-only">Renk ve beden</span></th>
        {sizes.map(size => <th scope="col" key={size} className="rr-mx-size">{box(idsIn((_, candidate) => candidate === size), `${size} bedenindeki tüm varyantları seç`)}<b title={size}>{size}</b></th>)}
      </tr></thead>
      <tbody>{rows.map((row, rowIndex) => <tr key={row.key}>
        <th scope="row" className="rr-mx-color">{box(idsIn(color => color === row.key), `${row.label} rengindeki tüm varyantları seç`)}<b title={row.label}>{row.label}</b></th>
        {sizes.map((size, colIndex) => {
          const items = cells.get(matrixCellKey(row.key, size))
          if (!items?.length) return <td key={size} className="rr-mx-none" aria-label={`${row.label} ${size}: varyant yok`}>—</td>
          const on = items.some(item => selected.has(idOf(item)))
          return <td key={size} className={on ? 'rr-mx-on' : undefined}>{items.map(item => <div className="rr-mx-cell" key={idOf(item)}>
            <MatrixCheck small checked={selected.has(idOf(item))} label={`${labelOf(item)} varyantını seç`} onChange={checked => onSelect([idOf(item)], checked)} />
            {renderCell(item, { row: rowIndex, col: colIndex })}
          </div>)}</td>
        })}
      </tr>)}</tbody>
    </table>
  </div>
}

function MatrixCheck({ checked, mixed = false, small = false, label, disabled = false, onChange }: { checked: boolean; mixed?: boolean; small?: boolean; label: string; disabled?: boolean; onChange: (checked: boolean) => void }) {
  const ref = useRef<HTMLInputElement>(null)
  useEffect(() => { if (ref.current) ref.current.indeterminate = mixed }, [mixed])
  return <input ref={ref} type="checkbox" className={`rr-cb${small ? ' rr-mx-pick' : ''}`} checked={checked} data-mixed={mixed ? '' : undefined} disabled={disabled} aria-label={label} onChange={event => { const next = event.currentTarget.checked; onChange(next) }} />
}
