import { useEffect, useRef, useState } from 'react'
import { UiIcon } from '../../shared/components'

// Picks any number of panel values for one marketplace value.
export function PanelValueMultiSelect({ label, options, selectedIds, disabled, onToggle }: {
  label: string
  options: ReadonlyArray<{ id: string; value: string }>
  selectedIds: readonly string[]
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
  const summary = selected.length === 0 ? 'Panel değeri seçin' : selected.map(option => option.value).join(', ')
  return <div className="panel-value-multi" ref={root}>
    <button type="button" className={`panel-value-multi-trigger${selected.length ? '' : ' is-empty'}`} aria-label={`${label} panel değerleri`} aria-haspopup="listbox" aria-expanded={open} disabled={disabled} onClick={() => setOpen(current => !current)}>
      <span title={summary}>{summary}</span>
      {selected.length > 1 && <b>{selected.length}</b>}
      <UiIcon name="chevronDown" size={16} />
    </button>
    {open && <div className="panel-value-multi-menu" role="listbox" aria-multiselectable="true" aria-label={`${label} panel değerleri`}>
      {options.map(option => {
        const isSelected = selectedIds.includes(option.id)
        return <button type="button" role="option" aria-selected={isSelected} className={isSelected ? 'active' : ''} key={option.id} onClick={() => onToggle(option.id)}>
          <i aria-hidden="true">{isSelected ? <UiIcon name="check" size={14} /> : null}</i><span>{option.value}</span>
        </button>
      })}
    </div>}
  </div>
}
