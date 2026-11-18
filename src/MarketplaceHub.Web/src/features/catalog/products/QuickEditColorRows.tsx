import { useEffect, useId, useRef, type ReactNode } from 'react'
import { variantGroupSelectionState } from '../variant-bulk-edit'
import './quick-edit-matrix.css'

// The variants of a product as one line per colour: the colour (with a round tick for the whole row) on the left, then its
// sizes side by side, each as a "size label + box" pair. A size a colour does not have is not drawn at all. A size label
// ticks that one variant (the pair turns purple); the chips above tick a size in every colour; the corner ticks all.
// Arrow keys, Tab and Enter move between the boxes (Enter and Down: the next colour, same size).
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

export function QuickEditColorRows<T>({ rows, sizes, cells, idOf, labelOf, selected, onSelect, renderCell, caption, head }: {
  rows: readonly MatrixLine[]
  sizes: readonly string[]
  cells: ReadonlyMap<string, T[]>
  idOf: (item: T) => string
  labelOf: (item: T) => string
  selected: ReadonlySet<string>
  onSelect: (ids: string[], checked: boolean) => void
  renderCell: (item: T, position: MatrixPosition) => ReactNode
  caption: string
  // Shown above the ticks, in the sticky row (the value picker).
  head?: ReactNode
}) {
  const idsIn = (predicate: (colorKey: string, size: string) => boolean) => rows.flatMap(row => sizes.flatMap(size => predicate(row.key, size) ? (cells.get(matrixCellKey(row.key, size)) ?? []).map(idOf) : []))
  const allIds = idsIn(() => true)
  const state = (ids: string[]) => variantGroupSelectionState(ids, selected)
  const all = state(allIds)
  const allId = useId()
  const list = useRef<HTMLDivElement>(null)
  // Native listener: the list is not a control, so a React handler on it would only be a lint error.
  const rowCount = rows.length; const sizeCount = sizes.length
  useEffect(() => {
    const element = list.current
    if (!element) return
    const move = (event: globalThis.KeyboardEvent) => {
      const input = event.target as HTMLInputElement
      if (!(input instanceof HTMLInputElement) || input.dataset.mr === undefined) return
      const key = event.key
      if (key !== 'ArrowUp' && key !== 'ArrowDown' && key !== 'ArrowLeft' && key !== 'ArrowRight' && key !== 'Enter') return
      // Left and right move the caret first; only at an edge of the text (or in a number box) they leave the box.
      const atStart = input.selectionStart == null || input.selectionStart === 0
      const atEnd = input.selectionEnd == null || input.selectionEnd === input.value.length
      if ((key === 'ArrowLeft' && !atStart) || (key === 'ArrowRight' && !atEnd)) return
      let row = Number(input.dataset.mr); let col = Number(input.dataset.mc)
      const [stepRow, stepCol] = key === 'ArrowUp' ? [-1, 0] : key === 'ArrowLeft' ? [0, -1] : key === 'ArrowRight' ? [0, 1] : [1, 0]
      event.preventDefault()
      // The next box in that direction, skipping sizes the colour does not have.
      for (;;) {
        row += stepRow; col += stepCol
        if (row < 0 || col < 0 || row >= rowCount || col >= sizeCount) return
        const next = element.querySelector<HTMLInputElement>(`input[data-mr="${row}"][data-mc="${col}"]`)
        if (next) { next.focus(); next.select?.(); return }
      }
    }
    element.addEventListener('keydown', move)
    return () => element.removeEventListener('keydown', move)
  }, [rowCount, sizeCount])
  return <div className="rr-cr">
    <div className="rr-cr-sticky">
    {head}
    <div className="rr-cr-bar">
      <div className="rr-cr-all"><Tick id={allId} checked={all.checked} mixed={all.indeterminate} round label="Tüm varyantları seç" disabled={!allIds.length} onChange={checked => onSelect(allIds, checked)} /><label htmlFor={allId}>Tümünü Seç</label></div>
      {sizes.length > 1 && <div className="rr-cr-sizebar" role="group" aria-label="Beden kısayolu"><span className="rr-cr-sizebar-label">Beden</span>{sizes.map(size => {
        const ids = idsIn((_, candidate) => candidate === size)
        const chip = state(ids)
        return <button key={size} type="button" className={`rr-cr-chip${chip.checked ? ' rr-on' : chip.indeterminate ? ' rr-mixed' : ''}`} aria-pressed={chip.checked ? true : chip.indeterminate ? 'mixed' : false} aria-label={`${size} bedenindeki tüm varyantları seç`} onClick={() => onSelect(ids, !chip.checked)}>{size}</button>
      })}</div>}
    </div>
    </div>
    <div className="rr-cr-list" ref={list} role="region" aria-label={caption}>
      {rows.map((row, rowIndex) => {
        const rowIds = idsIn(color => color === row.key)
        const rowState = state(rowIds)
        return <div className="rr-cr-row" key={row.key} role="group" aria-label={row.label}>
          <label className="rr-cr-color"><Tick checked={rowState.checked} mixed={rowState.indeterminate} round label={`${row.label} rengindeki tüm varyantları seç`} onChange={checked => onSelect(rowIds, checked)} /><b title={row.label}>{row.label}</b></label>
          <div className="rr-cr-sizes">{sizes.map((size, colIndex) => (cells.get(matrixCellKey(row.key, size)) ?? []).map(item => {
            const on = selected.has(idOf(item))
            return <div className={`rr-cr-pair${on ? ' rr-on' : ''}`} key={idOf(item)}>
              <label className="rr-cr-size" title={`${labelOf(item)} varyantını seç`}><input type="checkbox" className="rr-cr-tick sr-only" tabIndex={-1} checked={on} aria-label={`${labelOf(item)} varyantını seç`} onChange={event => onSelect([idOf(item)], event.currentTarget.checked)} /><span>{size}</span></label>
              {renderCell(item, { row: rowIndex, col: colIndex })}
            </div>
          }))}</div>
        </div>
      })}
    </div>
  </div>
}

function Tick({ id, checked, mixed = false, round = false, label, disabled = false, onChange }: { id?: string; checked: boolean; mixed?: boolean; round?: boolean; label: string; disabled?: boolean; onChange: (checked: boolean) => void }) {
  const ref = useRef<HTMLInputElement>(null)
  useEffect(() => { if (ref.current) ref.current.indeterminate = mixed }, [mixed])
  return <input ref={ref} id={id} type="checkbox" className={`rr-cb${round ? ' rr-cr-round' : ''}`} checked={checked} data-mixed={mixed ? '' : undefined} disabled={disabled} aria-label={label} onChange={event => onChange(event.currentTarget.checked)} />
}
