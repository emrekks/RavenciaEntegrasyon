import { useEffect, useId, useRef, useState } from 'react'
import { UiIcon } from './components'

export type PlatformFilterOption = { value: string; label: string }

type PlatformMultiSelectProps = {
  label: string
  options: PlatformFilterOption[]
  selectedCodes: string[] | null
  onChange: (selectedCodes: string[] | null) => void
}

export function PlatformMultiSelect({ label, options, selectedCodes, onChange }: PlatformMultiSelectProps) {
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  const id = useId()
  const allSelected = selectedCodes === null
  const selectedOptions = options.filter(option => selectedCodes?.includes(option.value))
  const triggerLabel = allSelected || selectedCodes?.length === options.length
    ? 'Tüm platformlar'
    : selectedOptions.length === 0
      ? 'Platform seçilmedi'
      : selectedOptions.length === 1
        ? selectedOptions[0].label
        : `${selectedOptions.length} platform seçili`

  useEffect(() => {
    const closeOutside = (event: PointerEvent) => {
      if (event.target instanceof Node && !root.current?.contains(event.target)) setOpen(false)
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false)
    }
    document.addEventListener('pointerdown', closeOutside)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeOutside)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [])

  function toggleOption(value: string, checked: boolean) {
    const current = allSelected ? options.map(option => option.value) : selectedCodes ?? []
    const next = checked ? Array.from(new Set([...current, value])) : current.filter(item => item !== value)
    onChange(next.length === options.length ? null : next)
  }

  return <div className="platform-multi-select-field">
    <span className="platform-multi-select-label" id={`${id}-label`}>{label}</span>
    <div className="platform-multi-select" ref={root}>
      <button type="button" className={`platform-multi-select-trigger${open ? ' is-open' : ''}${!allSelected ? ' has-selection' : ''}`} aria-labelledby={`${id}-label ${id}-value`} aria-expanded={open} aria-controls={open ? `${id}-menu` : undefined} onClick={() => setOpen(value => !value)} disabled={!options.length}>
        <span id={`${id}-value`}>{triggerLabel}</span><UiIcon name="chevronDown" />
      </button>
      {open && <div className="platform-multi-select-menu" id={`${id}-menu`} role="group" aria-labelledby={`${id}-label`}>
        <div className="platform-multi-select-menu-header"><strong>Platformlar</strong><button type="button" onClick={() => onChange(null)} disabled={allSelected}>Tümünü seç</button></div>
        {options.map(option => <label className="platform-multi-select-option" key={option.value}>
          <input type="checkbox" checked={allSelected || Boolean(selectedCodes?.includes(option.value))} onChange={event => toggleOption(option.value, event.target.checked)} />
          <span>{option.label}</span>
        </label>)}
      </div>}
    </div>
  </div>
}
