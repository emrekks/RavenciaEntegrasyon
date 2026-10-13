import { useEffect, useRef, useState } from 'react'
import { UiIcon } from '../../shared/components'

// Picks any number of panel values for one marketplace value.
export function PanelValueMultiSelect({ label, options, selectedIds, assignedElsewhere, disabled, onToggle }: {
  label: string
  options: ReadonlyArray<{ id: string; value: string }>
  selectedIds: readonly string[]
  /** panel value id -> name of the marketplace value it is already matched to, for values matched somewhere else */
  assignedElsewhere: Readonly<Record<string, string>>
  disabled?: boolean
  onToggle: (id: string) => void
}) {
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!open) return
    const close = (event: PointerEvent) => { if (!root.current?.contains(event.target as Node)) setOpen(false) }
    const closeOnEscape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false) }
    document.addEventListener('pointerdown', close)
    document.addEventListener('keydown', closeOnEscape)
    return () => { document.removeEventListener('pointerdown', close); document.removeEventListener('keydown', closeOnEscape) }
  }, [open])
  const selected = options.filter(option => selectedIds.includes(option.id))
  const stateOf = (id: string) => selectedIds.includes(id) ? 'here' : assignedElsewhere[id] ? 'elsewhere' : 'unmatched'
  const order = { unmatched: 0, here: 1, elsewhere: 2 } as const
  const sorted = [...options].sort((left, right) => order[stateOf(left.id)] - order[stateOf(right.id)])
  const unmatchedCount = options.filter(option => stateOf(option.id) === 'unmatched').length
  const summary = selected.length === 0 ? 'Panel değeri seçin' : selected.map(option => option.value).join(', ')
  return <div className="panel-value-multi" ref={root}>
    <button type="button" className={`panel-value-multi-trigger${selected.length ? '' : ' is-empty'}`} aria-label={`${label} panel değerleri`} aria-haspopup="listbox" aria-expanded={open} disabled={disabled} onClick={() => setOpen(current => !current)}>
      <span title={summary}>{summary}</span>
      {selected.length > 1 && <b>{selected.length}</b>}
      <UiIcon name="chevronDown" size={16} />
    </button>
    {open && <div className="panel-value-multi-menu" role="listbox" aria-multiselectable="true" aria-label={`${label} panel değerleri`}>
      <p className="panel-value-multi-hint">{unmatchedCount ? `${unmatchedCount} panel değeri henüz eşleşmedi` : 'Tüm panel değerleri eşleşti'}</p>
      {sorted.map(option => {
        const state = stateOf(option.id)
        const isSelected = state === 'here'
        return <button type="button" role="option" aria-selected={isSelected} className={`${isSelected ? 'active ' : ''}is-${state}`} key={option.id} onClick={() => onToggle(option.id)}>
          <i aria-hidden="true">{isSelected ? <UiIcon name="check" size={14} /> : null}</i>
          <span>{option.value}</span>
          <em>{state === 'unmatched' ? 'Eşleşmedi' : state === 'elsewhere' ? assignedElsewhere[option.id] : 'Eşleşti'}</em>
        </button>
      })}
    </div>}
  </div>
}
